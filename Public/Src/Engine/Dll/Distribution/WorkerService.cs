// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Distribution.Grpc;
using BuildXL.Engine.Distribution.Grpc;
using BuildXL.Engine.Tracing;
using BuildXL.Pips;
using BuildXL.Scheduler;
using BuildXL.Scheduler.Distribution;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Core.Tasks;
using BuildXL.Utilities.Instrumentation.Common;
using static BuildXL.Distribution.Grpc.HelloResponse.Types;
using static BuildXL.Engine.Distribution.Grpc.ClientConnectionManager;
using PipGraphCacheDescriptor = BuildXL.Engine.Cache.Fingerprints.PipGraphCacheDescriptor;

namespace BuildXL.Engine.Distribution
{
    /// <summary>
    /// Service methods called by the RPC layer as part of the RPC started in the orchestrator
    /// </summary>
    /// <remarks>This interface is marked internal to reduce visibility to the distribution layer only</remarks>
    internal interface IWorkerService
    {
        /// <summary>
        /// Inform the locaton of this worker to the orchestrator
        /// </summary>
        Task SayHelloAsync(IDistributionServiceLocation orchestratorLocation);
        
        /// <summary>
        /// Performs attachment 
        /// </summary>
        void Attach(BuildStartData buildStartData, string sender);

        /// <summary>
        /// Requests execution of a pip build request
        /// </summary>
        /// <param name="request"></param>
        Task ExecutePipsAsync(PipBuildRequest request);

        /// <summary>
        /// Notifies the WorkerService that the orchestrator has issued an exit request
        /// </summary>
        /// <param name="failure">If present, the build will be considered a failure</param>
        /// <param name="exitMessage">Details of the operation to log on exit</param>
        void ExitRequested(string exitMessage, Optional<string> failure);
    }

    /// <summary>
    /// Defines service run on worker nodes in a distributed build.
    /// </summary>
    public sealed partial class WorkerService : DistributionService, IWorkerService
    {
        /// <summary>
        /// Gets the build start data from the coordinator passed after the attach operation
        /// </summary>
        public BuildStartData BuildStartData { get; private set; }

        /// <summary>
        /// Returns a task representing the completion of the exit operation
        /// </summary>
        internal Task<bool> ExitCompletion => m_exitCompletionSource.Task;
        private readonly TaskSourceSlim<bool> m_exitCompletionSource;

        /// <summary>
        /// Returns a task representing the completion of the attach operation
        /// </summary>
        internal Task<bool> AttachCompletion => m_attachCompletionSource.Task;
        private readonly TaskSourceSlim<bool> m_attachCompletionSource;

        private readonly ConcurrentDictionary<(PipId, PipExecutionStep), SinglePipBuildRequest> m_pendingBuildRequests = new();

        private readonly ConcurrentDictionary<(PipId, PipExecutionStep), ExtendedPipCompletionData> m_pendingPipCompletions = new();

        internal readonly ConcurrentDictionary<(PipId, PipExecutionStep), bool> PendingScheduleRequests = new();

        private readonly ConcurrentBigSet<int> m_handledBuildRequests = new ConcurrentBigSet<int>();

        /// <summary>
        /// Identifies the worker
        /// </summary>
        public uint WorkerId { get; private set; }

        private volatile bool m_hasFailures;
        private volatile string m_failureMessage;
        private int m_connectionClosedFlag = 0;

        /// <summary>
        /// Whether orchestrator is done with the worker by sending a message to worker.
        /// </summary>
        private volatile bool m_isOrchestratorExited;

        private LoggingContext m_appLoggingContext;
        private bool m_orchestratorInitialized;
        private readonly IWorkerNotificationManager m_notificationManager;
        private readonly IWorkerPipExecutionService m_pipExecutionService;
        private readonly IConfiguration m_config;
        private readonly ushort m_port;
        
        private readonly IOrchestratorClient m_orchestratorClient;
        private readonly IServer m_workerServer;

