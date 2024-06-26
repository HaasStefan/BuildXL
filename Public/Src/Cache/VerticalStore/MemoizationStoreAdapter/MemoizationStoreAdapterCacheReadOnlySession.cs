// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.Interfaces;
using BuildXL.Cache.MemoizationStore.Interfaces.Results;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Core.Tasks;
using StrongFingerprint = BuildXL.Cache.Interfaces.StrongFingerprint;

namespace BuildXL.Cache.MemoizationStoreAdapter
{
    /// <summary>
    /// CacheReadOnlySession for MemoizationStoreAdapterCache
    /// </summary>
    public class MemoizationStoreAdapterCacheReadOnlySession : ICacheReadOnlySession
    {
        /// <summary>
        /// Backing ReadOnlyCacheSession for content and fingerprint calls.
        /// </summary>
        protected readonly MemoizationStore.Interfaces.Sessions.ICacheSession ReadOnlyCacheSession;

        /// <summary>
        /// The set of strong fingerprints touched by this cache session.
        /// </summary>
        /// <remarks>
        /// Null if this is an anonymous session (sessionId == null).
        /// </remarks>
        protected readonly ConcurrentDictionary<StrongFingerprint, int> SessionEntries;

        /// <summary>
        /// Diagnostic logger
        /// </summary>
        protected readonly ILogger Logger;

        /// <summary>
        /// Backing cache
        /// </summary>
        protected readonly BuildXL.Cache.MemoizationStore.Interfaces.Caches.ICache Cache;

        /// <summary>
        /// Replace existing file on placing file from cache.
        /// </summary>
        protected bool ReplaceExistingOnPlaceFile;

        /// <summary>
        /// .ctor
        /// </summary>
        /// <param name="readOnlyCacheSession">Backing ReadOnlyCacheSession for content and fingerprint calls</param>
        /// <param name="cache">Backing cache.</param>
        /// <param name="cacheId">Id of the parent cache that spawned the session.</param>
        /// <param name="logger">Diagnostic logger</param>
        /// <param name="sessionId">Telemetry ID for the session.</param>
        /// <param name="replaceExistingOnPlaceFile">When true, replace existing file when placing file.</param>
        public MemoizationStoreAdapterCacheReadOnlySession(
            MemoizationStore.Interfaces.Sessions.ICacheSession readOnlyCacheSession,
            BuildXL.Cache.MemoizationStore.Interfaces.Caches.ICache cache,
            CacheId cacheId,
            ILogger logger,
            string sessionId = null,
            bool replaceExistingOnPlaceFile = false)
        {
            ReadOnlyCacheSession = readOnlyCacheSession;
            Cache = cache;
            Logger = logger;
            CacheId = cacheId;
            CacheSessionId = sessionId;
            SessionEntries = sessionId == null ? null : new ConcurrentDictionary<StrongFingerprint, int>();
            ReplaceExistingOnPlaceFile = replaceExistingOnPlaceFile;
        }

        /// <inheritdoc />
        public CacheId CacheId { get; }

        /// <inheritdoc />
        public string CacheSessionId { get; }

        /// <inheritdoc />
        public bool IsClosed { get; private set; }

        /// <inheritdoc />
        public bool StrictMetadataCasCoupling => false;

        /// <inheritdoc />
        public async Task<Possible<string, Failure>> CloseAsync(Guid activityId)
        {
            if (IsClosed)
            {
                return CacheSessionId;
            }

            IsClosed = true;

            var shutdownResult = await ReadOnlyCacheSession.ShutdownAsync(CreateContext(activityId, Logger)).ConfigureAwait(false);
            ReadOnlyCacheSession.Dispose();
            if (!shutdownResult.Succeeded)
            {
                return new CacheFailure(shutdownResult.ErrorMessage);
            }

            return CacheSessionId;
        }

