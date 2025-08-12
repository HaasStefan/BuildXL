// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Utilities.Core;

namespace BuildXL.Engine.Cache.Artifacts
{
    /// <summary>
    /// Result from result <see cref="IArtifactContentCache.TryLoadAvailableContentAsync"/> for a single hash.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
    public readonly struct ContentAvailabilityResult
    {
        /// <summary>
        /// Number of bytes moved around as part of making this content available (if non-zero, suggests the content wasn't available initially).
        /// </summary>
        /// <remarks>
        /// TODO: Should clarify that this definition lines up with BuildCache's. Should maybe separate network vs. local transfer.
        /// </remarks>
        public readonly long BytesTransferred;

        /// <summary>
        /// Hash to which this result applies (in a batch query, each result's Hash is unique).
        /// </summary>
        public readonly ContentHash Hash;

        /// <summary>
        /// If true, the content with this hash is available in the cache for materialization.
        /// </summary>
        public readonly bool IsAvailable;

        /// <summary>
        /// The display name for the cache where the content originated.
        /// </summary>
        public readonly string SourceCache;

        /// <summary>
        /// Failure when ContentAvailabilityResult is unavailable
        /// </summary>
        public readonly Failure Failure;

        /// <summary>
        /// Optional remote content location.
        /// </summary>
        public readonly Uri RemoteContentLocation;

        /// <nodoc />
        public ContentAvailabilityResult(ContentHash hash, bool isAvailable, long bytesTransferred, string sourceCache, Failure failure = null, Uri remoteContentLocation = null)
        {
            Hash = hash;
            IsAvailable = isAvailable;
            BytesTransferred = bytesTransferred;
            SourceCache = sourceCache;
            Failure = failure;
            RemoteContentLocation = remoteContentLocation;
        }

        /// <inherit />
        public override string ToString()
        {
            return $"({Hash.ToString()}: is{(IsAvailable ? "" : " not")} available, {BytesTransferred}B transferred from {SourceCache}){(Failure != null ? " due to Failure: " + Failure.Describe() : "")}";
        }
    }
}
