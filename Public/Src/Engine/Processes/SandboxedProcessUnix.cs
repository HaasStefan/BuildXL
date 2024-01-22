// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Interop.Unix;
using BuildXL.Native.IO;
using BuildXL.Native.Processes;
using BuildXL.Pips;
using BuildXL.Processes.Tracing;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Core.Tasks;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.ParallelAlgorithms;
using BuildXL.Utilities.Threading;
using static BuildXL.Interop.Unix.Sandbox;
using static BuildXL.Processes.SandboxedProcessFactory;
using static BuildXL.Utilities.Core.FormattableStringEx;

#nullable enable

namespace BuildXL.Processes
{
    /// <summary>
    /// Implementation of <see cref="ISandboxedProcess"/> that relies on our kernel extension
    /// for Unix-based systems (including MacOS and Linux).
    /// </summary>
    public sealed class SandboxedProcessUnix : UnsandboxedProcess
    {
        private readonly SandboxedProcessReports m_reports;

        private readonly ActionBlockSlim<AccessReport> m_pendingReports;
        private readonly SandboxedProcessTraceBuilder? m_traceBuilder;

        private readonly CancellableTimedAction? m_perfCollector;

        private readonly ReadWriteLock m_snapshotRwl = ReadWriteLock.Create();
        private readonly Dictionary<string, Process.ProcessResourceUsage>? m_processResourceUsage;

        private IEnumerable<ReportedProcess>? m_survivingChildProcesses;

        private PipKextStats? m_pipKextStats;

        private long m_processKilledFlag;

        private ulong m_processExitTimeNs = ulong.MaxValue;

        private bool HasProcessExitBeenReceived => m_processExitTimeNs != ulong.MaxValue;

        private readonly CancellationTokenSource m_timeoutTaskCancelationSource = new CancellationTokenSource();

        private readonly LoggingContext m_loggingContext;

        private readonly IList<Task<AsyncProcessExecutor>> m_ptraceRunners;
        private readonly TaskSourceSlim<bool> m_ptraceRunnersCancellation = TaskSourceSlim.Create<bool>();

        /// <summary>
        /// Id of the underlying pip.
        /// </summary>
        public long PipId { get; }

        private ISandboxConnection SandboxConnection { get; }

        internal TimeSpan ChildProcessTimeout { get; }

        private TimeSpan? ReportQueueProcessTimeoutForTests { get; }

        /// <summary>
        /// Accumulates the time (in microseconds) access reports spend in the report queue
        /// </summary>
        private ulong m_sumOfReportQueueTimesUs;

        /// <summary>
        /// Accumulates the time (in microseconds) access reports need from kernel callbacks, over creation to the moment they are enqueued
        /// </summary>
        private ulong m_sumOfReportCreationTimesUs;

        /// <summary>
        /// Timeout period for inactivity from the sandbox kernel extension.
        /// </summary>
        internal TimeSpan ReportQueueProcessTimeout => SandboxConnection.IsInTestMode
            ? ReportQueueProcessTimeoutForTests ?? TimeSpan.FromSeconds(100)
            : TimeSpan.FromMinutes(45);

        private Task? m_processTreeTimeoutTask;

        /// <summary>
        /// Allowed surviving child process names.
        /// </summary>
        private string[]? AllowedSurvivingChildProcessNames { get; }

        private bool IgnoreReportedAccesses { get; }

        /// <summary>
        /// Absolute path to the executable file.
        /// </summary>
        public string ExecutableAbsolutePath => Process.StartInfo.FileName;

        /// <summary>
        /// Gets file path for standard input.
        /// </summary>
        public static string GetStdInFilePath(string workingDirectory, long pipSemiStableHash) =>
            // CODESYNC: Ensure the string.Format matches FormatSemiStableHash in Pip.cs.
            Path.Combine(workingDirectory, I($"Pip{pipSemiStableHash:X16}.stdin"));

        /// <summary>
        /// Shell executable that wraps the process to be executed.
        /// </summary>
        public const string ShellExecutable = "/bin/bash"; // /bin/sh doesn't support env vars that contain funky characters (e.g., [])

        /// <summary>
        /// Full path to "bxl-env" executable to use instead of '/usr/bin/env' (because some old versions of 'env' do not support the '-C' option).
        /// </summary>
        internal static readonly string EnvExecutable = OperatingSystemHelper.IsLinuxOS ? EnsureDeploymentFile("bxl-env", setExecuteBit: true) : "/usr/bin/env";

        /// <summary>
        /// Path to the ptrace runner to be used if the ptrace sandbox is enabled.
        /// </summary>
        internal static readonly Lazy<string> PTraceRunnerExecutable = new(() => EnsureDeploymentFile(SandboxConnectionLinuxDetours.PTraceRunnerFileName, setExecuteBit: true));

        private readonly Dictionary<string, PathCacheRecord> m_pathCache; // TODO: use AbsolutePath instead of string

        internal static string GetDeploymentFileFullPath(string relativePath)
        {
            var deploymentDir = Path.GetDirectoryName(AssemblyHelper.GetThisProgramExeLocation()) ?? string.Empty;
            return Path.Combine(deploymentDir, relativePath);
        }

        /// <summary>
        /// Ensures that the deployment file exists and returns its full path, and optionally sets the execute bit.
        /// </summary>
        public static string EnsureDeploymentFile(string relativePath, bool setExecuteBit = false)
        {
            var fullPath = GetDeploymentFileFullPath(relativePath);

            if (!File.Exists(fullPath))
            {
                throw new ArgumentException($"Deployment file '{relativePath}' not found");
            }

            if (setExecuteBit)
            {
                _ = FileUtilities.TrySetExecutePermissionIfNeeded(fullPath).ThrowIfFailure();
            }

            return fullPath;
        }

        /// <summary>
        /// Optional configuration for running this process in a root jail.
        /// </summary>
        internal RootJailInfo? RootJailInfo { get; }

        /// <summary>
        /// Optional directory where root jail should be established.
        /// </summary>
        internal string? RootJail => RootJailInfo?.RootJail;

        private const double NanosecondsToMillisecondsFactor = 1000000d;

        internal string ToPathInsideRootJail(string path) => RootJailInfo.ToPathInsideRootJail(path);

        /// <inheritdoc />
        public override int GetLastMessageCount() => m_reports.GetLastMessageCount();

