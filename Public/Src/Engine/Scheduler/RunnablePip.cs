// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Threading.Tasks;
using BuildXL.Native.IO;
using BuildXL.Pips;
using BuildXL.Pips.Operations;
using BuildXL.ProcessPipExecutor;
using BuildXL.Scheduler.Distribution;
using BuildXL.Scheduler.Tracing;
using BuildXL.Scheduler.WorkDispatcher;
using BuildXL.Storage.Fingerprints;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;
using static BuildXL.Utilities.Core.FormattableStringEx;

namespace BuildXL.Scheduler
{
    /// <summary>
    /// Execution state that is carried between pip execution steps.
    /// </summary>
    public class RunnablePip
    {
        /// <summary>
        /// PipId
        /// </summary>
        public PipId PipId { get; }

        /// <summary>
        /// The operation scope for the active operation for the runnable pip
        /// </summary>
        public OperationContext OperationContext { get; private set; }

        /// <summary>
        /// Gets the runnable pip observer
        /// </summary>
        public RunnablePipObserver Observer { get; private set; } = RunnablePipObserver.Default;

        /// <summary>
        /// Sequence number used to track changes in ChooseWorker state as indication that ChooseWorker should be
        /// paused/unpaused based on whether workers are possibly available
        /// </summary>
        public int ChooseWorkerSequenceNumber { get; set; }

        /// <summary>
        /// Pip type
        /// </summary>
        public PipType PipType { get; }

        /// <summary>
        /// Priority
        /// </summary>
        public int Priority { get; private set; }

        /// <summary>
        /// Execution environment
        /// </summary>
        public IPipExecutionEnvironment Environment { get; }

        /// <summary>
        /// Weight which specifies the number of slots to acquire in the workers and dispatchers.
        /// </summary>
        /// <remarks>
        /// It is always one for non-process pips.
        /// </remarks>
        public virtual int Weight => 1;

        /// <summary>
        /// Whether the pip is an IPC pips or light process pip.
        /// </summary>
        public virtual bool IsLight => PipType == PipType.Ipc;

        /// <summary>
        /// The underlying pip
        /// </summary>
        public Pip Pip
        {
            get
            {
                if (m_pip == null)
                {
                    m_pip = Environment.PipTable.HydratePip(PipId, PipQueryContext.RunnablePip);
                }

                return m_pip;
            }
        }

        private Pip m_pip;

        /// <summary>
        /// Pip description
        /// </summary>
        public string Description
        {
            get
            {
                if (m_description == null)
                {
                    m_description = Pip.GetDescription(Environment.Context);
                }

                return m_description;
            }
        }

        private string m_description;

        /// <summary>
        /// Formatted SemiStableHash
        /// </summary>
        public string FormattedSemiStableHash
        {
            get
            {
                if (m_formattedSemiStableHash == null)
                {
                    m_formattedSemiStableHash = Pip.FormattedSemiStableHash;
                }

                return m_formattedSemiStableHash;
            }
        }

        private string m_formattedSemiStableHash;

        /// <summary>
        /// Whether the pip is set as cancelled due to 'StopOnFirstFailure'.
        /// </summary>
        public bool IsCancelled { get; private set; }

        /// <summary>
        /// Logging context
        /// </summary>
        /// <remarks>
        /// Initially when the RunnablePip is constructed the loggingcontext will be a generic phase level context. when
        /// the pip actually starts, a pip specific context is created. This way we can ensure there is always an associated
        /// LoggingContext no matter what the state of the pip is.</remarks>
        public LoggingContext LoggingContext => OperationContext.LoggingContext;

        /// <nodoc/>
        public PooledObjectWrapper<Task[]>? MaterializeOutputsTasks;

        /// <summary>
        /// Time when the pip is started executing
        /// </summary>
        public DateTime StartTime { get; private set; }

        /// <summary>
        /// Time when the pip is scheduled
        /// </summary>
        public DateTime ScheduleTime { get; private set; }

        /// <summary>
        /// The time spent running for the pip (not queued)
        /// </summary>
        public TimeSpan RunningTime { get; private set; }

        /// <summary>
        /// Pip result
        /// </summary>
        public PipResult? Result { get; private set; }

        /// <summary>
        /// Pip execution result
        /// </summary>
        public ExecutionResult ExecutionResult { get; private set; }

        /// <summary>
        /// The current pip execution step
        /// </summary>
        public PipExecutionStep Step { get; private set; }

