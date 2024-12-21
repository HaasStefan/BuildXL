// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Linq;
using BuildXL.Interop.Unix;
using BuildXL.Utilities.Core;

namespace BuildXL.Utilities.Configuration.Mutable
{
    /// <nodoc />
    public sealed class ScheduleConfiguration : IScheduleConfiguration
    {
        /// <nodoc />
        public ScheduleConfiguration()
        {
            PinCachedOutputs = true;
            EnableLazyOutputMaterialization = true;
            UseHistoricalPerformanceInfo = true;
            TreatDirectoryAsAbsentFileOnHashingInputContent = true;
            MaximumRamUtilizationPercentage = 90;
            MaximumCommitUtilizationPercentage = 95;
            CriticalCommitUtilizationPercentage = 98;
            MaximumAllowedMemoryPressureLevel = Memory.PressureLevel.Normal;

            AllowCopySymlink = true;
            ForceSkipDependencies = ForceSkipDependenciesMode.Disabled;
            UseHistoricalRamUsageInfo = true;
            ForceUseEngineInfoFromCache = false;

            // In the past, we decided to use 1.25 * logicalCores to determine the concurrency for the process pips. 
            // However, at that time, cachelookup, materialization, and other non-execute steps were all taking a slot from that. 
            // As each major step runs in its own queue with a separate concurrency limit, we decided to revise using 1.25 multiplier.
            // After doing A/B testing on thousands of builds, using 0.9 instead of 1.25 multiplier decreases the load on the machine and improves the perf:
            // https://github.com/microsoft/BuildXL/blob/main/Documentation/Specs/SchedulerPerfExperiments.md
            MaxProcesses = (int)Math.Ceiling(0.9 * Environment.ProcessorCount);

            MaxIO = Environment.ProcessorCount;

            MaxLight = 1000;
            MaxIpc = 1000;

            // We decide the concurrency levels based on A/B testing results.
            // https://github.com/microsoft/BuildXL/blob/main/Documentation/Specs/SchedulerPerfExperiments.md
            MaxCacheLookup = Environment.ProcessorCount; 
            MaxMaterialize = Environment.ProcessorCount;

            MaxChooseWorkerCpu = 5;
            MaxChooseWorkerCacheLookup = 1;
            MaxChooseWorkerLight = 1;

            CanonicalizeFilterOutputs = true;

            UnsafeDisableGraphPostValidation = false;
            UnsafeLazySODeletion = false;

            ProcessRetries = 0;

            StoreOutputsToCache = true;

            // TODO: Fix me.
            EnableLazyWriteFileMaterialization = false;

            // TODO: Should this ever be enabled? Default to on outside of CloudBuild for now.
            WriteIpcOutput = true;

            InferNonExistenceBasedOnParentPathInRealFileSystem = true;

            OutputMaterializationExclusionRoots = new List<AbsolutePath>();

            IncrementalScheduling = false;
            ComputePipStaticFingerprints = false;
            LogPipStaticFingerprintTexts = false;

            CreateHandleWithSequentialScanOnHashingOutputFiles = false;
            OutputFileExtensionsForSequentialScanHandleOnHashing = new List<PathAtom>();

            TelemetryTagPrefix = null;

            SkipHashSourceFile = false;

            UnsafeDisableSharedOpaqueEmptyDirectoryScrubbing = false;
            InputChanges = AbsolutePath.Invalid;
            UpdateFileContentTableByScanningChangeJournal = true;

            EnableSetupCostWhenChoosingWorker = false;
            EnableLessAggressiveMemoryProjection = false;
            MaxRetriesDueToRetryableFailures = 5;
            MaxRetriesDueToLowMemory = 20; // Based on telemetry, p99 of the retries due to the low memory is 14. 

            PluginLocations = new List<AbsolutePath>();
            TreatAbsentDirectoryAsExistentUnderOpaque = true;

            EnableProcessRemoting = false;
            ProcessCanRunRemoteTags = new List<string>();
            ProcessMustRunLocalTags = new List<string>();
            RemotingThresholdMultiplier = 1.5;

            // Based on telemetry P90 of waiting time is 3s.
            RemoteAgentWaitTimeSec = 3.0;

            // The following is left commented so that it becomes handy to do experiments
            // particularly in the environment (pipeline) where setting extra
            // arguments is not possible (or not under our control).
            // NumOfRemoteAgentLeases = 80;

            // By default we will avoid querying the L3 cache after 2 consecutive misses
            // in a dependency chain
            RemoteCacheCutoff = true;
            RemoteCacheCutoffLength = 2;

            // The default value is 0.9, meaning the RAM semaphore limit is set to 90% of the available RAM.
            RamSemaphoreMultiplier = 0.9;
        }

