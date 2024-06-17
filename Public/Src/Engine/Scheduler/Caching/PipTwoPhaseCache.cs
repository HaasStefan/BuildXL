// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Engine.Cache;
using BuildXL.Engine.Cache.Artifacts;
using BuildXL.Engine.Cache.Fingerprints;
using BuildXL.Engine.Cache.Fingerprints.TwoPhase;
using BuildXL.Pips;
using BuildXL.Pips.Operations;
using BuildXL.Scheduler.Fingerprints;
using BuildXL.Storage;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Core.Tasks;
using BuildXL.Utilities.Tracing;
using static BuildXL.Utilities.Core.FormattableStringEx;
using static BuildXL.Tracing.Diagnostics;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;

namespace BuildXL.Scheduler.Cache
{
    /// <summary>
    /// Wraps <see cref="ITwoPhaseFingerprintStore"/> and <see cref="IArtifactContentCache"/> to add higher level methods for
    /// performing two phase lookup for pips (i.e. directly query for deserialized metadata given content hash rather than having consumer
    /// perform deserialization). This allows for greater customization/optimization of the two phase lookup process by derived types.
    /// </summary>
    public class PipTwoPhaseCache
    {
        /// <summary>
        /// The artifact content cache for retrieving/storing content by hash
        /// </summary>
        protected readonly IArtifactContentCache ArtifactContentCache;

        /// <summary>
        /// The two phase fingerprint store for storing/retrieving mapping from fingerprints to content/metadata
        /// </summary>
        protected readonly ITwoPhaseFingerprintStore TwoPhaseFingerprintStore;

        /// <summary>
        /// PathTable
        /// </summary>
        protected PathTable PathTable => Context.PathTable;

        /// <summary>
        /// The counters for measuring caching statistics
        /// </summary>
        public readonly CounterCollection<PipCachingCounter> Counters;

        /// <summary>
        /// Logging context.
        /// </summary>
        protected readonly LoggingContext LoggingContext;

        /// <summary>
        /// BuildXL execution context.
        /// </summary>
        protected readonly PipExecutionContext Context;

        private readonly PathExpander m_pathExpander;

        /// <nodoc />
        public PipTwoPhaseCache(LoggingContext loggingContext, EngineCache cache, PipExecutionContext context, PathExpander pathExpander)
        {
            Contract.Requires(loggingContext != null);
            Contract.Requires(cache != null);
            Contract.Requires(context != null);

            LoggingContext = loggingContext;
            ArtifactContentCache = cache.ArtifactContentCache;
            TwoPhaseFingerprintStore = cache.TwoPhaseFingerprintStore;
            Context = context;
            Counters = new CounterCollection<PipCachingCounter>();
            m_pathExpander = pathExpander;
        }

        /// <summary>
        /// Attempts to load state need for use of the cache.
        /// </summary>
        public virtual void StartLoading(bool waitForCompletion)
        {
        }

        /// <summary>
        /// Stores cache descriptor metadata for pip
        /// </summary>
        public virtual async Task<Possible<ContentHash>> TryStoreMetadataAsync(PipCacheDescriptorV2Metadata metadata)
        {
            BoxRef<long> metadataSize = new BoxRef<long>();
            var result = await ArtifactContentCache.TrySerializeAndStoreContent(metadata.ToEntry(), metadataSize, StoreArtifactOptions.CacheEntryContent);

            if (result.Succeeded)
            {
                Counters.IncrementCounter(PipCachingCounter.StoredMetadataCount);
                Counters.AddToCounter(PipCachingCounter.StoredMetadataSize, metadataSize.Value);
            }

            return result;
        }

        /// <summary>
        /// Stores path set for pip for use during two phase lookup
        /// </summary>
        public virtual Task<Possible<ContentHash>> TryStorePathSetAsync(ObservedPathSet pathSet, bool preservePathCasing)
        {
            return TrySerializedAndStorePathSetAsync(pathSet, (pathSetHash, pathSetBuffer) =>
            {
                return TryStorePathSetContentAsync(pathSetHash, pathSetBuffer);
            }, preservePathCasing);
        }

