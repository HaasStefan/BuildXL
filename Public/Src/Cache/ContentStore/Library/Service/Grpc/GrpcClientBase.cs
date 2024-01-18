// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.ContractsLight;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Grpc;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Sessions;
using BuildXL.Cache.ContentStore.Timers;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Utilities.Core.Tracing;
using ContentStore.Grpc;
using Grpc.Core;

#nullable enable annotations

namespace BuildXL.Cache.ContentStore.Service.Grpc
{
    /// <summary>
    /// Base implementation of service client based on GRPC.
    /// </summary>
    public abstract class GrpcClientBase : StartupShutdownSlimBase
    {
        private const string HeartbeatName = "Heartbeat";

        private Capabilities _clientCapabilities;
        private Capabilities _serviceCapabilities;
        private IntervalTimer? _heartbeatTimer;

        private bool _serviceUnavailable;

        /// <nodoc />
        protected readonly ChannelBase Channel;

        /// <nodoc />
        protected readonly IAbsFileSystem FileSystem;

        /// <nodoc />
        protected readonly ServiceClientContentSessionTracer ServiceClientTracer;

        /// <nodoc />
        protected readonly ServiceClientRpcConfiguration Configuration;

        /// <nodoc />
        protected readonly string? Scenario;

        /// <nodoc />
        protected SessionState? SessionState;

        /// <inheritdoc />
        protected override Tracer Tracer => ServiceClientTracer;

        /// <nodoc />
        protected GrpcClientBase(
            OperationContext context,
            IAbsFileSystem fileSystem,
            ServiceClientContentSessionTracer tracer,
            ServiceClientRpcConfiguration configuration,
            string? scenario,
            Capabilities clientCapabilities)
        {
            FileSystem = fileSystem;
            ServiceClientTracer = tracer;
            Configuration = configuration;
            Scenario = scenario;
            _clientCapabilities = clientCapabilities;

            if (!configuration.UseGrpcDotNetClient)
            {
                GrpcEnvironment.WaitUntilInitialized();
            }

            var port = configuration.EncryptionEnabled ? configuration.EncryptedGrpcPort : configuration.GrpcPort;
            var channelCreationOptions = new ChannelCreationOptions(
                UseGrpcDotNet: configuration.UseGrpcDotNetClient,
                configuration.GrpcHost,
                port,
                GrpcEnvironment.GetClientOptions(configuration.GrpcCoreClientOptions),
                configuration.GrpcDotNetClientOptions ?? GrpcDotNetClientOptions.Default);
            Channel = GrpcChannelFactory.CreateChannel(context, channelCreationOptions, channelType: GetType().Name);
        }

        /// <nodoc />
        public void Dispose()
        {
            _heartbeatTimer?.Dispose();
            SessionState?.Dispose();
        }

        /// <nodoc />
        protected abstract AsyncUnaryCall<ShutdownResponse> ShutdownSessionAsync(ShutdownRequest shutdownRequest);

        /// <nodoc />
        protected abstract AsyncUnaryCall<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest heartbeatRequest, CancellationToken token);

        /// <nodoc />
        protected abstract AsyncUnaryCall<HelloResponse> HelloAsync(HelloRequest helloRequest, CancellationToken token);

        /// <nodoc />
        protected abstract AsyncUnaryCall<GetStatsResponse> GetStatsAsync(GetStatsRequest getStatsRequest);

        /// <nodoc />
        protected abstract AsyncUnaryCall<CreateSessionResponse> CreateSessionAsync(CreateSessionRequest createSessionRequest);

