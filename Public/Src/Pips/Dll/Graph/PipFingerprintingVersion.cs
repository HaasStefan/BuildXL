// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BuildXL.Storage.Fingerprints;

namespace BuildXL.Pips.Graph
{
    /// <summary>
    /// Version for breaking changes in pip fingerprinting (or what is stored per fingerprint).
    /// These versions are used to salt the fingerprint, so versions can live side by side in the same database.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1008:EnumsShouldHaveZeroValue")]
    public enum PipFingerprintingVersion
    {
        // IMPORTANT: These identifiers must only always increase and never overlap with a prior value. They are used
        // when we have to rev the serialization format of the PipCacheDescriptor

        /// <summary>
        /// V2 scheme in which weak and strong content fingerprints are distinguished.
        /// Increment the value below when changes to Detours are made, so the cache is invalidated.
        /// </summary>
        /// <remarks>
        /// Add reason for bumping up the version. In this way, you get git conflict if two or more people are bumping up the version at the same time.
        /// 
        /// 46: Add ReparsePointInfo to the cache metadata.
        /// 47: Switch <see cref="BuildXL.Storage.ContentHashingUtilities.CreateSpecialValue(byte)"/> used for <see cref="WellKnownContentHashes.AbsentFile"/> to use first byte instead of last byte.
        /// 48: Belated bump for removing unsafe_allowMissingOutputs option (unsafe arguments are part of the path set).
        /// 49: Added IgnoreDynamicWritesOnAbsentProbes to IUnsafeSandboxConfiguration.
        /// 51: 50 is already used in a patched build (20180914.8.4).
        /// 52: Detours detects file probes using CreateFileW/NtCreateFile/ZwCreateFile.
        /// 53: Added UnsafeDoubleWriteErrorsAreWarnings option.
        /// 54: Added AbsentPathProbeUnderOpaquesMode to Process and WeakFingerPrint.
        /// 55: Changed FileMaterializationInfo/FileContentInfo bond serialization.
        /// 56: Added NeedsToRunInContainer, ContainerIsolationLevel and DoubleWritePolicy.
        /// 57: Fixed enumeration in StoreContentForProcessAndCreateCacheEntryAsync.
        /// 58: Added RequiresAdmin field into the process pip.
        /// 59: Report all accesses under shared opaque fix.
        /// 60: Save AbsolutePath in the StaticOutputHashes.
        /// 62: FileContentInfo - change how length/existence is stored.
        /// 63: IncrementalTool - change reparsepoint probes and enumeration probes to read.
        /// 65: 64 is already used since 20190903; change in UnsafeOption serialization (partial preserve outputs).
        /// 66: Changed rendering of VSO hashes.
        /// 67: Added SourceChangeAffectedContents.
        /// 68: Added ChildProcessesToBreakawayFromSandbox.
        /// 69: Added dynamic existing probe.
        /// 70: Removed duplicates from ObservedAccessedFileNames.
        /// 71: Rename fields in weak fingerprint.
        /// 72: Added PreserveoutputsTrustLevel.
        /// 73: Added Trust statically declared accesses.
        /// 74: Added IgnoreCreateProcessReport in IUnsafeSandboxConfiguration.
        /// 75: Changed the type of <see cref="Utilities.Configuration.IUnsafeSandboxConfiguration.IgnoreDynamicWritesOnAbsentProbes"/> 
        ///     from <c>bool</c> to <see cref="Utilities.Configuration.DynamicWriteOnAbsentProbePolicy"/>.
        /// 76: Put extra salt's options in weakfingerprint instead of ExecutionAndFingerprintOptionsHash.
        /// 77: Change semantics related to tracking dependencies under untracked scopes.
        /// 78: Add session id and related session of the build.
        /// 79: Change the field name in unsafe option from "PreserveOutputInfo" to nameof(PreserveOutputsInfo).
        /// 80: Added ProbeDirectorySymlinkAsDirectory in IUnsafeSandboxConfiguration.
        /// 81: Add OutputDirectoryContents for SealDirectories.
        /// 82: Add ProcessSymlinkedAcceses in SandboxConfiguration.
        /// 83: Add PreservePathSetCasing in Process.Options.
        /// 84: Added IgnoreFullReparsePointResolving in IUnsafeSandboxConfiguration.
        /// 85: Added WritingToStandardErrorFailsPip in Process.Options.
        /// 86: Normalize casing of ObservedAccessedFileNames.
        /// 87: Changed the cleaning logic of SOD for retries.
        /// 88: ObservationFlags are preserved for AbsentPathProbe.
        /// 89: Add unsafe sandbox option SkipFlaggingSharedOpaqueOutputs.
        /// 90: Add RetryAttempEnvironmentVariable.
        /// 91: Add CreatedDirectories to PipCacheDescriptorV2Metadata.
        /// 92: Add IgnorePreserveOutputsPrivatization in IUnsafeSandboxConfiguration.
        /// 93: Add succeed fast exit codes.
        /// 94: Detours no longer report chain of symlinks when symlinks are opened with FILE_FLAG_OPEN_REPARSE_POINT.
        /// 95: Add ActionKind to CompositeSharedOpaqueSealDirectory
        /// 96: Add DisableFullReparsePointResolving at the pip level
        /// 97: Download resolver schedules real pips
        /// 98: Update search path filter computation to track visited files and directories separately in "BuildXL.Scheduler.Fingerprints.ObservedInputProcessor.ComputeSearchPathsAndFilter".
        /// 99: Alien file enumerations are cached
        /// 100: Alien file enumerations exclude untracked artifacts
        /// 101: Direct dirty should apply to all pips, not just filtered pips
        /// 102: Add dynamic absent path probe observations to incremental scheduling state
        /// 103: Add apply-allow-list logic on dynamic (shared opaque) outputs.
        /// 104: Migrate Bond to Google.Protobuf
        /// 105: Add observation reclassification rules
        /// </remarks>
        TwoPhaseV2 = 105,

        /* 
         * We do not want to bump the fingerprint version more than needed, so we will accumulate the tasks to do when we really need to bump the fingerprint version:
         ************ TODOs ******************
         * 1) Remove UnexpectedFileAccessesAreErrors from ExtraFingerprintSalts.cs 
         */
    }
}