        /// <summary>
        /// Stores the path set content to the backing store
        /// </summary>
        protected virtual async Task<Possible<Unit>> TryStorePathSetContentAsync(ContentHash pathSetHash, MemoryStream pathSetBuffer)
        {
            Counters.IncrementCounter(PipCachingCounter.StoredPathSetCount);
            Counters.AddToCounter(PipCachingCounter.StoredPathSetSize, pathSetBuffer.Length);

            if (!EngineEnvironmentSettings.SkipExtraneousPins)
            {
                // First check to see if the content is already available in the cache since that's a faster noop path
                // than storing an already existing PathSet
                var result = await ArtifactContentCache.TryLoadAvailableContentAsync(new[] { pathSetHash }, Context.CancellationToken);
                if (result.Succeeded && result.Result.AllContentAvailable)
                {
                    return Unit.Void;
                }
            }

            return (await ArtifactContentCache.TryStoreAsync(pathSetBuffer, pathSetHash, StoreArtifactOptions.CacheEntryContent)).WithGenericFailure();
        }

        /// <summary>
        /// Serializes a path set.
        /// </summary>
        public async Task<ContentHash> SerializePathSetAsync(ObservedPathSet pathSet, bool preservePathCasing)
        {
            using (var pathSetBuffer = new MemoryStream())
            {
                return await SerializePathSetAsync(pathSet, pathSetBuffer, preservePathCasing);
            }
        }

        /// <summary>
        /// Serializes a path set to the given buffer.
        /// </summary>
        protected async Task<ContentHash> SerializePathSetAsync(ObservedPathSet pathSet, MemoryStream pathSetBuffer, bool preservePathCasing, ContentHash? pathSetHash = null)
        {
            using (var writer = new BuildXLWriter(stream: pathSetBuffer, debug: false, leaveOpen: true, logStats: false))
            {
                pathSet.Serialize(PathTable, writer, preservePathCasing, m_pathExpander);

                if (pathSetHash == null)
                {
                    pathSetBuffer.Position = 0;
                    pathSetHash = await ContentHashingUtilities.HashContentStreamAsync(pathSetBuffer);
                }

                pathSetBuffer.Position = 0;

                return pathSetHash.Value;
            }
        }

        /// <summary>
        /// Stores content for the given path set using the given store function
        /// </summary>
        protected async Task<Possible<ContentHash>> TrySerializedAndStorePathSetAsync(
            ObservedPathSet pathSet,
            Func<ContentHash, MemoryStream, Task<Possible<Unit>>> storeAsync,
            bool preservePathCasing,
            ContentHash? pathSetHash = null)
        {
            using (var pooledPathSetBuffer = Pools.MemoryStreamPool.GetInstance())
            {
                var pathSetBuffer = pooledPathSetBuffer.Instance;
                var hash = await SerializePathSetAsync(pathSet, pathSetBuffer, preservePathCasing, pathSetHash);
                var maybeStored = await storeAsync(hash, pathSetBuffer);
                if (!maybeStored.Succeeded)
                {
                    return maybeStored.Failure;
                }

                return hash;
            }
        }