        /// <inheritdoc />
        protected override async Task<BoolResult> ShutdownCoreAsync(OperationContext context)
        {
            try
            {
                _heartbeatTimer?.Dispose();

                if (SessionState == null)
                {
                    // SessionState is null if initialization has failed.
                    return BoolResult.Success;
                }

                var sessionContext = await CreateSessionContextAsync(context, context.Token);
                if (!sessionContext)
                {
                    return new BoolResult(sessionContext);
                }

                Tracer.Info(context, $"Shutting down session. {sessionContext.Value.SessionId.AsTraceableSessionId()}");

                if (_serviceUnavailable)
                {
                    Tracer.Debug(context, $"Skipping session shutdown because service is unavailable. {sessionContext.Value.SessionId.AsTraceableSessionId()}");
                }
                else
                {
                    try
                    {
                        await ShutdownSessionAsync(new ShutdownRequest { Header = sessionContext.Value.CreateHeader() });
                    }
                    catch (RpcException e)
                    {
                        Tracer.Error(context, $"Failed to shut down session with error: {e}");
                    }
                }

                await Channel.DisconnectAsync();
                return BoolResult.Success;
            }
            catch (Exception ex)
            {
                // Catching all exceptions, even ClientCanRetryExceptions, because the teardown steps aren't idempotent.
                // In the worst case, the shutdown call comes while the service is offline, the service never receives it,
                // and then the service times out the session 10 minutes later (by default).
                return new BoolResult(ex);
            }
        }

        private async Task HeartbeatAsync(Context context, int originalSessionId)
        {
            var sessionContext = await CreateSessionContextAsync(context);
            if (!sessionContext)
            {
                // We do not even attempt to send a heartbeat if we can't get a session ID.
                Tracer.Warning(context, $"Skipping heartbeat. Can't find session context for {originalSessionId.AsTraceableSessionId()}.");
                return;
            }

            // It is very important for the heartbeat not to get stuck forever because if the service won't receive
            // any calls for some time, the session will be closed on the service side due to inactivity.

            // This operation passes a cancellation token to gracefully cancel the request,
            // and then uses hard timeout to abort the async operation if the operation is not gracefully canceled.
            var operationContext = new OperationContext(context);

            // Can't use original session id, because it may have changed due to reconnect.
            var sessionId = sessionContext.Value.SessionId;
            await operationContext.PerformOperationWithTimeoutAsync(
                    Tracer,
                    nestedContext => sendHeartbeatAsync(nestedContext, sessionContext.Value),
                    timeout: Configuration.HardHeartbeatTimeout,
                    extraStartMessage: sessionId.AsTraceableSessionId(),
                    extraEndMessage: r => sessionId.AsTraceableSessionId())
                .IgnoreFailure(); // The error was already traced.

            async Task<BoolResult> sendHeartbeatAsync(OperationContext context, SessionContext localSessionContext)
            {
                using var softTimeoutCancellationTokenSource = new CancellationTokenSource(Configuration.HeartbeatTimeout);
                using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(context.Token, softTimeoutCancellationTokenSource.Token);
                try
                {
                    HeartbeatResponse response = await HeartbeatAsync(new HeartbeatRequest { Header = localSessionContext.CreateHeader() }, cancellationTokenSource.Token);

                    // Check for null header here as a workaround to a known service bug, which returns null headers on successful heartbeats.
                    if (response?.Header != null && !response.Header.Succeeded)
                    {
                        // Heartbeat failed.
                        // Maybe the session is stale. Resetting it without throwing an exception.
                        await ResetOnUnknownSessionAsync(context, response.Header, sessionId, throwFailures: false);

                        var error = new BoolResult(response.Header.ErrorMessage, response.Header.Diagnostics);
                        return new BoolResult(error, "Heartbeat failed");
                    }

                    return BoolResult.Success;
                }
                catch (Exception ex)
                {
                    if (cancellationTokenSource.IsCancellationRequested)
                    {
                        return new BoolResult(ex, $"Heartbeat timed out out after '{Configuration.HeartbeatTimeout}'.");
                    }

                    string message = (ex is RpcException rpcEx) && (rpcEx.Status.StatusCode == StatusCode.Unavailable)
                        ? "Heartbeat failed to detect running service."
                        : $"Heartbeat failed: [{ex}]";
                    return new BoolResult(ex, message);
                }
            }
        }

