// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using BuildXL.Distribution.Grpc;
using Grpc.Core;
using BuildXL.Utilities;
using BuildXL.Utilities.Core.Tasks;
using System;

namespace BuildXL.Engine.Distribution.Grpc
{
    /// <summary>
    /// Worker service impl
    /// </summary>
    public sealed class GrpcWorker : Worker.WorkerBase
    {
        private readonly IWorkerService m_workerService;

        internal GrpcWorker(IWorkerService service)
        {
            m_workerService = service;
        }

        /// Note: The logic of service methods should be replicated in Test.BuildXL.Distribution.WorkerServerMock
        /// <inheritdoc/>
        public override Task<RpcResponse> Attach(BuildStartData message, ServerCallContext context)
        {
            var callInformation = GrpcCallInformation.Extract(context);
            m_workerService.Attach(message, callInformation.Sender);

            return GrpcUtils.EmptyResponseTask;
        }

        /// <inheritdoc/>
        public override Task<RpcResponse> ExecutePips(PipBuildRequest message, ServerCallContext context)
        {
            m_workerService.ExecutePipsAsync(message).Forget();
            return GrpcUtils.EmptyResponseTask;
        }

        /// <inheritdoc/>
        public override Task<RpcResponse> Heartbeat(RpcResponse message, ServerCallContext context)
        {
            return GrpcUtils.EmptyResponseTask;
        }

        /// <inheritdoc/>
#pragma warning disable 1998 // Disable the warning for "This async method lacks 'await'"
        public override async Task<RpcResponse> StreamExecutePips(IAsyncStreamReader<PipBuildRequest> requestStream, ServerCallContext context)
        {
#if NETCOREAPP
            await foreach (var message in requestStream.ReadAllAsync())
            {
                m_workerService.ExecutePipsAsync(message).Forget();
            }

            return GrpcUtils.EmptyResponse;
#else
            throw new NotImplementedException();
#endif
        }
#pragma warning restore 1998

        /// <inheritdoc/>
        public override Task<WorkerExitResponse> Exit(BuildEndData message, ServerCallContext context)
        {
            var failure = string.IsNullOrEmpty(message.Failure) ? Optional<string>.Empty : message.Failure;

            var eventStats = m_workerService.RetrieveWorkerEventStats();
            string exitMsg = "Received exit call from the orchestrator";
            if (!string.IsNullOrEmpty(message.Failure))
            {
                exitMsg += $": {message.Failure}";
            }
            m_workerService.ExitRequested(exitMsg, failure);       
            WorkerExitResponse workerExitResponse = new WorkerExitResponse();
            workerExitResponse.EventCounts.AddRange(eventStats);

            return Task.FromResult(workerExitResponse);
        }
    }
}