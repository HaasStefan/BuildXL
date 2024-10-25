﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.MemoizationStore.Interfaces.Results;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Cache.MemoizationStore.Interfaces.Stores;
using BuildXL.Cache.MemoizationStore.Sessions;
using BuildXL.Cache.MemoizationStore.Tracing;

#nullable enable

namespace BuildXL.Cache.MemoizationStore.Stores
{
    /// <summary>
    ///     An IMemoizationStore implementation using RocksDb.
    /// </summary>
    public class DatabaseMemoizationStore : StartupShutdownBase, IMemoizationStore
    {
        /// <summary>
        /// The database backing the store
        /// </summary>
        public virtual MemoizationDatabase Database { get; }

        /// <summary>
        ///     Store tracer.
        /// </summary>
        private readonly MemoizationStoreTracer _tracer;

        /// <inheritdoc />
        protected override Tracer Tracer => _tracer;

        /// <summary>
        /// The component name
        /// </summary>
        protected string Component => Tracer.Name;

        /// <summary>
        /// Indicates calls to <see cref="AddOrGetContentHashListAsync"/> should do an optimistic write (via CompareExchange) assuming
        /// that content is not present for initial attempt.
        /// </summary>
        public bool OptimizeWrites { get; set; } = false;

        /// <summary>
        /// <see cref="Stores.ContentHashListReplacementCheckBehavior"/>
        /// </summary>
        public ContentHashListReplacementCheckBehavior ContentHashListReplacementCheckBehavior { get; set; } = ContentHashListReplacementCheckBehavior.PinAlways;

        /// <summary>
        /// Indicates calls to <see cref="AddOrGetContentHashListAsync"/> should register associated content for content hash lists
        /// </summary>
        public bool RegisterAssociatedContent { get; set; } = false;

        /// <summary>
        ///     Initializes a new instance of the <see cref="DatabaseMemoizationStore"/> class.
        /// </summary>
        public DatabaseMemoizationStore(MemoizationDatabase database)
        {
            Contract.RequiresNotNull(database);

            _tracer = new MemoizationStoreTracer(database.Name);
            Database = database;
        }

        /// <inheritdoc />
        public CreateSessionResult<IMemoizationSession> CreateSession(Context context, string name, IContentSession contentSession, bool automaticallyOverwriteContentHashLists)
        {
            var session = new DatabaseMemoizationSession(name, this, contentSession, automaticallyOverwriteContentHashLists);
            return new CreateSessionResult<IMemoizationSession>(session);
        }

        /// <inheritdoc />
        public Task<GetStatsResult> GetStatsAsync(Context context)
        {
            return GetStatsCall<MemoizationStoreTracer>.RunAsync(_tracer, new OperationContext(context), async () =>
            {
                var counters = new CounterSet();
                counters.Merge(_tracer.GetCounters(), $"{_tracer.Name}.");

                // Merge stats that may come from the database
                var databaseStats = await Database.GetStatsAsync(context);
                if (databaseStats.Succeeded)
                {
                    counters.Merge(databaseStats.Value, $"{Database.StatsProvenance}.");
                }

                return new GetStatsResult(counters);
            });
        }

        /// <inheritdoc />
        protected override Task<BoolResult> StartupCoreAsync(OperationContext context)
        {
            return Database.StartupAsync(context);
        }

        /// <inheritdoc />
        protected override Task<BoolResult> ShutdownCoreAsync(OperationContext context)
        {
            return Database.ShutdownAsync(context);
        }

        /// <inheritdoc />
        public IAsyncEnumerable<StructResult<StrongFingerprint>> EnumerateStrongFingerprints(Context context)
        {
            var ctx = new OperationContext(context);
            return AsyncEnumerableExtensions.CreateSingleProducerTaskAsyncEnumerable(() => EnumerateStrongFingerprintsAsync(ctx));
        }

        private async Task<IEnumerable<StructResult<StrongFingerprint>>> EnumerateStrongFingerprintsAsync(OperationContext context)
        {
            var result = await Database.EnumerateStrongFingerprintsAsync(context);
            return result.Select(r => StructResult.FromResult(r));
        }

