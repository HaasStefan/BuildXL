// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed;
using BuildXL.Cache.ContentStore.Exceptions;
using BuildXL.Cache.ContentStore.Grpc;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Utilities.Core.Tasks;
using BuildXL.Utilities.Core.Tracing;
using ContentStore.Grpc;
using Grpc.Core;

#nullable enable

namespace BuildXL.Cache.ContentStore.Service.Grpc
{
    /// <summary>
    /// An error that <see cref="GrpcCopyClient"/> throws in <see cref="StartupShutdownSlimBase.StartupAsync"/> if connection can not be established in allotted time.
    /// </summary>
    public sealed class GrpcConnectionTimeoutException : TimeoutException
    {
        /// <nodoc />
        public GrpcConnectionTimeoutException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// An implementation of a CAS copy helper client based on GRPC.
    /// TODO: Consolidate with GrpcClient to deduplicate code. (bug 1365340)
    /// </summary>
    public sealed class GrpcCopyClient : StartupShutdownSlimBase
    {
        private readonly IClock _clock;
        private readonly ChannelBase _channel;
        private readonly ContentServer.ContentServerClient _client;
        private readonly GrpcCopyClientConfiguration _configuration;

        private readonly BandwidthChecker _bandwidthChecker;

        private readonly ByteArrayPool _pool;

        /// <inheritdoc />
        protected override Tracer Tracer { get; } = new Tracer(nameof(GrpcCopyClient));

        internal GrpcCopyClientKey Key { get; }

        /// <inheritdoc />
        protected override string GetArgumentsMessage() => Key.ToString();

        /// <summary>
        /// Initializes a new instance of the <see cref="GrpcCopyClient" /> class.
        /// </summary>
        internal GrpcCopyClient(Context context, GrpcCopyClientKey key, GrpcCopyClientConfiguration configuration, IClock? clock = null, ByteArrayPool? sharedBufferPool = null)
        {
            Key = key;
            _configuration = configuration;
            _clock = clock ?? SystemClock.Instance;

            if (!configuration.UseGrpcDotNetVersion)
            {
                GrpcEnvironment.WaitUntilInitialized();
            }

            _channel = GrpcChannelFactory.CreateChannel(new OperationContext(context), ToChannelCreationOptions(key, configuration), channelType: nameof(GrpcCopyClient));
            _client = new ContentServer.ContentServerClient(_channel);

            _bandwidthChecker = new BandwidthChecker(_configuration.BandwidthCheckerConfiguration);
            _pool = sharedBufferPool ?? new ByteArrayPool(_configuration.ClientBufferSizeBytes);

        }