        /// <summary>
        /// The current dispatcher
        /// </summary>
        internal DispatcherKind DispatcherKind { get; private set; }

        /// <summary>
        /// Worker which executes this pip
        /// </summary>
        public Worker Worker { get; private set; }

        /// <summary>
        /// Worker which executes this pip. This field is only valid after acquiring worker resources and before releasing resources.
        /// NOTE: This is different than <see cref="Worker"/> which is set for steps that don't acquire resources.
        /// </summary>
        public Worker AcquiredResourceWorker { get; internal set; }

        /// <summary>
        /// Gets whether the machine represents a distributed worker
        /// </summary>
        private bool IsDistributedWorker => Environment.Configuration.Distribution.BuildRole == DistributedBuildRoles.Worker;

        private readonly Func<RunnablePip, Task> m_executionFunc;

        private DispatcherReleaser m_dispatcherReleaser;

        /// <summary>
        /// Worker id for the preferred worker when module affinity is enabled
        /// </summary>
        public int? PreferredWorkerId { get; internal set; }

        internal RunnablePipPerformanceInfo Performance { get; }

        /// <summary>
        /// Whether waiting on resources (worker).
        /// </summary>
        public bool IsWaitingForWorker { get; internal set; }

        /// <summary>
        /// Whether executing on a remote worker without acquiring a slot on the orchestrator
        /// </summary>
        public bool IsRemotelyExecuting { get; internal set; }

        /// <summary>
        /// Thread id of the step
        /// </summary>
        public int ThreadId { get; internal set; }

        /// <summary>
        /// Start time of the step
        /// </summary>
        public DateTime StepStartTime { get; internal set; }

        /// <summary>
        /// Duration of the execution step
        /// </summary>
        public TimeSpan StepDuration { get; internal set; }

        internal RunnablePip(
            LoggingContext phaseLoggingContext,
            PipId pipId,
            PipType type,
            int priority,
            Func<RunnablePip, Task> executionFunc,
            IPipExecutionEnvironment environment,
            Pip pip = null)
        {
            Contract.Requires(phaseLoggingContext != null);
            Contract.Requires(environment != null);

            PipId = pipId;
            PipType = type;
            Priority = priority;
            OperationContext = OperationContext.CreateUntracked(phaseLoggingContext);
            m_executionFunc = executionFunc;
            Environment = environment;
            Transition(PipExecutionStep.Start);
            ScheduleTime = DateTime.UtcNow;
            Performance = new RunnablePipPerformanceInfo(ScheduleTime);
            m_pip = pip;
        }

        /// <summary>
        /// Transition to another step
        /// </summary>
        public void Transition(PipExecutionStep toStep, bool force = false)
        {
            if (!force && !Step.CanTransitionTo(toStep))
            {
                Contract.Assert(false, I($"Cannot transition from {Step} to {toStep}"));
            }

            Step = toStep;

            if (toStep == PipExecutionStep.Done)
            {
                End();
            }
        }

        /// <summary>
        /// Whether to include the pip in the tracer log
        /// </summary>
        public bool IncludeInTracer => Environment.Configuration.Logging.LogTracer
            && Step.IncludeInTracer()
            && (PipType == PipType.Process || PipType == PipType.Ipc)
            && Worker != null;

        /// <summary>
        /// Changes the priority of the pip
        /// </summary>
        public void ChangePriority(int priority)
        {
            Priority = priority;
        }

        /// <summary>
        /// Sets logging context and start time of the pip
        /// </summary>
        public void Start(OperationTracker tracker, LoggingContext loggingContext)
        {
            Contract.Assert(Step == PipExecutionStep.Start || IsDistributedWorker);

            OperationContext = tracker.StartOperation(PipExecutorCounter.PipRunningStateDuration, PipId, PipType, loggingContext, OperationCompleted);
            StartTime = DateTime.UtcNow;
        }

        /// <nodoc/>
        protected virtual void OperationCompleted(OperationKind kind, TimeSpan duration)
        {
        }

        /// <summary>
        /// Ends the context for the runnable pip
        /// </summary>
        public void End()
        {
            OperationContext.Dispose();
            OperationContext = OperationContext.CreateUntracked(OperationContext.LoggingContext);
        }

