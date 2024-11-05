// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Engine.Cache.Fingerprints;
using BuildXL.Native.IO;
using BuildXL.Pips;
using BuildXL.Pips.Operations;
using BuildXL.Processes;
using BuildXL.ProcessPipExecutor;
using BuildXL.Scheduler.Artifacts;
using BuildXL.Scheduler.FileSystem;
using BuildXL.Scheduler.Tracing;
using BuildXL.Storage;
using BuildXL.Storage.Fingerprints;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Debugging;
using BuildXL.Utilities.Tracing;
using static BuildXL.Utilities.Core.FormattableStringEx;

#pragma warning disable 1591 // disabling warning about missing API documentation; TODO: Remove this line and write documentation!

namespace BuildXL.Scheduler.Fingerprints
{
    using DirectoryMemberEntry = ValueTuple<AbsolutePath, string>;

    /// <summary>
    /// An <see cref="ObservedInputProcessor"/> is a generic means from promoting some 'observation' (such as file
    /// accesses traced from detours) into a set of <see cref="ObservedInput"/>s (in a <see cref="ObservedInputProcessingResult"/>).
    /// The processor access-checks the observations (is the process allowed to access the particular file?) and also provides a hook
    /// for comparing the observation with a proposed <see cref="ObservedInput"/>.
    /// From the produced <see cref="ObservedInputProcessingResult"/>, one can derive a <see cref="BuildXL.Engine.Cache.Fingerprints.StrongContentFingerprint"/>
    /// and <see cref="ObservedPathSet"/> pair, which are the basis for the second phase of 'two-phase' fingerprint lookup.
    ///
    /// Processing is parameterized on a 'target' type, which must be a struct implementing <see cref="IObservedInputProcessingTarget{TObservation}"/>.
    /// A target must be able to project its observation type into a path (since common processing operates on the observed path).
    /// In response to access-check failures etc. and <see cref="ObservedInput"/> proposals, a target may choose how to respond; for example,
    /// a target responsible for processing observed file-accesses (from a real process) may log warnings and blame a tool, whereas a target
    /// responsible for cache-lookup may fail quietly (maybe logging miss-reason quietly).
    ///
    /// Of note is that a variety of targets allow observed input processing to be in common among various fingerprinting approaches, and between
    /// cache-lookup vs. runtime-enforcement:
    /// * Single-phase fingerprinting:
    ///   - Enforcement: / Storage: A just-executed process may generate warnings or errors if accesses are disallowed.
    ///                  The resulting observed inputs are stored directly in a cache descriptor.
    ///   - Lookup: The observed inputs in a cache descriptor are (re)processed to see if they are still permissible and if they agree on content.
    /// * Two-phase fingerprinting:
    ///   - Enforcement / Storage: Like single-phase, but the result is used to generate a path-set and strong fingerprint pair to store with.
    ///   - Lookup: Like single-phase, but a prior <see cref="ObservedPathSet"/> is used to generate a strong fingerprint to look up.
    /// </summary>
    /// <remarks>
    /// Note that the target type parameter is constrained to <see cref="IObservedInputProcessingTarget{TObservation}"/> and also
    /// <c>struct</c>. Since the JIT specializes for any value-type instantiation, this means we get static-dispatch, inlining, dead-code
    /// elimination, etc. rather than interface calls or (worse still!) calls to lambda closures.
    /// </remarks>
    public static class ObservedInputProcessor
    {
        private static readonly Task<FileMaterializationInfo?> s_nullFileMaterializationInfoTask = Task.FromResult<FileMaterializationInfo?>(null);

        /// <summary>
        /// Processes the paths in an <see cref="ObservedPathSet"/>. In two-phase lookup, this is used to derive a strong fingerprint
        /// corresponding to the path-set (lookup side).
        /// </summary>
        public static async Task<ObservedInputProcessingResult> ProcessPriorPathSetAsync<TTarget>(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state,
            TTarget target,
            CacheablePipInfo pip,
            ObservedPathSet pathSet)
            where TTarget : struct, IObservedInputProcessingTarget<ObservedPathEntry>
        {
            using (var environmentAdapter = new ObservedInputProcessingEnvironmentAdapter(environment, state))
            {
                return await ProcessInternalAsync(
                    operationContext,
                    environmentAdapter,
                    target,
                    pip,
                    pathSet.Paths.BaseArray,
                    // When processing a prior path set, shared opaques are already flagged as such, and therefore
                    // there are not non-populated ones
                    unPopulatedSharedOpaqueOutputs: null,
                    pathSet.ObservedAccessedFileNames,
                    isCacheLookup: true);
            }
        }

        /// <summary>
        /// Processes observed file accesses. This is used in both two-phase and single-phase fingerprinting, since both
        /// run processes the same way. Single-phase will store the resulting <see cref="ObservedInput"/>s,
        /// whereas two-phase fingerprinting will derive an <see cref="ObservedPathSet"/> and <see cref="BuildXL.Engine.Cache.Fingerprints.StrongContentFingerprint"/> pair.
        /// </summary>
        public static async Task<ObservedInputProcessingResult> ProcessNewObservationsAsync<TTarget>(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state,
            TTarget target,
            CacheablePipInfo pip,
            ReadOnlyArray<ObservedFileAccess> accesses,
            [AllowNull] IReadOnlyDictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>> sharedDynamicDirectoryWriteAccesses,
            bool trackFileChanges = true)
            where TTarget : struct, IObservedInputProcessingTarget<ObservedFileAccess>
        {
            using (var environmentAdapter = new ObservedInputProcessingEnvironmentAdapter(environment, state))
            {
                return await ProcessInternalAsync(
                    operationContext,
                    environmentAdapter,
                    target,
                    pip,
                    accesses,
                    sharedDynamicDirectoryWriteAccesses,
                    default(SortedReadOnlyArray<StringId, CaseInsensitiveStringIdComparer>),
                    isCacheLookup: false,
                    trackFileChanges: trackFileChanges);
            }
        }

