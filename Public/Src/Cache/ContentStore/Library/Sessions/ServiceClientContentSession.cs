// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.FileSystem;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Service;
using BuildXL.Cache.ContentStore.Service.Grpc;
using BuildXL.Cache.ContentStore.Sessions.Internal;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Utilities.Core;
using AbsolutePath = BuildXL.Cache.ContentStore.Interfaces.FileSystem.AbsolutePath;

namespace BuildXL.Cache.ContentStore.Sessions
{
    public class ServiceClientContentSession : ContentSessionBase, IContentSession, ITrustedContentSession
    {
        /// <summary>
        ///     The filesystem backing the session.
        /// </summary>
        protected readonly IAbsFileSystem FileSystem;

        /// <summary>
        ///     Generator of temporary, seekable streams.
        /// </summary>
        protected readonly TempFileStreamFactory TempFileStreamFactory;

        /// <inheritdoc />
        protected override Tracer Tracer { get; } = new Tracer(nameof(ServiceClientContentSession));

        /// <summary>
        ///     Request to server retry policy.
        /// </summary>
        protected readonly IRetryPolicy RetryPolicy;

        /// <summary>
        ///     The client backing the session.
        /// </summary>
        protected readonly IRpcClient RpcClient;

        /// <nodoc />
        protected readonly ServiceClientContentStoreConfiguration Configuration;

        /// <nodoc />
        protected readonly ServiceClientContentSessionTracer SessionTracer;

        /// <nodoc />
        protected readonly ILogger Logger;

        /// <nodoc />
        protected readonly ImplicitPin ImplicitPin;

        /// <inheritdoc />
        protected override bool TraceOperationStarted => Configuration.TraceOperationStarted;

        /// <summary>
        ///     Initializes a new instance of the <see cref="ServiceClientContentSession"/> class.
        /// </summary>
        public ServiceClientContentSession(
            OperationContext context,
            string name,
            ImplicitPin implicitPin,
            ILogger logger,
            IAbsFileSystem fileSystem,
            ServiceClientContentSessionTracer sessionTracer,
            ServiceClientContentStoreConfiguration configuration,
            Func<IRpcClient>? rpcClientFactory = null)
            : base(name)
        {
            Contract.Requires(name != null);
            Contract.Requires(logger != null);
            Contract.Requires(fileSystem != null);

            ImplicitPin = implicitPin;
            SessionTracer = sessionTracer;
            Logger = logger;
            FileSystem = fileSystem;
            Configuration = configuration;
            TempFileStreamFactory = new TempFileStreamFactory(FileSystem);

            RpcClient = (rpcClientFactory ?? (() => GetRpcClient(context)))();
            RetryPolicy = configuration.RetryPolicy;
        }

