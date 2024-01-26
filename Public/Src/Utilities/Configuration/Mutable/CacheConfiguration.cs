// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using BuildXL.Utilities.Core;

namespace BuildXL.Utilities.Configuration.Mutable
{
    /// <nodoc />
    public sealed class CacheConfiguration : ICacheConfiguration
    {
        /// <summary>
        /// Default max path sets to check during cache lookup when limiting path set check is enabled.
        /// </summary>
        /// <remarks>
        /// Kusto query shows that the average number of path sets visited during cache lookup before getting a cache hit is 6.
        /// This default simply doubles that average number.
        /// </remarks>
        public const int DefaultMaxPathSetsOnCacheLookupWhenEnabled = 12;

        /// <nodoc />
        public CacheConfiguration()
        {
            CacheConfigFile = FileArtifact.Invalid;
            CacheSpecs = SpecCachingOption.Auto;
            Incremental = true;
            CacheGraph = true;
            CacheSalt = null;
            CacheSessionName = string.Empty;
            AllowFetchingCachedGraphFromContentCache = true;
            MinimumReplicaCountForStrongGuarantee = 3;
            StrongContentGuaranteeRefreshProbability = 1;
            FileChangeTrackingExclusionRoots = new List<AbsolutePath>();
            FileChangeTrackingInclusionRoots = new List<AbsolutePath>();
            ReplaceExistingFileOnMaterialization = false;
            ElideMinimalGraphEnumerationAbsentPathProbes = true;
            AugmentWeakFingerprintRequiredPathCommonalityFactor = 1;
            MonitorAugmentedPathSets = 0;
            HonorDirectoryCasingOnDisk = false;
            AllowReuseOfWeakIdenityForSourceFiles = false;
            CacheLogToKusto = false;
        }

        /// <nodoc />
        public CacheConfiguration(ICacheConfiguration template, PathRemapper pathRemapper)
        {
            Contract.Assume(template != null);
            Contract.Assume(pathRemapper != null);

            CacheConfigFile = pathRemapper.Remap(template.CacheConfigFile);
            CacheLogFilePath = pathRemapper.Remap(template.CacheLogFilePath);
            Incremental = template.Incremental;
            CacheGraph = template.CacheGraph;
            CachedGraphPathToLoad = pathRemapper.Remap(template.CachedGraphPathToLoad);
            CachedGraphIdToLoad = template.CachedGraphIdToLoad;
            CachedGraphLastBuildLoad = template.CachedGraphLastBuildLoad;
            CacheSpecs = template.CacheSpecs;
            CacheSessionName = template.CacheSessionName;
            ArtificialCacheMissOptions = template.ArtificialCacheMissConfig == null ? null : new ArtificialCacheMissConfig(template.ArtificialCacheMissConfig);
            CacheSalt = template.CacheSalt;
            HistoricMetadataCache = template.HistoricMetadataCache;
            AllowFetchingCachedGraphFromContentCache = template.AllowFetchingCachedGraphFromContentCache;
            MinimumReplicaCountForStrongGuarantee = template.MinimumReplicaCountForStrongGuarantee;
            StrongContentGuaranteeRefreshProbability = template.StrongContentGuaranteeRefreshProbability;
            FileChangeTrackingExclusionRoots = pathRemapper.Remap(template.FileChangeTrackingExclusionRoots);
            FileChangeTrackingInclusionRoots = pathRemapper.Remap(template.FileChangeTrackingInclusionRoots);
            UseDedupStore = template.UseDedupStore;
            ReplaceExistingFileOnMaterialization = template.ReplaceExistingFileOnMaterialization;
            VfsCasRoot = pathRemapper.Remap(template.VfsCasRoot);
            VirtualizeUnknownPips = template.VirtualizeUnknownPips;
            ElideMinimalGraphEnumerationAbsentPathProbes = template.ElideMinimalGraphEnumerationAbsentPathProbes;
            AugmentWeakFingerprintPathSetThreshold = template.AugmentWeakFingerprintPathSetThreshold;
            AugmentWeakFingerprintRequiredPathCommonalityFactor = template.AugmentWeakFingerprintRequiredPathCommonalityFactor;
            MonitorAugmentedPathSets = template.MonitorAugmentedPathSets;
            MaxPathSetsOnCacheLookup = template.MaxPathSetsOnCacheLookup;
            UseLocalOnly = template.UseLocalOnly;
            HonorDirectoryCasingOnDisk = template.HonorDirectoryCasingOnDisk;
            AllowReuseOfWeakIdenityForSourceFiles = template.AllowReuseOfWeakIdenityForSourceFiles;
            FileContentTableEntryTimeToLive = template.FileContentTableEntryTimeToLive;
            CacheLogToKusto = template.CacheLogToKusto;
        }