        /// <inheritdoc />
        public IEnumerable<Task<Possible<StrongFingerprint, Failure>>> EnumerateStrongFingerprints(
             WeakFingerprintHash weak, OperationHints hints, Guid activityId)
        {
            // TODO: Extend IAsyncEnumerable up through EnumerateStrongFingerprints
            var tcs = TaskSourceSlim.Create<IEnumerable<GetSelectorResult>>();
            yield return Task.Run(
                async () =>
                {
                    try
                    {
                        var results = await ReadOnlyCacheSession.GetSelectors(CreateContext(activityId, Logger), weak.ToMemoization(), CancellationToken.None).ToListAsync();
                        tcs.SetResult(results);
                        return results.Any() ? results.First().FromMemoization(weak, CacheId) : StrongFingerprintSentinel.Instance;
                    }
                    catch (Exception ex)
                    {
                        // Theoretically, the error might happened after we changed the state of tcs, like if the 'resutls' variable is emppty.
                        tcs.TrySetException(ex);
                        throw;
                    }
                });

            // For now, callers should always await the first task before enumerating the rest
            Contract.Assert(tcs.Task.IsCompleted);
            IEnumerable<GetSelectorResult> otherResults = tcs.Task.GetAwaiter().GetResult();
            foreach (var otherResult in otherResults.Skip(1))
            {
                yield return Task.FromResult(otherResult.FromMemoization(weak, CacheId));
            }
        }

        /// <inheritdoc />
        public async Task<Possible<CasEntries, Failure>> GetCacheEntryAsync(StrongFingerprint strong, OperationHints hints, Guid activityId)
        {
            var hashListResult = await ReadOnlyCacheSession.GetContentHashListAsync(
                CreateContext(activityId, Logger),
                new BuildXL.Cache.MemoizationStore.Interfaces.Sessions.StrongFingerprint(
                    strong.WeakFingerprint.ToMemoization(),
                    new Selector(strong.CasElement.ToMemoization(), strong.HashElement.RawHash.ToByteArray())),
                CancellationToken.None);

            if (hashListResult.Succeeded)
            {
                if (hashListResult.ContentHashListWithDeterminism.ContentHashList == null)
                {
                    return new NoMatchingFingerprintFailure(strong);
                }

                SessionEntries?.TryAdd(strong, 1);

                return hashListResult.ContentHashListWithDeterminism.FromMemoization();
            }
            else
            {
                return new CacheFailure(hashListResult.ErrorMessage);
            }
        }

        /// <inheritdoc />
        public async Task<Possible<string, Failure>> PinToCasAsync(CasHash hash, CancellationToken cancellationToken, OperationHints hints, Guid activityId)
        {
            var result = await ReadOnlyCacheSession.PinAsync(CreateContext(activityId, Logger), hash.ToMemoization(), cancellationToken, hints.Urgency);
            return result.FromMemoization(hash, CacheId);
        }

