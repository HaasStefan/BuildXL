// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Interop;
using BuildXL.Native.IO;
using BuildXL.Native.Processes;
using BuildXL.Pips;
using BuildXL.Pips.Filter;
using BuildXL.Pips.Graph;
using BuildXL.Pips.Operations;
using BuildXL.Plugin;
using BuildXL.Processes;
using BuildXL.Processes.External;
using BuildXL.Processes.Remoting;
using BuildXL.Processes.Sideband;
using BuildXL.Processes.Tracing;
using BuildXL.Utilities;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.ParallelAlgorithms;
using BuildXL.Utilities.Core.Tasks;
using BuildXL.Processes.VmCommandProxy;
using static BuildXL.Processes.SandboxedProcessFactory;
using static BuildXL.Utilities.Core.BuildParameters;
using static BuildXL.Processes.FileAccessManifest;

namespace BuildXL.ProcessPipExecutor
{
    /// <summary>
    /// Adapter from <see cref="Process" /> pips to real (uncached) execution in a <see cref="SandboxedProcess" />.
    /// </summary>
    public sealed class SandboxedProcessPipExecutor : ISandboxedProcessFileStorage
    {
        /// <summary>
        /// Max console length for standard output/error before getting truncated.
        /// </summary>
        public const int MaxConsoleLength = 2048; // around 30 lines

        /// <summary>
        /// Azure Watson's dead exit code.
        /// </summary>
        /// <remarks>
        /// When running in CloudBuild, Process nondeterministically sometimes exits with 0xDEAD exit code. This is the exit code
        /// returned by Azure Watson dump after catching the process crash.
        /// </remarks>
        public const uint AzureWatsonExitCode = 0xDEAD;

        private static readonly string s_appDataLocalMicrosoftClrPrefix =
            Path.Combine(SpecialFolderUtilities.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "CLR");