        /// <nodoc />
        protected virtual IRpcClient GetRpcClient(OperationContext context)
        {
            var rpcConfiguration = Configuration.RpcConfiguration;

            return new GrpcContentClient(context, SessionTracer, FileSystem, rpcConfiguration, Configuration.Scenario);
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> StartupCoreAsync(OperationContext operationContext)
        {
            BoolResult result;

            try
            {
                result = await RetryPolicy.ExecuteAsync(
                    () => RpcClient.CreateSessionAsync(operationContext, Name, Configuration.CacheName, ImplicitPin),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = new BoolResult(ex);
            }

            if (!result)
            {
                await RetryPolicy.ExecuteAsync(() => RpcClient.ShutdownAsync(operationContext), CancellationToken.None).ThrowIfFailure();
            }

            return result;
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> ShutdownCoreAsync(OperationContext operationContext)
        {
            var result = await RetryPolicy.ExecuteAsync(() => RpcClient.ShutdownAsync(operationContext), CancellationToken.None);

            var counterSet = new CounterSet();
            counterSet.Merge(GetCounters(), $"{Tracer.Name}.");
            Tracer.TraceStatisticsAtShutdown(operationContext, counterSet, prefix: "ServiceClientContentSessionStats");

            return result;
        }

        /// <inheritdoc />
        protected override void DisposeCore()
        {
            base.DisposeCore();
            RpcClient.Dispose();
            TempFileStreamFactory.Dispose();
        }

        /// <inheritdoc />
        protected override Task<PinResult> PinCoreAsync(
            OperationContext operationContext,
            ContentHash contentHash,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            return PerformRetries(
                operationContext,
                () => RpcClient.PinAsync(operationContext, contentHash, urgencyHint),
                retryCounter: retryCounter);
        }

        /// <inheritdoc />
        protected override Task<OpenStreamResult> OpenStreamCoreAsync(
            OperationContext operationContext,
            ContentHash contentHash,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            return PerformRetries(
                operationContext,
                () => RpcClient.OpenStreamAsync(operationContext, contentHash, urgencyHint),
                retryCounter: retryCounter);
        }

        /// <inheritdoc />
        protected override Task<PlaceFileResult> PlaceFileCoreAsync(
            OperationContext operationContext,
            ContentHash contentHash,
            AbsolutePath path,
            FileAccessMode accessMode,
            FileReplacementMode replacementMode,
            FileRealizationMode realizationMode,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            if (replacementMode != FileReplacementMode.ReplaceExisting && FileSystem.FileExists(path))
            {
                if (replacementMode == FileReplacementMode.SkipIfExists)
                {
                    return Task.FromResult(PlaceFileResult.AlreadyExists);
                }
                else if (replacementMode == FileReplacementMode.FailIfExists)
                {
                    return Task.FromResult(
                        new PlaceFileResult(
                            PlaceFileResult.ResultCode.Error,
                            $"File exists at destination {path} with FailIfExists specified"));
                }
            }

            return PerformRetries(
                operationContext,
                () => RpcClient.PlaceFileAsync(operationContext, contentHash, path, accessMode, replacementMode, realizationMode, urgencyHint),
                retryCounter: retryCounter);
        }

        /// <inheritdoc />
        protected override async Task<IEnumerable<Task<Indexed<PinResult>>>> PinCoreAsync(
            OperationContext operationContext,
            IReadOnlyList<ContentHash> contentHashes,
            UrgencyHint urgencyHint,
            Counter retryCounter,
            Counter fileCounter)
        {
            var retry = 0;

            try
            {
                return await RetryPolicy.ExecuteAsync(PinBulkFunc, operationContext.Token);
            }
            catch (Exception ex)
            {
                Tracer.Warning(operationContext, $"PinBulk failed with exception {ex}");
                return contentHashes.Select((hash, index) => Task.FromResult(new PinResult(ex).WithIndex(index)));
            }

            async Task<IEnumerable<Task<Indexed<PinResult>>>> PinBulkFunc()
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    fileCounter.Add(contentHashes.Count);

                    if (retry > 0)
                    {
                        Tracer.Debug(operationContext, $"{Tracer.Name}.PinBulk retry #{retry}");
                        retryCounter.Increment();
                    }

                    return await RpcClient.PinAsync(operationContext, contentHashes, urgencyHint);
                }
                finally
                {
                    Tracer.Debug(
                        operationContext,
                        $"{Tracer.Name}.PinBulk({contentHashes.Count}) stop {sw.Elapsed.TotalMilliseconds}ms. Hashes: [{string.Join(",", contentHashes)}]");
                }
            }
        }

        /// <nodoc />
        protected Task<T> PerformRetries<T>(
            OperationContext operationContext,
            Func<Task<T>> action,
            Action<int>? onRetry = null,
            Counter? retryCounter = null,
            [CallerMemberName] string? operationName = null)
        {
            Contract.Requires(operationName != null);
            var retry = 0;

            return RetryPolicy.ExecuteAsync(Wrapper, operationContext.Token);

            Task<T> Wrapper()
            {
                if (retry > 0)
                {
                    // Normalize operation name
                    operationName = operationName.Replace("Async", "").Replace("Core", "");
                    Tracer.Debug(operationContext, $"{Tracer.Name}.{operationName} retry #{retry}");
                    Tracer.TrackMetric(operationContext, $"{operationName}Retry", 1);
                    retryCounter?.Increment();
                    onRetry?.Invoke(retry);
                }

                retry++;
                return action();
            }
        }

        /// <inheritdoc />
        protected override Task<PutResult> PutStreamCoreAsync(
            OperationContext operationContext,
            HashType hashType,
            Stream stream,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            return PutStreamCoreAsync(
                operationContext,
                stream,
                retryCounter,
                args => RpcClient.PutStreamAsync(operationContext, hashType, args.putStream, args.createDirectory, urgencyHint));
        }

        /// <inheritdoc />
        protected override Task<PutResult> PutStreamCoreAsync(
            OperationContext operationContext,
            ContentHash contentHash,
            Stream stream,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            return PutStreamCoreAsync(
                operationContext,
                stream,
                retryCounter,
                args => RpcClient.PutStreamAsync(operationContext, contentHash, args.putStream, args.createDirectory, urgencyHint));
        }

        private async Task<PutResult> PutStreamCoreAsync(
            OperationContext operationContext,
            Stream stream,
            Counter retryCounter,
            Func<(Stream putStream, bool createDirectory), Task<PutResult>> putStreamAsync)
        {
            // We need a seekable stream, that can give its length. If the input stream is seekable, we can use it directly.
            // Otherwise, we need to create a temp file for this purpose.
            var putStream = stream;
            Stream? disposableStream = null;
            if (!stream.CanSeek)
            {
                putStream = await TempFileStreamFactory.CreateAsync(operationContext, stream);
                disposableStream = putStream;
            }

            bool createDirectory = false;
            using (disposableStream)
            {
                return await PerformRetries(
                    operationContext,
                    () => putStreamAsync((putStream, createDirectory)),
                    onRetry: r => createDirectory = true,
                    retryCounter: retryCounter);
            }
        }

        /// <inheritdoc />
        protected override Task<PutResult> PutFileCoreAsync(
            OperationContext operationContext,
            HashType hashType,
            AbsolutePath path,
            FileRealizationMode realizationMode,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            return PerformRetries(
                operationContext,
                () => RpcClient.PutFileAsync(operationContext, hashType, path, realizationMode, urgencyHint),
                retryCounter: retryCounter);
        }

        /// <inheritdoc />
        protected override Task<PutResult> PutFileCoreAsync(
            OperationContext operationContext,
            ContentHash contentHash,
            AbsolutePath path,
            FileRealizationMode realizationMode,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            return PerformRetries(
                operationContext,
                () => RpcClient.PutFileAsync(operationContext, contentHash, path, realizationMode, urgencyHint),
                retryCounter: retryCounter);
        }



        Task<PutResult> ITrustedContentSession.PutTrustedFileAsync(Context context, ContentHashWithSize contentHashWithSize, AbsolutePath path, FileRealizationMode realizationMode, CancellationToken cts, UrgencyHint urgencyHint)
        {
            return PerformPutFileOperationAsync(
                context,
                contentHashWithSize.Hash,
                path,
                realizationMode,
                cts,
                operationContext => PerformRetries(
                    operationContext,
                    () => RpcClient.PutFileAsync(operationContext, contentHashWithSize.Hash, path, realizationMode, urgencyHint, trustedContentSize: contentHashWithSize.Size),
                    retryCounter: BaseCounters[ContentSessionBaseCounters.PutFileRetries]),
                trusted: true);
        }

        AbsolutePath? ITrustedContentSession.TryGetWorkingDirectory(AbsolutePath? pathHint)
        {
            return null;
        }
    }
}
