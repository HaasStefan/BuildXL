// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Engine.Cache.Artifacts;
using BuildXL.Native.IO;
using BuildXL.Pips;
using BuildXL.Pips.Artifacts;
using BuildXL.Pips.Operations;
using BuildXL.Scheduler.ChangeAffectedOutput;
using BuildXL.Scheduler.Tracing;
using BuildXL.Storage;
using BuildXL.Storage.ChangeTracking;
using BuildXL.Storage.Fingerprints;
using BuildXL.Tracing;
using BuildXL.Utilities;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Core.Tasks;
using static BuildXL.Tracing.Diagnostics;
using static BuildXL.Utilities.Core.FormattableStringEx;
using Logger = BuildXL.Scheduler.Tracing.Logger;
using System.Runtime;
using BuildXL.Utilities.ParallelAlgorithms;

namespace BuildXL.Scheduler.Artifacts
{
    /// <summary>
    /// This class tracks content hashes and materialization state of files allowing
    /// managing hashing and materialization of pip inputs and outputs. As pips run
    /// they report content (potentially not materialized). Later pips may request materialization
    /// of inputs whose content must have been reported by an earlier operation (either through
    /// <see cref="ReportOutputContent"/>, <see cref="ReportInputContent"/>, <see cref="TryHashDependenciesAsync"/>,
    /// or <see cref="TryRegisterOutputDirectoriesAndHashSharedOpaqueOutputsAsync"/>.
    ///
    /// Reporting:
    /// * <see cref="ReportOutputContent"/> is used to report the output content of all pips except hash source file
    /// * <see cref="ReportDynamicDirectoryContents"/> is used to report the files produced into dynamic output directories
    /// * <see cref="TryRegisterOutputDirectoriesAndHashSharedOpaqueOutputsAsync"/> is used to report content for the hash source file pip or during incremental scheduling.
    /// * <see cref="TryHashDependenciesAsync"/> is used to report content for inputs prior to running/cache lookup.
    /// * <see cref="ReportInputContent"/> is used to report the content of inputs (distributed workers only since prerequisite pips
    /// will run on the worker and report output content)
    ///
    /// Querying:
    /// * <see cref="GetInputContent"/> retrieves hash of reported content
    /// * <see cref="TryQuerySealedOrUndeclaredInputContentAsync"/> gets the hash of the input file inside a sealed directory or a file that
    /// is outside any declared containers, but allowed undeclared reads are on.
    /// This may entail hashing the file.
    /// * <see cref="ListSealedDirectoryContents"/> gets the files inside a sealed directory (including dynamic directories which
    /// have been reported via <see cref="ReportDynamicDirectoryContents"/>.
    ///
    /// Materialization:
    /// At a high level materialization has the following workflow
    /// * Get files/directories to materialize
    /// * Delete materialized output directories
    /// * Delete files required to be absent (reported with absent file hash)
    /// * Pin hashes of existent files in the cache
    /// * Place existent files from cache
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable")]
    public sealed class FileContentManager : IQueryableFileContentManager
    {
        private readonly ITempCleaner m_tempDirectoryCleaner;

        #region Internal State

        /// <summary>
        /// Cached completed pip output origin tasks
        /// </summary>
        private readonly ReadOnlyArray<Task<PipOutputOrigin>> m_originTasks =
            ReadOnlyArray<Task<PipOutputOrigin>>.From(EnumTraits<PipOutputOrigin>.EnumerateValues()
            .Select(origin => Task.FromResult(origin)));

        private static readonly Task<FileMaterializationInfo?> s_placeHolderFileHashTask = Task.FromResult<FileMaterializationInfo?>(null);

        /// <summary>
        /// Statistics for file content operations
        /// </summary>
        private FileContentStats m_stats;

        /// <summary>
        /// Gets the current file content stats
        /// </summary>
        public FileContentStats FileContentStats => m_stats;

        /// <summary>
        /// Gets the file content manager host.
        /// </summary>
        public IFileContentManagerHost Host => m_host;

        /// <summary>
        /// Dictionary of number of cache content hits by cache name.
        /// </summary>
        private readonly ConcurrentDictionary<string, int> m_cacheContentSource = new ConcurrentDictionary<string, int>();

        // Semaphore to limit concurrency for cache IO calls
        private readonly SemaphoreSlim m_materializationSemaphore = new SemaphoreSlim(EngineEnvironmentSettings.MaterializationConcurrency);

        /// <summary>
        /// Pending materializations
        /// </summary>
        private readonly ConcurrentBigMap<FileArtifact, Task<PipOutputOrigin>> m_materializationTasks = new ConcurrentBigMap<FileArtifact, Task<PipOutputOrigin>>();

        /// <summary>
        /// Map of deletion tasks for materialized directories
        /// </summary>
        private readonly ConcurrentBigMap<DirectoryArtifact, Task<bool>> m_dynamicDirectoryDeletionTasks = new ConcurrentBigMap<DirectoryArtifact, Task<bool>>();

        /// <summary>
        /// Map of tasks to hydrate previously materialized virtual files
        /// </summary>
        private readonly ConcurrentBigMap<AbsolutePath, VirtualizationState> m_fileVirtualizationStates = new ConcurrentBigMap<AbsolutePath, VirtualizationState>();

        /// <summary>
        /// The directories which have already been materialized
        /// </summary>
        private readonly ConcurrentBigSet<DirectoryArtifact> m_virtualizedDirectories = new ConcurrentBigSet<DirectoryArtifact>();

        /// <summary>
        /// The directories which have already been materialized
        /// </summary>
        private readonly ConcurrentBigSet<DirectoryArtifact> m_materializedDirectories = new ConcurrentBigSet<DirectoryArtifact>();

        /// <summary>
        /// File hashing tasks for tracking completion of hashing. Entries here are transient and only used to ensure
        /// a file is hashed only once
        /// </summary>
        private readonly ConcurrentBigMap<FileArtifact, Task<FileMaterializationInfo?>> m_fileArtifactHashTasks =
            new ConcurrentBigMap<FileArtifact, Task<FileMaterializationInfo?>>();

        /// <summary>
        /// The contents of dynamic output directories
        /// </summary>
        private readonly ConcurrentBigMap<DirectoryArtifact, SortedReadOnlyArray<FileArtifact, OrdinalFileArtifactComparer>> m_sealContents =
            new ConcurrentBigMap<DirectoryArtifact, SortedReadOnlyArray<FileArtifact, OrdinalFileArtifactComparer>>();

        /// <summary>
        /// Current materializations for files by their path. Allows ensuring that latest rewrite count is the only file materialized
        /// </summary>
        private readonly ConcurrentBigMap<AbsolutePath, FileArtifact> m_currentlyMaterializingFilesByPath = new ConcurrentBigMap<AbsolutePath, FileArtifact>();

        /// <summary>
        /// All the sealed files for registered sealed directories. Maps the sealed path to the file artifact which was sealed. We always seal at a
        /// particular rewrite count.
        /// </summary>
        private readonly ConcurrentBigMap<AbsolutePath, FileArtifact> m_sealedFiles = new ConcurrentBigMap<AbsolutePath, FileArtifact>();

        /// <summary>
        /// The registered seal directories (all the files in the directory should be in <see cref="m_sealedFiles"/> unless it is a sealed source directory
        /// </summary>
        private readonly ConcurrentBigSet<DirectoryArtifact> m_registeredSealDirectories = new ConcurrentBigSet<DirectoryArtifact>();

        /// <summary>
        /// Maps paths to the corresponding seal source directory artifact
        /// </summary>
        private readonly ConcurrentBigMap<AbsolutePath, DirectoryArtifact> m_sealedSourceDirectories =
            new ConcurrentBigMap<AbsolutePath, DirectoryArtifact>();

        /// <summary>
        /// Pool of artifact state objects contain state used during hashing and materialization operations
        /// </summary>
        private readonly ConcurrentQueue<PipArtifactsState> m_statePool = new ConcurrentQueue<PipArtifactsState>();

        /// <summary>
        /// Content hashes for source and output artifacts. Source artifacts have content hashes that are known statically,
        /// whereas output artifacts have hashes that are determined by tool execution (including cached execution).
        /// </summary>
        private readonly ConcurrentBigMap<FileArtifact, FileMaterializationInfo> m_fileArtifactContentHashes =
            new ConcurrentBigMap<FileArtifact, FileMaterializationInfo>();

        /// <summary>
        /// The distinct content used during the build and associated hashes. A <see cref="ContentId"/> represents
        /// a pairing of file and hash (via an index into <see cref="m_fileArtifactContentHashes"/>). In this set, the hash is used as the key.
        /// </summary>
        private readonly ConcurrentBigSet<ContentId> m_allCacheContentHashes = new ConcurrentBigSet<ContentId>();

        /// <summary>
        /// Set of paths for which <see cref="TryQueryContentAsync"/> was called which were determined to be a directory
        /// </summary>
        private readonly ConcurrentBigSet<AbsolutePath> m_contentQueriedDirectoryPaths = new ConcurrentBigSet<AbsolutePath>();

        /// <summary>
        /// Maps files in registered dynamic output directories to the corresponding dynamic output directory artifact
        /// </summary>
        private readonly ConcurrentBigMap<FileArtifact, DirectoryArtifact> m_dynamicOutputFileDirectories =
            new ConcurrentBigMap<FileArtifact, DirectoryArtifact>();

        /// <summary>
        /// Output paths which are allowed undeclared source/alien files rewrites. These are allowed based on configured relaxing policies.
        /// </summary>
        private readonly ConcurrentBigSet<AbsolutePath> m_allowedFileRewriteOutputs = new ConcurrentBigSet<AbsolutePath>();

        private readonly ConcurrentBigSet<AbsolutePath> m_pathsWithoutFileArtifact = new ConcurrentBigSet<AbsolutePath>();

        private readonly ObjectPool<OutputDirectoryEnumerationData> m_outputEnumerationDataPool =
            new ObjectPool<OutputDirectoryEnumerationData>(
                () => new OutputDirectoryEnumerationData(),
                data => { data.Clear(); return data; });

        private readonly ResultBasedActionBlockSlim<bool> m_recoverContentActionBlock;

        #endregion

        #region External State (i.e. passed into constructor)

        private PipExecutionContext Context => m_host.Context;

        private SemanticPathExpander SemanticPathExpander => m_host.SemanticPathExpander;

        private LocalDiskContentStore LocalDiskContentStore => m_host.LocalDiskContentStore;

        private IArtifactContentCache ArtifactContentCache { get; }

        private IConfiguration Configuration => m_host.Configuration;

        private IExecutionLogTarget ExecutionLog => m_host.ExecutionLog;

        private IOperationTracker OperationTracker { get; }

        private readonly FlaggedHierarchicalNameDictionary<Unit> m_outputMaterializationExclusionMap;

        private ILocalDiskFileSystemExistenceView m_localDiskFileSystemView;
        private ILocalDiskFileSystemExistenceView LocalDiskFileSystemView => m_localDiskFileSystemView ?? LocalDiskContentStore;

        /// <summary>
        /// The host for getting data about pips
        /// </summary>
        private readonly IFileContentManagerHost m_host;

        private bool IsDistributedWorker => Configuration.Distribution.BuildRole == DistributedBuildRoles.Worker;

        /// <summary>
        /// Unit tests only. Used to suppress warnings which are not considered by unit tests
        /// when hashing source files
        /// </summary>
        internal bool TrackFilesUnderInvalidMountsForTests = false;

        private static readonly SortedReadOnlyArray<FileArtifact, OrdinalFileArtifactComparer> s_emptySealContents =
            SortedReadOnlyArray<FileArtifact, OrdinalFileArtifactComparer>.CloneAndSort(new FileArtifact[0], OrdinalFileArtifactComparer.Instance);

        /// <summary>
        /// Holds change affected artifacts of the build
        /// </summary>
        public SourceChangeAffectedInputs SourceChangeAffectedInputs { get; }

        private enum PlaceFile { Success, UserError, InternalError }

        #endregion

        /// <summary>
        /// Creates a new file content manager with the specified host for providing
        /// auxillary data
        /// </summary>
        public FileContentManager(
            IFileContentManagerHost host,
            IOperationTracker operationTracker,
            ITempCleaner tempDirectoryCleaner = null)
        {
            m_host = host;
            ArtifactContentCache = new ElidingArtifactContentCacheWrapper(host.ArtifactContentCache);
            OperationTracker = operationTracker;
            m_tempDirectoryCleaner = tempDirectoryCleaner;

            m_outputMaterializationExclusionMap = new FlaggedHierarchicalNameDictionary<Unit>(host.Context.PathTable, HierarchicalNameTable.NameFlags.Root);
            foreach (var outputMaterializationExclusionRoot in host.Configuration.Schedule.OutputMaterializationExclusionRoots)
            {
                m_outputMaterializationExclusionMap.TryAdd(outputMaterializationExclusionRoot.Value, Unit.Void);
            }

            SourceChangeAffectedInputs = new SourceChangeAffectedInputs(this);
            m_recoverContentActionBlock = new ResultBasedActionBlockSlim<bool>(
                // The concurrency level is set to 2x the number of processors to allow for some oversubcribed parallelism, but not too much that can
                // degrade performance due to oversubcription in the thread pool.
                2 * Environment.ProcessorCount,
                m_host.Context.CancellationToken);
        }

        /// <summary>
        /// Sets local file system observer
        /// </summary>
        internal void SetLocalDiskFileSystemExistenceView(ILocalDiskFileSystemExistenceView localDiskFileSystem)
        {
            Contract.Requires(localDiskFileSystem != null);
            m_localDiskFileSystemView = localDiskFileSystem;
        }

        /// <summary>
        /// Registers the completion of a seal directory pip
        /// </summary>
        /// <returns> True if the register directory operation was successful or false if deleting the directory contents failed. </returns>
        public bool TryRegisterStaticDirectory(DirectoryArtifact artifact)
        {
            bool result = true;

            try
            {
                RegisterDirectoryContents(artifact);
            }
            catch (BuildXLException ex)
            {
                Logger.Log.FailedToSealDirectory(
                    m_host.LoggingContext,
                    artifact.Path.ToString(Context.PathTable),
                    GetAssociatedPipDescription(artifact),
                    ex.LogEventMessage);
                result = false;
            }

            return result;
        }

        /// <summary>
        /// Records the hash of an input of the pip. All static inputs must be reported, even those that were already up-to-date.
        /// </summary>
        public bool ReportWorkerPipInputContent(LoggingContext loggingContext, FileArtifact artifact, in FileMaterializationInfo info)
        {
            Contract.Assert(IsDistributedWorker);

            SetFileArtifactContentHashResult result = SetFileArtifactContentHash(artifact, info, PipOutputOrigin.NotMaterialized);

            // Notify the host with content that was reported
            m_host.ReportContent(artifact, info, PipOutputOrigin.NotMaterialized);

            if (result == SetFileArtifactContentHashResult.HasConflictingExistingEntry)
            {
                var existingInfo = m_fileArtifactContentHashes[artifact];

                // File was already hashed (i.e. seal directory input) prior to this pip which has the explicit input.
                // Report the content mismatch and fail.
                ReportWorkerContentMismatch(
                       loggingContext,
                       Context.PathTable,
                       artifact,
                       expectedHash: info.Hash,
                       actualHash: existingInfo.Hash);

                return false;
            }

            return true;
        }

        /// <summary>
        /// Records the hash of an input of the pip. All static inputs must be reported, even those that were already up-to-date.
        /// </summary>
        public void ReportInputContent(FileArtifact artifact, in FileMaterializationInfo info, bool contentMismatchErrorsAreWarnings = false)
        {
            ReportContent(artifact, info, PipOutputOrigin.NotMaterialized, contentMismatchErrorsAreWarnings);
        }

        /// <summary>
        /// Records the hash of an output of the pip. All static outputs must be reported, even those that were already up-to-date.
        /// </summary>
        public void ReportOutputContent(
            OperationContext operationContext,
            long pipSemiStableHash,
            FileArtifact artifact,
            in FileMaterializationInfo info,
            PipOutputOrigin origin,
            bool doubleWriteErrorsAreWarnings = false)
        {
            Contract.Requires(artifact.IsOutputFile);

            if (info.IsUndeclaredFileRewrite)
            {
                m_allowedFileRewriteOutputs.Add(artifact.Path);
            }

            if (ReportContent(artifact, info, origin, doubleWriteErrorsAreWarnings))
            {
                if (origin != PipOutputOrigin.NotMaterialized && artifact.IsOutputFile)
                {
                    LogOutputOrigin(operationContext, pipSemiStableHash, artifact.Path, Context.PathTable, info, origin);
                }
            }
        }

        /// <summary>
        /// Ensures pip source inputs are hashed
        /// </summary>
        public async Task<Possible<Unit>> TryHashSourceDependenciesAsync(Pip pip, OperationContext operationContext)
        {
            using (PipArtifactsState state = GetPipArtifactsState())
            {
                // Get inputs
                PopulateDependencies(pip, state.PipArtifacts, includeLazyInputs: true, onlySourceFiles: true);

                var maybeInputsHashed = await TryHashFileArtifactsAsync(state, operationContext, pip.ProcessAllowsUndeclaredSourceReads, pip.GetDescription(Context));

                if (!maybeInputsHashed.Succeeded)
                {
                    return maybeInputsHashed.Failure;
                }

                return maybeInputsHashed;
            }
        }

        /// <summary>
        /// Returns the hashes for the source file inputs
        /// </summary>
        public IReadOnlyList<(FileArtifact, FileContentInfo)> GetSourceInputHashes(Pip pip)
        {
            using (PipArtifactsState state = GetPipArtifactsState())
            {
                // Get inputs
                PopulateDependencies(pip, state.PipArtifacts, includeLazyInputs: true, onlySourceFiles: true);

                List<(FileArtifact, FileContentInfo)> sourceHashes = new List<(FileArtifact, FileContentInfo)>(state.PipArtifacts.Count);
                foreach (var artifact in state.PipArtifacts)
                {
                    sourceHashes.Add((artifact.FileArtifact, GetInputContent(artifact.FileArtifact).FileContentInfo));
                }

                return sourceHashes;
            }
        }

        /// <summary>
        /// Ensures pip inputs are hashed
        /// </summary>
        public async Task<Possible<Unit>> TryHashDependenciesAsync(Pip pip, OperationContext operationContext)
        {
            // If force skip dependencies then try to hash the files. How does this work with lazy materialization? Or distributed build?
            // Probably assume dependencies are materialized if force skip dependencies is enabled
            using (PipArtifactsState state = GetPipArtifactsState())
            {
                // Get inputs
                PopulateDependencies(pip, state.PipArtifacts, includeLazyInputs: true);

                // In case of dirty build, we need to hash the seal contents without relying on consumers of seal contents.
                SealDirectory sealDirectory = pip as SealDirectory;
                if (sealDirectory != null)
                {
                    foreach (var input in sealDirectory.Contents)
                    {
                        state.PipArtifacts.Add(FileOrDirectoryArtifact.Create(input));
                    }
                }

                return await TryHashArtifactsAsync(operationContext, state, pip.ProcessAllowsUndeclaredSourceReads, artifactsProducer: null);
            }
        }

        /// <summary>
        /// Registers output directories as being materialized and hashes outputs shared opaques
        /// Because shared opaques can recursively contain one another, we can't rely on hashing dependencies to pick up their contents.
        /// </summary>
        public async Task<Possible<Unit>> TryRegisterOutputDirectoriesAndHashSharedOpaqueOutputsAsync(Pip pip, OperationContext operationContext)
        {
            using (PipArtifactsState state = GetPipArtifactsState())
            {
                // Get outputs
                PopulateOutputs(pip, state.PipArtifacts, fileOrDirectory =>
                {
                    if (fileOrDirectory.IsDirectory)
                    {
                        if (!state.EnforceOutputMaterializationExclusionRootsForDirectoryArtifacts)
                        {
                            // If running TryRegisterOutputDirectoriesAndHashSharedOpaqueOutputsAsync, the pip's outputs are assumed to be materialized
                            // Mark directory artifacts as materialized so we don't try to materialize them later
                            // NOTE: We don't hash the contents of the directory because they will be hashed lazily
                            // by consumers of the seal directory
                            MarkDirectoryMaterialization(fileOrDirectory.DirectoryArtifact, state.Virtualize);
                        }

                        return fileOrDirectory.DirectoryArtifact.IsSharedOpaque;
                    }

                    return false;
                });

                // Most artifacts are hashes as dependencies when the pip which consumes them runs.
                // However shared opaque contents always need to be registered
                // because the consumer might need to access the contents of a directory multiple levels above it using composite opaques.
                return await TryHashArtifactsAsync(operationContext, state, pip.ProcessAllowsUndeclaredSourceReads, artifactsProducer: pip);
            }
        }

        /// <summary>
        /// Whether the path represents an output that rewrote an undeclared source or alien file
        /// </summary>
        public bool IsAllowedFileRewriteOutput(AbsolutePath path)
        {
            return m_allowedFileRewriteOutputs.Contains(path);
        }

        private async Task<Possible<Unit>> TryHashArtifactsAsync(
            OperationContext operationContext,
            PipArtifactsState state,
            bool allowUndeclaredSourceReads,
            Pip artifactsProducer)
        {
            if (state.PipArtifacts.Count == 0)
            {
                return await Unit.VoidTask;
            }

            using (operationContext.StartOperation(PipExecutorCounter.FileContentManagerEnumerateOutputDirectoryHashArtifacts))
            {
                var maybeReported = EnumerateAndTrackOutputDirectories(state, artifactsProducer, shouldReport: true);

                if (!maybeReported.Succeeded)
                {
                    return maybeReported.Failure;
                }
            }

            // Register the seal file contents of the directory dependencies
            RegisterDirectoryContents(state.PipArtifacts);

            // Hash inputs if necessary
            var maybeInputsHashed = await TryHashFileArtifactsAsync(state, operationContext, allowUndeclaredSourceReads);

            if (!maybeInputsHashed.Succeeded)
            {
                return maybeInputsHashed.Failure;
            }

            return Unit.Void;
        }

        /// <summary>
        /// Ensures pip directory inputs are registered with file content manager
        /// </summary>
        public void RegisterDirectoryDependencies(Pip pip)
        {
            using (PipArtifactsState state = GetPipArtifactsState())
            {
                // Get inputs
                PopulateDependencies(pip, state.PipArtifacts, registerDirectories: true);
            }
        }