        private static ChannelCreationOptions ToChannelCreationOptions(GrpcCopyClientKey key, GrpcCopyClientConfiguration configuration)
        {
            var clientOptions = configuration.GrpcCoreClientOptions;

            ChannelEncryptionOptions? encryptionOptions = clientOptions?.EncryptionEnabled == true ? GrpcEncryptionUtils.GetChannelEncryptionOptions() : null;

            return new ChannelCreationOptions(
                configuration.UseGrpcDotNetVersion,
                key.Host,
                key.GrpcPort,
                GrpcCoreOptions: configuration.UseGrpcDotNetVersion ? null : GrpcEnvironment.GetClientOptions(configuration.GrpcCoreClientOptions),
                GrpcDotNetOptions: configuration.UseGrpcDotNetVersion ? (configuration.GrpcDotNetClientOptions ?? GrpcDotNetClientOptions.Default) : null)
            {
                EncryptionEnabled = clientOptions?.EncryptionEnabled == true,
                EncryptionOptions = encryptionOptions
            };
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> StartupCoreAsync(OperationContext context)
        {
            await _channel.ConnectAsync(Key.Host, Key.GrpcPort, _clock, _configuration.ConnectionTimeout);
            return BoolResult.Success;
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> ShutdownCoreAsync(OperationContext context)
        {
            // We had seen case, when the following call was blocked effectively forever.
            // Adding external timeout to force a failure instead of waiting forever.
            var shutdownTask = _channel.DisconnectAsync();

            if (_configuration.DisconnectionTimeout != Timeout.InfiniteTimeSpan)
            {
                shutdownTask = shutdownTask.WithTimeoutAsync(_configuration.DisconnectionTimeout);
            }

            await shutdownTask;

            return BoolResult.Success;
        }

        /// <summary>
        /// Copies content from the server to the given local path.
        /// </summary>
        public Task<CopyFileResult> CopyFileAsync(OperationContext context, ContentHash hash, Interfaces.FileSystem.AbsolutePath destinationPath, CopyOptions options)
        {
            Func<Stream> streamFactory = () => new FileStream(destinationPath.Path, FileMode.Create, FileAccess.Write, FileShare.None, _configuration.ClientBufferSizeBytes, FileOptions.SequentialScan | FileOptions.Asynchronous);

            return CopyToAsync(context, hash, streamFactory, options, closeStream: true);
        }

        /// <summary>
        /// Copies content from the server to the given stream.
        /// </summary>
        public Task<CopyFileResult> CopyToAsync(OperationContext context, ContentHash hash, Stream stream, CopyOptions options)
        {
            // If a stream is passed from the outside this operation should not be closing it.
            return CopyToAsync(context, hash, () => stream, options, closeStream: false);
        }

        /// <nodoc />
        public static CopyFileResult CreateResultFromException(Exception e)
        {
            if (e is GrpcConnectionTimeoutException)
            {
                return new CopyFileResult(CopyResultCode.ConnectionTimeoutError, e);
            }

            if (e is RpcException r)
            {
                if (r.StatusCode == StatusCode.Unavailable)
                {
                    return new CopyFileResult(CopyResultCode.ServerUnavailable, e);
                }
                else
                {
                    return new CopyFileResult(CopyResultCode.RpcError, e);

                }
            }

            return new CopyFileResult(CopyResultCode.Unknown, e);
        }

        /// <summary>
        /// Copies content from the server to the stream returned by the factory.
        /// </summary>
        private async Task<CopyFileResult> CopyToAsync(OperationContext context, ContentHash hash, Func<Stream> streamFactory, CopyOptions options, bool closeStream)
        {
            // Need to track shutdown to prevent invalid operation errors when the instance is used after it was shut down is called.
            using var operationContext = TrackShutdown(context);
            return await CopyToCoreAsync(operationContext, hash, options, streamFactory, closeStream);
        }

        private TimeSpan GetResponseHeadersTimeout(CopyOptions options)
        {
            var bandwidthConnectionTimeout = options.BandwidthConfiguration?.ConnectionTimeout;
            if (bandwidthConnectionTimeout != null)
            {
                return bandwidthConnectionTimeout.Value;
            }

            // Using different configuration if we're connecting on startup.
            return _configuration.TimeToFirstByteTimeout;
        }

        /// <summary>
        /// Copies content from the server to the stream returned by the factory.
        /// </summary>
        private async Task<CopyFileResult> CopyToCoreAsync(OperationContext context, ContentHash hash, CopyOptions options, Func<Stream> streamFactory, bool closeStream)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
            var token = cts.Token;
            bool exceptionThrown = false;
            TimeSpan? headerResponseTime = null;
            CopyFileResult? result = null;
            try
            {
                CopyFileRequest request = new CopyFileRequest()
                {
                    TraceId = context.TracingContext.TraceId.ToString(),
                    HashType = (int)hash.HashType,
                    ContentHash = hash.ToByteString(),
                    Offset = 0,
                    Compression = options.CompressionHint,
                    FailFastIfBusy = options.BandwidthConfiguration?.FailFastIfServerIsBusy ?? false,
                };

                using AsyncServerStreamingCall<CopyFileResponse> response = _client.CopyFile(request, options: GetDefaultGrpcOptions(token));
                Metadata headers;
                var stopwatch = StopwatchSlim.Start();
                try
                {
                    var firstByteTimeout = GetResponseHeadersTimeout(options);
                    headers = await response.ResponseHeadersAsync.WithTimeoutAsync(firstByteTimeout, token);
                    headerResponseTime = stopwatch.Elapsed;
                }
                catch (TimeoutException t)
                {
                    // Trying to cancel the back end operation as well.
                    cts.Cancel();
                    result = new CopyFileResult(CopyResultCode.TimeToFirstByteTimeoutError, t);
                    return result;
                }

                // If the remote machine couldn't be contacted, GRPC returns an empty
                // header collection. GRPC would throw an RpcException when we tried
                // to stream response, but by that time we would have created target
                // stream. To avoid that, exit early instead.
                if (headers.Count == 0)
                {
                    result = new CopyFileResult(
                        CopyResultCode.ServerUnavailable,
                        $"Failed to connect to copy server {Key.Host} at port {Key.GrpcPort}.");
                    return result;
                }

                // Parse header collection.
                string? exception = null;
                string? message = null;
                CopyCompression compression = CopyCompression.None;
                foreach (Metadata.Entry header in headers)
                {
                    switch (header.Key.ToLowerInvariant())
                    {
                        case "exception":
                            exception = header.Value;
                            break;
                        case "message":
                            message = header.Value;
                            break;
                        case "compression":
                            if (!Enum.TryParse(header.Value, out compression))
                            {
                                return new CopyFileResult(
                                    CopyResultCode.Unknown,
                                    $"Unable to parse the server's intended compression '{header.Value}'. Requested compression is '{request.Compression}'");
                            }

                            break;
                    }
                }

                // Process reported server-side errors.
                if (exception != null)
                {
                    Contract.Assert(message != null);
                    switch (exception)
                    {
                        case "ContentNotFound":
                            result = new CopyFileResult(CopyResultCode.FileNotFoundError, message);
                            return result;
                        default:
                            result = new CopyFileResult(CopyResultCode.UnknownServerError, message);
                            return result;
                    }
                }

                // We got headers back with no errors, so create the target stream.
                Stream targetStream;
                try
                {
                    targetStream = streamFactory();
                }
                catch (Exception targetException)
                {
                    result = new CopyFileResult(CopyResultCode.DestinationPathError, targetException);
                    return result;
                }

                result = await _bandwidthChecker.CheckBandwidthAtIntervalAsync(
                    context,
                    innerToken => copyToCoreImplementation(response, compression, targetStream, innerToken),
                    options,
                    getErrorResult: diagnostics => new CopyFileResult(CopyResultCode.CopyBandwidthTimeoutError, diagnostics));

                return result;
            }
            catch (RpcException r)
            {
                result = CreateResultFromException(r);
                return result;
            }
            catch (Exception)
            {
                exceptionThrown = true;
                throw;
            }
            finally
            {
                // Even though we don't expect exceptions in this method, we can't assume they won't happen.
                // So asserting that the result is not null only when the method completes successfully or with a known errors.
                Contract.Assert(exceptionThrown || result != null);
                if (result != null)
                {
                    result.HeaderResponseTime = headerResponseTime;
                }
            }

            async Task<CopyFileResult> copyToCoreImplementation(AsyncServerStreamingCall<CopyFileResponse> response, CopyCompression compression, Stream targetStream, CancellationToken token)
            {
                // Copy the content to the target stream.
                try
                {
                    BoolResult streamResult = compression switch
                    {
                        CopyCompression.None => await StreamContentAsync(response.ResponseStream, targetStream, options, token),
                        CopyCompression.Gzip => await StreamContentWithCompressionAsync(response.ResponseStream, targetStream, options, token),
                        _ => throw new NotSupportedException(
                            $"Server is compressing stream with algorithm '{compression}', which is not supported client-side")
                    };

                    if (!streamResult.Succeeded)
                    {
                        return new CopyFileResult(streamResult);
                    }
                }
                finally
                {
                    if (closeStream)
                    {
                        targetStream.Dispose();
                    }
                }

                return CopyFileResult.Success;
            }
        }

        private CallOptions GetDefaultGrpcOptions(CancellationToken token)
        {
            return GetDefaultGrpcOptions(headers: null, token);
        }

        private CallOptions GetDefaultGrpcOptions(Metadata? headers, CancellationToken token)
        {
            return new CallOptions(headers: GetHeaders(headers), deadline: _clock.UtcNow + _configuration.OperationDeadline, cancellationToken: token);
        }

        private Metadata? GetHeaders(Metadata? headers)
        {
            headers ??= new Metadata();
            headers.Add(GrpcConstants.MachineMetadataFieldName, Environment.MachineName);

            return headers;
        }

        /// <summary>
        /// Requests host to copy a file from another source machine.
        /// </summary>
        public async Task<BoolResult> RequestCopyFileAsync(OperationContext context, ContentHash hash)
        {
            try
            {
                var request = new RequestCopyFileRequest
                {
                    TraceId = context.TracingContext.TraceId.ToString(),
                    ContentHash = hash.ToByteString(),
                    HashType = (int)hash.HashType
                };

                var response = await _client.RequestCopyFileAsync(request, cancellationToken: context.Token);

                return response.Header.Succeeded
                    ? BoolResult.Success
                    : new BoolResult(response.Header.ErrorMessage);
            }
            catch (RpcException r)
            {
                return new BoolResult(r);
            }
        }

        /// <summary>
        /// Pushes content to another machine.
        /// </summary>
        public async Task<PushFileResult> PushFileAsync(OperationContext context, ContentHash hash, Stream stream, CopyOptions options)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
            var token = cts.Token;
            bool exceptionThrown = false;
            TimeSpan? headerResponseTime = null;
            PushFileResult? result = null;
            try
            {
                var startingPosition = stream.Position;

                var pushRequest = new PushRequest(hash, traceId: context.TracingContext.TraceId);
                var headers = pushRequest.GetMetadata();

                using var call = _client.PushFile(options: GetDefaultGrpcOptions(headers, token));
                var requestStream = call.RequestStream;
                Metadata responseHeaders;

                var stopwatch = StopwatchSlim.Start();
                try
                {
                    var timeout = GetResponseHeadersTimeout(options);
                    responseHeaders = await call.ResponseHeadersAsync.WithTimeoutAsync(timeout, token);
                    headerResponseTime = stopwatch.Elapsed;
                }
                catch (TimeoutException t)
                {
                    cts.Cancel();
                    result = new PushFileResult(CopyResultCode.TimeToFirstByteTimeoutError, t);
                    return result;
                }

                // If the remote machine couldn't be contacted, GRPC returns an empty
                // header collection. To avoid an exception, exit early instead.
                if (responseHeaders.Count == 0)
                {
                    result = PushFileResult.ServerUnavailable();
                    return result;
                }

                var pushResponse = PushResponse.FromMetadata(responseHeaders);
                if (!pushResponse.ShouldCopy)
                {
                    result = PushFileResult.Rejected(pushResponse.Rejection);
                    return result;
                }

                // If we get a response before we finish streaming, it must be that the server cancelled the operation.
                var responseStream = call.ResponseStream;
                var responseMoveNext = responseStream.MoveNext(token);

                var responseCompletedTask = responseMoveNext.ContinueWith(
                    t =>
                    {
                        // It is possible that the next operation in this method will fail
                        // causing stack unwinding that will dispose serverIsDoneSource.
                        //
                        // Then when responseMoveNext is done serverIsDoneSource is already disposed and
                        // serverIsDoneSource.Cancel will throw ObjectDisposedException.
                        // This exception is not observed because the stack could've been unwound before
                        // the result of this method is awaited.
                        IgnoreObjectDisposedException(() => cts.Cancel());
                    });

                result = await _bandwidthChecker.CheckBandwidthAtIntervalAsync(
                    context,
                    innerToken => pushFileImplementation(stream, options, startingPosition, requestStream, responseStream, responseMoveNext, responseCompletedTask, innerToken),
                    options,
                    getErrorResult: diagnostics => PushFileResult.BandwidthTimeout(diagnostics));
                return result;
            }
            catch (Exception)
            {
                exceptionThrown = true;
                throw;
            }
            finally
            {
                // Even though we don't expect exceptions in this method, we can't assume they won't happen.
                // So asserting that the result is not null only when the method completes successfully or with a known errors.
                Contract.Assert(exceptionThrown || result != null);
                if (result != null)
                {
                    result.HeaderResponseTime = headerResponseTime;
                }
            }

            async Task<PushFileResult> pushFileImplementation(Stream stream, CopyOptions options, long startingPosition, IClientStreamWriter<PushFileRequest> requestStream, IAsyncStreamReader<PushFileResponse> responseStream, Task<bool> responseMoveNext, Task responseCompletedTask, CancellationToken token)
            {
                try
                {
                    using (var primaryBufferHandle = _pool.Get())
                    using (var secondaryBufferHandle = _pool.Get())
                    {
                        await StreamContentAsync(stream, primaryBufferHandle.Value, secondaryBufferHandle.Value, requestStream, options, token);
                    }

                    token.ThrowIfCancellationRequested();

                    await requestStream.CompleteAsync();
                    await responseCompletedTask;
                }
                catch (Exception e) when (e is TaskCanceledException or RpcException or OperationCanceledException)
                {
                    // It is possible that the streaming will fail with one of these exceptions, but the response from the backend is already available.
                    if (await responseMoveNext)
                    {
                        // If the response is available we can return it back with extra information from the backend.
                        var currentResponse = responseStream.Current;
                        if (!currentResponse.Header.Succeeded)
                        {
                            return new PushFileResult(currentResponse.Header.ErrorMessage, currentResponse.Header.Diagnostics);
                        }
                    }

                    if (token.IsCancellationRequested)
                    {
                        // The operation was canceled (by the bandwidth checker or by some other reasons)
                        return new PushFileResult("The operation was canceled") { IsCancelled = true };
                    }

                    if (e is RpcException)
                    {
                        return PushFileResult.RpcError(e);
                    }

                    return new PushFileResult(e);
                }

                // Make sure that we only attempt to read response when it is available.
                var responseIsAvailable = await responseMoveNext;
                if (!responseIsAvailable)
                {
                    return new PushFileResult("Failed to get final response.");
                }

                var response = responseStream.Current;

                var size = stream.Position - startingPosition;

                return response.Header.Succeeded
                    ? PushFileResult.PushSucceeded(size)
                    : new PushFileResult(response.Header.ErrorMessage, response.Header.Diagnostics);
            }
        }