        /// <summary>
        /// Actual processing logic for any <see cref="IObservedInputProcessingEnvironment"/> and any <see cref="IObservedInputProcessingTarget{TObservation}"/>.
        /// This method is left internal only for testing (tests may provide a fully mocked environment, preventing observation of the real filesystem).
        /// This implementation requires that the <paramref name="observations"/> array is sorted by each entry's expanded
        /// path. Observations for duplicate paths are permitted (the observations themselves may be distinct due to other members).
        /// </summary>
        internal static async Task<ObservedInputProcessingResult> ProcessInternalAsync<TTarget, TEnv, TObservation>(
            OperationContext operationContext,
            TEnv environment,
            TTarget target,
            CacheablePipInfo pip,
            ReadOnlyArray<TObservation> observations,
            [AllowNull] IReadOnlyDictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>> unPopulatedSharedOpaqueOutputs,
            SortedReadOnlyArray<StringId, CaseInsensitiveStringIdComparer> observedAccessedFileNames,
            bool isCacheLookup,
            bool trackFileChanges = true)
            where TTarget : struct, IObservedInputProcessingTarget<TObservation>
            where TEnv : IObservedInputProcessingEnvironment
        {
            Contract.Requires(!isCacheLookup ^ observedAccessedFileNames.IsValid);

            var envAdapter = environment as ObservedInputProcessingEnvironmentAdapter;
            var traceEnabled = EngineEnvironmentSettings.ObservedInputProcessorTracingEnabled 
                               || environment.PipProperties?.PipHasProperty(PipSpecificPropertiesConfig.PipSpecificProperty.ObservedInputProcessorTracing, pip.SemiStableHash) == true;
            using var debugTraces = new DebugTraceArray(enabled: traceEnabled, count: observations.Length);
            using (operationContext.StartOperation(PipExecutorCounter.ObservedInputProcessorProcessInternalDuration))
            using (var processingState = ObservedInputProcessingState.GetInstance())
            {
                var pathTable = environment.Context.PathTable;
                PathTable.ExpandedAbsolutePathComparer pathComparer = pathTable.ExpandedPathComparer;
                ReadOnlyArray<DirectoryArtifact> directoryDependencies = pip.DirectoryDependencies;

                int numAbsentPathsEliminated = 0;

                var sourceDirectoriesAllDirectories = processingState.SourceDirectoriesAllDirectories;
                var sourceDirectoriesTopDirectoryOnly = processingState.SourceDirectoriesTopDirectoryOnly;
                var dynamicObservations = processingState.DynamicObservations;
                var allowedUndeclaredSourceReads = processingState.AllowedUndeclaredReads;
                var directoryDependencyContentsFilePaths = processingState.DirectoryDependencyContentsFilePaths;
                var enumeratedDirectories = processingState.EnumeratedDirectories;
                var searchPaths = processingState.SearchPaths;

                var bitSetSize = BitSet.RoundToValidBitCount(observations.Length);
                var observationsUnderSourceSealDirectories = new BitSet(bitSetSize);
                observationsUnderSourceSealDirectories.SetLength(bitSetSize);
                var outOfSourceSealObservations = new BitSet(bitSetSize);
                outOfSourceSealObservations.SetLength(bitSetSize);
                HashSet<AbsolutePath> possiblyBadAccesses;
                HashSet<HierarchicalNameId> pipFileSystemViewPathIds = null;

                var allowUndeclaredSourceReads = pip.UnderlyingPip.ProcessAllowsUndeclaredSourceReads;

                // Compute the set of all shared dynamic outputs. This is only done if there is a chance we end up using MinimalGraphWithAlienFiles 
                // for this pip, otherwise we keep the set empty to avoid unnecessary computations
                Dictionary<AbsolutePath, bool> sharedOpaqueOutputs = processingState.SharedOpaqueOutputs;

                if (unPopulatedSharedOpaqueOutputs != null && envAdapter.MayDetermineMinimalGraphWithAlienFiles(allowUndeclaredSourceReads))
                {
                    // Let's distinguish rewrites vs not. We want rewritten files to be part of the fingerprint (those files
                    // existed before the build begun) so we could exclude them from the just-produced outputs we pass to the 
                    // fingerprint enumeration computation, but on the other hand we need to know about them for the creation time
                    // check
                    sharedOpaqueOutputs.AddRange(unPopulatedSharedOpaqueOutputs.Values.SelectMany(fileArtifacts =>
                        fileArtifacts.Select(fa => new KeyValuePair<AbsolutePath, bool>(fa.Path, fa.IsUndeclaredFileRewrite))));
                }

                using (operationContext.StartOperation(PipExecutorCounter.ObservedInputProcessorPreProcessDuration))
                {
                    using (operationContext.StartOperation(PipExecutorCounter.ObservedInputProcessorPreProcessListDirectoriesDuration))
                    {
                        for (int i = 0; i < directoryDependencies.Length; i++)
                        {
                            var directoryDependency = directoryDependencies[i];
                            var listDirectoryContents = environment.ListSealedDirectoryContents(directoryDependency);

                            if (!listDirectoryContents.BaseArray.IsValid)
                            {
                                // TODO: Debugging for a one-off crash.
                                Contract.Assume(listDirectoryContents.BaseArray.IsValid, I($"Environment failed to retrieve directory contents for directory:'{directoryDependency.Path.ToString(pathTable)}'. Directory dependency IsSharedOpaque:{directoryDependency.IsSharedOpaque}. PartialSealId:{directoryDependency.PartialSealId}. Base Array was invalid but SortedReadOnlyArray was valid."));
                            }

                            if (!listDirectoryContents.IsValid)
                            {
                                // TODO: Debugging for a one-off crash that we aren't sure still exists.
                                Contract.Assume(listDirectoryContents.IsValid, I($"Environment failed to retrieve directory contents for directory:'{directoryDependency.Path.ToString(pathTable)}'. Directory dependency IsSharedOpaque:{directoryDependency.IsSharedOpaque}. PartialSealId:{directoryDependency.PartialSealId} "));
                            }

                            directoryDependencyContentsFilePaths.UnionWith(listDirectoryContents.Select(f => f.Path));
                            if (environment.IsSourceSealedDirectory(directoryDependency, out var allDirectories, out var patterns))
                            {
                                if (allDirectories)
                                {
                                    sourceDirectoriesAllDirectories.Add(new SourceSealWithPatterns(directoryDependency.Path, patterns, false));
                                }
                                else
                                {
                                    sourceDirectoriesTopDirectoryOnly.Add(new SourceSealWithPatterns(directoryDependency.Path, patterns, true));
                                }
                            }
                        }
                    }

                    // We have observed path accesses, but need to match them up to file artifacts.
                    // We can do this with OrdinalPathOnlyFileArtifactComparer and arbitrary write counts
                    // TODO: This is a very silly conversion. Should re-implement the needed comparer things in a new IBinaryComparer<TLeft, TRight>
                    //       and make a FileArtifact - AbsolutePath comparer.
                    var observationArtifacts = processingState.ObservationArtifacts;

                    using (operationContext.StartOperation(PipExecutorCounter.ObservedInputProcessorPreProcessValidateSealSourceDuration))
                    {
                        for (int i = 0; i < observations.Length; i++)
                        {
                            AbsolutePath path = target.GetPathOfObservation(observations[i]);
                            if (directoryDependencyContentsFilePaths.Contains(path))
                            {
                                // Path is explicitly mentioned in a seal directory dependency's contents
                                // no need to check if it is under a source seal directory
                                continue;
                            }

                            // Check to see if the observation is under a topDirectoryOnly Sealed Source Directories
                            bool underSealedSource = false;
                            for (int j = 0; j < sourceDirectoriesTopDirectoryOnly.Count && !underSealedSource; j++)
                            {
                                var sourceSealWithPatterns = sourceDirectoriesTopDirectoryOnly[j];
                                if (sourceSealWithPatterns.Contains(pathTable, path))
                                {
                                    underSealedSource = true;
                                    observationsUnderSourceSealDirectories.Add(i);
                                }
                                else if (path == sourceSealWithPatterns.Path)
                                {
                                    // Consider the sealed directory itself as a dynamic observation as this likely
                                    // is an enumeration we want to capture for caching
                                    observationsUnderSourceSealDirectories.Add(i);
                                }
                            }

                            // Check to see if the observation is under one of the AllDirectory Sealed Source Directories
                            for (int j = 0; j < sourceDirectoriesAllDirectories.Count && !underSealedSource; j++)
                            {
                                var sourceSealWithPatterns = sourceDirectoriesAllDirectories[j];
                                if (sourceSealWithPatterns.Contains(pathTable, path))
                                {
                                    // Note the directories themselves are never part of the seal.
                                    underSealedSource = true;
                                    observationsUnderSourceSealDirectories.Add(i);
                                }
                                else if (path == sourceSealWithPatterns.Path)
                                {
                                    // Consider the sealed directory itself as a dynamic observation as this likely
                                    // is an enumeration we want to capture for caching
                                    observationsUnderSourceSealDirectories.Add(i);
                                }
                            }

                            if (!underSealedSource)
                            {
                                observationArtifacts.Add(path);
                            }
                        }
                    }

                    // Note that we validated the sort order of 'observations' above w.r.t expanded order, but need
                    // a fake artifacts sorted ordinally.
                    // TODO: We can remove this step if ListSealedDirectoryContents is changed to returned expansion-sorted results.
                    possiblyBadAccesses = observationArtifacts;
                }

                // Processed results.
                var observationInfos = new ObservationInfo[observations.Length];
                ObservedInput[] observedInputs = new ObservedInput[observations.Length];
                ObservedInputProcessingStatus status = ObservedInputProcessingStatus.Success;
                int valid = 0;
                int invalid = 0;

                bool minimalGraphUsed = false;
                // Do the processing in 2 passes.
                // First pass: obtain paths and start all hashing tasks.
                // Second pass: Do the actual processing.
                // Having 2 passes allows us to avoid Parallel.ForEach with all required locking.
                using (operationContext.StartOperation(PipExecutorCounter.ObservedInputProcessorPass1InitializeObservationInfosDuration))
                {
                    for (int i = 0; i < observations.Length; i++)
                    {
                        observationInfos[i] = GetObservationInfo(environment, target, observations[i], allowUndeclaredSourceReads, alwaysGetInputContent: false);
                    }
                }

                ObservedInputType[] observationTypes = new ObservedInputType[observations.Length];

                // Observations which fail access checks are suppressed (i.e. they do not contribute to
                // the fingerprint).
                bool[] isUnsuppressedObservation = new bool[observations.Length];

                int numAbsentPathProbes = 0, numFileContentReads = 0, numDirectoryEnumerations = 0;
                int numExistingDirectoryProbes = 0, numExistingFileProbes = 0;

                using (operationContext.StartOperation(PipExecutorCounter.ObservedInputProcessorPass2ProcessObservationInfosDuration))
                {
                    // Start the second pass
                    for (int i = 0; i < observations.Length; i++)
                    {
                        TObservation observation = observations[i];
                        ObservationInfo observationInfo = observationInfos[i];
                        AbsolutePath path = observationInfo.Path;
                        ObservationFlags observationFlags = observationInfo.ObservationFlags;
                        FileArtifact fakeArtifact = FileArtifact.CreateSourceFile(path);
                        FileMaterializationInfo? fileMaterializationInfo;
                        FileContentInfo? pathContentInfo;
                        HashSet<AbsolutePath> directoryDependenciesLookup = null;
                        var debugTrace = debugTraces[i];

                        debugTrace.AppendLine($"Starting second pass for {path.ToString(pathTable)}. ObservationFlags = {observationFlags}");

                        using (operationContext.StartOperation(PipExecutorCounter.ObservedInputProcessorTryQuerySealedInputContentDuration, fakeArtifact))
                        {
                            fileMaterializationInfo = await observationInfo.FileMaterializationInfoTask;
                            pathContentInfo = fileMaterializationInfo?.FileContentInfo;
                        }

                        // TODO: Don't use UntrackedFile for this...
                        if (pathContentInfo.HasValue && !pathContentInfo.Value.HasKnownLength &&
                            pathContentInfo.Value.Hash == WellKnownContentHashes.UntrackedFile)
                        {
                            // This is a HashSourceFile failure.
                            var mountInfo = environment.PathExpander.GetSemanticPathInfo(path);
                            Logger.Log.AbortObservedInputProcessorBecauseFileUntracked(
                                operationContext,
                                pip.Description,
                                path.ToString(pathTable),
                                mountInfo.RootName.IsValid ?
                                    mountInfo.RootName.ToString(environment.Context.StringTable) :
                                    "N/A");

                            status = CombineObservedInputProcessingStatus(status, ObservedInputProcessingStatus.Aborted);
                            invalid++;
                            continue;
                        }

                        bool wasPossiblyBad = possiblyBadAccesses.Contains(FileArtifact.CreateSourceFile(path));
                        debugTrace.AppendLine($"Was possibly bad: {wasPossiblyBad}");

                        if (!allowUndeclaredSourceReads)
                        {
                            // We do not hash the files that have been probed, so we skip the following validation for the file probes.
                            if (!pathContentInfo.HasValue && !wasPossiblyBad && !observationsUnderSourceSealDirectories.Contains(i) && observationFlags.IsHashingRequired())
                            {
                                Contract.Assume(
                                    false,
                                    "Observation is either a file or a directory found to be under a seal directory, although the file may not exist physically, " +
                                    "or possibly bad access (probing or reading a file that is not specified as a dependency), or possibly directory enumeration. " +
                                    (debugTrace.Enabled ? ("[DebugTrace: " + debugTrace.ToString() + "]") : "") +
                                    GetDiagnosticsInfo(path, pip, pathTable, directoryDependencyContentsFilePaths, sourceDirectoriesTopDirectoryOnly, sourceDirectoriesAllDirectories, observationFlags));
                            }
                        }

                        // TODO: Right now we check TryQuerySealedOrUndeclaredInputContent, and then the VFS if that fails (we assume the two are in agreement!)
                        //       Consider combining responsibilities so that the VFS can additionally provide per-file
                        //       state info, such as (Existence: File, State: Sealed, Hash: 123).
                        // pathContentInfo may be set to AbsentFile for a directory if TreatDirectoryAsAbsentFileOnHashingInputContent flag is specified.
                        // We need to ensure that we recognize it is a directory so the observed input shows up as a directory enumeration
                        // We do this by examining the Existence of the pathContentInfo which will be set by the FileContentManager to see if the path represents
                        // a directory
                        ObservedInputType type;

                        if (pathContentInfo.HasValue && pathContentInfo.Value.Existence != PathExistence.ExistsAsDirectory)
                        {
                            // Path content info may be an absent file, for example, a sealed directory includes a non-existent file.
                            type = pathContentInfo.Value.Hash == WellKnownContentHashes.AbsentFile
                                ? ObservedInputType.AbsentPathProbe
                                : ObservedInputType.FileContentRead;

                            debugTrace.AppendLine($"pathContentInfo.HasValue && pathContentInfo.Value.Existence != PathExistence.ExistsAsDirectory. Type: {type}");
                        }
                        else
                        {
                            // We tried to find FileContentInfo for the accessed path, but failed. This means that the 1) path is not part of a sealed directory,
                            // but *may* be part of the pip graph (just not sealed) or 2) the path did not require hashing (e.g. a probe). How we proceed here is quite delicate:
                            // - We make decisions based on 'existence' of the path (including if it is a file or a directory).
                            // - If the path is known to the pip graph, we might not have materialized it yet.
                            // - If the path is not known to the pip graph, it might exist in a way that is visible to build processes (e.g. a file not added to a spec).
                            // Specific rules for how to determine existence are an implementation detail of the IObservedInputProcessingEnvironment defined below.
                            Possible<PathExistence> maybeType;
                            using (operationContext.StartOperation(PipExecutorCounter.ObservedInputProcessorTryProbeForExistenceDuration, fakeArtifact))
                            {
                                var cachePathExistence = trackFileChanges;

                                // We shouldn't cache the path existence when the path might be under a still-not-scrubbed 
                                // exclusive opaque, because the existence might change after the directory is materialized.
                                // This might be the case during cache lookup of a pip when lazy materialization is enabled,
                                // when considering the paths under the opaque directories that are depdendencies of the pip
                                // (and thus potentially not materialized yet).
                                //
                                // For a concrete example (see test: StaleOutputsFromLazyMaterializationDoNotCreateSpuriousViolations)
                                // Consider a graph PipA <--- PipB where PipA produces different contents under an opaque OD.
                                // First build (full build):
                                //      PipA : Produces OD\file1.txt
                                //      PipB: Probes file1.txt and file2.txt   [Pathset:  { (file1.txt, absent probe), (file2.txt: probe)} ]
                                // Second build (filter: PipA):
                                //      PipA [cache miss]: Produces OD\file2.txt
                                //
                                // Now a third build runs where PipA is a cache hit with the fingerprint of the first build (which would produce file1.txt).
                                // Because of lazy materialization, OD is not materialized at cache-lookup time of PipB.
                                // This means there is now a discrepancy between the real file system (which has file2.txt) and the outputs for the running build (file1.txt).
                                // If, when processing the pathset, we cache the real existences, we will cache "file2.txt => ExistsAsFile",
                                // which would not be true if lazy materialization were disabled.
                                // This means that when we process the observations on PipB, we see an existing probe on file2.txt, which is not part of the
                                // opaque in this build. This causes a violation, but note that if lazy materialization was disabled this would never be a violation,
                                // because the 'output' view of OD would always coincide with the real filesystem (because it was materialized with the correct contents).
                                //
                                // Similar scenarios would cause cache misses instead of violations. The point is that we need to avoid caching existences under opaques
                                // that might still need scrubbing. Note the similarity with the lazy shared opaque scrubbing correction we do in TryProbeAndTrackForExistence
                                // under the condition if (IsRecordedDynamicOutputForPip()) { ... }.
                                if (trackFileChanges
                                    && isCacheLookup
                                    && envAdapter.RequiredOutputMaterialization != RequiredOutputMaterialization.All)
                                {
                                    var maybeOpaqueParent = envAdapter.TryGetParentOutputDirectory(path);
                                    if (maybeOpaqueParent.HasValue && !maybeOpaqueParent.Value.IsItSharedOpaque)
                                    {
                                        directoryDependenciesLookup ??= new HashSet<AbsolutePath>(pip.DirectoryDependencies.Select(d => d.Path));
                                        cachePathExistence = !directoryDependenciesLookup.Contains(maybeOpaqueParent.Value.Parent);
                                    }
                                }

                                maybeType = environment.TryProbeAndTrackForExistence(
                                    path,
                                    pip,
                                    observationFlags,
                                    isReadOnly: observationsUnderSourceSealDirectories.Contains(i),
                                    cachePathExistence,
                                    debugTrace);
                            }

                            if (!maybeType.Succeeded)
                            {
                                Logger.Log.ScheduleFileAccessCheckProbeFailed(
                                    operationContext,
                                    pip.Description,
                                    path.ToString(pathTable),
                                    maybeType.Failure.DescribeIncludingInnerFailures());

                                ObservedInputAccessCheckFailureAction accessCheckFailureResult = target.OnAccessCheckFailure(
                                    observation,
                                    fromTopLevelDirectory: sourceDirectoriesTopDirectoryOnly.Any(a => a.Contains(pathTable, path, isTopDirectoryOnlyOverride: false)));
                                HandleFailureResult(accessCheckFailureResult, ref status, ref invalid);
                                continue;
                            }
                            else
                            {
                                type = MapPathExistenceToObservedInputType(
                                    pathTable,
                                    path,
                                    maybeType.Result,
                                    observationFlags,
                                    () => fileMaterializationInfo.HasValue || observationFlags.IsHashingRequired()
                                        ? fileMaterializationInfo
                                        : GetObservationInfo(environment, target, observation, allowUndeclaredSourceReads, alwaysGetInputContent: true)
                                            .FileMaterializationInfoTask.GetAwaiter().GetResult());
                                debugTrace.AppendLine($"Tried to find FileContentInfo for the accessed path, but failed. Tracked file existence: {trackFileChanges}. Observation flags {observationFlags}. Mapped path existence {maybeType.Result} to type: {type}");
                            }
                        }

                        debugTrace.AppendLine($"Resolved type before reclassification: {type}");
                        observationTypes[i] = type;

                        if (wasPossiblyBad)
                        {
                            if (allowUndeclaredSourceReads)
                            {
                                ObservedInputAccessCheckFailureAction undeclaredAccessCheckResult;

                                using (operationContext.StartOperation(PipExecutorCounter.ObservedInputProcessorOnAllowingUndeclaredAccessCheckDuration, fakeArtifact))
                                {
                                    undeclaredAccessCheckResult = target.OnAllowingUndeclaredAccessCheck(observation);
                                }

                                if (undeclaredAccessCheckResult == ObservedInputAccessCheckFailureAction.Fail)
                                {
                                    // Undeclared access doesn't match any entry in the allow list.
                                    allowedUndeclaredSourceReads.Add(path, type);
                                }
                                else
                                {
                                    Contract.Assert(undeclaredAccessCheckResult == ObservedInputAccessCheckFailureAction.SuppressAndIgnorePath);
                                    continue;
                                }
                            }
                            else if (type == ObservedInputType.FileContentRead || type == ObservedInputType.ExistingFileProbe)
                            {
                                debugTrace.AppendLine($"type == ObservedInputType.FileContentRead || type == ObservedInputType.ExistingFileProbe");

                                ObservedInputAccessCheckFailureAction accessCheckFailureResult;
                                using (operationContext.StartOperation(PipExecutorCounter.ObservedInputProcessorOnAccessCheckFailureDuration, fakeArtifact))
                                {
                                    accessCheckFailureResult = target.OnAccessCheckFailure(
                                        observation,
                                        fromTopLevelDirectory: sourceDirectoriesTopDirectoryOnly.Any(a => a.Contains(pathTable, path, isTopDirectoryOnlyOverride: false)));
                                }

                                HandleFailureResult(accessCheckFailureResult, ref status, ref invalid);
                                continue;
                            }
                            else if (target.IsReportableUnexpectedAccess(path))
                            {
                                // TODO: This is used by unit tests only. Need to be removed.

                                if (pipFileSystemViewPathIds == null)
                                {
                                    // Lazily populate pipFileSystemViewPathIds if there is at least one reportable unexpected access.
                                    pipFileSystemViewPathIds = processingState.AllDependencyPathIds;
                                    using (operationContext.StartOperation(OperationCounter.ObservedInputProcessorComputePipFileSystemPaths))
                                    {
                                        foreach (var p in directoryDependencyContentsFilePaths)
                                        {
                                            foreach (var pathId in pathTable.EnumerateHierarchyBottomUp(p.GetParent(pathTable).Value))
                                            {
                                                if (!pipFileSystemViewPathIds.Add(pathId))
                                                {
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }

                                if (!pipFileSystemViewPathIds.Contains(path.Value))
                                {
                                    using (operationContext.StartOperation(OperationCounter.ObservedInputProcessorReportUnexpectedAccess))
                                    {
                                        // Pip does not contain the path in its file system view (as file or directory)
                                        // Report to target but don't fail as only unexpected FileContentRead currently fails input
                                        // processing. This is a policy decision which may be changed at some point. At which point,
                                        // this code would need to be updated.

                                        target.ReportUnexpectedAccess(observation, type);
                                    }
                                }
                            }
                        }

                        isUnsuppressedObservation[i] = true;

                    }
                }

                DirectoryMembershipFilter searchPathFilter;
                using (operationContext.StartOperation(PipExecutorCounter.ObservedInputProcessorComputeSearchPathsAndFilterDuration))
                {
                    searchPathFilter = ComputeSearchPathsAndFilter(
                                            ref observedAccessedFileNames,
                                            environment.Context.PathTable,
                                            pip,
                                            target,
                                            observations,
                                            observationTypes,
                                            isUnsuppressedObservation,
                                            searchPaths);
                }

                using (operationContext.StartOperation(PipExecutorCounter.ObservedInputProcessorPass3ProcessObservationInfosDuration))
                {
                    AbsolutePath lastAbsentPath = AbsolutePath.Invalid;

                    // Third and final pass
                    // Initialize the observation reclassifier for this pip, which will apply to all observations
                    var reclassificationRules = ((Process)pip.UnderlyingPip).ReclassificationRules; // yuck! but adding a field in the Pip itself (to avoid casting) is just as ugly
                    var reclassifierForPip = new ObservationReclassifier();
                    reclassifierForPip.Initialize(reclassificationRules, environment.Counters);

                    for (int i = 0; i < observations.Length; i++)
                    {
                        var debugTrace = debugTraces[i];
                        if (!isUnsuppressedObservation[i])
                        {
                            debugTrace.AppendLine($"!isUnsuppressedObservation --> continue");
                            continue;
                        }

                        TObservation observation = observations[i];
                        AbsolutePath path = observationInfos[i].Path;
                        var flags = observationInfos[i].ObservationFlags;

                        // Call GetAwaiter().GetResult() since we awaited above so we know the task has completed successfully
                        FileContentInfo? pathContentInfo = observationInfos[i].FileMaterializationInfoTask.GetAwaiter().GetResult()?.FileContentInfo;
                        var type = observationTypes[i];

                        if (type == ObservedInputType.FileContentRead && !pathContentInfo.HasValue)
                        {
                            var debugInfo = debugTrace.Enabled ? debugTrace.ToString() : string.Empty;
                            Contract.Assert(false, "If the access is a file content read, then the FileContentInfo cannot be null. Diagnostics:" + debugInfo +
                                GetDiagnosticsInfo(path, pip, pathTable, directoryDependencyContentsFilePaths, sourceDirectoriesTopDirectoryOnly, sourceDirectoriesAllDirectories, flags));
                        }

                        if (type == ObservedInputType.AbsentPathProbe)
                        {
                            // We need to iterate the observations in the order so that we can first visit the directory and then the child paths under that directory.
                            // CanIgnoreAbsentPathProbe relies on that assumption.
                            if (CanIgnoreAbsentPathProbe(environment, enumeratedDirectories, pathTable, path, lastAbsentPath, isCacheLookup))
                            {
                                numAbsentPathsEliminated++;
                                continue;
                            }
                        }

                        // Reclassify the observation if the user specified some matching rule
                        if (reclassifierForPip.TryReclassify(path, pathTable, type, out var reclassification)
                            || environment.GlobalReclassificationRules.TryReclassify(path, pathTable, type, out reclassification))
                        {
                            if (!reclassification.ReclassifyTo.HasValue)
                            {
                                // Observation must be ignored
                                Logger.Log.ObservationIgnored(
                                    operationContext,
                                    pip.Description,
                                    path.ToString(pathTable),
                                    reclassification.AppliedRuleName,
                                    type.ToString(),
                                    isCacheLookup);

                                continue;
                            }
                            else
                            {
                                // Observation must be reclassified
                                Logger.Log.ObservationReclassified(
                                    operationContext,
                                    pip.Description,
                                    path.ToString(pathTable),
                                    reclassification.AppliedRuleName,
                                    type.ToString(),
                                    reclassification.ToString(),
                                    isCacheLookup);

                                type = reclassification.ReclassifyTo.Value;
                            }
                        }

                        ObservedInput? maybeProposed;
                        switch (type)
                        {
                            case ObservedInputType.AbsentPathProbe:
                                maybeProposed = ObservedInput.CreateAbsentPathProbe(path, flags);

                                if (environment.IsPathUnderOutputDirectory(path)
                                    && !directoryDependencyContentsFilePaths.Contains(path))
                                {
                                    // Record that an absent file probe occurred under the root of a known output directory
                                    // We also exclude the probes of paths that we take a dependency on. This is done to cover the case where an upstream
                                    // pip produces an absent file (e.g., it produced and deleted a file), and a consuming pip probes this file.
                                    // The probe will be classified as AbsentFileProbe, and since there is a dependency between these pips, we allow the probe.
                                    dynamicObservations.Add((path, DynamicObservationKind.AbsentPathProbeUnderOutputDirectory));
                                }
                                else
                                {
                                    dynamicObservations.Add((path, DynamicObservationKind.AbsentPathProbeOutsideOutputDirectory));
                                }
                                break;
                            case ObservedInputType.FileContentRead:
                                if (pathContentInfo.Value.Hash.HashType == HashType.Unknown)
                                {
                                    throw Contract.AssertFailure($"Unknown content hash for path '{path.ToString(pathTable)}' and operation {type}");
                                }
                                maybeProposed = ObservedInput.CreateFileContentRead(path, pathContentInfo.Value.Hash);
                                dynamicObservations.Add((path, DynamicObservationKind.ObservedFile));
                                break;
                            case ObservedInputType.ExistingFileProbe:
                                maybeProposed = ObservedInput.CreateExistingFileProbe(path);
                                dynamicObservations.Add((path, DynamicObservationKind.ProbedFile));
                                break;
                            case ObservedInputType.ExistingDirectoryProbe:
                                // If the path is not under a mount or a path is under a non-hashable mount, we convert it into an AbsentPathProbe.
                                // This is in line with our treatment of directory enumerations that happen under invalid / non-hashable mounts.
                                // This conversion is not affected by AllowUndeclaredSourceReads setting.
                                // See: ObservedInputProcessingEnvironmentAdapter.DetermineEnumerationModeAndRule, and the code below that handles
                                // the DirectoryFingerprint.Zero fingerprint case.
                                SemanticPathInfo mountInfo = environment.PathExpander.GetSemanticPathInfo(path);
                                if (!mountInfo.IsValid || !mountInfo.AllowHashing)
                                {
                                    maybeProposed = ObservedInput.CreateAbsentPathProbe(path, flags);
                                }
                                else
                                {
                                    maybeProposed = ObservedInput.CreateExistingDirectoryProbe(path, flags);
                                }

                                // Directory probe is just like file probe.
                                // We are not changing this into an absent path probe to keep it in sync with how DirectoryEnumeration is treated.
                                dynamicObservations.Add((path, DynamicObservationKind.ProbedFile));
                                break;
                            case ObservedInputType.DirectoryEnumeration:
                                // TODO: TryQueryDirectoryFingerprint should be in agreement with the VirtualFileSystem somehow.
                                //       Right now, both independently make decisions based on path mountpoint.
                                DirectoryFingerprint? maybeDirectoryFingerprint;
                                bool isSearchPath = searchPaths.Contains(path);
                                string enumeratePatternRegex;
                                DirectoryEnumerationMode mode;

                                using (operationContext.StartOperation(
                                    PipExecutorCounter.ObservedInputProcessorTryQueryDirectoryFingerprintDuration,
                                    DirectoryArtifact.CreateWithZeroPartialSealId(path)))
                                {
                                    enumeratePatternRegex = target.GetEnumeratePatternRegex(observation);

                                    DirectoryMembershipFilter directoryFilter;

                                    if (enumeratePatternRegex == null)
                                    {
                                        // If enumeratePatternRegex is null, then isSearchPath must be true. However, for unit tests, that's not true.
                                        // That's why, I still keep the AllowAllFilter here.
                                        // TODO: Add an assertion here, Assert(isSearchPath).
                                        directoryFilter = isSearchPath ? searchPathFilter : DirectoryMembershipFilter.AllowAllFilter;
                                    }
                                    else
                                    {
                                        var enumeratePatternFilter = RegexDirectoryMembershipFilter.Create(enumeratePatternRegex);
                                        directoryFilter = isSearchPath ? enumeratePatternFilter.Union(searchPathFilter) : enumeratePatternFilter;
                                    }

                                    maybeDirectoryFingerprint = environment.TryQueryDirectoryFingerprint(
                                        path,
                                        pip,
                                        filter: directoryFilter,
                                        isReadOnlyDirectory: observationsUnderSourceSealDirectories.Contains(i),
                                        eventData: new DirectoryMembershipHashedEventData()
                                        {
                                            Directory = path,
                                            IsSearchPath = isSearchPath,
                                            EnumeratePatternRegex = enumeratePatternRegex
                                        },
                                        sharedOpaqueOutputs,
                                        environment.State.AlienFileEnumerationCache,
                                        enumerationMode: out mode,
                                        trackPathExistence: trackFileChanges);

                                    if (mode == DirectoryEnumerationMode.MinimalGraph)
                                    {
                                        minimalGraphUsed = true;
                                    }

                                    enumeratedDirectories.Add(path, (directoryFilter, mode));
                                }

                                if (maybeDirectoryFingerprint.HasValue)
                                {
                                    if (maybeDirectoryFingerprint == DirectoryFingerprint.Zero)
                                    {
                                        // We need to normalize 'empty' directories to look 'absent' since the determination of
                                        // directory vs. absent above is based on the *full graph* + real FS, but the
                                        // 'existential' VFS (either the pip scoped FS or real FS, depending on mount).
                                        // - A directory is in the global VFS, but not the existential VFS
                                        // - A directory is in neither.
                                        // Without some workaround, those two cases would cause oscillation between the Directory and Absent types respectively.
                                        // So, we canonicalize such that a path absent according to the existential FS becomes an absent probe regardless
                                        // TODO: We accomplish this for now by treating the null fingerprint specially; but this is kind of broken since that might mean "directory exists but empty", which can genuinely occur when looking at the real FS.
                                        maybeProposed = ObservedInput.CreateAbsentPathProbe(
                                            path,
                                            flags,
                                            isSearchPath: isSearchPath,
                                            enumeratePatternRegex: enumeratePatternRegex);
                                    }
                                    else
                                    {
                                        maybeProposed = ObservedInput.CreateDirectoryEnumeration(
                                            path,
                                            maybeDirectoryFingerprint.Value,
                                            isSearchPath: isSearchPath,
                                            enumeratePatternRegex: enumeratePatternRegex);
                                    }
                                }
                                else
                                {
                                    maybeProposed = null;

                                    // TODO: This shouldn't always be an error.
                                    Logger.Log.PipDirectoryMembershipFingerprintingError(
                                        operationContext,
                                        pip.Description,
                                        path.ToString(pathTable));
                                }

                                break;

                            default:
                                throw Contract.AssertFailure("Unreachable");
                        }

                        if (maybeProposed.HasValue)
                        {
                            ObservedInput proposed = maybeProposed.Value;

                            // This no longer has any function other than being a test hook;
                            target.CheckProposedObservedInput(observation, proposed);
                            observedInputs[valid++] = proposed;

                            if (!proposed.Path.IsValid)
                            {
                                Contract.Assume(proposed.Path.IsValid, "Created an ObservedInput with an invalid path in ObservedInputProcessor line 660. Type: " + proposed.Type.ToString());
                            }

                            switch (proposed.Type)
                            {
                                case ObservedInputType.AbsentPathProbe:
                                    numAbsentPathProbes++;
                                    break;
                                case ObservedInputType.DirectoryEnumeration:
                                    numDirectoryEnumerations++;
                                    break;
                                case ObservedInputType.ExistingDirectoryProbe:
                                    numExistingDirectoryProbes++;
                                    break;
                                case ObservedInputType.FileContentRead:
                                    numFileContentReads++;
                                    break;
                                case ObservedInputType.ExistingFileProbe:
                                    numExistingFileProbes++;
                                    break;
                                default:
                                    Contract.Assert(false, "Unknown ObservedInputType has been encountered: " + type);
                                    break;
                            }
                        }
                        else
                        {
                            status = CombineObservedInputProcessingStatus(status, ObservedInputProcessingStatus.Aborted);
                            invalid++;
                        }
                    }
                }

                if (minimalGraphUsed)
                {
                    environment.Counters.IncrementCounter(PipExecutorCounter.NumPipsUsingMinimalGraphFileSystem);
                }

                foreach (var path in enumeratedDirectories.Keys)
                {
                    dynamicObservations.Add((path, DynamicObservationKind.Enumeration));
                }

                environment.Counters.AddToCounter(PipExecutorCounter.NumAbsentPathsEliminated, numAbsentPathsEliminated);
                environment.Counters.AddToCounter(PipExecutorCounter.AbsentPathProbes, numAbsentPathProbes);
                environment.Counters.AddToCounter(PipExecutorCounter.DirectoryEnumerations, numDirectoryEnumerations);
                environment.Counters.AddToCounter(PipExecutorCounter.ExistingDirectoryProbes, numExistingDirectoryProbes);
                environment.Counters.AddToCounter(PipExecutorCounter.FileContentReads, numFileContentReads);
                environment.Counters.AddToCounter(PipExecutorCounter.ExistingFileProbes, numExistingFileProbes);

                if (status == ObservedInputProcessingStatus.Success)
                {
                    Contract.Assume(invalid == 0);
                    Contract.Assume(valid <= observedInputs.Length);
                    Contract.Assume(observedAccessedFileNames.IsValid);

                    if (valid != observedInputs.Length)
                    {
                        // We may have valid < observedInputs.Length due to SuppressAndIgnorePath, e.g. due to monitoring allowlists.
                        Array.Resize(ref observedInputs, valid);
                    }

                    // Note that we validated the sort order of 'observations', and 'observedInputs' is in an equivalent order.
                    return ObservedInputProcessingResult.CreateForSuccess(
                        observedInputs: SortedReadOnlyArray<ObservedInput, ObservedInputExpandedPathComparer>.FromSortedArrayUnsafe(
                            ReadOnlyArray<ObservedInput>.FromWithoutCopy(observedInputs),
                            new ObservedInputExpandedPathComparer(pathComparer)),
                        observedAccessedFileNames: observedAccessedFileNames,
                        dynamicObservations: ReadOnlyArray<(AbsolutePath, DynamicObservationKind)>.From(dynamicObservations),
                        allowedUndeclaredSourceReads: new Dictionary<AbsolutePath, ObservedInputType>(allowedUndeclaredSourceReads));
                }
                else
                {
                    if (valid != observedInputs.Length)
                    {
                        Array.Resize(ref observedInputs, valid);
                    }

                    Contract.Assume(invalid > 0);
                    return ObservedInputProcessingResult.CreateForFailure(
                        status: status,
                        observedInputs: SortedReadOnlyArray<ObservedInput, ObservedInputExpandedPathComparer>.FromSortedArrayUnsafe(
                            ReadOnlyArray<ObservedInput>.FromWithoutCopy(observedInputs),
                            new ObservedInputExpandedPathComparer(pathComparer)),
                        observedAccessedFileNames: observedAccessedFileNames,
                        numberOfValidEntries: valid,
                        numberOfInvalidEntries: invalid,
                        dynamicObservations: ReadOnlyArray<(AbsolutePath, DynamicObservationKind)>.From(dynamicObservations),
                        allowedUndeclaredSourceReads: new Dictionary<AbsolutePath, ObservedInputType>(allowedUndeclaredSourceReads));
                }
            }
        }

        private static bool CanIgnoreAbsentPathProbe<TEnv>(
            TEnv env,
            Dictionary<AbsolutePath, (DirectoryMembershipFilter, DirectoryEnumerationMode)> enumeratedDirectories,
            PathTable pathTable,
            AbsolutePath path,
            AbsolutePath lastAbsentPath,
            bool isCacheLookup)
            where TEnv : IObservedInputProcessingEnvironment
        {
            if (isCacheLookup)
            {
                return false;
            }

            (DirectoryMembershipFilter directoryMemberShipFilter, DirectoryEnumerationMode directoryEnumerationMode) tuple;

            AbsolutePath parent = path.GetParent(pathTable);
            if (enumeratedDirectories.TryGetValue(parent, out tuple)
                && (enumerationAllowsAbsentProbeElision(tuple))
                && tuple.directoryMemberShipFilter.Include(pathTable, path))
            {
                return true;
            }

            // Skip nested absent paths except the uppermost one
            return lastAbsentPath.IsValid && path.IsWithin(pathTable, lastAbsentPath);

            bool enumerationAllowsAbsentProbeElision((DirectoryMembershipFilter directoryMemberShipFilter, DirectoryEnumerationMode directoryEnumerationMode) info)
            {
                if (info.directoryEnumerationMode == DirectoryEnumerationMode.RealFilesystem)
                {
                    return true;
                }
                else if (info.directoryEnumerationMode == DirectoryEnumerationMode.MinimalGraph && env.Configuration.ElideMinimalGraphEnumerationAbsentPathProbes)
                {
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Temporary until fixing the subtle Bug #1016583, Bug #1016589 regarding processing observations in distributed builds.
        /// </summary>
        private static string GetDiagnosticsInfo(
            AbsolutePath path,
            CacheablePipInfo pip,
            PathTable pathTable,
            HashSet<AbsolutePath> sealContents,
            List<SourceSealWithPatterns> sourceDirectoriesTopDirectoryOnly,
            List<SourceSealWithPatterns> sourceDirectoriesAllDirectories,
            ObservationFlags flags)
        {
            bool isUnderSourceSeal = false;
            for (int j = 0; j < sourceDirectoriesTopDirectoryOnly.Count && !isUnderSourceSeal; j++)
            {
                var sourceDirectory = sourceDirectoriesTopDirectoryOnly[j].Path;
                if (path.GetParent(pathTable) == sourceDirectory)
                {
                    isUnderSourceSeal = true;
                }
            }

            for (int j = 0; j < sourceDirectoriesAllDirectories.Count && !isUnderSourceSeal; j++)
            {
                var sourceDirectory = sourceDirectoriesAllDirectories[j].Path;
                if (path != sourceDirectory && path.IsWithin(pathTable, sourceDirectory))
                {
                    isUnderSourceSeal = true;
                }
            }

            var isUnderSeal = sealContents.Contains(path);

            StringBuilder flagsStr = new StringBuilder();
            if ((flags & ObservationFlags.DirectoryLocation) != 0)
            {
                flagsStr.Append("DirectoryLocation,");
            }

            if ((flags & ObservationFlags.Enumeration) != 0)
            {
                flagsStr.Append("Enumeration,");
            }

            if ((flags & ObservationFlags.FileProbe) != 0)
            {
                flagsStr.Append("FileProbe,");
            }

            return I($"Path: {path.ToString(pathTable)} - PipDescription: {pip.Description} - IsUnderSourceSeal: {isUnderSourceSeal} - IsUnderSeal: {isUnderSeal} - Flags: {flagsStr.ToString()}");
        }

        /// <summary>
        /// Checks if a path is within any of a list of paths. This is not a very performant thing to do. make sure it stays
        /// in the error reporting path only
        /// </summary>
        private static bool IsPathWithinAny(PathTable pathTable, AbsolutePath path, IEnumerable<AbsolutePath> paths)
        {
            foreach (var item in paths)
            {
                if (path.IsWithin(pathTable, item))
                {
                    return true;
                }
            }

            return false;
        }

        private static ObservationInfo GetObservationInfo<TTarget, TEnv, TObservation>(
            TEnv environment,
            TTarget target,
            TObservation observation,
            bool allowUndeclaredSourceReads,
            bool alwaysGetInputContent)
            where TTarget : struct, IObservedInputProcessingTarget<TObservation>
            where TEnv : IObservedInputProcessingEnvironment
        {
            AbsolutePath path = target.GetPathOfObservation(observation);
            var flags = target.GetObservationFlags(observation);
            if (flags.IsHashingRequired() || alwaysGetInputContent)
            {
                var fileMaterializationInfoTask = environment.TryQuerySealedOrUndeclaredInputContent(path, target.Description, allowUndeclaredSourceReads);
                return new ObservationInfo(path, flags, fileMaterializationInfoTask);
            }

            // If flags have FileProbe, DirectoryLocation, or Enumeration, we do not need to hash the path.
            return new ObservationInfo(path, flags, s_nullFileMaterializationInfoTask);
        }

        /// <summary>
        /// Computes the set of paths which use search path enumerations and the accessed file name set under
        /// the search paths.
        /// </summary>
        /// <remarks>
        /// This method assumes that paths from observations and static dependencies are sorted (ie: a parent path will
        /// always appear before a child path), because of the visited set of paths is used by <see cref="AddAccessedFileNames"/>.
        /// </remarks>
        private static DirectoryMembershipFilter ComputeSearchPathsAndFilter<TTarget, TObservation>(
            ref SortedReadOnlyArray<StringId, CaseInsensitiveStringIdComparer> observedAccessedFileNames,
            PathTable pathTable,
            CacheablePipInfo pip,
            TTarget target,
            ReadOnlyArray<TObservation> observations,
            ObservedInputType[] observationTypes,
            bool[] isUnsuppressedObservation,
            HashSet<AbsolutePath> searchPaths)
            where TTarget : struct, IObservedInputProcessingTarget<TObservation>
        {
            using (var pooledFilePathSet = Pools.GetAbsolutePathSet())
            using (var pooledDirectoryPathSet = Pools.GetAbsolutePathSet())
            {
                HashSet<AbsolutePath> visitedFilePaths = pooledFilePathSet.Instance;
                HashSet<AbsolutePath> visitedDirectoryPaths = pooledDirectoryPathSet.Instance;

                HashSet<StringId> accessedFileNames = new HashSet<StringId>(pathTable.StringTable.CaseInsensitiveEqualityComparer);

                bool addFileNamesFromObservations = false;
                if (!observedAccessedFileNames.IsValid)
                {
                    addFileNamesFromObservations = true;
                }
                else
                {
                    foreach (var fileName in observedAccessedFileNames)
                    {
                        accessedFileNames.Add(fileName);
                    }
                }

                for (int i = 0; i < observations.Length; i++)
                {
                    if (!isUnsuppressedObservation[i])
                    {
                        continue;
                    }

                    TObservation observation = observations[i];
                    AbsolutePath path = target.GetPathOfObservation(observation);
                    var type = observationTypes[i];

                    bool isDirectoryEnumeration = type == ObservedInputType.DirectoryEnumeration;

                    if (isDirectoryEnumeration)
                    {
                        bool isSearchPathEnumeration = target.IsSearchPathEnumeration(observation);
                        if (isSearchPathEnumeration)
                        {
                            searchPaths.Add(path);
                        }
                    }

                    if (addFileNamesFromObservations)
                    {
                        AddAccessedFileNames(pathTable, searchPaths, visitedFilePaths, visitedDirectoryPaths, accessedFileNames, path, isDirectoryEnumeration);
                    }
                }

                if (addFileNamesFromObservations)
                {
                    observedAccessedFileNames = SortedReadOnlyArray<StringId, CaseInsensitiveStringIdComparer>.SortUnsafe(
                                                accessedFileNames.ToArray(),
                                                pathTable.StringTable.CaseInsensitiveComparer);
                }

                if (searchPaths.Count == 0)
                {
                    return DirectoryMembershipFilter.AllowAllFilter;
                }

                foreach (var dependency in pip.Dependencies)
                {
                    var path = dependency.Path;
                    bool isDirectory = false;
                    AddAccessedFileNames(pathTable, searchPaths, visitedFilePaths, visitedDirectoryPaths, accessedFileNames, path, isDirectory);
                }

                foreach (var dependency in pip.DirectoryDependencies)
                {
                    var path = dependency.Path;
                    bool isDirectory = true;
                    AddAccessedFileNames(pathTable, searchPaths, visitedFilePaths, visitedDirectoryPaths, accessedFileNames, path, isDirectory);
                }

                var searchPathFilter = new SearchPathDirectoryMembershipFilter(pathTable, accessedFileNames);
                return searchPathFilter;
            }
        }

        /// <summary>
        /// Adds accessed file names for files/directories under the search paths
        /// </summary>
        private static void AddAccessedFileNames(
            PathTable pathTable,
            HashSet<AbsolutePath> searchPaths,
            HashSet<AbsolutePath> visitedFilePaths,
            HashSet<AbsolutePath> visitedDirectoryPaths,
            HashSet<StringId> accessedFileNamesWithoutExtension,
            AbsolutePath path,
            bool isDirectory)
        {
            if (searchPaths.Count == 0)
            {
                return;
            }

            // Track visited files and directories separately due to bug#1878423
            while (path.IsValid && ((!isDirectory && visitedFilePaths.Add(path)) || (isDirectory && visitedDirectoryPaths.Add(path))))
            {
                var parent = path.GetParent(pathTable);
                if (searchPaths.Contains(parent))
                {
                    var fileNameWithoutExtension = path.GetName(pathTable);
                    if (!isDirectory)
                    {
                        fileNameWithoutExtension = fileNameWithoutExtension.RemoveExtension(pathTable.StringTable);
                    }

                    accessedFileNamesWithoutExtension.Add(fileNameWithoutExtension.StringId);
                }

                isDirectory = true;
                path = parent;
            }
        }

        private static ObservedInputType MapPathExistenceToObservedInputType(
            PathTable pathTable,
            AbsolutePath path,
            PathExistence pathExistence,
            ObservationFlags observationFlags,
            Func<FileMaterializationInfo?> getMaterializationInfo)
        {
            switch (pathExistence)
            {
                case PathExistence.Nonexistent:
                    return ObservedInputType.AbsentPathProbe;
                case PathExistence.ExistsAsFile:
                    if ((observationFlags & (ObservationFlags.DirectoryLocation | ObservationFlags.Enumeration)) != 0)
                    {
                        FileMaterializationInfo? fileMaterializationInfo;
                        if ((fileMaterializationInfo = getMaterializationInfo()).HasValue && isDirectorySymlinkOrJunction(fileMaterializationInfo.Value)
                            || FileUtilities.IsDirectorySymlinkOrJunction(path.ToString(pathTable)))
                        {
                            if ((observationFlags & ObservationFlags.Enumeration) != 0)
                            {
                                // Enumeration of directory through directory symlink or junction.
                                return ObservedInputType.DirectoryEnumeration;
                            }
                            else if ((observationFlags & ObservationFlags.DirectoryLocation) != 0)
                            {
                                // Probing of directory through directory symlink or junction.
                                return ObservedInputType.ExistingDirectoryProbe;
                            }
                        }

                        // If the location is a DirectoryLocation and the result of the probe is ExistsAs File,
                        // The directory is not existent. Report it as an absent path probe. The cache/fingerprints
                        // already deal with such probe properly since it is the same as absent path probe in the enumeration case.
                        return ObservedInputType.AbsentPathProbe;
                    }
                    else if ((observationFlags & ObservationFlags.FileProbe) == 0)
                    {
                        // If FileProbe flag does not exist, then this access is a content read.
                        return ObservedInputType.FileContentRead;
                    }
                    else
                    {
                        return ObservedInputType.ExistingFileProbe;
                    }
                case PathExistence.ExistsAsDirectory:
                    if ((observationFlags & ObservationFlags.Enumeration) == 0)
                    {
                        return ObservedInputType.ExistingDirectoryProbe;
                    }
                    else
                    {
                        return ObservedInputType.DirectoryEnumeration;
                    }

                default:
                    throw Contract.AssertFailure("Unhandled PathExistence value");
            }

            // CODESYNC: Sync this code with FileUtilities.IsDirectorySymlinkOrJunction
            static bool isDirectorySymlinkOrJunction(FileMaterializationInfo materializationInfo)
            {
                var reparsePointType = materializationInfo.ReparsePointInfo.ReparsePointType;
                return reparsePointType == ReparsePointType.DirectorySymlink
                    || reparsePointType == ReparsePointType.Junction
                    || reparsePointType == ReparsePointType.UnixSymlink;
            }
        }

        /// <summary>
        /// Returns the strongest validation status (success is weaker than mismatch, etc.) of two.
        /// </summary>
        private static ObservedInputProcessingStatus CombineObservedInputProcessingStatus(
            ObservedInputProcessingStatus current,
            ObservedInputProcessingStatus combineWith)
        {
            return (ObservedInputProcessingStatus)Math.Max((int)current, (int)combineWith);
        }

        private static void HandleFailureResult(ObservedInputAccessCheckFailureAction result, ref ObservedInputProcessingStatus status, ref int invalid)
        {
            if (result == ObservedInputAccessCheckFailureAction.Fail)
            {
                invalid++;
                status = CombineObservedInputProcessingStatus(status, ObservedInputProcessingStatus.Mismatched);
            }
            else
            {
                Contract.Assert(result == ObservedInputAccessCheckFailureAction.SuppressAndIgnorePath);
            }
        }

        /// <summary>
        /// Struct used to get detailed info of observation.
        /// </summary>
        private readonly struct ObservationInfo
        {
            /// <summary>
            /// Path of observation.
            /// </summary>
            public readonly AbsolutePath Path;

            /// <summary>
            /// Observation flags (modifiers).
            /// </summary>
            public readonly ObservationFlags ObservationFlags;

            /// <summary>
            /// Task for computing file materialization info.
            /// </summary>
            /// <remarks>
            /// If an observation is a file and it exists, then file content info cannot be null.
            /// </remarks>
            public readonly Task<FileMaterializationInfo?> FileMaterializationInfoTask;

            public ObservationInfo(
                AbsolutePath path,
                ObservationFlags observationFlags,
                Task<FileMaterializationInfo?> fileMaterializationInfo)
            {
                Contract.Requires(path.IsValid);
                Contract.Requires(fileMaterializationInfo != null);

                Path = path;
                ObservationFlags = observationFlags;
                FileMaterializationInfoTask = fileMaterializationInfo;
            }
        }
    }

    /// <summary>
    /// See <see cref="ObservedInputProcessor"/>.
    /// </summary>
    public interface IObservedInputProcessingTarget<in TObservation>
    {
        string Description { get; }

        AbsolutePath GetPathOfObservation(TObservation observation);

        ObservationFlags GetObservationFlags(TObservation observation);

        bool IsSearchPathEnumeration(TObservation directoryEnumeration);

        /// <summary>
        /// Action to perform on access check failure
        /// </summary>
        /// <param name="observation">the observation</param>
        /// <param name="fromTopLevelDirectory">Whether the observation was nested deeply under a source sealed directory
        /// that is configured as a top only directory. This scenar ends up hitting the sealed directory codepath due
        /// the access being allowed but we want to report it differently.</param>
        ObservedInputAccessCheckFailureAction OnAccessCheckFailure(TObservation observation, bool fromTopLevelDirectory);

        /// <summary>
        /// Actions to perform when BuildXL allows undeclared access.
        /// </summary>
        ObservedInputAccessCheckFailureAction OnAllowingUndeclaredAccessCheck(TObservation observation);

        void ReportUnexpectedAccess(TObservation observation, ObservedInputType observedInputType);

        bool IsReportableUnexpectedAccess(AbsolutePath path);

        /// <summary>
        /// Historically this was used as part of file accesss validation logic. Now that functionality has been moved
        /// but this is still utilized as a test hook.
        /// </summary>
        void CheckProposedObservedInput(TObservation observation, ObservedInput proposedObservedInput);

        string GetEnumeratePatternRegex(TObservation directoryEnumeration);
    }

    /// <summary>
    /// Interface representing all external queries made by <see cref="ObservedInputProcessor"/>.
    /// This is mostly a projection of <see cref="IPipExecutionEnvironment"/> to facilitate testing.
    /// </summary>
    internal interface IObservedInputProcessingEnvironment
    {
        /// <nodoc />
        PipExecutionContext Context { get; }

        /// <summary>
        /// Counters for pips executed in this environment. These counters include aggregate pip and caching performance information.
        /// </summary>
        CounterCollection<PipExecutorCounter> Counters { get; }

        /// <summary>
        /// The scoped execution state.
        /// </summary>
        PipExecutionState.PipScopeState State { get; }

        /// <summary>
        /// Used to retrieve semantic path information
        /// </summary>
        SemanticPathExpander PathExpander { get; }

        /// <summary>
        /// Configuration for caching behavior
        /// </summary>
        ICacheConfiguration Configuration { get; }

        /// <summary>
        /// Pip specific properties
        /// </summary>
        PipSpecificPropertiesConfig PipProperties { get;  }

        /// <summary>
        /// The reclassifier that applies globally-defined reclassification rules, i.e., rules that are applied to all pips.
        /// </summary>
        ObservationReclassifier GlobalReclassificationRules { get; }

        /// <summary>
        /// Probes a path for existence
        /// </summary>
        Possible<PathExistence> TryProbeAndTrackForExistence(AbsolutePath path, CacheablePipInfo pipInfo, ObservationFlags flags, bool isReadOnly, bool cachePathExistence, DebugTrace trace = default);

        /// <summary>
        /// Gets the fingerprint of a directory
        /// </summary>
        DirectoryFingerprint? TryQueryDirectoryFingerprint(
            AbsolutePath directoryPath,
            CacheablePipInfo process,
            DirectoryMembershipFilter filter,
            bool isReadOnlyDirectory,
            DirectoryMembershipHashedEventData eventData,
            IReadOnlyDictionary<AbsolutePath, bool> sharedOpaqueOutputs,
            ConcurrentBigMap<AbsolutePath, IReadOnlyList<DirectoryMemberEntry>> alienFileEnumerationCache,
            out DirectoryEnumerationMode enumerationMode,
            bool trackPathExistence);

        /// <summary>
        /// See <see cref="FileContentManager.TryQuerySealedOrUndeclaredMaterializationInfoAsync"/>
        /// </summary>
        Task<FileMaterializationInfo?> TryQuerySealedOrUndeclaredInputContent(AbsolutePath sealedPath, string consumerDescription, bool allowUndeclaredSourceReads);

        /// <summary>
        /// See <see cref="FileContentManager.ListSealedDirectoryContents"/>
        /// </summary>
        SortedReadOnlyArray<FileArtifact, OrdinalFileArtifactComparer> ListSealedDirectoryContents(DirectoryArtifact directoryArtifact);

        /// <summary>
        /// See <see cref="IPipExecutionEnvironment.IsSourceSealedDirectory"/>
        /// </summary>
        bool IsSourceSealedDirectory(DirectoryArtifact directoryArtifact, out bool allDirectories, out ReadOnlyArray<StringId> patterns);

        /// <summary>
        /// Returns whether there is (by walking the path upwards) an output directory -shared or exclusive- containing <paramref name="path"/>
        /// </summary>
        bool IsPathUnderOutputDirectory(AbsolutePath path);
    }

    /// <summary>
    /// Result of <see cref="IObservedInputProcessingTarget{TObservation}.OnAccessCheckFailure"/>
    /// </summary>
    public enum ObservedInputAccessCheckFailureAction
    {
        /// <summary>
        /// This single access-check failure should result in the 'check access' operation failing in aggregate
        /// (with no path -> content hash mappings returned).
        /// </summary>
        Fail,

        /// <summary>
        /// This single access-check failure should be suppressed. The overall 'check access' operation may succeed,
        /// but there will not be a path -> content hash mapping for this path (as if it was not in the set to check).
        /// </summary>
        SuppressAndIgnorePath,
    }

    /// <summary>
    /// Overall status of <see cref="ObservedInputProcessingResult"/>.
    /// </summary>
    public enum ObservedInputProcessingStatus
    {
        /// <summary>
        /// All assertions were validated and had expected fingerprints.
        /// </summary>
        Success = 0,

        /// <summary>
        /// Validation completed, but at least one assertion had an unexpected fingerprint / hash.
        /// </summary>
        Mismatched = 1,

        /// <summary>
        /// Validation was aborted. At least one fingerprint or hash could not be computed.
        /// An error-level event has been logged, and so the calling pip must fail.
        /// </summary>
        Aborted = 2,
    }

    /// <summary>
    /// Result of applying an <see cref="ObservedInputProcessor"/> to observations.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
    public readonly struct ObservedInputProcessingResult
    {
        /// <summary>
        /// Overall status. Note that some fields and operations are unavailable if this status is not <see cref="ObservedInputProcessingStatus.Success"/>
        /// </summary>
        public readonly ObservedInputProcessingStatus Status;

        /// <summary>
        /// Number of observations found to be valid (note that 'skipped' observations are excluded).
        /// </summary>
        public readonly int NumberOfValidEntries;

        /// <summary>
        /// Number of observations found to be invalid (always 0 on success, and non-zero on failure).
        /// </summary>
        public readonly int NumberOfInvalidEntries;

        
        /// <summary>
        /// The list of dynamic observations. 
        /// </summary>
        public readonly ReadOnlyArray<(AbsolutePath Path, DynamicObservationKind Kind)> DynamicObservations;

        /// <summary>
        /// The set of undeclared reads (or probes) that occurred on source files
        /// </summary>
        public readonly IReadOnlyDictionary<AbsolutePath, ObservedInputType> AllowedUndeclaredSourceReads;

        private readonly SortedReadOnlyArray<ObservedInput, ObservedInputExpandedPathComparer> m_observedInputs;

        private readonly SortedReadOnlyArray<StringId, CaseInsensitiveStringIdComparer> m_observedAccessFileNames;

        private ObservedInputProcessingResult(
            ObservedInputProcessingStatus status,
            SortedReadOnlyArray<ObservedInput, ObservedInputExpandedPathComparer> observedInputs,
            SortedReadOnlyArray<StringId, CaseInsensitiveStringIdComparer> observedAccessFileNames,
            int numberOfValidEntires,
            int numberOfInvalidEntries,
            ReadOnlyArray<(AbsolutePath, DynamicObservationKind)> dynamicObservations,
            IReadOnlyDictionary<AbsolutePath, ObservedInputType> allowedUndeclaredSourceReads)
        {
            Contract.Requires(status != ObservedInputProcessingStatus.Success || observedInputs.IsValid);
            Contract.Requires(status != ObservedInputProcessingStatus.Success || observedAccessFileNames.IsValid);
            Contract.Requires((status != ObservedInputProcessingStatus.Success) || (numberOfInvalidEntries == 0));
            Contract.Requires(dynamicObservations.IsValid);
            Contract.Requires(allowedUndeclaredSourceReads != null);

            Status = status;
            NumberOfValidEntries = numberOfValidEntires;
            NumberOfInvalidEntries = numberOfInvalidEntries;
            DynamicObservations = dynamicObservations;
            m_observedInputs = observedInputs;
            m_observedAccessFileNames = observedAccessFileNames;
            AllowedUndeclaredSourceReads = allowedUndeclaredSourceReads;
        }

        /// <nodoc />
        public static ObservedInputProcessingResult CreateForFailure(
            ObservedInputProcessingStatus status,
            SortedReadOnlyArray<ObservedInput, ObservedInputExpandedPathComparer> observedInputs,
            SortedReadOnlyArray<StringId, CaseInsensitiveStringIdComparer> observedAccessedFileNames,
            int numberOfValidEntries,
            int numberOfInvalidEntries,
            ReadOnlyArray<(AbsolutePath, DynamicObservationKind)> dynamicObservations,
            IReadOnlyDictionary<AbsolutePath, ObservedInputType> allowedUndeclaredSourceReads)
        {
            Contract.Requires(status != ObservedInputProcessingStatus.Success);
            Contract.Requires(dynamicObservations.IsValid);
            Contract.Requires(allowedUndeclaredSourceReads != null);

            return new ObservedInputProcessingResult(
                status,
                observedInputs,
                observedAccessedFileNames,
                numberOfValidEntires: numberOfValidEntries,
                numberOfInvalidEntries: numberOfInvalidEntries,
                dynamicObservations: dynamicObservations,
                allowedUndeclaredSourceReads: allowedUndeclaredSourceReads);
        }

        /// <nodoc />
        public static ObservedInputProcessingResult CreateForSuccess(
            SortedReadOnlyArray<ObservedInput, ObservedInputExpandedPathComparer> observedInputs,
            SortedReadOnlyArray<StringId, CaseInsensitiveStringIdComparer> observedAccessedFileNames,
            ReadOnlyArray<(AbsolutePath, DynamicObservationKind)> dynamicObservations,
            IReadOnlyDictionary<AbsolutePath, ObservedInputType> allowedUndeclaredSourceReads)
        {
            Contract.Requires(dynamicObservations.IsValid);
            Contract.Requires(allowedUndeclaredSourceReads != null);

            return new ObservedInputProcessingResult(
                ObservedInputProcessingStatus.Success,
                observedInputs,
                observedAccessedFileNames,
                numberOfValidEntires: observedInputs.Length,
                numberOfInvalidEntries: 0,
                dynamicObservations: dynamicObservations,
                allowedUndeclaredSourceReads: allowedUndeclaredSourceReads);
        }

        /// <summary>
        /// Individual <see cref="ObservedInputs"/> corresponding to the provided operations.
        /// </summary>
        public SortedReadOnlyArray<ObservedInput, ObservedInputExpandedPathComparer> ObservedInputs
        {
            get
            {
                return m_observedInputs;
            }
        }

        /// <summary>
        /// Individual <see cref="ObservedAccessFileNames"/> corresponding to the provided operations.
        /// </summary>
        public SortedReadOnlyArray<StringId, CaseInsensitiveStringIdComparer> ObservedAccessFileNames
        {
            get
            {
                return m_observedAccessFileNames;
            }
        }

        /// <summary>
        /// Projects the set of paths (from <see cref="ObservedInputs"/>) into an <see cref="ObservedPathSet"/>.
        /// </summary>
        public ObservedPathSet GetPathSet([AllowNull]UnsafeOptions unsafeOptions)
        {

            // Note that we don't deduplicate identical paths here. ObservedPathSet allows duplicates on construction,
            // though it reserves the right to canonicalize them away at any time.
            ObservedPathEntry[] paths = new ObservedPathEntry[m_observedInputs.Length];
            for (int i = 0; i < m_observedInputs.Length; i++)
            {
                paths[i] = ObservedPathEntry.FromObservedInput(m_observedInputs[i]);
            }

            return new ObservedPathSet(
                SortedReadOnlyArray<ObservedPathEntry, ObservedPathEntryExpandedPathComparer>.FromSortedArrayUnsafe(
                    ReadOnlyArray<ObservedPathEntry>.FromWithoutCopy(paths),
                    new ObservedPathEntryExpandedPathComparer(m_observedInputs.Comparer.PathComparer)),
                m_observedAccessFileNames,
                unsafeOptions);
        }

        /// <summary>
        /// Computes a strong fingerprint from <see cref="ObservedInputs"/>.
        /// </summary>
        public StrongContentFingerprint ComputeStrongFingerprint(PathTable pathTable, WeakContentFingerprint weakFingerprint, ContentHash pathSetHash)
        {
            using (var hasher = StrongContentFingerprint.CreateHashingHelper(
                pathTable,
                recordFingerprintString: false))
            {
                AddStrongFingerprintContent(hasher, weakFingerprint, pathSetHash, m_observedInputs.BaseArray);
                return new StrongContentFingerprint(hasher.GenerateHash());
            }
        }

        /// <summary>
        /// Adds the elements of a strong fingerprint computation to the fingerprinter.
        /// </summary>
        internal static void AddStrongFingerprintContent(IFingerprinter fingerprinter, WeakContentFingerprint weakFingerprint, ContentHash pathSetHash, ReadOnlyArray<ObservedInput> observedInputs)
        {
            fingerprinter.Add("Namespace", (int)StrongContentFingerprintNamespace.ObservedInputs);
            fingerprinter.Add("WeakFingerprint", weakFingerprint.Hash);

            // TODO: the PathSet and weakFingerprint shouldn't really need to be members of the StrongFingerprint.
            // When the cache is queries for the strong fingerprint, it is queried with a triple of:
            // * WeakFingerprint
            // * PathSet hash
            // * Strong fingerprint
            // Previously, the paths were included in the StrongFingerprint here, and now the PathSet hash is being
            // included to preserve the same uniqueness. But it most likely isn't necessary. See StrongContentFingerprint
            // for details.
            fingerprinter.Add(ObservedPathEntryConstants.PathSet, pathSetHash);

            fingerprinter.AddCollection<ObservedInput, ReadOnlyArray<ObservedInput>>(
                ObservedInputConstants.ObservedInputs,
                observedInputs,
                (h, observedInput) =>
                {
                    switch (observedInput.Type)
                    {
                        case ObservedInputType.AbsentPathProbe:
                            h.Add(ObservedInputConstants.AbsentPathProbe, string.Empty);
                            break;
                        case ObservedInputType.FileContentRead:
                            h.Add(ObservedInputConstants.FileContentRead, observedInput.Hash);
                            break;
                        case ObservedInputType.DirectoryEnumeration:
                            h.Add(ObservedInputConstants.DirectoryEnumeration, observedInput.Hash);
                            break;
                        case ObservedInputType.ExistingDirectoryProbe:
                            h.Add(ObservedInputConstants.ExistingDirectoryProbe, string.Empty);
                            break;
                        case ObservedInputType.ExistingFileProbe:
                            h.Add(ObservedInputConstants.ExistingFileProbe, string.Empty);
                            break;
                        default:
                            throw Contract.AssertFailure("Unhandled ObservedInputType");
                    }
                });
        }
    }

    /// <summary>
    /// Adapter from <see cref="IPipExecutionEnvironment"/> to <see cref="IObservedInputProcessingEnvironment"/>.
    /// </summary>
    /// <remarks>
    /// Determining which files to consider when computing a pip's input assertions is quite tricky:
    /// - The real filesystem will vary build over build as output files first don't exist, and then later are created.
    ///     If it were used directory, it would take multiple builds to achieve stable cache hits
    /// - A filesystem based on the build graph could be used because it should be aware of all files that will be produced
    ///     as part of the current build. But this has the problem that the scope of the filesystem changes depending on
    ///     how pips are filtered in the build.
    /// The following is the algorithm for determining the files available to generate input assertions is our best strategy
    /// for a compromise between getting a consistent level of caching regardless of how the build is filtered vs.
    /// rerunning pips when reasonable.
    ///
    /// Assume 3 filesystems:
    /// - realFs: the actual filesystem
    /// - graphFs: the entire graph. This can change build over build
    /// - pipFs: a pip projection of the filesystem that only sees the pip's immediately declared dependencies for enumerations
    ///
    /// InputAssertion ComputeInputAssertionForPath(AbsolutePath path, CacheablePipInfo pip)
    /// {
    ///     bool existsAnywhereAsFile = realFs.ExistsAsAFile(path) || graphFs.ExistsAsFile(path);
    ///     if (existsAnywhereAsFile)
    ///     {
    ///         RequireDeclaredDependency()
    ///         return CreateExistentFileInputAssertion()
    ///     }
    ///     else
    ///     {
    ///         // This could be a directory or an absent file
    ///         var existentialFs = IsMountWriteableByAnyModule(path) ? pipFs.Create(pip) : realFs;
    ///         var fingerprint = ComputeDirectoryFingerprint(path, existentialFs)
    ///         if (finterprint.IsValid)
    ///         {
    ///             return CreateDirectoryFilgerprint(fingerprint)
    ///         }
    ///         else
    ///         {
    ///             // Treat empty directories as absent files
    ///             return CreateAbsentFileInputAssertion()
    ///         }
    ///     }
    /// }
    ///
    /// </remarks>
    internal class ObservedInputProcessingEnvironmentAdapter : IObservedInputProcessingEnvironment, IDisposable
    {
        private static readonly ObjectPool<PipFileSystemView> s_pool = new(
            () => new PipFileSystemView(),
            state => state.Clear());

        private static readonly ConcurrentBigSet<AbsolutePath> s_regexFilterPaths = new();
        private static readonly ConcurrentBigSet<AbsolutePath> s_unionFilterPaths = new();
        private static readonly ConcurrentBigSet<AbsolutePath> s_allowAllFilterPaths = new();
        private static readonly ConcurrentBigSet<AbsolutePath> s_searchPathFilterPaths = new();

        private FileSystemView FileSystemView => m_env.State.FileSystemView;

        private readonly IPipExecutionEnvironment m_env;
        private readonly PipExecutionState.PipScopeState m_state;
        private PooledObjectWrapper<PipFileSystemView>? m_pooledPipFileSystem;
        private DirectoryMembershipFilter m_directoryMembershipFilter;

        private PipFileSystemView PipFileSystem => m_pooledPipFileSystem?.Instance;

        public PathTable PathTable => m_env.Context.PathTable;

        public PipExecutionContext Context => m_env.Context;

        public ICacheConfiguration Configuration => m_env.Configuration.Cache;

        public bool IsLazySODeletionEnabled => m_env.Configuration.Schedule.UnsafeLazySODeletion;

        public CounterCollection<PipExecutorCounter> Counters => m_env.Counters;

        public PipExecutionState.PipScopeState State => m_state;

        public SemanticPathExpander PathExpander => m_state.PathExpander;

        public PipSpecificPropertiesConfig PipProperties => m_env.PipSpecificPropertiesConfig;

        public RequiredOutputMaterialization RequiredOutputMaterialization => m_env.Configuration.Schedule.RequiredOutputMaterialization;

        public ObservationReclassifier GlobalReclassificationRules => m_env.State.GlobalReclassificationRules;

        public ObservedInputProcessingEnvironmentAdapter(
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state)
        {
            m_env = environment;
            m_state = state;
            m_directoryMembershipFilter = null;
            m_pooledPipFileSystem = null;
        }

        public void Dispose()
        {
            m_pooledPipFileSystem?.Dispose();
        }

        public Possible<PathExistence> TryProbeAndTrackForExistence(AbsolutePath path, CacheablePipInfo pipInfo, ObservationFlags flags, bool isReadOnly, bool cachePathExistence = true, DebugTrace trace = default)
        {
            // ****************** CAUTION *********************
            // The logic below is replicated conservatively in IncrementalSchedulingState.ProcessNewlyPresentPath.
            // Any change here should be implemented conservatively there.
            // ************************************************
            var mountInfo = PathExpander.GetSemanticPathInfo(path);
            if (mountInfo.IsValid && !mountInfo.AllowHashing)
            {
                trace.AppendLine("mountInfo.IsValid && !mountInfo.AllowHashing => PathExistence.Nonexistent");
                return PathExistence.Nonexistent;
            }

            if (IsRecordedDynamicOutputForPip(pipInfo, path))
            {
                // We can safely report this path as absent because if the path is in the sideband file:
                //  (1) If the file is an actual output of the pip, then we won't care about this observation
                //      (i.e., it is not included in the execution result).
                //  (2) If the file isn't an output of the pip in this run, then
                //      the file will be / will have been scrubbed, so it should be an absent path probe.
                //
                //  The split into (1) and (2) is because the pip can be non-deterministic about its outputs,
                //  and the sideband only holds the information of the latest run we observed, which may have different outputs
                //  than the ones produced the next time the pip actually runs.
                //
                // See LazySharedOpaqueOutputDeletionTests.SidebandPathsAreConsideredAbsentByInputProcessor
                // for a scenario where not having this reclassification would make the engine crash.
                //
                // This is becasue with lazy scrubbing of shared opaques we can get confused because:
                //  1. the actual existence of the file at cache lookup time is "Present",
                //  2. the file will be absent at execution time due to scrubbing (and potentially not produced!).
                //
                // Because path existence is recorded at cache-lookup time, we will decide that the file is present
                // when it actually wasn't, and we will crash (see bug #1838467).
                trace.AppendLine("IsRecordedDynamicOutputForPip => PathExistence.Nonexistent");
                return PathExistence.Nonexistent;
            }

            // PathExistence gets cached for the duration of the build. This is acceptable because:
            // 1. PathExistence checks not only look at the filesystem but also at files that will eventually be produced
            //      by other pips in the build.
            // 2. Processes are required to declare their outputs. So if a process produces a file not in the build graph
            //      and thus not correctly represented by this caching, the producing pip will fail the build anyway.
            // 3. Tracking file accesses does not need to happen multiple times within the same build.
            //
            // There are potentially some issues with this approach:
            // * Files created outside of the build may not be represented if they are created after the build starts.
            // * Processes that previously probed for nonexistent files that are newly produced but undeclared, won't
            //     be run. This is case #2 described above. The build will still fail and the subsequent build will cause
            //     the consuming pip to rerun
            // In general, these issues are acceptable since querying the filesystem every time wouldn't necessarily produce
            // better results. There'd still be an inherent race.
            Possible<PathExistence> existence;

            // Check if path will be eventually produced as a file/directory by querying 'output file system'
            existence = FileSystemView.GetExistence(path, FileSystemViewMode.Output);

            // NOTE: We don't check success of existence from produced file system as it should never return failure
            if (existence.Result != PathExistence.Nonexistent)
            {
                // Produced file system shows the path as existent, so use that result
                trace.AppendLine($"Produced file system shows the path as existent, so use that result. Existence {existence.Result}");
                return existence;
            }

            bool hasBuildOutputs = mountInfo.HasPotentialBuildOutputs && !isReadOnly;

            // If path is not eventually produced, query real file system
            existence = FileSystemView.GetExistence(path, FileSystemViewMode.Real, isReadOnly: !hasBuildOutputs, cachePathExistence: cachePathExistence);

            if (existence.Succeeded && (existence.Result == PathExistence.Nonexistent || existence.Result == PathExistence.ExistsAsDirectory) && hasBuildOutputs)
            {
                var fullGraphExistExistence = FileSystemView.GetExistence(path, FileSystemViewMode.FullGraph);
                if (fullGraphExistExistence.Result == PathExistence.ExistsAsDirectory)
                {
                    trace.AppendLine($"fullGraphExistExistence.Result == PathExistence.ExistsAsDirectory => PathExistence.ExistsAsDirectory");
                    return PathExistence.ExistsAsDirectory;
                }
                else if (fullGraphExistExistence.Result == PathExistence.ExistsAsFile)
                {
                    // The file is a source file so we return non-existent. This is to match legacy behavior where non-existence is returned in this case
                    // which returns NonExistent even though the real file system existence could also be ExistsAsDirectory
                    // TODO: Consider whether we should use the real file system existence which could be Nonexistent OR ExistsAsDirectory
                    trace.AppendLine($"fullGraphExistExistence.Result == PathExistence.ExistsAsFile => The file is a source file so we return PathExistence.Nonexistent");
                    return PathExistence.Nonexistent;
                }
                else
                {
                    Contract.Assert(fullGraphExistExistence.Result == PathExistence.Nonexistent);

                    if (m_env.Configuration.Schedule.TreatAbsentDirectoryAsExistentUnderOpaque &&
                        (flags & ObservationFlags.Enumeration) == 0 &&
                        (flags & ObservationFlags.DirectoryLocation) != 0 &&
                        IsPathUnderOutputDirectory(path))
                    {
                        // If a non-existent directory is probed without an enumeration under an opaque (i.e. output) directory,
                        // we allow it to exist as directory.
                        // Here is a reason why we do this: we have two pips without any execution ordering between two.
                        // Pip1 probes a directory: "opaque/childDir".
                        // Pip2 produces a file under that directory: "opaque/childDir/output.txt".
                        // If Pip1 executes before Pip2, then Pip1 has an AbsentPathProbe for probing "opaque/childDir".
                        // In the next run, if Pip2 executes before Pip1, Pip1 has an ExistingDirectoryProbe for "opaque/childDir",
                        // so it will get a miss because the parent directories of outputs are added to Output/Graph file system after Pip2 is executed.
                        // To avoid such cache misses, we treat absent directories as existent only for probing without enumeration.
                        Counters.IncrementCounter(PipExecutorCounter.NonexistentDirectoryProbesReclassifiedAsExistingDirectoryProbe);
                        trace.AppendLine($"NonexistentDirectoryProbesReclassifiedAsExistingDirectoryProbe => PathExistence.ExistsAsDirectory");
                        return PathExistence.ExistsAsDirectory;
                    }

                    // Generally speaking, if the full graph file system doesn't know about a given directory, that means there are no known inputs
                    // underneath and it is safe to return that the directory does not exist: this makes the pip fingerprint stable across changes in directories
                    // that pips are not allowed to read anyway. However, when undeclared source reads mode is on, we need to return what the real file system
                    // is seeing since there might be undeclared inputs underneath. Consider as well this can only be the case when the directory is not created by a pip, otherwise
                    // source files can't be underneath
                    // Observe this behavior is in sync wrt incremental scheduling: in case of undeclared reads, we are only returning that the directory exists when
                    // it actually exists in the real filesystem
                    var ret =  pipInfo.UnderlyingPip.ProcessAllowsUndeclaredSourceReads && !FileSystemView.ExistCreatedDirectoryInOutputFileSystem(path) 
                        ? existence : PathExistence.Nonexistent;
                    trace.AppendLine($"The full graph file system doesn't know about a given directory: returning {(ret.Succeeded ? ret.Result.ToString() : ret.Failure.Describe())}");

                    return ret;
                }
            }

            // Return the real file system existence
            trace.AppendLine($"Return the real file system existence {(existence.Succeeded ? existence.Result.ToString() : existence.Failure.Describe())}");
            return existence;
        }
        
        /// <summary>
        /// Returns true if we have a recorded entry for this path in 
        /// the sideband file for the pip, i.e. if this path is an observed
        /// dynamic output of the pip in a shared opaque directory.
        /// </summary>
        private bool IsRecordedDynamicOutputForPip(PipInfo pipInfo, AbsolutePath path)
        {
            return m_state.SidebandState?.Entries != null 
                && m_state.SidebandState.Entries.TryGetValue(pipInfo.SemiStableHash, out var state) 
                && state.Contains(path);
        }

        /// <inheritdoc />
        public SortedReadOnlyArray<FileArtifact, OrdinalFileArtifactComparer> ListSealedDirectoryContents(DirectoryArtifact directoryArtifact)
        {
            return m_env.State.FileContentManager.ListSealedDirectoryContents(directoryArtifact);
        }

        /// <inheritdoc />
        public bool IsSourceSealedDirectory(DirectoryArtifact directoryArtifact, out bool allDirectories, out ReadOnlyArray<StringId> patterns)
        {
            return m_env.IsSourceSealedDirectory(directoryArtifact, out allDirectories, out patterns);
        }

        /// <inheritdoc />
        public bool IsPathUnderOutputDirectory(AbsolutePath path)
        {
            return m_env.PipGraphView.IsPathUnderOutputDirectory(path, out _);
        }

        /// <summary>
        /// <see cref="Pips.Graph.IPipGraphFileSystemView.IsPathUnderOutputDirectory(AbsolutePath, out bool)"/>
        /// </summary>
        internal bool IsPathUnderOutputDirectory(AbsolutePath path, out bool isItSharedOpaque)
        {
            return m_env.PipGraphView.IsPathUnderOutputDirectory(path, out isItSharedOpaque);
        }

        /// <summary>
        /// <see cref="Pips.Graph.IPipGraphFileSystemView.TryGetParentOutputDirectory(AbsolutePath)"/>
        /// </summary>
        internal Optional<(AbsolutePath Parent, bool IsItSharedOpaque)> TryGetParentOutputDirectory(AbsolutePath path)
        {
            return m_env.PipGraphView.TryGetParentOutputDirectory(path);
        }

        /// <summary>
        /// Returns the producer pip of a file at location <paramref name="filePath"/>, if one exists.
        /// </summary>
        internal bool TryGetFileProducerPip(AbsolutePath filePath, out PipId producer)
        {
            producer = PipId.Invalid;
            var fileArtifact = m_env.PipGraphView.TryGetLatestFileArtifactForPath(filePath);
            return
                fileArtifact.IsValid &&
                m_env.TryGetProducerPip(fileArtifact, out producer);
        }

        /// <summary>
        /// Returns if pip <paramref name="to"/> is reachable from pip <paramref name="from"/>.
        /// </summary>
        internal bool IsReachableFrom(PipId from, PipId to)
        {
            return m_env.IsReachableFrom(from: from, to: to);
        }

        /// <summary>
        /// Determines the way a directory should be enumerated based on the directory. This is where the logic for switching
        /// between various filesystem rules lives
        /// </summary>
        internal DirectoryEnumerationMode DetermineEnumerationModeAndRule(AbsolutePath directoryPath, bool allowUndeclaredSourceReads, out DirectoryMembershipFingerprinterRule rule)
        {
            return DetermineEnumerationModeAndRule(directoryPath, isReadOnlyDirectory: false, allowUndeclaredSourceReads, rule: out rule);
        }

        /// <summary>
        /// Conservatively determines whether <see cref="DirectoryEnumerationMode.MinimalGraphWithAlienFiles"/> is a possible answer when 
        /// calling <see cref="DetermineEnumerationModeAndRule(AbsolutePath, bool, bool, out DirectoryMembershipFingerprinterRule)"/> for any given directory path
        /// </summary>
        /// <remarks>
        /// Keep in sync with <see cref="DetermineEnumerationModeAndRule(AbsolutePath, bool, bool, out DirectoryMembershipFingerprinterRule)"/>
        /// </remarks>
        internal bool MayDetermineMinimalGraphWithAlienFiles(bool allowUndeclaredSourceReads)
        {
            return m_env.Configuration.Sandbox.FileSystemMode == FileSystemMode.AlwaysMinimalWithAlienFilesGraph || allowUndeclaredSourceReads;
        }

        /// <summary>
        /// Determines the way a directory should be enumerated based on the directory. This is where the logic for switching
        /// between various filesystem rules lives
        /// </summary>
        /// <remarks>
        /// Keep in sync with <see cref="MayDetermineMinimalGraphWithAlienFiles(bool)"/>
        /// </remarks>
        internal DirectoryEnumerationMode DetermineEnumerationModeAndRule(AbsolutePath directoryPath, bool isReadOnlyDirectory, bool allowUndeclaredSourceReads, out DirectoryMembershipFingerprinterRule rule)
        {
            Contract.Assume(directoryPath.IsValid);

            // Enumerations to directories that are under un-hashable mounts do not contribute to fingerprints
            // Note: If a path isn't within a mount, it gets the DefaultFingerprint as well. This was the existing
            // behavior at the time of the time of the refactoring but is something that might need to change.
            SemanticPathInfo mountInfo = PathExpander.GetSemanticPathInfo(directoryPath);
            if (!mountInfo.AllowHashing || (!mountInfo.IsReadable && !mountInfo.IsWritable) || m_env.Configuration.Sandbox.FileSystemMode == FileSystemMode.AlwaysEmpty)
            {
                rule = null;
                // Even if undeclared sources are allowed, out of mount directories get the default fingerprint.
                // This is because there are tools that enumerate random places of the disk (a typical behavior is to consecutively enumerate 
                // 'upwards' looking for a particular file) but in a distributed environment the result of those enumerations will most likely differ
                return DirectoryEnumerationMode.DefaultFingerprint;
            }

            // If the config says to always use the minimal graph, honor it
            if (m_env.Configuration.Sandbox.FileSystemMode == FileSystemMode.AlwaysMinimalGraph)
            {
                rule = null;
                return DirectoryEnumerationMode.MinimalGraph;
            }

            // Similarly with the minimal graph with alien files file system enumeration mode
            if (m_env.Configuration.Sandbox.FileSystemMode == FileSystemMode.AlwaysMinimalWithAlienFilesGraph)
            {
                // Minimal graph with alien files may enumerate real filesystem, so get the directory membership fingerprinter rule.
                rule = GetRule();
                return DirectoryEnumerationMode.MinimalGraphWithAlienFiles;
            }

            // Now determine which graph based enumeration mode could be used, if we determine to use the graph
            DirectoryEnumerationMode graphEnumerationMode;

            // If allowed undeclared reads are allowed, then using the alien files file system avoids underbuilds
            // when a new undeclared read gets added so it only affects enumerations
            if (allowUndeclaredSourceReads)
            {
                graphEnumerationMode = DirectoryEnumerationMode.MinimalGraphWithAlienFiles;
            }
            else
            {
                switch (m_env.Configuration.Sandbox.FileSystemMode)
                {
                    case FileSystemMode.RealAndMinimalPipGraph:
                        graphEnumerationMode = DirectoryEnumerationMode.MinimalGraph;
                        break;
                    case FileSystemMode.RealAndPipGraph:
                        // If the directoryPath is under an opaque directory, then we always use the minimal graph, so
                        // all artifacts from direct dependencies (both static and dynamic content) will be used
                        graphEnumerationMode = m_env.PipGraphView.IsPathUnderOutputDirectory(directoryPath, out _) ?
                            DirectoryEnumerationMode.MinimalGraph :
                            DirectoryEnumerationMode.FullGraph;
                        break;
                    default:
                        Contract.Assert(false, $"Unknown FileSystemMode {m_env.Configuration.Sandbox.FileSystemMode}");
                        throw new BuildXLException(string.Empty);
                }
            }

            // If the directory has build outputs, the graph enumeration should be used
            if (mountInfo.HasPotentialBuildOutputs && !isReadOnlyDirectory)
            {
                // Minimal graph with alien files may enumerate real filesystem, so get the directory membership fingerprinter rule.
                rule = graphEnumerationMode == DirectoryEnumerationMode.MinimalGraphWithAlienFiles
                    ? GetRule()
                    : null;
                return graphEnumerationMode;
            }

            rule = GetRule();

            // Check if there is a rule preventing filesystem based enumeration; otherwise use the real filesystem.
            return rule?.DisableFilesystemEnumeration == true ? graphEnumerationMode : DirectoryEnumerationMode.RealFilesystem;

            DirectoryMembershipFingerprinterRule GetRule() =>
                m_state.DirectoryMembershipFingerprinterRuleSet?.TryGetRule(m_env.Context.PathTable, directoryPath, out DirectoryMembershipFingerprinterRule r) == true ? r : null;
        }

        /// <inheritdoc/>
        public DirectoryFingerprint? TryQueryDirectoryFingerprint(
            AbsolutePath directoryPath,
            CacheablePipInfo process,
            DirectoryMembershipFilter filter,
            bool isReadOnlyDirectory,
            DirectoryMembershipHashedEventData eventData,
            IReadOnlyDictionary<AbsolutePath, bool> sharedOpaqueOutputs,
            ConcurrentBigMap<AbsolutePath, IReadOnlyList<DirectoryMemberEntry>> alienFileEnumerationCache,
            out DirectoryEnumerationMode enumerationMode,
            bool trackPathExistence = true)
        {
            Contract.Assume(directoryPath.IsValid);
            Contract.Assume(process != null);
            Contract.Assume(filter != null);
            Contract.Assert(m_directoryMembershipFilter == null, "This method may not be called concurrently");
            Contract.RequiresNotNull(sharedOpaqueOutputs);

            try
            {
                m_directoryMembershipFilter = filter;
                
                DirectoryMembershipFingerprinterRule rule = null;
                bool allowsUndeclaredSourceReads = process.UnderlyingPip.ProcessAllowsUndeclaredSourceReads;
                enumerationMode = DetermineEnumerationModeAndRule(directoryPath, isReadOnlyDirectory, allowsUndeclaredSourceReads, out rule);
                Contract.Assert(enumerationMode != DirectoryEnumerationMode.MinimalGraphWithAlienFiles || MayDetermineMinimalGraphWithAlienFiles(allowsUndeclaredSourceReads));

                // If (1) the enumeration does not use the search-based filter and (2) the directory is not enumerated via the minimal graph, cache the fingerprint.
                bool cacheableFingerprint = !eventData.IsSearchPath && enumerationMode != DirectoryEnumerationMode.MinimalGraph && enumerationMode != DirectoryEnumerationMode.MinimalGraphWithAlienFiles;
                eventData.EnumerationMode = enumerationMode;

                if (filter == DirectoryMembershipFilter.AllowAllFilter && s_allowAllFilterPaths.Add(directoryPath))
                {
                    Counters.IncrementCounter(PipExecutorCounter.UniqueDirectoriesAllowAllFilter);
                }
                else if (filter is RegexDirectoryMembershipFilter && s_regexFilterPaths.Add(directoryPath))
                {
                    Counters.IncrementCounter(PipExecutorCounter.UniqueDirectoriesRegexFilter);
                }
                else if (filter is SearchPathDirectoryMembershipFilter && s_searchPathFilterPaths.Add(directoryPath))
                {
                    Counters.IncrementCounter(PipExecutorCounter.UniqueDirectoriesSearchPathFilter);
                }
                else if (filter is UnionDirectoryMembershipFilter && s_unionFilterPaths.Add(directoryPath))
                {
                    Counters.IncrementCounter(PipExecutorCounter.UniqueDirectoriesUnionFilter);
                }

                DirectoryFingerprint? result;
                switch (enumerationMode)
                {
                    case DirectoryEnumerationMode.DefaultFingerprint:
                        result = DirectoryFingerprint.Zero;
                        break;

                    case DirectoryEnumerationMode.FullGraph:
                        using (Counters.StartStopwatch(PipExecutorCounter.FullGraphDirectoryEnumerationsDuration))
                        {
                            Contract.Assert(rule == null, $"{DirectoryEnumerationMode.FullGraph} does not enumerate file system, so {nameof(rule)} must be null");

                            eventData.IsStatic = true;
                            result = m_env.State.DirectoryMembershipFingerprinter.TryComputeDirectoryFingerprint(
                                directoryPath,
                                process,
                                TryEnumerateDirectoryWithFullGraph,
                                cacheableFingerprint: cacheableFingerprint,
                                rule: rule,
                                eventData: eventData);
                            Counters.IncrementCounter(PipExecutorCounter.FullGraphDirectoryEnumerations);

                            break;
                        }
                    case DirectoryEnumerationMode.MinimalGraph:
                        using (Counters.StartStopwatch(PipExecutorCounter.MinimalGraphDirectoryEnumerationsDuration))
                        {
                            Contract.Assert(rule == null, $"{DirectoryEnumerationMode.MinimalGraph} does not enumerate file system, so {nameof(rule)} must be null");

                            eventData.IsStatic = true;
                            result = m_env.State.DirectoryMembershipFingerprinter.TryComputeDirectoryFingerprint(
                                directoryPath,
                                process,
                                EnumerateDirectoryWithMinimalPipGraph,
                                cacheableFingerprint: cacheableFingerprint,
                                rule: rule,
                                eventData: eventData);
                            Counters.IncrementCounter(PipExecutorCounter.MinimalGraphDirectoryEnumerations);

                            break;
                        }
                    case DirectoryEnumerationMode.MinimalGraphWithAlienFiles:
                        using (Counters.StartStopwatch(PipExecutorCounter.MinimalGraphWithAlienFilesDirectoryEnumerationsDuration))
                        {
                            eventData.IsStatic = false;
                            result = m_env.State.DirectoryMembershipFingerprinter.TryComputeDirectoryFingerprint(
                                directoryPath,
                                process,
                                TryEnumerateDirectoryWithMinimalGraphWithAlienFiles(sharedOpaqueOutputs, alienFileEnumerationCache, rule),
                                cacheableFingerprint: cacheableFingerprint,
                                rule: null /* Only apply the rule within the filesystem enumeration in TryEnumerateDirectoryWithMinimalGraphWithAlienFiles */,
                                eventData: eventData);
                            Counters.IncrementCounter(PipExecutorCounter.MinimalGraphWithAlienFilesDirectoryEnumerations);

                            break;
                        }
                    default:
                        Contract.Assert(enumerationMode == DirectoryEnumerationMode.RealFilesystem);

                        var enumerateFunc = trackPathExistence ? (Func<EnumerationRequest, PathExistence?>) TryEnumerateAndTrackDirectoryWithFilesystem : TryEnumerateDirectoryWithFilesystem;
                        using (Counters.StartStopwatch(PipExecutorCounter.RealFilesystemDirectoryEnumerationsDuration))
                        {
                            eventData.IsStatic = false;

                            result = m_env.State.DirectoryMembershipFingerprinter.TryComputeDirectoryFingerprint(
                                directoryPath,
                                process,
                                enumerateFunc,
                                cacheableFingerprint: cacheableFingerprint,
                                rule: rule,
                                eventData: eventData);
                            Counters.IncrementCounter(PipExecutorCounter.RealFilesystemDirectoryEnumerations);

                            break;
                        }
                }

                return result;
            }
            finally
            {
                m_directoryMembershipFilter = null;
            }
        }

        public Task<FileMaterializationInfo?> TryQuerySealedOrUndeclaredInputContent(AbsolutePath sealedPath, string consumerDescription, bool allowUndeclaredSourceReads)
        {
            return m_env.State.FileContentManager.TryQuerySealedOrUndeclaredMaterializationInfoAsync(sealedPath, consumerDescription, allowUndeclaredSourceReads);
        }

        private Action<AbsolutePath, string> FilteredHandledEntry(AbsolutePath directory, Action<AbsolutePath, string> handleEntry)
        {
            var filter = m_directoryMembershipFilter;
            var context = Context;
            var counters = Counters;
            return (member, fileName) =>
            {
                bool isFilterPassing;
                using (counters.StartStopwatch(PipExecutorCounter.DirectoryEnumerationFilterDuration))
                {
                    isFilterPassing = filter.Include(member.GetName(context.PathTable), fileName);
                }

                if (isFilterPassing)
                {
                    handleEntry(member, fileName);
                }
            };
        }

        #region Methods for performing directory enumeration

        private PathExistence? TryEnumerateAndTrackDirectoryWithFilesystem(EnumerationRequest request)
        {
            return TryEnumerateDirectory(request, FileSystemViewMode.Real);
        }

        private PathExistence? TryEnumerateDirectoryWithFilesystem(EnumerationRequest request)
        {
            return TryEnumerateDirectory(request, FileSystemViewMode.Real, trackPathExistence: false);
        }

        private PathExistence? TryEnumerateDirectoryWithFullGraph(EnumerationRequest request)
        {
            return TryEnumerateDirectory(request, FileSystemViewMode.FullGraph, trackPathExistence: false);
        }

        private Func<EnumerationRequest, PathExistence?> TryEnumerateDirectoryWithMinimalGraphWithAlienFiles(
            IReadOnlyDictionary<AbsolutePath, bool> sharedDynamicOutputs,
            ConcurrentBigMap<AbsolutePath, IReadOnlyList<DirectoryMemberEntry>> alienFileEnumerationCache,
            DirectoryMembershipFingerprinterRule rule)
        {
            return (request =>
            {
                // Enumerating the minimal + alien files means exposing outputs from immediate dependencies and hiding outputs
                // from everything else. Input files, undeclared sources or any other alien file (unknown to the build) will
                // become part of the enumeration. The main goal is to balance fingerprint stability and correctness.
                // The main steps are:
                // 1) add known immediate outputs using the pip file system. These should always be there.
                // 2) enumerate the real file system excluding outputs that are not immediate dependency ones
                using (var pooledPathSet = Pools.GetDirectoryMemberSet())
                {
                    var pipFileEnumResult = pooledPathSet.Instance;

                    // 1) Query the pip file system, since we want outputs from immediate dependencies to be there regardless
                    // of the state of the real file system
                    InitializePipFileSystem(request.PipInfo);

                    // Reuse the pip file system enumeration by setting a handle that just adds to the final enumeration
                    var path = request.DirectoryPath;
                    Action<AbsolutePath, string> addPipFileSystemEntry = new Action<AbsolutePath, string>((absolutePath, pathAsString) => pipFileEnumResult.Add((absolutePath, pathAsString)));
                    PipFileSystem.EnumerateDirectory(PathTable, path, addPipFileSystemEntry);

                    // If we are trying to enumerate a directory that we know is created by the build, we don't need to enumerate the real file system for that. This is because
                    // the real file system enumeration is what may bring in alien files, but that's never the case for in-build created directories
                    // So this case matches the regular minimal graph enumeration mode
                    if (!(FileSystemView.ExistCreatedDirectoryInOutputFileSystem(path) ||
                        // if the directory was created after the engine started, then we know it cannot contain sources
                        // This check should avoid races when a directory gets created in the filesystem (and picked up by another enumerating pip) but
                        // it hasn't reached the FileSystemView yet
                        m_env.State.FileTimestampTracker.PathCreatedAfterEngineStarted(path) ||
                        rule?.DisableFilesystemEnumeration == true))
                    {
                        // Check the cache to see if we already have an alien file enumeration for this path
                        // Observe this is safe to cache, since the purpose of this enumeration is to add to the pip file system
                        // any extra source file that might be under the directory being enumerated: output files should never
                        // be part of it. Therefore, extra source files should belong to any enumeration under this mode, regardless
                        // of when the enumeration takes place, as extra source files are assumed to be there before the build begins
                        if (!alienFileEnumerationCache.TryGetValue(path, out IReadOnlyList<(AbsolutePath, string)> alienFileEnumeration))
                        {
                            var alienFileEnumerationMutable = new List<DirectoryMemberEntry>();

                            // 2) Query the real file system since we want inputs (including undeclared sources) to be part of the fingerprint
                            // and add the result to the final enumeration
                            var realFileEnumResult = GetEnumerationResult(request, FileSystemViewMode.Real, trackPathExistence: false);

                            // Only add entries coming from the real enumeration if they are not recognized as outputs (and are not part of the immediate dependency outputs either)
                            if (realFileEnumResult.IsValid)
                            {
                                foreach (var realFileEntry in realFileEnumResult.Members)
                                {
                                    AbsolutePath realFileEntryPath = realFileEntry.Item1;
                                    string fileName = realFileEntry.Item2;

                                    // Entry should be ignored by the rule.
                                    if (rule?.ShouldIgnoreFileWhenEnumerating(fileName) == true)
                                    {
                                        continue;
                                    }

                                    // If the entry is globally untracked, then it should be invisible for the enumeration
                                    // Observe that since this is a global collection of untracked scopes, excluding this entry makes sense for all pips
                                    // enumerating this directory, and therefore it is safe to cache
                                    if (m_env.TranslatedGlobalUnsafeUntrackedScopes.Contains(realFileEntryPath))
                                    {
                                        continue;
                                    }

                                    // If the entry is a directory created by other pip, even if that directory is empty, it will be
                                    // ruled out here. Observe the filesystem view is already populated with created directories coming
                                    // from this same pip for the execution case, since that's reported as soon as the sandbox report comes
                                    // back in SandboxedProcessReports.
                                    //
                                    // However, we don't want to rule out any directories that are in the static graph: considering these as existing is also stable
                                    // and in back-to-back builds we might not see them as 'created directories' if they pre-exist in the filesystem.
                                    // For example, consider a build with pips A and B, where A declares an output D1\D2\f under an opaque directory D2, and B enumerates D1
                                    // If we don't exclude D2 here, then a first build has D2 as 'created' but a second build will not
                                    // (because the directory exists already, when preparing to run Pip A, the engine does not effectively create it and does not mark it).
                                    if (FileSystemView.ExistCreatedDirectoryInOutputFileSystem(realFileEntryPath) &&
                                        FileSystemView.GetExistence(realFileEntryPath, FileSystemViewMode.FullGraph).Result == PathExistence.Nonexistent
                                    )
                                    {
                                        continue;
                                    }

                                    // If the entry is reported as existent from the output file system, this means it is an output that is
                                    // not part of the immediate dependencies. So we don't add it.
                                    // The only exception is when the file is flagged as an undeclared source/alien file rewrite. In this case the file was there
                                    // before the build started, and therefore it makes sense to include it in the fingerprint
                                    // If the path exists as a directory, we know it is not one created by a pip, so it is a directory part of the sources, and in that case we keep it.
                                    // When checking existence, include outputs that are reported before caching to avoid the race where the file system is enumerated before just-produced
                                    // outputs are added to the Output filesystem.
                                    if (FileSystemView.GetExistence(realFileEntryPath, FileSystemViewMode.Output, includeOutputsProducedBeforeCaching: true).Result == PathExistence.ExistsAsFile &&
                                        !m_env.State.FileContentManager.IsAllowedFileRewriteOutput(realFileEntryPath))
                                    {
                                        continue;
                                    }

                                    // There might be stale outputs that bxl is not aware of as yet coming from exclusive opaques.
                                    // These outputs are not part of the output file system (because they were not produced yet).
                                    // Any file found under an exclusive opaque is considered an output
                                    if (m_env.PipGraphView.IsPathUnderOutputDirectory(request.DirectoryPath, out var isSharedOpaque) && !isSharedOpaque)
                                    {
                                        continue;
                                    }

                                    // Rule out all shared opaques produced by this pip. These files may not be part yet of the output file system since those get registered
                                    // in a post-execution step which is not reached yet on cache miss, so the output file system may have not captured them yet
                                    // Only exclude the ones that are not a file rewrite, in consonance with the check we did above against the output file system
                                    var isOutputProducedByThisPip = sharedDynamicOutputs.TryGetValue(realFileEntryPath, out var isFileRewrite);
                                    if (isOutputProducedByThisPip && !isFileRewrite)
                                    {
                                        continue;
                                    }

                                    // We can always remove any stale shared opaque files since those are permanently flagged.
                                    if (IsSharedOpaqueFile(realFileEntryPath))
                                    {
                                        continue;
                                    }

                                    // This check is to avoid races when an output is created but hasn't reached the output file system yet.
                                    // Some considerations here: the correctness of this check relies on making sure outputs get to the output
                                    // file system before they are either hardlinked from the cache or (on Windows) made shared opaque by virtue of timestamp flagging.
                                    // Otherwise, we run the risk of altering the file creation original timestamp.
                                    // We could have here an output produced by this pip that is a file rewrite (and whose creation will therefore had occurred after the engine
                                    // started. We do want those as part of the fingerprint, so don't rule it out here.
                                    // A similar route goes for directories that were rewritten by this pip. We want to include them in the same way we do for rewritten sources.
                                    // Observe that rewritten directories occurring at execution time will have its creation time modified to be after engine start, so we need to
                                    // check for this here. On the other hand, on cache lookup, the cache won't recreate the directory if it is already there, so although we don't
                                    // report rewritten directories on cache replay, we don't need to.
                                    if (m_env.State.FileTimestampTracker.IsFileCreationTrackingSupported && 
                                        !isOutputProducedByThisPip &&
                                        // If the directory was removed by the build but we are seeing it here as part of the enumeration, that means
                                        // the directory got rewritten and thus the directory needs to be considered as part of the fingerprint
                                        !FileSystemView.ExistRemovedDirectoryInOutputFileSystem(realFileEntryPath) &&
                                        m_env.State.FileTimestampTracker.PathCreatedAfterEngineStarted(realFileEntryPath))
                                    {
                                        continue;
                                    }

                                    alienFileEnumerationMutable.Add(realFileEntry);
                                }
                            }
                            // Try update the alien file enumeration cache
                            alienFileEnumerationCache.TryAdd(path, alienFileEnumerationMutable);
                            alienFileEnumeration = alienFileEnumerationMutable;
                        }

                        pipFileEnumResult.AddRange(alienFileEnumeration);
                    }

                    // Create a new directory enumeration result that represents this 'overlayed' view
                    var result = new DirectoryEnumerationResult(pipFileEnumResult.Count > 0 ? PathExistence.ExistsAsDirectory : PathExistence.Nonexistent, pipFileEnumResult.ToList());
                    var handleEntry = FilteredHandledEntry(path, request.HandleEntry);

                    return HandleDirectoryContents(request, result, handleEntry);
                }
            });
        }

        private bool IsSharedOpaqueFile(AbsolutePath path) 
        {
            try 
            {
                // Shared opaques can only be present if lazy deletion of shared opaque outputs is enabled, otherwise no shared opaque files
                // should be left under any directory.
                return  m_env.State.LazyDeletionOfSharedOpaqueOutputsEnabled &&
                    SharedOpaqueOutputHelper.IsSharedOpaqueOutput(path.ToString(PathTable), out var sharedOpaqueExistence) &&
                    sharedOpaqueExistence != PathExistence.ExistsAsDirectory;
            }    
            catch (BuildXLException)
            {
                // Checking for shared opaques can fail if the target file requires extra permissions
                // In this case we choose the most conservative option and do not consider the output a shared opaque
                return false;
            }
        }

        /// <summary>
        /// Attempts to enumerate a directory.
        /// </summary>
        /// <param name="request">
        /// Details of how to enumerate the directory.
        /// </param>
        /// <param name="mode">
        /// What BuildXL <see cref="FileSystemViewMode"/> to use.
        /// </param>
        /// <param name="trackPathExistence">
        /// Whether the existence of paths discovered during the enumeration should be tracked to optimize scheduling in future builds.
        /// </param>
        private PathExistence? TryEnumerateDirectory(EnumerationRequest request, FileSystemViewMode mode, bool trackPathExistence = true)
        {
            var directoryContents = GetEnumerationResult(request, mode, trackPathExistence: trackPathExistence);
            var path = request.DirectoryPath;
            var handleEntry = FilteredHandledEntry(path, request.HandleEntry);

            return HandleDirectoryContents(request, directoryContents, handleEntry);
        }

        private PathExistence? HandleDirectoryContents(EnumerationRequest request, DirectoryEnumerationResult directoryContents, Action<AbsolutePath, string> handleEntry)
        {
            if (!directoryContents.IsValid)
            {
                Logger.Log.DirectoryFingerprintingFilesystemEnumerationFailed(Events.StaticContext, request.DirectoryPath.ToString(PathTable), "The error was given before");
                return null;
            }

            foreach (var tuple in directoryContents.Members)
            {
                handleEntry(tuple.Item1, tuple.Item2);
            }

            return directoryContents.Existence;
        }

        private DirectoryEnumerationResult GetEnumerationResult(EnumerationRequest request, FileSystemViewMode mode, bool trackPathExistence = true)
        {
            var path = request.DirectoryPath;
            var pathTable = Context.PathTable;

            Lazy<DirectoryEnumerationResult> lazyDirectoryContents;
            if (!request.CachedDirectoryContents.TryGetValue(path, out lazyDirectoryContents))
            {
                var fileSystemView = FileSystemView;
                lazyDirectoryContents = Lazy.Create(() =>
                {
                    List<DirectoryMemberEntry> members = null;
                    var existence = fileSystemView.TryEnumerateDirectory(path, mode, (name, childPath, childExistence) =>
                    {
                        members = members ?? new List<DirectoryMemberEntry>();
                        members.Add((childPath, name));
                    },
                    cachePathExistence: trackPathExistence);

                    var resultMembers = (IReadOnlyList<DirectoryMemberEntry>)members ?? CollectionUtilities.EmptyArray<DirectoryMemberEntry>();
                    return existence.Succeeded ? new DirectoryEnumerationResult(existence.Result, resultMembers) : DirectoryEnumerationResult.Invalid;
                });

                request.CachedDirectoryContents.AddItem(path, lazyDirectoryContents);
            }

            DirectoryEnumerationResult directoryContents = lazyDirectoryContents.Value;
            return directoryContents;
        }

        private PathExistence? EnumerateDirectoryWithMinimalPipGraph(EnumerationRequest request)
        {
            var path = request.DirectoryPath;
            var handleEntry = FilteredHandledEntry(path, request.HandleEntry);

            InitializePipFileSystem(request.PipInfo);

            return PipFileSystem.EnumerateDirectory(PathTable, path, handleEntry);
        }

        private void InitializePipFileSystem(CacheablePipInfo process)
        {
            if (PipFileSystem != null)
            {
                return;
            }

            m_pooledPipFileSystem = s_pool.GetInstance();

            PipFileSystem.Initialize(PathTable);

            foreach (var input in process.Dependencies)
            {
                PipFileSystem.AddPath(PathTable, input.Path);
            }

            foreach (var output in process.Outputs)
            {
                PipFileSystem.AddPath(PathTable, output.Path);
            }

            foreach (var directoryDependency in process.DirectoryDependencies)
            {
                PipFileSystem.AddSealDirectoryContents(m_env, directoryDependency);
            }
        }

        #endregion
    }
}