        /// <inheritdoc />
        public Task<BoolResult> IncorporateStrongFingerprintsAsync(
            Context context,
            IEnumerable<Task<StrongFingerprint>> strongFingerprints,
            CancellationToken cts)
        {
            return Database.IncorporateStrongFingerprintsAsync(new OperationContext(context, cts), strongFingerprints);
        }

        internal Task<GetContentHashListResult> GetContentHashListAsync(Context context, StrongFingerprint strongFingerprint, CancellationToken token, IContentSession contentSession, bool automaticallyOverwriteContentHashLists, bool preferShared = false)
        {
            var ctx = new OperationContext(context, token);
            return ctx.PerformOperationAsync(_tracer, async () =>
            {
                var result = await Database.GetContentHashListAsync(ctx, strongFingerprint, preferShared: preferShared);

                if (!result.Succeeded)
                {
                    return new GetContentHashListResult(result, result.Source);
                }

                // if the associated content needs preventive pinning, do it here
                if (result.Value.contentHashListInfo.ContentHashList is not null &&
                    Database.AssociatedContentNeedsPinning(ctx, strongFingerprint, result))
                {
                    (ContentHashListWithDeterminism contentHashList, _, _) = result;
                    // Observe we explicitly pass automaticallyOverwriteContentHashLists: true. We want automatic overriding on since in this case we are *preventively* pinning, and we do want the pin operation to happen
                    var pinResult = await contentSession.EnsureContentIsAvailableWithResultAsync(ctx, Tracer.Name, contentHashList.ContentHashList, automaticallyOverwriteContentHashLists: true, ctx.Token).ConfigureAwait(false);

                    if (!pinResult.Succeeded)
                    {
                        return new GetContentHashListResult(pinResult, result.Source);
                    }

                    if (contentHashList.ContentHashList != null)
                    {
                        Tracer.Info(context, $"Strong fingerprint {strongFingerprint} content was preventively pinned. Hashes: {string.Join(", ", contentHashList.ContentHashList.Hashes)}");
                    }

                    // All the associated content is now pinned. Update the last content pinned time on the content hash list entry
                    var pinnedNotificationResult = await Database.AssociatedContentWasPinnedAsync(ctx, strongFingerprint, result);

                    if (!pinnedNotificationResult.Succeeded)
                    {
                        return new GetContentHashListResult(pinnedNotificationResult, result.Source);
                    }
                }

                return new GetContentHashListResult(result.Value.contentHashListInfo, result.Source);
            },
            extraEndMessage: _ => $"StrongFingerprint=[{strongFingerprint}] PreferShared=[{preferShared}]",
            traceOperationStarted: false);
        }

