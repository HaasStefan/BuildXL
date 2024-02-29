// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Service.Grpc;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Engine.Cache;
using BuildXL.Engine.Cache.Artifacts;
using BuildXL.Engine.Cache.Fingerprints;
using BuildXL.Engine.Cache.Fingerprints.SinglePhase;
using BuildXL.Engine.Cache.Fingerprints.TwoPhase;
using BuildXL.Engine.Cache.KeyValueStores;
using BuildXL.Native.IO;
using BuildXL.Pips.Graph;
using BuildXL.Pips.Operations;
using BuildXL.Scheduler.Fingerprints;
using BuildXL.Scheduler.Tracing;
using BuildXL.Storage;
using BuildXL.Storage.Fingerprints;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Core.Tasks;
using BuildXL.Utilities.Instrumentation.Common;
using Google.Protobuf;
using static BuildXL.Utilities.Core.FormattableStringEx;
using OperationHints = BuildXL.Cache.ContentStore.Interfaces.Sessions.OperationHints;

namespace BuildXL.Scheduler.Cache
{
    /// <summary>
    /// PipTwoPhaseCache with historic metadata cache data
    /// </summary>
    public class HistoricMetadataCache : PipTwoPhaseCache
    {
        /// <summary>
        /// The version for format of <see cref="HistoricMetadataCache"/>
        /// </summary>
        public const int FormatVersion = 27;

        /// <summary>
        /// Indicates if entries should be purged as soon as there TTL reaches zero versus reaching a limit in percentage expired.
        /// </summary>
        private static bool ProactivePurging => EngineEnvironmentSettings.ProactivePurgeHistoricMetadataEntries;

        /// <summary>
        /// Default time-to-live (TTL) for new entries
        /// The TTL of an entry is the number of save / load round-trips until eviction (assuming it is not accessed within that time).
        /// </summary>
        public static byte TimeToLive => (byte)(EngineEnvironmentSettings.HistoricMetadataCacheDefaultTimeToLive.Value ?? 5);

        /// <summary>
        /// Time-to-live for computed content hashes.
        /// </summary>
        public const int ComputedContentHashTimeToLive = 10;

        private static readonly Task<Possible<Unit>> s_genericSuccessTask = Task.FromResult(new Possible<Unit>(Unit.Void));

        /// <summary>
        /// Originating cache string
        /// </summary>
        public const string OriginatingCacheId = "HistoricMetadataCache";

        /// <summary>
        /// The location of the data files for the cache
        /// </summary>
        public readonly string StoreLocation;

        /// <summary>
        /// The directory for log files for the cache
        /// </summary>
        public readonly string LogDirectoryLocation;

        /// <summary>
        /// PathSet or Metadata content hashes that are newly added in the current session.
        /// </summary>
        /// <remarks>
        /// BuildXL only sends the new hashes to orchestrator from worker
        /// </remarks>
        private readonly ConcurrentBigMap<ContentHash, bool> m_newContentEntries;

        /// <summary>
        /// Hash codes of content which should be retained in the database. This is content associated with entries
        /// in <see cref="m_weakFingerprintEntries"/> and <see cref="m_fullFingerprintEntries"/>
        /// </summary>
        private readonly ConcurrentBigSet<int> m_retainedContentHashCodes = new ConcurrentBigSet<int>();

        /// <summary>
        /// Hash codes of content in the database.
        /// </summary>
        private readonly ConcurrentBigSet<int> m_existingContentEntries = new ConcurrentBigSet<int>();

        /// <summary>
        /// WeakFingerprint -> stack of (StrongFingeprint, PathSetHash).
        /// </summary>
        /// <remarks>
        ///  We want to iterate the lastly added cache entries earlier to increase the chance for strong fingerprint hits.
        ///  </remarks>
        private readonly ConcurrentBigMap<WeakContentFingerprint, Stack<Expirable<PublishedEntry>>> m_weakFingerprintEntries;

        /// <summary>
        /// Full Fingerprint (WeakFingerprint ^ StrongFingerprint) -> MetadataHash
        /// </summary>
        private readonly ConcurrentBigMap<ContentFingerprint, ContentHash> m_fullFingerprintEntries;

        /// <summary>
        /// Tracking mapping from weak fingerprint to semistable hash. Serialization is based on weak fingerprints. So in order to serialize
        /// <see cref="m_pipSemistableHashToWeakFingerprintMap"/> we need to lookup the corresponding semistable hash for a given weak
        /// fingerprint.
        /// </summary>
        private readonly ConcurrentBigMap<WeakContentFingerprint, long> m_weakFingerprintsToPipSemistableHashMap = new ConcurrentBigMap<WeakContentFingerprint, long>();

        /// <summary>
        /// Mapping from pip semistable hash to weak fingerprint for the purpose of looking up accessed paths for a pip (based on associated path sets).
        /// </summary>
        private readonly ConcurrentBigMap<long, WeakContentFingerprint> m_pipSemistableHashToWeakFingerprintMap = new ConcurrentBigMap<long, WeakContentFingerprint>();

        /// <summary>
        /// Newly added full fingerprints (WeakFingerprint ^ StrongFingerprint) 
        /// </summary>
        private readonly ConcurrentBigSet<ContentFingerprint> m_newFullFingerprints;

        /// <summary>
        /// The age of the historic metadata cache
        /// </summary>
        public int Age { get; private set; }

        /// <summary>
        /// Whether the historic metadata cache contains valid information.
        /// This is false if the historic metadata cache version is outdated,
        /// the backing store fails to open, or a read/write operation fails during the lifetime of the store.
        /// </summary>
        public bool Valid { get; private set; }

        /// <summary>
        /// Whether <see cref="CloseAsync"/> has been called.
        /// </summary>
        public bool Closed
        {
            get
            {
                return Volatile.Read(ref m_closed);
            }
            private set
            {
                Volatile.Write(ref m_closed, value);
            }
        }

        private bool m_closed = false;

        /// <summary>
        /// Whether the async initialization of <see cref="m_loadTask"/> has started.
        /// </summary>
        private bool m_loadStarted = false;

        private readonly Lazy<Task> m_loadTask;
        private readonly TaskSourceSlim<Unit> m_prepareCompletion;
        private Task m_garbageCollectTask = Task.FromResult(0);
        private readonly CancellationTokenSource m_garbageCollectCancellation = new CancellationTokenSource();

        private KeyValueStoreAccessor StoreAccessor
        {
            get
            {
                // If the store accessor failed during setup or has become disabled, return null
                if (m_storeAccessor.Value == null || m_storeAccessor.Value.Disabled)
                {
                    return null;
                }

                return m_storeAccessor.Value;
            }
        }

        private readonly Lazy<KeyValueStoreAccessor> m_storeAccessor;
        private int m_activeContentHashMappingColumnIndex;