        /// <summary>
        /// Gets the cache descriptor metadata given its content hash
        /// </summary>
        /// <returns>the metadata, or a Failure{<see cref="PipFingerprintEntry"/>} if metadata was retrieved
        /// but was a different kind, null if content was not available, or standard <see cref="Failure"/></returns>
        public virtual async Task<Possible<PipCacheDescriptorV2Metadata>> TryRetrieveMetadataAsync(
            Pip pip,
            // TODO: Do we need these fingerprints given that the metadata hash is provided by this interface in the first place
            WeakContentFingerprint weakFingerprint,
            StrongContentFingerprint strongFingerprint,
            ContentHash metadataHash,
            ContentHash pathSetHash)
        {
            BoxRef<long> metadataSize = new BoxRef<long>();
            Possible<PipFingerprintEntry> maybeMetadata =
                await ArtifactContentCache.TryLoadAndDeserializeContentWithRetry<PipFingerprintEntry>(
                    LoggingContext,
                    metadataHash,
                    contentSize: metadataSize,
                    cancellationToken: Context.CancellationToken,
                    shouldRetry: possibleResult => !possibleResult.Succeeded || (possibleResult.Result != null && possibleResult.Result.IsCorrupted),
                    maxRetry: PipFingerprintEntry.LoadingAndDeserializingRetries);

            if (!maybeMetadata.Succeeded)
            {
                return maybeMetadata.Failure;
            }

            Counters.IncrementCounter(PipCachingCounter.LoadedMetadataCount);
            Counters.AddToCounter(PipCachingCounter.LoadedMetadataSize, metadataSize.Value);

            var metadataEntry = maybeMetadata.Result;
            if (metadataEntry == null)
            {
                return (PipCacheDescriptorV2Metadata)null;
            }

            if (metadataEntry.Kind != PipFingerprintEntryKind.DescriptorV2)
            {
                // Metadata is incorrect kind.
                var message = I($"Expected metadata kind is '{nameof(PipFingerprintEntryKind.DescriptorV2)}' but got '{metadataEntry.Kind}'");
                return new Failure<PipFingerprintEntry>(metadataEntry, new Failure<string>(message));
            }

            return (PipCacheDescriptorV2Metadata)metadataEntry.Deserialize(
                Context.CancellationToken,
                new CacheQueryData
                {
                    WeakContentFingerprint = weakFingerprint,
                    PathSetHash = pathSetHash,
                    StrongContentFingerprint = strongFingerprint,
                    MetadataHash = metadataHash,
                    ContentCache = ArtifactContentCache
                });
        }

        /// <summary>
        /// Gets the deserialized path set given its content hash
        /// </summary>
        public virtual async Task<Possible<ObservedPathSet>> TryRetrievePathSetAsync(
            OperationContext operationContext,
            // TODO: Do we need this fingerprint given that the path set hash is provided by this interface in the first place
            WeakContentFingerprint weakFingerprint,
            ContentHash pathSetHash,
            bool avoidRemoteLookups = false)
        {
            using (operationContext.StartOperation(PipExecutorCounter.TryLoadPathSetFromContentCacheDuration))
            {
                Possible<StreamWithLength> maybePathSetStream = await TryLoadAndOpenPathSetStreamAsync(pathSetHash, avoidRemoteLookups);
                if (!maybePathSetStream.Succeeded)
                {
                    return maybePathSetStream.Failure;
                }

                using (operationContext.StartOperation(PipExecutorCounter.TryLoadPathSetFromContentCacheDeserializeDuration))
                using (var pathSetReader = new BuildXLReader(debug: false, stream: maybePathSetStream.Result, leaveOpen: false))
                {
                    var maybeDeserialized = ObservedPathSet.TryDeserialize(PathTable, pathSetReader, m_pathExpander);
                    if (!maybeDeserialized.Succeeded)
                    {
                        return maybeDeserialized.Failure;
                    }

                    return maybeDeserialized.Result;
                }
            }
        }

        /// <summary>
        /// Loads the path set stream
        /// </summary>
        protected virtual async Task<Possible<StreamWithLength>> TryLoadAndOpenPathSetStreamAsync(ContentHash pathSetHash, bool avoidRemoteLookups = false)
        {
            Possible<StreamWithLength> maybePathSetStream = await TryLoadContentAndOpenStreamAsync(pathSetHash, avoidRemoteLookups);
            if (!maybePathSetStream.Succeeded)
            {
                return maybePathSetStream.Failure;
            }

            Counters.IncrementCounter(PipCachingCounter.LoadedPathSetCount);
            Counters.AddToCounter(PipCachingCounter.LoadedPathSetSize, maybePathSetStream.Result.Length);

            return maybePathSetStream;
        }

