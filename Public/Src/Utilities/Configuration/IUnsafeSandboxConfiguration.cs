// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using BuildXL.Utilities.Core;

namespace BuildXL.Utilities.Configuration
{
    /// <summary>
    /// Policy for handling dynamic writes on absent path probe
    /// </summary>
    [Flags]
    public enum DynamicWriteOnAbsentProbePolicy : int
    {
        /// <summary>
        /// Do not ignore any dynamic writes on absent path probe
        /// </summary>
        IgnoreNothing = 0,

        /// <summary>
        /// Ignore when the path in question is a directory
        /// </summary>
        IgnoreDirectoryProbes = 1,

        /// <summary>
        /// Ignore when the path in question is a file
        /// </summary>
        IgnoreFileProbes = 1 << 1,

        /// <summary>
        /// Ignore always
        /// </summary>
        IgnoreAll = IgnoreDirectoryProbes | IgnoreFileProbes
    }

    /// <summary>
    /// Unsafe Sandbox Configuration
    /// </summary>
    public interface IUnsafeSandboxConfiguration
    {
        /// <summary>
        /// Whether BuildXL preserves the existing output file from a previous invocation of a process before invoking it.
        /// Preserving output files can be a source of nondeterminism since the behavior of the process can change based
        /// on the state of the outputs.
        /// </summary>
        PreserveOutputsMode PreserveOutputs { get; }

        /// <summary>
        /// Trust level of how much we trust the preserveoutputs per pip.
        /// </summary>
        int PreserveOutputsTrustLevel { get; }

        /// <summary>
        /// Whether or not to make preserved outputs private during sandbox preparation.
        /// </summary>
        /// <remarks>
        /// When dealing with preserve outputs, the sandboxed process pip executor must ensure that the outputs are private, i.e., writable,
        /// no hardlink (i.e., no association with cache) before the pip is executed. Making outputs private can be very expensive. For example,
        /// in a customer case, an output directory can consist of ~40,000 files, and making all of them private can take ~13 minutes.
        /// When the user opt to not storing outputs to cache (/storeOutputsToCache-), in principle there is no need to make the outputs private.
        /// However, the user can later decide to store outputs to cache later, and thus some outputs will have association with the cache. Then,
        /// when preserve output mode is applied to those outputs, they need to be made private.
        /// 
        /// Suppose that our build has 2 pips, pip A and pip B. Consider the following sequence of build sesssions:
        /// - 1st build (/storeOutputsToCache+, preserve output mode is disabled):
        ///   - A's and B's outputs are linked to the cache.
        /// - 2nd build (/storeOutputsToCache-, filter in only pip A, preserve output is applicable to A's outputs):
        ///   - pip A's outputs are now private after 2nd build.
        /// - 3rd build (/storeOutputsToCache-, no filter, preserve output is applicable to A's and B's outputs):
        ///   - Although this build does not intend to store the outputs to the cache, if privatization of B's output is not performed 
        ///     before B executes, then B's execution may fail or potentially modifies the cache.
        ///     
        /// Ideally, in the 3rd build above one needs to know if pip's outputs are already private or not. But this requires per-pip tracking.
        /// 
        /// Instead of per-pip tracking, we use this unsafe configuration to allow the user to gain performance with the risk of build failure
        /// when flip-floping /storeOutputsToCache option. In short, this unsafe configuration should only be used if the user decides to always not
        /// store outputs to cache.
        /// </remarks>
        bool IgnorePreserveOutputsPrivatization { get; }

        /// <summary>
        /// Whether BuildXL is to monitor file accesses of individual tools at all. Disabling monitoring results in an unsafe configuration (for diagnostic purposes only). Defaults to on.
        /// </summary>
        bool MonitorFileAccesses { get; }

        /// <summary>
        /// Deprecated: Still in code for cache fingerprint stability.
        /// Whether BuildXL is to detour the ZwRenameFileInformation API. Not detouring ZwRenameFileInformation is an unsafe configuration. Defaults to off (i.e., detour the ZwRenameFileInformation API).
        /// </summary>
        bool IgnoreZwRenameFileInformation { get; }