        private static readonly string s_nvidiaProgramDataPrefix =
            Path.Combine(SpecialFolderUtilities.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NVIDIA Corporation");

        private static readonly string s_nvidiaProgramFilesPrefix =
            Path.Combine(SpecialFolderUtilities.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation");

        private static readonly string s_forefrontTmgClientProgramFilesX86Prefix =
            Path.Combine(SpecialFolderUtilities.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Forefront TMG Client");

        private static readonly string s_userProfilePath =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);

        private static readonly string s_possibleRedirectedUserProfilePath =
            SpecialFolderUtilities.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);

        /// <summary>
        /// The maximum number that this executor will try to launch the given process pip.
        /// </summary>
        public const int ProcessLaunchRetryCountMax = 5;

        /// <summary>
        /// When directing full output to the console, number of lines to read before writing out the log event. This
        /// prevents a pip with very long output from consuming large amounts of memory in this process
        /// </summary>
        public const int OutputChunkInLines = 10000;

        /// <summary>
        /// Indicate whether a intentional retry attempt is initiated for the sake of integration testing.
        /// </summary>
        private static bool s_testRetryOccurred;
        private readonly IConfiguration m_configuration;
        private readonly PipExecutionContext m_context;
        private readonly PathTable m_pathTable;

        private readonly ISandboxConfiguration m_sandboxConfig;

        private readonly IReadOnlyDictionary<string, string> m_rootMappings;

        private readonly FileAccessManifest m_fileAccessManifest;

        private readonly bool m_disableConHostSharing;

        private readonly Action<int> m_processIdListener;

        private readonly PipFragmentRenderer m_pipDataRenderer;

        private readonly FileAccessAllowlist m_fileAccessAllowlist;

        private readonly Process m_pip;

        private readonly string m_pipDescription;

        private readonly Task<Regex> m_warningRegexTask;

        private readonly Task<Regex> m_errorRegexTask;

        private readonly Func<FileArtifact, Task<bool>> m_makeInputPrivate;

        private readonly Func<string, Task<bool>> m_makeOutputPrivate;

        private readonly bool m_warningRegexIsDefault;

        private readonly string m_workingDirectory;

        private readonly PluginEndpoints m_pluginEP;

        private string m_standardDirectory;

        private Regex m_warningRegex;

        private Regex m_errorRegex;

        private int m_numWarnings;

        private TimeSpan m_timeout;

        private readonly FileAccessPolicy m_excludeReportAccessMask;

        private readonly SemanticPathExpander m_semanticPathExpander;

        private readonly ISandboxedProcessLogger m_logger;

        private readonly bool m_shouldPreserveOutputs;

        private readonly PipEnvironment m_pipEnvironment;

        private readonly AbsolutePath m_buildEngineDirectory;

        private readonly bool m_validateDistribution;

        private readonly IDirectoryArtifactContext m_directoryArtifactContext;

        private string m_detoursFailuresFile;

        private readonly ILayoutConfiguration m_layoutConfiguration;

        private readonly ILoggingConfiguration m_loggingConfiguration;

        private readonly LoggingContext m_loggingContext;

        private readonly int m_remainingUserRetryCount;

        private readonly SidebandState m_sidebandState;

        private readonly ITempCleaner m_tempDirectoryCleaner;

        private readonly IReadOnlyDictionary<AbsolutePath, DirectoryArtifact> m_sharedOpaqueDirectoryRoots;

        private readonly VmInitializer m_vmInitializer;
        private readonly IRemoteProcessManager m_remoteProcessManager;

        private readonly Dictionary<AbsolutePath, AbsolutePath> m_tempFolderRedirectionForVm = new();

        private readonly List<AbsolutePath> m_engineCreatedPipOutputDirectories = new();

        private readonly bool m_verboseProcessLoggingEnabled;

        /// <summary>
        /// The active sandboxed process (if any)
        /// </summary>
        private ISandboxedProcess m_activeProcess;

        /// <summary>
        /// Fragments of incremental tools.
        /// </summary>
        private readonly IReadOnlyList<string> m_incrementalToolFragments;

        /// <summary>
        /// Inputs affected by file/source changes.
        /// </summary>
        private readonly IReadOnlyList<AbsolutePath> m_changeAffectedInputs;

        private readonly IDetoursEventListener m_detoursListener;
        private readonly ReparsePointResolver m_reparsePointResolver;

        /// <summary>
        /// Whether the process invokes an incremental tool with preserveOutputs mode.
        /// </summary>
        private bool IsIncrementalPreserveOutputPip => m_shouldPreserveOutputs && m_pip.IncrementalTool;

        private readonly ObjectCache<string, bool> m_incrementalToolMatchCache = new(37);

        /// <summary>
        /// Whether apply fake timestamp or not.
        /// </summary>
        /// <remarks>
        /// We do not apply fake timestamps for process pips that invoke an incremental tool with preserveOutputs mode.
        /// </remarks>
        private bool NoFakeTimestamp => IsIncrementalPreserveOutputPip;

        /// <summary>
        /// Whether file monitoring is enabled for this pip
        /// </summary>
        private bool MonitorFileAccesses => m_sandboxConfig.UnsafeSandboxConfiguration.MonitorFileAccesses && !m_pip.DisableSandboxing;

        private FileAccessPolicy DefaultMask => NoFakeTimestamp ? ~FileAccessPolicy.Deny : ~FileAccessPolicy.AllowRealInputTimestamps;

        private readonly IReadOnlyDictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>> m_staleOutputsUnderSharedOpaqueDirectories;
        private readonly List<string> m_staleOutputsUnderSharedOpaqueDirectoriesToBeDeletedInVM;

        private readonly ISandboxFileSystemView m_fileSystemView;
        private readonly IPipGraphFileSystemView m_pipGraphFileSystemView;

        /// <summary>
        /// Name of the directory in Log directory for std output files
        /// </summary>
        public static readonly string StdOutputsDirNameInLog = "StdOutputs";

        private readonly ProcessRunLocation m_runLocation = ProcessRunLocation.Default;
        private readonly RemoteDataBuilder m_remoteSbDataBuilder;
        private readonly HashSet<AbsolutePath> m_directorySymlinksAsDirectories;
        private readonly Dictionary<AbsolutePath, bool> m_isDirSymlinkCache = new();
        private readonly DirectoryTranslator m_directoryTranslator;

        /// <summary>
        /// Creates an executor for a process pip. Execution can then be started with <see cref="RunAsync" />.
        /// </summary>
        public SandboxedProcessPipExecutor(
            PipExecutionContext context,
            LoggingContext loggingContext,
            Process pip,
            IConfiguration configuration,
            IReadOnlyDictionary<string, string> rootMappings,
            FileAccessAllowlist allowlist,
            Func<FileArtifact, Task<bool>> makeInputPrivate,
            Func<string, Task<bool>> makeOutputPrivate,
            SemanticPathExpander semanticPathExpander,
            PipEnvironment pipEnvironment,
            IDirectoryArtifactContext directoryArtifactContext,
            ITempCleaner tempDirectoryCleaner,
            SidebandState sidebandState,
            ISandboxedProcessLogger logger = null,
            Action<int> processIdListener = null,
            PipFragmentRenderer pipDataRenderer = null,
            AbsolutePath buildEngineDirectory = default,
            DirectoryTranslator directoryTranslator = null,
            int remainingUserRetryCount = 0,
            VmInitializer vmInitializer = null,
            IRemoteProcessManager remoteProcessManager = null,
            SubstituteProcessExecutionInfo shimInfo = null,
            IReadOnlyList<AbsolutePath> changeAffectedInputs = null,
            IDetoursEventListener detoursListener = null,
            ReparsePointResolver reparsePointResolver = null,
            IReadOnlyDictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>> staleOutputsUnderSharedOpaqueDirectories = null,
            PluginManager pluginManager = null,
            ISandboxFileSystemView sandboxFileSystemView = null,
            IPipGraphFileSystemView pipGraphFileSystemView = null,
            ProcessRunLocation runLocation = ProcessRunLocation.Default,
            bool verboseProcessLogging = false)
        {
            Contract.Requires(pip != null);
            Contract.Requires(context != null);
            Contract.Requires(loggingContext != null);
            Contract.Requires(configuration != null);
            Contract.Requires(rootMappings != null);
            Contract.Requires(pipEnvironment != null);
            Contract.Requires(directoryArtifactContext != null);
            // The tempDirectoryCleaner must not be null since it is relied upon for robust file deletion
            Contract.Requires(tempDirectoryCleaner != null);

            m_configuration = configuration;
            m_context = context;
            m_loggingContext = loggingContext;
            m_pathTable = context.PathTable;
            m_pip = pip;
            m_pipDescription = m_pip.GetDescription(m_context);
            m_sandboxConfig = configuration.Sandbox;
            m_rootMappings = rootMappings;
            m_workingDirectory = pip.WorkingDirectory.ToString(m_pathTable);
            m_verboseProcessLoggingEnabled = verboseProcessLogging;
            m_directoryTranslator = directoryTranslator;
            m_fileAccessManifest =
                new FileAccessManifest(
                    m_pathTable,
                    directoryTranslator,
                    m_pip.ChildProcessesToBreakawayFromSandbox.Select(process => 
                        new BreakawayChildProcess(
                            process.ProcessName.ToString(context.StringTable), 
                            process.RequiredArguments, 
                            process.RequiredArgumentsIgnoreCase))
                        .ToReadOnlyArray())
                {
                    MonitorNtCreateFile = m_sandboxConfig.UnsafeSandboxConfiguration.MonitorNtCreateFile,
                    MonitorZwCreateOpenQueryFile = m_sandboxConfig.UnsafeSandboxConfiguration.MonitorZwCreateOpenQueryFile,
                    ForceReadOnlyForRequestedReadWrite = m_sandboxConfig.ForceReadOnlyForRequestedReadWrite,
                    IgnoreReparsePoints = m_sandboxConfig.UnsafeSandboxConfiguration.IgnoreReparsePoints,
                    IgnoreFullReparsePointResolving = !EnableFullReparsePointResolving(configuration, pip),
                    IgnorePreloadedDlls = m_sandboxConfig.UnsafeSandboxConfiguration.IgnorePreloadedDlls,
                    IgnoreZwRenameFileInformation = m_sandboxConfig.UnsafeSandboxConfiguration.IgnoreZwRenameFileInformation,
                    IgnoreZwOtherFileInformation = m_sandboxConfig.UnsafeSandboxConfiguration.IgnoreZwOtherFileInformation,
                    IgnoreNonCreateFileReparsePoints = m_sandboxConfig.UnsafeSandboxConfiguration.IgnoreNonCreateFileReparsePoints,
                    IgnoreSetFileInformationByHandle = m_sandboxConfig.UnsafeSandboxConfiguration.IgnoreSetFileInformationByHandle,
                    NormalizeReadTimestamps =
                        m_sandboxConfig.NormalizeReadTimestamps &&
                        // Do not normalize read timestamps if preserved-output mode is enabled and pip wants its outputs to be preserved.
                        (m_sandboxConfig.UnsafeSandboxConfiguration.PreserveOutputs == PreserveOutputsMode.Disabled || !pip.AllowPreserveOutputs),
                    UseLargeNtClosePreallocatedList = m_sandboxConfig.UseLargeNtClosePreallocatedList,
                    UseExtraThreadToDrainNtClose = m_sandboxConfig.UseExtraThreadToDrainNtClose,
                    DisableDetours = m_sandboxConfig.UnsafeSandboxConfiguration.DisableDetours(),
                    // Identifying Azure watson exit code requires logging process data, e.g., exit code.
                    LogProcessData =
                        (m_sandboxConfig.LogProcesses && m_sandboxConfig.LogProcessData)
                        || (m_sandboxConfig.RetryOnAzureWatsonExitCode && OperatingSystemHelper.IsWindowsOS)
                        || m_verboseProcessLoggingEnabled,
                    IgnoreGetFinalPathNameByHandle = m_sandboxConfig.UnsafeSandboxConfiguration.IgnoreGetFinalPathNameByHandle,
                    // SemiStableHash is 0 for pips with no provenance;
                    // since multiple pips can have no provenance, SemiStableHash is not always unique across all pips
                    PipId = m_pip.SemiStableHash != 0 ? m_pip.SemiStableHash : m_pip.PipId.Value,
                    IgnoreCreateProcessReport = m_sandboxConfig.UnsafeSandboxConfiguration.IgnoreCreateProcessReport,
                    ProbeDirectorySymlinkAsDirectory = m_sandboxConfig.UnsafeSandboxConfiguration.ProbeDirectorySymlinkAsDirectory,
                    SubstituteProcessExecutionInfo = shimInfo,
                    ExplicitlyReportDirectoryProbes = m_sandboxConfig.ExplicitlyReportDirectoryProbes,
                    PreserveFileSharingBehaviour = m_sandboxConfig.PreserveFileSharingBehaviour,
                    EnableLinuxPTraceSandbox = m_sandboxConfig.EnableLinuxPTraceSandbox,
                    EnableLinuxSandboxLogging = m_verboseProcessLoggingEnabled,
                    AlwaysRemoteInjectDetoursFrom32BitProcess = m_sandboxConfig.AlwaysRemoteInjectDetoursFrom32BitProcess,
                    UnconditionallyEnableLinuxPTraceSandbox = m_sandboxConfig.UnconditionallyEnableLinuxPTraceSandbox,
                    IgnoreDeviceIoControlGetReparsePoint = m_sandboxConfig.UnsafeSandboxConfiguration.IgnoreDeviceIoControlGetReparsePoint,
                };

            if (!MonitorFileAccesses)
            {
                // If monitoring of file accesses is disabled, make sure a valid
                // manifest is still provided and disable detours for this pip.
                m_fileAccessManifest.DisableDetours = true;
            }

            m_fileAccessAllowlist = allowlist;
            m_makeInputPrivate = makeInputPrivate;
            m_makeOutputPrivate = makeOutputPrivate;
            m_semanticPathExpander = semanticPathExpander;
            m_logger = logger ?? new SandboxedProcessLogger(m_loggingContext, pip, context);
            m_disableConHostSharing = configuration.Engine.DisableConHostSharing;
            m_shouldPreserveOutputs =
                m_pip.AllowPreserveOutputs
                && m_sandboxConfig.UnsafeSandboxConfiguration.PreserveOutputs != PreserveOutputsMode.Disabled
                && m_sandboxConfig.UnsafeSandboxConfiguration.PreserveOutputsTrustLevel <= m_pip.PreserveOutputsTrustLevel;
            m_processIdListener = processIdListener;
            m_pipEnvironment = pipEnvironment;
            m_pipDataRenderer = pipDataRenderer ?? new PipFragmentRenderer(m_pathTable);
            m_pluginEP = pluginManager != null ? new PluginEndpoints(pluginManager, m_pip, m_pathTable) : null;

            if (pip.WarningRegex.IsValid)
            {
                var expandedDescriptor = new ExpandedRegexDescriptor(pip.WarningRegex.Pattern.ToString(context.StringTable), pip.WarningRegex.Options);
                m_warningRegexTask = RegexFactory.GetRegexAsync(expandedDescriptor);
                m_warningRegexIsDefault = RegexDescriptor.IsDefault(expandedDescriptor.Pattern, expandedDescriptor.Options);
            }

            if (pip.ErrorRegex.IsValid)
            {
                var expandedDescriptor = new ExpandedRegexDescriptor(pip.ErrorRegex.Pattern.ToString(context.StringTable), pip.ErrorRegex.Options);
                m_errorRegexTask = RegexFactory.GetRegexAsync(expandedDescriptor);
            }

            m_excludeReportAccessMask = ~FileAccessPolicy.ReportAccess;
            if (!m_sandboxConfig.MaskUntrackedAccesses)
            {
                // TODO: Remove this when we ascertain that our customers are not
                // negatively impacted by excluding these reports
                // Use 'legacy' behavior where directory enumerations are tracked even
                // when report access is masked.
                m_excludeReportAccessMask |= FileAccessPolicy.ReportDirectoryEnumerationAccess;
            }

            m_buildEngineDirectory = buildEngineDirectory;
            m_validateDistribution = configuration.Distribution.ValidateDistribution;
            m_directoryArtifactContext = directoryArtifactContext;
            m_layoutConfiguration = configuration.Layout;
            m_loggingConfiguration = configuration.Logging;
            m_remainingUserRetryCount = remainingUserRetryCount;
            m_tempDirectoryCleaner = tempDirectoryCleaner;
            m_sidebandState = sidebandState;
            m_sharedOpaqueDirectoryRoots = m_pip.DirectoryOutputs
                .Where(directory => directory.IsSharedOpaque)
                .ToDictionary(directory => directory.Path, directory => directory);

            m_vmInitializer = vmInitializer;
            m_remoteProcessManager = remoteProcessManager;

            if (configuration.IncrementalTools != null)
            {
                m_incrementalToolFragments = configuration.IncrementalTools.Select(toolSuffix =>
                    // Append leading separator to ensure suffix only matches valid relative path fragments
                    Path.DirectorySeparatorChar + toolSuffix.ToString(context.StringTable)).ToArray();
            }
            else
            {
                m_incrementalToolFragments = Array.Empty<string>();
            }

            // Directories specified in the directory translator can be directory symlinks or junctions that are meant to be directories in normal circumstances.
            m_directorySymlinksAsDirectories = directoryTranslator == null
                ? new HashSet<AbsolutePath>()
                : directoryTranslator.Translations
                    .SelectMany(translation => new AbsolutePath[]
                        {
                            AbsolutePath.Create(m_pathTable, translation.SourcePath),
                            AbsolutePath.Create(m_pathTable, translation.TargetPath),
                            // Add the translation results of the source and target paths because policy result is calculated after the translation.
                            //
                            // For example, suppose that there is `/translateDirectory:d:\dbs\sh\bxlint\0315_183200\Out<D:\dbs\el\bxlint\Out` translation, where
                            // D:\dbs\el\bxlint\Out is a directory symlink/junction. Additionally, there is also a substitution `/substTarget:B:\ /substSource:D:\dbs\el\bxlint`.
                            // The tool can call API that enumerates `B:\Out` directly, which is categorized as Read access due to the need to resolve the path
                            // to the end target. If `B:\Out` is not added to the directorySymlinksAsDirectories, Detours and this sandbox will treat `B:\Out`
                            // as a file, and this may result in a disallowed file access.
                            AbsolutePath.Create(m_pathTable, directoryTranslator.Translate(translation.SourcePath)),
                            AbsolutePath.Create(m_pathTable, directoryTranslator.Translate(translation.TargetPath)),
                        })
                    .ToHashSet();

            m_changeAffectedInputs = changeAffectedInputs;
            m_detoursListener = detoursListener;
            m_reparsePointResolver = reparsePointResolver;
            m_staleOutputsUnderSharedOpaqueDirectories = staleOutputsUnderSharedOpaqueDirectories;
            m_staleOutputsUnderSharedOpaqueDirectoriesToBeDeletedInVM = new List<string>();
            m_fileSystemView = sandboxFileSystemView;
            m_pipGraphFileSystemView = pipGraphFileSystemView;
            m_runLocation = runLocation;

            if (runLocation == ProcessRunLocation.Remote)
            {
                m_remoteSbDataBuilder = new RemoteDataBuilder(remoteProcessManager, pip, m_pathTable);
            }
        }

        /// <inheritdoc />
        public string GetFileName(SandboxedProcessFile file)
        {
            FileArtifact fileArtifact = file.PipFileArtifact(m_pip);

            return fileArtifact.IsValid
                ? fileArtifact.Path.ToString(m_pathTable)
                : Path.Combine(GetStandardDirectory(), file.DefaultFileName());
        }

        /// <summary>
        /// <see cref="SandboxedProcess.GetMemoryCountersSnapshot"/>
        /// </summary>
        public ProcessMemoryCountersSnapshot? GetMemoryCountersSnapshot() => m_activeProcess?.GetMemoryCountersSnapshot();

        /// <summary>
        /// <see cref="SandboxedProcess.TryEmptyWorkingSet"/>
        /// </summary>
        public EmptyWorkingSetResult TryEmptyWorkingSet(bool isSuspend)
        {
            var result = m_activeProcess?.TryEmptyWorkingSet(isSuspend) ?? EmptyWorkingSetResult.None;

            if (result.HasFlag(EmptyWorkingSetResult.EmptyWorkingSetFailed)
                && result.HasFlag(EmptyWorkingSetResult.SetMaxWorkingSetFailed)
                && result.HasFlag(EmptyWorkingSetResult.SuspendFailed))
            {
                var errorCode = Marshal.GetLastWin32Error();
                Logger.Log.ResumeOrSuspendProcessError(
                    m_loggingContext,
                    m_pip.FormattedSemiStableHash,
                    result.ToString(),
                    errorCode);
            }

            return result;
        }

        /// <summary>
        /// <see cref="SandboxedProcess.TryResumeProcess"/>
        /// </summary>
        public bool TryResumeProcess()
        {
            var result = m_activeProcess?.TryResumeProcess();

            if (result == false)
            {
                var errorCode = Marshal.GetLastWin32Error();
                Logger.Log.ResumeOrSuspendProcessError(
                    m_loggingContext,
                    m_pip.FormattedSemiStableHash,
                    "ResumeProcess",
                    errorCode);
            }

            return result ?? false;
        }

        /// <summary>
        /// Only enabled when <see cref="ConfigurationExtensions.EnableFullReparsePointResolving(IConfiguration)"/> and the given process doesn't disable it explicitly
        /// </summary>
        private static bool EnableFullReparsePointResolving(IConfiguration configuration, Process process) =>
            configuration.EnableFullReparsePointResolving() && !process.DisableFullReparsePointResolving;

        private string GetDetoursInternalErrorFilePath()
        {
            string tempDir = null;

            if (m_layoutConfiguration != null)
            {
                // First try the object folder.
                // In fully instantiated engine this will never be null, but for some tests it is.
                tempDir = m_layoutConfiguration.ObjectDirectory.ToString(m_pathTable) + "\\Detours";
            }

            if (string.IsNullOrEmpty(tempDir) && m_standardDirectory != null)
            {
                // Then try the Standard directory.
                tempDir = m_standardDirectory;
            }

            // Normalize the temp location. And make sure it is valid.
            // Our tests sometime set the ObjectDirectory to be "\\Test\...".
            if (!string.IsNullOrEmpty(tempDir) && tempDir.StartsWith(@"\", StringComparison.OrdinalIgnoreCase))
            {
                tempDir = null;
            }
            else if (!string.IsNullOrEmpty(tempDir))
            {
                AbsolutePath absPath = AbsolutePath.Create(m_pathTable, tempDir);
                if (absPath.IsValid)
                {
                    tempDir = absPath.ToString(m_pathTable);
                }
                else
                {
                    tempDir = null;
                }
            }

            if (string.IsNullOrEmpty(tempDir))
            {
                // Get and use the BuildXL temp dir.
                tempDir = Path.GetTempPath();
            }

            if (!FileUtilities.DirectoryExistsNoFollow(tempDir))
            {
                try
                {
                    FileUtilities.CreateDirectory(tempDir);
                }
                catch (BuildXLException ex)
                {
                    Logger.Log.LogFailedToCreateDirectoryForInternalDetoursFailureFile(
                        m_loggingContext,
                        m_pip.SemiStableHash,
                        m_pipDescription,
                        tempDir,
                        ex.ToStringDemystified());
                    throw;
                }
            }

            if (!tempDir.EndsWith("\\", StringComparison.OrdinalIgnoreCase))
            {
                tempDir += "\\";
            }

            return tempDir + m_pip.SemiStableHash.ToString("X16", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString() + ".tmp";
        }

        private string GetStandardDirectory()
        {
            if (m_standardDirectory == null)
            {
                Contract.Assert(m_pip.StandardDirectory.IsValid, "Pip builder should guarantee that the assertion holds.");
                m_standardDirectory = m_pip.StandardDirectory.ToString(m_pathTable);
            }

            return m_standardDirectory;
        }

        /// <remarks>
        /// Adapted from Microsoft.Build.Utilities.Core / CanonicalError.cs / Parse
        /// </remarks>>
        private bool IsWarning(string line)
        {
            Contract.Requires(line != null);

            if (m_warningRegex == null)
            {
                return false;
            }

            // An unusually long string causes pathologically slow Regex back-tracking.
            // To avoid that, only scan the first 400 characters. That's enough for
            // the longest possible prefix: MAX_PATH, plus a huge subcategory string, and an error location.
            // After the regex is done, we can append the overflow.
            if (line.Length > 400)
            {
                line = line.Substring(0, 400);
            }

            // If a tool has a large amount of output that isn't a warning (eg., "dir /s %hugetree%")
            // the regex matching below may be slow. It's faster to pre-scan for "warning"
            // and bail out if neither are present.
            if (m_warningRegexIsDefault
                // Net472 does not support line.Contains("warning", StringComparison.OrdinalIgnoreCase)
                && line.IndexOf("warning", StringComparison.OrdinalIgnoreCase) == -1)
            {
                return false;
            }

            return m_warningRegex.IsMatch(line);
        }

        private void Observe(string line)
        {
            if (IsWarning(line))
            {
                Interlocked.Increment(ref m_numWarnings);
            }
        }

        /// <summary>
        /// Filters items from sandboxed output, depending on the predicate provided.
        /// </summary>
        /// <param name="output">Output stream (from sandboxed process) to read from.</param>
        /// <param name="filterPredicate"> Predicate, used to filter lines of interest.</param>
        /// <param name="appendNewLine">Whether to append newLine on non-empty content. Defaults to false.</param>
        private Task<FilterResult> TryFilterLineByLineAsync(SandboxedProcessOutput output, Predicate<string> filterPredicate, bool appendNewLine = false) =>
            TryFilterAsync(output, new OutputFilter(filterPredicate), appendNewLine);

        /// <summary>
        /// Result of filtering the <see cref="SandboxedProcessOutput"/>.
        /// </summary>
        private class FilterResult
        {
            /// <summary>
            /// The output. May or may not be filtered. Empty string means empty output. Null means there was an error processing the output
            /// </summary>
            public string FilteredOutput;

            /// <summary>
            /// Whether the result was filtered
            /// </summary>
            public bool IsFiltered;

            /// <summary>
            /// Whether the result was truncated
            /// </summary>
            public bool IsTruncated;

            /// <summary>
            /// Whether the result was truncated or filtered
            /// </summary>
            public bool IsTruncatedOrFilterd => IsTruncated || IsFiltered;

            /// <summary>
            /// Whether there was an error processing the output
            /// </summary>
            public bool HasError => FilteredOutput == null;

            /// <summary>
            /// FilterResult to use when there is a filtering error
            /// </summary>
            public static FilterResult ResultForError = new() { FilteredOutput = null, IsFiltered = false };
        }

        private async Task<FilterResult> TryFilterAsync(SandboxedProcessOutput output, OutputFilter filter, bool appendNewLine)
        {
            Contract.Assert(filter.LinePredicate != null || filter.Regex != null);
            var filterResult = new FilterResult
            {
                IsFiltered = false
            };
            bool isLineByLine = filter.LinePredicate != null;
            try
            {
                using PooledObjectWrapper<StringBuilder> wrapper = Pools.StringBuilderPool.GetInstance();

                StringBuilder sb = wrapper.Instance;

                using (TextReader reader = output.CreateReader())
                {
                    while (reader.Peek() != -1)
                    {
                        string inputChunk = isLineByLine
                            ? await reader.ReadLineAsync()
                            : await ReadNextChunkAsync(reader, output);

                        if (inputChunk == null)
                        {
                            break;
                        }

                        string outputText = filter.ExtractMatches(inputChunk);

                        if (inputChunk.Replace(Environment.NewLine, string.Empty).Trim().Length > outputText.Replace(Environment.NewLine, string.Empty).Trim().Length)
                        {
                            filterResult.IsFiltered = true;
                        }

                        if (!string.IsNullOrEmpty(outputText))
                        {
                            // only add leading newlines (when needed).
                            // Trailing newlines would cause the message logged to have double newlines
                            if (appendNewLine && sb.Length > 0)
                            {
                                sb.AppendLine();
                            }

                            sb.Append(outputText);
                            if (sb.Length >= MaxConsoleLength)
                            {
                                // Make sure we have a newline before the ellipsis
                                sb.AppendLine();
                                sb.AppendLine("[...]");
                                await output.SaveAsync();
                                sb.Append(output.FileName);
                                filterResult.IsTruncated = true;
                                break;
                            }
                        }
                    }
                }
                filterResult.FilteredOutput = sb.ToString();
                return filterResult;
            }
            catch (IOException ex)
            {
                PipStandardIOFailed(GetFileName(output.File), ex);
                return FilterResult.ResultForError;
            }
            catch (AggregateException ex)
            {
                if (TryLogRootIOException(GetFileName(output.File), ex))
                {
                    return FilterResult.ResultForError;
                }

                throw;
            }
            catch (BuildXLException ex)
            {
                PipStandardIOFailed(GetFileName(output.File), ex);
                return FilterResult.ResultForError;
            }
        }

        /// <summary>
        /// Runs the process pip (uncached).
        /// </summary>
        public async Task<SandboxedProcessPipExecutionResult> RunAsync(
            ISandboxConnection sandboxConnection = null,
            SidebandWriter sidebandWriter = null,
            ISandboxFileSystemView fileSystemView = null,
            CancellationToken cancellationToken = default)
        {
            if (!s_testRetryOccurred)
            {
                // For the integration test, we simulate a retryable failure here via ProcessStartFailure.
                if (m_pip.Priority == Process.IntegrationTestPriority &&
                    m_pip.Tags.Any(a => a.ToString(m_context.StringTable) == TagFilter.TriggerWorkerProcessStartFailed))
                {
                    s_testRetryOccurred = true;
                    return SandboxedProcessPipExecutionResult.FailureButRetryAble(
                        SandboxedProcessPipExecutionStatus.ExecutionFailed,
                        RetryInfo.GetDefault(RetryReason.ProcessStartFailure));
                }
            }

            try
            {
                var sandboxPrepTime = System.Diagnostics.Stopwatch.StartNew();

                if (!PrepareWorkingDirectory())
                {
                    return SandboxedProcessPipExecutionResult.PreparationFailure();
                }

                var possibleEnvironmentVariables = PrepareEnvironmentVariables();
                if (!possibleEnvironmentVariables.Succeeded)
                {
                    Logger.Log.EnvironmentPreparationFailed(m_loggingContext, possibleEnvironmentVariables.Failure.Describe());
                    return SandboxedProcessPipExecutionResult.PreparationFailure();
                }

                var environmentVariables = possibleEnvironmentVariables.Result;
                if (!PrepareTempDirectory(ref environmentVariables))
                {
                    return SandboxedProcessPipExecutionResult.FailureButRetryAble(
                        SandboxedProcessPipExecutionStatus.PreparationFailed,
                        RetryInfo.GetDefault(RetryReason.TempDirectoryCleanupFailure));
                }

                if (!await PrepareResponseFileAsync())
                {
                    return SandboxedProcessPipExecutionResult.PreparationFailure();
                }

                if (!await PrepareChangeAffectedInputListFileAsync(m_changeAffectedInputs))
                {
                    return SandboxedProcessPipExecutionResult.PreparationFailure();
                }

                using (var allInputPathsUnderSharedOpaquesWrapper = Pools.GetAbsolutePathSet())
                {
                    // Here we collect all the paths representing inputs under shared opaques dependencies
                    // These paths need to be flagged appropriately so timestamp faking happen for them. It is also used to identify accesses
                    // that belong to inputs vs accesses that belong to outputs under shared opaques
                    // Both dynamic inputs (the content of shared opaque dependencies) and static inputs are accumulated here.
                    // These paths represent not only the file artifacts, but also the directories that contain those artifacts, up
                    // to the root of the outmost shared opaque that contain them. This makes sure that if timestamps are retrieved
                    // on directories containing inputs, those are faked as well.
                    // The set is kept for two reasons 1) so we avoid duplicate work: as soon as a path is found to be already in this set, we can
                    // shortcut the upward traversal on a given path when doing timestamp faking setup and 2) so GetObservedFileAccesses
                    // doesn't need to recompute this and it can distinguish between accesses that only pertain to outputs vs inputs
                    // in the scope of a shared opaque
                    HashSet<AbsolutePath> allInputPathsUnderSharedOpaques = allInputPathsUnderSharedOpaquesWrapper.Instance;

                    if (MonitorFileAccesses && !TryPrepareFileAccessMonitoring(allInputPathsUnderSharedOpaques))
                    {
                        return SandboxedProcessPipExecutionResult.PreparationFailure();
                    }

                    using (Counters.StartStopwatch(SandboxedProcessCounters.PrepareOutputsDuration))
                    {
                        if (!await PrepareOutputsAsync())
                        {
                            return SandboxedProcessPipExecutionResult.PreparationFailure();
                        }
                    }

                    string executable = m_pip.Executable.Path.ToString(m_pathTable);
                    string arguments = m_pip.Arguments.ToString(m_pipDataRenderer);
                    m_timeout = GetEffectiveTimeout(m_pip.Timeout, m_sandboxConfig.DefaultTimeout, m_sandboxConfig.TimeoutMultiplier);

                    var info = new SandboxedProcessInfo(
                        m_pathTable,
                        this,
                        executable,
                        m_fileAccessManifest,
                        m_disableConHostSharing,
                        m_loggingContext,
                        m_pip.TestRetries,
                        sandboxConnection: sandboxConnection,
                        sidebandWriter: sidebandWriter,
                        detoursEventListener: m_detoursListener,
                        fileSystemView: fileSystemView,
                        forceAddExecutionPermission: m_sandboxConfig.ForceAddExecutionPermission,
                        // We always want to use gentle kill for EBPF to give the ebpf runner a chance to do proper tear down
                        useGentleKill: sandboxConnection?.Kind == SandboxKind.LinuxEBPF)
                    {
                        Arguments = arguments,
                        WorkingDirectory = m_workingDirectory,
                        RootMappings = m_rootMappings,
                        EnvironmentVariables = environmentVariables,
                        Timeout = m_timeout,
                        PipSemiStableHash = m_pip.SemiStableHash,
                        PipDescription = m_pipDescription,
                        TimeoutDumpDirectory = PreparePipTimeoutDumpDirectory(m_sandboxConfig, m_pip, m_pathTable),
                        SurvivingPipProcessChildrenDumpDirectory = m_sandboxConfig.SurvivingPipProcessChildrenDumpDirectory.ToString(m_pathTable),
                        SandboxKind = m_pip.DisableSandboxing ? SandboxKind.None : m_sandboxConfig.UnsafeSandboxConfiguration.SandboxKind,
                        AllowedSurvivingChildProcessNames = m_pip.AllowedSurvivingChildProcessNames.Select(n => n.ToString(m_pathTable.StringTable)).ToArray(),
                        NestedProcessTerminationTimeout = m_pip.NestedProcessTerminationTimeout ?? SandboxedProcessInfo.DefaultNestedProcessTerminationTimeout,
                        DetoursFailureFile = m_detoursFailuresFile,
                        MonitoringConfig = new SandboxedProcessResourceMonitoringConfig(enabled: m_sandboxConfig.MeasureProcessCpuTimes, refreshInterval: TimeSpan.FromSeconds(2)),
                        NumRetriesPipeReadOnCancel = EngineEnvironmentSettings.SandboxNumRetriesPipeReadOnCancel.Value
                            ?? SandboxedProcessInfo.DefaultPipeReadRetryOnCancellationCount,
                        CreateSandboxTraceFile = m_pip.TraceFile.IsValid,
                    };

                    if (m_pluginEP != null)
                    {
                        m_pluginEP.ProcessInfo = info;
                    }

                    if (m_sandboxConfig.AdminRequiredProcessExecutionMode.ExecuteExternalVm() || VmSpecialEnvironmentVariables.IsRunningInVm)
                    {
                        // When need to execute in VM, or is already in VM (e.g., executing integration tests in VM), we need the host shared unc drive translation.
                        TranslateHostSharedUncDrive(info);
                    }

                    var result = SandboxedProcessNeedsExecuteExternal
                        ? await RunExternalAsync(info, allInputPathsUnderSharedOpaques, sandboxPrepTime, cancellationToken)
                        : await RunInternalAsync(info, allInputPathsUnderSharedOpaques, sandboxPrepTime, cancellationToken);
                    if (result.Status == SandboxedProcessPipExecutionStatus.PreparationFailed)
                    {
                        m_processIdListener?.Invoke(0);
                    }

                    // If sideband writer is used and we are executing internally, make sure here that it is flushed to disk.
                    // Without doing this explicitly, if no writes into its SODs were recorded for the pip,
                    // the sideband file will not be automatically saved to disk.  When running externally, the external
                    // executor process will do this and if we do it here again we'll end up overwriting the sideband file.
                    if (!SandboxedProcessNeedsExecuteExternal)
                    {
                        info.SidebandWriter?.EnsureHeaderWritten();
                    }

                    return result;
                }
            }
            finally
            {
                Contract.Assert(m_fileAccessManifest != null);

                m_fileAccessManifest.UnsetMessageCountSemaphore();
            }
        }

        private bool SandboxedProcessShouldExecuteRemote => m_runLocation == ProcessRunLocation.Remote;

        private bool SandboxedProcessNeedsExecuteExternal =>
            SandboxedProcessShouldExecuteRemote
            || (// Execution mode is external
                m_sandboxConfig.AdminRequiredProcessExecutionMode.ExecuteExternal()
                // Only pip that requires admin privilege.
                && m_pip.RequiresAdmin);

        private bool ShouldSandboxedProcessExecuteInVm =>
            SandboxedProcessNeedsExecuteExternal
            && m_sandboxConfig.AdminRequiredProcessExecutionMode.ExecuteExternalVm()
            // Windows only.
            && !OperatingSystemHelper.IsUnixOS;

        private bool IsLazySharedOpaqueOutputDeletionEnabled
            => m_sidebandState?.ShouldPostponeDeletion == true && m_pip.HasSharedOpaqueDirectoryOutputs;

        private async Task<SandboxedProcessPipExecutionResult> RunInternalAsync(
            SandboxedProcessInfo info,
            HashSet<AbsolutePath> allInputPathsUnderSharedOpaques,
            System.Diagnostics.Stopwatch sandboxPrepTime,
            CancellationToken cancellationToken = default)
        {
            if (SandboxedProcessNeedsExecuteExternal)
            {
                Logger.Log.PipProcessNeedsExecuteExternalButExecuteInternal(
                    m_loggingContext,
                    m_pip.SemiStableHash,
                    m_pipDescription,
                    m_pip.RequiresAdmin,
                    m_sandboxConfig.AdminRequiredProcessExecutionMode.ToString(),
                    !OperatingSystemHelper.IsUnixOS,
                    m_processIdListener != null);
            }

            using Stream standardInputStream = TryOpenStandardInputStream(out bool openStandardInputStreamSuccess);

            if (!openStandardInputStreamSuccess)
            {
                return SandboxedProcessPipExecutionResult.PreparationFailure();
            }

            using StreamReader standardInputReader = standardInputStream == null ? null : new StreamReader(standardInputStream, CharUtilities.Utf8NoBomNoThrow);

            info.StandardInputReader = standardInputReader;
            info.StandardInputEncoding = standardInputReader?.CurrentEncoding;

            Action<string> observer = m_warningRegexTask == null ? null : Observe;

            info.StandardOutputObserver = observer;
            info.StandardErrorObserver = observer;

            if (info.GetCommandLine().Length > SandboxedProcessInfo.MaxCommandLineLength)
            {
                LogCommandLineTooLong(info);
                return SandboxedProcessPipExecutionResult.PreparationFailure();
            }

            if (!await TryInitializeWarningRegexAsync())
            {
                return SandboxedProcessPipExecutionResult.PreparationFailure();
            }

            sandboxPrepTime.Stop();
            ISandboxedProcess process = null;

            // Sometimes the injection of Detours fails with error ERROR_PARTIAL_COPY (0x12b)
            // This is random failure, not consistent at all and it seems to be in the lower levels of
            // Detours. If we get such error attempt running the process up to RetryStartCount times
            // before bailing out and reporting an error.
            int processLaunchRetryCount = 0;
            long maxDetoursHeapSize = 0L;
            bool shouldRelaunchProcess = true;

            while (shouldRelaunchProcess)
            {
                try
                {
                    shouldRelaunchProcess = false;
                    process = await StartAsync(info, forceSandboxing: false);
                }
                catch (BuildXLException ex)
                {
                    if (ex.LogEventErrorCode == NativeIOConstants.ErrorFileNotFound)
                    {
                        // The executable for this pip was not found, this is not a retryable failure
                        LocationData location = m_pip.Provenance.Token;
                        string specFile = location.Path.ToString(m_pathTable);

                        Logger.Log.PipProcessFileNotFound(m_loggingContext, m_pip.SemiStableHash, m_pipDescription, 2, info.FileName, specFile, location.Position);

                        return SandboxedProcessPipExecutionResult.PreparationFailure();
                    }
                    else if (ex.LogEventErrorCode == NativeIOConstants.ErrorPartialCopy && (processLaunchRetryCount < ProcessLaunchRetryCountMax))
                    {
                        processLaunchRetryCount++;
                        shouldRelaunchProcess = true;
                        Logger.Log.RetryStartPipDueToErrorPartialCopyDuringDetours(
                            m_loggingContext,
                            m_pip.SemiStableHash,
                            m_pipDescription,
                            ex.LogEventErrorCode,
                            processLaunchRetryCount);

                        // We are about to retry a process execution.
                        // Make sure we wait for the process to end. This way the reporting messages get flushed.
                        if (process != null)
                        {
                            maxDetoursHeapSize = process.GetDetoursMaxHeapSize();

                            try
                            {
                                await process.GetResultAsync();
                            }
                            finally
                            {
                                process.Dispose();
                            }
                        }

                        continue;
                    }
                    else
                    {
                        // not all start failures map to Win32 error code, so we have a message here too
                        Logger.Log.PipProcessStartFailed(
                            m_loggingContext,
                            m_pip.SemiStableHash,
                            m_pipDescription,
                            ex.LogEventErrorCode,
                            ex.LogEventMessage);
                    }

                    // TODO: Implement stricter filters so that we only retry failures that are worth retrying.
                    // See bug 1800258 for more context.
                    return SandboxedProcessPipExecutionResult.FailureButRetryAble(
                        SandboxedProcessPipExecutionStatus.ExecutionFailed,
                        RetryInfo.GetDefault(RetryReason.ProcessStartFailure),
                        maxDetoursHeapSize: maxDetoursHeapSize);
                }
            }

            return await GetAndProcessResultAsync(process, allInputPathsUnderSharedOpaques, sandboxPrepTime, cancellationToken);
        }

        private async Task<SandboxedProcessPipExecutionResult> RunExternalAsync(
            SandboxedProcessInfo info,
            HashSet<AbsolutePath> allInputPathsUnderSharedOpaques,
            System.Diagnostics.Stopwatch sandboxPrepTime,
            CancellationToken cancellationToken = default)
        {
            info.StandardInputSourceInfo = StandardInputInfoExtensions.CreateForProcess(m_pip, m_context.PathTable);

            if (m_pip.WarningRegex.IsValid)
            {
                var observerDescriptor = new SandboxObserverDescriptor
                {
                    WarningRegex = new ExpandedRegexDescriptor(m_pip.WarningRegex.Pattern.ToString(m_context.StringTable), m_pip.WarningRegex.Options)
                };

                info.StandardObserverDescriptor = observerDescriptor;
            }

            info.RedirectedTempFolders = m_tempFolderRedirectionForVm.Select(kvp => (kvp.Key.ToString(m_pathTable), kvp.Value.ToString(m_pathTable))).ToArray();

            ISandboxedProcess process = null;

            try
            {
                var externalSandboxedProcessExecutor = new ExternalToolSandboxedProcessExecutor(Path.Combine(
                    m_layoutConfiguration.BuildEngineDirectory.ToString(m_context.PathTable),
                    ExternalToolSandboxedProcessExecutor.DefaultToolRelativePath));

                foreach (var scope in externalSandboxedProcessExecutor.UntrackedScopes)
                {
                    AddUntrackedScopeToManifest(AbsolutePath.Create(m_pathTable, scope), info.FileAccessManifest);
                }

                // Preparation should be finished.
                sandboxPrepTime.Stop();

                string externalSandboxedProcessDirectory = m_layoutConfiguration.ExternalSandboxedProcessDirectory.ToString(m_pathTable);

                if (SandboxedProcessShouldExecuteRemote)
                {
                    // Initialize remoting process manager once.
                    await m_remoteProcessManager.InitAsync();

                    PopulateRemoteSandboxedProcessData(info);

                    Logger.Log.PipProcessStartRemoteExecution(m_loggingContext, m_pip.SemiStableHash, m_pipDescription, externalSandboxedProcessExecutor.ExecutablePath);

                    Contract.Assert(m_remoteSbDataBuilder != null);

                    RemoteData remoteData = await m_remoteSbDataBuilder.BuildAsync();

                    process = await ExternalSandboxedProcess.StartAsync(
                        info,
                        spi => new RemoteSandboxedProcess(
                            spi,
                            remoteData,
                            m_remoteProcessManager,
                            externalSandboxedProcessExecutor,
                            externalSandboxedProcessDirectory,
                            cancellationToken));
                }
                else if (m_sandboxConfig.AdminRequiredProcessExecutionMode == AdminRequiredProcessExecutionMode.ExternalTool)
                {
                    Logger.Log.PipProcessStartExternalTool(m_loggingContext, m_pip.SemiStableHash, m_pipDescription, externalSandboxedProcessExecutor.ExecutablePath);

                    process = await ExternalSandboxedProcess.StartAsync(
                        info,
                        spi => new ExternalToolSandboxedProcess(spi, externalSandboxedProcessExecutor, externalSandboxedProcessDirectory));
                }
                else
                {
                    Contract.Assert(ShouldSandboxedProcessExecuteInVm);

                    // Initialize VM once.
                    await m_vmInitializer.LazyInitVmAsync.Value;

                    PopulateExternalVMSandboxedProcessData(info);

                    Logger.Log.PipProcessStartExternalVm(m_loggingContext, m_pip.SemiStableHash, m_pipDescription);

                    process = await ExternalSandboxedProcess.StartAsync(
                        info,
                        spi => new ExternalVmSandboxedProcess(
                            spi,
                            m_vmInitializer,
                            externalSandboxedProcessExecutor,
                            externalSandboxedProcessDirectory,
                            m_context?.TestHooks?.SandboxedProcessExecutorTestHook));
                }
            }
            catch (BuildXLException ex)
            {
                Logger.Log.PipProcessStartFailed(
                    m_loggingContext,
                    m_pip.SemiStableHash,
                    m_pipDescription,
                    ex.LogEventErrorCode,
                    ex.LogEventMessage);

                if (SandboxedProcessShouldExecuteRemote && !m_remoteProcessManager.IsInitialized)
                {
                    // Failed to initialize remoting process manager --> fallback to local execution.
                    return SandboxedProcessPipExecutionResult.FailureButRetryAble(
                        SandboxedProcessPipExecutionStatus.ExecutionFailed,
                        RetryInfo.GetDefault(RetryReason.RemoteFallback));
                }

                return SandboxedProcessPipExecutionResult.FailureButRetryAble(
                    SandboxedProcessPipExecutionStatus.ExecutionFailed,
                    RetryInfo.GetDefault(RetryReason.ProcessStartFailure));
            }

            return await GetAndProcessResultAsync(process, allInputPathsUnderSharedOpaques, sandboxPrepTime, cancellationToken);
        }

        private void PopulateRemoteSandboxedProcessData(SandboxedProcessInfo info)
        {
            Contract.Requires(SandboxedProcessShouldExecuteRemote);
            Contract.Requires(m_remoteSbDataBuilder != null);

            m_remoteSbDataBuilder.SetProcessInfo(info);

            if (m_pip.TempDirectory.IsValid)
            {
                m_remoteSbDataBuilder.AddTempDirectory(m_pip.TempDirectory);
            }

            foreach (var tempDir in m_pip.AdditionalTempDirectories)
            {
                m_remoteSbDataBuilder.AddTempDirectory(tempDir);
            }

            // Due to bug in ProjFs, process remoting requires using large buffer for doing enumeration.
            // BUG: https://microsoft.visualstudio.com/OS/_workitems/edit/38539442
            // TODO: Remove this once the bug is resolved.
            info.FileAccessManifest.UseLargeEnumerationBuffer = true;
        }

        private void PopulateExternalVMSandboxedProcessData(SandboxedProcessInfo info)
        {
            if (ShouldSandboxedProcessExecuteInVm)
            {
                if (m_staleOutputsUnderSharedOpaqueDirectoriesToBeDeletedInVM != null && m_staleOutputsUnderSharedOpaqueDirectoriesToBeDeletedInVM.Count > 0)
                {
                    info.ExternalVmSandboxStaleFilesToClean = m_staleOutputsUnderSharedOpaqueDirectoriesToBeDeletedInVM;
                }
            }
        }

        /// <summary>
        /// Translates VMs' host shared drive.
        /// </summary>
        /// <remarks>
        /// VMs' host net shares the drive where the enlistiment resides, e.g., D, that is net used by the VMs. When the process running in a VM
        /// accesses D:\E\f.txt, the process actually accesses D:\E\f.txt in the host. Thus, the file access manifest constructed in the host
        /// is often sufficient for running pips in VMs. However, some tools, like dotnet.exe, can access the path in UNC format, i.e.,
        /// \\192.168.0.1\D\E\f.txt. In this case, we need to supply a directory translation from that UNC path to the non-UNC path.
        /// 
        /// TODO(erickul): Explain why we need to add UNC long path.
        /// </remarks>
        private static void TranslateHostSharedUncDrive(SandboxedProcessInfo info)
        {
            DirectoryTranslator newTranslator = info.FileAccessManifest.DirectoryTranslator?.GetUnsealedClone() ?? new DirectoryTranslator();
            newTranslator.AddTranslation($@"\\{VmConstants.Host.IpAddress}\{VmConstants.Host.NetUseDrive}", $@"{VmConstants.Host.NetUseDrive}:");
            newTranslator.AddTranslation($@"\\{VmConstants.Host.Name}\{VmConstants.Host.NetUseDrive}", $@"{VmConstants.Host.NetUseDrive}:");
            newTranslator.AddTranslation($@"UNC\{VmConstants.Host.Name}\{VmConstants.Host.NetUseDrive}".ToLower(), $@"{VmConstants.Host.NetUseDrive}:");
            newTranslator.Seal();
            info.FileAccessManifest.DirectoryTranslator = newTranslator;
        }

        private async Task<SandboxedProcessPipExecutionResult> GetAndProcessResultAsync(
            ISandboxedProcess process,
            HashSet<AbsolutePath> allInputPathsUnderSharedOpaques,
            System.Diagnostics.Stopwatch sandboxPrepTime,
            CancellationToken cancellationToken)
        {
            using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, m_context.CancellationToken);
            using var cancellationTokenRegistration = cancellationTokenSource.Token.Register(() => process.KillAsync());

            SandboxedProcessResult result;

            int lastMessageCount = 0;
            int lastConfirmedMessageCount = 0;
            bool isMessageCountSemaphoreCreated = false;

            try
            {
                m_activeProcess = process;
                try
                {
                    m_processIdListener?.Invoke(process.ProcessId);
                    result = await process.GetResultAsync();
                }
                finally
                {
                    m_processIdListener?.Invoke(-process.ProcessId);
                }

                lastMessageCount = process.GetLastMessageCount() + result.LastMessageCount;
                lastConfirmedMessageCount = process.GetLastConfirmedMessageCount() + result.LastConfirmedMessageCount;

                m_numWarnings += result.WarningCount;
                isMessageCountSemaphoreCreated = m_fileAccessManifest.MessageCountSemaphore != null || result.MessageCountSemaphoreCreated;

                if (process is ExternalSandboxedProcess externalSandboxedProcess)
                {
                    if (process is RemoteSandboxedProcess remoteSandboxedProcess && remoteSandboxedProcess.ShouldRunLocally == true)
                    {
                        return SandboxedProcessPipExecutionResult.FailureButRetryAble(
                            SandboxedProcessPipExecutionStatus.ExecutionFailed,
                            RetryInfo.GetDefault(RetryReason.RemoteFallback),
                            primaryProcessTimes: result.PrimaryProcessTimes);
                    }

                    int exitCode = externalSandboxedProcess.ExitCode ?? -1;
                    string stdOut = Environment.NewLine + "StdOut:" + Environment.NewLine + externalSandboxedProcess.StdOut;
                    string stdErr = Environment.NewLine + "StdErr:" + Environment.NewLine + externalSandboxedProcess.StdErr;

                    if (process is ExternalToolSandboxedProcess)
                    {
                        Logger.Log.PipProcessFinishedExternalTool(
                            m_loggingContext,
                            m_pip.SemiStableHash,
                            m_pipDescription,
                            exitCode,
                            stdOut,
                            stdErr);
                    }
                    else if (process is ExternalVmSandboxedProcess externalVmSandboxedProcess)
                    {
                        Logger.Log.PipProcessFinishedExternalVm(
                            m_loggingContext,
                            m_pip.SemiStableHash,
                            m_pipDescription,
                            exitCode,
                            stdOut,
                            stdErr);

                        if (externalVmSandboxedProcess.HasVmInfrastructureError)
                        {
                            return SandboxedProcessPipExecutionResult.FailureButRetryAble(
                                SandboxedProcessPipExecutionStatus.ExecutionFailed,
                                RetryInfo.GetDefault(RetryReason.VmExecutionError),
                                primaryProcessTimes: result.PrimaryProcessTimes);
                        }
                    }
                    else if (process is RemoteSandboxedProcess)
                    {
                        Logger.Log.PipProcessFinishedRemoteExecution(
                            m_loggingContext,
                            m_pip.SemiStableHash,
                            m_pipDescription,
                            exitCode,
                            stdOut,
                            stdErr);
                    }
                }
            }
            finally
            {
                m_activeProcess = null;
                process.Dispose();
            }

            // If we trust the statically declared accesses and the pip has processes configured to breakaway
            // then make sure we augment the reported accesses based on the pip static input/output declarations
            if (m_pip.TrustStaticallyDeclaredAccesses && m_pip.ChildProcessesToBreakawayFromSandbox.Length > 0)
            {
                AugmentWithTrustedAccessesFromDeclaredArtifacts(result, m_pip, m_directoryArtifactContext);
            }

            var start = DateTime.UtcNow;
            SandboxedProcessPipExecutionResult executionResult =
                await
                    ProcessSandboxedProcessResultAsync(
                        m_loggingContext,
                        result,
                        sandboxPrepTime.ElapsedMilliseconds,
                        cancellationTokenSource.Token,
                        allInputPathsUnderSharedOpaques,
                        process);
            LogSubPhaseDuration(m_loggingContext, m_pip, SandboxedProcessCounters.SandboxedPipExecutorPhaseProcessingSandboxProcessResult, DateTime.UtcNow.Subtract(start));

            return ValidateDetoursCommunication(
                executionResult,
                lastMessageCount,
                lastConfirmedMessageCount,
                isMessageCountSemaphoreCreated);
        }

        private void AugmentWithTrustedAccessesFromDeclaredArtifacts(SandboxedProcessResult result, Process process, IDirectoryArtifactContext directoryContext)
        {
            // If no ReportedProcess is found it's ok to just create an unnamed one since ReportedProcess is used for descriptive purposes only
            var reportedProcess = result.Processes?.FirstOrDefault() ?? new ReportedProcess(0, string.Empty, string.Empty);

            HashSet<ReportedFileAccess> trustedAccesses = ComputeDeclaredAccesses(process, directoryContext, reportedProcess);

            // All files accesses is an optional field. If present, we augment it with all the trusted ones
            result.FileAccesses?.UnionWith(trustedAccesses);

            // From all the trusted accesses, we only augment with the explicit ones
            result.ExplicitlyReportedFileAccesses.UnionWith(trustedAccesses.Where(access => access.ExplicitlyReported));
        }

        /// <summary>
        /// All declared inputs are represented by file reads, all declared outputs by file writes
        /// </summary>
        /// <remarks>
        /// All inputs are used as an over-approximation to stay on the safe side. Only outputs that are actually
        /// present on disk are turned into write accesses, since not all outputs are guaranteed to be produced.
        /// </remarks>
        private HashSet<ReportedFileAccess> ComputeDeclaredAccesses(Process process, IDirectoryArtifactContext directoryContext, ReportedProcess reportedProcess)
        {
            var trustedAccesses = new HashSet<ReportedFileAccess>();

            // Directory outputs are not supported for now. This is enforced by the process builder.
            Contract.Assert(process.DirectoryOutputs.Length == 0);

            foreach (var inputArtifact in process.Dependencies)
            {
                if (TryGetDeclaredAccessForFile(reportedProcess, inputArtifact, isRead: true, isDirectoryMember: false, out var reportedFileAccess))
                {
                    trustedAccesses.Add(reportedFileAccess);
                }
            }

            foreach (var inputDirectory in process.DirectoryDependencies)
            {
                // Source seal directories are not supported for now. Discovering them via enumerations
                // could be a way to do it in the future. This is enforced by the process builder.
                Contract.Assert(!directoryContext.GetSealDirectoryKind(inputDirectory).IsSourceSeal());

                var directoryContent = directoryContext.ListSealDirectoryContents(inputDirectory, out _);

                foreach (var inputArtifact in directoryContent)
                {
                    if (TryGetDeclaredAccessForFile(reportedProcess, inputArtifact, isRead: true, isDirectoryMember: true, out var reportedFileAccess))
                    {
                        trustedAccesses.Add(reportedFileAccess);
                    }
                }
            }

            foreach (var outputArtifact in process.FileOutputs)
            {
                // We only add outputs that were actually produced
                if (TryGetDeclaredAccessForFile(reportedProcess, outputArtifact.Path, isRead: false, isDirectoryMember: false, out var reportedFileAccess)
                    && FileUtilities.FileExistsNoFollow(outputArtifact.Path.ToString(m_pathTable)))
                {
                    trustedAccesses.Add(reportedFileAccess);
                }
            }

            return trustedAccesses;
        }

        private bool TryGetDeclaredAccessForFile(ReportedProcess process, AbsolutePath path, bool isRead, bool isDirectoryMember, out ReportedFileAccess reportedFileAccess)
        {
            // In some circumstances, to reduce bxl's memory footprint, FileAccessManifest objects are
            // released as soon as they are serialized and sent to the sandbox.  When that is the case,
            // we cannot look up the policy for the path because at this time the FAM is empty, i.e.,
            // we have to decide whether or not this access should be reported explicitly without having
            // the actual policy: only writes and sealed directory reads are reported explicitly.
            var reportExplicitly = !isRead || isDirectoryMember;
            var manifestPath = path;
            if (m_fileAccessManifest.IsFinalized && m_fileAccessManifest.TryFindManifestPathFor(path, out manifestPath, out var nodePolicy))
            {
                reportExplicitly = nodePolicy.HasFlag(FileAccessPolicy.ReportAccess);
            }

            // if the access is flagged to not be reported, and the global manifest flag does not require all accesses
            // to be reported, then we don't create it
            var shouldReport = m_fileAccessManifest.ReportFileAccesses || reportExplicitly;
            if (!shouldReport)
            {
                reportedFileAccess = default;
                return false;
            }

            reportedFileAccess = new ReportedFileAccess(
                ReportedFileOperation.CreateFile,
                process,
                isRead ? RequestedAccess.Read : RequestedAccess.Write,
                FileAccessStatus.Allowed,
                explicitlyReported: reportExplicitly,
                0,
                0,
                Usn.Zero,
                isRead ? DesiredAccess.GENERIC_READ : DesiredAccess.GENERIC_WRITE,
                ShareMode.FILE_SHARE_NONE,
                isRead ? CreationDisposition.OPEN_ALWAYS : CreationDisposition.CREATE_ALWAYS,
                FlagsAndAttributes.FILE_ATTRIBUTE_NORMAL,
                manifestPath,
                path: (path == manifestPath) ? null : path.ToString(m_pathTable),
                enumeratePattern: null,
                openedFileOrDirectoryAttribute: FlagsAndAttributes.FILE_ATTRIBUTE_NORMAL,
                FileAccessStatusMethod.TrustedTool);

            return true;
        }

        /// <summary>
        /// These various validations that the detours communication channel
        /// </summary>
        private SandboxedProcessPipExecutionResult ValidateDetoursCommunication(
            SandboxedProcessPipExecutionResult result,
            int lastMessageCount,
            int lastConfirmedMessageCount,
            bool isMessageSemaphoreCountCreated)
        {
            // If we have a failure already, that could have cause some of the mismatch in message count of writing the side communication file.
            if (result.Status == SandboxedProcessPipExecutionStatus.Succeeded && !string.IsNullOrEmpty(m_detoursFailuresFile))
            {
                if (OperatingSystemHelper.IsWindowsOS)
                {
                    FileInfo fi;

                    try
                    {
                        fi = new FileInfo(m_detoursFailuresFile);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log.LogGettingInternalDetoursErrorFile(
                            m_loggingContext,
                            m_pip.SemiStableHash,
                            m_pipDescription,
                            ex.ToStringDemystified());
                        return SandboxedProcessPipExecutionResult.DetouringFailure(result);
                    }

                    bool fileExists = fi.Exists;
                    if (fileExists)
                    {
                        Logger.Log.LogInternalDetoursErrorFileNotEmpty(
                            m_loggingContext,
                            m_pip.SemiStableHash,
                            m_pipDescription,
                            File.ReadAllText(m_detoursFailuresFile, Encoding.Unicode));

                        return SandboxedProcessPipExecutionResult.DetouringFailure(result);
                    }
                }

                // Avoid eager reporting of message count mismatch. We have observed two failures in WDG and both were due to
                // a pip running longer than the timeout (5 hours). The pip gets killed and in such cases the message count mismatch
                // is legitimate.
                // Report a counter mismatch only if there are no other errors.
                if (result.Status == SandboxedProcessPipExecutionStatus.Succeeded && isMessageSemaphoreCountCreated)
                {
                    if (lastMessageCount > 0)
                    {
                        // Some messages were sent (or were about to be sent), but were not received by the sandbox,
                        // probably because the process terminated before sending the messages or while sending the messages.
                        if (lastConfirmedMessageCount > 0)
                        {
                            // Received messages is less than the successfully sent messages.
                            Logger.Log.LogMismatchedDetoursCountLostMessages(
                                m_loggingContext,
                                m_pip.SemiStableHash,
                                m_pipDescription,
                                lastMessageCount,
                                lastConfirmedMessageCount);

                            return SandboxedProcessPipExecutionResult.MismatchedMessageCountFailure(result);
                        }

                        Logger.Log.LogMismatchedDetoursCount(
                            m_loggingContext,
                            m_pip.SemiStableHash,
                            m_pipDescription,
                            lastMessageCount,
                            lastConfirmedMessageCount);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Checks if the warning regex is valid.
        /// </summary>
        public async Task<bool> TryInitializeWarningRegexAsync()
        {
            if (m_warningRegexTask != null)
            {
                try
                {
                    m_warningRegex = await m_warningRegexTask;
                }
                catch (ArgumentException)
                {
                    LogInvalidWarningRegex();
                    return false;
                }
            }

            return true;
        }

        private async Task<bool> TryInitializeErrorRegexAsync()
        {
            if (m_errorRegexTask != null)
            {
                try
                {
                    m_errorRegex = await m_errorRegexTask;
                }
                catch (ArgumentException)
                {
                    LogInvalidErrorRegex();
                    return false;
                }
            }

            return true;
        }

        private bool TryLogRootIOException(string path, AggregateException aggregateException)
        {
            var flattenedEx = aggregateException.Flatten();
            if (flattenedEx.InnerExceptions.Count == 1 && flattenedEx.InnerExceptions[0] is IOException)
            {
                PipStandardIOFailed(path, flattenedEx.InnerExceptions[0]);
                return true;
            }

            return false;
        }

        private void PipStandardIOFailed(string path, Exception ex)
        {
            Logger.Log.PipStandardIOFailed(
                m_loggingContext,
                m_pip.SemiStableHash,
                m_pipDescription,
                path,
                ex.GetLogEventErrorCode(),
                ex.GetLogEventMessage());
        }

        private void LogOutputPreparationFailed(string path, BuildXLException ex)
        {
            Logger.Log.PipProcessOutputPreparationFailed(
                m_loggingContext,
                m_pip.SemiStableHash,
                m_pipDescription,
                path,
                ex.LogEventErrorCode,
                ex.LogEventMessage);
        }

        private void LogInvalidWarningRegex()
        {
            Logger.Log.PipProcessInvalidWarningRegex(
                m_loggingContext,
                m_pip.SemiStableHash,
                m_pipDescription,
                m_pathTable.StringTable.GetString(m_pip.WarningRegex.Pattern),
                m_pip.WarningRegex.Options.ToString());
        }

        private void LogInvalidErrorRegex()
        {
            Logger.Log.PipProcessInvalidErrorRegex(
                m_loggingContext,
                m_pip.SemiStableHash,
                m_pipDescription,
                m_pathTable.StringTable.GetString(m_pip.ErrorRegex.Pattern),
                m_pip.ErrorRegex.Options.ToString());
        }

        private void LogCommandLineTooLong(SandboxedProcessInfo info)
        {
            Logger.Log.PipProcessCommandLineTooLong(
                m_loggingContext,
                m_pip.SemiStableHash,
                m_pipDescription,
                info.GetCommandLine(),
                SandboxedProcessInfo.MaxCommandLineLength);
        }

        private async Task<SandboxedProcessPipExecutionResult> ProcessSandboxedProcessResultAsync(
            LoggingContext loggingContext,
            SandboxedProcessResult result,
            long sandboxPrepMs,
            CancellationToken cancellationToken,
            HashSet<AbsolutePath> allInputPathsUnderSharedOpaques,
            ISandboxedProcess process)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            result = await (m_pluginEP?.ProcessResultAsync(result) ?? Task.FromResult(result));

            bool canceled = result.Killed && cancellationToken.IsCancellationRequested;
            bool hasMessageParsingError = result?.MessageProcessingFailure != null;
            bool exitedWithSuccessExitCode = m_pip.SuccessExitCodes.Length == 0
                ? result.ExitCode == 0
                : m_pip.SuccessExitCodes.Contains(result.ExitCode);
            ReportedProcess azWatsonDeadProcess = m_sandboxConfig.RetryOnAzureWatsonExitCode && OperatingSystemHelper.IsWindowsOS
                ? result.Processes?.FirstOrDefault(p => p.ExitCode == AzureWatsonExitCode)
                : null;
            bool exitedSuccessfullyAndGracefully = !canceled && exitedWithSuccessExitCode;
            bool exitedWithRetryAbleUserError = m_pip.RetryExitCodes.Contains(result.ExitCode) && m_remainingUserRetryCount > 0;
            long maxDetoursHeapSize = process.GetDetoursMaxHeapSize() + result.DetoursMaxHeapSize;

            Dictionary<string, int> pipProperties = null;

            bool allOutputsPresent = false;
            bool loggingSuccess = true;

            ProcessTimes primaryProcessTimes = result.PrimaryProcessTimes;
            JobObject.AccountingInformation? jobAccounting = result.JobAccountingInformation;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var fileAccessReportingContext = new FileAccessReportingContext(
                loggingContext,
                m_context,
                m_sandboxConfig,
                m_pip,
                m_validateDistribution,
                m_fileAccessAllowlist);

            // If this operation fails, error was logged already
            bool sharedOpaqueProcessingSuccess = TryGetObservedFileAccesses(
                fileAccessReportingContext,
                result,
                allInputPathsUnderSharedOpaques,
                out var unobservedOutputs,
                out var sharedDynamicDirectoryWriteAccesses,
                out SortedReadOnlyArray<ObservedFileAccess, ObservedFileAccessExpandedPathComparer> observed,
                out IReadOnlySet<AbsolutePath> createdDirectories);

            LogSubPhaseDuration(m_loggingContext, m_pip, SandboxedProcessCounters.SandboxedPipExecutorPhaseGettingObservedFileAccesses, stopwatch.Elapsed, $"(count: {observed.Length})");

            TimeSpan processTotalWallClockTime = primaryProcessTimes.TotalWallClockTime;

            // Note that when MonitorFileAccesses flag is set to false, we should not assume the various reported-access sets are non-null.
            if (MonitorFileAccesses)
            {
                // First remove all the paths that are Injectable from in the process.
                RemoveInjectableFileAccesses(result.AllUnexpectedFileAccesses);

                foreach (ReportedFileAccess unexpectedFileAccess in result.AllUnexpectedFileAccesses)
                {
                    Contract.Assume(
                        unexpectedFileAccess.Status == FileAccessStatus.Denied ||
                        unexpectedFileAccess.Status == FileAccessStatus.CannotDeterminePolicy);

                    fileAccessReportingContext.ReportFileAccessDeniedByManifest(unexpectedFileAccess);
                }
            }

            if (result.TimedOut)
            {
                if (result.DumpCreationException != null)
                {
                    Logger.Log.PipFailedToCreateDumpFile(
                        loggingContext,
                        m_pip.SemiStableHash,
                        m_pipDescription,
                        result.DumpCreationException.GetLogEventMessage());
                }
            }
            else
            {
                TimeSpan warningTimeout = GetEffectiveTimeout(
                    m_pip.WarningTimeout,
                    m_sandboxConfig.DefaultWarningTimeout,
                    m_sandboxConfig.WarningTimeoutMultiplier);

                if (processTotalWallClockTime > warningTimeout)
                {
                    LogTookTooLongWarning(m_timeout, processTotalWallClockTime, warningTimeout);
                }

                // There are cases where the process exit code is not successful and the injection has failed.
                // (ExitCode code is set by Windows - TerminateProcess, kill(), process crash).
                // The build needs to fail in this case(s) as well and log that we had injection failure.
                if (result.HasDetoursInjectionFailures)
                {
                    Logger.Log.PipProcessFinishedDetourFailures(loggingContext, m_pip.SemiStableHash, m_pipDescription);
                }

                if (exitedSuccessfullyAndGracefully)
                {
                    Logger.Log.PipProcessFinished(loggingContext, m_pip.SemiStableHash, m_pipDescription, result.ExitCode);
                    allOutputsPresent = CheckExpectedOutputs();
                }
                else
                {
                    if (!canceled)
                    {
                        LogFinishedFailed(result);
                        (bool success, Dictionary<string, int> setPipProperties) = await TrySetPipPropertiesAsync(result);
                        pipProperties = setPipProperties;

                        if (!success)
                        {
                            Contract.Assert(loggingContext.ErrorWasLogged, "Error should be logged upon TrySetPipPropertiesAsync failure.");
                            // There was an error logged when extracting properties from the pip. The pip needs to fail and not retry
                            loggingSuccess = false;
                        }
                        else
                        {
                            if (exitedWithRetryAbleUserError)
                            {
                                Tuple<AbsolutePath, Encoding> encodedStandardError = null;
                                Tuple<AbsolutePath, Encoding> encodedStandardOutput = null;

                                if (await TrySaveAndLogStandardOutputAsync(result) && await TrySaveAndLogStandardErrorAsync(result))
                                {
                                    encodedStandardOutput = GetEncodedStandardConsoleStream(result.StandardOutput);
                                    encodedStandardError = GetEncodedStandardConsoleStream(result.StandardError);
                                    return SandboxedProcessPipExecutionResult.RetryProcessDueToUserSpecifiedExitCode(
                                        result.ExitCode,
                                        primaryProcessTimes,
                                        jobAccounting,
                                        result.DetouringStatuses,
                                        sandboxPrepMs,
                                        sw.ElapsedMilliseconds,
                                        result.ProcessStartTime,
                                        maxDetoursHeapSize,
                                        encodedStandardError,
                                        encodedStandardOutput,
                                        pipProperties,
                                        sharedDynamicDirectoryWriteAccesses,
                                        unexpectedFileAccesses: fileAccessReportingContext);
                                }

                                Contract.Assert(loggingContext.ErrorWasLogged, "Error should be logged upon TrySaveAndLogStandardOutput/Error failure.");
                                // There was an error logged when saving stdout or stderror.
                                loggingSuccess = false;
                            }
                            else if (azWatsonDeadProcess != null)
                            {
                                loggingSuccess = false;
                            }
                        }
                    }
                }
            }

            int numSurvivingChildErrors = 0;
            if (!canceled && result.SurvivingChildProcesses?.Any() == true)
            {
                numSurvivingChildErrors = ReportSurvivingChildProcesses(result);
            }

            if (!canceled && azWatsonDeadProcess != null)
            {
                Logger.Log.PipFinishedWithSomeProcessExitedWithAzureWatsonExitCode(
                    loggingContext,
                    m_pip.SemiStableHash,
                    m_pipDescription,
                    azWatsonDeadProcess.Path,
                    azWatsonDeadProcess.ProcessArgs,
                    azWatsonDeadProcess.ProcessId);
            }

            stopwatch.Restart();

            if (result.Killed && numSurvivingChildErrors > 0)
            {
                LogChildrenSurvivedKilled();
            }

            bool mainProcessExitedCleanly = (!result.Killed || numSurvivingChildErrors == 0) && exitedSuccessfullyAndGracefully;

            if (!mainProcessExitedCleanly)
            {
                Logger.Log.PipExitedUncleanly(
                    loggingContext,
                    m_pip.SemiStableHash,
                    m_pipDescription,
                    canceled,
                    result.ExitCode,
                    result.Killed,
                    numSurvivingChildErrors);
            }

            // Observe that in some cases stderr reports to have length, but it is zero. Make sure that length > 0 to conclude stderr was written.
            bool failedDueToWritingToStdErr = m_pip.WritingToStandardErrorFailsExecution && result.StandardError.HasLength && result.StandardError.Length > 0;
            if (failedDueToWritingToStdErr)
            {
                Logger.Log.PipProcessWroteToStandardErrorOnCleanExit(
                    m_loggingContext,
                    pipSemiStableHash: m_pip.SemiStableHash,
                    pipDescription: m_pipDescription,
                    pipSpecPath: m_pip.Provenance.Token.Path.ToString(m_context.PathTable),
                    pipWorkingDirectory: m_pip.WorkingDirectory.ToString(m_context.PathTable));
            }

            // This is the overall success of the process. At a high level, these are the things that can cause a process pip to fail:
            //      1. The process being killed (built into mainProcessExitedCleanly)
            //      2. The process not exiting with the appropriate exit code (mainProcessExitedCleanly)
            //      3. The process not creating all outputs (allOutputsPresent)
            //      4. The process wrote to standard error, and even though it may have exited with a succesfull exit code, WritingToStandardErrorFailsPip
            //         is set (failedDueToWritingToStdErr)
            bool mainProcessSuccess = mainProcessExitedCleanly && allOutputsPresent && !failedDueToWritingToStdErr;

            bool standardOutHasBeenWrittenToLog = false;
            bool errorOrWarnings = !mainProcessSuccess || m_numWarnings > 0;

            bool shouldPersistStandardOutput = errorOrWarnings || m_pip.StandardOutput.IsValid;
            if (shouldPersistStandardOutput)
            {
                if (!await TrySaveStandardOutputAsync(result))
                {
                    loggingSuccess = false;
                }
            }

            bool shouldPersistStandardError = !canceled && (errorOrWarnings || m_pip.StandardError.IsValid);
            if (shouldPersistStandardError)
            {
                if (!await TrySaveStandardErrorAsync(result))
                {
                    loggingSuccess = false;
                }
            }

            LogSubPhaseDuration(m_loggingContext, m_pip, SandboxedProcessCounters.SandboxedPipExecutorPhaseProcessingStandardOutputs, stopwatch.Elapsed);

            stopwatch.Restart();

            bool errorWasTruncated = false;
            // if some outputs are missing or the process wrote to stderr, we are logging this process as a failed one (even if it finished with a success exit code).
            if ((!mainProcessExitedCleanly || !allOutputsPresent || failedDueToWritingToStdErr) && !canceled && loggingSuccess)
            {
                standardOutHasBeenWrittenToLog = true;

                // We only checked if all outputs are present if exitedSuccessfullyAndGracefully is true. So if exitedSuccessfullyAndGracefully is false,
                // we don't log anything at this respect.
                LogErrorResult logErrorResult = await TryLogErrorAsync(result, (!exitedSuccessfullyAndGracefully || allOutputsPresent), failedDueToWritingToStdErr, processTotalWallClockTime);
                errorWasTruncated = logErrorResult.ErrorWasTruncated;
                loggingSuccess = logErrorResult.Success;
            }

            if (m_numWarnings > 0 && loggingSuccess && !canceled)
            {
                if (!await TryLogWarningAsync(result.StandardOutput, result.StandardError))
                {
                    loggingSuccess = false;
                }
            }

            // The full output may be requested based on the result of the pip. If the pip failed, the output may have been reported
            // in TryLogErrorAsync above. Only replicate the output if the error was truncated due to an error regex
            if ((!standardOutHasBeenWrittenToLog || errorWasTruncated) && loggingSuccess && !canceled)
            {
                // If the pip succeeded, we must check if one of the non-error output modes have been chosen
                if ((m_sandboxConfig.OutputReportingMode == OutputReportingMode.FullOutputAlways) ||
                    (m_sandboxConfig.OutputReportingMode == OutputReportingMode.FullOutputOnWarningOrError && errorOrWarnings) ||
                    (m_sandboxConfig.OutputReportingMode == OutputReportingMode.FullOutputOnError && !mainProcessSuccess))
                {
                    if (!await TryLogOutputWithTimeoutAsync(result, loggingContext))
                    {
                        loggingSuccess = false;
                    }
                }
            }

            LogSubPhaseDuration(m_loggingContext, m_pip, SandboxedProcessCounters.SandboxedPipExecutorPhaseLoggingOutputs, stopwatch.Elapsed);

            bool shouldCreateTraceFile = m_pip.TraceFile.IsValid;
            if (shouldCreateTraceFile)
            {
                if (!await TrySaveTraceFileAsync(result))
                {
                    loggingSuccess = false;
                }
            }

            // N.B. here 'observed' means 'all', not observed in the terminology of SandboxedProcessPipExecutor.
            List<ReportedFileAccess> allFileAccesses = null;

            if (MonitorFileAccesses && (m_sandboxConfig.LogObservedFileAccesses || m_verboseProcessLoggingEnabled))
            {
                allFileAccesses = new List<ReportedFileAccess>(result.FileAccesses);
                allFileAccesses.AddRange(result.AllUnexpectedFileAccesses);
            }

            m_logger.LogProcessObservation(
                processes: m_sandboxConfig.LogProcesses ? result.Processes : null,
                fileAccesses: allFileAccesses,
                detouringStatuses: result.DetouringStatuses);

            if (mainProcessSuccess && loggingSuccess)
            {
                Contract.Assert(!shouldPersistStandardOutput || result.StandardOutput.IsSaved);
                Contract.Assert(!shouldPersistStandardError || result.StandardError.IsSaved);
            }

            // Log a warning for having converted ReadWrite file access request to Read file access request and the pip was not canceled and failed.
            if (!mainProcessSuccess && !canceled && result.HasReadWriteToReadFileAccessRequest)
            {
                Logger.Log.ReadWriteFileAccessConvertedToReadWarning(loggingContext, m_pip.SemiStableHash, m_pipDescription);
            }

            var finalStatus = canceled
                ? SandboxedProcessPipExecutionStatus.Canceled
                : (mainProcessSuccess && loggingSuccess
                    ? SandboxedProcessPipExecutionStatus.Succeeded
                    : SandboxedProcessPipExecutionStatus.ExecutionFailed);

            if (result.StandardInputException != null && finalStatus == SandboxedProcessPipExecutionStatus.Succeeded)
            {
                // When process execution succeeded, standard input exception should not occur.

                // Log error to correlate the pip with the standard input exception.
                Logger.Log.PipProcessStandardInputException(
                    loggingContext,
                    m_pip.SemiStableHash,
                    m_pipDescription,
                    m_pip.Provenance.Token.Path.ToString(m_pathTable),
                    m_workingDirectory,
                    result.StandardInputException.Message + Environment.NewLine + result.StandardInputException.StackTrace);
            }

            if (hasMessageParsingError)
            {
                Logger.Log.PipProcessMessageParsingError(
                    loggingContext,
                    m_pip.SemiStableHash,
                    m_pipDescription,
                    result.MessageProcessingFailure.Content);
            }

            SandboxedProcessPipExecutionStatus status = SandboxedProcessPipExecutionStatus.ExecutionFailed;
            RetryInfo retryInfo = null;
            if (result.HasDetoursInjectionFailures)
            {
                status = SandboxedProcessPipExecutionStatus.PreparationFailed;
            }
            else if (canceled)
            {
                status = SandboxedProcessPipExecutionStatus.Canceled;
            }
            else if (!sharedOpaqueProcessingSuccess)
            {
                status = SandboxedProcessPipExecutionStatus.SharedOpaquePostProcessingFailed;
            }
            else if (mainProcessSuccess && loggingSuccess && !hasMessageParsingError)
            {
                status = SandboxedProcessPipExecutionStatus.Succeeded;
            }

            if (!mainProcessSuccess && !result.TimedOut && !canceled && azWatsonDeadProcess != null)
            {
                // Retry if main process failed and there is a process (can be a child process) that exits with exit code 0xDEAD.
                Logger.Log.PipRetryDueToExitedWithAzureWatsonExitCode(
                    m_loggingContext,
                    m_pip.SemiStableHash,
                    m_pipDescription,
                    azWatsonDeadProcess.Path,
                    azWatsonDeadProcess.ProcessId);

                retryInfo = RetryInfo.GetDefault(RetryReason.AzureWatsonExitCode);
            }
            else if (MonitorFileAccesses
                && status == SandboxedProcessPipExecutionStatus.Succeeded
                && m_pip.PipType == PipType.Process
                && unobservedOutputs != null)
            {
                foreach (var outputPath in unobservedOutputs)
                {
                    // Report non observed access only if the output exists.
                    // If the output was not produced, missing declared output logic
                    // will report another error.
                    string expandedOutputPath = outputPath.ToString(m_pathTable);
                    bool isFile;
                    if ((isFile = FileUtilities.FileExistsNoFollow(expandedOutputPath)) || FileUtilities.DirectoryExistsNoFollow(expandedOutputPath))
                    {
                        Logger.Log.PipOutputNotAccessed(
                          m_loggingContext,
                          m_pip.SemiStableHash,
                          m_pipDescription,
                          "'" + expandedOutputPath + "'. " + (isFile ? "Found path is a file" : "Found path is a directory"));

                        status = SandboxedProcessPipExecutionStatus.FileAccessMonitoringFailed;
                        retryInfo = RetryInfo.GetDefault(RetryReason.OutputWithNoFileAccessFailed);
                    }
                }
            }

            // If a PipProcessError was logged, the pip cannot be marked as succeeded
            Contract.Assert(!standardOutHasBeenWrittenToLog || status != SandboxedProcessPipExecutionStatus.Succeeded);

            return new SandboxedProcessPipExecutionResult(
                status: status,
                observedFileAccesses: observed,
                sharedDynamicDirectoryWriteAccesses: sharedDynamicDirectoryWriteAccesses,
                unexpectedFileAccesses: fileAccessReportingContext,
                encodedStandardOutput: loggingSuccess && shouldPersistStandardOutput ? GetEncodedStandardConsoleStream(result.StandardOutput) : null,
                encodedStandardError: loggingSuccess && shouldPersistStandardError ? GetEncodedStandardConsoleStream(result.StandardError) : null,
                // Treat Azure Watson dead process as an injected warning, so the process may not be cached if a warning is treated as an error.
                numberOfWarnings: m_numWarnings,
                primaryProcessTimes: primaryProcessTimes,
                jobAccountingInformation: jobAccounting,
                exitCode: result.ExitCode,
                sandboxPrepMs: sandboxPrepMs,
                processSandboxedProcessResultMs: sw.ElapsedMilliseconds,
                processStartTime: result.ProcessStartTime,
                allReportedFileAccesses: allFileAccesses,
                detouringStatuses: result.DetouringStatuses,
                maxDetoursHeapSize: maxDetoursHeapSize,
                pipProperties: pipProperties,
                timedOut: result.TimedOut,
                hasAzureWatsonDeadProcess: azWatsonDeadProcess != null,
                retryInfo: retryInfo,
                createdDirectories: createdDirectories);
        }

        private async Task<(bool success, Dictionary<string, int> pipProperties)> TrySetPipPropertiesAsync(SandboxedProcessResult result)
        {
            OutputFilter propertyFilter = OutputFilter.GetPipPropertiesFilter();

            var filteredErr = await TryFilterAsync(result.StandardError, propertyFilter, appendNewLine: true);
            var filteredOut = await TryFilterAsync(result.StandardOutput, propertyFilter, appendNewLine: true);
            if (filteredErr.HasError || filteredOut.HasError)
            {
                // We have logged an error when processing the standard error or standard output stream.
                return (success: false, pipProperties: null);
            }

            string errorMatches = filteredErr.FilteredOutput;
            string outputMatches = filteredOut.FilteredOutput;
            string allMatches = errorMatches + Environment.NewLine + outputMatches;

            if (!string.IsNullOrWhiteSpace(allMatches))
            {
                string[] matchedProperties = allMatches.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                var propertiesDictionary = new Dictionary<string, int>(matchedProperties.Length);
                foreach (var property in matchedProperties)
                {
                    // Guard against duplicates in properties but only count the property once for the pip
                    propertiesDictionary[property] = 1;
                }

                return (success: true, pipProperties: propertiesDictionary);
            }

            return (success: true, pipProperties: null);
        }

        private Tuple<AbsolutePath, Encoding> GetEncodedStandardConsoleStream(SandboxedProcessOutput output)
        {
            Contract.Requires(output.IsSaved);
            Contract.Requires(!output.HasException);

            return Tuple.Create(AbsolutePath.Create(m_pathTable, output.FileName), output.Encoding);
        }

        private bool TryPrepareFileAccessMonitoring(HashSet<AbsolutePath> allInputPathsUnderSharedOpaques)
        {
            if (!PrepareFileAccessMonitoringCommon())
            {
                return false;
            }

            TryPrepareFileAccessMonitoringForPip(m_pip, allInputPathsUnderSharedOpaques);

            return true;
        }

        private bool PrepareFileAccessMonitoringCommon()
        {
            if (m_pip.IsService || m_pip.IsStartOrShutdownKind)
            {
                // Service pips is also referred to as start pips.
                // Service pip is allowed to read all files because it has to read all files on behalf of its clients.
                // This behavior is safe because service pip and its clients are not cacheable.
                // Service pips do not report, because this introduces unnecessary attempts to process observed inputs.
                // Adding the same scope for stop pips as they are not cacheable as well.
                m_fileAccessManifest.AddScope(
                    AbsolutePath.Invalid,
                    FileAccessPolicy.MaskNothing,
                    FileAccessPolicy.AllowReadAlways);
            }
            else
            {
                // The default policy is to only allow absent file probes. But if undeclared source reads are enabled, we allow any kind of read
                // All directory enumerations are reported unless explicitly excluded
                var rootFileAccessPolicy = m_pip.AllowUndeclaredSourceReads
                    ? (FileAccessPolicy.AllowReadAlways | FileAccessPolicy.ReportAccess)
                    : (FileAccessPolicy.AllowReadIfNonexistent | FileAccessPolicy.ReportDirectoryEnumerationAccess | FileAccessPolicy.ReportAccessIfNonexistent);

                m_fileAccessManifest.AddScope(
                    AbsolutePath.Invalid,
                    FileAccessPolicy.MaskNothing,
                    rootFileAccessPolicy);
            }

            // Add a scope entry to allow reading in the BuildXL directories. We do read at least BuildXLServices.dll for the 2 currently supported platforms.
            if (m_buildEngineDirectory.IsValid)
            {
                m_fileAccessManifest.AddScope(
                    m_buildEngineDirectory,
                    FileAccessPolicy.MaskNothing,
                    FileAccessPolicy.AllowReadAlways);
            }

            // CreateDirectory access is allowed anywhere under a writable root. The rationale is that this is safe because file writes
            // are still blocked. If a previous build or external factor had created the directory, the CreateDirectory would be
            // interpreted as a probe anyway. So blocking it on the first build and allowing it on the second is inconsistent.
            //
            // This behavior is now controlled by the command line argument. We want to prevent the tools to secretly communicate
            // between each other by means of creating empty directories. Because empty directories are not stored in cache, this leads to
            // a non deterministic behavior. When EnforceAccessPoliciesOnDirectoryCreation is set to true, we will not downgrade
            // the CreateDirectory to a read-only probe in Detours and will emit file access error/warning for all CreateDirectory
            // calls that do not have an explicit allowing policy in the manifest, regardless of whether the corresponding directory exists or not.
            if (m_semanticPathExpander != null && !m_sandboxConfig.EnforceAccessPoliciesOnDirectoryCreation)
            {
                foreach (AbsolutePath writableRoot in m_semanticPathExpander.GetWritableRoots())
                {
                    m_fileAccessManifest.AddScope(writableRoot, FileAccessPolicy.MaskNothing, FileAccessPolicy.AllowCreateDirectory);
                }
            }

            // Untrack the globally untracked paths specified in the configuration
            if (m_pip.RequireGlobalDependencies)
            {
                foreach (var path in m_sandboxConfig.GlobalUnsafeUntrackedScopes)
                {
                    // Translate the path and untrack the translated one
                    if (m_fileAccessManifest.DirectoryTranslator != null)
                    {
                        var pathString = path.ToString(m_pathTable);
                        var translatedPathString = m_fileAccessManifest.DirectoryTranslator.Translate(pathString);
                        var translatedPath = AbsolutePath.Create(m_pathTable, translatedPathString);

                        if (path != translatedPath)
                        {
                            AddUntrackedScopeToManifest(translatedPath);
                        }
                    }

                    // Untrack the original path
                    AddUntrackedScopeToManifest(path);
                }
            }

            if (ShouldSandboxedProcessExecuteInVm)
            {
                // Untrack copy-local shim directory because it is meant to be abstracted away from customer's build.
                // It's used as a compatibility measure and therefore the customer's build shouldn't know about it.
                AddUntrackedScopeToManifest(AbsolutePath.Create(m_pathTable, VmSpecialFilesAndDirectories.CopyLocalShimDirectory));
            }

            foreach (var path in m_directorySymlinksAsDirectories)
            {
                m_fileAccessManifest.AddPath(
                    path,
                    mask: FileAccessPolicy.MaskNothing,
                    values: FileAccessPolicy.TreatDirectorySymlinkAsDirectory);
            }

            if (m_sandboxConfig.DirectoriesToEnableFullReparsePointParsing != null)
            {
                foreach (var directoryToEnableFullReparsePointParsing in m_sandboxConfig.DirectoriesToEnableFullReparsePointParsing)
                {
                    m_fileAccessManifest.AddScope(
                        directoryToEnableFullReparsePointParsing,
                        mask: FileAccessPolicy.MaskNothing,
                        values: FileAccessPolicy.EnableFullReparsePointParsing);

                    if (m_directoryTranslator != null)
                    {
                        m_fileAccessManifest.AddScope(
                            m_directoryTranslator.Translate(directoryToEnableFullReparsePointParsing, m_pathTable),
                            mask: FileAccessPolicy.MaskNothing,
                            values: FileAccessPolicy.EnableFullReparsePointParsing);
                    }
                }
            }

            if (!OperatingSystemHelper.IsUnixOS)
            {
                var binaryPaths = new BinaryPaths();

                AddUntrackedScopeToManifest(AbsolutePath.Create(m_pathTable, binaryPaths.DllDirectoryX64));
                AddUntrackedScopeToManifest(AbsolutePath.Create(m_pathTable, binaryPaths.DllDirectoryX86));
            }

            // For some static system mounts (currently only for AppData\Roaming) we allow CreateDirectory requests for all processes.
            // This is done because many tools are using CreateDirectory to check for the existence of that directory. Since system directories
            // always exist, allowing such requests would not lead to any changes on the disk. Moreover, we are applying an exact path policy (i.e., not a scope policy).
            if (m_semanticPathExpander != null)
            {
                foreach (AbsolutePath path in m_semanticPathExpander.GetPathsWithAllowedCreateDirectory())
                {
                    m_fileAccessManifest.AddPath(path, mask: FileAccessPolicy.MaskNothing, values: FileAccessPolicy.AllowCreateDirectory);
                }
            }

            m_fileAccessManifest.ReportUnexpectedFileAccesses = true;
            m_fileAccessManifest.ReportFileAccesses =
                m_sandboxConfig.LogObservedFileAccesses
                // When sandboxed process needs to be remoted, the remoting infrastructure, like AnyBuild, typically requires all
                // reported file accesses.
                || SandboxedProcessShouldExecuteRemote
                || m_verboseProcessLoggingEnabled;
            m_fileAccessManifest.BreakOnUnexpectedAccess = m_sandboxConfig.BreakOnUnexpectedFileAccess;
            m_fileAccessManifest.FailUnexpectedFileAccesses = m_sandboxConfig.FailUnexpectedFileAccesses;
            m_fileAccessManifest.ReportProcessArgs = m_sandboxConfig.LogProcesses;
            m_fileAccessManifest.EnforceAccessPoliciesOnDirectoryCreation = m_sandboxConfig.EnforceAccessPoliciesOnDirectoryCreation;

            bool allowInternalErrorsLogging = m_sandboxConfig.AllowInternalDetoursErrorNotificationFile;
            m_fileAccessManifest.CheckDetoursMessageCount = m_sandboxConfig.CheckDetoursMessageCount;

            if (allowInternalErrorsLogging || m_fileAccessManifest.CheckDetoursMessageCount)
            {
                if (OperatingSystemHelper.IsWindowsOS)
                {
                    // Create unique file name.
                    m_detoursFailuresFile = GetDetoursInternalErrorFilePath();

                    // Delete the file
                    if (FileUtilities.FileExistsNoFollow(m_detoursFailuresFile))
                    {
                        Analysis.IgnoreResult(FileUtilities.TryDeleteFile(m_detoursFailuresFile, tempDirectoryCleaner: m_tempDirectoryCleaner));
                    }
                }
                else
                {
                    // this doesn't point to a real path, but to align with the value used for the windows side, we will use the failures file for now for the semaphore name
                    m_detoursFailuresFile = "/" + Guid.NewGuid().ToString().Replace("-", string.Empty) + m_pip.FormattedSemiStableHash;
                }

                if (allowInternalErrorsLogging)
                {
                    m_fileAccessManifest.InternalDetoursErrorNotificationFile = m_detoursFailuresFile;
                }

                if (!SetMessageCountSemaphoreIfRequested())
                {
                    return false;
                }
            }

            m_fileAccessManifest.HardExitOnErrorInDetours = m_sandboxConfig.HardExitOnErrorInDetours;
            m_fileAccessManifest.LogProcessDetouringStatus = m_sandboxConfig.LogProcessDetouringStatus;

            if (m_sandboxConfig.FileAccessIgnoreCodeCoverage)
            {
                m_fileAccessManifest.IgnoreCodeCoverage = true;
            }

            return true;
        }

        private bool SetMessageCountSemaphoreIfRequested()
        {
            if (!m_fileAccessManifest.CheckDetoursMessageCount)
            {
                return true;
            }

            if ((OperatingSystemHelper.IsWindowsOS || OperatingSystemHelper.IsLinuxOS) && !SandboxedProcessNeedsExecuteExternal)
            {
                // Semaphore names don't allow '\\' chars.
                if (!m_fileAccessManifest.SetMessageCountSemaphore(m_detoursFailuresFile.Replace('\\', '_'), out string message))
                {
                    Logger.Log.LogMessageCountSemaphoreOpenFailure(m_loggingContext, m_pip.SemiStableHash, m_pipDescription, message);
                    return false;
                }
            }

            return true;

        }

        private void AddUntrackedScopeToManifest(AbsolutePath path, FileAccessManifest manifest = null)
        {
            (manifest ?? m_fileAccessManifest).AddScope(
                path,
                mask: m_excludeReportAccessMask,
                values: FileAccessPolicy.AllowAll | FileAccessPolicy.AllowRealInputTimestamps);
            m_remoteSbDataBuilder?.AddUntrackedScope(path);
        }

        private void AddUntrackedPathToManifest(AbsolutePath path, FileAccessManifest manifest = null)
        {
            (manifest ?? m_fileAccessManifest).AddPath(
                path,
                mask: m_excludeReportAccessMask,
                values: FileAccessPolicy.AllowAll | FileAccessPolicy.AllowRealInputTimestamps);
            m_remoteSbDataBuilder?.AddUntrackedPath(path);
        }

        private void AllowCreateDirectoryForDirectoriesOnPath(AbsolutePath path, HashSet<AbsolutePath> processedPaths, bool startWithParent = true)
        {
            Contract.Assert(path.IsValid);

            if (m_sandboxConfig.EnforceAccessPoliciesOnDirectoryCreation)
            {
                var parentPath = startWithParent ? path.GetParent(m_pathTable) : path;
                while (parentPath.IsValid && !processedPaths.Contains(parentPath))
                {
                    processedPaths.Add(parentPath);
                    m_fileAccessManifest.AddPath(parentPath, values: FileAccessPolicy.AllowCreateDirectory, mask: FileAccessPolicy.MaskNothing);
                    parentPath = parentPath.GetParent(m_pathTable);
                }
            }
        }

        private void TryPrepareFileAccessMonitoringForPip(Process pip, HashSet<AbsolutePath> allInputPathsUnderSharedOpaques)
        {
            // While we always define the %TMP% and %TEMP% variables to point to some legal directory
            // (many tools fail if those variables are not defined),
            // we don't allow any access to the default temp directory.
            m_fileAccessManifest.AddScope(
                AbsolutePath.Create(m_pathTable, PipEnvironment.RestrictedTemp),
                FileAccessPolicy.MaskAll,
                FileAccessPolicy.Deny);

            using (var processedPathWrapper = Pools.GetAbsolutePathSet())
            using (var untrackedPathsWrapper = Pools.GetAbsolutePathSet())
            using (var untrackedScopesWrapper = Pools.GetAbsolutePathAncestorChecker())
            {
                var processedPaths = processedPathWrapper.Instance;
                using (var wrapper = Pools.GetAbsolutePathSet())
                {
                    var outputFiles = wrapper.Instance;

                    foreach (FileArtifactWithAttributes attributedOutput in pip.FileOutputs)
                    {
                        var output = attributedOutput.ToFileArtifact();

                        if (output != pip.StandardOutput && output != pip.StandardError && output != pip.TraceFile)
                        {
                            // We mask 'report' here, since outputs are expected written and should never fail observed-access validation (directory dependency, etc.)
                            // Note that they would perhaps fail otherwise (simplifies the observed access checks in the first place).
                            // We allow the real input timestamps to be seen since if any of these outputs are rewritten, we should block input timestamp faking in favor of output timestamp faking
                            m_fileAccessManifest.AddPath(
                                output.Path,
                                values: FileAccessPolicy.AllowAll | FileAccessPolicy.ReportAccess | FileAccessPolicy.AllowRealInputTimestamps, // Always report output file accesses, so we can validate that output was produced.
                                mask: m_excludeReportAccessMask);
                            outputFiles.Add(output.Path);

                            AllowCreateDirectoryForDirectoriesOnPath(output.Path, processedPaths);
                        }
                    }

                    AddStaticFileDependenciesToFileAccessManifest(outputFiles, pip.Dependencies, allInputPathsUnderSharedOpaques);
                }

                var userProfilePath = AbsolutePath.Create(m_pathTable, s_userProfilePath);
                var redirectedUserProfilePath = m_layoutConfiguration != null && m_layoutConfiguration.RedirectedUserProfileJunctionRoot.IsValid
                    ? AbsolutePath.Create(m_pathTable, s_possibleRedirectedUserProfilePath)
                    : AbsolutePath.Invalid;

                var vmAdminProfilePath = ShouldSandboxedProcessExecuteInVm
                    ? AbsolutePath.Create(m_pathTable, VmConstants.UserProfile.Path)
                    : AbsolutePath.Invalid;

                HashSet<AbsolutePath> untrackedPaths = untrackedPathsWrapper.Instance;
                foreach (AbsolutePath path in pip.UntrackedPaths)
                {
                    // We mask Report to simplify handling of explicitly-reported directory-dependency or transitive-dependency accesses
                    // (they should never fail for untracked accesses, which should be invisible).

                    // We allow the real input timestamp to be seen for untracked paths. This is to preserve existing behavior, where the timestamp of untracked stuff is never modified.
                    addUntrackedPath(path, processedPaths, untrackedPaths);

                    var correspondingPath = CreatePathForActualRedirectedUserProfilePair(path, userProfilePath, redirectedUserProfilePath);
                    addUntrackedPath(correspondingPath, processedPaths, untrackedPaths);

                    if (ShouldSandboxedProcessExecuteInVm)
                    {
                        var vmPath = CreatePathForVmAdminUserProfile(vmAdminProfilePath, path, userProfilePath, redirectedUserProfilePath);
                        addUntrackedPath(vmPath, processedPaths, untrackedPaths);
                    }

                    // Untrack real logs directory if the redirected one is untracked.
                    if (m_loggingConfiguration != null && m_loggingConfiguration.RedirectedLogsDirectory.IsValid && m_loggingConfiguration.RedirectedLogsDirectory == path)
                    {
                        addUntrackedPath(m_loggingConfiguration.LogsDirectory, processedPaths, untrackedPaths);
                    }
                }

                if (ShouldSandboxedProcessExecuteInVm)
                {
                    // User profiles are tricky on the Admin VM since it's potentially running some of the first processes launched
                    // on a newly provisioned VM. When this happens various directories that would usually already be existing may be created.
                    // We assign a generous access policy for CreateDirectory access under the user profile to handle this.
                    m_fileAccessManifest.AddScope(userProfilePath, values: FileAccessPolicy.AllowCreateDirectory, mask: FileAccessPolicy.AllowAll);
                    m_fileAccessManifest.AddScope(redirectedUserProfilePath, values: FileAccessPolicy.AllowCreateDirectory, mask: FileAccessPolicy.AllowAll);
                }

                AbsolutePathAncestorChecker untrackedScopesChecker = untrackedScopesWrapper.Instance;
                foreach (AbsolutePath path in pip.UntrackedScopes)
                {
                    // Note that untracked scopes are quite dangerous. We allow writes, reads, and probes for non-existent files.
                    // We mask Report to simplify handling of explicitly-reported directory-dependence or transitive-dependency accesses
                    // (they should never fail for untracked accesses, which should be invisible).

                    // The default mask for untracked scopes is to not report anything.
                    // We block input timestamp faking for untracked scopes. This is to preserve existing behavior, where the timestamp of untracked stuff is never modified.
                    addUntrackedScope(path, processedPaths, untrackedScopesChecker);

                    var correspondingPath = CreatePathForActualRedirectedUserProfilePair(path, userProfilePath, redirectedUserProfilePath);
                    addUntrackedScope(correspondingPath, processedPaths, untrackedScopesChecker);

                    if (ShouldSandboxedProcessExecuteInVm)
                    {
                        var vmPath = CreatePathForVmAdminUserProfile(vmAdminProfilePath, path, userProfilePath, redirectedUserProfilePath);
                        addUntrackedScope(vmPath, processedPaths, untrackedScopesChecker);
                    }

                    // Untrack real logs directory if the redirected one is untracked.
                    if (m_loggingConfiguration != null && m_loggingConfiguration.RedirectedLogsDirectory.IsValid && m_loggingConfiguration.RedirectedLogsDirectory == path)
                    {
                        addUntrackedScope(m_loggingConfiguration.LogsDirectory, processedPaths, untrackedScopesChecker);
                    }
                }

                using (var wrapper = Pools.GetAbsolutePathSet())
                {
                    HashSet<AbsolutePath> outputDirectories = wrapper.Instance;

                    foreach (DirectoryArtifact directory in pip.DirectoryOutputs)
                    {
                        // Compute whether the output directory is under an exclusion. In that case we want to block writes, but configure the rest of the policy in the regular way so tools
                        // can operate normally as long as they don't produce any outputs under it
                        bool isUnderAnExclusion = pip.OutputDirectoryExclusions.Any(exclusion => directory.Path.IsWithin(m_pathTable, exclusion));

                        // We need to allow the real timestamp to be seen under a directory output (since these are outputs). If this directory output happens to share the root with
                        // a directory dependency (shared opaque case), this is overridden for specific input files when processing directory dependencies below
                        var values =
                            // If under an exclusion, only allow reads. Otherwise all operations are allowed.
                            (isUnderAnExclusion ? FileAccessPolicy.AllowReadAlways : FileAccessPolicy.AllowAll)
                            | FileAccessPolicy.AllowRealInputTimestamps
                            // For shared opaques, we need to know the (write) accesses that occurred, since we determine file ownership based on that.
                            | (directory.IsSharedOpaque ? FileAccessPolicy.ReportAccess : FileAccessPolicy.Deny)
                            // For shared opaques and if allowed undeclared source reads is enabled, make sure that any file used as an undeclared input under the
                            // shared opaque gets deny write access. Observe that with exclusive opaques they are wiped out before the pip runs, so it is moot to check for inputs
                            // TODO: considering configuring this policy for all shared opaques, and not only when AllowedUndeclaredSourceReads is set. The case of a write on an undeclared
                            // input is more likely to happen when undeclared sources are allowed, but also possible otherwise. For now, this is just a conservative way to try this feature
                            // out for a subset of our scenarios.
                            | (m_pip.AllowUndeclaredSourceReads && directory.IsSharedOpaque ? FileAccessPolicy.OverrideAllowWriteForExistingFiles : FileAccessPolicy.Deny);

                        // For exclusive opaques, we don't need reporting back and the content is discovered by enumerating the disk
                        var mask = directory.IsSharedOpaque ? FileAccessPolicy.MaskNothing : m_excludeReportAccessMask;

                        // If the output directory is under an exclusion, block writes and symlink creation
                        mask &= isUnderAnExclusion ? ~FileAccessPolicy.AllowWrite & ~FileAccessPolicy.AllowSymlinkCreation : FileAccessPolicy.MaskNothing;

                        var directoryPath = directory.Path;

                        m_fileAccessManifest.AddScope(directoryPath, values: values, mask: mask);
                        AllowCreateDirectoryForDirectoriesOnPath(directoryPath, processedPaths);

                        outputDirectories.Add(directoryPath);
                    }

                    // Directory artifact dependencies are supposed to be immutable. Therefore it is never okay to write, but it is safe to probe for non-existent files
                    // (the set of files is stable). We turn on explicit access reporting so that reads and
                    // failed probes can be used by the scheduler (rather than fingerprinting the precise directory contents).
                    foreach (DirectoryArtifact directory in pip.DirectoryDependencies)
                    {
                        if (directory.IsSharedOpaque)
                        {
                            // All members of the shared opaque need to be added to the manifest explicitly so timestamp faking happens for them.
                            AddSharedOpaqueInputContentToManifest(directory, allInputPathsUnderSharedOpaques, untrackedPaths, untrackedScopesChecker);
                        }

                        // If this directory dependency is also a directory output, then we don't set any additional policy, i.e.,
                        // we don't want to restrict writes.
                        if (!outputDirectories.Contains(directory.Path))
                        {
                            // Directories here represent inputs, we want to apply the timestamp faking logic
                            var mask = DefaultMask;
                            // Allow read accesses and reporting. Reporting is needed since these may be dynamic accesses and we need to cross check them
                            var values = FileAccessPolicy.AllowReadIfNonexistent | FileAccessPolicy.AllowRead | FileAccessPolicy.ReportAccess;

                            // In the case of a writable sealed directory under a produced shared opaque, we don't want to block writes
                            // for the entire cone: some files may be dynamically written in the context of the shared opaque that may fall
                            // under the cone of the sealed directory. So for partial sealed directories and shared opaque directories that
                            // are under a produced shared opaque we don't establish any specific policies.
                            var kind = m_directoryArtifactContext.GetSealDirectoryKind(directory);
                            if (!(IsPathUnderASharedOpaqueRoot(directory.Path) && (kind == SealDirectoryKind.Partial || kind == SealDirectoryKind.SharedOpaque)))
                            {
                                // TODO: Consider an UntrackedScope or UntrackedPath above that has exactly the same path.
                                //       That results in a manifest entry with AllowWrite masked out yet re-added via AllowWrite in the values.
                                //       Maybe the precedence should be (parent | values) & mask instead of (parent & mask) | values.
                                mask &= ~FileAccessPolicy.AllowWrite;
                                AllowCreateDirectoryForDirectoriesOnPath(directory.Path, processedPaths, false);
                            }

                            m_fileAccessManifest.AddScope(directory, mask: mask, values: values);
                            m_remoteSbDataBuilder?.AddDirectoryDependency(directory.Path);
                        }
                    }

                    // Process exclusions
                    foreach (AbsolutePath exclusion in pip.OutputDirectoryExclusions)
                    {
                        // We deny any writes (including symlink creation) and leave the rest of the policy as is
                        m_fileAccessManifest.AddScope(exclusion, ~FileAccessPolicy.AllowWrite & ~FileAccessPolicy.AllowSymlinkCreation, FileAccessPolicy.Deny);
                    }
                }

                if (OperatingSystemHelper.IsUnixOS)
                {
                    AddUnixSpecificSandboxedProcessFileAccessPolicies();
                }

                m_fileAccessManifest.MonitorChildProcesses = !pip.HasUntrackedChildProcesses;

                if (!string.IsNullOrEmpty(m_detoursFailuresFile))
                {
                    m_fileAccessManifest.AddPath(AbsolutePath.Create(m_pathTable, m_detoursFailuresFile), values: FileAccessPolicy.AllowAll, mask: ~FileAccessPolicy.ReportAccess);
                }

                if (m_sandboxConfig.LogFileAccessTables)
                {
                    LogFileAccessTables(pip);
                }

                if (m_pip.TraceFile.IsValid)
                {
                    m_fileAccessManifest.ReportFileAccesses = true;
                    m_fileAccessManifest.ReportProcessArgs = true;
                    m_fileAccessManifest.LogProcessData = true;
                }
            }

            void addUntrackedPath(AbsolutePath untrackedPath, HashSet<AbsolutePath> processedPaths, HashSet<AbsolutePath> untrackedPaths)
            {
                if (untrackedPath.IsValid)
                {
                    untrackedPaths.Add(untrackedPath);
                    AddUntrackedPathToManifest(untrackedPath);
                    AllowCreateDirectoryForDirectoriesOnPath(untrackedPath, processedPaths);
                }
            }

            void addUntrackedScope(AbsolutePath untrackedScope, HashSet<AbsolutePath> processedPaths, AbsolutePathAncestorChecker untrackedScopeChecker)
            {
                if (untrackedScope.IsValid)
                {
                    untrackedScopeChecker.AddPath(untrackedScope);
                    AddUntrackedScopeToManifest(untrackedScope);
                    AllowCreateDirectoryForDirectoriesOnPath(untrackedScope, processedPaths);
                }
            }
        }

        private void AddUnixSpecificSandboxedProcessFileAccessPolicies()
        {
            Contract.Requires(OperatingSystemHelper.IsUnixOS);

            if (SandboxedProcessNeedsExecuteExternal)
            {
                // When executing the pip using external tool, the file access manifest tree is sealed by
                // serializing it as bytes. Thus, after the external tool deserializes the manifest tree,
                // the manifest cannot be modified further.

                // CODESYNC: SandboxedProcessUnix.cs
                // The sandboxed process for unix modifies the manifest tree. We do the same modification here.
                m_fileAccessManifest.AddPath(
                    AbsolutePath.Create(m_pathTable, SandboxedProcessUnix.ShellExecutable),
                    mask: FileAccessPolicy.MaskNothing,
                    values: FileAccessPolicy.AllowReadAlways);

                AbsolutePath stdInFile = AbsolutePath.Create(
                    m_pathTable,
                    SandboxedProcessUnix.GetStdInFilePath(m_pip.WorkingDirectory.ToString(m_pathTable), m_pip.SemiStableHash));

                m_fileAccessManifest.AddPath(
                   stdInFile,
                   mask: FileAccessPolicy.MaskNothing,
                   values: FileAccessPolicy.AllowAll);
            }
        }

        /// <summary>
        /// If a supplied path is under real/redirected user profile, creates a corresponding path under redirected/real user profile.
        /// If profile redirect is disabled or the path is not under real/redirected user profile, returns AbsolutePath.Invalid.
        /// </summary>
        private AbsolutePath CreatePathForActualRedirectedUserProfilePair(AbsolutePath path, AbsolutePath realUserProfilePath, AbsolutePath redirectedUserProfilePath)
        {
            if (!redirectedUserProfilePath.IsValid)
            {
                return AbsolutePath.Invalid;
            }

            // path is in terms of realUserProfilePath -> create a path in terms of redirectedUserProfilePath
            if (path.IsWithin(m_pathTable, realUserProfilePath))
            {
                return path.Relocate(m_pathTable, realUserProfilePath, redirectedUserProfilePath);
            }

            // path is in terms of m_redirectedUserProfilePath -> create a path in terms of m_realUserProfilePath
            if (path.IsWithin(m_pathTable, redirectedUserProfilePath))
            {
                return path.Relocate(m_pathTable, redirectedUserProfilePath, realUserProfilePath);
            }

            return AbsolutePath.Invalid;
        }

        private AbsolutePath CreatePathForVmAdminUserProfile(
            AbsolutePath vmAdminProfilePath,
            AbsolutePath path,
            AbsolutePath realUserProfilePath,
            AbsolutePath redirectedUserProfilePath)
        {
            if (!vmAdminProfilePath.IsValid)
            {
                return AbsolutePath.Invalid;
            }

            if (realUserProfilePath.IsValid && path.IsWithin(m_pathTable, realUserProfilePath))
            {
                return path.Relocate(m_pathTable, realUserProfilePath, vmAdminProfilePath);
            }

            if (redirectedUserProfilePath.IsValid && path.IsWithin(m_pathTable, redirectedUserProfilePath))
            {
                return path.Relocate(m_pathTable, redirectedUserProfilePath, vmAdminProfilePath);
            }

            return AbsolutePath.Invalid;
        }

        private void AddSharedOpaqueInputContentToManifest(
            DirectoryArtifact directory,
            HashSet<AbsolutePath> allInputPathsUnderSharedOpaques,
            HashSet<AbsolutePath> untrackedPaths,
            AbsolutePathAncestorChecker untrackedScopeChecker)
        {
            var content = m_directoryArtifactContext.ListSealDirectoryContents(directory, out var temporaryFiles);

            foreach (var fileArtifact in content)
            {
                // A shared opaque might contain files that are marked as 'absent'. Essentially these are "temp" files produced by a pip in the cone
                // of that shared opaque. We do not add these paths to the manifest, so detours would not block write accesses.
                if (!temporaryFiles.Contains(fileArtifact.Path)
                    // If the shared opaque input is an untracked path of this pip, or is under an untracked scope, then we don't add it
                    // since we already added untracked artifacts and this operation will change the appropriate untracked masks & values
                    && !untrackedPaths.Contains(fileArtifact.Path)
                    && !untrackedScopeChecker.HasKnownAncestor(m_pathTable, fileArtifact.Path))
                {
                    AddDynamicInputFileAndAncestorsToManifest(fileArtifact, allInputPathsUnderSharedOpaques, directory.Path);
                }
            }
        }

        private void AddDynamicInputFileAndAncestorsToManifest(FileArtifact file, HashSet<AbsolutePath> allInputPathsUnderSharedOpaques, AbsolutePath sharedOpaqueRoot)
        {
            // Allow reads, but fake times, since this is an input to the pip
            // We want to report these accesses since they are dynamic and need to be cross checked
            var path = file.Path;
            m_fileAccessManifest.AddPath(
                path,
                values: FileAccessPolicy.AllowRead | FileAccessPolicy.AllowReadIfNonexistent | FileAccessPolicy.ReportAccess,
                // The file dependency may be under the cone of a shared opaque, which will give write access
                // to it. Explicitly block this (no need to check if this is under a shared opaque, since otherwise
                // it didn't have write access to begin with). Observe we already know this is not a rewrite since dynamic rewrites
                // are not allowed by construction under shared opaques.
                // Observe that if double writes are allowed, then we can't just block writes: we need to allow them to happen and then
                // observe the result to figure out if they conform to the double write policy
                mask: DefaultMask & (m_pip.RewritePolicy.ImpliesDoubleWriteAllowed() ? FileAccessPolicy.MaskNothing : ~FileAccessPolicy.AllowWrite));

            allInputPathsUnderSharedOpaques.Add(path);

            // The containing directories up to the shared opaque root need timestamp faking as well
            AddDirectoryAncestorsToManifest(path, allInputPathsUnderSharedOpaques, sharedOpaqueRoot);
        }

        private void AddDirectoryAncestorsToManifest(AbsolutePath path, HashSet<AbsolutePath> allInputPathsUnderSharedOpaques, AbsolutePath sharedOpaqueRoot)
        {
            // Fake the timestamp of all directories that are ancestors of the path, but inside the shared opaque root
            // Rationale: probes may be performed on those directories (directory probes don't need declarations)
            // so they need to be faked as well
            var currentPath = path.GetParent(m_pathTable);

            if (!currentPath.IsValid || !currentPath.IsWithin(m_pathTable, sharedOpaqueRoot))
            {
                return;
            }

            while (currentPath.IsValid && !allInputPathsUnderSharedOpaques.Contains(currentPath))
            {
                // We want to set a policy for the directory without affecting the scope for the underlying artifacts
                m_fileAccessManifest.AddPath(
                        currentPath,
                        values: FileAccessPolicy.AllowRead | FileAccessPolicy.AllowReadIfNonexistent, // we don't need access reporting here
                        mask: DefaultMask); // but block real timestamps

                allInputPathsUnderSharedOpaques.Add(currentPath);

                if (currentPath == sharedOpaqueRoot)
                {
                    break;
                }

                currentPath = currentPath.GetParent(m_pathTable);
            }
        }

        /// <summary>
        /// Checks if a path under a shared opaque root.
        /// </summary>
        /// <remarks>
        /// To determine if a path is under a shared opaque we need just to check if there is a containing shared opaque,
        /// so we don't need to look for the outmost containing opaque
        /// </remarks>
        private bool IsPathUnderASharedOpaqueRoot(AbsolutePath path) => TryGetContainingSharedOpaqueRoot(path, getOutmostRoot: false, out _);

        private bool TryGetContainingSharedOpaqueRoot(AbsolutePath path, bool getOutmostRoot, out AbsolutePath sharedOpaqueRoot)
        {
            sharedOpaqueRoot = AbsolutePath.Invalid;
            // Let's shortcut the case when there are no shared opaques at all for this pip, so
            // we play conservatively regarding perf wrt existing code paths
            if (m_sharedOpaqueDirectoryRoots.Count == 0)
            {
                return false;
            }

            foreach (var current in m_pathTable.EnumerateHierarchyBottomUp(path.Value))
            {
                var currentAsPath = new AbsolutePath(current);
                if (m_sharedOpaqueDirectoryRoots.ContainsKey(currentAsPath))
                {
                    sharedOpaqueRoot = currentAsPath;
                    // If the outmost root was not requested, we can shortcut the search and return when
                    // we find the first match
                    if (!getOutmostRoot)
                    {
                        return true;
                    }
                }
            }

            return sharedOpaqueRoot != AbsolutePath.Invalid;
        }

        private void AddStaticFileDependenciesToFileAccessManifest(
            HashSet<AbsolutePath> outputFiles,
            ReadOnlyArray<FileArtifact> fileDependencies,
            HashSet<AbsolutePath> allInputPathsUnderSharedOpaques)
        {
            foreach (FileArtifact dependency in fileDependencies)
            {
                // Outputs have already been added with read-write access. We should not attempt to add a less permissive entry.
                if (!outputFiles.Contains(dependency.Path))
                {
                    AddStaticFileDependencyToFileAccessManifest(dependency, allInputPathsUnderSharedOpaques);
                }
            }
        }

        private void AddStaticFileDependencyToFileAccessManifest(FileArtifact dependency, HashSet<AbsolutePath> allInputPathsUnderSharedOpaques)
        {
            // TODO:22476: We allow inputs to not exist. Is that the right thing to do?
            // We mask 'report' here, since inputs are expected read and should never fail observed-access validation (directory dependency, etc.)
            // Note that they would perhaps fail otherwise (simplifies the observed access checks in the first place).

            var path = dependency.Path;

            bool pathIsUnderSharedOpaque = TryGetContainingSharedOpaqueRoot(path, getOutmostRoot: true, out var sharedOpaqueRoot);

            m_fileAccessManifest.AddPath(
                path,
                values: FileAccessPolicy.AllowRead | FileAccessPolicy.AllowReadIfNonexistent,
                // Make sure we fake the input timestamp
                // The file dependency may be under the cone of a shared opaque, which will give write access
                // to it. Explicitly block this, since we want inputs to not be written. Observe we already know
                // this is not a rewrite.
                mask: m_excludeReportAccessMask
                      & DefaultMask
                      & (pathIsUnderSharedOpaque
                            ? ~FileAccessPolicy.AllowWrite
                            : FileAccessPolicy.MaskNothing));
            m_remoteSbDataBuilder?.AddFileDependency(dependency.Path);

            // If the file artifact is under the root of a shared opaque we make sure all the directories
            // walking that path upwards get added to the manifest explicitly, so timestamp faking happens for them
            // We need the outmost matching root in case shared opaques are nested within each other: timestamp faking
            // needs to happen for all directories under all shared opaques
            if (pathIsUnderSharedOpaque)
            {
                allInputPathsUnderSharedOpaques.Add(path);
                AddDirectoryAncestorsToManifest(path, allInputPathsUnderSharedOpaques, sharedOpaqueRoot);
            }
        }

        /// <summary>
        /// Tests to see if the semantic path info is invalid (path was not under a mount) or writable (path is under a writable mount). 
        /// </summary>
        /// <remarks>
        /// Paths not under mounts don't have any enforcement around them so we allow them to be written.
        /// </remarks>
        private static bool IsInvalidOrWritable(in SemanticPathInfo semanticPathInfo) => !semanticPathInfo.IsValid || semanticPathInfo.IsWritable;

        private bool PrepareWorkingDirectory()
        {
            if (m_semanticPathExpander == null || IsInvalidOrWritable(m_semanticPathExpander.GetSemanticPathInfo(m_pip.WorkingDirectory)))
            {
                var workingDirectoryPath = m_pip.WorkingDirectory.ToString(m_pathTable);
                try
                {
                    FileUtilities.CreateDirectory(workingDirectoryPath);
                }
                catch (BuildXLException ex)
                {
                    LogOutputPreparationFailed(workingDirectoryPath, ex);
                    return false;
                }
            }

            return true;
        }

        private Possible<IBuildParameters> PrepareEnvironmentVariables()
        {
            var environmentVariables = m_pipEnvironment.GetEffectiveEnvironmentVariables(
                m_pip,
                m_pipDataRenderer,
                m_pip.ProcessRetriesOrDefault(m_configuration.Schedule) - m_remainingUserRetryCount,
                m_pip.RequireGlobalDependencies ? m_sandboxConfig.GlobalUnsafePassthroughEnvironmentVariables : null);

            string userProfilePath = m_layoutConfiguration != null && m_layoutConfiguration.RedirectedUserProfileJunctionRoot.IsValid
                ? s_possibleRedirectedUserProfilePath
                : s_userProfilePath;

            if (ShouldSandboxedProcessExecuteInVm)
            {
                var vmSpecificEnvironmentVariables = new[]
                {
                    new KeyValuePair<string, string>(VmSpecialEnvironmentVariables.IsInVm, "1"),
                    new KeyValuePair<string, string>(
                        VmSpecialEnvironmentVariables.HostHasRedirectedUserProfile,
                        m_layoutConfiguration != null && m_layoutConfiguration.RedirectedUserProfileJunctionRoot.IsValid ? "1" : "0"),
                    new KeyValuePair<string, string>(VmSpecialEnvironmentVariables.HostUserProfile, userProfilePath)
                };

                // Because the pip is going to be run under a different user (i.e., Admin) in VM, all passthrough environment
                // variables that are related to user profile, like %UserProfile%, %UserName%, %AppData%, %LocalAppData%, etc, need
                // to be overriden with C:\User\Administrator. The original value of user-profile %ENVVAR%, if any, will be preserved
                // in %[BUILDXL]VM_HOST_<ENVVAR>%
                //
                // We cannot delay the values for all passthrough environment variables until the pip is "sent" to the VM because the pip
                // may need the environment variables and only the host has the values for them. For example, some pips need
                // %__CLOUDBUILD_AUTH_HELPER_ROOT__% that only exists in the host.
                var vmPassThroughEnvironmentVariables = m_pip.EnvironmentVariables
                    .Where(e =>
                    {
                        if (e.IsPassThrough)
                        {
                            string name = m_pipDataRenderer.Render(e.Name);
                            return VmConstants.UserProfile.Environments.ContainsKey(name) && environmentVariables.ContainsKey(name);
                        }

                        return false;
                    })
                    .SelectMany(e =>
                    {
                        string name = m_pipDataRenderer.Render(e.Name);
                        return new[]
                        {
                            new KeyValuePair<string, string>(name, VmConstants.UserProfile.Environments[name].value),
                            new KeyValuePair<string, string>(VmSpecialEnvironmentVariables.HostEnvVarPrefix + name, environmentVariables[name])
                        };
                    });

                environmentVariables = environmentVariables.Override(vmSpecificEnvironmentVariables.Concat(vmPassThroughEnvironmentVariables));
            }

            if (SandboxedProcessShouldExecuteRemote)
            {
                // Ensure that %USERNAME% or %USERPROFILE% is included in the environment variables to support an optimization to the Windows sandboxes where
                // we pre-create c:\users\PLACEHOLDER\... (something like 17 dirs) to avoid I/O during critical path. The logic wants to find %USERNAME%
                // in the environment variable to replace PLACEHOLDER with the correct one before starting the command.
                if (!environmentVariables.ContainsKey("USERPROFILE"))
                {
                    environmentVariables = environmentVariables.Override([new KeyValuePair<string, string>("USERPROFILE", userProfilePath)]);
                }
            }

            if (m_pluginEP != null)
            {
                try
                {
                    environmentVariables = environmentVariables.Override(
                    [
                        new KeyValuePair<string, string>(
                            PluginConstants.PluginCapabilitiesEnvVar,
                            string.Join(",", m_pluginEP.LoadedPluginSupportedMessageTypes.Select(m => m.ToString())))
                    ]);
                }
                catch (TimeoutException)
                {
                    return new Possible<IBuildParameters>(new Failure<string>("Plugin initionalization timeout"));
                }
            }

            return new Possible<IBuildParameters>(environmentVariables);
        }

        /// <summary>
        /// Creates and cleans the Process's temp directory if necessary
        /// </summary>
        /// <param name="environmentVariables">Environment</param>
        private bool PrepareTempDirectory(ref IBuildParameters environmentVariables)
        {
            Contract.Requires(environmentVariables != null);

            string path = null;

            // If specified, clean the pip specific temp directory.
            if (m_pip.TempDirectory.IsValid)
            {
                if (!PreparePath(m_pip.TempDirectory))
                {
                    return false;
                }
            }

            // Clean all specified temp directories.
            foreach (var additionalTempDirectory in m_pip.AdditionalTempDirectories)
            {
                if (!PreparePath(additionalTempDirectory))
                {
                    return false;
                }
            }

            if (!ShouldSandboxedProcessExecuteInVm)
            {
                try
                {
                    // Many things get angry if temp directories don't exist so ensure they're created regardless of
                    // what they're set to.
                    // TODO:Bug 75124 - should validate these paths
                    foreach (var tmpEnvVar in DisallowedTempVariables)
                    {
                        path = environmentVariables[tmpEnvVar];
                        FileUtilities.CreateDirectory(path);
                    }
                }
                catch (BuildXLException ex)
                {
                    Logger.Log.PipTempDirectorySetupFailure(
                        m_loggingContext,
                        m_pip.SemiStableHash,
                        m_pipDescription,
                        path,
                        ex.ToStringDemystified());
                    return false;
                }
            }

            // Override environment variable.
            if (ShouldSandboxedProcessExecuteInVm)
            {
                if (m_pip.TempDirectory.IsValid
                    && m_tempFolderRedirectionForVm.TryGetValue(m_pip.TempDirectory, out AbsolutePath redirectedTempDirectory))
                {
                    // When running in VM, a pip often queries TMP or TEMP to get the path to the temp directory.
                    // For most cases, the original path is sufficient because the path is redirected to the one in VM.
                    // However, a number of operations, like creating/accessing/enumerating junctions, will fail.
                    // Recall that junctions are evaluated locally, so that creating junction using host path is like creating junctions
                    // on the host from the VM.
                    string redirectedTempDirectoryPath = redirectedTempDirectory.ToString(m_pathTable);
                    var overridenEnvVars = DisallowedTempVariables
                        .Select(v => new KeyValuePair<string, string>(v, redirectedTempDirectoryPath))
                        .Concat(
                        [
                            new KeyValuePair<string, string>(VmSpecialEnvironmentVariables.VmTemp, redirectedTempDirectoryPath),
                            new KeyValuePair<string, string>(VmSpecialEnvironmentVariables.VmOriginalTemp, m_pip.TempDirectory.ToString(m_pathTable)),
                        ]);

                    environmentVariables = environmentVariables.Override(overridenEnvVars);
                }

                environmentVariables = environmentVariables.Override(new[]
                {
                    new KeyValuePair<string, string>(VmSpecialEnvironmentVariables.VmSharedTemp, PrepareSharedTempDirectoryForVm().ToString(m_pathTable))
                });
            }

            return true;

            bool PreparePath(AbsolutePath pathToPrepare)
            {
                return !ShouldSandboxedProcessExecuteInVm ? CleanTempDirectory(pathToPrepare) : PrepareTempDirectoryForVm(pathToPrepare);
            }
        }

        private bool CleanTempDirectory(AbsolutePath tempDirectoryPath)
        {
            Contract.Requires(tempDirectoryPath.IsValid);

            try
            {
                if (m_context?.TestHooks?.FailDeletingTempDirectory == true)
                {
                    throw new BuildXLException("TestHook: FailDeletingTempDirectory");
                }

                // Temp directories are lazily, best effort cleaned after the pip finished. The previous build may not
                // have finished this work before exiting so we must double check.
                PreparePathForDirectory(
                    new ExpandedAbsolutePath(tempDirectoryPath, m_pathTable),
                    createIfNonExistent: m_sandboxConfig.EnsureTempDirectoriesExistenceBeforePipExecution);
            }
            catch (BuildXLException ex)
            {
                Logger.Log.PipTempDirectoryCleanupFailure(
                    m_loggingContext,
                    m_pip.SemiStableHash,
                    m_pipDescription,
                    tempDirectoryPath.ToString(m_pathTable),
                    ex.LogEventMessage);

                return false;
            }

            return true;
        }

        private AbsolutePath PrepareSharedTempDirectoryForVm()
        {
            AbsolutePath vmTempRoot = m_sandboxConfig.RedirectedTempFolderRootForVmExecution.IsValid
                ? m_sandboxConfig.RedirectedTempFolderRootForVmExecution
                : AbsolutePath.Create(m_pathTable, VmConstants.Temp.Root);

            AbsolutePath vmSharedTemp = vmTempRoot.Combine(m_pathTable, VmSpecialFilesAndDirectories.SharedTempFolder);
            AddUntrackedScopeToManifest(vmSharedTemp);
            return vmSharedTemp;
        }

        private bool PrepareTempDirectoryForVm(AbsolutePath tempDirectoryPath)
        {
            // Suppose that the temp directory is D:\Bxl\Out\Object\Pip123\Temp\t_0.
            string path = tempDirectoryPath.ToString(m_pathTable);

            // Delete any existence of path D:\Bxl\Out\Object\Pip123\Temp\t_0.
            try
            {
                if (FileUtilities.FileExistsNoFollow(path))
                {
                    // Path exists as a file or a symlink (directory/file symlink).
                    FileUtilities.DeleteFile(path, retryOnFailure: true, tempDirectoryCleaner: m_tempDirectoryCleaner);
                }

                if (FileUtilities.DirectoryExistsNoFollow(path))
                {
                    // Path exists as a real directory: wipe out that directory.
                    FileUtilities.DeleteDirectoryContents(path, deleteRootDirectory: true, tempDirectoryCleaner: m_tempDirectoryCleaner);
                }
            }
            catch (BuildXLException ex)
            {
                Logger.Log.PipTempDirectorySetupFailure(
                    m_loggingContext,
                    m_pip.SemiStableHash,
                    m_pipDescription,
                    path,
                    ex.ToStringDemystified());
                return false;
            }

            // Suppose that the root of temp directory in VM is T:\BxlTemp.
            // Users can specify the root for redirected temp folder. This user-specified root is currently only used by self-host's unit tests because T drive is not
            // guaranteed to exist when running locally on desktop.
            string redirectedTempRoot = m_sandboxConfig.RedirectedTempFolderRootForVmExecution.IsValid
                ? m_sandboxConfig.RedirectedTempFolderRootForVmExecution.ToString(m_pathTable)
                : VmConstants.Temp.Root;

            // Create a target temp folder in VM, e.g., T:\BxlTemp\D__\Bxl\Out\Object\Pip123\Temp\t_0.
            // Note that this folder may not exist yet in VM. The sandboxed process executor that runs in the VM is responsible for
            // creating (or ensuring the existence) of the folder.
            string pathRoot = Path.GetPathRoot(path);
            string pathRootAsDirectoryName = pathRoot
                .Replace(Path.VolumeSeparatorChar, '_')
                .Replace(Path.DirectorySeparatorChar, '_')
                .Replace(Path.AltDirectorySeparatorChar, '_');
            string redirectedPath = Path.Combine(redirectedTempRoot, pathRootAsDirectoryName, path.Substring(pathRoot.Length));

            if (redirectedPath.Length > FileUtilities.MaxDirectoryPathLength())
            {
                // Force short path: T:\BxlTemp\Pip123\0
                redirectedPath = Path.Combine(redirectedTempRoot, m_pip.FormattedSemiStableHash, m_tempFolderRedirectionForVm.Count.ToString());
            }

            AbsolutePath tempRedirectedPath = AbsolutePath.Create(m_pathTable, redirectedPath);

            m_tempFolderRedirectionForVm.Add(tempDirectoryPath, tempRedirectedPath);

            // Create a directory symlink D:\Bxl\Out\Object\Pip123\Temp\t_0 -> T:\BxlTemp\D__\Bxl\Out\Object\Pip123\Temp\t_0.
            // Any access to D:\Bxl\Out\Object\Pip123\Temp\t_0 will be redirected to T:\BxlTemp\D__\Bxl\Out\Object\Pip123\Temp\t_0.
            // To make this access work, one needs to ensure that symlink evaluation behaviors R2R and R2L are enabled in VM.
            // VmCommandProxy is ensuring that such symlink evaluation behaviors are enabled during VM initialization.
            // To create the directory symlink, one also needs to ensure that the parent directory of the directory symlink exists,
            // i.e., D:\Bxl\Out\Object\Pip123\Temp exists.
            FileUtilities.CreateDirectory(Path.GetDirectoryName(path));
            var createDirectorySymlink = FileUtilities.TryCreateSymbolicLink(path, redirectedPath, isTargetFile: false);

            if (!createDirectorySymlink.Succeeded)
            {
                Logger.Log.PipTempSymlinkRedirectionError(
                    m_loggingContext,
                    m_pip.SemiStableHash,
                    m_pipDescription,
                    redirectedPath,
                    path,
                    createDirectorySymlink.Failure.Describe());
                return false;
            }

            Contract.Assert(m_fileAccessManifest != null);

            // Ensure that T:\BxlTemp\D__\Bxl\Out\Object\Pip123\Temp\t_0 is untracked. Thus, there is no need for a directory translation.
            AddUntrackedScopeToManifest(tempRedirectedPath);

            Logger.Log.PipTempSymlinkRedirection(
                m_loggingContext,
                m_pip.SemiStableHash,
                m_pipDescription,
                redirectedPath,
                path);

            return true;
        }

        /// <summary>
        /// Each output of this process is either written (output only) or rewritten (input and output).
        /// We delete written outputs (shouldn't be observed) and re-deploy rewritten outputs (should be a private copy).
        /// Rewritten outputs may be initially non-private and so not writable, i.e., hardlinked to other locations.
        /// Note that this function is also responsible for stamping private outputs such that the tool sees
        /// <see cref="WellKnownTimestamps.OldOutputTimestamp"/>.
        /// </summary>
        private async Task<bool> PrepareOutputsAsync()
        {
            using var preserveOutputAllowlistWrapper = Pools.GetAbsolutePathSet();
            using var dependenciesWrapper = Pools.GetAbsolutePathSet();
            using var outputDirectoriesWrapper = Pools.GetAbsolutePathSet();

            var preserveOutputAllowlist = preserveOutputAllowlistWrapper.Instance;
            foreach (AbsolutePath path in m_pip.PreserveOutputAllowlist)
            {
                preserveOutputAllowlist.Add(path);
            }

            using (Counters.StartStopwatch(SandboxedProcessCounters.PrepareDirectoryOutputsDuration))
            {
                if (!await PrepareDirectoryOutputsAsync(preserveOutputAllowlist))
                {
                    return false;
                }
            }

            var dependencies = dependenciesWrapper.Instance;
            foreach (FileArtifact dependency in m_pip.Dependencies)
            {
                dependencies.Add(dependency.Path);
            }

            var outputDirectories = outputDirectoriesWrapper.Instance;

            // Standard directory can contain outputs, and so its existence should be ensured.
            PrepareStandardDirectory(outputDirectories);

            foreach (FileArtifactWithAttributes output in m_pip.FileOutputs)
            {
                // Only subset of all outputs should be deleted, because some times we may want a tool to see its prior outputs
                if (!output.DeleteBeforeRun())
                {
                    continue;
                }

                try
                {
                    if (!dependencies.Contains(output.Path))
                    {
                        if (ShouldPreserveDeclaredOutput(output.Path, preserveOutputAllowlist))
                        {
                            Contract.Assume(m_makeOutputPrivate != null);
                            // A process may be configured to allow its prior outputs to be seen by future
                            // invocations. In this case we must make sure the outputs are no longer hardlinked to
                            // the cache to allow them to be writeable.
                            //
                            // We cannot use m_sandboxConfig.UnsafeSandboxConfiguration.IgnorePreserveOutputsPrivatization to skip
                            // privatization of output file because the output file itself can be rewritten by downstream pips.
                            // In such a case, BuildXL itself forces the output to be stored to the cache although the user may
                            // opt to not store outputs to the cache.
                            if (!await m_makeOutputPrivate(output.Path.ToString(m_pathTable)))
                            {
                                // Delete the file if it exists.
                                PreparePathForOutputFile(output.Path, outputDirectories);
                            }
                        }
                        else
                        {
                            // Delete the file, since we aren't re-writing it. Note that we do not use System.IO.File.Delete here,
                            // since we need to be tolerant to several exotic cases:
                            // - A previous run of the tool may have written a file and marked it as readonly.
                            // - There may be concurrent users of the file being deleted, but via other hardlinks:
                            // This DeleteFile tries several strategies relevant to those circumstances.
                            PreparePathForOutputFile(output.Path, outputDirectories);
                        }
                    }
                    else
                    {
                        Contract.Assume(output.RewriteCount > 0);
                        var inputVersionOfOutput = new FileArtifact(output.Path, output.RewriteCount - 1);
#if DEBUG
                        Contract.Assume(m_pip.Dependencies.Contains(inputVersionOfOutput), "Each rewrite increments the rewrite count by one");
#endif

                        if (m_makeInputPrivate != null)
                        {
                            if (!await m_makeInputPrivate(inputVersionOfOutput))
                            {
                                throw new BuildXLException("Failed to create a private, writable copy of the file so that it could be re-written.");
                            }
                        }

                        try
                        {
                            FileUtilities.SetFileTimestamps(
                                output.Path.ToString(m_pathTable),
                                new FileTimestamps(WellKnownTimestamps.OldOutputTimestamp));
                        }
                        catch (Exception ex)
                        {
                            throw new BuildXLException(
                                "Failed to open an output file for writing (it was just made writable and private to be written by a process).",
                                ex);
                        }
                    }
                }
                catch (BuildXLException ex)
                {
                    LogOutputPreparationFailed(output.Path.ToString(m_pathTable), ex);
                    return false;
                }
            }

            // Delete shared opaque outputs if enabled
            return !IsLazySharedOpaqueOutputDeletionEnabled || DeleteSharedOpaqueOutputsRecordedInSidebandFile();
        }

        private void PrepareStandardDirectory(HashSet<AbsolutePath> outputDirectories)
        {
            if (m_pip.StandardDirectory.IsValid
                && (outputDirectories == null || outputDirectories.Add(m_pip.StandardDirectory)))
            {
                // Ensure parent directory exists.
                FileUtilities.CreateDirectory(GetStandardDirectory());
            }

        }

        private bool DeleteSharedOpaqueOutputsRecordedInSidebandFile()
        {
            Contract.AssertNotNull(m_sidebandState, "DeleteSharedOpaqueOutputsRecordedInSidebandFile can't be called without a sideband state");
            var sidebandFile = SidebandWriterHelper.GetSidebandFileForProcess(m_context.PathTable, m_layoutConfiguration.SharedOpaqueSidebandDirectory, m_pip);

            try
            {
                var start = DateTime.UtcNow;
                var sharedOpaqueOutputsToDelete = m_sidebandState[m_pip.SemiStableHash];
                var deletionResults = sharedOpaqueOutputsToDelete // TODO: possibly parallelize file deletion
                    .Select(p => p.ToString(m_pathTable))
                    .Where(FileUtilities.FileExistsNoFollow)
                    .Select(path => FileUtilities.TryDeleteFile(path)) // TODO: what about deleting directories?
                    .ToArray();
                LogSubPhaseDuration(m_loggingContext, m_pip, SandboxedProcessCounters.SandboxedPipExecutorPhaseDeletingSharedOpaqueOutputs, DateTime.UtcNow.Subtract(start));

                // select failures
                var failures = deletionResults
                    .Where(maybeDeleted => !maybeDeleted.Succeeded) // select failures only
                    .Where(maybeDeleted => FileUtilities.FileExistsNoFollow(maybeDeleted.Failure.Path)) // double check that the files indeed exist
                    .ToArray();

                // log failures (if any)
                if (failures.Length > 0)
                {
                    var files = string.Join(string.Empty, failures.Select(f => $"{Environment.NewLine}  {f.Failure.Path}"));
                    var firstFailure = failures.First().Failure.DescribeIncludingInnerFailures();
                    Logger.Log.CannotDeleteSharedOpaqueOutputFile(m_loggingContext, m_pipDescription, sidebandFile, files, firstFailure);
                    return false;
                }

                // log deleted files
                var actuallyDeleted = deletionResults.Where(r => r.Succeeded);
                var deletedFiles = string.Join(string.Empty, actuallyDeleted.Select(r => $"{Environment.NewLine}  {r.Result}"));
                Logger.Log.SharedOpaqueOutputsDeletedLazily(m_loggingContext, m_pip.FormattedSemiStableHash, sidebandFile, deletedFiles, actuallyDeleted.Count());

                // delete the sideband file itself
                Analysis.IgnoreResult(FileUtilities.TryDeleteFile(sidebandFile));

                return true;
            }
            catch (Exception e) when (e is IOException || e is BuildXLException)
            {
                Logger.Log.CannotReadSidebandFileError(m_loggingContext, sidebandFile, e.Message);
                return false;
            }
        }

        private void CreatePipOutputDirectory(ExpandedAbsolutePath path, bool knownAbsent = false)
        {
            if (knownAbsent || !FileUtilities.DirectoryExistsNoFollow(path.ExpandedPath))
            {
                m_engineCreatedPipOutputDirectories.Add(path.Path);
            }

            FileUtilities.CreateDirectory(path.ExpandedPath);
        }

        private async Task<bool> PrepareDirectoryOutputsAsync(HashSet<AbsolutePath> preserveOutputAllowlist)
        {
            m_staleOutputsUnderSharedOpaqueDirectoriesToBeDeletedInVM.Clear(); // Remove any entries left over from a previous run

            foreach (var directoryOutput in m_pip.DirectoryOutputs)
            {
                try
                {
                    var directoryExpandedPath = new ExpandedAbsolutePath(directoryOutput.Path, m_pathTable);
                    bool dirExist = FileUtilities.DirectoryExistsNoFollow(directoryExpandedPath.ExpandedPath);

                    if (directoryOutput.IsSharedOpaque)
                    {
                        // Ensure it exists.
                        if (!dirExist)
                        {
                            CreatePipOutputDirectory(directoryExpandedPath, knownAbsent: true);
                        }
                        // if the directory is present, check whether there are any known stale outputs
                        else if (m_staleOutputsUnderSharedOpaqueDirectories != null
                            && m_staleOutputsUnderSharedOpaqueDirectories.TryGetValue(directoryOutput.Path, out var staleOutputs))
                        {
                            // Delete stale shared opaque outputs but spare undeclared rewrites: these files were there before the pip started so they act as undeclared sources to the pip
                            // We shouldn't delete sources under any circumstances. The undeclared source could have been modified by a previous attempt, so we could be not resetting the state of
                            // the pip completely, but this is the same compromise we take for rewritten sources in general doing back to back builds. It also aligns with how we scrub shared opaque
                            // in general (beyond this particular retry case)
                            foreach (var output in staleOutputs.Where(fa => !fa.IsUndeclaredFileRewrite))
                            {
                                // For external VM processes, if the output file cannot be deleted, we can retry it on the VM
                                // Therefore, don't throw an exception yet.
                                var deletionResult = PreparePathForOutputFile(output.Path, outputDirectories: null, doNotThrowExceptionOnFailure: ShouldSandboxedProcessExecuteInVm);

                                if (ShouldSandboxedProcessExecuteInVm && !deletionResult)
                                {
                                    // If one of the stale output directories is under the VM and the file handle is being held inside the VM
                                    // then it is not possible to release the file handle under the host without admin privileges.
                                    // Instead, we can try to take ownership and delete the file from within the VM on the next retry.
                                    var filePath = output.Path.ToString(m_pathTable);
                                    m_staleOutputsUnderSharedOpaqueDirectoriesToBeDeletedInVM.Add(filePath);
                                    Logger.Log.PipProcessOutputPreparationToBeRetriedInVM(m_loggingContext, m_pip.SemiStableHash, m_pipDescription, filePath);
                                }
                            }
                        }
                    }
                    else
                    {
                        if (dirExist && ShouldPreserveDeclaredOutput(directoryOutput.Path, preserveOutputAllowlist))
                        {
                            Contract.Assert(m_makeOutputPrivate != null);

                            if (!m_sandboxConfig.UnsafeSandboxConfiguration.IgnorePreserveOutputsPrivatization)
                            {
                                int failureCount = 0;
                                var makeOutputPrivateWorker = ActionBlockSlim.Create<string>(
                                    degreeOfParallelism: Environment.ProcessorCount,
                                    async path =>
                                    {
                                        if (failureCount == 0 && !await m_makeOutputPrivate(path))
                                        {
                                            Interlocked.Increment(ref failureCount);
                                            Logger.Log.PipProcessPreserveOutputDirectoryFailedToMakeFilePrivate(
                                                m_loggingContext,
                                                m_pip.SemiStableHash,
                                                m_pipDescription,
                                                directoryOutput.Path.ToString(m_pathTable),
                                                path);
                                        }
                                    });

                                FileUtilities.EnumerateDirectoryEntries(
                                    directoryExpandedPath.ExpandedPath,
                                    recursive: true,
                                    handleEntry: (currentDir, name, attributes) =>
                                    {
                                        if ((attributes & FileAttributes.Directory) == 0)
                                        {
                                            makeOutputPrivateWorker.Post(Path.Combine(currentDir, name));
                                        }
                                    });
                                makeOutputPrivateWorker.Complete();
                                await makeOutputPrivateWorker.Completion;

                                if (failureCount > 0)
                                {
                                    PreparePathForDirectory(directoryExpandedPath, createIfNonExistent: true, isPipOutputPath: true);
                                }
                            }
                            else
                            {
                                // Note that output directories cannot be rewritten. If there's a member of the output directory
                                // that gets rewritten, then it must be specified as output file, and that file is made private
                                // by the preparation of output file.
                                Logger.Log.PipProcessPreserveOutputDirectorySkipMakeFilesPrivate(
                                    m_loggingContext,
                                    m_pip.SemiStableHash,
                                    m_pipDescription,
                                    directoryOutput.Path.ToString(m_pathTable));
                            }
                        }
                        else
                        {
                            PreparePathForDirectory(directoryExpandedPath, createIfNonExistent: true, isPipOutputPath: true);
                        }
                    }

                    m_remoteSbDataBuilder?.AddOutputDirectory(directoryOutput);
                }
                catch (BuildXLException ex)
                {
                    LogOutputPreparationFailed(directoryOutput.Path.ToString(m_pathTable), ex);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// If used, response files must be created before executing pips that consume them
        /// </summary>
        private Task<bool> PrepareResponseFileAsync() =>
            WritePipAuxiliaryFileAsync(
                m_pip.ResponseFile,
                () =>
                {
                    Contract.Assume(m_pip.ResponseFileData.IsValid, "ResponseFile path requires having ResponseFile data");
                    return m_pip.ResponseFileData.ToString(m_pipDataRenderer);
                },
                Logger.Log.PipProcessResponseFileCreationFailed);

        /// <summary>
        /// ChangeAffectedInputListFile file is created before executing pips that consume it
        /// </summary>
        private Task<bool> PrepareChangeAffectedInputListFileAsync(IReadOnlyCollection<AbsolutePath> changeAffectedInputs = null) =>
            changeAffectedInputs == null
                ? Task.FromResult(true)
                : WritePipAuxiliaryFileAsync(
                    m_pip.ChangeAffectedInputListWrittenFile,
                    () => string.Join(
                        Environment.NewLine,
                        changeAffectedInputs.Select(i => i.ToString(m_pathTable))),
                    Logger.Log.PipProcessChangeAffectedInputsWrittenFileCreationFailed);

        private async Task<bool> WritePipAuxiliaryFileAsync(
            FileArtifact fileArtifact,
            Func<string> createContent,
            Action<LoggingContext, long, string, string, int, string> logException)
        {
            if (!fileArtifact.IsValid)
            {
                return true;
            }

            string destination = fileArtifact.Path.ToString(m_context.PathTable);

            try
            {
                string directoryName = ExceptionUtilities.HandleRecoverableIOException(
                    () => Path.GetDirectoryName(destination),
                    ex => { throw new BuildXLException("Cannot get directory name", ex); });
                PreparePathForOutputFile(fileArtifact);
                FileUtilities.CreateDirectory(directoryName);
                await FileUtilities.WriteAllTextAsync(destination, createContent(), Encoding.UTF8);
            }
            catch (BuildXLException ex)
            {
                logException(
                    m_loggingContext,
                    m_pip.SemiStableHash,
                    m_pipDescription,
                    destination,
                    ex.LogEventErrorCode,
                    ex.LogEventMessage);

                return false;
            }

            return true;
        }

        private enum SpecialProcessKind : byte
        {
            NotSpecial = 0,
            Csc = 1,
            Cvtres = 2,
            Resonexe = 3,
            RC = 4,
            CCCheck = 5,
            CCDocGen = 6,
            CCRefGen = 7,
            CCRewrite = 8,
            WinDbg = 9,
            XAMLWrapper = 11,
            Mt = 12
        }

        private static readonly Dictionary<string, SpecialProcessKind> s_specialTools = new Dictionary<string, SpecialProcessKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["csc"] = SpecialProcessKind.Csc,
            ["csc.exe"] = SpecialProcessKind.Csc,
            ["cvtres"] = SpecialProcessKind.Cvtres,
            ["cvtres.exe"] = SpecialProcessKind.Cvtres,
            ["cvtress.exe"] = SpecialProcessKind.Cvtres, // Legacy.
            ["reson"] = SpecialProcessKind.Resonexe,
            ["resonexe.exe"] = SpecialProcessKind.Resonexe,
            ["rc"] = SpecialProcessKind.RC,
            ["rc.exe"] = SpecialProcessKind.RC,
            ["mt"] = SpecialProcessKind.Mt,
            ["mt.exe"] = SpecialProcessKind.Mt,
            ["windbg"] = SpecialProcessKind.WinDbg,
            ["windbg.exe"] = SpecialProcessKind.WinDbg,
            ["tool.xamlcompilerwrapper"] = SpecialProcessKind.XAMLWrapper,
            ["tool.xamlcompilerwrapper.exe"] = SpecialProcessKind.XAMLWrapper,

            // TODO: deprecate this.
            ["cccheck"] = SpecialProcessKind.CCCheck,
            ["cccheck.exe"] = SpecialProcessKind.CCCheck,
            ["ccdocgen"] = SpecialProcessKind.CCDocGen,
            ["ccdocgen.exe"] = SpecialProcessKind.CCDocGen,
            ["ccrefgen"] = SpecialProcessKind.CCRefGen,
            ["ccrefgen.exe"] = SpecialProcessKind.CCRefGen,
            ["ccrewrite"] = SpecialProcessKind.CCRewrite,
            ["ccrewrite.exe"] = SpecialProcessKind.CCRewrite,
        };

        /// <summary>
        /// Gets the kind of process.
        /// </summary>
        /// <returns>The kind of process based on the process' name.</returns>
        /// <remarks>
        /// If <paramref name="processOverride"/> is invalid, then the process kind is obtained from the executable path specified in the pip.
        /// </remarks>
        [SuppressMessage("Microsoft.Globalization", "CA1309", Justification = "Already using Comparison.OrdinalIgnoreCase - looks like a bug in FxCop rules.")]
        private SpecialProcessKind GetProcessKind(AbsolutePath processOverride)
        {
            AbsolutePath processPath = processOverride.IsValid ? processOverride : m_pip.Executable.Path;
            string toolName = processPath.GetName(m_pathTable).ToString(m_pathTable.StringTable);

            return s_specialTools.TryGetValue(toolName.ToLowerInvariant(), out SpecialProcessKind kind) ? kind : SpecialProcessKind.NotSpecial;
        }

        private static bool StringLooksLikeRCTempFile(string fileName)
        {
            int len = fileName.Length;
            if (len < 9)
            {
                return false;
            }

            char c1 = fileName[len - 9];
            if (c1 != '\\')
            {
                return false;
            }

            char c2 = fileName[len - 8];
            if (c2.ToUpperInvariantFast() != 'R')
            {
                return false;
            }

            char c3 = fileName[len - 7];
            if (c3.ToUpperInvariantFast() != 'C' && c3.ToUpperInvariantFast() != 'D' && c3.ToUpperInvariantFast() != 'F')
            {
                return false;
            }

            char c4 = fileName[len - 4];
            if (c4.ToUpperInvariantFast() == '.')
            {
                // RC's temp files have no extension.
                return false;
            }

            return true;
        }

        private static bool StringLooksLikeMtTempFile(string fileName)
        {
            if (!fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int beginCharIndex = fileName.LastIndexOf('\\');

            if (beginCharIndex == -1 || beginCharIndex + 3 >= fileName.Length)
            {
                return false;
            }

            char c1 = fileName[beginCharIndex + 1];
            if (c1.ToUpperInvariantFast() != 'R')
            {
                return false;
            }

            char c2 = fileName[beginCharIndex + 2];
            if (c2.ToUpperInvariantFast() != 'C')
            {
                return false;
            }

            char c3 = fileName[beginCharIndex + 3];
            if (c3.ToUpperInvariantFast() != 'X')
            {
                return false;
            }

            return true;
        }

        private static bool StringLooksLikeBuildExeTraceLog(string fileName)
        {
            // detect filenames of the following form
            // _buildc_dep_out.pass<NUMBER>
            int len = fileName.Length;

            int trailingDigits = 0;
            for (; len > 0 && fileName[len - 1] >= '0' && fileName[len - 1] <= '9'; len--)
            {
                trailingDigits++;
            }

            if (trailingDigits == 0)
            {
                return false;
            }

            return fileName.EndsWith("_buildc_dep_out.pass", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns whether we should exclude special file access reports from processing
        /// </summary>
        /// <param name="fileAccessPath">The reported file  access path.</param>
        /// <returns>True is the file should be excluded, otherwise false.</returns>
        /// <remarks>
        /// This accounts for accesses when the FileAccessIgnoreCodeCoverage is set. A lot of our tests are running
        /// with this flag set..
        /// </remarks>
        private bool GetSpecialCaseRulesForCoverageAndSpecialDevices(
            AbsolutePath fileAccessPath)
        {
            Contract.Assert(fileAccessPath != AbsolutePath.Invalid);
            Contract.Assert(!string.IsNullOrEmpty(fileAccessPath.ToString(m_pathTable)));

            string accessedPath = fileAccessPath.ToString(m_pathTable);

            // When running test cases with Code Coverage enabled, some more files are loaded that we should ignore
            if (m_sandboxConfig.FileAccessIgnoreCodeCoverage)
            {
                if (accessedPath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                    || accessedPath.EndsWith(".nls", StringComparison.OrdinalIgnoreCase)
                    || accessedPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns whether we should exclude special file access reports from processing
        /// </summary>
        /// <param name="processPath">Path of process that access the file.</param>
        /// <param name="fileAccessPath">The reported file  access path.</param>
        /// <returns>True is the file should be excluded, otherwise false.</returns>
        /// <remarks>
        /// Some perform file accesses, which don't yet fall into any configurable file access manifest category.
        /// These special tools/cases should be allowlisted, but we already have customers deployed specs without
        /// using allowlists.
        /// </remarks>
        private bool GetSpecialCaseRulesForSpecialTools(AbsolutePath processPath, AbsolutePath fileAccessPath)
        {
            if (m_pip.PipType != PipType.Process)
            {
                return true;
            }

            string fileName = fileAccessPath.ToString(m_pathTable);

            switch (GetProcessKind(processPath))
            {
                case SpecialProcessKind.Csc:
                case SpecialProcessKind.Cvtres:
                case SpecialProcessKind.Resonexe:
                    // Some tools emit temporary files into the same directory
                    // as the final output file.
                    if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    break;

                case SpecialProcessKind.RC:
                    // The native resource compiler (RC) emits temporary files into the same
                    // directory as the final output file.
                    if (StringLooksLikeRCTempFile(fileName))
                    {
                        return true;
                    }

                    break;

                case SpecialProcessKind.Mt:
                    if (StringLooksLikeMtTempFile(fileName))
                    {
                        return true;
                    }

                    break;

                case SpecialProcessKind.CCCheck:
                case SpecialProcessKind.CCDocGen:
                case SpecialProcessKind.CCRefGen:
                case SpecialProcessKind.CCRewrite:
                    // The cc-line of tools like to find pdb files by using the pdb path embedded in a dll/exe.
                    // If the dll/exe was built with different roots, then this results in somewhat random file accesses.
                    if (fileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    break;

                case SpecialProcessKind.WinDbg:
                case SpecialProcessKind.NotSpecial:
                    // no special treatment
                    break;
            }

            // build.exe and tracelog.dll capture dependency information in temporary files in the object root called _buildc_dep_out.<pass#>
            if (StringLooksLikeBuildExeTraceLog(fileName))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true if any symlinks are found on a given path
        /// </summary>
        private bool PathContainsSymlinks(AbsolutePath path)
        {
            Counters.IncrementCounter(SandboxedProcessCounters.DirectorySymlinkPathsCheckedCount);
            return path.IsValid
                && (FileUtilities.IsDirectorySymlinkOrJunction(path.ToString(m_context.PathTable))
                    || PathContainsSymlinksCached(path.GetParent(m_context.PathTable)));
        }

        /// <summary>
        /// Same as <see cref="PathContainsSymlinks"/> but with caching around it.
        /// </summary>
        private bool PathContainsSymlinksCached(AbsolutePath path)
        {
            Counters.IncrementCounter(SandboxedProcessCounters.DirectorySymlinkPathsQueriedCount);
            return m_isDirSymlinkCache.GetOrAdd(path, PathContainsSymlinks);
        }

        private bool CheckIfPathContainsSymlinks(AbsolutePath path)
        {
            using (Counters.StartStopwatch(SandboxedProcessCounters.DirectorySymlinkCheckingDuration))
            {
                return PathContainsSymlinksCached(path);
            }
        }

        private IEnumerable<ReportedFileAccess> GetEnumeratedFileAccessesForIncrementalTool(SandboxedProcessResult result) =>
            result.ExplicitlyReportedFileAccesses
                .Where(r => r.RequestedAccess == RequestedAccess.Enumerate && IsIncrementalToolAccess(r) && r.Operation == ReportedFileOperation.NtQueryDirectoryFile)
                .SelectMany(r =>
                {
                    string maybeDirectory = r.GetPath(m_context.PathTable);
                    if (!Directory.Exists(maybeDirectory))
                    {
                        return Enumerable.Empty<ReportedFileAccess>();
                    }

                    return Directory.EnumerateFiles(maybeDirectory, string.IsNullOrEmpty(r.EnumeratePattern) ? "*" : r.EnumeratePattern)
                        .Select(f =>
                        {
                            AbsolutePath absF = AbsolutePath.Create(m_context.PathTable, f);
                            bool findManifest = m_fileAccessManifest.TryFindManifestPathFor(absF, out AbsolutePath manifestPath, out FileAccessPolicy nodePolicy);
                            bool explicitlyReported = findManifest && nodePolicy.HasFlag(FileAccessPolicy.ReportAccess);
                            return new ReportedFileAccess(
                                ReportedFileOperation.NtQueryDirectoryFile,
                                r.Process,
                                RequestedAccess.EnumerationProbe,
                                r.Status,
                                explicitlyReported,
                                r.Error,
                                r.RawError,
                                r.Usn,
                                DesiredAccess.GENERIC_READ,
                                ShareMode.FILE_SHARE_READ,
                                CreationDisposition.OPEN_EXISTING,
                                FlagsAndAttributes.FILE_ATTRIBUTE_NORMAL,
                                absF,
                                f,
                                null);
                        })
                        .Where(r => r.ExplicitlyReported);
                });

        /// <summary>
        /// Returns true if a directory reparse point should be treated as a file.
        /// </summary>
        /// <remarks>
        /// CODESYNC: Public\Src\Sandbox\Windows\DetoursServices\DetouredFunctions.cpp, ShouldTreatDirectoryReparsePointAsFile
        ///           See comment there for the rationale.
        /// </remarks>
        private bool ShouldTreatDirectoryReparsePointAsFile(AbsolutePath path, ReportedFileAccess access) =>
            // Only on Windows OS.
            OperatingSystemHelper.IsWindowsOS
            // Operation does not open reparse points, or it is a write operation (e.g., creating a symlink does not pass FILE_FLAG_OPEN_REPARSE_POINT)
            && (access.FlagsAndAttributes.HasFlag(FlagsAndAttributes.FILE_FLAG_OPEN_REPARSE_POINT) || access.RequestedAccess == RequestedAccess.Write)
            // The path is not specified to be treated as a directory.
            && !m_directorySymlinksAsDirectories.Contains(path)
            // The operation is not a probe or the configuration mandates that directory symlinks are not probed as directories.
            && ((access.RequestedAccess != RequestedAccess.Probe && access.RequestedAccess != RequestedAccess.EnumerationProbe)
                || !m_sandboxConfig.UnsafeSandboxConfiguration.ProbeDirectorySymlinkAsDirectory)
            // Either full reparse point resolution is enabled, or the path is in the manifest and the policy mandates full reparse point resolution.
            && (!m_fileAccessManifest.IgnoreFullReparsePointResolving
                || (m_fileAccessManifest.TryFindManifestPathFor(path, out _, out FileAccessPolicy policy)
                    && policy.HasFlag(FileAccessPolicy.EnableFullReparsePointParsing)));


        private bool IsAccessingDirectoryLocation(AbsolutePath path, ReportedFileAccess access) =>
            // If the path is available and ends with a trailing backlash, we know that represents a directory
            (access.Path != null && access.Path.EndsWith(FileUtilities.DirectorySeparatorString, StringComparison.OrdinalIgnoreCase))
            || access.IsOpenedHandleDirectory(() => ShouldTreatDirectoryReparsePointAsFile(path, access));

        /// <summary>
        /// Creates an <see cref="ObservedFileAccess"/> for each unique path accessed.
        /// The returned array is sorted by the expanded path and additionally contains no duplicates.
        /// </summary>
        /// <remarks>
        /// Additionally, it returns the write accesses that were observed in shared dynamic
        /// directories, which are used later to determine pip ownership.
        /// <paramref name="createdDirectories"/> contains the set of directories that were succesfully created during the pip execution and didn't exist before the build ran. 
        /// Observe there is no guarantee those directories still exist, but there was a point in time when they were not there and the creation was successful. Only
        /// populated if allowed undeclared reads is on, since these are used for computing directory fingerprint enumeration when undeclared files are allowed
        /// </remarks>
        /// <returns>Whether the operation succeeded. This operation may fail only in regards to shared dynamic write access processing.</returns>
        private bool TryGetObservedFileAccesses(
            FileAccessReportingContext fileAccessReportingContext,
            SandboxedProcessResult result,
            HashSet<AbsolutePath> allInputPathsUnderSharedOpaques,
            out List<AbsolutePath> unobservedOutputs,
            out IReadOnlyDictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>> sharedDynamicDirectoryWriteAccesses,
            out SortedReadOnlyArray<ObservedFileAccess, ObservedFileAccessExpandedPathComparer> observedAccesses,
            out IReadOnlySet<AbsolutePath> createdDirectories)
        {
            unobservedOutputs = null;
            if (result.ExplicitlyReportedFileAccesses == null || result.ExplicitlyReportedFileAccesses.Count == 0)
            {
                unobservedOutputs = m_pip.FileOutputs.Where(RequireOutputObservation).Select(f => f.Path).ToList();
                sharedDynamicDirectoryWriteAccesses = CollectionUtilities.EmptyDictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>>();
                observedAccesses = SortedReadOnlyArray<ObservedFileAccess, ObservedFileAccessExpandedPathComparer>.FromSortedArrayUnsafe(
                        ReadOnlyArray<ObservedFileAccess>.Empty,
                        new ObservedFileAccessExpandedPathComparer(m_context.PathTable.ExpandedPathComparer));
                createdDirectories = CollectionUtilities.EmptySet<AbsolutePath>();

                return true;
            }

            // Note that we are enumerating an unordered set to produce the array of observed paths.
            // As noted in SandboxedProcessPipExecutionResult, the caller must assume no particular order.
            // Since observed accesses contribute to a descriptor value (rather than a hashed key), this is fine; no normalization needed.
            // Since we're projecting many acceses into groups per path into just paths, we need a temporary dictionary.
            // TODO: Allocations ahoy!
            using PooledObjectWrapper<Dictionary<AbsolutePath, CompactSet<ReportedFileAccess>>> accessesByPathWrapper = ProcessPools.ReportedFileAccessesByPathPool.GetInstance();

            Dictionary<AbsolutePath, CompactSet<ReportedFileAccess>> accessesByPath = accessesByPathWrapper.Instance;
            var excludedToolsAndPaths = new HashSet<(AbsolutePath, AbsolutePath)>();
            using var createdDirectoriesMutableWrapper = Pools.GetAbsolutePathSet();
            var createdDirectoriesMutable = createdDirectoriesMutableWrapper.Instance;

            foreach (ReportedFileAccess reported in result.ExplicitlyReportedFileAccesses.Concat(GetEnumeratedFileAccessesForIncrementalTool(result)))
            {
                Contract.Assert(
                    reported.Status == FileAccessStatus.Allowed || reported.Method == FileAccessStatusMethod.FileExistenceBased,
                    "Explicitly reported accesses are defined to be successful or denied only based on file existence");

                // Enumeration probes have a corresponding Enumeration access (also explicitly reported).
                // Presently we are interested in capturing the existence of enumerations themselves rather than what was seen
                // (and for NtQueryDirectoryFile, we can't always report the individual probes anyway).
                if (reported.RequestedAccess == RequestedAccess.EnumerationProbe)
                {
                    // If it is an incremental tool and the pip allows preserving outputs, then do not ignore because
                    // the tool may depend on the directory membership.
                    if (!IsIncrementalToolAccess(reported))
                    {
                        continue;
                    }
                }

                AbsolutePath parsedPath;

                // We want an AbsolutePath for the full access. This may not be parse-able due to the accessed path
                // being invalid, or a path format we do not understand. Note that TryParseAbsolutePath logs as appropriate
                // in the latter case.
                if (!reported.TryParseAbsolutePath(m_context, m_loggingContext, m_pip, out parsedPath))
                {
                    continue;
                }

                bool shouldExclude = false;

                // Remove special accesses see Bug: #121875.
                // Some perform file accesses, which don't yet fall into any configurable file access manifest category.
                // These special tools/cases should be allowlisted, but we already have customers deployed specs without
                // using allowlists.
                if (GetSpecialCaseRulesForCoverageAndSpecialDevices(parsedPath))
                {
                    shouldExclude = true;
                }
                else
                {
                    if (AbsolutePath.TryCreate(m_context.PathTable, reported.Process.Path, out AbsolutePath processPath)
                        && (excludedToolsAndPaths.Contains((processPath, parsedPath))
                            || GetSpecialCaseRulesForSpecialTools(processPath, parsedPath)))
                    {
                        shouldExclude = true;
                        excludedToolsAndPaths.Add((processPath, parsedPath));
                    }
                }

                // We want to know if a directory was created by this pip. This means the create directory operation succeeded, but also that this directory was not deleted before by the build.
                // Directories that fall in this category were already reported in SandboxedProcessReports as report lines get received, so we only add it here if we can already find it. Consider the following cases:
                // 1) The directory was there before the build started but some other pip deletes it first. Then it will be reported in SandboxedProcessReports as a deleted directory and won't be added as a created one here. So we won't
                // consider it here as a created directory. This is correct since the directory is not actually created by the build.
                // 2) Consider now that the pip that deletes the directory in 1) is a cache hit. Removed directories are not reported on cache hit (because a cache replay does not remove them). This means the directory is now present.
                // And that means the directory cannot be effectively created by this pip (which may introduce a different behavior than the one in 1), but that's a bigger problem to solve). So we won't add it here either.
                // 3) The directory is removed and re-created by this same pip. In that case it will be reported to the output filesystem as removed and won't be added here. Similarly to 1), this is the right behavior. On cache replay, the directory
                // will never be removed, and the fact that it is still not considered as a created directory is sound.
                if (m_pip.AllowUndeclaredSourceReads
                    && reported.RequestedAccess.HasFlag(RequestedAccess.Write)
                    && reported.IsDirectoryEffectivelyCreated()
                       // m_fileSystemView can be null for some tests
                    && m_fileSystemView?.ExistCreatedDirectoryInOutputFileSystem(parsedPath) == true)
                {
                    createdDirectoriesMutable.Add(parsedPath);
                }

                // We should exclude writes on directory paths from the accesses constructed here, which are supposed to be inputs to the pip
                // Note the similar logic below with respect to accesses on output files, but in the case of directories we just remove the
                // write operations while potentially keeping some other ones (like probes) in the observed accesses - this is because typically
                // the directories are not fully declared as outputs, so we'd rather keep track of 'input' observations on those paths.
                shouldExclude |= reported.IsDirectoryCreationOrRemoval();

                accessesByPath.TryGetValue(parsedPath, out CompactSet<ReportedFileAccess> existingAccessesToPath);
                accessesByPath[parsedPath] = !shouldExclude ? existingAccessesToPath.Add(reported) : existingAccessesToPath;
            }

            foreach (var output in m_pip.FileOutputs)
            {
                if (!accessesByPath.ContainsKey(output.Path))
                {
                    if (RequireOutputObservation(output))
                    {
                        unobservedOutputs ??= new List<AbsolutePath>();
                        unobservedOutputs.Add(output.Path);
                    }
                }
                else
                {
                    accessesByPath.Remove(output.Path);
                }
            }

            using PooledObjectWrapper<Dictionary<AbsolutePath, HashSet<AbsolutePath>>> dynamicWriteAccessWrapper = ProcessPools.DynamicWriteAccesses.GetInstance();
            using PooledObjectWrapper<Dictionary<AbsolutePath, ObservedFileAccess>> accessesUnsortedWrapper = ProcessPools.AccessUnsorted.GetInstance();
            using var excludedPathsWrapper = Pools.GetAbsolutePathSet();
            using var maybeUnresolvedAbsentAccessessWrapper = Pools.GetAbsolutePathSet();
            using var fileExistenceDenialsWrapper = Pools.GetAbsolutePathSet();

            var fileExistenceDenials = fileExistenceDenialsWrapper.Instance;

            // We count outputs created by this executor as part of pip preparation 
            // as 'created by the pip', for consistency with the output filesystem
            // TODO: This is conditionalized by AllowUndeclaredSourceReads because
            // today we only roundtrip created directories through the cache in that case.
            // We need to remove this condition along with a fingerprint version bump
            if (m_pip.AllowUndeclaredSourceReads)
            {
                createdDirectoriesMutable.AddRange(m_engineCreatedPipOutputDirectories);
            }

            var maybeUnresolvedAbsentAccesses = maybeUnresolvedAbsentAccessessWrapper.Instance;

            // Initializes all shared directories in the pip with no accesses
            var dynamicWriteAccesses = dynamicWriteAccessWrapper.Instance;
            foreach (var sharedDirectory in m_sharedOpaqueDirectoryRoots.Keys)
            {
                dynamicWriteAccesses[sharedDirectory] = new HashSet<AbsolutePath>();
            }

            // Remove all the special file accesses that need removal.
            RemoveEmptyOrInjectableFileAccesses(accessesByPath);

            var accessesUnsorted = accessesUnsortedWrapper.Instance;
            foreach (KeyValuePair<AbsolutePath, CompactSet<ReportedFileAccess>> entry in accessesByPath)
            {
                bool isDirectoryLocation = false;
                bool hasEnumeration = false;
                bool isProbe = true;
                bool hasDirectoryReparsePointTreatedAsFile = false;

                // There is always at least one access for reported path by construction
                // Since the set of accesses occur on the same path, the manifest path is
                // the same for all of them. We only need to query one of them.
                ReportedFileAccess firstAccess = entry.Value.First();


                bool isPathCandidateToBeOwnedByASharedOpaque = false;

                foreach (var access in entry.Value)
                {
                    // If isDirectoryLocation was not already set, try one of the methods below
                    bool isAccessingDirectoryLocation = IsAccessingDirectoryLocation(entry.Key, access);
                    isDirectoryLocation |= isAccessingDirectoryLocation;

                    hasDirectoryReparsePointTreatedAsFile |=
                        OperatingSystemHelper.IsWindowsOS // Only relevant on Windows.
                        && !isAccessingDirectoryLocation
                        && access.OpenedFileOrDirectoryAttributes.HasFlag(FlagsAndAttributes.FILE_ATTRIBUTE_REPARSE_POINT | FlagsAndAttributes.FILE_ATTRIBUTE_DIRECTORY);

                    // To treat the paths as file probes, all accesses to the path must be the probe access.
                    isProbe &= access.RequestedAccess == RequestedAccess.Probe;

                    if (access.RequestedAccess == RequestedAccess.Probe && IsIncrementalToolAccess(access))
                    {
                        isProbe = false;
                    }

                    // TODO: Remove this when WDG can grog this feature with no flag.
                    if (m_sandboxConfig.UnsafeSandboxConfiguration.ExistingDirectoryProbesAsEnumerations ||
                        access.RequestedAccess == RequestedAccess.Enumerate)
                    {
                        hasEnumeration = true;
                    }

                    // if the access is a write on a file (that is, not on a directory), then the path is a candidate to be part of a shared opaque
                    isPathCandidateToBeOwnedByASharedOpaque |=
                        access.RequestedAccess.HasFlag(RequestedAccess.Write)
                        && !access.FlagsAndAttributes.HasFlag(FlagsAndAttributes.FILE_ATTRIBUTE_DIRECTORY)
                        && !access.IsDirectoryCreationOrRemoval();

                    // If the access is a shared opaque candidate and it was denied based on file existence, keep track of it
                    if (isPathCandidateToBeOwnedByASharedOpaque && access.Method == FileAccessStatusMethod.FileExistenceBased && access.Status == FileAccessStatus.Denied)
                    {
                        fileExistenceDenials.Add(entry.Key);
                    }
                }

                // if the path is still a candidate to be part of a shared opaque, that means there was at least a write to that path. If the path is then
                // in the cone of a shared opaque, then it is a dynamic write access
                bool? isAccessUnderASharedOpaque = null;
                if (isPathCandidateToBeOwnedByASharedOpaque &&
                    IsAccessUnderASharedOpaque(firstAccess, dynamicWriteAccesses, out AbsolutePath sharedDynamicDirectoryRoot))
                {
                    bool shouldBeConsideredAsOutput = ShouldBeConsideredSharedOpaqueOutput(fileAccessReportingContext, firstAccess, out FileAccessAllowlist.MatchType matchType);

                    if (matchType != FileAccessAllowlist.MatchType.NoMatch)
                    {
                        // If the match is cacheable/uncacheable, report the access so that pip executor knows if the pip can be cached or not.
                        fileAccessReportingContext.AddAndReportUncacheableFileAccess(firstAccess, matchType);
                    }

                    if (shouldBeConsideredAsOutput)
                    {
                        dynamicWriteAccesses[sharedDynamicDirectoryRoot].Add(entry.Key);
                        isAccessUnderASharedOpaque = true;
                    }

                    // This is a known output, so don't store it
                    continue;
                }
                // if the candidate was discarded because it was not under a shared opaque, make sure the set of denials based on file existence is also kept in sync
                else if (isPathCandidateToBeOwnedByASharedOpaque)
                {
                    fileExistenceDenials.Remove(entry.Key);
                }

                // The following two lines need to be removed in order to report file accesses for
                // undeclared files and sealed directories. But since this is a breaking change, we do
                // it under an unsafe flag.
                if (m_sandboxConfig.UnsafeSandboxConfiguration.IgnoreUndeclaredAccessesUnderSharedOpaques)
                {
                    // If the access occurred under any of the pip shared opaque outputs, and the access is not happening on any known input paths (neither dynamic nor static)
                    // then we just skip reporting the access. Together with the above step, this means that no accesses under shared opaques that represent outputs are actually
                    // reported as observed accesses. This matches the same behavior that occurs on static outputs.
                    if (!allInputPathsUnderSharedOpaques.Contains(entry.Key)
                        && (isAccessUnderASharedOpaque == true || IsAccessUnderASharedOpaque(firstAccess, dynamicWriteAccesses, out _)))
                    {
                        continue;
                    }
                }

                // Absent accesses may still contain reparse points. If we are fully resolving them, keep track of them for further processing
                if (!hasEnumeration
                    && EnableFullReparsePointResolving(m_configuration, m_pip)
                    && entry.Value.All(fa => (fa.Error == NativeIOConstants.ErrorPathNotFound || fa.Error == NativeIOConstants.ErrorFileNotFound)))
                {
                    maybeUnresolvedAbsentAccesses.Add(entry.Key);
                }

                ObservationFlags observationFlags = ObservationFlags.None;

                if (isProbe)
                {
                    observationFlags |= ObservationFlags.FileProbe;
                }

                if (isDirectoryLocation && !hasDirectoryReparsePointTreatedAsFile)
                {
                    observationFlags |= ObservationFlags.DirectoryLocation;
                }

                if (hasEnumeration)
                {
                    observationFlags |= ObservationFlags.Enumeration;
                }

                accessesUnsorted.Add(entry.Key, new ObservedFileAccess(entry.Key, observationFlags, entry.Value));
            }

            // AccessesUnsorted might include various accesses to directories leading to the files inside of shared opaques,
            // mainly CreateDirectory and ProbeDirectory. To make strong fingerprint computation more stable, we are excluding such
            // accesses from the list that is passed into the ObservedInputProcessor (as a result, they will not be a part of the path set).
            //
            // Example, given this path: '\sod\dir1\dir2\file.txt', we will exclude accesses to dir1 and dir2 only.
            var excludedPaths = excludedPathsWrapper.Instance;
            foreach (var sod in dynamicWriteAccesses)
            {
                foreach (var file in sod.Value)
                {
                    var pathElement = file.GetParent(m_context.PathTable);

                    while (pathElement.IsValid && pathElement != sod.Key && excludedPaths.Add(pathElement))
                    {
                        pathElement = pathElement.GetParent(m_context.PathTable);
                    }
                }
            }

            createdDirectories = createdDirectoriesMutable.ToReadOnlySet();

            var mutableWriteAccesses = new Dictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>>(dynamicWriteAccesses.Count);

            // We know that all accesses here were write accesses, but we don't actually know if in the end the corresponding file
            // still exists or whether the file was replaced with a directory afterwards. E.g.:
            // * the tool could have created a file but removed it right after
            // * the tool could have created a file but then removed it and created a directory
            // We only care about the access if its final shape is not a directory
            bool reparsePointProduced = false;

            using (var existenceAssertionsWrapper = Pools.GetFileArtifactSet())
            {
                HashSet<FileArtifact> existenceToAssert = existenceAssertionsWrapper.Instance;

                foreach (var kvp in dynamicWriteAccesses)
                {
                    // Let's validate here the existence assertions for shared opaques
                    // Exclusive opaque content is unknown at this point, so it is validated at a later stage
                    Contract.Assert(existenceToAssert.Count == 0);
                    var assertions = m_pipGraphFileSystemView?.GetExistenceAssertionsUnderOpaqueDirectory(m_sharedOpaqueDirectoryRoots[kvp.Key]);
                    // This is null for some tests
                    if (assertions != null)
                    {
                        existenceToAssert.AddRange(assertions);
                    }

                    var fileWrites = new List<FileArtifactWithAttributes>(kvp.Value.Count);
                    mutableWriteAccesses[kvp.Key] = fileWrites;
                    foreach (AbsolutePath writeAccess in kvp.Value)
                    {
                        string outputPath = writeAccess.ToString(m_pathTable);
                        var maybeResult = FileUtilities.TryProbePathExistence(outputPath, followSymlink: false, out var isReparsePoint);
                        reparsePointProduced |= isReparsePoint;

                        if (!maybeResult.Succeeded)
                        {
                            Logger.Log.CannotProbeOutputUnderSharedOpaque(
                                m_loggingContext,
                                m_pip.GetDescription(m_context),
                                writeAccess.ToString(m_pathTable),
                                maybeResult.Failure.DescribeIncludingInnerFailures());

                            sharedDynamicDirectoryWriteAccesses = CollectionUtilities.EmptyDictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>>();
                            observedAccesses = CollectionUtilities.EmptySortedReadOnlyArray<ObservedFileAccess, ObservedFileAccessExpandedPathComparer>(
                                new ObservedFileAccessExpandedPathComparer(m_context.PathTable.ExpandedPathComparer));

                            return false;
                        }

                        switch (maybeResult.Result)
                        {
                            case PathExistence.ExistsAsDirectory:
                                // Directories are not reported as explicit content, since we don't have the functionality today to persist them in the cache.
                                continue;
                            case PathExistence.ExistsAsFile:
                                // If the written file was a denied write based on file existence, that means an undeclared file was overriden.
                                // This file could be an allowed undeclared source or a file completely alien to the build, not mentioned at all.
                                var artifact = FileArtifact.CreateOutputFile(writeAccess);
                                fileWrites.Add(FileArtifactWithAttributes.Create(
                                    artifact,
                                    FileExistence.Required,
                                    isUndeclaredFileRewrite: fileExistenceDenials.Contains(writeAccess)));

                                // We found an output, remove it from the set of assertions to verify
                                existenceToAssert.Remove(artifact);
                                break;
                            case PathExistence.Nonexistent:
                                fileWrites.Add(FileArtifactWithAttributes.Create(FileArtifact.CreateOutputFile(writeAccess), FileExistence.Temporary));
                                break;
                        }
                    }

                    // There are some outputs that were asserted as belonging to the shared opaque that were not found
                    if (existenceToAssert.Count != 0)
                    {
                        Logger.Log.ExistenceAssertionUnderOutputDirectoryFailed(
                            m_loggingContext,
                            m_pip.GetDescription(m_context),
                            existenceToAssert.First().Path.ToString(m_pathTable),
                            kvp.Key.ToString(m_pathTable));

                        sharedDynamicDirectoryWriteAccesses = CollectionUtilities.EmptyDictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>>();
                        observedAccesses = CollectionUtilities.EmptySortedReadOnlyArray<ObservedFileAccess, ObservedFileAccessExpandedPathComparer>(
                            new ObservedFileAccessExpandedPathComparer(m_context.PathTable.ExpandedPathComparer));

                        return false;
                    }
                }
            }

            sharedDynamicDirectoryWriteAccesses = mutableWriteAccesses;

            // Consider the scenario where path/dir/file gets probed but at probing time the path is absent. Afterwards, a dir junction path/dir gets created, pointing
            // to path/target, and then path/target/file is created. Since path/dir/file was absent at probing time, detours doesn't resolve it because there is nothing
            // to resolve. However, the creation of the dir junction and file makes path/dir/file existing but unresolved. However, path/dir/file won't be there on cache lookup, the probe will
            // come back as absent and therefore we get a consistent cache miss.
            // Let's try to make sure then that unresolved absent probes don't end up in the observed path set. Here we address the case when reparse point are produced by the same pip. Cross-pip
            // scenarios are not addressed here.

            if (EnableFullReparsePointResolving(m_configuration, m_pip) && reparsePointProduced)
            {
                foreach (AbsolutePath absentAccess in maybeUnresolvedAbsentAccesses)
                {
                    // If the access is still absent, there is nothing to resolve
                    if (FileUtilities.TryProbePathExistence(absentAccess.ToString(m_pathTable), followSymlink: false) is var existence
                        && (!existence.Succeeded || existence.Result == PathExistence.Nonexistent))
                    {
                        continue;
                    }

                    // If the resolved path is the same as the original one, the probe didn't contain reparse points
                    var resolvedPath = m_reparsePointResolver.ResolveIntermediateDirectoryReparsePoints(absentAccess);
                    if (resolvedPath == absentAccess)
                    {
                        continue;
                    }

                    // We have an access that was originally absent, now it is present, and contains unresolved reparse points. Let exclude it from the
                    // acceses.
                    excludedPaths.Add(absentAccess);

                    // We only include a synthetic resolved one if the path is not an output of the pip (we never report accesses on outputs)
                    // It is not expected that a pip contains too many output directories, so going through each of them should be fine.
                    if (dynamicWriteAccesses.All(kvp => !kvp.Value.Contains(resolvedPath)))
                    {
                        m_fileAccessManifest.TryFindManifestPathFor(resolvedPath, out AbsolutePath manifestPath, out _);

                        // Generate equivalent accesses with the resolved path
                        foreach (ReportedFileAccess originalAccess in accessesByPath[absentAccess])
                        {
                            ReportedFileAccess syntheticAccess = originalAccess.CreateWithPathAndAttributes(
                                resolvedPath == manifestPath ? null : resolvedPath.ToString(m_pathTable),
                                manifestPath,
                                originalAccess.FlagsAndAttributes);

                            // Check if there is already an access with that path, and add to it in that case
                            if (accessesUnsorted.TryGetValue(resolvedPath, out var observedFileAccess))
                            {
                                accessesUnsorted[resolvedPath] = new ObservedFileAccess(
                                    resolvedPath,
                                    observedFileAccess.ObservationFlags,
                                    observedFileAccess.Accesses.Add(syntheticAccess));
                            }
                            else
                            {
                                accessesUnsorted.Add(
                                    resolvedPath,
                                    new ObservedFileAccess(
                                        resolvedPath,
                                        ObservationFlags.FileProbe,
                                        new CompactSet<ReportedFileAccess>().Add(syntheticAccess)));
                            }
                        }
                    }
                }
            }

            var filteredAccessesUnsorted = accessesUnsorted.Values.Where(shouldIncludeAccess);

            observedAccesses = SortedReadOnlyArray<ObservedFileAccess, ObservedFileAccessExpandedPathComparer>.CloneAndSort(
                filteredAccessesUnsorted,
                new ObservedFileAccessExpandedPathComparer(m_context.PathTable.ExpandedPathComparer));

            return true;

            bool shouldIncludeAccess(ObservedFileAccess access)
            {
                // if not in the excludedPaths set --> include
                if (!excludedPaths.Contains(access.Path))
                {
                    return true;
                }

                // else, include IFF:
                //   (1) access is a directory enumeration, AND
                //   (2) the directory was not created by this pip
                return
                    access.ObservationFlags.HasFlag(ObservationFlags.Enumeration)
                    && !access.Accesses.Any(rfa => rfa.IsDirectoryCreation());
            }
        }

        private bool IsAccessUnderASharedOpaque(
            ReportedFileAccess access,
            Dictionary<AbsolutePath, HashSet<AbsolutePath>> dynamicWriteAccesses,
            out AbsolutePath sharedDynamicDirectoryRoot)
        {
            sharedDynamicDirectoryRoot = AbsolutePath.Invalid;

            // Shortcut the search if there are no shared opaques or the manifest path
            // is invalid. For the latter, this can occur when the access happens on a location
            // the path table doesn't know about. But this means the access is not under a shared opaque.
            if (dynamicWriteAccesses.Count == 0 || !access.ManifestPath.IsValid)
            {
                return false;
            }

            // The only construct that defines a scope for detours that we allow under shared opaques is sealed directories
            // (other constructs are allowed, but they don't affect detours manifest).
            // This means we cannot directly use the manifest path to check if it is the root of a shared opaque,
            // but we can start looking up from the reported manifest path.
            // Because of bottom-up search, if a pip declares nested shared opaque directories, the innermost directory
            // wins the ownership of a produced file.

            var initialNode = access.ManifestPath.Value;

            // TODO: consider adding a cache from manifest paths to containing shared opaques. It is likely
            // that many writes for a given pip happens under the same cones.
            bool isFirstNode = true;
            foreach (var currentNode in m_context.PathTable.EnumerateHierarchyBottomUp(initialNode))
            {
                // In order to attribute accesses on shared opaque directory paths themselves to the parent
                // shared opaque directory, this will skip checking the first node when enumerating the path
                // as long as it is the same as the access 
                if (isFirstNode && access.Path == null)
                {
                    isFirstNode = false;
                    continue;
                }

                var currentPath = new AbsolutePath(currentNode);

                if (dynamicWriteAccesses.ContainsKey(currentPath))
                {
                    sharedDynamicDirectoryRoot = currentPath;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether a reported file access should be considered as an output under a shared opaque.
        /// </summary>
        private bool ShouldBeConsideredSharedOpaqueOutput(
            FileAccessReportingContext fileAccessReportingContext,
            ReportedFileAccess access,
            out FileAccessAllowlist.MatchType matchType)
        {
            if (m_sandboxConfig.UnsafeSandboxConfiguration.DoNotApplyAllowListToDynamicOutputs())
            {
                matchType = FileAccessAllowlist.MatchType.NoMatch;
                return true;
            }

            // Given a file access f under a shared opaque.
            // - NoMatch => true
            // - MatchCacheable / NotCacheable
            //   - case 1: f is static source/output => true
            //   - case 2: f is under exclusive opaque => true
            //   - otherwise: false
            //
            // In case 1 & 2 above, f is considered an output so that the pip executor can detect for double writes.
            matchType = fileAccessReportingContext.MatchReportedFileAccess(access);
            return matchType == FileAccessAllowlist.MatchType.NoMatch
                || (access.TryParseAbsolutePath(m_context, m_loggingContext, m_pip, out AbsolutePath accessPath)
                    && (m_pipGraphFileSystemView.TryGetLatestFileArtifactForPath(accessPath).IsValid
                        || (m_pipGraphFileSystemView.IsPathUnderOutputDirectory(accessPath, out bool isSharedOpaque) && !isSharedOpaque)));
        }

        /// <summary>
        /// Checks if a path starts with a prefix, given the fact that the path may start with "\??\" or "\\?\".
        /// </summary>
        private static bool PathStartsWith(string path, string prefix, StringComparison? comparison = default)
        {
            comparison ??= OperatingSystemHelper.PathComparison;

            return path.StartsWith(prefix, comparison.Value)
                   || path.StartsWith(@"\??\" + prefix, comparison.Value)
                   || path.StartsWith(@"\\?\" + prefix, comparison.Value);
        }

        private void RemoveEmptyOrInjectableFileAccesses(Dictionary<AbsolutePath, CompactSet<ReportedFileAccess>> accessesByPath)
        {
            var accessesToRemove = new List<AbsolutePath>();

            // CollectionUtilities all the file accesses that need to be removed.
            foreach (var absolutePath in accessesByPath.Keys)
            {
                if (!absolutePath.IsValid)
                {
                    continue;
                }

                if (accessesByPath[absolutePath].Count == 0)
                {
                    // Remove empty accesses and don't bother checking the rest.
                    accessesToRemove.Add(absolutePath);
                    continue;
                }

                // Remove only entries that come from unknown or from System, or Invalid mounts.
                bool removeEntry = false;

                if (m_semanticPathExpander != null)
                {
                    SemanticPathInfo semanticPathInfo = m_semanticPathExpander.GetSemanticPathInfo(absolutePath);
                    removeEntry = !semanticPathInfo.IsValid || semanticPathInfo.IsSystem;
                }

                if (m_semanticPathExpander == null || removeEntry)
                {
                    if (IsRemovableInjectedFileAccess(absolutePath))
                    {
                        accessesToRemove.Add(absolutePath);
                    }
                }
            }

            // Now, remove all the entries that were scheduled for removal.
            foreach (AbsolutePath pathToRemove in accessesToRemove)
            {
                accessesByPath.Remove(pathToRemove);
            }
        }

        private void RemoveInjectableFileAccesses(ISet<ReportedFileAccess> unexpectedAccesses)
        {
            var accessesToRemove = new List<ReportedFileAccess>();

            // CollectionUtilities all the file accesses that need to be removed.
            foreach (var reportedAccess in unexpectedAccesses)
            {
                AbsolutePath.TryCreate(m_pathTable, reportedAccess.GetPath(m_pathTable), out AbsolutePath absolutePath);
                if (!absolutePath.IsValid)
                {
                    continue;
                }

                // Remove only entries that come from unknown or from System, or Invalid mounts.
                bool removeEntry = false;

                if (m_semanticPathExpander != null)
                {
                    SemanticPathInfo semanticPathInfo = m_semanticPathExpander.GetSemanticPathInfo(absolutePath);
                    removeEntry = !semanticPathInfo.IsValid ||
                        semanticPathInfo.IsSystem;
                }

                if (m_semanticPathExpander == null || removeEntry)
                {
                    if (IsRemovableInjectedFileAccess(absolutePath))
                    {
                        accessesToRemove.Add(reportedAccess);
                    }
                }
            }

            // Now, remove all the entries that were scheduled for removal.
            foreach (ReportedFileAccess pathToRemove in accessesToRemove)
            {
                unexpectedAccesses.Remove(pathToRemove);
            }
        }

        private bool IsRemovableInjectedFileAccess(AbsolutePath absolutePath)
        {
            string path = absolutePath.ToString(m_pathTable);
            string filename = absolutePath.GetName(m_pathTable).IsValid ? absolutePath.GetName(m_pathTable).ToString(m_pathTable.StringTable) : null;
            string extension = absolutePath.GetExtension(m_pathTable).IsValid ? absolutePath.GetExtension(m_pathTable).ToString(m_pathTable.StringTable) : null;

            // Special case: The VC++ compiler probes %PATH% for c1xx.exe.  This file does not even exist, but
            // VC still looks for it.  We ignore it.
            if (StringComparer.OrdinalIgnoreCase.Equals(filename, "c1xx.exe"))
            {
                return true;
            }

            // This Microsoft Tablet PC component injects itself into processes.
            if (StringComparer.OrdinalIgnoreCase.Equals(filename, "tiptsf.dll"))
            {
                return true;
            }

            // This Microsoft Office InfoPath component injects itself into processes.
            if (StringComparer.OrdinalIgnoreCase.Equals(filename, "MSOXMLMF.DLL"))
            {
                return true;
            }

            // This Bonjour Namespace Provider from Apple component injects itself into processes.
            if (StringComparer.OrdinalIgnoreCase.Equals(filename, "mdnsNSP.DLL"))
            {
                return true;
            }

            // This ATI Technologies / HydraVision component injects itself into processes.
            if (StringComparer.OrdinalIgnoreCase.Equals(filename, "HydraDMH.dll") ||
                StringComparer.OrdinalIgnoreCase.Equals(filename, "HydraDMH64.dll") ||
                StringComparer.OrdinalIgnoreCase.Equals(filename, "HydraMDH.dll") ||
                StringComparer.OrdinalIgnoreCase.Equals(filename, "HydraMDH64.dll"))
            {
                return true;
            }

            // Special case: VSTest.Console.exe likes to open these files if Visual Studio is installed. This is benign.
            if (StringComparer.OrdinalIgnoreCase.Equals(filename, "Microsoft.VisualStudio.TeamSystem.Licensing.dll") ||
                StringComparer.OrdinalIgnoreCase.Equals(filename, "Microsoft.VisualStudio.QualityTools.Sqm.dll"))
            {
                return true;
            }

            // Special case: On Windows 8, the CLR keeps ngen logs: http://msdn.microsoft.com/en-us/library/hh691758(v=vs.110).aspx
            // Alternative, one can disable this on one's machine:
            //   From: Mark Miller (CLR)
            //   Sent: Monday, March 24, 2014 10:57 AM
            //   Subject: RE: automatic native image generation
            //   You can disable Auto NGEN activity (creation of logs) by setting:
            //   HKLM\SOFTWARE\Microsoft\.NETFramework\NGen\Policy\[put version here]\OptimizeUsedBinaries = (REG_DWORD)0
            //   Do this in the Wow Hive as well and for each version (v2.0 and v4.0 is the entire set)
            //   Mark
            if (StringComparer.OrdinalIgnoreCase.Equals(extension, ".log")
                && path.StartsWith(s_appDataLocalMicrosoftClrPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Special case: The NVIDIA driver injects itself into all processes and accesses files under '%ProgamData%\NVIDIA Corporation'.
            // Also attempts to access aurora.dll and petromod_nvidia_profile_identifier.ogl files.
            // Ignore file accesses in that directory or with those file names.
            if (path.StartsWith(s_nvidiaProgramDataPrefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(s_nvidiaProgramFilesPrefix, StringComparison.OrdinalIgnoreCase)
                || StringComparer.OrdinalIgnoreCase.Equals(filename, "aurora.dll")
                || StringComparer.OrdinalIgnoreCase.Equals(filename, "petromod_nvidia_profile_identifier.ogl"))
            {
                return true;
            }

            // Special case: The Forefront TMG client injects itself into some processes and accesses files under %ProgramFilxX86%\Forefront TMG Client'.
            // The check also considers the fact that the input path can start with prefix, '\\?' or '\??\'.
            if (PathStartsWith(path, s_forefrontTmgClientProgramFilesX86Prefix))
            {
                return true;
            }

            return false;
        }

        private bool RequireOutputObservation(FileArtifactWithAttributes output) =>
            output.IsRequiredOutputFile                   // Don't check temp & optional, nor stdout/stderr files.
            && (output.Path != m_pip.StandardError.Path)
            && (output.Path != m_pip.StandardOutput.Path)
            && (output.Path != m_pip.TraceFile.Path)
            && output.RewriteCount <= 1                   // Rewritten files are not required to be written by the tool
            && !m_shouldPreserveOutputs                   // Preserve output is incompatible with validation of output access (see Bug #1043533)
            && !m_pip.HasUntrackedChildProcesses
            && MonitorFileAccesses;

        private void LogFinishedFailed(SandboxedProcessResult result) =>
            Logger.Log.PipProcessFinishedFailed(m_loggingContext, m_pip.SemiStableHash, m_pipDescription, result.ExitCode);

        private string DurationForLog(TimeSpan duration)
        {
            if (duration.TotalSeconds < 1)
            {
                return $"{(int)duration.TotalMilliseconds}ms";
            }
            else if (duration.TotalMinutes < 1)
            {
                return $"{(int)duration.TotalSeconds}s";
            }
            else
            {
                return $"{Bound(duration.TotalMinutes)}min";
            }
        }
        
        private void LogTookTooLongWarning(TimeSpan timeout, TimeSpan time, TimeSpan warningTimeout) =>
            Logger.Log.PipProcessTookTooLongWarning(
                m_loggingContext,
                m_pip.SemiStableHash,
                m_pipDescription,
                DurationForLog(time),
                DurationForLog(warningTimeout),
                DurationForLog(timeout));

        private void LogTookTooLongError(SandboxedProcessResult result, TimeSpan timeout, TimeSpan time,
                                         string stdError, string stdOut, bool errorWasTruncated, bool errorWasFiltered)
        {
            Contract.Assume(result.Killed);
            Analysis.IgnoreArgument(result);
            string dumpString = result.DumpFileDirectory ?? string.Empty;
            FormatOutputAndPaths(
                stdOut,
                stdError,
                out string outputTolog,
                out _,
                out _,
                errorWasTruncated,
                errorWasFiltered);

            Logger.Log.PipProcessTookTooLongError(
                m_loggingContext,
                m_pip.SemiStableHash,
                m_pipDescription,
                DurationForLog(time),
                DurationForLog(timeout),
                dumpString,
                EnsureToolOutputIsNotEmpty(outputTolog));
        }

        private async Task<bool> TrySaveAndLogStandardErrorAsync(SandboxedProcessResult result)
        {
            if (!await TrySaveStandardErrorAsync(result))
            {
                return false;
            }

            string standardErrorPath = result.StandardError.FileName;
            Logger.Log.PipProcessStandardError(m_loggingContext, m_pip.SemiStableHash, m_pipDescription, standardErrorPath);
            return true;
        }

        private async Task<bool> TrySaveStandardErrorAsync(SandboxedProcessResult result)
        {
            try
            {
                await result.StandardError.SaveAsync();
            }
            catch (BuildXLException ex)
            {
                PipStandardIOFailed(GetFileName(SandboxedProcessFile.StandardError), ex);
                return false;
            }
            return true;
        }

        private async Task<bool> TrySaveAndLogStandardOutputAsync(SandboxedProcessResult result)
        {
            if (!await TrySaveStandardOutputAsync(result))
            {
                return false;
            }

            string standardOutputPath = result.StandardOutput.FileName;
            Logger.Log.PipProcessStandardOutput(m_loggingContext, m_pip.SemiStableHash, m_pipDescription, standardOutputPath);
            return true;
        }

        private async Task<bool> TrySaveStandardOutputAsync(SandboxedProcessResult result)
        {
            try
            {
                await result.StandardOutput.SaveAsync();
            }
            catch (BuildXLException ex)
            {
                PipStandardIOFailed(GetFileName(SandboxedProcessFile.StandardOutput), ex);
                return false;
            }
            return true;
        }

        private async Task<bool> TrySaveTraceFileAsync(SandboxedProcessResult result)
        {
            try
            {
                await result.TraceFile.SaveAsync();
            }
            catch (BuildXLException ex)
            {
                PipStandardIOFailed(GetFileName(SandboxedProcessFile.Trace), ex);
                return false;
            }

            return true;
        }

        private void LogChildrenSurvivedKilled() =>
            Logger.Log.PipProcessChildrenSurvivedKilled(
                m_loggingContext,
                m_pip.SemiStableHash,
                m_pipDescription);

        private bool CheckExpectedOutputs()
        {
            bool allOutputsPresent = true;

            // The process exited cleanly (though it may have accessed unexpected files), so we should expect that all outputs are
            // present. We don't bother checking or logging this otherwise, since the output of a failed process is unusable anyway.
            // Only required output artifacts should be considered by this method.
            // Optional output files could be or could not be a part of the valid process output.
            using (var stringPool1 = Pools.GetStringList())
            {
                var expectedMissingOutputs = stringPool1.Instance;

                foreach (FileArtifactWithAttributes output in m_pip.FileOutputs)
                {
                    if (!output.MustExist())
                    {
                        continue;
                    }

                    var expectedOutput = output.ToFileArtifact();
                    string expectedOutputPath = expectedOutput.Path.ToString(m_pathTable);

                    if (!FileExistsNoFollow(expectedOutputPath) &&
                        expectedOutput != m_pip.StandardOutput &&
                        expectedOutput != m_pip.StandardError &&
                        expectedOutput != m_pip.TraceFile)
                    {
                        allOutputsPresent = false;
                        Logger.Log.PipProcessMissingExpectedOutputOnCleanExit(
                            m_loggingContext,
                            pipSemiStableHash: m_pip.SemiStableHash,
                            pipDescription: m_pipDescription,
                            pipSpecPath: m_pip.Provenance.Token.Path.ToString(m_context.PathTable),
                            pipWorkingDirectory: m_pip.WorkingDirectory.ToString(m_context.PathTable),
                            path: expectedOutputPath);
                        expectedMissingOutputs.Add(expectedOutputPath);
                    }
                }

                Func<string[], string> pathAggregator = (paths) =>
                {
                    Array.Sort(paths, OperatingSystemHelper.PathComparer);
                    return string.Join(Environment.NewLine, paths);
                };

                if (expectedMissingOutputs.Count > 0)
                {
                    Logger.Log.PipProcessExpectedMissingOutputs(
                        m_loggingContext,
                        m_pip.SemiStableHash,
                        m_pipDescription,
                        pathAggregator(expectedMissingOutputs.ToArray()));
                }
            }

            return allOutputsPresent;
        }

        private static bool FileExistsNoFollow(string path)
        {
            var maybeResult = FileUtilities.TryProbePathExistence(path, followSymlink: false);
            var existsAsFile = maybeResult.Succeeded && maybeResult.Result == PathExistence.ExistsAsFile;
            return existsAsFile;
        }

        // (lubol): TODO: Add handling of the translate paths strings. Add code here to address VSO Task# 989041.
        private TimeSpan GetEffectiveTimeout(TimeSpan? configuredTimeout, int defaultTimeoutMs, double multiplier)
        {
            if (m_pip.IsService)
            {
                // Service pips live for the duration of the build. Don't kill them. NOTE: This doesn't include service shutdown
                // pips which should have a timeout.
                return Process.MaxTimeout;
            }

            TimeSpan timeout = configuredTimeout ?? TimeSpan.FromMilliseconds(defaultTimeoutMs);
            try
            {
                timeout = TimeSpan.FromMilliseconds(timeout.TotalMilliseconds * multiplier);
            }
            catch (OverflowException)
            {
                return Process.MaxTimeout;
            }

            return timeout > Process.MaxTimeout ? Process.MaxTimeout : timeout;
        }

        private string PreparePipTimeoutDumpDirectory(ISandboxConfiguration sandboxConfig, Process pip, PathTable pathTable)
        {
            AbsolutePath rootDirectory = sandboxConfig.TimeoutDumpDirectory.IsValid ? sandboxConfig.TimeoutDumpDirectory : pip.UniqueOutputDirectory;
            ExpandedAbsolutePath? directory = rootDirectory.IsValid ? new ExpandedAbsolutePath(rootDirectory.Combine(pathTable, pip.FormattedSemiStableHash), pathTable) : null;
            if (directory.HasValue)
            {
                PreparePathForDirectory(directory.Value, createIfNonExistent: false);
            }

            return directory?.ExpandedPath;
        }

        private void FormatOutputAndPaths(string standardOut, string standardError,
            out string outputToLog, out string pathsToLog, out string extraMessage,
            bool messageWasTruncated,
            bool errorWasFiltered)
        { 
            extraMessage = string.Empty;
            pathsToLog = string.Empty;    
            
            // Add extra message about message was filtered or truncated
            if (errorWasFiltered)
            {
                extraMessage = "This message has been filtered by a regex. ";
            }

            if (messageWasTruncated)
            {
                extraMessage += "This message has been truncated. ";
            }

            // These messages are results after filtered or handled by plugin.
            // Only display error/out in log if it is non-empty. This avoids adding duplicated newlines in the message.
            bool standardOutEmpty = string.IsNullOrWhiteSpace(standardOut);
            bool standardErrorEmpty = string.IsNullOrWhiteSpace(standardError);

            outputToLog = (standardOutEmpty ? string.Empty : standardOut)
                + (!standardOutEmpty && !standardErrorEmpty ? Environment.NewLine : string.Empty)
                + (standardErrorEmpty ? string.Empty : standardError);

            // If the messages were filtered or truncated, log stdout/stderr files in log directory.
            // Add extra message to guide user to find the files.
            if (errorWasFiltered || messageWasTruncated)
            {
                // These are the paths used to persist the standard out and standard error in log directory.
                // CODESYNC: PipExecutor.LoadAndPersistPipStdOutput
                var relativeDirectoryPath = Path.Combine(StdOutputsDirNameInLog, m_pip.FormattedSemiStableHash);
                string standardOutPathInLog = Path.Combine(relativeDirectoryPath, SandboxedProcessFile.StandardOutput.DefaultFileName());
                string standardErrorPathInLog = Path.Combine(relativeDirectoryPath, SandboxedProcessFile.StandardError.DefaultFileName());
    
                extraMessage += "Please find the complete stdout/stderr in the following file(s) in the log directory.";
                pathsToLog = standardOutPathInLog + Environment.NewLine + standardErrorPathInLog;
            }
        }

        private record LogErrorResult
        {
            public LogErrorResult(bool success, bool errorWasTruncated)
            {
                Success = success;
                // ErrorWasTruncated should be forced to false when logging was not successful
                ErrorWasTruncated = success && errorWasTruncated;
            }

            /// <summary>
            /// Whether logging was successful
            /// </summary>
            public readonly bool Success;

            /// <summary>
            /// Whether the Error that was logged was truncated for any reason. This may be due to an error regex or
            /// due to the error being too long
            /// </summary>
            public readonly bool ErrorWasTruncated;
        }

        private async Task<LogErrorResult> TryLogErrorAsync(SandboxedProcessResult result, bool allOutputsPresent, bool failedDueToWritingToStdErr, TimeSpan processTotalWallClockTime)
        {
            // Initializing error regex just before it is actually needed to save some cycles.
            if (!await TryInitializeErrorRegexAsync())
            {
                return new LogErrorResult(success: false, errorWasTruncated: false);
            }

            var errorFilter = OutputFilter.GetErrorFilter(m_errorRegex, m_pip.EnableMultiLineErrorScanning);

            bool errorWasFilteredOrTruncated = false;
            var exceedsLimit = OutputExceedsLimit(result.StandardOutput) || OutputExceedsLimit(result.StandardError);
            if (!exceedsLimit || m_sandboxConfig.OutputReportingMode == OutputReportingMode.TruncatedOutputOnError)
            {
                var standardErrorFilterResult = await TryFilterAsync(result.StandardError, errorFilter, appendNewLine: true);
                var standardOutputFilterResult = await TryFilterAsync(result.StandardOutput, errorFilter, appendNewLine: true);
                string standardError = standardErrorFilterResult.FilteredOutput;
                string standardOutput = standardOutputFilterResult.FilteredOutput;

                if (standardError == null || standardOutput == null)
                {
                    return new LogErrorResult(success: false, errorWasTruncated: false);
                }

                if (string.IsNullOrEmpty(standardError) && string.IsNullOrEmpty(standardOutput))
                {
                    // Standard error and standard output are empty.
                    // This could be because the filter is too aggressive and the entire output was filtered out.
                    // Rolling back to a non-filtered approach because some output is better than nothing.
                    standardErrorFilterResult = await TryFilterLineByLineAsync(result.StandardError, s => true, appendNewLine: true);
                    standardOutputFilterResult = await TryFilterLineByLineAsync(result.StandardOutput, s => true, appendNewLine: true);
                    standardError = standardErrorFilterResult.FilteredOutput;
                    standardOutput = standardOutputFilterResult.FilteredOutput;
                }

                errorWasFilteredOrTruncated = standardErrorFilterResult.IsTruncatedOrFilterd || standardOutputFilterResult.IsTruncatedOrFilterd;

                HandleErrorsFromTool(standardError);
                HandleErrorsFromTool(standardOutput);

                // Send the message to plugin if there is log parsing plugin available
                standardError = await (m_pluginEP?.ProcessStdOutAndErrorAsync(standardError, true) ?? Task.FromResult(standardError));
                standardOutput = await (m_pluginEP?.ProcessStdOutAndErrorAsync(standardOutput, true) ?? Task.FromResult(standardOutput));
                var errorFiltered = standardErrorFilterResult.IsFiltered || standardOutputFilterResult.IsFiltered;
                if (!result.TimedOut)
                {
                    LogPipProcessError(result, allOutputsPresent, failedDueToWritingToStdErr, standardError, standardOutput, errorWasFilteredOrTruncated, errorFiltered);
                }
                else
                {
                    LogTookTooLongError(result, m_timeout, processTotalWallClockTime, standardError, standardOutput, errorWasFilteredOrTruncated, errorFiltered);
                }

                return new LogErrorResult(success: true, errorWasTruncated: errorWasFilteredOrTruncated);
            }

            long stdOutTotalLength = 0;
            long stdErrTotalLength = 0;

            // The output exceeds the limit and the full output has been requested. Emit it in chunks
            if (!await TryEmitFullOutputInChunks(errorFilter))
            {
                return new LogErrorResult(success: false, errorWasTruncated: errorWasFilteredOrTruncated);
            }

            if (stdOutTotalLength == 0 && stdErrTotalLength == 0)
            {
                // Standard error and standard output are empty.
                // This could be because the filter is too aggressive and the entire output was filtered out.
                // Rolling back to a non-filtered approach because some output is better than nothing.
                errorWasFilteredOrTruncated = false;
                if (!await TryEmitFullOutputInChunks(filter: null))
                {
                    return new LogErrorResult(success: false, errorWasTruncated: errorWasFilteredOrTruncated);
                }
            }

            return new LogErrorResult(success: true, errorWasTruncated: errorWasFilteredOrTruncated);

            async Task<bool> TryEmitFullOutputInChunks(OutputFilter? filter)
            {
                using (TextReader errorReader = CreateReader(result.StandardError))
                {
                    using (TextReader outReader = CreateReader(result.StandardOutput))
                    {
                        if (errorReader == null || outReader == null)
                        {
                            return false;
                        }

                        while (errorReader.Peek() != -1 || outReader.Peek() != -1)
                        {
                            string stdError = await ReadNextChunkAsync(errorReader, result.StandardError, filter?.LinePredicate);
                            string stdOut = await ReadNextChunkAsync(outReader, result.StandardOutput, filter?.LinePredicate);

                            if (stdError == null || stdOut == null)
                            {
                                return false;
                            }

                            if (filter?.Regex != null)
                            {
                                stdError = filter.Value.ExtractMatches(stdError);
                                stdOut = filter.Value.ExtractMatches(stdOut);
                            }

                            if (string.IsNullOrEmpty(stdOut) && string.IsNullOrEmpty(stdError))
                            {
                                continue;
                            }

                            stdOutTotalLength += stdOut.Length;
                            stdErrTotalLength += stdError.Length;

                            HandleErrorsFromTool(stdError);
                            HandleErrorsFromTool(stdOut);

                            // Send the message to plugin if there is log parsing plugin available
                            stdError = await (m_pluginEP?.ProcessStdOutAndErrorAsync(stdError, true) ?? Task.FromResult(stdError));
                            stdOut = await (m_pluginEP?.ProcessStdOutAndErrorAsync(stdOut, true) ?? Task.FromResult(stdOut));

                            // For the last iteration, check if error was truncated
                            if (errorReader.Peek() == -1 && outReader.Peek() == -1)
                            {
                                if (stdOutTotalLength != result.StandardOutput.Length || stdErrTotalLength != result.StandardError.Length)
                                {
                                    errorWasFilteredOrTruncated = true;
                                }
                            }

                            if (!result.TimedOut)
                            {
                                LogPipProcessError(result, allOutputsPresent, failedDueToWritingToStdErr, stdError, stdOut, errorWasFilteredOrTruncated, false);
                            }
                            else
                            {
                                LogTookTooLongError(result, m_timeout, processTotalWallClockTime, stdError, stdOut, errorWasFilteredOrTruncated, false);
                            }
                        }

                        return true;
                    }
                }
            }
        }

        private void LogPipProcessError(
            SandboxedProcessResult result,
            bool allOutputsPresent,
            bool failedDueToWritingToStdErr,
            string stdError,
            string stdOut,
            bool errorWasTruncated,
            bool errorWasFiltered)
        {
            FormatOutputAndPaths(
                stdOut,
                stdError,
                out string outputTolog,
                out string outputPathsToLog,
                out string messageAboutPathsToLog,
                errorWasTruncated,
                errorWasFiltered);

            string optionalMessage = !allOutputsPresent
                ? EventConstants.PipProcessErrorMissingOutputsSuffix
                : (failedDueToWritingToStdErr
                    ? EventConstants.PipProcessErrorWroteToStandardError
                    : string.Empty);

            long totalElapsedTimeMS = Convert.ToInt64(result.PrimaryProcessTimes.TotalWallClockTime.TotalMilliseconds);

            Logger.Log.PipProcessError(
                m_loggingContext,
                m_pip.SemiStableHash,
                m_pipDescription,
                m_pip.Provenance.Token.Path.ToString(m_pathTable),
                m_workingDirectory,
                GetToolName(),
                EnsureToolOutputIsNotEmpty(outputTolog),
                messageAboutPathsToLog,
                AddTrailingNewLineIfNeeded(outputPathsToLog),
                result.ExitCode,
                optionalMessage,
                m_pip.GetShortDescription(m_context),
                totalElapsedTimeMS);

        }

        private void HandleErrorsFromTool(string error)
        {
            if (error == null)
            {
                return;
            }

            var process = GetProcessKind(AbsolutePath.Invalid);

            if (process == SpecialProcessKind.Csc)
            {
                // BUG 1124595
                const string Pattern =
                    @"'(?<ThePath>(?:[a-zA-Z]\:|\\\\[\w\.]+\\[\w.$]+)\\(?:[\w]+\\)*\w([\w.])+)' -- The process cannot access the file because it is being used by another process";
                foreach (Match match in Regex.Matches(error, Pattern, RegexOptions.IgnoreCase).Cast<Match>())
                {
                    var path = match.Groups["ThePath"].Value;
                    if (!FileUtilities.TryFindOpenHandlesToFile(path, out string diagnosticInfo))
                    {
                        diagnosticInfo = nameof(FileUtilities.TryFindOpenHandlesToFile) + " failed";
                    }

                    Logger.Log.PipProcessToolErrorDueToHandleToFileBeingUsed(
                        m_loggingContext,
                        m_pip.SemiStableHash,
                        m_pipDescription,
                        m_pip.Provenance.Token.Path.ToString(m_pathTable),
                        m_workingDirectory,
                        process.ToString(),
                        path,
                        diagnosticInfo);
                }
            }
        }

        private static bool OutputExceedsLimit(SandboxedProcessOutput output) => output.Length > MaxConsoleLength;

        private async Task<bool> TryLogOutputWithTimeoutAsync(SandboxedProcessResult result, LoggingContext loggingContext)
        {
            TimeSpan timeoutPerChunk = TimeSpan.FromMinutes(2);
            using TextReader errorReader = CreateReader(result.StandardError);
            using TextReader outReader = CreateReader(result.StandardOutput);
            if (errorReader == null || outReader == null)
            {
                return false;
            }

            try
            {
                while (errorReader.Peek() != -1 || outReader.Peek() != -1)
                {
                    string stdError = await ReadNextChunkAsync(errorReader, result.StandardError).WithTimeoutAsync(timeoutPerChunk);
                    string stdOut = await ReadNextChunkAsync(outReader, result.StandardOutput).WithTimeoutAsync(timeoutPerChunk);

                    if (stdError == null || stdOut == null)
                    {
                        return false;
                    }

                    // Sometimes stdOut/StdErr contains NUL characters (ASCII code 0). While this does not
                    // create a problem for text editors (they will either display the NUL char or show it
                    // as whitespace), NUL char messes up with ETW logging BuildXL uses. When a string is
                    // passed into ETW, it is treated as null-terminated string => everything after the
                    // the first NUL char is dropped. This causes Bug 1310020.
                    //
                    // Remove all NUL chars before logging the output.
                    if (stdOut.Contains("\0"))
                    {
                        stdOut = stdOut.Replace("\0", string.Empty);
                    }

                    if (stdError.Contains("\0"))
                    {
                        stdError = stdError.Replace("\0", string.Empty);
                    }

                    bool stdOutEmpty = string.IsNullOrWhiteSpace(stdOut);
                    bool stdErrorEmpty = string.IsNullOrWhiteSpace(stdError);

                    // Send the message to plugin if there is log parsing plugin available
                    stdError = await (m_pluginEP?.ProcessStdOutAndErrorAsync(stdError, true) ?? Task.FromResult(stdError));
                    stdOut = await (m_pluginEP?.ProcessStdOutAndErrorAsync(stdOut, true) ?? Task.FromResult(stdOut));

                    string outputToLog = (stdOutEmpty ? string.Empty : stdOut) +
                        (!stdOutEmpty && !stdErrorEmpty ? Environment.NewLine : string.Empty) +
                        (stdErrorEmpty ? string.Empty : stdError);

                    await Task.Run(
                        () => Logger.Log.PipProcessOutput(
                            m_loggingContext,
                            m_pip.SemiStableHash,
                            m_pipDescription,
                            m_pip.Provenance.Token.Path.ToString(m_pathTable),
                            m_workingDirectory,
                            AddTrailingNewLineIfNeeded(outputToLog)))
                    .WithTimeoutAsync(timeoutPerChunk);
                }
            }
            catch (TimeoutException)
            {
                // Return true even if a timeout occurs with a warning logged to avoid failing the build
                Logger.Log.SandboxedProcessResultLogOutputTimeout(loggingContext, m_pip.FormattedSemiStableHash, timeoutPerChunk.Minutes);
            }

            return true;
        }

        private TextReader CreateReader(SandboxedProcessOutput output)
        {
            try
            {
                return output.CreateReader();
            }
            catch (BuildXLException ex)
            {
                PipStandardIOFailed(GetFileName(output.File), ex);
                return null;
            }
        }

        /// <summary>
        /// Reads chunk of output from reader, with optional filtering:
        /// the result will only contain lines, that satisfy provided predicate (if it is non-null).
        /// </summary>
        private async Task<string> ReadNextChunkAsync(TextReader reader, SandboxedProcessOutput output, Predicate<string> filterPredicate = null)
        {
            try
            {
                using (var wrapper = Pools.StringBuilderPool.GetInstance())
                {
                    StringBuilder sb = wrapper.Instance;
                    for (int i = 0; i < OutputChunkInLines;)
                    {
                        string line = await reader.ReadLineAsync();
                        if (line == null)
                        {
                            if (sb.Length == 0)
                            {
                                return string.Empty;
                            }

                            break;
                        }

                        if (filterPredicate == null || filterPredicate(line))
                        {
                            sb.AppendLine(line);
                            ++i;
                        }
                    }

                    return sb.ToString();
                }
            }
            catch (BuildXLException ex)
            {
                PipStandardIOFailed(GetFileName(output.File), ex);
                return null;
            }
        }

        /// <summary>
        /// Tries to filter the standard error and standard output streams for warnings.
        /// </summary>
        public async Task<bool> TryLogWarningAsync(SandboxedProcessOutput standardError, SandboxedProcessOutput standardOutput)
        {
            var errorFilterResult = await TryFilterLineByLineAsync(standardError, IsWarning, appendNewLine: true);
            var outputFilterResult = await TryFilterLineByLineAsync(standardOutput, IsWarning, appendNewLine: true);

            string warningsError = standardError == null ? string.Empty : errorFilterResult.FilteredOutput;
            string warningsOutput = standardOutput == null ? string.Empty : outputFilterResult.FilteredOutput;

            if (warningsError == null ||
                warningsOutput == null)
            {
                return false;
            }

            bool warningWasTruncatedOrFiltered = errorFilterResult.IsTruncatedOrFilterd || outputFilterResult.IsTruncatedOrFilterd;

            FormatOutputAndPaths(
                warningsOutput,
                warningsError,
                out string outputTolog,
                out string outputPathsToLog,
                out string messageAboutPathsToLog,
                warningWasTruncatedOrFiltered,
                errorFilterResult.IsFiltered || outputFilterResult.IsFiltered);

            Logger.Log.PipProcessWarning(
                m_loggingContext,
                m_pip.SemiStableHash,
                m_pipDescription,
                m_pip.Provenance.Token.Path.ToString(m_pathTable),
                m_workingDirectory,
                GetToolName(),
                EnsureToolOutputIsNotEmpty(outputTolog),
                messageAboutPathsToLog,
                AddTrailingNewLineIfNeeded(outputPathsToLog));
            return true;
        }

        private static string EnsureToolOutputIsNotEmpty(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return "Tool failed without writing any output stream";
            }

            return output;
        }

        private static string AddTrailingNewLineIfNeeded(string message)
        {
            // If empty/whitespace/newline, return empty.
            // If text, return text+newLine.
            // If text+newLine, return text+newLine.
            // if text+newLine+newLine, return text+newLine.

            return string.IsNullOrWhiteSpace(message) ? string.Empty : message.TrimEnd(Environment.NewLine.ToCharArray()) + Environment.NewLine;
        }

        private string GetToolName()
        {
            return m_pip.GetToolName(m_pathTable).ToString(m_pathTable.StringTable);
        }

        // Returns the number of surviving processes that resulted in errors.
        // The caller should avoid throwing errors when this is zero.
        private int ReportSurvivingChildProcesses(SandboxedProcessResult result)
        {
            Contract.Assume(result.Killed);

            var unexpectedSurvivingChildProcesses = result
                .SurvivingChildProcesses
                .Where(pr =>
                    !hasProcessName(pr, "ProcessTreeContextCreator.exe") &&
                    !m_pip.AllowedSurvivingChildProcessNames.Any(procName => hasProcessName(pr, procName.ToString(m_context.StringTable))));

            int numErrors = unexpectedSurvivingChildProcesses.Count();

            if (numErrors == 0)
            {
                int numSurvive = result.SurvivingChildProcesses.Count();

                if (numSurvive > JobObject.InitialProcessIdListLength)
                {
                    // Report for too many surviving child processes.
                    Logger.Log.PipProcessChildrenSurvivedTooMany(
                        m_loggingContext,
                        m_pip.SemiStableHash,
                        m_pipDescription,
                        numSurvive,
                        Environment.NewLine + string.Join(Environment.NewLine, result.SurvivingChildProcesses.Select(p => p.Path)));
                }
            }
            else
            {
                Logger.Log.PipProcessChildrenSurvivedError(
                        m_loggingContext,
                        m_pip.SemiStableHash,
                        m_pipDescription,
                        numErrors,
                        Environment.NewLine + string.Join(Environment.NewLine, unexpectedSurvivingChildProcesses.Select(p => $"{p.Path} ({p.ProcessId}) {getProcessArgs(p)}")));
            }

            return numErrors;

            static bool hasProcessName(ReportedProcess pr, string name)
            {
                return string.Equals(Path.GetFileName(pr.Path), name, OperatingSystemHelper.PathComparison);
            }

            static string getProcessArgs(ReportedProcess pr)
            {
                if (string.IsNullOrWhiteSpace(pr.ProcessArgs))
                {
                    return string.Empty;
                }

                return pr.ProcessArgs.Substring(0, Math.Min(256, pr.ProcessArgs.Length));
            }
        }

        private void LogFileAccessTables(Process pip)
        {
            // TODO: This dumps a very low-level table; consider producing a nicer representation
            // Instead of logging each line, consider storing manifest and logging file name
            foreach (string line in m_fileAccessManifest.Describe())
            {
                Logger.Log.PipProcessFileAccessTableEntry(
                    m_loggingContext,
                    pip.SemiStableHash,
                    pip.GetDescription(m_context),
                    line);
            }
        }

        private static long Bound(double value)
        {
            return value >= long.MaxValue ? long.MaxValue : (long)value;
        }

        private Stream TryOpenStandardInputStream(out bool success)
        {
            success = true;
            if (!m_pip.StandardInput.IsValid)
            {
                return null;
            }

            return m_pip.StandardInput.IsFile
                ? TryOpenStandardInputStreamFromFile(m_pip.StandardInput.File, out success)
                : TryOpenStandardInputStreamFromData(m_pip.StandardInput.Data, out success);
        }

        private Stream TryOpenStandardInputStreamFromFile(FileArtifact file, out bool success)
        {
            Contract.Requires(file.IsValid);

            success = true;
            string standardInputFileName = file.Path.ToString(m_pathTable);

            try
            {
                return FileUtilities.CreateAsyncFileStream(
                    standardInputFileName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete);
            }
            catch (BuildXLException ex)
            {
                PipStandardIOFailed(standardInputFileName, ex);
                success = false;
                return null;
            }
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:DisposeObjectsBeforeLosingScope", Justification = "Disposed by its caller")]
        private Stream TryOpenStandardInputStreamFromData(in PipData data, out bool success)
        {
            Contract.Requires(data.IsValid);

            success = true;

            // TODO: Include encoding in the pip data. Task 869176
            return new MemoryStream(CharUtilities.Utf8NoBomNoThrow.GetBytes(data.ToString(m_context.PathTable)));
        }

        private bool PreparePathForOutputFile(AbsolutePath filePath, HashSet<AbsolutePath> outputDirectories = null, bool doNotThrowExceptionOnFailure = false)
        {
            Contract.Requires(filePath.IsValid);

            string expandedFilePath = filePath.ToString(m_pathTable);
            var mayBeDeleted = FileUtilities.TryDeletePathIfExists(expandedFilePath, tempDirectoryCleaner: m_tempDirectoryCleaner);

            if (!mayBeDeleted.Succeeded)
            {
                if (doNotThrowExceptionOnFailure)
                {
                    return false;
                }
                else
                {
                    mayBeDeleted.Failure.Throw();
                }
            }

            AbsolutePath parentDirectory = filePath.GetParent(m_pathTable);

            if (outputDirectories == null || outputDirectories.Add(parentDirectory))
            {
                // Ensure parent directory exists.
                CreatePipOutputDirectory(new ExpandedAbsolutePath(parentDirectory, m_pathTable));
            }

            m_remoteSbDataBuilder?.AddOutputDirectory(parentDirectory);

            return true;
        }

        private void PreparePathForDirectory(ExpandedAbsolutePath directoryPath, bool createIfNonExistent, bool isPipOutputPath = false)
        {
            bool exists = false;
            var path = directoryPath.Path;
            var expandedDirectoryPath = directoryPath.ExpandedPath;
            if (FileUtilities.DirectoryExistsNoFollow(expandedDirectoryPath))
            {
                FileUtilities.DeleteDirectoryContents(expandedDirectoryPath, deleteRootDirectory: false, tempDirectoryCleaner: m_tempDirectoryCleaner);
                exists = true;
            }
            else if (FileUtilities.FileExistsNoFollow(expandedDirectoryPath))
            {
                // We expect to produce a directory, but a file with the same name exists on disk.
                FileUtilities.DeleteFile(expandedDirectoryPath, tempDirectoryCleaner: m_tempDirectoryCleaner);
            }

            if (!exists && createIfNonExistent)
            {
                if (isPipOutputPath)
                {
                    CreatePipOutputDirectory(directoryPath, knownAbsent: true);
                }
                else
                {
                    // Directories created by this executor that do not correspond to
                    // bona-fide outputs of the pip, namely the temporary directory for
                    // the pip and the pip timeout dump directory.
                    FileUtilities.CreateDirectory(expandedDirectoryPath);
                }
            }
        }

        /// <summary>
        /// Whether we should preserve the given declared static file or directory output.
        /// </summary>
        private bool ShouldPreserveDeclaredOutput(AbsolutePath path, HashSet<AbsolutePath> allowlist)
        {
            if (!m_shouldPreserveOutputs)
            {
                // If the pip does not allow preserve outputs, return false
                return false;
            }

            if (allowlist.Count == 0)
            {
                // If the allowlist is empty, every output is preserved
                return true;
            }

            if (allowlist.Contains(path))
            {
                // Only preserve the file or directories that are given in the allowlist.
                return true;
            }

            return false;
        }

        private bool IsIncrementalToolAccess(ReportedFileAccess access)
        {
            if (!IsIncrementalPreserveOutputPip)
            {
                return false;
            }

            if (m_incrementalToolFragments.Count == 0)
            {
                return false;
            }

            string toolPath = access.Process.Path;

            if (m_incrementalToolMatchCache.TryGetValue(toolPath, out bool result))
            {
                return result;
            }

            result = false;
            foreach (var incrementalToolSuffix in m_incrementalToolFragments)
            {
                if (toolPath.EndsWith(incrementalToolSuffix, OperatingSystemHelper.PathComparison))
                {
                    result = true;
                    break;
                }
            }

            // Cache the result
            m_incrementalToolMatchCache.AddItem(toolPath, result);
            return result;
        }

        /// <summary>
        /// Logs a process sub phase and ensures the time is recored in the Counters
        /// </summary>
        public static void LogSubPhaseDuration(LoggingContext context, Pip pip, SandboxedProcessCounters counter, TimeSpan duration, string extraInfo = "")
        {
            Counters.AddToCounter(counter, duration);
            if (Processes.ETWLogger.Log.IsEnabled(EventLevel.Verbose, Keywords.Diagnostics))
            {
                Logger.Log.LogPhaseDuration(context, pip.FormattedSemiStableHash, counter.ToString(), duration.ToString(), extraInfo);
            }
        }
    }
}