        /// <summary>
        /// Called at completion of build to allow finalization of state
        /// </summary>
        public virtual Task CloseAsync()
        {
            return Unit.VoidTask;
        }

        /// <summary>
        /// Combined load+open. This assumes that the named content is probably available (e.g. named in a cache entry)
        /// and so soft misses are promoted to failures.
        /// </summary>
        protected virtual async Task<Possible<StreamWithLength>> TryLoadContentAndOpenStreamAsync(ContentHash contentHash, bool avoidRemoteLookups)
        {
            if (!EngineEnvironmentSettings.SkipExtraneousPins)
            {
                Possible<ContentAvailabilityBatchResult> maybeAvailable =
                    await ArtifactContentCache.TryLoadAvailableContentAsync(new[] { contentHash }, Context.CancellationToken, new() { AvoidRemote = avoidRemoteLookups });
                if (!maybeAvailable.Succeeded)
                {
                    return maybeAvailable.Failure;
                }

                bool contentIsAvailable = maybeAvailable.Result.AllContentAvailable;
                if (!contentIsAvailable)
                {
                    return new Failure<string>("Required content is not available in the cache");
                }
            }

            return await ArtifactContentCache.TryOpenContentStreamAsync(contentHash);
        }

        /// <summary>
        /// See <see cref="ITwoPhaseFingerprintStore.ListPublishedEntriesByWeakFingerprint"/>
        /// </summary>
        public virtual IEnumerable<Task<Possible<PublishedEntryRef, Failure>>> ListPublishedEntriesByWeakFingerprint(OperationContext operationContext, WeakContentFingerprint weak, OperationHints hints)
        {
            IEnumerator<Task<Possible<PublishedEntryRef, Failure>>> enumerator;

            using (operationContext.StartOperation(PipExecutorCounter.CacheQueryingWeakFingerprintDuration))
            {
                enumerator = TwoPhaseFingerprintStore.ListPublishedEntriesByWeakFingerprint(weak, hints).GetEnumerator();
            }

            while (true)
            {
                Task<Possible<PublishedEntryRef, Failure>> current;
                using (operationContext.StartOperation(PipExecutorCounter.CacheQueryingWeakFingerprintDuration))
                {
                    if (enumerator.MoveNext())
                    {
                        current = enumerator.Current;
                    }
                    else
                    {
                        break;
                    }
                }

                yield return current;
            }
        }

        /// <summary>
        /// See <see cref="ITwoPhaseFingerprintStore.TryGetCacheEntryAsync"/>
        /// </summary>
        public virtual async Task<Possible<CacheEntry?, Failure>> TryGetCacheEntryAsync(
            Pip pip,
            WeakContentFingerprint weakFingerprint,
            ContentHash pathSetHash,
            StrongContentFingerprint strongFingerprint,
            OperationHints hints)
        {
            var result = await TwoPhaseFingerprintStore.TryGetCacheEntryAsync(weakFingerprint, pathSetHash, strongFingerprint, hints);

            if (result.Succeeded &&
                ETWLogger.Log.IsEnabled(EventLevel.Verbose, Keywords.Diagnostics))
            {
                Tracing.Logger.Log.PipTwoPhaseCacheGetCacheEntry(
                    LoggingContext,
                    pip.GetDescription(Context),
                    weakFingerprint.ToString(),
                    pathSetHash.ToString(),
                    strongFingerprint.ToString(),
                    result.Result.HasValue ? result.Result.Value.MetadataHash.ToString() : "<NOVALUE>");
            }

            return result;
        }