        /// <nodoc />
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public AbsolutePath CacheConfigFile { get; set; }

        /// <inheritdoc />
        AbsolutePath ICacheConfiguration.CacheConfigFile => CacheConfigFile;

        /// <inheritdoc />
        public AbsolutePath CacheLogFilePath { get; set; }

        /// <inheritdoc />
        public bool Incremental { get; set; }

        /// <inheritdoc />
        public ushort? FileContentTableEntryTimeToLive { get; set; }

        /// <inheritdoc />
        public bool AllowFetchingCachedGraphFromContentCache { get; set; }

        /// <inheritdoc />
        public bool CacheGraph { get; set; }

        /// <inheritdoc />
        public AbsolutePath CachedGraphPathToLoad { get; set; }

        /// <inheritdoc />
        public string CachedGraphIdToLoad { get; set; }

        /// <inheritdoc />
        public bool CachedGraphLastBuildLoad { get; set; }

        /// <inheritdoc />
        public SpecCachingOption CacheSpecs { get; set; }

        /// <inheritdoc />
        public string CacheSessionName { get; set; }

        /// <nodoc />
        public ArtificialCacheMissConfig ArtificialCacheMissOptions { get; set; }

        /// <inheritdoc />
        IArtificialCacheMissConfig ICacheConfiguration.ArtificialCacheMissConfig => ArtificialCacheMissOptions;

        /// <inheritdoc />
        public string CacheSalt { get; set; }

        /// <inheritdoc />
        public bool ElideMinimalGraphEnumerationAbsentPathProbes { get; set; }

        /// <inheritdoc />
        public bool? HistoricMetadataCache { get; set; }

        /// <inheritdoc />
        public byte MinimumReplicaCountForStrongGuarantee { get; set; }

        /// <inheritdoc />
        public double StrongContentGuaranteeRefreshProbability { get; set; }

        /// <nodoc />
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public List<AbsolutePath> FileChangeTrackingExclusionRoots { get; set; }

        /// <inheritdoc />
        IReadOnlyList<AbsolutePath> ICacheConfiguration.FileChangeTrackingExclusionRoots => FileChangeTrackingExclusionRoots;

        /// <nodoc />
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public List<AbsolutePath> FileChangeTrackingInclusionRoots { get; set; }

        /// <inheritdoc />
        IReadOnlyList<AbsolutePath> ICacheConfiguration.FileChangeTrackingInclusionRoots => FileChangeTrackingInclusionRoots;

        /// <inheritdoc />
        public bool UseDedupStore { get; set; }

        /// <inheritdoc />
        public bool ReplaceExistingFileOnMaterialization { get; set; }

        /// <inheritdoc />
        public AbsolutePath VfsCasRoot { get; set; }

        /// <inheritdoc />
        public bool VirtualizeUnknownPips { get; set; }

        /// <inheritdoc />
        public int AugmentWeakFingerprintPathSetThreshold { get; set; }

        /// <inheritdoc />
        public double AugmentWeakFingerprintRequiredPathCommonalityFactor { get; set; }

        /// <inheritdoc />
        public int MonitorAugmentedPathSets { get; set; }

        /// <inheritdoc />
        public int MaxPathSetsOnCacheLookup { get; set; }

        /// <inheritdoc />
        public bool? UseLocalOnly { get; set; }

        /// <inheritdoc/>
        public bool HonorDirectoryCasingOnDisk { get; set; }

        /// <inheritdoc/>
        public bool AllowReuseOfWeakIdenityForSourceFiles { get; set; }

        /// <inheritdoc/>
        public bool CacheLogToKusto { get; set; }
    }
}