        /// <inheritdoc />
        public async Task<Possible<string, Failure>[]> PinToCasAsync(CasEntries hashes, CancellationToken cancellationToken, OperationHints hints, Guid activityId)
        {
            List<ContentHash> contentHashes = hashes.Select(hash => hash.ToContentHash()).ToList();
            IEnumerable<Task<Indexed<PinResult>>> resultSet = await ReadOnlyCacheSession.PinAsync(CreateContext(activityId, Logger), contentHashes, cancellationToken);

            var results = new Possible<string, Failure>[contentHashes.Count];

            foreach (Task<Indexed<PinResult>> resultTask in resultSet)
            {
                Indexed<PinResult> individualResult = await resultTask;
                results[individualResult.Index] = individualResult.Item.FromMemoization(hashes[individualResult.Index], CacheId);
            }

            return results;
        }

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly")]
        public async Task<Possible<string, Failure>> ProduceFileAsync(
            CasHash hash,
            string filename,
            FileState fileState,
            OperationHints hints,
            Guid activityId,
            CancellationToken cancellationToken)
        {
            var result = await ReadOnlyCacheSession.PlaceFileAsync(
                CreateContext(activityId, Logger),
                hash.ToMemoization(),
                new BuildXL.Cache.ContentStore.Interfaces.FileSystem.AbsolutePath(filename),
                fileState == FileState.ReadOnly ? FileAccessMode.ReadOnly : FileAccessMode.Write,
                ReplaceExistingOnPlaceFile ? FileReplacementMode.ReplaceExisting : FileReplacementMode.FailIfExists,
                fileState.ToMemoization(),
                cancellationToken);

            switch (result.Code)
            {
                case PlaceFileResult.ResultCode.PlacedWithHardLink:
                case PlaceFileResult.ResultCode.PlacedWithCopy:
                    return filename;
                case PlaceFileResult.ResultCode.NotPlacedAlreadyExists:
                    return new FileAlreadyExistsFailure(CacheId, hash, filename);
                case PlaceFileResult.ResultCode.NotPlacedContentNotFound:
                    return new NoCasEntryFailure(CacheId, hash);
                case PlaceFileResult.ResultCode.Error:
                case PlaceFileResult.ResultCode.Unknown:
                    return new CacheFailure(result.ErrorMessage);
                default:
                    return new CacheFailure("Unrecognized PlaceFileAsync result code: " + result.Code + ", error message: " + (result.ErrorMessage ?? string.Empty));
            }
        }

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly")]
        public async Task<Possible<StreamWithLength, Failure>> GetStreamAsync(CasHash hash, OperationHints hints, Guid activityId)
        {
            var result = await ReadOnlyCacheSession.OpenStreamAsync(CreateContext(activityId, Logger), hash.ToMemoization(), CancellationToken.None);
            switch (result.Code)
            {
                case OpenStreamResult.ResultCode.Success:
                    if (result.StreamWithLength != null)
                    {
                        return result.StreamWithLength.Value;
                    }

                    return new NoCasEntryFailure(CacheId, hash);
                case OpenStreamResult.ResultCode.Error:
                    return new CacheFailure(result.ErrorMessage);
                case OpenStreamResult.ResultCode.ContentNotFound:
                    return new NoCasEntryFailure(CacheId, hash);
                default:
                    return new CacheFailure("Unrecognized OpenStreamAsync result code: " + result.Code + ", error message: " + (result.ErrorMessage ?? string.Empty));
            }
        }

        /// <inheritdoc />
        public async Task<Possible<CacheSessionStatistics[], Failure>> GetStatisticsAsync(Guid activityId)
        {
            var cacheStats = await Cache.GetStatsAsync(CreateContext(activityId, Logger)).ConfigureAwait(false);
            if (!cacheStats.Succeeded)
            {
                return new Possible<CacheSessionStatistics[], Failure>(new CacheFailure(cacheStats.ErrorMessage));
            }

            CacheSessionStatistics finalStats =
                new CacheSessionStatistics(
                        CacheId,
                        Cache.GetType().FullName,
                        cacheStats.CounterSet.ToDictionaryIntegral().ToDictionary(kvp => kvp.Key, kvp => (double)kvp.Value));

            return new Possible<CacheSessionStatistics[], Failure>(new[] { finalStats });
        }

        /// <inheritdoc />
        public Task<Possible<ValidateContentStatus, Failure>> ValidateContentAsync(CasHash hash, UrgencyHint urgencyHint, Guid activityId)
        {
            // TODO:  Implement content validation/remediation
            return Task.FromResult(new Possible<ValidateContentStatus, Failure>(ValidateContentStatus.NotSupported));
        }

        /// <summary>
        /// Creates a new context (optionally using the activity from the async context if <paramref name="activityId"/> is not set)
        /// </summary>
        protected static Context CreateContext(Guid activityId, ILogger logger)
        {
            return new Context(CacheActivityRegistry.GetOrNewContextActivityId(activityId), logger);
        }
    }
}