        /// <summary>
        /// Class constructor
        /// </summary>
        /// <param name="appLoggingContext">Application-level logging context</param>
        /// <param name="config">Build config</param>
        /// <param name="invocationId">the distributed invocation id</param>
        public WorkerService(LoggingContext appLoggingContext, IConfiguration config, DistributedInvocationId invocationId) :  
            this(appLoggingContext,
                config,
                invocationId,
                executionService: null,
                workerServer: null,
                notificationManager: null,
                orchestratorClient: null)
        {
            m_pipExecutionService = new WorkerPipExecutionService(this);
            m_notificationManager = new WorkerNotificationManager(this, m_pipExecutionService, appLoggingContext);
            m_orchestratorClient = new Grpc.GrpcOrchestratorClient(m_appLoggingContext, InvocationId, Counters);
            m_workerServer = new Grpc.GrpcWorkerServer(appLoggingContext, this, invocationId);
        }

        internal static WorkerService CreateForTesting(
            LoggingContext appLoggingContext,
            IConfiguration config,
            DistributedInvocationId invocationId,
            // The following are used for testing:
            IWorkerPipExecutionService executionService,
            IServer server,
            IWorkerNotificationManager notificationManager,
            IOrchestratorClient orchestratorClient)
        {
            return new WorkerService(appLoggingContext, config, invocationId, executionService, server, orchestratorClient, notificationManager);
        }

        private WorkerService(LoggingContext appLoggingContext, 
            IConfiguration config,
            DistributedInvocationId invocationId, 
            IWorkerPipExecutionService executionService, 
            IServer workerServer,
            IOrchestratorClient orchestratorClient,
            IWorkerNotificationManager notificationManager) : base(invocationId)
        {
            m_appLoggingContext = appLoggingContext;
            m_port = config.Distribution.BuildServicePort;
            m_config = config;
            m_attachCompletionSource = TaskSourceSlim.Create<bool>();
            m_exitCompletionSource = TaskSourceSlim.Create<bool>();

            m_workerServer = workerServer;
            m_pipExecutionService = executionService;
            m_notificationManager = notificationManager;
            m_orchestratorClient = orchestratorClient;
        }

        internal void Start(EngineSchedule schedule, ExecutionResultSerializer resultSerializer)
        {
            Contract.Assert(AttachCompletion.IsCompleted && AttachCompletion.GetAwaiter().GetResult(), "Start called before finishing attach on worker");
            m_pipExecutionService.Start(schedule, BuildStartData);
            m_notificationManager.Start(m_orchestratorClient, schedule, new PipResultSerializer(resultSerializer), m_config.Logging);
        }

        /// <summary>
        /// Connects to the orchestrator and enables this node to receive build requests
        /// </summary>
        internal async Task<bool> WhenDoneAsync()
        {
            Contract.Assert(AttachCompletion.IsCompleted && AttachCompletion.GetAwaiter().GetResult(), "ProcessBuildRequests called before finishing attach on worker");

            bool success = await CompleteAttachmentAfterProcessBuildRequestStartedAsync();

            // Wait until the build finishes or we discovered that the orchestrator is dead
            success &= await ExitCompletion;

            success &= !m_hasFailures;
            if (m_failureMessage != null)
            {
                Logger.Log.DistributionWorkerExitFailure(m_appLoggingContext, m_failureMessage);
            }


            if (success && !PendingScheduleRequests.IsEmpty)
            {
                // We might meet this condition on fireForgetMaterializeOutputBuilds: if the orchestrator calls Exit on a worker
                // after fire-forgetting all the materializations, we might still be processing materialization requests and in a stage
                // where they haven't reached the pip queue.
                // We should avoid marking the pip queue as complete if this is the case, so let's wait for everything to be queued
                // before doing that.
                while (!PendingScheduleRequests.IsEmpty)
                {
                    await Task.Delay(5_000);
                }
            }

            m_pipExecutionService.WhenDone();

            return success;
        }