        /// <nodoc/>
        public HistoricMetadataCache(
            LoggingContext loggingContext,
            EngineCache cache,
            PipExecutionContext context,
            PathExpander pathExpander,
            AbsolutePath storeLocation,
            Func<HistoricMetadataCache, Task> prepareAsync = null,
            AbsolutePath? logDirectoryLocation = null)
            : base(loggingContext, cache, context, pathExpander)
        {
            Contract.Requires(loggingContext != null);
            Contract.Requires(cache != null);
            Contract.Requires(context != null);
            Contract.Requires(storeLocation.IsValid);

            StoreLocation = storeLocation.ToString(context.PathTable);
            LogDirectoryLocation = logDirectoryLocation.HasValue && logDirectoryLocation.Value.IsValid ? logDirectoryLocation.Value.ToString(context.PathTable) : null;
            m_storeAccessor = new Lazy<KeyValueStoreAccessor>(OpenStore);
            m_hashingPool = new ObjectPool<HashingHelper>(() => new HashingHelper(context.PathTable, recordFingerprintString: false), h => h.Dispose());
            Valid = false;

            m_newContentEntries = new ConcurrentBigMap<ContentHash, bool>();
            m_newFullFingerprints = new ConcurrentBigSet<ContentFingerprint>();
            m_weakFingerprintEntries = new ConcurrentBigMap<WeakContentFingerprint, Stack<Expirable<PublishedEntry>>>();
            m_fullFingerprintEntries = new ConcurrentBigMap<ContentFingerprint, ContentHash>();
            m_retainedContentHashCodes = new ConcurrentBigSet<int>();

            Age = 0;
            m_activeContentHashMappingColumnIndex = -1;

            m_prepareCompletion = TaskSourceSlim.Create<Unit>();
            m_loadTask = new Lazy<Task>(() => ExecuteLoadTask(prepareAsync));
        }

        private async Task ExecuteLoadTask(Func<HistoricMetadataCache, Task> prepareAsync)
        {
            // Unblock the caller
            await Task.Yield();

            try
            {
                var task = prepareAsync?.Invoke(this) ?? Unit.VoidTask;
                await task;

                m_prepareCompletion.TrySetResult(Unit.Void);

                StoreAccessor?.Use(database =>
                {
                    using (Counters.StartStopwatch(PipCachingCounter.HistoricDeserializationDuration))
                    {
                        LoadCacheEntries(database);
                    }

                    PrepareBuildManifestColumn(database);
                });

                m_garbageCollectTask = Task.Run(() => GarbageCollect());
            }
            catch (Exception ex)
            {
                Logger.Log.HistoricMetadataCacheLoadFailed(LoggingContext, ex.ToString());
                m_prepareCompletion.TrySetResult(Unit.Void);
                Valid = false;
            }
        }