        /// <nodoc />
        public SandboxedProcessUnix(SandboxedProcessInfo info, bool ignoreReportedAccesses = false)
            : base(info)
        {
            Contract.Requires(info.FileAccessManifest != null);
            Contract.Requires(info.SandboxConnection != null);

            PipId = info.FileAccessManifest.PipId;

            SandboxConnection = info.SandboxConnection!;
            ChildProcessTimeout = info.NestedProcessTerminationTimeout;
            AllowedSurvivingChildProcessNames = info.AllowedSurvivingChildProcessNames;
            ReportQueueProcessTimeoutForTests = info.ReportQueueProcessTimeoutForTests;
            IgnoreReportedAccesses = ignoreReportedAccesses;
            RootJailInfo = info.RootJailInfo;
            m_loggingContext = info.LoggingContext;
            m_ptraceRunners = new List<Task<AsyncProcessExecutor>>();
            m_pathCache = new Dictionary<string, PathCacheRecord>();

            if (info.MonitoringConfig is not null && info.MonitoringConfig.MonitoringEnabled)
            {
                m_processResourceUsage = new(capacity: 20);

                m_perfCollector = new CancellableTimedAction(
                    callback: UpdatePerfCounters,
                    intervalMs: (int)info.MonitoringConfig.RefreshInterval.TotalMilliseconds);
            }

            // We cannot create a trace file if we are ignoring file accesses.
            m_traceBuilder = info.CreateSandboxTraceFile && !ignoreReportedAccesses
               ? new SandboxedProcessTraceBuilder(info.FileStorage, info.PathTable)
               : null;

            m_reports = new SandboxedProcessReports(
                info.FileAccessManifest,
                info.PathTable,
                info.PipSemiStableHash,
                info.PipDescription,
                info.LoggingContext,
                info.DetoursEventListener,
                info.SidebandWriter,
                info.FileSystemView,
                m_traceBuilder);

            var useSingleProducer = !(SandboxConnection.Kind == SandboxKind.MacOsHybrid || SandboxConnection.Kind == SandboxKind.MacOsDetours);

            var executionOptions = new ActionBlockSlimConfiguration(
                DegreeOfParallelism: 1, // Must be one, otherwise SandboxedPipExecutor will fail asserting valid reports
                SingleProducerConstrained: useSingleProducer);

            m_pendingReports = ActionBlockSlim.Create<AccessReport>(configuration: executionOptions,
                (accessReport) =>
                {
                    HandleAccessReport(accessReport, info.ForceAddExecutionPermission);
                });
            // install 'ProcessReady' and 'ProcessStarted' handlers to inform the sandbox
            ProcessReady += () => SandboxConnection.NotifyPipReady(info.LoggingContext, info.FileAccessManifest, this, m_pendingReports.Completion);
            ProcessStarted += (pid) => OnProcessStartedAsync(info).GetAwaiter().GetResult();
        }

        private void UpdatePerfCounters()
        {
            // TODO: Full process tree observation is currently only supported on Linux based systems. macOS support will be added later.
            if (Process.HasExited || !OperatingSystemHelper.IsLinuxOS)
            {
                return;
            }

            var snapshots = Interop.Unix.Process.GetResourceUsageForProcessTree(ProcessId);
            using (m_snapshotRwl.AcquireWriteLock())
            {
                foreach (var snapshot in snapshots)
                {
                    if (snapshot.HasValue)
                    {
                        // We use a combination of pid and process name as lookup key, this allows to have resource tracking
                        // working when a process image gets substituted (exec) or when pids get reused (unlikely). If reuse
                        // should happen, the only loophole would be if the process would have the same name as the intially
                        // tracked one, in which case the code below would overwrite the resource usage data.
                        var update = snapshot.Value;
                        var key = $"{update.ProcessId}-{update.Name}";

                        // Remove and replace the snapshot value to reflect changes of processes that are potentially being snapshotted several times
                        m_processResourceUsage![key] = update;
                    }
                }
            }
        }

        private bool NeedsShellWrapping() => OperatingSystemHelper.IsMacOS;

        /// <inheritdoc />
        protected override System.Diagnostics.Process CreateProcess(SandboxedProcessInfo info)
        {
            var process = base.CreateProcess(info);
            process.StartInfo.RedirectStandardInput = true;
            if (info.RootJailInfo?.RootJail != null)
            {
                // the 'chroot' program will change the directory to what it's supposed to be
                process.StartInfo.WorkingDirectory = "/";
            }

            if (NeedsShellWrapping())
            {
                // shell script streamed to stdin
                process.StartInfo.FileName = ShellExecutable;
                process.StartInfo.Arguments = string.Empty;
            }
            else if (info.RootJailInfo == null)
            {
                foreach (var kvp in AdditionalEnvVars(info))
                {
                    process.StartInfo.EnvironmentVariables[kvp.Item1] = kvp.Item2;
                }

                // If the ptrace sandbox is used and the top level process requires ptrace or the ptrace sandbox is unconditionally enabled, then it needs to be updated to use bash
                // This allows the interposed sandbox to start up (because bash is known to not be statically linked) and wrap the exec of the real binary with ptrace.
                var ptraceChecker = PtraceSandboxProcessChecker.Instance;
                if (info.FileAccessManifest.EnableLinuxPTraceSandbox && 
                    (info.FileAccessManifest.UnconditionallyEnableLinuxPTraceSandbox || ptraceChecker.BinaryRequiresPTraceSandbox(info.FileName)))
                {
                    // This is a workaround for an issue that appears on Linux where some executables in the BuildXL
                    // nuget/npm package may not have the execute bit set causing a permission denied error
                    if (info.ForceAddExecutionPermission)
                    {
                        _ = FileUtilities.TrySetExecutePermissionIfNeeded(process.StartInfo.FileName);
                    }

                    process.StartInfo.Arguments = $"{process.StartInfo.FileName} {process.StartInfo.Arguments}";
                    process.StartInfo.FileName = EnvExecutable;
                }
            }
            else
            {
#if NETCOREAPP
                var rootJailInfo = info.RootJailInfo.Value;
                // top-level process is the root jail program
                process.StartInfo.FileName = rootJailInfo.RootJailProgram.program;
                process.StartInfo.Arguments = string.Empty;
                process.StartInfo.ArgumentList.Clear();
                // root jail arguments
                foreach (string rootJailArg in rootJailInfo.RootJailProgram.args)
                {
                    process.StartInfo.ArgumentList.Add(rootJailArg);
                }
                if (rootJailInfo.UserId != null && rootJailInfo.GroupId != null)
                {
                    process.StartInfo.ArgumentList.Add($"--userspec={rootJailInfo.UserId}:{rootJailInfo.GroupId}");
                }
                // root jail directory
                process.StartInfo.ArgumentList.Add(rootJailInfo.RootJail);
                // inside the jail, run "bxl-env" to change into user-specified directory as well as to set environment variables before running user-specified program
                // NOTE: -C <dir> must be the first two arguments, see bxl-env.c
                process.StartInfo.ArgumentList.Add(info.RootJailInfo.CopyToRootJailIfNeeded(EnvExecutable));

                if (info.WorkingDirectory != null)
                {
                    // change directory into what the user specified
                    process.StartInfo.ArgumentList.Add("-C");
                    process.StartInfo.ArgumentList.Add(info.WorkingDirectory);
                }

                // propagate environment variables (because root jail program won't do it)
                process.StartInfo.ArgumentList.Add("-i");
                foreach (var kvp in process.StartInfo.Environment.Select(kvp => (kvp.Key, kvp.Value)).Concat(AdditionalEnvVars(info)))
                {
                    process.StartInfo.ArgumentList.Add($"{kvp.Item1}={kvp.Item2}");
                }
                // finally add the original executable and its arguments
                process.StartInfo.ArgumentList.Add(info.FileName);
                foreach (var arg in CommandLineEscaping.SplitArguments(info.Arguments!))
                {
                    process.StartInfo.ArgumentList.Add(arg.Value.ToString());
                }
#else
                throw new ArgumentException($"Running {nameof(SandboxedProcessUnix)} in a non .NET Core environment should not be possible");
#endif
            }

            // In any case, allow read access to the process file.
            // When executed using external tool, the manifest tree has been sealed, and cannot be modified.
            // We take care of adding this path in the manifest in SandboxedProcessPipExecutor.cs;
            // see AddUnixSpecificSandcboxedProcessFileAccessPolicies
            if (!info.FileAccessManifest!.IsManifestTreeBlockSealed)
            {
                info.FileAccessManifest.AddPath(
                    AbsolutePath.Create(PathTable, process.StartInfo.FileName),
                    mask: FileAccessPolicy.MaskNothing,
                    values: FileAccessPolicy.AllowReadAlways);
            }

            return process;
        }