        internal bool SayHello(IDistributionServiceLocation orchestratorLocation)
        {
            var timeout = GrpcSettings.WorkerAttachTimeout;
            if(!((IWorkerService)this).SayHelloAsync(orchestratorLocation).Wait(timeout))
            {
                Logger.Log.DistributionWorkerTimeoutFailure(m_appLoggingContext, "trying to say hello to the orchestrator");
                Exit(failure: $"Timed out saying hello to the orchestrator. Timeout: {timeout.TotalMinutes} min", isUnexpected: true);
                return false;
            }

            return true;
        }


        /// <summary>
        /// Waits for the orchestrator to attach synchronously
        /// </summary>
        internal bool WaitForOrchestratorAttach()
        {
            Logger.Log.DistributionWaitingForOrchestratorAttached(m_appLoggingContext);
            
            var timeout = GrpcSettings.WorkerAttachTimeout;
            var sw = Stopwatch.StartNew();
            if (!AttachCompletion.Wait(timeout))
            {
                Logger.Log.DistributionWorkerTimeoutFailure(m_appLoggingContext, "waiting for attach request from orchestrator");
                Exit(failure: $"Timed out waiting for attach request from orchestrator. Timeout: {timeout.TotalMinutes} min", isUnexpected: true);
                return false;
            }

            if (!AttachCompletion.Result)
            {
                if (m_isOrchestratorExited)
                {
                    // The orchestrator can send an Exit request before attachment if early releasing this worker
                    // This is a corner case, as the orchestrator waits for the attachment to complete before doing this.
                    // We should log an error in this case as we will fail the engine after returning false.
                    Logger.Log.DistributionOrchestratorExitBeforeAttachment(m_appLoggingContext, (int)sw.Elapsed.TotalMilliseconds);
                }
                else
                {
                    Logger.Log.DistributionInactiveOrchestrator(m_appLoggingContext, (int)timeout.TotalMinutes);
                }

                return false;
            }

            return true;
        }

        /// <nodoc/>
        void IWorkerService.ExitRequested(string exitMessage, Optional<string> failure)
        {
            m_isOrchestratorExited = true;
            Logger.Log.DistributionExitReceived(m_appLoggingContext, exitMessage);
            Exit(failure);
        }

        /// <nodoc/>
        public void Exit(Optional<string> failure = default, bool isUnexpected = false)
        {
            var reportSuccess = !failure.HasValue;

            m_notificationManager.Exit(isClean: reportSuccess && !isUnexpected);
            m_orchestratorClient.CloseAsync().GetAwaiter().GetResult();

            m_attachCompletionSource.TrySetResult(false);
            
            if (!reportSuccess)
            {
                m_hasFailures = true;
                m_failureMessage = failure.Value;
            }

            if (isUnexpected && m_isOrchestratorExited)
            {
                // If the worker unexpectedly exits the build after orchestrator exits the build, 
                // we should log a message to keep track of the frequency.
                Logger.Log.DistributionWorkerUnexpectedFailureAfterOrchestratorExits(m_appLoggingContext);
            }

            // Request server shut down before exiting, so the orchestrator stops requests our way
            // gRPC ensures that any pending calls will still be served as part of the shutdown process,
            // so there is no problem if we're executing this as part of an "exit" RPC (which is the normal way of exiting)
            // Do not await, as this call to Exit may have been made as part of the exit RPC and we need to finish it.
            m_workerServer.ShutdownAsync().Forget();

            m_exitCompletionSource.TrySetResult(reportSuccess);
        }

        /// <summary>
        /// Retrieves the cache descriptor containing the build scheduler state from the coordinator node
        /// </summary>
        /// <returns>true if the operation was successful</returns>
        internal bool TryGetBuildScheduleDescriptor(out PipGraphCacheDescriptor descriptor)
        {
            descriptor = null;
            if (!AttachCompletion.Result)
            {
                return false;
            }

            descriptor = BuildStartData.CachedGraphDescriptor.ToCacheGrpc();
            return true;
        }