        /// <summary>
        /// Starting the loading task for the historic metadata cache
        /// </summary>
        public override void StartLoading(bool waitForCompletion)
        {
            var loadingTask = EnsureLoadedAsync();
            if (waitForCompletion)
            {
                loadingTask.GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// Ensures that the historic metadata cache is fully loaded before queries to metadata are allowed.
        /// </summary>
        private Task EnsureLoadedAsync()
        {
            if (Closed)
            {
                throw new BuildXLException("Attempts to load the HistoricMetadataCache should not be made after Close is called.");
            }

            Volatile.Write(ref m_loadStarted, true);

            return m_loadTask.Value;
        }

        /// <inheritdoc/>
        public override IEnumerable<Task<Possible<PublishedEntryRef, Failure>>> ListPublishedEntriesByWeakFingerprint(
            OperationContext operationContext,
            WeakContentFingerprint weak,
            OperationHints hints = default)
        {
            EnsureLoadedAsync().GetAwaiter().GetResult();

            Counters.IncrementCounter(PipCachingCounter.HistoricWeakFingerprintHits);

            Stack<Expirable<PublishedEntry>> stack;
            if (m_weakFingerprintEntries.TryGetValue(weak, out stack))
            {
                foreach (var entry in stack)
                {
                    // Exclude entries with no pointer to the full fingerprint. This state happens if GC has collected the full
                    // fingerprint due to missing content.
                    if (m_fullFingerprintEntries.ContainsKey(GetFullFingerprint(weak, entry.Value.StrongFingerprint)))
                    {
                        yield return Task.FromResult(
                            new Possible<PublishedEntryRef, Failure>(
                                new PublishedEntryRef(
                                    entry.Value.PathSetHash,
                                    entry.Value.StrongFingerprint,
                                    OriginatingCacheId,
                                    PublishedEntryRefLocality.Local)));
                    }
                }
            }

            Counters.DecrementCounter(PipCachingCounter.HistoricWeakFingerprintHits);
            Counters.IncrementCounter(PipCachingCounter.HistoricWeakFingerprintMisses);

            foreach (var entry in base.ListPublishedEntriesByWeakFingerprint(operationContext, weak, hints))
            {
                yield return entry;
            }

            Counters.IncrementCounter(PipCachingCounter.WeakFingerprintMisses);
        }

        /// <inheritdoc/>
        public override async Task<Possible<CacheEntry?, Failure>> TryGetCacheEntryAsync(
            Pip pip,
            WeakContentFingerprint weakFingerprint,
            ContentHash pathSetHash,
            StrongContentFingerprint strongFingerprint,
            OperationHints hints)
        {
            await EnsureLoadedAsync();

            var fullFingerprint = GetFullFingerprint(weakFingerprint, strongFingerprint);
            ContentHash metadataHash;
            if (m_fullFingerprintEntries.TryGetValue(fullFingerprint, out metadataHash))
            {
                Counters.IncrementCounter(PipCachingCounter.HistoricCacheEntryHits);

                // Update the TTL in the weakFingerprintEntries
                Stack<Expirable<PublishedEntry>> stack;
                if (m_weakFingerprintEntries.TryGetValue(weakFingerprint, out stack))
                {
                    var newEntry = NewExpirable(new PublishedEntry(strongFingerprint, pathSetHash), TimeToLive);
                    stack.Push(newEntry);
                }

                return new CacheEntry(metadataHash, OriginatingCacheId, new ContentHash[] { metadataHash });
            }

            var result = await base.TryGetCacheEntryAsync(pip, weakFingerprint, pathSetHash, strongFingerprint, hints);
            if (result.Succeeded && result.Result.HasValue)
            {
                Counters.IncrementCounter(PipCachingCounter.HistoricCacheEntryMisses);
                SetMetadataEntry(weakFingerprint, strongFingerprint, pathSetHash, result.Result.Value.MetadataHash, semistableHash: pip.SemiStableHash);
            }

            return result;
        }

        /// <inheritdoc/>
        public override async Task<Possible<CacheEntryPublishResult, Failure>> TryPublishCacheEntryAsync(
            Pip pip,
            WeakContentFingerprint weakFingerprint,
            ContentHash pathSetHash,
            StrongContentFingerprint strongFingerprint,
            CacheEntry entry,
            CacheEntryPublishMode mode = CacheEntryPublishMode.CreateNew)
        {
            var result = await base.TryPublishCacheEntryAsync(pip, weakFingerprint, pathSetHash, strongFingerprint, entry, mode);

            if (result.Succeeded)
            {
                SetMetadataEntry(
                    weakFingerprint,
                    strongFingerprint,
                    pathSetHash,
                    result.Result.Status == CacheEntryPublishStatus.Published ? entry.MetadataHash : result.Result.ConflictingEntry.MetadataHash,
                    pip.SemiStableHash);
            }

            return result;
        }

        /// <inheritdoc/>
        public override async Task<Possible<PipCacheDescriptorV2Metadata>> TryRetrieveMetadataAsync(
            Pip pip,
            WeakContentFingerprint weakFingerprint,
            StrongContentFingerprint strongFingerprint,
            ContentHash metadataHash,
            ContentHash pathSetHash)
        {
            await EnsureLoadedAsync();

            if (TryGetContent(metadataHash, out var content))
            {
                Counters.IncrementCounter(PipCachingCounter.HistoricMetadataHits);
                return CacheGrpcExtensions.Deserialize<PipCacheDescriptorV2Metadata>(content);
            }

            var possiblyRetrieved = await base.TryRetrieveMetadataAsync(pip, weakFingerprint, strongFingerprint, metadataHash, pathSetHash);
            if (possiblyRetrieved.Succeeded)
            {
                Counters.IncrementCounter(PipCachingCounter.HistoricMetadataMisses);
                if (possiblyRetrieved.Result != null)
                {
                    TryAdd(metadataHash, possiblyRetrieved.Result);
                }
            }

            return possiblyRetrieved;
        }

        /// <inheritdoc/>
        public override async Task<Possible<ContentHash>> TryStoreMetadataAsync(PipCacheDescriptorV2Metadata metadata)
        {
            var possiblyStored = await base.TryStoreMetadataAsync(metadata);
            if (possiblyStored.Succeeded)
            {
                await EnsureLoadedAsync();

                var metadataHash = possiblyStored.Result;
                TryAdd(metadataHash, metadata);
            }

            return possiblyStored;
        }

        /// <inheritdoc/>
        protected override async Task<Possible<StreamWithLength>> TryLoadAndOpenPathSetStreamAsync(ContentHash pathSetHash, bool avoidRemoteLookups = false)
        {
            await EnsureLoadedAsync();

            if (TryGetContent(pathSetHash, out var content))
            {
                Counters.IncrementCounter(PipCachingCounter.HistoricPathSetHits);
                return (new MemoryStream(content.Array, content.Offset, content.Count, writable: false)).WithLength();
            }

            var possiblyOpened = await base.TryLoadAndOpenPathSetStreamAsync(pathSetHash, avoidRemoteLookups);
            if (possiblyOpened.Succeeded)
            {
                Counters.IncrementCounter(PipCachingCounter.HistoricPathSetMisses);
                using (Stream stream = possiblyOpened.Result)
                {
                    content = new ArraySegment<byte>(new byte[(int)stream.Length]);
                    var readCount = await stream.ReadAsync(content.Array, 0, content.Count);
                    Contract.Assert(readCount == content.Count);
                    TryAddContent(pathSetHash, content);
                    return (new MemoryStream(content.Array, writable: false)).WithLength();
                }
            }

            return possiblyOpened;
        }

        /// <inheritdoc/>
        public override IEnumerable<Task<Possible<ObservedPathSet>>> TryGetAssociatedPathSetsAsync(OperationContext context, Pip pip)
        {
            if (m_pipSemistableHashToWeakFingerprintMap.TryGetValue(pip.SemiStableHash, out var weakFingerprint)
                && m_weakFingerprintEntries.TryGetValue(weakFingerprint, out var entries))
            {
                foreach (var entry in entries)
                {
                    yield return TryRetrievePathSetAsync(context, weakFingerprint, entry.Value.PathSetHash);
                }
            }
        }

        /// <inheritdoc/>
        protected override async Task<Possible<Unit>> TryStorePathSetContentAsync(ContentHash pathSetHash, MemoryStream pathSetBuffer)
        {
            await EnsureLoadedAsync();

            this.TryAddContent(pathSetHash, ToStorableContent(pathSetBuffer));
            return await base.TryStorePathSetContentAsync(pathSetHash, pathSetBuffer);
        }

        private static ArraySegment<byte> ToStorableContent(MemoryStream pathSetBuffer)
        {
            return new ArraySegment<byte>(pathSetBuffer.GetBuffer(), 0, (int)pathSetBuffer.Length);
        }

        /// <summary>
        /// Adding cache entry
        /// </summary>
        private bool SetMetadataEntry(
            WeakContentFingerprint weakFingerprint,
            StrongContentFingerprint strongFingerprint,
            ContentHash pathSetHash,
            ContentHash metadataHash,
            long semistableHash,
            byte? ttl = null)
        {
            m_retainedContentHashCodes.Add(pathSetHash.GetHashCode());
            m_retainedContentHashCodes.Add(metadataHash.GetHashCode());

            if (semistableHash != 0)
            {
                m_pipSemistableHashToWeakFingerprintMap[semistableHash] = weakFingerprint;
                m_weakFingerprintsToPipSemistableHashMap[weakFingerprint] = semistableHash;
            }

            var fullFingerprint = GetFullFingerprint(weakFingerprint, strongFingerprint);
            var addOrUpdateResult = m_fullFingerprintEntries.AddOrUpdate(
                fullFingerprint,
                metadataHash,
                (key, hash) => hash,
                (key, hash, existingEntry) => hash);

            if (!addOrUpdateResult.IsFound)
            {
                m_newFullFingerprints.Add(fullFingerprint);
            }

            var getOrAddResult = m_weakFingerprintEntries.GetOrAdd(
                weakFingerprint,
                new Stack<Expirable<PublishedEntry>>(),
                (key, result) => result);

            var stack = getOrAddResult.Item.Value;
            lock (stack)
            {
                stack.Push(NewExpirable(new PublishedEntry(strongFingerprint, pathSetHash), ttl ?? TimeToLive));
            }

            return !addOrUpdateResult.IsFound;
        }

        private static ContentFingerprint GetFullFingerprint(WeakContentFingerprint weakFingerprint, StrongContentFingerprint strongFingerprint)
        {
            Span<byte> fullFingerprintBytes = stackalloc byte[FingerprintUtilities.FingerprintLength];
            for (int i = 0; i < FingerprintUtilities.FingerprintLength; i++)
            {
                fullFingerprintBytes[i] = (byte)(weakFingerprint.Hash[i] ^ strongFingerprint.Hash[i]);
            }

            return new ContentFingerprint(new Fingerprint(new ReadOnlyFixedBytes(fullFingerprintBytes), FingerprintUtilities.FingerprintLength));
        }

        /// <inheritdoc />
        public override async Task CloseAsync()
        {
            // Allow Close to be called multiple times with no side effects
            if (Closed)
            {
                return;
            }

            Closed = true;
            Logger.Log.HistoricMetadataCacheCloseCalled(LoggingContext);

            // Only save info if historic metadata cache was accessed
            if (Volatile.Read(ref m_loadStarted))
            {
                // If a load was started, wait for full completion of the load
                // Otherwise, close and the load initialization can run concurrently and cause race conditions
                await m_loadTask.Value;

                Logger.Log.HistoricMetadataCacheTrace(LoggingContext, I($"Saving historic metadata cache Start"));

                if (StoreAccessor != null)
                {
                    // Stop garbage collection
                    await m_garbageCollectCancellation.CancelTokenAsyncIfSupported();
                    // Wait for garbage collection to complete
                    await m_garbageCollectTask;

                    await StoreCacheEntriesAsync();
                }

                Logger.Log.HistoricMetadataCacheTrace(LoggingContext, I($"Saving historic metadata cache Done"));
                StoreAccessor?.Dispose();

                // Once the store accessor has been closed, the log can be safely copied
                if (LogDirectoryLocation != null)
                {
                    try
                    {
                        if (!FileUtilities.Exists(LogDirectoryLocation))
                        {
                            FileUtilities.CreateDirectory(LogDirectoryLocation);
                        }

                        await FileUtilities.CopyFileAsync(
                            Path.Combine(StoreLocation, KeyValueStoreAccessor.LogFileName),
                            Path.Combine(LogDirectoryLocation, KeyValueStoreAccessor.LogFileName));
                    }
                    catch (Exception e)
                    {
                        //  It's not super important that the log file is copied, so just log a message
                        Logger.Log.HistoricMetadataCacheTrace(LoggingContext, "Failed to copy historic metadata cache log file to BuildXL logs folder:" + e.GetLogEventMessage());
                    }
                }
            }

            await base.CloseAsync();
        }

        private void GarbageCollect()
        {
            if (StoreAccessor != null)
            {
                Analysis.IgnoreResult(
                    StoreAccessor.Use(store =>
                    {
                        using (Counters.StartStopwatch(PipCachingCounter.HistoricCollectDuration))
                        {
                            byte[] startKey = null;
                            store.TryGetValue(StoreKeyNames.ContentGarbageCollectCursor, out startKey);

                            var gcStats = store.GarbageCollect(
                                canCollect: (byte[] key) =>
                                {
                                    if (ContentHashingUtilities.HashInfo.ByteLength == key.Length)
                                    {
                                        var hashCode = ContentHashingUtilities.CreateFrom(key).GetHashCode();
                                        if (m_retainedContentHashCodes.Contains(hashCode))
                                        {
                                            m_existingContentEntries.Add(hashCode);
                                            return false;
                                        }

                                        return true;
                                    }

                                    // Unexpected key in the content segment of the store. Just remove it.
                                    return true;
                                },
                                columnFamilyName: StoreColumnNames.Content,
                                cancellationToken: m_garbageCollectCancellation.Token,
                                startValue: startKey);

                            if (m_garbageCollectCancellation.IsCancellationRequested)
                            {
                                Counters.AddToCounter(PipCachingCounter.HistoricCollectCancelled, 1);
                            }

                            Counters.AddToCounter(PipCachingCounter.HistoricCollectMaxBatchEvictionTime, gcStats.MaxBatchEvictionTime);

                            if (m_garbageCollectCancellation.IsCancellationRequested && gcStats.LastKey != null)
                            {
                                // Garbage collection was terminated, so store the last key to enable resuming collection on the next build
                                store.Put(StoreKeyNames.ContentGarbageCollectCursor, gcStats.LastKey);
                            }
                            else
                            {
                                // Garbage collection finished, so start at the beginning for the next garbage collection
                                store.Remove(StoreKeyNames.ContentGarbageCollectCursor);
                            }

                            Counters.AddToCounter(PipCachingCounter.HistoricCollectRemovedBlobCount, gcStats.RemovedCount);
                            Counters.AddToCounter(PipCachingCounter.HistoricCollectTotalBlobCount, gcStats.TotalCount);
                        }
                    })
                );
            }
        }

        /// <inheritdoc />
        public override bool IsNewlyAdded(ContentHash hash)
        {
            return m_newContentEntries.TryGetValue(hash, out var newlyAdded) && newlyAdded;
        }

        /// <inheritdoc/>
        public override void ReportRemoteMetadata(
            PipCacheDescriptorV2Metadata metadata,
            ContentHash? metadataHash,
            ContentHash? pathSetHash,
            WeakContentFingerprint? weakFingerprint,
            StrongContentFingerprint? strongFingerprint,
            bool isExecution,
            bool preservePathCasing)
        {
            EnsureLoadedAsync().GetAwaiter().GetResult();
            Counters.IncrementCounter(PipCachingCounter.ReportRemoteMetadataCalls);

            if (metadata != null && metadataHash.HasValue)
            {
                if (TryAdd(metadataHash.Value, metadata))
                {
                    Counters.IncrementCounter(
                        isExecution
                            ? PipCachingCounter.HistoricMetadataCountFromRemoteExecution
                            : PipCachingCounter.HistoricMetadataCountFromRemoteLookup);
                }
                else
                {
                    Counters.IncrementCounter(
                        isExecution
                            ? PipCachingCounter.HistoricMetadataExistCountFromRemoteExecution
                            : PipCachingCounter.HistoricMetadataExistCountFromRemoteLookup);
                }
            }

            if (weakFingerprint.HasValue && strongFingerprint.HasValue && pathSetHash.HasValue && metadataHash.HasValue)
            {
                if (SetMetadataEntry(weakFingerprint.Value, strongFingerprint.Value, pathSetHash.Value, metadataHash.Value, metadata?.SemiStableHash ?? 0))
                {
                    Counters.IncrementCounter(
                        isExecution
                            ? PipCachingCounter.HistoricCacheEntryCountFromRemoteExecution
                            : PipCachingCounter.HistoricCacheEntryCountFromRemoteLookup);
                }
                else
                {
                    Counters.IncrementCounter(
                        isExecution
                            ? PipCachingCounter.HistoricCacheEntryExistCountFromRemoteExecution
                            : PipCachingCounter.HistoricCacheEntryExistCountFromRemoteLookup);
                }
            }
        }

        /// <inheritdoc/>
        public override void ReportRemotePathSet(
            ObservedPathSet? pathSet,
            ContentHash? pathSetHash,
            bool isExecution,
            bool preservePathCasing)
        {
            Counters.IncrementCounter(PipCachingCounter.ReportRemotePathSetCalls);

            if (pathSet.HasValue && pathSetHash.HasValue)
            {
                if (TryAdd(pathSetHash.Value, pathSet.Value, preservePathCasing))
                {
                    Counters.IncrementCounter(
                        isExecution
                            ? PipCachingCounter.HistoricPathSetCountFromRemoteExecution
                            : PipCachingCounter.HistoricPathSetCountFromRemoteLookup);
                }
                else
                {
                    Counters.IncrementCounter(
                        isExecution
                            ? PipCachingCounter.HistoricPathSetExistCountFromRemoteExecution
                            : PipCachingCounter.HistoricPathSetExistCountFromRemoteLookup);
                }
            }
        }

        /// <inheritdoc/>
        public override void TryStoreRemappedContentHash(ContentHash contentHash, ContentHash remappedContentHash)
        {
            EnsureLoadedAsync().GetAwaiter().GetResult();

            StoreAccessor?.Use(database =>
            {
                using var key = GetRemappingContentHashKey(contentHash, remappedContentHash.HashType);
                if (!database.TryGetValue(key.Value, out var existingValue, StoreColumnNames.ContentHashMappingColumn[m_activeContentHashMappingColumnIndex]))
                {
                    using var value = remappedContentHash.ToPooledByteArray();
                    // While we are checking both columns, we are storing only into the active one.
                    database.Put(key.Value, value.Value, StoreColumnNames.ContentHashMappingColumn[m_activeContentHashMappingColumnIndex]);
                }
            });
        }

        private readonly ObjectPool<HashingHelper> m_hashingPool;

        internal static readonly ByteArrayPool ContentMappingKeyArrayPool = new(ContentHash.SerializedLength + 1);

        /// <summary>
        /// Get key to store mapped content hash
        /// </summary>
        private ByteArrayPool.PoolHandle GetRemappingContentHashKey(ContentHash contentHash, HashType targetHashType)
        {
            var handle = ContentMappingKeyArrayPool.Get();
            byte[] buffer = handle.Value;

            // Store the hash type in the first byte
            unchecked
            {
                buffer[0] = (byte)targetHashType;
            }

            contentHash.Serialize(buffer, offset: 1);
            return handle;
        }

        /// <inheritdoc/>
        public override ContentHash TryGetMappedContentHash(ContentHash contentHash, HashType hashType)
        {
            EnsureLoadedAsync().GetAwaiter().GetResult();

            Contract.Assert(contentHash.IsValid, "ContentHash must be valid");

            ContentHash foundHash = default;
            StoreAccessor?.Use(database =>
            {
                Contract.Assert(m_activeContentHashMappingColumnIndex >= 0, "Build manifest column is not initialized");
                using var key = GetRemappingContentHashKey(contentHash, hashType);
                Contract.Assert(key.Value != null, "ByteArray was null for ContentHash");

                // First, check the active column. If the hash is not there, check the other column.
                if (database.TryGetValue(key.Value, out var value, StoreColumnNames.ContentHashMappingColumn[m_activeContentHashMappingColumnIndex]))
                {
                    foundHash = new ContentHash(value);
                }
                else if (database.TryGetValue(key.Value, out value, StoreColumnNames.ContentHashMappingColumn[(m_activeContentHashMappingColumnIndex + 1) % 2]))
                {
                    // need to update the active column
                    database.Put(key.Value, value, StoreColumnNames.ContentHashMappingColumn[m_activeContentHashMappingColumnIndex]);
                    foundHash = new ContentHash(value);
                }
            });

            if (foundHash.IsValid && foundHash.HashType != hashType)
            {
                Contract.Assert(false, $"Mismatched hash retrieval from historic metadata cache. Req: {hashType}. Type: {foundHash.HashType}");
            }

            return foundHash;
        }

        /// <summary>
        /// Adding pathset
        /// </summary>
        private bool TryAdd(ContentHash hash, in ObservedPathSet pathSet, bool preservePathCasing)
        {
            EnsureLoadedAsync().GetAwaiter().GetResult();

            using (Counters.StartStopwatch(PipCachingCounter.HistoricTryAddPathSetDuration))
            {
                bool added = false;
                Analysis.IgnoreResult(
                    TrySerializedAndStorePathSetAsync(
                        pathSet,
                        (pathSetHash, pathSetBuffer) =>
                        {
                            added = TryAddContent(pathSetHash, ToStorableContent(pathSetBuffer));
                            return s_genericSuccessTask;
                        },
                        preservePathCasing,
                        pathSetHash: hash
                    ).GetAwaiter().GetResult()
                );
                return added;
            }
        }

        /// <summary>
        /// Adding metadata
        /// </summary>
        private bool TryAdd(ContentHash metadataHash, PipCacheDescriptorV2Metadata metadata)
        {
            using (Counters.StartStopwatch(PipCachingCounter.HistoricTryAddMetadataDuration))
            {
                return TryAddContent(metadataHash, CacheGrpcExtensions.Serialize(metadata));
            }
        }

        private bool TryGetContent(ContentHash hash, out ArraySegment<byte> content)
        {
            using (Counters.StartStopwatch(PipCachingCounter.HistoricTryGetContentDuration))
            {
                content = default(ArraySegment<byte>);

                ArraySegment<byte>? result = null;
                StoreAccessor?.Use(database =>
                {
                    var hashKey = hash.ToHashByteArray();

                    if (database.TryGetValue(hashKey, out var value, StoreColumnNames.Content))
                    {
                        if (m_newContentEntries.TryAdd(hash, false))
                        {
                            m_retainedContentHashCodes.Add(hash.GetHashCode());
                        }
                        result = new ArraySegment<byte>(value);
                    }
                    else
                    {
                        result = null;
                    }
                });

                if (result == null)
                {
                    return false;
                }

                content = result.Value;
                return true;
            }
        }

        private bool TryAddContent(ContentHash hash, ArraySegment<byte> content)
        {
            using (Counters.StartStopwatch(PipCachingCounter.HistoricTryAddContentDuration))
            {
                if (m_newContentEntries.TryGetValue(hash, out var newlyAdded))
                {
                    // Already encountered content. No need to update backing store.
                    return false;
                }

                var success = false;
                StoreAccessor?.Use(database =>
                {
                    var hashKey = hash.ToHashByteArray();

                    if (!database.TryGetValue(hashKey, out var result, StoreColumnNames.Content))
                    {
                        // No hits. Content is new.
                        if (m_newContentEntries.TryAdd(hash, true))
                        {
                            m_retainedContentHashCodes.Add(hash.GetHashCode());
                            database.Put(hashKey, content.ToArray(), StoreColumnNames.Content);
                        }

                        success = true;
                    }
                    else
                    {
                        // Hits. Content is already present.
                        if (m_newContentEntries.TryAdd(hash, false))
                        {
                            m_retainedContentHashCodes.Add(hash.GetHashCode());
                        }

                        success = false;
                    }
                });

                return success;
            }
        }

        #region Serialization

        /// <summary>
        /// Loads the cache entries from the database
        /// </summary>
        private void LoadCacheEntries(RocksDbStore store)
        {
            if (store.TryGetValue(StoreKeyNames.HistoricMetadataCacheEntriesKey, out var serializedCacheEntries))
            {
                Age = GetAge(store) + 1;
                SetAge(store, Age);

                using (var stream = new MemoryStream(serializedCacheEntries))
                using (var reader = BuildXLReader.Create(stream, leaveOpen: true))
                {
                    DeserializeCacheEntries(reader);
                }
            }

            m_activeContentHashMappingColumnIndex = ComputeContentHashMappingActiveColumnIndex(Age);
            Counters.AddToCounter(PipCachingCounter.HistoricMetadataLoadedAge, Age);
        }

        private int GetAge(RocksDbStore store)
        {
            bool ageFound = store.TryGetValue(nameof(StoreKeyNames.Age), out var ageString);
            if (!ageFound || !int.TryParse(ageString, out var age))
            {
                SetAge(store, 0);
                return 0;
            }

            return age;
        }

        private void SetAge(RocksDbStore store, int age)
        {
            store.Put(nameof(StoreKeyNames.Age), age.ToString());
        }

        /// <summary>
        /// Stores the cache entries into the database
        /// </summary>
        private async Task StoreCacheEntriesAsync()
        {
            Contract.Requires(m_loadTask.IsValueCreated && m_loadTask.Value.IsCompleted);

            // Unblock the thread
            await Task.Yield();

            using (Counters.StartStopwatch(PipCachingCounter.HistoricSerializationDuration))
            using (var stream = new MemoryStream())
            using (var writer = BuildXLWriter.Create(stream, leaveOpen: true))
            {
                SerializeCacheEntries(writer);
                var serializedCacheEntries = ToStorableContent(stream);

                Analysis.IgnoreResult(StoreAccessor.Use(store =>
                {
                    store.Put(StoreKeyNames.HistoricMetadataCacheEntriesKey, serializedCacheEntries.ToArray());
                }));
            }
        }

        private static List<T> GetUnexpiredEntries<T>(IReadOnlyCollection<T> entries, Func<T, bool> isExpired, out bool isPurging)
        {
            if (!ProactivePurging)
            {
                // If not proactive purging, wait to reach over half entries expired
                var expiredCount = entries.Where(isExpired).Count();
                if (expiredCount < (0.5 * entries.Count))
                {
                    // If the amount of expired entries is less than half, we don't purge (i.e. retain all entries even if they are expired)
                    isPurging = false;
                    return entries.ToList();
                }
            }

            isPurging = true;
            return entries.Where(v => !isExpired(v)).ToList();
        }

        private static byte ReadByteAsTimeToLive(BuildXLReader reader)
        {
            byte timeToLive = reader.ReadByte();
            if (timeToLive == 0)
            {
                // Zero is minimum time to live. Serializing entry with zero time to live is
                // allowed if during serialization purging was disabled (namely due to not having
                // a 'significant' number of entries to purge). See GetUnexpiredEntries
                return 0;
            }

            // Tentatively decrement the TTL for the in-memory table; if the table is saved again without using this entry, 
            // the TTL will stay at this lower value.
            return (byte)(timeToLive - 1);
        }

        private void SerializeCacheEntries(BuildXLWriter writer)
        {
            using (Counters.StartStopwatch(PipCachingCounter.HistoricCacheEntrySerializationDuration))
            {
                foreach (var content in m_newContentEntries)
                {
                    if (content.Value)
                    {
                        m_existingContentEntries.Add(content.Key.GetHashCode());
                    }
                }

                var beginningLength = writer.BaseStream.Length;

                int totalStrongEntries = 0;
                uint totalStrongEntriesWritten = 0;
                int numDuplicates = 0;
                int numInvalid = 0;

                var stack = new Stack<Expirable<PublishedEntry>>();
                var set = new HashSet<Expirable<PublishedEntry>>(new PublishedEntryComparer());

                bool isPurging;
                var unexpiredWeakFingerprints = GetUnexpiredEntries(m_weakFingerprintEntries, isExpired: a => a.Value.All(b => b.TimeToLive == 0), isPurging: out isPurging);
                uint unexpiredCount = (uint)unexpiredWeakFingerprints.Count;

                writer.Write(unexpiredCount);

                foreach (var weakFingerprintEntry in unexpiredWeakFingerprints)
                {
                    WeakContentFingerprint weakFingerprint = weakFingerprintEntry.Key;
                    totalStrongEntries += weakFingerprintEntry.Value.Count;

                    stack.Clear();
                    set.Clear();
                    foreach (var entry in weakFingerprintEntry.Value)
                    {
                        if (stack.Count == TimeToLive)
                        {
                            // Only retain at most DefaultTimeToLive entries for a given weak fingerprint
                            break;
                        }

                        if (isPurging && entry.TimeToLive == 0)
                        {
                            // Skip expired entries when purging
                            continue;
                        }

                        // To remove the duplicates of (strongfingerprint, pathsethash)
                        if (!set.Add(entry))
                        {
                            numDuplicates++;
                        }
                        else if (m_fullFingerprintEntries.TryGetValue(GetFullFingerprint(weakFingerprint, entry.Value.StrongFingerprint), out var metadataHash)
                            && m_existingContentEntries.Contains(entry.Value.PathSetHash.GetHashCode())
                            && m_existingContentEntries.Contains(metadataHash.GetHashCode()))
                        {
                            // Only include entries which were not evicted due to (i) missing metadata hash, (ii) missing pathset or metadata blob in store
                            stack.Push(entry);
                        }
                        else
                        {
                            numInvalid++;
                        }
                    }

                    uint length = (uint)stack.Count;

                    // For some weakfingerprints, there might be no usable strongfingerprints; so the length might equal to zero. We do still serialize
                    // those weakfingerprints; but during deserialization, none record will be created for those. 
                    m_weakFingerprintsToPipSemistableHashMap.TryGetValue(weakFingerprint, out var semistableHash);

                    writer.Write(weakFingerprint);
                    writer.Write(semistableHash);
                    writer.Write(length);
                    totalStrongEntriesWritten += length;

                    // Write the strong fingerprints in the increasing order of TTL because we use stack during deserialization.
                    foreach (var entry in stack)
                    {
                        writer.Write(entry.Value.StrongFingerprint);
                        writer.Write(entry.Value.PathSetHash);

                        var result = m_fullFingerprintEntries.TryGet(GetFullFingerprint(weakFingerprint, entry.Value.StrongFingerprint));
                        Contract.Assert(result.IsFound);
                        ContentHash metadataHash = result.Item.Value;
                        writer.Write(metadataHash);
                        writer.Write(entry.TimeToLive);
                    }
                }

                Counters.AddToCounter(PipCachingCounter.HistoricWeakFingerprintSavedCount, unexpiredCount);
                Counters.AddToCounter(PipCachingCounter.HistoricWeakFingerprintExpiredCount, m_weakFingerprintEntries.Count - unexpiredCount);
                Counters.AddToCounter(PipCachingCounter.HistoricStrongFingerprintSavedCount, totalStrongEntriesWritten);
                Counters.AddToCounter(PipCachingCounter.HistoricStrongFingerprintPurgedCount, totalStrongEntries - totalStrongEntriesWritten);
                Counters.AddToCounter(PipCachingCounter.HistoricStrongFingerprintDuplicatesCount, numDuplicates);
                Counters.AddToCounter(PipCachingCounter.HistoricStrongFingerprintInvalidCount, numInvalid);

                Counters.AddToCounter(PipCachingCounter.HistoricSavedCacheEntriesSizeBytes, writer.BaseStream.Length - beginningLength);
            }
        }

        private void DeserializeCacheEntries(BuildXLReader reader)
        {
            using (Counters.StartStopwatch(PipCachingCounter.HistoricCacheEntryDeserializationDuration))
            {
                int weakFingerprintCount = reader.ReadInt32();
                for (int i = 0; i < weakFingerprintCount; ++i)
                {
                    var weakFingerprint = reader.ReadWeakFingerprint();
                    var semistableHash = reader.ReadInt64();

                    var strongFingerprintCount = reader.ReadInt32();
                    for (int j = 0; j < strongFingerprintCount; ++j)
                    {
                        var strongFingerprint = reader.ReadStrongFingerprint();
                        var pathSetHash = reader.ReadContentHash();
                        var metadataHash = reader.ReadContentHash();
                        var ttl = ReadByteAsTimeToLive(reader);
                        SetMetadataEntry(weakFingerprint, strongFingerprint, pathSetHash, metadataHash, semistableHash, (byte)ttl);
                    }
                }

                Counters.AddToCounter(PipCachingCounter.HistoricWeakFingerprintLoadedCount, m_weakFingerprintEntries.Count);
                Counters.AddToCounter(PipCachingCounter.HistoricStrongFingerprintLoadedCount, m_fullFingerprintEntries.Count);
            }
        }

        private void PrepareBuildManifestColumn(RocksDbStore database)
        {
            if (m_activeContentHashMappingColumnIndex != ComputeContentHashMappingActiveColumnIndex(Age - 1))
            {
                // The active column has changed, need to clean it before its first use.
                database.DropColumnFamily(StoreColumnNames.ContentHashMappingColumn[m_activeContentHashMappingColumnIndex]);
                database.CreateColumnFamily(StoreColumnNames.ContentHashMappingColumn[m_activeContentHashMappingColumnIndex]);
            }
        }

        #endregion

        private KeyValueStoreAccessor OpenStore()
        {
            Contract.Assert(StoreColumnNames.ContentHashMappingColumn.Length == 2);
            Contract.Assert(m_prepareCompletion.Task.IsCompleted, "Attempted to open the store before the loading has finished.");

            var keyTrackedColumns = new string[]
            {
                StoreColumnNames.Content
            };

            var possibleAccessor = KeyValueStoreAccessor.OpenWithVersioning(
                StoreLocation,
                FormatVersion,
                additionalColumns: StoreColumnNames.ContentHashMappingColumn,
                additionalKeyTrackedColumns: keyTrackedColumns,
                failureHandler: (f) => HandleStoreFailure(f.Failure),
                onFailureDeleteExistingStoreAndRetry: true,
                onStoreReset: failure =>
                {
                    Logger.Log.HistoricMetadataCacheCreateFailed(LoggingContext, failure.DescribeIncludingInnerFailures(), willResetAndRetry: true);
                });

            if (possibleAccessor.Succeeded)
            {
                Valid = true;
                return possibleAccessor.Result;
            }

            // If we fail when creating a new store, there will be no historic metadata cache, so log a warning
            Logger.Log.HistoricMetadataCacheCreateFailed(LoggingContext, possibleAccessor.Failure.DescribeIncludingInnerFailures(), willResetAndRetry: false);
            return null;
        }

        private void HandleStoreFailure(Failure failure)
        {
            // Conservatively assume all store failures indicate corrupted data and should not be saved
            Valid = false;
            KeyValueStoreUtilities.CheckAndLogRocksDbException(failure, LoggingContext);
            // If the store fails, future performance can be impacted, so log a warning
            Logger.Log.HistoricMetadataCacheOperationFailed(LoggingContext, failure.DescribeIncludingInnerFailures());

            // Note: All operations using the store are no-ops after the first failure,
            // but if there are concurrent operations that fail, then this may log multiple times.
        }

        /// <summary>
        /// Given the age of the cache, computes the index of the build manifest column that
        /// should be used for storing new build manifest hashes.
        /// 
        /// Essentially, we alternating between two columns every <see cref="ComputedContentHashTimeToLive"/> number of builds.
        /// </summary>
        private static int ComputeContentHashMappingActiveColumnIndex(int age) => age >= 0 ? (age / ComputedContentHashTimeToLive) % 2 : 0;

        private readonly struct StoreColumnNames
        {
            /// <summary>
            /// The content field containing the actual payload. (This stores the
            /// content blobs for metadata and pathsets)
            /// </summary>
            public static string Content = "Content";

            /// <summary>
            /// We use two columns for storing (contentHash, targetHashType) -> contentHash map. This way we don't need to track the TTL of
            /// individual entries; instead, all entries in a column sort of share the same TTL. 
            /// 
            /// On look up, we check both columns. If entry is found only on the non-active column, we copy it to the active one.
            /// Because of that, the active column always contains all entries that were accessed since it became active.
            /// 
            /// On active column change, we clear the new active column. So any entries that have not been accessed (i.e., copied
            /// to the other column) will be evicted.
            /// </summary>
            /// <remark>
            /// Change the column name can logically drop current columns which is bad 
            /// TODO: rename column to be more generic when bump format version in future
            /// </remark>
            public static string[] ContentHashMappingColumn = { "BuildManifestHash_1", "BuildManifestHash_2" };
        }

        private static byte[] StringToBytes(string str)
        {
            return Encoding.UTF8.GetBytes(str);
        }

        private readonly struct StoreKeyNames
        {
            /// <summary>
            /// The key for the cache entries blob
            /// </summary>
            public static readonly byte[] HistoricMetadataCacheEntriesKey = StringToBytes("HistoricMetadataCacheEntriesKeys");

            /// <summary>
            /// The key for the format <see cref="HistoricMetadataCache.FormatVersion"/>
            /// used when building the cache.
            /// </summary>
            public const string FormatVersion = "FormatVersion";

            /// <summary>
            /// The key for the age at which the content was last accessed
            /// </summary>
            public const string Age = "Age";

            /// <summary>
            /// Indicates the last key where garbage collection should resume (if any)
            /// </summary>
            public static readonly byte[] ContentGarbageCollectCursor = StringToBytes(nameof(ContentGarbageCollectCursor));
        }

        private static Expirable<T> NewExpirable<T>(T value, byte timeToLive)
        {
            return new Expirable<T>(value, timeToLive);
        }

        private readonly record struct Expirable<T>
        {
            public readonly T Value;

            public readonly byte TimeToLive;

            public Expirable(T value, byte timeToLive)
            {
                Value = value;
                TimeToLive = timeToLive;
            }
        }

        private class PublishedEntryComparer : IEqualityComparer<Expirable<PublishedEntry>>
        {
            public bool Equals(Expirable<PublishedEntry> x, Expirable<PublishedEntry> y)
            {
                // TTL is not a part of equality
                return x.Value.StrongFingerprint == y.Value.StrongFingerprint &&
                    x.Value.PathSetHash == y.Value.PathSetHash;
            }

            public int GetHashCode(Expirable<PublishedEntry> expirable)
            {
                unchecked
                {
                    return (expirable.Value.StrongFingerprint.GetHashCode() * 31) ^ expirable.Value.PathSetHash.GetHashCode();
                }
            }
        }

        private struct PublishedEntry
        {
            public readonly StrongContentFingerprint StrongFingerprint;

            public readonly ContentHash PathSetHash;

            public PublishedEntry(StrongContentFingerprint strongFingerprint, ContentHash pathSetHash)
            {
                StrongFingerprint = strongFingerprint;
                PathSetHash = pathSetHash;
            }
        }
    }

    /// <summary>
    /// Utilities for persisting/retrieving historic metadata cache Data to/from cache
    /// </summary>
    public static class HistoricMetadataCacheUtilities
    {
        /// <summary>
        /// The version for lookups of historic metadata cache
        /// </summary>
        public const int HistoricMetadataCacheLookupVersion = 1;

        /// <summary>
        /// Computes a fingerprint for looking up historic metadata cache
        /// </summary>
        private static ContentFingerprint ComputeFingerprint(
            LoggingContext loggingContext,
            PathTable pathTable,
            IConfiguration configuration,
            ContentFingerprint performanceDataFingerprint)
        {
            var extraFingerprintSalt = new ExtraFingerprintSalts(
                configuration,
                fingerprintSalt: EngineEnvironmentSettings.DebugHistoricMetadataCacheFingerprintSalt,
                searchPathToolsHash: null);

            using (var hasher = new HashingHelper(pathTable, recordFingerprintString: false))
            {
                hasher.Add("Type", "HistoricMetadataCacheFingerprint");
                hasher.Add("FormatVersion", HistoricMetadataCache.FormatVersion);
                hasher.Add("LookupVersion", HistoricMetadataCacheLookupVersion);
                hasher.Add("PerformanceDataFingerprint", performanceDataFingerprint.Hash);
                hasher.Add("ExtraFingerprintSalt", extraFingerprintSalt.CalculatedSaltsFingerprint);

                var fingerprint = new ContentFingerprint(hasher.GenerateHash());
                Logger.Log.HistoricMetadataCacheTrace(loggingContext, I($"Computed historic metadata cache fingerprint: {fingerprint}. Salt: '{EngineEnvironmentSettings.DebugHistoricMetadataCacheFingerprintSalt.Value}'"));
                return fingerprint;
            }
        }

        /// <summary>
        /// Store the historic metadata cache file to the cache
        /// </summary>
        public static async Task<Possible<long>> TryStoreHistoricMetadataCacheAsync(
            this EngineCache cache,
            LoggingContext loggingContext,
            string path,
            PathTable pathTable,
            IConfiguration configuration,
            ContentFingerprint performanceDataFingerprint)
        {
            var fingerprint = ComputeFingerprint(loggingContext, pathTable, configuration, performanceDataFingerprint);
            var absolutePath = AbsolutePath.Create(pathTable, path);

            BoxRef<long> size = 0;

            SemaphoreSlim concurrencyLimiter = new SemaphoreSlim(8);
            var storedFiles = await Task.WhenAll(Directory.EnumerateFiles(path).Select(async file =>
            {
                await Task.Yield();
                using (await concurrencyLimiter.AcquireAsync())
                {
                    var filePath = AbsolutePath.Create(pathTable, file);
                    var storeResult = await cache.ArtifactContentCache.TryStoreAsync(
                        FileRealizationMode.Copy,
                        filePath.Expand(pathTable));

                    if (storeResult.Succeeded)
                    {
                        Interlocked.Add(ref size.Value, new FileInfo(file).Length);
                    }

                    return storeResult.Then(result => new StringKeyedHash()
                    {
                        Key = absolutePath.ExpandRelative(pathTable, filePath),
                        ContentHash = result.ToByteString()
                    });
                }
            }).ToList());

            var failure = storedFiles.Where(p => !p.Succeeded).Select(p => p.Failure).FirstOrDefault();
            Logger.Log.HistoricMetadataCacheTrace(loggingContext, I($"Storing historic metadata cache to cache: Success='{failure == null}', FileCount={storedFiles.Length} Size={size.Value}"));
            if (failure != null)
            {
                return failure;
            }

            PackageDownloadDescriptor descriptor = new PackageDownloadDescriptor()
            {
                TraceInfo = loggingContext.Session.Environment,
                FriendlyName = nameof(HistoricMetadataCache)
            };
            descriptor.Contents.Add(storedFiles.Select(p => p.Result).ToList());

            var storeDescriptorResult = await cache.ArtifactContentCache.TrySerializeAndStoreContent(descriptor);
            Logger.Log.HistoricMetadataCacheTrace(loggingContext, I($"Storing historic metadata cache descriptor to cache: Success='{storeDescriptorResult.Succeeded}'"));
            if (!storeDescriptorResult.Succeeded)
            {
                return storeDescriptorResult.Failure;
            }

            var associatedFileHashes = descriptor.Contents.Select(s => s.ContentHash.ToContentHash()).ToArray().ToReadOnlyArray().GetSubView(0);
            var cacheEntry = new CacheEntry(storeDescriptorResult.Result, null, associatedFileHashes);

            var publishResult = await cache.TwoPhaseFingerprintStore.TryPublishTemporalCacheEntryAsync(loggingContext, fingerprint, cacheEntry);
            Logger.Log.HistoricMetadataCacheTrace(loggingContext, I($"Publishing historic metadata cache to cache: Fingerprint='{fingerprint}' Hash={storeDescriptorResult.Result}"));
            return size.Value;
        }

        /// <summary>
        /// Retrieve the running time table from the cache
        /// </summary>
        public static async Task<Possible<bool>> TryRetrieveHistoricMetadataCacheAsync(
            this EngineCache cache,
            LoggingContext loggingContext,
            string path,
            PathTable pathTable,
            IConfiguration configuration,
            ContentFingerprint performanceDataFingerprint,
            CancellationToken cancellationToken)
        {
            var fingerprint = ComputeFingerprint(loggingContext, pathTable, configuration, performanceDataFingerprint);
            var possibleCacheEntry = await cache.TwoPhaseFingerprintStore.TryGetLatestCacheEntryAsync(loggingContext, fingerprint);
            if (!possibleCacheEntry.Succeeded)
            {
                Logger.Log.HistoricMetadataCacheTrace(
                    loggingContext,
                    I($"Failed loading historic metadata cache entry from cache: Failure:{possibleCacheEntry.Failure.DescribeIncludingInnerFailures()}"));
                return possibleCacheEntry.Failure;
            }

            Logger.Log.HistoricMetadataCacheTrace(
                loggingContext,
                I($"Loaded historic metadata cache entry from cache: Fingerprint='{fingerprint}' MetadataHash={possibleCacheEntry.Result?.MetadataHash ?? ContentHashingUtilities.ZeroHash}"));

            if (!possibleCacheEntry.Result.HasValue)
            {
                return false;
            }

            var historicMetadataCacheDescriptorHash = possibleCacheEntry.Result.Value.MetadataHash;

            var absolutePath = AbsolutePath.Create(pathTable, path);

            var maybePinned = await cache.ArtifactContentCache.TryLoadAvailableContentAsync(possibleCacheEntry.Result.Value.ToArray(), cancellationToken);

            var result = await maybePinned.ThenAsync<Unit>(
                async pinResult =>
                {
                    if (!pinResult.AllContentAvailable)
                    {
                        return new Failure<string>(I($"Could not pin content for historic metadata cache '{string.Join(", ", pinResult.Results.Where(r => !r.IsAvailable).Select(r => r.Hash))}'"));
                    }

                    var maybeLoadedDescriptor = await cache.ArtifactContentCache.TryLoadAndDeserializeContent<PackageDownloadDescriptor>(historicMetadataCacheDescriptorHash, cancellationToken);
                    if (!maybeLoadedDescriptor.Succeeded)
                    {
                        return maybeLoadedDescriptor.Failure;
                    }

                    Logger.Log.HistoricMetadataCacheTrace(loggingContext, I($"Loaded historic metadata cache descriptor from cache: Hash='{historicMetadataCacheDescriptorHash}'"));

                    PackageDownloadDescriptor descriptor = maybeLoadedDescriptor.Result;

                    SemaphoreSlim concurrencyLimiter = new SemaphoreSlim(8);
                    var materializedFiles = await Task.WhenAll(descriptor.Contents.Select(async subPathKeyedHash =>
                    {
                        await Task.Yield();
                        using (await concurrencyLimiter.AcquireAsync())
                        {
                            var filePath = absolutePath.Combine(pathTable, subPathKeyedHash.Key);
                            var maybeMaterialized = await cache.ArtifactContentCache.TryMaterializeAsync(
                                FileRealizationMode.Copy,
                                filePath.Expand(pathTable),
                                subPathKeyedHash.ContentHash.ToContentHash(),
                                cancellationToken);

                            return maybeMaterialized;
                        }
                    }).ToList());

                    var failure = materializedFiles.Where(p => !p.Succeeded).Select(p => p.Failure).FirstOrDefault();
                    if (failure != null)
                    {
                        return failure;
                    }
                    return Unit.Void;
                });

            if (!result.Succeeded)
            {
                Logger.Log.HistoricMetadataCacheTrace(
                    loggingContext,
                    I($"Failed loading historic metadata cache from cache: Failure:{result.Failure.DescribeIncludingInnerFailures()}"));
                return result.Failure;
            }

            Logger.Log.HistoricMetadataCacheTrace(loggingContext, I($"Loaded historic metadata cache from cache: Path='{path}'"));

            return true;
        }
    }
}