        /// <summary>
        /// Deprecated: Still in code for cache fingerprint stability.
        /// Whether BuildXL is to detour the ZwOtherFileInformation API. Not detouring ZwOtherFileInformation is an unsafe configuration. Defaults to on (i.e., don't detour the ZwOtherFileInformation API).
        /// </summary>
        bool IgnoreZwOtherFileInformation { get; }

        /// <summary>
        /// Deprecated: Still in code for cache fingerprint stability.
        /// Whether BuildXL is to detour the follow symlinks for APIs different than CreateFile and NtCreate/OpenFile APIs.
        /// </summary>
        bool IgnoreNonCreateFileReparsePoints { get; }

        /// <summary>
        /// Deprecated: Still in code for cache fingerprint stability.
        /// Whether BuildXL is to detour the SetFileInformationByhandle API. Not detouring SetFileInformationByHandle is an unsafe configuration. Defaults to off (i.e., detour the SetFileInformationByHandle API).
        /// </summary>
        bool IgnoreSetFileInformationByHandle { get; }

        /// <summary>
        /// Whether BuildXL is to ignore reparse points. Ignoring reparse points is an unsafe configuration. Defaults to off (i.e., not ignoring reparse points).
        /// </summary>
        bool IgnoreReparsePoints { get; }

        /// <summary>
        /// Whether BuildXL is to ignore fully resolving of reparse points. Ignoring reparse point resolving is an unsafe configuration. Defaults to on (i.e., skipping full resolving, due to backwards compatibility).
        /// </summary>
        bool IgnoreFullReparsePointResolving { get; }

        /// <summary>
        /// Whether BuildXL is to ignore Dlls loaded before Detours was started. Ignoring the preloaded (statically loaded) dlls is an unsafe configuration.
        /// </summary>
        bool IgnorePreloadedDlls { get; }

        /// <summary>
        /// Whether BuildXL treats existing directory probes as enumerations. This could lead to cases of overbuilding. Defaults to off (i.e., existing directory probes are not hanled as enumeration).
        /// </summary>
        /// <remarks>
        /// Overbuilding could happen when you have directory that is just being probed for existence, but BuildXL treats it as a directory enumeration.
        /// If a temp (irrelevant) directory or file is added to that directory, we are rebuilding the pips that declared a enumeration dependency on the directory.
        /// </remarks>
        bool ExistingDirectoryProbesAsEnumerations { get; }

        /// <summary>
        /// Deprecated: Still in code for cache fingerprint stability.
        /// Monitor files opened for read by NtCreateFile
        /// </summary>
        bool MonitorNtCreateFile { get; }

        /// <summary>
        /// Deprecated: Still in code for cache fingerprint stability.
        /// Monitor files opened for read by ZwCreateFile or ZwOpenFile
        /// </summary>
        bool MonitorZwCreateOpenQueryFile { get; }

        /// <summary>
        /// The kind of process sandbox to use
        /// </summary>
        SandboxKind SandboxKind { get; }

        /// <summary>
        /// When enabled, if BuildXL detects that a tool accesses a file that was not declared in the specification dependencies, it is treated as an error instead of a warning. Turning this
        /// option off results in an unsafe configuration (for diagnostic purposes only). Defaults to on.
        /// </summary>
        bool UnexpectedFileAccessesAreErrors { get; }

        /// <summary>
        /// Whether BuildXL is to detour the GetFinalPathNameByHandle API.
        /// </summary>
        /// <remarks>
        /// Precisely, this option controls whether or not BuildXL Detours performs path translation for the final path returned by the GetFinalPathNameByHandle API.
        /// Internally, when this option is set to true, BuildXL Detours simply does not detours the GetFinalPathNameByHandle API. Performing path translation for the final path
        /// is unsafe because the detoured process can get a path with unexpected properties. For example, due to some translations, the final path that is supposed to be a real file turns out to be
        /// a reparse point.
        /// </remarks>
        bool IgnoreGetFinalPathNameByHandle { get; }

