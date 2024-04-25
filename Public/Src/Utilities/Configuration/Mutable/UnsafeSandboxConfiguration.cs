// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BuildXL.Utilities.Core;

namespace BuildXL.Utilities.Configuration.Mutable
{
    /// <nodoc />
    public sealed class UnsafeSandboxConfiguration : IUnsafeSandboxConfiguration
    {
        /// <nodoc />
        public UnsafeSandboxConfiguration()
        {
            MonitorFileAccesses = true;
            MonitorNtCreateFile = true;
            UnexpectedFileAccessesAreErrors = true;
            IgnoreReparsePoints = false;
            // On Windows, full reparse point resolution is turned off by default. On Linux,
            // the sandbox always resolves reparse points and this flag is ignored. However,
            // the flag is inspected on managed side to resolve absent-and-later-present probes
            IgnoreFullReparsePointResolving = OperatingSystemHelper.IsWindowsOS;
            IgnorePreloadedDlls = false;
            SandboxKind = SandboxKind.Default;

            ExistingDirectoryProbesAsEnumerations = false;
            IgnoreZwRenameFileInformation = false;
            IgnoreZwOtherFileInformation = false;
            IgnoreNonCreateFileReparsePoints = false;
            IgnoreSetFileInformationByHandle = false;
            PreserveOutputs = PreserveOutputsMode.Disabled;
            PreserveOutputsTrustLevel = (int)PreserveOutputsTrustValue.Lowest;
            IgnorePreserveOutputsPrivatization = false;
            IgnoreGetFinalPathNameByHandle = false;
            MonitorZwCreateOpenQueryFile = true;
            IgnoreDynamicWritesOnAbsentProbes = DynamicWriteOnAbsentProbePolicy.IgnoreDirectoryProbes; // TODO: eventually change this to IgnoreNothing
            IgnoreUndeclaredAccessesUnderSharedOpaques = false;
            DoNotApplyAllowListToDynamicOutputs = false;

            // Note that this flag is only relevant for the Windows sandbox because directory symlink 
            ProbeDirectorySymlinkAsDirectory = OperatingSystemHelper.IsWindowsOS;

            if (EngineVersion.Version < 1)
            {
                IgnoreCreateProcessReport = true;
            }

            // Make sure to update SafeOptions below if necessary when new flags are added
        }

        /// <summary>
        /// Object representing which options are safe. Generally this is just the defaults from above, except for
        /// when the defaults represent an unsafe mode of operation. In that case, the safe mode must be specified here.
        /// </summary>
        public static readonly IUnsafeSandboxConfiguration SafeOptions = new UnsafeSandboxConfiguration()
        {
            IgnorePreloadedDlls = false,
            IgnoreCreateProcessReport = false,
            IgnoreDynamicWritesOnAbsentProbes = DynamicWriteOnAbsentProbePolicy.IgnoreNothing,
            ProbeDirectorySymlinkAsDirectory = false,
            DoNotApplyAllowListToDynamicOutputs = false,
        };