        /// <summary>
        /// Called right after the process starts executing.
        ///
        /// Since we set the process file name to be /bin/sh and its arguments to be empty (<see cref="CreateProcess"/>),
        /// the process will effectively start in a "suspended" mode, with /bin/sh just waiting for some content to be
        /// piped to its standard input.  Therefore, in this handler we first notify the sandbox of the new process (so that
        /// it starts tracking it) and then just send the actual process command line to /bin/sh via its standard input.
        /// </summary>
        private async Task OnProcessStartedAsync(SandboxedProcessInfo info)
        {
            if (NeedsShellWrapping())
            {
                // The shell wrapper script started, so generate "Process Created" report before the actual pip process starts
                // (the rest of the system expects to see it before any other file access reports).
                //
                // IMPORTANT (macOS-only):
                //   do this before notifying sandbox kernel extension, because otherwise it can happen that a report
                //   from the extension is received before the "process created" report is handled, causing
                //   a "Should see a process creation before its accesses" assertion exception.
                ReportProcessCreated();
            }

            if (OperatingSystemHelper.IsLinuxOS)
            {
                m_perfCollector?.Start();
            }

            string? processStdinFileName = await FlushStandardInputToFileIfNeededAsync(info);

            if (!SandboxConnection.NotifyPipStarted(info.LoggingContext, info.FileAccessManifest, this))
            {
                ThrowCouldNotStartProcess("Failed to initialize the sandbox for process observation, make sure BuildXL is setup correctly!");
            }

            try
            {
                await FeedStdInAsync(info, processStdinFileName);
                m_processTreeTimeoutTask = ProcessTreeTimeoutTask();
            }
            catch (IOException e)
            {
                // IOException can happen if the process is forcefully killed while we're feeding its std in.
                // When that happens, instead of crashing, just make sure the process is killed.
                LogProcessState($"IOException caught while feeding the standard input: {e.ToString()}");
                await KillAsyncInternal(dumpProcessTree: false);
            }
            finally
            {
                // release the FileAccessManifest memory
                // NOTE: just by not keeping any references to 'info' should make the FileAccessManifest object
                //       unreachable and thus available for garbage collection.  We call Release() here explicitly
                //       just to emphasize the importance of reclaiming this memory.
                info.FileAccessManifest!.Release();
            }
        }

        private string DetoursFile => Path.Combine(Path.GetDirectoryName(AssemblyHelper.GetThisProgramExeLocation()) ?? string.Empty, "libBuildXLDetours.dylib");
        private const string DetoursEnvVar = "DYLD_INSERT_LIBRARIES";

        /// <inheritdoc />
        protected override IEnumerable<ReportedProcess>? GetSurvivingChildProcesses()
        {
            return m_survivingChildProcesses;
        }

        /// <inheritdoc />
        protected override bool Killed => Interlocked.Read(ref m_processKilledFlag) > 0;

        /// <inheritdoc />
        protected override async Task KillAsyncInternal(bool dumpProcessTree)
        {
            // In the case that the process gets shut down by either its timeout or e.g. SandboxedProcessPipExecutor
            // detecting resource usage issues and calling KillAsync(), we flag the process with m_processKilled so we
            // don't process any more kernel reports that get pushed into report structure asynchronously!
            long incrementedValue = Interlocked.Increment(ref m_processKilledFlag);

            // Make sure this is done no more than once.
            if (incrementedValue == 1)
            {
                // surviving child processes may only be set when the process is explicitly killed
                m_survivingChildProcesses = NullIfEmpty(CoalesceProcesses(GetCurrentlyActiveChildProcesses()));
                // Before notifying that the root process has exited, kill all ptracerunners to avoid receiving access reports after the communication pipe is closed.
                LogDebug("KillAsyncInternal: KillActivePTraceRunners()");
                KillActivePTraceRunners();
                LogDebug("KillAsyncInternal: System.Diagnostics.Process.Kill()");
                await base.KillAsyncInternal(dumpProcessTree);
                LogDebug("KillAsyncInternal: KillAllChildProcesses()");
                KillAllChildProcesses();
                LogDebug("KillAsyncInternal: SandboxConnection.NotifyRootProcessExited()");
                SandboxConnection.NotifyRootProcessExited(PipId, this);
                LogDebug("KillAsyncInternal: Waiting m_pendingReports.Completion");
                await m_pendingReports.Completion;
                LogDebug("KillAsyncInternal: Completed m_pendingReports.Completion");
            }
        }

