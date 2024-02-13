// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.ContractsLight;
using System.Linq;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Engine.Cache.Fingerprints;
using BuildXL.Pips;
using BuildXL.ProcessPipExecutor;
using BuildXL.Processes;
using BuildXL.Scheduler.Fingerprints;
using BuildXL.Storage;
using BuildXL.Storage.Fingerprints;
using BuildXL.Utilities;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Instrumentation.Common;

#pragma warning disable 1591 // disabling warning about missing API documentation; TODO: Remove this line and write documentation!

namespace BuildXL.Scheduler
{
    /// <summary>
    /// Defines the result of execution or cache lookup of a process pip.
    /// TODO: Capture and serialize/deserialize file access violations.
    /// </summary>
    public sealed class ExecutionResult
    {
        private UnsealedState m_unsealedState;

        private UnsealedState InnerUnsealedState
        {
            get
            {
                EnsureUnsealed();
                return m_unsealedState ?? (m_unsealedState = new UnsealedState());
            }
        }

        private PipResultStatus m_result;
        private IReadOnlyList<ReportedFileAccess> m_fileAccessViolationsNotAllowlisted;
        private IReadOnlyList<ReportedFileAccess> m_allowlistedFileAccessViolations;
        private int m_numberOfWarnings;
        private ProcessPipExecutionPerformance m_performanceInformation;
        private WeakContentFingerprint? m_weakFingerprint;
        private TwoPhaseCachingInfo m_twoPhaseCachingInfo;
        private ReadOnlyArray<(FileArtifact, FileMaterializationInfo, PipOutputOrigin)> m_outputContent;
        private ReadOnlyArray<(DirectoryArtifact, ReadOnlyArray<FileArtifactWithAttributes>)> m_directoryOutputs;
        private bool m_mustBeConsideredPerpetuallyDirty;
        private bool m_converged;
        private ReadOnlyArray<(AbsolutePath, DynamicObservationKind)> m_dynamicObservations;
        private IReadOnlySet<AbsolutePath> m_allowedUndeclaredSourceReads;
        private PipCacheDescriptorV2Metadata m_pipCacheDescriptorV2Metadata;
        private PipCachePerfInfo m_pipCachePerfInfo;
        private IReadOnlyDictionary<string, int> m_pipProperties;
        private bool m_hasUserRetries;
        private int m_exitCode;
        private RetryInfo m_retryInfo;
        private IReadOnlySet<AbsolutePath> m_createdDirectories;
        private PipCacheMissType? m_cacheMissType;

        public PipCachePerfInfo CacheLookupPerfInfo
        {
            get
            {
                EnsureSealed();
                return m_pipCachePerfInfo;
            }

            set
            {
                EnsureUnsealed();
                m_pipCachePerfInfo = value;
            }
        }

        /// <summary>
        /// Gets the pip result for the process execution
        /// </summary>
        public PipResultStatus Result
        {
            get
            {
                EnsureSealed();
                return m_result;
            }
        }

        /// <summary>
        /// Gets observed ownership for shared dynamic directories
        /// </summary>
        public IReadOnlyDictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>> SharedDynamicDirectoryWriteAccesses { get; private set; }

        /// <summary>
        /// Observed allowed undeclared source reads
        /// </summary>
        public IReadOnlySet<AbsolutePath> AllowedUndeclaredReads
        {
            get
            {
                EnsureSealed();
                return m_allowedUndeclaredSourceReads;
            }

            set
            {
                EnsureUnsealed();
                InnerUnsealedState.AllowedUndeclaredSourceReads = value;
            }
        }

        /// <summary>
        /// Collection of directories that were succesfully created during pip execution. 
        /// </summary>
        /// <remarks>
        /// Observe there is no guarantee those directories still exist. However, there was a point during the execution of the associated pip when these directories 
        /// were not there, the running pip created them and the creation was successful. 
        /// Only populated if allowed undeclared reads is on, since these are used for computing directory fingerprint enumeration when undeclared files are allowed.
        /// </remarks>
        public IReadOnlySet<AbsolutePath> CreatedDirectories
        {
            get
            {
                return m_createdDirectories;
            }

            set
            {
                EnsureUnsealed();
                m_createdDirectories = value;
            }
        }

        /// <summary>
        /// Indicates whether the cache entry was converged when storing cache entry to cache
        /// </summary>
        public bool Converged
        {
            get
            {
                return m_converged;
            }

            set
            {
                EnsureUnsealed();
                m_converged = value;
            }
        }