        /// <nodoc />
        public ScheduleConfiguration(IScheduleConfiguration template, PathRemapper pathRemapper)
        {
            Contract.Assume(template != null);

            MaxProcesses = template.MaxProcesses;
            MaxLight = template.MaxLight;
            MaxIpc = template.MaxIpc;
            MaxIO = template.MaxIO;
            MaxChooseWorkerCpu = template.MaxChooseWorkerCpu;
            MaxChooseWorkerLight = template.MaxChooseWorkerLight;
            MaxChooseWorkerCacheLookup = template.MaxChooseWorkerCacheLookup;

            MaxCacheLookup = template.MaxCacheLookup;
            MaxMaterialize = template.MaxMaterialize;
            EnvironmentFingerprint = template.EnvironmentFingerprint;

            DisableProcessRetryOnResourceExhaustion = template.DisableProcessRetryOnResourceExhaustion;
            StopOnFirstError = template.StopOnFirstError;
            StopOnFirstInternalError = template.StopOnFirstInternalError;
            LowPriority = template.LowPriority;
            EnableLazyOutputMaterialization = template.EnableLazyOutputMaterialization;
            ForceSkipDependencies = template.ForceSkipDependencies;
            UseHistoricalPerformanceInfo = template.UseHistoricalPerformanceInfo;
            RequiredOutputMaterialization = template.RequiredOutputMaterialization;
            TreatDirectoryAsAbsentFileOnHashingInputContent = template.TreatDirectoryAsAbsentFileOnHashingInputContent;
            MaximumRamUtilizationPercentage = template.MaximumRamUtilizationPercentage;
            MinimumDiskSpaceForPipsGb = template.MinimumDiskSpaceForPipsGb;
            MaximumAllowedMemoryPressureLevel = template.MaximumAllowedMemoryPressureLevel;
            AllowCopySymlink = template.AllowCopySymlink;
            AdaptiveIO = template.AdaptiveIO;
            ReuseOutputsOnDisk = template.ReuseOutputsOnDisk;
            UseHistoricalRamUsageInfo = template.UseHistoricalRamUsageInfo;
            VerifyCacheLookupPin = template.VerifyCacheLookupPin;
            PinCachedOutputs = template.PinCachedOutputs;
            CanonicalizeFilterOutputs = template.CanonicalizeFilterOutputs;
            ForceUseEngineInfoFromCache = template.ForceUseEngineInfoFromCache;

            UnsafeDisableGraphPostValidation = template.UnsafeDisableGraphPostValidation;

            ProcessRetries = template.ProcessRetries;
            StoreOutputsToCache = template.StoreOutputsToCache;

            EnableLazyWriteFileMaterialization = template.EnableLazyWriteFileMaterialization;
            WriteIpcOutput = template.WriteIpcOutput;
            OutputMaterializationExclusionRoots = pathRemapper.Remap(template.OutputMaterializationExclusionRoots);

            IncrementalScheduling = template.IncrementalScheduling;
            ComputePipStaticFingerprints = template.ComputePipStaticFingerprints;
            LogPipStaticFingerprintTexts = template.LogPipStaticFingerprintTexts;

            CreateHandleWithSequentialScanOnHashingOutputFiles = template.CreateHandleWithSequentialScanOnHashingOutputFiles;
            OutputFileExtensionsForSequentialScanHandleOnHashing =
                new List<PathAtom>(template.OutputFileExtensionsForSequentialScanHandleOnHashing.Select(pathRemapper.Remap));

            TelemetryTagPrefix = template.TelemetryTagPrefix;

            OrchestratorCpuMultiplier = template.OrchestratorCpuMultiplier;
            SkipHashSourceFile = template.SkipHashSourceFile;

            UnsafeDisableSharedOpaqueEmptyDirectoryScrubbing = template.UnsafeDisableSharedOpaqueEmptyDirectoryScrubbing;
            UnsafeLazySODeletion = template.UnsafeLazySODeletion;
            UseFixedApiServerMoniker = template.UseFixedApiServerMoniker;
            InputChanges = pathRemapper.Remap(template.InputChanges);
            UpdateFileContentTableByScanningChangeJournal = template.UpdateFileContentTableByScanningChangeJournal;
            CacheOnly = template.CacheOnly;
            EnableSetupCostWhenChoosingWorker = template.EnableSetupCostWhenChoosingWorker;
            MaximumCommitUtilizationPercentage = template.MaximumCommitUtilizationPercentage;
            CriticalCommitUtilizationPercentage = template.CriticalCommitUtilizationPercentage;
            DelayedCacheLookupMinMultiplier = template.DelayedCacheLookupMinMultiplier;
            DelayedCacheLookupMaxMultiplier = template.DelayedCacheLookupMaxMultiplier;
            MaxRetriesDueToLowMemory = template.MaxRetriesDueToLowMemory;
            MaxRetriesDueToRetryableFailures = template.MaxRetriesDueToRetryableFailures;
            EnableLessAggressiveMemoryProjection = template.EnableLessAggressiveMemoryProjection;
            ManageMemoryMode = template.ManageMemoryMode;
            EnablePlugin = template.EnablePlugin;
            PluginLocations = pathRemapper.Remap(template.PluginLocations);
            TreatAbsentDirectoryAsExistentUnderOpaque = template.TreatAbsentDirectoryAsExistentUnderOpaque;
            MaxWorkersPerModule = template.MaxWorkersPerModule;
            ModuleAffinityLoadFactor = template.ModuleAffinityLoadFactor;
            UseHistoricalCpuUsageInfo = template.UseHistoricalCpuUsageInfo;

            EnableProcessRemoting = template.EnableProcessRemoting;
            NumOfRemoteAgentLeases = template.NumOfRemoteAgentLeases;
            ProcessCanRunRemoteTags = new List<string>(template.ProcessCanRunRemoteTags);
            ProcessMustRunLocalTags = new List<string>(template.ProcessMustRunLocalTags);
            RemotingThresholdMultiplier = template.RemotingThresholdMultiplier;
            RemoteAgentWaitTimeSec = template.RemoteAgentWaitTimeSec;

            StopDirtyOnSucceedFastPips = template.StopDirtyOnSucceedFastPips;
            CpuResourceAware = template.CpuResourceAware;

            RemoteCacheCutoff = template.RemoteCacheCutoff;
            RemoteCacheCutoffLength = template.RemoteCacheCutoffLength;

            DeprioritizeOnSemaphoreConstraints = template.DeprioritizeOnSemaphoreConstraints;
            RamSemaphoreMultiplier = template.RamSemaphoreMultiplier;
        }