        /// <summary>
        /// Waits for all child processes to finish within a timeout limit and then terminates all still running children after that point.
        /// After all the children have been taken care of, the method waits for pending report processing to finish, then returns the
        /// collected reports.
        /// </summary>
        internal override async Task<SandboxedProcessReports?>? GetReportsAsync()
        {
            SandboxConnection.NotifyRootProcessExited(PipId, this);

            if (!Killed)
            {
                LogDebug("GetReportsAsync: Waiting for pending reports.");
                var awaitedTask = await Task.WhenAny(m_pendingReports.Completion, m_processTreeTimeoutTask!);
                if (awaitedTask == m_processTreeTimeoutTask)
                {
                    LogDebug("GetReportsAsync: Attempting to dump process tree after timeout.");
                    // The process tree timed out, so let's try to dump it
                    await KillAsyncInternal(dumpProcessTree: true);
                }
            }

            LogDebug("GetReportsAsync: Waiting for pending reports to complete.");
            // in any case must wait for pending reports to complete, because we must not freeze m_reports before that happens
            await m_pendingReports.Completion;

            // at this point this pip is done executing (it's only left to construct SandboxedProcessResult,
            // which is done by the base class) so notify the sandbox connection about it.
            SandboxConnection.NotifyPipFinished(PipId, this);

            LogDebug("GetReportsAsync: Returning reports.");

            return IgnoreReportedAccesses ? null : m_reports;
        }

        /// <inheritdoc />
        internal override SandboxedProcessTraceBuilder? GetTraceFileBuilderAsync() => m_traceBuilder;

        /// <inheritdoc />
        public override void Dispose()
        {
            m_perfCollector?.Cancel();
            m_perfCollector?.Join();
            m_perfCollector?.Dispose();

            m_timeoutTaskCancelationSource.Cancel();

            ulong reportCount = (ulong)Counters.GetCounterValue(SandboxedProcessCounters.AccessReportCount);
            if (reportCount > 0)
            {
                Counters.AddToCounter(SandboxedProcessCounters.SumOfAccessReportAvgQueueTimeUs, (long)(m_sumOfReportQueueTimesUs / reportCount));
                Counters.AddToCounter(SandboxedProcessCounters.SumOfAccessReportAvgCreationTimeUs, (long)(m_sumOfReportCreationTimesUs / reportCount));
            }

            if (!Killed)
            {
                // Try to kill all processes once the parent gets disposed, so we clean up all used
                // system resources appropriately
                KillAllChildProcesses();
            }

            if (m_pipKextStats != null)
            {
                var statsJson = JsonSerializer.Serialize(m_pipKextStats.Value);
                LogDebug($"Process Kext Stats: {statsJson}");
            }

            // If ptrace runners have not finished yet, then do that now
            // Completing the task below will make us kill any leftover PTraceRunner processes
            KillActivePTraceRunners();

            m_pathCache.Clear();

            base.Dispose();
        }

        /// <summary>
        /// This method reads some collections from the non-thread-safe <see cref="m_reports"/> object.
        /// The callers must make sure that when they call this method no concurrent modifications are
        /// being done to <see cref="m_reports"/>.
        /// </summary>
        private IEnumerable<ReportedProcess> GetCurrentlyActiveChildProcesses()
        {
            return m_reports.GetActiveProcesses().Where(p => p.ProcessId > 0 && p.ProcessId != ProcessId);
        }

        private void KillAllChildProcesses()
        {
            var distinctProcessIds = CoalesceProcesses(GetCurrentlyActiveChildProcesses())?
                .Select(p => p.ProcessId)
                .ToHashSet();
            if (distinctProcessIds != null)
            {
                foreach (int processId in distinctProcessIds)
                {
                    bool killed = BuildXL.Interop.Unix.Process.ForceQuit(processId);
                    LogDebug($"KillAllChildProcesses: kill({processId}) = {killed}");
                    SandboxConnection.NotifyPipProcessTerminated(PipId, processId);
                }
            }
        }

        private bool ShouldWaitForSurvivingChildProcesses()
        {
            var activeProcesses = GetCurrentlyActiveChildProcesses();

            if (!activeProcesses.Any())
            {
                return false;
            }

            // Wait for surviving child processes if no allowable process names are explicitly specified.
            if (AllowedSurvivingChildProcessNames == null || AllowedSurvivingChildProcessNames.Length == 0)
            {
                return true;
            }

            // Otherwise, wait if there are any alive processes that are not explicitly allowed to survive
            var aliveProcessNames = CoalesceProcesses(activeProcesses)?.Select(p => Path.GetFileName(p.Path));
            if (aliveProcessNames != null)
            {
                LogDebug("surviving processes: " + string.Join(",", aliveProcessNames));

                return aliveProcessNames
                    .Except(AllowedSurvivingChildProcessNames)
                    .Any();
            }

            return false;
        }

        /// <nodoc />
        protected override bool ReportsCompleted() => m_pendingReports?.Completion.IsCompleted ?? false;

        /// <summary>
        /// Must not be blocking and should return as soon as possible
        /// </summary>
        internal void PostAccessReport(AccessReport report)
        {
            m_pendingReports.Post(report, throwOnFullOrComplete: true);
        }

        private static string? EnsureQuoted(string? cmdLineArgs)
        {
#if NETCOREAPP
            if (cmdLineArgs == null)
            {
                return null;
            }

            using var sbHandle = Pools.GetStringBuilder();
            StringBuilder sb = sbHandle.Instance;
            foreach (var arg in CommandLineEscaping.SplitArguments(cmdLineArgs))
            {
                sb.Append(sb.Length > 0 ? " " : string.Empty);
                string escaped = CommandLineEscaping.EscapeAsCommandLineWord(arg.Value.ToString());
                if (escaped.Length > 0 && escaped[0] == '"')
                {
                    sb.Append(escaped);
                }
                else
                {
                    sb.Append('"').Append(escaped).Append('"');
                }
            }

            return sb.ToString();
#else
            throw new ArgumentException($"Running {nameof(SandboxedProcessUnix)} in a non .NET Core environment should not be possible");
#endif
        }