        /// <summary>
        /// Sets the pip result for the process execution. An error must be logged before calling this method
        /// </summary>
        public void SetResult(LoggingContext context,
            PipResultStatus status,
            RetryInfo retryInfo = null)
        {
            Contract.Requires(status != PipResultStatus.Succeeded || retryInfo == null, "Succeeded Pips should not have RetryInfo");
            if (status == PipResultStatus.Failed)
            {
                Contract.Assert(context.ErrorWasLogged, "Set a failed status without logging an error");
            }

            EnsureUnsealed();
            InnerUnsealedState.Result = status;
            m_retryInfo = retryInfo;
        }

        /// <summary>
        /// Gets the collection of unexpected file accesses reported so far that were not allowlisted. These are 'violations'.
        /// </summary>
        public IReadOnlyList<ReportedFileAccess> FileAccessViolationsNotAllowlisted
        {
            get
            {
                EnsureSealed();
                return m_fileAccessViolationsNotAllowlisted;
            }

            set
            {
                EnsureUnsealed();
                InnerUnsealedState.FileAccessViolationsNotAllowlisted = new Optional<IReadOnlyList<ReportedFileAccess>>(value);
            }
        }

        /// <summary>
        /// Gets the collection of unexpected file accesses reported so far that were allowlisted.
        /// </summary>
        public IReadOnlyList<ReportedFileAccess> AllowlistedFileAccessViolations
        {
            get
            {
                EnsureSealed();
                return m_allowlistedFileAccessViolations;
            }

            set
            {
                EnsureUnsealed();
                InnerUnsealedState.AllowlistedFileAccessViolations = new Optional<IReadOnlyList<ReportedFileAccess>>(value);
            }
        }

        /// <summary>
        /// Gets the pip cache fingerprint. Only used for cache lookup miss result and logging.
        /// </summary>
        public WeakContentFingerprint? WeakFingerprint
        {
            get
            {
                EnsureSealed();
                return m_weakFingerprint;
            }

            set
            {
                EnsureUnsealed();
                m_weakFingerprint = value;
            }
        }

        /// <summary>
        /// Gets the two-phase caching info
        /// </summary>
        public TwoPhaseCachingInfo TwoPhaseCachingInfo
        {
            get
            {
                EnsureSealed();
                return m_twoPhaseCachingInfo;
            }

            set
            {
                EnsureUnsealed();
                m_twoPhaseCachingInfo = value;
            }
        }

        /// <summary>
        /// Gets the pip cache descriptor metadata
        /// </summary>
        public PipCacheDescriptorV2Metadata PipCacheDescriptorV2Metadata
        {
            get
            {
                EnsureSealed();
                return m_pipCacheDescriptorV2Metadata;
            }

            set
            {
                EnsureUnsealed();
                m_pipCacheDescriptorV2Metadata = value;
            }
        }

        /// <summary>
        /// Gets the pip cache miss type
        /// </summary>
        public PipCacheMissType? CacheMissType
        {
            get
            {
                EnsureSealed();
                return m_cacheMissType;
            }

            set
            {
                EnsureUnsealed();
                m_cacheMissType = value;
            }
        }

        #region Reported State

        /// <summary>
        /// Dynamic observations
        /// </summary>
        public ReadOnlyArray<(AbsolutePath Path, DynamicObservationKind Kind)> DynamicObservations
        {
            get
            {
                EnsureSealed();
                return m_dynamicObservations;
            }

            set
            {
                EnsureUnsealed();
                InnerUnsealedState.DynamicObservations = value;
            }
        }

        /// <summary>
        /// Number of warnings raised by the process during execution
        /// </summary>
        public int NumberOfWarnings
        {
            get
            {
                EnsureSealed();
                return m_numberOfWarnings;
            }
        }

        /// <summary>
        /// Performance information for the pip execution
        /// </summary>
        public ProcessPipExecutionPerformance PerformanceInformation
        {
            get
            {
                EnsureSealed();
                return m_performanceInformation;
            }
        }

        /// <summary>
        /// Output content of the pip
        /// </summary>
        public ReadOnlyArray<(FileArtifact fileArtifact, FileMaterializationInfo fileInfo, PipOutputOrigin pipOutputOrigin)> OutputContent
        {
            get
            {
                EnsureSealed();
                return m_outputContent;
            }
        }