        /// <summary>
        /// Whether BuildXL is to detour the DeviceIoControl for IOCTL_GET_REPARSE_POINT command.
        /// </summary>
        /// <remarks>
        /// Precisely, this option controls whether or not BuildXL Detours performs path translation for the path obtained by invoking DeviceIoControl on IOCTL_GET_REPARSE_POINT.
        /// Internally, when this option is set to true, BuildXL Detours simply does not detours the DeviceIoControl API at all. Performing path translation for the path
        /// is unsafe because the detoured process can get a path with unexpected properties. For example, due to some translations, the path that is supposed to be a real file turns out to be
        /// yet another reparse point.
        /// </remarks>
        bool IgnoreDeviceIoControlGetReparsePoint { get; }

        /// <summary>
        /// Whether BuildXL flags writes under opaque directories (exclusive or shared) that make existing absent probes to become present probes.
        /// </summary>
        DynamicWriteOnAbsentProbePolicy IgnoreDynamicWritesOnAbsentProbes { get; }

        /// <summary>
        /// Policy to be applied when a process incurs in a double write
        /// </summary>
        /// <remarks>
        /// Can be individually controlled on a per-pip basis, this value sets the default
        /// </remarks>
        RewritePolicy? DoubleWritePolicy { get; }

        /// <summary>
        /// Deprecated: Still in code for cache fingerprint stability.
        /// Undeclared accesses under a shared opaque are not reported.
        /// </summary>
        /// <remarks>
        /// Temporary flag due to a bug in the sandboxed process pip executor to allow customers to snap to the fixed behavior
        /// </remarks>
        bool IgnoreUndeclaredAccessesUnderSharedOpaques { get; }

        /// <summary>
        /// Ignores CreateProcess report.
        /// </summary>
        bool IgnoreCreateProcessReport { get; }

        /// <summary>
        /// Deprecated: Still in code for cache fingerprint stability.
        /// Treats directory symlink probes as directory probes instead of file probes.
        /// </summary>
        /// <remarks>
        /// This configuration is unsafe because the target directory path may not be tracked.
        /// </remarks>
        bool ProbeDirectorySymlinkAsDirectory { get; }

        /// <summary>
        /// Indicates if full reparse point resolving should be enabled in the process sandbox.
        /// </summary>
        bool? EnableFullReparsePointResolving { get; }

        /// <summary>
        /// When true, outputs produced under shared opaques won't be flagged as such.
        /// </summary>
        /// <remarks>
        /// This means subsequent builds won't be able to recognize those as outputs and they won't be deleted before pips run
        /// </remarks>
        bool? SkipFlaggingSharedOpaqueOutputs { get; }

        /// <summary>
        /// Disable the application of allow list to filter dynamic (shared opaque) outputs; unsafe when the value is true.
        /// </summary>
        /// <remarks>
        /// The default is for now true since turning on this option is a breaking change for some customers. Will be eventually turned to false.
        /// </remarks>
        bool? DoNotApplyAllowListToDynamicOutputs { get; }

        /// <summary>
        /// If true, untracked paths, when symlinks, are not fully resolved nor reported.
        /// </summary>
        bool IgnoreUntrackedPathsInFullReparsePointResolving { get; }

        // NOTE: if you add a property here, don't forget to update UnsafeSandboxConfigurationExtensions

        // NOTE: whenever unsafe options change, the fingerprint version needs to be bumped
    }

    /// <summary>
    /// Extension methods for <see cref="IUnsafeSandboxConfiguration"/>.
    /// </summary>
    public static class UnsafeSandboxConfigurationExtensions
    {
        /// <summary>
        /// Defaults that are consider "safe".
        /// </summary>
        public static readonly IUnsafeSandboxConfiguration SafeDefaults = Mutable.UnsafeSandboxConfiguration.SafeOptions;

        /// <summary>
        /// Returns whether sandboxing is disabled.
        /// </summary>
        public static bool DisableDetours(this IUnsafeSandboxConfiguration @this)
        {
            return @this.SandboxKind == SandboxKind.None;
        }