        /// <inheritdoc />
        public bool StopOnFirstError { get; set; }

        /// <inheritdoc />
        public bool? StopOnFirstInternalError { get; set; }

        /// <inheritdoc />
        public int MaxProcesses { get; set; }

        /// <inheritdoc />
        public int MaxMaterialize { get; set; }

        /// <inheritdoc/>
        public bool StopDirtyOnSucceedFastPips { get; set; }

        /// <inheritdoc />
        public int MaxCacheLookup { get; set; }

        /// <inheritdoc />
        public int MaxIO { get; set; }

        /// <inheritdoc />
        public int MaxChooseWorkerCpu { get; set; }

        /// <inheritdoc />
        public int MaxChooseWorkerLight { get; set; }

        /// <inheritdoc />
        public int MaxChooseWorkerCacheLookup { get; set; }

        /// <inheritdoc />
        public bool LowPriority { get; set; }

        /// <inheritdoc />
        public bool AdaptiveIO { get; set; }

        /// <inheritdoc />
        public bool DisableProcessRetryOnResourceExhaustion { get; set; }

        /// <inheritdoc />
        public bool EnableLazyOutputMaterialization
        {
            get
            {
                return RequiredOutputMaterialization != RequiredOutputMaterialization.All;
            }

            set
            {
                // Only set this if not already equal to its boolean equivalent
                // (i.e., setting to true when existing is minimal
                // will not change the value)
                if (EnableLazyOutputMaterialization != value)
                {
                    RequiredOutputMaterialization = value ?
                        RequiredOutputMaterialization.Explicit :
                        RequiredOutputMaterialization.All;
                }
            }
        }