        /// <nodoc />
        public virtual Task<BoolResult> CreateSessionAsync(
            OperationContext context,
            string name,
            string cacheName,
            ImplicitPin implicitPin)
        {
            return CreateSessionAsync(context, name, cacheName, implicitPin, serializedConfig: null, pat: null);
        }

        /// <nodoc />
        protected async Task<BoolResult> CreateSessionAsync(
            OperationContext context,
            string name,
            string cacheName,
            ImplicitPin implicitPin,
            string? serializedConfig,
            string? pat)
        {
            var startupResult = await StartupAsync(context, 5000);
            if (!startupResult)
            {
                return startupResult;
            }

            var operationContext = new OperationContext(context);
            Result<SessionData> dataResult = await CreateSessionDataAsync(operationContext, name, cacheName, implicitPin, serializedConfig, pat, isReconnect: false);
            if (dataResult.Succeeded)
            {
                SessionData data = dataResult.Value;
                SessionState = new SessionState(() => CreateSessionDataAsync(operationContext, name, cacheName, implicitPin, serializedConfig, pat, isReconnect: true), data);

                int sessionId = data.SessionId;

                // Send a heartbeat iff both the service can receive one and the service was told to expect one.
                if ((_serviceCapabilities & Capabilities.Heartbeat) != 0 &&
                    (_clientCapabilities & Capabilities.Heartbeat) != 0)
                {
                    _heartbeatTimer = new IntervalTimer(() => HeartbeatAsync(context, sessionId), Configuration.HeartbeatInterval, message =>
                    {
                        Tracer.Debug(context, $"[{HeartbeatName}] {message}. Original {sessionId.AsTraceableSessionId()}");
                    });
                }

                return BoolResult.Success;
            }
            else
            {
                return new BoolResult(dataResult);
            }
        }

        /// <nodoc />
        public override Task<BoolResult> StartupAsync(Context context) => StartupAsync(context, 5000);

        /// <nodoc />
        public async Task<BoolResult> StartupAsync(Context context, int waitMs)
        {
            try
            {
                var targetMachine = Configuration.GrpcHost;
                Tracer.Info(context, $"Starting up GRPC client against service on '{targetMachine}' on port {Configuration.GrpcPort} with timeout {waitMs}.");

                HelloResponse helloResponse;
                using (var ct = new CancellationTokenSource(waitMs > 0 ? waitMs : Timeout.Infinite))
                {
                    helloResponse = await SendGrpcRequestAsync(
                        context,
                        async () => await HelloAsync(new HelloRequest(), ct.Token));
                }

                if (helloResponse.Success)
                {
                    _serviceCapabilities = (Capabilities)helloResponse.Capabilities;
                    CheckCompatibility(_clientCapabilities, _serviceCapabilities).ThrowIfFailure();
                }
                else
                {
                    return new BoolResult("Failed to connect to service for unknown reason.");
                }
            }
            catch (Exception ex) when (!(ex is ClientCanRetryException))
            {
                // The caller's retry policy needs to see ClientCanRetryExceptions in order to properly retry
                return new BoolResult(ex);
            }

            return BoolResult.Success;
        }

        /// <nodoc />
        public static BoolResult CheckCompatibility(Capabilities clientCapabilities, Capabilities serviceCapabilities)
        {
            var requiredClientCapabilities = clientCapabilities & Capabilities.RequiredCapabilitiesMask;
            var serviceRequiredCapabilities = serviceCapabilities & Capabilities.RequiredCapabilitiesMask;

            if (requiredClientCapabilities > serviceRequiredCapabilities)
            {
                return new BoolResult($"Required client's capabilities ({requiredClientCapabilities}) don't match service's capabilities ({serviceRequiredCapabilities}).");
            }

            return BoolResult.Success;
        }

