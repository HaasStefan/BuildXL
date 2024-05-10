// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Core;

namespace BuildXL.Utilities.Configuration
{
    /// <summary>
    /// Sandbox Configuration
    /// </summary>
    public interface ISandboxConfiguration
    {
        /// <summary>
        /// Break into the debugger when BuildXL detects that a tool accesses a file that was not declared in the specification dependencies. This option is useful when developing new tools or
        /// SDKs using these tools. Defaults to off.
        /// </summary>
        bool BreakOnUnexpectedFileAccess { get; }

        /// <summary>
        /// Whether to allow access to certain files that are touched during code coverage collection.
        /// </summary>
        bool FileAccessIgnoreCodeCoverage { get; }

        /// <summary>
        /// TODO: This is set via magic.
        /// </summary>
        bool FailUnexpectedFileAccesses { get; }

        /// <summary>
        /// Whether to enforce access policies for CreateDirectory calls for already existing directories and the ones under writable mounts.
        /// </summary>
        bool EnforceAccessPoliciesOnDirectoryCreation { get; }

        /// <summary>
        /// When enabled, force read only for requested read-write access so long as the tool is allowed to read.
        /// </summary>
        /// <remarks>
        /// Some tools, like Perl, open files with read-write access, but only read those files.
        /// </remarks>
        bool ForceReadOnlyForRequestedReadWrite { get; }

        /// <summary>
        /// Flushes page cache to file system on storing outputs to cache.
        /// </summary>
        /// <remarks>
        /// Disabling the flush should not affect correctness. However, this may affect USN-journal based incremental scheduling
        /// because dirty pages are written lazily to the file-system, and thus may indicate that a file is modified, although a CLOSE
        /// USN record has been written. Thus, in the next build the file is rehashed.
        /// </remarks>
        bool FlushPageCacheToFileSystemOnStoringOutputsToCache { get; }

        /// <summary>
        /// Whether BuildXL normalizes timestamps processes see when they read files. If false, reads will see the actual
        /// timestamp, as long as it is newer than the well known timestamp used to enforce rewrite ordering.
        /// </summary>
        bool NormalizeReadTimestamps { get; }

        /// <summary>
        /// Whether BuildXL will use larger NtClose prealocated list.
        /// </summary>
        bool UseLargeNtClosePreallocatedList { get; }

        /// <summary>
        /// Whether BuildXL will use extra thread to drain NtClose handle List or clean the cache directly.
        /// </summary>
        bool UseExtraThreadToDrainNtClose { get; }

        /// <summary>
        /// Whether accesses to untracked scopes are masked
        /// </summary>
        bool MaskUntrackedAccesses { get; }

        #region Timeout configuration

        /// <summary>
        /// How long to wait before terminating individual processes, in milliseconds. Setting this value will only have an effect if no other timeout is specified for a process.
        /// </summary>
        int DefaultTimeout { get; }

        /// <summary>
        /// After how much time to issue a warning that an individual process runs too long, in milliseconds. Setting this value will only have an effect if no other timeout is specified for
        /// a process.
        /// </summary>
        int DefaultWarningTimeout { get; }

        /// <summary>
        /// Multiplier applied to the final timeout for individual processes. Setting a multiplier greater than one will increase the timeout accordingly for all pips, even those with an
        /// explicit non-default timeout set.
        /// </summary>
        /// WIP: Property > 1
        int TimeoutMultiplier { get; }

        /// <summary>
        /// Multiplier applied to the warning timeout for individual processes. Setting a multiplier greater than one will increase the warning timeout accordingly for all pips, even those
        /// with an explicit non-default warning timeout set.
        /// </summary>
        /// WIP: Property > 1
        int WarningTimeoutMultiplier { get; }

        /// <summary>
        /// Root directory where timeout dumps should be saved
        /// </summary>
        AbsolutePath TimeoutDumpDirectory { get; }

        /// <summary>
        /// Root directory where surviving child process dumps should be saved
        /// </summary>
        AbsolutePath SurvivingPipProcessChildrenDumpDirectory { get; }

        #endregion

        #region Logging options for the Sandbox

        /// <summary>
        /// Records the files observed to be accessed by individual pips to the log. Defaults to off.
        /// </summary>
        bool LogObservedFileAccesses { get; }