        /// <nodoc />
        public bool MustBeConsideredPerpetuallyDirty
        {
            get
            {
                EnsureSealed();
                return m_mustBeConsideredPerpetuallyDirty;
            }

            set
            {
                EnsureUnsealed();
                InnerUnsealedState.MustBeConsideredPerpetuallyDirty |= value;
            }
        }

        /// <summary>
        /// Directory outputs.
        /// </summary>
        public ReadOnlyArray<(DirectoryArtifact directoryArtifact, ReadOnlyArray<FileArtifactWithAttributes> fileArtifactArray)> DirectoryOutputs
        {
            get
            {
                EnsureSealed();
                return m_directoryOutputs;
            }
        }

        /// <summary>
        /// Whether or not the execution result is sealed
        /// </summary>
        public bool IsSealed { get; private set; }

        /// <summary>
        /// Whether or not the process was retried due to user specified exit codes
        /// </summary>
        public bool HasUserRetries
        {
            get
            {
                return m_hasUserRetries;
            }
        }

        /// <summary>
        /// Exit code
        /// </summary>
        public int ExitCode
        {
            get
            {
                return m_exitCode;
            }
        }

        /// <summary>
        /// Whether the pip was cancelled. Returns the reason for cancellation.
        /// </summary>
        public RetryInfo RetryInfo
        {
            get
            {
                return m_retryInfo;
            }
        }

        /// <summary>
        /// Returns any pip properties (with their counts) extracted from the process output
        /// </summary>
        public IReadOnlyDictionary<string, int> PipProperties
        {
            get
            {
                EnsureSealed();
                return m_pipProperties;
            }
        }

        #endregion Reported State

        /// <summary>
        /// Creates a new sealed execution result with the given information
        /// </summary>
        public static ExecutionResult CreateSealed(
            PipResultStatus result,
            int numberOfWarnings,
            ReadOnlyArray<(FileArtifact, FileMaterializationInfo, PipOutputOrigin)> outputContent,
            ReadOnlyArray<(DirectoryArtifact, ReadOnlyArray<FileArtifactWithAttributes>)> directoryOutputs,
            ProcessPipExecutionPerformance performanceInformation,
            WeakContentFingerprint? fingerprint,
            IReadOnlyList<ReportedFileAccess> fileAccessViolationsNotAllowlisted,
            IReadOnlyList<ReportedFileAccess> allowlistedFileAccessViolations,
            bool mustBeConsideredPerpetuallyDirty,
            ReadOnlyArray<(AbsolutePath, DynamicObservationKind)> dynamicObservations,
            IReadOnlySet<AbsolutePath> allowedUndeclaredSourceReads,
            TwoPhaseCachingInfo twoPhaseCachingInfo,
            PipCacheDescriptorV2Metadata pipCacheDescriptorV2Metadata,
            bool converged,
            PipCachePerfInfo cacheLookupStepDurations,
            IReadOnlyDictionary<string, int> pipProperties,
            bool hasUserRetries,
            int exitCode,
            IReadOnlySet<AbsolutePath> createdDirectories,
            PipCacheMissType? cacheMissType,
            RetryInfo pipRetryInfo = null)
        {
            var processExecutionResult =
                new ExecutionResult
                {
                    m_result = result,
                    m_numberOfWarnings = numberOfWarnings,
                    m_outputContent = outputContent,
                    m_directoryOutputs = directoryOutputs,
                    SharedDynamicDirectoryWriteAccesses = ComputeSharedDynamicAccessesFrom(directoryOutputs),
                    m_performanceInformation = performanceInformation,
                    m_weakFingerprint = fingerprint,
                    m_fileAccessViolationsNotAllowlisted = fileAccessViolationsNotAllowlisted,
                    m_allowlistedFileAccessViolations = allowlistedFileAccessViolations,
                    m_mustBeConsideredPerpetuallyDirty = mustBeConsideredPerpetuallyDirty,
                    m_dynamicObservations = dynamicObservations,
                    m_allowedUndeclaredSourceReads = allowedUndeclaredSourceReads,
                    m_twoPhaseCachingInfo = twoPhaseCachingInfo,
                    m_pipCacheDescriptorV2Metadata = pipCacheDescriptorV2Metadata,
                    Converged = converged,
                    IsSealed = true,
                    m_pipCachePerfInfo = cacheLookupStepDurations,
                    m_pipProperties = pipProperties,
                    m_hasUserRetries = hasUserRetries,
                    m_retryInfo = pipRetryInfo,
                    m_createdDirectories = createdDirectories,
                    m_exitCode = exitCode,
                    m_cacheMissType = cacheMissType
                };
            return processExecutionResult;
        }