        async Task IWorkerService.SayHelloAsync(IDistributionServiceLocation orchestratorLocation)
        {
            m_orchestratorInitialized = true;
            m_orchestratorClient.Initialize(orchestratorLocation.IpAddress, orchestratorLocation.BuildServicePort, OnConnectionFailureAsync);

            var helloResult = await m_orchestratorClient.SayHelloAsync(new ServiceLocation() { IpAddress = m_config.Distribution.MachineHostName, Port = m_port });
            if (!helloResult.Succeeded)
            {
                // If we can't say hello there is no hope for attachment
                Exit(failure: $"SayHello call failed. Details: {helloResult.Failure.Describe()}", isUnexpected: true);
            }

            switch (helloResult.Result)
            {
                case HelloResponseType.Ok:
					// Nothing to do, the orchestrator will attach later.
					break;

				// The orchestrator sends a refusal, either when the worker has been early released
				// before it had a chance to say Hello, or that there are no slots available for this worker.
				// We should exit gracefully in these situations - this is basically the orchestrator refusing
				// attachment, so we treat it the same as an "Exit" call, and we include the reason.
				case HelloResponseType.Released:
                    exit("Worker was early released before initiating attachment");
                    break;
    
				case HelloResponseType.NoSlots:
                    exit("Orchestrator had no slots to allocate to this dynamic worker");
					break;
            }

            void exit(string reason)
            {
                var thisService = (IWorkerService)this;
                thisService.ExitRequested(reason, Optional<string>.Empty);
            }

        }

        /// <inheritdoc />
        void IWorkerService.Attach(BuildStartData buildStartData, string orchestratorName)
        {
            Logger.Log.DistributionAttachReceived(m_appLoggingContext, buildStartData.SessionId, orchestratorName);
            BuildStartData = buildStartData;

            Guid sessionIdFromAttach = Guid.Parse(buildStartData.SessionId);

            // The app-level logging context has a wrong session id. Fix it now that we know the right one.
            m_appLoggingContext = new LoggingContext(
                m_appLoggingContext.ActivityId,
                m_appLoggingContext.LoggerComponentInfo,
                new LoggingContext.SessionInfo(sessionIdFromAttach, m_appLoggingContext.Session.Environment, m_appLoggingContext.Session.RelatedActivityId),
                m_appLoggingContext);

            if (!m_orchestratorInitialized) // The orchestrator client is already initialized if the worker know its location at the start of the build
            {
                m_orchestratorClient.Initialize(buildStartData.OrchestratorLocation.IpAddress, buildStartData.OrchestratorLocation.Port, OnConnectionFailureAsync);
            }

            WorkerId = BuildStartData.WorkerId;
            m_attachCompletionSource.TrySetResult(true);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("AsyncUsage", "AsyncFixer02:MissingAsyncOpportunity")]
        private async Task<bool> CompleteAttachmentAfterProcessBuildRequestStartedAsync()
        {
            var attachCompletionInfo = m_pipExecutionService.ConstructAttachCompletionInfo();
            var attachCompletionResult = await m_orchestratorClient.AttachCompletedAsync(attachCompletionInfo);

            if (!attachCompletionResult.Succeeded)
            {
                var callDurationMin = (int)attachCompletionResult.Duration.TotalMinutes;

                if (m_isOrchestratorExited)
                {
                    // We failed to attach after receiving an exit call from the orchestrator.
                    // Don't treat this as a failure: it is a part of the normal early-release process,
                    // where the orchestrator shuts down the connection after releasing this worker.
                    // Log the ocurrence and exit gracefully.
                    Logger.Log.AttachmentFailureAfterOrchestratorExit(m_appLoggingContext, callDurationMin);

                    Exit();
                    return true;
                }
                else
                {
                    Exit($"Failed to attach to orchestrator. Duration: {callDurationMin}", isUnexpected: true);
                    return false;
                }
            }

            return true;
        }