        /// <summary>
        /// Records all launched processes, including nested processes, of each pip to the log. Defaults to off.
        /// </summary>
        bool LogProcesses { get; }

        /// <summary>
        /// Records user and kernel mode execution times as well as IO Counts.
        /// </summary>
        bool LogProcessData { get; }

        /// <summary>
        /// Records the file enforcement access tables for individual pips to the log. Defaults to off.
        /// </summary>
        bool LogFileAccessTables { get; }

        #endregion

        /// <summary>
        /// Specifies how process standard error and standard output should be reported. Allowed values are 'TruncatedOutputOnError', 'FullOutputAlways', 'FullOutputOnError',
        /// 'FullOutputOnWarningOrError'. Default is 'TruncatedOutputOnError'.
        /// </summary>
        OutputReportingMode OutputReportingMode { get; }

        /// <summary>
        /// The filesystem rules uses by the ObservedInputProcessor
        /// </summary>
        FileSystemMode FileSystemMode { get; }

        /// <summary>
        /// Gets the IUnsafeSandboxConfiguration that goes with this ISandboxConfiguration.
        /// </summary>
        IUnsafeSandboxConfiguration UnsafeSandboxConfiguration { get; }

        /// <summary>
        /// Hard exits on error with special exit code if internal error in the detours layer is detected;
        /// </summary>
        bool HardExitOnErrorInDetours { get; }

        /// <summary>
        /// Check Detours message count.
        /// </summary>
        bool CheckDetoursMessageCount { get; }

        /// <summary>
        /// Log the Detouring status messages.
        /// </summary>
        bool LogProcessDetouringStatus { get; }

        /// <summary>
        /// Allows logging internal error messages to a file.
        /// </summary>
        bool AllowInternalDetoursErrorNotificationFile { get; }

        /// <summary>
        /// Whether to measure CPU times (user/system) of sandboxed processes.  Default: true.
        /// </summary>
        bool MeasureProcessCpuTimes { get; }

        /// <summary>
        /// Execution mode for processes that require admin privilege.
        /// </summary>
        AdminRequiredProcessExecutionMode AdminRequiredProcessExecutionMode { get; }

        /// <summary>
        /// Root of redirected temporary folders for VM execution.
        /// </summary>
        /// <remarks>
        /// This is used mainly for testing.
        /// </remarks>
        AbsolutePath RedirectedTempFolderRootForVmExecution { get; }

        /// <summary>
        /// Retries process whose exit code or its children's exit code is Azure Watson's special exit code, i.e., 0xDEAD.
        /// </summary>
        /// <remarks>
        /// When running in CloudBuild, Process nondeterministically sometimes exits with 0xDEAD exit code. This is the exit code
        /// returned by Azure Watson dump after catching the process crash. The root cause of the crash is unknown,
        /// but the primary suspect is the way Detours handle NtClose.
        /// </remarks>
        bool RetryOnAzureWatsonExitCode { get; }

        /// <summary>
        /// Ensures temp directories existence before pip execution.
        /// </summary>
        /// <remarks>
        /// This is a temporary flag for enforcing consistent behavior in temp directories creation.
        /// If this flag is set to false, then only directories specified in %TMP% and %TEMP% are
        /// ensured to exist, but additional temp directories are not. The current default is false.
        /// Eventually, BuildXL will always ensure temp directory creation. However, currently, such a change
        /// can break customers who assume that additional temp directories are not created before the pip executes.
        /// Thus, this enforcement is made opt-in.
        /// </remarks>
        bool EnsureTempDirectoriesExistenceBeforePipExecution { get; }

        /// <summary>
        /// Paths and Directory Paths which should be untracked for all processes
        /// </summary>
        /// <remarks>
        /// When Directory Path is specified, all paths under that directory will be untracked
        /// This is an unsafe configuration, since it allows read and write access to the paths
        /// which is not specified as input or output.
        /// Moreover, this global configuration from cammand line will bypass cache,
        /// which means pips and graph will be cached ignoring paths specified in this configure
        /// </remarks>
        IReadOnlyList<AbsolutePath> GlobalUnsafeUntrackedScopes { get; }