        /// <summary>
        /// See <see cref="ITwoPhaseFingerprintStore.TryPublishCacheEntryAsync"/>
        /// </summary>
        public virtual async Task<Possible<CacheEntryPublishResult, Failure>> TryPublishCacheEntryAsync(
            Pip pip,
            WeakContentFingerprint weakFingerprint,
            ContentHash pathSetHash,
            StrongContentFingerprint strongFingerprint,
            CacheEntry entry,
            CacheEntryPublishMode mode = CacheEntryPublishMode.CreateNew)
        {
            Contract.Requires(pathSetHash.IsValid);
            Contract.Requires(entry.MetadataHash.IsValid);

            var result = await TwoPhaseFingerprintStore.TryPublishCacheEntryAsync(
                weakFingerprint,
                pathSetHash,
                strongFingerprint,
                entry,
                mode,
                PublishCacheEntryOptions.PublishAssociatedContent);

            if (result.Succeeded)
            {
                var publishedEntry = result.Result.Status == CacheEntryPublishStatus.Published ? entry : result.Result.ConflictingEntry;

                if (ETWLogger.Log.IsEnabled(EventLevel.Verbose, Keywords.Diagnostics))
                {
                    Tracing.Logger.Log.PipTwoPhaseCachePublishCacheEntry(
                        LoggingContext,
                        pip.GetDescription(Context),
                        weakFingerprint.ToString(),
                        pathSetHash.ToString(),
                        strongFingerprint.ToString(),
                        entry.MetadataHash.ToString(),
                        result.Result.Status.ToString(),
                        publishedEntry.MetadataHash.ToString());
                }
            }

            return result;
        }

        /// <summary>
        /// Report metadata from workers
        /// </summary>
        public virtual void ReportRemoteMetadata(
            PipCacheDescriptorV2Metadata metadata,
            ContentHash? metadataHash,
            ContentHash? pathSetHash,
            WeakContentFingerprint? weakFingerprint,
            StrongContentFingerprint? strongFingerprint,
            bool isExecution,
            bool preservePathCasing)
        {
        }

        /// <summary>
        /// Report pathset from workers
        /// </summary>
        public virtual void ReportRemotePathSet(
            ObservedPathSet? pathSet,
            ContentHash? pathSetHash,
            bool isExecution,
            bool preservePathCasing)
        {
        }

        /// <summary>
        /// Attempts to get path sets associated with a pip
        /// </summary>
        public virtual IEnumerable<Task<Possible<ObservedPathSet>>> TryGetAssociatedPathSetsAsync(OperationContext context, Pip pip)
        {
            yield break;
        }

        /// <summary>
        /// Contains the hash for the pathset or metadata
        /// </summary>
        public virtual bool IsNewlyAdded(ContentHash hash) => true;

        /// <summary>
        /// Gets whether the associated content for the entry has a strong availability guarantee
        /// </summary>
        public virtual bool HasStrongOutputContentAvailabilityGuarantee(ContentHash metadataHash) => false;

        /// <summary>
        /// Registers the result of materializing the output content for the given cache metadata
        /// </summary>
        public virtual void RegisterOutputContentMaterializationResult(StrongContentFingerprint strongFingerprint, ContentHash metadataHash, bool succeeded)
        {
        }

        /// <summary>
        /// Tries to store the hash-to-hash mapping into cache
        /// </summary>
        /// <remarks>
        /// The operation is only applicable to <see cref="PipTwoPhaseCacheWithHashLookup"/>.
        /// </remarks>
        public virtual void TryStoreRemappedContentHash(ContentHash contentHash, ContentHash remappedContentHash)
        {
        }

        /// <summary>
        /// Given a hash, tries to get a corresponding mapped hash from cache. If the hash cannot be find, returns an invalid ContentHash.
        /// </summary>
        /// <remarks>
        /// The operation is only applicable to <see cref="PipTwoPhaseCacheWithHashLookup"/>.
        /// </remarks>
        public virtual ContentHash TryGetMappedContentHash(ContentHash contentHash, HashType hashType) => default;
    }
}