        /// <inheritdoc />
        public ForceSkipDependenciesMode ForceSkipDependencies { get; set; }

        /// <inheritdoc />
        public bool VerifyCacheLookupPin { get; set; }

        /// <inheritdoc />
        public bool PinCachedOutputs { get; set; }

        /// <inheritdoc />
        public bool UseHistoricalPerformanceInfo { get; set; }

        /// <inheritdoc />
        public bool ForceUseEngineInfoFromCache { get; set; }
        
        /// <inheritdoc />
        public bool UseHistoricalRamUsageInfo { get; set; }

        /// <inheritdoc />
        public RequiredOutputMaterialization RequiredOutputMaterialization { get; set; }

        /// <nodoc />
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public List<AbsolutePath> OutputMaterializationExclusionRoots { get; set; }

        /// <inheritdoc />
        IReadOnlyList<AbsolutePath> IScheduleConfiguration.OutputMaterializationExclusionRoots => OutputMaterializationExclusionRoots;

        /// <inheritdoc />
        public int MaxLight { get; set; }

        /// <inheritdoc />
        public int MaxIpc { get; set; }

        /// <inheritdoc />
        public bool TreatDirectoryAsAbsentFileOnHashingInputContent { get; set; }

        /// <inheritdoc />
        public bool AllowCopySymlink { get; set; }

        /// <inheritdoc />
        public int MaximumRamUtilizationPercentage { get; set; }

        /// <inheritdoc />
        public Memory.PressureLevel MaximumAllowedMemoryPressureLevel { get; set; }

        /// <nodoc />
        public int MinimumWorkers { get; set; }

        /// <summary>
        /// Checks up-to-dateness of files on disk during cache lookup using USN journal.
        /// </summary>
        public bool ReuseOutputsOnDisk { get; set; }

        /// <inheritdoc />
        /// <remarks>
        /// TODO: Remove this!
        /// </remarks>
        public bool UnsafeDisableGraphPostValidation { get; set; }

        /// <inheritdoc />
        public bool UnsafeLazySODeletion { get; set; }

        /// <inheritdoc />
        public string EnvironmentFingerprint { get; set; }

        /// <inheritdoc />
        public bool CanonicalizeFilterOutputs { get; set; }

        /// <inheritdoc />
        public bool ScheduleMetaPips { get; set; }

        /// <inheritdoc />
        public int ProcessRetries { get; set; }

        /// <inheritdoc />
        public bool EnableLazyWriteFileMaterialization { get; set; }

        /// <inheritdoc />
        public bool WriteIpcOutput { get; set; }

        /// <inheritdoc />
        public bool StoreOutputsToCache { get; set; }

        /// <inheritdoc />
        public bool InferNonExistenceBasedOnParentPathInRealFileSystem { get; set; }

        /// <inheritdoc />
        public bool IncrementalScheduling { get; set; }

        /// <inheritdoc />
        public bool ComputePipStaticFingerprints { get; set; }

        /// <inheritdoc />
        public bool LogPipStaticFingerprintTexts { get; set; }

        /// <inheritdoc />
        public bool CreateHandleWithSequentialScanOnHashingOutputFiles { get; set; }

        /// <nodoc />
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public List<PathAtom> OutputFileExtensionsForSequentialScanHandleOnHashing { get; set; }

        /// <inheritdoc />
        IReadOnlyList<PathAtom> IScheduleConfiguration.OutputFileExtensionsForSequentialScanHandleOnHashing => OutputFileExtensionsForSequentialScanHandleOnHashing;

        /// <inheritdoc />
        public string TelemetryTagPrefix { get; set; }

        /// <inheritdoc />
        public double? OrchestratorCpuMultiplier { get; set; }