        /// <summary>
        /// Temporary flag to use tool incremental behavior when preserve outputs is enabled.
        /// </summary>
        bool PreserveOutputsForIncrementalTool { get; }

        /// <summary>
        /// Environment Variables which should be passed through for all processes
        /// </summary>
        /// <remarks>
        /// This is an unsafe configuration.
        /// This global configuration from cammand line will bypass cache,
        /// which means pips and graph will be cached ignoring environment variables specified in this configuration.
        /// </remarks>
        IReadOnlyList<string> GlobalUnsafePassthroughEnvironmentVariables { get; }

        /// <summary>
        /// Concurrency limit for executing pips inside VM. 
        /// </summary>
        int VmConcurrencyLimit { get; }

        /// <summary>
        /// List of directory paths where full reparse point resolving will be applied to any path under them.
        /// This list is only considered when <see cref="IUnsafeSandboxConfiguration.IgnoreFullReparsePointResolving"/> is set to true and<see cref="IUnsafeSandboxConfiguration.EnableFullReparsePointResolving"/> is set to false. 
        /// </summary>
        IReadOnlyList<AbsolutePath> DirectoriesToEnableFullReparsePointParsing { get; }

        /// <summary>
        /// Enable explicitly reporting directory probes from detours to help avoid underbuilds caused by unreported directory probes.
        /// </summary>
        /// <remarks>
        /// This is an experimental feature, enabling this option may result in more DFAs on a build.
        /// </remarks>
        public bool ExplicitlyReportDirectoryProbes { get; }

        /// <summary>
        /// Disables setting the FILE_SHARE_DELETE flag in Detours when opening file handles for tracked files.
        /// </summary>
        /// <remarks>
        /// Note that we need to add FILE_SHARE_DELETE to dwShareMode to leverage NTFS hardlinks to avoid copying cache
        /// content, i.e., we need to be able to delete one of many links to a file. Unfortunately, share-mode is aggregated only per file
        /// rather than per-link, so in order to keep unused links delete-able, we should ensure in-use links are delete-able as well.
        /// However, adding FILE_SHARE_DELETE may be unexpected, for example, some unit tests may test for sharing violation. Thus,
        /// we only add FILE_SHARE_DELETE if the file is tracked.
        /// </remarks>
        public bool PreserveFileSharingBehaviour { get;  }

        /// <summary>
        /// Enables using the PTrace sandbox on Linux. It will only be used if a statically linked process is executed.
        /// Disabled by default.
        /// </summary>
        /// <remarks>
        /// There will be a significant performance impact on the executing process under this sandbox.
        /// </remarks>
        public bool EnableLinuxPTraceSandbox { get; }

        /// <summary>
        /// Always use remote detours injection when launching processes from a 32-bit process.
        /// </summary>
        /// <remarks>
        /// Remote injection means asking the BuildXL process root to inject detours into the target process.
        /// A 32-bit process only has 2G address space, which can be insufficient inject detours and its payload the target process.
        /// With this option, we can use remote injection to avoid the address space limitation because the root BuildXL is always a 64-bit process.
        /// </remarks>
        public bool AlwaysRemoteInjectDetoursFrom32BitProcess { get; }

        /// <summary>
        /// Unconditionally enable the PTrace sandbox On Linux. <see cref="EnableLinuxPTraceSandbox"/>
        /// </summary>
        /// <remarks>
        /// This options is used in tests as a way to validate the PTrace sandbox reporting capabilities regardless
        /// of tools being statically linked or not. Not intended for production use, this option is explicitly not
        /// exposed as a command line argument nor as a DScript option.
        /// When turned on, this option also implies <see cref="EnableLinuxPTraceSandbox"/>.
        /// </remarks>
        public bool UnconditionallyEnableLinuxPTraceSandbox { get; }

        /// <summary>
        /// Ignores DeviceIoControl calls, in particular the case of FSCTL_GET_REPARSE_POINT
        /// </summary>
        bool IgnoreDeviceIoControlGetReparsePoint { get; }

        /// <summary>
        /// Force set the execute permission bit for the root process of process pips in Linux builds.
        /// </summary>
        public bool ForceAddExecutionPermission {  get; }
    }
}