        /// <summary>
        /// Sets the pip as cancelled and return <see cref="PipExecutionStep.Cancel"/> step
        /// </summary>
        public PipExecutionStep Cancel()
        {
            IsCancelled = true;
            // Pip might have been cancelled after we reached its execution step but before we actually started running it.
            // Make sure that ExecutionResult is always initialized.
            if ((PipType == PipType.Process || PipType == PipType.Ipc) && ExecutionResult == null)
            {
                Contract.Assert(Environment.IsTerminating, "Attempted to cancel a pip prior its execution but the scheduler is not terminating.");
                SetExecutionResult(ExecutionResult.GetCancelResult(LoggingContext));
            }

            return PipExecutionStep.Cancel;
        }

        /// <summary>
        /// Sets the pip result and return <see cref="PipExecutionStep.HandleResult"/> step to handle the result
        /// </summary>
        public PipExecutionStep SetPipResult(PipResultStatus status)
        {
            return SetPipResult(CreatePipResult(status));
        }

        /// <summary>
        /// Creates a pip result for the given status
        /// </summary>
        public PipResult CreatePipResult(PipResultStatus status)
        {
            return PipResult.Create(status, StartTime);
        }

        /// <summary>
        /// Sets the pip result and return <see cref="PipExecutionStep.HandleResult"/> step to handle the result
        /// </summary>
        public PipExecutionStep SetPipResult(in PipResult result)
        {
            Performance.Suspended(ExecutionResult?.PerformanceInformation?.SuspendedDurationMs ?? 0);
            Performance.SetPushOutputsToCacheDurationMs(ExecutionResult?.PerformanceInformation?.PushOutputsToCacheDurationMs ?? 0);

            if (Environment.IsTerminating)
            {
                // If we are terminating (and this means the build was cancelled externally)
                // don't bother checking our invariants below, just return and let the cancellation
                // run its course.
                Result = result;
                return PipExecutionStep.HandleResult;
            }

            if (result.Status == PipResultStatus.Canceled &&
                !Environment.IsTerminating)
            {
                // Handle Retryable Cancellations
                Performance.Retried(ExecutionResult?.RetryInfo ?? RetryInfo.GetDefault(RetryReason.RemoteWorkerFailure), ExecutionResult?.PerformanceInformation?.ProcessExecutionTime);
                return DecideNextStepForRetry();
            }

            if (result.Status == PipResultStatus.Failed)
            {
                Contract.Assert(LoggingContext.ErrorWasLogged, "Error was not logged for pip marked as failure");
            }

            Result = result;
            return PipExecutionStep.HandleResult;
        }

        private PipExecutionStep DecideNextStepForRetry()
        {
            if (PipType == PipType.Ipc)
            {
                return PipExecutionStep.ChooseWorkerIpc;
            }

            if (Step == PipExecutionStep.CacheLookup)
            {
                return PipExecutionStep.ChooseWorkerCacheLookup;
            }

            return PipExecutionStep.ChooseWorkerCpu;
        }

        /// <summary>
        /// Sets the pip result with <see cref="ExecutionResult"/>
        /// </summary>
        public virtual PipExecutionStep SetPipResult(ExecutionResult executionResult)
        {
            SetExecutionResult(executionResult);

            // For process pips, create the pip result with the performance info.
            bool withPerformanceInfo = Pip.PipType == PipType.Process;
            var pipResult = CreatePipResultFromExecutionResult(StartTime, executionResult, withPerformanceInfo);
            return SetPipResult(pipResult);
        }

        /// <summary>
        /// Sets the execution result
        /// </summary>
        public virtual void SetExecutionResult(ExecutionResult executionResult)
        {
            Contract.Requires(PipType == PipType.Process || PipType == PipType.Ipc, "Only process or IPC pips can set the execution result");

            if (!executionResult.IsSealed)
            {
                executionResult.Seal();
            }

            ExecutionResult = executionResult;
        }

        /// <summary>
        /// Sets the observer
        /// </summary>
        public void SetObserver(RunnablePipObserver observer)
        {
            Observer = observer ?? RunnablePipObserver.Default;
        }

        /// <summary>
        /// Sets the dispatcher kind
        /// </summary>
        public void SetDispatcherKind(DispatcherKind kind)
        {
            DispatcherKind = kind;
            Performance.Enqueued(kind);
        }

        /// <summary>
        /// Sets the worker
        /// </summary>
        public void SetWorker(Worker worker)
        {
            if (worker != null)
            {
                IsWaitingForWorker = false;
            }
            else if (Step.IsChooseWorker())
            {
                // If we did not choose a worker, the pip is waiting for a worker.
                IsWaitingForWorker = true;
            }

            Worker = worker;
        }