        private async Task FeedStdInAsync(SandboxedProcessInfo info, string? processStdinFileName, bool forceAddExecutionPermission = true)
        {
            Contract.Requires(info.RootJailInfo == null || !NeedsShellWrapping(), "Cannot run root jail on this OS");

            // if no shell wrapping is needed, only feed processStdinFileName (if specified)
            if (!NeedsShellWrapping())
            {
                if (processStdinFileName != null)
                {
                    string stdinContent =
#if NETCOREAPP
                        await File.ReadAllTextAsync(processStdinFileName);
#else
                        File.ReadAllText(processStdinFileName);
#endif
                    await Process.StandardInput.WriteAsync(stdinContent);
                }

                Process.StandardInput.Close();
                return;
            }

            // TODO: instead of generating a bash script (and be exposed to all kinds of injection attacks) we should write a wrapper runner program
            string redirectedStdin = processStdinFileName != null ? $" < {ToPathInsideRootJail(processStdinFileName)}" : string.Empty;

            // this additional round of escaping is needed because we are flushing the arguments to a shell script
            string escapedArguments = (EnsureQuoted(info.Arguments) ?? string.Empty)
                .Replace("$", "\\$")
                .Replace("`", "\\`");
            string cmdLine = $"{CommandLineEscaping.EscapeAsCommandLineWord(info.FileName)} {escapedArguments} {redirectedStdin}";

            LogDebug("Feeding stdin");

            var lines = new List<string>();

            lines.Add("set -e");

            if (info.SandboxConnection!.Kind == SandboxKind.MacOsHybrid || info.SandboxConnection.Kind == SandboxKind.MacOsDetours)
            {
                lines.Add($"export {DetoursEnvVar}={DetoursFile}");
            }

            foreach (var envKvp in info.SandboxConnection.AdditionalEnvVarsToSet(info, UniqueName))
            {
                lines.Add($"export {envKvp.Item1}={envKvp.Item2}");
            }

            lines.Add($"exec {cmdLine}");

            if (info.ForceAddExecutionPermission)
            {
                SetExecutePermissionIfNeeded(info.FileName, throwIfNotFound: false);
            }
            foreach (string line in lines)
            {
                await Process.StandardInput.WriteLineAsync(line);
            }

            LogDebug("Done feeding stdin:" + Environment.NewLine + string.Join(Environment.NewLine, lines));
            Process.StandardInput.Close();
        }

        private IEnumerable<(string, string?)> AdditionalEnvVars(SandboxedProcessInfo info)
        {
            return info.SandboxConnection!
                .AdditionalEnvVarsToSet(info, UniqueName)
                .Concat(info.SandboxConnection.Kind == SandboxKind.MacOsHybrid || info.SandboxConnection.Kind == SandboxKind.MacOsDetours
                    ? new (string, string?)[] { (DetoursEnvVar, DetoursFile) }
                    : Array.Empty<(string, string?)>());
        }

        internal override void FeedStdErr(SandboxedProcessOutputBuilder builder, string line)
        {
            FeedOutputBuilder(builder, line);
        }

        /// <nodoc />
        [return: NotNull]
        internal override CpuTimes GetCpuTimes()
        {
            if (m_processResourceUsage is null)
            {
                return base.GetCpuTimes();
            }
            else
            {
                using var _ = m_snapshotRwl.AcquireReadLock();
                return new CpuTimes(
                        user: TimeSpan.FromMilliseconds(m_processResourceUsage.Aggregate(0d, (acc, usage) => acc + usage.Value.UserTimeMs)),
                        system: TimeSpan.FromMilliseconds(m_processResourceUsage.Aggregate(0d, (acc, usage) => acc + usage.Value.SystemTimeMs)));
            }
        }

        // <inheritdoc />
        internal override JobObject.AccountingInformation GetJobAccountingInfo()
        {
            if (m_processResourceUsage is null)
            {
                return base.GetJobAccountingInfo();
            }
            else
            {
                using var _ = m_snapshotRwl.AcquireReadLock();

                IOCounters ioCounters;
                ProcessMemoryCounters memoryCounters;
                uint childProcesses = 0;

                try
                {
                    ioCounters = new IOCounters(new IO_COUNTERS()
                    {
                        ReadOperationCount = m_processResourceUsage.Aggregate(0UL, (acc, usage) => acc + usage.Value.DiskReadOps),
                        ReadTransferCount = m_processResourceUsage.Aggregate(0UL, (acc, usage) => acc + usage.Value.DiskBytesRead),
                        WriteOperationCount = m_processResourceUsage.Aggregate(0UL, (acc, usage) => acc + usage.Value.DiskWriteOps),
                        WriteTransferCount = m_processResourceUsage.Aggregate(0UL, (acc, usage) => acc + usage.Value.DiskBytesWritten),
                    });

                    memoryCounters = ProcessMemoryCounters.CreateFromBytes(
                        m_processResourceUsage.Aggregate(0UL, (acc, usage) => acc + usage.Value.PeakWorkingSetSize),
                        m_processResourceUsage.Aggregate(0UL, (acc, usage) => acc + usage.Value.WorkingSetSize),
                        0, 0);

                    childProcesses = m_processResourceUsage.Keys.Count > 0 ? (uint)(m_processResourceUsage.Keys.Count - 1) : 0; // Exclude the root process from the child count
                }
                catch (OverflowException ex)
                {
                    LogProcessState($"Overflow exception caught while calculating AccountingInformation:{Environment.NewLine}{ex.ToString()}");

                    ioCounters = new IOCounters(new IO_COUNTERS()
                    {
                        ReadOperationCount = 0,
                        WriteOperationCount = 0,
                        ReadTransferCount = 0,
                        WriteTransferCount = 0
                    });

                    memoryCounters = ProcessMemoryCounters.CreateFromBytes(0, 0, 0, 0);
                }

                return new JobObject.AccountingInformation
                {
                    IO = ioCounters,
                    MemoryCounters = memoryCounters,
                    KernelTime = TimeSpan.FromMilliseconds(m_processResourceUsage.Aggregate(0.0, (acc, usage) => acc + usage.Value.SystemTimeMs)),
                    UserTime = TimeSpan.FromMilliseconds(m_processResourceUsage.Aggregate(0.0, (acc, usage) => acc + usage.Value.UserTimeMs)),
                    NumberOfProcesses = childProcesses,
                };
            }
        }

        private void ReportProcessCreated()
        {
            var report = new Sandbox.AccessReport
            {
                Operation = FileOperation.OpProcessStart,
                Pid = Process.Id,
                PipId = PipId,
                PathOrPipStats = Sandbox.AccessReport.EncodePath(Process.StartInfo.FileName),
                Status = (uint)FileAccessStatus.Allowed
            };

            ReportFileAccess(ref report);
        }