        private Task<Result<SessionData>> CreateSessionDataAsync(
            OperationContext context,
            string name,
            string cacheName,
            ImplicitPin implicitPin,
            string? serializedConfig,
            string? pat,
            bool isReconnect)
        {
            return context.PerformOperationAsync(
                Tracer,
                async () =>
                {
                    Tracer.Info(context, $"Creating session with serialized configuration: {serializedConfig ?? "null"}");

                    CreateSessionResponse response = await CreateSessionAsyncInternalAsync(context, name, cacheName, implicitPin, serializedConfig, pat);
                    if (string.IsNullOrEmpty(response.ErrorMessage))
                    {
                        SessionData data = new SessionData(response.SessionId, new DisposableDirectory(FileSystem, new AbsolutePath(response.TempDirectory)));
                        return new Result<SessionData>(data);
                    }
                    else
                    {
                        return new Result<SessionData>(response.ErrorMessage);
                    }
                },
                traceOperationStarted: true,
                extraStartMessage: $"Reconnect={isReconnect}",
                extraEndMessage: r => $"Reconnect={isReconnect}, {(r.Succeeded ? r.Value.SessionId.AsTraceableSessionId() : "SessionId=[Error]")}"
                );
        }

        private async Task<CreateSessionResponse> CreateSessionAsyncInternalAsync(
            Context context,
            string name,
            string cacheName,
            ImplicitPin implicitPin,
            string? serializedConfig,
            string? pat)
        {
            Func<Task<CreateSessionResponse>> func = async () =>
            {
                var request = new CreateSessionRequest
                {
                    CacheName = cacheName,
                    SessionName = name,
                    ImplicitPin = (int)implicitPin,
                    TraceId = context.TraceId,
                    Capabilities = (int)_clientCapabilities,
                    // Protobuff does not support setting null values.
                    SerializedConfig = serializedConfig ?? string.Empty,
                    Pat = pat ?? string.Empty
                };

                return await CreateSessionAsync(request);
            };

            CreateSessionResponse response = await SendGrpcRequestAsync(context, func);
            return response;
        }

        /// <summary>
        /// Gets the statistics from the remote.
        /// </summary>
        public async Task<GetStatsResult> GetStatsAsync(Context context)
        {
            var counters = new CounterSet();

            // Get stats iff compatible with service and client
            if ((_serviceCapabilities & Capabilities.GetStats) != 0 &&
                (_clientCapabilities & Capabilities.GetStats) != 0)
            {
                var response = await SendGrpcRequestAsync(context, async () => await GetStatsAsync(new GetStatsRequest()));
                if (response.Success)
                {
                    foreach (var entry in response.Statistics)
                    {
                        counters.Add(entry.Key, entry.Value);
                    }
                }
            }

            return new GetStatsResult(counters);
        }

        /// <nodoc />
        protected async Task<Result<SessionContext>> CreateSessionContextAsync(Context context, CancellationToken token = default)
        {
            Contract.Assert(SessionState != null, "CreateSessionAsync method was not called, or the instance is shut down.");

            Result<SessionData> result = await SessionState.GetDataAsync(new OperationContext(context, token));
            if (!result.Succeeded)
            {
                return Result.FromError<SessionContext>(result);
            }

            var startTime = DateTime.UtcNow;
            return new SessionContext(context, startTime, result.Value, token);
        }

        /// <summary>
        /// Performs an rpc operation.
        /// </summary>
        protected async Task<TResult> PerformOperationAsync<TResult, TResponse>(
            OperationContext context,
            Func<SessionContext, AsyncUnaryCall<TResponse>> func,
            Func<TResponse, TResult> responseHandler)
            where TResult : ResultBase
            where TResponse : IGrpcResponse
        {
            var sessionContext = await CreateSessionContextAsync(context, context.Token);
            if (!sessionContext)
            {
                return new ErrorResult(sessionContext).AsResult<TResult>();
            }

            return await PerformOperationAsync(sessionContext.Value, func, responseHandler);
        }