        internal Task<AddOrGetContentHashListResult> AddOrGetContentHashListAsync(Context context, StrongFingerprint strongFingerprint, ContentHashListWithDeterminism contentHashListWithDeterminism, IContentSession contentSession, bool automaticallyOverwriteContentHashLists, CancellationToken token)
        {
            var ctx = new OperationContext(context, token);

            return ctx.PerformOperationAsync(_tracer, async () =>
            {
                if (RegisterAssociatedContent)
                {
                    await Database.RegisterAssociatedContentAsync(ctx, strongFingerprint, contentHashListWithDeterminism)
                        .ThrowIfFailureAsync();
                }

                // We do multiple attempts here because we have a "CompareExchange"
                // of this implementation, and this may fail if the strong fingerprint has concurrent
                // writers.
                const int MaxAttempts = 5;
                for (int attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    var contentHashList = contentHashListWithDeterminism.ContentHashList!;
                    var determinism = contentHashListWithDeterminism.Determinism;

                    // Load old value. Notice that this get updates the time, regardless of whether we replace the value or not.
                    var contentHashListResult = (!OptimizeWrites || attempt > 0)
                        ? await Database.GetContentHashListAsync(
                            ctx,
                            strongFingerprint,
                            // Prefer shared result because conflicts are resolved at shared level
                            preferShared: true).ThrowIfFailureAsync()
                        : new ContentHashListResult(default(ContentHashListWithDeterminism), string.Empty);
                    var (oldContentHashListInfo, replacementToken, _) = contentHashListResult;

                    var oldContentHashList = oldContentHashListInfo.ContentHashList;
                    var oldDeterminism = oldContentHashListInfo.Determinism;

                    // Make sure we're not mixing SinglePhaseNonDeterminism records
                    if (!(oldContentHashList is null) && oldDeterminism.IsSinglePhaseNonDeterministic != determinism.IsSinglePhaseNonDeterministic)
                    {
                        return AddOrGetContentHashListResult.SinglePhaseMixingError;
                    }

                    async ValueTask<bool> canReplaceAsync()
                    {
                        if (oldContentHashList is null || oldDeterminism.ShouldBeReplacedWith(determinism))
                        {
                            // No old value or new value has higher determinism precedence so replace
                            return true;
                        }

                        if (ContentHashListReplacementCheckBehavior == ContentHashListReplacementCheckBehavior.ReplaceAlways)
                        {
                            return true;
                        }
                        else if (ContentHashListReplacementCheckBehavior == ContentHashListReplacementCheckBehavior.ReplaceNever)
                        {
                            return false;
                        }
                        else if (ContentHashListReplacementCheckBehavior == ContentHashListReplacementCheckBehavior.AllowPinElision)
                        {
                            if (!Database.AssociatedContentNeedsPinning(ctx, strongFingerprint, contentHashListResult))
                            {
                                // Database guarantees that content is available without the need to pin, so do not replace
                                return false;
                            }
                        }
                        else
                        {
                            Contract.Assert(ContentHashListReplacementCheckBehavior == ContentHashListReplacementCheckBehavior.PinAlways);
                        }

                        return !await contentSession.EnsureContentIsAvailableAsync(
                            ctx,
                            Tracer.Name,
                            oldContentHashList,
                            automaticallyOverwriteContentHashLists,
                            ctx.Token).ConfigureAwait(false);
                    }

                    if (await canReplaceAsync())
                    {
                        // Replace if incoming has better determinism or some content for the existing
                        // entry is missing. The entry could have changed since we fetched the old value
                        // earlier, hence, we need to check it hasn't.
                        var exchanged = await Database.CompareExchangeAsync(
                           ctx,
                           strongFingerprint,
                           replacementToken,
                           oldContentHashListInfo,
                           contentHashListWithDeterminism).ThrowIfFailureAsync();
                        if (!exchanged)
                        {
                            // Our update lost, need to retry
                            continue;
                        }

                        // Returning null as the content hash list to indicate that the new value was accepted.
                        return new AddOrGetContentHashListResult(new ContentHashListWithDeterminism(null, determinism), contentHashCount: contentHashListWithDeterminism.ContentHashList?.Hashes?.Count);
                    }

                    // If we didn't accept the new value because it is the same as before, just with a not
                    // necessarily better determinism, then let the user know.
                    if (contentHashList.Equals(oldContentHashList))
                    {
                        return new AddOrGetContentHashListResult(new ContentHashListWithDeterminism(null, oldDeterminism), contentHashCount: oldContentHashList?.Hashes?.Count);
                    }

                    // If we didn't accept a deterministic tool's data, then we're in an inconsistent state
                    if (determinism.IsDeterministicTool)
                    {
                        return new AddOrGetContentHashListResult(
                            AddOrGetContentHashListResult.ResultCode.InvalidToolDeterminismError,
                            oldContentHashListInfo);
                    }

                    // If we did not accept the given value, return the value in the cache
                    return new AddOrGetContentHashListResult(oldContentHashListInfo);
                }

                return new AddOrGetContentHashListResult("Hit too many races attempting to add content hash list into the cache");
            },
            extraEndMessage: _ => $"StrongFingerprint=[{strongFingerprint}], ContentHashList=[{contentHashListWithDeterminism.ContentHashList}], Determinism=[{contentHashListWithDeterminism.Determinism}]",
            traceOperationStarted: false);
        }

        internal Task<Result<LevelSelectors>> GetLevelSelectorsAsync(Context context, Fingerprint weakFingerprint, CancellationToken cts, int level)
        {
            var ctx = new OperationContext(context);

            return ctx.PerformOperationAsync(_tracer, () => Database.GetLevelSelectorsAsync(ctx, weakFingerprint, level),
                extraEndMessage: _ => $"WeakFingerprint=[{weakFingerprint}], Level=[{level}]",
                traceOperationStarted: false);
        }
    }
}