        /// <summary>
        /// This function checks for timeout conditions and once met, times out a process tree. On normal process execution the sandbox kernel extension
        /// sends a process tree completion event, which we use to cleanup and flag an execution as successful. It also indicates that the root process and all
        /// of its children have exited and are no longer running. There are several edge-cases to consider if this is not the case though:
        /// 1) The sandbox kernel extension report queue stops receiving events in user-space while processes are still executing in the build engine, thus the
        ///    process tree complete event nor any other signal is ever received. This case is handled by comparing the time since the last event was received on any queue
        ///    to m_reportQueueProcessTimeout and timing out a process complete tree if that timeout threshold has been reached.
        /// 2) If a root processes has exited and its process exit event has been dequeued from the report queue, a child timeout has to be scheduled. This makes sure that
        ///    all children have enough time to execute as they could potentially outlive their parent prior to terminating them. To prevent indefinite waiting,
        ///    there is a NestedProcessTerminationTimeout defined which defaults to 30s or a value passed in by the frontend and setup by a user. Once we know the root process
        ///    has exited and other access reports in the queues are older than that event, we can safely schedule a process tree timeout with the aforementioned value.
        /// </summary>
        /// <returns>Task that signals if a process tree should be timed out</returns>
        private Task ProcessTreeTimeoutTask()
        {
            var processTreeTimeoutSource = new TaskCompletionSource<Unit>();

            Task.Run(async () =>
            {
                while (true)
                {
                    if (m_timeoutTaskCancelationSource.IsCancellationRequested)
                    {
                        break;
                    }

                    var minEnqueueTime = SandboxConnection.MinReportQueueEnqueueTime;

                    // Ensure we time out if the sandbox stops sending any events within ReportQueueProcessTimeout
                    if (minEnqueueTime != 0 &&
                        SandboxConnection.CurrentDrought >= ReportQueueProcessTimeout &&
                        CurrentRunningTime() >= ReportQueueProcessTimeout)
                    {
                        LogProcessState("Process timed out due to inactivity from sandbox kernel extension report queue: " +
                                        $"the process has been running for {CurrentRunningTime().TotalSeconds} seconds " +
                                        $"and no reports have been received for over {ReportQueueProcessTimeout.TotalSeconds} seconds!");

                        m_pendingReports.Complete();
                        // This is a corner case about the sandbox connection not responding, and therefore an unusual case.
                        // Let's dump the process tree in order to help debug this scenario.
                        await KillAsyncInternal(dumpProcessTree: true);
                        processTreeTimeoutSource.SetResult(Unit.Void);
                        break;
                    }

                    if (HasProcessExitBeenReceived && !Killed)
                    {
                        // Proceed if the event queue is beyond the point when ProcessExit was received
                        if (m_processExitTimeNs <= minEnqueueTime)
                        {
                            bool shouldWait = ShouldWaitForSurvivingChildProcesses();
                            if (shouldWait)
                            {
                                LogDebug($"Main process exited but some child processes are still alive. Waiting the specified nested child process timeout to see whether they end naturally.");
                                await Task.Delay(ChildProcessTimeout);
                            }

                            processTreeTimeoutSource.SetResult(Unit.Void);
                            break;
                        }
                        else
                        {
                            LogDebug($"Process exited but still waiting for reports :: exit time = {m_processExitTimeNs}, " +
                                $"min enqueue time = {minEnqueueTime}, current drought = {SandboxConnection.CurrentDrought.TotalMilliseconds}ms");
                        }
                    }

                    await Task.Delay(SandboxConnection.IsInTestMode ? 5 : 250);
                }
            }, m_timeoutTaskCancelationSource.Token).IgnoreErrorsAndReturnCompletion();

            return processTreeTimeoutSource.Task.IgnoreErrorsAndReturnCompletion();
        }

        private void UpdateAverageTimeSpentInReportQueue(AccessReportStatistics stats)
        {
            m_sumOfReportCreationTimesUs += (stats.EnqueueTime - stats.CreationTime) / 1000;
            m_sumOfReportQueueTimesUs += (stats.DequeueTime - stats.EnqueueTime) / 1000;
        }

        private void HandleAccessReport(AccessReport report, bool forceAddExecutionPermission)
        {
            if (report.Operation != FileOperation.OpDebugMessage)
            {
                LogDebug($"Access report received: {AccessReportToString(report)}");
            }

            Counters.IncrementCounter(SandboxedProcessCounters.AccessReportCount);
            using (Counters.StartStopwatch(SandboxedProcessCounters.HandleAccessReportDuration))
            {
                UpdateAverageTimeSpentInReportQueue(report.Statistics);

                // caching path existence checks would speed things up, but it would not be semantically sound w.r.t. our unit tests
                // TODO: check if the tests are over specified because in practice BuildXL doesn't really rely much on the outcome of this check
                var reportPath = report.DecodePath();

                if (m_reports.GetMessageCountSemaphore() != null && ShouldCountReportType(report.Operation) && report.UnexpectedReport == 0)
                {
                    try
                    {
                        m_reports.GetMessageCountSemaphore().WaitOne(0);
                    }
                    catch (Exception e)
                    {
                        // Semaphore was zero.
                        // We should not have to wait to be unblocked because the native side should have incremented
                        // the semaphore as soon as it sent a message.
                        // For now we don't consider this a failure, but in the future this should become a failure
                        LogDebug($"[Pip{PipId}] Message counting semaphore mismatch for pid '{report.Pid}' with reported path '{reportPath}' with exception {e}");
                    }
                }

                // ignore accesses to libDetours.so, because we injected that library
                if (reportPath == SandboxConnectionLinuxDetours.DetoursLibFile)
                {
                    return;
                }

                // Path cache checks are in here instead of SandboxConnectionLinuxDetours because we need to check the semaphore before discarding reports.
                // We don't want to check the path cache for processes requiring ptrace
                // because we rely on this report to start the ptrace sandbox.
                // OpProcessCommandLine can also be skipped because its not a path, and shouldn't be cached.
                if (report.Operation != FileOperation.OpProcessStart
                    && report.Operation != FileOperation.OpProcessExit
                    && report.Operation != FileOperation.OpProcessTreeCompleted
                    && report.Operation != FileOperation.OpProcessRequiresPtrace
                    && report.Operation != FileOperation.OpProcessCommandLine)
                {
                    // check the path cache (only when the message is not about process tree)                        
                    if (GetOrCreateCacheRecord(reportPath).CheckCacheHitAndUpdate((RequestedAccess)report.RequestedAccess))
                    {
                        LogDebug($"Cache hit for access report: ({reportPath}, {(RequestedAccess)report.RequestedAccess})");
                        return;
                    }
                }

                if (report.Operation == FileOperation.OpDebugMessage)
                {
                    // The debug message uses the path to carry the actual message
                    // We don't want this reported anywhere downstream, just log and return
                    LogDebug(reportPath);
                    return;
                }

                if (report.UnexpectedReport > 0)
                {
                    // The message counting semaphore was not incremented on the native side because this report happened before
                    // we were able to open the semaphore
                    Logger.Log.ReceivedFileAccessReportBeforeSemaphoreInit(m_loggingContext, reportPath);
                }

                // Set the process exit time once we receive it from the sandbox kernel extension report queue
                if (report.Operation == FileOperation.OpProcessExit && report.Pid == Process.Id)
                {
                    m_processExitTimeNs = report.Statistics.EnqueueTime;
                }

                // The process requires ptrace, a ptrace runner needs to be started up
                if (report.Operation == FileOperation.OpProcessRequiresPtrace)
                {
                    StartPTraceRunner(report.Pid, reportPath, forceAddExecutionPermission);
                }

                var pathExists = true;

                // special handling for MAC_LOOKUP:
                //   - don't report for existent paths (because for those paths other reports will follow)
                //   - otherwise, set report.RequestAccess to Probe (because the Sandbox reports 'Lookup', but hat BXL expects 'Probe'),
                if (report.Operation == FileOperation.OpMacLookup)
                {
                    pathExists = FileUtilities.Exists(reportPath);
                    if (pathExists)
                    {
                        return;
                    }
                    else
                    {
                        report.RequestedAccess = (uint)RequestedAccess.Probe;
                    }
                }
                // special handling for directory rename:
                //   - scenario: a pip writes a bunch of files into a directory (e.g., 'out.tmp') and then renames that directory (e.g., to 'out')
                //   - up to this point we know about the writes into the 'out.tmp' directory
                //   - once 'out.tmp' is renamed to 'out', we need to explicitly update all previously reported paths under 'out.tmp'
                //       - since we cannot rewrite the past and directly mutate previously reported paths, we simply enumerate
                //         the content of the renamed directory and report all the files in there as writes
                //       - (this is exactly how this is done on Windows, except that it's implemented in the Detours layer)
                else if (report.Operation == FileOperation.OpKAuthMoveDest &&
                         report.Status == (uint)FileAccessStatus.Allowed &&
                         FileUtilities.DirectoryExistsNoFollow(reportPath))
                {
                    FileUtilities.EnumerateFiles(
                        directoryPath: reportPath,
                        recursive: true,
                        pattern: "*",
                        (dir, fileName, attrs, length) =>
                        {
                            AccessReport reportClone = report;
                            reportClone.Operation = FileOperation.OpKAuthWriteFile;
                            reportClone.PathOrPipStats = AccessReport.EncodePath(Path.Combine(dir, fileName));
                            ReportFileAccess(ref reportClone);
                        });
                }

                // our sandbox kernel extension currently doesn't detect file existence, so do it here instead
                if (report.Error == 0 && !pathExists)
                {
                    report.Error = ReportedFileAccess.ERROR_PATH_NOT_FOUND;
                }

                if (report.Operation == FileOperation.OpProcessTreeCompleted)
                {
                    if (SandboxConnection is SandboxConnectionKext)
                    {
                        m_pipKextStats = report.DecodePipKextStats();
                    }
                    m_pendingReports.Complete();
                }
                else
                {
                    ReportFileAccess(ref report);
                }
            }
        }