        /// <summary>
        /// Ensures pip inputs are materialized
        /// </summary>
        public async Task<bool> TryMaterializeDependenciesAsync(Pip pip, OperationContext operationContext)
        {
            var result = await TryMaterializeDependenciesInternalAsync(pip, operationContext);
            return result == ArtifactMaterializationResult.Succeeded;
        }

        /// <summary>
        /// Ensures pip inputs are materialized and returns ArtifactMaterializationResult
        /// </summary>
        internal async Task<ArtifactMaterializationResult> TryMaterializeDependenciesInternalAsync(Pip pip, OperationContext operationContext)
        {
            using (PipArtifactsState state = GetPipArtifactsState())
            {
                // Get inputs
                PopulateDependencies(pip, state.PipArtifacts, registerDirectories: true);

                // Materialize inputs
                var result = await TryMaterializeArtifactsCore(new PipInfo(pip, Context), operationContext, state, materializatingOutputs: false, isDeclaredProducer: false, isApiServerRequest: false);
                Contract.Assert(result != ArtifactMaterializationResult.None);

                switch (result)
                {
                    case ArtifactMaterializationResult.Succeeded:
                        // TODO:
                        // There's an asymmetry here between materializing dependencies and materializing outputs.
                        // In materializing outputs we enumerate and track output directories because we need to ensure
                        // the incremental scheduling gets the latest version/fingerprints of the output directory.
                        // Materializing dependencies are only applied to lazy materialization, and that mode
                        // is incompatible with incremental scheduling. In the future, if we want to make lazy materialization
                        // compatible with incremental scheduling, then we need to address this issue.
                        break;
                    case ArtifactMaterializationResult.PlaceFileFailed:
                        if (state.InnerFailure is CacheTimeoutFailure)
                        {
                            Logger.Log.PipMaterializeDependenciesFromCacheTimeoutFailure(
                                operationContext,
                                pip.GetDescription(Context),
                                state.GetFailure().DescribeIncludingInnerFailures());
                        }
                        else
                        {
                            Logger.Log.PipMaterializeDependenciesFromCacheFailure(
                                operationContext,
                                pip.GetDescription(Context),
                                state.GetFailure().DescribeIncludingInnerFailures());
                        }

                        cacheMaterializationErrorLog(state);
                        break;
                    case ArtifactMaterializationResult.PlaceFileFailedDueToDeletionFailure:
                        Logger.Log.PipMaterializeDependenciesFromCacheFailureDueToFileDeletionFailure(
                            operationContext,
                            pip.GetDescription(Context),
                            state.GetFailure().DescribeIncludingInnerFailures());
                        cacheMaterializationErrorLog(state);
                        break;
                    case ArtifactMaterializationResult.VerifySourceFilesFailed:
                        Logger.Log.PipMaterializeDependenciesFailureDueToVerifySourceFilesFailed(
                            operationContext,
                            pip.GetDescription(Context),
                            state.GetFailure().DescribeIncludingInnerFailures());
                        break;
                    default:
                        // Catch-all error for non-cache dependency materialization failures
                        Logger.Log.PipMaterializeDependenciesFailureUnrelatedToCache(
                            operationContext,
                            pip.GetDescription(Context),
                            result.ToString(),
                            state.GetFailure().DescribeIncludingInnerFailures());
                        break;
                }

                return result;
            }

            void cacheMaterializationErrorLog(PipArtifactsState state)
            {
                ExecutionLog?.CacheMaterializationError(new CacheMaterializationErrorEventData()
                {
                    PipId = pip.PipId,
                    FailedFiles = state.FailedFiles.ToReadOnlyArray()
                });
            }
        }

        /// <summary>
        /// Ensures pip outputs are materialized
        /// </summary>
        public async Task<Possible<PipOutputOrigin>> TryMaterializeOutputsAsync(Pip pip, OperationContext operationContext)
        {
            using (PipArtifactsState state = GetPipArtifactsState())
            {
                bool hasExcludedOutput = false;

                // Get outputs
                PopulateOutputs(pip, state.PipArtifacts, exclude: output =>
                {
                    if (m_outputMaterializationExclusionMap.TryGetFirstMapping(output.Path.Value, out var mapping))
                    {
                        hasExcludedOutput = true;
                        return true;
                    }

                    return false;
                });

                // Register the seal file contents of the directory dependencies
                RegisterDirectoryContents(state.PipArtifacts);

                // Materialize outputs
                var result = await TryMaterializeArtifactsCore(new PipInfo(pip, Context), operationContext, state, materializatingOutputs: true, isDeclaredProducer: true, isApiServerRequest: false);
                if (result != ArtifactMaterializationResult.Succeeded)
                {
                    return state.GetFailure();
                }

                using (operationContext.StartOperation(PipExecutorCounter.FileContentManagerEnumerateOutputDirectoryMaterializeOutputs))
                {
                    var enumerateResult = EnumerateAndTrackOutputDirectories(
                        state,
                        pip,
                        shouldReport: false /* RegisterDirectoryContents has reported the contents */);

                    if (!enumerateResult.Succeeded)
                    {
                        return enumerateResult.Failure;
                    }
                }

                if (hasExcludedOutput)
                {
                    return PipOutputOrigin.NotMaterialized;
                }

                // NotMaterialized means the files were materialized by some other operation
                // Normalize this to DeployedFromCache because the outputs are materialized
                return state.OverallOutputOrigin == PipOutputOrigin.NotMaterialized
                    ? PipOutputOrigin.DeployedFromCache
                    : state.OverallOutputOrigin;
            }
        }

        /// <summary>
        /// Attempts to load the output content for the pip to ensure it is available for materialization
        /// </summary>
        public async Task<bool> TryLoadAvailableOutputContentAsync(
            PipInfo pipInfo,
            OperationContext operationContext,
            IReadOnlyList<(FileArtifact fileArtifact, ContentHash contentHash, AbsolutePath outputDirectoryRoot, bool isExecutable)> filesAndContentHashes,
            Action onFailure = null,
            Action<int, string, string, Failure> onContentUnavailable = null,
            bool materialize = false)
        {
            Logger.Log.ScheduleTryBringContentToLocalCache(operationContext, pipInfo.Description);
            Interlocked.Increment(ref m_stats.TryBringContentToLocalCache);

            if (!materialize)
            {
                var result = await TryLoadAvailableContentAsync(
                    operationContext,
                    pipInfo,
                    materializingOutputs: true,
                    // Only failures matter since we are checking a cache entry and not actually materializing
                    onlyLogUnavailableContent: true,
                    filesAndContentHashes: filesAndContentHashes.SelectList((tuple, index) => (tuple.fileArtifact, tuple.contentHash, index, tuple.outputDirectoryRoot, tuple.isExecutable)),
                    onFailure: failure => { onFailure?.Invoke(); },
                    onContentUnavailable: onContentUnavailable ?? ((index, expectedHash, hashOnDiskIfAvailableOrNull, failure) => { /* Do nothing. Callee already logs the failure */ }));

                return result;
            }
            else
            {
                using (var state = GetPipArtifactsState())
                {
                    state.VerifyMaterializationOnly = true;

                    foreach (var fileAndContentHash in filesAndContentHashes)
                    {
                        FileArtifact file = fileAndContentHash.fileArtifact;
                        ContentHash hash = fileAndContentHash.contentHash;
                        state.PipInfo = pipInfo;
                        state.AddMaterializationFile(
                            fileToMaterialize: file,
                            allowReadOnly: true,
                            materializationInfo: FileMaterializationInfo.CreateWithUnknownLength(hash, fileAndContentHash.isExecutable),
                            materializationCompletion: TaskSourceSlim.Create<PipOutputOrigin>());
                    }

                    var placeResult = await PlaceFilesAsync(operationContext, pipInfo, state);
                    return placeResult == PlaceFile.Success;
                }
            }
        }

        /// <summary>
        /// Reports the contents of an output directory
        /// </summary>
        public void ReportDynamicDirectoryContents(DirectoryArtifact directoryArtifact, IEnumerable<FileArtifactWithAttributes> contents, PipOutputOrigin outputOrigin)
        {
            using (var artifactsWrapper = Pools.FileArtifactListPool.GetInstance())
            {
                var artifacts = artifactsWrapper.Instance;

                foreach (FileArtifactWithAttributes faa in contents)
                {
                    var fileArtifact = faa.ToFileArtifact();
                    artifacts.Add(fileArtifact);

                    if (faa.IsUndeclaredFileRewrite)
                    {
                        m_allowedFileRewriteOutputs.Add(faa.Path);
                    }

                    if (faa.FileExistence == FileExistence.Required)
                    {
                        m_host.ReportDynamicOutputFile(fileArtifact);
                    }
                }

                var result = m_sealContents.GetOrAdd(directoryArtifact, artifacts, (key, contents2) =>
                    SortedReadOnlyArray<FileArtifact, OrdinalFileArtifactComparer>.CloneAndSort(contents2, OrdinalFileArtifactComparer.Instance));

                m_host?.ExecutionLog?.DynamicDirectoryContentsDecided(new DynamicDirectoryContentsDecidedEventData()
                {
                    Directory = directoryArtifact.Path,
                    Contents = result.Item.Value.BaseArray,
                    OutputOrigin = outputOrigin
                });
            }

            RegisterDirectoryContents(directoryArtifact);

            if (outputOrigin != PipOutputOrigin.NotMaterialized)
            {
                // Mark directory as materialized and ensure no deletions for the directory
                m_dynamicDirectoryDeletionTasks.TryAdd(directoryArtifact, BoolTask.True);
                MarkDirectoryMaterialization(directoryArtifact);
            }
        }

        /// <summary>
        /// Enumerates and tracks dynamic output directory.
        /// </summary>
        public Possible<Unit> EnumerateAndTrackOutputDirectory(
            DirectoryArtifact directoryArtifact,
            OutputDirectoryEnumerationData enumerationData,
            Action<FileArtifact> handleFile,
            Action<AbsolutePath> handleDirectory)
        {
            Contract.Requires(directoryArtifact.IsValid);
            Contract.Requires(enumerationData != null);

            var pathTable = Context.PathTable;
            var directoryPath = directoryArtifact.Path;
            var queue = new Queue<(AbsolutePath path, bool shouldTrack)>();
            queue.Enqueue((directoryPath, true));

            while (queue.Count > 0)
            {
                (AbsolutePath currentDirectoryPath, bool shouldTrack) = queue.Dequeue();

                if (shouldTrack)
                {
                    var result = m_host.LocalDiskContentStore.TryEnumerateDirectoryAndTrackMembership(
                        currentDirectoryPath,
                        handleEntry: (entry, attributes) =>
                        {
                            var path = currentDirectoryPath.Combine(pathTable, entry);

                            if (!FileUtilities.IsDirectoryNoFollow(attributes))
                            {
                                handleFile?.Invoke(FileArtifact.CreateOutputFile(path));
                            }
                            else
                            {
                                handleDirectory?.Invoke(path);
                            }
                        },
                        shouldIncludeEntry: (entry, attributes) =>
                        {
                            var path = currentDirectoryPath.Combine(pathTable, entry);
                            bool shouldIncludeEntry = !enumerationData.UntrackedPaths.Contains(path) && !enumerationData.UntrackedScopes.Contains(path);

                            // Treat directory reparse points as files: recursing on directory reparse points can lead to infinite loops
                            if (FileUtilities.IsDirectoryNoFollow(attributes) && !enumerationData.OutputDirectoryExclusions.Contains(path))
                            {
                                queue.Enqueue((path, shouldIncludeEntry));
                            }

                            return shouldIncludeEntry;
                        },
                        supersedeWithLastEntryUsn: true);

                    if (!result.Succeeded)
                    {
                        return result.Failure;
                    }
                }
                else
                {
                    FileUtilities.EnumerateDirectoryEntries(
                        currentDirectoryPath.ToString(pathTable),
                        handleEntry: (entry, attributes) =>
                        {
                            var path = currentDirectoryPath.Combine(pathTable, entry);

                            // Treat directory reparse points as files: recursing on directory reparse points can lead to infinite loops
                            if (FileUtilities.IsDirectoryNoFollow(attributes))
                            {
                                // Once the directory is untracked, its descendent directories will always be untracked.
                                queue.Enqueue((path, false));
                            }
                            else
                            {
                                if (enumerationData.OutputFilePaths.Contains(path))
                                {
                                    handleFile?.Invoke(FileArtifact.CreateOutputFile(path));
                                }
                            }
                        });
                }
            }

            return Unit.Void;
        }

        /// <summary>
        /// Lists the contents of a sealed directory (static or dynamic).
        /// </summary>
        public SortedReadOnlyArray<FileArtifact, OrdinalFileArtifactComparer> ListSealedDirectoryContents(DirectoryArtifact directoryArtifact)
        {
            SortedReadOnlyArray<FileArtifact, OrdinalFileArtifactComparer> contents;

            var sealDirectoryKind = m_host.GetSealDirectoryKind(directoryArtifact);
            if (sealDirectoryKind.IsDynamicKind())
            {
                // If sealContents does not have the dynamic directory, then the dynamic directory has no content and it is produced by another worker.
                return m_sealContents.TryGetValue(directoryArtifact, out contents) ? contents : s_emptySealContents;
            }

            if (!m_sealContents.TryGetValue(directoryArtifact, out contents))
            {
                // Load and cache contents from host
                contents = m_host.ListSealDirectoryContents(directoryArtifact);
                m_sealContents.TryAdd(directoryArtifact, contents);
            }

            return contents;
        }

        /// <summary>
        /// Gets the pip inputs including all direct file dependencies and output file seal directory file dependencies.
        /// </summary>
        public void CollectPipInputsToMaterialize(
            PipTable pipTable,
            Pip pip,
            HashSet<FileArtifact> files,
            MultiValueDictionary<FileArtifact, DirectoryArtifact> dynamicInputs = null,
            Func<FileOrDirectoryArtifact, bool> filter = null,
            Func<PipId, bool> serviceFilter = null,
            bool excludeSourceFiles = false)
        {
            CollectPipFilesToMaterialize(
                isMaterializingInputs: true,
                pipTable: pipTable,
                pip: pip,
                files: files,
                dynamicFileMap: dynamicInputs,
                shouldInclude: filter,
                shouldIncludeServiceFiles: serviceFilter,
                excludeSourceFiles: excludeSourceFiles);
        }

        /// <summary>
        /// Gets the pip outputs
        /// </summary>
        public void CollectPipOutputsToMaterialize(
            PipTable pipTable,
            Pip pip,
            HashSet<FileArtifact> files,
            MultiValueDictionary<FileArtifact, DirectoryArtifact> dynamicOutputs = null,
            Func<FileOrDirectoryArtifact, bool> shouldInclude = null)
        {
            CollectPipFilesToMaterialize(
                isMaterializingInputs: false,
                pipTable: pipTable,
                pip: pip,
                files: files,
                dynamicFileMap: dynamicOutputs,
                shouldInclude: shouldInclude);
        }

        /// <summary>
        /// Gets the pip inputs or outputs
        /// </summary>
        public void CollectPipFilesToMaterialize(
            bool isMaterializingInputs,
            PipTable pipTable,
            Pip pip,
            HashSet<FileArtifact> files = null,
            MultiValueDictionary<FileArtifact, DirectoryArtifact> dynamicFileMap = null,
            Func<FileOrDirectoryArtifact, bool> shouldInclude = null,
            Func<PipId, bool> shouldIncludeServiceFiles = null,
            bool excludeSourceFiles = false)
        {
            // Always include if no filter specified
            shouldInclude ??= (a => true);
            shouldIncludeServiceFiles ??= (a => true);

            using (PipArtifactsState state = GetPipArtifactsState())
            {
                if (isMaterializingInputs)
                {
                    // Get inputs
                    PopulateDependencies(pip, state.PipArtifacts, includeLazyInputs: true, excludeSourceFiles: excludeSourceFiles);
                }
                else
                {
                    PopulateOutputs(pip, state.PipArtifacts);
                }

                foreach (var artifact in state.PipArtifacts)
                {
                    if (!shouldInclude(artifact))
                    {
                        continue;
                    }

                    if (artifact.IsDirectory)
                    {
                        DirectoryArtifact directory = artifact.DirectoryArtifact;
                        SealDirectoryKind sealDirectoryKind = m_host.GetSealDirectoryKind(directory);

                        foreach (var file in ListSealedDirectoryContents(directory))
                        {
                            if (sealDirectoryKind.IsDynamicKind())
                            {
                                dynamicFileMap?.Add(file, directory);
                            }

                            if (file.IsOutputFile)
                            {
                                if (!shouldInclude(file))
                                {
                                    continue;
                                }

                                files?.Add(file);
                            }
                        }
                    }
                    else
                    {
                        files?.Add(artifact.FileArtifact);
                    }
                }
            }

            if (isMaterializingInputs)
            {
                // For the IPC pips, we need to collect the inputs of their service dependencies as well.
                if (pip.PipType == PipType.Ipc)
                {
                    var ipc = (IpcPip)pip;
                    foreach (var servicePipId in ipc.ServicePipDependencies)
                    {
                        if (!shouldIncludeServiceFiles(servicePipId))
                        {
                            continue;
                        }

                        var servicePip = pipTable.HydratePip(servicePipId, PipQueryContext.CollectPipInputsToMaterializeForIPC);
                        CollectPipInputsToMaterialize(pipTable, servicePip, files, dynamicFileMap, filter: shouldInclude, excludeSourceFiles: excludeSourceFiles);
                    }
                }
            }
        }

        /// <summary>
        /// Gets the updated semantic path information for the given path with data from the file content manager
        /// </summary>
        internal SemanticPathInfo GetUpdatedSemanticPathInfo(in SemanticPathInfo mountInfo)
        {
            return mountInfo;
        }

        /// <summary>
        /// Gets whether the given directory has potential outputs of the build
        /// </summary>
        public bool HasPotentialBuildOutputs(AbsolutePath directoryPath, in SemanticPathInfo mountInfo, bool isReadOnlyDirectory)
        {
            return (mountInfo.IsWritable && !isReadOnlyDirectory);
        }

        /// <summary>
        /// <see cref="IQueryableFileContentManager.TryQueryUndeclaredInputContentAsync(AbsolutePath, string)"/>
        /// </summary>
        public async Task<FileContentInfo?> TryQueryUndeclaredInputContentAsync(AbsolutePath path, string consumerDescription = null)
        {
            var result = await TryQuerySealedOrUndeclaredInputContentInternalAsync(path, consumerDescription, allowUndeclaredSourceReads: true);
            if (result.isUndeclaredInput)
            {
                return result.fileMaterialization?.FileContentInfo;
            }

            return null;
        }

        /// <summary>
        /// For a given path (which must be one of the pip's input artifacts; not an output), returns a content hash if:
        /// - that path has been 'sealed' (i.e., is under a sealed directory) . Since a sealed directory never changes, the path is un-versioned
        /// (unlike a <see cref="FileArtifact" />)
        /// - that path is not under any sealed container (source or full/partial seal directory), but undeclared source reads are allowed. In that
        /// case the path is also unversioned because immutability is also guaranteed by dynamic enforcements.
        /// This method always succeeds or fails synchronously.
        /// </summary>
        public async Task<FileContentInfo?> TryQuerySealedOrUndeclaredInputContentAsync(AbsolutePath path, string consumerDescription, bool allowUndeclaredSourceReads)
        {
            return (await TryQuerySealedOrUndeclaredInputContentInternalAsync(path, consumerDescription, allowUndeclaredSourceReads)).fileMaterialization?.FileContentInfo;
        }

        /// <summary>
        /// <see cref="TryQueryUndeclaredInputContentAsync(AbsolutePath, string)"/>
        /// </summary>
        public async Task<FileMaterializationInfo?> TryQuerySealedOrUndeclaredMaterializationInfoAsync(AbsolutePath path, string consumerDescription, bool allowUndeclaredSourceReads)
        {
            return (await TryQuerySealedOrUndeclaredInputContentInternalAsync(path, consumerDescription, allowUndeclaredSourceReads)).fileMaterialization;
        }

        private async Task<(FileMaterializationInfo? fileMaterialization, bool isUndeclaredInput)> TryQuerySealedOrUndeclaredInputContentInternalAsync(AbsolutePath path, string consumerDescription, bool allowUndeclaredSourceReads)
        {
            FileOrDirectoryArtifact declaredArtifact;

            var isDynamicallyObservedSource = false;
            bool isUndeclaredInput;
            if (!m_sealedFiles.TryGetValue(path, out FileArtifact sealedFile))
            {
                var sourceSealDirectory = TryGetSealSourceAncestor(path);
                if (!sourceSealDirectory.IsValid)
                {
                    // If there is no sealed artifact that contains the read, then we check if undeclared source reads are allowed
                    if (!allowUndeclaredSourceReads)
                    {
                        // No source seal directory found
                        return (null, false);
                    }
                    else
                    {
                        // If undeclared source reads is enabled but the path does not exist, then we can just shortcut
                        // the query here. This matches the declared case when no static artifact is found that contains
                        // the path
                        var maybeResult = FileUtilities.TryProbePathExistence(path.ToString(Context.PathTable), followSymlink: false);
                        if (!maybeResult.Succeeded || maybeResult.Result == PathExistence.Nonexistent)
                        {
                            return (null, false);
                        }
                    }

                    isUndeclaredInput = true;
                    // We set the declared artifact as the path itself, there is no 'declared container' for this case.
                    declaredArtifact = FileArtifact.CreateSourceFile(path);
                }
                else
                {
                    isUndeclaredInput = false;
                    // The source seal directory is the artifact which is actually declared
                    // file artifact is created on the fly and never declared
                    declaredArtifact = sourceSealDirectory;
                }

                // The path is not under a full/partial seal directory. So it is either under a source seal, or it is an allowed undeclared read. In both
                // cases it is a dynamically observed source
                isDynamicallyObservedSource = true;

                // This path is in a sealed source directory or undeclared source reads are allowed
                // so create a source file and query the content of it
                sealedFile = FileArtifact.CreateSourceFile(path);
            }
            else
            {
                isUndeclaredInput = true;
                declaredArtifact = sealedFile;
            }

            FileMaterializationInfo? materializationInfo;
            using (var operationContext = OperationTracker.StartOperation(OperationKind.PassThrough, m_host.LoggingContext))
            using (operationContext.StartOperation(PipExecutorCounter.FileContentManagerTryQuerySealedInputContentDuration, sealedFile))
            {
                materializationInfo = await TryQueryContentAsync(sealedFile, operationContext, declaredArtifact, allowUndeclaredSourceReads, consumerDescription);
            }

            if (materializationInfo == null)
            {
                if (isDynamicallyObservedSource && m_contentQueriedDirectoryPaths.Contains(path))
                {
                    // Querying a directory under a source seal directory is allowed, return null which
                    // allows the ObservedInputProcessor to proceed
                    // This is different from the case of other seal directory types because the paths registered
                    // in those directories are required to be files or missing
                    // Querying a directory when undeclared source reads are allowed is also allowed
                    return (null, false);
                }

                // This causes the ObservedInputProcessor to abort the strong fingerprint computation
                // by indicating a failure in hashing a path
                // TODO: In theory, ObservedInputProcessor should not treat
                return (FileMaterializationInfo.CreateWithUnknownLength(WellKnownContentHashes.UntrackedFile), isUndeclaredInput);
            }

            return (materializationInfo.Value, isUndeclaredInput);
        }

