﻿// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.FileSystem;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Interfaces.Utils;
using BuildXL.Cache.ContentStore.Sessions.Internal;
using BuildXL.Cache.ContentStore.Synchronization;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.MemoizationStore.Interfaces.Results;
using BuildXL.Utilities.Core.Tasks;

#nullable enable annotations

namespace BuildXL.Cache.MemoizationStore.Interfaces.Sessions
{
    /// <nodoc />
    [DataContract]
    public class TwoLevelCacheConfiguration
    {
        /// <nodoc />
        [DataMember]
        public bool RemoteCacheIsReadOnly { get; set; } = true;

        /// <nodoc />
        [DataMember]
        public bool AlwaysUpdateFromRemote { get; set; } = false;

        /// <nodoc />
        [DataMember]
        public bool BatchRemotePinsOnPut { get; set; } = false;

        /// <nodoc />
        [DataMember]
        public int RemotePinOnPutBatchMaxSize { get; set; } = 255;

        /// <nodoc />
        [DataMember]
        public TimeSpan RemotePinOnPutBatchInterval { get; set; } = TimeSpan.FromSeconds(1);


        /// <nodoc />
        [DataMember]
        public int RemotePinOnPutBatchConcurrencyLimit { get; set; } = 1;

        /// <summary>
        /// This is an experimental feature that should only be used in the context where the local cache
        /// represents a *temporary* L1, which means that any content that we've already put into the L1,
        /// the remote will already have, since we've already added it during the current session.
        /// </summary>
        [DataMember]
        public bool SkipRemotePutIfAlreadyExistsInLocal { get; set; } = false;

        /// <summary>
        /// If the remote fails fast and graciously when trying to put some content that already exists, it makes little
        /// sense to pin first. Instead, let the put operation run and return "ContentAlreadyExistsInCache=true"
        /// </summary>
        [DataMember]
        public bool SkipRemotePinOnPut { get; set; } = false;

        /// <nodoc />
        [DataMember]
        public TimeSpan RemotePutElisionDuration { get; set; } = TimeSpan.FromDays(1);

        /// <summary>
        /// Optionally, the temp directory to use when ingesting content.
        /// When not specified, content is stream directly from the remote cache to the local cache.
        ///   This mode can be advantageous when downloading from the remote cache occurs as a single stream.
        ///   (e.g. RPC within CloudBuild)
        /// When specified, content is placed to a temp file in this folder and then put into the local cache. 
        ///   This should be on the same volume as the local CAS to potentially use hard links or CoW instead of full copies.
        ///   This mode can be advantageous when downloading from the remote cache occurs in parallel streams. 
        ///   (e.g. downloading large files from VSTS L3)
        /// </summary>
        [DataMember]
        public string TempDir { get; set; } = null;
    }

    /// <summary>
    /// Wraps multiple underlying cache sessions under a single wrapper.
    /// </summary>
    /// TODO: Derive from ContentSessionBase
    public sealed class TwoLevelCacheSession : StartupShutdownSlimBase, ICacheSession
    {
        /// <inheritdoc />
        protected override Tracer Tracer { get; } = new Tracer(nameof(TwoLevelCacheSession));

        private readonly DisposableDirectory _tempFolder;
        private readonly ICacheSession _localCacheSession;
        private readonly ICacheSession _remoteCacheSession;
        private readonly TwoLevelCacheConfiguration _config;
        private readonly LockSet<ContentHash> _remoteFetchLock = new LockSet<ContentHash>();

        // It is important to use this cache if we want to skip the remote put. If we don't, there is a race condition where
        // another put for the same content might come in while we're still finishing up the remote put for the first put,
        // potentially resulting in us publishing an unbacked content hash list.
        private readonly VolatileSet<ContentHash> _putElisionCache;

        private ResultNagleQueue<(Context context, ContentHash hash), PinResult> _batchSinglePinNagleQueue;