        private bool ShouldCountReportType(FileOperation op)
        {
            // CODESYNC: Public/Src/Sandbox/Linux/bxl_observer.cpp
            // We explicitly ignore start/exit/processtreecompleted here because during
            // exit signalling a semaphore doesn't seem to work.
            return op != FileOperation.OpProcessStart
                && op != FileOperation.OpProcessExit
                && op != FileOperation.OpProcessTreeCompleted
                && op != FileOperation.OpDebugMessage;
        }

        private void ReportFileAccess(ref AccessReport report)
        {
            if (ReportsCompleted())
            {
                return;
            }

            if (IgnoreReportedAccesses &&
                report.Operation != FileOperation.OpProcessStart &&
                report.Operation != FileOperation.OpProcessExit)
            {
                return;
            }

            m_reports.ReportFileAccess(ref report, ReportProvider);
        }

        /// <summary>
        /// If <see cref="SandboxedProcessInfo.StandardInputReader"/> is set, it flushes the content of that reader
        /// to a file in the process's working directory and returns the absolute path of that file; otherwise returns null.
        /// </summary>
        private async Task<string?> FlushStandardInputToFileIfNeededAsync(SandboxedProcessInfo info)
        {
            if (info.StandardInputReader == null)
            {
                return null;
            }

            string stdinFileName = GetStdInFilePath(Process.StartInfo.WorkingDirectory, info.PipSemiStableHash);
            string stdinText = await info.StandardInputReader.ReadToEndAsync();
            Encoding encoding = info.StandardInputEncoding ?? Console.InputEncoding;
            byte[] stdinBytes = encoding.GetBytes(stdinText);
            bool stdinFileWritten = await FileUtilities.WriteAllBytesAsync(stdinFileName, stdinBytes);
            if (!stdinFileWritten)
            {
                ThrowCouldNotStartProcess($"failed to flush standard input to file '{stdinFileName}'");
            }

            // Allow read from the created stdin file
            // When executed using external tool, the manifest tree has been sealed, and cannot be modified.
            // We take care of adding this path in the manifest in SandboxedProcessPipExecutor.cs;
            // see AddUnixSpecificSandcboxedProcessFileAccessPolicies
            if (!info.FileAccessManifest!.IsManifestTreeBlockSealed)
            {
                info.FileAccessManifest.AddPath(
                    AbsolutePath.Create(PathTable, stdinFileName),
                    mask: FileAccessPolicy.MaskNothing,
                    values: FileAccessPolicy.AllowAll);
            }

            return stdinFileName;
        }

        private IReadOnlyList<T>? NullIfEmpty<T>(IReadOnlyList<T>? list)
        {
            return list == null || list.Count == 0
                ? null
                : list;
        }

        private static readonly int s_maxFileAccessStatus = Enum.GetValues(typeof(FileAccessStatus)).Cast<FileAccessStatus>().Max(e => (int)e);
        private static readonly int s_maxRequestedAccess = Enum.GetValues(typeof(RequestedAccess)).Cast<RequestedAccess>().Max(e => (int)e);