        private static void IgnoreObjectDisposedException(Action action)
        {
            try
            {
                action();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private Task<(long Chunks, long Bytes)> StreamContentAsync(Stream input, byte[] primaryBuffer, byte[] secondaryBuffer, IClientStreamWriter<PushFileRequest> requestStream, CopyOptions options, CancellationToken cancellationToken)
        {
            return GrpcExtensions.CopyStreamToChunksAsync(
                input,
                requestStream,
                (content, _) => new PushFileRequest() { Content = content },
                primaryBuffer,
                secondaryBuffer,
                progressReport: (totalBytesRead) => options?.UpdateTotalBytesCopied(totalBytesRead),
                cancellationToken);
        }

        private async Task<BoolResult> StreamContentAsync(IAsyncStreamReader<CopyFileResponse> input, Stream output, CopyOptions? options, CancellationToken cancellationToken)
        {
            try
            {
                await GrpcExtensions.CopyChunksToStreamAsync(
                    input,
                    output,
                    response =>
                    {
                        if (response.Header != null)
                        {
                            var error = new ErrorResult(response.Header.ErrorMessage ?? "Unknown error", response.Header.Diagnostics);
                            error.ThrowIfFailure();
                        }
                        return response.Content;
                    },
                    totalBytes => options?.UpdateTotalBytesCopied(totalBytes),
                    cancellationToken);

                return BoolResult.Success;
            }
            catch (ResultPropagationException e)
            {
                return new BoolResult(e.Result);
            }
        }

        private async Task<BoolResult> StreamContentWithCompressionAsync(IAsyncStreamReader<CopyFileResponse> input, Stream output, CopyOptions? options, CancellationToken cancellationToken)
        {
            try
            {
                long chunks = 0L;
                long bytes = 0L;
                TimeSpan copyDuration = new TimeSpan();

                using (var grpcStream = new BufferedReadStream(
                    async () =>
                    {
                        StopwatchSlim sw = StopwatchSlim.Start();
                        if (await input.MoveNext(cancellationToken))
                        {
                            if (input.Current.Header is { } header)
                            {
                                var error = new ErrorResult(header.ErrorMessage ?? "Unknown error", header.Diagnostics);
                                error.ThrowIfFailure();
                            }

                            chunks++;
                            bytes += input.Current.Content.Length;
                            copyDuration += sw.Elapsed;
                            options?.UpdateTotalBytesCopied(new CopyStatistics(bytes, copyDuration));

                            return input.Current.Content;
                        }
                        else
                        {
                            return null;
                        }
                    }))
                {
                    using (Stream decompressedStream = new GZipStream(grpcStream, CompressionMode.Decompress, true))
                    {
                        await decompressedStream.CopyToAsync(output, _configuration.ClientBufferSizeBytes, cancellationToken);
                    }
                }

                return BoolResult.Success;
            }
            catch (ResultPropagationException e)
            {
                return new BoolResult(e.Result);
            }
        }

        /// <nodoc />
        public void Dispose()
        {
            if (ShutdownStarted && !ShutdownCompleted)
            {
                throw new CacheException($"{nameof(GrpcCopyClient)} must be shutdown before disposing.");
            }
        }
    }
}