        /// <summary>
        /// Performs an rpc operation.
        /// </summary>
        protected Task<TResult> PerformOperationAsync<TResult, TResponse>(
            SessionContext context,
            Func<SessionContext, AsyncUnaryCall<TResponse>> func,
            Func<TResponse, TResult> responseHandler)
            where TResult : ResultBase
            where TResponse : IGrpcResponse
        {
            return SendGrpcRequestAsync(
                context.TracingContext,
                async () =>
                {
                    var response = await func(context);
                    await ProcessResponseHeaderAsync(context, response, throwFailures: false);
                    return responseHandler(response);
                });
        }

        /// <nodoc />
        protected async Task<TResult> PerformOperationAsync<TResult>(OperationContext context, Func<SessionContext, Task<TResult>> operation, Func<string>? startMessageFactory = null, [CallerMemberName] string? caller = null) where TResult : ResultBase
        {
            var stopwatch = StopwatchSlim.Start();
            TraceStartIfEnabled(context, caller!, startMessageFactory);

            try
            {
                var result = await performOperationCoreAsync();
                TraceStopIfEnabled(context, result, stopwatch.Elapsed, caller!);
                return result;
            }
            catch (Exception e)
            {
                // This can be only ClientCanRetryException.
                // Still tracing it explicitly.
                var error = new ErrorResult(e).AsResult<TResult>();
                TraceStopIfEnabled(context, error, stopwatch.Elapsed, caller!);
                throw;
            }

            async Task<TResult> performOperationCoreAsync()
            {
                var sessionContext = await CreateSessionContextAsync(context, context.Token);

                if (!sessionContext)
                {
                    return new ErrorResult(sessionContext).AsResult<TResult>();
                }

                try
                {
                    return await operation(sessionContext.Value);
                }
                catch (ResultPropagationException error)
                {
                    return new ErrorResult(error).AsResult<TResult>();
                }
                catch (Exception e) when (!(e is ClientCanRetryException))
                {
                    return new ErrorResult(e).AsResult<TResult>();
                }
            }
        }

        private void TraceStartIfEnabled(OperationContext context, string operation, Func<string>? startMessageFactory)
        {
            if (Configuration.TraceGrpcCalls)
            {
                Tracer.OperationStarted(context, operation, enabled: true, startMessageFactory?.Invoke());
            }
        }

        private void TraceStopIfEnabled(OperationContext context, ResultBase result, TimeSpan duration, string operationName)
        {
            if (Configuration.TraceGrpcCalls)
            {
                Tracer.OperationFinished(context, result, duration, operationName, traceErrorsOnly: false);
            }
        }

        /// <summary>
        /// Tracks a latency of a server's response and throws an exception if <paramref name="responseHeader"/>'s Success property returns false.
        /// </summary>
        private void TrackLatencyAndThrowIfFailure(DateTime startTime, ResponseHeader responseHeader, bool throwFailures)
        {
            Contract.Assert(responseHeader != null);

            var serverTicks = responseHeader.ServerReceiptTimeUtcTicks;
            if (serverTicks > 0 && serverTicks > startTime.Ticks)
            {
                // It make no sense to trace negative numbers.
                // It is possible that the time on different machines is different and the server time is less then the local one.
                long ticksWaited = serverTicks - startTime.Ticks;
                ServiceClientTracer.TrackClientWaitForServerTicks(ticksWaited);
            }

            if (!responseHeader.Succeeded && throwFailures)
            {
                var errorResult = new ErrorResult(responseHeader.ErrorMessage, responseHeader.Diagnostics);
                // This method will throw ResultPropagationException that is tracked by the PerformSessionOperationAsync
                errorResult.ThrowIfFailure();
            }
        }