        private async void OnConnectionFailureAsync(object sender, ConnectionFailureEventArgs e)
        {
            if (Interlocked.Increment(ref m_connectionClosedFlag) != 1)
            {
                // Only go through the failure logic once
                return;
            }

            e?.Log(m_appLoggingContext, machineName: "orchestrator");

            // Stop sending messages
            m_notificationManager.Cancel();
           
            // Unblock caller to make it a fire&forget event handler.
            await Task.Yield();
            Logger.Log.DistributionInactiveOrchestrator(m_appLoggingContext, (int)(GrpcSettings.CallTimeout.TotalMinutes * GrpcSettings.MaxAttempts));
            ExitAsync("Connection failure", isUnexpected: true).Forget();
        }

        /// <inheritdoc />
        async Task IWorkerService.ExecutePipsAsync(PipBuildRequest request)
        {
            Possible<Unit> reportInputsResult;

            using (Counters.StartStopwatch(DistributionCounter.ReportInputsDuration))
            {
                reportInputsResult = m_pipExecutionService.TryReportInputs(request.Hashes);
            }

            // We want to unblock the caller to process this asynchronously except on builds where we might get
            // fire-and-forget, where we need to make sure to add them to the 'pending' collections before unblocking
            // an orchestrator that might quicky call exit on this worker before we finish that processing.
            // This race, though presumably unlikely, would be silent and hard to diagnose, so we'll pay the extra cost.
            if (!((m_config.Distribution.ReplicateOutputsToWorkers ?? false) && (m_config.Distribution.FireForgetMaterializeOutput ?? false)))
            {
                await Task.Yield();
            }
            
            using (Counters.StartStopwatch(DistributionCounter.StartPipStepDuration))
            {
                for (int i = 0; i < request.Pips.Count; i++)
                {
                    SinglePipBuildRequest pipBuildRequest = request.Pips[i];

                    // Start the pip. Handle the case of a retry - the pip may be already started by a previous call.
                    if (m_handledBuildRequests.Add(pipBuildRequest.SequenceNumber))
                    {
                        var pipId = new PipId(pipBuildRequest.PipIdValue);
                        var pipIdStepTuple = (pipId, (PipExecutionStep)pipBuildRequest.Step);

                        PendingScheduleRequests[pipIdStepTuple] = true;
                        m_pendingBuildRequests[pipIdStepTuple] = pipBuildRequest;
                        
                        var pipCompletionData = new ExtendedPipCompletionData(new PipCompletionData() { PipIdValue = pipId.Value, Step = pipBuildRequest.Step });
                        m_pendingPipCompletions[pipIdStepTuple] = pipCompletionData;

                        m_pipExecutionService.StartPipStepAsync(pipId, pipCompletionData, pipBuildRequest, reportInputsResult).Forget();
                    }
                }
            }
        }

        internal void ReportResult(
            PipId pipId,
            ExecutionResult executionResult,
            PipExecutionStep step)
        {
            if (m_pendingPipCompletions.TryRemove((pipId, step), out var pipCompletion))
            {
                if (executionResult.Result == PipResultStatus.Failed)
                {
                    m_hasFailures = true;
                }

                if (step == PipExecutionStep.MaterializeOutputs && m_config.Distribution.FireForgetMaterializeOutput())
                {
                    // We do not report 'MaterializeOutput' step results back to orchestrator.
                    return;
                }

                pipCompletion.ExecutionResult = executionResult;
                m_notificationManager.ReportResult(pipCompletion);
            }
        }

        /// <inheritdoc/>
        public override void Dispose()
        {
            LogStatistics(m_appLoggingContext);
            m_workerServer.Dispose();
        }

        /// <nodoc/>
        public override async Task ExitAsync(Optional<string> failure, bool isUnexpected)
        {
            await Task.Yield();
            Exit(failure, isUnexpected);
        }

        /// <inheritdoc />
        public override bool Initialize()
        {
            // Start listening to the port
            try
            {
                m_workerServer.Start(m_port);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log.DistributionServiceInitializationError(m_appLoggingContext, DistributedBuildRole.Worker.ToString(), m_port, ex.ToStringDemystified());
                return false;
            }
        }
    }
}