        /// <summary>
        /// Creates a sealed result from another sealed result, altering the status.
        /// </summary>
        /// <param name="convergedCacheResult">The result containing the cache hit information (namely output content and observations).</param>
        /// <returns>A new sealed result with output content and result status from the converged result.</returns>
        /// <remarks>
        /// <paramref name="convergedCacheResult"/> contains only empty dynamically observed observations like dynamically observed files and
        /// dynamically observed enumerations. These observations are needed by incremental scheduling. Given that convergence means cache hit 
        /// based on the result of execution, the dynamic observations from the execution can be used as the converged result.
        /// </remarks>
        public ExecutionResult CreateSealedConvergedExecutionResult(ExecutionResult convergedCacheResult)
        {
            Contract.Requires(convergedCacheResult.Converged);
            EnsureSealed();

            return CreateSealed(
                convergedCacheResult.Result,
                NumberOfWarnings,
                convergedCacheResult.OutputContent,
                convergedCacheResult.DirectoryOutputs,
                PerformanceInformation,
                WeakFingerprint,
                FileAccessViolationsNotAllowlisted,
                AllowlistedFileAccessViolations,
                convergedCacheResult.MustBeConsideredPerpetuallyDirty,
                // Converged result does not have values for the following dynamic observations. Use the observations from this result.
                DynamicObservations,
                AllowedUndeclaredReads,
                convergedCacheResult.TwoPhaseCachingInfo,
                convergedCacheResult.PipCacheDescriptorV2Metadata,
                converged: true,
                cacheLookupStepDurations: convergedCacheResult.m_pipCachePerfInfo,
                PipProperties,
                HasUserRetries,
                ExitCode,
                CreatedDirectories,
                PipCacheMissType.Hit,
                RetryInfo);
        }

        /// <summary>
        /// Creates a sealed result from another sealed result, altering the status.
        /// </summary>
        /// <param name="result">The new status to be set in the new sealed result.</param>
        /// <returns>A new sealed result with replaced status field.</returns>
        public ExecutionResult CloneSealedWithResult(PipResultStatus result)
        {
            EnsureSealed();
            return CreateSealed(
                result,
                NumberOfWarnings,
                OutputContent,
                DirectoryOutputs,
                PerformanceInformation,
                WeakFingerprint,
                FileAccessViolationsNotAllowlisted,
                AllowlistedFileAccessViolations,
                MustBeConsideredPerpetuallyDirty,
                DynamicObservations,
                AllowedUndeclaredReads,
                TwoPhaseCachingInfo,
                PipCacheDescriptorV2Metadata,
                Converged,
                CacheLookupPerfInfo,
                PipProperties,
                HasUserRetries,
                ExitCode,
                CreatedDirectories,
                CacheMissType,
                RetryInfo);
        }

        /// <summary>
        /// Populates high level cache info from the given cache result.
        /// Specifically <see cref="WeakFingerprint"/>, <see cref="PipCacheDescriptorV2Metadata"/>, and <see cref="TwoPhaseCachingInfo"/> 
        /// are populated.
        /// </summary>
        public void PopulateCacheInfoFromCacheResult(RunnableFromCacheResult cacheResult)
        {
            EnsureUnsealed();

            WeakFingerprint = cacheResult.WeakFingerprint;
            CacheMissType = cacheResult.CacheMissType;

            if (cacheResult.CanRunFromCache)
            {
                var cacheHitData = cacheResult.GetCacheHitData();
                PipCacheDescriptorV2Metadata = cacheHitData.Metadata;
                TwoPhaseCachingInfo = new TwoPhaseCachingInfo(
                    weakFingerprint: cacheResult.WeakFingerprint,
                    pathSetHash: cacheHitData.PathSetHash,
                    strongFingerprint: cacheHitData.StrongFingerprint,

                    // NOTE: This should not be used so we set it to default values except the metadata hash (it is used for HistoricMetadataCache).
                    cacheEntry: new CacheEntry(cacheHitData.MetadataHash, "<Unspecified>", ArrayView<ContentHash>.Empty));
            }
        }

        /// <summary>
        /// Records the hash of an output of the pip. All static outputs must be reported, even those that were already up-to-date.
        /// </summary>
        public void ReportOutputContent(FileArtifact artifact, in FileMaterializationInfo hash, PipOutputOrigin origin)
        {
            EnsureUnsealed();
            InnerUnsealedState.OutputContent.Add((artifact, hash, origin));
        }