        private bool ReportProvider(
            ref AccessReport report, out uint processId, out uint id, out uint correlationId, out ReportedFileOperation operation, out RequestedAccess requestedAccess, out FileAccessStatus status,
            out bool explicitlyReported, out uint error, out Usn usn, out DesiredAccess desiredAccess, out ShareMode shareMode, out CreationDisposition creationDisposition,
            out FlagsAndAttributes flagsAndAttributes, out FlagsAndAttributes openedFileOrDirectoryAttributes, out AbsolutePath manifestPath, out string path, out string enumeratePattern, out string processArgs, out string errorMessage)
        {
            var errorMessages = new List<string>();
            checked
            {
                processId = (uint)report.Pid;
                id = SandboxedProcessReports.FileAccessNoId;
                correlationId = SandboxedProcessReports.FileAccessNoId;

                if (!FileAccessReportLine.TryGetOperation(report.DecodeOperation(), out operation))
                {
                    errorMessages.Add($"Unknown operation '{report.DecodeOperation()}'");
                }

                requestedAccess = (RequestedAccess)report.RequestedAccess;
                if (report.RequestedAccess > s_maxRequestedAccess)
                {
                    errorMessages.Add($"Illegal value for 'RequestedAccess': {requestedAccess}; maximum allowed: {(int)RequestedAccess.All}");
                }

                status = (FileAccessStatus)report.Status;
                if (report.Status > s_maxFileAccessStatus)
                {
                    errorMessages.Add($"Illegal value for 'Status': {status}");
                }

                bool isWrite = (report.RequestedAccess & (byte)RequestedAccess.Write) != 0;

                explicitlyReported = report.ExplicitLogging > 0;
                error = report.Error;
                usn = ReportedFileAccess.NoUsn;
                desiredAccess = isWrite ? DesiredAccess.GENERIC_WRITE : DesiredAccess.GENERIC_READ;
                shareMode = ShareMode.FILE_SHARE_READ;
                creationDisposition = CreationDisposition.OPEN_ALWAYS;
                flagsAndAttributes = 0;
                openedFileOrDirectoryAttributes = report.IsDirectory == 0 ? FlagsAndAttributes.FILE_ATTRIBUTE_NORMAL : FlagsAndAttributes.FILE_ATTRIBUTE_DIRECTORY;
                path = report.DecodePath();
                enumeratePattern = string.Empty;
                processArgs = string.Empty;

                AbsolutePath.TryCreate(PathTable, path, out manifestPath);

                errorMessage = errorMessages.Any()
                    ? $"Illegal access report: '{AccessReportToString(report)}' :: {string.Join(";", errorMessages)}"
                    : string.Empty;

                return errorMessage == string.Empty;
            }
        }

        internal static string AccessReportToString(AccessReport report)
        {
            var operation = report.DecodeOperation();
            var pid = report.Pid;
            var requestedAccess = report.RequestedAccess;
            var status = report.Status;
            var explicitLogging = report.ExplicitLogging != 0 ? 1 : 0;
            var error = report.Error;
            var path = report.DecodePath();
            var isDirectory = report.IsDirectory;
            ulong processTime = (report.Statistics.EnqueueTime - report.Statistics.CreationTime) / 1000;
            ulong queueTime = (report.Statistics.DequeueTime - report.Statistics.EnqueueTime) / 1000;

            return
                I($"{operation}:{pid}|{requestedAccess}|{status}|{explicitLogging}|{error}|{path}|{isDirectory}|e:{report.Statistics.EnqueueTime}|h:{processTime}us|q:{queueTime}us");
        }

        private void StartPTraceRunner(int pid, string path, bool forceAddExecutionPermission)
        {
            var paths = SandboxConnectionLinuxDetours.GetPaths(RootJailInfo, UniqueName);
            var args = $"-c {pid} -x {path}";
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(PTraceRunnerExecutable.Value, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = Path.GetDirectoryName(PTraceRunnerExecutable.Value)
                },
                EnableRaisingEvents = true
            };
            
            process.StartInfo.Environment[SandboxConnectionLinuxDetours.BuildXLFamPathEnvVarName] = paths.fam;
            process.StartInfo.Environment[SandboxConnectionLinuxDetours.BuildXLTracedProcessPid] = pid.ToString();
            process.StartInfo.Environment[SandboxConnectionLinuxDetours.BuildXLTracedProcessPath] = path;

            var ptraceRunner = new AsyncProcessExecutor
            (
                process,
                // We will kill these manually if the pip is exiting
                Timeout.InfiniteTimeSpan,
                // The runner will only log to stderr if there's a problem, other logs go to the main log using the fifo
                errorBuilder: line => { if (line != null) { Logger.Log.PTraceRunnerError(m_loggingContext, line); } },
                forceAddExecutionPermission: forceAddExecutionPermission
           );

            ptraceRunner.Start();
            m_ptraceRunners.Add(runnerTask(ptraceRunner));

            async Task<AsyncProcessExecutor> runnerTask(AsyncProcessExecutor runner) 
            {
                var runnersCancellation = m_ptraceRunnersCancellation.Task;
                var completedTask = await Task.WhenAny(runner.WaitForExitAsync(), runnersCancellation);

                if (completedTask == runnersCancellation) 
                {
                    // This task is completedwhen this sandbox process is exiting:
                    // this means this runner got left over even though the pip is done.
                    // Let's silently kill it.
                    await runner.KillAsync(dumpProcessTree: false);
                    return runner;
                }

                // Runner exited while the pip is still running.  
                // Let's verify that it didn't fail, or kill the pip otherwise, as we will be missing reports.
                await runner.WaitForStdOutAndStdErrAsync();

                if (runner.Process.ExitCode != 0)
                {
                    // An error should have been logged by the stderr listener
                    // Nothing better to do at this point except to panic and die.
                    await KillAsync();
                }

                return runner;
            }
        }

        private void KillActivePTraceRunners()
        {
            var ptraceRunners = m_ptraceRunners.ToArray();
            m_ptraceRunners.Clear();

            m_ptraceRunnersCancellation.TrySetResult(true);
            foreach (var runner in TaskUtilities.SafeWhenAll(ptraceRunners).GetAwaiter().GetResult())
            {
                runner.Dispose();
            }
        }

        private PathCacheRecord GetOrCreateCacheRecord(string path)
        {
            if (!m_pathCache.TryGetValue(path, out var cacheRecord))
            {
                cacheRecord = new PathCacheRecord()
                {
                    RequestedAccess = RequestedAccess.None
                };
                m_pathCache[path] = cacheRecord;
            }

            return cacheRecord;
        }

        #region HelperClasses
        internal sealed class PathCacheRecord
        {
            internal RequestedAccess RequestedAccess { get; set; }

            internal RequestedAccess GetClosure(RequestedAccess access)
            {
                var result = RequestedAccess.None;

                // Read implies Probe
                if (access.HasFlag(RequestedAccess.Read))
                {
                    result |= RequestedAccess.Probe;
                }

                // Write implies Read and Probe
                if (access.HasFlag(RequestedAccess.Write))
                {
                    result |= RequestedAccess.Read | RequestedAccess.Probe;
                }

                return result;
            }

            internal bool CheckCacheHitAndUpdate(RequestedAccess access)
            {
                // if all flags in 'access' are already present --> cache hit
                bool isCacheHit = (RequestedAccess & access) == access;
                if (!isCacheHit)
                {
                    RequestedAccess |= GetClosure(access);
                }
                return isCacheHit;
            }
        }
        #endregion
    }
}