        /// <summary>
        /// For a given file artifact (which must be one of the pip's input artifacts; not an output), returns a content hash.
        /// This method always succeeds synchronously. The specified artifact must be a statically known (not dynamic) dependency.
        /// </summary>
        public FileMaterializationInfo GetInputContent(FileArtifact fileArtifact)
        {
            FileMaterializationInfo materializationInfo;
            bool found = TryGetInputContent(fileArtifact, out materializationInfo);

            if (!found)
            {
                Contract.Assume(
                    false,
                    "Attempted to query the content hash for an artifact which has not passed through SetFileArtifactContentHash: "
                    + fileArtifact.Path.ToString(Context.PathTable));
            }

            return materializationInfo;
        }

        /// <summary>
        /// Attempts to get the materialization info for the input artifact
        /// </summary>
        public bool TryGetInputContent(FileArtifact file, out FileMaterializationInfo info)
        {
            return m_fileArtifactContentHashes.TryGetValue(file, out info);
        }

        /// <summary>
        /// Whether there is a directory artifact representing an output directory (shared or exclusive opaque) that contains the given path
        /// </summary>
        public bool TryGetContainingOutputDirectory(AbsolutePath path, out DirectoryArtifact containingOutputDirectory)
        {
            containingOutputDirectory = DirectoryArtifact.Invalid;
            return (m_sealedFiles.TryGetValue(path, out FileArtifact sealedFile) && m_dynamicOutputFileDirectories.TryGetValue(sealedFile, out containingOutputDirectory));
        }

        /// <summary>
        /// Attempts to materialize the given file.
        /// </summary>
        /// <remarks>
        /// Note: this method is only used by the API Server.
        /// </remarks>
        public async Task<ArtifactMaterializationResult> TryMaterializeFileAsync(FileArtifact outputFile)
        {
            var producer = GetDeclaredProducer(outputFile);
            using (var operationContext = OperationTracker.StartOperation(PipExecutorCounter.FileContentManagerTryMaterializeFileDuration, m_host.LoggingContext))
            {
                return await TryMaterializeFilesAsync(producer, operationContext, new[] { outputFile }, materializatingOutputs: true, isDeclaredProducer: true, isApiServerRequest: true);
            }
        }

        /// <summary>
        /// Attempts to materialize a sealed file given its path.
        /// </summary>
        /// <remarks>
        /// NOTE: this only works for sealed files (i.e., files where path->artifact mapping is known to FileContentManager).
        /// If FileArtifact value cannot be inferred, the call will fail (even if the path already exists).
        /// This method is only used by the API Server.
        /// </remarks>
        public async Task<ArtifactMaterializationResult> TryMaterializeSealedFileAsync(AbsolutePath outputFilePath)
        {
            if (!m_sealedFiles.TryGetValue(outputFilePath, out var fileArtifact))
            {
                // cannot infer FileArtifact value
                m_pathsWithoutFileArtifact.Add(outputFilePath);
                return ArtifactMaterializationResult.None;
            }

            return await TryMaterializeFileAsync(fileArtifact);
        }

        /// <summary>
        /// Attempts to materialize the specified files
        /// </summary>
        public async Task<ArtifactMaterializationResult> TryMaterializeFilesAsync(
            Pip requestingPip,
            OperationContext operationContext,
            IEnumerable<FileArtifact> filesToMaterialize,
            bool materializatingOutputs,
            bool isDeclaredProducer,
            bool isApiServerRequest)
        {
            Contract.Requires(requestingPip != null);

            var pipInfo = new PipInfo(requestingPip, Context);

            using (PipArtifactsState state = GetPipArtifactsState())
            {
                foreach (var item in filesToMaterialize)
                {
                    state.PipArtifacts.Add(item);
                }

                return await TryMaterializeArtifactsCore(
                                    pipInfo,
                                    operationContext,
                                    state,
                                    materializatingOutputs: materializatingOutputs,
                                    isDeclaredProducer: isDeclaredProducer,
                                    isApiServerRequest: isApiServerRequest);
            }
        }

        /// <summary>
        /// Gets the materialization origin of a file.
        /// </summary>
        public PipOutputOrigin GetPipOutputOrigin(FileArtifact file)
        {
            Contract.Requires(file.IsValid);

            return m_materializationTasks.TryGetValue(file, out Task<PipOutputOrigin> outputOrigin) ? outputOrigin.Result : PipOutputOrigin.NotMaterialized;
        }

        private PipArtifactsState GetPipArtifactsState()
        {
            PipArtifactsState state;
            if (!m_statePool.TryDequeue(out state))
            {
                return new PipArtifactsState(this);
            }

            return state;
        }

        private void PopulateDependencies(Pip pip, HashSet<FileOrDirectoryArtifact> dependencies, bool includeLazyInputs = false, bool onlySourceFiles = false, bool registerDirectories = false, bool excludeSourceFiles = false)
        {
            Contract.Requires(!onlySourceFiles || !excludeSourceFiles);

            if (pip.PipType == PipType.SealDirectory)
            {
                // Seal directory contents are handled by consumer of directory
                return;
            }

            Func<FileOrDirectoryArtifact, bool> action = (input) =>
            {
                if (registerDirectories && input.IsDirectory)
                {
                    // Register the seal file contents of the directory dependencies
                    RegisterDirectoryContents(input.DirectoryArtifact);
                }

                if (input.IsFile && input.FileArtifact.IsSourceFile)
                {
                    if (!excludeSourceFiles)
                    {
                        // Add source files only if excludeSourceFiles is false
                        dependencies.Add(input);
                    }
                }
                else if (!onlySourceFiles)
                {
                    // Add output files or directories only if onlySourceFiles is false.
                    dependencies.Add(input);
                }

                return true;
            };

            Func<FileOrDirectoryArtifact, bool> lazyInputAction = action;
            if (!includeLazyInputs)
            {
                lazyInputAction = lazyInput =>
                {
                    // Remove lazy inputs
                    dependencies.Remove(lazyInput);
                    return true;
                };
            }

            PipArtifacts.ForEachInput(pip, action, includeLazyInputs: true, overrideLazyInputAction: lazyInputAction);
        }

        private static void PopulateOutputs(Pip pip, HashSet<FileOrDirectoryArtifact> outputs, Func<FileOrDirectoryArtifact, bool> exclude = null)
        {
            PipArtifacts.ForEachOutput(pip, output =>
            {
                if (exclude?.Invoke(output) == true)
                {
                    return true;
                }

                outputs.Add(output);
                return true;
            }, includeUncacheable: false);
        }

        // TODO: Consider calling this from TryHashDependencies. That would allow us to remove logic which requires
        // that direct seal directory dependencies are scheduled
        private Possible<Unit> EnumerateAndTrackOutputDirectories(PipArtifactsState state, Pip artifactsProducer, bool shouldReport)
        {
            using (var outputDirectoryEnumerationDataWrapper = m_outputEnumerationDataPool.GetInstance())
            {
                OutputDirectoryEnumerationData outputDirectoryEnumerationData = null;
                Process process = artifactsProducer as Process;

                if (process != null)
                {
                    outputDirectoryEnumerationData = outputDirectoryEnumerationDataWrapper.Instance;
                    outputDirectoryEnumerationData.Process = process;
                }

                foreach (var artifact in state.PipArtifacts)
                {
                    if (artifact.IsDirectory)
                    {
                        var directory = artifact.DirectoryArtifact;
                        SealDirectoryKind sealDirectoryKind = m_host.GetSealDirectoryKind(directory);

                        if (sealDirectoryKind == SealDirectoryKind.Opaque)
                        {
                            if (m_sealContents.ContainsKey(directory))
                            {
                                // Only enumerate and report if the directory has not already been reported
                                continue;
                            }

                            if (Context.CancellationToken.IsCancellationRequested)
                            {
                                return Context.CancellationToken.CreateFailure();
                            }

                            using var outputDirectoryEnumerationDataInnerWrapper = m_outputEnumerationDataPool.GetInstance();

                            OutputDirectoryEnumerationData outputDirectoryEnumerationDataInner = outputDirectoryEnumerationData;

                            if (outputDirectoryEnumerationDataInner == null)
                            {
                                process = m_host.GetProducer(directory) as Process;
                                Contract.Assert(process != null, "Opaque directory artifact must be produced by a process");

                                outputDirectoryEnumerationDataInner = outputDirectoryEnumerationDataInnerWrapper.Instance;
                                outputDirectoryEnumerationDataInner.Process = process;
                            }

                            var result = EnumerateAndTrackOutputDirectory(directory, outputDirectoryEnumerationDataInner, shouldReport);
                            if (!result.Succeeded)
                            {
                                return result;
                            }
                        }
                    }
                }
            }

            return Unit.Void;
        }

        private Possible<Unit> EnumerateAndTrackOutputDirectory(
            DirectoryArtifact directoryArtifact,
            OutputDirectoryEnumerationData outputDirectoryEnumerationData,
            bool shouldReport)
        {
            Contract.Requires(directoryArtifact.IsValid);
            Contract.Requires(outputDirectoryEnumerationData != null);

            using (var poolFileList = Pools.GetFileArtifactList())
            {
                var fileList = poolFileList.Instance;

                var result = EnumerateAndTrackOutputDirectory(
                    directoryArtifact,
                    outputDirectoryEnumerationData,
                    handleFile: shouldReport ? (file => fileList.Add(file)) : (Action<FileArtifact>)null,
                    handleDirectory: null);

                if (!result.Succeeded)
                {
                    return result.Failure;
                }

                if (shouldReport)
                {
                    // When enumerating the local disk in order to get an output directory contents, the result is always required artifacts with no rewritten sources
                    // The source of rewritten sources are always reported from the pip executor
                    ReportDynamicDirectoryContents(
                        directoryArtifact,
                        fileList.Select(fa => FileArtifactWithAttributes.Create(fa, FileExistence.Required, isUndeclaredFileRewrite: false)),
                        PipOutputOrigin.UpToDate);
                }
            }

            return Unit.Void;
        }

        private void RegisterDirectoryContents(HashSet<FileOrDirectoryArtifact> artifacts)
        {
            foreach (var artifact in artifacts)
            {
                if (artifact.IsDirectory)
                {
                    var directory = artifact.DirectoryArtifact;
                    RegisterDirectoryContents(directory);
                }
            }
        }

        private void RegisterDirectoryContents(DirectoryArtifact directory)
        {
            if (!m_registeredSealDirectories.Contains(directory))
            {
                SealDirectoryKind sealDirectoryKind = m_host.GetSealDirectoryKind(directory);

                foreach (var file in ListSealedDirectoryContents(directory))
                {
                    var addedFile = m_sealedFiles.GetOrAdd(file.Path, file).Item.Value;
                    Contract.Assert(addedFile == file, $"Attempted to seal path twice with different rewrite counts ({addedFile.RewriteCount} != {file.RewriteCount}): {addedFile.Path.ToString(Context.PathTable)}");

                    if (sealDirectoryKind.IsDynamicKind())
                    {
                        // keep only the original {file -> directory} mapping
                        m_dynamicOutputFileDirectories.TryAdd(file, directory);
                    }
                }

                if (m_host.ShouldScrubFullSealDirectory(directory))
                {
                    using (var pooledList = Pools.GetStringList())
                    {
                        var unsealedFiles = pooledList.Instance;
                        FileUtilities.DeleteDirectoryContents(
                            path: directory.Path.ToString(Context.PathTable),
                            deleteRootDirectory: false,
                            shouldDelete: (filePath, _) =>
                            {
                                if (!m_sealedFiles.ContainsKey(AbsolutePath.Create(Context.PathTable, filePath)))
                                {
                                    unsealedFiles.Add(filePath);
                                    return true;
                                }
                                return false;
                            },
                            tempDirectoryCleaner: m_tempDirectoryCleaner);

                        Logger.Log.DeleteFullySealDirectoryUnsealedContents(
                            context: m_host.LoggingContext,
                            directoryPath: directory.Path.ToString(Context.PathTable),
                            pipDescription: GetAssociatedPipDescription(directory),
                            string.Join(Environment.NewLine, unsealedFiles.Select(f => "\t" + f)));
                    }

                }
                else if (sealDirectoryKind.IsSourceSeal())
                {
                    m_sealedSourceDirectories.TryAdd(directory.Path, directory);
                }

                m_registeredSealDirectories.Add(directory);
            }
        }

        /// <summary>
        /// Attempts to get the producer pip id for the producer of the file. The file may be
        /// a file produced inside a dynamic directory so its 'declared' producer will be the
        /// producer of the dynamic directory
        /// </summary>
        private PipId TryGetDeclaredProducerId(FileArtifact file)
        {
            return m_host.TryGetProducerId(GetDeclaredArtifact(file));
        }

        /// <summary>
        /// Attempts to get the producer pip for the producer of the file. The file may be
        /// a file produced inside a dynamic directory so its 'declared' producer will be the
        /// producer of the dynamic directory
        /// </summary>
        public Pip GetDeclaredProducer(FileArtifact file)
        {
            var declaredFile = GetDeclaredArtifact(file);
            var declaredProducer = m_host.GetProducer(declaredFile);
            if (declaredProducer == null)
            {
                var filePath = file.Path.IsValid ? file.Path.ToString(Context.PathTable) : "Invalid";
                var declaredFilePath = declaredFile.Path.IsValid ? declaredFile.Path.ToString(Context.PathTable) : "Invalid";
                throw new BuildXLException($"No declared producer pip found. File: {file}, File.Path: {filePath}, DeclaredFile: {declaredFile}, DeclaredFile.Path: {declaredFilePath}");
            }
            return declaredProducer;
        }

        /// <summary>
        /// Gets the statically declared artifact corresponding to the file. In most cases, this is the file
        /// except for dynamic outputs or seal source files which are dynamically discovered
        /// </summary>
        private FileOrDirectoryArtifact GetDeclaredArtifact(FileArtifact file)
        {
            DirectoryArtifact declaredDirectory;
            if (m_sealedFiles.ContainsKey(file.Path))
            {
                if (file.IsOutputFile)
                {
                    if (m_dynamicOutputFileDirectories.TryGetValue(file, out declaredDirectory))
                    {
                        return declaredDirectory;
                    }
                }
            }
            else if (file.IsSourceFile)
            {
                declaredDirectory = TryGetSealSourceAncestor(file.Path);
                if (declaredDirectory.IsValid)
                {
                    return declaredDirectory;
                }
            }

            return file;
        }

        private async Task<Possible<Unit>> TryHashFileArtifactsAsync(
            PipArtifactsState state,
            OperationContext operationContext,
            bool allowUndeclaredSourceReads,
            string pipDescription = null)
        {
            foreach (var artifact in state.PipArtifacts)
            {
                if (!artifact.IsDirectory)
                {
                    // Directory artifact contents are not hashed since they will be hashed dynamically
                    // if the pip accesses them, so the file is the declared artifact
                    FileArtifact file = artifact.FileArtifact;
                    if (!m_fileArtifactContentHashes.TryGetValue(file, out _))
                    {
                        // Directory artifact contents are not hashed since they will be hashed dynamically
                        // if the pip accesses them, so the file is the declared artifact
                        state.HashTasks.Add(TryQueryContentAndLogHashFailureAsync(
                            file,
                            operationContext,
                            declaredArtifact: file,
                            allowUndeclaredSourceReads,
                            pipDescription));
                    }
                }
            }

            FileMaterializationInfo?[] artifactContentInfos = await Task.WhenAll(state.HashTasks);

            foreach (var artifactContentInfo in artifactContentInfos)
            {
                if (!artifactContentInfo.HasValue)
                {
                    return new Failure<string>("Could not retrieve input content for pip");
                }
            }

            return Unit.Void;
        }

        /// <nodoc/>
        public async Task<FileMaterializationInfo?> TryHashSourceFile(OperationContext operationContext, FileArtifact fileArtifact)
        {
            var artifactContentInfo = await TryQueryContentAsync(
                            fileArtifact,
                            operationContext,
                            fileArtifact,
                            allowUndeclaredSourceReads: true,
                            consumerDescription: string.Empty);

            if (!artifactContentInfo.HasValue)
            {
                Logger.Log.PipSourceDependencyCannotBeHashed(operationContext.LoggingContext, fileArtifact.Path.ToString(Context.PathTable), string.Empty, isSourceFile: true);
            }

            return artifactContentInfo;
        }

        private async Task<FileMaterializationInfo?> TryQueryContentAndLogHashFailureAsync(
            FileArtifact fileArtifact,
            OperationContext operationContext,
            FileOrDirectoryArtifact declaredArtifact,
            bool allowUndeclaredSourceReads,
            string consumerDescription)
        {
            var artifactContentInfo = await TryQueryContentAsync(
                            fileArtifact,
                            operationContext,
                            declaredArtifact,
                            allowUndeclaredSourceReads,
                            consumerDescription);

            if (!artifactContentInfo.HasValue)
            {
                Logger.Log.PipSourceDependencyCannotBeHashed(operationContext.LoggingContext, fileArtifact.Path.ToString(Context.PathTable), consumerDescription, fileArtifact.IsSourceFile);
            }
            return artifactContentInfo;
        }

        private async Task<FileMaterializationInfo?> TryQueryContentAsync(
            FileArtifact fileArtifact,
            OperationContext operationContext,
            FileOrDirectoryArtifact declaredArtifact,
            bool allowUndeclaredSourceReads,
            string consumerDescription = null,
            bool verifyingHash = false)
        {
            if (!verifyingHash)
            {
                // Just use the stored hash if available and we are not verifying the hash which must bypass the
                // use of the stored hash
                FileMaterializationInfo recordedfileContentInfo;
                if (m_fileArtifactContentHashes.TryGetValue(fileArtifact, out recordedfileContentInfo))
                {
                    return recordedfileContentInfo;
                }
            }

            Task<FileMaterializationInfo?> alreadyHashingTask;
            TaskSourceSlim<FileMaterializationInfo?> hashCompletion;
            if (!TryReserveCompletion(m_fileArtifactHashTasks, fileArtifact, out alreadyHashingTask, out hashCompletion))
            {
                var hash = await alreadyHashingTask;
                return hash;
            }

            // for output files, call GetAndRecordFileContentHashAsyncCore directly which
            // doesn't include checks for mount points used for source file hashing
            TrackedFileContentInfo? trackedFileContentInfo = fileArtifact.IsSourceFile
                ? await GetAndRecordSourceFileContentHashAsync(operationContext, fileArtifact, declaredArtifact, allowUndeclaredSourceReads, consumerDescription)
                : await GetAndRecordFileContentHashAsyncCore(operationContext, fileArtifact, declaredArtifact, consumerDescription);

            FileMaterializationInfo? fileContentInfo = trackedFileContentInfo?.FileMaterializationInfo;
            if (fileContentInfo.HasValue)
            {
                if (!verifyingHash)
                {
                    // Don't store the hash when performing verification
                    ReportContent(fileArtifact, fileContentInfo.Value, PipOutputOrigin.UpToDate);
                }

                // Remove task now that content info is stored in m_fileArtifactContentHashes
                m_fileArtifactHashTasks.TryRemove(fileArtifact, out alreadyHashingTask);
            }

            hashCompletion.SetResult(fileContentInfo);
            return fileContentInfo;
        }

