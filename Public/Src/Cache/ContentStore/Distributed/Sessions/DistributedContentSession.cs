// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.Stores;
using BuildXL.Cache.ContentStore.Distributed.Utilities;
using BuildXL.Cache.ContentStore.Extensions;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Distributed;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Interfaces.Utils;
using BuildXL.Cache.ContentStore.Service.Grpc;
using BuildXL.Cache.ContentStore.Sessions;
using BuildXL.Cache.ContentStore.Sessions.Internal;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Core.Tasks;
using BuildXL.Utilities.ParallelAlgorithms;
using ContentStore.Grpc;
using AbsolutePath = BuildXL.Cache.ContentStore.Interfaces.FileSystem.AbsolutePath;
using ResultsExtensions = BuildXL.Cache.ContentStore.Interfaces.Results.ResultsExtensions;

namespace BuildXL.Cache.ContentStore.Distributed.Sessions
{
    public class DistributedContentSession : ContentSessionBase, IContentSession, IHibernateContentSession, ILocalContentSessionProvider
    {
        internal enum Counters
        {
            GetLocationsSatisfiedFromLocal,
            GetLocationsSatisfiedFromRemote,
            PinUnverifiedCountSatisfied,
            StartCopyForPinWhenUnverifiedCountSatisfied,
            ProactiveCopiesSkipped,
            ProactiveCopy_OutsideRingFromPreferredLocations,
            ProactiveCopy_OutsideRingCopies,
            ProactiveCopyRetries,
            ProactiveCopyInsideRingRetries,
            ProactiveCopyOutsideRingRetries,
            ProactiveCopy_InsideRingCopies,
            ProactiveCopy_InsideRingFullyReplicated,
        }

        internal CounterCollection<Counters> SessionCounters { get; } = new CounterCollection<Counters>();

        private string _buildId = null;
        private ContentHash? _buildIdHash = null;
        private readonly ExpiringValue<MachineLocation[]> _buildRingMachinesCache;

        private MachineLocation[] BuildRingMachines => _buildRingMachinesCache.GetValueOrDefault() ?? Array.Empty<MachineLocation>();

        private readonly ConcurrentBigSet<ContentHash> _pendingProactivePuts = new ConcurrentBigSet<ContentHash>();
        private ResultNagleQueue<ContentHash, ContentHashWithSizeAndLocations> _proactiveCopyGetBulkNagleQueue;

        // The method used for remote pins depends on which pin configuration is enabled.
        private readonly RemotePinAsync _remotePinner;

        /// <summary>
        /// The store that persists content locations to a persistent store.
        /// </summary>
        internal readonly IContentLocationStore ContentLocationStore;

        /// <summary>
        /// The machine location for the current cache.
        /// </summary>
        protected readonly MachineLocation LocalCacheRootMachineLocation;

        /// <summary>
        /// The content session that actually stores content.
        /// </summary>
        public IContentSession Inner { get; }

        /// <inheritdoc />
        protected override Tracer Tracer { get; } = new Tracer(nameof(DistributedContentSession));

        /// <nodoc />
        protected readonly DistributedContentCopier DistributedCopier;

        /// <summary>
        /// Settings for the session.
        /// </summary>
        protected readonly DistributedContentStoreSettings Settings;

        /// <summary>
        /// Trace only stops and errors to reduce the Kusto traffic.
        /// </summary>
        protected override bool TraceOperationStarted => false;

        /// <summary>
        /// Semaphore that limits the maximum number of concurrent put and place operations
        /// </summary>
        protected readonly SemaphoreSlim PutAndPlaceFileGate;

        private readonly DistributedContentStore _contentStore;

