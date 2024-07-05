// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Grpc;
using BuildXL.Engine.Distribution.Grpc;
using BuildXL.Engine.Tracing;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Tracing;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace BuildXL.Engine.Distribution
{
    internal class ServerInterceptor : Interceptor
    {
        private readonly LoggingContext m_loggingContext;
        private readonly DistributedInvocationId m_invocationId;
        private readonly string m_token;
        private readonly bool m_authenticationEnabled;

        public ServerInterceptor(LoggingContext loggingContext, DistributedInvocationId invocationId)
        {
            m_loggingContext = loggingContext;
            m_invocationId = invocationId;
            m_authenticationEnabled = GrpcSettings.AuthenticationEnabled;
            if (m_authenticationEnabled)
            {
                m_token = GrpcEncryptionUtils.TryGetAuthorizationToken(EngineEnvironmentSettings.CBBuildIdentityTokenPath);
            }
        }

        private void InterceptCallContext(ServerCallContext context, out string sender)
        {
            string method = context.Method.Split('/')[2];

            GrpcSettings.ParseHeader(context.RequestHeaders, out sender, out var senderInvocationId, out string traceId, out string token);

            if (m_invocationId != senderInvocationId)
            {
                string failureMessage = $"The receiver and sender distributed invocation ids do not match. Receiver invocation id: {m_invocationId}. Sender invocation id: {senderInvocationId}.";
                Logger.Log.GrpcServerTrace(m_loggingContext, failureMessage);

                var trailers = new Metadata
                {
                    { GrpcMetadata.InvocationIdMismatch, GrpcMetadata.True },
                    { GrpcMetadata.IsUnrecoverableError, GrpcMetadata.True }
                };

                throw new RpcException(
                    new Status(
                        StatusCode.InvalidArgument,
                        failureMessage),
                    trailers);
            }

            if (m_authenticationEnabled && token != m_token)
            {
                Logger.Log.GrpcTrace(m_loggingContext, sender, $"Authentication tokens do not match:\r\nReceived:{token}\r\nExpected:{m_token}");

                var trailers = new Metadata
                {
                    { GrpcMetadata.IsUnrecoverableError, GrpcMetadata.True }
                };

                throw new RpcException(new Status(StatusCode.Unauthenticated, "Call could not be authenticated."), trailers);
            }

            Logger.Log.GrpcTrace(m_loggingContext, sender, $"Recv {traceId} {method}");
        }

        public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
        {
            string sender = "(Unknown)";
            try
            {
                InterceptCallContext(context, out sender);
                return continuation(request, context);
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception e)
            {
                var failureMessage = $"Unexpected exception processing unary call: {e.ToStringDemystified()}";
                Logger.Log.GrpcTrace(m_loggingContext, sender, failureMessage);

                var trailers = new Metadata
                {
                    { GrpcMetadata.IsUnrecoverableError, GrpcMetadata.True } // Bail
                };

                throw new RpcException(
                    new Status(
                        StatusCode.Unknown,
                        failureMessage),
                    trailers);
            }
        }

        public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
        {
            InterceptCallContext(context, out _);
            return continuation(requestStream, context);
        }

        public override Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
        {
            InterceptCallContext(context, out _);
            return continuation(request, responseStream, context);
        }

        public override Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        {
            InterceptCallContext(context, out _);
            return continuation(requestStream, responseStream, context);
        }
    }
}