        private async Task<ArtifactMaterializationResult> TryMaterializeArtifactsCore(
            PipInfo pipInfo,
            OperationContext operationContext,
            PipArtifactsState state,
            bool materializatingOutputs,
            bool isDeclaredProducer,
            bool isApiServerRequest)
        {
            // If materializing outputs, all files come from the same pip and therefore have the same
            // policy for whether they are readonly
            bool? allowReadOnly = materializatingOutputs ? !PipArtifacts.IsOutputMustRemainWritablePip(pipInfo.UnderlyingPip) : (bool?)null;

            state.PipInfo = pipInfo;
            state.MaterializingOutputs = materializatingOutputs;
            state.IsDeclaredProducer = isDeclaredProducer;
            state.IsApiServerRequest = isApiServerRequest;
            // We only enforce exclusion roots when we are materializing outputs of MaterializeOutputs execution step.
            // We materialize everything for input materialization and when we materialize artifacts for API Server.
            state.EnforceOutputMaterializationExclusionRootsForDirectoryArtifacts = materializatingOutputs && !isApiServerRequest;

            // We DO NOT virtualize materialized outputs under the assumption that those will be
            // required after the build is done. If files are already virtualized, we force hydration.
            state.Virtualize = !materializatingOutputs && IsVirtualizationEnabled();

            IEnumerable<AbsolutePath> readPaths = null;
            // The first encountered materialization failure. Even if there are more failures, we only return the first one.
            ArtifactMaterializationResult? firstFailure = null;

            if (state.Virtualize)
            {
                using (operationContext.StartOperation(PipExecutorCounter.FileContentManagerGetHydrateFilesDuration))
                {
                    var readPathsResult = await Host.GetReadPathsAsync(operationContext, state.PipInfo.UnderlyingPip);
                    if (readPathsResult.HasValue)
                    {
                        readPaths = readPathsResult.Value;
                    }
                    else if (!Configuration.Cache.VirtualizeUnknownPips)
                    {
                        // No historical info for pip and configuration specifies that
                        // it should not be virtualized in this case. Force all inputs to be hydrated.
                        state.Virtualize = false;
                    }
                }
            }

            // Get the files which need to be materialized
            // We reserve completion of directory deletions and file materialization so only a single deleter/materializer of a
            // directory/file. If the operation is reserved, code will perform the operation. Otherwise, it will await a task
            // signaling the completion of the operation.
            // PopulateArtifactsToMaterialize tries to reserve the completion for the files we are trying to materialize. If the 
            // completion is reserved, the current invocation of this method is the only place where that completion can be marked
            // as completed. Therefore it's crucial that we reach the end of the method and do not return early, oterwise other 
            // threads will be deadlocked waiting on those never finished completions.
            PopulateArtifactsToMaterialize(state, allowReadOnly);

            // When virtualization is enabled, check which input files to fully materialize
            if (IsVirtualizationEnabled())
            {
                using (operationContext.StartOperation(PipExecutorCounter.FileContentManagerGetHydrateFilesDuration))
                {
                    PopulateFilesToHydrate(operationContext, state, readPaths);
                }
            }

            using (operationContext.StartOperation(PipExecutorCounter.FileContentManagerDeleteDirectoriesDuration))
            {
                // Delete dynamic directory contents prior to placing files
                // NOTE: This should happen before any per-file materialization/deletion operations because
                // it skips deleting declared files in the materialized directory under the assumption that
                // a later step will materialize/delete the file as necessary
                if (!await PrepareDirectoriesAsync(state, operationContext))
                {
                    firstFailure ??= ArtifactMaterializationResult.PrepareDirectoriesFailed;
                }
            }

            if (IsDistributedWorker)
            {
                // Check that source files match (this may fail the materialization or leave the
                // source file to be materialized later if the materializeSourceFiles option is set (distributed worker))
                if (!await VerifySourceFileInputsAsync(operationContext, pipInfo, state))
                {
                    firstFailure ??= ArtifactMaterializationResult.VerifySourceFilesFailed;
                }
            }

            // Delete the absent files if any
            if (!await DeleteFilesRequiredAbsentAsync(state, operationContext))
            {
                firstFailure ??= ArtifactMaterializationResult.DeleteFilesRequiredAbsentFailed;
            }

            // Place Files:
            var possiblyPlaced = await PlaceFilesAsync(operationContext, pipInfo, state);
            if (possiblyPlaced != PlaceFile.Success)
            {
                firstFailure ??= (possiblyPlaced == PlaceFile.UserError
                    ? ArtifactMaterializationResult.PlaceFileFailedDueToDeletionFailure
                    : ArtifactMaterializationResult.PlaceFileFailed);
            }

            if (!firstFailure.HasValue)
            {
                // Mark directories as materialized so that the full set of files in the directory will
                // not need to be checked for completion on subsequent materialization calls
                MarkDirectoryMaterializations(state);
            }

            Contract.Assert(state.MaterializationFiles.All(file => file.MaterializationCompletion.Task.IsCompleted), "All stared materializations must have finished.");

            return firstFailure ?? ArtifactMaterializationResult.Succeeded;
        }

        private void PopulateFilesToHydrate(OperationContext operationContext, PipArtifactsState state, IEnumerable<AbsolutePath> readPaths)
        {
            IEnumerable<AbsolutePath> pathsToHydrate = Enumerable.Empty<AbsolutePath>();

            if (state.Virtualize)
            {
                // Hydrate explicit file dependencies
                pathsToHydrate = state.PipArtifacts.Where(a => a.IsFile).Select(f => f.Path);

                if (readPaths != null)
                {
                    // Hydrate read files from directory dependencies
                    pathsToHydrate = pathsToHydrate.Concat(readPaths);
                }
            }
            else
            {
                // Hydrate all outputs when materializing outputs
                pathsToHydrate = state.MaterializationFiles.Select(f => f.Artifact.Path);
            }

            foreach (var readPath in pathsToHydrate)
            {
                if (m_fileVirtualizationStates.TryGetValue(readPath, out var fileState)
                    && (fileState == VirtualizationState.PendingVirtual || fileState == VirtualizationState.Virtual))
                {
                    if (m_fileVirtualizationStates.TryUpdate(readPath, comparisonValue: VirtualizationState.PendingVirtual, newValue: VirtualizationState.PendingFullMaterialization))
                    {
                        // Changed state to full materialization before file placement marked file as Virtual.
                        // The materialization which is responsible for materializing the file will use full materialization
                    }
                    else if (m_fileVirtualizationStates.TryUpdate(readPath, comparisonValue: VirtualizationState.Virtual, newValue: VirtualizationState.PendingHydration))
                    {
                        // This file is already virtualized so this materialization will hydrate the file
                        state.HydrationFiles.Add(readPath);
                    }
                }
            }
        }

        private void PopulateArtifactsToMaterialize(PipArtifactsState state, bool? allowReadOnlyOverride)
        {
            foreach (var artifact in state.PipArtifacts)
            {
                if (artifact.IsDirectory)
                {
                    DirectoryArtifact directory = artifact.DirectoryArtifact;
                    if (m_materializedDirectories.Contains(directory) || (state.Virtualize && m_virtualizedDirectories.Contains(directory)))
                    {
                        // Directory is already fully materialized, no need to materialize its contents
                        continue;
                    }

                    SealDirectoryKind sealDirectoryKind = m_host.GetSealDirectoryKind(directory);

                    bool? directoryAllowReadOnlyOverride = allowReadOnlyOverride;

                    if (sealDirectoryKind == SealDirectoryKind.Opaque)
                    {
                        // Dynamic directories must be deleted before materializing files
                        // We don't want this to happen for shared dynamic ones
                        AddDirectoryDeletion(state, artifact.DirectoryArtifact, m_host.IsPreservedOutputArtifact(artifact));

                        // For dynamic directories we need to specify the value of
                        // allow read only since the host will not know about the
                        // dynamically produced file
                        if (directoryAllowReadOnlyOverride == null && m_host.TryGetProducerId(directory).IsValid)
                        {
                            var producer = m_host.GetProducer(directory);
                            directoryAllowReadOnlyOverride = !PipArtifacts.IsOutputMustRemainWritablePip(producer);
                        }
                    }
                    else if (sealDirectoryKind == SealDirectoryKind.SourceTopDirectoryOnly)
                    {
                        continue;
                    }
                    else if (sealDirectoryKind == SealDirectoryKind.SourceAllDirectories)
                    {
                        continue;
                    }

                    // Full, partial, and dynamic output must have contents materialized
                    foreach (var file in ListSealedDirectoryContents(directory))
                    {
                        // This is not needed for shared dynamic since we are not deleting them to begin with
                        if (sealDirectoryKind == SealDirectoryKind.Opaque)
                        {
                            // Track reported files inside dynamic directories so they are not deleted
                            // during the directory deletion step (they will be replaced by the materialization
                            // step or may have already been replaced if the file was explicitly materialized)
                            state.MaterializedDirectoryContents.Add(file, false);

                            // Add all directory paths between the file and opaque root directory.
                            // We do that to prevent those directories from being deleted by PrepareDirectoriesAsync even when those dirs are empty. 
                            var parentPath = file.Path.GetParent(Context.PathTable);
                            while (parentPath.IsValid && parentPath != directory.Path && state.MaterializedDirectoryContents.TryAdd(parentPath, true))
                            {
                                parentPath = parentPath.GetParent(Context.PathTable);
                            }
                        }

                        // if required, ensure that we are not materializing files that are under any exclusion root
                        if (state.EnforceOutputMaterializationExclusionRootsForDirectoryArtifacts
                            && m_outputMaterializationExclusionMap.TryGetFirstMapping(file.Path.Value, out var _))
                        {
                            // the file is under an exclusion root
                            continue;
                        }

                        AddFileMaterialization(state, file, directoryAllowReadOnlyOverride, sealDirMember: true);
                    }
                }
                else
                {
                    AddFileMaterialization(state, artifact.FileArtifact, allowReadOnlyOverride);
                }
            }
        }

        private void MarkDirectoryMaterializations(PipArtifactsState state)
        {
            // Mark directories as materialized only if the exclusion roots were NOT enforced, 
            // i.e., all the content was materialized.
            if (!state.EnforceOutputMaterializationExclusionRootsForDirectoryArtifacts)
            {
                foreach (var artifact in state.PipArtifacts)
                {
                    if (artifact.IsDirectory)
                    {
                        MarkDirectoryMaterialization(artifact.DirectoryArtifact, state.Virtualize);
                    }
                }
            }
        }

        /// <summary>
        /// Checks if a <see cref="DirectoryArtifact"/> is materialized.
        /// </summary>
        public bool IsMaterialized(DirectoryArtifact directoryArtifact)
        {
            return m_materializedDirectories.Contains(directoryArtifact);
        }

        private void MarkDirectoryMaterialization(DirectoryArtifact directoryArtifact, bool isVirtual = false)
        {
            if (isVirtual)
            {
                m_virtualizedDirectories.Add(directoryArtifact);
            }
            else
            {
                m_materializedDirectories.Add(directoryArtifact);
            }
            m_host.ReportMaterializedArtifact(directoryArtifact);
        }

        private void AddDirectoryDeletion(PipArtifactsState state, DirectoryArtifact directoryArtifact, bool isPreservedOutputsDirectory)
        {
            var sealDirectoryKind = m_host.GetSealDirectoryKind(directoryArtifact);
            if (sealDirectoryKind != SealDirectoryKind.Opaque)
            {
                // Only dynamic output directories should be deleted
                return;
            }

            TaskSourceSlim<bool> deletionCompletion;
            Task<bool> alreadyDeletingTask;
            if (!TryReserveCompletion(m_dynamicDirectoryDeletionTasks, directoryArtifact, out alreadyDeletingTask, out deletionCompletion))
            {
                if (alreadyDeletingTask.Status != TaskStatus.RanToCompletion || !alreadyDeletingTask.Result)
                {
                    state.PendingDirectoryDeletions.Add(alreadyDeletingTask);
                }
            }
            else
            {
                state.DirectoryDeletionCompletions.Add((directoryArtifact, isPreservedOutputsDirectory, deletionCompletion));
            }
        }

        /// <summary>
        /// Adds a file to the list of files to be materialized.
        /// </summary>
        /// <param name="state">the state object containing the list of file materializations.</param>
        /// <param name="file">the file to materialize.</param>
        /// <param name="allowReadOnlyOverride">specifies whether the file is allowed to be read-only. If not specified, the host is queried.</param>
        /// <param name="dependentFileIndex">the index of a file (in the list of materialized files) which requires the materialization of this file as
        /// a prerequisite (if any). This is used when restoring content into cache for a host materialized file (i.e. write file output).</param>
        /// <param name="sealDirMember">Whether a file is a member of a sealed directory</param>
        private void AddFileMaterialization(
            PipArtifactsState state,
            FileArtifact file,
            bool? allowReadOnlyOverride,
            int? dependentFileIndex = null,
            bool sealDirMember = false)
        {
            var behavior = getBehavior();
            if (behavior == AddFileMaterializationBehavior.Skip)
            {
                return;
            }
            else if (behavior == AddFileMaterializationBehavior.Materialize && IsVirtualizationEnabled())
            {
                m_fileVirtualizationStates.TryAdd(file, VirtualizationState.PendingVirtual);
            }

            TaskSourceSlim<PipOutputOrigin> materializationCompletion;
            Task<PipOutputOrigin> alreadyMaterializingTask;

            if (!TryReserveCompletion(m_materializationTasks, file, out alreadyMaterializingTask, out materializationCompletion))
            {
                if (dependentFileIndex != null)
                {
                    // Ensure the dependent artifact waits on the materialization of this file to complete
                    state.SetDependencyArtifactCompletion(dependentFileIndex.Value, alreadyMaterializingTask);
                }

                // Another thread tried to materialize this file
                // so add this to the list of pending placements so that we await the result before trying to place the other files.
                // Note: File is not added if it already finish materializing with a successful result since its safe
                // to just bypass it in this case
                if (alreadyMaterializingTask.Status != TaskStatus.RanToCompletion ||
                    alreadyMaterializingTask.Result == PipOutputOrigin.NotMaterialized)
                {
                    state.PendingPlacementTasks.Add((file, alreadyMaterializingTask));
                }
                else
                {
                    // Update OverallMaterializationResult
                    state.MergeResult(alreadyMaterializingTask.Result);
                }
            }
            else
            {
                if (dependentFileIndex != null)
                {
                    // Ensure the dependent artifact waits on the materialization of this file to complete
                    state.SetDependencyArtifactCompletion(dependentFileIndex.Value, materializationCompletion.Task);
                }

                FileMaterializationInfo materializationInfo = GetInputContent(file);

                state.AddMaterializationFile(
                    fileToMaterialize: file,
                    allowReadOnly: allowReadOnlyOverride ?? AllowFileReadOnly(file),
                    materializationInfo: materializationInfo,
                    materializationCompletion: materializationCompletion);
            }

            AddFileMaterializationBehavior getBehavior()
            {
                if (file.IsSourceFile)
                {
                    // Only distributed workers need to verify/materialize source files
                    if (IsDistributedWorker && !sealDirMember && Configuration.Distribution.VerifySourceFilesOnWorkers)
                    {
                        return AddFileMaterializationBehavior.Verify;
                    }
                    else
                    {
                        return AddFileMaterializationBehavior.Skip;
                    }
                }
                else
                {
                    return AddFileMaterializationBehavior.Materialize;
                }
            }
        }


        private async Task<bool> DeleteFilesRequiredAbsentAsync(PipArtifactsState state, OperationContext operationContext)
        {
            // Don't do anything for materialization that are already completed by prior states
            state.RemoveCompletedMaterializations();

            bool deletionSuccess = await Task.Run(() =>
            {
                bool success = true;
                for (int i = 0; i < state.MaterializationFiles.Count; i++)
                {
                    MaterializationFile materializationFile = state.MaterializationFiles[i];

                    if (materializationFile.MaterializationInfo.Hash == WellKnownContentHashes.AbsentFile)
                    {
                        var file = materializationFile.Artifact;
                        var filePath = file.Path.ToString(Context.PathTable);

                        try
                        {
                            ContentMaterializationOrigin origin = ContentMaterializationOrigin.UpToDate;

                            if (FileUtilities.Exists(filePath))
                            {
                                // Delete the file if it exists
                                FileUtilities.DeleteFile(filePath, tempDirectoryCleaner: m_tempDirectoryCleaner);
                                origin = ContentMaterializationOrigin.DeployedFromCache;
                            }

                            state.SetMaterializationSuccess(i, origin: origin, operationContext: operationContext);
                        }
                        catch (BuildXLException ex)
                        {
                            Logger.Log.StorageRemoveAbsentFileOutputWarning(
                                operationContext,
                                pipDescription: GetAssociatedPipDescription(file),
                                destinationPath: filePath,
                                errorMessage: ex.LogEventMessage);

                            success = false;
                            state.SetMaterializationFailure(i);
                        }
                    }
                }

                return success;
            });

            return deletionSuccess;
        }

        /// <summary>
        /// Delete the contents of opaque (or dynamic) directories before deploying files from cache if directories exist; otherwise create empty directories.
        /// </summary>
        /// <remarks>
        /// Creating empty directories when they don't exist ensures the correctness of replaying pip outputs. Those existence of such directories may be needed
        /// by downstream pips. Empty directories are not stored into the cache, but their paths are stored in the pip itself and are collected when we populate <see cref="PipArtifactsState"/>.
        /// If the pip outputs are removed, then to replay the empty output directories in the next build, when we have a cache hit, those directories need to be created.
        /// </remarks>
        private async Task<bool> PrepareDirectoriesAsync(PipArtifactsState state, OperationContext operationContext)
        {
            bool deletionSuccess = await Task.Run(() =>
            {
                bool success = true;

                // Delete the contents of opaque directories before deploying files from cache, or re-create empty directories if they don't exist.
                foreach (var (directory, isPreservedOutputsDirectory, completion) in state.DirectoryDeletionCompletions)
                {
                    try
                    {
                        var dirOutputPath = directory.Path.ToString(Context.PathTable);

                        if (FileUtilities.DirectoryExistsNoFollow(dirOutputPath))
                        {
                            // Delete directory contents if the directory itself exists. Directory content deletion
                            // can throw an exception if users are naughty, e.g. they remove the output directory, rename directory, etc.
                            // The exception is thrown because the method tries to verify that the directory has been emptied by
                            // enumerating the directory or a descendant directory. Note that this is a possibly expensive I/O operation.
                            FileUtilities.DeleteDirectoryContents(
                                dirOutputPath,
                                shouldDelete: (filePath, isReparsePoint) =>
                                {
                                    using (
                                        operationContext.StartAsyncOperation(
                                            PipExecutorCounter.FileContentManagerDeleteDirectoriesPathParsingDuration))
                                    {
                                        var path = AbsolutePath.Create(Context.PathTable, filePath);

                                        // For opaque directories that have preserved outputs enabled, we want to leave all contents alone unless
                                        // a parent of an output to potentially be materialized is in the wrong state (reparse point instead of a directory)
                                        if (isPreservedOutputsDirectory)
                                        {
                                            // Delete the path if it is supposed to be a directory leading up to an output but is instead a reparse point
                                            return state.MaterializedDirectoryContents.TryGetValue(path, out bool isThisADirectory) && isThisADirectory && isReparsePoint;
                                        }

                                        // MaterializedDirectoryContents will contain all declared contents of the directory which should not be deleted
                                        // as the file may already have been materialized by the file content manager. If the file was not materialized
                                        // by the file content manager, it will be deleted and replaced as a part of file materialization
                                        // We also delete the directory symlinks whose paths are added to MaterializedDirectoryContents due to being the parents of some contents. 
                                        return !state.MaterializedDirectoryContents.TryGetValue(path, out bool isDirectory) || (isDirectory && isReparsePoint);
                                    }
                                },
                                tempDirectoryCleaner: m_tempDirectoryCleaner);
                        }
                        else
                        {
                            if (FileUtilities.FileExistsNoFollow(dirOutputPath))
                            {
                                FileUtilities.DeleteFile(dirOutputPath, retryOnFailure: true, tempDirectoryCleaner: m_tempDirectoryCleaner);
                            }

                            // If the directory does not exist, create one. This is to ensure that an opaque directory is always present on disk.
                            FileUtilities.CreateDirectory(dirOutputPath);
                        }

                        m_dynamicDirectoryDeletionTasks[directory] = BoolTask.True;
                        completion.SetResult(true);
                    }
                    catch (BuildXLException ex)
                    {
                        Logger.Log.StorageCacheCleanDirectoryOutputError(
                            operationContext,
                            pipDescription: GetAssociatedPipDescription(directory),
                            destinationPath: directory.Path.ToString(Context.PathTable),
                            errorMessage: ex.LogEventMessage);
                        state.AddFailedDirectory(directory);

                        success = false;

                        m_dynamicDirectoryDeletionTasks[directory] = BoolTask.False;
                        completion.SetResult(false);
                    }
                }

                return success;
            });

            var deletionResults = await Task.WhenAll(state.PendingDirectoryDeletions);
            deletionSuccess &= deletionResults.All(result => result);

            return deletionSuccess;
        }

        /// <summary>
        /// Attempt to place files from local cache
        /// </summary>
        /// <remarks>
        /// Logs warnings when a file placement fails; does not log errors.
        /// </remarks>
        private async Task<PlaceFile> PlaceFilesAsync(
            OperationContext operationContext,
            PipInfo pipInfo,
            PipArtifactsState state)
        {
            bool success = true;
            bool userError = false;

            var counter =
              state.VerifyMaterializationOnly ? PipExecutorCounter.FileContentManagerPlaceFilesVerifiedPinDuration : (
              state.IsApiServerRequest ? PipExecutorCounter.FileContentManagerPlaceFilesApiServerDuration : (
              state.MaterializingOutputs ? PipExecutorCounter.FileContentManagerPlaceFilesOutputsDuration :
              PipExecutorCounter.FileContentManagerPlaceFilesInputsDuration));

            using (operationContext.StartOperation(counter))
            {
                if (state.MaterializationFiles.Count != 0)
                {
                    var pathTable = Context.PathTable;

                    // Remove the completed materializations (this is mainly to remove source file 'materializations') which
                    // may have already completed if running in the mode where source files are assumed to be materialized prior to the
                    // start of the build on a distributed worker
                    state.RemoveCompletedMaterializations();

                    success &= await TryLoadAvailableContentAsync(
                        operationContext,
                        pipInfo,
                        state);

                    if (!success && state.InnerFailure is FailToDeleteForMaterializationFailure)
                    {
                        userError = true;
                    }

                    // Remove the failures
                    // After calling TryLoadAvailableContentAsync some files may be marked completed (as failures)
                    // we need to remove them so we don't try to place them
                    state.RemoveCompletedMaterializations();

                    // Maybe we didn't manage to fetch all of the remote content. However, for the content that was fetched,
                    // we still are mandated to finish materializing if possible and eventually complete the materialization task.

                    for (int i = 0; i < state.MaterializationFiles.Count; i++)
                    {
                        MaterializationFile materializationFile = state.MaterializationFiles[i];
                        FileArtifact file = materializationFile.Artifact;
                        FileMaterializationInfo materializationInfo = materializationFile.MaterializationInfo;
                        ContentHash hash = materializationInfo.Hash;
                        bool allowReadOnly = materializationFile.AllowReadOnly;
                        int materializationFileIndex = i;

                        state.PlacementTasks.Add(Task.Run(
                            async () =>
                            {
                                if (Context.CancellationToken.IsCancellationRequested)
                                {
                                    state.SetMaterializationFailure(fileIndex: materializationFileIndex);
                                    success = false;
                                    return;
                                }

                                Possible<ContentMaterializationResult> possiblyPlaced = await PlaceSingleFileAsync(operationContext, state, materializationFileIndex, materializationFile);

                                Possible<Unit> finalResult = possiblyPlaced
                                                                .Then(_ => m_host.ReportFileArtifactPlaced(file, materializationInfo))
                                                                .Then(_ =>
                                                                {
                                                                    state.SetMaterializationSuccess(
                                                                        fileIndex: materializationFileIndex,
                                                                        origin: possiblyPlaced.Result.Origin,
                                                                        operationContext: operationContext);
                                                                    return Unit.Void;
                                                                });
                                if (!finalResult.Succeeded)
                                {
                                    Logger.Log.StorageCacheGetContentWarning(
                                        operationContext,
                                        pipDescription: pipInfo.Description,
                                        contentHash: hash.ToHex(),
                                        destinationPath: file.Path.ToString(pathTable),
                                        errorMessage: finalResult.Failure.DescribeIncludingInnerFailures());

                                    state.SetMaterializationFailure(fileIndex: materializationFileIndex);

                                    if (finalResult.Failure is FailToDeleteForMaterializationFailure)
                                    {
                                        userError = true;
                                    }

                                    // Latch overall success (across all placements) to false.
                                    success = false;
                                }
                            }));
                    }

                }

                foreach (var file in state.HydrationFiles)
                {
                    state.PlacementTasks.Add(HydrateFileAsync(operationContext, state, file));
                }

                if (state.PlacementTasks.Count != 0)
                {
                    await TaskUtilities.SafeWhenAll(state.PlacementTasks);
                }

                // Wait on any placements for files already in progress by other pips
                state.PlacementTasks.Clear();
                foreach (var pendingPlacementTask in state.PendingPlacementTasks)
                {
                    state.PlacementTasks.Add(pendingPlacementTask.tasks);
                }

                await TaskUtilities.SafeWhenAll(state.PlacementTasks);

                foreach (var pendingPlacement in state.PendingPlacementTasks)
                {
                    var result = await pendingPlacement.tasks;
                    if (result == PipOutputOrigin.NotMaterialized)
                    {
                        var file = pendingPlacement.fileArtifact;
                        state.AddFailedFile(file, GetInputContent(file).Hash);

                        // Not materialized indicates failure
                        success = false;
                    }
                    else
                    {
                        state.MergeResult(result);
                    }
                }
            }

            return success ? PlaceFile.Success : (userError ? PlaceFile.UserError : PlaceFile.InternalError);
        }