        /// <nodoc/>
        public static void Serialize(this IUnsafeSandboxConfiguration @this, BuildXLWriter writer)
        {
            writer.Write((byte)@this.SandboxKind);
            writer.Write(@this.ExistingDirectoryProbesAsEnumerations);
            writer.Write(@this.IgnoreGetFinalPathNameByHandle);
            writer.Write(@this.IgnoreDeviceIoControlGetReparsePoint);
            writer.Write(@this.IgnoreNonCreateFileReparsePoints);
            writer.Write(@this.IgnoreReparsePoints);
            writer.Write(@this.IgnoreSetFileInformationByHandle);
            writer.Write(@this.IgnoreZwOtherFileInformation);
            writer.Write(@this.IgnoreZwRenameFileInformation);
            writer.Write(@this.MonitorFileAccesses);
            writer.Write(@this.MonitorNtCreateFile);
            writer.Write(@this.MonitorZwCreateOpenQueryFile);
            writer.Write((byte)@this.PreserveOutputs);
            writer.Write(@this.PreserveOutputsTrustLevel);
            writer.Write(@this.IgnorePreserveOutputsPrivatization);
            writer.Write(@this.UnexpectedFileAccessesAreErrors);
            writer.Write(@this.IgnorePreloadedDlls);
            writer.WriteCompact((int)@this.IgnoreDynamicWritesOnAbsentProbes);
            writer.Write(@this.DoubleWritePolicy.HasValue);
            if (@this.DoubleWritePolicy.HasValue)
            {
                writer.Write((byte)@this.DoubleWritePolicy.Value);
            }
            writer.Write(@this.IgnoreUndeclaredAccessesUnderSharedOpaques);
            writer.Write(@this.IgnoreCreateProcessReport);
            writer.Write(@this.ProbeDirectorySymlinkAsDirectory);
            writer.Write(@this.IgnoreFullReparsePointResolving);
            writer.Write(@this.SkipFlaggingSharedOpaqueOutputs.HasValue);
            if (@this.SkipFlaggingSharedOpaqueOutputs.HasValue)
            {
                writer.Write(@this.SkipFlaggingSharedOpaqueOutputs.Value);
            }
            writer.Write(@this.EnableFullReparsePointResolving.HasValue);
            if (@this.EnableFullReparsePointResolving.HasValue)
            {
                writer.Write(@this.EnableFullReparsePointResolving.Value);
            }
            writer.Write(@this.DoNotApplyAllowListToDynamicOutputs.HasValue);
            if (@this.DoNotApplyAllowListToDynamicOutputs.HasValue)
            {
                writer.Write(@this.DoNotApplyAllowListToDynamicOutputs.Value);
            }
            writer.Write(@this.IgnoreUntrackedPathsInFullReparsePointResolving);
        }

        /// <nodoc/>
        public static IUnsafeSandboxConfiguration Deserialize(BuildXLReader reader)
        {
            return new Mutable.UnsafeSandboxConfiguration()
            {
                SandboxKind = (SandboxKind)reader.ReadByte(),
                ExistingDirectoryProbesAsEnumerations = reader.ReadBoolean(),
                IgnoreGetFinalPathNameByHandle = reader.ReadBoolean(),
                IgnoreDeviceIoControlGetReparsePoint = reader.ReadBoolean(),
                IgnoreNonCreateFileReparsePoints = reader.ReadBoolean(),
                IgnoreReparsePoints = reader.ReadBoolean(),
                IgnoreSetFileInformationByHandle = reader.ReadBoolean(),
                IgnoreZwOtherFileInformation = reader.ReadBoolean(),
                IgnoreZwRenameFileInformation = reader.ReadBoolean(),
                MonitorFileAccesses = reader.ReadBoolean(),
                MonitorNtCreateFile = reader.ReadBoolean(),
                MonitorZwCreateOpenQueryFile = reader.ReadBoolean(),
                PreserveOutputs = (PreserveOutputsMode)reader.ReadByte(),
                PreserveOutputsTrustLevel = reader.ReadInt32(),
                IgnorePreserveOutputsPrivatization = reader.ReadBoolean(),
                UnexpectedFileAccessesAreErrors = reader.ReadBoolean(),
                IgnorePreloadedDlls = reader.ReadBoolean(),
                IgnoreDynamicWritesOnAbsentProbes = (DynamicWriteOnAbsentProbePolicy)reader.ReadInt32Compact(),
                DoubleWritePolicy = reader.ReadBoolean() ? (RewritePolicy?)reader.ReadByte() : null,
                IgnoreUndeclaredAccessesUnderSharedOpaques = reader.ReadBoolean(),
                IgnoreCreateProcessReport = reader.ReadBoolean(),
                ProbeDirectorySymlinkAsDirectory = reader.ReadBoolean(),
                IgnoreFullReparsePointResolving = reader.ReadBoolean(),
                SkipFlaggingSharedOpaqueOutputs = reader.ReadBoolean() ? (bool?)reader.ReadBoolean() : null,
                EnableFullReparsePointResolving = reader.ReadBoolean() ? (bool?)reader.ReadBoolean() : null,
                DoNotApplyAllowListToDynamicOutputs = reader.ReadBoolean() ? (bool?)reader.ReadBoolean() : null,
                IgnoreUntrackedPathsInFullReparsePointResolving = reader.ReadBoolean(),
            };
        }

