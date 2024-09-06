﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Engine.Cache;
using BuildXL.Engine.Cache.Artifacts;
#if PLATFORM_OSX
using BuildXL.Interop;
#endif
using BuildXL.Interop.Unix;
using BuildXL.Ipc;
using BuildXL.Ipc.Common;
using BuildXL.Ipc.Common.Multiplexing;
using BuildXL.Ipc.Interfaces;
using BuildXL.Plugin;
using BuildXL.Native.IO;
using BuildXL.ProcessPipExecutor;
using BuildXL.Pips;
using BuildXL.Pips.Artifacts;
using BuildXL.Pips.DirectedGraph;
using BuildXL.Pips.Filter;
using BuildXL.Pips.Graph;
using BuildXL.Pips.Operations;
using BuildXL.Processes;
using BuildXL.Processes.Sideband;
using BuildXL.Processes.Remoting;
using BuildXL.Processes.VmCommandProxy;
using BuildXL.Scheduler.Artifacts;
using BuildXL.Scheduler.Cache;
using BuildXL.Scheduler.Diagnostics;
using BuildXL.Scheduler.Distribution;
using BuildXL.Scheduler.FileSystem;
using BuildXL.Scheduler.Fingerprints;
using BuildXL.Scheduler.Graph;
using BuildXL.Scheduler.IncrementalScheduling;
using BuildXL.Scheduler.Tracing;
using BuildXL.Scheduler.WorkDispatcher;
using BuildXL.Storage;
using BuildXL.Storage.ChangeTracking;
using BuildXL.Storage.InputChange;
using BuildXL.Storage.Fingerprints;
using BuildXL.Tracing;
using BuildXL.Tracing.CloudBuild;
using BuildXL.Utilities;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Configuration.Mutable;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Core.Tasks;
using BuildXL.Utilities.Tracing;
using BuildXL.ViewModel;
using static BuildXL.Processes.SandboxedProcessFactory;
using static BuildXL.Utilities.Core.FormattableStringEx;
using Logger = BuildXL.Scheduler.Tracing.Logger;
using Process = BuildXL.Pips.Operations.Process;
using System.Runtime.InteropServices;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.Interfaces;

namespace BuildXL.Scheduler
{
    using DirectoryMemberEntry = ValueTuple<AbsolutePath, string>;

    /// <summary>
    /// Class implementing the scheduler.
    /// </summary>
    /// <remarks>
    /// All public methods are thread-safe.
    /// </remarks>
    [SuppressMessage("Microsoft.Maintainability", "CA1506")]
    public partial class Scheduler : IPipScheduler, IPipExecutionEnvironment, IFileContentManagerHost, IOperationTrackerHost, IDisposable
    {
        #region Constants

        /// <summary>
        /// The limit for I/O pip execution step to log a warning message.
        /// </summary>
        private const int PipExecutionIOStepDelayedLimitMin = 30;

        /// <summary>
        /// Ref count for pips which have executed already (distinct from ref count 0; ready to execute)
        /// </summary>
        private const int CompletedRefCount = -1;

        /// <summary>
        /// How many bits of priority are assigned to critical path portion.  The rest are assigned to the priority in the spec files
        /// </summary>
        private const int CriticalPathPriorityBitCount = 24;

        /// <summary>
        /// The max priority assigned to pips in the initial critical path
        /// prioritization
        /// </summary>
        private const int MaxInitialPipPriority = (1 << CriticalPathPriorityBitCount) - 1;

        /// <summary>
        /// The piptypes we want to report stats for.
        /// </summary>
        private static readonly PipType[] s_pipTypesToLogStats =
        {
            PipType.Process, PipType.SealDirectory,
            PipType.CopyFile, PipType.WriteFile, PipType.Ipc,
        };

        /// <summary>
        /// The piptypes we want to report stats for.
        /// </summary>
        private static readonly PipType[] s_processPipTypesToLogStats =
        {
            PipType.Process,
        };

        /// <summary>
        /// Prefix used by the IDE integration for the name of EventHandles marking a value's successful completion
        /// </summary>
        public const string IdeSuccessPrefix = "Success";

        /// <summary>
        /// Prefix used by the IDE integration for the name of EventHandles marking a value's failed completion
        /// </summary>
        public const string IdeFailurePrefix = "Failure";

        /// <summary>
        /// The CPU utilization that gets logged when performance data isn't available
        /// </summary>
        public const long UtilizationWhenCountersNotAvailable = -1;

        /// <summary>
        /// Interval used to capture the status snapshot
        /// </summary>
        private const long StatusSnapshotInterval = 60;

        /// <summary>
        /// Interval used to trigger/unpause ChooseWorkerCpu queue
        /// </summary>
        /// <remarks>
        /// ChooseWorkerCpu queue can be paused for a long time if the available RAM is low when it was run the last time.
        /// We do not trigger ChooseWorkerCpu whenever the available RAM increases.
        /// Instead, we started to trigger it every 60 seconds regardless of available RAM.
        /// </remarks>
        private const long ChooseWorkerCpuInterval = 60;

        /// <summary>
        /// Dirty nodes file name for incremental scheduling.
        /// </summary>
        public const string DefaultIncrementalSchedulingStateFile = "SchedulerIncrementalSchedulingState";

        /// <summary>
        /// File change tracker file name.
        /// </summary>
        public const string DefaultSchedulerFileChangeTrackerFile = "SchedulerFileChangeTracker";

        /// <summary>
        /// <see cref="FingerprintStore"/> directory name.
        /// </summary>
        public const string FingerprintStoreDirectory = "FingerprintStore";

        /// <summary>
        /// <see cref="ILayoutConfiguration.SharedOpaqueSidebandDirectory"/> directory name.
        /// </summary>
        public const string SharedOpaqueSidebandDirectory = "SharedOpaqueSidebandState";

        private const int SealDirectoryContentFilterTimeoutMs = 60_000; // 60s

        #endregion Constants

        #region State

        /// <summary>
        /// Configuration. Ideally shouldn't be used because it reads config state not related to the scheduler.
        /// </summary>
        private readonly IConfiguration m_configuration;

        /// <summary>
        /// Configuration for schedule settings.
        /// </summary>
        private readonly IScheduleConfiguration m_scheduleConfiguration;

        /// <summary>
        /// The operation tracker. Internal for use by distribution
        /// </summary>
        internal readonly OperationTracker OperationTracker;

        /// <summary>
        /// File content manager for handling file materialization/hashing/content state tracking
        /// </summary>
        private readonly FileContentManager m_fileContentManager;

        /// <summary>
        /// Tracker of output materializations.
        /// </summary>
        private PipOutputMaterializationTracker m_pipOutputMaterializationTracker;

        /// <summary>
        /// RootMappings converted to string to be resued by pipExecutor
        /// </summary>
        /// <remarks>
        /// We should see if we can make the sandbox code take AbsolutePaths.
        /// Barring that change, we'd need to convert it constantly for each process
        /// This is the only 'reacheable' place to return a single instance for a given build
        /// through the IPipExecutionEnvironment.
        /// This is not ideal, but baby steps :)
        /// </remarks>
        private readonly IReadOnlyDictionary<string, string> m_rootMappings;

        /// <summary>
        /// Object that will simulate cache misses.
        /// </summary>
        /// <remarks>
        /// This is null when none are configured
        /// </remarks>
        private readonly ArtificialCacheMissOptions m_artificialCacheMissOptions;

        private readonly List<Worker> m_workers;

        /// <summary>
        /// Indicates if processes should be scheduled using a unix sandbox when BuildXL is executing
        /// </summary>
        protected virtual bool UnixSandboxingEnabled =>
            OperatingSystemHelper.IsUnixOS &&
            m_configuration.Sandbox.UnsafeSandboxConfiguration.SandboxKind != SandboxKind.None;

        /// <summary>
        /// A connection to a sandbox (for unix sandboxing)
        /// </summary>
        [AllowNull]
        protected ISandboxConnection SandboxConnection;

        /// <summary>
        /// Workers
        /// </summary>
        /// <remarks>
        /// There is at least one worker in the list of workers: LocalWorker.
        /// LocalWorker must be at the beginning of the list. All other workers must be remote.
        /// </remarks>
        public IList<Worker> Workers => m_workers;

        /// <summary>
        /// Enumerates the remote workers
        /// </summary>
        private RemoteWorkerBase[] m_remoteWorkers = new RemoteWorkerBase[0];

        /// <summary>
        /// Encapsulates data and logic for choosing a worker for cpu queue in a distributed build
        /// </summary>
        private readonly ChooseWorkerCpu m_chooseWorkerCpu;

        private readonly ChooseWorkerCacheLookup m_chooseWorkerCacheLookup;
        private readonly ChooseWorkerIpc m_chooseWorkerIpc;

        /// <summary>
        /// Local worker
        /// </summary>
        public LocalWorker LocalWorker { get; }

        /// <summary>
        /// Available workers count
        /// </summary>
        public int AvailableWorkersCount => Workers.Count(a => a.IsAvailable);

        /// <summary>
        /// Available remote workers count
        /// </summary>
        public int AvailableRemoteWorkersCount => Workers.Count(a => a.IsAvailable && a.IsRemote);

        /// <summary>
        /// Contains set up tasks for remote workers in a distributed build. (see <see cref="RemoteWorkerBase.AttachCompletionTask"/>)
        /// </summary>
        private List<Task<bool>> m_workersAttachmentTasks = new();

        /// <summary>
        /// Cached delegate for the main method which executes the pips
        /// </summary>
        private readonly Func<RunnablePip, Task> m_executePipFunc;

        /// <summary>
        /// Cleans temp directories in background
        /// </summary>
        public ITempCleaner TempCleaner { get; }

        /// <summary>
        /// The pip graph
        /// </summary>
        public readonly PipGraph PipGraph;

        /// <summary>
        /// Underlying data-flow graph for the pip graph.
        /// </summary>
        public IReadonlyDirectedGraph DirectedGraph => PipGraph.DirectedGraph;

        /// <summary>
        /// Test hooks.
        /// </summary>
        private readonly SchedulerTestHooks m_testHooks;

        /// <summary>
        /// Whether this is a scheduler used for testing
        /// </summary>
        private readonly bool m_isTestScheduler;

        /// <summary>
        /// Whether the current BuildXL instance serves as a orchestrator node in the distributed build and has workers attached.
        /// </summary>
        public bool AnyRemoteWorkers => m_workers.Count > 1;

        private readonly ConcurrentDictionary<PipId, RunnablePipPerformanceInfo> m_runnablePipPerformance;

        private readonly AbsolutePath m_fileChangeTrackerFile;

        private readonly AbsolutePath m_incrementalSchedulingStateFile;

        private readonly bool m_shouldCreateIncrementalSchedulingState;

        private readonly HashSet<PathAtom> m_outputFileExtensionsForSequentialScan;

        private int m_unresponsivenessFactor = 0;
        private int m_maxUnresponsivenessFactor = 0;
        private DateTime m_statusLastCollected = DateTime.MaxValue;

        private readonly PipRetryInfo m_pipRetryInfo = new PipRetryInfo();
        private readonly PipPropertyInfo m_pipPropertyInfo = new PipPropertyInfo();

        private readonly HashSet<string> m_diskSpaceMonitoredDrives;

        /// <summary>
        /// Top N Pip performance info for telemetry logging
        /// </summary>
        private readonly PerProcessPipPerformanceInformationStore m_perPipPerformanceInfoStore;

        private const double BytesInMb = 1024 * 1024;

        /// <summary>
        /// Task array to keep track of materialization output requests for remote workers.
        /// </summary>
        private ObjectPool<Task[]> m_taskArrayPool;

        /// <summary>
        /// The number of problematic remote workers
        /// </summary>
        private int m_numProblematicWorkers;

        /// <summary>
        /// Enables distribution for the orchestrator node
        /// </summary>
        public void EnableDistribution(RemoteWorkerBase[] remoteWorkers)
        {
            Contract.Requires(remoteWorkers != null);

            Contract.Assert(m_workers.Count == 1, "Local worker must exist");
            Contract.Assert(IsDistributedOrchestrator, $"{nameof(EnableDistribution)} can be called only for the orchestrator node");

            // Ensure that the resource mappings match between workers
            foreach (var worker in remoteWorkers)
            {
                worker.SyncResourceMappings(LocalWorker);
            }

            m_workers.AddRange(remoteWorkers);
            PipExecutionCounters.AddToCounter(PipExecutorCounter.RemoteWorkerCount, remoteWorkers.Length);
            m_taskArrayPool = new ObjectPool<Task[]>(() => new Task[remoteWorkers.Length], tb => { return tb; });
            m_remoteWorkers = remoteWorkers;
        }

        private void StartWorkers(LoggingContext loggingContext)
        {
            m_workersStatusOperation = OperationTracker.StartOperation(Worker.WorkerStatusParentOperationKind, loggingContext);

            // The first of the workers must be local and all others must be remote.
            Contract.Assert(m_workers[0] is LocalWorker && m_workers.Skip(1).All(w => w.IsRemote));

            foreach (var worker in m_workers)
            {
                // Create combined log target for remote workers
                IExecutionLogTarget workerExecutionLogTarget = worker.IsLocal ?
                    ExecutionLog :
                    ExecutionLog?.CreateWorkerTarget((uint)worker.WorkerId);

                worker.InitializeForDistribution(
                    m_workersStatusOperation,
                    m_configuration,
                    PipGraph,
                    workerExecutionLogTarget,
                    m_schedulerCompletion.Task,
                    OnWorkerStatusChanged);

                worker.Start();
            }

            m_workersAttachmentTasks = m_remoteWorkers.Select(static w => w.AttachCompletionTask).ToList();

            ExecutionLog?.WorkerList(new WorkerListEventData { Workers = m_workers.SelectArray(w => w.Name) });
        }

        private bool AnyPendingPipsExceptMaterializeOutputs()
        {
            // We check here whether the scheduler is busy only with materializeOutputs.
            // Because retrieving pip states is expensive, we first calculate how many pips there are in non-materialize queues.
            // If it is 0, then we get the pip states. If there is no ready, waiting, and running pips; it means that the scheduler is done with all work
            // or it is only busy with materializeOutput step.
            // As we mark the pips as completed if materializeOutputsInBackground/fireForgetMaterializeOutput is enabled, they have "Done" state.

            long numRunningOrQueuedOrRemote = PipQueue.NumRunningOrQueuedOrRemote;
            long numRunningOrQueuedExceptMaterialize = numRunningOrQueuedOrRemote - PipQueue.GetNumRunningPipsByKind(DispatcherKind.Materialize) - PipQueue.GetNumQueuedByKind(DispatcherKind.Materialize);

            if (numRunningOrQueuedExceptMaterialize == 0)
            {
                RetrievePipStateCounts(out _, out long readyPips, out long waitingPips, out long runningPips, out _, out _, out _, out _);

                if (readyPips + waitingPips + runningPips == 0)
                {
                    // It means that there are only pips materializing outputs in the background.
                    return false;
                }
            }

            return true;
        }

        private readonly object m_workerStatusLock = new object();
        private OperationContext m_workersStatusOperation;

        private void OnWorkerStatusChanged(Worker worker)
        {
            lock (m_workerStatusLock)
            {
                worker.UpdateStatusOperation();
                AdjustLocalWorkerSlots();
            }
        }

        private void AdjustLocalWorkerSlots()
        {
            int availableRemoteWorkersCount = AvailableRemoteWorkersCount;
            int targetProcessSlots = m_scheduleConfiguration.EffectiveMaxProcesses;
            int newProcessSlots;

            if (availableRemoteWorkersCount < 3)
            {
                // If there are less than 3 remote workers, the process slots for the orchestrator is not affected. 
                // The distribution overhead is negligible in those builds. 
                newProcessSlots = targetProcessSlots;
            }
            else
            {
                // In the distributed builds, the burden on the orchestrator machine increases with the number of available
                // remote workers, especially after 5. 

                double defaultMultiplier = Math.Max(0.1, 1 - (availableRemoteWorkersCount / 10.0));

                // If the user does not pass orchestratorCpuMultiplier, then the local worker slots are configured based on the calculation above.
                newProcessSlots = (int)(targetProcessSlots * (m_scheduleConfiguration.OrchestratorCpuMultiplier ?? defaultMultiplier));
            }

            LocalWorker.AdjustTotalProcessSlots(newProcessSlots);

            int totalProcessSlots = Workers.Where(w => w.IsAvailable).Sum(w => w.TotalProcessSlots);
            PipQueue.SetTotalProcessSlots(totalProcessSlots);
        }

        /// <summary>
        /// The pip runtime information 
        /// </summary>
        private PipRuntimeInfo[] m_pipRuntimeInfos;

        private HistoricPerfDataTable m_historicPerfDataTable;
        private readonly AsyncLazy<HistoricPerfDataTable> m_historicPerfDataTableTask;

        /// <summary>
        /// The last node in the currently computed critical path
        /// </summary>
        private int m_criticalPathTailPipIdValue = unchecked((int)PipId.Invalid.Value);

        /// <summary>
        /// Historical estimation for duration of each pip, indexed by semi stable hashes
        /// </summary>
        public HistoricPerfDataTable HistoricPerfDataTable => (m_historicPerfDataTable = m_historicPerfDataTable ?? (m_historicPerfDataTableTask?.Value ?? new HistoricPerfDataTable(m_loggingContext)));

        /// <summary>
        /// Nodes that are explicitly scheduled by filtering.
        /// </summary>
        /// <remarks>
        /// Only includes the pips matching the filter itself, not their dependencies or dependents that may be included
        /// based on the filter's dependency selection settings
        /// </remarks>
        private HashSet<NodeId> m_explicitlyScheduledNodes;

        /// <summary>
        /// Process nodes that are explicitly scheduled by filtering.
        /// </summary>
        /// <remarks>
        /// Only includes the pips matching the filter itself, not their dependencies or dependents that may be included
        /// based on the filter's dependency selection settings
        /// </remarks>
        private HashSet<NodeId> m_explicitlyScheduledProcessNodes;

        /// <summary>
        /// Nodes that must be executed when dirty build is enabled(/unsafe_forceSkipDeps+)
        /// </summary>
        /// <remarks>
        /// During scheduling, dirty build already skips some pips whose inputs are present.
        /// However, there are some pips cannot be skipped during scheduling even though their inputs are present.
        /// Those pips are in the transitive dependency chain between explicitly scheduled nodes.
        /// This list only contains Process and Copy file pips.
        /// </remarks>
        private HashSet<NodeId> m_mustExecuteNodesForDirtyBuild;

        /// <summary>
        /// Service manager.
        /// </summary>
        private readonly SchedulerServiceManager m_serviceManager;

        /// <summary>
        /// External API server.
        /// </summary>
        [AllowNull]
        private ApiServer m_apiServer;

        [AllowNull]
        private PluginManager m_pluginManager;

        /// <summary>
        /// Tracker for service pips.
        /// </summary>
        [AllowNull]
        private readonly ServicePipTracker m_servicePipTracker;

        /// <summary>
        /// PipIds of all service pips in a graph.
        /// </summary>
        private readonly IReadOnlyList<PipId> m_servicePipIds;

        /// <summary>
        /// Pip table holding all known pips.
        /// </summary>
        private readonly PipTable m_pipTable;

        /// <summary>
        /// Set to true when the scheduler should stop scheduling further pips.
        /// </summary>
        /// <remarks>
        /// It is volatile because all threads accessing this variable should read latest values.
        /// Reading and writing to a boolean are atomic operations.
        /// </remarks>
        private volatile bool m_scheduleTerminating;

        /// <summary>
        /// Set to true when the scheduler is being terminated due to the internal error
        /// </summary>
        /// <remarks>
        /// It is volatile because all threads accessing this variable should read latest values.
        /// Reading and writing to a boolean are atomic operations.
        /// </remarks>
        private volatile bool m_scheduleTerminatingWithInternalError;

        /// <summary>
        /// Number of pips that ran and exited with success fast set, and thus their downstreams were skipped.
        /// </summary>
        /// <remarks>
        /// It is volatile because all threads accessing this variable should read latest values.
        /// Reading and writing to a boolean are atomic operations.
        /// </remarks>
        private volatile int m_pipSkippingDownstreamDueToSuccessFast;

        /// <summary>
        /// Indicates if there are failures in any of the scheduled pips.
        /// </summary>
        /// <remarks>
        /// It is volatile because all threads accessing this variable should read latest values.
        /// Reading and writing to a boolean are atomic operations.
        /// </remarks>
        private volatile bool m_hasFailures;

        /// <summary>
        /// A dedicated thread to schedule pips in the PipQueue.
        /// </summary>
        private Thread m_drainThread;

        /// <summary>
        /// Optional analyzer for post-processing file monitoring violations. Exposed as <see cref="IPipExecutionEnvironment.FileMonitoringViolationAnalyzer" />
        /// for use by executing pips.
        /// </summary>
        private readonly FileMonitoringViolationAnalyzer m_fileMonitoringViolationAnalyzer;

        /// <summary>
        /// Dictionary of number of cache descriptor hits by cache name.
        /// </summary>
        private readonly ConcurrentDictionary<string, int> m_cacheIdHits = new ConcurrentDictionary<string, int>();

        /// <summary>
        /// This set contains all nodes that become direct dirty during execution time (not during scheduling time).
        /// A node becomes direct dirty at execution time if it is executed or if its outputs are deployed from the cache.
        /// </summary>
        private readonly ConcurrentBigSet<NodeId> m_executionTimeDirectDirtiedNodes = new ConcurrentBigSet<NodeId>();

        /// <summary>
        /// Whether the scheduler is initialized with pip stats and priorities
        /// </summary>
        public bool IsInitialized { get; private set; }

        /// <summary>
        /// Time the process started. Used for reporting
        /// </summary>
        private DateTime? m_processStartTimeUtc;

        /// <summary>
        /// Duration until the first pip is started. Used for reporting
        /// </summary>
        private TimeSpan m_timeToFirstPip;

        /// <summary>
        /// Tracks time when the status snapshot was last updated
        /// </summary>
        private DateTime m_statusSnapshotLastUpdated;

        /// <summary>
        /// Tracks time when the tracer was last updated
        /// </summary>
        private DateTime m_tracerLastUpdated;

        /// <summary>
        /// Tracks time when ChooseWorkerCpu queue is unpaused last time.
        /// </summary>
        private DateTime m_chooseWorkerCpuLastUnpaused;

        /// <summary>
        /// Indicates that the scheduler is disposed
        /// </summary>
        private bool m_isDisposed;

        /// <summary>
        /// Nodes to schedule after filtering (not including the dependents of filter passing nodes)
        /// </summary>
        public RangedNodeSet FilterPassingNodes { get; private set; }

        /// <summary>
        /// The graph representing all scheduled nodes after filtering
        /// </summary>
        /// <remarks>
        /// If filtering is not used, this equals to DataflowGraph
        /// </remarks>
        public IReadonlyDirectedGraph ScheduledGraph { get; private set; }

        /// <summary>
        /// Root filter
        /// </summary>
        public RootFilter RootFilter { get; private set; }

        /// <summary>
        /// Whether the first pip is started processing (checking for cache hit) (0: no, 1: yes)
        /// </summary>
        private int m_firstPip;

        /// <summary>
        /// Whether the first pip is started executing (external process launch) (0: no, 1: yes)
        /// </summary>
        private int m_firstExecutedPip;

        /// <summary>
        /// Retrieve the count of pips in all the different states
        /// </summary>
        /// <param name="totalPips">Total number of pips</param>
        /// <param name="readyPips">Number of pending pips</param>
        /// <param name="waitingPips">Number of queued pips</param>
        /// <param name="runningPips">Number of running pips</param>
        /// <param name="donePips">Number of completed pips</param>
        /// <param name="failedPips">Number of failed pips</param>
        /// <param name="skippedPipsDueToFailedDependencies">Number of skipped pips due to failed dependencies</param>
        /// <param name="ignoredPips">Number of ignored pips</param>
        private void RetrievePipStateCounts(
            out long totalPips,
            out long readyPips,
            out long waitingPips,
            out long runningPips,
            out long donePips,
            out long failedPips,
            out long skippedPipsDueToFailedDependencies,
            out long ignoredPips)
        {
            lock (m_statusLock)
            {
                m_pipStateCounters.CollectSnapshot(s_pipTypesToLogStats, m_pipTypesToLogCountersSnapshot);

                readyPips = m_pipTypesToLogCountersSnapshot[PipState.Ready];
                donePips = m_pipTypesToLogCountersSnapshot.DoneCount;
                failedPips = m_pipTypesToLogCountersSnapshot[PipState.Failed];
                skippedPipsDueToFailedDependencies = m_pipTypesToLogCountersSnapshot.SkippedDueToFailedDependenciesCount;
                ignoredPips = m_pipTypesToLogCountersSnapshot.IgnoredCount;
                waitingPips = m_pipTypesToLogCountersSnapshot[PipState.Waiting];
                runningPips = m_pipTypesToLogCountersSnapshot.RunningCount;
            }

            totalPips = m_pipTable.Count;
        }

        /// <summary>
        /// Saves file change tracker and its associates, e.g., incremental scheduling state.
        /// </summary>
        /// <remarks>
        /// This operation requires that the schedule is quiescent, i.e., has completed and nothing else has been queued (end of a build).
        /// </remarks>
        public async Task SaveFileChangeTrackerAsync(LoggingContext loggingContext)
        {
            Contract.Requires(loggingContext != null);

            if (m_fileChangeTracker == null)
            {
                return;
            }

            FileEnvelopeId fileEnvelopeId = m_fileChangeTracker.GetFileEnvelopeToSaveWith();
            string fileChangeTrackerPath = m_fileChangeTrackerFile.ToString(Context.PathTable);

            // Unblock caller.
            await Task.Yield();

            var fileChangeTrackerSaveTask = Task.Run(async () =>
            {
                m_fileChangeTracker.SaveTrackingStateIfChanged(fileChangeTrackerPath, fileEnvelopeId);
                if (m_configuration.Logging.LogExecution && m_configuration.Engine.ScanChangeJournal)
                {
                    await TryDuplicateSchedulerFileToLogDirectoryAsync(loggingContext, m_fileChangeTrackerFile, DefaultSchedulerFileChangeTrackerFile);
                }
            });

            var incrementalStateSaveTask = Task.Run(async () =>
            {
                if (IncrementalSchedulingState != null)
                {
                    string dirtyNodePath = m_incrementalSchedulingStateFile.ToString(Context.PathTable);
                    IncrementalSchedulingState.SaveIfChanged(fileEnvelopeId, dirtyNodePath);

                    if (m_configuration.Logging.LogExecution)
                    {
                        await TryDuplicateSchedulerFileToLogDirectoryAsync(loggingContext, m_incrementalSchedulingStateFile, DefaultIncrementalSchedulingStateFile);
                    }
                }
            });

            await Task.WhenAll(fileChangeTrackerSaveTask, incrementalStateSaveTask);
        }

        private async Task TryDuplicateSchedulerFileToLogDirectoryAsync(LoggingContext loggingContext, AbsolutePath filePath, string destinationFileName)
        {
            Contract.Requires(loggingContext != null);
            Contract.Requires(filePath.IsValid);

            var sourcePath = filePath.ToString(Context.PathTable);
            var logDirectory = m_configuration.Logging.EngineCacheLogDirectory.ToString(Context.PathTable);
            var destinationPath = Path.Combine(logDirectory, destinationFileName);

            try
            {
                await FileUtilitiesExtensions.TryDuplicateOneFileAsync(sourcePath, destinationPath);
            }
            catch (BuildXLException ex)
            {
                Logger.Log.FailedToDuplicateSchedulerFile(loggingContext, sourcePath, destinationPath, (ex.InnerException ?? ex).Message);
            }
        }

        /// <summary>
        /// Tries get pip ref-count.
        /// </summary>
        public bool GetPipRefCount(PipId pipId, out int refCount)
        {
            refCount = -1;

            if (pipId.IsValid && m_pipTable.IsValid(pipId))
            {
                refCount = GetPipRuntimeInfo(pipId).RefCount;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Get the current state of a pip
        /// </summary>
        /// <returns>The pip state</returns>
        public PipState GetPipState(PipId pipId) => GetPipRuntimeInfo(pipId).State;

        /// <summary>
        /// Get the pip type from <see cref="PipId"/>
        /// </summary>
        public PipType GetPipType(PipId pipId) => m_pipTable.GetPipType(pipId);

        private bool IsPipCleanMaterialized(PipId pipId)
        {
            return IncrementalSchedulingState != null && IncrementalSchedulingState.DirtyNodeTracker.IsNodeCleanAndMaterialized(pipId.ToNodeId());
        }

        private bool ShouldIncrementalSkip(PipId pipId)
        {
            var nodeId = pipId.ToNodeId();
            return IncrementalSchedulingState != null && IncrementalSchedulingState.DirtyNodeTracker.IsNodeMaterialized(nodeId) && !m_executionTimeDirectDirtiedNodes.Contains(nodeId);
        }

        #endregion State

        #region Members: Fingerprinting, Content Hashing, and Output Caching

        /// <summary>
        /// Manages materialization and tracking of source and output content.
        /// </summary>
        /// <remarks>
        /// May be null, if pip caching is disabled.
        /// </remarks>
        private LocalDiskContentStore m_localDiskContentStore;

        /// <summary>
        /// File content table.
        /// </summary>
        private readonly FileContentTable m_fileContentTable;

        /// <summary>
        /// Tracks 'dirtied' nodes that need to re-run, build-over-build. The dirty-node set can be updated by scanning volumes for file changes.
        /// (to do so, this records the identities of files used within graph execution
        /// </summary>
        public IIncrementalSchedulingState IncrementalSchedulingState { get; private set; }

        /// <summary>
        /// File change tracker.
        /// </summary>
        private FileChangeTracker m_fileChangeTracker;

        /// <summary>
        /// Journal state.
        /// </summary>
        private readonly JournalState m_journalState;

        /// <summary>
        /// File access allowlist.
        /// </summary>
        private readonly FileAccessAllowlist m_fileAccessAllowlist;

        /// <summary>
        /// Directory membership fingerprinter rule set.
        /// </summary>
        private readonly DirectoryMembershipFingerprinterRuleSet m_directoryMembershipFingerprinterRules;

        /// <summary>
        /// Previous inputs salt.
        /// </summary>
        private readonly PreserveOutputsInfo m_previousInputsSalt;

        /// <summary>
        /// Pip content fingerprinter.
        /// </summary>
        private readonly PipContentFingerprinter m_pipContentFingerprinter;

        /// <summary>
        /// Pip fragment renderer.
        /// </summary>
        private readonly PipFragmentRenderer m_pipFragmentRenderer;

        /// <summary>
        /// IpcProvider for executing IPC pips.
        /// </summary>
        private readonly IpcProviderWithMemoization m_ipcProvider;

        /// <summary>
        /// Fingerprinter for the membership of directories, for generating and validating cache assertions on directories.
        /// </summary>
        private readonly DirectoryMembershipFingerprinter m_directoryMembershipFingerprinter;

        /// <summary>
        /// Expander used when a path string should be machine / configuration independent.
        /// </summary>
        private readonly SemanticPathExpander m_semanticPathExpander;

        /// <summary>
        /// The Execute phase logging context - used during the pip execution only.
        /// </summary>
        private LoggingContext m_executePhaseLoggingContext;

        /// <summary>
        /// Logging interval in ms for performance information. A time interval of 0 represents no restrictions to logging (always log)
        /// </summary>
        private readonly int m_loggingIntervalPeriodMs;

        /// <summary>
        /// Previous UTC time when the UpdateStatus logs where logged
        /// </summary>
        private DateTime m_previousStatusLogTimeUtc;

        /// <summary>
        /// The fingerprint of the build engine.
        /// </summary>
        private readonly string m_buildEngineFingerprint;

        /// <summary>
        /// Gets whether the machine represents a distributed worker
        /// </summary>
        private bool IsDistributedWorker => m_configuration.Distribution.BuildRole == DistributedBuildRoles.Worker;

        /// <summary>
        /// Gets whether the machine represents a distributed orchestrator
        /// </summary>
        private bool IsDistributedOrchestrator => m_configuration.Distribution.BuildRole.IsOrchestrator();

        /// <summary>
        /// Gets whether inputs are lazily materialized
        /// </summary>
        private bool InputsLazilyMaterialized =>
            m_scheduleConfiguration.EnableLazyOutputMaterialization
            || IsDistributedBuild
            || m_scheduleConfiguration.OutputMaterializationExclusionRoots.Count != 0;

        /// <summary>
        /// Indicates if outputs should be materialized in background rather than inline
        /// </summary>
        private bool MaterializeOutputsInBackground => InputsLazilyMaterialized && IsDistributedBuild;

        /// <summary>
        /// Gets whether the machine represents a distributed orchestrator or worker
        /// </summary>
        private bool IsDistributedBuild => IsDistributedWorker || IsDistributedOrchestrator;

        /// <summary>
        /// PipTwoPhaseCache
        /// </summary>
        private readonly PipTwoPhaseCache m_pipTwoPhaseCache;

        /// <summary>
        /// Checks if incremental scheduling is enabled in the scheduler.
        /// </summary>
        public bool IsIncrementalSchedulingEnabled => IncrementalSchedulingState != null;

        /// <summary>
        /// Logging context
        /// </summary>
        private readonly LoggingContext m_loggingContext;

        /// <summary>
        /// Cache used to hold alien file enumerations per directory
        /// </summary>
        private readonly ConcurrentBigMap<AbsolutePath, IReadOnlyList<DirectoryMemberEntry>> m_alienFileEnumerationCache;

        /// <summary>
        /// Used for making coarse grained decisions about files being created/modified after the build started
        /// when computing directory fingerprints in <see cref="ObservedInputProcessor"/>
        /// </summary>
        private readonly FileTimestampTracker m_fileTimestampTracker;
        private readonly ObservationReclassifier m_globalReclassificationRules;

        /// <summary>
        /// Whether diagnostics events are enabled to be logged.
        /// </summary>
        private readonly bool m_diagnosticsEnabled;
        #endregion

        #region Ready Queue

        /// <summary>
        /// Ready queue of executable pips.
        /// </summary>
        /// <remarks>
        /// This is internal as we need to access it from unit tests.
        /// </remarks>
        internal readonly IPipQueue PipQueue;

        private PipQueue OptionalPipQueueImpl => PipQueue as PipQueue;

        #endregion

        #region Statistics

        private ulong m_totalPeakWorkingSetMb;
        private ulong m_totalAverageWorkingSetMb;

        private ulong m_totalPeakCommitSizeMb;
        private ulong m_totalAverageCommitSizeMb;

        private readonly object m_statusLock = new object();

        /// <summary>
        /// Live counters for the number of pips in each state.
        /// </summary>
        /// <remarks>
        /// For these counters to be accurate, all pip transitions must be via the extension methods on
        /// <see cref="PipRuntimeInfoCounterExtensions" />.
        /// </remarks>
        private readonly PipStateCounters m_pipStateCounters = new PipStateCounters();

        /// <summary>
        /// A pre-allocated container for snapshots of per-state pip counts.
        /// </summary>
        /// <remarks>
        /// Must be updated and read under <see cref="m_statusLock" /> for consistent results.
        /// </remarks>
        private readonly PipStateCountersSnapshot m_pipTypesToLogCountersSnapshot = new PipStateCountersSnapshot();
        private readonly PipStateCountersSnapshot m_processStateCountersSnapshot = new PipStateCountersSnapshot();
        private readonly PipStateCountersSnapshot[] m_pipStateCountersSnapshots = new PipStateCountersSnapshot[(int)PipType.Max];

        /// <summary>
        /// This is the total number of process pips that were run through the scheduler. They may be hit, miss, pass,
        /// fail, skip, etc.
        /// </summary>
        private long m_numProcessPipsCompleted;

        /// <summary>
        /// This is the count of processes that were cache hits and not launched.
        /// </summary>
        private long m_numProcessPipsSatisfiedFromCache;

        /// <summary>
        /// The count of processes that were determined up to date by incremental scheduling and didn't flow
        /// through the scheduler. This count is included in <see cref="m_numProcessPipsSatisfiedFromCache"/>. This count
        /// does not include the "frontier" which do flow through the scheduler to ensure their outputs are cached.
        /// </summary>
        private long m_numProcessesIncrementalSchedulingPruned;

        /// <summary>
        /// This is the count of processes that were cache misses and had the external process launched.
        /// </summary>
        private long m_numProcessPipsUnsatisfiedFromCache;

        /// <summary>
        /// This is the count of processes that were skipped due to failed dependencies.
        /// </summary>
        private long m_numProcessPipsSkipped;

        /// <summary>
        /// This is the total number of IPC pips that were run through the scheduler. They may be pass, fail, skip, etc.
        /// </summary>
        private long m_numIpcPipsCompleted;

        /// <summary>
        /// The total number of service pips scheduled (i.e. not in the Ignored state)
        /// </summary>
        private long m_numServicePipsScheduled;

        /// <summary>
        /// Number of pips which produced tool warnings from cache.
        /// </summary>
        private int m_numPipsWithWarningsFromCache;

        /// <summary>
        /// How many tool warnings were replayed from cache.
        /// </summary>
        private long m_numWarningsFromCache;

        /// <summary>
        /// Number of pips which produced tool warnings (excluding those from cache).
        /// </summary>
        private int m_numPipsWithWarnings;

        /// <summary>
        /// How many tool warnings occurred (excluding those from cache).
        /// </summary>
        private long m_numWarnings;

        /// <summary>
        /// What is the maximum critical path based on historical and suggested data, and what is the good-ness (origin) of critical path info.
        /// </summary>
        private CriticalPathStats m_criticalPathStats;

        /// <summary>
        /// <see cref="PipExecutionState.SidebandState"/>
        /// </summary>
        private SidebandState m_sidebandState;

        /// <summary>
        /// Gets counters for the details of pip execution and cache interaction.
        /// These counters are thread safe, but are only complete once all pips have executed.
        /// </summary>
        public CounterCollection<PipExecutorCounter> PipExecutionCounters { get; } = new CounterCollection<PipExecutorCounter>();

        /// <summary>
        /// Counter collections aggregated by in-filter (explicitly scheduled) or dependencies-of-filter (implicitly scheduled).
        /// </summary>
        /// <remarks>
        /// These counters exclude service start or shutdown process pips.
        /// </remarks>
        public PipCountersByFilter ProcessPipCountersByFilter { get; private set; }

        /// <summary>
        /// Counter collections aggregated by telemetry tag.
        /// </summary>
        /// <remarks>
        /// These counters exclude service start or shutdown process pips.
        /// </remarks>
        public PipCountersByTelemetryTag ProcessPipCountersByTelemetryTag { get; private set; }

        private PipCountersByGroupAggregator m_groupedPipCounters;
        private readonly CounterCollection<PipExecutionStep> m_pipExecutionStepCounters = new CounterCollection<PipExecutionStep>();
        private readonly CounterCollection<FingerprintStoreCounters> m_fingerprintStoreCounters = new CounterCollection<FingerprintStoreCounters>();

        /// <summary>
        /// Counts the number of Pips failing due to network failures 0 times, 1 time, 2 times, etc. upto Configuration.Distribution.NumRetryFailedPipsOnAnotherWorker
        /// </summary>
        private readonly int[] m_pipRetryCountersDueToNetworkFailures;

        private readonly ConcurrentDictionary<int, int> m_pipRetryCountersDueToLowMemory = new ConcurrentDictionary<int, int>();

        private sealed class CriticalPathStats
        {
            /// <summary>
            /// Number of nodes for which critical path duration suggestions were available
            /// </summary>
            public long NumHits;

            /// <summary>
            /// Number of nodes for which a critical path duration suggestions have been guessed by a default heuristic
            /// </summary>
            public long NumWildGuesses;

            /// <summary>
            /// Longest critical path length.
            /// </summary>
            public long LongestPath;
        }

        private readonly PerformanceCollector.Aggregator m_performanceAggregator;

        /// <summary>
        /// Last machine performance info collected
        /// </summary>
        private PerformanceCollector.MachinePerfInfo m_perfInfo;

        /// <summary>
        /// Samples performance characteristics of the execution phase
        /// </summary>
        public ExecutionSampler ExecutionSampler { get; }

        /// <summary>
        /// Whether a low ram memory perf smell was reached
        /// </summary>
        private volatile bool m_hitLowRamMemoryPerfSmell;

        /// <summary>
        /// Whether a low commit memory perf smell was reached
        /// </summary>
        private volatile bool m_hitLowCommitMemoryPerfSmell;

        /// <summary>
        /// Whether a high file descriptor usage perf smell was reached
        /// </summary>
        private volatile bool m_hitHighFileDescriptorUsagePerfSmell;

        private int m_historicPerfDataMisses;
        private int m_historicPerfDataZeroMemoryHits;
        private int m_historicPerfDataNonZeroMemoryHits;

        /// <summary>
        /// Maps modules to the number of process pips and the list of workers assigned.
        /// </summary>
        /// <remarks>
        /// This is populated only with /maxWorkersPerModule is passed with a value greater than 0.
        /// </remarks>
        private readonly Dictionary<ModuleId, (int NumPips, bool[] Workers)> m_moduleWorkerMapping = new Dictionary<ModuleId, (int, bool[])>();

        /// <summary>
        /// The PackedExecution exporter, used for emitting analysis-optimized log data.
        /// </summary>
        private readonly PackedExecutionExporter m_packedExecutionExporter;

        #endregion Statistics

        /// <summary>
        /// Sets the process start time
        /// </summary>
        public void SetProcessStartTime(DateTime processStartTimeUtc)
        {
            m_processStartTimeUtc = processStartTimeUtc;
        }

        #region Constructor

        /// <summary>
        /// Constructs a scheduler for an immutable pip graph.
        /// </summary>
        public Scheduler(
            PipGraph graph,
            IPipQueue pipQueue,
            PipExecutionContext context,
            FileContentTable fileContentTable,
            EngineCache cache,
            IConfiguration configuration,
            FileAccessAllowlist fileAccessAllowlist,
            LoggingContext loggingContext,
            string buildEngineFingerprint,
            DirectoryMembershipFingerprinterRuleSet directoryMembershipFingerprinterRules = null,
            ITempCleaner tempCleaner = null,
            AsyncLazy<HistoricPerfDataTable> runningTimeTable = null,
            PerformanceCollector performanceCollector = null,
            string fingerprintSalt = null,
            PreserveOutputsInfo? previousInputsSalt = null,
            DirectoryTranslator directoryTranslator = null,
            IIpcProvider ipcProvider = null,
            PipTwoPhaseCache pipTwoPhaseCache = null,
            JournalState journalState = null,
            VmInitializer vmInitializer = null,
            SchedulerTestHooks testHooks = null,
            FileTimestampTracker fileTimestampTracker = null,
            bool isTestScheduler = false,
            PipSpecificPropertiesConfig pipSpecificPropertiesConfig = null,
            ObservationReclassifier globalReclassificationRules = null)
        {
            Contract.Requires(graph != null);
            Contract.Requires(pipQueue != null);
            Contract.Requires(cache != null);
            Contract.Requires(fileContentTable != null);
            Contract.Requires(configuration != null);
            Contract.Requires(fileAccessAllowlist != null);
            // Only allow this to be null in testing
            if (tempCleaner == null)
            {
                Contract.Requires(testHooks != null);
            }

            // FIX: Change to assert to work around bug in rewriter
            Contract.Assert(context != null);

            m_buildEngineFingerprint = buildEngineFingerprint;
            fingerprintSalt = fingerprintSalt ?? string.Empty;
            m_configuration = configuration;
            m_scheduleConfiguration = configuration.Schedule;
            PipFingerprintingVersion fingerprintVersion = PipFingerprintingVersion.TwoPhaseV2;
            var extraFingerprintSalts = new ExtraFingerprintSalts(
                    configuration,
                    fingerprintSalt,
                    searchPathToolsHash: directoryMembershipFingerprinterRules?.ComputeSearchPathToolsHash(),
                    observationReclassificationRulesHash: ObservationReclassifier.ComputeObservationReclassificationRulesHash(configuration));

            Logger.Log.PipFingerprintData(loggingContext, fingerprintVersion: (int)fingerprintVersion, fingerprintSalt: extraFingerprintSalts.FingerprintSalt);

            PipGraph = graph;
            m_semanticPathExpander = PipGraph.SemanticPathExpander;
            m_pipTable = PipGraph.PipTable;

            m_performanceAggregator = performanceCollector?.CreateAggregator();
            ExecutionSampler = new ExecutionSampler(IsDistributedBuild, pipQueue.MaxProcesses);

            PipQueue = pipQueue;
            Context = context;
            PipSpecificPropertiesConfig = pipSpecificPropertiesConfig;

            m_pipContentFingerprinter = new PipContentFingerprinter(
                context.PathTable,
                artifact => m_fileContentManager.GetInputContent(artifact).FileContentInfo,
                extraFingerprintSalts,
                m_semanticPathExpander,
                PipGraph.QueryFileArtifactPipData,
                process => m_fileContentManager?.SourceChangeAffectedInputs.GetChangeAffectedInputs(process) ?? CollectionUtilities.EmptyArray<AbsolutePath>(),
                pipId => PipGraph.TryGetPipFingerprint(pipId, out var fingerprint) ? fingerprint.Hash : default,
                process => pipSpecificPropertiesConfig?.GetPipSpecificPropertyValue(PipSpecificPropertiesConfig.PipSpecificProperty.PipFingerprintSalt, process.SemiStableHash));
            m_historicPerfDataTableTask = runningTimeTable;

            // Prepare Root Map redirection table. see m_rootMappings comment on why this is happening here.
            var rootMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (m_configuration.Engine.RootMap != null)
            {
                foreach (var rootMapping in m_configuration.Engine.RootMap)
                {
                    rootMappings.Add(rootMapping.Key, rootMapping.Value.ToString(context.PathTable));
                }
            }

            m_rootMappings = rootMappings;

            // Prepare artificial cache miss.
            var artificalCacheMissConfig = configuration.Cache.ArtificialCacheMissConfig ?? new ArtificialCacheMissConfig();
            // Retrieve the list of pipIds which have the forcedCacheMiss property set on them.
            var forcedCacheMissSemistableHashes = PipSpecificPropertiesConfig?.GetPipIdsForProperty(PipSpecificPropertiesConfig.PipSpecificProperty.ForcedCacheMiss) ?? Enumerable.Empty<long>();

            m_artificialCacheMissOptions = new ArtificialCacheMissOptions(
                artificalCacheMissConfig.Rate / (double)ushort.MaxValue,
                artificalCacheMissConfig.IsInverted,
                artificalCacheMissConfig.Seed,
                forcedCacheMissSemistableHashes);

            m_fileContentTable = fileContentTable;
            m_journalState = journalState ?? JournalState.DisabledJournal;
            DirectoryTranslator = directoryTranslator;
            TranslatedGlobalUnsafeUntrackedScopes = m_configuration.Sandbox.GlobalUnsafeUntrackedScopes.Select(p => DirectoryTranslator.Translate(p, context.PathTable)).ToReadOnlySet();

            m_directoryMembershipFingerprinterRules = directoryMembershipFingerprinterRules;
            m_previousInputsSalt = previousInputsSalt ?? UnsafeOptions.PreserveOutputsNotUsed;
            m_fileAccessAllowlist = fileAccessAllowlist;

            // Done setting up tracking of local disk state.

            // Caching artifact content and fingerprints:
            // - We always have a cache of artifact content (we want one path to materialize content at any location, by hash)
            // - We always have a store for 'fingerprint' -> prior run information (PipCacheDescriptor)
            Cache = cache;

            // Prime the dummy provenance since its creation requires adding a string to the TokenText table, which gets frozen after scheduling
            // is complete. GetDummyProvenance may be called during execution (after the schedule phase)
            GetDummyProvenance();

            TempCleaner = tempCleaner;

            // Ensure that when the cancellationToken is signaled, we respond with the
            // internal cancellation process.
            m_cancellationTokenRegistration = context.CancellationToken.Register(() => RequestTermination(cancelRunningPips: true));

            m_schedulerCancellationTokenSource = new CancellationTokenSource();

            m_testHooks = testHooks;
            m_ipcProvider = new IpcProviderWithMemoization(
                ipcProvider ?? IpcFactory.GetProvider(),
                defaultClientLogger: CreateLoggerForIpcClients(loggingContext));
            m_servicePipIds = new List<PipId>(graph.GetServicePipIds());
            m_servicePipTracker = new ServicePipTracker(context);
            m_serviceManager = new SchedulerServiceManager(graph, context, m_servicePipTracker, m_testHooks, m_ipcProvider);
            m_pipFragmentRenderer = this.CreatePipFragmentRenderer();

            OperationTracker = new OperationTracker(loggingContext, this);
            m_fileContentManager = new FileContentManager(this, OperationTracker);
            m_apiServer = null;
            m_pluginManager = null;

            m_diskSpaceMonitoredDrives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var reverseDirectoryTranslator = directoryTranslator?.GetReverseTranslator();

            var pathsToMonitor = m_semanticPathExpander.GetWritableRoots().Concat(m_semanticPathExpander.GetSystemRoots());
            foreach (AbsolutePath path in pathsToMonitor)
            {
                // GetSystemRoots adds Windows OS installation drives (Generally C Drive)
                var driveName = !OperatingSystemHelper.IsUnixOS
                    ? GetRootDriveForPath(path, reverseDirectoryTranslator, context, loggingContext)
                    : IO.GetMountNameForPath(path.ToString(Context.PathTable));
                if (driveName != null)
                {
                    m_diskSpaceMonitoredDrives.Add(driveName);
                }
            }

            var sealContentsById = new ConcurrentBigMap<DirectoryArtifact, int[]>();

            // Cache delegate for ExecutePip to avoid creating delegate everytime you pass ExecutePip to PipQueue.
            m_executePipFunc = ExecutePip;

            for (int i = 0; i < m_pipStateCountersSnapshots.Length; i++)
            {
                m_pipStateCountersSnapshots[i] = new PipStateCountersSnapshot();
            }

            LocalWorker = m_scheduleConfiguration.EnableProcessRemoting
                ? new LocalWorkerWithRemoting(m_scheduleConfiguration, m_configuration.Sandbox, pipQueue, m_testHooks?.DetoursListener, context)
                : new LocalWorker(m_scheduleConfiguration, pipQueue, m_testHooks?.DetoursListener, context);
            m_workers = new List<Worker> { LocalWorker };

            m_statusSnapshotLastUpdated = DateTime.UtcNow;

            m_loggingIntervalPeriodMs = configuration.Logging.StatusFrequencyMs != 0 ?
                configuration.Logging.StatusFrequencyMs :
                configuration.Logging.GetTimerUpdatePeriodInMs();
            m_previousStatusLogTimeUtc = DateTime.UtcNow.AddMilliseconds(-1 * m_loggingIntervalPeriodMs); // Reducing by loggingIntervalPeriodMs to enable logging in the first call to UpdateStatus
            m_pipTwoPhaseCache = pipTwoPhaseCache ?? new PipTwoPhaseCache(loggingContext, cache, context, m_semanticPathExpander);
            m_pipTwoPhaseCache.SchedulerCancellationToken = m_schedulerCancellationTokenSource.Token;
            m_runnablePipPerformance = new ConcurrentDictionary<PipId, RunnablePipPerformanceInfo>();

            m_fileChangeTrackerFile = m_configuration.Layout.SchedulerFileChangeTrackerFile;
            m_incrementalSchedulingStateFile = m_configuration.Layout.IncrementalSchedulingStateFile;

            var numChangedGvfsProjections = m_journalState.VolumeMap?.ChangedGvfsProjections.Count ?? 0;

            m_shouldCreateIncrementalSchedulingState =
                m_journalState.IsEnabled &&
                m_configuration.Schedule.IncrementalScheduling &&
                m_configuration.Distribution.BuildRole == DistributedBuildRoles.None &&
                m_configuration.Schedule.ForceSkipDependencies == ForceSkipDependenciesMode.Disabled &&
                numChangedGvfsProjections == 0;

            if (numChangedGvfsProjections > 0)
            {
                Logger.Log.IncrementalSchedulingDisabledDueToGvfsProjectionChanges(
                    m_loggingContext,
                    string.Join(", ", m_journalState.VolumeMap.ChangedGvfsProjections));
            }

            // Execution log targets
            m_executionLogFileTarget = CreateExecutionLog(
                    configuration,
                    context,
                    graph,
                    extraFingerprintSalts,
                    loggingContext);

            Contract.Assert(configuration.Logging.StoreFingerprints.HasValue, "Configuration.Logging.StoreFingerprints should be assigned some value before constructing the scheduler.");

            OperationContext operationContext = new OperationContext(loggingContext, operation: null);
            m_fingerprintStoreTarget = CreateFingerprintStoreTarget(
                    loggingContext,
                    configuration,
                    context,
                    graph.PipTable,
                    new PipContentFingerprinter(
                        context.PathTable,
                        artifact =>
                        {
                            if (m_fileContentManager.TryGetInputContent(artifact, out var info))
                            {
                                return info.FileContentInfo;
                            }

                            // When calculating fingerprints for cache miss analysis, the orchestrator needs to have source file hashes for the pips executed so far.
                            // For the distributed builds, the source files are hashed on the machine where the pip is being executed. Then, those hashes are sent to
                            // the orchestrator via an xlg event (FileArtifactContentDecided). Sometimes, we might not have processed those events ontime, so it can cause
                            // missing hash errors. To prevent this from happening, we hash those missing files here. The number of those files is expected to be very low.
                            PipExecutionCounters.IncrementCounter(PipExecutorCounter.NumMissingInputContent);
                            using (PipExecutionCounters.StartStopwatch(PipExecutorCounter.MissingInputContentHashDuration))
                            {
                                var result = m_fileContentManager.TryHashSourceFile(operationContext, artifact).GetAwaiter().GetResult();
                                return result.HasValue ? result.Value.FileContentInfo : default(FileContentInfo);
                            }
                        },
                        extraFingerprintSalts,
                        m_semanticPathExpander,
                        PipGraph.QueryFileArtifactPipData,
                        process => m_fileContentManager?.SourceChangeAffectedInputs.GetChangeAffectedInputs(process) ?? CollectionUtilities.EmptyArray<AbsolutePath>(),
                        pipId => PipGraph.TryGetPipFingerprint(pipId, out var fingerprint) ? fingerprint.Hash : default,
                        process => pipSpecificPropertiesConfig?.GetPipSpecificPropertyValue(PipSpecificPropertiesConfig.PipSpecificProperty.PipFingerprintSalt, process.SemiStableHash)),
                    cache,
                    DirectedGraph,
                    m_fingerprintStoreCounters,
                    m_runnablePipPerformance,
                    m_fileContentManager,
                    m_testHooks?.FingerprintStoreTestHooks);

            // create the directory where shared opaque outputs journals will be stored
            FileUtilities.CreateDirectoryWithRetry(configuration.Layout.SharedOpaqueSidebandDirectory.ToString(Context.PathTable));

            WeakFingerprintAugmentationExecutionLogTarget fingerprintAugmentationTarget = null;

            var executionLogPath = configuration.Logging.ExecutionLog;
            if (configuration.Logging.LogPackedExecution && executionLogPath.IsValid)
            {
                var packedExecutionPath = Path.ChangeExtension(executionLogPath.ToString(Context.PathTable), "PXL"); // Packed eXecution Log
                m_packedExecutionExporter = new PackedExecutionExporter(PipGraph, packedExecutionPath);
            }

            m_dumpPipLiteExecutionLogTarget = null;

            if (!IsDistributedWorker)
            {
                m_orchestratorTarget = new OchestratorSpecificExecutionLogTarget(loggingContext, this, m_pipTwoPhaseCache);

                // Fingerprint augmentation monitoring must be running only on the orchestrator (it's the only worker that will observe
                // both ProcessFingerprintComputed events for the same pip).
                if (configuration.Cache.MonitorAugmentedPathSets > 0)
                {
                    fingerprintAugmentationTarget = new WeakFingerprintAugmentationExecutionLogTarget(loggingContext, this, configuration.Cache.MonitorAugmentedPathSets);
                }

                m_buildManifestGenerator = new BuildManifestGenerator(loggingContext, Context.StringTable);
                m_manifestExecutionLog = new BuildManifestStoreTarget(m_buildManifestGenerator, m_pipTwoPhaseCache);

                // Only log failed pips on orchestrator to make it easier to retrieve logs for failing pips on workers
                if (configuration.Logging.DumpFailedPips.GetValueOrDefault())
                {
                    m_dumpPipLiteExecutionLogTarget = new DumpPipLiteExecutionLogTarget(context, graph.PipTable, loggingContext, configuration, graph);
                }
            }

            m_eventStatsExecutionLogTarget = new EventStatsExecutionLogTarget();

            m_multiExecutionLogTarget = MultiExecutionLogTarget.CombineTargets(
                m_executionLogFileTarget,
                m_fingerprintStoreTarget,
                new ObservedInputAnomalyAnalyzer(loggingContext, graph),
                m_orchestratorTarget,
                m_manifestExecutionLog,
                fingerprintAugmentationTarget,
                m_dumpPipLiteExecutionLogTarget,
                m_packedExecutionExporter,
                m_eventStatsExecutionLogTarget);

            // Things that use execution log targets
            m_directoryMembershipFingerprinter = new DirectoryMembershipFingerprinter(
                loggingContext,
                context,
                ExecutionLog);

            m_fileMonitoringViolationAnalyzer = new FileMonitoringViolationAnalyzer(
                    loggingContext,
                    context,
                    graph,
                    m_fileContentManager,
                    configuration.Distribution.ValidateDistribution,
                    configuration.Sandbox.UnsafeSandboxConfiguration.UnexpectedFileAccessesAreErrors,
                    configuration.Sandbox.UnsafeSandboxConfiguration.IgnoreDynamicWritesOnAbsentProbes,
                    ExecutionLog);

            m_outputFileExtensionsForSequentialScan = new HashSet<PathAtom>(configuration.Schedule.OutputFileExtensionsForSequentialScanHandleOnHashing);

            m_loggingContext = loggingContext;
            m_groupedPipCounters = new PipCountersByGroupAggregator(loggingContext);
            m_pipRetryCountersDueToNetworkFailures = new int[configuration.Distribution.MaxRetryLimitOnRemoteWorkers + 1];

            VmInitializer = vmInitializer;
            RemoteProcessManager = RemoteProcessManagerFactory.Create(loggingContext, Context, configuration, new RemoteFilePredictor(this, this, loggingContext), Counters);
            m_perPipPerformanceInfoStore = new PerProcessPipPerformanceInformationStore(configuration.Logging.MaxNumPipTelemetryBatches, configuration.Logging.AriaIndividualMessageSizeLimitBytes);

            ReparsePointAccessResolver = new ReparsePointResolver(context, directoryTranslator);

            m_alienFileEnumerationCache = new ConcurrentBigMap<AbsolutePath, IReadOnlyList<DirectoryMemberEntry>>();

            m_diagnosticsEnabled = ETWLogger.Log.IsEnabled(EventLevel.Verbose, Keywords.Diagnostics);

            m_chooseWorkerCpu = new ChooseWorkerCpu(
                loggingContext,
                m_configuration.Schedule,
                m_workers,
                PipQueue,
                PipGraph,
                m_fileContentManager,
                m_moduleWorkerMapping);
            m_chooseWorkerCacheLookup = new ChooseWorkerCacheLookup(m_workers);
            m_chooseWorkerIpc = new ChooseWorkerIpc(m_workers, m_moduleWorkerMapping);
            m_fileTimestampTracker = fileTimestampTracker ?? new FileTimestampTracker(DateTime.UtcNow, context.PathTable);
            m_globalReclassificationRules = globalReclassificationRules ?? new ObservationReclassifier();
            try
            {
                m_globalReclassificationRules.Initialize(configuration.GlobalReclassificationRules.Select(rc => rc.GetRule()).ToList(), PipExecutionCounters);
            }
            catch (BuildXLException ex)
            {
                Logger.Log.FailedToInitalizeReclassificationRules(loggingContext, ex.Message);
                throw;
            }

            if (!m_fileTimestampTracker.IsFileCreationTrackingSupported)
            {
                Logger.Log.CreationTimeNotSupported(m_loggingContext);
            }

            m_isTestScheduler = isTestScheduler;
        }

        /// <summary>
        /// Returns the pre subst root drive for given path.
        /// </summary>
        private static string GetRootDriveForPath(AbsolutePath path, DirectoryTranslator reverseDirectoryTranslator, PipExecutionContext context, LoggingContext loggingContext)
        {
            string drive;

            if (FileUtilities.TryGetSubstSourceAndTarget(path.GetRoot(context.PathTable).ToString(context.PathTable), out string substSource, out string substTarget, out string errorMessage) && errorMessage == null)
            {
                drive = substSource;
            }
            else if (errorMessage != null)
            {
                // TryGetSubstSourceAndTarget may return false and set a warning message to be logged if something went wrong
                Logger.Log.UnableToMonitorDriveWithSubst(loggingContext, path.ToString(context.PathTable), errorMessage);
                drive = string.Empty;
            }
            else
            {
                AbsolutePath translatedPath = reverseDirectoryTranslator != null
                    ? reverseDirectoryTranslator.Translate(path, context.PathTable)
                    : path;
                drive = translatedPath.GetRoot(context.PathTable).ToString(context.PathTable);
            }

            if (drive.Length == 3 && drive.EndsWith(@":\", StringComparison.OrdinalIgnoreCase))      // Example "D:\"
            {
                return drive.Substring(0, 1);
            }

            return null;
        }

        private static IIpcLogger CreateLoggerForIpcClients(LoggingContext loggingContext)
        {
            return new LambdaLogger((level, message, args) =>
                Logger.Log.IpcClientForwardedMessage(
                    loggingContext,
                    level.ToString(),
                    args.Length > 0 ? string.Format(CultureInfo.InvariantCulture, message, args) : message));
        }

        private static IIpcLogger CreateLoggerForApiServer(LoggingContext loggingContext)
        {
            return new LambdaLogger((level, message, args) =>
                Logger.Log.ApiServerForwardedIpcServerMessage(
                    loggingContext,
                    level.ToString(),
                    args.Length > 0 ? string.Format(CultureInfo.InvariantCulture, message, args) : message));
        }

        #endregion Constructor

        #region Execution

        /// <summary>
        /// Returns a Boolean indicating if the scheduler has received a request for cancellation.
        /// </summary>
        public bool IsTerminating => m_scheduleTerminating;

        /// <summary>
        /// Start running.
        /// </summary>
        public void Start(LoggingContext loggingContext)
        {
            Contract.Requires(loggingContext != null);

            m_executePhaseLoggingContext = loggingContext;
            m_serviceManager.Start(loggingContext, OperationTracker);

            if (PipGraph.ApiServerMoniker.IsValid)
            {
                // Add try catch block to catch any exception thrown by new ApiServer()
                // Possible exception: SocketException. When machine ran out of resouces (ports or some other resouces), 
                // SocketException will be thrown by socket.bind() in new ApiServer().
                // Possible exception: IpcException. When all available ports are returned before, we consider there is no free port and return -1 as port number.
                // new ApiServer() will throw IpcException when parsing -1 as port number.
                try
                {
                    // To reduce the time between rendering the server moniker and starting a server using that moniker,
                    // we create the server here and not in the Scheduler's ctor.
                    m_apiServer = new ApiServer(
                        m_ipcProvider,
                        PipGraph.ApiServerMoniker.ToString(Context.StringTable),
                        m_fileContentManager,
                        Context,
                        new ServerConfig
                        {
                            MaxConcurrentClients = 1_000, // not currently based on any science or experimentation
                            MaxConcurrentRequestsPerClient = 10,
                            StopOnFirstFailure = false,
                            Logger = CreateLoggerForApiServer(loggingContext),
                        },
                        m_pipTwoPhaseCache,
                        m_manifestExecutionLog,
                        m_buildManifestGenerator,
                        m_serviceManager,
                        m_configuration.Engine.VerifyFileContentOnBuildManifestHashComputation);
                    m_apiServer.Start(loggingContext);
                }
                catch (SocketException socketException)
                {
                    // Log socket exception and request for termination
                    Logger.Log.ApiServerFailedToStartDueToSocketError(loggingContext, socketException.GetLogEventMessage());
                    RequestTermination();
                }
                catch (IpcException ipcException)
                {
                    // Log ipc exception and request for termination
                    Logger.Log.ApiServerFailedToStartDueToIpcError(loggingContext, ipcException.GetLogEventMessage());
                    RequestTermination();
                }
            }

            if (m_configuration.Schedule.EnablePlugin == true)
            {
                m_pluginManager = new PluginManager(
                    loggingContext,
                    m_configuration.Logging.LogsDirectory.ToString(Context.PathTable),
                    m_configuration.Schedule.PluginLocations.Select(path => path.ToString(Context.PathTable)));
                m_pluginManager.Start();
            }

            ExecutionLog?.BxlInvocation(new BxlInvocationEventData(m_configuration));

            m_drainThread = new Thread(PipQueue.DrainQueues);

            if (!m_scheduleTerminating)
            {
                // UpdateStatus() checks if all writable drives have specified disk space available and calls RequestTermination for low disk space
                // Start the draining thread if scheduler isn't in terminating state
                m_drainThread.Start();
            }
        }

        /// <summary>
        /// Marks that a pip was executed. This logs a stat the first time it is called
        /// </summary>
        private void MarkPipStartExecuting()
        {
            if (Interlocked.CompareExchange(ref m_firstExecutedPip, 1, 0) == 0)
            {
                // Time to first pip only has meaning if we know when the process started
                if (m_processStartTimeUtc.HasValue)
                {
                    LogStatistic(
                        m_executePhaseLoggingContext,
                        Statistics.TimeToFirstPipExecuted,
                        (int)(DateTime.UtcNow - m_processStartTimeUtc.Value).TotalMilliseconds);
                }
            }
        }

        /// <summary>
        /// Returns a task representing the completion of all the scheduled pips
        /// </summary>
        /// <returns>Result of task is true if pips completed successfully. Otherwise, false.</returns>
        public async Task<bool> WhenDone()
        {
            Contract.Assert(m_drainThread != null, "Scheduler has not been started");

            if (IsDistributedOrchestrator)
            {
                await EnsureMinimumWorkersAsync(m_configuration.Distribution.MinimumWorkers, m_configuration.Distribution.LowWorkersWarningThreshold.Value);
            }

            if (m_drainThread.IsAlive)
            {
                m_drainThread.Join();
            }

            Contract.Assert(!HasFailed || m_executePhaseLoggingContext.ErrorWasLogged, "Scheduler encountered errors during execution, but none were logged.");

            if (!IsDistributedWorker && !HasFailed && !m_isTestScheduler)
            {
                RetrievePipStateCounts(out _, out _, out long waitingPips, out long runningPips, out _, out long failedPips, out long skippedPips, out _);
                Contract.Assert(runningPips == 0, $"There are still pips at running state at the end of the build. WaitingPips: {waitingPips}, RunningPips: {runningPips}, FailedPips: {failedPips}, SkippedPips: {skippedPips}.");
            }

            // We want TimeToFirstPipExecuted to always have a value. Mark the end of the execute phase as when the first
            // pip was executed in case all pips were cache hits
            MarkPipStartExecuting();

            m_schedulerDoneTimeUtc = DateTime.UtcNow;
            m_schedulerCompletion.TrySetResult(true);
            Logger.Log.SchedulerComplete(m_loggingContext);

            using (PipExecutionCounters.StartStopwatch(PipExecutorCounter.AfterDrainingWhenDoneDuration))
            {
                LogWorkerStats();
                string[] perProcessPipPerf = m_perPipPerformanceInfoStore.GenerateTopPipPerformanceInfoJsonArray();
                foreach (string processPipPerf in perProcessPipPerf)
                {
                    Logger.Log.TopPipsPerformanceInfo(m_loggingContext, processPipPerf);
                }

                var shutdownServicesSucceeded = await m_serviceManager.ShutdownStartedServices(Context.CancellationToken.IsCancellationRequested || m_schedulerCancellationTokenSource.Token.IsCancellationRequested);
                Contract.Assert(
                    shutdownServicesSucceeded || m_executePhaseLoggingContext.ErrorWasLogged,
                    "ServiceManager encountered errors during shutdown, but none were logged.");

                if (m_apiServer != null)
                {
                    await m_apiServer.Stop();
                }

                if (m_pluginManager != null)
                {
                    await m_pluginManager.Stop();
                }

                await StopIpcProvider();

                using (PipExecutionCounters.StartStopwatch(PipExecutorCounter.WhenDoneWorkerFinishDuration))
                {
                    await m_workers.ParallelForEachAsync((worker) => worker.FinishAsync());

                    // Wait for all workers to confirm that they have stopped.
                    while (m_workers.Any(w => w.Status != WorkerNodeStatus.Stopped))
                    {
                        await Task.Delay(50);
                    }
                }

                using (PipExecutionCounters.StartStopwatch(PipExecutorCounter.CompleteAndWaitPathSetReportDuration))
                {
                    if (m_orchestratorTarget != null)
                    {
                        await m_orchestratorTarget.CompleteAndWaitPathSetReport();
                    }
                }

                // We intentionally close the cache (including HistoricMetadataCache - HMC) after we finish the workers. 
                // We might report PathSets coming from the workers to HMC, so it is important for HMC to be active.
                await State.Cache.CloseAsync();

                if (m_fingerprintStoreTarget != null)
                {
                    // Dispose the fingerprint store to allow copying the files
                    m_fingerprintStoreTarget.Dispose();

                    // After the FingerprintStoreExecutionLogTarget is disposed and store files are no longer locked,
                    // create fingerprint store copy in logs.
                    if (m_configuration.Logging.SaveFingerprintStoreToLogs.GetValueOrDefault())
                    {
                        await FingerprintStore.CopyAsync(
                            m_loggingContext,
                            m_testHooks?.FingerprintStoreTestHooks,
                            Context.PathTable,
                            m_configuration,
                            m_fingerprintStoreCounters);
                    }

                    m_fingerprintStoreCounters.LogAsStatistics("FingerprintStore", m_loggingContext);
                    if (m_testHooks?.FingerprintStoreTestHooks != null)
                    {
                        m_testHooks.FingerprintStoreTestHooks.Counters = m_fingerprintStoreCounters;
                    }
                }

                if (m_configuration.Schedule.ModuleAffinityEnabled())
                {
                    StringBuilder strBuilder = new StringBuilder();
                    foreach (var kvp in m_moduleWorkerMapping.OrderByDescending(a => a.Value.NumPips))
                    {
                        strBuilder.Append($"{kvp.Key.Value.ToString(Context.StringTable)}: {kvp.Value.NumPips} pips executed on [");
                        for (int i = 0; i < m_workers.Count; i++)
                        {
                            if (kvp.Value.Workers[i])
                            {
                                strBuilder.Append(i);
                                strBuilder.Append(",");
                            }
                        }

                        strBuilder.AppendLine("]");
                    }

                    Logger.Log.ModuleWorkerMapping(m_loggingContext, strBuilder.ToString());
                }

                var failedPaths = m_fileContentManager.GetPathsRegisteredAfterMaterializationCall();
                if (failedPaths.Count > 0)
                {
                    Logger.Log.FileContentManagerTryMaterializeFileAsyncFileArtifactAvailableLater(
                        m_loggingContext,
                        failedPaths.Count,
                        $"{Environment.NewLine}{string.Join(Environment.NewLine, failedPaths.Select(p => p.ToString(Context.PathTable)))}");
                }

                // Complete writing out PackedExecution log (on orchestrator only, since exporter is only created on orchestrator)
                m_packedExecutionExporter?.Analyze();

                return !HasFailed && shutdownServicesSucceeded;
            }
        }

        private void LogWorkerStats()
        {
            PipExecutionCounters.AddToCounter(PipExecutorCounter.AvailableWorkerCountAtEnd, AvailableWorkersCount);

            int everAvailableWorkerCount = Workers.Count(a => a.EverAvailable);
            PipExecutionCounters.AddToCounter(PipExecutorCounter.EverAvailableWorkerCount, everAvailableWorkerCount);
            PipExecutionCounters.AddToCounter(PipExecutorCounter.EverConnectedWorkerCount, Workers.Count(a => a.EverConnected));

            var workerOpKinds = Worker.WorkerStatusOperationKinds;

            var runningOpKing = workerOpKinds[(int)WorkerNodeStatus.Running];
            long totalWorkerRunningDuration = SafeConvert.ToLong(OperationTracker.TryGetAggregateCounter(runningOpKing)?.Duration.TotalMilliseconds ?? 0);

            PipExecutionCounters.AddToCounter(PipExecutorCounter.WorkerAverageRunningDurationMs, totalWorkerRunningDuration / everAvailableWorkerCount);

            var pendingOpKinds = new OperationKind[] { workerOpKinds[(int)WorkerNodeStatus.Starting], workerOpKinds[(int)WorkerNodeStatus.Started] };
            long totalWorkerPendingDuration = 0;
            foreach (var opKind in pendingOpKinds)
            {
                totalWorkerPendingDuration += SafeConvert.ToLong(OperationTracker.TryGetAggregateCounter(opKind)?.Duration.TotalMilliseconds ?? 0);
            }

            PipExecutionCounters.AddToCounter(PipExecutorCounter.WorkerAveragePendingDurationMs, totalWorkerPendingDuration / everAvailableWorkerCount);
        }

        private async Task EnsureMinimumWorkersAsync(int minimumWorkers, int warningThreshold)
        {
            var allAttachmentTasks = TaskUtilities.SafeWhenAll(m_workersAttachmentTasks);

            while (true)
            {
                // If the build is done do not continue with the validation, unless explicitly configured
                if ((!m_drainThread.IsAlive || IsTerminating) && !EngineEnvironmentSettings.AlwaysEnsureMinimumWorkers)
                {
                    break;
                }

                // Wait for all attachment requests to complete
                bool allWorkerAttachmentCompleted = allAttachmentTasks.IsCompleted;

                var succesfullyAttachedSoFar = m_workersAttachmentTasks
                    .Where(t => t.IsCompleted)
                    .Select(t => t.GetAwaiter().GetResult())
                    .Count(b => b);

                // Count all workers that were running at some point (including the local worker)
                int everAvailableWorkers = 1 + succesfullyAttachedSoFar;

                if (everAvailableWorkers >= Math.Max(minimumWorkers, warningThreshold))
                {
                    // The strongest condition is satisfied. No need to keep checking.
                    break;
                }

                if (allWorkerAttachmentCompleted)
                {
                    // All workers completed their attachment processes (either sucessfully or otherwise)
                    if (everAvailableWorkers < minimumWorkers)
                    {
                        Logger.Log.MinimumWorkersNotSatisfied(m_executePhaseLoggingContext, minimumWorkers, everAvailableWorkers);
                        m_hasFailures = true;
                        RequestTermination(cancelQueue: false);
                    }
                    else if (everAvailableWorkers < warningThreshold && !m_workers.Any(w => w.IsEarlyReleaseInitiated))
                    {
                        // Warn of a low-worker count only if we didn't decide to early release,
                        // in which case the warning is not warranted
                        Logger.Log.WorkerCountBelowWarningThreshold(m_executePhaseLoggingContext, warningThreshold, everAvailableWorkers);
                    }

                    // Validation is done
                    break;
                }

                // Wait for a few seconds before checking again, but short-circuit if all attachments are completed
                _ = await Task.WhenAny(allAttachmentTasks, Task.Delay(15_000));
            }
        }

        private async Task<bool> StopIpcProvider()
        {
            try
            {
                await m_ipcProvider.Stop();
                return true;
            }
            catch (Exception e)
            {
                Logger.Log.IpcClientFailed(m_executePhaseLoggingContext, e.ToStringDemystified());
                return false;
            }
        }

        /// <summary>
        /// Reports schedule stats that are relevant at the completion of a build.
        /// </summary>
        /// <remarks>
        /// This is called after all pips have been added and the pip queue has emptied.
        /// Warning: Some variables may be null if scheduler's Init() is not called.
        /// </remarks>
        public SchedulerPerformanceInfo LogStats(LoggingContext loggingContext, [AllowNull] BuildSummary buildSummary)
        {
            Dictionary<string, long> statistics = new Dictionary<string, long>();
            LocalWorkerWithRemoting localWorkerWithRemoting = LocalWorker as LocalWorkerWithRemoting;

            lock (m_statusLock)
            {
                m_fileContentManager.LogStats(loggingContext);

                OperationTracker.Stop(Context, m_configuration.Logging, PipExecutionCounters, Worker.WorkerStatusOperationKinds);

                LogCriticalPath(statistics, buildSummary);

                int processPipsStartOrShutdownService = m_serviceManager.TotalServicePipsCompleted + m_serviceManager.TotalServiceShutdownPipsCompleted;

                PipExecutionCounters.AddToCounter(PipExecutorCounter.TotalRunRemoteProcesses, localWorkerWithRemoting != null ? localWorkerWithRemoting.TotalRunRemote : 0);
                PipExecutionCounters.AddToCounter(PipExecutorCounter.TotalRunLocallyProcessesOnRemotingWorker, localWorkerWithRemoting != null ? localWorkerWithRemoting.TotalRunLocally : 0);
                PipExecutionCounters.AddToCounter(PipExecutorCounter.TotalRemoteFallbackRetries, localWorkerWithRemoting != null ? localWorkerWithRemoting.TotalRemoteFallbackRetryLocally : 0);

                // Overall caching summary
                if (m_numProcessPipsCompleted > 0)
                {
                    // Grab a snapshot just looking at processes so we can log the count that were ignored
                    PipStateCountersSnapshot snapshot = new PipStateCountersSnapshot();
                    m_pipStateCounters.CollectSnapshot(new[] { PipType.Process }, snapshot);
                    long totalProcessesNotIgnoredOrService = snapshot.Total - (snapshot.IgnoredCount + processPipsStartOrShutdownService);
                    double cacheRate = (double)m_numProcessPipsSatisfiedFromCache / totalProcessesNotIgnoredOrService;

                    Logger.Log.IncrementalBuildSavingsSummary(
                        loggingContext,
                        // Make sure not to show 100% due to rounding when there are any misses
                        cacheRate: cacheRate == 1 ? cacheRate : Math.Min(cacheRate, .9999),
                        totalProcesses: totalProcessesNotIgnoredOrService,
                        ignoredProcesses: snapshot.IgnoredCount);

                    long processPipsSatisfiedFromRemoteCache =
                        PipExecutionCounters.GetCounterValue(PipExecutorCounter.RemoteCacheHitsForProcessPipDescriptorAndContent);
                    long remoteContentDownloadedBytes =
                        PipExecutionCounters.GetCounterValue(PipExecutorCounter.RemoteContentDownloadedBytes);
                    if (processPipsSatisfiedFromRemoteCache > 0)
                    {
                        if (processPipsSatisfiedFromRemoteCache <= m_numProcessPipsSatisfiedFromCache)
                        {
                            double relativeCacheRate = (double)processPipsSatisfiedFromRemoteCache / m_numProcessPipsSatisfiedFromCache;
                            string remoteContentDownloadedBytesHumanReadable = ByteSizeFormatter.Format(remoteContentDownloadedBytes);

                            Logger.Log.IncrementalBuildSharedCacheSavingsSummary(
                                loggingContext,
                                relativeCacheRate: relativeCacheRate,
                                remoteProcesses: processPipsSatisfiedFromRemoteCache,
                                contentDownloaded: remoteContentDownloadedBytesHumanReadable);
                        }
                        else
                        {
                            Logger.Log.RemoteCacheHitsGreaterThanTotalCacheHits(
                                loggingContext,
                                processPipsSatisfiedFromRemoteCache,
                                m_numProcessPipsSatisfiedFromCache);
                        }
                    }

                    var runRemoteProcesses = PipExecutionCounters.GetCounterValue(PipExecutorCounter.TotalRunRemoteProcesses);
                    var runRemoteFallback = PipExecutionCounters.GetCounterValue(PipExecutorCounter.TotalRemoteFallbackRetries);
                    long pipsActuallyRemoted = runRemoteProcesses - runRemoteFallback;
                    if (pipsActuallyRemoted > 0)
                    {
                        Logger.Log.RemoteBuildSavingsSummary(loggingContext, pipsActuallyRemoted, cacheMisses: totalProcessesNotIgnoredOrService - m_numProcessPipsSatisfiedFromCache);
                    }

                    if (m_configuration.Engine.Converge && cacheRate < 1 && m_configuration.Logging.ExecutionLog.IsValid)
                    {
                        Logger.Log.SchedulerDidNotConverge(
                            loggingContext,
                            m_configuration.Logging.ExecutionLog.ToString(Context.PathTable),
                            Path.Combine(Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly())), Branding.AnalyzerExecutableName),
                            m_configuration.Logging.LogsDirectory.ToString(Context.PathTable) + Path.DirectorySeparatorChar + "CacheMiss");
                    }
                }

                m_pipStateCounters.CollectSnapshot(s_pipTypesToLogStats, m_pipTypesToLogCountersSnapshot);
                statistics.Add(Statistics.TotalPips, m_pipTypesToLogCountersSnapshot.Total);

                long succeededCount = m_pipTypesToLogCountersSnapshot.DoneCount;
                Logger.Log.PipsSucceededStats(loggingContext, succeededCount);
                statistics.Add(Statistics.PipsSucceeded, succeededCount);
                Logger.Log.PipsFailedStats(loggingContext, m_pipTypesToLogCountersSnapshot[PipState.Failed]);
                statistics.Add(Statistics.PipsFailed, m_pipTypesToLogCountersSnapshot[PipState.Failed]);
                statistics.Add(Statistics.PipsIgnored, m_pipTypesToLogCountersSnapshot.IgnoredCount);

                var statsName = "PipStats.{0}_{1}";

                // Log the stats for each pipType.
                foreach (var pipType in Enum.GetValues(typeof(PipType)).Cast<PipType>())
                {
                    if (pipType == PipType.Max)
                    {
                        continue;
                    }

                    var detailedSnapShot = new PipStateCountersSnapshot();
                    m_pipStateCounters.CollectSnapshot(new[] { pipType }, detailedSnapShot);
                    Logger.Log.PipDetailedStats(
                        loggingContext,
                        pipType.ToString(),
                        detailedSnapShot.DoneCount,
                        detailedSnapShot[PipState.Failed],
                        detailedSnapShot.SkippedDueToFailedDependenciesCount,
                        detailedSnapShot.IgnoredCount,
                        detailedSnapShot.Total);

                    statistics.Add(string.Format(statsName, pipType, "Done"), detailedSnapShot.DoneCount);
                    statistics.Add(string.Format(statsName, pipType, "Failed"), detailedSnapShot[PipState.Failed]);
                    statistics.Add(string.Format(statsName, pipType, "Skipped"), detailedSnapShot.SkippedDueToFailedDependenciesCount);
                    statistics.Add(string.Format(statsName, pipType, "Ignored"), detailedSnapShot.IgnoredCount);
                    statistics.Add(string.Format(statsName, pipType, "Total"), detailedSnapShot.Total);
                }

                Logger.Log.ProcessesCacheMissStats(loggingContext, m_numProcessPipsUnsatisfiedFromCache);
                Logger.Log.ProcessesCacheHitStats(loggingContext, m_numProcessPipsSatisfiedFromCache);
                statistics.Add(Statistics.TotalProcessPips, m_numProcessPipsCompleted);

                // Below stats sum to num process pips completed
                statistics.Add(Statistics.ProcessPipCacheHits, m_numProcessPipsSatisfiedFromCache);
                statistics.Add(Statistics.ProcessPipCacheMisses, m_numProcessPipsUnsatisfiedFromCache);
                statistics.Add(Statistics.ProcessPipStartOrShutdownService, processPipsStartOrShutdownService);
                statistics.Add(Statistics.ProcessPipsSkippedDueToFailedDependencies, m_numProcessPipsSkipped);
                statistics.Add(Statistics.ProcessPipsIncrementalSchedulingPruned, m_numProcessesIncrementalSchedulingPruned);
                statistics.Add(Statistics.SucceededFast, m_pipSkippingDownstreamDueToSuccessFast);

                // Verify the stats sum correctly
                long processPipsSum = m_numProcessPipsSatisfiedFromCache + m_numProcessPipsUnsatisfiedFromCache + m_numProcessPipsSkipped;
                if (m_numProcessPipsCompleted != processPipsSum)
                {
                    BuildXL.Tracing.UnexpectedCondition.Log(loggingContext, $"Total process pips != (pip cache hits + pip cache misses + service start/shutdown pips). Total: {m_numProcessPipsCompleted}, Sum: {processPipsSum}");
                }
            }

            if (m_criticalPathStats != null)
            {
                statistics.Add("HistoricalCriticalPath.NumWildGuesses", m_criticalPathStats.NumWildGuesses);
                statistics.Add("HistoricalCriticalPath.NumHits", m_criticalPathStats.NumHits);
                statistics.Add("HistoricalCriticalPath.LongestPathMs", m_criticalPathStats.LongestPath);
            }

            statistics.Add("HistoricPerfData.Misses", m_historicPerfDataMisses);
            statistics.Add("HistoricPerfData.ZeroMemoryHits", m_historicPerfDataZeroMemoryHits);
            statistics.Add("HistoricPerfData.NonZeroMemoryHits", m_historicPerfDataNonZeroMemoryHits);

            statistics.Add("MaxUnresponsivenessFactor", m_maxUnresponsivenessFactor);

            m_historicPerfDataTable?.LogStats(loggingContext);

            Logger.Log.WarningStats(
                loggingContext,
                Volatile.Read(ref m_numPipsWithWarnings),
                Volatile.Read(ref m_numWarnings),
                Volatile.Read(ref m_numPipsWithWarningsFromCache),
                Volatile.Read(ref m_numWarningsFromCache));
            statistics.Add(Statistics.ExecutedPipsWithWarnings, m_numPipsWithWarnings);
            statistics.Add(Statistics.WarningsFromExecutedPips, m_numWarnings);
            statistics.Add(Statistics.CachedPipsWithWarnings, m_numPipsWithWarningsFromCache);
            statistics.Add(Statistics.WarningsFromCachedPips, m_numWarningsFromCache);

            statistics.Add("DirectoryMembershipFingerprinter.RegexObjectCacheMisses", RegexDirectoryMembershipFilter.RegexCache.Misses);
            statistics.Add("DirectoryMembershipFingerprinter.RegexObjectCacheHits", RegexDirectoryMembershipFilter.RegexCache.Hits);
            statistics.Add("DirectoryMembershipFingerprinter.DirectoryContentCacheMisses", m_directoryMembershipFingerprinter.CachedDirectoryContents.Misses);
            statistics.Add("DirectoryMembershipFingerprinter.DirectoryContentCacheHits", m_directoryMembershipFingerprinter.CachedDirectoryContents.Hits);

            int numOfRetires = 1;
            var pipRetryCountersDueToNetworkFailures = m_pipRetryCountersDueToNetworkFailures.Skip(1); // Removing the pips with 0 retires (Successful in 1st attempt)
            foreach (int retryCount in pipRetryCountersDueToNetworkFailures)
            {
                statistics.Add("RetriedDueToStoppedWorker_" + numOfRetires, retryCount);
                numOfRetires++;
            }

            SortedDictionary<int, int> sortedLowMemoryRetryCounters = new SortedDictionary<int, int>(m_pipRetryCountersDueToLowMemory);
            foreach (var current in sortedLowMemoryRetryCounters)
            {
                statistics.Add("RetriedDueToLowMemory_" + current.Key, current.Value);
            }

            Logger.Log.CacheFingerprintHitSources(loggingContext, m_cacheIdHits);

            List<PipCachePerfInfo> cacheLookupPerfInfos = m_runnablePipPerformance.Values.Where(a => a.CacheLookupPerfInfo != null).Select(a => a.CacheLookupPerfInfo).ToList();
            List<PipCachePerfInfo> cacheLookupPerfInfosForHits = cacheLookupPerfInfos.Where(a => a.CacheMissType == PipCacheMissType.Hit).DefaultIfEmpty().ToList();
            List<PipCachePerfInfo> cacheLookupPerfInfosForMisses = cacheLookupPerfInfos.Where(a => a.CacheMissType != PipCacheMissType.Hit).DefaultIfEmpty().ToList();

            PipExecutionCounters.AddToCounter(PipExecutorCounter.MaxCacheEntriesVisitedForHit, cacheLookupPerfInfosForHits.Max(a => a?.NumCacheEntriesVisited) ?? -1);
            PipExecutionCounters.AddToCounter(PipExecutorCounter.MinCacheEntriesVisitedForHit, cacheLookupPerfInfosForHits.Min(a => a?.NumCacheEntriesVisited) ?? -1);
            PipExecutionCounters.AddToCounter(PipExecutorCounter.MaxCacheEntriesVisitedForMiss, cacheLookupPerfInfosForMisses.Max(a => a?.NumCacheEntriesVisited) ?? -1);
            PipExecutionCounters.AddToCounter(PipExecutorCounter.MinCacheEntriesVisitedForMiss, cacheLookupPerfInfosForMisses.Min(a => a?.NumCacheEntriesVisited) ?? -1);

            PipExecutionCounters.AddToCounter(PipExecutorCounter.MaxCacheEntriesAbsentForHit, cacheLookupPerfInfosForHits.Max(a => a?.NumCacheEntriesAbsent) ?? -1);
            PipExecutionCounters.AddToCounter(PipExecutorCounter.MinCacheEntriesAbsentForHit, cacheLookupPerfInfosForHits.Min(a => a?.NumCacheEntriesAbsent) ?? -1);
            PipExecutionCounters.AddToCounter(PipExecutorCounter.MaxCacheEntriesAbsentForMiss, cacheLookupPerfInfosForMisses.Max(a => a?.NumCacheEntriesAbsent) ?? -1);
            PipExecutionCounters.AddToCounter(PipExecutorCounter.MinCacheEntriesAbsentForMiss, cacheLookupPerfInfosForMisses.Min(a => a?.NumCacheEntriesAbsent) ?? -1);

            PipExecutionCounters.AddToCounter(PipExecutorCounter.MaxPathSetsDownloadedForHit, cacheLookupPerfInfosForHits.Max(a => a?.NumPathSetsDownloaded) ?? -1);
            PipExecutionCounters.AddToCounter(PipExecutorCounter.MinPathSetsDownloadedForHit, cacheLookupPerfInfosForHits.Min(a => a?.NumPathSetsDownloaded) ?? -1);
            PipExecutionCounters.AddToCounter(PipExecutorCounter.MaxPathSetsDownloadedForMiss, cacheLookupPerfInfosForMisses.Max(a => a?.NumPathSetsDownloaded) ?? -1);
            PipExecutionCounters.AddToCounter(PipExecutorCounter.MinPathSetsDownloadedForMiss, cacheLookupPerfInfosForMisses.Min(a => a?.NumPathSetsDownloaded) ?? -1);

            var currentTime = DateTime.UtcNow;
            var earlyReleaseSavingDurationMs = Workers.Where(a => a.WorkerEarlyReleasedTime != null).Select(a => (currentTime - a.WorkerEarlyReleasedTime.Value).TotalMilliseconds).Sum();
            PipExecutionCounters.AddToCounter(PipExecutorCounter.RemoteWorker_EarlyReleaseSavingDurationMs, (long)earlyReleaseSavingDurationMs);

            PipExecutionCounters.LogAsStatistics("PipExecution", loggingContext);

            m_groupedPipCounters.LogAsPipCounters();

            // Verify counters for different types of cache misses sum to pips executed due to cache misses
            long cacheMissSum = 0;
            using (var pooledStringBuilder = Pools.GetStringBuilder())
            {
                var sb = pooledStringBuilder.Instance;
                foreach (var missType in PipCacheMissTypeExtensions.AllCacheMisses)
                {
                    var counter = missType.ToCounter();
                    var counterValue = PipExecutionCounters.GetCounterValue(counter);
                    cacheMissSum += counterValue;

                    var frontierPipCounterValue = PipExecutionCounters.GetCounterValue(counter.ToFrontierPipCacheMissCounter());
                    if (frontierPipCounterValue > counterValue)
                    {
                        sb.Append($"{(sb.Length == 0 ? "" : ", ")}['{counter}' : {counterValue} != {frontierPipCounterValue}]");
                    }
                }

                if (sb.Length > 0)
                {
                    BuildXL.Tracing.UnexpectedCondition.Log(loggingContext, $"Cache miss counters for frontier pips have unexpected values: {sb.ToString()}.");
                }
            }

            long processPipsExecutedDueToCacheMiss = PipExecutionCounters.GetCounterValue(PipExecutorCounter.ProcessPipsExecutedDueToCacheMiss);
            long processPipsSkippedExecutionDueToCacheOnly = PipExecutionCounters.GetCounterValue(PipExecutorCounter.ProcessPipsSkippedExecutionDueToCacheOnly);
            // Cache miss type counters are not updated on workers in a distributed build.
            if (!IsDistributedWorker && (processPipsExecutedDueToCacheMiss + processPipsSkippedExecutionDueToCacheOnly) != cacheMissSum)
            {
                BuildXL.Tracing.UnexpectedCondition.Log(loggingContext, $"ProcessPipsExecutedDueToCacheMiss + ProcessPipsSkippedExecutionDueToCacheOnly != sum of counters for all cache miss types. " +
                    $"ProcessPipsExecutedDueToCacheMiss: {processPipsExecutedDueToCacheMiss}, ProcessPipsSkippedExecutionDueToCacheOnly: {processPipsSkippedExecutionDueToCacheOnly}, Sum: {cacheMissSum}");
            }

            // Log details about pips skipped under /CacheOnly mode only if pips were actually skipped.
            if (m_configuration.Schedule.CacheOnly && processPipsSkippedExecutionDueToCacheOnly > 0)
            {
                // Log the total number of pips skipped including downstream pips.
                // processPipsSkippedExecutionDueToCacheOnly only contains the pips where cache lookup was performed, not downstream pips that were skipped
                Logger.Log.CacheOnlyStatistics(loggingContext, m_numProcessPipsSkipped);
            }

            m_apiServer?.LogStats();

            State?.Cache.Counters.LogAsStatistics("PipCaching", loggingContext);
            State?.FileSystemView?.Counters.LogAsStatistics("FileSystemView", loggingContext);
            m_localDiskContentStore?.LogStats();
            m_fileChangeTracker?.Counters.LogAsStatistics("FileChangeTracker", loggingContext);
            m_fileContentTable.Counters.LogAsStatistics("FileContentTable", loggingContext);
            FileUtilities.Counters?.LogAsStatistics("Storage", loggingContext);
            m_fileMonitoringViolationAnalyzer?.Counters.LogAsStatistics("FileMonitoringViolationAnalysis", loggingContext);
            m_pipExecutionStepCounters.LogAsStatistics("PipExecutionStep", loggingContext);
            m_executionLogFileTarget?.PopulateStats();
            m_executionLogFileTarget?.Counters.LogAsStatistics("ExecutionLogFileTarget", loggingContext);
            if (IsDistributedWorker)
            {
                // Log the NotifyOrchestratorExecutionLogTarget stats for the distributed worker
                m_workerManifestExecutionLogTarget?.PopulateStats();
                m_workerManifestExecutionLogTarget?.Counters.LogAsStatistics("ManifestExecutionLog", loggingContext);
                m_reportExecutionLogTarget?.PopulateStats();
                m_reportExecutionLogTarget?.Counters.LogAsStatistics("ReportExecutionLog", loggingContext);
            }
            SandboxedProcessFactory.Counters.LogAsStatistics("SandboxedProcess", loggingContext);
            BuildManifestGenerator.Counters.LogAsStatistics("BuildManifestGenerator", loggingContext);
            statistics.AddRange(ContentHashingUtilities.GetContentHasher(ContentHashingUtilities.HashInfo.HashType).GetCounters().ToDictionaryIntegral());

            m_pipPropertyInfo.LogPipPropertyInfo(loggingContext);
            m_pipRetryInfo.LogPipRetryInfo(loggingContext, PipExecutionCounters);

            m_servicePipTracker?.LogStats(loggingContext);

            if (m_configuration.Logging.EnableCloudBuildEtwLoggingIntegration)
            {
                Contract.Assert(m_servicePipTracker != null, "Must use DropPipTracker when running in CloudBuild");
                CloudBuildEventSource.Log.DominoFinalStatisticsEvent(new DominoFinalStatisticsEvent
                {
                    LastDropPipCompletionUtcTicks = m_servicePipTracker.LastServicePipCompletionTime.Ticks,
                    LastNonDropPipCompletionUtcTicks = m_servicePipTracker.LastNonServicePipCompletionTime.Ticks,
                });
            }

            var totalQueueDurations = new long[(int)DispatcherKind.Materialize + 1];
            var totalStepDurations = new long[(int)PipExecutionStep.Done + 1];
            var totalRemoteStepDurations = new long[(int)PipExecutionStep.Done + 1];
            var totalQueueRequestDurations = new long[(int)PipExecutionStep.Done + 1];
            var totalSendRequestDurations = new long[(int)PipExecutionStep.Done + 1];

            foreach (var perfData in m_runnablePipPerformance)
            {
                UpdateDurationList(totalQueueDurations, perfData.Value.QueueDurations);
                UpdateDurationList(totalStepDurations, perfData.Value.StepDurations);
                UpdateDurationList(totalRemoteStepDurations, perfData.Value.RemoteStepDurations);
                UpdateDurationList(totalQueueRequestDurations, perfData.Value.PipBuildRequestQueueDurations);
                UpdateDurationList(totalSendRequestDurations, perfData.Value.PipBuildRequestGrpcDurations);
            }

            var perfStatsName = "PipPerfStats.{0}_{1}";
            for (int i = 0; i < totalQueueDurations.Length; i++)
            {
                statistics.Add(string.Format(perfStatsName, "Queue", (DispatcherKind)i), totalQueueDurations[i]);
            }

            for (int i = 0; i < totalStepDurations.Length; i++)
            {
                statistics.Add(string.Format(perfStatsName, "Run", (PipExecutionStep)i), totalStepDurations[i]);
            }

            for (int i = 0; i < totalRemoteStepDurations.Length; i++)
            {
                statistics.Add(string.Format(perfStatsName, "RemoteRun", (PipExecutionStep)i), totalRemoteStepDurations[i]);
            }

            for (int i = 0; i < totalQueueRequestDurations.Length; i++)
            {
                statistics.Add(string.Format(perfStatsName, "QueueRequest", (PipExecutionStep)i), totalQueueRequestDurations[i]);
            }

            for (int i = 0; i < totalSendRequestDurations.Length; i++)
            {
                statistics.Add(string.Format(perfStatsName, "SendRequest", (PipExecutionStep)i), totalSendRequestDurations[i]);
            }

            statistics.Add("TotalPeakWorkingSetMb", (long)m_totalPeakWorkingSetMb);
            statistics.Add("TotalAverageWorkingSetMb", (long)m_totalAverageWorkingSetMb);
            statistics.Add("TotalPeakCommitSizeMb", (long)m_totalPeakCommitSizeMb);
            statistics.Add("TotalAverageCommitSizeMb", (long)m_totalAverageCommitSizeMb);

            if (m_pluginManager != null)
            {
                statistics.Add(Statistics.PluginLoadingTime, (long)m_pluginManager.PluginLoadingTime);
                statistics.Add(Statistics.PluginTotalProcessTime, (long)m_pluginManager.PluginTotalProcessTime);
                statistics.Add(Statistics.PluginLoadedSuccessfulCounts, (long)m_pluginManager.PluginLoadedSuccessfulCount);
                statistics.Add(Statistics.PluginLoadedFailureCounts, (long)m_pluginManager.PluginLoadedFailureCount);
                statistics.Add(Statistics.PluginProcessedRequestCounts, (long)m_pluginManager.PluginProcessedRequestCounts);
                statistics.Add(Statistics.PluginUnregisteredCounts, (long)m_pluginManager.PluginProcessedRequestCounts);
            }

            m_chooseWorkerCpu.LogStats(statistics);
            ExecutionSampler.GetLimitingResourcePercentages().AddToStats(statistics);

            BuildXL.Tracing.Logger.Log.BulkStatistic(loggingContext, statistics);

            return new SchedulerPerformanceInfo
            {
                PipExecutionStepCounters = m_pipExecutionStepCounters,
                ExecuteProcessDurationMs = SafeConvert.ToLong(PipExecutionCounters.GetElapsedTime(PipExecutorCounter.ExecuteProcessDuration).TotalMilliseconds),
                ProcessOutputsObservedInputValidationDurationMs = SafeConvert.ToLong(PipExecutionCounters.GetElapsedTime(PipExecutorCounter.ProcessOutputsObservedInputValidationDuration).TotalMilliseconds),
                ProcessOutputsStoreContentForProcessAndCreateCacheEntryDurationMs = SafeConvert.ToLong(PipExecutionCounters.GetElapsedTime(PipExecutorCounter.ProcessOutputsStoreContentForProcessAndCreateCacheEntryDuration).TotalMilliseconds),
                CanceledProcessExecuteDurationMs = SafeConvert.ToLong(PipExecutionCounters.GetElapsedTime(PipExecutorCounter.CanceledProcessExecuteDuration).TotalMilliseconds),
                ProcessPipCacheHits = m_numProcessPipsSatisfiedFromCache,
                ProcessPipIncrementalSchedulingPruned = m_numProcessesIncrementalSchedulingPruned,
                TotalProcessPips = m_numProcessPipsCompleted,
                ProcessPipCacheMisses = m_numProcessPipsUnsatisfiedFromCache,
                ProcessPipsUncacheable = PipExecutionCounters.GetCounterValue(PipExecutorCounter.ProcessPipsExecutedButUncacheable),
                CriticalPathTableHits = m_criticalPathStats?.NumHits ?? 0,
                CriticalPathTableMisses = m_criticalPathStats?.NumWildGuesses ?? 0,
                FileContentStats = m_fileContentManager.FileContentStats,
                RunProcessFromCacheDurationMs = SafeConvert.ToLong(PipExecutionCounters.GetElapsedTime(PipExecutorCounter.RunProcessFromCacheDuration).TotalMilliseconds),
                RunProcessFromRemoteCacheDurationMs = SafeConvert.ToLong(PipExecutionCounters.GetElapsedTime(PipExecutorCounter.RunProcessFromRemoteCacheDuration).TotalMilliseconds),
                SandboxedProcessPrepDurationMs = PipExecutionCounters.GetCounterValue(PipExecutorCounter.SandboxedProcessPrepDurationMs),
                MachineMinimumAvailablePhysicalMB = SafeConvert.ToLong(((m_performanceAggregator != null && m_performanceAggregator.MachineAvailablePhysicalMB.Count > 2) ? m_performanceAggregator.MachineAvailablePhysicalMB.Minimum : -1)),
                AverageMachineCPU = (m_performanceAggregator != null && m_performanceAggregator.MachineCpu.Count > 2) ? (int)m_performanceAggregator.MachineCpu.Average : 0,
                DiskStatistics = m_performanceAggregator != null ? m_performanceAggregator.DiskStats : null,
                HitLowMemorySmell = m_hitLowRamMemoryPerfSmell,
                ProcessPipCountersByTelemetryTag = ProcessPipCountersByTelemetryTag
            };
        }

        private static void LogStatistic(LoggingContext loggingContext, string key, int value)
        {
            BuildXL.Tracing.Logger.Log.Statistic(loggingContext, new Statistic { Name = key, Value = value });
        }

        /// <summary>
        /// Gets scheduler stats.
        /// </summary>
        public SchedulerStats SchedulerStats => new SchedulerStats
        {
            ProcessPipsCompleted = Volatile.Read(ref m_numProcessPipsCompleted),
            IpcPipsCompleted = Volatile.Read(ref m_numIpcPipsCompleted),
            ProcessPipsSatisfiedFromCache = Volatile.Read(ref m_numProcessPipsSatisfiedFromCache),
            ProcessPipsUnsatisfiedFromCache = Volatile.Read(ref m_numProcessPipsUnsatisfiedFromCache),
            FileContentStats = m_fileContentManager.FileContentStats,
            PipsWithWarnings = Volatile.Read(ref m_numPipsWithWarnings),
            PipsWithWarningsFromCache = Volatile.Read(ref m_numPipsWithWarningsFromCache),
            ServicePipsCompleted = m_serviceManager.TotalServicePipsCompleted,
            ServiceShutdownPipsCompleted = m_serviceManager.TotalServiceShutdownPipsCompleted,
        };

        private StatusRows m_statusRows;
        private readonly PipExecutionStepTracker m_executionStepTracker = new PipExecutionStepTracker();

        private StatusRows GetStatusRows()
        {
            var windowsDiskStats = !OperatingSystemHelper.IsUnixOS ? m_performanceAggregator?.DiskStats : null; // Some disk stats are available only in Windows, we remove these columns from Mac builds for a cleaner status.csv file
            return new StatusRows()
            {
                { "Cpu Percent", data => data.CpuPercent },
                { "Cpu Percent (WMI)", data => m_perfInfo.CpuWMIUsagePercentage },

                { "ContextSwitches (WMI)", data => m_perfInfo.ContextSwitchesPerSec },
                { "CpuQueueLength (WMI)", data => m_perfInfo.CpuQueueLength },
                { "Threads (WMI)", data => m_perfInfo.Threads },
                { "Processes (WMI)", data => m_perfInfo.Processes },

                { "BuildXL Cpu Percent", data => m_perfInfo.ProcessCpuPercentage },
                { "JobObject Cpu Percent", data => m_perfInfo.JobObjectCpu },
                { "JobObject Processes", data => m_perfInfo.JobObjectProcesses },
                { "Ram Percent", data => data.RamPercent },
                { "EffectiveRam Percent", data => m_perfInfo.EffectiveRamUsagePercentage ?? 0},
                { "Used Ram Mb", data => data.RamUsedMb },
                { "Free Ram Mb", data => data.RamFreeMb },
                { "ModifiedPagelistMb", data => m_perfInfo.ModifiedPagelistMb ?? 0},
                { "Commit Percent", data => data.CommitPercent },
                { "Used Commit Mb", data => data.CommitUsedMb },
                { "Free Commit Mb", data => data.CommitFreeMb },
                { "NetworkBandwidth", data => m_perfInfo.MachineBandwidth },
                { "MachineKbitsPerSecSent", data => (long)m_perfInfo.MachineKbitsPerSecSent },
                { "MachineKbitsPerSecReceived", data => (long)m_perfInfo.MachineKbitsPerSecReceived },
                { "DispatchIterations", data => OptionalPipQueueImpl?.DispatcherIterations ?? 0 },
                { "DispatchTriggers", data => OptionalPipQueueImpl?.TriggerDispatcherCount ?? 0 },
                { "DispatchMs", data => (long)(OptionalPipQueueImpl?.DispatcherLoopTime.TotalMilliseconds ?? 0) },
                { "ChooseQueueFastNextCount", data => OptionalPipQueueImpl?.ChooseQueueFastNextCount ?? 0 },
                { "ChooseQueueRunTimeMs", data => OptionalPipQueueImpl?.ChooseQueueRunTime.TotalMilliseconds ?? 0 },
                { "LastSchedulerConcurrencyLimiter", data => m_chooseWorkerCpu.LastConcurrencyLimiter?.Name ?? "N/A" },
                { "LimitingResource", data => data.LimitingResource},
                { "MemoryResourceAvailability", data => LocalWorker.MemoryResource.ToString().Replace(',', '-')},
                { "CpuResourceAvailability", data => LocalWorker.CpuResourceAvailable ? 1 : 0},
                { "ProcessRetriesDueToResourceLimits", data => PipExecutionCounters.GetCounterValue(PipExecutorCounter.ProcessRetriesDueToResourceLimits)},
                { "EmptyWorkingSetSucceeded", data => PipExecutionCounters.GetCounterValue(PipExecutorCounter.EmptyWorkingSetSucceeded)},
                { "ResourceManager_TotalUsedWorkingSet", data => State.ResourceManager.TotalUsedWorkingSet},
                { "ResourceManager_TotalUsedPeakWorkingSet", data => State.ResourceManager.TotalUsedPeakWorkingSet},
                { "ResourceManager_TotalRamNeededForResume", data => State.ResourceManager.TotalRamMbNeededForResume},
                { "ResourceManager_LastRequiredSize", data => State.ResourceManager.LastRequiredSizeMb},
                { "ResourceManager_LastManageMemoryMode", data => State.ResourceManager.LastManageMemoryMode?.ToString() ?? ""},
                { "ResourceManager_NumSuspended", data => State.ResourceManager.NumSuspended},
                { "ResourceManager_ServicePipsTotalUsedWorkingSet", data => State.ResourceManager.GetPipsCurrentUsedWorkingSet(m_servicePipIds) },
                {
                    EnumTraits<PipState>.EnumerateValues(), (rows, state) =>
                    {
                        if (!IsDistributedWorker)
                        {
                            rows.Add(I($"State.{state}"), _ => m_pipTypesToLogCountersSnapshot[state]);
                        }
                    }
                },
                {
                    EnumTraits<DispatcherKind>.EnumerateValues(), (rows, kind) =>
                    {
                        if (kind != DispatcherKind.None)
                        {
                            rows.Add(I($"{kind} Queued"), _ => PipQueue.GetNumQueuedByKind(kind));
                            rows.Add(I($"{kind} Running"), _ => PipQueue.GetNumAcquiredSlotsByKind(kind));
                            rows.Add(I($"{kind} CurrentMax"), _ => PipQueue.GetMaxParallelDegreeByKind(kind));

                            if (PipQueue.IsUseWeightByKind(kind))
                            {
                                // For CPU dispatcher, the number of running pips and slots might not be the same due to the weight.
                                rows.Add(I($"{kind} RunningPips"), _ => PipQueue.GetNumRunningPipsByKind(kind));
                            }
                        }
                    }
                },
                { "Running Pips", data => PipQueue.NumRunningOrQueuedOrRemote },
                { "Running Pips Remotely", data => PipQueue.NumRemoteRunning },
                { "Running PipExecutor Processes", data => data.RunningPipExecutorProcesses },
                { "Running Processes", data => data.RunningProcesses },
                { "Running Process Remotely", data => data.RunningRemotelyPipExecutorProcesses },
                { "Running Process Locally", data => data.RunningLocallyPipExecutorProcesses },
                { "Total Run Process Remotely", data => data.TotalRunRemotelyProcesses },
                { "Total Run Process Locally", data => data.TotalRunLocallyProcesses },
                { "Running service pips", data => m_serviceManager.RunningServicesCount },
                { "PipTable.ReadCount", data => m_pipTable.Reads },
                { "PipTable.ReadDurationMs", data => m_pipTable.ReadsMilliseconds },
                { "PipTable.WriteCount", data => m_pipTable.Writes },
                { "PipTable.WriteDurationMs", data => m_pipTable.WritesMilliseconds },

                // Drive stats
                { windowsDiskStats, d => I($"Drive \'{d.Drive}\' % Active"), (d, index) => (data => data.DiskPercents[index]) },
                { windowsDiskStats, d => I($"Drive \'{d.Drive}\' QueueDepth"), (d, index) => (data => data.DiskQueueDepths[index]) },
                { m_performanceAggregator?.DiskStats, d => I($"Drive \'{d.Drive}\' AvailableSpaceGB"), (d, index) => (data => data.DiskAvailableSpaceGb[index]) },

                {
                    EnumTraits<PipType>.EnumerateValues().Where(pipType => pipType != PipType.Max), (rows, pipType) =>
                    {
                        if (!IsDistributedWorker)
                        {
                            rows.Add(I($"{pipType} Waiting"), _ => m_pipStateCountersSnapshots[(int)pipType][PipState.Waiting]);
                            rows.Add(I($"{pipType} Ready"), _ => m_pipStateCountersSnapshots[(int)pipType][PipState.Ready]);
                            rows.Add(I($"{pipType} Running"), _ => m_pipStateCountersSnapshots[(int)pipType][PipState.Running]);
                            rows.Add(I($"{pipType} Done"), _ => m_pipStateCountersSnapshots[(int)pipType][PipState.Done]);
                        }
                    }
                },

                // BuildXL process stats
                { "Domino.CPUPercent", data => data.ProcessCpuPercent },
                { "Domino.WorkingSetMB", data => data.ProcessWorkingSetMB },
                { "UnresponsivenessFactor", data => data.UnresponsivenessFactor },

                // PipExecutionStep counts
                {
                    EnumTraits<PipExecutionStep>.EnumerateValues().Where(step => step != PipExecutionStep.None), (rows, step) =>
                    {
                        rows.Add(I($"{step} Active"), _ => m_executionStepTracker.CurrentSnapshot[step]);
                        rows.Add(I($"{step} Total"), _ => m_executionStepTracker.CurrentSnapshot.GetCumulativeCount(step));
                    }
                },

                { "ProcessPipsCacheMiss", _ => Volatile.Read(ref m_numProcessPipsUnsatisfiedFromCache) },
                { "ProcessPipsCacheHit", _ => Volatile.Read(ref m_numProcessPipsSatisfiedFromCache) },
                { "ProcessPipsPending", data => data.ProcessPipsPending },
                { "ProcessPipsAllocatedSlots", data => data.ProcessPipsAllocatedSlots },
                { "ProcessPipsWaiting", data => data.ProcessPipsPending - data.ProcessPipsAllocatedSlots },
                { "TotalAcquiredProcessSlots", data => Workers.Where(a => a.IsAvailable).Sum(a => a.AcquiredProcessSlots) },
                { "MachineActiveTcpConnections", _ => m_perfInfo.MachineActiveTcpConnections },
                { "MachineOpenFileDescriptors", _ => m_perfInfo.MachineOpenFileDescriptors },
                { "AvailableWorkersCount", data => AvailableWorkersCount },

                // Worker Pip State counts and status
                {
                    m_workers, (rows, worker) =>
                    {
                        rows.Add(I($"W{worker.WorkerId} Total CacheLookup Slots"), _ => worker.TotalCacheLookupSlots, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Used CacheLookup Slots"), _ => worker.AcquiredCacheLookupSlots, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Total MaterializeInput Slots"), _ => worker.TotalMaterializeInputSlots, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Used MaterializeInput Slots"), _ => worker.AcquiredMaterializeInputSlots, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Total Process Slots"), _ => worker.TotalProcessSlots, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Used Process Slots"), _ => worker.AcquiredProcessSlots, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Used PostProcess Slots"), _ => worker.AcquiredPostProcessSlots, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Total LightProcess Slots"), _ => worker.TotalLightProcessSlots, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Used LightProcess Slots"), _ => worker.AcquiredLightProcessSlots, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Total Ipc Slots"), _ => worker.TotalIpcSlots, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Used Ipc Slots"), _ => worker.AcquiredIpcSlots, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Waiting BuildRequests Count"), _ => worker.WaitingBuildRequestsCount, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} BatchSize Count"), _ => worker.CurrentBatchSize, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Total Ram Mb"), _ => worker.TotalRamMb ?? 0, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Estimated Free Ram Mb"), _ => worker.EstimatedFreeRamMb, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Actual Free Ram Mb"), _ => worker.ActualFreeMemoryMb ?? 0, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Total Commit Mb"), _ => worker.TotalCommitMb ?? 0, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Estimated Free Commit Mb"), _ => worker.EstimatedFreeCommitMb, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Actual Free Commit Mb"), _ => worker.ActualFreeCommitMb ?? 0, includeInSnapshot: false);
                        rows.Add(I($"W{worker.WorkerId} Status"), _ => worker.Status, includeInSnapshot: false);
                    }
                },
            }.Seal();
        }

        /// <summary>
        /// Sends a status update to the log if the minimal interval of time since the last update has passed and updates resource manager
        /// with latest resource utilization if enabled.
        /// </summary>
        public void UpdateStatus(bool overwriteable = true, int expectedCallbackFrequency = 0)
        {
            lock (m_statusLock)
            {
                DateTime utcNow = DateTime.UtcNow;
                bool isLoggingEnabled = !overwriteable || (utcNow > m_previousStatusLogTimeUtc.AddMilliseconds(m_loggingIntervalPeriodMs));
                if (isLoggingEnabled)
                {
                    m_previousStatusLogTimeUtc = utcNow;
                }

                m_unresponsivenessFactor = ComputeUnresponsivenessFactor(expectedCallbackFrequency, m_statusLastCollected, DateTime.UtcNow);
                m_maxUnresponsivenessFactor = Math.Max(m_unresponsivenessFactor, m_maxUnresponsivenessFactor);
                m_statusLastCollected = DateTime.UtcNow;

                // Log a specific message we can query from telemetry if unresponsiveness gets very high
                if (m_unresponsivenessFactor > 10)
                {
                    BuildXL.Tracing.Logger.Log.StatusCallbacksDelayed(m_executePhaseLoggingContext, m_unresponsivenessFactor);
                }

                if (m_statusRows == null)
                {
                    m_statusRows = GetStatusRows();
                    BuildXL.Tracing.Logger.Log.StatusHeader(m_executePhaseLoggingContext, m_statusRows.PrintHeaders());
                }

                OperationTracker.WriteCountersFile(Context, m_configuration.Logging, refreshInterval: TimeSpan.FromSeconds(30));

                // Update snapshots for status reporting
                m_executionStepTracker.CurrentSnapshot.Update();

                m_pipStateCounters.CollectSnapshotsForEachType(m_pipStateCountersSnapshots);
                m_pipTypesToLogCountersSnapshot.AggregateByPipTypes(m_pipStateCountersSnapshots, s_pipTypesToLogStats);

                var pipsWaiting = m_pipTypesToLogCountersSnapshot[PipState.Waiting];
                var pipsReady = m_pipTypesToLogCountersSnapshot[PipState.Ready];
                long semaphoreQueued = PipQueue.NumSemaphoreQueued;

                // The PipQueue might concurrently start to run queued items, so we match the numbers we get back with
                // the current scheduler state to avoid confusing our user looking at the status log message.
                semaphoreQueued = Math.Min(semaphoreQueued, pipsReady);

                // Treat queued semaphores as waiting pips  for status messages
                // rather than ready pips (even though their state is Ready).
                pipsReady -= semaphoreQueued;
                pipsWaiting += semaphoreQueued;

                ExecutionSampler.LimitingResource limitingResource = ExecutionSampler.LimitingResource.Other;
                if (m_performanceAggregator != null)
                {
                    limitingResource = ExecutionSampler.OnPerfSample(
                        m_performanceAggregator,
                        pendingProcessPips: m_processStateCountersSnapshot[PipState.Ready] + m_processStateCountersSnapshot.RunningCount - LocalWorker.RunningPipExecutorProcesses.Count,
                        lastConcurrencyLimiter: m_chooseWorkerCpu.LastConcurrencyLimiter);
                }

                m_processStateCountersSnapshot.AggregateByPipTypes(m_pipStateCountersSnapshots, s_processPipTypesToLogStats);

                // Only log process counters for distributed build
                if (isLoggingEnabled && IsDistributedBuild)
                {
                    Logger.Log.ProcessStatus(
                        m_executePhaseLoggingContext,
                        pipsSucceeded: m_processStateCountersSnapshot.DoneCount,
                        pipsFailed: m_processStateCountersSnapshot[PipState.Failed],
                        pipsSkippedDueToFailedDependencies: m_processStateCountersSnapshot.SkippedDueToFailedDependenciesCount,
                        pipsRunning: m_processStateCountersSnapshot.RunningCount,
                        pipsReady: m_processStateCountersSnapshot[PipState.Ready] - semaphoreQueued,
                        pipsWaiting: m_processStateCountersSnapshot[PipState.Waiting] + semaphoreQueued,
                        pipsWaitingOnSemaphore: semaphoreQueued);
                }

                m_perfInfo = m_performanceAggregator?.ComputeMachinePerfInfo(ensureSample: m_testHooks != null) ??
                    (m_testHooks?.GenerateSyntheticMachinePerfInfo != null ? m_testHooks?.GenerateSyntheticMachinePerfInfo(m_executePhaseLoggingContext, this) : null) ??
                    default(PerformanceCollector.MachinePerfInfo);

                UpdateResourceAvailability(m_perfInfo);

                // The number of pips in the ChooseWorkerCpu step is our best analog for the amount of work that could be
                // run if there were more resources.
                // Previously this was comparing TotalProcessSlots vs. AcquiredProcessSlots. That ended up being problematic because
                // when resource based cancellation happens, the TotalProcessSlots becomes smaller than AquiredProcessSlots and the
                // result of this comparison was negative. Moreover, in steady state when the scheduler is resource throttled, the two
                // will be equal and also not give an accurate measurement.
                int pipsWaitingOnResources = m_executionStepTracker.CurrentSnapshot[PipExecutionStep.ChooseWorkerCpu];

                // Log pip statistics to CloudBuild.
                if (isLoggingEnabled && m_configuration.Logging.EnableCloudBuildEtwLoggingIntegration)
                {
                    CloudBuildEventSource.Log.DominoContinuousStatisticsEvent(new DominoContinuousStatisticsEvent
                    {
                        // The number of ignored pips should not contribute to the total because Batmon progress depends on this calculation: executedPips / totalPips
                        TotalPips = m_pipTypesToLogCountersSnapshot.Total - m_pipTypesToLogCountersSnapshot[PipState.Ignored],
                        TotalProcessPips = m_processStateCountersSnapshot.Total - m_processStateCountersSnapshot[PipState.Ignored] - m_numServicePipsScheduled,
                        PipsFailed = m_pipTypesToLogCountersSnapshot[PipState.Failed],
                        PipsSkippedDueToFailedDependencies = m_pipTypesToLogCountersSnapshot.SkippedDueToFailedDependenciesCount,
                        PipsSuccessfullyExecuted = m_pipTypesToLogCountersSnapshot.DoneCount,
                        // This gives the number of pips that were in ExecuteProcess state and does not include pips from other steps like ChooseWorker, MaterializeInputs. 
                        PipsExecuting = m_executionStepTracker.CurrentSnapshot[PipExecutionStep.ExecuteProcess],
                        PipsReadyToRun = pipsReady,
                        // Process pips executed only counts pips that went through cache lookup (i.e. service pips are not included)
                        ProcessPipsExecuted = m_numProcessPipsSatisfiedFromCache + m_numProcessPipsUnsatisfiedFromCache,
                        ProcessPipsExecutedFromCache = m_numProcessPipsSatisfiedFromCache,
                    });
                }

                PipStateCountersSnapshot copyFileStats = new PipStateCountersSnapshot();
                copyFileStats.AggregateByPipTypes(m_pipStateCountersSnapshots, new PipType[] { PipType.CopyFile });

                PipStateCountersSnapshot writeFileStats = new PipStateCountersSnapshot();
                writeFileStats.AggregateByPipTypes(m_pipStateCountersSnapshots, new PipType[] { PipType.WriteFile });

                // The number of processes executing on the orchestrator and the remote workers in a distributed build.
                var processesExecutingOnWorkers = Workers.Where(a => a.IsAvailable).Sum(a => a.AcquiredProcessSlots);

                if (isLoggingEnabled)
                {
                    // Log pip statistics to Console
                    LogPipStatus(
                        m_executePhaseLoggingContext,
                        pipsSucceeded: m_pipTypesToLogCountersSnapshot.DoneCount,
                        pipsFailed: m_pipTypesToLogCountersSnapshot[PipState.Failed],
                        pipsSkippedDueToFailedDependencies: m_pipTypesToLogCountersSnapshot.SkippedDueToFailedDependenciesCount,
                        pipsRunning: m_pipTypesToLogCountersSnapshot.RunningCount,
                        pipsReady: pipsReady,
                        pipsWaiting: pipsWaiting,
                        pipsWaitingOnSemaphore: semaphoreQueued,
                        servicePipsRunning: m_serviceManager.RunningServicesCount,
                        perfInfoForConsole: m_perfInfo.ConsoleResourceSummary,
                        pipsWaitingOnResources: pipsWaitingOnResources,
                        // For the worker machines in ADO, we need to use the DispatcherQueue to obtain the number of processesExecuting. 
                        // While these counters represent the accurate values for the orchestrator machine, they do not for the worker machines.
                        procsExecuting: (!IsDistributedWorker) ? processesExecutingOnWorkers : PipQueue.GetNumRunningPipsByKind(DispatcherKind.CPU),
                        procsSucceeded: m_processStateCountersSnapshot[PipState.Done],
                        procsFailed: m_processStateCountersSnapshot[PipState.Failed],
                        procsSkippedDueToFailedDependencies: m_processStateCountersSnapshot[PipState.Skipped],

                        // This uses a seemingly peculiar calculation to make sure it makes sense regardless of whether pipelining
                        // is on or not. Pending is an intentionally invented state since it doesn't correspond to a real state
                        // in the scheduler. It is basically meant to be a bucket of things that could be run if more parallelism
                        // were available. This technically isn't true because cache lookups fall in there as well, but it's close enough.
                        procsPending: m_processStateCountersSnapshot[PipState.Ready] + m_processStateCountersSnapshot[PipState.Running] - LocalWorker.RunningPipExecutorProcesses.Count,
                        procsWaiting: m_processStateCountersSnapshot[PipState.Waiting],
                        procsCacheHit: m_numProcessPipsSatisfiedFromCache,
                        procsNotIgnored: m_processStateCountersSnapshot.Total - m_processStateCountersSnapshot.IgnoredCount,
                        limitingResource: limitingResource.ToString(),
                        perfInfoForLog: m_perfInfo.LogResourceSummary,
                        overwriteable: overwriteable,
                        copyFileDone: copyFileStats.DoneCount,
                        copyFileNotDone: copyFileStats.Total - copyFileStats.DoneCount - copyFileStats.IgnoredCount,
                        writeFileDone: writeFileStats.DoneCount,
                        writeFileNotDone: writeFileStats.Total - writeFileStats.DoneCount - writeFileStats.IgnoredCount,
                        procsRemoted: LocalWorker is LocalWorkerWithRemoting remoteLocal ? remoteLocal.CurrentRunRemoteCount : 0);
                }

                // Number of process pips that are not completed yet.
                long numProcessPipsPending = m_processStateCountersSnapshot[PipState.Waiting] + m_processStateCountersSnapshot[PipState.Ready] + m_processStateCountersSnapshot[PipState.Running];

                // PipState.Running does not mean that the pip is actually running. The pip might be waiting for a slot.
                // That's why, we need to get the actual number of process pips that were allocated a slot on the workers (including localworker).
                long numProcessPipsAllocatedSlots = Workers.Sum(a => a.AcquiredSlotsForProcessPips);

                // Verify available disk space is greater than the minimum available space specified in /minimumDiskSpaceForPipsGb:<int>
                if (m_diskSpaceMonitoredDrives != null &&
                    !m_scheduleTerminating &&
                    m_performanceAggregator != null &&
                    (m_scheduleConfiguration.MinimumDiskSpaceForPipsGb ?? 0) > 0)
                {
                    foreach (var disk in m_performanceAggregator.DiskStats)
                    {
                        if (m_diskSpaceMonitoredDrives.Contains(disk.Drive)
                            && disk.AvailableSpaceGb.Count != 0 // If we ever have a successful collection of the disk space
                            && disk.AvailableSpaceGb.Latest < (double)m_scheduleConfiguration.MinimumDiskSpaceForPipsGb)
                        {
                            Logger.Log.WorkerFailedDueToLowDiskSpace(
                                m_loggingContext,
                                disk.Drive,
                                (int)m_scheduleConfiguration.MinimumDiskSpaceForPipsGb,
                                (int)disk.AvailableSpaceGb.Latest);

                            RequestTermination(cancelQueue: true, cancelRunningPips: true);
                            break;
                        }
                    }
                }

                LocalWorkerWithRemoting workerWithRemoting = LocalWorker as LocalWorkerWithRemoting;

                var data = new StatusEventData
                {
                    Time = DateTime.UtcNow,
                    CpuPercent = m_perfInfo.CpuUsagePercentage,
                    DiskPercents = m_perfInfo.DiskUsagePercentages ?? Array.Empty<int>(),
                    DiskQueueDepths = m_perfInfo.DiskQueueDepths ?? Array.Empty<int>(),
                    DiskAvailableSpaceGb = m_perfInfo.DiskAvailableSpaceGb ?? Array.Empty<int>(),
                    ProcessCpuPercent = m_perfInfo.ProcessCpuPercentage,
                    ProcessWorkingSetMB = m_perfInfo.ProcessWorkingSetMB,
                    RamPercent = m_perfInfo.RamUsagePercentage ?? 0,
                    RamUsedMb = (m_perfInfo.TotalRamMb.HasValue && m_perfInfo.AvailableRamMb.HasValue) ? m_perfInfo.TotalRamMb.Value - m_perfInfo.AvailableRamMb.Value : 0,
                    RamFreeMb = m_perfInfo.AvailableRamMb ?? 0,
                    CommitPercent = m_perfInfo.CommitUsagePercentage ?? 0,
                    CommitUsedMb = m_perfInfo.CommitUsedMb ?? 0,
                    CommitFreeMb = (m_perfInfo.CommitLimitMb.HasValue && m_perfInfo.CommitUsedMb.HasValue) ? m_perfInfo.CommitLimitMb.Value - m_perfInfo.CommitUsedMb.Value : 0,
                    CpuWaiting = PipQueue.GetNumQueuedByKind(DispatcherKind.CPU),
                    CpuRunning = PipQueue.GetNumAcquiredSlotsByKind(DispatcherKind.CPU),
                    CpuRunningPips = PipQueue.GetNumRunningPipsByKind(DispatcherKind.CPU),
                    IoCurrentMax = PipQueue.GetMaxParallelDegreeByKind(DispatcherKind.IO),
                    IoWaiting = PipQueue.GetNumQueuedByKind(DispatcherKind.IO),
                    IoRunning = PipQueue.GetNumAcquiredSlotsByKind(DispatcherKind.IO),
                    LookupWaiting = PipQueue.GetNumQueuedByKind(DispatcherKind.CacheLookup),
                    LookupRunning = PipQueue.GetNumAcquiredSlotsByKind(DispatcherKind.CacheLookup),
                    LimitingResource = limitingResource,
                    RunningPipExecutorProcesses = LocalWorker.RunningPipExecutorProcesses.Count,
                    RunningRemotelyPipExecutorProcesses = workerWithRemoting?.CurrentRunRemoteCount ?? 0,
                    RunningLocallyPipExecutorProcesses = workerWithRemoting?.CurrentRunLocalCount ?? 0,
                    TotalRunRemotelyProcesses = workerWithRemoting?.TotalRunRemote ?? 0,
                    TotalRunLocallyProcesses = workerWithRemoting?.TotalRunLocally ?? 0,
                    RunningProcesses = LocalWorker.RunningProcesses,
                    PipsSucceededAllTypes = m_pipStateCountersSnapshots.SelectArray(a => a.DoneCount),
                    UnresponsivenessFactor = m_unresponsivenessFactor,
                    ProcessPipsPending = numProcessPipsPending,
                    ProcessPipsAllocatedSlots = numProcessPipsAllocatedSlots
                };

                BuildXL.Tracing.Logger.Log.Status(m_executePhaseLoggingContext, m_statusRows.PrintRow(data));

                if (DateTime.UtcNow > m_statusSnapshotLastUpdated.AddSeconds(StatusSnapshotInterval))
                {
                    var snapshotData = m_statusRows.GetSnapshot(data);
                    BuildXL.Tracing.Logger.Log.StatusSnapshot(m_executePhaseLoggingContext, snapshotData);

                    m_statusSnapshotLastUpdated = DateTime.UtcNow;
                }

                if (DateTime.UtcNow > m_chooseWorkerCpuLastUnpaused.AddSeconds(ChooseWorkerCpuInterval))
                {
                    m_chooseWorkerCpuLastUnpaused = DateTime.UtcNow;
                    m_chooseWorkerCpu.TogglePauseChooseWorkerQueue(pause: false);
                }

                if (m_scheduleConfiguration.AdaptiveIO)
                {
                    Contract.Assert(m_performanceAggregator != null, "Adaptive IO requires non-null performanceAggregator");
                    PipQueue.AdjustIOParallelDegree(m_perfInfo);
                }

                if (m_configuration.Distribution.EarlyWorkerRelease && IsDistributedOrchestrator)
                {
                    PerformEarlyReleaseWorker(numProcessPipsPending, numProcessPipsAllocatedSlots);
                }

                if (m_configuration.Distribution.FireForgetMaterializeOutput() &&
                    m_materializeOutputsQueued &&
                    !AnyPendingPipsExceptMaterializeOutputs() &&
                    !m_schedulerCompletionExceptMaterializeOutputsTimeUtc.HasValue)
                {
                    // There are no pips running anything except materializeOutputs.
                    Logger.Log.SchedulerCompleteExceptMaterializeOutputs(m_loggingContext);
                    var maxMessages = (int)(EngineEnvironmentSettings.MaxMessagesPerBatch * EngineEnvironmentSettings.MaterializeOutputsBatchMultiplier);
                    foreach (var worker in m_remoteWorkers)
                    {
                        worker.MaxMessagesPerBatch = maxMessages;
                    }

                    m_schedulerCompletionExceptMaterializeOutputsTimeUtc = DateTime.UtcNow;
                }

                if (m_configuration.Logging.LogTracer && DateTime.UtcNow > m_tracerLastUpdated.AddSeconds(EngineEnvironmentSettings.MinStepDurationSecForTracer))
                {
                    LogPercentageCounter(LocalWorker, "CPU", data.CpuPercent, data.Time.Ticks);
                    LogPercentageCounter(LocalWorker, "RAM", data.RamPercent, data.Time.Ticks);
                    m_tracerLastUpdated = DateTime.UtcNow;
                }
            }
        }

        private void LogPercentageCounter(Worker worker, string name, int percentValue, long ticks)
        {
            if (worker.InitializedTracerCounters.TryAdd(name, 0))
            {
                // To show the counters nicely in the UI, we set percentage counters to 100 for very short time
                // so that UI aligns the rest based on 100% instead of the maximum observed value
                BuildXL.Tracing.Logger.Log.TracerCounterEvent(m_loggingContext, name, worker.Name, ticks, 100);
            }

            BuildXL.Tracing.Logger.Log.TracerCounterEvent(m_loggingContext, name, worker.Name, ticks, percentValue);
        }

        /// <summary>
        /// We have 2 versions of this message for the sake of letting one be overwriteable and the other not.
        /// Other than they should always stay identical. So to enforce that we have them reference the same
        /// set of attribute arguments and go through the same method
        /// </summary>
        public static void LogPipStatus(
            LoggingContext loggingContext,
            long pipsSucceeded,
            long pipsFailed,
            long pipsSkippedDueToFailedDependencies,
            long pipsRunning,
            long pipsReady,
            long pipsWaiting,
            long pipsWaitingOnSemaphore,
            long servicePipsRunning,
            string perfInfoForConsole,
            long pipsWaitingOnResources,
            long procsExecuting,
            long procsSucceeded,
            long procsFailed,
            long procsSkippedDueToFailedDependencies,
            long procsPending,
            long procsWaiting,
            long procsCacheHit,
            long procsNotIgnored,
            string limitingResource,
            string perfInfoForLog,
            bool overwriteable,
            long copyFileDone,
            long copyFileNotDone,
            long writeFileDone,
            long writeFileNotDone,
            long procsRemoted)
        {
            // Noop if no process information is included. This can happen for the last status event in a build using
            // incremental scheduling if it goes through the codepath where zero files changed. All other codepaths
            // compute the actual process count and can be logged
            if (procsExecuting + procsSucceeded + procsFailed + procsSkippedDueToFailedDependencies + procsPending + procsWaiting + procsCacheHit == 0)
            {
                return;
            }

            if (overwriteable)
            {
                Logger.Log.PipStatus(
                    loggingContext,
                    pipsSucceeded,
                    pipsFailed,
                    pipsSkippedDueToFailedDependencies,
                    pipsRunning,
                    pipsReady,
                    pipsWaiting,
                    pipsWaitingOnSemaphore,
                    servicePipsRunning,
                    perfInfoForConsole,
                    pipsWaitingOnResources,
                    procsExecuting,
                    procsSucceeded,
                    procsFailed,
                    procsSkippedDueToFailedDependencies,
                    procsPending,
                    procsWaiting,
                    procsCacheHit,
                    procsNotIgnored,
                    limitingResource,
                    perfInfoForLog,
                    copyFileDone,
                    copyFileNotDone,
                    writeFileDone,
                    writeFileNotDone,
                    procsRemoted);
            }
            else
            {
                Logger.Log.PipStatusNonOverwriteable(
                    loggingContext,
                    pipsSucceeded,
                    pipsFailed,
                    pipsSkippedDueToFailedDependencies,
                    pipsRunning,
                    pipsReady,
                    pipsWaiting,
                    pipsWaitingOnSemaphore,
                    servicePipsRunning,
                    perfInfoForConsole,
                    pipsWaitingOnResources,
                    procsExecuting,
                    procsSucceeded,
                    procsFailed,
                    procsSkippedDueToFailedDependencies,
                    procsPending,
                    procsWaiting,
                    procsCacheHit,
                    procsNotIgnored,
                    limitingResource,
                    perfInfoForLog,
                    copyFileDone,
                    copyFileNotDone,
                    writeFileDone,
                    writeFileNotDone,
                    procsRemoted);
            }
        }

        /// <summary>
        /// Decide whether we can release a remote worker. This method is executed every 2 seconds depending on the frequency of LogStatus timer.
        /// </summary>
        private void PerformEarlyReleaseWorker(long numProcessPipsPending, long numProcessPipsAllocatedSlots)
        {
            if (Workers.Where(w => w.EverAvailable).Count() < m_configuration.Distribution.MinimumWorkers)
            {
                // Don't release if minimum workers is not satisfied
                return;
            }

            if (m_chooseWorkerCpu.LastConcurrencyLimiter != null)
            {
                // If there is a resource limiting the scheduler, we should not release any worker.
                return;
            }

            long numProcessPipsWaiting = numProcessPipsPending - numProcessPipsAllocatedSlots;

            // Try releasing the remote worker which has the lowest acquired slots for process execution.
            // It is intentional that we do not include cachelookup slots here as cachelookup step is a lot faster than execute step.
            var workerToReleaseCandidate = m_remoteWorkers.Where(workerIsReleasable).OrderBy(a => a.AcquiredProcessSlots).FirstOrDefault();
            if (workerToReleaseCandidate == null)
            {
                return;
            }

            // If the available remote workers perform at that multiplier capacity in future, how many process pips we can concurrently execute:
            int totalProcessSlots = LocalWorker.TotalProcessSlots +
               (int)Math.Ceiling(m_configuration.Distribution.EarlyWorkerReleaseMultiplier * m_remoteWorkers.Where(a => a.IsAvailable).Sum(a => a.TotalProcessSlots));

            // Release worker if numProcessPipsWaiting can be satisfied by remaining workers
            if (numProcessPipsWaiting >= 0 && numProcessPipsWaiting < totalProcessSlots - workerToReleaseCandidate.TotalProcessSlots)
            {
                Logger.Log.InitiateWorkerRelease(
                        m_loggingContext,
                        workerToReleaseCandidate.Name,
                        numProcessPipsWaiting,
                        totalProcessSlots,
                        workerToReleaseCandidate.AcquiredCacheLookupSlots,
                        workerToReleaseCandidate.AcquiredProcessSlots,
                        workerToReleaseCandidate.AcquiredIpcSlots);

                var task = workerToReleaseCandidate.EarlyReleaseAsync();
                Analysis.IgnoreResult(task);
            }

            bool workerIsReleasable(RemoteWorkerBase w)
            {
                // Candidates for early release are remote workers that
                //   1. Are available, or
                //   2. Dynamic unknown workers (never connected, attached)
                // We also filter out the ones that were already picked for early release
                return w.IsRemote && !w.IsEarlyReleaseInitiated &&
                    (w.IsAvailable ||
                     w.IsUnknownDynamic);
            }
        }

        /// <summary>
        /// Compares the time the UpdateStatus timer was invoked against how it was configured as a proxy to how unresponsive the machine is.
        /// </summary>
        /// <returns>A value of 1 means the timer is as often as expected. 2 would be twice as slowly as expected. etc.</returns>
        internal static int ComputeUnresponsivenessFactor(int expectedCallbackFrequencyMs, DateTime statusLastCollected, DateTime currentTime)
        {
            if (expectedCallbackFrequencyMs > 0)
            {
                TimeSpan timeSinceLastUpdate = currentTime - statusLastCollected;
                if (timeSinceLastUpdate.TotalMilliseconds > 0)
                {
                    return (int)(timeSinceLastUpdate.TotalMilliseconds / expectedCallbackFrequencyMs);
                }
            }

            return 0;
        }

        private void UpdateResourceAvailability(PerformanceCollector.MachinePerfInfo perfInfo)
        {
            var resourceManager = State.ResourceManager;
            resourceManager.RefreshMemoryCounters();

            ManageMemoryMode defaultManageMemoryMode = m_scheduleConfiguration.GetManageMemoryMode();
            MemoryResource memoryResource = MemoryResource.Available;

            // RAM (WORKINGSET) USAGE.
            // If ram resources are not available, the scheduler is throttled (effectiveprocessslots becoming 1) and
            // we cancel the running ones.

            if (LocalWorker.TotalRamMb == null && m_perfInfo.AvailableRamMb.HasValue)
            {
                // TotalRam represent the available size at the beginning of the build.
                // Because graph construction can consume a large memory as a part of BuildXL process,
                // we add ProcessWorkingSetMb to the current available ram.
                LocalWorker.TotalRamMb = m_perfInfo.AvailableRamMb + m_perfInfo.ProcessWorkingSetMB;
            }

            // Allow increases to the worker's total installed ram over the course of the build. This may happen if the build
            // is running on a virtual machine with dynamic memory. We do not model shrinking of installed ram during the build
            // because that will interfere with the process working set adjustment above.
            if (m_perfInfo.TotalRamMb.HasValue && LocalWorker.TotalRamMb.HasValue && LocalWorker.TotalRamMb.Value < m_perfInfo.TotalRamMb.Value)
            {
                LocalWorker.TotalRamMb = m_perfInfo.TotalRamMb;
            }

            if (perfInfo.RamUsagePercentage != null)
            {
                // This is the calculation for the low memory perf smell. This is somewhat of a check against how effective
                // the throttling is. It happens regardless of the throttling limits and is logged when we're pretty
                // sure there is a ram problem
                bool isAvailableRamCritical = perfInfo.AvailableRamMb.Value < 100 || perfInfo.RamUsagePercentage.Value >= 98;
                if (isAvailableRamCritical)
                {
                    PipExecutionCounters.IncrementCounter(PipExecutorCounter.CriticalLowRamMemory);

                    if (!m_hitLowRamMemoryPerfSmell)
                    {
                        m_hitLowRamMemoryPerfSmell = true;
                        // Log the perf smell at the time that it happens since the machine is likely going to get very
                        // bogged down and we want to make sure this gets sent to telemetry before the build is killed.
                        Logger.Log.LowRamMemory(m_executePhaseLoggingContext, perfInfo.AvailableRamMb.Value, perfInfo.RamUsagePercentage.Value);
                    }
                }

                bool exceededMaxRamUtilizationPercentage = perfInfo.EffectiveRamUsagePercentage.Value > m_configuration.Schedule.MaximumRamUtilizationPercentage;
                if (exceededMaxRamUtilizationPercentage)
                {
                    memoryResource |= MemoryResource.LowRam;
                }
                else if (isAvailableRamCritical && perfInfo.ModifiedPagelistPercentage > 50)
                {
                    // Ram >= 98% and ModifiedPageSet > 50%
                    // Thrashing is an issue - try to cancel suspended processes, if any,
                    // or even running processes to alleviate the pressure.
                    memoryResource |= MemoryResource.LowRam;
                    defaultManageMemoryMode = ManageMemoryMode.CancelSuspendedFirst;
                }
            }

            /*
             * How COMMIT MEMORY works:
             * Committed Memory is the number of bytes allocated by processes when the OS stores a page frame (from physical memory) or a page slot (from logical/virtual memory) or both into the page file.
             * Process reserves a series of memory addresses (sometimes more that it currently requires, to control a contiguous block of memory)
             * Reserved memory does not necessarily represent real space in the physical memory (RAM) or on disk and a process can reserve more memory that available on the system.
             * To become usable, the memory address needs to correspond to byte space in memory (physical or disk).
             * Commit memory is the association between this reserved memory and it�s physical address (RAM or disk) causing them to be unavailable to other processes in most cases.
             * Since commit memory is a combination of the physical memory and the page file on disk, the used committed memory can exceed the physical memory available to the operating system.
             */

            // If commit memory usage is high, the scheduler is throttled without cancelling any pips.
            if (m_perfInfo.CommitLimitMb.HasValue)
            {
                LocalWorker.TotalCommitMb = m_perfInfo.CommitLimitMb.Value;
            }
            else if (LocalWorker.TotalCommitMb == null)
            {
                // If we cannot get commit usage for Windows, or it is the MacOS, we do not track of swap file usage.
                // That's why, we set it to very high number to disable throttling.
                LocalWorker.TotalCommitMb = int.MaxValue;
            }

            bool isCommitCriticalLevel = false;
            if (perfInfo.CommitUsagePercentage != null)
            {
                int availableCommit = m_perfInfo.CommitLimitMb.Value - m_perfInfo.CommitUsedMb.Value;

                if (perfInfo.CommitUsagePercentage.Value >= m_configuration.Schedule.CriticalCommitUtilizationPercentage)
                {
                    isCommitCriticalLevel = true;
                    PipExecutionCounters.IncrementCounter(PipExecutorCounter.CriticalLowCommitMemory);

                    if (!m_hitLowCommitMemoryPerfSmell)
                    {
                        m_hitLowCommitMemoryPerfSmell = true;
                        Logger.Log.LowCommitMemory(m_executePhaseLoggingContext, availableCommit, perfInfo.CommitUsagePercentage.Value);
                    }
                }

                // By default, MaximumCommitUtilizationPercentage is 95%.
                bool exceededMaxCommitUtilizationPercentage = perfInfo.CommitUsagePercentage.Value > m_configuration.Schedule.MaximumCommitUtilizationPercentage;

                if (exceededMaxCommitUtilizationPercentage)
                {
                    memoryResource |= MemoryResource.LowCommit;
                }
            }

            bool cpuResourceAvailable = true;
            if (m_scheduleConfiguration.CpuResourceAware)
            {
                // More than 5000 context switches per second per core can cause congestion issues. 
                cpuResourceAvailable = !(perfInfo.CpuWMIUsagePercentage >= 98 && perfInfo.ContextSwitchesPerSec > (Environment.ProcessorCount * 5000));
            }

            ToggleResourceAvailability(perfInfo, memoryResource, cpuResourceAvailable);

            resourceManager.LastRequiredSizeMb = 0;
            resourceManager.LastManageMemoryMode = null;

            if (isCommitCriticalLevel)
            {
                // If commit usage is at the critical level (>= 98% by default), cancel pips to avoid out-of-page file errors.
                int desiredCommitPercentToFreeSlack = EngineEnvironmentSettings.DesiredCommitPercentToFreeSlack.Value ?? 0;

                // e.g., 98-95 = 3 + slack
                int desiredCommitPercentToFree = (perfInfo.CommitUsagePercentage.Value - m_configuration.Schedule.MaximumCommitUtilizationPercentage) + desiredCommitPercentToFreeSlack;

                // Ensure percentage to free is in valid percent range [0, 100]
                desiredCommitPercentToFree = Math.Max(0, Math.Min(100, desiredCommitPercentToFree));

                // Get the megabytes to free
                var desiredCommitMbToFree = (perfInfo.CommitLimitMb.Value * desiredCommitPercentToFree) / 100;

                resourceManager.TryManageResources(desiredCommitMbToFree, ManageMemoryMode.CancellationCommit);
            }
            else if (memoryResource.HasFlag(MemoryResource.LowRam))
            {
#if PLATFORM_OSX
                bool simulateHighMemory = m_testHooks?.SimulateHighMemoryPressure ?? false;
                Memory.PressureLevel pressureLevel = simulateHighMemory ? Memory.PressureLevel.Critical : Memory.PressureLevel.Normal;
                var result = simulateHighMemory ? true : Memory.GetMemoryPressureLevel(ref pressureLevel) == Dispatch.MACOS_INTEROP_SUCCESS;
                var startCancellingPips = false;

                if (result)
                {
                    // If the memory pressure level is above the configured level we start canceling pips to avoid Jetsam to kill our process
                    startCancellingPips = pressureLevel > m_configuration.Schedule.MaximumAllowedMemoryPressureLevel;
                }
                else
                {
                    Logger.Log.UnableToGetMemoryPressureLevel(
                            m_executePhaseLoggingContext,
                            availableRam: perfInfo.AvailableRamMb.Value,
                            ramUtilization: perfInfo.RamUsagePercentage.Value,
                            maximumRamUtilization: m_configuration.Schedule.MaximumRamUtilizationPercentage);
                }

                // CancellationRam is the only mode for OSX.
                defaultManageMemoryMode = ManageMemoryMode.CancellationRam;

                if (!m_scheduleConfiguration.DisableProcessRetryOnResourceExhaustion && startCancellingPips)
#else
                // We only retry when the ram memory is not available.
                // When commit memory is not available, we stop scheduling; but we do not cancel the currently running ones
                // because OS can resize the commit memory.
                if (!m_scheduleConfiguration.DisableProcessRetryOnResourceExhaustion)
#endif
                {
                    int desiredRamPercentToFreeSlack = EngineEnvironmentSettings.DesiredRamPercentToFreeSlack.Value ?? 5;

                    // Free down to the specified max RAM utilization percentage with some slack
                    int desiredRamPercentToFree = (perfInfo.EffectiveRamUsagePercentage.Value - m_configuration.Schedule.MaximumRamUtilizationPercentage) + desiredRamPercentToFreeSlack;

                    // Ensure percentage to free is in valid percent range [0, 100]
                    desiredRamPercentToFree = Math.Max(0, Math.Min(100, desiredRamPercentToFree));

                    // Get the megabytes to free, at least 1MB so that we can suspend/cancel/emptyWorkingSet one pip
                    var desiredRamMbToFree = Math.Max(1, (perfInfo.TotalRamMb.Value * desiredRamPercentToFree) / 100);

                    resourceManager.TryManageResources(desiredRamMbToFree, defaultManageMemoryMode);
                }
            }
            else if (perfInfo.RamUsagePercentage.HasValue
                    && m_configuration.Schedule.MaximumRamUtilizationPercentage > perfInfo.RamUsagePercentage.Value
                    && resourceManager.NumSuspended > 0)
            {
                // Use EffectiveAvailableRam when to throttle the scheduler and cancel more.

                // We might use the actual available ram to resume though.
                // If there is available ram, then resume any suspended pips.
                // 90% memory - current percent = availableRamForResume
                // When it is resumed, start from the larger execution time.

                var desiredRamPercentToUse = m_configuration.Schedule.MaximumRamUtilizationPercentage - perfInfo.RamUsagePercentage.Value;

                // Ensure percentage is in valid percent range [0, 100]
                desiredRamPercentToUse = Math.Max(0, Math.Min(100, desiredRamPercentToUse));

                // Get the megabytes to free
                var desiredRamMbToUse = (perfInfo.TotalRamMb.Value * desiredRamPercentToUse) / 100;

                resourceManager.TryManageResources(desiredRamMbToUse, ManageMemoryMode.Resume);
            }

            if (resourceManager.NumActive == 0 && resourceManager.NumSuspended > 0)
            {
                // Maybe something changed with our previous actions (we refreshed this number before potentially resuming some processes).
                // Before taking drastic action, refresh the counters.
                resourceManager.RefreshMemoryCounters();

                if (resourceManager.NumActive == 0 && resourceManager.NumSuspended > 0)
                {
                    // There are no active process pips running, cancel one pip to check whether the scheduler will move forward.
                    PipExecutionCounters.IncrementCounter(PipExecutorCounter.CancelSuspendedPipDueToNoRunningProcess);
                    resourceManager.TryManageResources(1, ManageMemoryMode.CancellationRam);
                }
            }

            // Log an internal warning if the number of open file descriptors (by the BuildXL process) is greater than 10k
            // The threshold is arbitrary but:
            // - conservative: based on telemetry, BuildXL having more than 1000 file descriptors open is an anomaly
            // - low enough that we have a chance to measure and log this warning (if we're going overboard with file descriptors, all sorts of operations start failing)
            const int FileDescriptorCountThreshold = 10_000;
            if (!m_hitHighFileDescriptorUsagePerfSmell && m_perfInfo.MachineOpenFileDescriptors > FileDescriptorCountThreshold)
            {
                m_hitHighFileDescriptorUsagePerfSmell = true;
                Logger.Log.HighFileDescriptorCount(m_executePhaseLoggingContext, perfInfo.MachineOpenFileDescriptors, FileDescriptorCountThreshold);
            }
        }

        private void ToggleResourceAvailability(PerformanceCollector.MachinePerfInfo perfInfo, MemoryResource memoryResource, bool cpuResourceAvailable)
        {
            if (memoryResource.HasFlag(MemoryResource.LowRam))
            {
                PipExecutionCounters.IncrementCounter(PipExecutorCounter.MemoryResourceBecomeUnavailableDueToRam);
            }

            if (memoryResource.HasFlag(MemoryResource.LowCommit))
            {
                PipExecutionCounters.IncrementCounter(PipExecutorCounter.MemoryResourceBecomeUnavailableDueToCommit);
            }

            if (memoryResource == MemoryResource.Available && !LocalWorker.MemoryResourceAvailable)
            {
                // Set resources to available to allow executing further work
                Logger.Log.ResumingProcessExecutionAfterSufficientResources(m_executePhaseLoggingContext,
                    availableRam: perfInfo.AvailableRamMb ?? 0,
                    effectiveAvailableRam: perfInfo.EffectiveAvailableRamMb ?? 0,
                    ramUtilization: perfInfo.RamUsagePercentage ?? 0,
                    effectiveRamUtilization: perfInfo.EffectiveRamUsagePercentage ?? 0,
                    maximumRamUtilization: m_configuration.Schedule.MaximumRamUtilizationPercentage);

                LocalWorker.MemoryResource = memoryResource;
            }

            if (memoryResource != MemoryResource.Available && LocalWorker.MemoryResourceAvailable)
            {
                // Set resources to unavailable to prevent executing further work
                Logger.Log.StoppingProcessExecutionDueToMemory(
                    m_executePhaseLoggingContext,
                    reason: memoryResource.ToString(),
                    availableRam: perfInfo.AvailableRamMb ?? 0,
                    ramUtilization: perfInfo.RamUsagePercentage ?? 0,
                    maximumRamUtilization: m_configuration.Schedule.MaximumRamUtilizationPercentage,
                    commitUtilization: perfInfo.CommitUsagePercentage ?? 0,
                    maximumCommitUtilization: m_configuration.Schedule.MaximumCommitUtilizationPercentage);

                LocalWorker.MemoryResource = memoryResource;
            }

            LocalWorker.CpuResourceAvailable = cpuResourceAvailable;

            PipQueue.SetMaxParallelDegreeByKind(DispatcherKind.CPU, LocalWorker.TotalProcessSlots);

        }

        /// <summary>
        /// Allows the scheduler to be externally signaled for termination in the case of an internal or infrastructure error.
        /// </summary>
        public void TerminateForInternalError()
        {
            if (!IsTerminating && !IsDistributedWorker)
            {
                Logger.Log.TerminatingDueToInternalError(m_executePhaseLoggingContext);
                m_scheduleTerminatingWithInternalError = true;
                RequestTermination(cancelQueue: true, cancelRunningPips: true, cancelQueueTimeout: TimeSpan.FromMinutes(2));

                // Early-released workers can prevent the build from terminating if we do not complete DrainCompletion task source. 
                foreach (var worker in m_remoteWorkers)
                {
                    worker.DrainCompletion.TrySetResult(false);
                }
            }
        }

        /// <summary>
        /// Callback event that gets raised when a Pip finished executing
        /// </summary>
        /// <remarks>
        /// Multiple events may be fired concurrently. <code>WhenDone</code> will only complete when all event handlers have
        /// returned.
        /// The event handler should do minimal work, as the queue won't re-use the slot before the event handler returns.
        /// Any exception leaked by the event handler may terminate the process.
        /// </remarks>
        public virtual async Task OnPipCompleted(RunnablePip runnablePip)
        {
            Contract.Requires(runnablePip != null);

            // Don't perform any completion work or bookkeeping if the scheduler is shutting down aggressively with an internal error.
            // This allows both a faster shutdown and also prevents interaction with objects that may be torn down.
            // This also prevents scheduling downstream pips which aids in speeding up shutdown
            if (m_scheduleTerminatingWithInternalError)
            {
                return;
            }

            runnablePip.Performance.Completed();
            var pipLoggingContext = runnablePip.LoggingContext;
            var pip = runnablePip.Pip;
            string pipDescription = runnablePip.Description;
            if (!runnablePip.Result.HasValue)
            {
                // This should happen only in case of cancellation
                Contract.Assert(runnablePip.IsCancelled, "Runnable pip should always have a result unless it was cancelled");
                return;
            }

            if (runnablePip.Performance.RetryCountOnRemoteWorkers < m_pipRetryCountersDueToNetworkFailures.Length)
            {
                m_pipRetryCountersDueToNetworkFailures[runnablePip.Performance.RetryCountOnRemoteWorkers]++;
            }

            if (runnablePip.Performance.RetryCountDueToLowMemory > 0)
            {
                m_pipRetryCountersDueToLowMemory.AddOrUpdate(runnablePip.Performance.RetryCountDueToLowMemory, 1, (id, count) => count + 1);
            }

            using (runnablePip.OperationContext.StartOperation(PipExecutorCounter.OnPipCompletedDuration))
            {
                var result = runnablePip.Result.Value;

                // Queued pip tasks are supposed to return a bool indicating success or failure.
                // Any exception (even a BuildXLException) captured by the task is considered a terminating error,
                // since that indicates a bug in the PipRunner implementation. Consequently, we access Result,
                // which may throw (rather than checking fault status first).
                LogEventPipEnd(pipLoggingContext, pip, result.Status, result.PerformanceInfo == null ? 0 : DateTime.UtcNow.Ticks - result.PerformanceInfo.ExecutionStart.Ticks);

                Contract.Assert((result.PerformanceInfo == null) == !result.Status.IndicatesExecution());

                bool succeeded = !result.Status.IndicatesFailure();
                bool skipped = result.Status == PipResultStatus.Skipped;
                PipId pipId = pip.PipId;
                PipType pipType = pip.PipType;
                var nodeId = pipId.ToNodeId();

                PipRuntimeInfo pipRuntimeInfo = GetPipRuntimeInfo(pipId);

                if (pipType == PipType.Process)
                {
                    CleanTempDirs(runnablePip);

                    // Don't count service pips in process pip counters
                    var processRunnablePip = runnablePip as ProcessRunnablePip;
                    if (!processRunnablePip.Process.IsStartOrShutdownKind)
                    {
                        var processDuration = runnablePip.RunningTime;
                        PipExecutionCounters.AddToCounter(PipExecutorCounter.ProcessDuration, processDuration);
                        m_groupedPipCounters.IncrementCounter(processRunnablePip.Process, PipCountersByGroup.Count);
                        m_groupedPipCounters.AddToCounter(processRunnablePip.Process, PipCountersByGroup.ProcessDuration, processDuration);

                        if (!succeeded && result.Status == PipResultStatus.Failed)
                        {
                            m_groupedPipCounters.IncrementCounter(processRunnablePip.Process, PipCountersByGroup.Failed);
                        }
                    }

                    // Keep logging the process stats near the Pip's state transition so we minimize having inconsistent
                    // stats like having more cache hits than completed process pips
                    LogProcessStats(runnablePip);
                }
                else if (pipType == PipType.Ipc)
                {
                    Interlocked.Increment(ref m_numIpcPipsCompleted);
                }

                if (!IsDistributedWorker && m_configuration.Schedule.InputChanges.IsValid && (pipType == PipType.CopyFile || pipType == PipType.Process))
                {
                    ReadOnlyArray<FileArtifact> outputContents = ReadOnlyArray<FileArtifact>.Empty;
                    PipResultStatus status = result.Status;

                    if (pipType == PipType.CopyFile)
                    {
                        outputContents = new[] { ((CopyFile)runnablePip.Pip).Destination }.ToReadOnlyArray();
                    }
                    else if (runnablePip.ExecutionResult?.OutputContent != null)
                    {
                        outputContents = runnablePip.ExecutionResult.OutputContent.SelectList(o => o.fileArtifact).ToReadOnlyArray();
                    }

                    m_fileContentManager.SourceChangeAffectedInputs.ReportSourceChangeAffectedFiles(
                        pip,
                        result.DynamicallyObservedFiles,
                        outputContents);
                }

                if (!succeeded)
                {
                    m_hasFailures = true;

                    if (result.Status == PipResultStatus.Failed)
                    {
                        if (pipRuntimeInfo.State != PipState.Running)
                        {
                            Contract.Assume(false, "Prior state assumed to be Running. Was: " + pipRuntimeInfo.State.ToString());
                        }

                        pipRuntimeInfo.Transition(m_pipStateCounters, pipType, PipState.Failed);
                    }
                    else if (result.Status == PipResultStatus.Canceled)
                    {
                        if (pipRuntimeInfo.State != PipState.Running)
                        {
                            Contract.Assume(false, $"Prior state assumed to be {nameof(PipState.Running)}. Was: {pipRuntimeInfo.State}");
                        }

                        pipRuntimeInfo.Transition(m_pipStateCounters, pipType, PipState.Canceled);
                    }
                    else
                    {
                        Contract.Assume(false, "Unhandled failed PipResult");
                        return;
                    }
                }
                else if (skipped)
                {
                    // No state transition in this case (already terminal)
                    if (pipRuntimeInfo.State != PipState.Skipped)
                    {
                        Contract.Assume(false, "Prior state assumed to be skipped. Was: " + pipRuntimeInfo.State.ToString());
                    }
                }
                else
                {
                    Contract.Assert(
                        result.Status == PipResultStatus.DeployedFromCache ||
                        result.Status == PipResultStatus.UpToDate ||
                        result.Status == PipResultStatus.Succeeded ||
                        result.Status == PipResultStatus.NotMaterialized, $"{result.Status} should not be here at this point");

                    pipRuntimeInfo.Transition(
                        m_pipStateCounters,
                        pipType,
                        PipState.Done);
                }

                Contract.Assume(pipRuntimeInfo.State.IndicatesFailure() == !succeeded);
                Contract.Assume(pipRuntimeInfo.RefCount == 0 || /* due to output materialization */ pipRuntimeInfo.RefCount == CompletedRefCount);

                // A pip was executed, but then it doesn't materialize its outputs due to lazy materialization.
                // Then, the pip may get executed again to materialize its outputs. For that pip, wasAlreadyCompleted is true.
                var wasAlreadyCompleted = pipRuntimeInfo.RefCount == CompletedRefCount;

                if (!wasAlreadyCompleted)
                {
                    pipRuntimeInfo.RefCount = CompletedRefCount;
                }

                // Possibly begin tearing down the schedule (without executing all pips) on failure.
                // This happens before we traverse the dependents of this failed pip; since SchedulePipIfReady
                // checks m_scheduleTerminating this means that there will not be 'Skipped' pips in this mode
                // (they remain unscheduled entirely).
                if (!succeeded && !IsDistributedWorker)
                {
                    // We stop on the first error only on the orchestrator or single-machine builds.
                    // During cancellation, orchestrator coordinates with workers to stop the build.

                    // Early terminate the build if stopOnFirstError is enabled
                    if (!IsTerminating && m_scheduleConfiguration.StopOnFirstError)
                    {
                        Logger.Log.ScheduleTerminatingDueToPipFailure(m_executePhaseLoggingContext, pipDescription);

                        RequestTermination(cancelQueue: false);
                    }

                    Contract.Assert(m_executePhaseLoggingContext.ErrorWasLogged, $"Should have logged error for pip: {pipDescription}");
                }

                if (!succeeded && !m_executePhaseLoggingContext.ErrorWasLogged)
                {
                    Contract.Assert(
                        false,
                        I($"Pip failed but no error was logged. Failure kind: {result.Status}. Look through the log for other messages related to this pip: {pipDescription}"));
                }

                if (!wasAlreadyCompleted)
                {
                    if (succeeded && !skipped)
                    {
                        // Incremental scheduling: On success, a pip is 'clean' in that we know its outputs are up to date w.r.t. its inputs.
                        // When incrementally scheduling on the next build, we can skip this pip altogether unless it or a dependency have become dirty (due to file changes).
                        // However, if the pip itself is clean-materialized, then the pip enters this completion method through the CheckIncrementalSkip step.
                        // In that case, the incremental scheduling state should not be modified.
                        if (IncrementalSchedulingState != null && !IsPipCleanMaterialized(pipId))
                        {
                            using (runnablePip.OperationContext.StartOperation(PipExecutorCounter.UpdateIncrementalSchedulingStateDuration))
                            {
                                // TODO: Should IPC pips always be marked perpetually dirty?
                                if (result.MustBeConsideredPerpetuallyDirty)
                                {
                                    IncrementalSchedulingState.PendingUpdates.MarkNodePerpetuallyDirty(nodeId);
                                    PipExecutionCounters.IncrementCounter(PipExecutorCounter.PipMarkPerpetuallyDirty);
                                    Logger.Log.PipIsPerpetuallyDirty(m_executePhaseLoggingContext, pipDescription);
                                }
                                else
                                {
                                    IncrementalSchedulingState.PendingUpdates.MarkNodeClean(nodeId);
                                    PipExecutionCounters.IncrementCounter(PipExecutorCounter.PipMarkClean);
                                    Logger.Log.PipIsMarkedClean(m_executePhaseLoggingContext, pipDescription);
                                }

                                // The pip is clean, but it may have not materialized its outputs, so we track that fact as well.
                                if (result.Status != PipResultStatus.NotMaterialized)
                                {
                                    IncrementalSchedulingState.PendingUpdates.MarkNodeMaterialized(nodeId);
                                    PipExecutionCounters.IncrementCounter(PipExecutorCounter.PipMarkMaterialized);
                                    Logger.Log.PipIsMarkedMaterialized(m_executePhaseLoggingContext, pipDescription);
                                }
                                else
                                {
                                    // Track non materialized pip.
                                    m_pipOutputMaterializationTracker.AddNonMaterializedPip(pip);
                                }
                            }
                        }

                        if (IncrementalSchedulingState != null && !ShouldIncrementalSkip(pipId))
                        {
                            using (runnablePip.OperationContext.StartOperation(PipExecutorCounter.UpdateIncrementalSchedulingStateDuration))
                            {
                                // Record dynamic observation outside lock.
                                if (pipType == PipType.Process)
                                {
                                    var processPip = (Process)pip;

                                    if (result.HasDynamicObservations || processPip.DirectoryOutputs.Length > 0)
                                    {
                                        using (runnablePip.OperationContext.StartOperation(PipExecutorCounter.RecordDynamicObservationsDuration))
                                        {
                                            // Note that we don't include untracked paths when recoring dynamic observation for output
                                            // directories. This is because m_fileContentManager.ListSealedDirectoryContents(d) contains
                                            // only tracked paths. See m_fileContentManager.EnumerateOutputDirectory method for details.
                                            // Also see the documentation of RecordDynamicObservations for details why untracked scopes
                                            // are still needed for recording dynamic observation into incremental scheduling state.
                                            IncrementalSchedulingState.RecordDynamicObservations(
                                                nodeId,
                                                result.DynamicallyObservedFiles.Select(path => path.ToString(Context.PathTable)),
                                                result.DynamicallyProbedFiles.Select(path => path.ToString(Context.PathTable)),
                                                result.DynamicallyObservedEnumerations.Select(path => path.ToString(Context.PathTable)),
                                                result.DynamicallyObservedAbsentPathProbes.Select(path => path.ToString(Context.PathTable)),
                                                processPip.DirectoryOutputs.Select(
                                                    d =>
                                                        (
                                                            d.Path.ToString(Context.PathTable),
                                                            m_fileContentManager.ListSealedDirectoryContents(d)
                                                                .Select(f => f.Path.ToString(Context.PathTable)))),
                                                processPip.UntrackedScopes.Select(p => p.ToString(Context.PathTable)));
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // The pip has been completed. We log perf info and opaque directory outputs at most once per pip.
                    if (result.Status.IndicatesExecution() && !pipType.IsMetaPip())
                    {
                        Contract.Assert(result.PerformanceInfo != null);
                        HandleExecutionPerformance(runnablePip, result.PerformanceInfo);

                        if (pipType == PipType.Process)
                        {
                            ReportOpaqueOutputs(runnablePip);
                        }
                    }

                    if (pipType == PipType.Process)
                    {
                        ProcessPipExecutionPerformance processPerformanceResult = result.PerformanceInfo as ProcessPipExecutionPerformance;
                        // Determine the uncacheability impact. There are 3 cases here:
                        if (processPerformanceResult != null)
                        {
                            // 1. The pip ran and had uncacheable file accesses. We set the flag that it is UncacheableImpacted
                            if (processPerformanceResult.FileMonitoringViolations.HasUncacheableFileAccesses)
                            {
                                pipRuntimeInfo.IsUncacheableImpacted = true;
                            }
                            else
                            {
                                // 2. The pip ran but didn't have uncacheable file accesses. We don't know conclusively whether
                                // it ran because it had an uncacheable parent that wasn't deterministic or if a direct input
                                // also changed causing it to run. That's splitting hairs so we leave the flag as-is
                            }
                        }
                        else
                        {
                            // 3. We may have marked this pip as being impacted by uncacheability when it was scheduled if it
                            // depended on an uncacheable pip. But if the uncacheable process was deterministic, this pip may
                            // have actually been a cache hit. In that case we reset the flag
                            pipRuntimeInfo.IsUncacheableImpacted = false;

                            // Similar logic applies to IsMissingContentImpacted.
                            pipRuntimeInfo.IsMissingContentImpacted = false;
                        }

                        // Now increment the counters for uncacheability
                        if (pipRuntimeInfo.IsUncacheableImpacted)
                        {
                            PipExecutionCounters.IncrementCounter(PipExecutorCounter.ProcessPipsUncacheableImpacted);
                            PipExecutionCounters.AddToCounter(
                                PipExecutorCounter.ProcessPipsUncacheableImpactedDurationMs,
                                (long)(result.PerformanceInfo.ExecutionStop - result.PerformanceInfo.ExecutionStart).TotalMilliseconds);
                            Logger.Log.ProcessDescendantOfUncacheable(
                                m_executePhaseLoggingContext,
                                pipDescription: pipDescription);
                        }

                        if (pipRuntimeInfo.IsMissingContentImpacted)
                        {
                            PipExecutionCounters.IncrementCounter(PipExecutorCounter.ProcessPipMissingContentImpacted);
                            PipExecutionCounters.AddToCounter(
                                PipExecutorCounter.ProcessPipMissingContentImpactedDurationMs,
                                (long)(result.PerformanceInfo.ExecutionStop - result.PerformanceInfo.ExecutionStart).TotalMilliseconds);
                        }
                    }
                }

                // Schedule the dependent pips
                if (!wasAlreadyCompleted)
                {
                    using (runnablePip.OperationContext.StartOperation(PipExecutorCounter.ScheduleDependentsDuration))
                    {
                        await ScheduleDependents(result, succeeded, runnablePip, pipRuntimeInfo);
                    }
                }

                // Report pip completed to DropTracker
                m_servicePipTracker?.ReportPipCompleted(pip);
            }
        }

        private void ReportOpaqueOutputs(RunnablePip runnablePip)
        {
            var executionResult = runnablePip.ExecutionResult;
            // The execution result can be null for some tests
            if (executionResult == null)
            {
                return;
            }

            var directoryOutputs = executionResult.DirectoryOutputs.Select(tuple =>
                (tuple.directoryArtifact, ReadOnlyArray<FileArtifact>.From(tuple.fileArtifactArray.Select(faa => faa.ToFileArtifact()))));
            ExecutionLog?.PipExecutionDirectoryOutputs(new PipExecutionDirectoryOutputs
            {
                PipId = runnablePip.PipId,
                DirectoryOutputs = ReadOnlyArray<(DirectoryArtifact, ReadOnlyArray<FileArtifact>)>.From(directoryOutputs),
            });
        }

        private async Task ScheduleDependents(PipResult result, bool succeeded, RunnablePip runnablePip, PipRuntimeInfo pipRuntimeInfo)
        {
            var pipId = runnablePip.PipId;
            var nodeId = pipId.ToNodeId();
            var processPip = (runnablePip.Pip as Process);
            var isSucceedFastPip = PipGraph.IsSucceedFast(pipId);

            bool shouldSkipDownstreamPipsDueToSucccessFast = isSucceedFastPip && processPip.SucceedFastExitCodes.Contains(result.ExitCode);
            if (shouldSkipDownstreamPipsDueToSucccessFast)
            {
                Interlocked.Increment(ref m_pipSkippingDownstreamDueToSuccessFast);
                Logger.Log.SkipDownstreamPipsDueToPipSuccess(m_executePhaseLoggingContext, runnablePip.Description);
            }

            // The dependents are marked direct dirty if the current pip executes or deploys its outputs from the cache.
            // This is analogous to a pip gets dirty because one of its inputs is modified/touched.
            bool directDirtyDownStreams =
                IncrementalSchedulingState?.DirtyNodeTracker != null &&
                runnablePip?.Result != null &&
                runnablePip.Result.Value.Status != PipResultStatus.UpToDate &&
                (runnablePip.Result.Value.Status != PipResultStatus.Skipped) &&
                (!m_configuration.Schedule.StopDirtyOnSucceedFastPips || !isSucceedFastPip);

            if (directDirtyDownStreams)
            {
                foreach (Edge outEdge in DirectedGraph.GetOutgoingEdges(nodeId))
                {
                    // We need to check to make sure the pip is dirty because we can't mark a pip direct dirty without it being dirty.
                    // The scenario that this happens for is when a sealed directory is clean and not materialized, but the downstream pip is clean and materialized.
                    if (!IsPipCleanMaterialized(outEdge.OtherNode.ToPipId()))
                    {
                        m_executionTimeDirectDirtiedNodes.Add(outEdge.OtherNode);
                        IncrementalSchedulingState.PendingUpdates.MarkNodeMaterialization(outEdge.OtherNode, false);
                    }
                }
            }

            foreach (Edge outEdge in ScheduledGraph.GetOutgoingEdges(nodeId))
            {
                // Light edges do not propagate failure or ref-count changes.
                if (outEdge.IsLight)
                {
                    continue;
                }

                PipId dependentPipId = outEdge.OtherNode.ToPipId();
                PipRuntimeInfo dependentPipRuntimeInfo = GetPipRuntimeInfo(dependentPipId);

                PipState currentDependentState = dependentPipRuntimeInfo.State;
                if (currentDependentState != PipState.Waiting &&
                    currentDependentState != PipState.Ignored &&
                    currentDependentState != PipState.Skipped)
                {
                    Contract.Assume(
                        false,
                        I($"Nodes with pending heavy edges must be pending or skipped already (due to failure or filtering), but its state is '{currentDependentState}'"));
                }

                if (currentDependentState == PipState.Ignored)
                {
                    continue;
                }

                // Mark the dependent as uncacheable impacted if the parent was marked as impacted
                if (pipRuntimeInfo.IsUncacheableImpacted)
                {
                    dependentPipRuntimeInfo.IsUncacheableImpacted = true;
                }

                if (pipRuntimeInfo.IsMissingContentImpacted)
                {
                    dependentPipRuntimeInfo.IsMissingContentImpacted = true;
                }

                if (pipRuntimeInfo.IsFrontierMissCandidate)
                {
                    // if the current pip is a process pip that was executed, its dependents cannot be frontier pips
                    if (runnablePip.PipType == PipType.Process && result.Status == PipResultStatus.Succeeded)
                    {
                        dependentPipRuntimeInfo.IsFrontierMissCandidate = false;
                    }
                }
                else
                {
                    // if the current pip is not a frontier miss candidate, its dependents cannot be candidates
                    dependentPipRuntimeInfo.IsFrontierMissCandidate = false;
                }

                if (!succeeded || result.Status == PipResultStatus.Skipped || shouldSkipDownstreamPipsDueToSucccessFast)
                {
                    // The current pip failed, so skip the dependent pip.
                    // Note that we decrement the ref count; this dependent pip will eventually have ref count == 0
                    // at which point we will 'run' the pip in ReportSkippedPip (simply to unwind the stack and then
                    // skip further transitive dependents).
                    if (currentDependentState == PipState.Waiting)
                    {
                        do
                        {
                            // There can be a race on calling TryTransition. One thread may lose on Interlocked.CompareExchange
                            // in PipRunTimeInfo.TryTransitionInternal, but before the other thread finishes the method, the former thread
                            // checks in the Contract.Assert below if the state is PipState.Skipped. One need to ensure that both threads
                            // end up with PipState.Skipped.
                            bool transitionToSkipped = dependentPipRuntimeInfo.TryTransition(
                                m_pipStateCounters,
                                m_pipTable.GetPipType(dependentPipId),
                                currentDependentState,
                                PipState.Skipped);

                            if (transitionToSkipped && dependentPipRuntimeInfo.State != PipState.Skipped)
                            {
                                Contract.Assert(
                                    false,
                                    I($"Transition to {nameof(PipState.Skipped)} is successful, but the state of dependent is {dependentPipRuntimeInfo.State}"));
                            }

                            currentDependentState = dependentPipRuntimeInfo.State;
                        }
                        while (currentDependentState != PipState.Skipped);
                    }
                    else
                    {
                        Contract.Assert(
                            dependentPipRuntimeInfo.State.IsTerminal(),
                            "Upon failure, dependent pips must be in a terminal failure state");
                    }
                }

                // If a pip is a cache miss we consider it part of a path of misses
                // and we inform the dependent so it can update the length of its maximal path of misses
                // We only consider successive process pips in a dependency chain for this computation,
                // so non-process pips just forward the accumulated value.
                if (runnablePip.PipType != PipType.Process)
                {
                    // If the pip is not a process pip just propagate the number
                    dependentPipRuntimeInfo.InformDependencyCacheMissChain(pipRuntimeInfo.UpstreamCacheMissLongestChain);
                }
                else if (pipRuntimeInfo.Result == PipExecutionLevel.Executed)
                {
                    if (((Process)runnablePip.Pip).DisableCacheLookup)
                    {
                        // Do not increment length for pips that depends on disable cache lookup
                        dependentPipRuntimeInfo.InformDependencyCacheMissChain(pipRuntimeInfo.UpstreamCacheMissLongestChain);
                    }
                    else
                    {
                        dependentPipRuntimeInfo.InformDependencyCacheMissChain(pipRuntimeInfo.UpstreamCacheMissLongestChain + 1);
                    }
                }

                // Decrement reference count and possibly queue the pip (even if it is doomed to be skipped).
                var readyToSchedule = dependentPipRuntimeInfo.DecrementRefCount();

                if (readyToSchedule)
                {
                    OperationKind scheduledByOperationKind = PipExecutorCounter.ScheduledByDependencyDuration;
                    scheduledByOperationKind = scheduledByOperationKind.GetPipTypeSpecialization(m_pipTable.GetPipType(dependentPipId));

                    using (runnablePip.OperationContext.StartOperation(PipExecutorCounter.ScheduleDependentDuration))
                    using (runnablePip.OperationContext.StartOperation(scheduledByOperationKind))
                    {
                        // If it is ready to schedule, we do not need to call 'SchedulePip' under the lock
                        // because we call SchedulePip only once for pip.
                        await SchedulePip(outEdge.OtherNode, dependentPipId);
                    }
                }
            }
        }

        private void CleanTempDirs(RunnablePip runnablePip)
        {
            if (!m_configuration.Engine.CleanTempDirectories)
            {
                return;
            }

            Contract.Requires(runnablePip.PipType == PipType.Process);
            Contract.Requires(runnablePip.Result.HasValue);
            // Only allow this to be null in testing
            if (TempCleaner == null)
            {
                Contract.Assert(m_testHooks != null);
                return;
            }

            var process = (Process)runnablePip.Pip;
            var resultStatus = runnablePip.Result.Value.Status;

            // Don't delete the temp directories when a pip fails for debugging.
            if (resultStatus != PipResultStatus.Succeeded &&
                resultStatus != PipResultStatus.Canceled)
            {
                return;
            }

            // Roots of temp directories need to be deleted so that we have a consistent behavior with scrubber.
            // If those roots are not deleted and the user enables scrubber, then those roots will get deleted because
            // temp directories are not considered as outputs.
            if (process.TempDirectory.IsValid)
            {
                TempCleaner.RegisterDirectoryToDelete(process.TempDirectory.ToString(Context.PathTable), deleteRootDirectory: true);
            }

            foreach (var additionalTempDirectory in process.AdditionalTempDirectories)
            {
                // Unlike process.TempDirectory, which is invalid for pips without temp directories,
                // AdditionalTempDirectories should not have invalid paths added
                Contract.Requires(additionalTempDirectory.IsValid);
                TempCleaner.RegisterDirectoryToDelete(additionalTempDirectory.ToString(Context.PathTable), deleteRootDirectory: true);
            }

            // Only for successful run scheduling temporary outputs for deletion
            foreach (FileArtifactWithAttributes output in process.FileOutputs)
            {
                // Deleting all the outputs that can't be referenced
                // Non-reference-able outputs are safe to delete, since it would be an error for any concurrent build step to read them.
                // CanBeReferencedOrCached() is false for e.g. 'intermediate' outputs, and deleting them proactively can be a nice space saving
                if (!output.CanBeReferencedOrCached())
                {
                    TempCleaner.RegisterFileToDelete(output.Path.ToString(Context.PathTable));
                }
            }
        }

        private void LogProcessStats(RunnablePip runnablePip)
        {
            Contract.Requires(runnablePip.PipType == PipType.Process);
            Contract.Requires(runnablePip.Result.HasValue);

            var result = runnablePip.Result.Value;
            var runnableProcess = (ProcessRunnablePip)runnablePip;

            // Start service and shutdown service pips do not go through CacheLookUp,
            // so they shouldn't be considered for cache stats
            if (runnableProcess.Process.IsStartOrShutdownKind)
            {
                return;
            }

            Interlocked.Increment(ref m_numProcessPipsCompleted);
            switch (result.Status)
            {
                case PipResultStatus.DeployedFromCache:
                case PipResultStatus.UpToDate:
                case PipResultStatus.NotMaterialized:
                    // These results describe output materialization state and
                    // can also occur for pips run on distributed workers since the outputs are
                    // not produced on this machine. Distinguish using flag on runnable process indicating
                    // execution
                    if (runnableProcess.Executed)
                    {
                        Interlocked.Increment(ref m_numProcessPipsUnsatisfiedFromCache);
                        m_groupedPipCounters.IncrementCounter(runnableProcess.Process, PipCountersByGroup.CacheMiss);
                    }
                    else
                    {
                        Interlocked.Increment(ref m_numProcessPipsSatisfiedFromCache);
                        m_groupedPipCounters.IncrementCounter(runnableProcess.Process, PipCountersByGroup.CacheHit);
                    }

                    break;
                case PipResultStatus.Failed:
                case PipResultStatus.Succeeded:
                case PipResultStatus.Canceled:
                    Interlocked.Increment(ref m_numProcessPipsUnsatisfiedFromCache);
                    m_groupedPipCounters.IncrementCounter(runnableProcess.Process, PipCountersByGroup.CacheMiss);
                    break;
                case PipResultStatus.Skipped:
                    Interlocked.Increment(ref m_numProcessPipsSkipped);
                    m_groupedPipCounters.IncrementCounter(runnableProcess.Process, PipCountersByGroup.Skipped);
                    break;
                default:
                    throw Contract.AssertFailure("PipResult case not handled");
            }
        }

        /// <summary>
        /// Schedule pip for evaluation. The pip's content fingerprint will be computed
        /// and it will be scheduled for execution.
        /// </summary>
        /// <remarks>
        /// At the call time the given pip must not have any further dependencies to wait on.
        /// </remarks>
        private async Task SchedulePip(NodeId node, PipId pipId)
        {
            Contract.Requires(pipId.IsValid);
            Contract.Requires(ScheduledGraph.ContainsNode(node));

            using (PipExecutionCounters.StartStopwatch(PipExecutorCounter.SchedulePipDuration))
            {
                PipRuntimeInfo pipRuntimeInfo = GetPipRuntimeInfo(pipId);
                Contract.Assert(pipRuntimeInfo.RefCount == 0, "All dependencies of the pip must be completed before the pip is scheduled to run.");

                PipState currentState = pipRuntimeInfo.State;

                Contract.Assume(
                    currentState == PipState.Waiting ||
                    currentState == PipState.Ignored ||
                    currentState == PipState.Skipped);

                // Pips which have not been explicitly scheduled (when that's required) are never 'ready' (even at refcount 0).
                if (currentState == PipState.Ignored)
                {
                    return;
                }

                Contract.Assert(currentState == PipState.Waiting || currentState == PipState.Skipped, "Current pip state should be either waiting or skipped.");

                // With a ref count of zero, all pip dependencies (if any) have already executed, and so we have
                // all needed content hashes (and content on disk) for inputs. This means the pip we can both
                // compute the content fingerprint and execute it (possibly in a cached manner).
                if (IsTerminating)
                {
                    // We're bringing down the schedule quickly. Even pips which become ready without any dependencies will be skipped.
                    // We return early here to skip OnPipNewlyQueuedOrRunning (which prevents m_numPipsQueuedOrRunning from increasing).
                    Pip pip = m_pipTable.HydratePip(pipId, PipQueryContext.SchedulerSchedulePipIfReady);

                    Contract.Assert(m_executePhaseLoggingContext != null, "m_executePhaseLoggingContext should be set at this point. Did you forget to initialize it?");
                    Logger.Log.ScheduleIgnoringPipSinceScheduleIsTerminating(
                        m_executePhaseLoggingContext,
                        pip.GetDescription(Context));
                    return;
                }

                var pipState = m_pipTable.GetMutable(pipId);

                if (currentState != PipState.Skipped)
                {
                    // If the pip is not skipped, then transition its state to Ready.
                    pipRuntimeInfo.Transition(m_pipStateCounters, pipState.PipType, PipState.Ready);
                }

                await SchedulePip(pipId, pipState.PipType);
            }
        }

        private Task SchedulePip(PipId pipId, PipType pipType, RunnablePipObserver observer = null, int? priority = null, PipExecutionStep? step = null)
        {
            Contract.Requires(step == null || IsDistributedWorker, "Step can only be explicitly specified when scheduling pips on distributed worker");
            Contract.Requires(step != null || !IsDistributedWorker, "Step MUST be explicitly specified when scheduling pips on distributed worker");

            // Offload the execution of the pip to one of the queues in the PipQueue.
            // If it is a meta or SealDirectory pip and the PipQueue has started draining, then the execution will be inlined here!
            // Because it is not worth to enqueue the fast operations such as the execution of meta and SealDirectory pips.

            ushort cpuUsageInPercent = m_scheduleConfiguration.UseHistoricalCpuUsageInfo()
                ? HistoricPerfDataTable[m_pipTable.GetPipSemiStableHash(pipId)].ProcessorsInPercents
                : (ushort)0;

            // TODO(seokur): cpuUsageInPercent sometimes becomes way higher than the number of physical threads of the machine. 
            // There is an issue about getting user and kernel time for processes in some cases. It is under investigation.
            // For now, we cap the cpuUsageInPercent to 1000 (weight:10). 
            if (OperatingSystemHelper.IsLinuxOS)
            {
                cpuUsageInPercent = Math.Min(cpuUsageInPercent, (ushort)1000); // TEMPORARY. Work item #2116515
            }

            var runnablePip = RunnablePip.Create(
                m_executePhaseLoggingContext,
                this,
                pipId,
                pipType,
                priority ?? GetPipPriority(pipId),
                m_executePipFunc,
                cpuUsageInPercent);

            runnablePip.SetObserver(observer);
            if (IsDistributedWorker)
            {
                runnablePip.Transition(step.Value, force: true);
                runnablePip.SetWorker(LocalWorker);
                m_executionStepTracker.Transition(pipId, step.Value);
            }
            // Only keep performance info for Process and Ipc pips
            else if (pipType == PipType.Process || runnablePip.PipType == PipType.Ipc)
            {
                // Only on orchestrator, we keep performance info per pip
                m_runnablePipPerformance.Add(pipId, runnablePip.Performance);
            }

            return ExecuteAsyncOrEnqueue(runnablePip);
        }

        internal void AddExecutionLogTarget(IExecutionLogTarget target)
        {
            Contract.Requires(target != null);

            m_multiExecutionLogTarget.AddExecutionLogTarget(target);
        }

        internal void RemoveExecutionLogTarget(IExecutionLogTarget target)
        {
            Contract.Requires(target != null);

            lock (m_multiExecutionLogTarget)
            {
                m_multiExecutionLogTarget.RemoveExecutionLogTarget(target);
            }
        }

        internal void StartPipStep(PipId pipId, RunnablePipObserver observer, PipExecutionStep step, int priority)
        {
            Contract.Assert(IsDistributedWorker, "Only workers can handle distributed pip requests");

            // Start by updating the pip to the ready state
            PipRuntimeInfo pipRuntimeInfo = GetPipRuntimeInfo(pipId);
            pipRuntimeInfo.TryTransition(m_pipStateCounters, m_pipTable.GetPipType(pipId), PipState.Ignored, PipState.Waiting);
            pipRuntimeInfo.TryTransition(m_pipStateCounters, m_pipTable.GetPipType(pipId), PipState.Waiting, PipState.Ready);
            pipRuntimeInfo.TryTransition(m_pipStateCounters, m_pipTable.GetPipType(pipId), PipState.Ready, PipState.Running);

            SchedulePip(pipId, m_pipTable.GetPipType(pipId), observer, step: step, priority: priority).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Inline or enqueue the execution of the pip starting from the step given in the <see cref="RunnablePip.Step"/>
        /// </summary>
        private async Task ExecuteAsyncOrEnqueue(RunnablePip runnablePip)
        {
            Contract.Requires(runnablePip.Step != PipExecutionStep.None);

            var previousQueue = runnablePip.DispatcherKind;
            var nextQueue = DecideDispatcherKind(runnablePip);

            if (!PipQueue.IsDraining && nextQueue == DispatcherKind.None)
            {
                // If the queue has not been started draining, we should add the runnable pip to the queue.
                // SchedulePip is called as a part of 'schedule' phase.
                // That's why, we should not inline executions when the queue is not draining.
                nextQueue = DispatcherKind.Light;
            }

            bool inline = false;

            // If the pip should be cancelled, make sure we inline the next step. The pip queue may be also flagged as cancelled and won't dequeue the pip otherwise.
            // The check for cancellation will then happen on ExecutePipStep and the pip will be transitioned to PipExecutionStep.Cancel
            if (ShouldCancelPip(runnablePip))
            {
                inline = true;
            }

            // If the next queue is none or the same as the previous one, do not change the current queue and inline execution here.
            // However, when choosing worker, we should enqueue again even though the next queue is chooseworker again.
            if (nextQueue == DispatcherKind.None)
            {
                inline = true;
            }

            if (previousQueue == nextQueue && !nextQueue.IsChooseWorker())
            {
                // If the dispatcher kind is the same and we start a new pip, our new setting should decide to inline or not.
                if (runnablePip.Step == PipExecutionStep.Start)
                {
                    inline = !EngineEnvironmentSettings.DoNotInlineWhenNewPipRunInSameQueue;
                }
                else if (runnablePip.Step == PipExecutionStep.CacheLookup)
                {
                    inline = false;
                }
                else
                {
                    inline = true;
                }
            }

            if (inline)
            {
                await runnablePip.RunAsync();
            }
            else
            {
                runnablePip.SetDispatcherKind(nextQueue);
                m_chooseWorkerCpu.UnpauseChooseWorkerQueueIfEnqueuingNewPip(runnablePip, nextQueue);

                using (PipExecutionCounters.StartStopwatch(PipExecutorCounter.PipQueueEnqueueDuration))
                {
                    PipQueue.Enqueue(runnablePip);
                }
            }
        }

        private async Task ExecutePip(RunnablePip runnablePip)
        {
            var startTime = DateTime.UtcNow;
            runnablePip.StepStartTime = startTime;

            // Execute the current step
            PipExecutionStep nextStep;
            TimeSpan duration;

            if (runnablePip.Step == PipExecutionStep.Start || IsDistributedWorker)
            {
                // Initialize the runnable pip operation context so any
                // operations for the pip are properly attributed
                var loggingContext = LogEventPipStart(runnablePip);
                runnablePip.Start(OperationTracker, loggingContext);
            }

            // Measure the time spent executing pip steps as opposed to the
            // just sitting in the queue
            using (var operationContext = runnablePip.OperationContext.StartOperation(PipExecutorCounter.ExecutePipStepDuration))
            using (runnablePip.OperationContext.StartOperation(runnablePip.Step, runnablePip.Pip))
            {
                runnablePip.Observer.StartStep(runnablePip);

                nextStep = await ExecutePipStep(runnablePip);

                if (m_scheduleTerminatingWithInternalError)
                {
                    // When scheduler is being terminated with an internal error, the state machine for pip executions is terminated immediately.
                    return;
                }

                duration = operationContext.Duration.Value;

                if (runnablePip.Worker?.IsLocal ?? true)
                {
                    // For the remote worker, the stepduration is set on the worker and sent it to the orchestrator via grpc message.
                    runnablePip.StepDuration = duration;
                }

                Contract.Assert(!IsDistributedWorker || runnablePip.AcquiredResourceWorker == null);
                // Release the worker resources if we are done executing
                runnablePip.AcquiredResourceWorker?.ReleaseResources(runnablePip, nextStep);


                // If the duration is larger than EngineEnvironmentSettings.MinStepDurationSecForTracer (30 seconds by default)
                if (runnablePip.IncludeInTracer && (long)runnablePip.StepDuration.TotalSeconds > EngineEnvironmentSettings.MinStepDurationSecForTracer)
                {
                    var durationMs = (long)runnablePip.StepDuration.TotalMilliseconds;
                    string workerName = runnablePip.Worker.Name + (runnablePip is ProcessRunnablePip proc && proc.RunLocation == ProcessRunLocation.Remote ? "(Remote)" : string.Empty);
                    BuildXL.Tracing.Logger.Log.TracerCompletedEvent(runnablePip.OperationContext,
                        runnablePip.FormattedSemiStableHash,
                        runnablePip.Step.AsString(),
                        workerName + " - " + DecideDispatcherKind(runnablePip),
                        runnablePip.ThreadId,
                        runnablePip.StepStartTime.Ticks,
                        durationMs,
                        runnablePip.Pip.GetShortDescription(runnablePip.Environment.Context, withQualifer: false).Replace(@"\", @"\\").Replace("\"", "\\\""));
                }

                if (runnablePip.StepDuration.TotalMinutes > PipExecutionIOStepDelayedLimitMin && runnablePip.Step.IsIORelated())
                {
                    // None of I/O pip execution steps is supposed to take more than 15 minutes. However, there are some large Cosine pips whose inputs are materialized around 20m-25m.
                    // That's why, we chose 30 minutes for the limit to log a warning message, so that we can keep track of the frequency.
                    Logger.Log.PipExecutionIOStepDelayed(runnablePip.OperationContext, runnablePip.Description, runnablePip.Step.AsString(), PipExecutionIOStepDelayedLimitMin, (int)runnablePip.StepDuration.TotalMinutes);
                }

                runnablePip.Observer.EndStep(runnablePip);
            }

            // Store the duration
            m_pipExecutionStepCounters.AddToCounter(runnablePip.Step, duration);

            if (IsDistributedWorker)
            {
                runnablePip.End();
                m_executionStepTracker.Transition(runnablePip.PipId, PipExecutionStep.None);

                // Distributed workers do not traverse state machine
                return;
            }

            // Only orchestrator executes the following statements:

            // Send an Executionlog event
            runnablePip.LogExecutionStepPerformance(runnablePip.Step, startTime, duration);

            // Pip may need to materialize inputs/outputs before the next step
            // depending on the configuration
            MaterializeOutputsNextIfNecessary(runnablePip, ref nextStep);

            // Transition to the next step
            runnablePip.Transition(nextStep);

            m_executionStepTracker.Transition(runnablePip.PipId, nextStep);

            if (nextStep == PipExecutionStep.MaterializeOutputs && m_configuration.Distribution.ReplicateOutputsToWorkers())
            {
                // Send MaterializeOutput requests to all remote workers before
                Logger.Log.DistributionExecutePipRequest(runnablePip.LoggingContext, runnablePip.FormattedSemiStableHash, "RemoteWorkers", nameof(PipExecutionStep.MaterializeOutputs));

                var taskArrayInstance = m_taskArrayPool.GetInstance();
                for (int i = 0; i < m_remoteWorkers.Length; i++)
                {
                    taskArrayInstance.Instance[i] = m_remoteWorkers[i].MaterializeOutputsAsync(runnablePip);
                }

                runnablePip.MaterializeOutputsTasks = taskArrayInstance;
            }

            if (nextStep == PipExecutionStep.Done)
            {
                return;
            }

            if (runnablePip.Worker?.IsRemote == true)
            {
                bool isAlreadyRemotelyExecuting = runnablePip.IsRemotelyExecuting;
                Task remoteTask = PipQueue.RemoteAsync(runnablePip);
                if (isAlreadyRemotelyExecuting)
                {
                    await remoteTask;
                }

                Analysis.IgnoreResult(remoteTask);
                return;
            }

            runnablePip.IsRemotelyExecuting = false;

            // (a) Execute as inlined here, OR
            // (b) Enqueue the execution of the rest of the steps until another enqueue.
            await ExecuteAsyncOrEnqueue(runnablePip);
        }

        private void FlagSharedOpaqueOutputsOnCancellation(RunnablePip runnablePip)
        {
            Contract.Assert(runnablePip.IsCancelled);
            if (runnablePip is ProcessRunnablePip processRunnable)
            {
                FlagAndReturnScrubbableSharedOpaqueOutputs(runnablePip.Environment, processRunnable);
            }
        }

        /// <summary>
        /// Modifies the next step to one of the materialization steps if required
        /// </summary>
        private void MaterializeOutputsNextIfNecessary(RunnablePip runnablePip, ref PipExecutionStep nextStep)
        {
            if (!runnablePip.Result.HasValue)
            {
                return;
            }

            if (m_scheduleConfiguration.RequiredOutputMaterialization == RequiredOutputMaterialization.All &&
                runnablePip.PipType == PipType.SealDirectory)
            {
                // Seal directories do not need to be materialized when materializing all outputs. Seal directory outputs
                // are composed of other pip outputs which would necessarily be materialized if materializing all outputs
                if (IncrementalSchedulingState != null && !runnablePip.Result.Value.Status.IndicatesNoOutput())
                {
                    IncrementalSchedulingState.PendingUpdates.MarkNodeMaterialized(runnablePip.PipId.ToNodeId());
                    PipExecutionCounters.IncrementCounter(PipExecutorCounter.PipMarkMaterialized);
                }

                return;
            }

            if (!EngineEnvironmentSettings.DoNotSkipIpcWhenMaterializingOutputs &&
                runnablePip.PipType == PipType.Ipc)
            {
                // By default, we skip IPC pips when materializing outputs because the outputs of IPC pips are not important
                // to materialize on the machines.
                return;
            }

            if (!runnablePip.Result.Value.Status.IndicatesNoOutput() &&
                PipArtifacts.CanProduceOutputs(runnablePip.PipType) &&
                runnablePip.Step != PipExecutionStep.MaterializeOutputs &&

                // Background output materialize happens after HandleResult (before Done) so that dependents are scheduled before attempting
                // to materialize outputs.
                (MaterializeOutputsInBackground ? nextStep == PipExecutionStep.Done : nextStep == PipExecutionStep.HandleResult) &&

                // Need to run materialize outputs are not materialized or if outputs are replicated to all workers
                (m_configuration.Distribution.ReplicateOutputsToWorkers()
                    || runnablePip.Result.Value.Status == PipResultStatus.NotMaterialized) &&
                RequiresPipOutputs(runnablePip.PipId.ToNodeId()))
            {
                runnablePip.SetWorker(LocalWorker);

                if (MaterializeOutputsInBackground)
                {
                    // Background output materialization should yield to other tasks since its not required
                    // unblock anything
                    runnablePip.ChangePriority(0);
                }

                // Prior to completing the pip and handling the result
                // materialize outputs for pips that require outputs to be materialized
                nextStep = PipExecutionStep.MaterializeOutputs;
            }
        }

        /// <summary>
        /// Decide which dispatcher queue needs to execute the given pip in the given step
        /// </summary>
        /// <remarks>
        /// If the result is <see cref="DispatcherKind.None"/>, the execution will be inlined.
        /// </remarks>
        private DispatcherKind DecideDispatcherKind(RunnablePip runnablePip)
        {
            switch (runnablePip.Step)
            {
                case PipExecutionStep.Start:
                    switch (runnablePip.PipType)
                    {
                        case PipType.SealDirectory:
                        case PipType.WriteFile:
                        case PipType.CopyFile:
                            return m_configuration.EnableDistributedSourceHashing() ? DispatcherKind.Light : IODispatcher;

                        case PipType.Ipc:
                        case PipType.Process:
                            return m_configuration.EnableDistributedSourceHashing() ? DispatcherKind.None : IODispatcher;

                        default:
                            // For metabuilds, this is noop.
                            return DispatcherKind.None;
                    }

                case PipExecutionStep.DelayedCacheLookup:
                    return DispatcherKind.DelayedCacheLookup;

                case PipExecutionStep.ChooseWorkerCacheLookup:
                    // First attempt should be inlined; if it does not acquire a worker, then it should be enqueued to ChooseWorkerCacheLookup queue.
                    return runnablePip.IsWaitingForWorker ? DispatcherKind.ChooseWorkerCacheLookup : DispatcherKind.None;

                case PipExecutionStep.CacheLookup:
                case PipExecutionStep.PostProcess:
                    // DispatcherKind.CacheLookup is mainly for CAS and VSTS resources.
                    // As we store cache entries in PostProcess, so it makes sense to process those in CacheLookup queue.
                    return CacheLookupDispatcher;

                case PipExecutionStep.ChooseWorkerCpu:
                    if (!runnablePip.IsWaitingForWorker)
                    {
                        // First attempt should be inlined.
                        return DispatcherKind.None;
                    }

                    return runnablePip.IsLight ? DispatcherKind.ChooseWorkerLight : DispatcherKind.ChooseWorkerCpu;

                case PipExecutionStep.ChooseWorkerIpc:
                    return runnablePip.IsWaitingForWorker ? DispatcherKind.ChooseWorkerIpc : DispatcherKind.None;

                case PipExecutionStep.MaterializeInputs:
                case PipExecutionStep.MaterializeOutputs:
                    return DispatcherKind.Materialize;

                case PipExecutionStep.ExecuteProcess:
                case PipExecutionStep.ExecuteNonProcessPip:
                    {
                        switch (runnablePip.PipType)
                        {
                            case PipType.Process:
                                return runnablePip.IsLight ? DispatcherKind.Light : DispatcherKind.CPU;
                            case PipType.Ipc:
                                return DispatcherKind.IpcPips;
                            default:
                                return DispatcherKind.None;
                        }
                    }

                // INEXPENSIVE STEPS
                case PipExecutionStep.CheckIncrementalSkip:
                case PipExecutionStep.RunFromCache: // Just reports hashes and replay warnings (inline)
                case PipExecutionStep.Cancel:
                case PipExecutionStep.Skip:
                case PipExecutionStep.HandleResult:
                case PipExecutionStep.None:
                case PipExecutionStep.Done:
                    // Do not change the current queue and inline execution.
                    return DispatcherKind.None;

                default:
                    throw Contract.AssertFailure(I($"Invalid pip execution step: '{runnablePip.Step}'"));
            }
        }

        private DispatcherKind IODispatcher => EngineEnvironmentSettings.MergeIOCacheLookupDispatcher.Value ? CacheLookupDispatcher : DispatcherKind.IO;

        private DispatcherKind CacheLookupDispatcher => EngineEnvironmentSettings.MergeCacheLookupMaterializeDispatcher.Value ? DispatcherKind.Materialize : DispatcherKind.CacheLookup;

        private PipExecutionStep PreCacheLookupStep => m_configuration.Schedule.DelayedCacheLookupEnabled() ? PipExecutionStep.DelayedCacheLookup : PipExecutionStep.ChooseWorkerCacheLookup;

        /// <summary>
        /// Execute the given pip in the current step and return the next step
        /// </summary>
        /// <remarks>
        /// The state diagram for pip execution steps is in <see cref="PipExecutionStep"/> class.
        /// </remarks>
        private async Task<PipExecutionStep> ExecutePipStep(RunnablePip runnablePip)
        {
            Contract.Requires(runnablePip != null);
            Contract.Requires(runnablePip.Step != PipExecutionStep.Done && runnablePip.Step != PipExecutionStep.None, $"Cannot execute {runnablePip.Step} for {runnablePip.PipType}");
            Contract.Requires(!IsDistributedWorker || runnablePip.Step.CanWorkerExecute(), $"Workers cannot execute {runnablePip.Step}");

            ProcessRunnablePip processRunnable = runnablePip as ProcessRunnablePip;
            Process process = runnablePip.Pip as Process;
            var pipId = runnablePip.PipId;
            var pipType = runnablePip.PipType;
            var loggingContext = runnablePip.LoggingContext;
            var operationContext = runnablePip.OperationContext;
            var environment = runnablePip.Environment;
            var fileContentManager = environment.State.FileContentManager;
            var step = runnablePip.Step;
            var worker = runnablePip.Worker;

            // If schedule is terminating (e.g., StopOnFirstFailure), cancel the pip
            // as long as (i) 'start' step has been executed, (ii) the pip is in running state, and (iii) the pip has not been cancelled before.
            if (ShouldCancelPip(runnablePip))
            {
                return runnablePip.Cancel();
            }

            switch (step)
            {
                case PipExecutionStep.Start:
                    {
                        var state = TryStartPip(runnablePip);
                        if (state == PipState.Skipped)
                        {
                            return PipExecutionStep.Skip;
                        }

                        Contract.Assert(state == PipState.Running, $"Cannot start pip in state: {state}");

                        if (pipType.IsMetaPip())
                        {
                            return PipExecutionStep.ExecuteNonProcessPip;
                        }

                        if (process?.IsStartOrShutdownKind == true)
                        {
                            // Service start and shutdown pips are noop in the scheduler.
                            // They will be run on demand by the service manager which is not tracked directly by the scheduler.
                            return runnablePip.SetPipResult(PipResult.CreateWithPointPerformanceInfo(PipResultStatus.Succeeded));
                        }

                        // For module affinity, we need to set the preferred worker id.
                        // This is intentionally put here after we hydrate the pip for the first time when accessing
                        // runnablePip.Pip above for hashing dependencies.
                        if (runnablePip.Pip.Provenance.ModuleId.IsValid &&
                            m_moduleWorkerMapping.TryGetValue(runnablePip.Pip.Provenance.ModuleId, out var tuple))
                        {
                            for (int i = 0; i < m_workers.Count; i++)
                            {
                                if (tuple.Workers[i])
                                {
                                    runnablePip.PreferredWorkerId = i;
                                }
                            }
                        }

                        bool hashSourceFiles = !m_configuration.EnableDistributedSourceHashing() || !pipType.IsDistributable();

                        if (hashSourceFiles)
                        {
                            using (operationContext.StartOperation(PipExecutorCounter.HashSourceFileDependenciesDuration))
                            {
                                // Hash source file dependencies
                                var maybeHashed = await fileContentManager.TryHashSourceDependenciesAsync(runnablePip.Pip, operationContext);
                                if (!maybeHashed.Succeeded)
                                {
                                    Logger.Log.PipFailedDueToSourceDependenciesCannotBeHashed(
                                        loggingContext,
                                        runnablePip.Description);
                                    return runnablePip.SetPipResult(PipResultStatus.Failed);
                                }
                            }
                        }

                        if (pipType == PipType.Ipc)
                        {
                            // IPC pips go to ChooseWorkerIpc without checking the incremental state
                            return PipExecutionStep.ChooseWorkerIpc;
                        }

                        if (pipType == PipType.Process)
                        {
                            return m_configuration.EnableDistributedSourceHashing() ? PreCacheLookupStep : PipExecutionStep.CheckIncrementalSkip;
                        }

                        // CopyFile, WriteFile, SealDirectory pips
                        return m_configuration.EnableDistributedSourceHashing() ? PipExecutionStep.ExecuteNonProcessPip : PipExecutionStep.CheckIncrementalSkip;
                    }

                case PipExecutionStep.Cancel:
                    {
                        // Make sure shared opaque outputs are flagged as such.
                        FlagSharedOpaqueOutputsOnCancellation(runnablePip);

                        Logger.Log.ScheduleCancelingPipSinceScheduleIsTerminating(
                            loggingContext,
                            runnablePip.Description);
                        return runnablePip.SetPipResult(PipResult.CreateWithPointPerformanceInfo(PipResultStatus.Canceled));
                    }

                case PipExecutionStep.Skip:
                    {
                        // We report skipped pips when all dependencies (failed or otherwise) complete.
                        // This has the side-effect that stack depth is bounded when a pip fails; ReportSkippedPip
                        // reports failure which is then handled in OnPipCompleted as part of the normal queue processing
                        // (rather than recursively abandoning dependents here).
                        LogEventWithPipProvenance(runnablePip, Logger.Log.SchedulePipFailedDueToFailedPrerequisite);
                        return runnablePip.SetPipResult(PipResult.Skipped);
                    }

                case PipExecutionStep.MaterializeOutputs:
                    {
                        m_materializeOutputsQueued = true;
                        PipResultStatus materializationResult = await worker.MaterializeOutputsAsync(runnablePip);

                        var nextStep = processRunnable?.ExecutionResult != null
                            ? processRunnable.SetPipResult(processRunnable.ExecutionResult.CloneSealedWithResult(materializationResult))
                            : runnablePip.SetPipResult(materializationResult);

                        if (!MaterializeOutputsInBackground)
                        {
                            return nextStep;
                        }

                        if (materializationResult.IndicatesFailure())
                        {
                            m_hasFailures = true;
                        }
                        else
                        {
                            IncrementalSchedulingState?.PendingUpdates.MarkNodeMaterialized(runnablePip.PipId.ToNodeId());
                            Logger.Log.PipIsMarkedMaterialized(loggingContext, runnablePip.Description);
                        }

                        if (runnablePip.MaterializeOutputsTasks != null)
                        {
                            runnablePip.ReleaseDispatcher();
                            await Task.WhenAll(runnablePip.MaterializeOutputsTasks.Value.Instance);
                            Logger.Log.DistributionFinishedPipRequest(runnablePip.LoggingContext, runnablePip.FormattedSemiStableHash, "RemoteWorkers", nameof(PipExecutionStep.MaterializeOutputs));
                            runnablePip.MaterializeOutputsTasks.Value.Dispose();
                        }

                        return PipExecutionStep.Done;
                    }

                case PipExecutionStep.CheckIncrementalSkip:
                    {
                        // Enable incremental scheduling when distributed build role is none, and
                        // dirty build is not used (forceSkipDependencies is false).
                        if (ShouldIncrementalSkip(pipId))
                        {
                            var maybeHashed = await fileContentManager.TryRegisterOutputDirectoriesAndHashSharedOpaqueOutputsAsync(runnablePip.Pip, operationContext);
                            if (!maybeHashed.Succeeded)
                            {
                                if (maybeHashed.Failure is CancellationFailure)
                                {
                                    Contract.Assert(loggingContext.ErrorWasLogged);
                                }
                                else
                                {
                                    Logger.Log.PipFailedDueToOutputsCannotBeHashed(
                                        loggingContext,
                                        runnablePip.Description);
                                }
                            }
                            else
                            {
                                PipExecutionCounters.IncrementCounter(PipExecutorCounter.IncrementalSkipPipDueToCleanMaterialized);

                                if (runnablePip.Pip.PipType == PipType.Process)
                                {
                                    PipExecutionCounters.IncrementCounter(PipExecutorCounter.IncrementalSkipProcessDueToCleanMaterialized);
                                }

                                Logger.Log.PipIsIncrementallySkippedDueToCleanMaterialized(loggingContext, runnablePip.Description);
                            }

                            return runnablePip.SetPipResult(PipResult.Create(
                                maybeHashed.Succeeded ? PipResultStatus.UpToDate : PipResultStatus.Failed,
                                runnablePip.StartTime));
                        }

                        if (m_scheduleConfiguration.ForceSkipDependencies != ForceSkipDependenciesMode.Disabled && m_mustExecuteNodesForDirtyBuild != null)
                        {
                            if (!m_mustExecuteNodesForDirtyBuild.Contains(pipId.ToNodeId()))
                            {
                                // When dirty build is enabled, we skip the scheduled pips whose outputs are present and are in the transitive dependency chain
                                // The skipped ones during execution are not explicitly scheduled pips at all.
                                return runnablePip.SetPipResult(PipResult.Create(
                                    PipResultStatus.UpToDate,
                                    runnablePip.StartTime));
                            }
                        }

                        PipExecutionCounters.IncrementCounter(PipExecutorCounter.CantIncrementalSkipProcessDueToNotMaterialized);

                        // Contents of sealed directories get hashed by their consumer
                        if (pipType != PipType.SealDirectory)
                        {
                            using (PipExecutionCounters.StartStopwatch(PipExecutorCounter.HashProcessDependenciesDuration))
                            {
                                // The dependencies may have been skipped, so hash the processes inputs
                                var maybeHashed = await fileContentManager.TryHashDependenciesAsync(runnablePip.Pip, operationContext);
                                if (!maybeHashed.Succeeded)
                                {
                                    if (!(maybeHashed.Failure is CancellationFailure))
                                    {
                                        Logger.Log.PipFailedDueToDependenciesCannotBeHashed(
                                            loggingContext,
                                            runnablePip.Description);
                                    }

                                    return runnablePip.SetPipResult(PipResultStatus.Failed);
                                }
                            }
                        }

                        if (pipType == PipType.Process)
                        {
                            return PreCacheLookupStep;
                        }

                        return PipExecutionStep.ExecuteNonProcessPip;
                    }

                case PipExecutionStep.DelayedCacheLookup:
                    {
                        return PipExecutionStep.ChooseWorkerCacheLookup;
                    }

                case PipExecutionStep.ChooseWorkerCacheLookup:
                    {
                        Contract.Assert(pipType == PipType.Process);
                        Contract.Assert(worker == null);

                        worker = m_chooseWorkerCacheLookup.ChooseWorker(processRunnable);
                        runnablePip.SetWorker(worker);

                        if (worker == null)
                        {
                            // If none of the workers is available, enqueue again.
                            // We always want to choose a worker for the highest priority item. That's why, we enqueue again
                            return PipExecutionStep.ChooseWorkerCacheLookup;
                        }

                        return PipExecutionStep.CacheLookup;
                    }

                case PipExecutionStep.ChooseWorkerIpc:
                    {
                        Contract.Assert(pipType == PipType.Ipc);
                        Contract.Assert(worker == null);

                        worker = m_chooseWorkerIpc.ChooseWorker(runnablePip);
                        runnablePip.SetWorker(worker);

                        if (worker == null)
                        {
                            // If none of the workers is available, enqueue again.
                            return PipExecutionStep.ChooseWorkerIpc;
                        }

                        return PipExecutionStep.ExecuteNonProcessPip;
                    }

                case PipExecutionStep.ChooseWorkerCpu:
                    {
                        Contract.Assert(pipType == PipType.Process, $"{pipType} cannot be executed");
                        Contract.Assert(worker == null, "worker is not null");

                        worker = ChooseWorkerCpu(processRunnable);
                        runnablePip.SetWorker(worker);

                        if (worker == null)
                        {
                            // If none of the workers is available, enqueue again.
                            // We always want to choose a worker for the highest priority item. That's why, we enqueue again
                            return PipExecutionStep.ChooseWorkerCpu;
                        }

                        return InputsLazilyMaterialized ? PipExecutionStep.MaterializeInputs : PipExecutionStep.ExecuteProcess;
                    }

                case PipExecutionStep.MaterializeInputs:
                    {
                        Contract.Assert(pipType == PipType.Process);

                        PipResultStatus materializationResult = await worker.MaterializeInputsAsync(processRunnable);
                        if (materializationResult.IndicatesFailure())
                        {
                            return runnablePip.SetPipResult(materializationResult);
                        }

                        worker.OnInputMaterializationCompletion(runnablePip.Pip, this);

                        return PipExecutionStep.ExecuteProcess;
                    }

                case PipExecutionStep.ExecuteNonProcessPip:
                    {
                        var pipResult = await ExecuteNonProcessPipAsync(runnablePip);

                        if (runnablePip.PipType == PipType.Ipc && runnablePip.Worker?.IsRemote == true)
                        {
                            PipExecutionCounters.IncrementCounter(PipExecutorCounter.IpcPipsExecutedRemotely);
                        }

                        return runnablePip.SetPipResult(pipResult);
                    }

                case PipExecutionStep.CacheLookup:
                    {
                        Contract.Assert(processRunnable != null);
                        Contract.Assert(worker != null);

                        var pipScope = State.GetScope(process);
                        var cacheableProcess = pipScope.GetCacheableProcess(process, environment);
                        var pipRunTimeInfo = GetPipRuntimeInfo(pipId);

                        // Avoid querying the remote cache if this pip is part of a chain
                        // of two consecutive cache misses.
                        // We assume that this will probably be also a miss so we don't want to
                        // spend time through the network.
                        var avoidRemoteCache = m_scheduleConfiguration.RemoteCacheCutoff &&
                                                pipRunTimeInfo.UpstreamCacheMissLongestChain >= m_scheduleConfiguration.RemoteCacheCutoffLength;

                        var tupleResult = await worker.CacheLookupAsync(
                            processRunnable,
                            pipScope,
                            cacheableProcess,
                            avoidRemoteCache);

                        if (avoidRemoteCache)
                        {
                            PipExecutionCounters.IncrementCounter(PipExecutorCounter.TotalCacheLookupsAvoidingRemote);
                        }

                        var cacheResult = tupleResult.Item1;
                        if (cacheResult == null)
                        {
                            Contract.Assert(tupleResult.Item2 == PipResultStatus.Canceled || loggingContext.ErrorWasLogged, "Error should have been logged for dependency pip.");
                            return processRunnable.SetPipResult(tupleResult.Item2);
                        }

                        processRunnable.SetCacheableProcess(cacheableProcess);
                        processRunnable.SetCacheResult(cacheResult);

                        if (!IsDistributedWorker && !cacheResult.CanRunFromCache)
                        {
                            // It's a cache miss, update the counters.
                            Contract.Assert(cacheResult.CacheMissType != PipCacheMissType.Invalid, "Must have valid cache miss reason");
                            environment.Counters.IncrementCounter(cacheResult.CacheMissType.ToCounter());

                            if (pipRunTimeInfo.IsFrontierMissCandidate)
                            {
                                environment.Counters.IncrementCounter(cacheResult.CacheMissType.ToCounter().ToFrontierPipCacheMissCounter());
                            }

                            if (cacheResult.CacheMissType == PipCacheMissType.MissForProcessMetadata
                                || cacheResult.CacheMissType == PipCacheMissType.MissForProcessMetadataFromHistoricMetadata
                                || cacheResult.CacheMissType == PipCacheMissType.MissForProcessOutputContent)
                            {
                                pipRunTimeInfo.IsMissingContentImpacted = true;
                            }
                        }

                        using (operationContext.StartOperation(PipExecutorCounter.ReportRemoteMetadataDuration))
                        {
                            // It only executes on orchestrator; but we still acquire the slot on the worker.
                            if (cacheResult.CanRunFromCache && worker.IsRemote)
                            {
                                // We report the pathset to HistoricMetadataCache by using Orchestrator execution log target.
                                var cacheHitData = cacheResult.GetCacheHitData();
                                m_pipTwoPhaseCache.ReportRemoteMetadata(
                                    cacheHitData.Metadata,
                                    cacheHitData.MetadataHash,
                                    cacheHitData.PathSetHash,
                                    cacheResult.WeakFingerprint,
                                    cacheHitData.StrongFingerprint,
                                    isExecution: false,
                                    process.PreservePathSetCasing);
                            }
                        }

                        if (cacheResult.CanRunFromCache)
                        {
                            return PipExecutionStep.RunFromCache;
                        }
                        else if (m_configuration.Schedule.CacheOnly)
                        {
                            // CacheOnly mode only wants to perform cache lookups and skip execution for pips that are misses
                            environment.Counters.IncrementCounter(PipExecutorCounter.ProcessPipsSkippedExecutionDueToCacheOnly);
                            PipRuntimeInfo pipRuntimeInfo = GetPipRuntimeInfo(pipId);
                            pipRuntimeInfo.Transition(m_pipStateCounters, pipType, PipState.Skipped);
                            return PipExecutionStep.Skip;
                        }
                        else
                        {
                            environment.Counters.IncrementCounter(PipExecutorCounter.ProcessPipsExecutedDueToCacheMiss);
                        }

                        return PipExecutionStep.ChooseWorkerCpu;
                    }

                case PipExecutionStep.RunFromCache:
                    {
                        Contract.Assert(processRunnable != null);

                        var pipScope = State.GetScope(process);
                        var executionResult = await PipExecutor.RunFromCacheWithWarningsAsync(operationContext, environment, pipScope, process, processRunnable.CacheResult, processRunnable.Description);

                        return processRunnable.SetPipResult(executionResult);
                    }

                case PipExecutionStep.ExecuteProcess:
                    {
                        MarkPipStartExecuting();

                        if (processRunnable.Weight > 1)
                        {
                            // Only log for pips with non-standard process weights
                            Logger.Log.ProcessPipProcessWeight(loggingContext, processRunnable.Description, processRunnable.Weight);
                        }

                        processRunnable.Executed = true;

                        var executionResult = await worker.ExecuteProcessAsync(processRunnable);

                        // Don't count service pips in process pip counters
                        if (!processRunnable.Process.IsStartOrShutdownKind && executionResult.PerformanceInformation != null)
                        {
                            var perfInfo = executionResult.PerformanceInformation;

                            try
                            {
                                m_groupedPipCounters.AddToCounters(processRunnable.Process,
                                    new[]
                                    {
                                        (PipCountersByGroup.IOReadBytes,  (long) perfInfo.IO.ReadCounters.TransferCount),
                                        (PipCountersByGroup.IOWriteBytes, (long) perfInfo.IO.WriteCounters.TransferCount)
                                    },
                                    new[] { (PipCountersByGroup.ExecuteProcessDuration, perfInfo.ProcessExecutionTime) }
                                );
                            }
                            catch (OverflowException ex)
                            {
                                Logger.Log.ExecutePipStepOverflowFailure(operationContext, ex.Message);

                                m_groupedPipCounters.AddToCounters(processRunnable.Process,
                                    new[] { (PipCountersByGroup.IOReadBytes, 0L), (PipCountersByGroup.IOWriteBytes, 0L) },
                                    new[] { (PipCountersByGroup.ExecuteProcessDuration, perfInfo.ProcessExecutionTime) }
                                );
                            }
                        }

                        // The pip was canceled due to retryable failure
                        if (executionResult.Result == PipResultStatus.Canceled && !IsTerminating)
                        {
                            Contract.Assert(executionResult.RetryInfo != null, $"Retry Information is required for all retry cases. IsTerminating: {m_scheduleTerminating}");
                            RetryReason retryReason = executionResult.RetryInfo.RetryReason;

                            if (worker.IsLocal)
                            {
                                // Because the scheduler will re-run this pip, we have to nuke all outputs created under shared opaque directories
                                var sharedOpaqueOutputs = FlagAndReturnScrubbableSharedOpaqueOutputs(environment, processRunnable);
                                if (!ScrubSharedOpaqueOutputs(operationContext, m_pipTable.GetPipSemiStableHash(pipId), sharedOpaqueOutputs))
                                {
                                    return runnablePip.SetPipResult(PipResultStatus.Failed);
                                }
                            }

                            // If it is a single machine or distributed build orchestrator
                            if (!IsDistributedBuild || IsDistributedOrchestrator)
                            {
                                if (retryReason == RetryReason.ResourceExhaustion)
                                {
                                    // Use the max of the observed memory and the worker's expected memory (multiplied with 1.25 to increase the expectations) for the pip
                                    var expectedCounters = processRunnable.ExpectedMemoryCounters.Value;
                                    var actualCounters = executionResult.PerformanceInformation?.MemoryCounters;

                                    processRunnable.ExpectedMemoryCounters = ProcessMemoryCounters.CreateFromMb(
                                        peakWorkingSetMb: Math.Max((int)(expectedCounters.PeakWorkingSetMb * 1.25), actualCounters?.PeakWorkingSetMb ?? 0),
                                        averageWorkingSetMb: Math.Max((int)(expectedCounters.AverageWorkingSetMb * 1.25), actualCounters?.AverageWorkingSetMb ?? 0),
                                        peakCommitSizeMb: Math.Max((int)(expectedCounters.PeakCommitSizeMb * 1.25), actualCounters?.PeakCommitSizeMb ?? 0),
                                        averageCommitSizeMb: Math.Max((int)(expectedCounters.AverageCommitSizeMb * 1.25), actualCounters?.AverageCommitSizeMb ?? 0));

                                    if (processRunnable.Performance.RetryCountDueToLowMemory == m_scheduleConfiguration.MaxRetriesDueToLowMemory)
                                    {
                                        Logger.Log.ExcessivePipRetriesDueToLowMemory(operationContext, processRunnable.Description, processRunnable.Performance.RetryCountDueToLowMemory);
                                        return runnablePip.SetPipResult(PipResultStatus.Failed);
                                    }
                                    else
                                    {
                                        Logger.Log.PipRetryDueToLowMemory(operationContext, processRunnable.Description, worker.DefaultWorkingSetMbPerProcess, expectedCounters.PeakWorkingSetMb, actualCounters?.PeakWorkingSetMb ?? 0);
                                    }
                                }
                                else if (retryReason.IsPreProcessExecOrRemotingInfraFailure())
                                {
                                    if (processRunnable.Performance.RetryCountDueToRetryableFailures == m_scheduleConfiguration.MaxRetriesDueToRetryableFailures)
                                    {
                                        Logger.Log.ExcessivePipRetriesDueToRetryableFailures(operationContext, processRunnable.Description,
                                            processRunnable.Performance.RetryCountDueToRetryableFailures, executionResult.RetryInfo.RetryReason.ToString());
                                        return runnablePip.SetPipResult(PipResultStatus.Failed);
                                    }
                                    else
                                    {
                                        Logger.Log.PipRetryDueToRetryableFailures(operationContext, processRunnable.Description, retryReason.ToString());
                                        if (retryReason == RetryReason.RemoteFallback)
                                        {
                                            // Force local execution.
                                            processRunnable.RunLocation = ProcessRunLocation.Local;
                                        }
                                    }
                                }
                                else if (retryReason == RetryReason.DistributionFailure)
                                {
                                    // When we cannot send the pip to the workers after some retries, we should try it on the orchestrator.
                                    processRunnable.MustRunOnOrchestrator = true;
                                }
                            }

                            return processRunnable.SetPipResult(executionResult.Result);
                        }

                        m_pipPropertyInfo.UpdatePipPropertyInfo(processRunnable, executionResult);
                        m_pipRetryInfo.UpdatePipRetryInfo(processRunnable, executionResult, PipExecutionCounters);

                        if (runnablePip.Worker?.IsRemote == true)
                        {
                            PipExecutionCounters.IncrementCounter(PipExecutorCounter.ProcessesExecutedRemotely);
                        }

                        if (processRunnable.CacheResult.CanRunFromCache)
                        {
                            return PipExecutionStep.RunFromCache;
                        }

                        return PipExecutionStep.PostProcess;
                    }

                case PipExecutionStep.PostProcess:
                    {
                        var executionResult = processRunnable.ExecutionResult;

                        if (executionResult.PerformanceInformation != null)
                        {
                            var perfInfo = executionResult.PerformanceInformation;
                            m_perPipPerformanceInfoStore.AddPip(new PerProcessPipPerformanceInformation(
                                ref processRunnable,
                                (int)perfInfo.ProcessExecutionTime.TotalMilliseconds,
                                perfInfo.MemoryCounters.PeakWorkingSetMb,
                                (int)Math.Ceiling(perfInfo.IO.ReadCounters.TransferCount / BytesInMb),
                                (int)Math.Ceiling(perfInfo.IO.WriteCounters.TransferCount / BytesInMb)));
                        }

                        // Make sure all shared outputs are flagged as such.
                        // We need to do this even if the pip failed, so any writes under shared opaques are flagged anyway.
                        // This allows the scrubber to remove those files as well in the next run.
                        var start = DateTime.UtcNow;
                        var sharedOpaqueOutputs = FlagAndReturnScrubbableSharedOpaqueOutputs(environment, processRunnable);
                        SandboxedProcessPipExecutor.LogSubPhaseDuration(operationContext, runnablePip.Pip, SandboxedProcessFactory.SandboxedProcessCounters.SchedulerPhaseFlaggingSharedOpaqueOutputs, DateTime.UtcNow.Subtract(start), $"(count: {sharedOpaqueOutputs.Count})");

                        // Set the process as executed. NOTE: We do this here rather than during ExecuteProcess to handle
                        // case of processes executed remotely
                        var pipScope = State.GetScope(processRunnable.Process);

                        bool pipIsSafeToCache = true;

                        IReadOnlyDictionary<FileArtifact, (FileMaterializationInfo, ReportedViolation)> allowedSameContentViolations = null;

                        if (!IsDistributedWorker)
                        {
                            var expectedMemoryCounters = processRunnable.ExpectedMemoryCounters.Value;

                            int peakWorkingSetMb = executionResult.PerformanceInformation?.MemoryCounters.PeakWorkingSetMb ?? 0;
                            int averageWorkingSetMb = executionResult.PerformanceInformation?.MemoryCounters.AverageWorkingSetMb ?? 0;
                            int peakCommitSizeMb = executionResult.PerformanceInformation?.MemoryCounters.PeakCommitSizeMb ?? 0;
                            int averageCommitSizeMb = executionResult.PerformanceInformation?.MemoryCounters.AverageCommitSizeMb ?? 0;

                            try
                            {
                                Logger.Log.ProcessPipExecutionInfo(
                                    operationContext,
                                    runnablePip.Description,
                                    executionResult.PerformanceInformation?.NumberOfProcesses ?? 0,
                                    (processRunnable.HistoricPerfData?.ExeDurationInMs ?? 0) / 1000.0,
                                    executionResult.PerformanceInformation?.ProcessExecutionTime.TotalSeconds ?? 0,
                                    executionResult.PerformanceInformation?.ProcessorsInPercents ?? 0,
                                    processRunnable.Weight,
                                    worker.DefaultWorkingSetMbPerProcess,
                                    expectedMemoryCounters.PeakWorkingSetMb,
                                    peakWorkingSetMb,
                                    expectedMemoryCounters.AverageWorkingSetMb,
                                    averageWorkingSetMb,
                                    expectedMemoryCounters.PeakCommitSizeMb,
                                    peakCommitSizeMb,
                                    expectedMemoryCounters.AverageCommitSizeMb,
                                    averageCommitSizeMb,
                                    (int)(processRunnable.HistoricPerfData?.DiskIOInMB ?? 0),
                                    (int)ByteSizeFormatter.BytesToMegabytes(executionResult.PerformanceInformation?.IO.GetAggregateIO().TransferCount ?? 0),
                                    (processRunnable.HistoricPerfData?.MaxExeDurationInMs ?? 0) / 1000.0);

                                m_totalPeakWorkingSetMb += (ulong)peakWorkingSetMb;
                                m_totalAverageWorkingSetMb += (ulong)averageWorkingSetMb;

                                m_totalPeakCommitSizeMb += (ulong)peakCommitSizeMb;
                                m_totalAverageCommitSizeMb += (ulong)averageCommitSizeMb;
                            }
                            catch (OverflowException ex)
                            {
                                Logger.Log.ExecutePipStepOverflowFailure(operationContext, ex.Message);
                            }

                            // File violation analysis needs to happen on the orchestrator as it relies on
                            // graph-wide data such as detecting duplicate
                            start = DateTime.UtcNow;
                            executionResult = PipExecutor.AnalyzeFileAccessViolations(
                                operationContext,
                                environment,
                                pipScope,
                                processRunnable.Process,
                                processRunnable.AllExecutionResults,
                                out pipIsSafeToCache,
                                out allowedSameContentViolations);
                            SandboxedProcessPipExecutor.LogSubPhaseDuration(operationContext, runnablePip.Pip, SandboxedProcessCounters.SchedulerPhaseAnalyzingFileAccessViolations, DateTime.UtcNow.Subtract(start));

                            processRunnable.SetExecutionResult(executionResult);

                            if (executionResult.Result.IndicatesFailure())
                            {
                                // Dependency analysis failure. Bail out before performing post processing. This prevents
                                // the output from being cached as well as downstream pips from being run.
                                return processRunnable.SetPipResult(executionResult);
                            }
                        }

                        if (pipIsSafeToCache)
                        {
                            // The worker should only cache the pip if the violation analyzer allows it to.
                            executionResult = await worker.PostProcessAsync(processRunnable);
                        }
                        else
                        {
                            Logger.Log.ScheduleProcessNotStoredToCacheDueToFileMonitoringViolations(loggingContext, processRunnable.Description);
                        }

                        // If the result converged we should delete shared opaque outputs where the execution happened. On convergence, the result
                        // will be consumed from the already cached pip and the just produced outputs should be absent.
                        if (executionResult.Converged && worker.IsLocal)
                        {
                            if (!ScrubSharedOpaqueOutputs(operationContext, m_pipTable.GetPipSemiStableHash(pipId), sharedOpaqueOutputs))
                            {
                                return runnablePip.SetPipResult(PipResultStatus.Failed);
                            }

                        }

                        if (!IsDistributedWorker)
                        {
                            // If the cache converged outputs, we need to check for double writes again, since the configured policy may care about
                            // the content of the (final) outputs
                            if (executionResult.Converged)
                            {
                                start = DateTime.UtcNow;
                                executionResult = PipExecutor.AnalyzeDoubleWritesOnCacheConvergence(
                                   operationContext,
                                   environment,
                                   pipScope,
                                   executionResult,
                                   processRunnable.Process,
                                   allowedSameContentViolations);
                                SandboxedProcessPipExecutor.LogSubPhaseDuration(operationContext, runnablePip.Pip, SandboxedProcessCounters.SchedulerPhaseAnalyzingDoubleWrites, DateTime.UtcNow.Subtract(start));

                                processRunnable.SetExecutionResult(executionResult);

                                if (executionResult.Result.IndicatesFailure())
                                {
                                    // Dependency analysis failure. Even though the pip is already cached, we got a cache converged event, so
                                    // it is safe for other downstream pips to consume the cached result. However, some double writes were found based
                                    // on the configured policy, so we fail the build
                                    return processRunnable.SetPipResult(executionResult);
                                }
                            }
                        }

                        if (runnablePip.Worker?.IsRemote == true)
                        {
                            m_pipTwoPhaseCache.ReportRemoteMetadata(
                                executionResult.PipCacheDescriptorV2Metadata,
                                executionResult.TwoPhaseCachingInfo?.CacheEntry.MetadataHash,
                                executionResult.TwoPhaseCachingInfo?.PathSetHash,
                                executionResult.TwoPhaseCachingInfo?.WeakFingerprint,
                                executionResult.TwoPhaseCachingInfo?.StrongFingerprint,
                                isExecution: !executionResult.Converged,
                                preservePathCasing: processRunnable.Process.PreservePathSetCasing);
                        }

                        // Output content is reported here to ensure that it happens both on worker executing PostProcess and
                        // orchestrator which called worker to execute post process.
                        start = DateTime.UtcNow;
                        PipExecutor.ReportExecutionResultOutputContent(
                            operationContext,
                            environment,
                            processRunnable.Pip.SemiStableHash,
                            executionResult,
                            processRunnable.Process.RewritePolicy.ImpliesDoubleWriteIsWarning());
                        SandboxedProcessPipExecutor.LogSubPhaseDuration(operationContext, runnablePip.Pip, SandboxedProcessCounters.SchedulerPhaseReportingOutputContent, DateTime.UtcNow.Subtract(start), $"(num outputs: {executionResult.OutputContent.Length})");
                        return processRunnable.SetPipResult(executionResult);
                    }

                case PipExecutionStep.HandleResult:
                    await OnPipCompleted(runnablePip);
                    return PipExecutionStep.Done;

                default:
                    throw Contract.AssertFailure(I($"Do not know how to run this pip step: '{step}'"));
            }
        }

        private bool ShouldCancelPip(RunnablePip runnablePip)
        {
            return IsTerminating && runnablePip.Step != PipExecutionStep.Start && GetPipRuntimeInfo(runnablePip.PipId).State == PipState.Running && !runnablePip.IsCancelled;
        }

        private List<string> FlagAndReturnScrubbableSharedOpaqueOutputs(IPipExecutionEnvironment environment, ProcessRunnablePip process)
        {
            List<string> outputPaths = new List<string>();

            // Select all declared output files that are not source rewrites (and therefore scrubbable, we don't want to flag what was a source file as a shared opaque
            // since we don't want to delete it next time)
            foreach (var fileArtifact in process.Process.FileOutputs.Where(fa => !fa.IsUndeclaredFileRewrite))
            {
                MakeSharedOpaqueOutputIfNeeded(fileArtifact.Path);

                if (IsPathUnderSharedOpaqueDirectory(fileArtifact.Path))
                {
                    // We do not want to mark the shared opaque outputs when SkipFlaggingSharedOpaqueOutputs is set to true; however, we want to return the list of those ouputs
                    // so that we can scrub them in case of cache convergence or cancellation (retry).
                    outputPaths.Add(fileArtifact.Path.ToString(Context.PathTable));
                }
            }

            // The shared dynamic accesses can be null when the pip failed on preparation, in which case it didn't run at all, so there is
            // nothing to flag
            if (process.ExecutionResult?.SharedDynamicDirectoryWriteAccesses != null)
            {
                // Directory outputs are reported only when the pip is successful. So we need to rely on the raw shared dynamic write accesses,
                // since flagging also happens on failed pips
                foreach (IReadOnlyCollection<FileArtifactWithAttributes> writesPerSharedOpaque in process.ExecutionResult.SharedDynamicDirectoryWriteAccesses.Values)
                {
                    // Only add the files that are not source rewrites (and therefore scrubbable, we don't want to flag what was a source file as a shared opaque
                    // since we don't want to delete it next time)
                    foreach (FileArtifactWithAttributes writeInPath in writesPerSharedOpaque.Where(fa => !fa.IsUndeclaredFileRewrite))
                    {
                        var path = writeInPath.Path.ToString(environment.Context.PathTable);

                        if (!environment.Configuration.Sandbox.UnsafeSandboxConfiguration.SkipFlaggingSharedOpaqueOutputs())
                        {
                            SharedOpaqueOutputHelper.EnforceFileIsSharedOpaqueOutput(path);
                        }

                        // We do not want to mark the shared opaque outputs when SkipFlaggingSharedOpaqueOutputs is set to true; however, we want to return the list of those ouputs
                        // so that we can scrub them in case of cache convergence or cancellation (retry).
                        outputPaths.Add(path);
                    }
                }
            }

            return outputPaths;
        }

        private bool ScrubSharedOpaqueOutputs(LoggingContext loggingContext, long pipSemiStableHash, List<string> outputs)
        {
            foreach (string o in outputs)
            {
                try
                {
                    FileUtilities.DeleteFile(o);
                }
                catch (BuildXLException ex)
                {
                    Logger.Log.PipFailedSharedOpaqueOutputsCleanup(loggingContext, pipSemiStableHash, o, ex.LogEventMessage);
                    return false;
                }
            }

            return true;
        }

        private PipState TryStartPip(RunnablePip runnablePip)
        {
            if (Interlocked.CompareExchange(ref m_firstPip, 1, 0) == 0)
            {
                // Time to first pip only has meaning if we know when the process started
                if (m_processStartTimeUtc.HasValue)
                {
                    m_schedulerStartedTimeUtc = DateTime.UtcNow;
                    m_timeToFirstPip = m_schedulerStartedTimeUtc - m_processStartTimeUtc.Value;
                    LogStatistic(
                        m_executePhaseLoggingContext,
                        Statistics.TimeToFirstPipMs,
                        (int)m_timeToFirstPip.TotalMilliseconds);
                }
            }

            PipId pipId = runnablePip.PipId;
            PipType pipType = runnablePip.PipType;
            PipRuntimeInfo pipRuntimeInfo = GetPipRuntimeInfo(pipId);
            PipState state = pipRuntimeInfo.State;

            m_executionStepTracker.Transition(pipId, PipExecutionStep.Start);

            if (state != PipState.Skipped)
            {
                pipRuntimeInfo.Transition(m_pipStateCounters, pipType, PipState.Running);
            }

            // PipState is either Skipped or Running at this point
            state = pipRuntimeInfo.State;
            Contract.Assume(state == PipState.Skipped || state == PipState.Running);

            return state;
        }

        private async Task<PipResult> ExecuteNonProcessPipAsync(RunnablePip runnablePip)
        {
            Contract.Requires(runnablePip.Pip != null);
            Contract.Requires(runnablePip.OperationContext.IsValid);
            Contract.Requires(runnablePip.Environment != null);

            var pip = runnablePip.Pip;
            var operationContext = runnablePip.OperationContext;
            var environment = runnablePip.Environment;

            switch (runnablePip.PipType)
            {
                case PipType.SealDirectory:
                    // SealDirectory pips are also scheduler internal. Once completed, we can unblock consumers of the corresponding DirectoryArtifact
                    // and mark the contained paths as immutable (thus no longer requiring a rewrite count).
                    return ExecuteSealDirectoryPip(operationContext, environment, (SealDirectory)pip);

                case PipType.Value:
                case PipType.SpecFile:
                case PipType.Module:
                    // Value, specfile, and module pips are noop.
                    return PipResult.CreateWithPointPerformanceInfo(PipResultStatus.Succeeded);

                case PipType.WriteFile:
                    // Don't materialize eagerly (this is handled by the MaterializeOutputs step)
                    return
                        await
                            PipExecutor.ExecuteWriteFileAsync(
                                operationContext,
                                environment,
                                (WriteFile)pip,
                                materializeOutputs: !m_configuration.Schedule.EnableLazyWriteFileMaterialization);

                case PipType.CopyFile:
                    // Don't materialize eagerly (this is handled by the MaterializeOutputs step)
                    return await PipExecutor.ExecuteCopyFileAsync(operationContext, environment, (CopyFile)pip, materializeOutputs: false);

                case PipType.Ipc:
                    var result = await runnablePip.Worker.ExecuteIpcAsync(runnablePip);
                    if (!result.Status.IndicatesFailure())
                    {
                        // Output content is reported here to ensure that it happens both on worker executing IPC pip and
                        // orchestrator which called worker to execute IPC pip.
                        PipExecutor.ReportExecutionResultOutputContent(
                            runnablePip.OperationContext,
                            runnablePip.Environment,
                            runnablePip.Pip.SemiStableHash,
                            runnablePip.ExecutionResult);
                    }

                    return result;

                default:
                    throw Contract.AssertFailure("Do not know how to run pip " + pip);
            }
        }

        /// <summary>
        /// Returns whether a node is explicitly scheduled.
        /// </summary>
        /// <remarks>
        /// All nodes are explicitly scheduled unless a filter is applied that does not match the node.
        /// </remarks>
        private bool RequiresPipOutputs(NodeId node)
        {
            // For minimal required materialization, no pip's outputs are required.
            if (m_scheduleConfiguration.RequiredOutputMaterialization == RequiredOutputMaterialization.Minimal)
            {
                return false;
            }

            if (m_scheduleConfiguration.RequiredOutputMaterialization == RequiredOutputMaterialization.All)
            {
                return true;
            }

            // When all nodes are scheduled, the collection is null and all nodes are matched
            if (m_explicitlyScheduledNodes == null)
            {
                return true;
            }

            // Otherwise the node must be checked
            return m_explicitlyScheduledNodes.Contains(node);
        }

        /// <summary>
        /// Chooses a worker to execute the process pips
        /// </summary>
        private Worker ChooseWorkerCpu(ProcessRunnablePip runnablePip)
        {
            Contract.Requires(runnablePip.PipType == PipType.Process);

            using (PipExecutionCounters.StartStopwatch(PipExecutorCounter.ChooseWorkerCpuDuration))
            {
                // Only if there is no historic perf data associated with the process,
                // lookup the historic perf data table.
                if (runnablePip.HistoricPerfData == null)
                {
                    var perfData = HistoricPerfDataTable[runnablePip];
                    if (perfData != ProcessPipHistoricPerfData.Empty)
                    {
                        var memoryCounters = perfData.MemoryCounters;
                        if (memoryCounters.AverageWorkingSetMb == 0 || memoryCounters.PeakWorkingSetMb == 0)
                        {
                            Interlocked.Increment(ref m_historicPerfDataZeroMemoryHits);
                        }
                        else
                        {
                            Interlocked.Increment(ref m_historicPerfDataNonZeroMemoryHits);
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref m_historicPerfDataMisses);
                    }

                    // Update even if it's a miss (so the data will be Empty rather than null):
                    // We don't want to keep checking the HistoricPerfDataTable if we can't acquire the worker
                    // in the subsequent logic and we have to retry later.
                    runnablePip.HistoricPerfData = perfData;
                }

                // Find the estimated setup time for the pip on each builder.
                return m_chooseWorkerCpu.ChooseWorker(runnablePip);
            }
        }

        /// <inheritdoc />
        public PipExecutionContext Context { get; }

        /// <inheritdoc />
        public bool HasFailed => m_hasFailures;

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        bool IPipExecutionEnvironment.MaterializeOutputsInBackground => MaterializeOutputsInBackground;

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        bool IPipExecutionEnvironment.IsTerminating => IsTerminating;

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        bool IPipExecutionEnvironment.IsTerminatingWithInternalError => m_scheduleTerminatingWithInternalError;

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        SchedulerTestHooks IPipExecutionEnvironment.SchedulerTestHooks => m_testHooks;

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        CancellationToken IPipExecutionEnvironment.SchedulerCancellationToken => m_schedulerCancellationTokenSource.Token;

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        bool IPipExecutionEnvironment.InputsLazilyMaterialized => InputsLazilyMaterialized;

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        PipTable IPipExecutionEnvironment.PipTable => m_pipTable;

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        PipContentFingerprinter IPipExecutionEnvironment.ContentFingerprinter => m_pipContentFingerprinter;

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        PipFragmentRenderer IPipExecutionEnvironment.PipFragmentRenderer => m_pipFragmentRenderer;

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        IIpcProvider IPipExecutionEnvironment.IpcProvider => m_ipcProvider;

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        PluginManager IPipExecutionEnvironment.PluginManager => m_pluginManager;

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        IConfiguration IPipExecutionEnvironment.Configuration => m_configuration;

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        IReadOnlyDictionary<string, string> IPipExecutionEnvironment.RootMappings => m_rootMappings;

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        ISandboxConnection IPipExecutionEnvironment.SandboxConnection => !UnixSandboxingEnabled ? null : SandboxConnection;

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        bool IPipExecutionEnvironment.TryGetProducerPip(in FileOrDirectoryArtifact artifact, out PipId producer)
        {
            producer = PipGraph.TryGetProducer(in artifact);
            return producer.IsValid;
        }

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        bool IPipExecutionEnvironment.IsReachableFrom(PipId from, PipId to)
        {
            return PipGraph.IsReachableFrom(from: from.ToNodeId(), to: to.ToNodeId());
        }

        /// <summary>
        /// Content and metadata cache for prior pip outputs.
        /// </summary>
        public EngineCache Cache { get; }

        /// <inheritdoc />
        public LocalDiskContentStore LocalDiskContentStore => m_localDiskContentStore;

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        IFileMonitoringViolationAnalyzer IPipExecutionEnvironment.FileMonitoringViolationAnalyzer => m_fileMonitoringViolationAnalyzer;

        /// <inheritdoc />
        public int GetPipPriority(PipId pipId)
        {
            Contract.Requires(pipId.IsValid);
            Contract.Assert(IsInitialized);

            var pipState = m_pipTable.GetMutable(pipId);

            if (pipState.PipType.IsMetaPip())
            {
                // Meta pips are used for reporting and should run ASAP
                // Give them a maximum priority.
                return MaxInitialPipPriority;
            }

            return GetPipRuntimeInfo(pipId).Priority;
        }

        /// <inheritdoc />
        public DirectoryTranslator DirectoryTranslator { get; }

        /// <inheritdoc />
        public IReadOnlySet<AbsolutePath> TranslatedGlobalUnsafeUntrackedScopes { get; }

        /// <inheritdoc />
        public PipSpecificPropertiesConfig PipSpecificPropertiesConfig { get; set; }

        /// <summary>
        /// Gets the execution information for the producer pip of the given file.
        /// </summary>
        public string GetProducerInfoForFailedMaterializeFile(in FileArtifact artifact)
        {
            var producer = m_fileContentManager.GetDeclaredProducer(artifact);

            PipExecutionStep step = PipExecutionStep.RunFromCache;
            uint workerId = 0;

            if (m_runnablePipPerformance.TryGetValue(producer.PipId, out var perfInfo))
            {
                step = perfInfo.IsExecuted ? PipExecutionStep.ExecuteProcess : step;
                workerId = perfInfo.Workers.GetOrDefault(step, (uint)0);
            }

            var worker = m_workers[(int)workerId];
            bool isWorkerReleasedEarly = worker.WorkerEarlyReleasedTime != null;

            PipExecutionCounters.IncrementCounter(PipExecutorCounter.NumFilesFailedToMaterialize);
            if (isWorkerReleasedEarly)
            {
                PipExecutionCounters.IncrementCounter(PipExecutorCounter.NumFilesFailedToMaterializeDueToEarlyWorkerRelease);
            }

            string whenWorkerReleased = isWorkerReleasedEarly ?
                $"UTC {worker.WorkerEarlyReleasedTime.Value.ToLongTimeString()} ({(DateTime.UtcNow - worker.WorkerEarlyReleasedTime.Value).TotalMinutes.ToString("0.0")} minutes ago)" :
                "N/A";

            return $"{producer.FormattedSemiStableHash} {step} on Worker #{workerId} - {worker.Name} ({worker.Status} - WhenReleased: {whenWorkerReleased})";
        }

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        bool IPipExecutionEnvironment.IsSourceSealedDirectory(
            DirectoryArtifact directoryArtifact,
            out bool allDirectories,
            out ReadOnlyArray<StringId> pattern)
        {
            Contract.Requires(directoryArtifact.IsValid);
            pattern = ReadOnlyArray<StringId>.Empty;
            var sealDirectoryKind = GetSealDirectoryKind(directoryArtifact);

            if (sealDirectoryKind.IsSourceSeal())
            {
                pattern = GetSourceSealDirectoryPatterns(directoryArtifact);
            }

            switch (sealDirectoryKind)
            {
                case SealDirectoryKind.SourceAllDirectories:
                    allDirectories = true;
                    return true;
                case SealDirectoryKind.SourceTopDirectoryOnly:
                    allDirectories = false;
                    return true;
                default:
                    allDirectories = false;
                    return false;
            }
        }

        /// <inheritdoc />
        public IEnumerable<Pip> GetServicePipClients(PipId servicePipId) => PipGraph.GetServicePipClients(servicePipId);

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        void IPipExecutionEnvironment.ReportWarnings(bool fromCache, int count)
        {
            Contract.Requires(count > 0);
            if (fromCache)
            {
                Interlocked.Increment(ref m_numPipsWithWarningsFromCache);
                Interlocked.Add(ref m_numWarningsFromCache, count);
            }
            else
            {
                Interlocked.Increment(ref m_numPipsWithWarnings);
                Interlocked.Add(ref m_numWarnings, count);
            }
        }

        #region Critical Path Logging

        private void LogCriticalPath(Dictionary<string, long> statistics, [AllowNull] BuildSummary buildSummary)
        {
            int currentCriticalPathTailPipIdValue;
            PipRuntimeInfo criticalPathRuntimeInfo;
            var builder = new StringBuilder();

            List<(PipRuntimeInfo pipRunTimeInfo, PipId pipId)> criticalPath = new List<(PipRuntimeInfo pipRunTimeInfo, PipId pipId)>();

            if (TryGetCriticalPathTailRuntimeInfo(out currentCriticalPathTailPipIdValue, out criticalPathRuntimeInfo))
            {
                PipExecutionCounters.AddToCounter(PipExecutorCounter.CriticalPathDuration, TimeSpan.FromMilliseconds(criticalPathRuntimeInfo.CriticalPathDurationMs));

                PipId pipId = new PipId(unchecked((uint)currentCriticalPathTailPipIdValue));
                criticalPath.Add((criticalPathRuntimeInfo, pipId));

                while (true)
                {
                    criticalPathRuntimeInfo = null;
                    foreach (var dependencyEdge in ScheduledGraph.GetIncomingEdges(pipId.ToNodeId()))
                    {
                        var dependencyRuntimeInfo = GetPipRuntimeInfo(dependencyEdge.OtherNode);
                        if (dependencyRuntimeInfo.CriticalPathDurationMs >= (criticalPathRuntimeInfo?.CriticalPathDurationMs ?? 0))
                        {
                            criticalPathRuntimeInfo = dependencyRuntimeInfo;
                            pipId = dependencyEdge.OtherNode.ToPipId();
                        }
                    }

                    if (criticalPathRuntimeInfo != null)
                    {
                        criticalPath.Add((criticalPathRuntimeInfo, pipId));
                    }
                    else
                    {
                        break;
                    }
                }

                IList<long> totalOrchestratorQueueDurations = new long[(int)DispatcherKind.Materialize + 1];
                IList<long> totalRemoteQueueDurations = new long[(int)PipExecutionStep.Done + 1];
                IList<long> totalStepDurations = new long[(int)PipExecutionStep.Done + 1];

                IList<long> totalPipBuildRequestQueueDurations = new long[(int)PipExecutionStep.Done + 1];
                IList<long> totalPipBuildRequestGrpcDurations = new long[(int)PipExecutionStep.Done + 1];

                IList<long> totalBeforeExecutionCacheStepDurations = new long[OperationKind.TrackedCacheLookupCounterCount];
                IList<long> totalAfterExecutionCacheStepDurations = new long[OperationKind.TrackedCacheLookupCounterCount];

                long totalCacheMissAnalysisDuration = 0, totalSuspendedDuration = 0, totalRetryCount = 0, totalPushOutputsToCacheDuration = 0, totalRetryDuration = 0;

                var summaryTable = new StringBuilder();
                var detailedLog = new StringBuilder();
                detailedLog.AppendLine(I($"Fine-grained Duration (ms) for Each Pip on the Critical Path (from end to beginning)"));

                int index = 0;
                Func<long, string> addMin = (duration) => $"{duration}ms ({Math.Round(duration / (double)60000, 1)}m)";

                long exeDurationCriticalPathMs = 0;
                long pipDurationCriticalPathMs = 0;

                foreach (var node in criticalPath)
                {
                    if (!m_runnablePipPerformance.ContainsKey(node.pipId))
                    {
                        continue;
                    }

                    RunnablePipPerformanceInfo performance = m_runnablePipPerformance[node.pipId];

                    LogPipPerformanceInfo(detailedLog, node.pipId, performance);

                    Pip pip = PipGraph.GetPipFromPipId(node.pipId);
                    PipRuntimeInfo runtimeInfo = node.pipRunTimeInfo;

                    long pipDurationMs = performance.CalculatePipDurationMs(this);
                    long pipQueueDurationMs = performance.CalculateQueueDurationMs();

                    pipDurationCriticalPathMs += pipDurationMs;
                    exeDurationCriticalPathMs += runtimeInfo.ProcessExecuteTimeMs;

                    Logger.Log.CriticalPathPipRecord(m_executePhaseLoggingContext,
                        pipSemiStableHash: pip.SemiStableHash,
                        pipDescription: pip.GetDescription(Context),
                        pipDurationMs: pipDurationMs,
                        exeDurationMs: runtimeInfo.ProcessExecuteTimeMs,
                        queueDurationMs: pipQueueDurationMs,
                        indexFromBeginning: criticalPath.Count - index - 1,
                        isExplicitlyScheduled: (m_explicitlyScheduledProcessNodes == null ? false : m_explicitlyScheduledProcessNodes.Contains(node.Item2.ToNodeId())),
                        executionLevel: runtimeInfo.Result.ToString(),
                        numCacheEntriesVisited: performance.CacheLookupPerfInfo.NumCacheEntriesVisited,
                        numPathSetsDownloaded: performance.CacheLookupPerfInfo.NumPathSetsDownloaded);

                    Func<TimeSpan, string> formatTime = (t) => string.Format("{0:hh\\:mm\\:ss}", t);

                    string scheduledTime = "N/A";
                    string completedTime = "N/A";
                    TimeSpan scheduledTimeTs = TimeSpan.Zero;
                    TimeSpan completedTimeTs = TimeSpan.Zero;

                    if (m_processStartTimeUtc.HasValue)
                    {
                        scheduledTimeTs = performance.ScheduleTime - m_processStartTimeUtc.Value;
                        scheduledTime = formatTime(scheduledTimeTs);
                        completedTimeTs = performance.CompletedTime - m_processStartTimeUtc.Value;
                        completedTime = formatTime(completedTimeTs);
                    }

                    summaryTable.AppendLine(I($"{addMin(pipDurationMs),20} | {addMin(runtimeInfo.ProcessExecuteTimeMs),20} | {addMin(pipQueueDurationMs),20} | {runtimeInfo.Result,12} | {scheduledTime,14} | {completedTime,14} | {pip.GetDescription(Context)}"));

                    if (buildSummary != null)
                    {
                        buildSummary.CriticalPathSummary.Lines.Add(
                            new CriticalPathSummaryLine
                            {
                                PipDuration = TimeSpan.FromMilliseconds(pipDurationMs),
                                ProcessExecuteTime = TimeSpan.FromMilliseconds(runtimeInfo.ProcessExecuteTimeMs),
                                PipQueueDuration = TimeSpan.FromMilliseconds(pipQueueDurationMs),
                                Result = runtimeInfo.Result.ToString(),
                                ScheduleTime = scheduledTimeTs,
                                Completed = completedTimeTs,
                                PipDescription = pip.GetDescription(Context),
                            });
                    }

                    UpdateDurationList(totalStepDurations, performance.StepDurations);
                    UpdateDurationList(totalRemoteQueueDurations, performance.RemoteQueueDurations);
                    UpdateDurationList(totalPipBuildRequestGrpcDurations, performance.PipBuildRequestGrpcDurations);
                    UpdateDurationList(totalPipBuildRequestQueueDurations, performance.PipBuildRequestQueueDurations);
                    UpdateDurationList(totalOrchestratorQueueDurations, performance.QueueDurations);

                    totalBeforeExecutionCacheStepDurations = totalBeforeExecutionCacheStepDurations
                        .Zip(performance.CacheLookupPerfInfo.BeforeExecutionCacheStepCounters, (x, y) => (x + (long)(new TimeSpan(y.durationTicks).TotalMilliseconds))).ToList();
                    totalAfterExecutionCacheStepDurations = totalAfterExecutionCacheStepDurations
                        .Zip(performance.CacheLookupPerfInfo.AfterExecutionCacheStepCounters, (x, y) => (x + (long)(new TimeSpan(y.durationTicks).TotalMilliseconds))).ToList();

                    totalCacheMissAnalysisDuration += (long)performance.CacheMissAnalysisDuration.TotalMilliseconds;
                    totalSuspendedDuration += performance.SuspendedDurationMs;
                    totalRetryDuration += performance.RetryDurationMs;
                    totalRetryCount += performance.RetryCount;
                    totalPushOutputsToCacheDuration += performance.PushOutputsToCacheDurationMs;

                    index++;
                }

                // Putting logs together - a summary table followed by a detailed log for each pip


                string hr = I($"{Environment.NewLine}======================================================================{Environment.NewLine}");

                builder.AppendLine(I($"Fine-grained Duration (ms) for Top 5 Pips Sorted by Pip Duration"));
                var topPipDurations =
                    (from a in m_runnablePipPerformance
                     let i = a.Value.CalculatePipDurationMs(this)
                     where i > 0
                     orderby i descending
                     select a).Take(5);

                foreach (var kvp in topPipDurations)
                {
                    LogPipPerformanceInfo(builder, kvp.Key, kvp.Value);
                }

                builder.AppendLine(hr);
                builder.AppendLine(I($"Fine-grained Duration (ms) for Top 5 Pips Sorted by CacheLookup Duration"));
                var topCacheLookupDurations =
                    (from a in m_runnablePipPerformance
                     let i = a.Value.StepDurations.GetOrDefault(PipExecutionStep.CacheLookup, new TimeSpan()).TotalMilliseconds
                     where i > 0
                     orderby i descending
                     select a).Take(5);

                foreach (var kvp in topCacheLookupDurations)
                {
                    LogPipPerformanceInfo(builder, kvp.Key, kvp.Value);
                }

                builder.AppendLine(hr);
                builder.AppendLine(I($"Fine-grained Duration (ms) for Top 5 Pips Sorted by ExecuteProcess Duration"));
                var topExecuteDurations =
                    (from a in m_runnablePipPerformance
                     let i = a.Value.StepDurations.GetOrDefault(PipExecutionStep.ExecuteProcess, new TimeSpan()).TotalMilliseconds
                     where i > 0
                     orderby i descending
                     select a).Take(5);

                foreach (var kvp in topExecuteDurations)
                {
                    LogPipPerformanceInfo(builder, kvp.Key, kvp.Value);
                }

                builder.AppendLine(hr);

                builder.AppendLine("Critical path:");
                builder.AppendLine(I($"{"Pip Duration",-20} | {"Exe Duration",-20}| {"Queue Duration",-20} | {"Pip Result",-12} | {"Scheduled Time",-14} | {"Completed Time",-14} | Pip"));

                // Total critical path running time is a sum of all steps except ChooseWorker and MaterializeOutput (if it is done in background)
                long totalCriticalPathRunningTime = totalStepDurations.Where((i, j) => ((PipExecutionStep)j).IncludeInRunningTime(this)).Sum();
                long totalOrchestratorQueueTime = totalOrchestratorQueueDurations.Sum();

                builder.AppendLine(I($"{addMin(pipDurationCriticalPathMs),20} | {addMin(exeDurationCriticalPathMs),20} | {addMin(totalOrchestratorQueueTime),20} | {string.Empty,12} | {string.Empty,14} | {string.Empty,14} | *Total"));
                builder.AppendLine(summaryTable.ToString());

                if (buildSummary != null)
                {
                    buildSummary.CriticalPathSummary.TotalCriticalPathRuntime = TimeSpan.FromMilliseconds(totalCriticalPathRunningTime);
                    buildSummary.CriticalPathSummary.ExeDurationCriticalPath = TimeSpan.FromMilliseconds(exeDurationCriticalPathMs);
                    buildSummary.CriticalPathSummary.TotalOrchestratorQueueTime = TimeSpan.FromMilliseconds(totalOrchestratorQueueTime);
                }

                builder.AppendLine(detailedLog.ToString());

                statistics.Add("CriticalPath.TotalOrchestratorQueueDurationMs", totalOrchestratorQueueTime);
                for (int i = 0; i < totalOrchestratorQueueDurations.Count; i++)
                {
                    if (totalOrchestratorQueueDurations[i] != 0)
                    {
                        statistics.Add(I($"CriticalPath.{(DispatcherKind)i}_OrchestratorQueueDurationMs"), totalOrchestratorQueueDurations[i]);
                    }
                }

                statistics.Add("CriticalPath.TotalRemoteQueueDurationMs", totalRemoteQueueDurations.Sum());
                for (int i = 0; i < totalRemoteQueueDurations.Count; i++)
                {
                    statistics.Add(I($"CriticalPath.{(PipExecutionStep)i}_RemoteQueueDurationMs"), totalRemoteQueueDurations[i]);

                }

                for (int i = 0; i < totalStepDurations.Count; i++)
                {
                    var step = (PipExecutionStep)i;
                    if (step != PipExecutionStep.MaterializeOutputs || !MaterializeOutputsInBackground)
                    {
                        statistics.Add(I($"CriticalPath.{step}DurationMs"), totalStepDurations[i]);
                    }
                }

                for (int i = 0; i < totalBeforeExecutionCacheStepDurations.Count; i++)
                {
                    var duration = totalBeforeExecutionCacheStepDurations[i];
                    var name = OperationKind.GetTrackedCacheOperationKind(i).ToString();
                    statistics.Add(I($"CriticalPath.BeforeExecution_{name}DurationMs"), duration);
                }

                for (int i = 0; i < totalAfterExecutionCacheStepDurations.Count; i++)
                {
                    var duration = totalAfterExecutionCacheStepDurations[i];
                    var name = OperationKind.GetTrackedCacheOperationKind(i).ToString();
                    statistics.Add(I($"CriticalPath.AfterExecution_{name}DurationMs"), duration);
                }

                statistics.Add("CriticalPath.TotalQueueRequestDurationMs", totalPipBuildRequestQueueDurations.Sum());
                statistics.Add("CriticalPath.TotalGrpcDurationMs", totalPipBuildRequestGrpcDurations.Sum());

                long totalChooseWorker = totalStepDurations[(int)PipExecutionStep.ChooseWorkerCpu] + totalStepDurations[(int)PipExecutionStep.ChooseWorkerCacheLookup] + totalStepDurations[(int)PipExecutionStep.ChooseWorkerIpc];
                statistics.Add("CriticalPath.ChooseWorkerDurationMs", totalChooseWorker);

                statistics.Add("CriticalPath.CacheMissAnalysisDurationMs", totalCacheMissAnalysisDuration);
                statistics.Add("CriticalPath.TotalSuspendedDurationMs", totalSuspendedDuration);
                statistics.Add("CriticalPath.TotalRetryDurationMs", totalRetryDuration);
                statistics.Add("CriticalPath.TotalRetryCount", totalRetryCount);
                statistics.Add("CriticalPath.TotalPushOutputsToCacheDurationMs", totalPushOutputsToCacheDuration);
                statistics.Add("CriticalPath.ExeDurationMs", exeDurationCriticalPathMs);
                statistics.Add("CriticalPath.PipDurationMs", totalCriticalPathRunningTime);

                long materializeOutputOverhangMs = m_schedulerCompletionExceptMaterializeOutputsTimeUtc.HasValue ? (long)(m_schedulerDoneTimeUtc - m_schedulerCompletionExceptMaterializeOutputsTimeUtc.Value).TotalMilliseconds : 0;
                statistics.Add("CriticalPath.MaterializeOutputOverhangMs", materializeOutputOverhangMs);

                builder.AppendLine(hr);

                long schedulerDurationMs = (long)(m_schedulerDoneTimeUtc - m_schedulerStartedTimeUtc).TotalMilliseconds;
                long criticalPathMs = totalOrchestratorQueueTime + totalChooseWorker + totalCriticalPathRunningTime;

                logDuration("Time to First Pip", (long)m_timeToFirstPip.TotalMilliseconds);
                logDuration("Scheduler", schedulerDurationMs);
                logDuration("Total Critical Path Length", criticalPathMs, indentLevel: 1);
                for (int i = 0; i < totalStepDurations.Count; i++)
                {
                    var step = (PipExecutionStep)i;
                    if (step != PipExecutionStep.MaterializeOutputs || !MaterializeOutputsInBackground)
                    {
                        logDuration($"PipExecutionStep.{(step)}", totalStepDurations[i], indentLevel: 2);
                        logDuration($"PipBuildRequest Queue", totalPipBuildRequestQueueDurations[i], indentLevel: 3);
                        logDuration($"PipBuildRequest Send/Receive (gRPC)", totalPipBuildRequestGrpcDurations[i], indentLevel: 3);
                        logDuration($"Dispatcher Queue on RemoteWorker", totalRemoteQueueDurations[i], indentLevel: 3);

                        if (step == PipExecutionStep.CacheLookup)
                        {
                            for (int j = 0; j < totalBeforeExecutionCacheStepDurations.Count; j++)
                            {
                                logDuration(OperationKind.GetTrackedCacheOperationKind(j).ToString(), totalBeforeExecutionCacheStepDurations[j], indentLevel: 3);
                            }
                        }

                        if (step == PipExecutionStep.ExecuteProcess)
                        {
                            logDuration("Push Outputs to Cache", totalPushOutputsToCacheDuration, indentLevel: 3);
                            logDuration("Suspend due to Memory", totalSuspendedDuration, indentLevel: 3);
                            logDuration("Retry Duration", totalRetryDuration, indentLevel: 3);
                            logDuration("Retry Count", totalRetryCount, indentLevel: 3);

                            for (int j = 0; j < totalAfterExecutionCacheStepDurations.Count; j++)
                            {
                                logDuration(OperationKind.GetTrackedCacheOperationKind(j).ToString(), totalAfterExecutionCacheStepDurations[j], indentLevel: 3);
                            }
                        }
                    }
                }

                for (int i = 0; i < totalOrchestratorQueueDurations.Count; i++)
                {
                    logDuration($"Dispatcher.{((DispatcherKind)i)} Queue", totalOrchestratorQueueDurations[i], indentLevel: 2);
                }

                logDuration("Non-Critical Path", schedulerDurationMs - criticalPathMs, indentLevel: 1);
                logDuration("MaterializeOutput Overhang", materializeOutputOverhangMs, indentLevel: 2);

                logDuration("Post Scheduler Tasks", (long)PipExecutionCounters.GetElapsedTime(PipExecutorCounter.AfterDrainingWhenDoneDuration).TotalMilliseconds);

                Logger.Log.CriticalPathChain(m_executePhaseLoggingContext, builder.ToString());
            }

            void logDuration(string desc, long durationMs, int indentLevel = 0)
            {
                long durationSec = durationMs / 1000;
                if (durationSec > 0)
                {
                    desc = "".PadLeft(indentLevel * 4) + desc;
                    builder.AppendLine($"{desc,-120}:{durationSec,10}s");
                }
            }
        }

        private void LogPipPerformanceInfo(StringBuilder stringBuilder, PipId pipId, RunnablePipPerformanceInfo performanceInfo)
        {
            Pip pip = PipGraph.GetPipFromPipId(pipId);

            stringBuilder.AppendLine(I($"\t{pip.GetDescription(Context)}"));

            if (pip.PipType == PipType.Process)
            {
                bool isExplicitlyScheduled = (m_explicitlyScheduledProcessNodes == null ? false : m_explicitlyScheduledProcessNodes.Contains(pipId.ToNodeId()));
                stringBuilder.AppendLine(I($"\t\t{"Explicitly Scheduled",-90}: {isExplicitlyScheduled,10}"));
            }

            foreach (KeyValuePair<DispatcherKind, TimeSpan> kv in performanceInfo.QueueDurations)
            {
                var duration = (long)kv.Value.TotalMilliseconds;
                if (duration != 0)
                {
                    stringBuilder.AppendLine(I($"\t\tQueue - {kv.Key,-82}: {duration,10}"));
                }
            }

            for (int i = 0; i < (int)PipExecutionStep.Done + 1; i++)
            {
                var step = (PipExecutionStep)i;
                var stepDuration = (long)performanceInfo.StepDurations.GetOrDefault(step, new TimeSpan()).TotalMilliseconds;
                if (stepDuration != 0)
                {
                    stringBuilder.AppendLine(I($"\t\tStep  - {step,-82}: {stepDuration,10}"));
                }

                long remoteStepDuration = 0;
                uint workerId = performanceInfo.Workers.GetOrDefault(step, (uint)0);
                if (workerId != 0)
                {
                    string workerName = $"{$"W{workerId}",10}:{m_workers[(int)workerId].Name}";
                    stringBuilder.AppendLine(I($"\t\t  {"WorkerName",-88}: {workerName}"));

                    var queueRequest = (long)performanceInfo.PipBuildRequestQueueDurations.GetOrDefault(step, new TimeSpan()).TotalMilliseconds;
                    stringBuilder.AppendLine(I($"\t\t  {"OrchestratorQueueRequest",-88}: {queueRequest,10}"));

                    var grpcDuration = (long)performanceInfo.PipBuildRequestGrpcDurations.GetOrDefault(step, new TimeSpan()).TotalMilliseconds;
                    stringBuilder.AppendLine(I($"\t\t  {"Grpc",-88}: {grpcDuration,10}"));

                    var remoteQueueDuration = (long)performanceInfo.RemoteQueueDurations.GetOrDefault(step, new TimeSpan()).TotalMilliseconds;
                    stringBuilder.AppendLine(I($"\t\t  {"RemoteQueue",-88}: {remoteQueueDuration,10}"));

                    remoteStepDuration = (long)performanceInfo.RemoteStepDurations.GetOrDefault(step, new TimeSpan()).TotalMilliseconds;
                    stringBuilder.AppendLine(I($"\t\t  {"RemoteStep",-88}: {remoteStepDuration,10}"));

                }

                if (stepDuration != 0 && step == PipExecutionStep.CacheLookup)
                {
                    stringBuilder.AppendLine(I($"\t\t  {"NumCacheEntriesVisited",-88}: {performanceInfo.CacheLookupPerfInfo.NumCacheEntriesVisited,10}"));
                    stringBuilder.AppendLine(I($"\t\t  {"NumPathSetsDownloaded",-88}: {performanceInfo.CacheLookupPerfInfo.NumPathSetsDownloaded,10}"));
                    stringBuilder.AppendLine(I($"\t\t  {"NumCacheEntriesAbsent",-88}: {performanceInfo.CacheLookupPerfInfo.NumCacheEntriesAbsent,10}"));

                    for (int j = 0; j < performanceInfo.CacheLookupPerfInfo.BeforeExecutionCacheStepCounters.Length; j++)
                    {
                        var name = OperationKind.GetTrackedCacheOperationKind(j).ToString();
                        var tuple = performanceInfo.CacheLookupPerfInfo.BeforeExecutionCacheStepCounters[j];
                        long duration = (long)(new TimeSpan(tuple.durationTicks)).TotalMilliseconds;

                        if (duration != 0)
                        {
                            stringBuilder.AppendLine(I($"\t\t  {name,-88}: {duration,10} - occurred {tuple.occurrences,10} times"));
                        }
                    }
                }

                if (stepDuration != 0 && step == PipExecutionStep.ExecuteProcess)
                {
                    long inputMaterializationExtraCostMbDueToUnavailability = performanceInfo.InputMaterializationCostMbForChosenWorker - performanceInfo.InputMaterializationCostMbForBestWorker;
                    stringBuilder.AppendLine(I($"\t\t  {"InputMaterializationExtraCostMbDueToUnavailability",-88}: {inputMaterializationExtraCostMbDueToUnavailability,10}"));
                    stringBuilder.AppendLine(I($"\t\t  {"InputMaterializationCostMbForChosenWorker",-88}: {performanceInfo.InputMaterializationCostMbForChosenWorker,10}"));
                    stringBuilder.AppendLine(I($"\t\t  {"PushOutputsToCacheDurationMs",-88}: {performanceInfo.PushOutputsToCacheDurationMs,10}"));

                    if (performanceInfo.CacheMissAnalysisDuration.TotalMilliseconds != 0)
                    {
                        stringBuilder.AppendLine(I($"\t\t  {"CacheMissAnalysis",-88}: {(long)performanceInfo.CacheMissAnalysisDuration.TotalMilliseconds,10}"));
                    }

                    if (performanceInfo.SuspendedDurationMs != 0)
                    {
                        stringBuilder.AppendLine(I($"\t\t  {"SuspendedDurationMs",-88}: {performanceInfo.SuspendedDurationMs,10}"));
                    }

                    if (performanceInfo.RetryDurationMs != 0)
                    {
                        stringBuilder.AppendLine(I($"\t\t  {"RetryDurationMs",-88}: {performanceInfo.RetryDurationMs,10}"));
                    }

                    if (performanceInfo.RetryCount != 0)
                    {
                        stringBuilder.AppendLine(I($"\t\t  {"RetryCount",-88}: {performanceInfo.RetryCount,10}"));
                    }

                    if (performanceInfo.ExeDuration.TotalMilliseconds != 0)
                    {
                        stringBuilder.AppendLine(I($"\t\t  {"ExeDurationMs",-88}: {(long)performanceInfo.ExeDuration.TotalMilliseconds,10}"));
                    }
                }

                if (stepDuration != 0 && step == PipExecutionStep.MaterializeOutputs)
                {
                    stringBuilder.AppendLine(I($"\t\t  {"InBackground",-88}: {MaterializeOutputsInBackground,10}"));

                    if (performanceInfo.QueueWaitDurationForMaterializeOutputsInBackground.TotalMilliseconds != 0)
                    {
                        stringBuilder.AppendLine(I($"\t\t  {"Queue.Materialize.InBackground",-88}: {(long)performanceInfo.QueueWaitDurationForMaterializeOutputsInBackground.TotalMilliseconds,10}"));
                    }
                }
            }
        }

        /// <summary>
        /// Helper function to update duration list with pip execution steps performance info 
        /// </summary>
        /// <param name="durationList"></param>
        /// <param name="durationDictionary"></param>
        private void UpdateDurationList(IList<long> durationList, Dictionary<PipExecutionStep, TimeSpan> durationDictionary)
        {
            foreach (KeyValuePair<PipExecutionStep, TimeSpan> kv in durationDictionary)
            {
                int step = (int)kv.Key;
                durationList[step] += (long)kv.Value.TotalMilliseconds;
            }
        }

        /// <summary>
        /// Helper function to update duration list with different dispatcher kind performance info 
        /// </summary>
        /// <param name="durationList"></param>
        /// <param name="durationDictionary"></param>
        private void UpdateDurationList(IList<long> durationList, Dictionary<DispatcherKind, TimeSpan> durationDictionary)
        {
            foreach (KeyValuePair<DispatcherKind, TimeSpan> kv in durationDictionary)
            {
                int dispatcher = (int)kv.Key;
                durationList[dispatcher] += (long)kv.Value.TotalMilliseconds;
            }
        }

        private bool TryGetCriticalPathTailRuntimeInfo(out int currentCriticalPathTailPipIdValue, out PipRuntimeInfo runtimeInfo)
        {
            runtimeInfo = null;

            currentCriticalPathTailPipIdValue = Volatile.Read(ref m_criticalPathTailPipIdValue);
            if (currentCriticalPathTailPipIdValue == 0)
            {
                return false;
            }

            PipId criticalPathTailId = new PipId(unchecked((uint)currentCriticalPathTailPipIdValue));
            runtimeInfo = GetPipRuntimeInfo(criticalPathTailId);
            return true;
        }

        private void UpdateCriticalPath(RunnablePip runnablePip, PipExecutionPerformance performance)
        {
            var totalDurationMs = (long)runnablePip.Performance.TotalDuration.TotalMilliseconds;
            var pip = runnablePip.Pip;

            if (pip.PipType.IsMetaPip())
            {
                return;
            }

            long criticalChainMs = totalDurationMs;
            foreach (var dependencyEdge in ScheduledGraph.GetIncomingEdges(pip.PipId.ToNodeId()))
            {
                var dependencyRuntimeInfo = GetPipRuntimeInfo(dependencyEdge.OtherNode);
                criticalChainMs = Math.Max(criticalChainMs, totalDurationMs + dependencyRuntimeInfo.CriticalPathDurationMs);
            }

            var pipRuntimeInfo = GetPipRuntimeInfo(pip.PipId);

            pipRuntimeInfo.Result = performance.ExecutionLevel;
            pipRuntimeInfo.CriticalPathDurationMs = criticalChainMs > int.MaxValue ? int.MaxValue : (int)criticalChainMs;
            ProcessPipExecutionPerformance processPerformance = performance as ProcessPipExecutionPerformance;
            if (processPerformance != null)
            {
                pipRuntimeInfo.ProcessExecuteTimeMs = (int)processPerformance.ProcessExecutionTime.TotalMilliseconds;
            }

            var pipIdValue = unchecked((int)pip.PipId.Value);

            int currentCriticalPathTailPipIdValue;
            PipRuntimeInfo criticalPathRuntimeInfo;

            while (!TryGetCriticalPathTailRuntimeInfo(out currentCriticalPathTailPipIdValue, out criticalPathRuntimeInfo)
                || (criticalChainMs > (criticalPathRuntimeInfo?.CriticalPathDurationMs ?? 0)))
            {
                if (Interlocked.CompareExchange(ref m_criticalPathTailPipIdValue, pipIdValue, currentCriticalPathTailPipIdValue) == currentCriticalPathTailPipIdValue)
                {
                    return;
                }
            }
        }

        #endregion Critical Path Logging

        /// <summary>
        /// Given the execution performance of a just-completed pip, records its performance info for future schedules
        /// and notifies any execution observers.
        /// </summary>
        private void HandleExecutionPerformance(RunnablePip runnablePip, PipExecutionPerformance performance)
        {
            var pip = runnablePip.Pip;
            UpdateCriticalPath(runnablePip, performance);

            ProcessPipExecutionPerformance processPerf = performance as ProcessPipExecutionPerformance;
            if (runnablePip is ProcessRunnablePip processRunnablePip &&
                performance.ExecutionLevel == PipExecutionLevel.Executed &&
                processPerf != null)
            {
                long pipDurationMs = runnablePip.Performance.CalculatePipDurationMs(this);

                HistoricPerfDataTable[processRunnablePip] = new ProcessPipHistoricPerfData(processPerf, pipDurationMs);
                processRunnablePip.Performance.ExeDuration = processPerf.ProcessExecutionTime;
            }

            if (ExecutionLog != null && performance != null)
            {
                ExecutionLog.PipExecutionPerformance(new PipExecutionPerformanceEventData
                {
                    PipId = pip.PipId,
                    ExecutionPerformance = performance,
                });
            }
        }

        /// <summary>
        /// The state required for pip execution
        /// </summary>
        public PipExecutionState State { get; private set; }

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        CounterCollection<PipExecutorCounter> IPipExecutionEnvironment.Counters => PipExecutionCounters;

        /// <summary>
        /// Strongly-typed, optionally persisted log of execution events.
        /// </summary>
        public IExecutionLogTarget ExecutionLog => m_multiExecutionLogTarget;

        private readonly ExecutionLogFileTarget m_executionLogFileTarget;
        private readonly FingerprintStoreExecutionLogTarget m_fingerprintStoreTarget;
        private readonly MultiExecutionLogTarget m_multiExecutionLogTarget;
        private readonly BuildManifestGenerator m_buildManifestGenerator;
        private ExecutionLogTargetBase m_manifestExecutionLog;
        private ExecutionLogFileTarget m_reportExecutionLogTarget;
        private ExecutionLogFileTarget m_workerManifestExecutionLogTarget;
        private readonly DumpPipLiteExecutionLogTarget m_dumpPipLiteExecutionLogTarget;
        private readonly EventStatsExecutionLogTarget m_eventStatsExecutionLogTarget;

        /// <inheritdoc/>
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        IPipGraphFileSystemView IPipExecutionEnvironment.PipGraphView => PipGraph;

        /// <inheritdoc />
        public void ReportCacheDescriptorHit(string sourceCache)
        {
            Contract.Requires(!string.IsNullOrWhiteSpace(sourceCache));
            m_cacheIdHits.AddOrUpdate(sourceCache, 1, (key, value) => value + 1);
        }

        /// <inheritdoc />
        public bool ShouldHaveArtificialMiss(Pip pip)
        {
            Contract.Requires(pip != null);
            return m_artificialCacheMissOptions != null &&
                   m_artificialCacheMissOptions.ShouldHaveArtificialMiss(pip.SemiStableHash);
        }

        #endregion Execution

        #region Runtime Initialization

        /// <summary>
        /// Initialize runtime state, optionally apply a filter and schedule all ready pips
        /// </summary>
        public bool InitForOrchestrator(LoggingContext loggingContext, RootFilter filter = null, SchedulerState schedulerState = null, ISandboxConnection sandboxConnection = null)
        {
            Contract.Requires(loggingContext != null);
            Contract.Assert(!IsInitialized);
            Contract.Assert(!IsDistributedWorker);

            using (var pm = PerformanceMeasurement.Start(
                    loggingContext,
                    Statistics.ApplyFilterAndScheduleReadyNodes,
                    Logger.Log.StartSchedulingPipsWithFilter,
                    Logger.Log.EndSchedulingPipsWithFilter))
            {
                m_hasFailures = m_hasFailures || !TryInitSchedulerRuntimeState(pm.LoggingContext, schedulerState: schedulerState);

                InitPipStates(pm.LoggingContext);

                IEnumerable<NodeId> nodesToSchedule;
                if (filter != null && !filter.IsEmpty)
                {
                    if (!TryGetFilteredNodes(pm.LoggingContext, filter, schedulerState, out nodesToSchedule))
                    {
                        Contract.Assume(loggingContext.ErrorWasLogged);
                        return false;
                    }
                }
                else
                {
                    nodesToSchedule = CalculateNodesToSchedule(loggingContext);
                }

                ProcessPipCountersByFilter = new PipCountersByFilter(loggingContext, m_explicitlyScheduledProcessNodes ?? new HashSet<NodeId>());
                ProcessPipCountersByTelemetryTag = new PipCountersByTelemetryTag(loggingContext, Context.StringTable, m_scheduleConfiguration.TelemetryTagPrefix);

                m_groupedPipCounters = new PipCountersByGroupAggregator(loggingContext, ProcessPipCountersByFilter, ProcessPipCountersByTelemetryTag);

                // This logging context must be set prior to any scheduling, as it might be accessed.
                m_executePhaseLoggingContext = pm.LoggingContext;

                m_hasFailures = m_hasFailures || InitSandboxConnection(loggingContext, sandboxConnection);
                // Start workers after scheduler runtime state is successfully established and we have some build to run
                if (!HasFailed && IsDistributedOrchestrator)
                {
                    StartWorkers(loggingContext);
                }

                PrioritizeAndSchedule(pm.LoggingContext, nodesToSchedule);

                if (m_configuration.Schedule.ModuleAffinityEnabled())
                {
                    PopulateModuleWorkerMapping(nodesToSchedule);
                }

                if (m_configuration.Schedule.EnableProcessRemoting)
                {
                    RegisterStaticDirectoriesForRemoting(nodesToSchedule);
                }

                Contract.Assert(!HasFailed || loggingContext.ErrorWasLogged, "Scheduler encountered errors during initialization, but none were logged.");
                return !HasFailed;
            }
        }

        private void RegisterStaticDirectoriesForRemoting(IEnumerable<NodeId> nodesToSchedule)
        {
            Dictionary<AbsolutePath, SemanticPathInfo> readOnlyMounts = m_semanticPathExpander
                .GetSemanticPathInfos(i => i.IsReadable && !i.IsWritable)
                .ToDictionary(i => i.Root, i => i);

            // Remove RO-mounts whose descendants are writable.
            foreach (AbsolutePath path in m_semanticPathExpander.GetWritableRoots())
            {
                foreach (HierarchicalNameId pathId in Context.PathTable.EnumerateHierarchyBottomUp(path.GetParent(Context.PathTable).Value))
                {
                    readOnlyMounts.Remove(new AbsolutePath(pathId));
                }
            }

            IEnumerable<AbsolutePath> readOnlyAll = readOnlyMounts.Select(kvp => kvp.Value.Root);

            // Get all source sealed directories that include all directories, instead of just top-level ones.
            IEnumerable<AbsolutePath> allDirSourceSealedDirs = nodesToSchedule
                .Select(n => n.ToPipId())
                .Where(p => m_pipTable.GetPipType(p) == PipType.SealDirectory)
                .Select(p => (SealDirectoryMutablePipState)m_pipTable.GetMutable(p))
                .Where(s => s.SealDirectoryKind == SealDirectoryKind.SourceAllDirectories)
                .Select(s => s.DirectoryRoot)
                .Where(d => !readOnlyAll.Any(t => d.IsWithin(Context.PathTable, t)));

            RemoteProcessManager.RegisterStaticDirectories(readOnlyAll.Concat(allDirSourceSealedDirs).Distinct());
        }

        private void PopulateModuleWorkerMapping(IEnumerable<NodeId> nodesToSchedule)
        {
            foreach (var node in nodesToSchedule)
            {
                var pipId = node.ToPipId();
                if (m_pipTable.GetPipType(pipId) == PipType.Process || m_pipTable.GetPipType(pipId) == PipType.Ipc)
                {
                    var pipState = (ProcessMutablePipState)m_pipTable.GetMutable(pipId);
                    (int NumPips, bool[] Workers) tuple;
                    if (!m_moduleWorkerMapping.TryGetValue(pipState.ModuleId, out tuple))
                    {
                        tuple = (0, new bool[m_configuration.Distribution.BuildWorkers.Count + 1]);
                    }

                    m_moduleWorkerMapping[pipState.ModuleId] = (tuple.NumPips + 1, tuple.Workers);
                }
            }

            int i = 0;
            foreach (var kvp in m_moduleWorkerMapping.OrderByDescending(a => a.Value.NumPips))
            {
                int workerIndex = i % m_workers.Count;
                kvp.Value.Workers[workerIndex] = true;
                i++;
            }
        }

        /// <summary>
        /// Initilizes the sandbox connection if required and reports back success or failure, allowing
        /// for a graceful terminaton of BuildXL.
        /// </summary>
        protected virtual bool InitSandboxConnection(LoggingContext loggingContext, ISandboxConnection sandboxConnection = null)
        {
            if (UnixSandboxingEnabled)
            {
                try
                {
                    // Setup the sandbox connection so we can potentially execute pips later
                    if (sandboxConnection == null)
                    {
                        // The only unix sandbox supported at the moment:
                        var sandboxKind = m_configuration.Sandbox.UnsafeSandboxConfiguration.SandboxKind;
                        Contract.Assert(sandboxKind == SandboxKind.Default || sandboxKind == SandboxKind.LinuxDetours,
                                        $"Unknown Unix sandbox kind: {m_configuration.Sandbox.UnsafeSandboxConfiguration.SandboxKind}");
                        sandboxConnection = new SandboxConnectionLinuxDetours(sandboxFailureCallback);
                    }

                    SandboxConnection = sandboxConnection;
                }
                catch (BuildXLException ex)
                {
                    Logger.Log.FailedToInitializeSandboxConnection(loggingContext, (ex.InnerException ?? ex).Message);
                    return true; // Indicates error
                }
            }

            return false;

            void sandboxFailureCallback(int status, string description)
            {
                Logger.Log.SandboxFailureNotificationReceived(loggingContext, status, description);
                RequestTermination();
            }
        }

        private bool TryInitSchedulerRuntimeState(LoggingContext loggingContext, SchedulerState schedulerState)
        {
            using (PipExecutionCounters.StartStopwatch(PipExecutorCounter.InitSchedulerRuntimeStateDuration))
            {
                Contract.Requires(loggingContext != null);

                // Start loading data for pip two phase cache and running time table. No need to wait since any operation against the components
                // will block until the required component is ready.
                m_pipTwoPhaseCache.StartLoading(waitForCompletion: false);
                m_historicPerfDataTableTask?.Start();

                InitFileChangeTracker(loggingContext);
                if (!TryProcessFileChanges(loggingContext, schedulerState))
                {
                    return false;
                }

                var fileChangeTrackingSelector = new FileChangeTrackingSelector(
                    pathTable: Context.PathTable,
                    loggingContext: loggingContext,
                    tracker: m_fileChangeTracker,
                    includedRoots: m_configuration.Cache.FileChangeTrackingInclusionRoots,
                    excludedRoots: m_configuration.Cache.FileChangeTrackingExclusionRoots);

                // Set-up tracking of local disk state:
                // - If 'incremental scheduling' is turned on, we have a tracker for file changes
                // - We always have a FileContentTable to remember hashes of files (shared among different build graphs)
                // In aggregate, we manage local disk state with a LocalDiskContentStore (m_localDiskContentStore).
                // It updates the change tracker (specific to this graph) and FileContentTable (shared) in response to pip-related I/O.
                // Additionally, we track pip-related directory enumerations via requests to DirectoryMembershipFingerprinter (which happens
                // to not be related to the LocalDiskContentStore) and in the VFS (which may pass through some probes to the real filesystem).
                // TODO: The VFS, LocalDiskContentStore, and DirectoryMembershipFingerprinter may need to be better reconciled.
                m_localDiskContentStore = new LocalDiskContentStore(
                    loggingContext,
                    Context.PathTable,
                    m_fileContentTable,
                    m_fileChangeTracker,
                    DirectoryTranslator,
                    fileChangeTrackingSelector,
                    vfsCasRoot: m_configuration.Cache.VfsCasRoot,
                    allowReuseOfWeakIdenityForSourceFiles: m_configuration.Cache.AllowReuseOfWeakIdenityForSourceFiles,
                    honorDirectoryCasingOnDisk: m_configuration.Cache.HonorDirectoryCasingOnDisk);

                m_pipOutputMaterializationTracker = new PipOutputMaterializationTracker(this, IncrementalSchedulingState);

                FileSystemView fileSystemView;
                using (PipExecutionCounters.StartStopwatch(PipExecutorCounter.CreateFileSystemViewDuration))
                {
                    fileSystemView = FileSystemView.Create(
                        Context.PathTable,
                        PipGraph,
                        m_localDiskContentStore,
                        maxInitializationDegreeOfParallelism: m_scheduleConfiguration.MaxProcesses,
                        inferNonExistenceBasedOnParentPathInRealFileSystem: m_scheduleConfiguration.InferNonExistenceBasedOnParentPathInRealFileSystem);
                }

                State = new PipExecutionState(
                    m_configuration,
                    loggingContext,
                    cache: m_pipTwoPhaseCache,
                    directoryMembershipFingerprinter: m_directoryMembershipFingerprinter,
                    fileAccessAllowlist: m_fileAccessAllowlist,
                    pathExpander: m_semanticPathExpander,
                    executionLog: ExecutionLog,
                    fileSystemView: fileSystemView,
                    directoryMembershipFinterprinterRuleSet: m_directoryMembershipFingerprinterRules,
                    fileContentManager: m_fileContentManager,
                    unsafeConfiguration: m_configuration.Sandbox.UnsafeSandboxConfiguration,
                    preserveOutputsSalt: m_previousInputsSalt,
                    sidebandState: m_sidebandState,
                    serviceManager: m_serviceManager,
                    alienFileEnumerationCache: m_alienFileEnumerationCache,
                    fileTimestampTracker: m_fileTimestampTracker,
                    globalReclassificationRules: m_globalReclassificationRules);

                if (m_scheduleConfiguration.EnableProcessRemoting)
                {
                    IRemoteProcessManagerInstaller installer = RemoteProcessManager.GetInstaller();

                    if (installer != null && !installer.InstallAsync(m_schedulerCancellationTokenSource.Token).GetAwaiter().GetResult())
                    {
                        // Disable remoting when installation is unsuccessful.
                        ((LocalWorkerWithRemoting)LocalWorker).DisableRemoting = true;
                    }
                }
            }

            return true;
        }

        private bool TryProcessFileChanges(LoggingContext loggingContext, SchedulerState schedulerState)
        {
            InputChangeList inputChangeList = null;

            if (m_configuration.Schedule.InputChanges.IsValid)
            {
                inputChangeList = InputChangeList.CreateFromFile(
                    loggingContext,
                    m_configuration.Schedule.InputChanges.ToString(Context.PathTable),
                    m_configuration.Layout.SourceDirectory.ToString(Context.PathTable),
                    DirectoryTranslator);

                if (inputChangeList == null)
                {
                    return false;
                }

                m_fileContentManager.SourceChangeAffectedInputs.InitialAffectedOutputList(inputChangeList, Context.PathTable);
            }

            IncrementalSchedulingStateFactory incrementalSchedulingStateFactory = null;

            if (m_shouldCreateIncrementalSchedulingState)
            {
                incrementalSchedulingStateFactory = new IncrementalSchedulingStateFactory(
                    loggingContext,
                    analysisMode: false,
                    tempDirectoryCleaner: TempCleaner);
            }

            if (m_fileChangeTracker.IsBuildingInitialChangeTrackingSet)
            {
                if (m_shouldCreateIncrementalSchedulingState)
                {
                    Contract.Assert(incrementalSchedulingStateFactory != null);
                    IncrementalSchedulingState = incrementalSchedulingStateFactory.CreateNew(
                        m_fileChangeTracker.FileEnvelopeId,
                        PipGraph,
                        m_configuration,
                        m_previousInputsSalt);
                }
            }
            else if (m_fileChangeTracker.IsTrackingChanges)
            {
                var fileChangeProcessor = new FileChangeProcessor(loggingContext, m_fileChangeTracker, inputChangeList);

                if (m_scheduleConfiguration.UpdateFileContentTableByScanningChangeJournal)
                {
                    fileChangeProcessor.Subscribe(m_fileContentTable);
                }

                if (m_shouldCreateIncrementalSchedulingState)
                {
                    Contract.Assert(incrementalSchedulingStateFactory != null);

                    IncrementalSchedulingState = incrementalSchedulingStateFactory.LoadOrReuse(
                        m_fileChangeTracker.FileEnvelopeId,
                        PipGraph,
                        m_configuration,
                        m_previousInputsSalt,
                        m_incrementalSchedulingStateFile.ToString(Context.PathTable),
                        schedulerState);

                    if (IncrementalSchedulingState != null)
                    {
                        fileChangeProcessor.Subscribe(IncrementalSchedulingState);
                    }
                    else
                    {
                        IncrementalSchedulingState = incrementalSchedulingStateFactory.CreateNew(
                            m_fileChangeTracker.FileEnvelopeId,
                            PipGraph,
                            m_configuration,
                            m_previousInputsSalt);
                    }
                }

                ScanningJournalResult scanningJournalResult = fileChangeProcessor.TryProcessChanges(
                    m_configuration.Engine.ScanChangeJournalTimeLimitInSec < 0
                        ? (TimeSpan?)null
                        : TimeSpan.FromSeconds(m_configuration.Engine.ScanChangeJournalTimeLimitInSec),
                    Logger.Log.JournalProcessingStatisticsForScheduler,
                    Logger.Log.JournalProcessingStatisticsForSchedulerTelemetry);

                if (m_testHooks != null)
                {
                    m_testHooks.ScanningJournalResult = scanningJournalResult;
                }

                if (m_shouldCreateIncrementalSchedulingState)
                {
                    m_testHooks?.ValidateIncrementalSchedulingStateAfterJournalScan(IncrementalSchedulingState);
                }
            }

            if (m_testHooks != null)
            {
                m_testHooks.IncrementalSchedulingState = IncrementalSchedulingState;
            }

            return true;
        }

        private void InitFileChangeTracker(LoggingContext loggingContext)
        {
            if (!m_journalState.IsDisabled)
            {
                LoadingTrackerResult loadingResult;
                if (m_configuration.Engine.FileChangeTrackerInitializationMode == FileChangeTrackerInitializationMode.ForceRestart)
                {
                    m_fileChangeTracker = FileChangeTracker.StartTrackingChanges(
                        loggingContext,
                        m_journalState.VolumeMap,
                        m_journalState.Journal,
                        m_buildEngineFingerprint);
                    loadingResult = null;
                }
                else
                {
                    loadingResult = FileChangeTracker.ResumeOrRestartTrackingChanges(
                        loggingContext,
                        m_journalState.VolumeMap,
                        m_journalState.Journal,
                        m_fileChangeTrackerFile.ToString(Context.PathTable),
                        m_buildEngineFingerprint,
                        out m_fileChangeTracker);
                }
            }
            else
            {
                m_fileChangeTracker = FileChangeTracker.CreateDisabledTracker(loggingContext);
            }
        }

        /// <summary>
        /// Initialize runtime state but do not apply any filter and do not schedule any pip.
        /// This method is used by the workers only. It is mutually exclusive with StartScheduling
        /// </summary>
        public bool InitForWorker(LoggingContext loggingContext)
        {
            Contract.Requires(loggingContext != null);

            m_hasFailures = m_hasFailures || !TryInitSchedulerRuntimeState(loggingContext, schedulerState: null);
            InitPipStates(loggingContext);
            m_hasFailures = m_hasFailures || InitSandboxConnection(loggingContext);

            Contract.Assert(!HasFailed || loggingContext.ErrorWasLogged, "Scheduler encountered errors during initialization, but none were logged.");
            return !HasFailed;
        }

        private void InitPipStates(LoggingContext loggingContext)
        {
            using (PerformanceMeasurement.Start(
                loggingContext,
                "InitPipStates",
                Logger.Log.StartSettingPipStates,
                Logger.Log.EndSettingPipStates))
            {
                IsInitialized = true;
                m_pipRuntimeInfos = new PipRuntimeInfo[m_pipTable.Count + 1]; // PipId starts from 1!

                // Note: We need IList<...> in order to get good Parallel.ForEach performance
                IList<PipId> keys = m_pipTable.StableKeys;

                int[] counts = new int[(int)PipType.Max];
                object countsLock = new object();
                Parallel.ForEach(
                    keys,
                    new ParallelOptions { MaxDegreeOfParallelism = m_scheduleConfiguration.MaxProcesses },
                    () =>
                    {
                        return new int[(int)PipType.Max];
                    },
                    (pipId, state, count) =>
                    {
                        count[(int)m_pipTable.GetPipType(pipId)]++;
                        return count;
                    },
                    (count) =>
                    {
                        lock (countsLock)
                        {
                            for (int i = 0; i < counts.Length; i++)
                            {
                                counts[i] = counts[i] + count[i];
                            }
                        }
                    });

                for (int i = 0; i < counts.Length; i++)
                {
                    int count = counts[i];
                    if (count > 0)
                    {
                        m_pipStateCounters.AccumulateInitialStateBulk(PipState.Ignored, (PipType)i, count);
                    }
                }
            }
        }

        /// <summary>
        /// Assigning priorities to the pips
        /// </summary>
        private void PrioritizeAndSchedule(LoggingContext loggingContext, IEnumerable<NodeId> nodes)
        {
            var readyNodes = new List<NodeId>();
            using (PerformanceMeasurement.Start(
                loggingContext,
                "AssigningPriorities",
                Logger.Log.StartAssigningPriorities,
                Logger.Log.EndAssigningPriorities))
            {
                NodeIdDebugView.RuntimeInfos = m_pipRuntimeInfos;

                VisitationTracker nodeFilter = new VisitationTracker(DirectedGraph);
                nodes = nodes.Where(a => m_pipTable.GetPipType(a.ToPipId()) != PipType.HashSourceFile).ToList();
                foreach (var node in nodes)
                {
                    nodeFilter.MarkVisited(node);
                }

                IReadonlyDirectedGraph graph = new FilteredDirectedGraph(PipGraph.DataflowGraph, nodeFilter);
                NodeIdDebugView.AlternateGraph = graph;

                // Store the graph which only contains the scheduled nodes.
                ScheduledGraph = graph;

                m_criticalPathStats = new CriticalPathStats();


                // We walk the graph starting from the sink nodes,
                // computing the critical path of all nodes (in terms of cumulative process execution times).
                // We update the table as we go.

                // TODO: Instead of proceeding in coarse-grained waves, which leaves some potential parallelism on the table,
                // schedule nodes to be processed as soon as all outgoing edges have been processed (tracking refcounts).

                // Phase 1: We order all nodes by height
                MultiValueDictionary<int, NodeId> nodesByHeight = graph.TopSort(nodes);
                var maxHeight = nodesByHeight.Count > 0 ? nodesByHeight.Keys.Max() : -1;

                // Phase 2: For each height, we can process nodes in parallel
                for (int height = maxHeight; height >= 0; height--)
                {
                    IReadOnlyList<NodeId> list;
                    if (!nodesByHeight.TryGetValue(height, out list))
                    {
                        continue;
                    }

                    // Note: It's important that list is an IList<...> in order to get good Parallel.ForEach performance
                    Parallel.ForEach(
                        list,
                        new ParallelOptions { MaxDegreeOfParallelism = m_scheduleConfiguration.MaxProcesses },
                        node =>
                        {
                            var pipId = node.ToPipId();
                            var pipRuntimeInfo = GetPipRuntimeInfo(pipId);
                            var pipState = m_pipTable.GetMutable(pipId);
                            var pipType = pipState.PipType;

                            // Below, we add one or more quanitites in the uint range.
                            // We use a long here to trivially avoid any overflow, and saturate to uint.MaxValue if needed as the last step.
                            long criticalPath = 0;
                            int priorityBase = 0;

                            // quick check to avoid allocation of enumerator (as we are going through an interface, and where everything gets boxed!)
                            if (!graph.IsSinkNode(node))
                            {
                                foreach (var edge in graph.GetOutgoingEdges(node))
                                {
                                    var otherPriority = GetPipRuntimeInfo(edge.OtherNode).Priority;

                                    // Priority consists of given priority in the specs (bits 24-31, and the critical path priority (bits 0-23)
                                    unchecked
                                    {
                                        criticalPath = Math.Max(criticalPath, otherPriority & MaxInitialPipPriority);
                                        priorityBase = Math.Max(priorityBase, otherPriority >> CriticalPathPriorityBitCount);
                                    }
                                }
                            }

                            if (pipType.IsMetaPip())
                            {
                                // We pretend meta pips are themselves free.
                                // We use the critical path calculated from aggregating outgoing edges.
                            }
                            else
                            {
                                // Note that we only try to look up historical runtimes for process pips, since we only record
                                // historical data for that pip type. Avoiding the failed lookup here means that we have more
                                // useful 'hit' / 'miss' counters for the running time table.
                                uint historicalMilliseconds = 0;
                                if (pipType == PipType.Process && HistoricPerfDataTable != null)
                                {
                                    historicalMilliseconds = HistoricPerfDataTable[m_pipTable.GetPipSemiStableHash(pipId)].RunDurationInMs;
                                }

                                if (historicalMilliseconds != 0)
                                {
                                    Interlocked.Increment(ref m_criticalPathStats.NumHits);
                                    criticalPath += historicalMilliseconds;
                                }
                                else
                                {
                                    // TODO:
                                    // The following wild guesses are subject to further tweaking.
                                    // They are based on no hard data.
                                    Interlocked.Increment(ref m_criticalPathStats.NumWildGuesses);

                                    uint estimatedMilliseconds = (uint)graph.GetIncomingEdgesCount(node);
                                    switch (pipType)
                                    {
                                        case PipType.Process:
                                            estimatedMilliseconds += 10;
                                            break;
                                        case PipType.Ipc:
                                            estimatedMilliseconds += 15;
                                            break;
                                        case PipType.CopyFile:
                                            estimatedMilliseconds += 2;
                                            break;
                                        case PipType.WriteFile:
                                            estimatedMilliseconds += 1;
                                            break;
                                    }

                                    criticalPath += estimatedMilliseconds;
                                }
                            }

                            long currentLongestPath;
                            while ((currentLongestPath = Volatile.Read(ref m_criticalPathStats.LongestPath)) < criticalPath)
                            {
                                if (Interlocked.CompareExchange(ref m_criticalPathStats.LongestPath, criticalPath, comparand: currentLongestPath) == currentLongestPath)
                                {
                                    break;
                                }
                            }

                            priorityBase = Math.Max(m_pipTable.GetPipPriority(pipId), priorityBase) << CriticalPathPriorityBitCount;
                            int criticalPathPriority = (criticalPath < 0 || criticalPath > MaxInitialPipPriority) ? MaxInitialPipPriority : unchecked((int)criticalPath);
                            pipRuntimeInfo.Priority = unchecked(priorityBase + criticalPathPriority);

                            Contract.Assert(pipType != PipType.HashSourceFile);
                            pipRuntimeInfo.Transition(m_pipStateCounters, pipType, PipState.Waiting);
                            if (pipType == PipType.Process && ((ProcessMutablePipState)pipState).IsStartOrShutdown)
                            {
                                Interlocked.Increment(ref m_numServicePipsScheduled);
                            }

                            bool isReady;
                            if (graph.IsSourceNode(node))
                            {
                                isReady = true;
                            }
                            else
                            {
                                int refCount = graph.CountIncomingHeavyEdges(node);
                                pipRuntimeInfo.RefCount = refCount;
                                isReady = refCount == 0;
                            }

                            if (isReady)
                            {
                                lock (readyNodes)
                                {
                                    readyNodes.Add(node);
                                }
                            }
                        });
                }

#if DEBUG
                foreach (var node in nodes)
                {
                    var pipId = node.ToPipId();
                    var pipRuntimeInfo = GetPipRuntimeInfo(pipId);
                    Contract.Assert(pipRuntimeInfo.State != PipState.Ignored);
                }
#endif
            }

            using (PipExecutionCounters.StartStopwatch(PipExecutorCounter.InitialSchedulePipWallTime))
            {
                Parallel.ForEach(
                    readyNodes,

                    // Limit the concurrency here because most work is in PipQueue.Enqueue which immediately has a lock, so this helps some by parellizing the hydratepip.
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(8, m_scheduleConfiguration.MaxProcesses) },
                    (node) => SchedulePip(node, node.ToPipId()).GetAwaiter().GetResult());

                // From this point, only pips that are already scheduled can enqueue new work items
                PipQueue.SetAsFinalized();
            }
        }

        #endregion Runtime Initialization

        /// <summary>
        /// Records the final content hashes (by path; no rewrite count) of the given <see cref="SealDirectory" /> pip's contents.
        /// </summary>
        /// <remarks>
        /// The scheduler lock need not be held.
        /// </remarks>
        private PipResult ExecuteSealDirectoryPip(OperationContext operationContext, IPipExecutionEnvironment environment, SealDirectory pip)
        {
            Contract.Requires(pip != null);

            DateTime pipStart = DateTime.UtcNow;
            bool registerDirectoryResult = true;
            PipResultStatus result;

            using (operationContext.StartOperation(PipExecutorCounter.RegisterStaticDirectory))
            {
                // If the pip is a composite opaque directory, then its dynamic content needs to be reported, since the usual reporting of
                // opaque directories happens for process pips only.
                if (pip.IsComposite)
                {
                    Contract.Assert(pip.Kind == SealDirectoryKind.SharedOpaque);
                    if (!TryReportCompositeOpaqueContents(environment, pip))
                    {
                        // An error should have been logged by this point.
                        return PipResult.Create(PipResultStatus.Failed, pipStart);
                    }
                }

                // The consumers of an opaque directory will register the directory when they use it.
                if (pip.Kind != SealDirectoryKind.Opaque)
                {
                    registerDirectoryResult = m_fileContentManager.TryRegisterStaticDirectory(pip.Directory);
                }
            }

            if (registerDirectoryResult)
            {
                result = PipResultStatus.NotMaterialized;
                if (pip.Kind == SealDirectoryKind.SourceAllDirectories || pip.Kind == SealDirectoryKind.SourceTopDirectoryOnly)
                {
                    result = PipResultStatus.Succeeded;
                }
            }
            else
            {
                // An error for why this failed should already be logged
                result = PipResultStatus.Failed;
            }

            return PipResult.Create(result, pipStart);
        }

        private bool TryReportCompositeOpaqueContents(IPipExecutionEnvironment environment, SealDirectory pip)
        {
            Contract.Assert(pip.IsComposite);
            Contract.Assert(pip.Kind == SealDirectoryKind.SharedOpaque);

            // Aggregates the content of all non-composite directories and report it
            using (var pooledAggregatedContent = Pools.FileArtifactWithAttributesSetPool.GetInstance())
            using (var filteredContentWrapper = Pools.FileArtifactWithAttributesListPool.GetInstance())
            {
                HashSet<FileArtifactWithAttributes> aggregatedContent = pooledAggregatedContent.Instance;
                var filteredContent = filteredContentWrapper.Instance;
                long duration;
                using (var sw = PipExecutionCounters[PipExecutorCounter.ComputeCompositeSharedOpaqueContentDuration].Start())
                {
                    foreach (var directoryElement in pip.ComposedDirectories)
                    {
                        // Regardless whether directoryElement is a non-composite or composite directory, it was
                        // produced by an upstream pip (ProcessPip/SealDirectoryPip respectively). At this point,
                        // FileContentManager knows the content of this directory artifact.
                        IEnumerable<FileArtifact> memberContents = m_fileContentManager.ListSealedDirectoryContents(directoryElement);

                        // If the seal pip is creating a sub directory out of a sod, take only those files that are
                        // under the root of the directory.
                        if (pip.CompositionActionKind == SealDirectoryCompositionActionKind.NarrowDirectoryCone)
                        {
                            memberContents = memberContents.Where(file => file.Path.IsWithin(Context.PathTable, pip.DirectoryRoot));
                        }

                        aggregatedContent.AddRange(memberContents.Select(member =>
                            FileArtifactWithAttributes.Create(member, FileExistence.Required, m_fileContentManager.IsAllowedFileRewriteOutput(member.Path))));
                    }

                    // if the filter is specified, restrict the final content
                    if (pip.ContentFilter != null && environment.Configuration.Schedule.DisableCompositeOpaqueFilters != true)
                    {
                        var regex = new Regex(pip.ContentFilter.Value.Regex,
                            RegexOptions.IgnoreCase,
                            TimeSpan.FromMilliseconds(SealDirectoryContentFilterTimeoutMs));
                        var isIncludeFilter = pip.ContentFilter.Value.Kind == SealDirectoryContentFilter.ContentFilterKind.Include;

                        foreach (var fileArtifact in aggregatedContent)
                        {
                            var filePath = fileArtifact.Path.ToString(Context.PathTable);
                            try
                            {
                                if (regex.IsMatch(filePath) == isIncludeFilter)
                                {
                                    filteredContent.Add(fileArtifact);
                                }
                            }
                            catch (RegexMatchTimeoutException)
                            {
                                Logger.Log.CompositeSharedOpaqueRegexTimeout(
                                    m_loggingContext,
                                    pip.GetDescription(environment.Context),
                                    pip.ContentFilter.Value.Regex,
                                    filePath);

                                return false;
                            }
                        }
                    }

                    duration = sw.Elapsed.ToMilliseconds();
                }

                // the directory artifacts that this composite shared opaque consists of might or might not be materialized
                var contents = pip.ContentFilter == null ? aggregatedContent : (IEnumerable<FileArtifactWithAttributes>)filteredContent;
                m_fileContentManager.ReportDynamicDirectoryContents(
                    pip.Directory,
                    contents,
                    PipOutputOrigin.NotMaterialized);

                Logger.Log.CompositeSharedOpaqueContentDetermined(
                    m_loggingContext,
                    pip.GetDescription(environment.Context),
                    pip.ComposedDirectories.Count,
                    aggregatedContent.Count,
                    pip.ContentFilter == null ? aggregatedContent.Count : filteredContent.Count,
                    duration);

                ExecutionLog?.PipExecutionDirectoryOutputs(new PipExecutionDirectoryOutputs
                {
                    PipId = pip.PipId,
                    DirectoryOutputs = ReadOnlyArray<(DirectoryArtifact directoryArtifact, ReadOnlyArray<FileArtifact> fileArtifactArray)>.From(
                        new[] {
                            (pip.Directory, ReadOnlyArray<FileArtifact>.From(contents.Select(content => content.ToFileArtifact())))
                        })
                });
            }

            return true;
        }

        #region IFileContentManagerHost Members

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        LoggingContext IFileContentManagerHost.LoggingContext => m_executePhaseLoggingContext;

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        SemanticPathExpander IFileContentManagerHost.SemanticPathExpander => m_semanticPathExpander;

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        IConfiguration IFileContentManagerHost.Configuration => m_configuration;

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        IArtifactContentCache IFileContentManagerHost.ArtifactContentCache => Cache.ArtifactContentCache;

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        SealDirectoryKind IFileContentManagerHost.GetSealDirectoryKind(DirectoryArtifact directory)
        {
            return GetSealDirectoryKind(directory);
        }

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        async Task<Optional<IEnumerable<AbsolutePath>>> IFileContentManagerHost.GetReadPathsAsync(OperationContext context, Pip pip)
        {
            foreach (var pathSetTask in m_pipTwoPhaseCache.TryGetAssociatedPathSetsAsync(context, pip))
            {
                var pathSet = await pathSetTask;
                if (pathSet.Succeeded)
                {
                    return new Optional<IEnumerable<AbsolutePath>>(pathSet.Result.Paths
                        // Currently, all flags refer to operations which are not reads (i.e. probes or directory enumerations)
                        .Where(entry => entry.Flags == ObservedPathEntryFlags.None)
                        .Select(entry => entry.Path));
                }

                break;
            }

            return default;
        }

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        bool IFileContentManagerHost.TryGetSourceSealDirectory(DirectoryArtifact directory, out SourceSealWithPatterns sourceSealWithPatterns)
        {
            sourceSealWithPatterns = default;

            if (((IPipExecutionEnvironment)this).IsSourceSealedDirectory(directory, out bool allDirectories, out ReadOnlyArray<StringId> patterns))
            {
                sourceSealWithPatterns = new SourceSealWithPatterns(directory.Path, patterns, !allDirectories);
                return true;
            }

            return false;
        }

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        bool IFileContentManagerHost.ShouldScrubFullSealDirectory(DirectoryArtifact directory)
        {
            return ShouldScrubFullSealDirectory(directory);
        }

        /// <inheritdoc/>
        public bool TryGetCopySourceFile(FileArtifact artifact, out FileArtifact sourceFile)
        {
            var producer = PipGraph.TryGetProducer(artifact);
            if (producer.IsValid)
            {
                var pipType = PipGraph.PipTable.GetPipType(producer);
                if (pipType == PipType.CopyFile)
                {
                    var copyPip = (CopyFile)PipGraph.GetPipFromPipId(producer);
                    sourceFile = copyPip.Source;
                    return true;
                }
            }

            sourceFile = FileArtifact.Invalid;
            return false;
        }

        /// <inheritdoc/>
        public SealDirectoryKind GetSealDirectoryKind(DirectoryArtifact directory)
        {
            Contract.Requires(directory.IsValid);
            var sealDirectoryId = PipGraph.GetSealedDirectoryNode(directory).ToPipId();
            return PipGraph.PipTable.GetSealDirectoryKind(sealDirectoryId);
        }

        /// <inheritdoc/>
        public bool ShouldScrubFullSealDirectory(DirectoryArtifact directory)
        {
            Contract.Requires(directory.IsValid);
            var sealDirectoryId = PipGraph.GetSealedDirectoryNode(directory).ToPipId();
            return PipGraph.PipTable.ShouldScrubFullSealDirectory(sealDirectoryId);
        }

        private ReadOnlyArray<StringId> GetSourceSealDirectoryPatterns(DirectoryArtifact directory)
        {
            var sealDirectoryId = PipGraph.GetSealedDirectoryNode(directory).ToPipId();
            return PipGraph.PipTable.GetSourceSealDirectoryPatterns(sealDirectoryId);
        }

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        Pip IFileContentManagerHost.GetProducer(in FileOrDirectoryArtifact artifact)
        {
            var producerId = PipGraph.GetProducer(artifact);
            return PipGraph.GetPipFromPipId(producerId);
        }

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        PipId IFileContentManagerHost.TryGetProducerId(in FileOrDirectoryArtifact artifact)
        {
            return PipGraph.TryGetProducer(artifact);
        }

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        string IFileContentManagerHost.GetProducerDescription(in FileOrDirectoryArtifact artifact)
        {
            var producerId = PipGraph.GetProducer(artifact);
            var producer = PipGraph.GetPipFromPipId(producerId);
            return producer.GetDescription(Context);
        }

        /// <summary>
        /// Gets the first consumer description associated with a FileOrDirectory artifact.
        /// </summary>
        /// <param name="artifact">The artifact for which to get the first consumer description.</param>
        /// <returns>The first consumer description or null if there is no consumer.</returns>
        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        public string GetConsumerDescription(in FileOrDirectoryArtifact artifact)
        {
            var producerId = PipGraph.GetProducer(artifact);
            foreach (var consumerEdge in DirectedGraph.GetOutgoingEdges(producerId.ToNodeId()))
            {
                Pip consumer = PipGraph.GetPipFromPipId(consumerEdge.OtherNode.ToPipId());
                return consumer.GetDescription(Context);
            }

            // No consumer
            return null;
        }

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        SortedReadOnlyArray<FileArtifact, OrdinalFileArtifactComparer> IFileContentManagerHost.ListSealDirectoryContents(DirectoryArtifact directory)
        {
            return PipGraph.ListSealedDirectoryContents(directory);
        }

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        bool IFileContentManagerHost.AllowArtifactReadOnly(in FileOrDirectoryArtifact artifact) => !PipGraph.MustArtifactRemainWritable(artifact);

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        bool IFileContentManagerHost.IsPreservedOutputArtifact(in FileOrDirectoryArtifact artifact)
        {
            Contract.Requires(artifact.IsValid);

            if (m_configuration.Sandbox.UnsafeSandboxConfiguration.PreserveOutputs == PreserveOutputsMode.Disabled)
            {
                return false;
            }

            return PipGraph.IsPreservedOutputArtifact(artifact, m_configuration.Sandbox.UnsafeSandboxConfiguration.PreserveOutputsTrustLevel);
        }

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        bool IFileContentManagerHost.IsFileRewritten(in FileArtifact artifact)
        {
            Contract.Requires(artifact.IsValid);

            return IsFileRewritten(artifact);
        }

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        void IFileContentManagerHost.ReportDynamicOutputFile(FileArtifact artifact)
        {
            State.FileSystemView.ReportOutputFileSystemExistence(artifact.Path, PathExistence.ExistsAsFile);
        }

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        void IFileContentManagerHost.ReportContent(FileArtifact artifact, in FileMaterializationInfo trackedFileContentInfo, PipOutputOrigin origin)
        {
            // NOTE: Artifacts may be materialized as absent path so we need to check here
            PathExistence? existence = trackedFileContentInfo.FileContentInfo.Existence;
            if (trackedFileContentInfo.Hash == WellKnownContentHashes.AbsentFile)
            {
                existence = PathExistence.Nonexistent;
            }

            if (origin != PipOutputOrigin.NotMaterialized && existence != null)
            {
                State.FileSystemView.ReportRealFileSystemExistence(artifact.Path, existence.Value);
            }

            if (artifact.IsValid && artifact.IsOutputFile)
            {
                if (existence != PathExistence.Nonexistent && trackedFileContentInfo.Hash.IsSpecialValue())
                {
                    Contract.Assert(false, I($"Hash={trackedFileContentInfo.Hash}, Length={trackedFileContentInfo.FileContentInfo.SerializedLengthAndExistence}, Existence={existence}, Path={artifact.Path.ToString(Context.PathTable)}, Origin={origin}"));
                }

                // Since it's an output file, force the existence as ExistsAsFile.
                //
                // Note: It is possible to construct FileContentInfo by calling CreateWithUnknownLength(hash, PathExistence.Nonexistent).
                // Calls to Existence property of this struct will return 'null'. This means that we would be 'overriding' the original existence.
                // However, we do not currently create such FileContentInfo's and it's improbable that we'd create them in the future,
                // so forcing the existence here should be fine.
                if (existence == null)
                {
                    existence = PathExistence.ExistsAsFile;
                }

                State.FileSystemView.ReportOutputFileSystemExistence(artifact.Path, existence.Value);
            }

            if (artifact.IsSourceFile && IncrementalSchedulingState != null && origin != PipOutputOrigin.NotMaterialized)
            {
                // Source file artifact may not have a producer because it's part of sealed source directory.
                var producer = PipGraph.TryGetProducer(artifact);

                if (producer.IsValid)
                {
                    IncrementalSchedulingState.PendingUpdates.MarkNodeClean(producer.ToNodeId());
                    PipExecutionCounters.IncrementCounter(PipExecutorCounter.PipMarkClean);

                    IncrementalSchedulingState.PendingUpdates.MarkNodeMaterialized(producer.ToNodeId());
                    PipExecutionCounters.IncrementCounter(PipExecutorCounter.PipMarkMaterialized);
                }
            }
        }

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        void IFileContentManagerHost.ReportMaterializedArtifact(in FileOrDirectoryArtifact artifact)
        {
            if (artifact.IsDirectory && IncrementalSchedulingState != null)
            {
                // Ensure seal directory gets marked as materialized when file content manager reports that
                // the artifact is materialized.
                var sealDirectoryNode = PipGraph.GetSealedDirectoryNode(artifact.DirectoryArtifact);

                IncrementalSchedulingState.PendingUpdates.MarkNodeClean(sealDirectoryNode);
                PipExecutionCounters.IncrementCounter(PipExecutorCounter.PipMarkClean);

                IncrementalSchedulingState.PendingUpdates.MarkNodeMaterialized(sealDirectoryNode);
                PipExecutionCounters.IncrementCounter(PipExecutorCounter.PipMarkMaterialized);
            }

            m_pipOutputMaterializationTracker.ReportMaterializedArtifact(artifact);
        }

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        Possible<Unit> IFileContentManagerHost.ReportFileArtifactPlaced(in FileArtifact artifact, FileMaterializationInfo fileMaterializationInfo)
        {
            var pathAsString = artifact.Path.ToString(Context.PathTable);
            bool artifactIsModified = false;

            // Don't flag allowed source rewrites as shared opaque outputs since we don't want to delete them
            // in the next build.
            if (!fileMaterializationInfo.IsUndeclaredFileRewrite)
            {
                if (MakeSharedOpaqueOutputIfNeeded(artifact.Path))
                {
                    artifactIsModified = true;
                }
            }

            // If the file has execution permissions set, make sure we honor that when the file is placed
            if (fileMaterializationInfo.IsExecutable)
            {
                var result = FileUtilities.SetExecutePermissionIfNeeded(pathAsString);
                if (!result.Succeeded)
                {
                    return result.Failure;
                }

                artifactIsModified |= result.Result;
            }

            // If the file was modified after being placed, make sure we update the file content table
            if (artifactIsModified && fileMaterializationInfo.FileContentInfo.Hash.IsValid)
            {
                var flags = FileFlagsAndAttributes.FileFlagOverlapped | FileFlagsAndAttributes.FileFlagOpenReparsePoint;

                if (fileMaterializationInfo.IsReparsePointActionable)
                {
                    flags |= FileFlagsAndAttributes.FileFlagBackupSemantics;
                }

                try
                {
                    FileUtilities.UsingFileHandleAndFileLength(
                        pathAsString,
                        FileDesiredAccess.GenericRead,
                        FileShare.Read | FileShare.Delete,
                        FileMode.Open,
                        flags,
                        (handle, length) => m_fileContentTable.RecordContentHash(handle, pathAsString, fileMaterializationInfo.FileContentInfo.Hash, fileMaterializationInfo.FileContentInfo.Length));
                }
                catch (Exception e)
                {
                    // We sporadically get a file not found error when trying to open a handle to the path (on Linux, with the ephemeral cache on). Let's list the content of the directory to try to spot what's going on. One theory
                    // is that the cache has some sort of casing issue. This is a best-effort attempt to get more information about the issue. We can remove the enumeration once we understand the problem.
                    IEnumerable<string> containingEntries = CollectionUtilities.EmptyArray<string>();

                    try
                    {
                        var directory = Directory.GetParent(pathAsString);
                        if (directory != null)
                        {
                            containingEntries = Directory.EnumerateFileSystemEntries(directory.FullName);
                        }
                    }
#pragma warning disable ERP022 // Unobserved exception in generic exception handler
                    catch
                    {
                        // Ignore any exceptions that may occur while enumerating the directory, this is done on a best-effort basis for debugging purposes.
                    }
#pragma warning restore ERP022 // Unobserved exception in generic exception handler

                    return new NativeFailure(e.GetLogEventErrorCode()).Annotate($"An error occurred updating the file content table for modified file '{pathAsString}'. " +
                        $"Content of the directory is: [{string.Join(",", containingEntries)}]. Details: {e}");
                }
            }

            return Unit.Void;
        }

        private bool MakeSharedOpaqueOutputIfNeeded(AbsolutePath path)
        {
            if (!m_configuration.Sandbox.UnsafeSandboxConfiguration.SkipFlaggingSharedOpaqueOutputs() && IsPathUnderSharedOpaqueDirectory(path))
            {
                SharedOpaqueOutputHelper.EnforceFileIsSharedOpaqueOutput(path.ToString(Context.PathTable));
                return true;
            }

            return false;
        }

        private bool IsPathUnderSharedOpaqueDirectory(AbsolutePath path)
        {
            return
                PipGraph.IsPathUnderOutputDirectory(path, out var isItSharedOpaque) &&
                isItSharedOpaque;
        }

        /// <inheritdoc />
        public bool CanMaterializeFile(FileArtifact artifact)
        {
            if (!m_configuration.Schedule.EnableLazyWriteFileMaterialization)
            {
                return false;
            }

            var producerId = PipGraph.TryGetProducer(artifact);
            return producerId.IsValid && m_pipTable.GetPipType(producerId) == PipType.WriteFile;
        }

        /// <inheritdoc />
        public async Task<Possible<ContentMaterializationOrigin>> TryMaterializeFileAsync(FileArtifact artifact, OperationContext operationContext)
        {
            var producerId = PipGraph.TryGetProducer(artifact);
            Contract.Assert(producerId.IsValid && m_pipTable.GetPipType(producerId) == PipType.WriteFile);

            if (!m_configuration.Schedule.EnableLazyWriteFileMaterialization)
            {
                return new Failure<string>(I($"Failed to materialize write file destination because lazy write file materialization is not enabled"));
            }

            var writeFile = (WriteFile)m_pipTable.HydratePip(producerId, PipQueryContext.SchedulerFileContentManagerHostMaterializeFile);
            var result = await PipExecutor.TryExecuteWriteFileAsync(operationContext, this, writeFile, materializeOutputs: true, reportOutputs: false);
            return result.Then<ContentMaterializationOrigin>(
                status =>
                {
                    if (status.IndicatesFailure())
                    {
                        return new Failure<string>(I($"Failed to materialize write file destination because write file pip execution results in '{status.ToString()}'"));
                    }

                    if (IncrementalSchedulingState != null)
                    {
                        IncrementalSchedulingState.PendingUpdates.MarkNodeMaterialized(producerId.ToNodeId());
                        PipExecutionCounters.IncrementCounter(PipExecutorCounter.PipMarkMaterialized);
                    }

                    return status.ToContentMaterializationOriginHidingExecution();
                });
        }

        #endregion IFileContentManagerHost Members

        #region IOperationTrackerHost Members

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        string IOperationTrackerHost.GetDescription(in FileOrDirectoryArtifact artifact)
        {
            if (artifact.IsValid)
            {
                if (artifact.IsFile)
                {
                    return I($"File: {artifact.Path.ToString(Context.PathTable)} [{artifact.FileArtifact.RewriteCount}]");
                }
                else
                {
                    return I($"Directory: {artifact.Path.ToString(Context.PathTable)} [{artifact.DirectoryArtifact.PartialSealId}]");
                }
            }

            return null;
        }

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes")]
        string IOperationTrackerHost.GetDescription(PipId pipId)
        {
            if (pipId.IsValid)
            {
                return PipGraph.GetPipFromPipId(pipId).GetDescription(Context);
            }

            return null;
        }

        #endregion IOperationTrackerHost Members

        #region Event Logging

        private delegate void PipProvenanceEvent(
            LoggingContext loggingContext,
            string file,
            int line,
            int column,
            string pipDesc,
            string pipValueId);

        private delegate void PipProvenanceEventWithFilePath(
            LoggingContext loggingContext,
            string file,
            int line,
            int column,
            string pipDesc,
            string pipValueId,
            string filePath);

        // Handy for errors related to sealed directories, since there is a directory root associated with the file.
        private delegate void PipProvenanceEventWithFilePathAndDirectoryPath(
            LoggingContext loggingContext,
            string file,
            int line,
            int column,
            string pipDesc,
            string pipValueId,
            string filePath,
            string directoryPath);

        private delegate void PipProvenanceEventWithFilePathAndRelatedPip(
            LoggingContext loggingContext,
            string file,
            int line,
            int column,
            string pipDesc,
            string pipValueId,
            string outputFile,
            string producingPipDesc,
            string producingPipValueId);

        // Handy for errors related to sealed directories, since there is a directory root associated with the file.
        private delegate void PipProvenanceEventWithFilePathAndDirectoryPathAndRelatedPip(
            LoggingContext loggingContext,
            string file,
            int line,
            int column,
            string pipDesc,
            string pipValueId,
            string outputFile,
            string directoryPath,
            string producingPipDesc,
            string producingPipValueId);

        private PipProvenance m_dummyProvenance;

        private readonly CancellationTokenRegistration m_cancellationTokenRegistration;
        private readonly CancellationTokenSource m_schedulerCancellationTokenSource;

        private PipProvenance GetDummyProvenance()
        {
            return m_dummyProvenance = m_dummyProvenance ?? PipProvenance.CreateDummy(Context);
        }

        private void LogEventWithPipProvenance(RunnablePip runnablePip, PipProvenanceEvent pipEvent)
        {
            Contract.Requires(pipEvent != null);
            Contract.Requires(runnablePip != null);

            PipProvenance provenance = runnablePip.Pip.Provenance ?? GetDummyProvenance();
            pipEvent(
                runnablePip.LoggingContext,
                provenance.Token.Path.ToString(Context.PathTable),
                provenance.Token.Line,
                provenance.Token.Position,
                runnablePip.Pip.GetDescription(Context),
                provenance.OutputValueSymbol.ToString(Context.SymbolTable));
        }

        private delegate void PipStartEvent(
            LoggingContext loggingContext,
            string file,
            int line,
            int column,
            string pipDesc,
            string pipValueId);

        private delegate void PipEndEvent(LoggingContext loggingContext, string pipDesc, string pipValueId, int status, long ticks);

        private LoggingContext LogEventPipStart(RunnablePip runnablePip)
        {
            Contract.Requires(runnablePip != null);
            var pip = runnablePip.Pip;

            PipProvenance provenance = pip.Provenance;

            if (provenance == null)
            {
                return m_executePhaseLoggingContext;
            }

            LoggingContext pipLoggingContext = new LoggingContext(
                m_executePhaseLoggingContext,
                IsDistributedWorker ? "remote call" : pip.PipId.ToString(),
                runnablePip.Observer.GetActivityId(runnablePip));

            EventSource.SetCurrentThreadActivityId(pipLoggingContext.ActivityId);

            if (pip.PipType == PipType.Process)
            {
                var process = pip as Process;
                Contract.Assume(process != null);

                string executablePath = process.Executable.Path.ToString(Context.PathTable);

                FileMaterializationInfo executableVersionedHash;
                string executableHashStr =
                    (m_fileContentManager.TryGetInputContent(process.Executable, out executableVersionedHash) &&
                     executableVersionedHash.Hash != WellKnownContentHashes.UntrackedFile)
                        ? executableVersionedHash.Hash.ToHex()
                        : executablePath;

                if (m_diagnosticsEnabled)
                {
                    Logger.Log.ProcessStart(
                        pipLoggingContext,
                        provenance.Token.Path.ToString(Context.PathTable),
                        provenance.Token.Line,
                        provenance.Token.Position,
                        runnablePip.Description,
                        provenance.OutputValueSymbol.ToString(Context.SymbolTable),
                        executablePath,
                        executableHashStr);
                }
            }
            else
            {
                PipStartEvent startEvent = null;

                switch (pip.PipType)
                {
                    case PipType.WriteFile:
                        startEvent = Logger.Log.WriteFileStart;
                        break;
                    case PipType.CopyFile:
                        startEvent = Logger.Log.CopyFileStart;
                        break;
                }

                if (startEvent != null && m_diagnosticsEnabled)
                {
                    startEvent(
                        pipLoggingContext,
                        provenance.Token.Path.ToString(Context.PathTable),
                        provenance.Token.Line,
                        provenance.Token.Position,
                        pip.GetDescription(Context),
                        provenance.OutputValueSymbol.ToString(Context.SymbolTable));
                }
            }

            return pipLoggingContext;
        }

        private void LogEventPipEnd(LoggingContext pipLoggingContext, Pip pip, PipResultStatus status, long ticks)
        {
            Contract.Requires(pip != null);

            PipProvenance provenance = pip.Provenance;

            if (provenance == null)
            {
                return;
            }

            EventSource.SetCurrentThreadActivityId(pipLoggingContext.ActivityId);

            if (pip.PipType == PipType.Process)
            {
                var process = pip as Process;
                Contract.Assume(process != null);

                string executablePath = process.Executable.Path.ToString(Context.PathTable);

                FileMaterializationInfo executableVersionedHash;
                string executableHashStr =
                    (m_fileContentManager.TryGetInputContent(process.Executable, out executableVersionedHash) &&
                     executableVersionedHash.Hash != WellKnownContentHashes.UntrackedFile)
                        ? executableVersionedHash.Hash.ToHex()
                        : executablePath;

                if (ETWLogger.Log.IsEnabled(EventLevel.Verbose, Keywords.Diagnostics))
                {
                    Logger.Log.ProcessEnd(
                        pipLoggingContext,
                        pip.GetDescription(Context),
                        provenance.OutputValueSymbol.ToString(Context.SymbolTable),
                        (int)status,
                        ticks,
                        executableHashStr);
                }
            }
            else
            {
                PipEndEvent endEvent = null;

                switch (pip.PipType)
                {
                    case PipType.WriteFile:
                        endEvent = Logger.Log.WriteFileEnd;
                        break;
                    case PipType.CopyFile:
                        endEvent = Logger.Log.CopyFileEnd;
                        break;
                }

                if (endEvent != null && m_diagnosticsEnabled)
                {
                    endEvent(
                        pipLoggingContext,
                        pip.GetDescription(Context),
                        provenance.OutputValueSymbol.ToString(Context.SymbolTable),
                        (int)status,
                        ticks);
                }
            }

            EventSource.SetCurrentThreadActivityId(pipLoggingContext.ParentActivityId);
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:DisposeObjectsBeforeLosingScope")]
        private static ExecutionLogFileTarget CreateExecutionLog(
            IConfiguration configuration,
            PipExecutionContext context,
            PipGraph pipGraph,
            ExtraFingerprintSalts salts,
            LoggingContext loggingContext)
        {
            var executionLogPath = configuration.Logging.ExecutionLog;
            if (configuration.Logging.LogExecution && executionLogPath.IsValid && configuration.Engine.Phase.HasFlag(EnginePhases.Execute))
            {
                var executionLogPathString = executionLogPath.ToString(context.PathTable);

                FileStream executionLogStream;

                try
                {
                    FileUtilities.CreateDirectoryWithRetry(Path.GetDirectoryName(executionLogPathString));
                    executionLogStream = File.Open(executionLogPathString, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete);
                }
                catch (Exception ex)
                {
                    Logger.Log.UnableToCreateLogFile(loggingContext, executionLogPathString, ex.Message);
                    throw new BuildXLException("Unable to create execution log file: ", ex);
                }

                try
                {
                    // The path table is either:
                    // 1. Newly loaded - all paths are serialized so taking the last path value is valid
                    // 2. Populated with all paths for files in constructed scheduled and will be serialized later - taking the current last
                    // path is safe since at least the current set of paths will be serialized
                    var lastStaticAbsolutePathValue = pipGraph.MaxAbsolutePathIndex;

                    var logFile = new BinaryLogger(executionLogStream, context, pipGraph.GraphId, lastStaticAbsolutePathValue);
                    var executionLogTarget = new ExecutionLogFileTarget(logFile, disabledEventIds: configuration.Logging.NoExecutionLog);
                    executionLogTarget.BuildSessionConfiguration(new BuildSessionConfigurationEventData(salts));

                    return executionLogTarget;
                }
                catch
                {
                    executionLogStream.Dispose();
                    throw;
                }
            }

            return null;
        }

        private static FingerprintStoreExecutionLogTarget CreateFingerprintStoreTarget(
            LoggingContext loggingContext,
            IConfiguration configuration,
            PipExecutionContext context,
            PipTable pipTable,
            PipContentFingerprinter fingerprinter,
            EngineCache cache,
            IReadonlyDirectedGraph graph,
            CounterCollection<FingerprintStoreCounters> fingerprintStoreCounters,
            IDictionary<PipId, RunnablePipPerformanceInfo> runnablePipPerformance,
            FileContentManager fileContentManager,
            FingerprintStoreTestHooks testHooks)
        {
            if (configuration.FingerprintStoreEnabled())
            {
                return FingerprintStoreExecutionLogTarget.Create(
                    context,
                    pipTable,
                    fingerprinter,
                    loggingContext,
                    configuration,
                    cache,
                    graph,
                    fingerprintStoreCounters,
                    runnablePipPerformance,
                    fileContentManager,
                    testHooks);
            }

            return null;
        }

        #endregion Event Logging

        #region Helpers

        private PipRuntimeInfo GetPipRuntimeInfo(PipId pipId)
        {
            return GetPipRuntimeInfo(pipId.ToNodeId());
        }

        private PipRuntimeInfo GetPipRuntimeInfo(NodeId nodeId)
        {
            Contract.Assume(IsInitialized);

            var info = m_pipRuntimeInfos[(int)nodeId.Value];
            if (info == null)
            {
                Interlocked.CompareExchange(ref m_pipRuntimeInfos[(int)nodeId.Value], new PipRuntimeInfo(), null);
            }

            info = m_pipRuntimeInfos[(int)nodeId.Value];
            Contract.Assume(info != null);
            return info;
        }

        #endregion Helpers

        #region Schedule Requests

        /// <summary>
        /// Retrieves the list of pips of a particular type that are in the provided state
        /// </summary>
        public IEnumerable<PipReference> RetrievePipReferencesByStateOfType(PipType pipType, PipState state)
        {
            // This method may be called externally after this Scheduler has been disposed, such as when FancyConsole
            // calls it from another thread. Calls should not be honored after it has been disposed because there's
            // no guarantee about the state of the underlying PipTable that gets queried.
            lock (m_statusLock)
            {
                if (!m_isDisposed)
                {
                    foreach (PipId pipId in m_pipTable.Keys)
                    {
                        if (m_pipTable.GetPipType(pipId) == pipType &&
                            GetPipState(pipId) == state)
                        {
                            yield return new PipReference(m_pipTable, pipId, PipQueryContext.PipGraphRetrievePipsByStateOfType);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Retrieves a list of all externally executing process pips
        /// </summary>
        public IEnumerable<PipReference> RetrieveExecutingProcessPips()
        {
            lock (m_statusLock)
            {
                if (!m_isDisposed)
                {
                    foreach (var item in LocalWorker.RunningPipExecutorProcesses)
                    {
                        yield return new PipReference(m_pipTable, item.Key, PipQueryContext.PipGraphRetrievePipsByStateOfType);
                    }
                }
            }
        }

        /// <summary>
        /// Evaluates a filter. All nodes satisfying the filter are put in m_explicitlyScheduledNodes.
        /// Returns all nodes that must be scheduled - this includes the explicitly scheduled,
        /// their dependencies and (if filter.DependencySelection == DependencySelection.DependenciesAndDependents)
        /// all their dependents.
        /// </summary>
        private bool TryGetFilteredNodes(LoggingContext loggingContext, RootFilter filter, SchedulerState state, out IEnumerable<NodeId> includedNodes)
        {
            Contract.Requires(filter != null);
            Contract.Assume(IsInitialized);

            RangedNodeSet filterPassingNodesNotYetScheduled;

            // If the previous state is not null and root filter matches, do not need to filter nodes again.
            if (state?.RootFilter != null && state.RootFilter.Matches(filter))
            {
                filterPassingNodesNotYetScheduled = state.FilterPassingNodes;
            }
            else if (!PipGraph.FilterNodesToBuild(
                loggingContext,
                filter,
                out filterPassingNodesNotYetScheduled))
            {
                // Find which nodes are in the set.
                Contract.Assume(loggingContext.ErrorWasLogged, "PipGraph.FilterNodesToBuild returned false but didn't log an error");
                includedNodes = new HashSet<NodeId>();
                return false;
            }

            // Save the filter and passing nodes for future builds (for SchedulerState in EngineState)
            FilterPassingNodes = filterPassingNodesNotYetScheduled.Clone();
            RootFilter = filter;

            m_explicitlyScheduledNodes = new HashSet<NodeId>();
            m_explicitlyScheduledProcessNodes = new HashSet<NodeId>();
            foreach (var filteredNode in filterPassingNodesNotYetScheduled)
            {
                m_explicitlyScheduledNodes.Add(filteredNode);
                if (m_pipTable.GetPipType(filteredNode.ToPipId()) == PipType.Process)
                {
                    m_explicitlyScheduledProcessNodes.Add(filteredNode);
                }
            }

            // Calculate nodes to schedule based off of explicitly scheduled nodes
            var calculatedNodes = CalculateNodesToSchedule(
                loggingContext,
                explicitlySelectedNodes: m_explicitlyScheduledNodes);

            includedNodes = ScheduleServiceFinalizations(calculatedNodes);
            return true;
        }

        private IEnumerable<NodeId> ScheduleServiceFinalizations(IEnumerable<NodeId> calculatedNodes)
        {
            // If there are any service client nodes, make sure corresponding service finalizers are included
            var scheduledServices = new HashSet<PipId>();
            foreach (var node in calculatedNodes)
            {
                var mutable = m_pipTable.GetMutable(node.ToPipId());
                if (mutable.PipType == PipType.Ipc || mutable.PipType == PipType.Process)
                {
                    ProcessMutablePipState processMutable = mutable as ProcessMutablePipState;
                    Contract.Assert(mutable != null, "Unexpected mutable pip type");
                    var nodeServiceInfo = processMutable.ServiceInfo;
                    if (nodeServiceInfo != null && nodeServiceInfo.Kind == ServicePipKind.ServiceClient)
                    {
                        scheduledServices.UnionWith(nodeServiceInfo.ServicePipDependencies);
                    }
                }
            }

            // if there are no services, don't bother creating a union
            if (!scheduledServices.Any())
            {
                return calculatedNodes;
            }

            // else, create a union of calculated nodes and finalization pips of all scheduled services
            var union = new HashSet<NodeId>(calculatedNodes);
            foreach (var servicePipId in scheduledServices)
            {
                ProcessMutablePipState processMutable = m_pipTable.GetMutable(servicePipId) as ProcessMutablePipState;
                Contract.Assert(processMutable != null, "Unexpected mutable pip type");
                var servicePipServiceInfo = processMutable.ServiceInfo;
                if (servicePipServiceInfo != null)
                {
                    foreach (var serviceFinalizationPipId in servicePipServiceInfo.FinalizationPipIds)
                    {
                        union.Add(serviceFinalizationPipId.ToNodeId());
                    }
                }
            }

            return union;
        }

        private IEnumerable<NodeId> CalculateNodesToSchedule(
            LoggingContext loggingContext,
            IEnumerable<NodeId> explicitlySelectedNodes = null,
            bool scheduleDependents = false)
        {
            var forceSkipDepsMode = m_configuration.Schedule.ForceSkipDependencies;

            if (explicitlySelectedNodes == null)
            {
                if (IncrementalSchedulingState == null)
                {
                    // Short cut.
                    return DirectedGraph.Nodes;
                }

                // We don't select nodes explicitly (through filters). This also means that we select all nodes.
                // BuildSetCalculator will add the meta-pips after calculating the nodes to scheduled.
                explicitlySelectedNodes = DirectedGraph.Nodes.Where(node => !m_pipTable.GetPipType(node.ToPipId()).IsMetaPip());
                forceSkipDepsMode = ForceSkipDependenciesMode.Disabled;
            }

            var buildSetCalculator = new SchedulerBuildSetCalculator(loggingContext, this);
            var scheduledNodesResult = buildSetCalculator.GetNodesToSchedule(
                scheduleDependents: scheduleDependents,
                explicitlyScheduledNodes: explicitlySelectedNodes,
                forceSkipDepsMode: forceSkipDepsMode,
                scheduleMetaPips: m_configuration.Schedule.ScheduleMetaPips);

            // Update counters to reflect pips that are marked clean from incremental scheduling
            m_numProcessesIncrementalSchedulingPruned = scheduledNodesResult.IncrementalSchedulingCacheHitProcesses - scheduledNodesResult.CleanMaterializedProcessFrontierCount;
            m_numProcessPipsSatisfiedFromCache += m_numProcessesIncrementalSchedulingPruned;
            for (int i = 0; i < m_numProcessesIncrementalSchedulingPruned; i++)
            {
                m_pipStateCounters.AccumulateTransition(PipState.Ignored, PipState.Done, PipType.Process);
            }

            m_numProcessPipsCompleted += m_numProcessesIncrementalSchedulingPruned;
            m_mustExecuteNodesForDirtyBuild = scheduledNodesResult.MustExecuteNodes;
            return scheduledNodesResult.ScheduledNodes;
        }

        /// <summary>
        /// Maximum number of external processes run concurrently so far.
        /// </summary>
        public long MaxExternalProcessesRan => Volatile.Read(ref m_maxExternalProcessesRan);

        /// <inheritdoc/>
        public VmInitializer VmInitializer { get; }

        /// <inheritdoc/>
        public IRemoteProcessManager RemoteProcessManager { get; }

        /// <inheritdoc/>
        public ReparsePointResolver ReparsePointAccessResolver { get; }

        private long m_maxExternalProcessesRan;

        private bool m_materializeOutputsQueued;

        private readonly TaskSourceSlim<bool> m_schedulerCompletion = TaskSourceSlim.Create<bool>();
        private readonly OchestratorSpecificExecutionLogTarget m_orchestratorTarget;
        private DateTime? m_schedulerCompletionExceptMaterializeOutputsTimeUtc;

        private DateTime m_schedulerDoneTimeUtc;
        private DateTime m_schedulerStartedTimeUtc;

        /// <inheritdoc/>
        public void SetMaxExternalProcessRan()
        {
            long currentMaxRunning;
            do
            {
                currentMaxRunning = MaxExternalProcessesRan;
            }
            while (Interlocked.CompareExchange(ref m_maxExternalProcessesRan, PipExecutionCounters.GetCounterValue(PipExecutorCounter.ExternalProcessCount), currentMaxRunning) != currentMaxRunning);
        }

#pragma warning disable CA1010 // Collections should implement generic interface
        private sealed class StatusRows : IEnumerable
#pragma warning restore CA1010 // Collections should implement generic interface
        {
            private readonly List<string> m_headers = new List<string>();
            private readonly List<bool> m_includeInSnapshot = new List<bool>();
            private readonly List<Func<StatusEventData, object>> m_rowValueGetters = new List<Func<StatusEventData, object>>();
            private bool m_sealed;

            public void Add(string header, Func<StatusEventData, object> rowValueGetter, bool includeInSnapshot = true)
            {
                Contract.Assert(!m_sealed);
                m_headers.Add(header);
                m_includeInSnapshot.Add(includeInSnapshot);
                m_rowValueGetters.Add(rowValueGetter);
            }

            public void Add(Action<StatusRows> rowAdder)
            {
                rowAdder(this);
            }

            public void Add<T>(IEnumerable<T> items, Action<StatusRows, T> rowAdder)
            {
                foreach (var item in items)
                {
                    rowAdder(this, item);
                }
            }

            public void Add<T>(IEnumerable<T> items, Func<T, string> itemHeaderGetter, Func<T, int, Func<StatusEventData, object>> itemRowValueGetter)
            {
                if (items == null)
                {
                    return;
                }

                int index = 0;
                foreach (var item in items)
                {
                    Add(itemHeaderGetter(item), itemRowValueGetter(item, index));
                    index++;
                }
            }

            public IEnumerator GetEnumerator()
            {
                foreach (var header in m_headers)
                {
                    yield return header;
                }
            }

            public string PrintHeaders()
            {
                Contract.Assert(m_sealed);
                return string.Join(",", m_headers);
            }

            public IDictionary<string, string> GetSnapshot(StatusEventData data)
            {
                Dictionary<string, string> snapshot = new Dictionary<string, string>();
                for (int i = 0; i < m_headers.Count; i++)
                {
                    if (m_includeInSnapshot[i])
                    {
                        snapshot.Add(m_headers[i], m_rowValueGetters[i](data).ToString());
                    }
                }

                return snapshot;
            }

            public string PrintRow(StatusEventData data)
            {
                Contract.Assert(m_sealed);
                return string.Join(",", m_rowValueGetters.Select((rowValueGetter, index) => rowValueGetter(data).ToString().PadLeft(m_headers[index].Length)));
            }

            public StatusRows Seal()
            {
                m_sealed = true;
                return this;
            }
        }

        /// <summary>
        /// Build set calculator which interfaces with the scheduler
        /// </summary>
        internal sealed class SchedulerBuildSetCalculator : BuildSetCalculator<Process, AbsolutePath, FileArtifact, DirectoryArtifact>
        {
            private readonly Scheduler m_scheduler;

            public SchedulerBuildSetCalculator(LoggingContext loggingContext, Scheduler scheduler)
                : base(
                    loggingContext,
                    scheduler.PipGraph.DirectedGraph,
                    scheduler.IncrementalSchedulingState?.DirtyNodeTracker,
                    scheduler.PipExecutionCounters)
            {
                m_scheduler = scheduler;
            }

            protected override bool ExistsAsFile(AbsolutePath path)
            {
                Possible<PathExistence> possibleProbeResult = m_scheduler.m_localDiskContentStore.TryProbeAndTrackPathForExistence(path);
                return possibleProbeResult.Succeeded && possibleProbeResult.Result == PathExistence.ExistsAsFile;
            }

            protected override ReadOnlyArray<DirectoryArtifact> GetDirectoryDependencies(Process process) => process.DirectoryDependencies;

            protected override ReadOnlyArray<FileArtifact> GetFileDependencies(Process process) => process.Dependencies;

            protected override AbsolutePath GetPath(FileArtifact file) => file.Path;

            protected override string GetPathString(AbsolutePath path) => path.ToString(m_scheduler.Context.PathTable);

            protected override PipType GetPipType(NodeId node) => m_scheduler.m_pipTable.GetPipType(node.ToPipId());

            protected override Process GetProcess(NodeId node) =>
                (Process)m_scheduler.m_pipTable.HydratePip(node.ToPipId(), PipQueryContext.SchedulerAreInputsPresentForSkipDependencyBuild);

            protected override FileArtifact GetCopyFile(NodeId node) =>
                ((CopyFile)m_scheduler.m_pipTable.HydratePip(node.ToPipId(), PipQueryContext.SchedulerAreInputsPresentForSkipDependencyBuild)).Source;

            protected override DirectoryArtifact GetSealDirectoryArtifact(NodeId node) =>
                ((SealDirectory)m_scheduler.m_pipTable.HydratePip(node.ToPipId(), PipQueryContext.SchedulerAreInputsPresentForSkipDependencyBuild)).Directory;

            protected override ReadOnlyArray<FileArtifact> ListSealedDirectoryContents(DirectoryArtifact directory) => m_scheduler.PipGraph.ListSealedDirectoryContents(directory);

            protected override bool IsFileRequiredToExist(FileArtifact file)
            {
                // Source files are not required to exist and rerunning the hash source file pip
                // will not cause them to exist so this shouldn't invalidate the existence check.
                return !file.IsSourceFile;
            }

            protected override NodeId GetProducer(FileArtifact file) => m_scheduler.PipGraph.GetProducerNode(file);

            protected override NodeId GetProducer(DirectoryArtifact directory) => m_scheduler.PipGraph.GetSealedDirectoryNode(directory);

            protected override bool IsDynamicKindDirectory(NodeId node) => m_scheduler.m_pipTable.GetSealDirectoryKind(node.ToPipId()).IsDynamicKind();

            protected override SealDirectoryKind GetSealedDirectoryKind(NodeId node) => m_scheduler.m_pipTable.GetSealDirectoryKind(node.ToPipId());

            protected override ModuleId GetModuleId(NodeId node) =>
                m_scheduler.m_pipTable.HydratePip(node.ToPipId(), PipQueryContext.SchedulerAreInputsPresentForSkipDependencyBuild).Provenance?.ModuleId ?? ModuleId.Invalid;

            protected override string GetModuleName(ModuleId moduleId)
            {
                if (!moduleId.IsValid)
                {
                    return "Invalid";
                }

                var pip = (ModulePip)m_scheduler.m_pipTable.HydratePip(
                    m_scheduler.PipGraph.Modules[moduleId].ToPipId(),
                    PipQueryContext.SchedulerAreInputsPresentForSkipDependencyBuild);
                return pip.Identity.ToString(m_scheduler.Context.StringTable);
            }

            protected override string GetDescription(NodeId node)
            {
                var pip = m_scheduler.m_pipTable.HydratePip(
                    node.ToPipId(),
                    PipQueryContext.SchedulerAreInputsPresentForSkipDependencyBuild);
                var moduleId = pip.Provenance?.ModuleId ?? ModuleId.Invalid;
                return pip.GetDescription(m_scheduler.Context) + " - Module: " + GetModuleName(moduleId);
            }

            protected override bool IsRewrittenPip(NodeId node) => m_scheduler.PipGraph.IsRewrittenPip(node.ToPipId());

            protected override bool IsSucceedFast(NodeId node) => m_scheduler.m_pipTable.GetMutable(node.ToPipId()) is ProcessMutablePipState ps && ps.IsSucceedFast;
        }

        /// <summary>
        /// Inform the scheduler that we want to terminate ASAP (but with clean shutdown as needed).
        /// </summary>
        private void RequestTermination(bool cancelQueue = true, bool cancelRunningPips = false, TimeSpan? cancelQueueTimeout = null)
        {
            if (m_scheduleTerminating)
            {
                if (cancelRunningPips)
                {
                    // Previous termination call may not have requested cancellation of already running pips.
                    // Hence we need to process that part of the request even if another termination is already in progress.
                    m_schedulerCancellationTokenSource?.Cancel();
                }
                return;
            }

            // This flag prevents normally-scheduled pips (i.e., by refcount) from starting (thus m_numPipsQueuedOrRunning should
            // reach zero quickly). But we do allow further pips to run inline (see RunPipInline); that's safe from an error
            // reporting perspective since m_hasFailures latches to false.
            m_scheduleTerminating = true;

            // A build that got canceled certainly didn't succeed.
            m_hasFailures = true;

            if (cancelQueue)
            {
                // We cancel the queue for more aggressive but still graceful cancellation.
                // This will stop pips to make transition between PipExecutionSteps.
                PipQueue.Cancel(cancelQueueTimeout);
            }

            if (cancelRunningPips)
            {
                // Cancel actively running external processes.
                m_schedulerCancellationTokenSource?.Cancel();
            }
        }

        /// <inheritdoc />
        public void ReportProblematicWorker()
        {
            int numProblematicWorkers = Interlocked.Increment(ref m_numProblematicWorkers);

            if (EngineEnvironmentSettings.LimitProblematicWorkerCount &&
                m_remoteWorkers.Length >= 4 &&
                numProblematicWorkers >= (m_remoteWorkers.Length * EngineEnvironmentSettings.LimitProblematicWorkerThreshold))
            {
                // Because LimitProblematicWorkerThreshold is 0.9 by default, we will fail the build only when all workers fail until 10 workers.
                Logger.Log.HighCountProblematicWorkers(m_loggingContext, numProblematicWorkers, m_remoteWorkers.Length);
                TerminateForInternalError();
            }
        }

        #endregion

        /// <inheritdoc />
        public void Dispose()
        {
            lock (m_statusLock)
            {
                m_isDisposed = true;
            }

            m_cancellationTokenRegistration.Dispose();

            ExecutionLog?.Dispose();
            SandboxConnection?.Dispose();
            RemoteProcessManager?.Dispose();

            m_workers.ForEach(w => w.Dispose());

            m_performanceAggregator?.Dispose();
            m_ipcProvider.Dispose();
            m_apiServer?.Dispose();
            m_pluginManager?.Dispose();
            m_schedulerCancellationTokenSource.Dispose();

            m_pipTwoPhaseCache?.CloseAsync().GetAwaiter().GetResult();

            // The store is disposed if WhenDone method was called and finished successfully.
            // Disposing here just in case if something went wrong in WhenDone or that method was never call.
            // Dispsing the fingerprint store twice is safe.
            m_fingerprintStoreTarget?.Dispose();
        }

        /// <inheritdoc />
        public bool IsFileRewritten(FileArtifact file)
        {
            var latestFile = PipGraph.TryGetLatestFileArtifactForPath(file.Path);
            return latestFile.IsValid && latestFile.RewriteCount > file.RewriteCount;
        }

        /// <inheritdoc />
        public bool ShouldCreateHandleWithSequentialScan(FileArtifact file)
        {
            if (m_scheduleConfiguration.CreateHandleWithSequentialScanOnHashingOutputFiles
                && file.IsOutputFile
                && PipGraph.TryGetLatestFileArtifactForPath(file.Path) == file
                && m_outputFileExtensionsForSequentialScan.Contains(file.Path.GetExtension(Context.PathTable)))
            {
                PipExecutionCounters.IncrementCounter(PipExecutorCounter.CreateOutputFileHandleWithSequentialScan);
                return true;
            }

            return false;
        }

        /// <summary>
        /// <see cref="PipExecutionState.LazyDeletionOfSharedOpaqueOutputsEnabled"/>
        /// </summary>
        internal void SetSidebandState(SidebandState sidebandState)
        {
            m_sidebandState = sidebandState;
        }

        /// <summary>
        /// Combine with EventStatsExecutionLogTarget to track manifest event count for worker.
        /// </summary>
        internal void SetManifestExecutionLogForWorker(ExecutionLogFileTarget manifestExecutionLog)
        {
            m_workerManifestExecutionLogTarget = manifestExecutionLog;
            m_manifestExecutionLog = MultiExecutionLogTarget.CombineTargets(m_workerManifestExecutionLogTarget, m_eventStatsExecutionLogTarget);
        }

        /// <summary>
        /// Set the report execution log LogTarget and add it into multiple execution log targets.
        /// </summary>
        internal void AddReportExecutionLogTargetForWorker(ExecutionLogFileTarget reportExecutionLogTarget)
        {
            m_reportExecutionLogTarget = reportExecutionLogTarget;
            AddExecutionLogTarget(m_reportExecutionLogTarget);
        }

        /// <summary>
        /// Log the number of pending events remaining after the execution log is disposed.
        /// </summary>
        public void LogPendingEventsRemaingAfterDispose(long pendingEvents)
        {
            Logger.Log.PendingEventsRemaingAfterDisposed(m_loggingContext, pendingEvents);
        }
    }
}