        /// <nodoc />
        public UnsafeSandboxConfiguration(IUnsafeSandboxConfiguration template)
        {
            MonitorFileAccesses = template.MonitorFileAccesses;
            MonitorNtCreateFile = template.MonitorNtCreateFile;
            MonitorZwCreateOpenQueryFile = template.MonitorZwCreateOpenQueryFile;
            UnexpectedFileAccessesAreErrors = template.UnexpectedFileAccessesAreErrors;
            IgnoreZwRenameFileInformation = template.IgnoreZwRenameFileInformation;
            IgnoreZwOtherFileInformation = template.IgnoreZwOtherFileInformation;
            IgnoreNonCreateFileReparsePoints = template.IgnoreNonCreateFileReparsePoints;
            IgnoreSetFileInformationByHandle = template.IgnoreSetFileInformationByHandle;
            IgnoreReparsePoints = template.IgnoreReparsePoints;
            IgnoreFullReparsePointResolving = template.IgnoreFullReparsePointResolving;
            IgnorePreloadedDlls = template.IgnorePreloadedDlls;
            SandboxKind = template.SandboxKind;
            ExistingDirectoryProbesAsEnumerations = template.ExistingDirectoryProbesAsEnumerations;
            PreserveOutputs = template.PreserveOutputs;
            PreserveOutputsTrustLevel = template.PreserveOutputsTrustLevel;
            IgnorePreserveOutputsPrivatization = template.IgnorePreserveOutputsPrivatization;
            IgnoreGetFinalPathNameByHandle = template.IgnoreGetFinalPathNameByHandle;
            IgnoreDynamicWritesOnAbsentProbes = template.IgnoreDynamicWritesOnAbsentProbes;
            DoubleWritePolicy = template.DoubleWritePolicy;
            IgnoreUndeclaredAccessesUnderSharedOpaques = template.IgnoreUndeclaredAccessesUnderSharedOpaques;
            IgnoreCreateProcessReport = template.IgnoreCreateProcessReport;
            ProbeDirectorySymlinkAsDirectory = template.ProbeDirectorySymlinkAsDirectory;
            SkipFlaggingSharedOpaqueOutputs = template.SkipFlaggingSharedOpaqueOutputs;
            EnableFullReparsePointResolving = template.EnableFullReparsePointResolving;
            DoNotApplyAllowListToDynamicOutputs = template.DoNotApplyAllowListToDynamicOutputs;
        }

        /// <inheritdoc />
        public PreserveOutputsMode PreserveOutputs { get; set; }

        /// <inheritdoc />
        public int PreserveOutputsTrustLevel { get; set; }

        /// <inheritdoc />
        public bool MonitorFileAccesses { get; set; }

        /// <inheritdoc />
        public bool IgnoreZwRenameFileInformation { get; set; }

        /// <inheritdoc />
        public bool IgnoreZwOtherFileInformation { get; set; }

        /// <inheritdoc />
        public bool IgnoreNonCreateFileReparsePoints { get; set; }

        /// <inheritdoc />
        public bool IgnoreSetFileInformationByHandle { get; set; }

        /// <inheritdoc />
        public bool IgnoreReparsePoints { get; set; }

        /// <inheritdoc />
        public bool IgnoreFullReparsePointResolving { get; set; }

        /// <inheritdoc />
        public bool IgnorePreloadedDlls { get; set; }

        /// <inheritdoc />
        public bool ExistingDirectoryProbesAsEnumerations { get; set; }

        /// <inheritdoc />
        public bool MonitorNtCreateFile { get; set; }

        /// <inheritdoc />
        public bool MonitorZwCreateOpenQueryFile { get; set; }

        /// <inheritdoc />
        public SandboxKind SandboxKind { get; set; }

        /// <inheritdoc />
        public bool UnexpectedFileAccessesAreErrors { get; set; }

        /// <inheritdoc />
        public bool IgnoreGetFinalPathNameByHandle { get; set; }

        /// <inheritdoc />
        public DynamicWriteOnAbsentProbePolicy IgnoreDynamicWritesOnAbsentProbes { get; set; }

        /// <inheritdoc />
        public RewritePolicy? DoubleWritePolicy { get; set; }

        /// <inheritdoc />
        public bool IgnoreUndeclaredAccessesUnderSharedOpaques { get; set; }

        /// <inheritdoc />
        public bool IgnoreCreateProcessReport { get; set; }

        /// <inheritdoc />
        public bool ProbeDirectorySymlinkAsDirectory { get; set; }

        /// <inheritdoc />
        public bool? EnableFullReparsePointResolving { get; set; }

        /// <inheritdoc/>
        public bool? SkipFlaggingSharedOpaqueOutputs { get; set; }

        /// <inheritdoc/>
        public bool IgnorePreserveOutputsPrivatization { get; set; }

        /// <inheritdoc/>
        public bool? DoNotApplyAllowListToDynamicOutputs { get; set; }
    }
}
