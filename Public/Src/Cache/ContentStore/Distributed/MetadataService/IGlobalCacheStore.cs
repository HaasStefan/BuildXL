// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.NuCache;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.MemoizationStore.Interfaces.Results;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;

namespace BuildXL.Cache.ContentStore.Distributed.MetadataService
{
    /// <summary>
    /// Interface that represents a central global store.
    /// </summary>
    public interface IGlobalCacheStore : IContentMetadataStore, IStartupShutdownSlim
    {
        
    }

    public interface IContentMetadataStore : IMetadataStore, IStartupShutdownSlim
    {
        /// <summary>
        /// Gets the list of <see cref="ContentLocationEntry"/> for every hash specified by <paramref name="contentHashes"/> from a central store.
        /// </summary>
        /// <remarks>
        /// The resulting collection (in success case) will have the same size as <paramref name="contentHashes"/>.
        /// </remarks>
        Task<Result<IReadOnlyList<ContentLocationEntry>>> GetBulkAsync(OperationContext context, IReadOnlyList<ShortHash> contentHashes);

        /// <summary>
        /// Notifies a central store that content represented by <paramref name="contentHashes"/> is available on a current machine.
        /// </summary>
        /// <remarks>
        /// Using <see cref="ValueTask{BoolResult}"/> instead of normal tasks because <see cref="GlobalCacheService.RegisterContentLocationsAsync"/> can
        /// do the registration synchronously, and using <code>ValueTask</code> allows achieving allocation free implementation that is very useful
        /// because this method can be called a lot at start time of the service.
        /// </remarks>
        ValueTask<BoolResult> RegisterLocationAsync(OperationContext context, MachineId machineId, IReadOnlyList<ShortHashWithSize> contentHashes, bool touch);

        /// <summary>
        /// Notifies a central store that content represented by <paramref name="contentHashes"/> is unavailable on a current machine.
        /// </summary>
        /// <remarks>
        /// Using <see cref="ValueTask{BoolResult}"/> instead of normal tasks because <see cref="GlobalCacheService.DeleteContentLocationsAsync"/> can
        /// do the registration synchronously, and using <code>ValueTask</code> allows achieving allocation free implementation that is very useful
        /// because this method can be called a lot at start time of the service.
        /// </remarks>
        ValueTask<BoolResult> DeleteLocationAsync(OperationContext context, MachineId machineId, IReadOnlyList<ShortHash> contentHashes);
    }

    public interface IMetadataStore : IStartupShutdownSlim, IName
    {
        /// <nodoc />
        Task<Result<bool>> CompareExchangeAsync(
            OperationContext context,
            StrongFingerprint strongFingerprint,
            SerializedMetadataEntry replacement,
            string expectedReplacementToken);

        /// <nodoc />
        Task<Result<LevelSelectors>> GetLevelSelectorsAsync(OperationContext context, Fingerprint weakFingerprint, int level);

        /// <nodoc />
        Task<Result<SerializedMetadataEntry>> GetContentHashListAsync(OperationContext context, StrongFingerprint strongFingerprint);

        /// <nodoc/>
        Task<GetStatsResult> GetStatsAsync(Context context);
    }

    public interface IMetadataStoreWithIncorporation : IMetadataStore
    {
        Task<BoolResult> IncorporateStrongFingerprintsAsync(OperationContext context, IEnumerable<Task<StrongFingerprint>> strongFingerprints);
    }

    public interface IMetadataStoreWithContentPinNotification: IMetadataStore
    {
        /// <summary>
        /// Notifies the store that all the content associated to the given strong fingerprint was pinned, allowing it to update any internal invariants
        /// </summary>
        Task<Result<bool>> NotifyContentWasPinnedAsync(OperationContext context, StrongFingerprint strongFingerprint);

        /// <summary>
        /// Notifies the store that content associated to the given strong fingerprint was pinned, but a place operation failed to find it, allowing it to update any internal invariants
        /// </summary>
        Task<Result<bool>> NotifyPinnedContentWasNotFoundAsync(OperationContext context, StrongFingerprint strongFingerprint);
    }
}