        /// <nodoc />
        public DistributedContentSession(
            string name,
            IContentSession inner,
            IContentLocationStore contentLocationStore,
            DistributedContentCopier contentCopier,
            DistributedContentStore distributedContentStore,
            MachineLocation localMachineLocation,
            DistributedContentStoreSettings settings = default)
            : base(name)
        {
            Contract.Requires(name != null);
            Contract.Requires(inner != null);
            Contract.Requires(contentLocationStore != null);
            Contract.Requires(contentLocationStore is StartupShutdownSlimBase, "The store must derive from StartupShutdownSlimBase");
            Contract.Requires(localMachineLocation.IsValid);

            Inner = inner;
            ContentLocationStore = contentLocationStore;
            LocalCacheRootMachineLocation = localMachineLocation;
            Settings = settings ?? DistributedContentStoreSettings.DefaultSettings;
            _contentStore = distributedContentStore;
            _remotePinner = PinFromMultiLevelContentLocationStore;
            DistributedCopier = contentCopier;
            PutAndPlaceFileGate = new SemaphoreSlim(Settings.MaximumConcurrentPutAndPlaceFileOperations);

            _buildRingMachinesCache = new ExpiringValue<MachineLocation[]>(
                Settings.ProactiveCopyInRingMachineLocationsExpiryCache,
                SystemClock.Instance,
                originalValue: Array.Empty<MachineLocation>());
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> StartupCoreAsync(OperationContext context)
        {
            var canHibernate = Inner is IHibernateContentSession ? "can" : "cannot";
            Tracer.Debug(context, $"Session {Name} {canHibernate} hibernate");
            await Inner.StartupAsync(context).ThrowIfFailure();

            _proactiveCopyGetBulkNagleQueue = new ResultNagleQueue<ContentHash, ContentHashWithSizeAndLocations>(
                execute: hashes => GetLocationsForProactiveCopyAsync(context.CreateNested(Tracer.Name), hashes),
                maxDegreeOfParallelism: 1,
                interval: Settings.ProactiveCopyGetBulkInterval,
                batchSize: Settings.ProactiveCopyGetBulkBatchSize);
            _proactiveCopyGetBulkNagleQueue.Start();

            TryRegisterMachineWithBuildId(context);

            return BoolResult.Success;
        }

        private void TryRegisterMachineWithBuildId(OperationContext context)
        {
            if (Constants.TryExtractBuildId(Name, out _buildId) && Guid.TryParse(_buildId, out var buildIdGuid))
            {
                // Generate a fake hash for the build and register a content entry in the location store to represent
                // machines in the build ring
                var buildIdContent = buildIdGuid.ToByteArray();

                var buildIdHash = GetBuildIdHash(buildIdContent);

                var arguments = $"Build={_buildId}, BuildIdHash={buildIdHash.ToShortString()}";

                context.PerformOperationAsync(
                    Tracer,
                    async () =>
                    {
                        // Storing the build id in the cache to prevent this data to be removed during reconciliation.
                        using var stream = new MemoryStream(buildIdContent);
                        var result = await Inner.PutStreamAsync(context, buildIdHash, stream, CancellationToken.None, UrgencyHint.Nominal);
                        result.ThrowIfFailure();

                        // Even if 'StoreBuildIdInCache' is true we need to register the content manually,
                        // because Inner.PutStreamAsync will just add the content to the cache but won't send a registration message.
                        await ContentLocationStore.RegisterLocalLocationAsync(
                            context,
                            new[] { new ContentHashWithSize(buildIdHash, buildIdContent.Length) },
                            context.Token,
                            UrgencyHint.Nominal).ThrowIfFailure();

                        // Updating the build id field only if the registration or the put operation succeeded.
                        _buildIdHash = buildIdHash;

                        return BoolResult.Success;
                    },
                    extraStartMessage: arguments,
                    extraEndMessage: r => arguments).FireAndForget(context);
            }
        }

        private static ContentHash GetBuildIdHash(byte[] buildId) => buildId.CalculateHash(HashType.MD5);

        /// <inheritdoc />
        protected override async Task<BoolResult> ShutdownCoreAsync(OperationContext context)
        {
            var counterSet = new CounterSet();
            counterSet.Merge(GetCounters(), $"{Tracer.Name}.");

            // Unregister from build machine location set
            if (_buildIdHash.HasValue)
            {
                Guid.TryParse(_buildId, out var buildIdGuid);
                var buildIdHash = GetBuildIdHash(buildIdGuid.ToByteArray());
                Tracer.Debug(context, $"Deleting in-ring mapping from cache: Build={_buildId}, BuildIdHash={buildIdHash.ToShortString()}");

                // DeleteAsync will unregister the content as well. No need for calling 'TrimBulkAsync'.
                await TaskUtilities.IgnoreErrorsAndReturnCompletion(
                    _contentStore.DeleteAsync(context, _buildIdHash.Value, new DeleteContentOptions() { DeleteLocalOnly = true }));
            }

            await Inner.ShutdownAsync(context).ThrowIfFailure();

            _proactiveCopyGetBulkNagleQueue?.Dispose();
            Tracer.TraceStatisticsAtShutdown(context, counterSet, prefix: "DistributedContentSessionStats");

            return BoolResult.Success;
        }

        /// <inheritdoc />
        protected override void DisposeCore()
        {
            base.DisposeCore();

            Inner.Dispose();
        }

        /// <inheritdoc />
        protected override async Task<PinResult> PinCoreAsync(
            OperationContext operationContext,
            ContentHash contentHash,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            // Call bulk API
            var result = await PinHelperAsync(operationContext, new[] { contentHash }, urgencyHint, PinOperationConfiguration.Default());
            return (await result.First()).Item;
        }

        /// <inheritdoc />
        protected override async Task<OpenStreamResult> OpenStreamCoreAsync(
            OperationContext operationContext,
            ContentHash contentHash,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            OpenStreamResult streamResult =
                await Inner.OpenStreamAsync(operationContext, contentHash, operationContext.Token, urgencyHint);
            if (streamResult.Code == OpenStreamResult.ResultCode.Success)
            {
                return streamResult;
            }

            var contentRegistrationUrgencyHint = urgencyHint;

            long? size = null;
            GetBulkLocationsResult localGetBulkResult = null;

            // First try to fetch file based on locally stored locations for the hash
            // Then fallback to fetching file based on global locations minus the locally stored locations which were already checked
            foreach (var getBulkTask in ContentLocationStore.MultiLevelGetLocations(
                         operationContext,
                         new[] { contentHash },
                         operationContext.Token,
                         urgencyHint,
                         subtractLocalResults: true))
            {
                var getBulkResult = await getBulkTask;
                // There is an issue with GetBulkLocationsResult construction from Exception that may loose the information about the origin.
                // So we rely on the result order of MultiLevelGetLocations method: the first result is always local and the second one is global.

                GetBulkOrigin origin = localGetBulkResult == null ? GetBulkOrigin.Local : GetBulkOrigin.Global;
                if (origin == GetBulkOrigin.Local)
                {
                    localGetBulkResult = getBulkResult;
                }

                // Local function: Use content locations for GetBulk to copy file locally
                async Task<BoolResult> tryCopyContentLocalAsync()
                {
                    if (!getBulkResult || !getBulkResult.ContentHashesInfo.Any())
                    {
                        return new BoolResult($"Metadata records for hash {contentHash.ToShortString()} not found in content location store.");
                    }

                    // Don't reconsider locally stored results that were checked in prior iteration
                    getBulkResult = getBulkResult.Subtract(localGetBulkResult);

                    var hashInfo = getBulkResult.ContentHashesInfo.Single();

                    if (!CanCopyContentHash(hashInfo, out var errorMessage))
                    {
                        return new BoolResult(errorMessage);
                    }

                    // Using in-ring machines if configured
                    var copyResult = await TryCopyAndPutAsync(
                        operationContext,
                        hashInfo,
                        urgencyHint,
                        CopyReason.OpenStream,
                        trace: false);
                    if (!copyResult)
                    {
                        return new BoolResult(copyResult);
                    }

                    size = copyResult.ContentSize;

                    // If configured, register the content eagerly on put to make sure the content is discoverable by the other builders,
                    // even if the current machine used to have the content and evicted it recently.
                    if (Settings.RegisterEagerlyOnPut && !copyResult.ContentAlreadyExistsInCache)
                    {
                        contentRegistrationUrgencyHint = UrgencyHint.RegisterEagerly;
                    }

                    return BoolResult.Success;
                }

                var copyLocalResult = await tryCopyContentLocalAsync();

                // Throw operation canceled to avoid operations below which are not value for canceled case.
                operationContext.Token.ThrowIfCancellationRequested();

                if (copyLocalResult.Succeeded)
                {
                    // Succeeded in copying content locally. No need to try with more content locations
                    break;
                }
                else if (origin == GetBulkOrigin.Global)
                {
                    return new OpenStreamResult(copyLocalResult, OpenStreamResult.ResultCode.ContentNotFound);
                }
            }

            Contract.Assert(size != null, "Size should be set if operation succeeded");

            var updateResult = await UpdateContentTrackerWithNewReplicaAsync(
                operationContext,
                new[] { new ContentHashWithSize(contentHash, size.Value) },
                contentRegistrationUrgencyHint);
            if (!updateResult.Succeeded)
            {
                return new OpenStreamResult(updateResult);
            }

            return await Inner.OpenStreamAsync(operationContext, contentHash, operationContext.Token, urgencyHint);
        }

        /// <inheritdoc />
        public override Task<IEnumerable<Task<Indexed<PinResult>>>> PinAsync(
            Context context,
            IReadOnlyList<ContentHash> contentHashes,
            PinOperationConfiguration pinOperationConfiguration)
        {
            // The lifetime of this operation should be detached from the lifetime of the session.
            // But still tracking the lifetime of the store.
            return WithStoreCancellationAsync(
                context,
                opContext => PinHelperAsync(opContext, contentHashes, pinOperationConfiguration.UrgencyHint, pinOperationConfiguration),
                pinOperationConfiguration.CancellationToken);
        }

        /// <inheritdoc />
        protected override Task<IEnumerable<Task<Indexed<PinResult>>>> PinCoreAsync(
            OperationContext operationContext,
            IReadOnlyList<ContentHash> contentHashes,
            UrgencyHint urgencyHint,
            Counter retryCounter,
            Counter fileCounter)
        {
            return PinHelperAsync(operationContext, contentHashes, urgencyHint, PinOperationConfiguration.Default());
        }

        private async Task<IEnumerable<Task<Indexed<PinResult>>>> PinHelperAsync(
            OperationContext operationContext,
            IReadOnlyList<ContentHash> contentHashes,
            UrgencyHint urgencyHint,
            PinOperationConfiguration pinOperationConfiguration)
        {
            Contract.Requires(contentHashes != null);

            IEnumerable<Task<Indexed<PinResult>>> pinResults = null;

            IEnumerable<Task<Indexed<PinResult>>> intermediateResult = null;
            if (pinOperationConfiguration.ReturnGlobalExistenceFast)
            {
                Tracer.Debug(operationContext.TracingContext, $"Detected {nameof(PinOperationConfiguration.ReturnGlobalExistenceFast)}");

                // Check globally for existence, but do not copy locally and do not update content tracker.
                pinResults = await Workflows.RunWithFallback(
                    contentHashes,
                    async hashes =>
                    {
                        intermediateResult = await Inner.PinAsync(operationContext, hashes, operationContext.Token, urgencyHint);
                        return intermediateResult;
                    },
                    hashes => _remotePinner(operationContext, hashes, succeedWithOneLocation: true, urgencyHint),
                    result => result.Succeeded);

                // Replace operation context with a new cancellation token so it can outlast this call.
                // Using a cancellation token from the store avoid the operations lifetime to be greater than the lifetime of the outer store.
                operationContext = new OperationContext(operationContext.TracingContext, token: StoreShutdownStartedCancellationToken);
            }

            // Default pin action
            var pinTask = Workflows.RunWithFallback(
                contentHashes,
                hashes => intermediateResult == null
                    ? Inner.PinAsync(operationContext, hashes, operationContext.Token, urgencyHint)
                    : Task.FromResult(intermediateResult),
                hashes => _remotePinner(operationContext, hashes, succeedWithOneLocation: false, urgencyHint),
                result => result.Succeeded,
                // Exclude the empty hash because it is a special case which is hard coded for place/openstream/pin.
                async hits => await UpdateContentTrackerWithLocalHitsAsync(
                    operationContext,
                    hits.Where(x => !contentHashes[x.Index].IsEmptyHash()).Select(
                        x => new ContentHashWithSizeAndLastAccessTime(contentHashes[x.Index], x.Item.ContentSize, x.Item.LastAccessTime)).ToList(),
                    urgencyHint));

            // Initiate a proactive copy if just pinned content is under-replicated
            if (Settings.ProactiveCopyOnPin && Settings.ProactiveCopyMode != ProactiveCopyMode.Disabled)
            {
                pinTask = ProactiveCopyOnPinAsync(operationContext, contentHashes, pinTask);
            }

            if (pinOperationConfiguration.ReturnGlobalExistenceFast)
            {
                // Fire off the default pin action, but do not await the result.
                //
                // Creating a new OperationContext instance without existing 'CancellationToken',
                // because the operation we triggered that stored in 'pinTask' can outlive the lifetime of the current instance.
                // And we don't want the PerformNonResultOperationAsync to fail because the current instance is shut down (or disposed).
                new OperationContext(operationContext.TracingContext).PerformNonResultOperationAsync(
                    Tracer,
                    () => pinTask,
                    extraEndMessage: results =>
                                     {
                                         var resultString = string.Join(
                                             ",",
                                             results.Select(
                                                 async task =>
                                                 {
                                                     // Since all bulk operations are constructed with Task.FromResult, it is safe to just access the result;
                                                     Indexed<PinResult> result = await task;
                                                     return result != null
                                                         ? $"{contentHashes[result.Index].ToShortString()}:{result.Item}"
                                                         : string.Empty;
                                                 }));

                                         return $"ConfigurablePin Count={contentHashes.Count}, Hashes=[{resultString}]";
                                     },
                    traceErrorsOnly: TraceErrorsOnly,
                    traceOperationStarted: TraceOperationStarted,
                    traceOperationFinished: true,
                    isCritical: false).FireAndForget(operationContext);
            }
            else
            {
                pinResults = await pinTask;
            }

            Contract.Assert(pinResults != null);
            return pinResults;
        }

        private async Task<IEnumerable<Task<Indexed<PinResult>>>> ProactiveCopyOnPinAsync(
            OperationContext context,
            IReadOnlyList<ContentHash> contentHashes,
            Task<IEnumerable<Task<Indexed<PinResult>>>> pinTask)
        {
            var results = await pinTask;

            // Since the rest of the operation is done asynchronously, using a cancellation context from the outer store.
            var proactiveCopyTask = WithStoreCancellationAsync(
                context,
                async opContext =>
                {
                    var proactiveTasks = results.Select(resultTask => proactiveCopyOnSinglePinAsync(opContext, resultTask)).ToList();

                    // Ensure all tasks are completed, after awaiting outer task
                    await Task.WhenAll(proactiveTasks);

                    return proactiveTasks;
                });

            if (Settings.InlineOperationsForTests)
            {
                return await proactiveCopyTask;
            }
            else
            {
                proactiveCopyTask.FireAndForget(context);
                return results;
            }

            // Proactive copy an individual pin
            async Task<Indexed<PinResult>> proactiveCopyOnSinglePinAsync(OperationContext opContext, Task<Indexed<PinResult>> resultTask)
            {
                Indexed<PinResult> indexedPinResult = await resultTask;
                var pinResult = indexedPinResult.Item;

                // Local pins and distributed pins which are copied locally allow proactive copy
                if (pinResult.Succeeded && (!(pinResult is DistributedPinResult distributedPinResult) || distributedPinResult.CopyLocally))
                {
                    var proactiveCopyResult = await ProactiveCopyIfNeededAsync(
                        opContext,
                        contentHashes[indexedPinResult.Index],
                        tryBuildRing: true,
                        CopyReason.ProactiveCopyOnPin);

                    // Only fail if all copies failed.
                    if (!proactiveCopyResult.Succeeded)
                    {
                        return new PinResult(proactiveCopyResult).WithIndex(indexedPinResult.Index);
                    }
                }

                return indexedPinResult;
            }
        }

        /// <inheritdoc />
        protected override async Task<PlaceFileResult> PlaceFileCoreAsync(
            OperationContext operationContext,
            ContentHash contentHash,
            AbsolutePath path,
            FileAccessMode accessMode,
            FileReplacementMode replacementMode,
            FileRealizationMode realizationMode,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            var resultWithData = await PerformPlaceFileGatedOperationAsync(
                operationContext,
                () => PlaceHelperAsync(
                    operationContext,
                    new[] { new ContentHashWithPath(contentHash, path) },
                    accessMode,
                    replacementMode,
                    realizationMode,
                    urgencyHint
                ));

            var result = await resultWithData.Result.SingleAwaitIndexed();
            result.Metadata = resultWithData.Metadata;
            return result;
        }

        /// <inheritdoc />
        protected override async Task<IEnumerable<Task<Indexed<PlaceFileResult>>>> PlaceFileCoreAsync(
            OperationContext operationContext,
            IReadOnlyList<ContentHashWithPath> hashesWithPaths,
            FileAccessMode accessMode,
            FileReplacementMode replacementMode,
            FileRealizationMode realizationMode,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            // The fallback is invoked for cache misses only. This preserves existing behavior of
            // bubbling up errors with Inner store instead of trying remote.
            var resultWithData = await PerformPlaceFileGatedOperationAsync(
                operationContext,
                () => PlaceHelperAsync(
                    operationContext,
                    hashesWithPaths,
                    accessMode,
                    replacementMode,
                    realizationMode,
                    urgencyHint
                ));

            // We are tracing here because we did not want to change the signature for PlaceFileCoreAsync, which is implemented in multiple locations
            operationContext.TracingContext.Debug(
                $"PlaceFileBulk, Gate.OccupiedCount={resultWithData.Metadata.GateOccupiedCount} Gate.Wait={resultWithData.Metadata.GateWaitTime.TotalMilliseconds}ms Hashes.Count={hashesWithPaths.Count}",
                component: nameof(DistributedContentSession),
                operation: "PlaceFileBulk");

            return resultWithData.Result;
        }

        private Task<IEnumerable<Task<Indexed<PlaceFileResult>>>> PlaceHelperAsync(
            OperationContext operationContext,
            IReadOnlyList<ContentHashWithPath> hashesWithPaths,
            FileAccessMode accessMode,
            FileReplacementMode replacementMode,
            FileRealizationMode realizationMode,
            UrgencyHint urgencyHint)
        {
            return Workflows.RunWithFallback(
                hashesWithPaths,
                args => Inner.PlaceFileAsync(
                    operationContext,
                    args,
                    accessMode,
                    replacementMode,
                    realizationMode,
                    operationContext.Token,
                    urgencyHint),
                args => fetchFromMultiLevelContentLocationStoreThenPlaceFileAsync(args),
                result => IsPlaceFileSuccess(result),
                async hits => await UpdateContentTrackerWithLocalHitsAsync(
                    operationContext,
                    hits.Select(
                            x => new ContentHashWithSizeAndLastAccessTime(
                                hashesWithPaths[x.Index].Hash,
                                x.Item.FileSize,
                                x.Item.LastAccessTime))
                        .ToList(),
                    urgencyHint));

            Task<IEnumerable<Task<Indexed<PlaceFileResult>>>> fetchFromMultiLevelContentLocationStoreThenPlaceFileAsync(
                IReadOnlyList<ContentHashWithPath> fetchedContentInfo)
            {
                return MultiLevelUtilities.RunMultiLevelAsync(
                    fetchedContentInfo,
                    runFirstLevelAsync: args => FetchFromMultiLevelContentLocationStoreThenPutAsync(
                                            operationContext,
                                            args,
                                            urgencyHint,
                                            CopyReason.Place),
                    runSecondLevelAsync: args => innerPlaceAsync(args),
                    // NOTE: We just use the first level result if the fetch using content location store fails because the place cannot succeed since the
                    // content will not have been put into the local CAS
                    useFirstLevelResult: result => !IsPlaceFileSuccess(result));
            }

            async Task<IEnumerable<Task<Indexed<PlaceFileResult>>>> innerPlaceAsync(IReadOnlyList<ContentHashWithPath> input)
            {
                // When the content is obtained from remote we still have to place it from the local.
                // So in order to return the right source of the file placement
                // we have to place locally but change the source for each result.
                var results = await Inner.PlaceFileAsync(
                    operationContext,
                    input,
                    accessMode,
                    replacementMode,
                    realizationMode,
                    operationContext.Token,
                    urgencyHint);
                var updatedResults = new List<Indexed<PlaceFileResult>>();
                foreach (var resultTask in results)
                {
                    var result = await resultTask;
                    updatedResults.Add(
                        IndexedExtensions.WithIndex<PlaceFileResult>(
                            result.Item
                                .WithMaterializationSource(PlaceFileResult.Source.DatacenterCache),
                            result.Index));
                }

                return updatedResults.AsTasks();
            }
        }

        private Task<ResultWithMetaData<IEnumerable<Task<Indexed<PlaceFileResult>>>>> PerformPlaceFileGatedOperationAsync(
            OperationContext operationContext,
            Func<Task<IEnumerable<Task<Indexed<PlaceFileResult>>>>> func)
        {
            return GateExtensions.GatedOperationAsync(
                PutAndPlaceFileGate,
                async (timeWaiting, currentCount) =>
                {
                    var gateOccupiedCount = Settings.MaximumConcurrentPutAndPlaceFileOperations - currentCount;

                    var result = await func();

                    return new ResultWithMetaData<IEnumerable<Task<Indexed<PlaceFileResult>>>>(
                        new ResultMetaData(timeWaiting, gateOccupiedCount),
                        result);
                },
                operationContext.Token);
        }

        private static bool IsPlaceFileSuccess(PlaceFileResult result)
        {
            return result.Code != PlaceFileResult.ResultCode.Error && result.Code != PlaceFileResult.ResultCode.NotPlacedContentNotFound;
        }

        /// <inheritdoc />
        public IEnumerable<ContentHash> EnumeratePinnedContentHashes()
        {
            return Inner is IHibernateContentSession session
                ? session.EnumeratePinnedContentHashes()
                : Enumerable.Empty<ContentHash>();
        }

        /// <inheritdoc />
        public Task PinBulkAsync(Context context, IEnumerable<ContentHash> contentHashes)
        {
            // TODO: Replace PinBulkAsync in hibernate with PinAsync bulk call (bug 1365340)
            return Inner is IHibernateContentSession session
                ? session.PinBulkAsync(context, contentHashes)
                : Task.FromResult(0);
        }

        /// <inheritdoc />
        public Task<BoolResult> ShutdownEvictionAsync(Context context)
        {
            return Inner is IHibernateContentSession session
                ? session.ShutdownEvictionAsync(context)
                : BoolResult.SuccessTask;
        }

        private Task<IEnumerable<Task<Indexed<PlaceFileResult>>>> FetchFromMultiLevelContentLocationStoreThenPutAsync(
            OperationContext context,
            IReadOnlyList<ContentHashWithPath> hashesWithPaths,
            UrgencyHint urgencyHint,
            CopyReason reason)
        {
            // First try to place file by fetching files based on locally stored locations for the hash
            // Then fallback to fetching file based on global locations minus the locally stored locations which were already checked

            var localGetBulkResult = new BuildXL.Utilities.AsyncOut<GetBulkLocationsResult>();

            GetIndexedResults<ContentHashWithPath, PlaceFileResult> initialFunc = async args =>
                                                                                  {
                                                                                      var contentHashes = args.Select(p => p.Hash).ToList();
                                                                                      localGetBulkResult.Value =
                                                                                          await ContentLocationStore.GetBulkAsync(
                                                                                              context,
                                                                                              contentHashes,
                                                                                              context.Token,
                                                                                              urgencyHint,
                                                                                              GetBulkOrigin.Local);
                                                                                      return await FetchFromContentLocationStoreThenPutAsync(
                                                                                          context,
                                                                                          args,
                                                                                          GetBulkOrigin.Local,
                                                                                          urgencyHint,
                                                                                          localGetBulkResult.Value,
                                                                                          reason);
                                                                                  };

            GetIndexedResults<ContentHashWithPath, PlaceFileResult> fallbackFunc = async args =>
                                                                                   {
                                                                                       var contentHashes = args.Select(p => p.Hash).ToList();
                                                                                       var globalGetBulkResult =
                                                                                           await ContentLocationStore.GetBulkAsync(
                                                                                               context,
                                                                                               contentHashes,
                                                                                               context.Token,
                                                                                               urgencyHint,
                                                                                               GetBulkOrigin.Global);
                                                                                       globalGetBulkResult =
                                                                                           globalGetBulkResult.Subtract(localGetBulkResult.Value);
                                                                                       return await FetchFromContentLocationStoreThenPutAsync(
                                                                                           context,
                                                                                           args,
                                                                                           GetBulkOrigin.Global,
                                                                                           urgencyHint,
                                                                                           globalGetBulkResult,
                                                                                           reason);
                                                                                   };

            return Workflows.RunWithFallback(
                hashesWithPaths,
                initialFunc: initialFunc,
                fallbackFunc: fallbackFunc,
                isSuccessFunc: result => IsPlaceFileSuccess(result));
        }

        private async Task<IEnumerable<Task<Indexed<PlaceFileResult>>>> FetchFromContentLocationStoreThenPutAsync(
            OperationContext context,
            IReadOnlyList<ContentHashWithPath> hashesWithPaths,
            GetBulkOrigin origin,
            UrgencyHint urgencyHint,
            GetBulkLocationsResult getBulkResult,
            CopyReason reason)
        {
            try
            {
                // Tracing the hashes here for the entire list, instead of tracing one hash at a time inside TryCopyAndPutAsync method.

                // This returns failure if any item in the batch wasn't copied locally
                // TODO: split results and call PlaceFile on successfully copied files (bug 1365340)
                if (!getBulkResult.Succeeded || !getBulkResult.ContentHashesInfo.Any())
                {
                    return hashesWithPaths.Select(
                            _ => new PlaceFileResult(
                                getBulkResult,
                                PlaceFileResult.ResultCode.NotPlacedContentNotFound,
                                "Metadata records not found in content location store"))
                        .AsIndexedTasks();
                }

                async Task<Indexed<PlaceFileResult>> copyAndPut(ContentHashWithSizeAndLocations contentHashWithSizeAndLocations, int index)
                {
                    PlaceFileResult result;
                    try
                    {
                        if (!CanCopyContentHash(contentHashWithSizeAndLocations, out var errorMessage))
                        {
                            result = PlaceFileResult.CreateContentNotFound(errorMessage);
                        }
                        else
                        {
                            var copyResult = await TryCopyAndPutAsync(
                                context,
                                contentHashWithSizeAndLocations,
                                urgencyHint,
                                reason,
                                // We just traced all the hashes as a result of GetBulk call, no need to trace each individual hash.
                                trace: false,
                                outputPath: hashesWithPaths[index].Path);

                            if (!copyResult)
                            {
                                // For ColdStorage we should treat all errors as cache misses
                                result = origin != GetBulkOrigin.ColdStorage
                                    ? new PlaceFileResult(copyResult)
                                    : new PlaceFileResult(copyResult, PlaceFileResult.ResultCode.NotPlacedContentNotFound);
                            }
                            else
                            {
                                var source = origin == GetBulkOrigin.ColdStorage
                                    ? PlaceFileResult.Source.ColdStorage
                                    : PlaceFileResult.Source.DatacenterCache;
                                result = PlaceFileResult.CreateSuccess(PlaceFileResult.ResultCode.PlacedWithMove, copyResult.ContentSize, source);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        // The transform block should not fail with an exception otherwise the block's state will be changed to failed state and the exception
                        // won't be propagated to the caller.
                        result = new PlaceFileResult(e);
                    }

                    return result.WithIndex(index);
                }

                var copyFilesLocallyActionQueue = new ActionQueue(Settings.ParallelCopyFilesLimit);
                var copyFilesLocally = await copyFilesLocallyActionQueue.SelectAsync(
                    items: getBulkResult.ContentHashesInfo,
                    body: (item, index) => copyAndPut(item, index));

                var updateResults = await UpdateContentTrackerWithNewReplicaAsync(
                    context,
                    copyFilesLocally.Where(r => r.Item.Succeeded).Select(r => new ContentHashWithSize(hashesWithPaths[r.Index].Hash, r.Item.FileSize))
                        .ToList(),
                    urgencyHint);

                if (!updateResults.Succeeded)
                {
                    return copyFilesLocally.Select(result => new PlaceFileResult(updateResults).WithIndex(result.Index)).AsTasks();
                }

                return copyFilesLocally.AsTasks();
            }
            catch (Exception ex)
            {
                return hashesWithPaths.Select((hash, index) => new PlaceFileResult(ex).WithIndex(index)).AsTasks();
            }
        }

        private bool CanCopyContentHash(
            ContentHashWithSizeAndLocations result,
            [NotNullWhen(false)] out string message)
        {
            message = null;

            // Null represents no replicas were ever registered, where as empty list implies content is missing from all replicas
            if (result.Locations == null)
            {
                message = $"No replicas registered for hash {result.ContentHash.ToShortString()}";
                return false;
            }

            if (!result.Locations.Any())
            {
                message = $"No replicas currently exist in content tracker for hash {result.ContentHash.ToShortString()}";
                return false;
            }

            return true;
        }

        private async Task<PutResult> TryCopyAndPutAsync(
            OperationContext operationContext,
            ContentHashWithSizeAndLocations hashInfo,
            UrgencyHint urgencyHint,
            CopyReason reason,
            bool trace,
            AbsolutePath outputPath = null)
        {
            Context context = operationContext;
            CancellationToken cts = operationContext.Token;

            if (trace)
            {
                Tracer.Debug(operationContext, $"Copying {hashInfo.ContentHash.ToShortString()} with {hashInfo.Locations?.Count ?? 0} locations");
            }

            var copyCompression = CopyCompression.None;
            var copyCompressionThreshold = Settings.GrpcCopyCompressionSizeThreshold ?? 0;
            if (copyCompressionThreshold > 0 && hashInfo.Size > copyCompressionThreshold)
            {
                copyCompression = Settings.GrpcCopyCompressionAlgorithm;
            }

            var copyRequest = new DistributedContentCopier.CopyRequest(
                _contentStore,
                hashInfo,
                reason,
                HandleCopyAsync: async args =>
                                 {
                                     (CopyFileResult copyFileResult, AbsolutePath tempLocation, _) = args;

                                     PutResult innerPutResult;
                                     long actualSize = copyFileResult.Size ?? hashInfo.Size;
                                     if (DistributedCopier.UseTrustedHash(actualSize) && Inner is ITrustedContentSession trustedInner)
                                     {
                                         // The file has already been hashed, so we can trust the hash of the file.
                                         innerPutResult = await trustedInner.PutTrustedFileAsync(
                                             context,
                                             new ContentHashWithSize(hashInfo.ContentHash, actualSize),
                                             tempLocation,
                                             FileRealizationMode.Move,
                                             cts,
                                             urgencyHint);
                                     }
                                     else
                                     {
                                         // Pass the HashType, not the Hash. This prompts a re-hash of the file, which places it where its actual hash requires.
                                         // If the actual hash differs from the expected hash, then we fail below and move to the next location.
                                         // Also, record the bytes if the file is small enough to be put into the ContentLocationStore.
                                         innerPutResult = await Inner.PutFileAsync(
                                             context,
                                             hashInfo.ContentHash.HashType,
                                             tempLocation,
                                             FileRealizationMode.Move,
                                             cts,
                                             urgencyHint);
                                     }

                                     return innerPutResult;
                                 },
                copyCompression,
                OverrideWorkingFolder: (Inner as ITrustedContentSession)?.TryGetWorkingDirectory(outputPath));

            var putResult = await DistributedCopier.TryCopyAndPutAsync(operationContext, copyRequest);

            return putResult;
        }

        /// <summary>
        /// Runs a given function in the cancellation context of the outer store.
        /// </summary>
        /// <remarks>
        /// The lifetime of some session-based operation is longer than the session itself.
        /// For instance, proactive copies, or put blob can outlive the lifetime of the session.
        /// But these operations should not outlive the lifetime of the store, because when the store is closed
        /// most likely all the operations will fail with some weird errors like "ObjectDisposedException".
        ///
        /// This helper method allows running the operations that may outlive the lifetime of the session but should not outlive the lifetime of the store.
        /// </remarks>
        protected Task<TResult> WithStoreCancellationAsync<TResult>(
            Context context,
            Func<OperationContext, Task<TResult>> func,
            CancellationToken token = default)
        {
            return ((StartupShutdownSlimBase)ContentLocationStore).WithOperationContext(context, token, func);
        }

        /// <summary>
        /// Returns a cancellation token that is triggered when the outer store shutdown is started.
        /// </summary>
        protected CancellationToken StoreShutdownStartedCancellationToken =>
            ((StartupShutdownSlimBase)ContentLocationStore).ShutdownStartedCancellationToken;

        private Task<BoolResult> UpdateContentTrackerWithNewReplicaAsync(
            OperationContext context,
            IReadOnlyList<ContentHashWithSize> contentHashes,
            UrgencyHint urgencyHint)
        {
            if (contentHashes.Count == 0)
            {
                return BoolResult.SuccessTask;
            }

            // TODO: Pass location store option (seems to only be used to prevent updating TTL when replicating for proactive replication) (bug 1365340)
            return ContentLocationStore.RegisterLocalLocationAsync(context, contentHashes, context.Token, urgencyHint);
        }

        private Task<IEnumerable<Task<Indexed<PinResult>>>> PinFromMultiLevelContentLocationStore(
            OperationContext context,
            IReadOnlyList<ContentHash> contentHashes,
            bool succeedWithOneLocation,
            UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            // Pinning a content based on a number of location is inherently dangerous in case of eviction storms in a system.
            // The LLS data might be stale because of event processing delay and the global information can be inaccurate because
            // the trim events are not sent to the global store.
            // We can't do anything when the LLS data is stale, but in some stamps its beneficial not to rely on the global loctions.
            if (Settings.PinConfiguration.UseLocalLocationsOnlyOnUnverifiedPin)
            {
                return PinFromContentLocationStoreOriginAsync(
                    context,
                    contentHashes,
                    GetBulkOrigin.Local,
                    succeedWithOneLocation: succeedWithOneLocation,
                    urgencyHint);
            }

            return Workflows.RunWithFallback(
                contentHashes,
                hashes => PinFromContentLocationStoreOriginAsync(
                    context,
                    hashes,
                    GetBulkOrigin.Local,
                    succeedWithOneLocation: succeedWithOneLocation,
                    urgencyHint),
                hashes => PinFromContentLocationStoreOriginAsync(
                    context,
                    hashes,
                    GetBulkOrigin.Global,
                    succeedWithOneLocation: succeedWithOneLocation,
                    urgencyHint),
                result => result.Succeeded);
        }

        // This method creates pages of hashes, makes one bulk call to the content location store to get content location record sets for all the hashes on the page,
        // and fires off processing of the returned content location record sets while proceeding to the next page of hashes in parallel.
        private async Task<IEnumerable<Task<Indexed<PinResult>>>> PinFromContentLocationStoreOriginAsync(
            OperationContext operationContext,
            IReadOnlyList<ContentHash> hashes,
            GetBulkOrigin origin,
            bool succeedWithOneLocation,
            UrgencyHint urgency = UrgencyHint.Nominal)
        {
            CancellationToken cancel = operationContext.Token;
            // Create an action block to process all the requested remote pins while limiting the number of simultaneously executed.
            var pinnings = new List<RemotePinning>(hashes.Count);
            var pinningAction = ActionBlockSlim.CreateWithAsyncAction<RemotePinning>(
                degreeOfParallelism: Settings.PinConfiguration?.MaxIOOperations ?? 1,
                async pinning => await PinRemoteAsync(
                    operationContext,
                    pinning,
                    isLocal: origin == GetBulkOrigin.Local,
                    updateContentTracker: false,
                    succeedWithOneLocation: succeedWithOneLocation),
                cancellationToken: cancel);

            // Make a bulk call to content location store to get location records for all hashes on the page.
            // NOTE: We use GetBulkStackedAsync so that when Global results are retrieved we also include Local results to ensure we get a full view of available content
            GetBulkLocationsResult pageLookup = await ContentLocationStoreExtensions.GetBulkStackedAsync(
                ContentLocationStore,
                operationContext,
                hashes,
                cancel,
                urgency,
                origin);

            // If successful, fire off the remote pinning logic for each hash. If not, set all pins to failed.
            if (pageLookup.Succeeded)
            {
                foreach (ContentHashWithSizeAndLocations record in pageLookup.ContentHashesInfo)
                {
                    RemotePinning pinning = new RemotePinning(record);
                    pinnings.Add(pinning);
                    await pinningAction.PostAsync(pinning, cancel);
                }
            }
            else
            {
                foreach (ContentHash hash in hashes)
                {
                    Tracer.Warning(
                        operationContext,
                        $"Pin failed for hash {hash}: directory query failed with error {pageLookup.ErrorMessage}");
                    RemotePinning pinning = new RemotePinning(new ContentHashWithSizeAndLocations(hash, -1L)) { Result = new PinResult(pageLookup) };
                    pinnings.Add(pinning);
                }
            }

            Contract.Assert(pinnings.Count == hashes.Count);

            // Wait for all the pinning actions to complete.
            pinningAction.Complete();

            try
            {
                await pinningAction.Completion;
            }
            catch (TaskCanceledException)
            {
                // Cancellation token provided to an action block can be canceled.
                // Ignoring the exception in this case.
            }

            // Inform the content directory that we copied the files.
            // Looking for distributed pin results that were successful by copying the content locally.
            var localCopies = pinnings.Select((rp, index) => (result: rp, index))
                .Where(x => x.result.Result is DistributedPinResult dpr && dpr.CopyLocally).ToList();

            BoolResult updated = await UpdateContentTrackerWithNewReplicaAsync(
                operationContext,
                localCopies.Select(lc => new ContentHashWithSize(lc.result.Record.ContentHash, lc.result.Record.Size)).ToList(),
                UrgencyHint.Nominal);
            if (!updated.Succeeded)
            {
                // We failed to update the tracker. Need to update the results.
                string hashesAsString = string.Join(", ", localCopies.Select(lc => lc.result.Record.ContentHash));
                Tracer.Warning(
                    operationContext,
                    $"Pin failed for hashes {hashesAsString}: local copy succeeded, but could not inform content directory due to {updated.ErrorMessage}.");
                foreach (var (_, index) in localCopies)
                {
                    pinnings[index].Result = new PinResult(updated);
                }
            }

            // The return type should probably be just Task<IList<PinResult>>, but higher callers require the Indexed wrapper and that the PinResults be encased in Tasks.
            return pinnings.Select(x => x.Result ?? createCanceledPutResult()).AsIndexed().AsTasks();

            static PinResult createCanceledPutResult() => new ErrorResult("The operation was canceled").AsResult<PinResult>();
        }

        // The dataflow framework can process only a single object, and returns no output from that processing. By combining the input and output of each remote pinning into a single object,
        // we can nonetheless use the dataflow framework to process pinnings and read the output from the updated objects afterward.
        private class RemotePinning
        {
            public ContentHashWithSizeAndLocations Record { get; }

            private PinResult _result;

            public PinResult Result
            {
                get => _result;
                set
                {
                    value!.ContentSize = Record.Size;
                    _result = value;
                }
            }

            public RemotePinning(ContentHashWithSizeAndLocations record)
                => Record = record;
        }

        // This method processes each remote pinning, setting the output when the operation is completed.
        private async Task PinRemoteAsync(
            OperationContext context,
            RemotePinning pinning,
            bool isLocal,
            bool updateContentTracker = true,
            bool succeedWithOneLocation = false)
        {
            pinning.Result = await PinRemoteAsync(
                context,
                pinning.Record,
                isLocal,
                updateContentTracker,
                succeedWithOneLocation: succeedWithOneLocation);
        }

        // This method processes a single content location record set for pinning.
        private async Task<PinResult> PinRemoteAsync(
            OperationContext operationContext,
            ContentHashWithSizeAndLocations remote,
            bool isLocal,
            bool updateContentTracker = true,
            bool succeedWithOneLocation = false)
        {
            IReadOnlyList<MachineLocation> locations = remote.Locations;

            // If no remote locations are recorded, we definitely can't pin
            if (locations == null || locations.Count == 0)
            {
                if (!isLocal)
                {
                    // Trace only when pin failed based on the data from the global store.
                    Tracer.Warning(operationContext, $"Pin failed for hash {remote.ContentHash}: no remote records.");
                }

                return DistributedPinResult.ContentNotFound(replicaCount: 0, "No locations found");
            }

            // When we only require the content to exist at least once anywhere, we can ignore pin thresholds
            // and return success after finding a single location.
            if (succeedWithOneLocation && locations.Count >= 1)
            {
                return DistributedPinResult.EnoughReplicas(locations.Count, "Global succeeds");
            }

            if (locations.Count >= Settings.PinConfiguration.PinMinUnverifiedCount)
            {
                SessionCounters[Counters.PinUnverifiedCountSatisfied].Increment();

                // Tracing extra data if the locations were merged to separate the local locations and the global locations that we added to the final result.
                string extraMessage = null;
                if (!isLocal)
                {
                    // Extra locations does make sense only for the global case when the entries were merged.
                    extraMessage = $"ExtraGlobal: {remote.ExtraMergedLocations}";
                }

                var result = DistributedPinResult.EnoughReplicas(locations.Count, extraMessage);

                // Triggering an async copy if the number of replicas are close to a PinMinUnverifiedCount threshold.
                int threshold = Settings.PinConfiguration.PinMinUnverifiedCount +
                                Settings.PinConfiguration.AsyncCopyOnPinThreshold;
                if (locations.Count < threshold)
                {
                    Tracer.Info(
                        operationContext,
                        $"Starting asynchronous copy of the content for hash {remote.ContentHash.ToShortString()} because the number of locations '{locations.Count}' is less then a threshold of '{threshold}'.");
                    SessionCounters[Counters.StartCopyForPinWhenUnverifiedCountSatisfied].Increment();

                    // For "synchronous" pins the tracker is updated at once for all the hashes for performance reasons,
                    // but for asynchronous copy we always need to update the tracker with a new location.
                    var task = WithStoreCancellationAsync(
                        operationContext.TracingContext,
                        opContext => TryCopyAndPutAndUpdateContentTrackerAsync(
                            opContext,
                            remote,
                            updateContentTracker: true,
                            CopyReason.AsyncCopyOnPin));
                    if (Settings.InlineOperationsForTests)
                    {
                        (await task).TraceIfFailure(operationContext);
                    }
                    else
                    {
                        task.FireAndForget(operationContext.TracingContext, traceErrorResult: true, operation: "AsynchronousCopyOnPin");
                    }

                    // Note: Pin result traces with CpA (copied asynchronously) code is to provide the information that the content is being copied asynchronously, and that replica count is enough but not above async copy threshold.
                    // This trace result does not represent that of the async copy since that is done FireAndForget.
                    result = DistributedPinResult.AsynchronousCopy(locations.Count);
                }

                return result;
            }

            if (isLocal)
            {
                // Don't copy content locally based on locally cached result. So stop here and return content not found.
                // This method will be called again with global locations at which time we will attempt to copy the files locally.
                // When allowing global locations to succeed a put, report success.
                return DistributedPinResult.ContentNotFound(locations.Count);
            }

            // Previous checks were not sufficient, so copy the file locally.
            PutResult copy = await TryCopyAndPutAsync(operationContext, remote, UrgencyHint.Nominal, CopyReason.Pin, trace: false);
            if (copy)
            {
                if (!updateContentTracker)
                {
                    return DistributedPinResult.SynchronousCopy(locations.Count);
                }

                // Inform the content directory that we have the file.
                // We wait for this to complete, rather than doing it fire-and-forget, because another machine in the ring may need the pinned content immediately.
                BoolResult updated = await UpdateContentTrackerWithNewReplicaAsync(
                    operationContext,
                    new[] { new ContentHashWithSize(remote.ContentHash, copy.ContentSize) },
                    UrgencyHint.Nominal);
                if (updated.Succeeded)
                {
                    return DistributedPinResult.SynchronousCopy(locations.Count);
                }
                else
                {
                    // Tracing the error separately.
                    Tracer.Warning(
                        operationContext,
                        $"Pin failed for hash {remote.ContentHash}: local copy succeeded, but could not inform content directory due to {updated.ErrorMessage}.");
                    return new DistributedPinResult(locations.Count, updated);
                }
            }
            else
            {
                // Tracing the error separately.
                Tracer.Warning(operationContext, $"Pin failed for hash {remote.ContentHash}: local copy failed with {copy}.");
                return DistributedPinResult.ContentNotFound(locations.Count);
            }
        }

        private async Task<BoolResult> TryCopyAndPutAndUpdateContentTrackerAsync(
            OperationContext operationContext,
            ContentHashWithSizeAndLocations remote,
            bool updateContentTracker,
            CopyReason reason)
        {
            PutResult copy = await TryCopyAndPutAsync(operationContext, remote, UrgencyHint.Nominal, reason, trace: true);
            if (copy && updateContentTracker)
            {
                return await UpdateContentTrackerWithNewReplicaAsync(
                    operationContext,
                    new[] { new ContentHashWithSize(remote.ContentHash, copy.ContentSize) },
                    UrgencyHint.Nominal);
            }

            return copy;
        }

        private Task UpdateContentTrackerWithLocalHitsAsync(
            OperationContext context,
            IReadOnlyList<ContentHashWithSizeAndLastAccessTime> contentHashesWithInfo,
            UrgencyHint urgencyHint)
        {
            if (Disposed)
            {
                // Nothing to do.
                return BoolTask.True;
            }

            if (contentHashesWithInfo.Count == 0)
            {
                // Nothing to do.
                return BoolTask.True;
            }

            IReadOnlyList<ContentHashWithSize> hashesToEagerUpdate =
                contentHashesWithInfo.Select(x => new ContentHashWithSize(x.Hash, x.Size)).ToList();

            // Wait for update to complete on remaining hashes to cover case where the record has expired and another machine in the ring requests it immediately after this pin succeeds.
            return UpdateContentTrackerWithNewReplicaAsync(context, hashesToEagerUpdate, urgencyHint);
        }

        internal async Task<IReadOnlyList<ContentHashWithSizeAndLocations>> GetLocationsForProactiveCopyAsync(
            OperationContext context,
            IReadOnlyList<ContentHash> hashes)
        {
            var originalLength = hashes.Count;
            if (_buildIdHash.HasValue && !_buildRingMachinesCache.IsUpToDate())
            {
                Tracer.Debug(
                    context,
                    $"{Tracer.Name}.{nameof(GetLocationsForProactiveCopyAsync)}: getting in-ring machines for BuildId='{_buildId}'.");
                // Add build id hash to hashes so build ring machines can be updated
                hashes = hashes.AppendItem(_buildIdHash.Value).ToList();
            }

            var result = await MultiLevelUtilities.RunMultiLevelWithMergeAsync(
                hashes,
                inputs => ResultsExtensions.ThrowIfFailureAsync<GetBulkLocationsResult, IReadOnlyList<ContentHashWithSizeAndLocations>>(
                    ContentLocationStore.GetBulkAsync(context, inputs, context.Token, UrgencyHint.Nominal, GetBulkOrigin.Local),
                    g => g.ContentHashesInfo),
                inputs => ResultsExtensions.ThrowIfFailureAsync<GetBulkLocationsResult, IReadOnlyList<ContentHashWithSizeAndLocations>>(
                    ContentLocationStore.GetBulkAsync(context, inputs, context.Token, UrgencyHint.Nominal, GetBulkOrigin.Global),
                    g => g.ContentHashesInfo),
                mergeResults: ContentHashWithSizeAndLocations.Merge,
                useFirstLevelResult: result =>
                                     {
                                         if (result.Locations?.Count >= Settings.ProactiveCopyLocationsThreshold)
                                         {
                                             SessionCounters[Counters.GetLocationsSatisfiedFromLocal].Increment();
                                             return true;
                                         }
                                         else
                                         {
                                             SessionCounters[Counters.GetLocationsSatisfiedFromRemote].Increment();
                                             return false;
                                         }
                                     });

            if (hashes.Count != originalLength)
            {
                // Update build ring machines with retrieved locations
                var buildRingMachines = result.Last().Locations?.AppendItem(LocalCacheRootMachineLocation).ToArray() ??
                                        CollectionUtilities.EmptyArray<MachineLocation>();
                _buildRingMachinesCache.Update(buildRingMachines);
                Tracer.Debug(
                    context,
                    $"{Tracer.Name}.{nameof(GetLocationsForProactiveCopyAsync)}: InRingMachines=[{string.Join(", ", buildRingMachines)}] BuildId='{_buildId}'");
                return result.Take(originalLength).ToList();
            }
            else
            {
                return result;
            }
        }

        internal async Task<ProactiveCopyResult> ProactiveCopyIfNeededAsync(
            OperationContext context,
            ContentHash hash,
            bool tryBuildRing,
            CopyReason reason)
        {
            var nagleQueue = _proactiveCopyGetBulkNagleQueue;
            if (nagleQueue is null)
            {
                return new ProactiveCopyResult(new ErrorResult("StartupAsync was not called"));
            }

            ContentHashWithSizeAndLocations result = await nagleQueue.EnqueueAsync(hash);
            return await ProactiveCopyIfNeededAsync(context, result, tryBuildRing, reason);
        }

        internal Task<ProactiveCopyResult> ProactiveCopyIfNeededAsync(
            OperationContext context,
            ContentHashWithSizeAndLocations info,
            bool tryBuildRing,
            CopyReason reason)
        {
            var hash = info.ContentHash;
            if (!_pendingProactivePuts.Add(hash)
                || info.ContentHash.IsEmptyHash()) // No reason to push an empty hash to another machine.
            {
                return Task.FromResult(ProactiveCopyResult.CopyNotRequiredResult);
            }

            // Don't trace this case since it would add too much log traffic.
            var replicatedLocations = (info.Locations ?? CollectionUtilities.EmptyArray<MachineLocation>()).ToList();

            if (replicatedLocations.Count >= Settings.ProactiveCopyLocationsThreshold)
            {
                SessionCounters[Counters.ProactiveCopiesSkipped].Increment();
                return Task.FromResult(ProactiveCopyResult.CopyNotRequiredResult);
            }

            // By adding the master to replicatedLocations, it will be excluded from proactive replication
            var masterLocation = _contentStore.LocalLocationStore?.MasterElectionMechanism.Master;
            if (masterLocation is not null && masterLocation.Value.IsValid)
            {
                replicatedLocations.Add(masterLocation.Value);
            }

            return context.PerformOperationAsync(
                Tracer,
                operation: async () =>
                           {
                               try
                               {
                                   var outsideRingCopyTask = ProactiveCopyOutsideBuildRingWithRetryAsync(context, info, replicatedLocations, reason);
                                   var insideRingCopyTask = ProactiveCopyInsideBuildRingWithRetryAsync(
                                       context,
                                       info,
                                       tryBuildRing,
                                       replicatedLocations,
                                       reason);
                                   await Task.WhenAll(outsideRingCopyTask, insideRingCopyTask);

                                   var (insideRingRetries, insideRingResult) = await insideRingCopyTask;
                                   var (outsideRingRetries, outsideRingResult) = await outsideRingCopyTask;

                                   int totalRetries = insideRingRetries + outsideRingRetries;
                                   return new ProactiveCopyResult(insideRingResult, outsideRingResult, totalRetries, info.Entry);
                               }
                               finally
                               {
                                   _pendingProactivePuts.Remove(hash);
                               }
                           },
                traceOperationStarted: false,
                extraEndMessage: r => $"Hash={info.ContentHash}, Retries={r.TotalRetries}, Reason=[{reason}]");
        }

        private async Task<(int retries, ProactivePushResult outsideRingCopyResult)> ProactiveCopyOutsideBuildRingWithRetryAsync(
            OperationContext context,
            ContentHashWithSize hash,
            IReadOnlyList<MachineLocation> replicatedLocations,
            CopyReason reason)
        {
            var outsideRingCopyResult = await ProactiveCopyOutsideBuildRingAsync(context, hash, replicatedLocations, reason, retries: 0);
            int retries = 0;
            while (outsideRingCopyResult.QualifiesForRetry && retries < Settings.ProactiveCopyMaxRetries)
            {
                SessionCounters[Counters.ProactiveCopyRetries].Increment();
                SessionCounters[Counters.ProactiveCopyOutsideRingRetries].Increment();
                retries++;
                outsideRingCopyResult = await ProactiveCopyOutsideBuildRingAsync(context, hash, replicatedLocations, reason, retries);
            }

            return (retries, outsideRingCopyResult);
        }

        private async Task<ProactivePushResult> ProactiveCopyOutsideBuildRingAsync(
            OperationContext context,
            ContentHashWithSize hash,
            IReadOnlyList<MachineLocation> replicatedLocations,
            CopyReason reason,
            int retries)
        {
            // The first attempt is not considered as retry
            int attempt = retries + 1;
            if ((Settings.ProactiveCopyMode & ProactiveCopyMode.OutsideRing) == 0)
            {
                return ProactivePushResult.FromPushFileResult(PushFileResult.Disabled(), attempt);
            }

            Result<MachineLocation> getLocationResult = null;
            var source = ProactiveCopyLocationSource.Random;

            // Make sure that the machine is not in the build ring and does not already have the content.
            var machinesToSkip = replicatedLocations.Concat(BuildRingMachines).ToArray();

            // Try to select one of the designated machines for this hash.
            if (Settings.ProactiveCopyUsePreferredLocations)
            {
                var designatedLocationsResult = ContentLocationStore.GetDesignatedLocations(hash);
                if (designatedLocationsResult.Succeeded)
                {
                    // A machine in the build may be a designated location for the hash,
                    // but we won't pushing to the same machine twice, because 'replicatedLocations' argument
                    // has a local machine that we're about to push for inside the ring copy.
                    // We also want to skip inside ring machines for outsidecopy task
                    var candidates = Enumerable.Except(designatedLocationsResult.Value, machinesToSkip).ToArray();

                    if (candidates.Length > 0)
                    {
                        getLocationResult = candidates[ThreadSafeRandom.Generator.Next(0, candidates.Length)];
                        source = ProactiveCopyLocationSource.DesignatedLocation;
                        SessionCounters[Counters.ProactiveCopy_OutsideRingFromPreferredLocations].Increment();
                    }
                }
            }

            // Try to select one machine at random.
            if (getLocationResult?.Succeeded != true)
            {
                getLocationResult = ContentLocationStore.GetRandomMachineLocation(except: machinesToSkip);
                source = ProactiveCopyLocationSource.Random;
            }

            if (!getLocationResult.Succeeded)
            {
                return ProactivePushResult.FromPushFileResult(new PushFileResult(getLocationResult), attempt);
            }

            var candidate = getLocationResult.Value;
            SessionCounters[Counters.ProactiveCopy_OutsideRingCopies].Increment();
            PushFileResult pushFileResult = await PushContentAsync(context, hash, candidate, isInsideRing: false, reason, source, attempt);
            return ProactivePushResult.FromPushFileResult(pushFileResult, attempt);
        }

        private async Task<(int retries, ProactivePushResult insideRingCopyResult)> ProactiveCopyInsideBuildRingWithRetryAsync(
            OperationContext context,
            ContentHashWithSize hash,
            bool tryBuildRing,
            IReadOnlyList<MachineLocation> replicatedLocations,
            CopyReason reason)
        {
            ProactivePushResult insideRingCopyResult = await ProactiveCopyInsideBuildRing(
                context,
                hash,
                tryBuildRing,
                replicatedLocations,
                reason,
                retries: 0);
            int retries = 0;
            while (insideRingCopyResult.QualifiesForRetry && retries < Settings.ProactiveCopyMaxRetries)
            {
                SessionCounters[Counters.ProactiveCopyRetries].Increment();
                SessionCounters[Counters.ProactiveCopyInsideRingRetries].Increment();
                retries++;
                insideRingCopyResult = await ProactiveCopyInsideBuildRing(context, hash, tryBuildRing, replicatedLocations, reason, retries);
            }

            return (retries, insideRingCopyResult);
        }

        /// <summary>
        /// Gets all the active in-ring machines (excluding the current one).
        /// </summary>
        public MachineLocation[] GetInRingActiveMachines()
        {
            return Enumerable.Where<MachineLocation>(BuildRingMachines, m => !m.Equals(LocalCacheRootMachineLocation))
                .Where(m => ContentLocationStore.IsMachineActive(m))
                .ToArray();
        }

        private async Task<ProactivePushResult> ProactiveCopyInsideBuildRing(
            OperationContext context,
            ContentHashWithSize hash,
            bool tryBuildRing,
            IReadOnlyList<MachineLocation> replicatedLocations,
            CopyReason reason,
            int retries)
        {
            // The first attempt is not considered as retry
            int attempt = retries + 1;

            // Get random machine inside build ring
            if (!tryBuildRing || (Settings.ProactiveCopyMode & ProactiveCopyMode.InsideRing) == 0)
            {
                return ProactivePushResult.FromPushFileResult(PushFileResult.Disabled(), attempt);
            }

            if (_buildIdHash == null)
            {
                return ProactivePushResult.FromStatus(ProactivePushStatus.BuildIdNotSpecified, attempt);
            }

            // Having an explicit case to check if the in-ring machine list is empty to separate the case
            // when the machine list is not empty but all the candidates are unavailable.
            if (BuildRingMachines.Length == 0)
            {
                return ProactivePushResult.FromStatus(ProactivePushStatus.InRingMachineListIsEmpty, attempt);
            }

            var candidates = GetInRingActiveMachines();

            if (candidates.Length == 0)
            {
                return ProactivePushResult.FromStatus(ProactivePushStatus.MachineNotFound, attempt);
            }

            candidates = candidates.Except(replicatedLocations).ToArray();
            if (candidates.Length == 0)
            {
                SessionCounters[Counters.ProactiveCopy_InsideRingFullyReplicated].Increment();
                return ProactivePushResult.FromStatus(ProactivePushStatus.MachineAlreadyHasCopy, attempt);
            }

            SessionCounters[Counters.ProactiveCopy_InsideRingCopies].Increment();
            var candidate = candidates[ThreadSafeRandom.Generator.Next(0, candidates.Length)];
            PushFileResult pushFileResult = await PushContentAsync(
                context,
                hash,
                candidate,
                isInsideRing: true,
                reason,
                ProactiveCopyLocationSource.Random,
                attempt);
            return ProactivePushResult.FromPushFileResult(pushFileResult, attempt);
        }

        private async Task<PushFileResult> PushContentAsync(
            OperationContext context,
            ContentHashWithSize hash,
            MachineLocation target,
            bool isInsideRing,
            CopyReason reason,
            ProactiveCopyLocationSource source,
            int attempt)
        {
            // This is here to avoid hanging ProactiveCopyIfNeededAsync on inside/outside ring copies before starting
            // the other one.
            await Task.Yield();

            if (Settings.PushProactiveCopies)
            {
                // It is possible that this method is used during proactive replication
                // and the hash was already evicted at the time this method is called.
                var streamResult = await Inner.OpenStreamAsync(context, hash, context.Token);
                if (!streamResult.Succeeded)
                {
                    return PushFileResult.SkipContentUnavailable();
                }

                using var stream = streamResult.Stream!;

                return await DistributedCopier.PushFileAsync(
                    context,
                    hash,
                    target,
                    stream,
                    isInsideRing,
                    reason,
                    source,
                    attempt);
            }
            else
            {
                var requestResult = await DistributedCopier.RequestCopyFileAsync(context, hash, target, isInsideRing, attempt);
                if (requestResult)
                {
                    return PushFileResult.PushSucceeded(size: null);
                }

                return new PushFileResult(requestResult, "Failed requesting a copy");
            }
        }

        /// <inheritdoc />
        protected override CounterSet GetCounters() =>
            base.GetCounters()
                .Merge(DistributedCopier.GetCounters())
                .Merge(CounterUtilities.ToCounterSet((CounterCollection)SessionCounters));

        /// <inheritdoc />
        protected override Task<PutResult> PutFileCoreAsync(
            OperationContext operationContext,
            HashType hashType,
            AbsolutePath path,
            FileRealizationMode realizationMode,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            return PerformPutFileGatedOperationAsync(
                operationContext,
                () =>
                {
                    return PutCoreAsync(
                        operationContext,
                        urgencyHint,
                        session => session.PutFileAsync(operationContext, hashType, path, realizationMode, operationContext.Token, urgencyHint));
                });
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
            // We are intentionally not gating PutStream operations because we don't expect a high number of them at
            // the same time.
            return PerformPutFileGatedOperationAsync(
                operationContext,
                () =>
                {
                    return PutCoreAsync(
                        operationContext,
                        urgencyHint,
                        session => session.PutFileAsync(operationContext, contentHash, path, realizationMode, operationContext.Token, urgencyHint));
                });
        }

        private Task<PutResult> PerformPutFileGatedOperationAsync(OperationContext operationContext, Func<Task<PutResult>> func)
        {
            return PutAndPlaceFileGate.GatedOperationAsync(
                async (timeWaiting, currentCount) =>
                {
                    var gateOccupiedCount = Settings.MaximumConcurrentPutAndPlaceFileOperations - currentCount;

                    var result = await func();
                    result.MetaData = new ResultMetaData(timeWaiting, gateOccupiedCount);

                    return result;
                },
                operationContext.Token);
        }

        /// <inheritdoc />
        protected override Task<PutResult> PutStreamCoreAsync(
            OperationContext operationContext,
            HashType hashType,
            Stream stream,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            return PutCoreAsync(
                operationContext,
                urgencyHint,
                session => session.PutStreamAsync(operationContext, hashType, stream, operationContext.Token, urgencyHint));
        }

        /// <inheritdoc />
        protected override Task<PutResult> PutStreamCoreAsync(
            OperationContext operationContext,
            ContentHash contentHash,
            Stream stream,
            UrgencyHint urgencyHint,
            Counter retryCounter)
        {
            return PutCoreAsync(
                operationContext,
                urgencyHint,
                session => session.PutStreamAsync(operationContext, contentHash, stream, operationContext.Token, urgencyHint));
        }

        /// <summary>
        /// Executes a put operation, while providing the logic to retrieve the bytes that were put through a RecordingStream.
        /// RecordingStream makes it possible to see the actual bytes that are being read by the inner ContentSession.
        /// </summary>
        private async Task<PutResult> PutCoreAsync(
            OperationContext context,
            UrgencyHint urgencyHint,
            Func<IContentSession, Task<PutResult>> putAsync)
        {
            PutResult result = await putAsync(Inner);
            if (!result)
            {
                return result;
            }

            // It is important to register location before requesting the proactive copy; otherwise, we can fail the proactive copy.
            var registerResult = await RegisterPutAsync(context, urgencyHint, result);

            // Only perform proactive copy to other machines if we succeeded in registering our location.
            if (registerResult && Settings.ProactiveCopyOnPut && Settings.ProactiveCopyMode != ProactiveCopyMode.Disabled)
            {
                // Since the rest of the operation is done asynchronously, create new context to stop cancelling operation prematurely.
                var proactiveCopyTask = WithStoreCancellationAsync(
                    context,
                    operationContext => ProactiveCopyIfNeededAsync(
                        operationContext,
                        result.ContentHash,
                        tryBuildRing: true,
                        CopyReason.ProactiveCopyOnPut)
                );

                if (Settings.InlineOperationsForTests)
                {
                    var proactiveCopyResult = await proactiveCopyTask;

                    // Only fail if all copies failed.
                    if (proactiveCopyResult.InsideRingCopyResult?.Succeeded == false && proactiveCopyResult.OutsideRingCopyResult?.Succeeded == false)
                    {
                        return new PutResult(proactiveCopyResult);
                    }
                }
                else
                {
                    // Tracing task-related errors because normal failures already traced by the operation provider
                    proactiveCopyTask.TraceIfFailure(
                        context,
                        failureSeverity: Severity.Debug,
                        traceTaskExceptionsOnly: true,
                        operation: "ProactiveCopyIfNeeded");
                }
            }

            return registerResult;
        }

        private async Task<PutResult> RegisterPutAsync(OperationContext context, UrgencyHint urgencyHint, PutResult putResult)
        {
            if (putResult.Succeeded && ShouldRegister(urgencyHint))
            {
                if (Settings.RegisterEagerlyOnPut && !putResult.ContentAlreadyExistsInCache)
                {
                    // The content might have been recently evicted from the current machine and due to various optimizations
                    // the global registration might be skipped for the newly produced content.
                    // This might cause build failures and we want to make sure that the content that was produced during the build
                    // is definitely available in the global store.
                    urgencyHint = UrgencyHint.RegisterEagerly;
                }

                var updateResult = await ContentLocationStore.RegisterLocalLocationAsync(
                    context,
                    new[] { new ContentHashWithSize(putResult.ContentHash, putResult.ContentSize) },
                    context.Token,
                    urgencyHint);

                if (!updateResult.Succeeded)
                {
                    return new PutResult(updateResult, putResult.ContentHash);
                }
            }

            return putResult;
        }

        private bool ShouldRegister(UrgencyHint urgencyHint)
        {
            return !Settings.RespectSkipRegisterContentHint || urgencyHint != UrgencyHint.SkipRegisterContent;
        }

        public IContentSession TryGetLocalContentSession()
        {
            return (Inner as ILocalContentSessionProvider)?.TryGetLocalContentSession();
        }
    }
}