        /// <summary>
        /// Record the result of running process in the sandbox
        /// </summary>
        public void ReportSandboxedExecutionResult(SandboxedProcessPipExecutionResult executionResult)
        {
            EnsureUnsealed();
            m_numberOfWarnings = executionResult.NumberOfWarnings;
            m_pipProperties = executionResult.PipProperties;
            m_hasUserRetries = executionResult.HadUserRetries;
            m_retryInfo = executionResult.RetryInfo;
            InnerUnsealedState.ExecutionResult = executionResult;
            SharedDynamicDirectoryWriteAccesses = executionResult.SharedDynamicDirectoryWriteAccesses;
            CreatedDirectories = executionResult.CreatedDirectories;
            m_exitCode = executionResult.ExitCode;
        }

        public void ReportFileAccesses(FileAccessReportingContext fileAccessReportingContext)
        {
            EnsureUnsealed();

            // We have all violations now.
            UnexpectedFileAccessCounters unexpectedFilesAccesses = fileAccessReportingContext.Counters;
            ReportUnexpectedFileAccesses(unexpectedFilesAccesses);

            // Set file access violations which were not allowlisted for use by file access violation analyzer
            InnerUnsealedState.FileAccessViolationsNotAllowlisted = fileAccessReportingContext.FileAccessViolationsNotAllowlisted?.ToList();
            InnerUnsealedState.AllowlistedFileAccessViolations = fileAccessReportingContext.AllowlistedFileAccessViolations?.ToList();
        }