        /// <inheritdoc />
        public bool SkipHashSourceFile { get; set; }

        /// <inheritdoc />
        public bool UnsafeDisableSharedOpaqueEmptyDirectoryScrubbing { get; set; }

        /// <inheritdoc />
        public bool? UseHistoricalCpuUsageInfo { get; set; }

        /// <inheritdoc />
        public bool UseFixedApiServerMoniker { get; set; }

        /// <inheritdoc />
        public AbsolutePath InputChanges { get; set; }

        /// <inheritdoc />
        public int? MinimumDiskSpaceForPipsGb { get; set; }

        /// <inheritdoc />
        public int MaxRetriesDueToLowMemory { get; set; }

        /// <inheritdoc />
        public int MaxRetriesDueToRetryableFailures { get; set; }

        /// <inheritdoc />
        public bool CacheOnly { get; set; }

        /// <inheritdoc />
        public bool EnableSetupCostWhenChoosingWorker { get; set;  }

        /// <inheritdoc />
        public int MaximumCommitUtilizationPercentage { get; set; }

        /// <inheritdoc />
        public int CriticalCommitUtilizationPercentage { get; set; }

        /// <inheritdoc />
        public double? DelayedCacheLookupMinMultiplier { get; set; }

        /// <inheritdoc />
        public double? DelayedCacheLookupMaxMultiplier { get; set; }

        /// <inheritdoc />
        public bool EnableLessAggressiveMemoryProjection { get; set; }

        /// <inheritdoc />
        public bool EnableEmptyingWorkingSet { get; set; }

        /// <inheritdoc />
        public ManageMemoryMode? ManageMemoryMode { get; set; }

        /// <inheritdoc />
        public bool? DisableCompositeOpaqueFilters { get; set; }

        /// <inheritdoc />
        public bool? EnablePlugin { get; set; }

        /// <nodoc />
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public List<AbsolutePath> PluginLocations { get; set; }

        /// <inheritdoc />
        IReadOnlyList<AbsolutePath> IScheduleConfiguration.PluginLocations => PluginLocations;

        /// <inheritdoc />
        public bool TreatAbsentDirectoryAsExistentUnderOpaque { get; set; }

        /// <inheritdoc />
        public int? MaxWorkersPerModule { get; set; }

        /// <inheritdoc />
        public double? ModuleAffinityLoadFactor { get; set; }

        /// <inheritdoc />
        public bool UpdateFileContentTableByScanningChangeJournal { get; set; }

        /// <inheritdoc />
        public bool EnableProcessRemoting { get; set; }

        /// <inheritdoc />
        public int? NumOfRemoteAgentLeases { get; set; }

        /// <inheritdoc />
        public List<string> ProcessCanRunRemoteTags { get; set; }

        /// <inheritdoc />
        IReadOnlyList<string> IScheduleConfiguration.ProcessCanRunRemoteTags => ProcessCanRunRemoteTags;

        /// <inheritdoc />
        public List<string> ProcessMustRunLocalTags { get; set; }

        /// <inheritdoc />
        IReadOnlyList<string> IScheduleConfiguration.ProcessMustRunLocalTags => ProcessMustRunLocalTags;

        /// <inheritdoc />
        public int EffectiveMaxProcesses => MaxProcesses + (EnableProcessRemoting ? (NumOfRemoteAgentLeasesValue < 0 ? 0 : NumOfRemoteAgentLeasesValue) : 0);

        /// <inheritdoc />
        public double RemotingThresholdMultiplier { get; set; }

        /// <inheritdoc />
        public double RemoteAgentWaitTimeSec { get; set; }

        /// <inheritdoc />
        public int RemoteCacheCutoffLength { get; set; }

        /// <inheritdoc />
        public bool RemoteCacheCutoff { get; set; }

        private int NumOfRemoteAgentLeasesValue => NumOfRemoteAgentLeases ?? (int)(2.5 * MaxProcesses);

        /// <inheritdoc />
        public bool CpuResourceAware { get; set; }

        /// <inheritdoc />
        public bool DeprioritizeOnSemaphoreConstraints { get; set; }

        /// <inheritdoc />
        public double RamSemaphoreMultiplier { get; set; }
    }
}