        /// <summary>
        /// Initializes an instance of the <see cref="TwoLevelCacheSession"/> class
        /// </summary>
        /// <param name="name"></param>
        /// <param name="localCacheSession"></param>
        /// <param name="remoteCacheSession"></param>
        /// <param name="clock"></param>
        /// <param name="config"></param>
        public TwoLevelCacheSession(
            string name,
            ICacheSession localCacheSession,
            ICacheSession remoteCacheSession,
            IClock clock,
            TwoLevelCacheConfiguration config)
        {
            Name = name;
            _remoteCacheSession = remoteCacheSession;
            _localCacheSession = localCacheSession;
            _config = config;

            _putElisionCache = _config.SkipRemotePutIfAlreadyExistsInLocal
                ? new VolatileSet<ContentHash>(clock)
                : null;

            static AbsolutePath? getWorkingDir(ICacheSession session) => (session as ITrustedContentSession)?.TryGetWorkingDirectory(null);

            var tempDir = _config.TempDir != null ? new AbsolutePath(_config.TempDir) : null;
            tempDir ??= getWorkingDir(_localCacheSession) ?? getWorkingDir(_remoteCacheSession);

            if (tempDir != null)
            {
                _tempFolder = new DisposableDirectory(
                    PassThroughFileSystem.Default,
                    tempDir / $"{nameof(TwoLevelCacheSession)}Temp");
            }
        }

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        protected override async Task<BoolResult> StartupCoreAsync(OperationContext context)
        {
            if (_tempFolder != null)
            {
                Tracer.Debug(context, $"Initializing with temp dir '{_tempFolder.Path}'");
            }

            var (local, remote) = await MultiLevel(session => session.StartupAsync(context));

            var result = local & remote;
            if (!result.Succeeded)
            {
                // One of the initialization failed.
                // Need to shut down the stores that were properly initialized.
                if (local.Succeeded)
                {
                    await _localCacheSession.ShutdownAsync(context).TraceIfFailure(context);
                }

                if (remote.Succeeded)
                {
                    await _remoteCacheSession.ShutdownAsync(context).TraceIfFailure(context);
                }
            }
            else
            {
                _batchSinglePinNagleQueue = _config.BatchRemotePinsOnPut
                    ? ResultNagleQueue<(Context context, ContentHash hash), PinResult>.CreateAndStart(
                        execute: requests => ExecutePinBatch(context, requests),
                        maxDegreeOfParallelism: _config.RemotePinOnPutBatchConcurrencyLimit,
                        interval: _config.RemotePinOnPutBatchInterval,
                        batchSize: _config.RemotePinOnPutBatchMaxSize)
                    : null;
            }

            return result;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _remoteCacheSession.Dispose();
            _localCacheSession.Dispose();
            _tempFolder?.Dispose();
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> ShutdownCoreAsync(OperationContext context)
        {
            var (localResult, remoteResult) = await MultiLevel(store => store.ShutdownAsync(context));
            return localResult & remoteResult;
        }

        /// <inheritdoc />
        public IAsyncEnumerable<GetSelectorResult> GetSelectors(
            Context context,
            Fingerprint weakFingerprint,
            CancellationToken cts,
            UrgencyHint urgencyHint)
        {
            return _localCacheSession.GetSelectors(context, weakFingerprint, cts, urgencyHint)
                .Concat(_remoteCacheSession.GetSelectors(context, weakFingerprint, cts, urgencyHint));
        }

        /// <inheritdoc />
        public async Task<GetContentHashListResult> GetContentHashListAsync(
            Context context,
            StrongFingerprint strongFingerprint,
            CancellationToken cts,
            UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            return await OperationContext(context, cts).PerformOperationAsync(
                Tracer,
                () => InternalGetContentHashListAsync(context, strongFingerprint, cts, urgencyHint),
                traceErrorsOnly: true);
        }

        // Enforces use of Extended result via compiler.
        private async Task<ExtendedGetContentHashListResult> InternalGetContentHashListAsync(
            Context context,
            StrongFingerprint strongFingerprint,
            CancellationToken cts,
            UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            GetContentHashListResult remoteContentHashListResult = null;
            if (_config.AlwaysUpdateFromRemote)
            {
                remoteContentHashListResult = await lookupInRemoteAsync();
                if (remoteContentHashListResult.Succeeded && remoteContentHashListResult.ContentHashListWithDeterminism.ContentHashList != null)
                {
                    return ExtendedGetContentHashListResult.FromGetContentHashListResult(remoteContentHashListResult, ArtifactSource.BackingStore);
                }
            }

            // Looking into a local cache only when not forced getting the results from the remote.
            GetContentHashListResult localResult = await _localCacheSession.GetContentHashListAsync(context, strongFingerprint, cts, urgencyHint);
            if (localResult.Succeeded && localResult.ContentHashListWithDeterminism.ContentHashList != null)
            {
                return ExtendedGetContentHashListResult.FromGetContentHashListResult(localResult, ArtifactSource.LocalCache);
            }

            // Make sure we don't call the remote twice.
            remoteContentHashListResult ??= await lookupInRemoteAsync();
            if (!remoteContentHashListResult.Succeeded)
            {
                return ExtendedGetContentHashListResult.FromGetContentHashListResult(
                    remoteContentHashListResult,
                    ArtifactSource.Unknown);
            }

            // TODO: Need more info to determine L2 or L3
            return ExtendedGetContentHashListResult.FromGetContentHashListResult(remoteContentHashListResult, ArtifactSource.BackingStore);

            async Task<GetContentHashListResult> lookupInRemoteAsync()
            {
                var result = await _remoteCacheSession.GetContentHashListAsync(context, strongFingerprint, cts, urgencyHint);

                if (result.Succeeded && result.ContentHashListWithDeterminism.ContentHashList != null)
                {
                    // found in remote, add to local and content will be pinned by the implicit pin requirement.
                    _ = await _localCacheSession.AddOrGetContentHashListAsync(
                        context,
                        strongFingerprint,
                        result.ContentHashListWithDeterminism,
                        cts,
                        urgencyHint);
                }

                return result;
            }
        }

        /// <inheritdoc />
        public Task<PinResult> PinAsync(Context context, ContentHash contentHash, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            return OperationContext(context, cts).PerformOperationAsync(
                Tracer,
                async () =>
                {
                    PinResult pinResult = await _localCacheSession.PinAsync(context, contentHash, cts, urgencyHint);
                    if (_config.RemoteCacheIsReadOnly)
                    {
                        return pinResult;
                    }

                    return await _remoteCacheSession.PinAsync(context, contentHash, cts, urgencyHint);
                },
                extraEndMessage: r => $"Hash={contentHash.ToShortString()}",
                traceErrorsOnly: true);
        }

        /// <inheritdoc />
        public Task<OpenStreamResult> OpenStreamAsync(Context context, ContentHash contentHash, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            return OperationContext(context, cts).PerformOperationAsync(
                Tracer,
                async () =>
                {
                    var openLocalAsync = () => _localCacheSession.OpenStreamAsync(
                        context, contentHash, cts, urgencyHint);

                    OpenStreamResult localOpenResult = await openLocalAsync();

                    if (localOpenResult.Code == OpenStreamResult.ResultCode.ContentNotFound)
                    {
                        using var _ = await _remoteFetchLock.AcquireAsync(contentHash, cts);

                        localOpenResult = await openLocalAsync();

                        if (localOpenResult.Code == OpenStreamResult.ResultCode.ContentNotFound)
                        {
                            ResultBase ingestFromRemoteResult = await IngestFileFromRemoteAsync(context, contentHash, urgencyHint, cts);
                            if (!ingestFromRemoteResult.Succeeded)
                            {
                                return new OpenStreamResult(ingestFromRemoteResult);
                            }

                            localOpenResult = await openLocalAsync();
                        }
                    }

                    return localOpenResult;
                },
                extraEndMessage: r => $"Hash={contentHash.ToShortString()}",
                traceErrorsOnly: true);
        }

        /// <inheritdoc />
        public Task<PlaceFileResult> PlaceFileAsync(
            Context context,
            ContentHash contentHash,
            AbsolutePath path,
            FileAccessMode accessMode,
            FileReplacementMode replacementMode,
            FileRealizationMode realizationMode,
            CancellationToken cts,
            UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            return OperationContext(context, cts).PerformOperationAsync(
                Tracer,
                async () => (PlaceFileResult)await InternalPlaceFileAsync(
                    context,
                    contentHash,
                    path,
                    accessMode,
                    replacementMode,
                    realizationMode,
                    urgencyHint,
                    cts),
                extraEndMessage: r => $"Hash={contentHash.ToShortString()}, Path={path}",
                traceErrorsOnly: true);
        }

        // Enforces use of Extended result via compiler.
        private async Task<ExtendedPlaceFileResult> InternalPlaceFileAsync(
            Context context,
            ContentHash contentHash,
            AbsolutePath path,
            FileAccessMode accessMode,
            FileReplacementMode replacementMode,
            FileRealizationMode realizationMode,
            UrgencyHint urgencyHint,
            CancellationToken cts)
        {
            // TODO: We need more info to discern whether L2 or L3
            ArtifactSource source = ArtifactSource.LocalCache;

            var placeLocalAsync = () => _localCacheSession.PlaceFileAsync(
                context,
                contentHash,
                path,
                accessMode,
                replacementMode,
                realizationMode,
                cts);

            PlaceFileResult localPlaceResult = await placeLocalAsync();

            if (!localPlaceResult.IsPlaced() && localPlaceResult.Code != PlaceFileResult.ResultCode.NotPlacedAlreadyExists)
            {
                using var _ = await _remoteFetchLock.AcquireAsync(contentHash, cts);

                localPlaceResult = await placeLocalAsync();

                if (!localPlaceResult.IsPlaced() && localPlaceResult.Code != PlaceFileResult.ResultCode.NotPlacedAlreadyExists)
                {
                    // First download from datacenter/remote or backing store (L2 or L3) into L1.
                    ResultBase ingestFromRemoteResult = await IngestFileFromRemoteAsync(context, contentHash, urgencyHint, cts);
                    if (!ingestFromRemoteResult.Succeeded)
                    {
                        return new ExtendedPlaceFileResult(ingestFromRemoteResult);
                    }

                    localPlaceResult = await placeLocalAsync();

                    source = ArtifactSource.BackingStore;
                }
            }

            return ExtendedPlaceFileResult.FromPlaceFileResult(localPlaceResult, source);
        }

        private async Task<ResultBase> IngestFileFromRemoteAsync(Context context, ContentHash contentHash, UrgencyHint urgencyHint, CancellationToken cts)
        {
            // see TwoLevelCacheConfiguration.TempDir for context on why there are two paths here.
            if (_tempFolder is null)
            {
                OpenStreamResult openStreamResult = await _remoteCacheSession.OpenStreamAsync(context, contentHash, cts, urgencyHint);
                if (!openStreamResult.Succeeded)
                {
                    return openStreamResult;
                }

                PutResult localPutResult = await _localCacheSession.PutStreamAsync(context, contentHash, openStreamResult.Stream, cts, urgencyHint);
                if (!localPutResult)
                {
                    return localPutResult;
                }
            }
            else
            {
                AbsolutePath tempFilePath = _tempFolder.CreateRandomFileName();
                bool fileMoved = false;

                try
                {
                    var placeFromRemoteResult = await _remoteCacheSession.PlaceFileAsync(
                        context,
                        contentHash,
                        tempFilePath,
                        FileAccessMode.ReadOnly,
                        FileReplacementMode.FailIfExists,
                        FileRealizationMode.Any,
                        cts);

                    if (!placeFromRemoteResult.Succeeded)
                    {
                        return placeFromRemoteResult;
                    }

                    var putIntoLocalResult = await _localCacheSession.PutOrPutTrustedFileAsync(
                        context,
                        new ContentHashWithSize(contentHash, placeFromRemoteResult.FileSize),
                        tempFilePath,
                        FileRealizationMode.Move,
                        cts,
                        urgencyHint);

                    if (!putIntoLocalResult.Succeeded)
                    {
                        return putIntoLocalResult;
                    }

                    fileMoved = true;
                }
                finally
                {
                    if (!fileMoved && File.Exists(tempFilePath.Path))
                    {
                        File.Delete(tempFilePath.Path);
                    }
                }
            }
            
            _putElisionCache?.Add(contentHash, _config.RemotePutElisionDuration);

            return BoolResult.Success;
        }

        /// <inheritdoc />
        public Task<AddOrGetContentHashListResult> AddOrGetContentHashListAsync(
            Context context,
            StrongFingerprint strongFingerprint,
            ContentHashListWithDeterminism contentHashList,
            CancellationToken cts,
            UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            return OperationContext(context, cts).PerformOperationAsync(
                Tracer,
                async () =>
                {
                    if (_config.RemoteCacheIsReadOnly)
                    {
                        // If the remote cache is readonly, then just relying on the local cache.
                        return await _localCacheSession.AddOrGetContentHashListAsync(
                            context,
                            strongFingerprint,
                            contentHashList,
                            cts,
                            urgencyHint);
                    }

                    // Remote is not readonly.
                    // In this case, calling the remote first to avoid race condition.
                    // It is possible that two machines are calling 'AddOrGetContentHashList' for the same fingerprint
                    // at the same time and remote store will get the content hash list if the current caller
                    // loses the race.
                    // In this case, we need change the contentHashList based on the results from the remote.
                    AddOrGetContentHashListResult remoteResult = await _remoteCacheSession.AddOrGetContentHashListAsync(
                        context,
                        strongFingerprint,
                        contentHashList,
                        cts,
                        urgencyHint);

                    if (!remoteResult.Succeeded)
                    {
                        return remoteResult;
                    }

                    ContentHashListWithDeterminism hashListForLocalCache = contentHashList;

                    // Remote result is successful.
                    if (remoteResult.ContentHashListWithDeterminism.ContentHashList != null)
                    {
                        // The fingerprint was added by another client.
                        hashListForLocalCache = remoteResult.ContentHashListWithDeterminism;
                    }

                    AddOrGetContentHashListResult localResult = await _localCacheSession.AddOrGetContentHashListAsync(
                        context,
                        strongFingerprint,
                        hashListForLocalCache,
                        cts,
                        urgencyHint);

                    if (!localResult.Succeeded)
                    {
                        return localResult;
                    }

                    return remoteResult;
                },
                traceErrorsOnly: true);
        }

        /// <inheritdoc />
        public Task<BoolResult> IncorporateStrongFingerprintsAsync(
            Context context,
            IEnumerable<Task<StrongFingerprint>> strongFingerprints,
            CancellationToken cts,
            UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            return OperationContext(context, cts).PerformOperationAsync(
                Tracer,
                async () =>
                {
                    List<Task<StrongFingerprint>> strongFingerprintsList = strongFingerprints.ToList();
                    BoolResult result = await _localCacheSession.IncorporateStrongFingerprintsAsync(context, strongFingerprintsList, cts, urgencyHint);
                    if (!result || _config.RemoteCacheIsReadOnly)
                    {
                        // TODO: is IncorporateStrongFingerprint actually a write? Or it is ok to call it for the remote even when _remoteMetadataIsReadonly is true?
                        return result;
                    }

                    return await _remoteCacheSession.IncorporateStrongFingerprintsAsync(
                        context,
                        strongFingerprintsList,
                        cts,
                        urgencyHint);
                },
                traceErrorsOnly: true);
        }

        private Task<PutResult> PutCoreAsync(
            Context context,
            CancellationToken cancellationToken,
            Func<Task<PutResult>> localPut,
            Func<PutResult, Task<PutResult>> remotePut,
            UrgencyHint urgencyHint,
            Func<PutResult, string> extraEndMessage,
            [CallerMemberName] string caller = "")
        {
            return OperationContext(context, cancellationToken).PerformOperationAsync(
                Tracer,
                async () =>
                {
                    var localResult = await localPut();
                    if (!localResult || _config.RemoteCacheIsReadOnly ||
                        (localResult.ContentAlreadyExistsInCache && (_putElisionCache?.Contains(localResult.ContentHash) == true)))
                    {
                        return localResult;
                    }

                    if (!_config.SkipRemotePinOnPut)
                    {
                        var pinResult = _config.BatchRemotePinsOnPut
                            ? await _batchSinglePinNagleQueue.EnqueueAsync((context, localResult.ContentHash))
                            : await _remoteCacheSession.PinAsync(context, localResult.ContentHash, cancellationToken, urgencyHint);

                        if (pinResult.Code == PinResult.ResultCode.Success)
                        {
                            // content was found in the remote - simply return local result at this point.
                            return localResult;
                        }
                    }

                    var remoteResult = await remotePut(localResult);
                    if (remoteResult.Succeeded)
                    {
                        _putElisionCache?.Add(remoteResult.ContentHash, _config.RemotePutElisionDuration);
                    }

                    return remoteResult;
                },
                extraEndMessage: extraEndMessage,
                traceErrorsOnly: true,
                caller: caller);
        }

        private async Task<IReadOnlyList<PinResult>> ExecutePinBatch(Context nagleQueueContext, IReadOnlyList<(Context context, ContentHash hash)> requests)
        {
            var context = nagleQueueContext.CreateNested(Tracer.Name);

            var hashes = requests.Select(x => x.hash).ToList();
            var results = await _remoteCacheSession.PinAsync(nagleQueueContext, hashes, CancellationToken.None, UrgencyHint.Nominal);
            var awaitedResults = await TaskUtilities.SafeWhenAll(results);
            var orderedResults = awaitedResults.OrderBy(r => r.Index).Select(r => r.Item).ToArray();

            for (var i = 0; i < requests.Count; i++)
            {
                var result = orderedResults[i];
                Tracer.Debug(requests[i].context, $"PinAsync({requests[i].hash.ToShortString()}) was bulkified with result=[{result.Code}]." +
                    $" Bulk operation correlation ID: [{context.TraceId}]");
            }

            return orderedResults;
        }

        /// <inheritdoc />
        public Task<PutResult> PutFileAsync(
            Context context,
            HashType hashType,
            AbsolutePath path,
            FileRealizationMode realizationMode,
            CancellationToken cts,
            UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            return PutCoreAsync(
                context,
                cts,
                localPut: () => _localCacheSession.PutFileAsync(context, hashType, path, realizationMode, cts, urgencyHint),
                remotePut: _ => _remoteCacheSession.PutFileAsync(context, hashType, path, realizationMode, cts, urgencyHint),
                urgencyHint,
                extraEndMessage: r => $"HashType={hashType}, Path={path}");
        }

        /// <inheritdoc />
        public Task<PutResult> PutFileAsync(
            Context context,
            ContentHash contentHash,
            AbsolutePath path,
            FileRealizationMode realizationMode,
            CancellationToken cts,
            UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            return PutCoreAsync(
                context,
                cts,
                localPut: () => _localCacheSession.PutFileAsync(context, contentHash, path, realizationMode, cts, urgencyHint),
                remotePut: _ => _remoteCacheSession.PutFileAsync(context, contentHash, path, realizationMode, cts, urgencyHint),
                urgencyHint,
                extraEndMessage: r => $"Hash={contentHash.ToShortString()}, Path={path}");
        }

        /// <inheritdoc />
        public Task<PutResult> PutStreamAsync(
            Context context,
            HashType hashType,
            Stream stream,
            CancellationToken cts,
            UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            return PutCoreAsync(
                context,
                cts,
                localPut: () => _localCacheSession.PutStreamAsync(context, hashType, stream, cts, urgencyHint),
                remotePut: localPutResult => PropagateStreamToRemote(context, localPutResult, stream, cts, urgencyHint),
                urgencyHint,
                extraEndMessage: r => $"HashType={hashType}");
        }

        /// <inheritdoc />
        public Task<PutResult> PutStreamAsync(
            Context context,
            ContentHash contentHash,
            Stream stream,
            CancellationToken cts,
            UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            return PutCoreAsync(
                context,
                cts,
                localPut: () => _localCacheSession.PutStreamAsync(context, contentHash, stream, cts, urgencyHint),
                remotePut: localPutResult => PropagateStreamToRemote(context, localPutResult, stream, cts, urgencyHint),
                urgencyHint,
                extraEndMessage: r => $"Hash={contentHash.ToShortString()}");
        }

        /// <inheritdoc />
        public Task<IEnumerable<Task<Indexed<PinResult>>>> PinAsync(
            Context context,
            IReadOnlyList<ContentHash> contentHashes,
            CancellationToken cts,
            UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            if (_config.RemoteCacheIsReadOnly)
            {
                return _localCacheSession.PinAsync(context, contentHashes, cts, urgencyHint);
            }

            return Workflows.RunWithFallback(
                contentHashes,
                hashes => _localCacheSession.PinAsync(context, hashes, cts, urgencyHint),
                hashes => _remoteCacheSession.PinAsync(context, hashes, cts, urgencyHint),
                // Pin the remote only when the remote is used for content as well.
                result => result.Code == PinResult.ResultCode.Success);
        }

        /// <inheritdoc />
        public Task<IEnumerable<Task<Indexed<PinResult>>>> PinAsync(Context context, IReadOnlyList<ContentHash> contentHashes, PinOperationConfiguration config)
        {
            if (_config.RemoteCacheIsReadOnly)
            {
                return _localCacheSession.PinAsync(context, contentHashes, config);
            }

            return Workflows.RunWithFallback(
                contentHashes,
                hashes => _localCacheSession.PinAsync(context, hashes, config),
                hashes => _remoteCacheSession.PinAsync(context, hashes, config),
                // Pin the remote only when the remote is used for content as well.
                result => result.Code == PinResult.ResultCode.Success);
        }

        /// <inheritdoc />
        public Task<IEnumerable<Task<Indexed<PlaceFileResult>>>> PlaceFileAsync(Context context, IReadOnlyList<ContentHashWithPath> placeFileArgs, FileAccessMode accessMode, FileReplacementMode replacementMode, FileRealizationMode realizationMode, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            throw new NotImplementedException();
        }

        private async Task<PutResult> PropagateStreamToRemote(
            Context context,
            PutResult localPutResult,
            Stream stream,
            CancellationToken cts,
            UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            // content wasn't found in remote ...
            if (stream.CanSeek)
            {
                // ... and stream is seekable, so can be used again.
                stream.Position = 0;
                PutResult remotePutResult = await _remoteCacheSession.PutStreamAsync(
                    context, localPutResult.ContentHash, stream, cts, urgencyHint);
                return remotePutResult;
            }

            // stream is not seekable, so we need a new one.
            OpenStreamResult localOpenResult = await _localCacheSession.OpenStreamAsync(
                context, localPutResult.ContentHash, cts, urgencyHint);

            if (!localOpenResult.Succeeded)
            {
                return new PutResult(localOpenResult);
            }

            using (localOpenResult.Stream)
            {
                PutResult remotePutResult = await _remoteCacheSession.PutStreamAsync(
                    context, localPutResult.ContentHash, localOpenResult.Stream, cts, urgencyHint);
                return remotePutResult;
            }
        }

        private static OperationContext OperationContext(Context context, CancellationToken token) => new OperationContext(context, token);

        private async Task<(TResult Local, TResult Remote)> MultiLevel<TResult>(Func<ICacheSession, Task<TResult>> func)
        {
            var localCacheTask = Task.Run(() => func(_localCacheSession));
            var remoteCacheTask = Task.Run(() => func(_remoteCacheSession));

            await Task.WhenAll(localCacheTask, remoteCacheTask);

            return (await localCacheTask, await remoteCacheTask);
        }
    }
}