        /// <nodoc />
        protected async Task<T> SendGrpcRequestAsync<T>(Context context, Func<Task<T>> func)
        {
            try
            {
                var result = await func();
                _serviceUnavailable = false;

                return result;
            }
            catch (RpcException ex)
            {
                if (ex.Status.StatusCode == StatusCode.Unavailable)
                {
                    // If the service is unavailable we can save time by not shutting down the service gracefully.
                    // TODO ST: I think it should be easier to track successful state, not an error state!
                    _serviceUnavailable = true;
                    throw new ClientCanRetryException(context, $"{nameof(GrpcContentClient)} failed to detect running service while running client action");
                }

                throw new ClientCanRetryException(context, ex.ToString(), ex);
            }
        }

        /// <nodoc />
        protected Task<T> SendGrpcRequestAndThrowIfFailedAsync<T>(SessionContext context, Func<Task<T>> func, bool throwFailures = true) where T : IGrpcResponse
        {
            return SendGrpcRequestAsync(
                context.TracingContext,
                async () =>
                {
                    var result = await func();

                    await ProcessResponseHeaderAsync(context, result, throwFailures);

                    return result;
                });
        }

        /// <summary>
        /// Process response header from a given <paramref name="result"/> and throw an exception if the result is unsuccessful and <paramref name="throwFailures"/> is true.
        /// </summary>
        protected async Task ProcessResponseHeaderAsync<T>(SessionContext context, T result, bool throwFailures)
            where T : IGrpcResponse
        {
            if (result.Header != null)
            {
                long ticksWaited = result.Header.ServerReceiptTimeUtcTicks - context.StartTime.Ticks;
                ServiceClientTracer.TrackClientWaitForServerTicks(ticksWaited);

                TrackLatencyAndThrowIfFailure(context.StartTime, result.Header, throwFailures);

                await ResetOnUnknownSessionAsync(context.TracingContext, result.Header, context.SessionId);
            }
        }

        /// <summary>
        /// Test hook for overriding the client's capabilities
        /// </summary>
        internal void DisableCapabilities(Capabilities capabilities)
        {
            _clientCapabilities &= ~capabilities;
        }

        /// <nodoc />
        protected async Task ResetOnUnknownSessionAsync(Context context, ResponseHeader header, int sessionId, bool throwFailures = true)
        {
            if (IsUnknownSessionError(header))
            {
                Contract.Assert(SessionState != null, "CreateSessionAsync method was not called, or the instance is shut down.");

                Tracer.Warning(context, $"Could not find session by id. Resetting session state. {sessionId.AsTraceableSessionId()}");
                await SessionState.ResetAsync(new OperationContext(context), sessionId);
                if (throwFailures)
                {
                    throw new ClientCanRetryException(context, $"Could not find session by id {sessionId.AsTraceableSessionId()}");
                }
            }
        }

        private static bool IsUnknownSessionError(ResponseHeader header)
        {
            // At the moment, the error message is the only way to identify that the error was due to
            // the session ID being unknown to the server.
            return !header.Succeeded && header.ErrorMessage.Contains("Could not find session");
        }

        /// <summary>
        /// Context for handling session requests.
        /// </summary>
        public readonly struct SessionContext
        {
            /// <nodoc />
            public Context TracingContext { get; }

            /// <nodoc />
            public CancellationToken Token { get; }

            /// <summary>
            /// Time when the operation has started.
            /// </summary>
            public DateTime StartTime { get; }

            /// <nodoc />
            public int SessionId => SessionData.SessionId;

            /// <nodoc />
            public SessionData SessionData { get; }

            /// <nodoc />
            public SessionContext(Context tracingContext, DateTime startTime, SessionData sessionData, CancellationToken token)
            {
                TracingContext = tracingContext;
                Token = token;
                StartTime = startTime;
                SessionData = sessionData;
            }

            /// <nodoc />
            public RequestHeader CreateHeader(UrgencyHint urgencyHint = UrgencyHint.Nominal) => new RequestHeader(TracingContext.TraceId, SessionId)
            {
                UrgencyHint = (int)urgencyHint
            };
        }

    }
}