        /// <summary>
        /// Returns <code>true</code> if <paramref name="lhs"/> does not contain a single unsafe value that is not present in <paramref name="rhs"/>.
        /// </summary>
        public static bool IsAsSafeOrSaferThan(this IUnsafeSandboxConfiguration lhs, IUnsafeSandboxConfiguration rhs)
        {
            // Intentionally missing fields:
            //
            // - UnexpectedFileAccessesAreErrors: When a pip has an unexpected file access, PipExecutor.cs enforces that its metadata
            //      is not stored to the cache. Therefore it is not necessary to consider this setting for sake of caching.
            // - PreserveOutputs: The sandbox configuration setting globally decides whether preserve outputs.
            //      Whether the current run is as safe or safer also depends on whether preserve outputs is allowed for
            //      the pip in question. Because that requires pip specific details, that is determined in UnsafeOptions
            return IsAsSafeOrSafer(lhs.DisableDetours(), rhs.DisableDetours(), SafeDefaults.DisableDetours())
                && IsAsSafeOrSafer(lhs.ExistingDirectoryProbesAsEnumerations, rhs.ExistingDirectoryProbesAsEnumerations, SafeDefaults.ExistingDirectoryProbesAsEnumerations)
                && IsAsSafeOrSafer(lhs.IgnoreGetFinalPathNameByHandle, rhs.IgnoreGetFinalPathNameByHandle, SafeDefaults.IgnoreGetFinalPathNameByHandle)
                && IsAsSafeOrSafer(lhs.IgnoreDeviceIoControlGetReparsePoint, rhs.IgnoreDeviceIoControlGetReparsePoint, SafeDefaults.IgnoreDeviceIoControlGetReparsePoint)
                && IsAsSafeOrSafer(lhs.IgnoreNonCreateFileReparsePoints, rhs.IgnoreNonCreateFileReparsePoints, SafeDefaults.IgnoreNonCreateFileReparsePoints)
                && IsAsSafeOrSafer(lhs.IgnoreReparsePoints, rhs.IgnoreReparsePoints, SafeDefaults.IgnoreReparsePoints)
                && IsAsSafeOrSafer(lhs.IgnoreFullReparsePointResolving, rhs.IgnoreFullReparsePointResolving, SafeDefaults.IgnoreFullReparsePointResolving)
                && IsAsSafeOrSafer(lhs.IgnoreSetFileInformationByHandle, rhs.IgnoreSetFileInformationByHandle, SafeDefaults.IgnoreSetFileInformationByHandle)
                && IsAsSafeOrSafer(lhs.IgnoreZwOtherFileInformation, rhs.IgnoreZwOtherFileInformation, SafeDefaults.IgnoreZwOtherFileInformation)
                && IsAsSafeOrSafer(lhs.IgnoreZwRenameFileInformation, rhs.IgnoreZwRenameFileInformation, SafeDefaults.IgnoreZwRenameFileInformation)
                && IsAsSafeOrSafer(lhs.MonitorFileAccesses, rhs.MonitorFileAccesses, SafeDefaults.MonitorFileAccesses)
                && IsAsSafeOrSafer(lhs.MonitorNtCreateFile, rhs.MonitorNtCreateFile, SafeDefaults.MonitorNtCreateFile)
                && IsAsSafeOrSafer(lhs.MonitorZwCreateOpenQueryFile, rhs.MonitorZwCreateOpenQueryFile, SafeDefaults.MonitorZwCreateOpenQueryFile)
                && IsAsSafeOrSafer(lhs.IgnorePreserveOutputsPrivatization, rhs.IgnorePreserveOutputsPrivatization, SafeDefaults.IgnorePreserveOutputsPrivatization)
                && IsAsSafeOrSafer(lhs.IgnorePreloadedDlls, rhs.IgnorePreloadedDlls, SafeDefaults.IgnorePreloadedDlls)
                && IsAsSafeOrSafer(lhs.IgnoreDynamicWritesOnAbsentProbes, rhs.IgnoreDynamicWritesOnAbsentProbes, SafeDefaults.IgnoreDynamicWritesOnAbsentProbes)
                && IsAsSafeOrSafer(lhs.DoubleWritePolicy(), rhs.DoubleWritePolicy(), SafeDefaults.DoubleWritePolicy())
                && IsAsSafeOrSafer(lhs.IgnoreUndeclaredAccessesUnderSharedOpaques, rhs.IgnoreUndeclaredAccessesUnderSharedOpaques, SafeDefaults.IgnoreUndeclaredAccessesUnderSharedOpaques)
                && IsAsSafeOrSafer(lhs.IgnoreCreateProcessReport, rhs.IgnoreCreateProcessReport, SafeDefaults.IgnoreCreateProcessReport)
                && IsAsSafeOrSafer(lhs.ProbeDirectorySymlinkAsDirectory, rhs.ProbeDirectorySymlinkAsDirectory, SafeDefaults.ProbeDirectorySymlinkAsDirectory)
                && IsAsSafeOrSafer(lhs.SkipFlaggingSharedOpaqueOutputs(), rhs.SkipFlaggingSharedOpaqueOutputs(), SafeDefaults.SkipFlaggingSharedOpaqueOutputs())
                && IsAsSafeOrSafer(lhs.EnableFullReparsePointResolving(), rhs.EnableFullReparsePointResolving(), SafeDefaults.EnableFullReparsePointResolving())
                && IsAsSafeOrSafer(lhs.DoNotApplyAllowListToDynamicOutputs(), rhs.DoNotApplyAllowListToDynamicOutputs(), SafeDefaults.DoNotApplyAllowListToDynamicOutputs())
                && IsAsSafeOrSafer(lhs.IgnoreUntrackedPathsInFullReparsePointResolving, rhs.IgnoreUntrackedPathsInFullReparsePointResolving, SafeDefaults.IgnoreUntrackedPathsInFullReparsePointResolving);
        }

        /// <nodoc />
        public static bool IsAsSafeOrSafer(DynamicWriteOnAbsentProbePolicy lhsValue, DynamicWriteOnAbsentProbePolicy rhsValue)
        {
            return (lhsValue & rhsValue) == lhsValue;
        }

        private static bool IsAsSafeOrSafer(DynamicWriteOnAbsentProbePolicy lhsValue, DynamicWriteOnAbsentProbePolicy rhsValue, DynamicWriteOnAbsentProbePolicy _)
            => IsAsSafeOrSafer(lhsValue, rhsValue);

        private static bool IsAsSafeOrSafer<T>(T lhsValue, T rhsValue, T safeValue) where T: struct
        {
            return
                EqualityComparer<T>.Default.Equals(lhsValue, safeValue) ||
                EqualityComparer<T>.Default.Equals(lhsValue, rhsValue);
        }
    }
}