        /// <summary>
        /// Merges all the UnExpectedFileAccesses, SharedDynamicDirectoryWriteAccess from all the inline retries for DFA analysis.
        /// </summary>
        /// <remarks>
        /// Inline - retry happens on the same worker. Reschedule - where the retry happens on a different worker.
        /// This method also ensures that we do not do the union of the lists unless there has been a retry.
        /// </remarks>
        public void MergeAllFileAccessesAndViolationsForInlineRetry(SandboxedProcessPipExecutionResult sandboxedProcessPipExecutionResult)
        {
            if (sandboxedProcessPipExecutionResult.PreviousResult == null)
            {
                return;
            }

            EnsureUnsealed();

            HashSet<ReportedFileAccess> allFileAccessViolationsNotAllowlisted = new HashSet<ReportedFileAccess>();
            HashSet<ReportedFileAccess> allFileAccessViolationsAllowlisted = new HashSet<ReportedFileAccess>();
            Dictionary<AbsolutePath, HashSet<FileArtifactWithAttributes>> unionSharedOpaqueDirectoryWriteAccesses = new Dictionary<AbsolutePath, HashSet<FileArtifactWithAttributes>>();

            while (sandboxedProcessPipExecutionResult != null)
            {
                // FileAccessViolationsNotAllowlisted
                if (sandboxedProcessPipExecutionResult.UnexpectedFileAccesses?.FileAccessViolationsNotAllowlisted != null)
                {
                    allFileAccessViolationsNotAllowlisted.UnionWith(sandboxedProcessPipExecutionResult.UnexpectedFileAccesses?.FileAccessViolationsNotAllowlisted);
                }

                // AllowlistedFileAccessViolations
                if (sandboxedProcessPipExecutionResult.UnexpectedFileAccesses?.AllowlistedFileAccessViolations != null)
                {
                    allFileAccessViolationsAllowlisted.UnionWith(sandboxedProcessPipExecutionResult.UnexpectedFileAccesses?.AllowlistedFileAccessViolations);
                }

                // SharedDynamicDirectoryWriteAccesses
                if (sandboxedProcessPipExecutionResult.SharedDynamicDirectoryWriteAccesses != null)
                {
                    foreach (var entry in sandboxedProcessPipExecutionResult.SharedDynamicDirectoryWriteAccesses)
                    {
                        if (!unionSharedOpaqueDirectoryWriteAccesses.TryGetValue(entry.Key, out var existingSetOfFileArtifactWithAttributes))
                        {
                            unionSharedOpaqueDirectoryWriteAccesses.Add(entry.Key, new HashSet<FileArtifactWithAttributes>(entry.Value));
                        }
                        else
                        {
                            foreach (var value in entry.Value)
                            {
                                existingSetOfFileArtifactWithAttributes.Add(value);
                            }
                        }
                    }
                }

                sandboxedProcessPipExecutionResult = sandboxedProcessPipExecutionResult.PreviousResult;
            }

            // We do not merge all of the ObservedFileAccesses - AllowedUndeclaredSourceReads or DynamicObservations generated by ValidateObservedFileAccessesAsync.
            // Since it is not desirable to call this method for every retry given the existing design and this is less concering as it is a read access.
            if (allFileAccessViolationsNotAllowlisted != null && allFileAccessViolationsNotAllowlisted.Count > 0)
            {
                InnerUnsealedState.FileAccessViolationsNotAllowlisted = allFileAccessViolationsNotAllowlisted.ToList();
            }

            if (allFileAccessViolationsAllowlisted != null && allFileAccessViolationsAllowlisted.Count > 0)
            {
                InnerUnsealedState.AllowlistedFileAccessViolations = allFileAccessViolationsAllowlisted.ToList();
            }

            if (unionSharedOpaqueDirectoryWriteAccesses != null && unionSharedOpaqueDirectoryWriteAccesses.Count > 0)
            {
                SharedDynamicDirectoryWriteAccesses = unionSharedOpaqueDirectoryWriteAccesses.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyCollection<FileArtifactWithAttributes>)kvp.Value);
            }

        }


        /// <summary>
        /// Records the start and stop time for a pip.
        /// </summary>
        public void ReportExecutionSpan(DateTime executionStart, DateTime executionStop)
        {
            Contract.Requires(executionStart.Kind == DateTimeKind.Utc);
            Contract.Requires(executionStop.Kind == DateTimeKind.Utc);
            InnerUnsealedState.ExecutionStart = executionStart;
            InnerUnsealedState.ExecutionStop = executionStop;
        }

        /// <summary>
        /// Records the time it took to push the pip outputs to the cache
        /// </summary>
        public void ReportPushOutputsToCacheDurationMs(long durationMs)
        {
            InnerUnsealedState.PushOutputsToCacheDurationMs = durationMs;
        }

        /// <summary>
        /// Record unexpected file access counters
        /// </summary>
        public void ReportUnexpectedFileAccesses(UnexpectedFileAccessCounters unexpectedFileAccessCounters)
        {
            InnerUnsealedState.UnexpectedFileAccessCounters = unexpectedFileAccessCounters;
        }

        /// <summary>
        /// Records the output directory along with its contents as strings.
        /// </summary>
        public void ReportDirectoryOutput(DirectoryArtifact directoryArtifact, IReadOnlyList<FileArtifactWithAttributes> contents)
        {
            EnsureUnsealed();
            InnerUnsealedState.DirectoryOutputs.Add((directoryArtifact, ReadOnlyArray<FileArtifactWithAttributes>.From(contents)));
        }

        /// <summary>
        /// Records that a collection of directories was created
        /// </summary>
        public void ReportCreatedDirectories(IReadOnlySet<AbsolutePath> directories)
        {
            EnsureUnsealed();
            m_createdDirectories = directories;
        }

        private void EnsureSealed()
        {
            Contract.Assert(IsSealed, "Must be sealed to retrieve state");
        }

        private void EnsureUnsealed()
        {
            Contract.Assert(!IsSealed, "Cannot be modified after sealing");
        }

        /// <summary>
        /// Gets a failure result without run information. An error must be logged before calling this method.
        /// </summary>
        public static ExecutionResult GetFailureNotRunResult(LoggingContext loggingContext)
        {
            var result = new ExecutionResult();
            result.SetResult(loggingContext, PipResultStatus.Failed);
            result.Seal();

            return result;
        }

        /// <summary>
        /// Gets a failure result for testing purposes.
        /// </summary>
        public static ExecutionResult GetFailureResultForTesting()
        {
            var result = new ExecutionResult();
            result.InnerUnsealedState.Result = PipResultStatus.Failed;
            result.Seal();
            return result;
        }

        /// <summary>
        /// Gets a canceled result without run information for retry.
        /// </summary>
        public static ExecutionResult GetRetryableNotRunResult(LoggingContext loggingContext, RetryInfo retryInfo)
        {
            var result = new ExecutionResult();
            result.SetResult(loggingContext, PipResultStatus.Canceled, retryInfo);
            result.Seal();

            return result;
        }

        /// <summary>
        /// Gets a canceled result without run information.
        /// </summary>
        public static ExecutionResult GetCancelResult(LoggingContext loggingContext)
        {
            var result = new ExecutionResult();
            result.SetResult(loggingContext, PipResultStatus.Canceled);
            result.Seal();

            return result;
        }

        /// <summary>
        /// Gets an empty success result for Materialization
        /// </summary>
        public static ExecutionResult GetEmptySuccessResult(LoggingContext loggingContext)
        {
            var result = new ExecutionResult();
            result.SetResult(loggingContext, PipResultStatus.Succeeded);
            result.Seal();

            return result;
        }

        /// <summary>
        /// Disallow further modifications, finalize state, and allow reading state
        /// </summary>
        public void Seal()
        {
            if (!IsSealed)
            {
                Contract.Assert(InnerUnsealedState.Result.HasValue, "Result must be set.");

                if (m_unsealedState != null)
                {
                    m_result = m_unsealedState.Result.Value;
                    m_outputContent = ReadOnlyArray<(FileArtifact, FileMaterializationInfo, PipOutputOrigin)>.From(m_unsealedState.OutputContent);
                    m_directoryOutputs = ReadOnlyArray<(DirectoryArtifact, ReadOnlyArray<FileArtifactWithAttributes>)>.From(m_unsealedState.DirectoryOutputs);

                    // If the result from the sandbox was not reported, that means this pip came from the cache, and therefore
                    // the shared dynamic accesses need to be populated from the already reported output directories
                    if (!m_unsealedState.SandboxedResultReported)
                    {
                        SharedDynamicDirectoryWriteAccesses = ComputeSharedDynamicAccessesFrom(m_directoryOutputs);
                    }

                    m_mustBeConsideredPerpetuallyDirty = m_unsealedState.MustBeConsideredPerpetuallyDirty;
                    m_dynamicObservations = m_unsealedState.DynamicObservations;
                    m_allowedUndeclaredSourceReads = m_unsealedState.AllowedUndeclaredSourceReads;
                    m_createdDirectories ??= CollectionUtilities.EmptySet<AbsolutePath>();

                    SandboxedProcessPipExecutionResult processResult = m_unsealedState.ExecutionResult;

                    if (processResult != null
                        && processResult.Status != SandboxedProcessPipExecutionStatus.PreparationFailed
                        && (processResult.RetryInfo == null
                            || !(processResult.RetryInfo.RetryReason).IsPreProcessExecOrRemotingInfraFailure()))
                    {
                        if (!(processResult.Status == SandboxedProcessPipExecutionStatus.Succeeded ||
                            processResult.Status == SandboxedProcessPipExecutionStatus.ExecutionFailed ||
                            processResult.Status == SandboxedProcessPipExecutionStatus.Canceled ||
                            processResult.Status == SandboxedProcessPipExecutionStatus.FileAccessMonitoringFailed ||
                            processResult.Status == SandboxedProcessPipExecutionStatus.SharedOpaquePostProcessingFailed ||
                            processResult.RetryInfo?.RetryReason == RetryReason.OutputWithNoFileAccessFailed ||
                            processResult.RetryInfo?.RetryReason == RetryReason.MismatchedMessageCount))
                        {
                            string retryReason = processResult.RetryInfo != null ? $", Retry Reason: {processResult.RetryInfo.RetryReason}, Retry Location: {processResult.RetryInfo.RetryMode}" : "";
                            Contract.Assert(false, "Invalid execution status: " + processResult.Status + retryReason);
                        }

                        Contract.Assert(
                            processResult.PrimaryProcessTimes != null,
                            "Execution counters are available when the status is not PreparationFailed");
                        Contract.Assert(
                            m_unsealedState.UnexpectedFileAccessCounters.HasValue,
                            "File access counters are available when the status is not PreparationFailed");
                        Contract.Assert(
                            m_unsealedState.FileAccessViolationsNotAllowlisted.HasValue,
                            "File access violations not set when the status is not PreparationFailed");

                        TimeSpan wallClockTime = (TimeSpan)processResult.PrimaryProcessTimes?.TotalWallClockTime;
                        JobObject.AccountingInformation jobAccounting = processResult.JobAccountingInformation ??
                                                                        default(JobObject.AccountingInformation);
                        m_fileAccessViolationsNotAllowlisted = m_unsealedState.FileAccessViolationsNotAllowlisted.Value;
                        m_allowlistedFileAccessViolations = m_unsealedState.AllowlistedFileAccessViolations.Value;

                        m_performanceInformation = new ProcessPipExecutionPerformance(
                            m_result.ToExecutionLevel(),
                            m_unsealedState.ExecutionStart,
                            m_unsealedState.ExecutionStop,
                            fingerprint: m_weakFingerprint?.Hash ?? FingerprintUtilities.ZeroFingerprint,
                            processExecutionTime: wallClockTime,
                            fileMonitoringViolations: ConvertFileMonitoringViolationCounters(m_unsealedState.UnexpectedFileAccessCounters.Value),
                            ioCounters: jobAccounting.IO,
                            userTime: jobAccounting.UserTime,
                            kernelTime: jobAccounting.KernelTime,
                            memoryCounters: jobAccounting.MemoryCounters,
                            numberOfProcesses: jobAccounting.NumberOfProcesses,
                            workerId: 0,
                            suspendedDurationMs: processResult.SuspendedDurationMs,
                            pushOutputsToCacheDurationMs: m_unsealedState.PushOutputsToCacheDurationMs);
                    }
                }
                else
                {
                    m_dynamicObservations = ReadOnlyArray<(AbsolutePath, DynamicObservationKind)>.Empty;
                    m_outputContent = ReadOnlyArray<(FileArtifact, FileMaterializationInfo, PipOutputOrigin)>.Empty;
                    m_directoryOutputs = ReadOnlyArray<(DirectoryArtifact, ReadOnlyArray<FileArtifactWithAttributes>)>.Empty;
                    m_allowedUndeclaredSourceReads = CollectionUtilities.EmptySet<AbsolutePath>();
                    m_createdDirectories = CollectionUtilities.EmptySet<AbsolutePath>();
                }

                m_unsealedState = null;
                IsSealed = true;
            }
        }

        private static ReadOnlyDictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>> ComputeSharedDynamicAccessesFrom(ReadOnlyArray<(DirectoryArtifact, ReadOnlyArray<FileArtifactWithAttributes>)> directoryOutputs)
        {
            var sharedDynamicAccesses = directoryOutputs
                .Where(kvp => kvp.Item1.IsSharedOpaque)
                .ToDictionary(kvp => kvp.Item1.Path, kvp => (IReadOnlyCollection<FileArtifactWithAttributes>)kvp.Item2);

            return new ReadOnlyDictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>>(sharedDynamicAccesses);
        }

        private static FileMonitoringViolationCounters ConvertFileMonitoringViolationCounters(UnexpectedFileAccessCounters counters)
        {
            return new FileMonitoringViolationCounters(
                numFileAccessViolationsNotAllowlisted: counters.NumFileAccessViolationsNotAllowlisted,
                numFileAccessesAllowlistedAndCacheable: counters.NumFileAccessesAllowlistedAndCacheable,
                numFileAccessesAllowlistedButNotCacheable: counters.NumFileAccessesAllowlistedButNotCacheable);
        }

        private sealed class UnsealedState
        {
            private SandboxedProcessPipExecutionResult m_executionResult;

            public bool SandboxedResultReported { get; private set; }
            public Optional<IReadOnlyList<ReportedFileAccess>> FileAccessViolationsNotAllowlisted;
            public Optional<IReadOnlyList<ReportedFileAccess>> AllowlistedFileAccessViolations;
            public PipResultStatus? Result;
            public SandboxedProcessPipExecutionResult ExecutionResult
            {
                get => m_executionResult;
                set
                {
                    SandboxedResultReported = true;
                    m_executionResult = value;
                }
            }
            public UnexpectedFileAccessCounters? UnexpectedFileAccessCounters;
            public DateTime ExecutionStart;
            public DateTime ExecutionStop;
            public bool MustBeConsideredPerpetuallyDirty;
            public ReadOnlyArray<(AbsolutePath Path, DynamicObservationKind Kind)> DynamicObservations = ReadOnlyArray<(AbsolutePath, DynamicObservationKind)>.Empty;
            public IReadOnlySet<AbsolutePath> AllowedUndeclaredSourceReads = CollectionUtilities.EmptySet<AbsolutePath>();

            /// <summary>
            /// How long it took to push the process outputs to the cache
            /// </summary>
            public long PushOutputsToCacheDurationMs;

            public readonly List<(FileArtifact, FileMaterializationInfo, PipOutputOrigin)> OutputContent =
                new List<(FileArtifact, FileMaterializationInfo, PipOutputOrigin)>();

            public readonly List<(DirectoryArtifact, ReadOnlyArray<FileArtifactWithAttributes>)> DirectoryOutputs =
                new List<(DirectoryArtifact, ReadOnlyArray<FileArtifactWithAttributes>)>();

            public UnsealedState()
            {
                ExecutionStart = DateTime.UtcNow;
                ExecutionStop = ExecutionStart;
            }
        }
    }
}
