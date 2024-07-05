// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using BuildXL.Engine.Distribution;
using BuildXL.Utilities.Configuration.Mutable;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;
using Xunit.Abstractions;
using GrpcPipBuildRequest = BuildXL.Distribution.Grpc.PipBuildRequest;
using BuildXL.Scheduler;
using BuildXL.Utilities.Instrumentation.Common;
using System.Threading;
using BuildXL.Utilities.Configuration;
using BuildXL.Scheduler.Distribution;
using System;

namespace Test.BuildXL.Distribution
{
    public class WorkerServiceTestsBase : DistributionTestsBase
    {
        internal WorkerServiceTestRun CreateTestRun(IConfiguration config = null)
        {
            return new WorkerServiceTestRun(LoggingContext, config ?? Configuration);
        }
    
        public WorkerServiceTestsBase(ITestOutputHelper output) : base(output)
        {
            // Reset default value between tests
            EngineEnvironmentSettings.WorkerAttachTimeout.Value = EngineEnvironmentSettings.DefaultWorkerAttachTimeout;
        }

        internal class WorkerServiceTestRun
        {
            private const string BuildId = "01010101-cafe-1995-beef-1ee7c0ffee42";
            private const string EngineVersion = "0.1.0-19951026.1";
            public IConfiguration Configuration;
            public WorkerService WorkerService;
            public WorkerPipExecutionServiceMock PipExecutionService;
            public WorkerServerMock WorkerServer;
            public OrchestratorClientMock OrchestratorClient;
            public WorkerNotificationManagerMock NotificationManager;
            private Task<bool> m_whenDoneTask;

            private int m_pipRequestNextSequenceNumber;
            private int m_receivedRequests;
            private int m_processedRequests;

            public GrpcPipBuildRequest CreateBuildRequest(params (uint, PipExecutionStep)[] pips)
            {
                var req = GrpcMockData.PipBuildRequest(m_pipRequestNextSequenceNumber, pips);
                m_pipRequestNextSequenceNumber += pips.Length;
                return req;
            }

            public WorkerServiceTestRun(LoggingContext loggingContext, IConfiguration config)
            {
                Configuration = config;
                PipExecutionService = new WorkerPipExecutionServiceMock(loggingContext);
                WorkerServer = new WorkerServerMock();
                OrchestratorClient = new OrchestratorClientMock();
                NotificationManager = new WorkerNotificationManagerMock();
                WorkerService = WorkerService.CreateForTesting(loggingContext, Configuration, new(BuildId, "Test", EngineVersion), PipExecutionService, WorkerServer, NotificationManager, OrchestratorClient);
                WorkerServer.WorkerService = WorkerService;
                PipExecutionService.WorkerService = WorkerService;
            }

            public void AttachOrchestrator()
            {
                WorkerServer.Attach(GrpcMockData.BuildStartData);
                WaitForOrchestratorAttach(true);
            }

            /// <summary>
            /// Simulates the initialization of the worker from the engine:
            /// wait for a successful attachment and then start the service.
            /// </summary>
            public async Task StartServiceAsync()
            {
                // Engine initializes the service (see Engine.DoRun)
                WorkerService.Initialize();

                // Orchestrator attaches to worker
                var attached = await WorkerService.AttachCallTask;
                Assert.Equal(WorkerService.AttachResult.Attached, attached);

                // EngineSchedule starts the service (EngineSchedule.ExecuteScheduledPips)
                // passing null as the EngineSchedule is safe for tests
                WorkerService.Start(null, new ExecutionResultSerializer(0, null));
                Assert.True(PipExecutionService.Started);

                // Awaited by the engine as last step in the Execution phase
                m_whenDoneTask = WorkerService.WhenDoneAsync();
            }

            public void ReceiveExitCallFromOrchestator(bool isFailed = false)
            {
                var endData = GrpcMockData.GetBuildEndData(isFailed);
                WorkerServer.Exit(endData);
            }

            public async Task EndRunAsync(bool expectSuccess = true)
            {
                var success = await m_whenDoneTask;
                Assert.Equal(expectSuccess, success);
            }

            public void WaitForOrchestratorAttach(bool expectSuccess = true)
            {
                var attached = WorkerService.WaitForOrchestratorAttach();
                Assert.Equal(expectSuccess ? WorkerService.AttachResult.Attached : WorkerService.AttachResult.Failed, attached);
            }

            public void ReceiveBuildRequest(params (uint, PipExecutionStep)[] pips) => ReceiveBuildRequest(CreateBuildRequest(pips));

            public void ReceiveBuildRequest(GrpcPipBuildRequest buildRequest)
            {
                var count = buildRequest.Pips.Count;
                Interlocked.Add(ref m_receivedRequests, count);
                WorkerServer.ExecutePips(buildRequest);
                Interlocked.Add(ref m_processedRequests, count);
            }

            public void WaitForRequestsToBeProcessed(int expectedCount, TimeSpan? timeout = null)
            {
                SpinWait.SpinUntil(() => m_receivedRequests == m_processedRequests);
                PipExecutionService.WaitForPendingRequests(expectedCount, timeout);
            }
        }
    }
}