        private static readonly byte[] s_fileHydrationByteArray = new byte[1];

        private async Task<bool> HydrateFileAsync(
            OperationContext operationContext,
            PipArtifactsState state,
            AbsolutePath path)
        {
            if (m_currentlyMaterializingFilesByPath.TryGetValue(path, out var artifact)
                && m_materializationTasks.TryGetValue(artifact, out var materializationTask))
            {
                // Wait for file to be materialized if still outstanding before hydrating
                await materializationTask;
            }

            if (!m_fileVirtualizationStates.TryUpdate(path, comparisonValue: VirtualizationState.PendingHydration, newValue: VirtualizationState.Hydrated))
            {
                // File is not currently virtualized. No need to hydrate.
                return false;
            }

            // Ensure task is not executed synchronously
            await Task.Yield();

            var artifactForTracing = FileArtifact.CreateOutputFile(path);
            using (var outerContext = operationContext.StartAsyncOperation(PipExecutorCounter.FileContentManagerTryMaterializeOuterDuration, artifactForTracing))
            using (await m_materializationSemaphore.AcquireAsync())
            using (outerContext.StartOperation(PipExecutorCounter.FileContentManagerHydrateDuration, artifactForTracing))
            {
                try
                {
                    var fullPath = path.ToString(Context.PathTable);
                    using (var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        await stream.ReadAsync(s_fileHydrationByteArray, 0, 1);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.PipHydrateFileFailure(
                        operationContext,
                        state.PipInfo.Description,
                        path.ToString(Context.PathTable),
                        ex.GetLogEventMessage());
                }

                Logger.Log.PipHydratedFile(
                        operationContext,
                        state.PipInfo.Description,
                        path.ToString(Context.PathTable));
                return true;
            }
        }

        private async Task<Possible<ContentMaterializationResult>> PlaceSingleFileAsync(
            OperationContext operationContext,
            PipArtifactsState state,
            int fileIndex,
            MaterializationFile materializationFile)
        {
            FileArtifact file = materializationFile.Artifact;
            FileMaterializationInfo materializationInfo = materializationFile.MaterializationInfo;
            ContentHash hash = materializationInfo.Hash;
            PathAtom fileName = materializationInfo.FileName;
            RelativePath dynamicOutputCaseSensitiveRelativeDirectory = materializationInfo.DynamicOutputCaseSensitiveRelativeDirectory;
            // Read only is allowed if set on the materialization file and the file is not an allowed source rewrite: we don't want to make the
            // file readonly when placing it since the rewrite was allowed to begin with, in a source or alien file which was very likely not a readonly one.
            // The ultimate goal is to allow the file to continue to be rewritten in subsequent builds, if possible
            bool allowReadOnly = materializationFile.AllowReadOnly && !materializationFile.MaterializationInfo.IsUndeclaredFileRewrite;

            using (var outerContext = operationContext.StartAsyncOperation(PipExecutorCounter.FileContentManagerTryMaterializeOuterDuration, file))
            using (await m_materializationSemaphore.AcquireAsync())
            {
                // Quickly fail pending placements when cancellation is requested
                if (Context.CancellationToken.IsCancellationRequested)
                {
                    var possiblyPlaced = new Possible<ContentMaterializationResult>(new CtrlCCancellationFailure());
                    return WithLineInfo(possiblyPlaced);
                }

                // Wait for the prior version of the file artifact to finish materialization
                await materializationFile.PriorArtifactVersionCompletion;

                if (m_host.CanMaterializeFile(file))
                {
                    using (outerContext.StartOperation(PipExecutorCounter.FileContentManagerHostTryMaterializeDuration, file))
                    {
                        var possiblyMaterialized = await m_host.TryMaterializeFileAsync(file, outerContext);
                        return possiblyMaterialized.Then(origin =>
                            new ContentMaterializationResult(
                                origin,
                                TrackedFileContentInfo.CreateUntracked(materializationInfo.FileContentInfo)));
                    }
                }
                else
                {
                    using (var op = outerContext.StartOperation(
                        materializationFile.CreateReparsePoint
                            ? PipExecutorCounter.TryMaterializeReparsePointDuration
                            : PipExecutorCounter.FileContentManagerTryMaterializeDuration,
                        file))
                    {
                        if (state.VerifyMaterializationOnly)
                        {
                            // Ensure local existence by opening content stream.
                            var possiblyStream = await ArtifactContentCache.TryOpenContentStreamAsync(hash);

                            if (possiblyStream.Succeeded)
                            {
                                possiblyStream.Result.Dispose();

                                var possiblyPlaced =
                                    new Possible<ContentMaterializationResult>(
                                        new ContentMaterializationResult(
                                            ContentMaterializationOrigin.DeployedFromCache,
                                            TrackedFileContentInfo.CreateUntracked(materializationInfo.FileContentInfo, fileName, materializationInfo.OpaqueDirectoryRoot, dynamicOutputCaseSensitiveRelativeDirectory)));
                                return WithLineInfo(possiblyPlaced);
                            }
                            else
                            {
                                var possiblyPlaced = new Possible<ContentMaterializationResult>(possiblyStream.Failure);
                                return WithLineInfo(possiblyPlaced);
                            }
                        }
                        else
                        {
                            var (checkExistsOnDisk, _, contentOnDiskInfo) = await CheckExistsContentOnDiskIfNeededAsync(
                                outerContext,
                                materializationFile.Artifact,
                                state.PipInfo,
                                state.MaterializingOutputs,
                                materializationInfo.OpaqueDirectoryRoot);

                            if (checkExistsOnDisk
                                && contentOnDiskInfo.HasValue
                                && contentOnDiskInfo.Value.Hash == materializationFile.MaterializationInfo.Hash)
                            {
                                return WithLineInfo(
                                    new Possible<ContentMaterializationResult>(new ContentMaterializationResult(ContentMaterializationOrigin.UpToDate, contentOnDiskInfo.Value)));
                            }

                            // Don't virtualize outputs
                            bool canVirtualize = ShouldVirtualize(state, materializationFile, out var virtualizationInfo);
                            state.SetVirtualizationInfo(fileIndex, virtualizationInfo);

                            // Try materialize content.
                            Possible<ContentMaterializationResult> possiblyPlaced = await LocalDiskContentStore.TryMaterializeAsync(
                                ArtifactContentCache,
                                fileRealizationModes: GetFileRealizationMode(allowReadOnly: allowReadOnly)
                                    .WithAllowVirtualization(allowVirtualization: canVirtualize),
                                path: file.Path,
                                fileName: fileName,
                                caseSensitiveRelativeDirectory: dynamicOutputCaseSensitiveRelativeDirectory,
                                contentHash: hash,
                                reparsePointInfo: materializationInfo.ReparsePointInfo,
                                // Don't track or record hashes of virtual files since they should be replaced if encountered
                                // in subsequent builds. Due to the volatility of the VFS provider.
                                trackPath: !canVirtualize,
                                recordPathInFileContentTable: !canVirtualize,
                                cancellationToken: Context.CancellationToken);

                            if (possiblyPlaced.Succeeded)
                            {
                                // Materialization will fail after 30m due to timeout limit. At the same time,
                                // we'd like to find out for how many operations the materialization takes more than 5 min.
                                // We hard-coded this limit as we do not want to get it used by somewhere else in this class.
                                bool longOperation = op.Duration.HasValue && op.Duration.Value.TotalMinutes > 5;

                                if (state.MaterializingOutputs)
                                {
                                    // Count output materialization requested by API Server (i.e., an external call to the MaterializeFile API)
                                    // separately from output materialization done by the engine.
                                    if (state.IsApiServerRequest)
                                    {
                                        Interlocked.Add(ref m_stats.TotalApiServerMaterializedOutputsSize, materializationInfo.Length);
                                        Interlocked.Increment(ref m_stats.TotalApiServerMaterializedOutputsCount);
                                    }
                                    else
                                    {
                                        Interlocked.Add(ref m_stats.TotalMaterializedOutputsSize, materializationInfo.Length);
                                        Interlocked.Increment(ref m_stats.TotalMaterializedOutputsCount);
                                    }

                                    if (longOperation)
                                    {
                                        Interlocked.Increment(ref m_stats.TotalMaterializedOutputsExpensiveCount);
                                    }
                                }
                                else
                                {
                                    Interlocked.Add(ref m_stats.TotalMaterializedInputsSize, materializationInfo.Length);
                                    Interlocked.Increment(ref m_stats.TotalMaterializedInputsCount);
                                    if (longOperation)
                                    {
                                        Interlocked.Increment(ref m_stats.TotalMaterializedInputsExpensiveCount);
                                    }
                                }
                            }
                            else if (Context.CancellationToken.IsCancellationRequested)
                            {
                                // If the materialization was unsuccessful and a cancellation was requested, we can skip logging this message to the console.
                                // Return CtrlCCancellationFailure instead of possiblyPlaced to indicate that it does not need to be logged.
                                return WithLineInfo(new Possible<ContentMaterializationResult>(new CtrlCCancellationFailure()));
                            }

                            return WithLineInfo(possiblyPlaced);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Checks whether file should be virtualized by TryMaterializeFile
        /// NOTE: This should only be called prior to TryMaterializeFile since it changes the state of the file to FullMaterialized
        /// in cases where the file should not be virtualized.
        /// </summary>
        private bool ShouldVirtualize(PipArtifactsState state, MaterializationFile materializationFile, out string virtualizationInfo)
        {
            if (!IsVirtualizationEnabled())
            {
                virtualizationInfo = null;
                return false;
            }

            if (!shouldVirtualizeCore(out virtualizationInfo))
            {
                m_fileVirtualizationStates[materializationFile.Artifact] = VirtualizationState.FullMaterialized;
                return false;
            }

            return true;

            bool shouldVirtualizeCore(out string virtualizationInfo)
            {
                if (materializationFile.CreateReparsePoint)
                {
                    // Reparse points are not virtualized
                    virtualizationInfo = "[VirtualizationInfo=Full:ReparsePoint]";
                    return false;
                }

                if (state.MaterializingOutputs)
                {
                    // The file a materialized output. Assume file is required outside of build and don't virtualize.
                    virtualizationInfo = "[VirtualizationInfo=Full:MaterializingOutputs]";
                    return false;
                }

                if (!state.Virtualize)
                {
                    // Virtualization for pip was disabled due to lack of historic information
                    virtualizationInfo = "[VirtualizationInfo=Full:PipVirtualizationOff]";
                    return false;
                }

                if (m_host.TryGetCopySourceFile(materializationFile.Artifact, out var copySource) && copySource.IsSourceFile)
                {
                    // Copies lazily store into cache from the source file, so we can't virtualize since we don't get a callback
                    // to store into the cache if the file is not already present.
                    virtualizationInfo = "[VirtualizationInfo=Full:CopySource]";
                    return false;
                }

                if (!m_fileVirtualizationStates.TryUpdate(materializationFile.Artifact, comparisonValue: VirtualizationState.PendingVirtual, newValue: VirtualizationState.Virtual))
                {
                    // The file requires hydration. Don't virtualize.
                    virtualizationInfo = "[VirtualizationInfo=Full:MarkedForHydration]";
                    return false;
                }

                virtualizationInfo = "[VirtualizationInfo=Virtualize]";
                return true;
            }
        }

        private bool IsVirtualizationEnabled() => m_host.Configuration.Cache.VfsCasRoot.IsValid;

        private static Possible<T> WithLineInfo<T>(Possible<T> possible, [CallerMemberName] string caller = null, [CallerLineNumber] int line = 0)
        {
            return possible.Succeeded ? possible : new Failure<string>(I($"Failure line info: {caller} ({line})"), possible.Failure);
        }

        private static PipOutputOrigin GetPipOutputOrigin(ContentMaterializationOrigin origin, Pip pip)
        {
            switch (pip.PipType)
            {
                case PipType.WriteFile:
                case PipType.CopyFile:
                    return origin.ToPipOutputOriginHidingDeploymentFromCache();
                default:
                    return origin.ToPipOutputOrigin();
            }
        }

        /// <summary>
        /// Attempt to bring multiple file contents into the local cache.
        /// </summary>
        /// <remarks>
        /// May log warnings. Does not log errors.
        /// </remarks>
        private Task<bool> TryLoadAvailableContentAsync(
            OperationContext operationContext,
            PipInfo pipInfo,
            PipArtifactsState state)
        {
            return TryLoadAvailableContentAsync(
                operationContext,
                pipInfo,
                state.MaterializingOutputs,
                state.GetCacheMaterializationFiles().SelectList(i => (i.materializationFile.Artifact, i.materializationFile.MaterializationInfo.Hash, i.index, i.materializationFile.MaterializationInfo.OpaqueDirectoryRoot, i.materializationFile.MaterializationInfo.IsExecutable)),
                onFailure: failure =>
                {
                    for (int index = 0; index < state.MaterializationFiles.Count; index++)
                    {
                        state.SetMaterializationFailure(index);
                    }

                    state.InnerFailure = failure;
                },
                onContentUnavailable: (index, expectedHash, hashOnDiskIfAvailableOrNull, failure) =>
                {
                    state.SetMaterializationFailure(index);
                    FileArtifact file = state.MaterializationFiles[index].Artifact;

                    // Log the eventual path on failure for sake of correlating the file within the build
                    if (Configuration.Schedule.StoreOutputsToCache)
                    {
                        Logger.Log.FailedToLoadFileContentWarning(
                            operationContext,
                            pipInfo.Description,
                            expectedHash,
                            file.Path.ToString(Context.PathTable));

                        state.InnerFailure = failure;
                    }
                    else
                    {
                        if (state.MaterializingOutputs
                            && pipInfo.UnderlyingPip.PipType == PipType.Process)
                        {
                            // We are materializing outputs of a process pip for the case where no outputs are stored to the cache.
                            // At this point, the cache look-up has determined that all output targets are up-to-date (i.e.,
                            // they have correct hashes). However, upon output materialization, it turns out that the
                            // target is no longer up-to-date. (Customers claim that they do not modify the file, nor anything in the build
                            // modifies the file.)
                            //
                            // The following log will show what hashes seen after cache look-up,
                            // and the expected hashes of the targets. One should check CopyingPipOutputToLocalStorage log for
                            // the actual hashes on disk. The reason for showing the cache look-up content hash is to ensure that
                            // the file has been checked to be up-to-date during the cache look-up.

                            string cacheLookUpContentHash = "<UNKNOWN>";

                            if (m_fileArtifactContentHashes.TryGetValue(file, out FileMaterializationInfo info))
                            {
                                cacheLookUpContentHash = info.Hash.ToHex();
                            }

                            Logger.Log.FailedToMaterializeFileNotUpToDateOutputWarning(
                                operationContext,
                                pipInfo.Description,
                                file.Path.ToString(Context.PathTable),
                                expectedHash,
                                cacheLookUpContentHash,
                                hashOnDiskIfAvailableOrNull ?? WellKnownContentHashes.AbsentFile.ToHex());
                        }
                    }
                },
                state: state);
        }

        private async Task<(bool performedCheck, bool isPreservedOutputFile, TrackedFileContentInfo? contentInfo)> CheckExistsContentOnDiskIfNeededAsync(
            OperationContext operationContext,
            FileArtifact fileArtifact,
            PipInfo pipInfo,
            bool materializingOutputs,
            AbsolutePath outputDirectoryRoot)
        {
            bool isPreservedOutputFile = IsPreservedOutputFile(pipInfo.UnderlyingPip, materializingOutputs, fileArtifact);

            bool shouldDiscoverContentOnDisk =
                Configuration.Schedule.ReuseOutputsOnDisk ||
                !Configuration.Schedule.StoreOutputsToCache ||
                isPreservedOutputFile;

            if (shouldDiscoverContentOnDisk)
            {
                using (operationContext.StartOperation(OperationCounter.FileContentManagerDiscoverExistingContent, fileArtifact))
                {
                    // Discover the existing file (if any) and get its content hash
                    Possible<ContentDiscoveryResult, Failure> existingContent = await LocalDiskContentStore.TryDiscoverAsync(fileArtifact, outputDirectoryRoot: outputDirectoryRoot);
                    return existingContent.Succeeded
                        ? (true, isPreservedOutputFile, existingContent.Result.TrackedFileContentInfo)
                        : (true, isPreservedOutputFile, default);
                }
            }

            return (false, isPreservedOutputFile, default);
        }

        /// <summary>
        /// Attempt to bring multiple file contents into the local cache.
        /// </summary>
        /// <remarks>
        /// May log warnings. Does not log errors.
        /// </remarks>
        private async Task<bool> TryLoadAvailableContentAsync(
            OperationContext operationContext,
            PipInfo pipInfo,
            bool materializingOutputs,
            IReadOnlyList<(FileArtifact fileArtifact, ContentHash contentHash, int fileIndex, AbsolutePath outputDirectoryRoot, bool isExecutable)> filesAndContentHashes,
            Action<Failure> onFailure,
            Action<int, string, string, Failure> onContentUnavailable,
            bool onlyLogUnavailableContent = false,
            PipArtifactsState state = null)
        {
            bool success = true;

            Possible<ContentAvailabilityBatchResult, Failure> possibleResults;
            using (operationContext.StartOperation(PipExecutorCounter.FileContentManagerTryLoadAvailableContentDuration))
            {
                if (state != null && EngineEnvironmentSettings.SkipExtraneousPins.Value)
                {
                    // When actually materializing files, skip the pin and place directly.
                    possibleResults = await PlaceFilesPinAsync(operationContext, state);
                }
                else
                {
                    possibleResults =
                        await
                            ArtifactContentCache.TryLoadAvailableContentAsync(
                                filesAndContentHashes.Select(pathAndContentHash => pathAndContentHash.contentHash).ToList(), Context.CancellationToken);
                }
            }

            if (!possibleResults.Succeeded)
            {
                // Actual failure (distinct from a per-hash miss); should be unusual
                // We need to fail all materialization tasks since we don't have per-hash results.

                // TODO: We may want to check if the files on disk are up-to-date.
                // We can avoid logging failures that are a result of cancelling the build early to keep the log clean.
                if (!Context.CancellationToken.IsCancellationRequested)
                {
                    Logger.Log.StorageBringProcessContentLocalWarning(
                        operationContext,
                        pipInfo.Description,
                        possibleResults.Failure.DescribeIncludingInnerFailures());
                }

                onFailure(possibleResults.Failure);

                success = false;
            }
            else
            {
                ContentAvailabilityBatchResult resultsBatch = possibleResults.Result;
                ReadOnlyArray<ContentAvailabilityResult> results = resultsBatch.Results;
                Contract.Assert(filesAndContentHashes.Count == results.Length);

                var recoverContents = Enumerable
                    .Range(0, results.Length)
                    .Select(i => m_recoverContentActionBlock.ProcessAsync(() => RecoverContentIfNeededAsync(
                        operationContext,
                        pipInfo,
                        materializingOutputs,
                        onContentUnavailable,
                        results[i],
                        filesAndContentHashes[i],
                        onlyLogUnavailableContent,
                        state)));

                var recoverResults = await TaskUtilities.SafeWhenAll(recoverContents);
                success = recoverResults.All(r => r);
            }

            return success;
        }

        private async Task<bool> RecoverContentIfNeededAsync(
            OperationContext operationContext,
            PipInfo pipInfo,
            bool materializingOutputs,
            Action<int, string, string, Failure> onContentUnavailable,
            ContentAvailabilityResult result,
            (FileArtifact fileArtifact, ContentHash contentHash, int fileIndex, AbsolutePath outputDirectoryRoot, bool isExecutable) filesAndContentHashesEntry,
            bool onlyLogUnavailableContent = false,
            PipArtifactsState state = null)
        {
            if (Context.CancellationToken.IsCancellationRequested)
            {
                return false;
            }

            const string TargetUpToDate = "True";
            const string TargetNotUpToDate = "False";
            const string TargetNotChecked = "Not Checked";

            var fileArtifact = filesAndContentHashesEntry.fileArtifact;
            var contentHash = filesAndContentHashesEntry.contentHash;
            var currentFileIndex = filesAndContentHashesEntry.fileIndex;
            var outputDirectoryRoot = filesAndContentHashesEntry.outputDirectoryRoot;
            var isExecutable = filesAndContentHashesEntry.isExecutable;

            Contract.Assume(contentHash == result.Hash);
            var outerContext = operationContext.StartAsyncOperation(OperationCounter.FileContentManagerHandleContentAvailabilityOuter, fileArtifact);
            using (outerContext.StartOperation(OperationCounter.FileContentManagerHandleContentAvailability, fileArtifact))
            {
                bool isAvailable = result.IsAvailable;
                string targetLocationUpToDate = TargetNotChecked;
                TrackedFileContentInfo? existingContentOnDiskInfo = default;

                if (!isAvailable)
                {
                    // Try to recover content that was wanted but not found in the cache
                    Interlocked.Increment(ref m_stats.FileRecoveryAttempts);

                    var (checkExistsOnDisk, isPreservedOutputFile, contentOnDiskInfo) = await CheckExistsContentOnDiskIfNeededAsync(
                        outerContext,
                        fileArtifact,
                        pipInfo,
                        materializingOutputs,
                        outputDirectoryRoot);

                    if (checkExistsOnDisk && contentOnDiskInfo.HasValue)
                    {
                        existingContentOnDiskInfo = contentOnDiskInfo.Value;
                    }

                    if (checkExistsOnDisk
                        && contentOnDiskInfo.HasValue
                        && contentOnDiskInfo.Value.Hash == contentHash)
                    {
                        targetLocationUpToDate = TargetUpToDate;

                        if (isPreservedOutputFile || !Configuration.Schedule.StoreOutputsToCache)
                        {
                            // If file should be preserved, then we do not restore its content back to the cache.
                            // If the preserved file is copied using a copy-file pip, then the materialization of
                            // the copy-file destination relies on the else-clause below where we try to get
                            // other file using TryGetFileArtifactForHash.
                            // If we don't store outputs to cache, then we should not include cache operation to determine
                            // if the content is available. However, we just checked, by TryDiscoverAsync above, that the content
                            // is available with the expected content hash. Thus, we can safely say that the content is available.
                            isAvailable = true;
                        }
                        else
                        {
                            // The file has the correct hash so we can restore it back into the cache for future use/retention.
                            // But don't restore files that need to be preserved because they were not stored to the cache.
                            var possiblyStored =
                                await RestoreContentInCacheAsync(
                                    outerContext,
                                    pipInfo.UnderlyingPip,
                                    materializingOutputs,
                                    fileArtifact,
                                    contentHash,
                                    fileArtifact,
                                    outputDirectoryRoot,
                                    isExecutable: isExecutable);

                            // Try to be conservative here due to distributed builds (e.g., the files may not exist on other machines).
                            isAvailable = possiblyStored.Succeeded;
                        }

                        if (isAvailable)
                        {
                            // Content is up to date and available, so just mark the file as successfully materialized.
                            state?.SetMaterializationSuccess(currentFileIndex, ContentMaterializationOrigin.UpToDate, outerContext);
                        }
                    }
                    else
                    {
                        if (checkExistsOnDisk)
                        {
                            // Content was checked if it exists on disk or not, but it is non-existent.
                            targetLocationUpToDate = TargetNotUpToDate;
                        }

                        // If the up-to-dateness of file on disk is not checked, or the file on disk is not up-to-date,
                        // then fall back to using the cache.

                        // Attempt to find a materialized file for the hash and store that
                        // into the cache to ensure the content is available
                        // This is mainly used for incremental scheduling which does not account
                        // for content which has been evicted from the cache when performing copies
                        (FileArtifact otherFile, AbsolutePath otherFileOutputDirectoryRoot) = TryGetFileArtifactForHash(contentHash);
                        if (!otherFile.IsValid)
                        {
                            FileArtifact copyOutput = fileArtifact;
                            FileArtifact copySource;
                            while (m_host.TryGetCopySourceFile(copyOutput, out copySource))
                            {
                                // Use the source of the copy file as the file to restore
                                // Observe in this case the other file is never a dynamic one, so otherFileDirectoryRoot is not updated (and kept invalid)
                                otherFile = copySource;

                                if (copySource.IsSourceFile)
                                {
                                    // Reached a source file. Just abort rather than calling the host again.
                                    break;
                                }

                                // Try to keep going back through copy chain
                                copyOutput = copySource;
                            }
                        }

                        if (otherFile.IsValid)
                        {
                            if (otherFile.IsSourceFile || IsFileMaterialized(otherFile))
                            {
                                var possiblyStored =
                                    await RestoreContentInCacheAsync(
                                        outerContext,
                                        pipInfo.UnderlyingPip,
                                        materializingOutputs,
                                        otherFile,
                                        contentHash,
                                        fileArtifact,
                                        otherFileOutputDirectoryRoot,
                                        isExecutable: isExecutable);

                                isAvailable = possiblyStored.Succeeded;
                            }
                            else if (state != null && m_host.CanMaterializeFile(otherFile))
                            {
                                // Add the file containing the required content to the list of files to be materialized.
                                // The added to the list rather than inlining the materializing to prevent duplicate/concurrent
                                // materializations of the same file. It also ensures that the current file waits on the materialization
                                // of the other file before attempting materialization.

                                if (!TryGetInputContent(otherFile, out var otherFileMaterializationInfo))
                                {
                                    // Need to set the materialization info in case it is not set on the current machine (i.e. distributed worker)
                                    // This can happen with copied write file outputs. Since the hash of the write file output will not be transferred to worker
                                    // but instead the copied output consumed by the pip will be transferred. We use the hash from the copied file since it is
                                    // the same. We recreate without the file name because copied files can have different names that the originating file.
                                    otherFileMaterializationInfo = FileMaterializationInfo.CreateWithUnknownName(state.MaterializationFiles[currentFileIndex].MaterializationInfo.FileContentInfo);
                                    ReportInputContent(otherFile, otherFileMaterializationInfo);
                                }

                                // Example (dataflow graph)
                                // W[F_W0] -> C[F_C0] -> P1,P2
                                // Where 'W' is a write file which writes an output 'F_W0' with hash '#W0'
                                // Where 'C' is a copy file which copies 'F_W0' to 'F_C0' (with hash '#W0').
                                // Where 'P1' and 'P2' are consumers of 'F_C0'

                                // In this case when P1 materializes inputs,
                                // This list of materialized files are:
                                // 0: F_C0 = #W0 (i.e. materialize file with hash #W0 at the location F_C0)

                                // When processing F_C0 (currentFileIndex=0) we enter this call and
                                // The add file materialization call adds an entry to the list of files to materialize
                                // and modifies F_C0 entry to wait for the completion of F_W0
                                // 0: C0 = #W0 (+ wait for F_W0 to complete materialization)
                                // + 1: F_W0 = #W0
                                AddFileMaterialization(
                                    state,
                                    otherFile,
                                    allowReadOnlyOverride: null,
                                    // Ensure that the current file waits on the materialization before attempting its materialization.
                                    // This ensures that content is present in the cache
                                    dependentFileIndex: currentFileIndex);
                                isAvailable = true;
                            }
                        }
                    }

                    if (isAvailable)
                    {
                        Interlocked.Increment(ref m_stats.FileRecoverySuccesses);
                    }
                }

                // Log the result of each requested hash

                using (outerContext.StartOperation(OperationCounter.FileContentManagerHandleContentAvailabilityLogContentAvailability, fileArtifact))
                {
                    if ((!onlyLogUnavailableContent || !isAvailable) &&
                        ETWLogger.Log.IsEnabled(EventLevel.Verbose, Keywords.Diagnostics))
                    {
                        string expectedContentHash = contentHash.ToHex();

                        if (materializingOutputs)
                        {
                            Logger.Log.ScheduleCopyingPipOutputToLocalStorage(
                                outerContext,
                                pipInfo.UnderlyingPip.FormattedSemiStableHash,
                                expectedContentHash,
                                result: isAvailable,
                                targetLocationUpToDate: targetLocationUpToDate,
                                remotelyCopyBytes: result.BytesTransferred);
                        }
                        else
                        {
                            Logger.Log.ScheduleCopyingPipInputToLocalStorage(
                                outerContext,
                                pipInfo.UnderlyingPip.FormattedSemiStableHash,
                                expectedContentHash,
                                result: isAvailable,
                                targetLocationUpToDate: targetLocationUpToDate,
                                remotelyCopyBytes: result.BytesTransferred);
                        }
                    }

                    if (result.IsAvailable)
                    {
                        // The result was available in cache so report it
                        // Note that, in the above condition, we are using "result.isAvailable" instead of "isAvailable".
                        // If we used "isAvailable", the couter would be incorrect because "isAvailable" can also mean that
                        // the artifact is already on disk (or in the object folder). This can happen when the preserved-output
                        // mode is enabled or when BuildXL doesn't store outputs to cache.
                        ReportTransferredArtifactToLocalCache(true, result.BytesTransferred, result.SourceCache);
                    }

                    // Misses for content are graceful (i.e., the 'load available content' succeeded but deemed something unavailable).
                    // We need to fail the materialization task in that case; there may be other waiters for the same hash / file.
                    if (!isAvailable)
                    {
                        onContentUnavailable(
                            currentFileIndex,
                            contentHash.ToHex(),
                            existingContentOnDiskInfo.HasValue
                                ? existingContentOnDiskInfo.Value.Hash.ToHex()
                                : null,
                            result.Failure);
                        return false;
                    }

                    return true;
                }
            }
        }

        private async Task<Possible<ContentAvailabilityBatchResult, Failure>> PlaceFilesPinAsync(
            OperationContext operationContext,
            PipArtifactsState state)
        {
            var files = state.GetCacheMaterializationFiles();
            var results = new ContentAvailabilityResult[files.Count];
            bool allContentAvailable = true;

            for (int i = 0; i < files.Count; i++)
            {
                var fileAndIndex = files[i];
                Func<int, Task> placeFile = async (int resultIndex) =>
                {
                    FileArtifact fileArtifact = fileAndIndex.materializationFile.Artifact;
                    ContentHash contentHash = fileAndIndex.materializationFile.MaterializationInfo.Hash;

                    var result = await PlaceSingleFileAsync(
                        operationContext,
                        state,
                        fileAndIndex.index,
                        fileAndIndex.materializationFile);

                    if (result.Succeeded)
                    {
                        state.SetMaterializationSuccess(fileAndIndex.index, result.Result.Origin, operationContext);

                        var placed = m_host.ReportFileArtifactPlaced(fileArtifact, fileAndIndex.materializationFile.MaterializationInfo);
                        if (placed.Succeeded)
                        {
                            results[resultIndex] = new ContentAvailabilityResult(contentHash, true, result.Result.TrackedFileContentInfo.Length, "ContentPlaced");
                        }
                        else
                        {
                            allContentAvailable = false;
                            results[resultIndex] = new ContentAvailabilityResult(contentHash, false, 0, "ContentMiss", placed.Failure);
                        }
                    }
                    else
                    {
                        allContentAvailable = false;
                        results[resultIndex] = new ContentAvailabilityResult(contentHash, false, 0, "ContentMiss", result.Failure.InnerFailure);
                    }
                };

                state.PlacementTasks.Add(placeFile(i));
            }

            await TaskUtilities.SafeWhenAll(state.PlacementTasks);

            return new ContentAvailabilityBatchResult(ReadOnlyArray<ContentAvailabilityResult>.FromWithoutCopy(results), allContentAvailable);
        }

        private FileRealizationMode GetFileRealizationModeForCacheRestore(
            Pip pip,
            bool materializingOutputs,
            FileArtifact file,
            AbsolutePath targetPath)
        {
            if (file.Path != targetPath)
            {
                // File has different path from the target path.
                return FileRealizationMode.Copy;
            }

            bool isPreservedOutputFile = IsPreservedOutputFile(pip, materializingOutputs, file);

            bool allowReadOnly = materializingOutputs
                ? !PipArtifacts.IsOutputMustRemainWritablePip(pip)
                : AllowFileReadOnly(file);

            return GetFileRealizationMode(allowReadOnly && !isPreservedOutputFile);
        }

        private async Task<Possible<TrackedFileContentInfo>> RestoreContentInCacheAsync(
            OperationContext operationContext,
            Pip pip,
            bool materializingOutputs,
            FileArtifact fileArtifact,
            ContentHash hash,
            FileArtifact targetFile,
            AbsolutePath fileArtifactOutputDirectoryRoot,
            bool isExecutable)
        {
            if (!Configuration.Schedule.StoreOutputsToCache && !m_host.IsFileRewritten(targetFile))
            {
                return new Failure<string>("Storing content to cache is not allowed");
            }

            using (operationContext.StartOperation(OperationCounter.FileContentManagerRestoreContentInCache))
            {
                FileRealizationMode fileRealizationMode = GetFileRealizationModeForCacheRestore(pip, materializingOutputs, fileArtifact, targetFile.Path);
                bool shouldReTrack = fileRealizationMode != FileRealizationMode.Copy;

                var possiblyStored = await LocalDiskContentStore.TryStoreAsync(
                    ArtifactContentCache,
                    fileRealizationModes: fileRealizationMode,
                    path: fileArtifact.Path,
                    tryFlushPageCacheToFileSystem: shouldReTrack,
                    knownContentHash: hash,
                    trackPath: shouldReTrack,
                    outputDirectoryRoot: fileArtifactOutputDirectoryRoot,
                    isExecutable: isExecutable);

                return possiblyStored;
            }
        }

        private void ReportTransferredArtifactToLocalCache(bool contentIsLocal, long transferredBytes, string sourceCache)
        {
            if (contentIsLocal && transferredBytes > 0)
            {
                Interlocked.Increment(ref m_stats.ArtifactsBroughtToLocalCache);
                Interlocked.Add(ref m_stats.TotalSizeArtifactsBroughtToLocalCache, transferredBytes);
            }

            m_cacheContentSource.AddOrUpdate(sourceCache, 1, (key, value) => value + 1);
        }

        private async Task<bool> VerifySourceFileInputsAsync(
            OperationContext operationContext,
            PipInfo pipInfo,
            PipArtifactsState state)
        {
            Contract.Requires(IsDistributedWorker);

            var pathTable = Context.PathTable;
            bool success = true;

            for (int i = 0; i < state.MaterializationFiles.Count; i++)
            {
                if (Context.CancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                MaterializationFile materializationFile = state.MaterializationFiles[i];
                FileArtifact file = materializationFile.Artifact;
                bool createReparsePoint = materializationFile.CreateReparsePoint;

                if (file.IsSourceFile && !createReparsePoint)
                {
                    // Start the task to hash input
                    state.HashTasks.Add(TryQueryContentAsync(
                        file,
                        operationContext,
                        declaredArtifact: file,
                        pipInfo.UnderlyingPip.ProcessAllowsUndeclaredSourceReads,
                        verifyingHash: true));
                }
                else
                {
                    // Just store placeholder task for output files/reparse points since they are not verified
                    state.HashTasks.Add(s_placeHolderFileHashTask);
                }
            }

            for (int i = 0; i < state.MaterializationFiles.Count; i++)
            {
                MaterializationFile materializationFile = state.MaterializationFiles[i];
                FileArtifact file = materializationFile.Artifact;
                var materializationInfo = materializationFile.MaterializationInfo;
                var expectedHash = materializationInfo.Hash;
                bool createReparsePoint = materializationFile.CreateReparsePoint;

                if (file.IsOutputFile ||

                    // TODO: Bug #995938: Temporary hack to handle pip graph construction verification oversight
                    // where source files declared inside output directories
                    state.MaterializedDirectoryContents.ContainsKey(file.Path) ||

                    // Don't verify if it is a reparse point creation
                    createReparsePoint)
                {
                    // Only source files should be verified
                    continue;
                }

                FileMaterializationInfo? maybeFileInfo = await state.HashTasks[i];
                bool sourceFileHashMatches = maybeFileInfo?.Hash.Equals(expectedHash) == true;

                // Not materializing source files, so verify that the file matches instead
                if (!maybeFileInfo.HasValue)
                {
                    Logger.Log.PipInputVerificationUntrackedInput(
                        operationContext,
                        pipInfo.SemiStableHash,
                        pipInfo.Description,
                        file.Path.ToString(pathTable));
                }
                else if (maybeFileInfo.Value.Hash != expectedHash)
                {
                    var actualFileInfo = maybeFileInfo.Value;
                    ReportWorkerContentMismatch(operationContext, pathTable, file, expectedHash, actualFileInfo.Hash);
                }

                if (sourceFileHashMatches)
                {
                    state.SetMaterializationSuccess(
                        fileIndex: i,
                        origin: ContentMaterializationOrigin.UpToDate,
                        operationContext: operationContext);
                }
                else
                {
                    state.SetMaterializationFailure(fileIndex: i);
                    success = false;
                }
            }

            Contract.Assert(success || operationContext.LoggingContext.WarningWasLogged, "Warning must be logged if source file verification fails");
            return success;
        }

        private static void ReportWorkerContentMismatch(
            LoggingContext loggingContext,
            PathTable pathTable,
            FileArtifact file,
            ContentHash expectedHash,
            ContentHash actualHash)
        {
            if (actualHash == WellKnownContentHashes.AbsentFile)
            {
                Logger.Log.PipInputVerificationMismatchExpectedExistence(
                    loggingContext,
                    filePath: file.Path.ToString(pathTable));
            }
            else if (expectedHash == WellKnownContentHashes.AbsentFile)
            {
                Logger.Log.PipInputVerificationMismatchExpectedNonExistence(
                    loggingContext,
                    filePath: file.Path.ToString(pathTable));
            }
            else if (file.IsSourceFile)
            {
                Logger.Log.PipInputVerificationMismatchForSourceFile(
                    loggingContext,
                    actualHash: actualHash.ToHex(),
                    expectedHash: expectedHash.ToHex(),
                    filePath: file.Path.ToString(pathTable));
            }
            else
            {
                Logger.Log.PipInputVerificationMismatch(
                    loggingContext,
                    actualHash: actualHash.ToHex(),
                    expectedHash: expectedHash.ToHex(),
                    filePath: file.Path.ToString(pathTable));
            }
        }

        private DirectoryArtifact TryGetSealSourceAncestor(AbsolutePath path)
        {
            // Walk the parent directories of the sealedPath to find if it is under a sealedSourceDirectory.
            // The entries are cached and short-circuited otherwise
            var pathTable = Context.PathTable;
            var initialDirectory = path.GetParent(pathTable);
            var currentPath = initialDirectory;

            while (currentPath.IsValid)
            {
                DirectoryArtifact directory;
                if (m_sealedSourceDirectories.TryGetValue(currentPath, out directory))
                {
                    // Cache the parent folder of the file so that subsequent lookups don't have to traverse the parent chain.
                    if (currentPath != path && currentPath != initialDirectory)
                    {
                        m_sealedSourceDirectories.TryAdd(initialDirectory, directory);
                    }

                    return directory;
                }

                currentPath = currentPath.GetParent(pathTable);
            }

            return DirectoryArtifact.Invalid;
        }

        private static bool TryReserveCompletion<TKey, TResult>(
            ConcurrentBigMap<TKey, Task<TResult>> taskCompletionMap,
            TKey key,
            out Task<TResult> retrievedTask,
            out TaskSourceSlim<TResult> addedTaskCompletionSource)
        {
            Task<TResult> taskResult;
            if (taskCompletionMap.TryGetValue(key, out taskResult))
            {
                retrievedTask = taskResult;
                addedTaskCompletionSource = default;
                return false;
            }

            addedTaskCompletionSource = TaskSourceSlim.Create<TResult>();
            var actualMaterializationTask = taskCompletionMap.GetOrAdd(key, addedTaskCompletionSource.Task).Item.Value;

            if (actualMaterializationTask != addedTaskCompletionSource.Task)
            {
                retrievedTask = actualMaterializationTask;
                addedTaskCompletionSource = default;
                return false;
            }

            retrievedTask = null;
            return true;
        }

        /// <summary>
        /// Computes a content hash for a file artifact presently on disk.
        /// Computed content hashes are stored in the scheduler's <see cref="FileContentTable" />, if present.
        /// </summary>
        private async Task<TrackedFileContentInfo?> GetAndRecordSourceFileContentHashAsync(
            OperationContext operationContext,
            FileArtifact fileArtifact,
            FileOrDirectoryArtifact declaredArtifact,
            bool allowUndeclaredSourceReads,
            string consumerDescription = null)
        {
            Contract.Requires(fileArtifact.IsValid);
            Contract.Requires(fileArtifact.IsSourceFile);

            SemanticPathInfo mountInfo = SemanticPathExpander.GetSemanticPathInfo(fileArtifact.Path);

            // if there is a declared mount for the file, it has to allow hashing
            // otherwise, if there is no mount defined, we only hash the file if allowed undeclared source reads is on (and for some tests)
            if ((mountInfo.IsValid && mountInfo.AllowHashing) ||
                (!mountInfo.IsValid && (TrackFilesUnderInvalidMountsForTests || allowUndeclaredSourceReads)))
            {
                return await GetAndRecordFileContentHashAsyncCore(operationContext, fileArtifact, declaredArtifact, consumerDescription);
            }

            if (!mountInfo.IsValid)
            {
                Logger.Log.ScheduleIgnoringUntrackedSourceFileNotUnderMount(operationContext, fileArtifact.Path.ToString(Context.PathTable));
            }
            else
            {
                Logger.Log.ScheduleIgnoringUntrackedSourceFileUnderMountWithHashingDisabled(
                    operationContext,
                    fileArtifact.Path.ToString(Context.PathTable),
                    mountInfo.RootName.ToString(Context.StringTable));
            }

            Interlocked.Increment(ref m_stats.SourceFilesUntracked);
            return TrackedFileContentInfo.CreateUntrackedWithUnknownLength(WellKnownContentHashes.UntrackedFile);
        }

        private string GetAssociatedPipDescription(FileOrDirectoryArtifact declaredArtifact, string consumerDescription = null)
        {
            if (consumerDescription != null)
            {
                return consumerDescription;
            }

            if (declaredArtifact.IsFile)
            {
                DirectoryArtifact dynamicDirectoryArtifact;
                if (declaredArtifact.FileArtifact.IsSourceFile)
                {
                    consumerDescription = m_host.GetConsumerDescription(declaredArtifact);
                    if (consumerDescription != null)
                    {
                        return consumerDescription;
                    }
                }
                else if (m_dynamicOutputFileDirectories.TryGetValue(declaredArtifact.FileArtifact, out dynamicDirectoryArtifact))
                {
                    declaredArtifact = dynamicDirectoryArtifact;
                }
            }

            return m_host.GetProducerDescription(declaredArtifact);
        }

        private async Task<TrackedFileContentInfo?> GetAndRecordFileContentHashAsyncCore(
            OperationContext operationContext,
            FileArtifact fileArtifact,
            FileOrDirectoryArtifact declaredArtifact,
            string consumerDescription = null)
        {
            using (var outerContext = operationContext.StartAsyncOperation(PipExecutorCounter.FileContentManagerGetAndRecordFileContentHashDuration, fileArtifact))
            {
                ExpandedAbsolutePath artifactExpandedPath = fileArtifact.Path.Expand(Context.PathTable);
                string artifactFullPath = artifactExpandedPath.ExpandedPath;

                TrackedFileContentInfo fileTrackedHash;
                Possible<PathExistence> possibleProbeResult = LocalDiskFileSystemView.TryProbeAndTrackPathForExistence(artifactExpandedPath);
                if (!possibleProbeResult.Succeeded)
                {
                    Logger.Log.FailedToHashInputFileDueToFailedExistenceCheck(
                        operationContext,
                        m_host.GetProducerDescription(declaredArtifact),
                        artifactFullPath,
                        possibleProbeResult.Failure.DescribeIncludingInnerFailures());
                    return null;
                }

                if (possibleProbeResult.Result == PathExistence.ExistsAsDirectory)
                {
                    // Record the fact that this path is a directory for use by TryQuerySealedOrUndeclaredInputContent.
                    // Special behavior is used for directories
                    m_contentQueriedDirectoryPaths.Add(fileArtifact.Path);
                }

                if (possibleProbeResult.Result == PathExistence.ExistsAsFile)
                {
                    var outputDirectoryRoot = declaredArtifact.IsDirectory && m_host.GetSealDirectoryKind(declaredArtifact.DirectoryArtifact).IsDynamicKind() 
                        ? declaredArtifact.Path 
                        : AbsolutePath.Invalid;
                    // Need to pass the executable bit for the fileArtifact.
                    // This code path can be invoked when we try to hash the dependencies of the source files during the start step of the pip.
                    // In such cases we are yet to hash the source files. Hence it is not possible to get the right results using the TryGetInputContent method.
                    var isExecutable = FileUtilities.CheckForExecutePermission(fileArtifact.Path.ToString(Context.PathTable));
                    Possible<ContentDiscoveryResult> possiblyDiscovered =
                        await LocalDiskContentStore.TryDiscoverAsync(fileArtifact, artifactExpandedPath, outputDirectoryRoot: outputDirectoryRoot, isExecutable: isExecutable.Result);

                    DiscoveredContentHashOrigin origin;
                    if (possiblyDiscovered.Succeeded)
                    {
                        fileTrackedHash = possiblyDiscovered.Result.TrackedFileContentInfo;
                        origin = possiblyDiscovered.Result.Origin;
                    }
                    else
                    {
                        // We may fail to access a file due to permissions issues, or due to some other process that has the file locked (opened for writing?)
                        var ex = possiblyDiscovered.Failure.CreateException();
                        Logger.Log.FailedToHashInputFile(
                            operationContext,
                            GetAssociatedPipDescription(declaredArtifact, consumerDescription),
                            artifactFullPath,
                            ex.LogEventErrorCode,
                            ex.LogEventMessage);
                        return null;
                    }

                    switch (origin)
                    {
                        case DiscoveredContentHashOrigin.NewlyHashed:
                            if (fileArtifact.IsSourceFile)
                            {
                                Interlocked.Increment(ref m_stats.SourceFilesHashed);
                            }
                            else
                            {
                                Interlocked.Increment(ref m_stats.OutputFilesHashed);
                            }

                            if (ETWLogger.Log.IsEnabled(EventLevel.Verbose, Keywords.Diagnostics))
                            {
                                Logger.Log.StorageHashedSourceFile(operationContext, artifactFullPath, fileTrackedHash.Hash.ToHex());
                            }

                            break;
                        case DiscoveredContentHashOrigin.Cached:
                            if (fileArtifact.IsSourceFile)
                            {
                                Interlocked.Increment(ref m_stats.SourceFilesUnchanged);
                            }
                            else
                            {
                                Interlocked.Increment(ref m_stats.OutputFilesUnchanged);
                            }

                            if (ETWLogger.Log.IsEnabled(BuildXL.Tracing.Diagnostics.EventLevel.Verbose, Keywords.Diagnostics))
                            {
                                Logger.Log.StorageUsingKnownHashForSourceFile(operationContext, artifactFullPath, fileTrackedHash.Hash.ToHex());
                            }

                            break;
                        default:
                            throw Contract.AssertFailure("Unhandled DiscoveredContentHashOrigin");
                    }
                }
                else if (possibleProbeResult.Result == PathExistence.ExistsAsDirectory)
                {
                    // Attempted to query the hash of a directory
                    // Case 1: For declared source files when TreatDirectoryAsAbsentFileOnHashingInputContent=true, we treat them as absent file hash
                    // Case 2: For declared source files when TreatDirectoryAsAbsentFileOnHashingInputContent=false, we return null and error
                    // Case 3: For other files (namely paths under sealed source directories or outputs), we return null. Outputs will error. Paths under
                    // sealed source directories will be handled by ObservedInputProcessor which will treat them as Enumeration/DirectoryProbe.
                    if (fileArtifact.IsSourceFile && declaredArtifact.IsFile)
                    {
                        // Declared source file
                        if (Configuration.Schedule.TreatDirectoryAsAbsentFileOnHashingInputContent)
                        {
                            // Case 1:
                            fileTrackedHash = TrackedFileContentInfo.CreateUntrackedWithUnknownLength(WellKnownContentHashes.AbsentFile, possibleProbeResult.Result);
                        }
                        else
                        {
                            // Case 2:
                            // Log error because this can indicate a bug in the specification. This causes an issue later, and so
                            // it's better to log an error immediately instead of "delaying" it with a warning.
                            Logger.Log.FailedToHashInputFileBecauseTheFileIsDirectory(
                                operationContext,
                                GetAssociatedPipDescription(declaredArtifact, consumerDescription),
                                artifactFullPath);

                            // This should error
                            return null;
                        }
                    }
                    else
                    {
                        // Case 3:
                        // Path under sealed source directory
                        // Caller will not error since this is a valid operation. ObservedInputProcessor will later discover that this is a directory
                        return null;
                    }
                }
                else
                {
                    Interlocked.Increment(ref m_stats.SourceFilesAbsent);
                    fileTrackedHash = TrackedFileContentInfo.CreateUntrackedWithUnknownLength(WellKnownContentHashes.AbsentFile, possibleProbeResult.Result);
                }

                if (ETWLogger.Log.IsEnabled(EventLevel.Verbose, Keywords.Diagnostics))
                {
                    if (fileArtifact.IsSourceFile)
                    {
                        Logger.Log.ScheduleHashedSourceFile(operationContext, artifactFullPath, fileTrackedHash.Hash.ToHex());
                    }
                    else
                    {
                        Logger.Log.ScheduleHashedOutputFile(
                            operationContext,
                            GetAssociatedPipDescription(declaredArtifact, consumerDescription),
                            artifactFullPath,
                            fileTrackedHash.Hash.ToHex());
                    }
                }

                return fileTrackedHash;
            }
        }

        private FileRealizationMode GetFileRealizationMode(bool allowReadOnly)
        {
            return Configuration.Engine.UseHardlinks && allowReadOnly
                ? FileRealizationMode.HardLinkOrCopy // Prefers hardlinks, but will fall back to copying when creating a hard link fails. (e.g. >1023 links)
                : FileRealizationMode.Copy;
        }

        private void UpdateOutputContentStats(PipOutputOrigin origin)
        {
            switch (origin)
            {
                case PipOutputOrigin.UpToDate:
                    Interlocked.Increment(ref m_stats.OutputsUpToDate);
                    break;
                case PipOutputOrigin.DeployedFromCache:
                    Interlocked.Increment(ref m_stats.OutputsDeployed);
                    break;

                case PipOutputOrigin.Produced:
                    Interlocked.Increment(ref m_stats.OutputsProduced);
                    break;

                case PipOutputOrigin.NotMaterialized:
                    break;

                default:
                    throw Contract.AssertFailure("Unhandled PipOutputOrigin");
            }
        }

        private bool ReportContent(
            FileArtifact fileArtifact,
            in FileMaterializationInfo fileMaterializationInfo,
            PipOutputOrigin origin,
            bool contentMismatchErrorsAreWarnings = false)
        {
            SetFileArtifactContentHashResult result = SetFileArtifactContentHash(
                fileArtifact,
                fileMaterializationInfo,
                origin);

            // Notify the host with content that was reported
            m_host.ReportContent(fileArtifact, fileMaterializationInfo, origin);

            if (result == SetFileArtifactContentHashResult.Added)
            {
                return true;
            }

            if (result == SetFileArtifactContentHashResult.HasMatchingExistingEntry)
            {
                return false;
            }

            Contract.Equals(SetFileArtifactContentHashResult.HasConflictingExistingEntry, result);

            var existingInfo = m_fileArtifactContentHashes[fileArtifact];
            if (!Configuration.Sandbox.UnsafeSandboxConfiguration.UnexpectedFileAccessesAreErrors || contentMismatchErrorsAreWarnings)
            {
                // If we reached this case and UnexpectedFileAccessesAreErrors is false or
                // pip level option doubleWriteErrorsAreWarnings is set to true, that means
                // the flag supressed a double write violation detection. So let's just warn
                // and move on.
                Logger.Log.FileArtifactContentMismatch(
                    m_host.LoggingContext,
                    fileArtifact.Path.ToString(Context.PathTable),
                    existingInfo.Hash.ToHex(),
                    fileMaterializationInfo.Hash.ToHex());

                return false;
            }

            throw Contract.AssertFailure(I($"Content hash of file artifact '{fileArtifact.Path.ToString(Context.PathTable)}:{fileArtifact.RewriteCount}' can be set multiple times, but only with the same content hash (old hash: {existingInfo.Hash.ToHex()}, new hash: {fileMaterializationInfo.Hash.ToHex()})"));
        }

        private enum VirtualizationState
        {
            /// <summary>
            /// No state is set
            /// </summary>
            PendingVirtual,

            /// <summary>
            /// File will be hydrated by outstanding materialization. The materialization
            /// which transitions the file to this state from Virtual is responsible for hydration.
            /// </summary>
            PendingHydration,

            /// <summary>
            /// An outstanding materialization will materialize the file as full (i.e. non-virtual) file
            /// </summary>
            PendingFullMaterialization,

            /// <summary>
            /// File is fully materialized
            /// </summary>
            FullMaterialized,

            /// <summary>
            /// File is materialized as a virtual file
            /// </summary>
            Virtual,

            /// <summary>
            /// File is hydrated from virtual state
            /// </summary>
            Hydrated
        }

        private enum SetFileArtifactContentHashResult
        {
            /// <summary>
            /// Found entry with differing content hash
            /// </summary>
            HasConflictingExistingEntry,

            /// <summary>
            /// Found entry with the same content hash
            /// </summary>
            HasMatchingExistingEntry,

            /// <summary>
            /// New entry was added with the given content hash
            /// </summary>
            Added,
        }

        /// <summary>
        /// Records the given file artifact as having the given content hash.
        /// </summary>
        private SetFileArtifactContentHashResult SetFileArtifactContentHash(
            FileArtifact fileArtifact,
            in FileMaterializationInfo fileMaterializationInfo,
            PipOutputOrigin origin)
        {
            Contract.Requires(fileArtifact.IsValid, "Argument fileArtifact must be valid");
            AssertFileNamesMatch(Context, fileArtifact, fileMaterializationInfo);

            var result = m_fileArtifactContentHashes.GetOrAdd(fileArtifact, fileMaterializationInfo);
            if (result.IsFound)
            {
                FileContentInfo storedFileContentInfo = result.Item.Value.FileContentInfo;
                if (storedFileContentInfo.Hash != fileMaterializationInfo.Hash)
                {
                    // We allow the same hash to be reported multiple times, but only with the same content hash.
                    return SetFileArtifactContentHashResult.HasConflictingExistingEntry;
                }

                if (storedFileContentInfo.HasKnownLength &&
                    fileMaterializationInfo.FileContentInfo.HasKnownLength &&
                    storedFileContentInfo.Length != fileMaterializationInfo.Length)
                {
                    Contract.Assert(false,
                        $"File length mismatch for file '{fileMaterializationInfo.FileName}' :: " +
                        $"arg = {{ hash: {fileMaterializationInfo.Hash.ToHex()}, length: {fileMaterializationInfo.Length} }}, " +
                        $"stored = {{ hash: {storedFileContentInfo.Hash.ToHex()}, length: {storedFileContentInfo.Length}, serializedLength: {storedFileContentInfo.SerializedLengthAndExistence}, existence: '{storedFileContentInfo.Existence}' }}");
                }
            }

            bool added = !result.IsFound;
            var contentId = new ContentId(result.Index);
            var contentSetItem = new ContentIdSetItem(contentId, fileMaterializationInfo.Hash, this);

            bool? isNewContent = null;
            if (origin != PipOutputOrigin.NotMaterialized)
            {
                // Mark the file as materialized
                // Due to StoreNoOutputToCache, we need to update the materialization task.
                // For copy file pip, with StoreNoOutputToCache enabled, BuildXL first tries to materialize the output, but
                // because the output is most likely not in the cache, TryLoadAvailableContent will fail and subsequently
                // will set the materialization task for the output file to NotMaterialized.
                var originAsTask = ToTask(origin);

                var addOrUpdateResult = m_materializationTasks.AddOrUpdate(
                    fileArtifact,
                    ToTask(origin),
                    (f, newOrigin) => newOrigin,
                    (f, newOrigin, oldOrigin) => oldOrigin.Result == PipOutputOrigin.NotMaterialized ? newOrigin : oldOrigin);

                if (!addOrUpdateResult.IsFound || addOrUpdateResult.OldItem.Value.Result == PipOutputOrigin.NotMaterialized)
                {
                    EnsureHashMappedToMaterializedFile(contentSetItem, isNewContent: out isNewContent);
                }

                m_host.ReportMaterializedArtifact(fileArtifact);
            }

            if (added)
            {
                if (fileMaterializationInfo.FileContentInfo.HasKnownLength &&
                    (isNewContent ?? m_allCacheContentHashes.AddItem(contentSetItem)))
                {
                    if (fileArtifact.IsOutputFile)
                    {
                        Interlocked.Add(ref m_stats.TotalCacheSizeNeeded, fileMaterializationInfo.Length);
                    }
                }

                ExecutionLog?.FileArtifactContentDecided(new FileArtifactContentDecidedEventData
                {
                    FileArtifact = fileArtifact,
                    FileContentInfo = fileMaterializationInfo.FileContentInfo,
                    OutputOrigin = origin,
                });

                return SetFileArtifactContentHashResult.Added;
            }

            return SetFileArtifactContentHashResult.HasMatchingExistingEntry;
        }

        /// <summary>
        /// Gets whether a file artifact is materialized
        /// </summary>
        private bool IsFileMaterialized(FileArtifact file)
        {
            Task<PipOutputOrigin> materializationResult;
            return m_materializationTasks.TryGetValue(file, out materializationResult)
                    && materializationResult.IsCompleted
                    && materializationResult.Result != PipOutputOrigin.NotMaterialized;
        }

        private bool AllowFileReadOnly(FileArtifact file)
        {
            Contract.Requires(file.IsValid);

            // File can be a dynamic output. First get the declared artifact.
            FileOrDirectoryArtifact declaredArtifact = GetDeclaredArtifact(file);
            return m_host.AllowArtifactReadOnly(declaredArtifact);
        }

        private bool IsPreservedOutputFile(Pip pip, bool materializingOutput, FileArtifact file)
        {
            Contract.Requires(file.IsValid);

            if (Configuration.Sandbox.UnsafeSandboxConfiguration.PreserveOutputs == PreserveOutputsMode.Disabled)
            {
                return false;
            }

            if (pip is Process process && Configuration.Sandbox.UnsafeSandboxConfiguration.PreserveOutputsTrustLevel > process.PreserveOutputsTrustLevel)
            {
                return false;
            }

            if (!materializingOutput)
            {
                // File can be a dynamic output. First get the declared artifact.
                FileOrDirectoryArtifact declaredArtifact = GetDeclaredArtifact(file);
                return m_host.IsPreservedOutputArtifact(declaredArtifact);
            }

            var pipId = m_host.TryGetProducerId(file);
            // If the producer is invalid, the file is a dynamic output under a directory output.
            // As the pip did not run yet and sealContents is not populated, we cannot easily get 
            // declared directory artifact for the given dynamic file.

            return PipArtifacts.IsPreservedOutputByPip(pip, file, Context.PathTable, Configuration.Sandbox.UnsafeSandboxConfiguration.PreserveOutputsTrustLevel, isDynamicFileOutput: !pipId.IsValid);
        }

        /// <summary>
        /// Adds the mapping of hash to file into <see cref="m_allCacheContentHashes"/>
        /// </summary>
        private void EnsureHashMappedToMaterializedFile(ContentIdSetItem materializedFileContentItem, out bool? isNewContent)
        {
            // Update m_allCacheContentHashes to point to the content hash for a materialized
            // file (used to recover content which cannot be materialized because it is not available in
            // the cache by storing the content from the materialized file).
            // Only updated if current value does not point to materialized file to avoid
            // lock contention with lots of files with the same content (not known to be a problem
            // but this logic is added as a precaution)
            var hashAddResult = m_allCacheContentHashes.GetOrAddItem(materializedFileContentItem);
            isNewContent = !hashAddResult.IsFound;
            if (hashAddResult.IsFound)
            {
                // Only update m_allCacheContentHashes if there was already an entry
                // and it was not materialized
                FileArtifact contentFile = hashAddResult.Item.GetFile(this);
                if (!IsFileMaterialized(contentFile))
                {
                    m_allCacheContentHashes.UpdateItem(materializedFileContentItem);
                }
            }
        }

        /// <summary>
        /// Attempts to get the file artifact which has the given hash (if any)
        /// </summary>
        /// <remarks>
        /// If the file artifact is a dynamic output, its opaque directory root is also returned. Otherwise, an invalid path is returned.
        /// </remarks>
        private (FileArtifact fileArtifact, AbsolutePath opaqueDirectoryRoot) TryGetFileArtifactForHash(ContentHash hash)
        {
            ContentId contentId;
            if (m_allCacheContentHashes.TryGetItem(new ContentIdSetItem(hash, this), out contentId))
            {
                var (file, materializationInfo) = contentId.GetFileAndMaterializationInfo(this);
                if (IsFileMaterialized(file) || m_host.CanMaterializeFile(file))
                {
                    return (file, materializationInfo.OpaqueDirectoryRoot);
                }
            }

            return (FileArtifact.Invalid, AbsolutePath.Invalid);
        }

        private void LogOutputOrigin(OperationContext operationContext, long pipSemiStableHash, AbsolutePath absPath, PathTable pathTable, in FileMaterializationInfo info, PipOutputOrigin origin, string additionalInfo = null)
        {
            if (origin == PipOutputOrigin.NotMaterialized && !ETWLogger.Log.IsEnabled(EventLevel.Verbose, Keywords.Diagnostics))
            {
                return;
            }

            string path = absPath.ToString(pathTable);
            string hashHex = info.Hash.ToHex();
            string pipDescription = Pip.FormatSemiStableHash(pipSemiStableHash);
            var reparseInfo = info.ReparsePointInfo.IsActionableReparsePoint ? info.ReparsePointInfo.ToString() : string.Empty;
            if (additionalInfo == null)
            {
                additionalInfo = reparseInfo;
            }
            else if (!string.IsNullOrEmpty(reparseInfo))
            {
                additionalInfo = string.Join(" ", additionalInfo, reparseInfo);
            }

            if (info.IsExecutable)
            {
                additionalInfo = string.Join(" ", additionalInfo, "IsExecutable:true");
            }

            switch (origin)
            {
                case PipOutputOrigin.Produced:
                    Logger.Log.SchedulePipOutputProduced(operationContext, pipDescription, path, hashHex, additionalInfo);
                    break;

                case PipOutputOrigin.UpToDate:
                    Logger.Log.SchedulePipOutputUpToDate(operationContext, pipDescription, path, hashHex, additionalInfo);
                    break;

                case PipOutputOrigin.NotMaterialized:
                    Logger.Log.SchedulePipOutputNotMaterialized(operationContext, pipDescription, path, hashHex, additionalInfo);
                    break;

                default:
                    Contract.Assert(origin == PipOutputOrigin.DeployedFromCache, "Unhandled PipOutputOrigin");
                    Logger.Log.SchedulePipOutputDeployedFromCache(operationContext, pipDescription, path, hashHex, additionalInfo);
                    break;
            }

            UpdateOutputContentStats(origin);
        }

        private static void AssertFileNamesMatch(PipExecutionContext context, FileArtifact fileArtifact, in FileMaterializationInfo fileMaterializationInfo)
        {
            Contract.Requires(fileArtifact.IsValid);
            
            if (!fileMaterializationInfo.FileName.IsValid)
            {
                return;
            }

            PathAtom fileArtifactFileName = fileArtifact.Path.GetName(context.PathTable);
            if (!fileMaterializationInfo.FileName.CaseInsensitiveEquals(context.StringTable, fileArtifactFileName))
            {
                string fileArtifactPathString = fileArtifact.Path.ToString(context.PathTable);
                string fileMaterializationFileNameString = fileMaterializationInfo.FileName.ToString(context.StringTable);
                Contract.Assert(
                    false,
                    I($"File name should only differ by casing. File artifact's full path: '{fileArtifactPathString}'; file artifact's file name: '{fileArtifactFileName.ToString(context.StringTable)}'; materialization info file name: '{fileMaterializationFileNameString}'"));
            }
        }

        /// <summary>
        /// Reports schedule stats that are relevant at the completion of a build.
        /// </summary>
        public void LogStats(LoggingContext loggingContext)
        {
            Logger.Log.StorageCacheContentHitSources(loggingContext, m_cacheContentSource);

            Dictionary<string, long> statistics = new Dictionary<string, long> { { Statistics.TotalCacheSizeNeeded, m_stats.TotalCacheSizeNeeded } };

            Logger.Log.CacheTransferStats(
                loggingContext,
                tryBringContentToLocalCacheCounts: Volatile.Read(ref m_stats.TryBringContentToLocalCache),
                artifactsBroughtToLocalCacheCounts: Volatile.Read(ref m_stats.ArtifactsBroughtToLocalCache),
                totalSizeArtifactsBroughtToLocalCache: ((double)Volatile.Read(ref m_stats.TotalSizeArtifactsBroughtToLocalCache)) / (1024 * 1024));

            Logger.Log.SourceFileHashingStats(
                loggingContext,
                sourceFilesHashed: Volatile.Read(ref m_stats.SourceFilesHashed),
                sourceFilesUnchanged: Volatile.Read(ref m_stats.SourceFilesUnchanged),
                sourceFilesUntracked: Volatile.Read(ref m_stats.SourceFilesUntracked),
                sourceFilesAbsent: Volatile.Read(ref m_stats.SourceFilesAbsent));

            Logger.Log.OutputFileHashingStats(
                loggingContext,
                outputFilesHashed: Volatile.Read(ref m_stats.OutputFilesHashed),
                outputFilesUnchanged: Volatile.Read(ref m_stats.OutputFilesUnchanged));

            statistics.Add(Statistics.OutputFilesChanged, m_stats.OutputFilesHashed);
            statistics.Add(Statistics.OutputFilesUnchanged, m_stats.OutputFilesUnchanged);

            statistics.Add(Statistics.SourceFilesChanged, m_stats.SourceFilesHashed);
            statistics.Add(Statistics.SourceFilesUnchanged, m_stats.SourceFilesUnchanged);
            statistics.Add(Statistics.SourceFilesUntracked, m_stats.SourceFilesUntracked);
            statistics.Add(Statistics.SourceFilesAbsent, m_stats.SourceFilesAbsent);

            Logger.Log.OutputFileStats(
                loggingContext,
                outputFilesNewlyCreated: Volatile.Read(ref m_stats.OutputsProduced),
                outputFilesDeployed: Volatile.Read(ref m_stats.OutputsDeployed),
                outputFilesUpToDate: Volatile.Read(ref m_stats.OutputsUpToDate));

            statistics.Add(Statistics.OutputFilesProduced, m_stats.OutputsProduced);
            statistics.Add(Statistics.OutputFilesCopiedFromCache, m_stats.OutputsDeployed);
            statistics.Add(Statistics.OutputFilesUpToDate, m_stats.OutputsUpToDate);

            int numDirectoryArtifacts = m_sealContents.Count;
            long numFileArtifacts = 0;
            foreach (var kvp in m_sealContents)
            {
                numFileArtifacts += kvp.Value.Length;
            }

            statistics.Add("FileContentManager_SealContents_NumDirectoryArtifacts", numDirectoryArtifacts);
            statistics.Add("FileContentManager_SealContents_NumFileArtifacts", numFileArtifacts);

            statistics.Add(Statistics.TotalMaterializedInputsSize, m_stats.TotalMaterializedInputsSize);
            statistics.Add(Statistics.TotalMaterializedOutputsSize, m_stats.TotalMaterializedOutputsSize);
            statistics.Add(Statistics.TotalMaterializedInputsCount, m_stats.TotalMaterializedInputsCount);
            statistics.Add(Statistics.TotalMaterializedOutputsCount, m_stats.TotalMaterializedOutputsCount);
            statistics.Add(Statistics.TotalMaterializedInputsExpensiveCount, m_stats.TotalMaterializedInputsExpensiveCount);
            statistics.Add(Statistics.TotalMaterializedOutputsExpensiveCount, m_stats.TotalMaterializedOutputsExpensiveCount);
            statistics.Add(Statistics.TotalApiServerMaterializedOutputsCount, m_stats.TotalApiServerMaterializedOutputsCount);
            statistics.Add(Statistics.TotalApiServerMaterializedOutputsSize, m_stats.TotalApiServerMaterializedOutputsSize);

            BuildXL.Tracing.Logger.Log.BulkStatistic(loggingContext, statistics);
        }

        private Task<PipOutputOrigin> ToTask(PipOutputOrigin origin)
        {
            return m_originTasks[(int)origin];
        }

        /// <summary>
        /// Returns paths that
        /// 1) FileContentManager was asked to materialize,
        /// 2) were not known at the time at the time of a materialization request, and
        /// 3) are currently known.
        /// </summary>
        internal List<AbsolutePath> GetPathsRegisteredAfterMaterializationCall()
        {
            var pathsWithAvailableArtifacts = new List<AbsolutePath>();

            foreach (var path in m_pathsWithoutFileArtifact.UnsafeGetList())
            {
                if (m_sealedFiles.ContainsKey(path))
                {
                    // m_sealedFiles now contains the path, but the path was not there when FileContentManager
                    // received TryMaterializeSealedFileAsync(AbsolutePath) call.
                    pathsWithAvailableArtifacts.Add(path);
                }
            }

            return pathsWithAvailableArtifacts;
        }

        /// <summary>
        /// A content id representing an index into <see cref="m_fileArtifactContentHashes"/> referring
        /// to a file and hash
        /// </summary>
        private readonly struct ContentId
        {
            public static readonly ContentId Invalid = new ContentId(-1);

            public bool IsValid => FileArtifactContentHashesIndex >= 0;

            public readonly int FileArtifactContentHashesIndex;

            public ContentId(int fileArtifactContentHashesIndex)
            {
                FileArtifactContentHashesIndex = fileArtifactContentHashesIndex;
            }

            private KeyValuePair<FileArtifact, FileMaterializationInfo> GetEntry(FileContentManager manager)
            {
                Contract.Assert(FileArtifactContentHashesIndex >= 0);
                return manager
                    .m_fileArtifactContentHashes
                    .BackingSet[FileArtifactContentHashesIndex];
            }

            public ContentHash GetHash(FileContentManager manager)
            {
                FileMaterializationInfo info = GetEntry(manager).Value;
                return info.Hash;
            }

            public FileArtifact GetFile(FileContentManager manager)
            {
                return GetEntry(manager).Key;
            }

            public (FileArtifact, FileMaterializationInfo) GetFileAndMaterializationInfo(FileContentManager manager)
            {
                var entry = GetEntry(manager);
                return (entry.Key, entry.Value);
            }
        }

        /// <summary>
        /// Wrapper for adding a content id (index into <see cref="m_fileArtifactContentHashes"/>) to a concurrent
        /// big set keyed by hash and also for looking up content id by hash.
        /// </summary>
        private readonly struct ContentIdSetItem : IPendingSetItem<ContentId>
        {
            private readonly ContentId m_contentId;
            private readonly FileContentManager m_manager;
            private readonly ContentHash m_hash;

            public ContentIdSetItem(ContentId contentId, ContentHash hash, FileContentManager manager)
            {
                m_contentId = contentId;
                m_manager = manager;
                m_hash = hash;
            }

            public ContentIdSetItem(ContentHash hash, FileContentManager manager)
            {
                m_contentId = ContentId.Invalid;
                m_manager = manager;
                m_hash = hash;
            }

            public int HashCode => m_hash.GetHashCode();

            public ContentId CreateOrUpdateItem(ContentId oldItem, bool hasOldItem, out bool remove)
            {
                remove = false;
                Contract.Assert(m_contentId.IsValid);
                return m_contentId;
            }

            public bool Equals(ContentId other)
            {
                return m_hash.Equals(other.GetHash(m_manager));
            }
        }

        private struct MaterializationFile
        {
            public readonly FileArtifact Artifact;
            public readonly FileMaterializationInfo MaterializationInfo;
            public readonly bool AllowReadOnly;
            public readonly TaskSourceSlim<PipOutputOrigin> MaterializationCompletion;
            public Task PriorArtifactVersionCompletion;
            public string VirtualizationInfo;

            public bool CreateReparsePoint => MaterializationInfo.ReparsePointInfo.IsActionableReparsePoint == true;

            public MaterializationFile(
                FileArtifact artifact,
                FileMaterializationInfo materializationInfo,
                bool allowReadOnly,
                TaskSourceSlim<PipOutputOrigin> materializationCompletion,
                Task priorArtifactVersionCompletion)
            {
                Artifact = artifact;
                MaterializationInfo = materializationInfo;
                AllowReadOnly = allowReadOnly;
                MaterializationCompletion = materializationCompletion;
                PriorArtifactVersionCompletion = priorArtifactVersionCompletion;
                VirtualizationInfo = null;
            }
        }

        /// <summary>
        /// Pooled state used by hashing and materialization operations
        /// </summary>
        private sealed class PipArtifactsState : IDisposable
        {
            private readonly FileContentManager m_manager;

            public PipArtifactsState(FileContentManager manager) => m_manager = manager;

            /// <summary>
            /// The pip info for the materialization operation
            /// </summary>
            public PipInfo PipInfo { get; set; }

            /// <summary>
            /// Indicates whether content is materialized for verification purposes only
            /// </summary>
            public bool VerifyMaterializationOnly { get; set; }

            /// <summary>
            /// Indicates whether the operation is materializing outputs
            /// </summary>
            public bool MaterializingOutputs { get; set; }

            /// <summary>
            /// Indicates pip is the declared producer for the materialized files
            /// </summary>
            public bool IsDeclaredProducer { get; set; }

            /// <summary>
            /// Indicates whether the operation is triggered by an API Server
            /// </summary>
            public bool IsApiServerRequest { get; set; }

            /// <summary>
            /// If set, ensures that exclusion roots are applied to the content of directory artifacts
            /// </summary>
            public bool EnforceOutputMaterializationExclusionRootsForDirectoryArtifacts { get; set; }

            /// <summary>
            /// If set, specifies that file virtualization is active for the materialization
            /// </summary>
            public bool Virtualize { get; set; }

            /// <summary>
            /// The overall output origin result for materializing outputs
            /// </summary>
            public PipOutputOrigin OverallOutputOrigin { get; private set; } = PipOutputOrigin.NotMaterialized;

            /// <summary>
            /// The materialized paths (files and directories) in all materialized directories. The boolean represents isDirectory info.
            /// </summary>
            public readonly Dictionary<AbsolutePath, bool> MaterializedDirectoryContents = new();

            /// <summary>
            /// All the artifacts to process
            /// </summary>
            public readonly HashSet<FileOrDirectoryArtifact> PipArtifacts = new();

            /// <summary>
            /// The completion results for directory deletions
            /// </summary>
            public readonly List<(DirectoryArtifact, bool, TaskSourceSlim<bool>)> DirectoryDeletionCompletions = new();

            /// <summary>
            /// Required directory deletions initiated by other pips which must be awaited
            /// </summary>
            public readonly List<Task<bool>> PendingDirectoryDeletions = new();

            /// <summary>
            /// The set of files to materialize
            /// </summary>
            public readonly List<MaterializationFile> MaterializationFiles = new();

            /// <summary>
            /// The set of virtual files to hydrate
            /// </summary>
            public readonly List<AbsolutePath> HydrationFiles = new();

            /// <summary>
            /// The paths and content hashes for files in <see cref="MaterializationFiles"/>
            /// </summary>
            private readonly List<(MaterializationFile, int)> m_filesAndContentHashes = new();

            /// <summary>
            /// The tasks for hashing files
            /// </summary>
            public readonly List<Task<FileMaterializationInfo?>> HashTasks = new();

            /// <summary>
            /// Materialization tasks initiated by other pips which must be awaited
            /// </summary>
            public readonly List<(FileArtifact fileArtifact, Task<PipOutputOrigin> tasks)> PendingPlacementTasks = new();

            /// <summary>
            /// Materialization tasks initiated by the current pip
            /// </summary>
            public readonly List<Task> PlacementTasks = new();

            /// <summary>
            /// Files which failed to materialize
            /// </summary>
            public readonly List<(FileArtifact, ContentHash)> FailedFiles = new();

            /// <summary>
            /// Directories which failed to materialize
            /// </summary>
            private readonly List<DirectoryArtifact> m_failedDirectories = new();

            /// <nodoc />
            public Failure InnerFailure = null;

            /// <summary>
            /// Get the content hashes for <see cref="MaterializationFiles"/>
            /// </summary>
            public IReadOnlyList<(MaterializationFile materializationFile, int index)> GetCacheMaterializationFiles()
            {
                m_filesAndContentHashes.Clear();
                for (int i = 0; i < MaterializationFiles.Count; i++)
                {
                    var file = MaterializationFiles[i];
                    if (!(file.CreateReparsePoint || file.MaterializationInfo.IsReparsePointActionable) && !m_manager.m_host.CanMaterializeFile(file.Artifact))
                    {
                        m_filesAndContentHashes.Add((file, i));
                    }
                }

                return m_filesAndContentHashes;
            }

            public void Dispose()
            {
                PipInfo = null;

                VerifyMaterializationOnly = false;
                MaterializingOutputs = false;
                IsDeclaredProducer = false;
                IsApiServerRequest = false;
                EnforceOutputMaterializationExclusionRootsForDirectoryArtifacts = false;
                Virtualize = false;

                OverallOutputOrigin = PipOutputOrigin.NotMaterialized;
                MaterializedDirectoryContents.Clear();
                PipArtifacts.Clear();
                DirectoryDeletionCompletions.Clear();
                PendingDirectoryDeletions.Clear();
                MaterializationFiles.Clear();
                HydrationFiles.Clear();

                m_filesAndContentHashes.Clear();

                HashTasks.Clear();
                PendingPlacementTasks.Clear();
                PlacementTasks.Clear();
                FailedFiles.Clear();
                m_failedDirectories.Clear();

                InnerFailure = null;

                m_manager.m_statePool.Enqueue(this);
            }

            /// <summary>
            /// Gets the materialization failure. NOTE: This should only be called when the materialization result is not successful
            /// </summary>
            public ArtifactMaterializationFailure GetFailure()
            {
                Contract.Assert(FailedFiles.Count != 0 || m_failedDirectories.Count != 0);
                return new ArtifactMaterializationFailure(FailedFiles.ToReadOnlyArray(), m_failedDirectories.ToReadOnlyArray(), m_manager.m_host.Context.PathTable, InnerFailure);
            }

            /// <summary>
            /// Set the materialization result for the file to failure
            /// </summary>
            public void SetMaterializationFailure(int fileIndex)
            {
                var failedFile = MaterializationFiles[fileIndex];
                AddFailedFile(failedFile.Artifact, failedFile.MaterializationInfo.FileContentInfo.Hash);

                SetMaterializationResult(fileIndex, success: false);
            }

            /// <summary>
            /// Adds a failed file
            /// </summary>
            public void AddFailedFile(FileArtifact file, ContentHash contentHash)
            {
                lock (FailedFiles)
                {
                    FailedFiles.Add((file, contentHash));
                }
            }

            /// <summary>
            /// Adds a failed directory
            /// </summary>
            public void AddFailedDirectory(DirectoryArtifact directory)
            {
                lock (m_failedDirectories)
                {
                    m_failedDirectories.Add(directory);
                }
            }

            /// <summary>
            /// Sets virtualization info logged when file is materialized
            /// </summary>
            public void SetVirtualizationInfo(int fileIndex, string virtualizationInfo)
            {
                var materializationFile = MaterializationFiles[fileIndex];
                materializationFile.VirtualizationInfo = virtualizationInfo;
                MaterializationFiles[fileIndex] = materializationFile;
            }

            /// <summary>
            /// Ensures that the materialization of the specified file is not started until after the given
            /// completion of an artifact dependency (i.e. host materialized files).
            /// </summary>
            public void SetDependencyArtifactCompletion(int fileIndex, Task dependencyArtifactCompletion)
            {
                var materializationFile = MaterializationFiles[fileIndex];
                var priorArtifactCompletion = materializationFile.PriorArtifactVersionCompletion;
                if (priorArtifactCompletion != null && !priorArtifactCompletion.IsCompleted)
                {
                    // Wait for prior artifact and the dependency artifact before attempting materialization
                    priorArtifactCompletion = Task.WhenAll(dependencyArtifactCompletion, priorArtifactCompletion);
                }
                else
                {
                    // No outstanding prior artifact, just wait for the dependency artifact before attempting materialization
                    priorArtifactCompletion = dependencyArtifactCompletion;
                }

                materializationFile.PriorArtifactVersionCompletion = priorArtifactCompletion;
                MaterializationFiles[fileIndex] = materializationFile;
            }

            /// <summary>
            /// Set the materialization result for the file to success with the given <see cref="PipOutputOrigin"/>
            /// </summary>
            public void SetMaterializationSuccess(int fileIndex, ContentMaterializationOrigin origin, OperationContext operationContext)
            {
                PipOutputOrigin result = origin.ToPipOutputOrigin();

                if (!VerifyMaterializationOnly)
                {
                    MaterializationFile materializationFile = MaterializationFiles[fileIndex];
                    var file = materializationFile.Artifact;
                    if (file.IsOutputFile &&
                        (IsDeclaredProducer || m_manager.TryGetDeclaredProducerId(file).IsValid))
                    {
                        var producer = IsDeclaredProducer ? PipInfo.UnderlyingPip : m_manager.GetDeclaredProducer(file);

                        result = GetPipOutputOrigin(origin, producer);
                        var producerSemiStableHash = IsDeclaredProducer
                            ? PipInfo.SemiStableHash
                            : producer.SemiStableHash;

                        m_manager.LogOutputOrigin(
                            operationContext,
                            producerSemiStableHash,
                            file.Path,
                            m_manager.Context.PathTable,
                            materializationFile.MaterializationInfo,
                            result,
                            materializationFile.VirtualizationInfo);

                        // Notify the host that output content for a pip was materialized
                        // NOTE: This is specifically for use when materializing outputs
                        // to preserve legacy behavior for tests.
                        m_manager.m_host.ReportContent(file, materializationFile.MaterializationInfo, result);
                    }
                }

                SetMaterializationResult(fileIndex, success: true, result: result);
            }

            private void SetMaterializationResult(int materializationFileIndex, bool success, PipOutputOrigin result = PipOutputOrigin.NotMaterialized)
            {
                Contract.Requires(result != PipOutputOrigin.NotMaterialized || !success, "Successfully materialization cannot have NotMaterialized result");
                MaterializationFile file = MaterializationFiles[materializationFileIndex];
                file.MaterializationCompletion.SetResult(result);

                if (!VerifyMaterializationOnly)
                {
                    // Normalize to task results to shared cached pip origin tasks to save memory
                    m_manager.m_materializationTasks[file.Artifact] = m_manager.ToTask(result);
                }

                m_manager.m_currentlyMaterializingFilesByPath.CompareRemove(file.Artifact.Path, file.Artifact);
                MergeResult(result);
            }

            /// <summary>
            /// Combines the individual result with <see cref="OverallOutputOrigin"/>
            /// </summary>
            public void MergeResult(PipOutputOrigin result)
            {
                lock (this)
                {
                    // Merged result is result with highest precedence
                    OverallOutputOrigin =
                        GetMergePrecedence(result) > GetMergePrecedence(OverallOutputOrigin)
                            ? result
                            : OverallOutputOrigin;
                }
            }

            private static int GetMergePrecedence(PipOutputOrigin origin)
            {
                switch (origin)
                {
                    case PipOutputOrigin.Produced:
                        // Takes precedence over all other results. Producing any content
                        // means the pip result is produced
                        return 3;
                    case PipOutputOrigin.DeployedFromCache:
                        // Pip result is deployed from cache if its outputs are up to date or deployed
                        // deployed from cache
                        return 2;
                    case PipOutputOrigin.UpToDate:
                        // Pip result is only up to date if all its outputs are up to date
                        return 1;
                    case PipOutputOrigin.NotMaterialized:
                        return 0;
                    default:
                        throw Contract.AssertFailure(I($"Unexpected PipOutputOrigin: {origin}"));
                }
            }

            /// <summary>
            /// Adds a file to be materialized
            /// </summary>
            public void AddMaterializationFile(
                FileArtifact fileToMaterialize,
                bool allowReadOnly,
                in FileMaterializationInfo materializationInfo,
                TaskSourceSlim<PipOutputOrigin> materializationCompletion)
            {
                Contract.Assert(PipInfo != null, "PipInfo must be set to materialize files");

                var result = m_manager.m_currentlyMaterializingFilesByPath.AddOrUpdate(
                    fileToMaterialize.Path,
                    fileToMaterialize,
                    (path, file) => file,
                    (path, file, oldFile) => file.RewriteCount > oldFile.RewriteCount
                        ? file
                        : oldFile);

                Task priorArtifactCompletion = Unit.VoidTask;

                // Only materialize the file if it is the latest version
                bool isLatestVersion = result.Item.Value == fileToMaterialize;

                if (isLatestVersion)
                {
                    if (result.IsFound && result.OldItem.Value != fileToMaterialize)
                    {
                        priorArtifactCompletion = m_manager.m_materializationTasks[result.OldItem.Value];
                    }

                    // Populate collections with corresponding information for files
                    MaterializationFiles.Add(new MaterializationFile(
                        fileToMaterialize,
                        materializationInfo,
                        allowReadOnly,
                        materializationCompletion,
                        priorArtifactCompletion));
                }
                else
                {
                    // File is not materialized because it is not the latest file version
                    materializationCompletion.SetResult(PipOutputOrigin.NotMaterialized);
                }
            }

            /// <summary>
            /// Remove completed materializations
            /// </summary>
            public void RemoveCompletedMaterializations()
            {
                MaterializationFiles.RemoveAll(file => file.MaterializationCompletion.Task.IsCompleted);
            }
        }

        /// <summary>
        /// Failure returned by operations that are not performed due to ctrl-c cancellation of the build
        /// </summary>
        public class CtrlCCancellationFailure : Failure
        {
            private const string Message = "Operation failed because build was cancelled";

            /// <inheritdoc/>
            public override BuildXLException CreateException()
            {
                return new BuildXLException(Message);
            }

            /// <inheritdoc/>
            public override string Describe()
            {
                return Message;
            }

            /// <inheritdoc/>
            public override BuildXLException Throw()
            {
                throw new BuildXLException(Message);
            }
        }

        private enum AddFileMaterializationBehavior
        {
            /// <summary>
            /// Do not materialize a file
            /// </summary>
            Skip,

            /// <summary>
            /// Check that the file's hash matches the hash known by the FileContentManager.
            /// </summary>
            Verify,

            /// <summary>
            /// Materialize a file
            /// </summary>
            Materialize,
        }
    }
}