        /// <summary>
        /// Runs executionFunc and release resources if any worker is given
        /// </summary>
        public Task RunAsync(DispatcherReleaser dispatcherReleaser = null)
        {
            Contract.Requires(m_executionFunc != null);

            m_dispatcherReleaser = dispatcherReleaser ?? m_dispatcherReleaser;

            bool hasWaitedForMaterializeOutputsInBackground = Step == PipExecutionStep.MaterializeOutputs && Environment.MaterializeOutputsInBackground;
            Performance.Dequeued(hasWaitedForMaterializeOutputsInBackground);
            return m_executionFunc(this);
        }

        /// <summary>
        /// Release dispatcher
        /// </summary>
        public void ReleaseDispatcher()
        {
            m_dispatcherReleaser?.Release(Weight);
        }

        /// <summary>
        /// Logs the performance information for the <see cref="PipExecutionStep"/> to the execution log
        /// </summary>
        /// <remarks>
        /// If the step is executed on the remote worker, the duration will include the distribution 
        /// overhead (sending, receiving, queue time on worker) as well. 
        /// </remarks>
        public void LogExecutionStepPerformance(
            PipExecutionStep step,
            DateTime startTime,
            TimeSpan duration)
        {
            bool includeInRunningTime = step.IncludeInRunningTime(Environment);
            if (includeInRunningTime)
            {
                RunningTime += duration;
            }

            Performance.Executed(step, duration);

            // There are too many of these events and they bloat the xlg (100GB+ is possible)
            if (!step.IsChooseWorker())
            {
                Environment.State.ExecutionLog?.PipExecutionStepPerformanceReported(new PipExecutionStepPerformanceEventData
                {
                    PipId = PipId,
                    StartTime = startTime,
                    Duration = duration,
                    Dispatcher = DispatcherKind,
                    Step = step,
                    IncludeInRunningTime = includeInRunningTime
                });
            }
        }

        /// <summary>
        /// Logs the performance information for the <see cref="PipExecutionStep"/> to the execution log
        /// </summary>
        public void LogRemoteExecutionStepPerformance(
            uint workerId,
            PipExecutionStep step,
            TimeSpan remoteStepDuration,
            TimeSpan remoteQueueDuration,
            TimeSpan queueRequestDuration,
            TimeSpan grpcDuration)
        {
            Performance.RemoteExecuted(workerId, step, remoteStepDuration, remoteQueueDuration, queueRequestDuration, grpcDuration);
        }

        /// <summary>
        /// Creates a runnable pip
        /// </summary>
        public static RunnablePip Create(
            LoggingContext loggingContext,
            IPipExecutionEnvironment environment,
            PipId pipId,
            PipType type,
            int priority,
            Func<RunnablePip, Task> executionFunc,
            ushort cpuUsageInPercent)
        {
            switch (type)
            {
                case PipType.Process:
                    return new ProcessRunnablePip(loggingContext, pipId, priority, executionFunc, environment, cpuUsageInPercent);
                default:
                    return new RunnablePip(loggingContext, pipId, type, priority, executionFunc, environment);
            }
        }

        /// <summary>
        /// Creates a runnable pip with a hydrated pip
        /// </summary>
        public static RunnablePip Create(
            LoggingContext loggingContext,
            IPipExecutionEnvironment environment,
            Pip pip,
            int priority,
            Func<RunnablePip, Task> executionFunc)
        {
            switch (pip.PipType)
            {
                case PipType.Process:
                    return new ProcessRunnablePip(loggingContext, pip.PipId, priority, executionFunc, environment, pip: pip);
                default:
                    return new RunnablePip(loggingContext, pip.PipId, pip.PipType, priority, executionFunc, environment, pip);
            }
        }

        /// <summary>
        /// Creates the pip result from the execution result
        /// </summary>
        public static PipResult CreatePipResultFromExecutionResult(DateTime start, ExecutionResult result, bool withPerformanceInfo = false)
        {
            result.Seal();

            Contract.Assert(result.Result.IndicatesExecution());
            DateTime stop = DateTime.UtcNow;

            PipExecutionPerformance perf;

            if (withPerformanceInfo)
            {
                if (result.PerformanceInformation != null)
                {
                    var performanceInformation = result.PerformanceInformation;

                    perf = new ProcessPipExecutionPerformance(
                        performanceInformation.ExecutionLevel,
                        start,
                        stop,
                        fingerprint: performanceInformation.Fingerprint,
                        processExecutionTime: performanceInformation.ProcessExecutionTime,
                        fileMonitoringViolations: performanceInformation.FileMonitoringViolations,
                        ioCounters: performanceInformation.IO,
                        userTime: performanceInformation.UserTime,
                        kernelTime: performanceInformation.KernelTime,
                        memoryCounters: performanceInformation.MemoryCounters,
                        numberOfProcesses: performanceInformation.NumberOfProcesses,
                        workerId: performanceInformation.WorkerId,
                        suspendedDurationMs: performanceInformation.SuspendedDurationMs,
                        pushOutputsToCacheDurationMs: performanceInformation.PushOutputsToCacheDurationMs);
                }
                else
                {
                    PipExecutionLevel level = result.Result.ToExecutionLevel();

                    // We didn't try to run a sandboxed process at all, or it didn't make it to the execution phase (no useful counters).
                    perf = new ProcessPipExecutionPerformance(
                            level,
                            start,
                            stop,
                            fingerprint: result.WeakFingerprint?.Hash ?? FingerprintUtilities.ZeroFingerprint,
                            processExecutionTime: TimeSpan.Zero,
                            fileMonitoringViolations: default(FileMonitoringViolationCounters),
                            ioCounters: default(IOCounters),
                            userTime: TimeSpan.Zero,
                            kernelTime: TimeSpan.Zero,
                            memoryCounters: ProcessMemoryCounters.CreateFromMb(0, 0, 0, 0),
                            numberOfProcesses: 0,
                            workerId: 0,
                            suspendedDurationMs: 0,
                            pushOutputsToCacheDurationMs: 0);
                }
            }
            else
            {
                perf = PipExecutionPerformance.CreatePoint(result.Result);
            }

            SplitDynamicObservations(result.DynamicObservations, out var observedFiles, out var probes, out var enumerations, out var absentPathProbes);

            return new PipResult(
                result.Result,
                perf,
                result.MustBeConsideredPerpetuallyDirty,
                observedFiles.ToReadOnlyArray(),
                probes.ToReadOnlyArray(),
                enumerations.ToReadOnlyArray(),
                absentPathProbes.ToReadOnlyArray(),
                result.ExitCode);
        }

        private static void SplitDynamicObservations(ReadOnlyArray<(AbsolutePath Path, DynamicObservationKind Kind)> dynamicObservations,
            out List<AbsolutePath> observedFiles, out List<AbsolutePath> probes, out List<AbsolutePath> enumerations, out List<AbsolutePath> absentPathProbes)
        {
            observedFiles = new List<AbsolutePath>();
            probes = new List<AbsolutePath>();
            enumerations = new List<AbsolutePath>();
            absentPathProbes = new List<AbsolutePath>();

            for (var i = 0; i < dynamicObservations.Length; i++)
            {
                switch (dynamicObservations[i].Kind)
                {
                    case DynamicObservationKind.ObservedFile:
                        observedFiles.Add(dynamicObservations[i].Path);
                        break;
                    case DynamicObservationKind.Enumeration:
                        enumerations.Add(dynamicObservations[i].Path);
                        break;
                    case DynamicObservationKind.ProbedFile:
                        probes.Add(dynamicObservations[i].Path);
                        break;
                    case DynamicObservationKind.AbsentPathProbe:
                    case DynamicObservationKind.AbsentPathProbeOutsideOutputDirectory:
                    case DynamicObservationKind.AbsentPathProbeUnderOutputDirectory:
                        absentPathProbes.Add(dynamicObservations[i].Path);
                        break;
                    default:
                        Contract.Assume(false, "Unknown DynamicObservationKind");
                        break;
                }
            }
        }

        /// <summary>
        /// Replaces active operation context for the runnable pip and returns a scope
        /// which restores the current active context when disposed
        /// </summary>
        public OperationScope EnterOperation(OperationContext context)
        {
            var scope = new OperationScope(this);
            OperationContext = context;
            return scope;
        }

        /// <summary>
        /// Captures operation context and restores it when the scope is exited
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public readonly struct OperationScope : IDisposable
        {
            private readonly OperationContext m_capturedContext;
            private readonly RunnablePip m_runnablePip;

            /// <nodoc />
            public OperationScope(RunnablePip runnablePip)
            {
                m_runnablePip = runnablePip;
                m_capturedContext = runnablePip.OperationContext;
            }

            /// <nodoc />
            public void Dispose()
            {
                m_runnablePip.OperationContext = m_capturedContext;
            }
        }
    }
}
