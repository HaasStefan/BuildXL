// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Service.Grpc;
using OperationHints = BuildXL.Cache.ContentStore.Interfaces.Sessions.OperationHints;
using BuildXL.Engine.Cache.Artifacts;
using BuildXL.Engine.Cache.Fingerprints;
using BuildXL.Engine.Cache.Fingerprints.TwoPhase;
using BuildXL.Interop;
using BuildXL.Ipc.Common;
using BuildXL.Ipc.Interfaces;
using BuildXL.Native.IO;
using BuildXL.Pips;
using BuildXL.ProcessPipExecutor;
using BuildXL.Pips.Artifacts;
using BuildXL.Pips.Graph;
using BuildXL.Pips.Operations;
using BuildXL.Processes;
using BuildXL.Processes.Sideband;
using BuildXL.Scheduler.Artifacts;
using BuildXL.Scheduler.Cache;
using BuildXL.Scheduler.Fingerprints;
using BuildXL.Scheduler.Tracing;
using BuildXL.Storage;
using BuildXL.Storage.ChangeTracking;
using BuildXL.Storage.Fingerprints;
using BuildXL.Utilities;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.ParallelAlgorithms;
using BuildXL.Utilities.Core.Tasks;
using BuildXL.Utilities.Core.Tracing;
using static BuildXL.Processes.SandboxedProcessFactory;
using static BuildXL.Utilities.Core.FormattableStringEx;
using System.Collections.Concurrent;
using System.Data;
using System.Collections.ObjectModel;

namespace BuildXL.Scheduler
{
    /// <summary>
    /// This class brings pips to life
    /// </summary>
    public static partial class PipExecutor
    {
        /// <summary>
        /// The maximum number of times to retry running a pip due to internal sandboxed process execution failure.
        /// </summary>
        /// <remarks>
        /// Internal failure include <see cref="RetryReason.OutputWithNoFileAccessFailed"/>
        /// and <see cref="RetryReason.MismatchedMessageCount"/>.
        /// </remarks>
        public const int InternalSandboxedProcessExecutionFailureRetryCountMax = 5;

        private static readonly object s_telemetryDetoursHeapLock = new();

        private static readonly ObjectPool<Dictionary<AbsolutePath, ExtractedPathEntry>> s_pathToObservationEntryMapPool =
            new(() => new Dictionary<AbsolutePath, ExtractedPathEntry>(), map => { map.Clear(); return map; });

        private static readonly ObjectPool<Dictionary<StringId, int>> s_accessedFileNameToUseCountPool =
            new(() => new Dictionary<StringId, int>(), map => { map.Clear(); return map; });

        private static readonly ObjectPool<Dictionary<AbsolutePath, FileOutputData>> s_absolutePathFileOutputDataMapPool =
            new(() => new Dictionary<AbsolutePath, FileOutputData>(), map => { map.Clear(); return map; });

        private static readonly ObjectPool<List<(AbsolutePath, FileMaterializationInfo)>> s_absolutePathFileMaterializationInfoTuppleListPool = Pools.CreateListPool<(AbsolutePath, FileMaterializationInfo)>();

        private static readonly ObjectPool<HashSet<FileArtifact>> s_fileArtifactStoreToCacheSet = Pools.CreateSetPool<FileArtifact>();

        private static readonly ObjectPool<OutputDirectoryEnumerationData> s_outputEnumerationDataPool =
            new(() => new OutputDirectoryEnumerationData(), data => { data.Clear(); return data; });

        private static readonly ArrayPool<Possible<FileMaterializationInfo>?> s_materializationResultsPool = new(1024);

        private class PathSetCheckData
        {
            /// <summary>
            /// Number of unique path sets that have been checked before warning the user that there are too many unique path sets.
            /// </summary>
            private const int NumberOfUniquePathSetsToWarn = 70;

            public int NumberOfUniquePathSetsToCheck => m_processRunnablePip.NumberOfUniquePathSetsToCheck;
            public int UniquePathSetsCount => NumberOfUniquePathSetsToCheck - m_remainingUniquePathSetsToCheck;
            public bool ShouldCheckMorePathSet => m_remainingUniquePathSetsToCheck > 0;

            private int m_remainingUniquePathSetsToCheck;
            private bool m_warnedAboutTooManyUniquePathSets;
            private readonly ProcessRunnablePip m_processRunnablePip;
            private readonly LoggingContext m_loggingContext;

            public PathSetCheckData(LoggingContext loggingContext, ProcessRunnablePip processRunnablePip)
            {
                m_loggingContext = loggingContext;
                m_processRunnablePip = processRunnablePip;
                m_remainingUniquePathSetsToCheck = m_processRunnablePip.NumberOfUniquePathSetsToCheck;
            }

            public void DecrementRemainingUniquePathSetsToCheck() => --m_remainingUniquePathSetsToCheck;

            public void WarnAboutTooManyUniquePathSetsIfNeeded()
            {
                if (!m_warnedAboutTooManyUniquePathSets && UniquePathSetsCount >= NumberOfUniquePathSetsToWarn)
                {
                    Logger.Log.TwoPhaseCheckingTooManyPathSets(m_loggingContext, m_processRunnablePip.Description, UniquePathSetsCount);
                    m_warnedAboutTooManyUniquePathSets = true;
                }
            }
        }

        /// <summary>
        /// Materializes pip's inputs.
        /// </summary>
        public static async Task<PipResultStatus> MaterializeInputsAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            Pip pip)
        {
            Contract.Requires(environment != null);
            Contract.Requires(pip != null);

            // Any errors will be logged within FileContentManager
            var materializedSuccess = await environment.State.FileContentManager.TryMaterializeDependenciesAsync(pip, operationContext);

            // Make sure an error was logged here close to the source since the build will fail later on without dependencies anyway
            Contract.Assert(materializedSuccess || operationContext.LoggingContext.ErrorWasLogged);
            return materializedSuccess ? PipResultStatus.Succeeded : PipResultStatus.Failed;
        }

        /// <summary>
        /// Materializes pip's outputs.
        /// </summary>
        public static async Task<PipResultStatus> MaterializeOutputsAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            Pip pip)
        {
            Contract.Requires(environment != null);
            Contract.Requires(pip != null);

            var maybeMaterialized = await environment.State.FileContentManager.TryMaterializeOutputsAsync(pip, operationContext);

            if (!maybeMaterialized.Succeeded)
            {
                if (!environment.Context.CancellationToken.IsCancellationRequested)
                {
                    Logger.Log.PipFailedToMaterializeItsOutputs(
                        operationContext,
                        pip.GetDescription(environment.Context),
                        maybeMaterialized.Failure.DescribeIncludingInnerFailures());
                }

                AssertErrorWasLoggedWhenNotCancelled(environment, operationContext);
            }

            return maybeMaterialized.Succeeded ? maybeMaterialized.Result.ToPipResult() : PipResultStatus.Failed;
        }

        /// <summary>
        /// Performs a file copy if the destination is not up-to-date with respect to the source.
        /// </summary>
        public static async Task<PipResult> ExecuteCopyFileAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            CopyFile pip,
            bool materializeOutputs = true)
        {
            Contract.Requires(environment != null);
            Contract.Requires(pip != null);

            var context = environment.Context;
            var pathTable = context.PathTable;
            var pipInfo = new PipInfo(pip, context);
            var pipDescription = pipInfo.Description;

            string destination = pip.Destination.Path.ToString(pathTable);
            string source = pip.Source.Path.ToString(pathTable);

            DateTime startTime = DateTime.UtcNow;

            using (operationContext.StartOperation(PipExecutorCounter.CopyFileDuration))
            {
                try
                {
                    FileMaterializationInfo sourceMaterializationInfo = environment.State.FileContentManager.GetInputContent(pip.Source);
                    FileContentInfo sourceContentInfo = sourceMaterializationInfo.FileContentInfo;
                    // Determine whether the destination file of the CopyFile pip is an executable or not.
                    bool isDestinationExecutable;
                    ReadOnlyArray<AbsolutePath> symlinkChain;
                    var symlinkTarget = AbsolutePath.Invalid;
                    bool isSymLink = false;

                    var possibleSymlinkChain = CheckValidSymlinkChainAsync(pip.Source, environment);
                    if (!possibleSymlinkChain.Succeeded)
                    {
                        possibleSymlinkChain.Failure.Throw();
                    }

                    symlinkChain = possibleSymlinkChain.Result;
                    if (symlinkChain.Length > 0)
                    {
                        symlinkTarget = symlinkChain[symlinkChain.Length - 1];
                        isSymLink = true;
                    }

                    if (isSymLink && !environment.Configuration.Schedule.AllowCopySymlink)
                    {
                        (new Failure<string>(I($"Copy symlink '{source}' is not allowed"))).Throw();
                    }

                    if (pip.Source.IsSourceFile)
                    {
                        // If the source file is not symlink, we rely on the dependents of copy file to call 'RestoreContentInCache'.

                        if (sourceContentInfo.Hash == WellKnownContentHashes.AbsentFile)
                        {
                            Logger.Log.PipCopyFileSourceFileDoesNotExist(operationContext, pipDescription, source, destination);
                            return PipResult.Create(PipResultStatus.Failed, startTime);
                        }

                        if (sourceContentInfo.Hash == WellKnownContentHashes.UntrackedFile)
                        {
                            Logger.Log.PipCopyFileFromUntrackableDir(operationContext, pipDescription, source, destination);
                            return PipResult.Create(PipResultStatus.Failed, startTime);
                        }
                    }
                    else
                    {
                        Contract.Assume(sourceContentInfo.Hash != WellKnownContentHashes.UntrackedFile);
                    }

                    bool shouldStoreOutputToCache = environment.Configuration.Schedule.StoreOutputsToCache || IsRewriteOutputFile(environment, pip.Destination);

                    // If the file is symlink and the chain is valid, the final target is a source file
                    // (otherwise, we would not have passed symlink chain validation).
                    // We now need to store the final target to the cache so it is available for file-level materialization downstream.
                    if (isSymLink && shouldStoreOutputToCache)
                    {
                        // We assume that source files cannot be made read-only so we use copy file materialization
                        // rather than hardlinking
                        // If the source of the CopyFile pip is a symlink then we check if the symlink target file is an executable or not.
                        isDestinationExecutable = FileUtilities.CheckForExecutePermission(symlinkTarget.ToString(pathTable)).Result;
                        var maybeStored = await environment.LocalDiskContentStore.TryStoreAsync(
                            environment.Cache.ArtifactContentCache,
                            fileRealizationModes: FileRealizationMode.Copy,
                            path: symlinkTarget,
                            tryFlushPageCacheToFileSystem: false,

                            // Trust the cache for content hash because we need the hash of the content of the target.
                            knownContentHash: null,

                            // Source should have been tracked by hash-source file pip or by CheckValidSymlinkChainAsync, no need to retrack.
                            trackPath: false,
                            // A copy file is never a dynamic output
                            outputDirectoryRoot: AbsolutePath.Invalid,
                            isExecutable: isDestinationExecutable);

                        if (!maybeStored.Succeeded)
                        {
                            maybeStored.Failure.Throw();
                        }

                        // save the content info of the final target
                        sourceContentInfo = maybeStored.Result.FileContentInfo;

                        var possiblyTracked = await TrackSymlinkChain(symlinkChain);
                        if (!possiblyTracked.Succeeded)
                        {
                            possiblyTracked.Failure.Throw();
                        }
                    }
                    else
                    {
                        // If the source of the copy file pip is not symlink, then we use the isExecutable property from sourceMaterializationInfo to determine whether it’s an executable or not.
                        isDestinationExecutable = sourceMaterializationInfo.IsExecutable;
                    }

                    // Just pass through the hash
                    environment.State.FileContentManager.ReportOutputContent(
                        operationContext,
                        pip.SemiStableHash,
                        pip.Destination,
                        FileMaterializationInfo.CreateWithUnknownName(sourceContentInfo, isExecutable: isDestinationExecutable),
                        PipOutputOrigin.NotMaterialized);

                    var result = PipResultStatus.NotMaterialized;
                    if (materializeOutputs || !shouldStoreOutputToCache)
                    {
                        // Materialize the outputs if specified
                        var maybeMaterialized = await environment.State.FileContentManager.TryMaterializeOutputsAsync(pip, operationContext);

                        if (!maybeMaterialized.Succeeded)
                        {
                            if (!shouldStoreOutputToCache)
                            {
                                result = await CopyAndTrackAsync(operationContext, environment, pip);

                                // Report again to notify the FileContentManager that the file has been materialized.
                                environment.State.FileContentManager.ReportOutputContent(
                                    operationContext,
                                    pip.SemiStableHash,
                                    pip.Destination,
                                    FileMaterializationInfo.CreateWithUnknownName(sourceContentInfo, isExecutable: isDestinationExecutable),
                                    PipOutputOrigin.Produced);

                                var possiblyTracked = await TrackSymlinkChain(symlinkChain);
                                if (!possiblyTracked.Succeeded)
                                {
                                    possiblyTracked.Failure.Throw();
                                }
                            }
                            else
                            {
                                maybeMaterialized.Failure.Throw();
                            }
                        }
                        else
                        {
                            // No need to report pip output origin because TryMaterializeOutputAsync did that already through
                            // PlaceFileAsync of FileContentManager.

                            result = maybeMaterialized.Result.ToPipResult();
                        }
                    }

                    return new PipResult(
                        result,
                        PipExecutionPerformance.Create(result, startTime),
                        false,
                        // report accesses to symlink chain elements
                        symlinkChain,
                        ReadOnlyArray<AbsolutePath>.Empty,
                        ReadOnlyArray<AbsolutePath>.Empty,
                        ReadOnlyArray<AbsolutePath>.Empty,
                        0);
                }
                catch (BuildXLException ex)
                {
                    string message = ex.LogEventMessage;
                    if (ex.NativeErrorCode() == NativeIOConstants.ErrorSharingViolation && OperatingSystemHelper.IsWindowsOS && FileUtilities.TryFindOpenHandlesToFile(source, out string diagnosticInfo, printCurrentFilePath: true))
                    {
                        message = message + " " + $"Open file handle diagnostic data: {diagnosticInfo}";
                    }
                    Logger.Log.PipCopyFileFailed(operationContext, pipDescription, source, destination, ex.LogEventErrorCode, message);

                    return PipResult.Create(PipResultStatus.Failed, startTime);
                }
            }

            async Task<Possible<Unit>> TrackSymlinkChain(ReadOnlyArray<AbsolutePath> chain)
            {
                foreach (var chainElement in chain)
                {
                    var possiblyTracked = await environment.LocalDiskContentStore.TryTrackAsync(
                        FileArtifact.CreateSourceFile(chainElement),
                        // This chain corresponds to the source of the copy file pip, and therefore not an output file. Flushing is intended to happen
                        // for just-produced outputs.
                        tryFlushPageCacheToFileSystem: false,
                        // Tracking symlinks happen in the context of a non-process pip, so this is never a dynamic output
                        outputDirectoryRoot: AbsolutePath.Invalid,
                        ignoreKnownContentHashOnDiscoveringContent: true,
                        isReparsePoint: true);

                    if (!possiblyTracked.Succeeded)
                    {
                        return possiblyTracked.Failure;
                    }
                }

                return Unit.Void;
            }
        }

        /// <summary>
        /// Checks whether a file forms a valid symlink chain.
        /// </summary>
        /// <remarks>
        /// A symlink chain is valid iff:
        /// (1) all target paths are valid paths
        /// (2) every element in the chain (except the head of the chain) is a source file (i.e., not produced during the build)
        /// </remarks>
        /// <returns>List of chain elements that 'source' points to (i.e., source is not included)</returns>
        private static Possible<ReadOnlyArray<AbsolutePath>> CheckValidSymlinkChainAsync(FileArtifact source, IPipExecutionEnvironment environment)
        {
            // check whether 'source' is a symlink
            // we are doing the check here using FileMaterializationInfo because 'source' might not be present on disk
            // (e.g., in case of lazyOutputMaterialization)
            var materializationInfo = environment.State.FileContentManager.GetInputContent(source);
            if (!materializationInfo.ReparsePointInfo.IsActionableReparsePoint)
            {
                return ReadOnlyArray<AbsolutePath>.Empty;
            }

            var symlinkPath = source.Path;
            var maybeTarget = FileUtilities.ResolveSymlinkTarget(
                symlinkPath.ToString(environment.Context.PathTable),
                materializationInfo.ReparsePointInfo.GetReparsePointTarget());

            if (!maybeTarget.Succeeded)
            {
                return maybeTarget.Failure;
            }

            var symlinkTarget = maybeTarget.Result;

            // get the symlink chain starting at the source's target (i.e., the 2nd element of the chain formed by 'source')
            // all the elements in this sub-chain must be source files
            var openResult = FileUtilities.TryCreateOrOpenFile(
                symlinkTarget,
                FileDesiredAccess.GenericRead,
                FileShare.Read | FileShare.Delete,
                FileMode.Open,
                FileFlagsAndAttributes.FileFlagOverlapped | FileFlagsAndAttributes.FileFlagOpenReparsePoint,
                out var handle);

            if (!openResult.Succeeded)
            {
                // we could not get a handle for the head of the sub-chain
                // it could be because the file/path does not exist
                // it might not exists because it's an output file and the file was not materialized -> invalid chain,
                // or because a symlink points to a missing file -> invalid chain
                return CreateInvalidChainFailure(I($"Failed to create a handle for a chain element ('{symlinkTarget}')"));
            }

            using (handle)
            {
                var chain = new List<AbsolutePath>();
                var symlinkChainElements = new List<string>();
                FileUtilities.GetChainOfReparsePoints(handle, symlinkTarget, symlinkChainElements);
                Contract.Assume(symlinkChainElements.Count > 0);

                // The existence of the last element in the chain returned by GetChainOfReparsePoints
                // is not guaranteed, so we need to check that the file is available.
                if (!FileUtilities.Exists(symlinkChainElements[symlinkChainElements.Count - 1]))
                {
                    return CreateInvalidChainFailure(I($"File does not exist ('{symlinkChainElements[symlinkChainElements.Count - 1]}')"));
                }

                foreach (string chainElement in symlinkChainElements)
                {
                    AbsolutePath.TryCreate(environment.Context.PathTable, chainElement, out var targetPath);

                    if (!targetPath.IsValid)
                    {
                        return CreateInvalidChainFailure(I($"Failed to parse an element of the chain ('{chainElement}')"));
                    }

                    chain.Add(targetPath);

                    var targetArtifact = environment.PipGraphView.TryGetLatestFileArtifactForPath(targetPath);
                    if (targetArtifact.IsValid && targetArtifact.IsOutputFile)
                    {
                        return CreateInvalidChainFailure(I($"An element of the chain ('{chainElement}') is a declared output of another pip."));
                    }

                    // If the file is not known to the graph, check whether the file is in an opaque or shared opaque directory.
                    // If it's inside such a directory, we treat it as an output file -> chain is not valid.
                    if (!targetArtifact.IsValid && environment.PipGraphView.IsPathUnderOutputDirectory(targetPath, out _))
                    {
                        return CreateInvalidChainFailure(I($"An element of the chain ('{chainElement}') is inside of an opaque directory."));
                    }
                }

                return ReadOnlyArray<AbsolutePath>.From(chain);
            }

            Failure<string> CreateInvalidChainFailure(string message, Failure innerFailure = null)
            {
                return new Failure<string>(I($"Invalid symlink chain ('{source.Path.ToString(environment.Context.PathTable)}' -> ...). {message}"), innerFailure);
            }
        }

        private static async Task<PipResultStatus> CopyAndTrackAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            CopyFile copyFile)
        {
            PathTable pathTable = environment.Context.PathTable;
            ExpandedAbsolutePath destination = copyFile.Destination.Path.Expand(pathTable);
            ExpandedAbsolutePath source = copyFile.Source.Path.Expand(pathTable);

            FileUtilities.CreateDirectory(Path.GetDirectoryName(destination.ExpandedPath));

            var copy = await FileUtilities.CopyFileAsync(source.ExpandedPath, destination.ExpandedPath);
            if (!copy)
            {
                (new Failure<string>(I($"Unable to copy from '{source}' to '{destination}'"))).Throw();
            }

            // if /storeOutputsToCache- was used, mark the destination here;
            // otherwise it will get marked in ReportFileArtifactPlaced.
            if (!environment.Configuration.Schedule.StoreOutputsToCache)
            {
                MakeSharedOpaqueOutputIfNeeded(environment, copyFile.Destination);
            }

            var mayBeTracked = await TrackPipOutputAsync(
                operationContext, 
                copyFile, 
                environment, 
                copyFile.Destination, 
                // This is a copy file and therefore never a dynamic file
                outputDirectoryRoot: AbsolutePath.Invalid);

            if (!mayBeTracked.Succeeded)
            {
                mayBeTracked.Failure.Throw();
            }

            return PipResultStatus.Succeeded;
        }

        /// <summary>
        /// Writes the given <see cref="PipData"/> contents if the destination's content does not already match.
        /// </summary>
        public static async Task<PipResult> ExecuteWriteFileAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            WriteFile pip,
            bool materializeOutputs = true)
        {
            Contract.Requires(environment != null);
            Contract.Requires(pip != null);

            DateTime startTime = DateTime.UtcNow;
            return PipResult.Create(
                status:
                    FromPossibleResult(
                        await
                            TryExecuteWriteFileAsync(operationContext, environment, pip, materializeOutputs: materializeOutputs, reportOutputs: true)),
                executionStart: startTime);
        }

        private static PipResultStatus FromPossibleResult(Possible<PipResultStatus> possibleResult)
        {
            return possibleResult.Succeeded ? possibleResult.Result : PipResultStatus.Failed;
        }

        /// <summary>
        /// Writes the given <see cref="PipData"/> contents if the destination's content does not already match.
        /// </summary>
        public static async Task<Possible<PipResultStatus>> TryExecuteWriteFileAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            WriteFile pip,
            bool materializeOutputs,
            bool reportOutputs)
        {
            Contract.Requires(environment != null);
            Contract.Requires(pip != null);

            using (operationContext.StartOperation(PipExecutorCounter.WriteFileDuration))
            {
                // TODO: It'd be nice if PipData could instead write encoded bytes to a stream, in which case we could
                //       first compute the hash and then possibly do a second pass to actually write to a file (without allocating
                //       several possibly large buffers and strings).
                string contents = pip.Contents.ToString(GetPipFragmentRendererForWriteFile(pip.WriteFileOptions, environment.Context.PathTable));

                Encoding encoding;
                switch (pip.Encoding)
                {
                    case WriteFileEncoding.Utf8:
                        encoding = Encoding.UTF8;
                        break;
                    case WriteFileEncoding.Ascii:
                        encoding = Encoding.ASCII;
                        break;
                    default:
                        throw Contract.AssertFailure("Unexpected encoding");
                }

                Possible<PipResultStatus> writeFileStatus = await TryWriteFileAndReportOutputsAsync(
                    operationContext,
                    environment,
                    pip.Destination,
                    contents,
                    encoding,
                    pip,
                    materializeOutputs: materializeOutputs,
                    reportOutputs: reportOutputs);
                return writeFileStatus;
            }
        }

        /// <summary>
        /// Executes an Ipc pip
        /// </summary>
        public static async Task<ExecutionResult> ExecuteIpcAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            IpcPip pip)
        {
            var pathTable = environment.Context.PathTable;

            // ensure services are running
            bool ensureServicesRunning =
                await environment.State.ServiceManager.TryRunServiceDependenciesAsync(environment, pip.PipId, pip.ServicePipDependencies, operationContext);
            if (!ensureServicesRunning)
            {
                Logger.Log.PipFailedDueToServicesFailedToRun(operationContext, pip.GetDescription(environment.Context));
                return ExecutionResult.GetFailureNotRunResult(operationContext);
            }

            // create IPC operation
            IIpcProvider ipcProvider = environment.IpcProvider;
            string monikerId = pip.IpcInfo.IpcMonikerId.ToString(pathTable.StringTable);
            string connectionString = ipcProvider.LoadAndRenderMoniker(monikerId);
            IClient client = ipcProvider.GetClient(connectionString, pip.IpcInfo.IpcClientConfig);

            var ipcOperationPayload = pip.MessageBody.ToString(environment.PipFragmentRenderer, useIpcEscaping: true);
            var operation = new IpcOperation(ipcOperationPayload, waitForServerAck: true);

            // execute async
            IIpcResult ipcResult;
            using (operationContext.StartOperation(PipExecutorCounter.IpcSendAndHandleDuration))
            {
                // execute async
                ipcResult = await IpcSendAndHandleErrorsAsync(client, operation);
            }

            ExecutionResult executionResult = new ExecutionResult
            {
                MustBeConsideredPerpetuallyDirty = true,
            };

            if (ipcResult.Succeeded)
            {
                TimeSpan request_queueDuration = operation.Timestamp.Request_BeforeSendTime - operation.Timestamp.Request_BeforePostTime;
                TimeSpan request_sendDuration = operation.Timestamp.Request_AfterSendTime - operation.Timestamp.Request_BeforeSendTime;
                TimeSpan request_serverAckDuration = operation.Timestamp.Request_AfterServerAckTime - operation.Timestamp.Request_AfterSendTime;
                TimeSpan responseDuration = ipcResult.Timestamp.Response_BeforeDeserializeTime - operation.Timestamp.Request_AfterServerAckTime;

                TimeSpan response_deserializeDuration = ipcResult.Timestamp.Response_AfterDeserializeTime - ipcResult.Timestamp.Response_BeforeDeserializeTime;
                TimeSpan response_queueSetDuration = ipcResult.Timestamp.Response_BeforeSetTime - ipcResult.Timestamp.Response_AfterDeserializeTime;
                TimeSpan response_SetDuration = ipcResult.Timestamp.Response_AfterSetTime - ipcResult.Timestamp.Response_BeforeSetTime;
                TimeSpan response_AfterSetTaskDuration = DateTime.UtcNow - ipcResult.Timestamp.Response_AfterSetTime;

                environment.Counters.AddToCounter(
                    PipExecutorCounter.Ipc_RequestQueueDurationMs,
                    (long)request_queueDuration.TotalMilliseconds);

                environment.Counters.AddToCounter(
                    PipExecutorCounter.Ipc_RequestSendDurationMs,
                    (long)request_sendDuration.TotalMilliseconds);

                environment.Counters.AddToCounter(
                    PipExecutorCounter.Ipc_RequestServerAckDurationMs,
                    (long)request_serverAckDuration.TotalMilliseconds);

                environment.Counters.AddToCounter(
                    PipExecutorCounter.Ipc_ResponseDurationMs,
                    (long)responseDuration.TotalMilliseconds);

                environment.Counters.AddToCounter(
                    PipExecutorCounter.Ipc_ResponseDeserializeDurationMs,
                    (long)response_deserializeDuration.TotalMilliseconds);

                environment.Counters.AddToCounter(
                    PipExecutorCounter.Ipc_ResponseQueueSetDurationMs,
                    (long)response_queueSetDuration.TotalMilliseconds);

                environment.Counters.AddToCounter(
                    PipExecutorCounter.Ipc_ResponseSetDurationMs,
                    (long)response_SetDuration.TotalMilliseconds);

                environment.Counters.AddToCounter(
                    PipExecutorCounter.Ipc_ResponseAfterSetTaskDurationMs,
                    (long)response_AfterSetTaskDuration.TotalMilliseconds);

                if (environment.Configuration.Schedule.WriteIpcOutput || environment.State.ServiceManager.HasRealConsumers(pip))
                {
                    // write payload to pip.OutputFile
                    Possible<PipResultStatus> writeFileStatus = await TryWriteFileAndReportOutputsAsync(
                        operationContext,
                        environment,
                        FileArtifact.CreateOutputFile(pip.OutputFile.Path),
                        ipcResult.Payload,
                        Encoding.UTF8,
                        pip,
                        executionResult,
                        materializeOutputs: true,
                        logErrors: true);

                    executionResult.SetResult(operationContext, writeFileStatus.Succeeded ? writeFileStatus.Result : PipResultStatus.Failed);
                }
                else
                {
                    // Use absent file when write IPC output is disabled.
                    var absentFileInfo = FileMaterializationInfo.CreateWithUnknownLength(WellKnownContentHashes.AbsentFile);

                    // Report output content in result
                    executionResult.ReportOutputContent(pip.OutputFile, absentFileInfo, PipOutputOrigin.NotMaterialized);
                    executionResult.SetResult(operationContext, PipResultStatus.NotMaterialized);
                }
            }
            else
            {
                var pipDescription = pip.GetDescription(environment.Context);

                if (environment.IsTerminatingWithInternalError)
                {
                    // Schedule is terminating due to an internal error. This is an aggressive termination and
                    // service pips are likely to be killed. All IPC errors received after the termination was
                    // started are very likely were caused by that termination. Downgrade all these errors to
                    // warnings to make error log more readable and reduce confusion.
                    Logger.Log.PipIpcFailedWhileSheduleWasTerminating(
                        operationContext,
                        pipDescription,
                        operation.Payload,
                        connectionString,
                        ipcResult.ExitCode.ToString(),
                        ipcResult.Payload);
                }
                // log error if execution failed
                else if (ipcResult.ExitCode == IpcResultStatus.InvalidInput)
                {
                    // we separate the 'invalid input' errors here, so they can be classified as 'user errors'
                    Logger.Log.PipIpcFailedDueToInvalidInput(
                        operationContext,
                        pipDescription,
                        operation.Payload,
                        connectionString,
                        ipcResult.Payload);
                }
                else if (ipcResult.ExitCode == IpcResultStatus.TransmissionError)
                {
                    // we separate transmission errors here, so they can be properly classified as InfrastructureErrors
                    Logger.Log.PipIpcFailedDueToInfrastructureError(
                        operationContext,
                        pipDescription,
                        operation.Payload,
                        connectionString,
                        ipcResult.Payload);
                }
                else if (ipcResult.ExitCode == IpcResultStatus.ManifestGenerationError)
                {
                    // we separate build manifest-related errors here, so they can be properly tracked/reported
                    Logger.Log.PipIpcFailedDueToBuildManifestGenerationError(
                        operationContext,
                        pipDescription,
                        operation.Payload,
                        connectionString,
                        ipcResult.Payload);
                }
                else if (ipcResult.ExitCode == IpcResultStatus.SigningError)
                {
                    // we separate build manifest-related errors here, so they can be properly tracked/reported
                    Logger.Log.PipIpcFailedDueToBuildManifestSigningError(
                        operationContext,
                        pipDescription,
                        operation.Payload,
                        connectionString,
                        ipcResult.Payload);
                }
                else if (ipcResult.ExitCode == IpcResultStatus.ExternalServiceError)
                {
                    // separate external errors for proper tracking
                    Logger.Log.PipIpcFailedDueToExternalServiceError(
                        operationContext,
                        pipDescription,
                        operation.Payload,
                        connectionString,
                        ipcResult.Payload);
                }
                else
                {
                    Logger.Log.PipIpcFailed(
                        operationContext,
                        pipDescription,
                        operation.Payload,
                        connectionString,
                        ipcResult.ExitCode.ToString(),
                        ipcResult.Payload);
                }

                // Mark the pip as failed even if we logged a warning above instead of an error (due to schedule termination).
                executionResult.SetResult(operationContext, PipResultStatus.Failed);
            }

            executionResult.Seal();
            return executionResult;
        }

        private static async Task<IIpcResult> IpcSendAndHandleErrorsAsync(IClient client, IIpcOperation operation)
        {
            try
            {
                // this should never throw, but to be extra safe we wrap this in try/catch.
                return await client.Send(operation);
            }
            catch (Exception e)
            {
                return new IpcResult(IpcResultStatus.TransmissionError, e.ToStringDemystified());
            }
        }

        private static void MakeSharedOpaqueOutputIfNeeded(IPipExecutionEnvironment environment, AbsolutePath path)
        {
            if (!environment.Configuration.Sandbox.UnsafeSandboxConfiguration.SkipFlaggingSharedOpaqueOutputs() &&
                environment.PipGraphView.IsPathUnderOutputDirectory(path, out bool isItSharedOpaque) && isItSharedOpaque)
            {
                string expandedPath = path.ToString(environment.Context.PathTable);
                SharedOpaqueOutputHelper.EnforceFileIsSharedOpaqueOutput(expandedPath);
            }
        }

        /// <summary>
        /// Writes <paramref name="contents"/> to disk at location <paramref name="destinationFile"/> using
        /// <paramref name="encoding"/>.
        ///
        /// If writing to disk succeeds, reports the produced output to the environment (<see cref="FileContentManager.ReportOutputContent"/>).
        ///
        /// Catches any <see cref="BuildXLException"/> and logs an error when that happens.
        /// </summary>
        private static async Task<Possible<PipResultStatus>> TryWriteFileAndReportOutputsAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            FileArtifact destinationFile,
            string contents,
            Encoding encoding,
            Pip producerPip,
            ExecutionResult executionResult = null,
            bool materializeOutputs = true,
            bool logErrors = true,
            bool reportOutputs = true)
        {
            var context = environment.Context;
            var pathTable = context.PathTable;
            var pipInfo = new PipInfo(producerPip, context);
            var pipDescription = pipInfo.Description;
            var fileContentManager = environment.State.FileContentManager;

            var destinationAsString = destinationFile.Path.ToString(pathTable);
            byte[] encoded = encoding.GetBytes(contents);

            try
            {
                ContentHash contentHash;
                PipOutputOrigin outputOrigin;
                FileMaterializationInfo fileContentInfo;

                using (operationContext.StartOperation(PipExecutorCounter.WriteFileHashingDuration))
                {
                    // No need to hash the file if it is already registered with the file content manager.
                    if (!fileContentManager.TryGetInputContent(destinationFile, out fileContentInfo))
                    {
                        contentHash = ContentHashingUtilities.HashBytes(encoded);
                    }
                    else
                    {
                        contentHash = fileContentInfo.Hash;
                    }
                }

                if (materializeOutputs)
                {
                    string directoryName = ExceptionUtilities.HandleRecoverableIOException(
                        () => Path.GetDirectoryName(destinationAsString),
                        ex => { throw new BuildXLException("Cannot get directory name", ex); });
                    FileUtilities.CreateDirectory(directoryName);

                    Possible<ContentMaterializationResult>? possiblyMaterialized = null;
                    if (environment.Configuration.Distribution.BuildRole == DistributedBuildRoles.None)
                    {
                        // Optimistically check to see if the file is already in the cache. If so we can just exit
                        // TFS 929846 prevents us from utilizing this optimization on distributed builds since the pin doesn't
                        // flow through to the remote when it is successful on the local. That means that files aren't guaranteed
                        // to be available on other machines.
                        possiblyMaterialized = await environment.LocalDiskContentStore.TryMaterializeAsync(
                                environment.Cache.ArtifactContentCache,
                                GetFileRealizationMode(environment),
                                destinationFile,
                                contentHash,
                                cancellationToken: environment.Context.CancellationToken);
                    }

                    if (possiblyMaterialized.HasValue && possiblyMaterialized.Value.Succeeded)
                    {
                        outputOrigin = possiblyMaterialized.Value.Result.Origin.ToPipOutputOriginHidingDeploymentFromCache();
                        fileContentInfo = possiblyMaterialized.Value.Result.TrackedFileContentInfo.FileMaterializationInfo;
                    }
                    else
                    {
                        bool fileWritten = await FileUtilities.WriteAllBytesAsync(destinationAsString, encoded);
                        Contract.Assume(
                            fileWritten,
                            "WriteAllBytes only returns false when the predicate parameter (not supplied) fails. Otherwise it should throw a BuildXLException and be handled below.");

                        bool shouldStoreOutputsToCache = environment.Configuration.Schedule.StoreOutputsToCache || IsRewriteOutputFile(environment, destinationFile);

                        var possiblyStored = shouldStoreOutputsToCache
                            ? await environment.LocalDiskContentStore.TryStoreAsync(
                                environment.Cache.ArtifactContentCache,
                                GetFileRealizationMode(environment),
                                destinationFile,
                                tryFlushPageCacheToFileSystem: environment.Configuration.Sandbox.FlushPageCacheToFileSystemOnStoringOutputsToCache,
                                knownContentHash: contentHash,
                                isReparsePoint: false,
                                // This is a write file and therefore never a dynamic output
                                outputDirectoryRoot: AbsolutePath.Invalid)
                            : await TrackPipOutputAsync(
                                operationContext, 
                                producerPip, 
                                environment, 
                                destinationFile, 
                                // This is a write file and therefore never a dynamic output
                                outputDirectoryRoot: AbsolutePath.Invalid);

                        if (!possiblyStored.Succeeded)
                        {
                            throw possiblyStored.Failure.Throw();
                        }

                        outputOrigin = PipOutputOrigin.Produced;
                        fileContentInfo = possiblyStored.Result.FileMaterializationInfo;
                    }
                }
                else
                {
                    outputOrigin = PipOutputOrigin.NotMaterialized;
                    fileContentInfo = FileMaterializationInfo.CreateWithUnknownName(new FileContentInfo(contentHash, encoded.Length));
                }

                if (reportOutputs)
                {
                    if (executionResult != null)
                    {
                        // IPC pips specify an execution result which is reported back to the scheduler
                        // which then reports the output content to the file content manager on the worker
                        // and orchestrator machines in distributed builds
                        executionResult.ReportOutputContent(destinationFile, fileContentInfo, outputOrigin);
                    }
                    else
                    {
                        // Write file pips do not specify execution result since they are not distributed
                        // (i.e. they only run on the orchestrator). Given that, they report directly to the file content manager.
                        fileContentManager.ReportOutputContent(
                            operationContext,
                            pipInfo.SemiStableHash,
                            destinationFile,
                            fileContentInfo,
                            outputOrigin);
                    }
                }

                MakeSharedOpaqueOutputIfNeeded(environment, destinationFile.Path);

                return outputOrigin.ToPipResult();
            }
            catch (BuildXLException ex)
            {
                if (logErrors)
                {
                    Logger.Log.PipWriteFileFailed(operationContext, pipDescription, destinationAsString, ex.LogEventErrorCode, ex.LogEventMessage);
                    return PipResultStatus.Failed;
                }
                else
                {
                    return new Failure<string>(PipWriteFileFailedMessage(pipDescription, destinationAsString, ex));
                }
            }
        }

        private static string PipWriteFileFailedMessage(string pipDescription, string path, BuildXLException ex)
        {
            return I($"[{pipDescription}] Write file '{path}' failed with error code {ex.LogEventErrorCode:X8}: {ex.LogEventMessage}");
        }

        /// <summary>
        /// Analyze pip violations and store two-phase cache entry.
        /// </summary>
        public static async Task<ExecutionResult> PostProcessExecutionAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state,
            CacheableProcess cacheableProcess,
            ExecutionResult processExecutionResult)
        {
            Contract.Requires(environment != null);
            Contract.Requires(cacheableProcess != null);

            var process = cacheableProcess.Process;
            processExecutionResult.Seal();

            PipResultStatus status = processExecutionResult.Result;
            StoreCacheEntryResult storeCacheEntryResult = StoreCacheEntryResult.Succeeded;

            using (operationContext.StartOperation(PipExecutorCounter.StoreProcessToCacheDurationMs, details: processExecutionResult.TwoPhaseCachingInfo?.ToString()))
            {
                if (processExecutionResult.TwoPhaseCachingInfo != null)
                {
                    storeCacheEntryResult = await StoreTwoPhaseCacheEntryAsync(
                        operationContext,
                        process,
                        cacheableProcess,
                        environment,
                        state,
                        processExecutionResult.TwoPhaseCachingInfo,
                        processExecutionResult.PipCacheDescriptorV2Metadata.Id);

                    if (storeCacheEntryResult.Converged && !IsProcessPreservingOutputs(environment, process))
                    {
                        environment.Counters.AddToCounter(PipExecutorCounter.ExecuteConvergedProcessDuration, processExecutionResult.PerformanceInformation.ProcessExecutionTime);
                        environment.Counters.IncrementCounter(PipExecutorCounter.ProcessPipTwoPhaseCacheEntriesConverged);

                        // Copy the status into the result, if the pip was successful, it will remain so, if the pip
                        // failed during fingerprint storage we want that status,
                        // and finally, the pip can have its status converted from executed to run from cache
                        // if determinism recovery happened and the cache forced convergence.
                        processExecutionResult = processExecutionResult.CreateSealedConvergedExecutionResult(storeCacheEntryResult.ConvergedExecutionResult);
                    }
                    else
                    {
                        environment.Counters.IncrementCounter(PipExecutorCounter.ProcessPipTwoPhaseCacheEntriesAdded);
                    }
                }
            }

            return processExecutionResult;
        }

        /// <summary>
        /// Report results from given execution result to the environment and file content manager
        /// </summary>
        internal static void ReportExecutionResultOutputContent(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            long pipSemiStableHash,
            ExecutionResult processExecutionResult,
            bool doubleWriteErrorsAreWarnings = false)
        {
            PipOutputOrigin? overrideOutputOrigin = null;
            if (processExecutionResult.Result == PipResultStatus.NotMaterialized)
            {
                overrideOutputOrigin = PipOutputOrigin.NotMaterialized;
            }

            foreach (var (directoryArtifact, fileArtifactArray) in processExecutionResult.DirectoryOutputs)
            {
                environment.State.FileContentManager.ReportDynamicDirectoryContents(
                    directoryArtifact,
                    fileArtifactArray,
                    overrideOutputOrigin ?? PipOutputOrigin.Produced);
            }

            foreach (var output in processExecutionResult.OutputContent)
            {
                environment.State.FileContentManager.ReportOutputContent(
                    operationContext,
                    pipSemiStableHash,
                    output.fileArtifact,
                    output.fileInfo,
                    overrideOutputOrigin ?? output.Item3,
                    doubleWriteErrorsAreWarnings);
            }

            // The file content manager is not really aware of directories, unless there are files
            // underneath them. But in order to properly compute directory enumeration fingerprints for
            // minimal graph with alien files mode, we need to make the output file system aware of
            // created directories, even if they are empty
            foreach (var directory in processExecutionResult.CreatedDirectories)
            {
                environment.State.FileSystemView.ReportOutputFileSystemDirectoryCreated(directory);
            }

            if (processExecutionResult.NumberOfWarnings > 0)
            {
                environment.ReportWarnings(fromCache: false, count: processExecutionResult.NumberOfWarnings);
            }
        }

        /// <summary>
        /// Merges all the file access allowlist and violations of a pip from all its retries.
        /// This includes both inline and reschedule retry.
        /// </summary>
        /// <remarks>
        /// Inline - retry happens on the same worker. Reschedule - where the retry happens on a different worker.
        /// This method also ensures that we do not merge the lists unless there has been a retry.
        /// </remarks>
        internal static void MergeAllAccessesAndViolations(IEnumerable<ExecutionResult> allExecutionResults, out IReadOnlyCollection<ReportedFileAccess> fileAccessViolationsNotAllowlisted, out IReadOnlyCollection<ReportedFileAccess> allowlistedFileAccessViolations)
        {
            fileAccessViolationsNotAllowlisted = null;
            allowlistedFileAccessViolations = null;

            if (allExecutionResults == null)
            {
                return;
            }

            if (allExecutionResults.Count() == 1)
            {
                fileAccessViolationsNotAllowlisted = allExecutionResults.First()?.FileAccessViolationsNotAllowlisted;
                allowlistedFileAccessViolations = allExecutionResults.First()?.AllowlistedFileAccessViolations;
            }
            else
            {
                // Merge FileAccessViolationNotAllowlisted.
                fileAccessViolationsNotAllowlisted = allExecutionResults.Where(r => r.FileAccessViolationsNotAllowlisted != null)
                                                                        .SelectMany(r => r.FileAccessViolationsNotAllowlisted)
                                                                        .Distinct()
                                                                        .ToList();

                // Merge AllowlistedFileAccessViolations.
                allowlistedFileAccessViolations = allExecutionResults.Where(r => r.AllowlistedFileAccessViolations != null)
                                                                     .SelectMany(r => r.AllowlistedFileAccessViolations)
                                                                     .Distinct()
                                                                     .ToList();
            }
        }

        /// <summary>
        /// Merges all the ObservedFileAccesses, SharedDynamicDirectoryWriteAccess, DynamicObservations and ExclusiveOpaqueContent of a pip from all its retires.
        /// This includes both inline and reschedule retry.
        /// </summary>
        /// <remarks>
        /// Inline - retry happens on the same worker. Reschedule - where the retry happens on a different worker.
        /// This method also ensures that we do not do merge the lists unless there has been a retry.
        /// </remarks>
        internal static void MergeAllDynamicAccessesAndViolations(IEnumerable<ExecutionResult> allExecutionResults,
                                                                  out IReadOnlyDictionary<AbsolutePath, ObservedInputType> allowedUndeclaredRead,
                                                                  out IReadOnlyDictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>> sharedDynamicDirectoryWriteAccesses,
                                                                  out ReadOnlyArray<(AbsolutePath Path, DynamicObservationKind Kind)>? dynamicObservations,
                                                                  out ReadOnlyArray<(DirectoryArtifact directoryArtifact, ReadOnlyArray<FileArtifactWithAttributes> fileArtifactArray)>? exclusiveOpaqueContent)
        {
            allowedUndeclaredRead = null;
            sharedDynamicDirectoryWriteAccesses = null;
            dynamicObservations = null;
            exclusiveOpaqueContent = null;

            if (allExecutionResults == null)
            {
                return;
            }

            if (allExecutionResults.Count() == 1)
            {
                allowedUndeclaredRead = allExecutionResults.First().AllowedUndeclaredReads;
                sharedDynamicDirectoryWriteAccesses = allExecutionResults.First()?.SharedDynamicDirectoryWriteAccesses;
                dynamicObservations = allExecutionResults.First()?.DynamicObservations;
                exclusiveOpaqueContent = allExecutionResults.First().DirectoryOutputs.Where(directoryArtifactWithContent => !directoryArtifactWithContent.directoryArtifact.IsSharedOpaque).ToReadOnlyArray();
            }
            else
            {
                // Merge AllowedUndeclaredRead
                allowedUndeclaredRead = MergeAllowedUndeclaredReads(allExecutionResults);

                // Merge SharedOpaqueDirectoryWriteAccesses
                sharedDynamicDirectoryWriteAccesses = new ReadOnlyDictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>>(allExecutionResults.Where(result => result.SharedDynamicDirectoryWriteAccesses != null)
                                                                                  .SelectMany(result => result.SharedDynamicDirectoryWriteAccesses)
                                                                                  .GroupBy(kvp => kvp.Key)
                                                                                  .ToDictionary(
                                                                                                 group => group.Key,
                                                                                                 group => (IReadOnlyCollection<FileArtifactWithAttributes>)(new HashSet<FileArtifactWithAttributes>(group.SelectMany(kvp => kvp.Value ?? new List<FileArtifactWithAttributes>()))).ToList()));
              

                // Merge DynamicObservations
                dynamicObservations = ReadOnlyArray<(AbsolutePath, DynamicObservationKind)>.From(allExecutionResults.Where(result => result.DynamicObservations.IsValid)
                                                                                                                    .SelectMany(result => result.DynamicObservations));

                // Merge exclusiveOpaqueDirectories
                exclusiveOpaqueContent = (allExecutionResults.Where(result => result.DirectoryOutputs.IsValid)
                                                                         .SelectMany(result => result.DirectoryOutputs)
                                                                         .Where(directoryArtifactWithContent => !directoryArtifactWithContent.directoryArtifact.IsSharedOpaque)
                                                                         .GroupBy(tuple => tuple.directoryArtifact)
                                                                         .Select(group =>
                                                                         {
                                                                             var fileArtifacts = group?.SelectMany(tuple => tuple.fileArtifactArray).Distinct();
                                                                             return (group.Key, FileArtifacts: fileArtifacts.Any() ? ReadOnlyArray<FileArtifactWithAttributes>.From(fileArtifacts) : ReadOnlyArray<FileArtifactWithAttributes>.Empty);
                                                                         })).ToReadOnlyArray();
            }
        }

        /// <summary>
        /// Merges the AllowedUndeclaredReads from multiple ExecutionResults and resolves duplicates.
        /// </summary>
        /// <remarks>
        /// If during the retries, the same path is encountered more than once (duplicate keys), it compares the ObservedInputType values:
        /// 1.) If the types are different, it selects the one with the highest precedence of ObservedInputType.
        /// For example, if `FileContentRead` and `AbsentPathProbe` are both reported for a path, we opt for `FileContentRead` as it reflects a more critical observation of the file's state.
        /// 2.) If the types are the same, we need to report the same observedInputType for the path. 
        /// </remarks>
        internal static IReadOnlyDictionary<AbsolutePath, ObservedInputType> MergeAllowedUndeclaredReads(IEnumerable<ExecutionResult> allExecutionResults)
        {
            var allowedUndeclaredRead = new Dictionary<AbsolutePath, ObservedInputType>();

            foreach (var kvp in allExecutionResults
                .Where(result => result.AllowedUndeclaredReads != null)
                .SelectMany(result => result.AllowedUndeclaredReads))
            {
                if (!allowedUndeclaredRead.TryAdd(kvp.Key, kvp.Value))
                {
                    if (allowedUndeclaredRead.TryGetValue(kvp.Key, out var existingValue) &&
                        (kvp.Value != existingValue) &&
                        ((int)kvp.Value < (int)existingValue))
                    {
                        allowedUndeclaredRead[kvp.Key] = kvp.Value;
                    }
                }
            }

            return new ReadOnlyDictionary<AbsolutePath, ObservedInputType>(allowedUndeclaredRead);
        }

        /// <summary>
        /// Analyze process file access violations
        /// </summary>
        internal static ExecutionResult AnalyzeFileAccessViolations(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            Process process,
            IEnumerable<ExecutionResult> allExecutionResults,
            out bool pipIsSafeToCache,
            out IReadOnlyDictionary<FileArtifact, (FileMaterializationInfo, ReportedViolation)> allowedSameContentViolations)
        {
            pipIsSafeToCache = true;
            // Obtaining the latest execution result.
            ExecutionResult latestProcessExecutionResult = allExecutionResults.Last();
            using (operationContext.StartOperation(PipExecutorCounter.AnalyzeFileAccessViolationsDuration))
            {
                var analyzePipViolationsResult = AnalyzePipViolationsResult.NoViolations;
                allowedSameContentViolations = CollectionUtilities.EmptyDictionary<FileArtifact, (FileMaterializationInfo, ReportedViolation)>();

                // We merge the fileAccessViolations, allowlistedaccessviolations, allowedUndeclaredReads, SharedDynamicDirectoryWriteAccesses, dynamicObservations and exclusiveOpaqueContent from all retries.
                // We do not perform the union if there hasn't been a retry and this is handled in these methods below.
                MergeAllAccessesAndViolations(allExecutionResults, out IReadOnlyCollection<ReportedFileAccess> fileAccessViolationsNotAllowlisted, out IReadOnlyCollection<ReportedFileAccess> allowlistedFileAccessViolations);
                
                MergeAllDynamicAccessesAndViolations(allExecutionResults,
                                                            out IReadOnlyDictionary<AbsolutePath, ObservedInputType> allowedUndeclaredReads,
                                                            out IReadOnlyDictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>> sharedDynamicDirectoryWriteAccesses,
                                                            out ReadOnlyArray<(AbsolutePath Path, DynamicObservationKind Kind)>? dynamicObservations,
                                                            out ReadOnlyArray<(DirectoryArtifact directoryArtifact, ReadOnlyArray<FileArtifactWithAttributes> fileArtifactArray)>? exclusiveOpaqueContent);

                // Regardless of if we will fail the pip or not, maybe analyze them for higher-level dependency violations.
                if (sharedDynamicDirectoryWriteAccesses != null
                    || exclusiveOpaqueContent != null
                    || allowedUndeclaredReads != null
                    || dynamicObservations != null
                    || fileAccessViolationsNotAllowlisted != null
                    || allowlistedFileAccessViolations != null)
                {
                    analyzePipViolationsResult = environment.FileMonitoringViolationAnalyzer.AnalyzePipViolations(
                        process,
                        fileAccessViolationsNotAllowlisted,
                        allowlistedFileAccessViolations,
                        exclusiveOpaqueContent,
                        sharedDynamicDirectoryWriteAccesses,
                        allowedUndeclaredReads,
                        dynamicObservations,
                        latestProcessExecutionResult.OutputContent,
                        out allowedSameContentViolations);
                }

                if (!analyzePipViolationsResult.IsViolationClean)
                {
                    AssumeErrorWasLoggedWhenNotCancelled(environment, operationContext, errorMessage: "Error should have been logged by FileMonitoringViolationAnalyzer");
                    latestProcessExecutionResult = latestProcessExecutionResult.CloneSealedWithResult(PipResultStatus.Failed);
                }

                pipIsSafeToCache = analyzePipViolationsResult.PipIsSafeToCache;

                return latestProcessExecutionResult;
            }
        }

        /// <summary>
        /// Analyze process double write violations after the cache converged outputs
        /// </summary>
        internal static ExecutionResult AnalyzeDoubleWritesOnCacheConvergence(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            ExecutionResult processExecutionResult,
            Process process,
            IReadOnlyDictionary<FileArtifact, (FileMaterializationInfo, ReportedViolation)> allowedSameContentViolations)
        {
            using (operationContext.StartOperation(PipExecutorCounter.AnalyzeFileAccessViolationsDuration))
            {
                var analyzePipViolationsResult = AnalyzePipViolationsResult.NoViolations;

                if (allowedSameContentViolations.Count > 0)
                {
                    analyzePipViolationsResult = environment.FileMonitoringViolationAnalyzer.AnalyzeSameContentViolationsOnCacheConvergence(
                        process,
                        processExecutionResult.OutputContent,
                        allowedSameContentViolations);
                }

                if (!analyzePipViolationsResult.IsViolationClean)
                {
                    AssumeErrorWasLoggedWhenNotCancelled(environment, operationContext, errorMessage: "Error should have been logged by FileMonitoringViolationAnalyzer");
                    processExecutionResult = processExecutionResult.CloneSealedWithResult(PipResultStatus.Failed);
                }

                return processExecutionResult;
            }
        }

        /// <summary>
        /// Run process from cache and replay warnings.
        /// </summary>
        public static async Task<ExecutionResult> RunFromCacheWithWarningsAsync(
             OperationContext operationContext,
             IPipExecutionEnvironment environment,
             PipExecutionState.PipScopeState state,
             Process pip,
             RunnableFromCacheResult runnableFromCacheCheckResult,
             string processDescription)
        {
            using (operationContext.StartOperation(PipExecutorCounter.RunProcessFromCacheDuration))
            {
                RunnableFromCacheResult.CacheHitData cacheHitData = runnableFromCacheCheckResult.GetCacheHitData();
                Logger.Log.ScheduleProcessPipCacheHit(
                    operationContext,
                    processDescription,
                    runnableFromCacheCheckResult.Fingerprint.ToString(),
                    cacheHitData.StrongFingerprint.ToString(),
                    cacheHitData.Metadata.Id);

                // If the cache hit came from the remote cache, we want to know the duration of that in a separate counter
                using (cacheHitData.Locality == PublishedEntryRefLocality.Remote ? operationContext.StartOperation(PipExecutorCounter.RunProcessFromRemoteCacheDuration) : (OperationContext?) null)
                {
                    if (!TryGetCacheHitExecutionResult(operationContext, environment, pip, runnableFromCacheCheckResult, out var executionResult))
                    {
                        // Error should have been logged
                        executionResult.SetResult(operationContext.LoggingContext, PipResultStatus.Failed);
                        executionResult.Seal();

                        return executionResult;
                    }

                    executionResult.Seal();

                    // Save all dynamic writes to a sideband file if the pip needs it
                    if (PipNeedsSidebandFile(environment, pip))
                    {
                        using var sidebandWriter = CreateSidebandWriter(environment, pip);
                        sidebandWriter.EnsureHeaderWritten();
                        var dynamicWrites = executionResult.SharedDynamicDirectoryWriteAccesses?.SelectMany(kvp => kvp.Value).Select(fa => fa.Path) ?? CollectionUtilities.EmptyArray<AbsolutePath>();
                        foreach (var dynamicWrite in dynamicWrites)
                        {
                            sidebandWriter.RecordFileWrite(environment.Context.PathTable, dynamicWrite, flushImmediately: false);
                        }
                    }

                    var semistableHash = pip.FormattedSemiStableHash;
                    if (environment.Configuration.Logging.LogCachedPipOutputs)
                    {
                        foreach (var (file, fileInfo, origin) in executionResult.OutputContent)
                        {
                            if (!file.IsOutputFile || fileInfo.Hash.IsSpecialValue())
                            {
                                // only log real output content
                                continue;
                            }

                            Logger.Log.LogCachedPipOutput(
                                operationContext,
                                semistableHash,
                                file.Path.ToString(environment.Context.PathTable),
                                fileInfo.Hash.ToHex());
                        }
                    }

                    // File access violation analysis must be run before reporting the execution result output content.
                    var exclusiveOpaqueContent = executionResult.DirectoryOutputs.Where(directoryArtifactWithContent => !directoryArtifactWithContent.directoryArtifact.IsSharedOpaque).ToReadOnlyArray();

                    if ((executionResult.SharedDynamicDirectoryWriteAccesses?.Count > 0 || executionResult.AllowedUndeclaredReads?.Count > 0 || executionResult.DynamicObservations.Length > 0 || exclusiveOpaqueContent.Length > 0)
                        && !environment.FileMonitoringViolationAnalyzer.AnalyzeDynamicViolations(
                                pip,
                                exclusiveOpaqueContent,
                                executionResult.SharedDynamicDirectoryWriteAccesses,
                                executionResult.AllowedUndeclaredReads,
                                executionResult.DynamicObservations,
                                executionResult.OutputContent))
                    {
                        AssumeErrorWasLoggedWhenNotCancelled(environment, operationContext, errorMessage: "Error should have been logged by FileMonitoringViolationAnalyzer");
                        return executionResult.CloneSealedWithResult(PipResultStatus.Failed);
                    }

                    ReportExecutionResultOutputContent(
                        operationContext,
                        environment,
                        pip.SemiStableHash,
                        executionResult,
                        pip.PipType == PipType.Process ? ((Process)pip).RewritePolicy.ImpliesDoubleWriteIsWarning() : false);

                    if (cacheHitData.Metadata.NumberOfWarnings > 0 && environment.Configuration.Logging.ReplayWarnings())
                    {
                        Logger.Log.PipWarningsFromCache(
                            operationContext,
                            processDescription,
                            cacheHitData.Metadata.NumberOfWarnings);

                        if (!await ReplayWarningsFromCacheAsync(operationContext, environment, state, pip, cacheHitData))
                        {
                            // An error has been logged, the pip must fail
                            return executionResult.CloneSealedWithResult(PipResultStatus.Failed);

                        }
                    }

                    return executionResult;
                }
            }
        }

        /// <summary>
        /// Execute a service start or shutdown pip
        /// </summary>
        /// <param name="operationContext">Current logging context</param>
        /// <param name="environment">The pip environment</param>
        /// <param name="pip">The pip to execute</param>
        /// <param name="processIdListener">Callback to call when the process is actually started</param>
        /// <returns>A task that returns the execution result when done</returns>
        internal static async Task<ExecutionResult> ExecuteServiceStartOrShutdownAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            Process pip,
            Action<int> processIdListener = null)
        {
            // TODO: Try to materialize dependencies. This is not needed in the normal case because
            // scheduler has explicit MaterializeInputs step for pips which it schedules
            using (operationContext.StartOperation(PipExecutorCounter.ServiceInputMaterializationDuration))
            {
                // ensure dependencies materialized
                var materializationResult = await MaterializeInputsAsync(operationContext, environment, pip);
                if (materializationResult.IndicatesFailure())
                {
                    return ExecutionResult.GetFailureNotRunResult(operationContext);
                }
            }

            var result = await ExecuteProcessAsync(
                operationContext,
                environment,
                environment.State.GetScope(pip),
                pip,
                fingerprint: null,
                processIdListener: processIdListener);

            result.Seal();
            return result;
        }

        /// <summary>
        /// Execute a process pip
        /// </summary>
        /// <param name="operationContext">Current logging context</param>
        /// <param name="environment">The pip environment</param>
        /// <param name="state">the pip scoped execution state</param>
        /// <param name="pip">The pip to execute</param>
        /// <param name="fingerprint">The pip fingerprint</param>
        /// <param name="processIdListener">Callback to call when the process is actually started (PID is passed to it) and when the process exited (negative PID is passed to it)</param>
        /// <param name="expectedMemoryCounters">the expected memory counters for the process in megabytes</param>
        /// <param name="detoursEventListener">Detours listener to collect detours reported accesses. For tests only</param>
        /// <param name="runLocation">Location for running the process.</param>
        /// <returns>A task that returns the execution result when done</returns>
        public static async Task<ExecutionResult> ExecuteProcessAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state,
            Process pip,

            // TODO: This should be removed, or should become a WeakContentFingerprint
            ContentFingerprint? fingerprint,
            Action<int> processIdListener = null,
            ProcessMemoryCounters expectedMemoryCounters = default(ProcessMemoryCounters),
            IDetoursEventListener detoursEventListener = null,
            ProcessRunLocation runLocation = ProcessRunLocation.Default)
        {
            var context = environment.Context;
            var counters = environment.Counters;
            var configuration = environment.Configuration;
            var pathTable = context.PathTable;
            var processExecutionResult = new ExecutionResult
            {
                MustBeConsideredPerpetuallyDirty = IsUnconditionallyPerpetuallyDirty(pip, environment.PipGraphView)
            };

            if (fingerprint.HasValue)
            {
                processExecutionResult.WeakFingerprint = new WeakContentFingerprint(fingerprint.Value.Hash);
            }

            // Pips configured to disable cache lookup must be set to being perpetually dirty to ensure incremental scheduling
            // gets misses
            if (pip.DisableCacheLookup)
            {
                processExecutionResult.MustBeConsideredPerpetuallyDirty = true;
            }

            string processDescription = pip.GetDescription(context);

            using (operationContext.StartOperation(PipExecutorCounter.RunServiceDependenciesDuration))
            {
                bool ensureServicesRunning =
                    await environment.State.ServiceManager.TryRunServiceDependenciesAsync(environment, pip.PipId, pip.ServicePipDependencies, operationContext);
                if (!ensureServicesRunning)
                {
                    Logger.Log.PipFailedDueToServicesFailedToRun(operationContext, processDescription);
                    return ExecutionResult.GetFailureNotRunResult(operationContext);
                }
            }

            // Service related pips cannot be cancelled
            bool allowResourceBasedCancellation = !pip.ProcessOptions.HasFlag(Process.Options.Uncancellable) && (pip.ServiceInfo == null || pip.ServiceInfo.Kind == ServicePipKind.None);

            DateTime start = DateTime.UtcNow;

            // Execute the process when resources are available
            SandboxedProcessPipExecutionResult executionResult = await environment.State.ResourceManager
                .ExecuteWithResourcesAsync(
                    operationContext,
                    pip,
                    expectedMemoryCounters,
                    allowResourceBasedCancellation,
                    async (resourceScope) =>
                    {
                        return await ExecutePipAndHandleRetryAsync(
                            resourceScope,
                            operationContext,
                            pip,
                            expectedMemoryCounters,
                            environment,
                            state,
                            processIdListener,
                            detoursEventListener,
                            runLocation,
                            start);
                    });

            start = DateTime.UtcNow;
            processExecutionResult.ReportSandboxedExecutionResult(executionResult);
            SandboxedProcessPipExecutor.LogSubPhaseDuration(operationContext, pip, SandboxedProcessCounters.PipExecutorPhaseReportingExeResult, DateTime.UtcNow.Subtract(start));

            counters.AddToCounter(PipExecutorCounter.SandboxedProcessPrepDurationMs, executionResult.SandboxPrepMs);
            counters.AddToCounter(
                PipExecutorCounter.SandboxedProcessProcessResultDurationMs,
                executionResult.ProcessSandboxedProcessResultMs);
            counters.AddToCounter(PipExecutorCounter.ProcessStartTimeMs, executionResult.ProcessStartTimeMs);

            // We may have some violations reported already (outright denied by the sandbox manifest).
            FileAccessReportingContext fileAccessReportingContext = executionResult.UnexpectedFileAccesses;

            if (executionResult.RetryInfo.CanBeRetriedByRescheduleOrFalseIfNull())
            {
                // No post processing for retryable pips
                processExecutionResult.SetResult(operationContext, PipResultStatus.Canceled, executionResult.RetryInfo);

                if (fileAccessReportingContext != null)
                {
                    processExecutionResult.ReportFileAccesses(fileAccessReportingContext);
                }

                if (executionResult?.PrimaryProcessTimes != null)
                {
                    counters.AddToCounter(
                        PipExecutorCounter.CanceledProcessExecuteDuration,
                        executionResult.PrimaryProcessTimes.TotalWallClockTime);
                }

                // We merge all the DFA's reported from all the inline retries for the pip.
                processExecutionResult.MergeAllFileAccessesAndViolationsForInlineRetry(executionResult);

                return processExecutionResult;
            }

            if (executionResult.Status == SandboxedProcessPipExecutionStatus.PreparationFailed)
            {
                // Preparation failures provide minimal feedback.
                // We do not have any execution-time information (observed accesses or file monitoring violations) to analyze.
                // executionResult.RetryInfo.
                // No error here
                processExecutionResult.SetResult(operationContext, PipResultStatus.Failed);

                counters.IncrementCounter(PipExecutorCounter.PreparationFailureCount);
                counters.IncrementCounter(PipExecutorCounter.PreparationFailurePartialCopyCount);

                return processExecutionResult;
            }

            // These are the results we know how to handle. PreperationFailed has already been handled above.
            if (!(executionResult.Status == SandboxedProcessPipExecutionStatus.Succeeded ||
                executionResult.Status == SandboxedProcessPipExecutionStatus.ExecutionFailed ||
                executionResult.Status == SandboxedProcessPipExecutionStatus.FileAccessMonitoringFailed ||
                executionResult.Status == SandboxedProcessPipExecutionStatus.Canceled ||
                executionResult.Status == SandboxedProcessPipExecutionStatus.SharedOpaquePostProcessingFailed))
            {
                Contract.Assert(false, "Unexpected execution result " + executionResult.Status);
            }

            bool succeeded = executionResult.Status == SandboxedProcessPipExecutionStatus.Succeeded;

            if (executionResult.Status == SandboxedProcessPipExecutionStatus.ExecutionFailed ||
                executionResult.Status == SandboxedProcessPipExecutionStatus.FileAccessMonitoringFailed)
            {
                AssertErrorWasLoggedWhenNotCancelled(environment, operationContext, errorMessage: $"Error should have been logged for '{executionResult.Status}'");
            }

            if (executionResult.RetryInfo?.RetryReason == RetryReason.OutputWithNoFileAccessFailed ||
                executionResult.RetryInfo?.RetryReason == RetryReason.MismatchedMessageCount ||
                executionResult.RetryInfo?.RetryReason == RetryReason.AzureWatsonExitCode)
            {
                AssertErrorWasLoggedWhenNotCancelled(environment, operationContext,
                    errorMessage: $"Error should have been logged for failures after multiple retries on {executionResult.RetryInfo?.RetryMode.ToString()} due to '{executionResult.RetryInfo?.RetryReason.ToString()}'");
            }

            Contract.Assert(executionResult.UnexpectedFileAccesses != null, "Success / ExecutionFailed provides all execution-time fields");
            Contract.Assert(executionResult.PrimaryProcessTimes != null, "Success / ExecutionFailed provides all execution-time fields");

            // Do not update the counter for service pips
            if (!pip.IsStartOrShutdownKind)
            {
                counters.AddToCounter(PipExecutorCounter.ExecuteProcessDuration, executionResult.PrimaryProcessTimes.TotalWallClockTime);
            }

            // Skip the post-processing if the pip was run outside of the sandbox
            if (pip.DisableSandboxing)
            {
                // We just populate processExecutionResult with empty observations
                // to appease the contract assertions regarding these fields being set
                processExecutionResult.DynamicObservations = ReadOnlyArray<(AbsolutePath Path, DynamicObservationKind Kind)>.Empty;
                processExecutionResult.AllowedUndeclaredReads = new Dictionary<AbsolutePath, ObservedInputType>();
                processExecutionResult.FileAccessViolationsNotAllowlisted = new List<ReportedFileAccess>();
                processExecutionResult.AllowlistedFileAccessViolations = new List<ReportedFileAccess>();
                processExecutionResult.ReportUnexpectedFileAccesses(default);

                Logger.Log.ScheduleProcessNotStoredToCacheDueToSandboxDisabled(operationContext, processDescription);
                processExecutionResult.SetResult(operationContext, succeeded ? PipResultStatus.Succeeded : PipResultStatus.Failed);
                return processExecutionResult;
            }

            using (operationContext.StartOperation(PipExecutorCounter.ProcessOutputsDuration))
            {
                ObservedInputProcessingResult observedInputValidationResult;

                using (operationContext.StartOperation(PipExecutorCounter.ProcessOutputsObservedInputValidationDuration))
                {
                    // In addition, we need to verify that additional reported inputs are actually allowed, and furthermore record them.
                    //
                    // Don't track file changes in observed input processor when process execution failed. Running observed input processor has side effects
                    // that some files get tracked by the file change tracker. Suppose that the process failed because it accesses paths that
                    // are supposed to be untracked (but the user forgot to specify it in the spec). Those paths will be tracked by
                    // file change tracker because the observed input processor may try to probe and track those paths.
                    start = DateTime.UtcNow;
                    observedInputValidationResult =
                        await ValidateObservedFileAccessesAsync(
                            operationContext,
                            environment,
                            state,
                            state.GetCacheableProcess(pip, environment),
                            fileAccessReportingContext,
                            executionResult.ObservedFileAccesses,
                            executionResult.SharedDynamicDirectoryWriteAccesses,
                            trackFileChanges: succeeded);
                    SandboxedProcessPipExecutor.LogSubPhaseDuration(
                        operationContext, pip, SandboxedProcessCounters.PipExecutorPhaseValidateObservedFileAccesses, DateTime.UtcNow.Subtract(start),
                        $"(DynObs: {observedInputValidationResult.DynamicObservations.Length})");
                }

                // Store the dynamically observed accesses
                processExecutionResult.DynamicObservations = observedInputValidationResult.DynamicObservations;
                processExecutionResult.AllowedUndeclaredReads = observedInputValidationResult.AllowedUndeclaredSourceReads;

                if (observedInputValidationResult.Status == ObservedInputProcessingStatus.Aborted)
                {
                    succeeded = false;
                    AssumeErrorWasLoggedWhenNotCancelled(environment, operationContext, errorMessage:"No error was logged when ValidateObservedAccesses failed");
                }

                if (pip.ProcessAbsentPathProbeInUndeclaredOpaquesMode == Process.AbsentPathProbeInUndeclaredOpaquesMode.Relaxed)
                {
                    var absentPathProbesUnderNonDependenceOutputDirectories =
                        observedInputValidationResult.DynamicObservations
                        .Where(o => o.Kind == DynamicObservationKind.AbsentPathProbeUnderOutputDirectory)
                        .Select(o => o.Path);

                    if (absentPathProbesUnderNonDependenceOutputDirectories.Any())
                    {
                        start = DateTime.UtcNow;
                        bool isDirty = false;
                        foreach (var absentPathProbe in absentPathProbesUnderNonDependenceOutputDirectories)
                        {
                            if (!pip.DirectoryDependencies.Any(dir => absentPathProbe.IsWithin(pathTable, dir)))
                            {
                                isDirty = true;
                                break;
                            }
                        }

                        SandboxedProcessPipExecutor.LogSubPhaseDuration(operationContext, pip, SandboxedProcessCounters.PipExecutorPhaseComputingIsDirty, DateTime.UtcNow.Subtract(start));
                        processExecutionResult.MustBeConsideredPerpetuallyDirty = isDirty;
                    }
                }

                // We have all violations now.
                UnexpectedFileAccessCounters unexpectedFilesAccesses = fileAccessReportingContext.Counters;
                processExecutionResult.ReportUnexpectedFileAccesses(unexpectedFilesAccesses);

                // Set file access violations which were not allowlisted for use by file access violation analyzer
                processExecutionResult.FileAccessViolationsNotAllowlisted = fileAccessReportingContext.FileAccessViolationsNotAllowlisted;
                processExecutionResult.AllowlistedFileAccessViolations = fileAccessReportingContext.AllowlistedFileAccessViolations;

                // We need to update this instance so used a boxed representation
                BoxRef<ProcessFingerprintComputationEventData> fingerprintComputation =
                    new ProcessFingerprintComputationEventData
                    {
                        Kind = FingerprintComputationKind.Execution,
                        PipId = pip.PipId,
                        WeakFingerprint = new WeakContentFingerprint((fingerprint ?? ContentFingerprint.Zero).Hash),

                        // This field is set later for successful strong fingerprint computation
                        StrongFingerprintComputations = CollectionUtilities.EmptyArray<ProcessStrongFingerprintComputationData>(),
                    };

                bool outputHashSuccess = false;

                bool skipCaching = true;

                if (succeeded)
                {
                    // We are now be able to store a descriptor and content for this process to cache if we wish.
                    // But if the pip completed with (warning level) file monitoring violations (suppressed or not), there's good reason
                    // to believe that there are missing inputs or outputs for the pip. This allows a nice compromise in which a build
                    // author can iterate quickly on fixing monitoring errors in a large build - mostly cached except for those parts with warnings.
                    // Of course, if the allowlist was configured to explicitly allow caching for those violations, we allow it.
                    //
                    // N.B. fileAccessReportingContext / unexpectedFilesAccesses accounts for violations from the execution itself as well as violations added by ValidateObservedAccesses
                    ObservedInputProcessingResult? observedInputProcessingResultForCaching = null;

                    if (unexpectedFilesAccesses.HasUncacheableFileAccesses)
                    {
                        Logger.Log.ScheduleProcessNotStoredToCacheDueToFileMonitoringViolations(operationContext, processDescription);
                    }
                    else if ((executionResult.NumberOfWarnings > 0 || executionResult.HasAzureWatsonDeadProcess) &&
                             ExtraFingerprintSalts.ArePipWarningsPromotedToErrors(configuration.Logging))
                    {
                        // Just like not caching errors, we also don't want to cache warnings that are promoted to errors
                        Logger.Log.ScheduleProcessNotStoredToWarningsUnderWarnAsError(operationContext, processDescription);
                    }
                    else if (!fingerprint.HasValue)
                    {
                        Logger.Log.ScheduleProcessNotStoredToCacheDueToInherentUncacheability(operationContext, processDescription);
                    }
                    else if ((pip.UncacheableExitCodes.Length > 0) && (pip.UncacheableExitCodes.Contains(executionResult.ExitCode)))
                    {
                        // If a successful pip has been marked with an uncacheable exit code, we do not cache it.
                        skipCaching = true;
                    }
                    else
                    {
                        Contract.Assume(
                            observedInputValidationResult.Status == ObservedInputProcessingStatus.Success,
                            "Should never cache a process that failed observed file input validation (cacheable-allowlisted violations leave the validation successful).");

                        // Note that we discard observed inputs if cache-ineligible (required by StoreDescriptorAndContentForProcess)
                        observedInputProcessingResultForCaching = observedInputValidationResult;
                        skipCaching = false;
                    }

                    // TODO: Maybe all counter updates should occur on distributed build orchestrator.
                    if (skipCaching)
                    {
                        counters.IncrementCounter(PipExecutorCounter.ProcessPipsExecutedButUncacheable);
                    }

                    // Even though we call StoreContentForProcessAndCreateCacheEntryAsync here, the observed inputs are actually not stored when skipCaching = true
                    using (operationContext.StartOperation(PipExecutorCounter.ProcessOutputsStoreContentForProcessAndCreateCacheEntryDuration))
                    {
                        start = DateTime.UtcNow;
                        outputHashSuccess = await StoreContentForProcessAndCreateCacheEntryAsync(
                            operationContext,
                            environment,
                            state,
                            pip,
                            processDescription,
                            observedInputProcessingResultForCaching,
                            executionResult.EncodedStandardOutput,
                            // Possibly null
                            executionResult.EncodedStandardError,
                            // Possibly null
                            executionResult.NumberOfWarnings,
                            processExecutionResult,
                            enableCaching: !skipCaching,
                            fingerprintComputation: fingerprintComputation);

                        var pushOutputsToCache = DateTime.UtcNow.Subtract(start);
                        SandboxedProcessPipExecutor.LogSubPhaseDuration(operationContext, pip, SandboxedProcessCounters.PipExecutorPhaseStoringCacheContent, pushOutputsToCache);
                        processExecutionResult.ReportPushOutputsToCacheDurationMs(pushOutputsToCache.ToMilliseconds());
                    }

                    if (outputHashSuccess)
                    {
                        processExecutionResult.SetResult(operationContext, PipResultStatus.Succeeded);
                        processExecutionResult.MustBeConsideredPerpetuallyDirty = skipCaching;
                    }
                    else
                    {
                        // The Pip itself did not fail, but we are marking it as a failure because we could not handle the post processing.
                        // If a build is terminating, we do not wait for post processing to finish (i.e., the operation is aborted and
                        // content is not stored to cache).
                        Contract.Assume(
                            operationContext.LoggingContext.ErrorWasLogged || environment.Context.CancellationToken.IsCancellationRequested,
                            "Error should have been logged for StoreContentForProcessAndCreateCacheEntry() failure");
                    }
                }

                // If there were any failures, attempt to log partial information to execution log.
                // In order to log information for failed pip, removed the limition of successful ObservedInputProcessorResult
                // Also log fingerprintComputations for non cachable successful pips completed with (warning level) file monitoring violations (suppressed or not)
                // This will include the Aborted and Mismatch states, which means builds with file access violations will get their StrongFingerprint information logged too.
                // There are a number of paths in the ObservedInputProcessor where the final result has invalid state if the status wasn't Success.
                if (!succeeded || skipCaching)
                {
                    start = DateTime.UtcNow;
                    var pathSet = observedInputValidationResult.GetPathSet(state.UnsafeOptions);
                    var pathSetHash = await environment.State.Cache.SerializePathSetAsync(pathSet, pip.PreservePathSetCasing);

                    // This strong fingerprint is meaningless and not-cached, compute it for execution analyzer logic that rely on having a successful strong fingerprint and log into xlg
                    var strongFingerprint = observedInputValidationResult.ComputeStrongFingerprint(
                        pathTable,
                        fingerprintComputation.Value.WeakFingerprint,
                        pathSetHash);

                    fingerprintComputation.Value.StrongFingerprintComputations = new[]
                    {
                        ProcessStrongFingerprintComputationData.CreateForExecution(
                            pathSetHash,
                            pathSet,
                            observedInputValidationResult.ObservedInputs,
                            strongFingerprint),
                    };

                    fingerprintComputation.Value.Kind = !succeeded ? FingerprintComputationKind.ExecutionFailed : FingerprintComputationKind.ExecutionNotCacheable;
                    SandboxedProcessPipExecutor.LogSubPhaseDuration(operationContext, pip, SandboxedProcessCounters.PipExecutorPhaseComputingStrongFingerprint, DateTime.UtcNow.Subtract(start), $"(ps: {pathSet.Paths.Length})");

                    // Store pip standard output and standard error into cache for orchestrator to retrieve them
                    // Add hashes into ExecutionResult but don't add them into any cache descriptor metadata
                    // Standard output and standard error are already part of PipCacheDescriptorV2Metadata for cachable pips
                    EncodedStringKeyedHash stdoutEncodedStringKeyedHash;
                    if (executionResult.EncodedStandardOutput != null)
                    {
                        var possibleStdEncodedStringKeyedHash = await TryStorePipStandardOutputOrErrorToCache(
                            executionResult.EncodedStandardOutput,
                            environment,
                            pathTable,
                            pip,
                            operationContext,
                            state);

                        if (possibleStdEncodedStringKeyedHash.Succeeded)
                        {
                            stdoutEncodedStringKeyedHash = possibleStdEncodedStringKeyedHash.Result;
                            processExecutionResult.ReportStandardOutput(stdoutEncodedStringKeyedHash);
                        }
                    }

                    EncodedStringKeyedHash stderrEncodedStringKeyedHash;
                    if (executionResult.EncodedStandardError != null)
                    {
                        var possibleStdErrEncodedStringKeyedHash = await TryStorePipStandardOutputOrErrorToCache(
                            executionResult.EncodedStandardError,
                            environment,
                            pathTable,
                            pip,
                            operationContext,
                            state);

                        if (possibleStdErrEncodedStringKeyedHash.Succeeded)
                        {
                            stderrEncodedStringKeyedHash = possibleStdErrEncodedStringKeyedHash.Result;
                            processExecutionResult.ReportStandardError(stderrEncodedStringKeyedHash);
                        }
                    }
                }

                // Log the fingerprint computation
                start = DateTime.UtcNow;
                environment.State.ExecutionLog?.ProcessFingerprintComputation(fingerprintComputation.Value);

                SandboxedProcessPipExecutor.LogSubPhaseDuration(operationContext, pip, SandboxedProcessCounters.PipExecutorPhaseStoringStrongFingerprintToXlg, DateTime.UtcNow.Subtract(start));

                // Merge all the DFA's reported from all the inline retries for the pip.
                // We perform this operation towards the end of this method.
                // This is because we want to use the latest SandboxedProcessPipExecutionResult object for validating all observed file accesses.
                processExecutionResult.MergeAllFileAccessesAndViolationsForInlineRetry(executionResult);

                if (!outputHashSuccess)
                {
                    processExecutionResult.SetResult(operationContext, PipResultStatus.Failed);
                }

                return processExecutionResult;
            }
        }

        /// <summary>
        /// Execute Pip and handle retries within the same worker
        /// </summary>
        private static async Task<SandboxedProcessPipExecutionResult> ExecutePipAndHandleRetryAsync(ProcessResourceManager.ResourceScope resourceScope,
            OperationContext operationContext,
            Process pip,
            ProcessMemoryCounters expectedMemoryCounters,
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state,
            Action<int> processIdListener,
            IDetoursEventListener detoursEventListener,
            ProcessRunLocation runLocation,
            DateTime start)
        {
            var context = environment.Context;
            var counters = environment.Counters;
            var configuration = environment.Configuration;
            var pathTable = context.PathTable;

            string processDescription = pip.GetDescription(context);

            // When preserving outputs, we need to make sure to remove any hardlinks to the cache.
            Func<string, Task<bool>> makeOutputPrivate =
                async path =>
                {
                    try
                    {
                        if (!FileUtilities.FileExistsNoFollow(path))
                        {
                            // Output file doesn't exist. No need to make it private,
                            // but return false so BuildXL ensures the output directory is created.
                            return false;
                        }

                        if (FileUtilities.GetHardLinkCount(path) == 1 &&
                            FileUtilities.HasWritableAccessControl(path))
                        {
                            // Output file is already private. File will not be deleted.
                            return true;
                        }

                        // We want to use a temp filename that's as short as the original filename.
                        // To achieve this, we use the random filename generator from System.IO
                        var maybePrivate = await FileUtilities.TryMakeExclusiveLinkAsync(
                            path,
                            optionalTemporaryFileName: Path.GetRandomFileName(),
                            preserveOriginalTimestamp: true);

                        if (!maybePrivate.Succeeded)
                        {
                            maybePrivate.Failure.Throw();
                        }

                        return true;
                    }
                    catch (BuildXLException ex)
                    {
                        Logger.Log.PreserveOutputsFailedToMakeOutputPrivate(
                            operationContext,
                            processDescription,
                            path,
                            ex.GetLogEventMessage());
                        return false;
                    }
                };

            // To do in-place rewrites, we need to make writable, private copies of inputs to be rewritten (they may be read-only hardlinks into the cache, for example).
            Func<FileArtifact, Task<bool>> makeInputPrivate =
                async artifactNeededPrivate =>
                {
                    FileMaterializationInfo inputMaterializationInfo =
                        environment.State.FileContentManager.GetInputContent(artifactNeededPrivate);

                    if (inputMaterializationInfo.ReparsePointInfo.IsActionableReparsePoint)
                    {
                        // Do nothing in case of re-writing a symlink --- a process can safely change
                        // symlink's target since it won't affect things in CAS.
                        return true;
                    }

                    ContentHash artifactHash = inputMaterializationInfo.Hash;

                    // Source files aren't guaranteed in cache, until we first have a reason to ingress them.
                    // Note that this is only relevant for source files rewritten in place, which is only
                    // used in some team-internal trace-conversion scenarios as of writing.
                    if (artifactNeededPrivate.IsSourceFile)
                    {
                        // We assume that source files cannot be made read-only so we use copy file materialization
                        // rather than ever hardlinking
                        var maybeStored = await environment.LocalDiskContentStore.TryStoreAsync(
                            environment.Cache.ArtifactContentCache,
                            fileRealizationModes: FileRealizationMode.Copy,
                            path: artifactNeededPrivate.Path,
                            tryFlushPageCacheToFileSystem: false,
                            knownContentHash: artifactHash,

                            // Source should have been tracked by hash-source file pip, no need to retrack.
                            trackPath: false,
                            isReparsePoint: false,
                            // This is a source file, and therefore never a dynamic output
                            outputDirectoryRoot: AbsolutePath.Invalid);

                        if (!maybeStored.Succeeded)
                        {
                            Logger.Log.StorageCacheIngressFallbackContentToMakePrivateError(
                                operationContext,
                                processDescription,
                                contentHash: artifactHash.ToHex(),
                                fallbackPath:
                                    artifactNeededPrivate.Path.ToString(pathTable),
                                errorMessage: maybeStored.Failure.DescribeIncludingInnerFailures());
                            return false;
                        }
                    }

                    // We need a private version of the output - it must be writable and have link count 1.
                    // We can achieve that property by forcing a copy of the content (by hash) out of cache.
                    // The content should be in the cache in usual cases. See special case above for source-file rewriting
                    // (should not be common; only used in some trace-conversion scenarios as of writing).
                    var maybeMadeWritable =
                        await
                            environment.LocalDiskContentStore
                                .TryMaterializeTransientWritableCopyAsync(
                                    environment.Cache.ArtifactContentCache,
                                    artifactNeededPrivate.Path,
                                    artifactHash,
                                    environment.Context.CancellationToken);

                    if (!maybeMadeWritable.Succeeded)
                    {
                        Logger.Log.StorageCacheGetContentError(
                            operationContext,
                            pip.GetDescription(context),
                            contentHash: artifactHash.ToHex(),
                            destinationPath:
                                artifactNeededPrivate.Path.ToString(pathTable),
                            errorMessage:
                                maybeMadeWritable.Failure.DescribeIncludingInnerFailures());
                        return false;
                    }

                    return true;
                };

            SemanticPathExpander semanticPathExpander = state.PathExpander;

            var processMonitoringLogger = new ProcessExecutionMonitoringLogger(operationContext, pip, context, environment.State.ExecutionLog);

            // Inner cancellation token source for tracking cancellation time
            using (var innerResourceLimitCancellationTokenSource = new CancellationTokenSource())
            using (var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(innerResourceLimitCancellationTokenSource.Token, environment.SchedulerCancellationToken))
            using (var counter = operationContext.StartOperation(PipExecutorCounter.ProcessPossibleRetryWallClockDuration))
            {
                ProcessMemoryCountersSnapshot lastObservedMemoryCounters = default(ProcessMemoryCountersSnapshot);
                TimeSpan? cancellationStartTime = null;
                using var cancellationTokenRegistration = resourceScope.Token.Register(
                    () =>
                    {
                        cancellationStartTime = TimestampUtilities.Timestamp;
                        Logger.Log.StartCancellingProcessPipExecutionDueToResourceExhaustion(
                            operationContext,
                            processDescription,
                            resourceScope.CancellationReason?.ToString() ?? "",
                            resourceScope.ScopeId,
                            (long)(counter.Duration?.TotalMilliseconds ?? -1),
                            expectedMemoryCounters.PeakWorkingSetMb,
                            lastObservedMemoryCounters.PeakWorkingSetMb,
                            lastObservedMemoryCounters.LastWorkingSetMb);

                        using (operationContext.StartAsyncOperation(PipExecutorCounter.ResourceLimitCancelProcessDuration))
                        {
#pragma warning disable AsyncFixer02
                            innerResourceLimitCancellationTokenSource.Cancel();
#pragma warning restore AsyncFixer02
                        }
                    });

                IReadOnlyList<AbsolutePath> changeAffectedInputs = pip.ChangeAffectedInputListWrittenFile.IsValid
                    ? environment.State.FileContentManager.SourceChangeAffectedInputs.GetChangeAffectedInputs(pip)
                    : null;

                int remainingUserRetries = pip.RetryExitCodes.Length > 0 ? pip.ProcessRetriesOrDefault(configuration.Schedule) : 0;
                int remainingInternalSandboxedProcessExecutionFailureRetries = InternalSandboxedProcessExecutionFailureRetryCountMax;

                bool firstAttempt = true;
                bool userRetry = false;
                SandboxedProcessPipExecutionResult result = null;

                //Used to keep track of the previous result of running sandboxed process executor.
                SandboxedProcessPipExecutionResult prevResult = null;
                var aggregatePipProperties = new Dictionary<string, int>();
                IReadOnlyDictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>> staleDynamicOutputs = null;

                // A collection of formatted semistable hashes for pips which will have verbose sandbox logging enabled. 
                var isVerboseLoggingEnabled = environment.PipSpecificPropertiesConfig?.PipHasProperty(PipSpecificPropertiesConfig.PipSpecificProperty.EnableVerboseProcessLogging, pip.SemiStableHash) == true;

                // Retry pip count up to limit if we produce result without detecting file access.
                // There are very rare cases where a child process is started not Detoured and we don't observe any file accesses from such process.
                while (true)
                {
                    lastObservedMemoryCounters = default(ProcessMemoryCountersSnapshot);

                    var verboseLogging = false;

                    if (isVerboseLoggingEnabled)
                    {
                        verboseLogging = true;
                    }

                    if (userRetry && EngineEnvironmentSettings.VerboseModeForPipsOnRetry)
                    {
                        verboseLogging = true;
                    }

                    var executor = new SandboxedProcessPipExecutor(
                        context,
                        operationContext.LoggingContext,
                        pip,
                        configuration,
                        environment.RootMappings,
                        state.FileAccessAllowlist,
                        makeInputPrivate,
                        makeOutputPrivate,
                        semanticPathExpander,
                        sidebandState: environment.State.SidebandState,
                        pipEnvironment: environment.State.PipEnvironment,
                        directoryArtifactContext: new DirectoryArtifactContext(environment),
                        logger: processMonitoringLogger,
                        processIdListener: processIdListener,
                        pipDataRenderer: environment.PipFragmentRenderer,
                        buildEngineDirectory: configuration.Layout.BuildEngineDirectory,
                        directoryTranslator: environment.DirectoryTranslator,
                        remainingUserRetryCount: remainingUserRetries,
                        vmInitializer: environment.VmInitializer,
                        remoteProcessManager: environment.RemoteProcessManager,
                        tempDirectoryCleaner: environment.TempCleaner,
                        changeAffectedInputs: changeAffectedInputs,
                        detoursListener: detoursEventListener,
                        reparsePointResolver: environment.ReparsePointAccessResolver,
                        staleOutputsUnderSharedOpaqueDirectories: staleDynamicOutputs,
                        pluginManager: environment.PluginManager,
                        pipGraphFileSystemView: environment.PipGraphView,
                        runLocation: runLocation,
                        sandboxFileSystemView: environment.State.FileSystemView,
                        verboseProcessLogging: verboseLogging);

                    resourceScope.RegisterQueryRamUsageMb(
                        () =>
                        {
                            using (counters[PipExecutorCounter.QueryRamUsageDuration].Start())
                            {
                                lastObservedMemoryCounters = executor.GetMemoryCountersSnapshot() ?? default(ProcessMemoryCountersSnapshot);
                                return lastObservedMemoryCounters;
                            }
                        });

                    resourceScope.RegisterEmptyWorkingSet(
                        (bool isSuspend) =>
                        {
                            using (counters[PipExecutorCounter.EmptyWorkingSetDuration].Start())
                            {
                                var result = executor.TryEmptyWorkingSet(isSuspend);
                                if (result == EmptyWorkingSetResult.Success)
                                {
                                    counters.IncrementCounter(PipExecutorCounter.EmptyWorkingSetSucceeded);

                                    if (resourceScope.SuspendedDurationMs > 0)
                                    {
                                        counters.IncrementCounter(PipExecutorCounter.EmptyWorkingSetSucceededMoreThanOnce);
                                    }
                                }

                                return result;
                            }
                        });

                    resourceScope.RegisterResumeProcess(
                        () =>
                        {
                            using (counters[PipExecutorCounter.ResumeProcessDuration].Start())
                            {
                                return executor.TryResumeProcess();
                            }
                        });

                    if (firstAttempt)
                    {
                        counters.IncrementCounter(PipExecutorCounter.ExternalProcessCount);
                        environment.SetMaxExternalProcessRan();
                        firstAttempt = false;
                    }

                    using (var sidebandWriter = CreateSidebandWriterIfNeeded(environment, pip))
                    {
                        staleDynamicOutputs = null;
                        start = DateTime.UtcNow;
                        prevResult = result;
                        result = await executor.RunAsync(
                            sandboxConnection: environment.SandboxConnection,
                            sidebandWriter: sidebandWriter,
                            fileSystemView: pip.AllowUndeclaredSourceReads ? environment.State.FileSystemView : null,
                            linkedCancellationTokenSource.Token);
                        result.PreviousResult = prevResult;
                        SandboxedProcessPipExecutor.LogSubPhaseDuration(operationContext, pip, SandboxedProcessCounters.PipExecutorPhaseRunningPip, DateTime.UtcNow.Subtract(start));
                        staleDynamicOutputs = result.SharedDynamicDirectoryWriteAccesses;
                    }

                    if (result.PipProperties != null)
                    {
                        foreach (var kvp in result.PipProperties)
                        {
                            if (aggregatePipProperties.TryGetValue(kvp.Key, out var value))
                            {
                                aggregatePipProperties[kvp.Key] = value + kvp.Value;
                            }
                            else
                            {
                                aggregatePipProperties.Add(kvp.Key, kvp.Value);
                            }
                        }
                    }

                    lock (s_telemetryDetoursHeapLock)
                    {
                        if (counters.GetCounterValue(PipExecutorCounter.MaxDetoursHeapInBytes) <
                            result.MaxDetoursHeapSizeInBytes)
                        {
                            // Zero out the counter first and then set the new value.
                            counters.AddToCounter(
                                PipExecutorCounter.MaxDetoursHeapInBytes,
                                -counters.GetCounterValue(PipExecutorCounter.MaxDetoursHeapInBytes));
                            counters.AddToCounter(
                                PipExecutorCounter.MaxDetoursHeapInBytes,
                                result.MaxDetoursHeapSizeInBytes);
                        }
                    }

                    if (result.RetryInfo?.RetryMode == RetryMode.Reschedule)
                    {
                        Logger.Log.PipProcessToBeRetriedByReschedule(operationContext,
                            processDescription, result.RetryInfo.RetryReason.ToString());
                    }

                    if (result.RetryInfo?.RetryReason == RetryReason.UserSpecifiedExitCode)
                    {
                        Contract.Assert(remainingUserRetries > 0);
                        --remainingUserRetries;
                        LogUserSpecifiedExitCodeEvent(result, operationContext, context, pip, processDescription, remainingUserRetries);

                        userRetry = true;

                        counters.AddToCounter(PipExecutorCounter.RetriedUserExecutionDuration, result.PrimaryProcessTimes.TotalWallClockTime);
                        counters.IncrementCounter(PipExecutorCounter.ProcessUserRetries);
                        continue;
                    }

                    if (result.RetryInfo.CanBeRetriedInlineOrFalseIfNull())
                    {
                        if (remainingInternalSandboxedProcessExecutionFailureRetries <= 0)
                        {
                            if (result.RetryInfo.RetryMode == RetryMode.Inline)
                            {
                                // Log errors for inline retry on the same worker which have reached their local retry limit
                                LogRetryInlineErrors(result.RetryInfo.RetryReason, operationContext, pip, processDescription);
                                break;
                            }
                            else // Case: RetryLocation.Both
                            {
                                Logger.Log.PipProcessToBeRetriedByReschedule(operationContext, processDescription, result.RetryInfo.RetryReason.ToString());
                                break;
                            }
                        }
                        else
                        {
                            if (EngineEnvironmentSettings.DisableDetoursRetries && result.RetryInfo.RetryReason.IsDetoursRetryableFailure())
                            {
                                Logger.Log.DisabledDetoursRetry(operationContext, pip.SemiStableHash, processDescription, result.RetryInfo.RetryReason.ToString());
                                break;
                            }

                            --remainingInternalSandboxedProcessExecutionFailureRetries;

                            Logger.Log.PipProcessRetriedInline(
                                operationContext,
                                InternalSandboxedProcessExecutionFailureRetryCountMax - remainingInternalSandboxedProcessExecutionFailureRetries,
                                InternalSandboxedProcessExecutionFailureRetryCountMax,
                                processDescription,
                                result.RetryInfo.RetryReason.ToString());

                            counters.AddToCounter(PipExecutorCounter.RetriedInternalExecutionDuration, result.PrimaryProcessTimes.TotalWallClockTime);

                            if (!IncrementInternalErrorRetryCounters(result.RetryInfo.RetryReason, counters))
                            {
                                Contract.Assert(false, "Unexpected result error type.");
                            }

                            continue;
                        }
                        // Just break the loop below. The result is already set properly.
                    }

                    break;
                }

                counters.DecrementCounter(PipExecutorCounter.ExternalProcessCount);

                result.SuspendedDurationMs = resourceScope.SuspendedDurationMs;

                if (result.Status == SandboxedProcessPipExecutionStatus.Canceled)
                {
                    if (resourceScope.CancellationReason.HasValue)
                    {
                        // Canceled due to resource exhaustion
                        result.RetryInfo = RetryInfo.GetDefault(RetryReason.ResourceExhaustion);

                        counters.IncrementCounter(resourceScope.CancellationReason == ProcessResourceManager.ResourceScopeCancellationReason.ResourceLimits ?
                            PipExecutorCounter.ProcessRetriesDueToResourceLimits :
                            PipExecutorCounter.ProcessRetriesDueToSuspendOrResumeFailure);

                        TimeSpan? cancelTime = TimestampUtilities.Timestamp - cancellationStartTime;

                        Logger.Log.CancellingProcessPipExecutionDueToResourceExhaustion(
                            operationContext,
                            processDescription,
                            resourceScope.CancellationReason.ToString(),
                            (long)(operationContext.Duration?.TotalMilliseconds ?? -1),
                            peakMemoryMb: result.JobAccountingInformation?.MemoryCounters.PeakWorkingSetMb ?? 0,
                            expectedMemoryMb: expectedMemoryCounters.PeakWorkingSetMb,
                            cancelMilliseconds: (int)(cancelTime?.TotalMilliseconds ?? 0));
                    }
                    else if (environment.SchedulerCancellationToken.IsCancellationRequested
                             && environment.Configuration.Distribution.BuildRole == DistributedBuildRoles.Worker)
                    {
                        // The pip was cancelled due to the scheduler terminating on this distributed worker.
                        // In case this pip result is reported back to the orchestrator (it might not if the worker is disconnected),
                        // we want to make the pip retry on another worker. For this, we need to set RetryInfo, or the pip
                        // will not be marked as cancelled (notice that the setting on PipResultStatus.Canceled
                        // in the ExecuteProcessAsync that wraps this method requires this).
                        result.RetryInfo = RetryInfo.GetDefault(RetryReason.RemoteWorkerFailure);
                    }
                }


                if (userRetry)
                {
                    counters.IncrementCounter(PipExecutorCounter.ProcessUserRetriesImpactedPipsCount);
                    result.HadUserRetries = true;
                }

                if (aggregatePipProperties.Count > 0)
                {
                    result.PipProperties = aggregatePipProperties;
                }

                return result;
            }
        }

        private static bool IncrementInternalErrorRetryCounters(RetryReason retryReason, CounterCollection<PipExecutorCounter> counters)
        {
            // Return true to break the caller's loop
            switch (retryReason)
            {
                case RetryReason.OutputWithNoFileAccessFailed:
                    counters.IncrementCounter(PipExecutorCounter.OutputsWithNoFileAccessRetriesCount);
                    return true;

                case RetryReason.MismatchedMessageCount:
                    counters.IncrementCounter(PipExecutorCounter.MismatchMessageRetriesCount);
                    return true;

                case RetryReason.AzureWatsonExitCode:
                    counters.IncrementCounter(PipExecutorCounter.AzureWatsonExitCodeRetriesCount);
                    return true;

                case RetryReason.VmExecutionError:
                    counters.IncrementCounter(PipExecutorCounter.VmExecutionRetriesCount);
                    return true;
            }

            return false; // Unhandled case, needs to be handled by the caller
        }

        private static void LogRetryInlineErrors(RetryReason retryReason, OperationContext operationContext, Process pip, string processDescription)
        {
            switch (retryReason)
            {
                case RetryReason.OutputWithNoFileAccessFailed:
                    Logger.Log.FailPipOutputWithNoAccessed(
                        operationContext,
                        pip.SemiStableHash,
                        processDescription);
                    return;

                case RetryReason.MismatchedMessageCount:
                    Logger.Log.LogMismatchedDetoursErrorCount(
                        operationContext,
                        pip.SemiStableHash,
                        processDescription);
                    return;

                case RetryReason.AzureWatsonExitCode:
                    Logger.Log.PipExitedWithAzureWatsonExitCode(
                        operationContext,
                        pip.SemiStableHash,
                        processDescription);
                    return;
            }
        }

        private static void LogUserSpecifiedExitCodeEvent(SandboxedProcessPipExecutionResult result, OperationContext operationContext, PipExecutionContext context, Process pip, string processDescription, int remainingUserRetries)
        {
            var stdErr = string.Empty;
            if (result.EncodedStandardError != null)
            {
                string path = result.EncodedStandardError.Item1.ToString(context.PathTable);
                if (File.Exists(path))
                {
                    stdErr += Environment.NewLine
                             + "Standard error:"
                             + Environment.NewLine
                             + File.ReadAllText(path, result.EncodedStandardError.Item2);
                }
            }

            var stdOut = string.Empty;
            if (result.EncodedStandardOutput != null)
            {
                string path = result.EncodedStandardOutput.Item1.ToString(context.PathTable);
                if (File.Exists(path))
                {
                    stdOut += Environment.NewLine
                             + "Standard output:"
                             + Environment.NewLine
                             + File.ReadAllText(path, result.EncodedStandardOutput.Item2);
                }
            }
            Logger.Log.PipWillBeRetriedDueToExitCode(
                operationContext,
                pip.SemiStableHash,
                processDescription,
                result.ExitCode,
                remainingUserRetries,
                stdErr,
                stdOut);
        }

        /// <summary>
        /// A pip needs a sideband file if
        ///   - the sideband root directory is set up in the configuration layout, and
        ///   - the pip's semistable hash is not 0 (happens only in tests where multiple pips can have this hash)
        ///   - the pip has shared opaque directory outputs
        /// </summary>
        private static bool PipNeedsSidebandFile(IPipExecutionEnvironment env, Process pip)
        {
            return
                env.Configuration.Layout.SharedOpaqueSidebandDirectory.IsValid
                && env.Configuration.Schedule.UnsafeLazySODeletion
                && pip.SemiStableHash != 0
                && pip.HasSharedOpaqueDirectoryOutputs;
        }

        /// <summary>
        /// Creates and returns a <see cref="SidebandWriter"/> when the <see cref="PipNeedsSidebandFile"/> condition is met; returns <c>null</c> otherwise.
        /// </summary>
        private static SidebandWriter CreateSidebandWriterIfNeeded(IPipExecutionEnvironment env, Process pip)
            => PipNeedsSidebandFile(env, pip) ? CreateSidebandWriter(env, pip) : null;

        /// <summary>
        /// Creates a <see cref="SidebandWriter"/> for <paramref name="pip"/>.
        /// </summary>
        private static SidebandWriter CreateSidebandWriter(IPipExecutionEnvironment env, Process pip)
            => SidebandWriterHelper.CreateSidebandWriterFromProcess(CreateSidebandMetadata(env, pip), env.Context, pip, env.Configuration.Layout.SharedOpaqueSidebandDirectory);

        /// <summary>
        /// Uses <see cref="IPipExecutionEnvironment.ContentFingerprinter"/> of <paramref name="env"/> to look up the static
        /// fingerprint for <paramref name="pip"/>, then creates <see cref="SidebandMetadata"/> from it and the pip's semistable hash.
        /// </summary>
        public static SidebandMetadata CreateSidebandMetadata(IPipExecutionEnvironment env, Process pip)
            => CreateSidebandMetadata(env.ContentFingerprinter.StaticFingerprintLookup(pip.PipId), pip);

        /// <summary>
        /// Creates a <see cref="SidebandMetadata"/> from the static fingerprint and the pip's semistable hash.
        /// </summary>
        public static SidebandMetadata CreateSidebandMetadata(BuildXL.Cache.MemoizationStore.Interfaces.Sessions.Fingerprint staticFingerprint, Process pip)
        {
            return new SidebandMetadata(
                pip.SemiStableHash,
                // in some tests the static fingerprint can have 0 length in which case ToByteArray() throws
                staticFingerprint.Length > 0 ? staticFingerprint.ToByteArray() : new byte[0]);
        }

        /// <summary>
        /// Tries to find a valid cache descriptor for the given process.
        /// - If a cache lookup proceeds successfully (whether or not it produces a usable descriptor / runnable-from-cache process),
        ///   a non-null result is returned.
        /// - If cache lookup fails (i.e., the result is inconclusive due to failed hashing, etc.), a null result is returned.
        /// </summary>
        public static async Task<RunnableFromCacheResult> TryCheckProcessRunnableFromCacheAsync(
            ProcessRunnablePip processRunnable,
            PipExecutionState.PipScopeState state,
            CacheableProcess cacheableProcess,
            bool avoidRemoteLookups = false)
        {
            var environment = processRunnable.Environment;

            BoxRef<PipCacheMissEventData> pipCacheMiss = new PipCacheMissEventData
            {
                PipId = processRunnable.PipId,
                CacheMissType = PipCacheMissType.Invalid,
            };

            BoxRef<ProcessFingerprintComputationEventData> processFingerprintComputationResult = new ProcessFingerprintComputationEventData
            {
                Kind = FingerprintComputationKind.CacheCheck,
                PipId = cacheableProcess.Process.PipId,
                StrongFingerprintComputations = CollectionUtilities.EmptyArray<ProcessStrongFingerprintComputationData>(),
            };

            var operationContext = processRunnable.OperationContext;

            RunnableFromCacheResult runnableFromCacheResult = null;

            using (var strongFingerprintComputationListWrapper = SchedulerPools.StrongFingerprintDataListPool.GetInstance())
            using (var weakFingerprintSetWrapper = SchedulerPools.WeakContentFingerprintSet.GetInstance())
            using (operationContext.StartOperation(PipExecutorCounter.CheckProcessRunnableFromCacheDuration))
            {
                List<BoxRef<ProcessStrongFingerprintComputationData>> strongFingerprintComputationList = strongFingerprintComputationListWrapper.Instance;

                // We collect here all the augmented weak fingerprints that were traversed during this cache lookup.
                var traversedAugmentedWeakFingerprintSet = weakFingerprintSetWrapper.Instance;

                runnableFromCacheResult = await TryCheckProcessRunnableFromCacheAsync(
                    processRunnable,
                    state,
                    cacheableProcess,
                    computeWeakFingerprint: () => new WeakContentFingerprint(cacheableProcess.ComputeWeakFingerprint().Hash),
                    pipCacheMiss,
                    processFingerprintComputationResult,
                    strongFingerprintComputationList,
                    canAugmentWeakFingerprint: processRunnable.Process.AugmentWeakFingerprintPathSetThreshold(processRunnable.Environment.Configuration.Cache) > 0,
                    isWeakFingerprintAugmented: false,
                    avoidRemoteLookups: avoidRemoteLookups,
                    pathSetCheckData: new PathSetCheckData(operationContext, processRunnable),
                    traversedAugmentedWeakFingerprintSet);

                processFingerprintComputationResult.Value.StrongFingerprintComputations = strongFingerprintComputationList.SelectArray(s => s.Value);

                if (runnableFromCacheResult != null)
                {
                    if (runnableFromCacheResult.CanRunFromCache)
                    {
                        // Track from which build (by session id and related session id) the cache hit data comes.
                        processFingerprintComputationResult.Value.SessionId = runnableFromCacheResult.GetCacheHitData().Metadata.SessionId;
                        processFingerprintComputationResult.Value.RelatedSessionId = runnableFromCacheResult.GetCacheHitData().Metadata.RelatedSessionId;
                    }
                    else
                    {
                        Contract.Assert(pipCacheMiss.Value.CacheMissType != PipCacheMissType.Invalid, "Must have valid cache miss reason");

                        Logger.Log.ScheduleProcessPipCacheMiss(
                            processRunnable.OperationContext,
                            cacheableProcess.Description,
                            runnableFromCacheResult.Fingerprint.ToString(),
                            pipCacheMiss.Value.CacheMissType.ToString());

                        processRunnable.Environment.State.ExecutionLog?.PipCacheMiss(pipCacheMiss.Value);
                    }
                }
            }

            using (operationContext.StartOperation(PipExecutorCounter.CheckProcessRunnableFromCacheExecutionLogDuration))
            {
                processRunnable.Environment.State.ExecutionLog?.ProcessFingerprintComputation(processFingerprintComputationResult.Value);
            }

            return runnableFromCacheResult;
        }

        private static async Task<RunnableFromCacheResult> TryCheckProcessRunnableFromCacheAsync(
            ProcessRunnablePip processRunnable,
            PipExecutionState.PipScopeState state,
            CacheableProcess cacheableProcess,
            Func<WeakContentFingerprint> computeWeakFingerprint,
            BoxRef<PipCacheMissEventData> pipCacheMiss,
            BoxRef<ProcessFingerprintComputationEventData> processFingerprintComputationResult,
            List<BoxRef<ProcessStrongFingerprintComputationData>> strongFingerprintComputationList,
            bool canAugmentWeakFingerprint,
            bool isWeakFingerprintAugmented,
            bool avoidRemoteLookups,
            PathSetCheckData pathSetCheckData,
            HashSet<WeakContentFingerprint> traversedAugmentedWeakFingerprintSet)
        {
            Contract.Requires(processRunnable != null);
            Contract.Requires(cacheableProcess != null);
            Contract.Requires(!isWeakFingerprintAugmented || !canAugmentWeakFingerprint);

            var operationContext = processRunnable.OperationContext;
            var environment = processRunnable.Environment;

            var pathTable = environment.Context.PathTable;
            Contract.Assume(pathTable != null);
            var cache = environment.State.Cache;
            Contract.Assume(cache != null);
            var content = environment.Cache.ArtifactContentCache;
            Contract.Assume(content != null);

            var process = cacheableProcess.Process;

            int numPathSetsDownloaded = 0, numCacheEntriesVisited = 0, numCacheEntriesAbsent = 0;
            WeakContentFingerprint weakFingerprint;

            using (operationContext.StartOperation(PipExecutorCounter.ComputeWeakFingerprintDuration))
            {
                weakFingerprint = computeWeakFingerprint();

                if (!isWeakFingerprintAugmented)
                {
                    // Only set the weak fingerprint if it is not an augmented one.
                    // The reason for this is we want to hide the fact that the weak fingerprint is augmented.
                    processFingerprintComputationResult.Value.WeakFingerprint = weakFingerprint;
                }
            }

            RunnableFromCacheResult result;

            // Check whether this is not the first time we are seeing this augmented weak fingerprint as part of this lookup.
            // This can happen if due to a race (e.g. concurrent builds) more than one special marker entry (with StrongContentFingerprint.AugmentedWeakFingerprintMarker)
            // was pushed to the cache with the same (weak fp -> augmented fp) values. We don't need to go through all the checks again, we know this was a miss the first time.
            if (isWeakFingerprintAugmented && !traversedAugmentedWeakFingerprintSet.Add(weakFingerprint))
            {
                Logger.Log.TwoPhaseCacheDescriptorDuplicatedAugmentedFingerprint(
                    operationContext,
                    processRunnable.Description,
                    weakFingerprint.ToString());

                // The miss reasons might have been slightly different the first time (e.g. we could have failed because content couldn't be downloaded)
                // but it is not worth storing the original reason and the extra complexity that would bring. The original reason for the miss was also
                // logged already.
                result = RunnableFromCacheResult.CreateForMiss(weakFingerprint, PipCacheMissType.MissForDescriptorsDueToAugmentedWeakFingerprints);
                pipCacheMiss.Value.CacheMissType = PipCacheMissType.MissForDescriptorsDueToAugmentedWeakFingerprints;
            }
            else
            {
                result = await innerCheckRunnableFromCacheAsync();
            }

            // Update the strong fingerprint computations list

            processRunnable.CacheLookupPerfInfo.LogCounters(pipCacheMiss.Value.CacheMissType, numPathSetsDownloaded, numCacheEntriesVisited, numCacheEntriesAbsent);

            Logger.Log.PipCacheLookupStats(
                operationContext,
                process.FormattedSemiStableHash,
                isWeakFingerprintAugmented,
                weakFingerprint.ToString(),
                numCacheEntriesVisited,
                numCacheEntriesAbsent,
                numPathSetsDownloaded);

            return result;

            // Extracted local function with main logic for performing the cache lookup.
            // This is done to ensure that execution log logging is always done even in cases of early return (namely augmented weak fingerprint cache lookup
            // defers to an inner cache lookup and performs an early return of the result)
            async Task<RunnableFromCacheResult> innerCheckRunnableFromCacheAsync()
            {
                // Totally usable descriptor (may additionally require content availability), or null.
                RunnableFromCacheResult.CacheHitData cacheHitData = null;
                PublishedEntryRefLocality? refLocality;
                ObservedInputProcessingResult? maybeUsableProcessingResult = null;

                string description = processRunnable.Description;

                // Augmented weak fingerprint used for storing cache entry in case of cache miss
                WeakContentFingerprint? augmentedWeakFingerprint = null;
                BoxRef<PipCacheMissEventData> augmentedWeakFingerprintMiss = null;

                if (cacheableProcess.ShouldHaveArtificialMiss())
                {
                    pipCacheMiss.Value.CacheMissType = PipCacheMissType.MissForDescriptorsDueToArtificialMissOptions;
                    Logger.Log.ScheduleArtificialCacheMiss(operationContext, description);
                    refLocality = null;
                }
                else if (cacheableProcess.DisableCacheLookup())
                {
                    // No sense in going into the strong fingerprint lookup if cache lookup is disabled.
                    pipCacheMiss.Value.CacheMissType = PipCacheMissType.MissForProcessConfiguredUncacheable;
                    Logger.Log.ScheduleProcessConfiguredUncacheable(operationContext, description);
                    refLocality = null;
                }
                else
                {

                    // Chapter 1: Determine Strong Fingerprint
                    // First, we will evaluate a sequence of (path set, strong fingerprint) pairs.
                    // Each path set generates a particular strong fingerprint based on local build state (input hashes);
                    // if we find a pair such that the generated strong fingerprint matches, then we should be able to find
                    // a usable entry (describing the output hashes, etc.) to replay.

                    // We will generally set this to the first usable-looking entry we find, if any. In particular:
                    // * If an entry-ref can't be fetched, we do not bother investigating further pairs. This is a fairly unusual failure for well-behaved caches.
                    // * If an entry refers to content that cannot be found, we keep traversing potential pairs. This is a case that is not uncommon when garbage collection
                    //   is done purely based on access time and without a semantic knowledge of all the elements that make up the cache metadata (e.g. a blob-based cache that uses 
                    //   the blob lifetime management rules as the garbage collection mechanism)
                    // Overall, a usable-looking entry is assigned at most once for entry into Chapter 2. Whenever a usable-looking entry is assigned, an entry fetch result is also set (successful or not)
                    PublishedEntryRef? maybeUsableEntryRef = null;
                    Possible<CacheEntry?>? entryFetchResult = null;
                    ObservedPathSet? maybePathSet = null;
                    OperationHints hints = new() { AvoidRemote = avoidRemoteLookups };

                    // Set if we find a usable entry.
                    refLocality = null;

                    using (operationContext.StartOperation(PipExecutorCounter.CheckProcessRunnableFromCacheChapter1DetermineStrongFingerprintDuration))
                    using (var strongFingerprintCacheWrapper = SchedulerPools.HashFingerprintDataMapPool.GetInstance())
                    {
                        // It is common to have many entry refs for the same PathSet, since often path content changes more often than the set of paths
                        // (i.e., the refs differ by strong fingerprint). We cache the strong fingerprint computation per PathSet; this saves the repeated
                        // cost of fetching and deserializing the path set, validating access to the paths and finding their content, and computing the overall strong fingerprint.
                        // For those path sets that are ill-defined for the pip (e.g. inaccessible paths), we use a null marker.
                        Dictionary<ContentHash, Tuple<BoxRef<ProcessStrongFingerprintComputationData>, ObservedInputProcessingResult, ObservedPathSet>> strongFingerprintCache =
                            strongFingerprintCacheWrapper.Instance;

                        foreach (Task<Possible<PublishedEntryRef, Failure>> batchPromise in cache.ListPublishedEntriesByWeakFingerprint(operationContext, weakFingerprint, hints))
                        {
                            if (environment.Context.CancellationToken.IsCancellationRequested)
                            {
                                break;
                            }

                            if (!pathSetCheckData.ShouldCheckMorePathSet)
                            {
                                Logger.Log.TwoPhaseReachMaxPathSetsToCheck(
                                    operationContext,
                                    description,
                                    pathSetCheckData.NumberOfUniquePathSetsToCheck,
                                    weakFingerprint.ToString(),
                                    isWeakFingerprintAugmented);
                                break;
                            }

                            Possible<PublishedEntryRef> maybeBatch;
                            using (operationContext.StartOperation(PipExecutorCounter.CacheQueryingWeakFingerprintDuration))
                            {
                                maybeBatch = await batchPromise;
                            }

                            if (!maybeBatch.Succeeded)
                            {
                                Logger.Log.TwoPhaseFailureQueryingWeakFingerprint(
                                    operationContext,
                                    description,
                                    weakFingerprint.ToString(),
                                    maybeBatch.Failure.DescribeIncludingInnerFailures());

                                if (maybeBatch.Failure is CacheTimeoutFailure)
                                {
                                    // if ListPublishedEntriesByWeakFingerprint timed out, this pip will be a cache miss.
                                    break;
                                }

                                continue;
                            }

                            PublishedEntryRef entryRef = maybeBatch.Result;

                            if (entryRef.IgnoreEntry)
                            {
                                continue;
                            }

                            // Only increment for valid entries
                            ++numCacheEntriesVisited;

                            // First, we use the path-set component of the entry to compute the strong fingerprint we would accept.
                            // Note that we often can re-use an already computed strong fingerprint (this wouldn't be needed if instead
                            // the cache returned (path set, [strong fingerprint 1, strong fingerprint 2, ...])
                            Tuple<BoxRef<ProcessStrongFingerprintComputationData>, ObservedInputProcessingResult, ObservedPathSet> strongFingerprintComputation;
                            StrongContentFingerprint? strongFingerprint = null;
                            if (!strongFingerprintCache.TryGetValue(entryRef.PathSetHash, out strongFingerprintComputation))
                            {
                                using (operationContext.StartOperation(PipExecutorCounter.TryLoadPathSetFromContentCacheDuration))
                                {
                                    maybePathSet = await TryLoadPathSetFromContentCacheAsync(
                                        operationContext,
                                        environment,
                                        description,
                                        weakFingerprint,
                                        entryRef.PathSetHash,
                                        avoidRemoteLookups);
                                }

                                ++numPathSetsDownloaded;

                                if (!maybePathSet.HasValue)
                                {
                                    // Failure reason already logged.
                                    // Poison this path set hash so we don't repeatedly try to retrieve and parse it.
                                    strongFingerprintCache[entryRef.PathSetHash] = null;
                                    continue;
                                }

                                var pathSet = maybePathSet.Value;

                                // Record the most relevant strong fingerprint information, defaulting to information retrieved from cache
                                BoxRef<ProcessStrongFingerprintComputationData> strongFingerprintComputationData = new ProcessStrongFingerprintComputationData(
                                    pathSet: pathSet,
                                    pathSetHash: entryRef.PathSetHash,
                                    priorStrongFingerprints: new List<StrongContentFingerprint>(1) { entryRef.StrongFingerprint });

                                strongFingerprintComputationList.Add(strongFingerprintComputationData);

                                // check if now running with safer options than before (i.e., prior are not strictly safer than current)
                                var currentUnsafeOptions = state.UnsafeOptions;
                                var priorUnsafeOptions = pathSet.UnsafeOptions;

                                if (priorUnsafeOptions.IsLessSafeThan(currentUnsafeOptions))
                                {
                                    // This path set's options are less safe than our current options so we cannot use it. Just ignore it.
                                    // Poison this path set hash so we don't repeatedly try to retrieve and parse it.
                                    strongFingerprintCache[entryRef.PathSetHash] = null;
                                    continue;
                                }

                                (ObservedInputProcessingResult observedInputProcessingResult, StrongContentFingerprint computedStrongFingerprint) =
                                    await TryComputeStrongFingerprintBasedOnPriorObservedPathSetAsync(
                                        operationContext,
                                        environment,
                                        state,
                                        cacheableProcess,
                                        weakFingerprint,
                                        pathSet,
                                        entryRef.PathSetHash);

                                pathSetCheckData.DecrementRemainingUniquePathSetsToCheck();
                                pathSetCheckData.WarnAboutTooManyUniquePathSetsIfNeeded();

                                ObservedInputProcessingStatus processingStatus = observedInputProcessingResult.Status;

                                switch (processingStatus)
                                {
                                    case ObservedInputProcessingStatus.Success:
                                        strongFingerprint = computedStrongFingerprint;
                                        Contract.Assume(strongFingerprint.HasValue);

                                        strongFingerprintComputationData.Value = strongFingerprintComputationData.Value.ToSuccessfulResult(
                                            computedStrongFingerprint: computedStrongFingerprint,
                                            observedInputs: observedInputProcessingResult.ObservedInputs.BaseArray);

                                        if (ETWLogger.Log.IsEnabled(EventLevel.Verbose, Keywords.Diagnostics))
                                        {
                                            Logger.Log.TwoPhaseStrongFingerprintComputedForPathSet(
                                                operationContext,
                                                description,
                                                weakFingerprint.ToString(),
                                                entryRef.PathSetHash.ToHex(),
                                                strongFingerprint.Value.ToString());
                                        }

                                        break;
                                    case ObservedInputProcessingStatus.Mismatched:
                                        // This pip can't access some of the paths. We should remember that (the path set may be repeated many times).
                                        strongFingerprint = null;
                                        if (ETWLogger.Log.IsEnabled(EventLevel.Verbose, Keywords.Diagnostics))
                                        {
                                            Logger.Log.TwoPhaseStrongFingerprintUnavailableForPathSet(
                                                operationContext,
                                                description,
                                                weakFingerprint.ToString(),
                                                entryRef.PathSetHash.ToHex());
                                        }

                                        break;
                                    default:
                                        AssumeErrorWasLoggedWhenNotCancelled(environment, operationContext);
                                        Contract.Assert(processingStatus == ObservedInputProcessingStatus.Aborted);

                                        // An error has already been logged. We have to bail out and fail the pip.
                                        return null;
                                }

                                strongFingerprintCache[entryRef.PathSetHash] = strongFingerprintComputation = Tuple.Create(strongFingerprintComputationData, observedInputProcessingResult, pathSet);
                            }
                            else if (strongFingerprintComputation != null)
                            {
                                // Add the strong fingerprint to the list of strong fingerprints to be reported
                                strongFingerprintComputation.Item1.Value.AddPriorStrongFingerprint(entryRef.StrongFingerprint);

                                // Set the strong fingerprint computed for this path set so it can be compared to the
                                // prior strong fingerprint for a cache hit/miss
                                if (strongFingerprintComputation.Item1.Value.Succeeded)
                                {
                                    strongFingerprint = strongFingerprintComputation.Item1.Value.ComputedStrongFingerprint;
                                }
                            }

                            // Now we might have a strong fingerprint.
                            if (!strongFingerprint.HasValue)
                            {
                                // Recall that 'null' is a special value meaning 'this path set will never work'
                                continue;
                            }

                            if (strongFingerprint.Value == entryRef.StrongFingerprint)
                            {
                                // Hit! Before we commit to this entry-ref, let's try to fetch the entry. We want to keep
                                // traversing candidates if the entry happens to be absent (being eviction the most common cause for that).
                                using (operationContext.StartOperation(PipExecutorCounter.CheckProcessRunnableFromCacheChapter2RetrieveCacheEntryDuration))
                                {
                                    // Chapter 2: Retrieve Cache Entry
                                    // If we found a usable-looking entry-ref, then we should be able to fetch the actual entry (containing metadata, and output hashes).

                                    // The speed of Chapter2 is basically all just this call to GetContentHashList
                                    var maybeEntryFetchResult = await cache.TryGetCacheEntryAsync(
                                        cacheableProcess.Process,
                                        weakFingerprint,
                                        entryRef.PathSetHash,
                                        entryRef.StrongFingerprint,
                                        hints);

                                    // TryGetCacheEntryAsync indicates a graceful miss by returning a null entry. We want to keep traversing candidates
                                    // in this case. The entry may have been evicted, but there could be other (path set, strong fingerprint) pairs that
                                    // match.
                                    if (maybeEntryFetchResult.Succeeded == true && maybeEntryFetchResult.Result == null)
                                    {
                                        numCacheEntriesAbsent++;
                                        continue;
                                    }

                                    // At this point we either successfully retrieved a non-absent entry, or we failed to retrieve an entry (e.g. due to a cache error). In any
                                    // of these cases we will immediately commit to this entry-ref and won't explore further pairs. We will have a cache-hit iff
                                    // the entry was fetched successfully and (if requested) the referenced content can be loaded.
                                    entryFetchResult = maybeEntryFetchResult;
                                }

                                strongFingerprintComputation.Item1.Value.IsStrongFingerprintHit = true;
                                maybeUsableEntryRef = entryRef;

                                // We remember locality (local or remote) for attribution later (e.g. we count remote hits separately from local hits).
                                refLocality = entryRef.Locality;

                                // We also remember the processingResult
                                maybeUsableProcessingResult = strongFingerprintComputation.Item2;
                                maybePathSet = strongFingerprintComputation.Item3;

                                Logger.Log.TwoPhaseStrongFingerprintMatched(
                                    operationContext,
                                    description,
                                    strongFingerprint: entryRef.StrongFingerprint.ToString(),
                                    strongFingerprintCacheId: entryRef.OriginatingCache);
                                environment.ReportCacheDescriptorHit(entryRef.OriginatingCache);
                                break;
                            }
                            else if (canAugmentWeakFingerprint && entryRef.StrongFingerprint == StrongContentFingerprint.AugmentedWeakFingerprintMarker)
                            {
                                // The strong fingeprint is the marker fingerprint indicating that computing an augmented weak fingerprint is required.
                                augmentedWeakFingerprint = new WeakContentFingerprint(strongFingerprint.Value.Hash);

                                // We want to give priority to the cache look-up with augmented weak fingerprint, and so we gives a new pip cahe miss event.
                                augmentedWeakFingerprintMiss = new PipCacheMissEventData
                                {
                                    PipId = processRunnable.PipId,
                                    CacheMissType = PipCacheMissType.Invalid,
                                };
                                strongFingerprintComputation.Item1.Value.AugmentedWeakFingerprint = augmentedWeakFingerprint;

                                // Notice this is a recursive call to same method with augmented weak fingerprint but disallowing
                                // further augmentation
                                var result = await TryCheckProcessRunnableFromCacheAsync(
                                    processRunnable,
                                    state,
                                    cacheableProcess,
                                    () => augmentedWeakFingerprint.Value,
                                    augmentedWeakFingerprintMiss,
                                    processFingerprintComputationResult,
                                    strongFingerprintComputationList,
                                    canAugmentWeakFingerprint: false,
                                    isWeakFingerprintAugmented: true,
                                    avoidRemoteLookups: avoidRemoteLookups,
                                    pathSetCheckData: pathSetCheckData,
                                    traversedAugmentedWeakFingerprintSet);

                                string keepAliveResult = "N/A";

                                try
                                {
                                    if (result == null)
                                    {
                                        // The recursive call can return null when observed input processor aborts (see innerCheckRunnableFromCacheAsync).
                                        // An error has already been logged - we should have asserted this already in the inner call before returning null
                                        // but it doesn't hurt to double-check.
                                        AssumeErrorWasLoggedWhenNotCancelled(environment, operationContext);
                                        return null;
                                    }

                                    if (result.CanRunFromCache)
                                    {
                                        // Fetch the augmenting path set entry to keep it alive
                                        // NOTE: This is best-effort so we don't observe the result here. This would
                                        // be a good candidate for incorporate since we don't actually need the cache entry
                                        var fetchAugmentingPathSetEntryResult = await cache.TryGetCacheEntryAsync(
                                            cacheableProcess.Process,
                                            weakFingerprint,
                                            entryRef.PathSetHash,
                                            entryRef.StrongFingerprint,
                                            hints);

                                        keepAliveResult = fetchAugmentingPathSetEntryResult.Succeeded
                                            ? (fetchAugmentingPathSetEntryResult.Result == null ? "Missing" : "Success")
                                            : fetchAugmentingPathSetEntryResult.Failure.Describe();

                                        return result;
                                    }
                                }
                                finally
                                {
                                    Logger.Log.AugmentedWeakFingerprint(
                                        operationContext,
                                        description,
                                        weakFingerprint: weakFingerprint.ToString(),
                                        augmentedWeakFingerprint: augmentedWeakFingerprint.ToString(),
                                        pathSetHash: entryRef.PathSetHash.ToHex(),
                                        pathCount: maybePathSet?.Paths.Length ?? -1,
                                        keepAliveResult: keepAliveResult);
                                }
                            }

                            if (ETWLogger.Log.IsEnabled(EventLevel.Verbose, Keywords.Diagnostics))
                            {
                                Logger.Log.TwoPhaseStrongFingerprintRejected(
                                    operationContext,
                                    description,
                                    pathSetHash: entryRef.PathSetHash.ToHex(),
                                    rejectedStrongFingerprint: entryRef.StrongFingerprint.ToString(),
                                    availableStrongFingerprint: strongFingerprint.Value.ToString());
                            }
                        }
                    }

                    // If we commited to a usable entry ref, then the entry fetch result shouldn't be null
                    Contract.Assert(!maybeUsableEntryRef.HasValue || entryFetchResult != null, "An entry ref was commited, and therefore the entry fetch result should be populated.");
                    // If we commited to a usable entry ref and the entry fetch result is successful, the fetch result cannot be null, since in that case we should have kept traversing
                    // candidates (and not commiting to an entry-ref)
                    Contract.Assert(!(maybeUsableEntryRef.HasValue && entryFetchResult?.Succeeded == true) || entryFetchResult?.Result != null, "The entry fetch result is successful but the content is absent.");

                    // Here we process the result of the lookup and set the cache miss type appropriately.
                    if (maybeUsableEntryRef.HasValue)
                    {
                        // We commited to an entry-ref but we couldn't fetch the cache entry. This is a fairly unusual case that indicates something wrong on the cache side
                        if (entryFetchResult?.Succeeded == false)
                        { 
                            Logger.Log.TwoPhaseFetchingCacheEntryFailed(
                                operationContext,
                                description,
                                maybeUsableEntryRef.Value.StrongFingerprint.ToString(),
                                entryFetchResult?.Failure.DescribeIncludingInnerFailures());
                            pipCacheMiss.Value.CacheMissType = PipCacheMissType.MissForCacheEntry;
                        }
                    }
                    else
                    {
                        // We didn't find a usable ref. We can attribute this as a new fingerprint (no refs checked at all)
                        // or a mismatch of strong fingerprints (at least one ref checked).
                        if (numCacheEntriesVisited == 0)
                        {
                            pipCacheMiss.Value.CacheMissType = isWeakFingerprintAugmented
                                ? PipCacheMissType.MissForDescriptorsDueToAugmentedWeakFingerprints
                                : PipCacheMissType.MissForDescriptorsDueToWeakFingerprints;

                            Logger.Log.TwoPhaseCacheDescriptorMissDueToWeakFingerprint(
                                    operationContext,
                                    description,
                                    weakFingerprint.ToString(),
                                    isWeakFingerprintAugmented);
                        }
                        else
                        {
                            if (augmentedWeakFingerprintMiss != null)
                            {
                                // If we ever check augmented weak fingerprint, then we use its miss reason as the miss type.
                                // This gives priority to the result from cache look-up with augmented weak fingerprint.
                                // This also makes the data align with execution because if we ever check augmented weak fingerprint,
                                // then the weak fingerprint for execution is the augmented one.
                                pipCacheMiss.Value.CacheMissType = augmentedWeakFingerprintMiss.Value.CacheMissType;
                            }
                            else
                            {
                                pipCacheMiss.Value.CacheMissType = PipCacheMissType.MissForDescriptorsDueToStrongFingerprints;
                                Logger.Log.TwoPhaseCacheDescriptorMissDueToStrongFingerprints(
                                    operationContext,
                                    description,
                                    weakFingerprint.ToString(),
                                    isWeakFingerprintAugmented);
                            }
                        }
                    }

                    // Having a successful entry fetched means that we are ready to try to convert this to a usable cache hit data instance
                    if (entryFetchResult?.Succeeded == true)
                    {
                        cacheHitData = await TryConvertToRunnableFromCacheResultAsync(
                            operationContext,
                            environment,
                            state,
                            cacheableProcess,
                            refLocality.Value,
                            description,
                            weakFingerprint,
                            maybeUsableEntryRef.Value.PathSetHash,
                            maybeUsableEntryRef.Value.StrongFingerprint,
                            entryFetchResult?.Result,
                            maybePathSet,
                            pipCacheMiss);
                    }
                }

                RunnableFromCacheResult runnableFromCacheResult;

                bool isCacheHit = cacheHitData != null;

                if (!isCacheHit)
                {
                    var pathSetCount = strongFingerprintComputationList.Count;
                    int threshold = processRunnable.Process.AugmentWeakFingerprintPathSetThreshold(environment.Configuration.Cache);
                    if (augmentedWeakFingerprint == null
                        && threshold > 0
                        && canAugmentWeakFingerprint
                        && pathSetCount >= threshold)
                    {
                        // Compute 'weak augmenting' path set with common paths among path sets
                        ObservedPathSet weakAugmentingPathSet = ExtractPathSetForAugmentingWeakFingerprint(environment.Configuration.Cache, process, strongFingerprintComputationList);

                        var minPathCount = strongFingerprintComputationList.Select(s => s.Value.PathSet.Paths.Length).Min();
                        var maxPathCount = strongFingerprintComputationList.Select(s => s.Value.PathSet.Paths.Length).Max();

                        var weakAugmentingPathSetHashResult = await cache.TryStorePathSetAsync(weakAugmentingPathSet, processRunnable.Process.PreservePathSetCasing);
                        string addAugmentingPathSetResultDescription;

                        if (weakAugmentingPathSetHashResult.Succeeded)
                        {
                            ContentHash weakAugmentingPathSetHash = weakAugmentingPathSetHashResult.Result;

                            // Optional (not currently implemented): If augmenting path set already exists (race condition), we
                            // could compute augmented weak fingerprint and perform the cache lookup as above
                            (ObservedInputProcessingResult observedInputProcessingResult, StrongContentFingerprint computedStrongFingerprint) =
                                await TryComputeStrongFingerprintBasedOnPriorObservedPathSetAsync(
                                    operationContext,
                                    environment,
                                    state,
                                    cacheableProcess,
                                    weakFingerprint,
                                    weakAugmentingPathSet,
                                    weakAugmentingPathSetHash);

                            BoxRef<ProcessStrongFingerprintComputationData> strongFingerprintComputation = new ProcessStrongFingerprintComputationData(
                                weakAugmentingPathSetHash,
                                new List<StrongContentFingerprint>() { StrongContentFingerprint.AugmentedWeakFingerprintMarker },
                                weakAugmentingPathSet);

                            // Add the computation of the augmenting weak fingerprint.
                            // This addition is particularly useful for fingerprint store based cache miss analysis. Later, post execution (due to cache miss),
                            // we will send ProcessStrongFingerprintComputationEventData to the fingerprint store. That event data will have the augmented
                            // weak fingerprint as its weak fingerprint. However, we only want to store the original weak fingerprint. Thus, we need
                            // to create a mapping from the augmented weak fingerprint to the original one when the ProcessStrongFingerprintComputationEventData
                            // is sent for the cache look-up. Since, the augmented weak fingerprint is actually the strong fingerprint of the current
                            // strongFingerprintComputation, then we need to include the computation into the computation list.
                            strongFingerprintComputationList.Add(strongFingerprintComputation);

                            if (observedInputProcessingResult.Status == ObservedInputProcessingStatus.Success)
                            {
                                // Add marker selector with weak augmenting path set
                                var addAugmentationResult = await cache.TryPublishCacheEntryAsync(
                                    cacheableProcess.Process,
                                    weakFingerprint,
                                    weakAugmentingPathSetHash,
                                    StrongContentFingerprint.AugmentedWeakFingerprintMarker,
                                    CacheEntry.FromArray((new[] { weakAugmentingPathSetHash }).ToReadOnlyArray(), "AugmentWeakFingerprint"));

                                addAugmentingPathSetResultDescription = addAugmentationResult.Succeeded
                                    ? addAugmentationResult.Result.Status.ToString()
                                    : addAugmentationResult.Failure.Describe();

                                augmentedWeakFingerprint = new WeakContentFingerprint(computedStrongFingerprint.Hash);
                                strongFingerprintComputation.Value = strongFingerprintComputation.Value.ToSuccessfulResult(
                                    computedStrongFingerprint,
                                    observedInputProcessingResult.ObservedInputs.BaseArray);

                                strongFingerprintComputation.Value.AugmentedWeakFingerprint = augmentedWeakFingerprint;
                                strongFingerprintComputation.Value.IsNewlyPublishedAugmentedWeakFingerprint = true;
                            }
                            else
                            {
                                addAugmentingPathSetResultDescription = observedInputProcessingResult.Status.ToString();
                            }
                        }
                        else
                        {
                            addAugmentingPathSetResultDescription = weakAugmentingPathSetHashResult.Failure.Describe();
                        }

                        Logger.Log.AddAugmentingPathSet(
                            operationContext,
                            cacheableProcess.Description,
                            weakFingerprint: weakFingerprint.ToString(),
                            pathSetHash: weakAugmentingPathSetHashResult.Succeeded ? weakAugmentingPathSetHashResult.Result.ToHex() : "N/A",
                            pathCount: weakAugmentingPathSet.Paths.Length,
                            pathSetCount: pathSetCount,
                            minPathCount: minPathCount,
                            maxPathCount: maxPathCount,
                            result: addAugmentingPathSetResultDescription);
                    }
                }

                WeakContentFingerprint cacheResultWeakFingerprint = isCacheHit || augmentedWeakFingerprint == null
                    ? weakFingerprint
                    : augmentedWeakFingerprint.Value;

                runnableFromCacheResult = CreateRunnableFromCacheResult(
                    cacheHitData,
                    environment,
                    refLocality,
                    maybeUsableProcessingResult,
                    cacheResultWeakFingerprint,
                    pipCacheMiss.Value.CacheMissType);

                return runnableFromCacheResult;
            }
        }

        /// <summary>
        /// Extract a path set to represent the commonly accessed paths which can be used to compute an augmented weak fingerprint
        /// </summary>
        private static ObservedPathSet ExtractPathSetForAugmentingWeakFingerprint(
            ICacheConfiguration cacheConfiguration,
            Process process,
            List<BoxRef<ProcessStrongFingerprintComputationData>> strongFingerprintComputationList)
        {
            var requiredUseCount = Math.Max(1, process.AugmentWeakFingerprintPathSetThreshold(cacheConfiguration) * process.AugmentWeakFingerprintRequiredPathCommonalityFactor(cacheConfiguration));
            using (var pool = s_pathToObservationEntryMapPool.GetInstance())
            using (var accessedNameUseCountMapPool = s_accessedFileNameToUseCountPool.GetInstance())
            using (var stringIdPool = Pools.StringIdSetPool.GetInstance())
            {
                Dictionary<AbsolutePath, ExtractedPathEntry> map = pool.Instance;
                var accessedNameUseCountMap = accessedNameUseCountMapPool.Instance;
                var accessedFileNameSet = stringIdPool.Instance;

                foreach (var pathSet in strongFingerprintComputationList.Select(s => s.Value.PathSet))
                {
                    // Union common observed access file names to increase the strength (specificity) of the augmented weak fingerprint
                    // for search path enumerations in the path sets.
                    foreach (var accessedFileName in pathSet.ObservedAccessedFileNames)
                    {
                        if (!accessedNameUseCountMap.TryGetValue(accessedFileName, out var useCount))
                        {
                            useCount = 0;
                        }

                        useCount++;

                        if (useCount >= requiredUseCount)
                        {
                            accessedFileNameSet.Add(accessedFileName);
                        }

                        accessedNameUseCountMap[accessedFileName] = useCount;
                    }

                    // Union common observed paths to increase the strength (specificity) of the augmented weak fingerprint.
                    foreach (ObservedPathEntry pathEntry in pathSet.Paths)
                    {
                        if (map.TryGetValue(pathEntry.Path, out var existingEntry))
                        {
                            if (IsCompatible(pathEntry, existingEntry.Entry))
                            {
                                existingEntry.UseCount++;
                                map[pathEntry.Path] = existingEntry;
                            }
                        }
                        else
                        {
                            map[pathEntry.Path] = new ExtractedPathEntry()
                            {
                                Entry = pathEntry,
                                UseCount = 1
                            };
                        }
                    }
                }

                var firstPathSet = strongFingerprintComputationList[0].Value.PathSet;
                var paths = SortedReadOnlyArray<ObservedPathEntry, ObservedPathEntryExpandedPathComparer>.CloneAndSort(
                    map.Values.Where(e => e.UseCount >= requiredUseCount).Select(e => e.Entry),
                    firstPathSet.Paths.Comparer);

                var observedAccessedFileNames = SortedReadOnlyArray<StringId, CaseInsensitiveStringIdComparer>.CloneAndSort(
                    accessedFileNameSet,
                    firstPathSet.ObservedAccessedFileNames.Comparer);

                return new ObservedPathSet(
                    paths,
                    observedAccessedFileNames,
                    // Use default unsafe options which prevents path set from ever being rejected due to
                    // incompatibility with the currently specified unsafe options during cache lookup
                    unsafeOptions: null);
            }
        }

        private static bool IsCompatible(ObservedPathEntry pathEntry, ObservedPathEntry existingEntry)
        {
            return pathEntry.Flags == existingEntry.Flags
                && pathEntry.EnumeratePatternRegex == existingEntry.EnumeratePatternRegex;
        }

        private static RunnableFromCacheResult CreateRunnableFromCacheResult(
            RunnableFromCacheResult.CacheHitData cacheHitData,
            IPipExecutionEnvironment environment,
            PublishedEntryRefLocality? refLocality,
            ObservedInputProcessingResult? observedInputProcessingResult,
            WeakContentFingerprint weakFingerprint,
            PipCacheMissType cacheMissType)
        {
            if (cacheHitData != null)
            {
                // We remembered the locality of the descriptor's ref earlier, since we want to count
                // 'remote' hits separately (i.e., how much does a remote cache help?)
                Contract.Assume(refLocality.HasValue);
                if (refLocality.Value == PublishedEntryRefLocality.Remote)
                {
                    environment.Counters.IncrementCounter(PipExecutorCounter.RemoteCacheHitsForProcessPipDescriptorAndContent);

                    // TODO: For now we estimate the size of remotely downloaded content as the sum of output sizes
                    //       for remote descriptors. However, this is an over-estimate of what was *actually* downloaded,
                    //       since some or all of that content may be already local (or maybe several outputs have the same content).
                    environment.Counters.AddToCounter(
                        PipExecutorCounter.RemoteContentDownloadedBytes,
                        cacheHitData.Metadata.TotalOutputSize);
                }

                return RunnableFromCacheResult.CreateForHit(
                    weakFingerprint,
                    // We use the weak fingerprint so that misses and hits are consistent (no strong fingerprint available on some misses).
                    dynamicObservations: observedInputProcessingResult.HasValue
                        ? observedInputProcessingResult.Value.DynamicObservations
                        : ReadOnlyArray<(AbsolutePath, DynamicObservationKind)>.Empty,
                    allowedUndeclaredSourceReads: observedInputProcessingResult.HasValue
                        ? observedInputProcessingResult.Value.AllowedUndeclaredSourceReads
                        : CollectionUtilities.EmptyDictionary<AbsolutePath, ObservedInputType>(),
                    cacheHitData: cacheHitData);
            }

            return RunnableFromCacheResult.CreateForMiss(weakFingerprint, cacheMissType);
        }

        /// <summary>
        /// Tries convert <see cref="ExecutionResult"/> to <see cref="RunnableFromCacheResult"/>.
        /// </summary>
        /// <remarks>
        /// This method is used for distributed cache look-up. The result of cache look-up done on the worker is transferred back
        /// to the orchestrator as <see cref="ExecutionResult"/> (for the sake of reusing existing transport structure). This method
        /// then converts it to <see cref="RunnableFromCacheResult"/> that can be consumed by the scheduler's cache look-up step.
        /// </remarks>
        [SuppressMessage("Microsoft.Usage", "CA1801:ReviewUnusedParameters")]
        public static RunnableFromCacheResult TryConvertToRunnableFromCacheResult(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state,
            CacheablePip pip,
            ExecutionResult executionResult)
        {
            Contract.Assert(!executionResult.Result.IndicatesFailure());
            Contract.Assert(executionResult.WeakFingerprint.HasValue);
            Contract.Assert(executionResult.CacheMissType.HasValue);

            if (executionResult.PipCacheDescriptorV2Metadata == null || executionResult.TwoPhaseCachingInfo == null)
            {
                return RunnableFromCacheResult.CreateForMiss(executionResult.WeakFingerprint.Value, executionResult.CacheMissType.Value);
            }

            var cacheHitData = TryCreatePipCacheDescriptorFromMetadata(
                operationContext,
                environment,
                state,
                pip,
                metadata: executionResult.PipCacheDescriptorV2Metadata,
                refLocality: PublishedEntryRefLocality.Remote,
                pathSetHash: executionResult.TwoPhaseCachingInfo.PathSetHash,
                strongFingerprint: executionResult.TwoPhaseCachingInfo.StrongFingerprint,
                metadataHash: executionResult.TwoPhaseCachingInfo.CacheEntry.MetadataHash);

            return cacheHitData != null
                ? RunnableFromCacheResult.CreateForHit(
                    weakFingerprint: executionResult.TwoPhaseCachingInfo.WeakFingerprint,
                    dynamicObservations: executionResult.DynamicObservations,
                    allowedUndeclaredSourceReads: executionResult.AllowedUndeclaredReads,
                    cacheHitData: cacheHitData)
                : RunnableFromCacheResult.CreateForMiss(executionResult.TwoPhaseCachingInfo.WeakFingerprint, executionResult.CacheMissType.Value);
        }

        private static async Task<RunnableFromCacheResult.CacheHitData> TryConvertToRunnableFromCacheResultAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state,
            CacheablePip pip,
            PublishedEntryRefLocality refLocality,
            string processDescription,
            WeakContentFingerprint weakFingerprint,
            ContentHash pathSetHash,
            StrongContentFingerprint strongFingerprint,
            CacheEntry? maybeUsableCacheEntry,
            ObservedPathSet? pathSet,
            BoxRef<PipCacheMissEventData> pipCacheMiss)
        {
            RunnableFromCacheResult.CacheHitData maybeParsedDescriptor = null;
            using (operationContext.StartOperation(PipExecutorCounter.CheckProcessRunnableFromCacheChapter3RetrieveAndParseMetadataDuration))
            {
                // Chapter 3: Interpret Cache Entry
                // Finally, we will try to turn a usable cache entry into a complete RunnableFromCacheResult.
                // Given a naked entry just retrieved from the cache, we need to interpret that entry according
                // to the process pip in question:
                // - The cache entry must have the special reserved slots for execution metadata and stdout/stderr.
                // - We *always* fetch metadata content (used as part of the RunnableFromCacheResult), even
                //   if fetching *output* content was not requested.
                if (maybeUsableCacheEntry != null)
                {
                    // Almost all of the cost of chapter3 is in the TryLoadAndDeserializeContent call
                    CacheEntry usableCacheEntry = maybeUsableCacheEntry.Value;
                    bool isFromHistoricMetadataCache = usableCacheEntry.OriginatingCache == HistoricMetadataCache.OriginatingCacheId;
                    Possible<PipCacheDescriptorV2Metadata> maybeMetadata =
                        await environment.State.Cache.TryRetrieveMetadataAsync(
                            pip.UnderlyingPip,
                            weakFingerprint,
                            strongFingerprint,
                            usableCacheEntry.MetadataHash,
                            pathSetHash);

                    if (maybeMetadata.Succeeded && maybeMetadata.Result != null)
                    {
                        environment.SchedulerTestHooks?.ReportPathSet(pathSet, pip.PipId);

                        maybeParsedDescriptor = TryCreatePipCacheDescriptorFromMetadata(
                            operationContext,
                            environment,
                            state,
                            pip,
                            maybeMetadata.Result,
                            refLocality,
                            pathSetHash,
                            strongFingerprint,
                            metadataHash: usableCacheEntry.MetadataHash);

                        // Parsing can fail if the descriptor is malformed, despite being valid from the cache's perspective
                        // (e.g. missing required content)
                        if (maybeParsedDescriptor == null)
                        {
                            Logger.Log.ScheduleInvalidCacheDescriptorForContentFingerprint(
                                        operationContext,
                                        processDescription,
                                        weakFingerprint.ToString(),
                                        GetCacheLevelForLocality(refLocality),
                                        string.Empty);
                            pipCacheMiss.Value.CacheMissType = PipCacheMissType.MissDueToInvalidDescriptors;
                        }
                    }
                    else if (!maybeMetadata.Succeeded)
                    {
                        environment.State.Cache.Counters.IncrementCounter(PipCachingCounter.MetadataRetrievalFails);
                        if (maybeMetadata.Failure is Failure<PipFingerprintEntry>)
                        {
                            Logger.Log.ScheduleInvalidCacheDescriptorForContentFingerprint(
                                operationContext,
                                processDescription,
                                weakFingerprint.ToString(),
                                GetCacheLevelForLocality(refLocality),
                                maybeMetadata.Failure.DescribeIncludingInnerFailures());
                            pipCacheMiss.Value.CacheMissType = PipCacheMissType.MissDueToInvalidDescriptors;
                        }
                        else
                        {
                            Logger.Log.TwoPhaseFetchingMetadataForCacheEntryFailed(
                                operationContext,
                                processDescription,
                                strongFingerprint.ToString(),
                                usableCacheEntry.MetadataHash.ToHex(),
                                maybeMetadata.Failure.DescribeIncludingInnerFailures());
                            pipCacheMiss.Value.CacheMissType = isFromHistoricMetadataCache
                                ? PipCacheMissType.MissForProcessMetadataFromHistoricMetadata
                                : PipCacheMissType.MissForProcessMetadata;
                        }
                    }
                    else
                    {
                        Contract.Assert(maybeMetadata.Result == null);

                        // This is a content-miss for the metadata blob. We expected it present since it was referenced
                        // by the cache entry, and for well-behaved caches that should imply a hit.
                        Logger.Log.TwoPhaseMissingMetadataForCacheEntry(
                            operationContext,
                            processDescription,
                            strongFingerprint: strongFingerprint.ToString(),
                            metadataHash: usableCacheEntry.MetadataHash.ToHex());
                        pipCacheMiss.Value.CacheMissType = isFromHistoricMetadataCache
                            ? PipCacheMissType.MissForProcessMetadataFromHistoricMetadata
                            : PipCacheMissType.MissForProcessMetadata;
                    }
                }

                if (maybeParsedDescriptor != null)
                {
                    // Descriptor hit. We may increment the 'miss due to unavailable content' counter below however.
                    Logger.Log.ScheduleCacheDescriptorHitForContentFingerprint(
                        operationContext,
                        processDescription,
                        weakFingerprint.ToString(),
                        maybeParsedDescriptor.Metadata.Id,
                        GetCacheLevelForLocality(refLocality));
                    environment.Counters.IncrementCounter(PipExecutorCounter.CacheHitsForProcessPipDescriptors);
                }
            }

            using (operationContext.StartOperation(PipExecutorCounter.CheckProcessRunnableFromCacheChapter4CheckContentAvailabilityDuration))
            {
                // Chapter 4: Check Content Availability
                // We additionally require output content availability.
                // This is the last check; we set `usableDescriptor` here.
                RunnableFromCacheResult.CacheHitData usableDescriptor;
                if (maybeParsedDescriptor != null)
                {
                    var missingOutputs = Lazy.Create(() => new ConcurrentQueue<(string, string)>());
                    bool isContentAvailable =
                        await
                            TryLoadAvailableOutputContentAsync(
                                operationContext,
                                environment,
                                pip,
                                maybeParsedDescriptor.CachedArtifactContentHashes,
                                strongFingerprint: strongFingerprint,
                                metadataHash: maybeParsedDescriptor.MetadataHash,
                                standardOutput: maybeParsedDescriptor.StandardOutput,
                                standardError: maybeParsedDescriptor.StandardError,
                                onContentUnavailable: (file, hash) => missingOutputs.Value.Enqueue((file.Path.ToString(environment.Context.PathTable), hash.ToHex())));

                    if (missingOutputs.IsValueCreated && !missingOutputs.Value.IsEmpty)
                    {
                        pipCacheMiss.Value.MissedOutputs = missingOutputs.Value.ToList();
                    }

                    if (!isContentAvailable)
                    {
                        usableDescriptor = null;

                        Logger.Log.ScheduleContentMissAfterContentFingerprintCacheDescriptorHit(
                            operationContext,
                            processDescription,
                            weakFingerprint.ToString(),
                            maybeParsedDescriptor.Metadata.Id);
                        pipCacheMiss.Value.CacheMissType = PipCacheMissType.MissForProcessOutputContent;
                    }
                    else
                    {
                        usableDescriptor = maybeParsedDescriptor;
                    }
                }
                else
                {
                    // Non-usable descriptor; no content to fetch, and we've failed already.
                    usableDescriptor = null;
                }

                return usableDescriptor;
            }
        }

        private static int GetCacheLevelForLocality(PublishedEntryRefLocality locality)
        {
            return locality == PublishedEntryRefLocality.Local ? 1 : 2;
        }

        private static async Task<ObservedPathSet?> TryLoadPathSetFromContentCacheAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            string processDescription,
            WeakContentFingerprint weakFingerprint,
            ContentHash pathSetHash,
            bool avoidRemoteLookups)
        {
            var maybePathSet = await environment.State.Cache.TryRetrievePathSetAsync(operationContext, weakFingerprint, pathSetHash, avoidRemoteLookups);

            if (!maybePathSet.Succeeded)
            {
                if (maybePathSet.Failure is ObservedPathSet.DeserializeFailure)
                {
                    Logger.Log.TwoPhasePathSetInvalid(
                        operationContext,
                        processDescription,
                        weakFingerprint: weakFingerprint.ToString(),
                        pathSetHash: pathSetHash.ToHex(),
                        failure: maybePathSet.Failure.Describe());
                }
                else
                {
                    Logger.Log.TwoPhaseLoadingPathSetFailed(
                        operationContext,
                        processDescription,
                        weakFingerprint: weakFingerprint.ToString(),
                        pathSetHash: pathSetHash.ToHex(),
                        failure: maybePathSet.Failure.DescribeIncludingInnerFailures());
                }

                return null;
            }

            return maybePathSet.Result;
        }

        private static void CheckCachedMetadataIntegrity(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            Process process,
            RunnableFromCacheResult runnableFromCacheCheckResult)
        {
            Contract.Requires(environment != null);
            Contract.Requires(process != null);
            Contract.Requires(runnableFromCacheCheckResult != null);
            Contract.Requires(runnableFromCacheCheckResult.CanRunFromCache);

            var pathTable = environment.Context.PathTable;
            var stringTable = environment.Context.StringTable;
            var cacheHitData = runnableFromCacheCheckResult.GetCacheHitData();
            var metadata = cacheHitData.Metadata;
            var currentProcessWeakFingerprintText = runnableFromCacheCheckResult.WeakFingerprint.ToString();
            var currentProcessStrongFingerprintText = cacheHitData.StrongFingerprint.ToString();

            if ((!string.IsNullOrEmpty(metadata.WeakFingerprint) &&
                 !string.Equals(currentProcessWeakFingerprintText, metadata.WeakFingerprint, StringComparison.OrdinalIgnoreCase))
                ||
                (!string.IsNullOrEmpty(metadata.StrongFingerprint) &&
                 !string.Equals(currentProcessStrongFingerprintText, metadata.StrongFingerprint, StringComparison.OrdinalIgnoreCase)))
            {
                string message =
                    I($"Metadata retrieved for Pip{process.SemiStableHash:X16} (Weak fingerprint: {currentProcessWeakFingerprintText}, Strong fingerprint: {currentProcessStrongFingerprintText}) belongs to Pip{metadata.SemiStableHash:X16} (Weak fingerprint:{metadata.WeakFingerprint}, Strong fingerprint:{metadata.StrongFingerprint})");
                var stringBuilder = new StringBuilder();
                stringBuilder.AppendLine(message);

                if (process.FileOutputs.Count(f => f.CanBeReferencedOrCached()) != cacheHitData.CachedArtifactContentHashes.Length)
                {
                    stringBuilder.AppendLine(I($"Output files of Pip{process.SemiStableHash:X16}:"));
                    stringBuilder.AppendLine(
                        string.Join(
                            Environment.NewLine,
                            process.FileOutputs.Where(f => f.CanBeReferencedOrCached())
                                .Select(f => "\t" + f.Path.ToString(pathTable))));
                }

                stringBuilder.AppendLine(
                    I($"Output files of Pip{process.SemiStableHash:X16} and their corresponding file names in metadata of Pip{metadata.SemiStableHash:X16}:"));
                stringBuilder.AppendLine(
                   string.Join(
                       Environment.NewLine,
                       cacheHitData.CachedArtifactContentHashes.Select(f => I($"\t{f.fileArtifact.Path.ToString(pathTable)} : ({f.fileMaterializationInfo.FileName.ToString(stringTable)})"))));

                if (process.DirectoryOutputs.Length != metadata.DynamicOutputs.Count)
                {
                    stringBuilder.AppendLine(I($"{Pip.FormatSemiStableHash(process.SemiStableHash)} and {Pip.FormatSemiStableHash(metadata.SemiStableHash)} have different numbers of output directories"));
                }

                Logger.Log.PipCacheMetadataBelongToAnotherPip(
                    operationContext.LoggingContext,
                    process.SemiStableHash,
                    process.GetDescription(environment.Context),
                    stringBuilder.ToString());

                throw new BuildXLException(message, ExceptionRootCause.CorruptedCache);
            }
        }

        private static void AssertNoFileNamesMismatch(
            IPipExecutionEnvironment environment,
            Process process,
            RunnableFromCacheResult runnableFromCacheCheckResult,
            FileArtifact file,
            in FileMaterializationInfo info)
        {
            Contract.Requires(environment != null);
            Contract.Requires(process != null);
            Contract.Requires(file.IsValid);

            if (!info.FileName.IsValid)
            {
                return;
            }

            PathAtom fileArtifactFileName = file.Path.GetName(environment.Context.PathTable);
            if (!info.FileName.CaseInsensitiveEquals(environment.Context.StringTable, fileArtifactFileName))
            {
                var pathTable = environment.Context.PathTable;
                var stringTable = environment.Context.StringTable;
                var cacheHitData = runnableFromCacheCheckResult.GetCacheHitData();

                string fileArtifactPathString = file.Path.ToString(pathTable);
                string fileMaterializationFileNameString = info.FileName.ToString(stringTable);
                var stringBuilder = new StringBuilder();
                stringBuilder.AppendLine(
                    I($"File name should only differ by casing. File artifact's full path: '{fileArtifactPathString}'; file artifact's file name: '{fileArtifactFileName.ToString(stringTable)}'; materialization info file name: '{fileMaterializationFileNameString}'."));
                stringBuilder.AppendLine(I($"[{process.FormattedSemiStableHash}] Weak FP: '{runnableFromCacheCheckResult.WeakFingerprint.ToString()}', Strong FP: '{cacheHitData.StrongFingerprint.ToString()}', Metadata Hash: '{cacheHitData.MetadataHash.ToString()}'"));

                Contract.Assert(false, stringBuilder.ToString());
            }
        }

        private static bool TryGetCacheHitExecutionResult(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            Process pip,
            RunnableFromCacheResult runnableFromCacheCheckResult,
            out ExecutionResult executionResult)
        {
            Contract.Requires(environment != null);
            Contract.Requires(pip != null);
            Contract.Requires(runnableFromCacheCheckResult != null);
            Contract.Requires(runnableFromCacheCheckResult.CanRunFromCache);

            var cacheHitData = runnableFromCacheCheckResult.GetCacheHitData();

            executionResult = new ExecutionResult
            {
                MustBeConsideredPerpetuallyDirty = IsUnconditionallyPerpetuallyDirty(pip, environment.PipGraphView),
                DynamicObservations = runnableFromCacheCheckResult.DynamicObservations,
                AllowedUndeclaredReads = runnableFromCacheCheckResult.AllowedUndeclaredReads,
            };

            executionResult.PopulateCacheInfoFromCacheResult(runnableFromCacheCheckResult);

            CheckCachedMetadataIntegrity(operationContext, environment, pip, runnableFromCacheCheckResult);

            for (int i = 0; i < cacheHitData.CachedArtifactContentHashes.Length; i++)
            {
                var info = cacheHitData.CachedArtifactContentHashes[i].fileMaterializationInfo;
                var file = cacheHitData.CachedArtifactContentHashes[i].fileArtifact;

                AssertNoFileNamesMismatch(environment, pip, runnableFromCacheCheckResult, file, info);
                executionResult.ReportOutputContent(file, info, PipOutputOrigin.NotMaterialized);
            }

            // For each opaque directory, iterate its dynamic outputs which are stored in cache descriptor metadata.
            // The ordering of pip.DirectoryOutputs and metadata.DynamicOutputs is consistent.

            // The index of the first artifact corresponding to an opaque directory input
            using (var poolFileList = Pools.GetFileArtifactWithAttributesList())
            using (var existenceAssertionsWrapper = Pools.GetFileArtifactSet())
            {
                HashSet<FileArtifact> existenceAssertions = existenceAssertionsWrapper.Instance;

                var fileList = poolFileList.Instance;
                for (int i = 0; i < pip.DirectoryOutputs.Length; i++)
                {
                    fileList.Clear();

                    // Let's validate here the existence assertions for opaques.
                    // Observe that even though this is a cache hit, the existence assertions may have changed
                    // Existence assertions are explicitly not part of the producer fingerprint since they are not
                    // part of its inputs, and a change in them shouldn't make a pip a cache miss
                    Contract.Assert(existenceAssertions.Count == 0);
                    existenceAssertions.AddRange(environment.PipGraphView.GetExistenceAssertionsUnderOpaqueDirectory(pip.DirectoryOutputs[i]));

                    foreach (var dynamicOutputFileAndInfo in cacheHitData.DynamicDirectoryContents[i])
                    {
                        var fileExistence = WellKnownContentHashUtilities.IsAbsentFileHash(dynamicOutputFileAndInfo.fileMaterializationInfo.FileContentInfo.Hash) ?
                                FileExistence.Temporary :
                                FileExistence.Required;
                        fileList.Add(FileArtifactWithAttributes.Create(
                            dynamicOutputFileAndInfo.fileArtifact,
                            fileExistence,
                            dynamicOutputFileAndInfo.fileMaterializationInfo.IsUndeclaredFileRewrite));

                        if (fileExistence == FileExistence.Required)
                        {
                            existenceAssertions.Remove(dynamicOutputFileAndInfo.fileArtifact);
                        }
                    }

                    // There are some outputs that were asserted as belonging to the opaque that were not found
                    if (existenceAssertions.Count != 0)
                    {
                        Processes.Tracing.Logger.Log.ExistenceAssertionUnderOutputDirectoryFailed(
                            operationContext,
                            pip.GetDescription(environment.Context),
                            existenceAssertions.First().Path.ToString(environment.Context.PathTable),
                            pip.DirectoryOutputs[i].Path.ToString(environment.Context.PathTable));

                        return false;
                    }

                    executionResult.ReportDirectoryOutput(pip.DirectoryOutputs[i], fileList);
                }
            }

            // Report absent files
            var absentFileInfo = FileMaterializationInfo.CreateWithUnknownLength(WellKnownContentHashes.AbsentFile);
            if (cacheHitData.AbsentArtifacts != null)
            {
                foreach (var absentFile in cacheHitData.AbsentArtifacts)
                {
                    executionResult.ReportOutputContent(absentFile, absentFileInfo, PipOutputOrigin.NotMaterialized);
                }
            }

            // Report the standard error/output files
            // These may or may not also be declared pip outputs, it is safe to report the content twice
            if (cacheHitData.StandardError != null)
            {
                var fileArtifact = GetCachedSandboxedProcessOutputArtifact(cacheHitData, pip, SandboxedProcessFile.StandardError);
                var fileMaterializationInfo = FileMaterializationInfo.CreateWithUnknownLength(cacheHitData.StandardError.Item2);

                executionResult.ReportOutputContent(fileArtifact, fileMaterializationInfo, PipOutputOrigin.NotMaterialized);
            }

            if (cacheHitData.StandardOutput != null)
            {
                var fileArtifact = GetCachedSandboxedProcessOutputArtifact(cacheHitData, pip, SandboxedProcessFile.StandardOutput);
                var fileMaterializationInfo = FileMaterializationInfo.CreateWithUnknownLength(cacheHitData.StandardOutput.Item2);

                executionResult.ReportOutputContent(fileArtifact, fileMaterializationInfo, PipOutputOrigin.NotMaterialized);
            }

            // Populate created directories from the cache hit data
            executionResult.ReportCreatedDirectories(cacheHitData.CreatedDirectories);

            executionResult.SetResult(operationContext, PipResultStatus.NotMaterialized);
            return true;
        }

        private static async Task<bool> ReplayWarningsFromCacheAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state,
            Process pip,
            RunnableFromCacheResult.CacheHitData cacheHitData)
        {
            Contract.Requires(environment != null);
            Contract.Requires(pip != null);
            Contract.Requires(cacheHitData.Metadata.NumberOfWarnings > 0);

            // TODO: Deploying here is redundant if the console stream was also a declared output.
            //       Should collapse together such that console streams are always declared outputs.
            var fileContentManager = environment.State.FileContentManager;
            IEnumerable<(FileArtifact, ContentHash)> failedFiles = CollectionUtilities.EmptyArray<(FileArtifact, ContentHash)>();

            if (cacheHitData.StandardOutput != null)
            {
                var file = GetCachedSandboxedProcessOutputArtifact(cacheHitData, pip, SandboxedProcessFile.StandardOutput);
                failedFiles = await TryMaterializeStandardOutputFileHelperAsync(
                    operationContext, 
                    pip, 
                    fileContentManager, 
                    failedFiles, 
                    file, 
                    cacheHitData.StandardOutput.Item2,
                    // Replaying warnings from cache is a blocking step. We don't want this materialization request to get throttled
                    // together with regular input/output materialization. We just need to materialize standard output and error (below),
                    // so materializing 2 files per pip without throttling shouldn't be an issue.
                    throttleMaterialization: false);
            }

            if (cacheHitData.StandardError != null)
            {
                var file = GetCachedSandboxedProcessOutputArtifact(cacheHitData, pip, SandboxedProcessFile.StandardError);
                failedFiles = await TryMaterializeStandardOutputFileHelperAsync(
                    operationContext, 
                    pip, 
                    fileContentManager, 
                    failedFiles, 
                    file, 
                    cacheHitData.StandardError.Item2,
                    // See throttling considerations on the materialization request for standard output above
                    throttleMaterialization: false);
            }

            if (failedFiles.Any())
            {
                // FileContentManager will log warnings for materialization failures
                // Log overall error for failed materialization
                Logger.Log.PipFailedToMaterializeItsOutputs(
                    operationContext,
                    pip.GetDescription(environment.Context),
                    new ArtifactMaterializationFailure(failedFiles.ToReadOnlyArray(), environment.Context.PathTable).DescribeIncludingInnerFailures());
                return false;
            }

            var pathTable = environment.Context.PathTable;
            var configuration = environment.Configuration;
            SemanticPathExpander semanticPathExpander = state.PathExpander;
            var pipDataRenderer = new PipFragmentRenderer(
                pathTable,
                monikerRenderer: monikerGuid => environment.IpcProvider.LoadAndRenderMoniker(monikerGuid),
                hashLookup: environment.ContentFingerprinter.ContentHashLookupFunction);

            var executor = new SandboxedProcessPipExecutor(
                environment.Context,
                operationContext.LoggingContext,
                pip,
                configuration,
                environment.RootMappings,
                pipEnvironment: environment.State.PipEnvironment,
                sidebandState: environment.State.SidebandState,
                directoryArtifactContext: new DirectoryArtifactContext(environment),
                allowlist: null,
                makeInputPrivate: null,
                makeOutputPrivate: null,
                semanticPathExpander: semanticPathExpander,
                pipDataRenderer: pipDataRenderer,
                directoryTranslator: environment.DirectoryTranslator,
                vmInitializer: environment.VmInitializer,
                tempDirectoryCleaner: environment.TempCleaner,
                reparsePointResolver: environment.ReparsePointAccessResolver,
                pipGraphFileSystemView: environment.PipGraphView,
                sandboxFileSystemView: environment.State.FileSystemView);

            if (!await executor.TryInitializeWarningRegexAsync())
            {
                AssertErrorWasLoggedWhenNotCancelled(environment, operationContext, errorMessage: "Error was not logged for initializing the warning regex");
                return false;
            }

            var standardOutput = GetOptionalSandboxedProcessOutputFromFile(pathTable, cacheHitData.StandardOutput, SandboxedProcessFile.StandardOutput);
            var standardError = GetOptionalSandboxedProcessOutputFromFile(pathTable, cacheHitData.StandardError, SandboxedProcessFile.StandardError);
            var success = await executor.TryLogWarningAsync(standardOutput, standardError);

            if (success)
            {
                environment.ReportWarnings(fromCache: true, count: cacheHitData.Metadata.NumberOfWarnings);
            }
            else
            {
                return false;
            }

            return true;
        }

        private static async Task<IEnumerable<(FileArtifact, ContentHash)>> TryMaterializeStandardOutputFileHelperAsync(
            OperationContext operationContext,
            Process pip,
            FileContentManager fileContentManager,
            IEnumerable<(FileArtifact, ContentHash)> failedFiles,
            FileArtifact file,
            ContentHash contentHash,
            bool throttleMaterialization)
        {
            var filesToMaterialize = new[] { file };
            var result = await fileContentManager.TryMaterializeFilesAsync(
                    requestingPip: pip,
                    operationContext: operationContext,
                    filesToMaterialize: filesToMaterialize,
                    materializatingOutputs: true,
                    isDeclaredProducer: pip.GetOutputs().Contains(file),
                    isApiServerRequest: false,
                    throttleMaterialization);

            if (result != ArtifactMaterializationResult.Succeeded)
            {
                failedFiles = failedFiles.Concat(new[] { (file, contentHash) });
            }

            return failedFiles;
        }

        private static FileArtifact GetCachedSandboxedProcessOutputArtifact(
            RunnableFromCacheResult.CacheHitData cacheHitData,
            Process pip,
            SandboxedProcessFile file)
        {
            var standardFileData = file == SandboxedProcessFile.StandardError ?
                cacheHitData.StandardError :
                cacheHitData.StandardOutput;

            if (standardFileData == null)
            {
                return FileArtifact.Invalid;
            }

            FileArtifact pipStandardFileArtifact = file.PipFileArtifact(pip);
            AbsolutePath standardFilePath = standardFileData.Item1;

            if (pipStandardFileArtifact.Path == standardFilePath)
            {
                return pipStandardFileArtifact;
            }

            return FileArtifact.CreateOutputFile(standardFilePath);
        }

        private static SandboxedProcessOutput GetOptionalSandboxedProcessOutputFromFile(
            PathTable pathTable,
            Tuple<AbsolutePath, ContentHash, string> output,
            SandboxedProcessFile file)
        {
            return output == null
                ? null
                : SandboxedProcessOutput.FromFile(
                    output.Item1.ToString(pathTable),
                    output.Item3,
                    file);
        }

        private static RunnableFromCacheResult.CacheHitData TryCreatePipCacheDescriptorFromMetadata(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state,
            CacheablePip pip,
            PipCacheDescriptorV2Metadata metadata,
            PublishedEntryRefLocality refLocality,
            ContentHash pathSetHash,
            StrongContentFingerprint strongFingerprint,
            ContentHash metadataHash)
        {
            Contract.Requires(environment != null);
            Contract.Requires(state != null);
            Contract.Requires(pip != null);
            
            var pathTable = environment.Context.PathTable;
            var stringTable = environment.Context.StringTable;
            var pathExpander = state.PathExpander;

            if (metadata.StaticOutputHashes.Count != pip.CacheableStaticOutputsCount ||
                metadata.DynamicOutputs.Count != pip.DirectoryOutputs.Length)
            {
                return null;
            }

            // TODO: We store a (path, hash, encoding) tuple for stdout/stderr on the metadata. This is because
            //       these streams have been special cases, and the output paths are not necessarily even declared on the pip...
            Tuple<AbsolutePath, ContentHash, string> standardOutput;
            Tuple<AbsolutePath, ContentHash, string> standardError;
            if (!TryParseOptionalStandardConsoleStreamHash(pathTable, pathExpander, metadata.StandardOutput, out standardOutput) ||
                !TryParseOptionalStandardConsoleStreamHash(pathTable, pathExpander, metadata.StandardError, out standardError))
            {
                return null;
            }

            List<FileArtifact> absentArtifacts = null; // Almost never populated, since outputs are almost always required.
            List<(FileArtifact, FileMaterializationInfo)> cachedArtifactContentHashes =
                new List<(FileArtifact, FileMaterializationInfo)>(pip.Outputs.Length);

            // Only the CanBeReferencedOrCached output will be saved in metadata.StaticOutputHashes
            // We looped metadata.StaticOutputHashes and meanwhile find the corresponding output in current executing pip.
            FileArtifactWithAttributes attributedOutput;
            using (var poolAbsolutePathFileArtifactWithAttributes = Pools.GetAbsolutePathFileArtifactWithAttributesMap())
            {
                Dictionary<AbsolutePath, FileArtifactWithAttributes> outputs = poolAbsolutePathFileArtifactWithAttributes.Instance;
                outputs.AddRange(pip.Outputs.Select(o => new KeyValuePair<AbsolutePath, FileArtifactWithAttributes>(o.Path, o)));

                foreach (var staticOutputHashes in metadata.StaticOutputHashes)
                {
                    // TODO: The code path that returns null looks dubious. Could they ever be reached? Should we write a contract here instead of silently concluding weak fingerprint miss?
                    
                    FileMaterializationInfo materializationInfo = staticOutputHashes.Info.ToFileMaterializationInfo(pathTable, outputDirectoryRoot: AbsolutePath.Invalid, dynamicOutputCaseSensitiveRelativeDirectory: RelativePath.Invalid);
                    AbsolutePath metadataPath = AbsolutePath.Create(pathTable, staticOutputHashes.AbsolutePath);

                    if (!outputs.TryGetValue(metadataPath, out attributedOutput))
                    {
                        // Output in metadata is missing from the pip specification; entry is invalid.
                        Logger.Log.InvalidMetadataStaticOutputNotFound(operationContext, pip.Description, staticOutputHashes.AbsolutePath);
                        return null;
                    }

                    Contract.Assert(attributedOutput.IsValid);

                    FileArtifact output = attributedOutput.ToFileArtifact();

                    // Following logic should be in sync with StoreContentForProcess method.
                    bool isRequired = IsRequiredForCaching(attributedOutput);

                    if (materializationInfo.Hash != WellKnownContentHashes.AbsentFile)
                    {
                        cachedArtifactContentHashes.Add((output, materializationInfo));
                    }
                    else if (isRequired)
                    {
                        // Required but looks absent; entry is invalid.
                        Logger.Log.InvalidMetadataRequiredOutputIsAbsent(operationContext, pip.Description, staticOutputHashes.AbsolutePath);
                        return null;
                    }
                    else
                    {
                        if (absentArtifacts == null)
                        {
                            absentArtifacts = new List<FileArtifact>();
                        }

                        absentArtifacts.Add(output);
                    }
                }
            }

            int staticExistentArtifactCount = cachedArtifactContentHashes.Count;

            // For each opaque directory, iterate its dynamic outputs which are stored in cache descriptor metadata.
            // The ordering of pip.DirectoryOutputs and metadata.DynamicOutputs is consistent.
            var opaqueIndex = 0;
            foreach (var opaqueDir in pip.DirectoryOutputs)
            {
                var dirPath = opaqueDir.Path;
                foreach (var dynamicOutput in metadata.DynamicOutputs[opaqueIndex++].RelativePathFileMaterializationInfos)
                {
                    // Dynamic output is stored with content hash and relative path from its opaque directory.
                    var filePath = dirPath.Combine(pathTable, RelativePath.Create(stringTable, dynamicOutput.RelativePath));
                    FileArtifact outputFile = FileArtifact.CreateOutputFile(filePath);
                    var relativeDirectory = RelativePath.Create(stringTable, dynamicOutput.RelativePath).GetParent();
                    cachedArtifactContentHashes.Add((outputFile, dynamicOutput.Info.ToFileMaterializationInfo(pathTable, opaqueDir.Path, relativeDirectory)));
                }
            }

            var cachedArtifactContentHashesArray = cachedArtifactContentHashes.ToArray();

            // Create segments of cached artifact contents array that correspond to dynamic directory contents
            var dynamicDirectoryContents = new ArrayView<(FileArtifact, FileMaterializationInfo)>[pip.DirectoryOutputs.Length];
            int lastDynamicArtifactIndex = staticExistentArtifactCount;
            for (int i = 0; i < metadata.DynamicOutputs.Count; i++)
            {
                var directoryContentsCount = metadata.DynamicOutputs[i].RelativePathFileMaterializationInfos.Count;

                dynamicDirectoryContents[i] = new ArrayView<(FileArtifact, FileMaterializationInfo)>(
                    cachedArtifactContentHashesArray,
                    lastDynamicArtifactIndex,
                    directoryContentsCount);

                lastDynamicArtifactIndex += directoryContentsCount;
            }

            var createdDirectories = metadata.CreatedDirectories.Select(directory => AbsolutePath.Create(pathTable, directory)).ToReadOnlySet();

            return new RunnableFromCacheResult.CacheHitData(
                    pathSetHash: pathSetHash,
                    strongFingerprint: strongFingerprint,
                    metadata: metadata,
                    cachedArtifactContentHashes: cachedArtifactContentHashesArray,
                    absentArtifacts: (IReadOnlyList<FileArtifact>)absentArtifacts ?? CollectionUtilities.EmptyArray<FileArtifact>(),
                    createdDirectories: createdDirectories,
                    standardError: standardError,
                    standardOutput: standardOutput,
                    dynamicDirectoryContents: dynamicDirectoryContents,
                    locality: refLocality,
                    metadataHash: metadataHash);
        }

        private static bool TryParseOptionalStandardConsoleStreamHash(
            PathTable pathTable,
            PathExpander semanticPathExpander,
            EncodedStringKeyedHash standardConsoleStream,
            out Tuple<AbsolutePath, ContentHash, string> resolvedStandardConsoleStream)
        {
            if (standardConsoleStream == null)
            {
                resolvedStandardConsoleStream = null;
                return true;
            }

            AbsolutePath path;
            if (!semanticPathExpander.TryCreatePath(pathTable, standardConsoleStream.StringKeyedHash.Key, out path))
            {
                resolvedStandardConsoleStream = null;
                return false;
            }

            resolvedStandardConsoleStream = Tuple.Create(path, standardConsoleStream.StringKeyedHash.ContentHash.ToContentHash(), standardConsoleStream.EncodingName);
            return true;
        }

        /// <summary>
        /// Ensures an error event is logged for the ContractType-Assume when a CancellationToken is not requested.
        /// </summary>
        public static void AssumeErrorWasLoggedWhenNotCancelled(IPipExecutionEnvironment pipExecutionEnvironment, OperationContext operationContext, string errorMessage = "Error event should have been logged")
        {
            if (!pipExecutionEnvironment.Context.CancellationToken.IsCancellationRequested)
            {
                Contract.Assume(operationContext.LoggingContext.ErrorWasLogged, errorMessage);
            }
        }

        /// <summary>
        /// Ensure error event is logged for ContractType-Assert when a CancellationToken is not requested.
        /// </summary>
        public static void AssertErrorWasLoggedWhenNotCancelled(IPipExecutionEnvironment pipExecutionEnvironment, OperationContext operationContext, string errorMessage = "Error event should have been logged")
        {
            if (!pipExecutionEnvironment.Context.CancellationToken.IsCancellationRequested)
            {
                 Contract.Assert(operationContext.LoggingContext.ErrorWasLogged, errorMessage);
            }
        }

        private readonly struct TwoPhasePathSetValidationTarget : IObservedInputProcessingTarget<ObservedPathEntry>
        {
            private readonly string m_pipDescription;
            private readonly OperationContext m_operationContext;
            private readonly PathTable m_pathTable;
            private readonly IPipExecutionEnvironment m_environment;

            public TwoPhasePathSetValidationTarget(IPipExecutionEnvironment environment, OperationContext operationContext, string pipDescription, PathTable pathTable)
            {
                m_environment = environment;
                m_pipDescription = pipDescription;
                m_operationContext = operationContext;
                m_pathTable = pathTable;
            }

            public string Description => m_pipDescription;

            public AbsolutePath GetPathOfObservation(ObservedPathEntry assertion) => assertion.Path;

            public ObservationFlags GetObservationFlags(ObservedPathEntry assertion)
            {
                return (assertion.IsFileProbe ? ObservationFlags.FileProbe : ObservationFlags.None) |
                    (assertion.IsDirectoryPath ? ObservationFlags.DirectoryLocation : ObservationFlags.None) |
                    // If there are enumerations on the Path then it is an enumeration.
                    (assertion.DirectoryEnumeration ? ObservationFlags.Enumeration : ObservationFlags.None);
            }

            public ObservedInputAccessCheckFailureAction OnAccessCheckFailure(ObservedPathEntry assertion, bool fromTopLevelDirectory)
            {
                // The path can't be accessed. Note that we don't apply a allowlist here (that only applies to process execution).
                // We let this cause overall failure (i.e., a failed ObservedInputProcessingResult, and an undefined StrongContentFingerprint).
                if (!ETWLogger.Log.IsEnabled(EventLevel.Verbose, Keywords.Diagnostics))
                {
                    Logger.Log.PathSetValidationTargetFailedAccessCheck(m_operationContext, m_pipDescription, assertion.Path.ToString(m_pathTable));
                }

                return ObservedInputAccessCheckFailureAction.Fail;
            }

            public void CheckProposedObservedInput(ObservedPathEntry assertion, ObservedInput proposedObservedInput)
            {
            }

            public bool IsSearchPathEnumeration(ObservedPathEntry directoryEnumeration) => directoryEnumeration.IsSearchPath;

            public string GetEnumeratePatternRegex(ObservedPathEntry directoryEnumeration) => directoryEnumeration.EnumeratePatternRegex;

            public void ReportUnexpectedAccess(ObservedPathEntry assertion, ObservedInputType observedInputType)
            {
            }

            public bool IsReportableUnexpectedAccess(AbsolutePath path) => false;

            public ObservedInputAccessCheckFailureAction OnAllowingUndeclaredAccessCheck(ObservedPathEntry observation) => ObservedInputAccessCheckFailureAction.Fail;
        }

        private readonly struct ObservedFileAccessValidationTarget : IObservedInputProcessingTarget<ObservedFileAccess>
        {
            private readonly IPipExecutionEnvironment m_environment;
            private readonly OperationContext m_operationContext;
            private readonly FileAccessReportingContext m_fileAccessReportingContext;
            private readonly string m_processDescription;
            private readonly PipExecutionState.PipScopeState m_state;

            public string Description => m_processDescription;

            public ObservedFileAccessValidationTarget(
                OperationContext operationContext,
                IPipExecutionEnvironment environment,
                FileAccessReportingContext fileAccessReportingContext,
                PipExecutionState.PipScopeState state,
                string processDescription)
            {
                m_operationContext = operationContext;
                m_environment = environment;
                m_processDescription = processDescription;
                m_fileAccessReportingContext = fileAccessReportingContext;
                m_state = state;
            }

            public ObservationFlags GetObservationFlags(ObservedFileAccess observation) => observation.ObservationFlags;

            public AbsolutePath GetPathOfObservation(ObservedFileAccess observation) => observation.Path;

            public void CheckProposedObservedInput(ObservedFileAccess observation, ObservedInput proposedObservedInput)
            {
            }

            public void ReportUnexpectedAccess(ObservedFileAccess observation, ObservedInputType observedInputType)
            {
            }

            public bool IsReportableUnexpectedAccess(AbsolutePath path) => false;

            public ObservedInputAccessCheckFailureAction OnAccessCheckFailure(ObservedFileAccess observation, bool fromTopLevelDirectory)
            {
                // TODO: Should be able to log provenance of the sealed directory here (we don't even know which directory artifact corresponds).
                //       This is a fine argument to move this function into the execution environment.
                if (m_fileAccessReportingContext.MatchAndReportUnexpectedObservedFileAccess(observation) != FileAccessAllowlist.MatchType.NoMatch)
                {
                    return ObservedInputAccessCheckFailureAction.SuppressAndIgnorePath;
                }

                // If the access was allowlisted, some allowlist-related events will have been reported.
                // Otherwise, error or warning level events for the unexpected accesses will have been reported; in
                // that case we will additionally log a final message specific to this being a sealed directory
                // related issue (see/* TODO:above about logging provenance of a containing seal).
                if (fromTopLevelDirectory)
                {
                    Logger.Log.DisallowedFileAccessInTopOnlySourceSealedDirectory(m_operationContext, m_processDescription, observation.Path.ToString(m_environment.Context.PathTable));
                }
                else
                {
                    Logger.Log.ScheduleDisallowedFileAccessInSealedDirectory(m_operationContext, m_processDescription, observation.Path.ToString(m_environment.Context.PathTable));
                }

                return ObservedInputAccessCheckFailureAction.Fail;
            }

            public bool IsSearchPathEnumeration(ObservedFileAccess directoryEnumeration)
            {
                // A directory enumeration is a search path enumeration if at least one of the accessing tools are marked
                // as search path enumeration tools in the directory membership fingerprinter rule set
                string lastToolPath = null;
                foreach (var access in directoryEnumeration.Accesses)
                {
                    if (access.Process.Path == lastToolPath)
                    {
                        // Skip if we already checked this path
                        continue;
                    }

                    if (access.RequestedAccess != RequestedAccess.Enumerate)
                    {
                        continue;
                    }

                    lastToolPath = access.Process.Path;
                    if (m_state.DirectoryMembershipFingerprinterRuleSet?.IsSearchPathEnumerationTool(access.Process.Path) == true)
                    {
                        return true;
                    }
                }

                return false;
            }

            public string GetEnumeratePatternRegex(ObservedFileAccess directoryEnumeration)
            {
                using (var setPool = Pools.GetStringSet())
                {
                    var set = setPool.Instance;
                    foreach (var access in directoryEnumeration.Accesses)
                    {
                        if (access.RequestedAccess != RequestedAccess.Enumerate)
                        {
                            continue;
                        }

                        if (m_state.DirectoryMembershipFingerprinterRuleSet?.IsSearchPathEnumerationTool(access.Process.Path) == true)
                        {
                            continue;
                        }

                        if (access.EnumeratePattern == null)
                        {
                            Contract.Assert(false, "Enumerate pattern cannot be null: " + directoryEnumeration.Path.ToString(m_environment.Context.PathTable) + Environment.NewLine
                                + string.Join(Environment.NewLine, directoryEnumeration.Accesses.Select(a => a.Describe())));
                        }

                        set.Add(access.EnumeratePattern);
                    }

                    return RegexDirectoryMembershipFilter.ConvertWildcardsToRegex(set.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToArray());
                }
            }

            public ObservedInputAccessCheckFailureAction OnAllowingUndeclaredAccessCheck(ObservedFileAccess observation)
            {
                (FileAccessAllowlist.MatchType aggregateMatchType, (ReportedFileAccess, FileAccessAllowlist.MatchType)[] reportedMatchTypes) = m_fileAccessReportingContext.MatchObservedFileAccess(observation);
                if (aggregateMatchType != FileAccessAllowlist.MatchType.NoMatch)
                {
                    // Report cacheable/uncachable so that pip executor can decide if the pip is cacheable or uncacheable.
                    foreach ((ReportedFileAccess fa, FileAccessAllowlist.MatchType matchType) in reportedMatchTypes.Where(t => t.Item2 != FileAccessAllowlist.MatchType.NoMatch))
                    {
                        m_fileAccessReportingContext.AddAndReportUncacheableFileAccess(fa, matchType);
                    }

                    return ObservedInputAccessCheckFailureAction.SuppressAndIgnorePath;
                }

                return ObservedInputAccessCheckFailureAction.Fail;
            }
        }

        /// <summary>
        /// Processes an <see cref="ObservedPathSet"/> consisting of prior observed accesses (as if the process were to run now, and
        /// access these paths). If all accesses are allowable for the pip (this may not be the case due to pip graph changes since the prior execution),
        /// this function returns a <see cref="StrongContentFingerprint"/>. That returned fingerprint extends the provided <paramref name="weakFingerprint"/>
        /// to account for those additional inputs; a prior execution for the same strong fingerprint is safely re-usable.
        /// Note that if the returned processing status is <see cref="ObservedInputProcessingStatus.Aborted"/>, then a failure has been logged and pip
        /// execution must fail.
        /// </summary>
        private static async Task<(ObservedInputProcessingResult, StrongContentFingerprint)> TryComputeStrongFingerprintBasedOnPriorObservedPathSetAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state,
            CacheablePip pip,
            WeakContentFingerprint weakFingerprint,
            ObservedPathSet pathSet,
            ContentHash pathSetHash)
        {
            Contract.Requires(environment != null);
            Contract.Requires(pip != null);
            Contract.Requires(pathSet.Paths.IsValid);

            using (operationContext.StartOperation(PipExecutorCounter.PriorPathSetEvaluationToProduceStrongFingerprintDuration))
            {
                ObservedInputProcessingResult validationResult =
                    await ObservedInputProcessor.ProcessPriorPathSetAsync(
                        operationContext,
                        environment,
                        state,
                        new TwoPhasePathSetValidationTarget(environment, operationContext, pip.Description, environment.Context.PathTable),
                        pip,
                        pathSet);

                environment.Counters.IncrementCounter(PipExecutorCounter.PriorPathSetsEvaluatedToProduceStrongFingerprint);

                // force cache miss if observed input processing result is not 'Success'
                if (validationResult.Status != ObservedInputProcessingStatus.Success)
                {
                    return (validationResult, default(StrongContentFingerprint));
                }

                // log and compute strong fingerprint using the PathSet hash from the cache
                LogInputAssertions(
                    operationContext,
                    environment.Context,
                    pip,
                    validationResult);

                StrongContentFingerprint strongFingerprint;
                using (operationContext.StartOperation(PipExecutorCounter.ComputeStrongFingerprintDuration))
                {
                    strongFingerprint = validationResult.ComputeStrongFingerprint(
                        environment.Context.PathTable,
                        weakFingerprint,
                        pathSetHash);
                }

                return (validationResult, strongFingerprint);
            }
        }

        /// <summary>
        /// Validates that all observed file accesses of a pip's execution are allowable, based on the pip's declared dependencies and configuration.
        /// Note that **<paramref name="observedFileAccesses"/> may be sorted in place**.
        /// </summary>
        private static async Task<ObservedInputProcessingResult> ValidateObservedFileAccessesAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state,
            CacheablePip pip,
            FileAccessReportingContext fileAccessReportingContext,
            SortedReadOnlyArray<ObservedFileAccess, ObservedFileAccessExpandedPathComparer> observedFileAccesses,
            [AllowNull] IReadOnlyDictionary<AbsolutePath, IReadOnlyCollection<FileArtifactWithAttributes>> sharedDynamicDirectoryWriteAccesses,
            bool trackFileChanges = true)
        {
            Contract.Requires(environment != null);
            Contract.Requires(pip != null);
            Contract.Requires(fileAccessReportingContext != null);

            var target = new ObservedFileAccessValidationTarget(
                operationContext,
                environment,
                fileAccessReportingContext,
                state,
                pip.Description);

            var result = await ObservedInputProcessor.ProcessNewObservationsAsync(
                operationContext,
                environment,
                state,
                target,
                pip,
                observedFileAccesses,
                sharedDynamicDirectoryWriteAccesses,
                trackFileChanges);

            LogInputAssertions(
                operationContext,
                environment.Context,
                pip,
                result);

            return result;
        }

        /// <summary>
        /// We have optional tracing for all discovered directory / file / absent-path dependencies.
        /// This function dumps out the result of an <see cref="ObservedInputProcessor"/> if needed.
        /// Note that this is generic to cache-hit vs. cache-miss as well as single-phase vs. two-phase lookup.
        /// </summary>
        private static void LogInputAssertions(
            OperationContext operationContext,
            PipExecutionContext context,
            CacheablePip pip,
            ObservedInputProcessingResult processedInputs)
        {
            // ObservedInputs are only available on processing success.
            if (processedInputs.Status != ObservedInputProcessingStatus.Success)
            {
                return;
            }

            // Tracing input assertions is expensive (many events and many string expansions); we avoid tracing when nobody is listening.
            if (!BuildXL.Scheduler.ETWLogger.Log.IsEnabled(EventLevel.Verbose, Keywords.Diagnostics))
            {
                return;
            }

            foreach (ObservedInput input in processedInputs.ObservedInputs)
            {
                if (input.Type == ObservedInputType.DirectoryEnumeration)
                {
                    Logger.Log.PipDirectoryMembershipAssertion(
                        operationContext,
                        pip.Description,
                        input.Path.ToString(context.PathTable),
                        input.Hash.ToHex());
                }
                else
                {
                    Logger.Log.TracePipInputAssertion(
                        operationContext,
                        pip.Description,
                        input.Path.ToString(context.PathTable),
                        input.Hash.ToHex());
                }
            }
        }

        /// <summary>
        /// Attempt to bring multiple file contents into the local cache.
        /// </summary>
        /// <remarks>
        /// May log warnings (but not errors) on failure.
        /// </remarks>
        /// <param name="operationContext">Logging context associated with the pip</param>
        /// <param name="environment">Execution environment</param>
        /// <param name="pip">Pip that requested these contents (for logging)</param>
        /// <param name="cachedArtifactContentHashes">Enumeration of content to copy locally.</param>
        /// <param name="strongFingerprint">the associated strong fingerprint</param>
        /// <param name="metadataHash">the hash of the metadata which entry which references the content</param>
        /// <param name="standardOutput">Standard output</param>
        /// <param name="standardError">Standard error</param>
        /// <param name="onContentUnavailable">Handler for content unavailable case</param>
        /// <returns>True if all succeeded, otherwise false.</returns>
        private static async Task<bool> TryLoadAvailableOutputContentAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            CacheablePip pip,
            IReadOnlyCollection<(FileArtifact fileArtifact, FileMaterializationInfo fileMaterializationInfo)> cachedArtifactContentHashes,
            StrongContentFingerprint strongFingerprint,
            ContentHash metadataHash,
            Tuple<AbsolutePath, ContentHash, string> standardOutput = null,
            Tuple<AbsolutePath, ContentHash, string> standardError = null,
            Action<FileArtifact, ContentHash> onContentUnavailable = null)
        {
            Contract.Requires(environment != null);
            Contract.Requires(pip != null);
            Contract.Requires(cachedArtifactContentHashes != null);

            var allHashes =
                new List<(FileArtifact fileArtifact, ContentHash contentHash, AbsolutePath outputDirectoryRoot, bool isExecutable)>(
                    cachedArtifactContentHashes.Count + (standardError == null ? 0 : 1) + (standardOutput == null ? 0 : 1));

            // only check/load "real" files - reparse points are not stored in CAS, they are stored in metadata that we have already obtained
            // if we try to load reparse points' content from CAS, content availability check would fail, and as a result,
            // BuildXL would have to re-run the pip (even if all other outputs are available)
            // Also, do not load zero-hash files (there is nothing in CAS with this hash)
            allHashes.AddRange(cachedArtifactContentHashes
                .Where(pair => pair.fileMaterializationInfo.IsCacheable)
                .Select(a => (a.fileArtifact, a.fileMaterializationInfo.Hash, a.fileMaterializationInfo.OpaqueDirectoryRoot, a.fileMaterializationInfo.IsExecutable)));

            if (standardOutput != null)
            {
                allHashes.Add((FileArtifact.CreateOutputFile(standardOutput.Item1), standardOutput.Item2, AbsolutePath.Invalid, false));
            }

            if (standardError != null)
            {
                allHashes.Add((FileArtifact.CreateOutputFile(standardError.Item1), standardError.Item2, AbsolutePath.Invalid, false));
            }

            // Check whether the cache provides a strong guarantee that content will be available for a successful pin
            bool hasStrongOutputContentAvailabilityGuarantee = environment.State.Cache.HasStrongOutputContentAvailabilityGuarantee(metadataHash);

            // When VerifyCacheLookupPin is specified (and cache provides no strong guarantee), we need to materialize as well to ensure the content is actually available
            bool materializeToVerifyAvailability = environment.Configuration.Schedule.VerifyCacheLookupPin && !hasStrongOutputContentAvailabilityGuarantee;

            // If pin cached outputs is off and verify cache lookup pin is not enabled/triggered for the current pip,
            // then just return true.
            if (!environment.Configuration.Schedule.PinCachedOutputs && !materializeToVerifyAvailability)
            {
                return true;
            }

            using (operationContext.StartOperation(materializeToVerifyAvailability ?
                PipExecutorCounter.TryLoadAvailableOutputContent_VerifyCacheLookupPinDuration :
                PipExecutorCounter.TryLoadAvailableOutputContent_PinDuration))
            {
                var succeeded = await environment.State.FileContentManager.TryLoadAvailableOutputContentAsync(
                    pip,
                    operationContext,
                    allHashes,
                    materialize: materializeToVerifyAvailability,
                    onContentUnavailable: (index, expectedHash, hashOnDiskIfAvailableOrNull, failure) =>
                    {
                        onContentUnavailable?.Invoke(allHashes[index].fileArtifact, allHashes[index].contentHash);
                    });

                if (materializeToVerifyAvailability || !succeeded)
                {
                    environment.State.Cache.RegisterOutputContentMaterializationResult(strongFingerprint, metadataHash, succeeded);
                }

                return succeeded;
            }
        }

        private struct ExtractedPathEntry
        {
            public ObservedPathEntry Entry;
            public int UseCount;
        }

        [Flags]
        private enum OutputFlags
        {
            /// <summary>
            /// Declared output file. <see cref="Process.FileOutputs"/>
            /// </summary>
            DeclaredFile = 1,

            /// <summary>
            /// Dynamic output file under process output directory. <see cref="Process.DirectoryOutputs"/>
            /// </summary>
            DynamicFile = 1 << 1,

            /// <summary>
            /// Standard output file
            /// </summary>
            StandardOut = 1 << 2,

            /// <summary>
            /// Standard error file
            /// </summary>
            StandardError = 1 << 3,
        }

        /// <summary>
        /// The file artifact with attributes and output flags
        /// </summary>
        private struct FileOutputData
        {
            /// <summary>
            /// The output file artifact.
            /// </summary>
            internal int OpaqueDirectoryIndex;

            /// <summary>
            /// The flags associated with the output file
            /// </summary>
            internal OutputFlags Flags;

            /// <summary>
            /// Gets whether all the given flags are applicable to the output file
            /// </summary>
            internal bool HasAllFlags(OutputFlags flags)
            {
                return (Flags & flags) == flags;
            }

            /// <summary>
            /// Gets whether any of the given flags are applicable to the output file
            /// </summary>
            internal bool HasAnyFlag(OutputFlags flags)
            {
                return (Flags & flags) != 0;
            }

            /// <summary>
            /// Updates the file data for the path in the map
            /// </summary>
            /// <param name="map">map of path to file data</param>
            /// <param name="path">the path to the output file</param>
            /// <param name="flags">flags to add (if any)</param>
            /// <param name="index">the opaque directory index (if applicable i.e. <paramref name="flags"/> is <see cref="OutputFlags.DynamicFile"/>)</param>
            internal static void UpdateFileData(Dictionary<AbsolutePath, FileOutputData> map, AbsolutePath path, OutputFlags? flags = null, int? index = null)
            {
                Contract.Assert(flags != OutputFlags.DynamicFile || index != null, "Opaque index must be specified for dynamic output files");

                FileOutputData fileData;
                if (!map.TryGetValue(path, out fileData))
                {
                    fileData = default(FileOutputData);
                }

                if (flags != null)
                {
                    fileData.Flags |= flags.Value;
                }

                if (index != null)
                {
                    fileData.OpaqueDirectoryIndex = index.Value;
                }

                map[path] = fileData;
            }
        }

        private static bool CheckForAllowedReparsePointProduction(AbsolutePath outputPath, OperationContext operationContext, string description, PathTable pathTable, ExecutionResult processExecutionResult, IConfiguration configuration)
        {
            if (OperatingSystemHelper.IsUnixOS)
            {
                return true;
            }

            if (configuration.Sandbox.UnsafeSandboxConfiguration.IgnoreFullReparsePointResolving)
            {
                if (configuration.Sandbox.DirectoriesToEnableFullReparsePointParsing.Any(x => outputPath.IsWithin(pathTable, x)))
                {
                    return true;
                }

                var pathstring = outputPath.ToString(pathTable);
                var possibleReparsePointType = FileUtilities.TryGetReparsePointType(pathstring);
                if (possibleReparsePointType.Succeeded && possibleReparsePointType.Result == ReparsePointType.DirectorySymlink)
                {
                    // We don't support storing directory symlinks to the cache on Windows unless full reparse point resolving is enabled.
                    // 1. We won't fail the pip!
                    // 2. We won't cache it either!
                    Logger.Log.StorageReparsePointInOutputDirectoryWarning(operationContext, description, pathstring);
                    processExecutionResult.MustBeConsideredPerpetuallyDirty = true;
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns true if a pip must be considered perpetually dirty irrespective of everything else.
        /// </summary>
        /// <remarks>
        /// If a pip has shared opaque directory outputs it is always considered dirty since
        /// it is not clear how to infer what to run based on the state of the file system.
        /// If a pip has exclusive opaques with existence assertions, it is considered dirty as well
        /// since validating the assertions in a top-down scheduling is not straightforward (even though it could
        /// be achieved in the future)
        /// </remarks>
        public static bool IsUnconditionallyPerpetuallyDirty(Pip pip, IPipGraphFileSystemView pipGraphView)
            => pip.PipType == PipType.Process && pip is Process process &&
               (process.HasSharedOpaqueDirectoryOutputs ||
                process.DirectoryOutputs.Any(directory => pipGraphView.GetExistenceAssertionsUnderOpaqueDirectory(directory).Count > 0));

        /// <summary>
        /// Discovers the content hashes of a process pip's outputs, which must now be on disk.
        /// The pip's outputs will be stored into the <see cref="IArtifactContentCache"/> of <see cref="IPipExecutionEnvironment.Cache"/>,
        /// and (if caching is enabled) a cache entry (the types varies; either single-phase or two-phase depending on configuration) will be created.
        /// The cache entry itself is not immediately stored, and is instead placed on the <paramref name="processExecutionResult"/>. This is so that
        /// in distributed builds, workers can handle output processing and validation but defer all metadata storage to the orchestrator.
        /// </summary>
        /// <remarks>
        /// This may be called even if the execution environment lacks a cache, in which case the outputs are hashed and reported (but nothing else).
        /// </remarks>
        /// <param name="operationContext">Current logging context</param>
        /// <param name="environment">Execution environment</param>
        /// <param name="state">Pip execution state</param>
        /// <param name="process">The process which has finished executing</param>
        /// <param name="description">Description of <paramref name="process"/></param>
        /// <param name="observedInputs">Observed inputs which should be part of the cache value</param>
        /// <param name="encodedStandardOutput">The optional standard output file</param>
        /// <param name="encodedStandardError">The optional standard error file</param>
        /// <param name="numberOfWarnings">Number of warnings found in standard output and error</param>
        /// <param name="processExecutionResult">The process execution result for recording process execution information</param>
        /// <param name="enableCaching">If set, the pip's descriptor and content will be stored to the cache. Otherwise, its outputs will be hashed but not stored or referenced by a new descriptor.</param>
        /// <param name="fingerprintComputation">Stores fingerprint computation information</param>
        private static async Task<bool> StoreContentForProcessAndCreateCacheEntryAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state,
            Process process,
            string description,
            ObservedInputProcessingResult? observedInputs,
            Tuple<AbsolutePath, Encoding> encodedStandardOutput,
            Tuple<AbsolutePath, Encoding> encodedStandardError,
            int numberOfWarnings,
            ExecutionResult processExecutionResult,
            bool enableCaching,
            BoxRef<ProcessFingerprintComputationEventData> fingerprintComputation)
        {
            Contract.Requires(environment != null);
            Contract.Requires(process != null);
            Contract.Requires(
                enableCaching == observedInputs.HasValue,
                "Should provide observed inputs (relevant only when caching) iff caching is enabled.");
            Contract.Requires(encodedStandardOutput == null || (encodedStandardOutput.Item1.IsValid && encodedStandardOutput.Item2 != null));
            Contract.Requires(encodedStandardError == null || (encodedStandardError.Item1.IsValid && encodedStandardError.Item2 != null));
            Contract.Requires(!processExecutionResult.IsSealed);

            var pathTable = environment.Context.PathTable;

            long totalOutputSize = 0;
            int numDynamicOutputs = 0;
            ContentHash? standardOutputContentHash = null;
            ContentHash? standardErrorContentHash = null;

            using (var poolFileArtifactWithAttributesList = Pools.GetFileArtifactWithAttributesList())
            using (var poolAbsolutePathFileOutputDataMap = s_absolutePathFileOutputDataMapPool.GetInstance())
            using (var poolAbsolutePathFileArtifactWithAttributes = Pools.GetAbsolutePathFileArtifactWithAttributesMap())
            using (var poolFileList = Pools.GetFileArtifactWithAttributesList())
            using (var poolAbsolutePathFileMaterializationInfoTuppleList = s_absolutePathFileMaterializationInfoTuppleListPool.GetInstance())
            using (var poolFileArtifactStoreToCacheSet = s_fileArtifactStoreToCacheSet.GetInstance())
            {
                // Each dynamic output should map to which opaque directory they belong.
                // Because we will store the hashes and paths of the dynamic outputs by preserving the ordering of process.DirectoryOutputs
                var allOutputs = poolFileArtifactWithAttributesList.Instance;
                var allOutputData = poolAbsolutePathFileOutputDataMap.Instance;

                // Let's compute what got actually redirected
                foreach (var output in process.FileOutputs)
                {
                    FileOutputData.UpdateFileData(allOutputData, output.Path, OutputFlags.DeclaredFile);

                    if (!CheckForAllowedReparsePointProduction(output.Path, operationContext, description, pathTable, processExecutionResult, environment.Configuration))
                    {
                        enableCaching = false;
                        continue;
                    }

                    allOutputs.Add(output);
                }

                using var outputDirectoryDataWrapper = s_outputEnumerationDataPool.GetInstance();
                var outputDirectoryData = outputDirectoryDataWrapper.Instance;
                outputDirectoryData.Process = process;

                // We need to discover dynamic outputs in the given opaque directories.
                var fileList = poolFileList.Instance;

                using (var existenceAssertionsWrapper = Pools.GetFileArtifactSet())
                {
                    HashSet<FileArtifact> existenceAssertions = existenceAssertionsWrapper.Instance;

                    for (int i = 0; i < process.DirectoryOutputs.Length; i++)
                    {
                        fileList.Clear();
                        var directoryArtifact = process.DirectoryOutputs[i];
                        var directoryArtifactPath = directoryArtifact.Path;

                        var index = i;

                        // For the case of an opaque directory, the content is determined by scanning the file system
                        if (!directoryArtifact.IsSharedOpaque)
                        {
                            // Let's validate here the existence assertions for exclusive opaques
                            // Shared opaques were already validated during sandboxed execution
                            // The validation is implemented in difference places for each to avoid unnecessary
                            // re-enumerations
                            Contract.Assert(existenceAssertions.Count == 0);
                            existenceAssertions.AddRange(environment.PipGraphView.GetExistenceAssertionsUnderOpaqueDirectory(directoryArtifact));

                            var enumerationResult = environment.State.FileContentManager.EnumerateAndTrackOutputDirectory(
                                directoryArtifact,
                                outputDirectoryData,
                                handleFile: fileArtifact =>
                                {
                                    if (!CheckForAllowedReparsePointProduction(fileArtifact.Path, operationContext, description, pathTable, processExecutionResult, environment.Configuration))
                                    {
                                        enableCaching = false;
                                        return;
                                    }

                                    // Files under an exclusive opaques are always considered required outputs
                                    fileList.Add(FileArtifactWithAttributes.Create(fileArtifact, FileExistence.Required));
                                    FileOutputData.UpdateFileData(allOutputData, fileArtifact.Path, OutputFlags.DynamicFile, index);
                                    var fileArtifactWithAttributes = fileArtifact.WithAttributes(FileExistence.Required);
                                    allOutputs.Add(fileArtifactWithAttributes);
                                    existenceAssertions.Remove(fileArtifact);
                                },

                                // TODO: Currently the logic skips empty subdirectories. The logic needs to preserve the structure of opaque directories.
                                // TODO: THe above issue is tracked by task 872930.
                                handleDirectory: null);

                            if (!enumerationResult.Succeeded)
                            {
                                Logger.Log.ProcessingPipOutputDirectoryFailed(
                                    operationContext,
                                    description,
                                    directoryArtifactPath.ToString(pathTable),
                                    enumerationResult.Failure.DescribeIncludingInnerFailures());
                                return false;
                            }

                            // There are some outputs that were asserted as belonging to the shared opaque that were not found
                            if (existenceAssertions.Count != 0)
                            {
                                Processes.Tracing.Logger.Log.ExistenceAssertionUnderOutputDirectoryFailed(
                                            operationContext,
                                            description,
                                            existenceAssertions.First().Path.ToString(pathTable),
                                            directoryArtifact.Path.ToString(pathTable));

                                return false;
                            }
                        }
                        else
                        {
                            // For the case of shared opaque directories, the content is based on detours
                            var writeAccessesByPath = processExecutionResult.SharedDynamicDirectoryWriteAccesses;
                            if (!writeAccessesByPath.TryGetValue(directoryArtifactPath, out var accesses))
                            {
                                accesses = CollectionUtilities.EmptyArray<FileArtifactWithAttributes>();
                            }

                            foreach (var access in accesses)
                            {
                                if (!CheckForAllowedReparsePointProduction(access.Path, operationContext, description, pathTable, processExecutionResult, environment.Configuration))
                                {
                                    enableCaching = false;
                                }
                                else
                                {
                                    fileList.Add(access);
                                    // Let's report the shared opaque directory content to the file content manager before they get cached. This is so the output file system gets populated
                                    // and therefore we avoid races when using DirectoryEnumerationMode.MinimalGraphWithAlienFiles mode in ObservedInputProcessor. By caching outputs we may be changing
                                    // the creation date of outputs via cache hardlinking and hindering one of heuristics we use there to identify files that existed before the build started. This heuristic
                                    // assumes the output file system does not believe these are outputs, but there is a window between the file gets produced, it gets cached, and the output file system gets populated.
                                    // By reporting these here, we minimize the chance of a race.
                                    // On the other hand, statically declared outputs need to be reported after pushing to the cache since hashes are reported as part of the same report operation. However
                                    // consider that statically declared outputs do not suffer from the problem described here since they are already part of the pip graph.
                                    if (access.FileExistence == FileExistence.Required)
                                    {
                                        environment.State.FileSystemView.ReportSharedOpaqueOutputProducedBeforeCaching(access.Path);
                                    }

                                    FileOutputData.UpdateFileData(allOutputData, access.Path, OutputFlags.DynamicFile, index);
                                    allOutputs.Add(access);
                                }
                            }
                        }

                        processExecutionResult.ReportDirectoryOutput(directoryArtifact, fileList);

                        numDynamicOutputs += fileList.Count;
                    }
                }

                if (encodedStandardOutput != null)
                {
                    var path = encodedStandardOutput.Item1;
                    FileOutputData.UpdateFileData(allOutputData, path, OutputFlags.StandardOut);
                    allOutputs.Add(FileArtifact.CreateOutputFile(path).WithAttributes(FileExistence.Required));
                }

                if (encodedStandardError != null)
                {
                    var path = encodedStandardError.Item1;
                    FileOutputData.UpdateFileData(allOutputData, path, OutputFlags.StandardError);
                    allOutputs.Add(FileArtifact.CreateOutputFile(path).WithAttributes(FileExistence.Required));
                }

                var outputHashPairs = poolAbsolutePathFileMaterializationInfoTuppleList.Instance;
                var fileArtifactStoreToCacheSet = poolFileArtifactStoreToCacheSet.Instance;

                bool successfullyProcessedOutputs = true;

                // Files with the same filename have a higher chance to have the same content.
                // When pushing same-content outputs to the cache concurrently, the cache may need extra work
                // to detect the same content is being pushed and shortcut the push operation. Therefore,
                // we assign to each file a weight based on how many other files with the same filename are being pushed
                // and order by that. Therefore, we have higher chances to make pushes with the same content be farther away,
                // and in that way mitigate concurrent same-content pushes.
                var sortedOutputs = allOutputs
                    .GroupBy(output => output.Path.GetName(pathTable))
                    .SelectMany(group => group.Select((file, index) => (file, index)))
                    .OrderBy(fileWithCount => fileWithCount.index)
                    .Select(fileWithCount => fileWithCount.file);

                using (var materializationResultsPool = s_materializationResultsPool.GetInstance(allOutputs.Count))
                {
                    var materializationResults = materializationResultsPool.Instance;

                    var storeOutputsQueue = ActionBlockSlim.CreateWithAsyncAction<(int, FileArtifactWithAttributes, FileOutputData)>(
                        degreeOfParallelism: EngineEnvironmentSettings.StoringOutputsToCacheConcurrency,
                        processItemAction: async ((int index, FileArtifactWithAttributes artifact, FileOutputData data) dataToStore) =>
                        {
                            using (operationContext.StartAsyncOperation(PipExecutorCounter.SerializeAndStorePipOutputDuration))
                            {
                                var result = await StoreCacheableProcessOutputAsync(
                                                        environment,
                                                        operationContext,
                                                        process,
                                                        dataToStore.artifact,
                                                        dataToStore.data,
                                                        isProcessCacheable: true);

                                // Observe it is fine to store this in a lock-free manner since we make sure same indexes are not updated concurrently
                                materializationResults[dataToStore.index] = result;
                            }
                        },
                        singleProducedConstrained: true,
                        cancellationToken: environment.Context.CancellationToken);

                    int outputIndex = 0;

                    foreach (var output in sortedOutputs)
                    {
                        var outputData = allOutputData[output.Path];

                        // For all cacheable outputs, start a task to store into the cache
                        if (output.CanBeReferencedOrCached())
                        {
                            // Deduplicate output store operations so outputs are not stored to the cache concurrently.
                            // (namely declared file outputs can also be under a dynamic directory as a dynamic output)
                            // Observe that indexes in materializationResults representing duplicated outputs will be left unassigned (and therefore null)
                            if (fileArtifactStoreToCacheSet.Add(output.ToFileArtifact()))
                            {
                                var contentToStore = output;
                                storeOutputsQueue.Post((outputIndex, contentToStore, outputData));
                            }
                        }
                        else if (outputData.HasAllFlags(OutputFlags.DynamicFile))
                        {
                            // Do not attempt to store dynamic temporary files into cache. However, we store them as a part of metadata as files with AbsentFile hash,
                            // so accesses could be properly reported to FileMonitoringViolationAnalyzer on cache replay.
                            materializationResults[outputIndex] = Possible.Create(FileMaterializationInfo.CreateWithUnknownLength(WellKnownContentHashes.AbsentFile));
                        }

                        outputIndex++;
                    }

                    // We are done posting items to the queue and we can wait for the overall completion
                    // Consider that output registration could start processing items as they are ready, but
                    // the loop below is not computational intensive and therefore doesn't seem justified
                    storeOutputsQueue.Complete();

                    try
                    {
                        await storeOutputsQueue.Completion;
                    }
                    catch (TaskCanceledException)
                    {
                        return false;
                    }

                    if (environment.Context.CancellationToken.IsCancellationRequested)
                    {
                        return false;
                    }

                    // We cannot enumerate over storeProcessOutputCompletionsByPath here
                    // because the order of such an enumeration is not deterministic.
                    outputIndex = 0;
                    foreach (var output in sortedOutputs)
                    {
                        Possible<FileMaterializationInfo>? maybePossiblyStoredOutputArtifactInfo = materializationResults[outputIndex];

                        // A null value here means this output is a duplicate. Just skip it.
                        if (maybePossiblyStoredOutputArtifactInfo == null)
                        {
                            outputIndex++;
                            continue;
                        }

                        var possiblyStoredOutputArtifactInfo = maybePossiblyStoredOutputArtifactInfo.Value;

                        FileArtifact outputArtifact = output.ToFileArtifact();

                        var outputData = allOutputData[outputArtifact.Path];

                        if (possiblyStoredOutputArtifactInfo.Succeeded)
                        {
                            FileMaterializationInfo outputArtifactInfo = possiblyStoredOutputArtifactInfo.Result;
                            outputHashPairs.Add((outputArtifact.Path, outputArtifactInfo));

                            // Sometimes standard error / standard out is a declared output. Other times it is an implicit output that we shouldn't report.
                            // If it is a declared output, we notice that here and avoid trying to look at the file again below.
                            // Generally we want to avoid looking at a file repeatedly to avoid seeing it in multiple states (perhaps even deleted).
                            // TODO: Would be cleaner to always model console streams as outputs, but 'maybe present' (a generally useful status for outputs).
                            if (outputData.HasAllFlags(OutputFlags.StandardOut))
                            {
                                standardOutputContentHash = outputArtifactInfo.Hash;
                            }

                            if (outputData.HasAllFlags(OutputFlags.StandardError))
                            {
                                standardErrorContentHash = outputArtifactInfo.Hash;
                            }

                            PipOutputOrigin origin;
                            if (outputArtifactInfo.FileContentInfo.HasKnownLength)
                            {
                                totalOutputSize += outputArtifactInfo.Length;
                                origin = PipOutputOrigin.Produced;
                            }
                            else
                            {
                                // Absent file
                                origin = PipOutputOrigin.UpToDate;
                            }

                            if (!outputData.HasAnyFlag(OutputFlags.DeclaredFile | OutputFlags.DynamicFile))
                            {
                                // Only report output content if file is a 'real' pip output
                                // i.e. declared or dynamic output (not just standard out/error)
                                outputIndex++;
                                continue;
                            }

                            processExecutionResult.ReportOutputContent(
                                outputArtifact,
                                outputArtifactInfo,
                                origin);
                        }
                        else
                        {
                            if (!(possiblyStoredOutputArtifactInfo.Failure is CancellationFailure))
                            {
                                // Storing output to cache failed. Log failure.
                                Logger.Log.ProcessingPipOutputFileFailed(
                                    operationContext,
                                    description,
                                    outputArtifact.Path.ToString(pathTable),
                                    possiblyStoredOutputArtifactInfo.Failure.DescribeIncludingInnerFailures());
                            }

                            successfullyProcessedOutputs = false;
                        }

                        outputIndex++;
                    }
                }

                // Short circuit before updating cache to avoid potentially creating an incorrect cache descriptor since there may
                // be some missing output file hashes
                if (!successfullyProcessedOutputs)
                {
                    AssumeErrorWasLoggedWhenNotCancelled(environment, operationContext);
                    return false;
                }

                Contract.Assert(encodedStandardOutput == null || standardOutputContentHash.HasValue, "Hashed as a declared output, or independently");
                Contract.Assert(encodedStandardError == null || standardErrorContentHash.HasValue, "Hashed as a declared output, or independently");

                if (enableCaching)
                {
                    Contract.Assert(observedInputs.HasValue);

                    PipCacheDescriptorV2Metadata metadata =
                        new PipCacheDescriptorV2Metadata
                        {
                            Id = PipFingerprintEntry.CreateUniqueId(),
                            NumberOfWarnings = numberOfWarnings,
                            StandardError = GetOptionalEncodedStringKeyedHash(environment, state, encodedStandardError, standardErrorContentHash),
                            StandardOutput = GetOptionalEncodedStringKeyedHash(environment, state, encodedStandardOutput, standardOutputContentHash),
                            TraceInfo = operationContext.LoggingContext.Session.Environment,
                            TotalOutputSize = totalOutputSize,
                            SemiStableHash = process.SemiStableHash,
                            WeakFingerprint = fingerprintComputation.Value.WeakFingerprint.ToString(),
                            SessionId = operationContext.LoggingContext.Session.Id,
                            RelatedSessionId = operationContext.LoggingContext.Session.RelatedId,
                        };

                    RecordOutputsOnMetadata(metadata, process, allOutputData, outputHashPairs, pathTable);

                    RecordCreatedDirectoriesOnMetadata(metadata, pathTable, processExecutionResult.CreatedDirectories);

                    // An assertion for the static outputs
                    Contract.Assert(metadata.StaticOutputHashes.Count == process.GetCacheableOutputsCount());

                    // An assertion for the dynamic outputs
                    Contract.Assert(metadata.DynamicOutputs.Sum(a => a.RelativePathFileMaterializationInfos.Count) == numDynamicOutputs);

                    if (environment.Context.CancellationToken.IsCancellationRequested)
                    {
                        return false;
                    }

                    var entryStore = await TryCreateTwoPhaseCacheEntryAndStoreMetadataAsync(
                        operationContext,
                        environment,
                        state,
                        process,
                        description,
                        metadata,
                        outputHashPairs,
                        standardOutputContentHash,
                        standardErrorContentHash,
                        observedInputs.Value,
                        fingerprintComputation);

                    if (entryStore == null)
                    {
                        AssumeErrorWasLoggedWhenNotCancelled(environment, operationContext);
                        return false;
                    }

                    processExecutionResult.TwoPhaseCachingInfo = entryStore;

                    if (environment.State.Cache.IsNewlyAdded(entryStore.CacheEntry.MetadataHash))
                    {
                        processExecutionResult.PipCacheDescriptorV2Metadata = metadata;
                    }

                    if (environment.SchedulerTestHooks != null)
                    {
                        environment.SchedulerTestHooks.ReportPathSet(observedInputs.Value.GetPathSet(state.UnsafeOptions), process.PipId);
                    }
                }

                return true;
            }
        }

        private static void RecordCreatedDirectoriesOnMetadata(PipCacheDescriptorV2Metadata metadata, PathTable pathTable, IReadOnlySet<AbsolutePath> createdDirectories)
        {
            metadata.CreatedDirectories.AddRange(createdDirectories.Select(directory => directory.ToString(pathTable)));
        }

        private static async Task<Possible<FileMaterializationInfo>> StoreCacheableProcessOutputAsync(
            IPipExecutionEnvironment environment,
            OperationContext operationContext,
            Process process,
            FileArtifactWithAttributes output,
            FileOutputData outputData,
            bool isProcessCacheable)
        {
            Contract.Assert(output.CanBeReferencedOrCached());

            var pathTable = environment.Context.PathTable;

            FileArtifact outputArtifact = output.ToFileArtifact();

            ExpandedAbsolutePath expandedPath = outputArtifact.Path.Expand(pathTable);
            string path = expandedPath.ExpandedPath;

            var isRequired =
                // Dynamic outputs and standard files are required
                outputData.HasAnyFlag(OutputFlags.DynamicFile | OutputFlags.StandardError | OutputFlags.StandardOut) ||
                IsRequiredForCaching(output);

            // Store content for the existing outputs and report them.
            // For non-existing ones just store well known descriptors
            FileMaterializationInfo outputArtifactInfo;

            bool requiredOrExistent;
            if (isRequired)
            {
                requiredOrExistent = true;
            }
            else
            {
                // TODO: Shouldn't be doing a tracking probe here; instead should just make the store operation allow absence.
                // TODO: File.Exists returns false for directories; this is unintentional and we should move away from that (but that may break some existing users)
                //       So for now we replicate File.Exists behavior by checking for ExistsAsFile.
                // N.B. we use local-disk store here rather than the VFS. We need an authentic local-file-system result
                // (the VFS would just say 'output path should exist eventually', which is what we are working on).
                Possible<bool> possibleProbeResult =
                    environment.LocalDiskContentStore.TryProbeAndTrackPathForExistence(expandedPath)
                        .Then(existence => existence == PathExistence.ExistsAsFile);
                if (!possibleProbeResult.Succeeded)
                {
                    return possibleProbeResult.Failure;
                }

                requiredOrExistent = possibleProbeResult.Result;
            }

            if (requiredOrExistent)
            {
                bool isProcessPreservingOutputs = IsProcessPreservingOutputFile(environment, process, outputArtifact, outputData);
                bool isRewrittenOutputFile = IsRewriteOutputFile(environment, outputArtifact);

                bool shouldOutputBePreserved =
                    // Process is marked for allowing preserved output.
                    isProcessPreservingOutputs &&
                    // Rewritten output is stored to the cache.
                    !isRewrittenOutputFile;

                var pathAsString = outputArtifact.Path.ToString(environment.Context.PathTable);
                var reparsePointType = FileUtilities.TryGetReparsePointType(pathAsString);
                bool isReparsePoint = reparsePointType.Succeeded && FileUtilities.IsReparsePointActionable(reparsePointType.Result);

                // If on non-Windows OS, retrieve the owner execution permission bit so it can be stored as part of the cache metadata
                // TODO: on Linux/macOS, both TryGetIsExecutableIfNeeded and TryGetReparsePointType can be obtained with a single stat system call, whereas here stat will be done twice.
                // Presumably it won't make a big difference, but if there is an easy way to optimize this, then maybe we can just as well do it (e.g., we could call FileSystem.GetFilePermission() once and extract both from the result)
                var isExecutable = FileUtilities.CheckForExecutePermission(pathAsString);
                if (!isExecutable.Succeeded)
                {
                    return isExecutable.Failure;
                }

                bool shouldStoreOutputToCache =
                    !process.DisableSandboxing  // Don't store outputs to cache for processes running outside of the sandbox
                    && ((environment.Configuration.Schedule.StoreOutputsToCache && !shouldOutputBePreserved) || isRewrittenOutputFile)
                    && !isReparsePoint;

                AbsolutePath outputDirectoryRoot = AbsolutePath.Invalid;
                if (outputData.HasAnyFlag(OutputFlags.DynamicFile))
                {
                    // If it is a dynamic file, let's find the declared directory path.
                    outputDirectoryRoot = process.DirectoryOutputs[outputData.OpaqueDirectoryIndex].Path;
                }

                Possible<TrackedFileContentInfo> possiblyStoredOutputArtifact = shouldStoreOutputToCache
                    ? await StoreProcessOutputToCacheAsync(
                        operationContext, 
                        environment, 
                        process, 
                        outputArtifact,
                        outputDirectoryRoot,
                        output.IsUndeclaredFileRewrite, 
                        isReparsePoint, 
                        isProcessCacheable: isProcessCacheable, 
                        isExecutable: isExecutable.Result)
                    : await TrackPipOutputAsync(
                        operationContext,
                        process,
                        environment,
                        outputArtifact,
                        outputDirectoryRoot,
                        createHandleWithSequentialScan: environment.ShouldCreateHandleWithSequentialScan(outputArtifact),
                        isReparsePoint: isReparsePoint,
                        shouldOutputBePreserved: shouldOutputBePreserved,
                        isUndeclaredFileRewrite: output.IsUndeclaredFileRewrite,
                        isExecutable: isExecutable.Result);

                if (!possiblyStoredOutputArtifact.Succeeded)
                {
                    return possiblyStoredOutputArtifact.Failure;
                }

                outputArtifactInfo = possiblyStoredOutputArtifact.Result.FileMaterializationInfo;
                return outputArtifactInfo;
            }

            outputArtifactInfo = FileMaterializationInfo.CreateWithUnknownLength(WellKnownContentHashes.AbsentFile);
            return outputArtifactInfo;
        }

        private static bool IsProcessPreservingOutputs(IPipExecutionEnvironment environment, Process process)
        {
            Contract.Requires(environment != null);
            Contract.Requires(process != null);

            return process.AllowPreserveOutputs &&
                   environment.Configuration.Sandbox.UnsafeSandboxConfiguration.PreserveOutputs != PreserveOutputsMode.Disabled
                   && process.PreserveOutputsTrustLevel >= environment.Configuration.Sandbox.UnsafeSandboxConfiguration.PreserveOutputsTrustLevel;

        }

        private static bool IsProcessPreservingOutputFile(IPipExecutionEnvironment environment, Process process, FileArtifact fileArtifact, FileOutputData fileOutputData)
        {
            Contract.Requires(environment != null);
            Contract.Requires(process != null);

            if (!IsProcessPreservingOutputs(environment, process))
            {
                return false;
            }

            AbsolutePath declaredArtifactPath = fileArtifact.Path;
            if (fileOutputData.HasAnyFlag(OutputFlags.DynamicFile))
            {
                // If it is a dynamic file, let's find the declared directory path.
                declaredArtifactPath = process.DirectoryOutputs[fileOutputData.OpaqueDirectoryIndex].Path;
            }

            return PipArtifacts.IsPreservedOutputByPip(process, declaredArtifactPath, environment.Context.PathTable, environment.Configuration.Sandbox.UnsafeSandboxConfiguration.PreserveOutputsTrustLevel);
        }

        private static bool IsRewriteOutputFile(IPipExecutionEnvironment environment, FileArtifact file)
        {
            Contract.Requires(environment != null);
            Contract.Requires(file.IsOutputFile);

            // Either the file is the next version of an output file or it will be rewritten later.
            return file.RewriteCount > 1 || environment.IsFileRewritten(file);
        }

        /// <summary>
        /// Records the static and dynamic (SealedDynamicDirectories) outputs data on the cache entry.
        /// </summary>
        private static void RecordOutputsOnMetadata(
            PipCacheDescriptorV2Metadata metadata,
            Process process,
            Dictionary<AbsolutePath, FileOutputData> allOutputData,
            List<(AbsolutePath, FileMaterializationInfo)> outputHashPairs,
            PathTable pathTable)
        {
            // Initialize the list of dynamic outputs per directory output (opaque directory)
            for (int i = 0; i < process.DirectoryOutputs.Length; i++)
            {
                metadata.DynamicOutputs.Add(new PipCacheDescriptorV2Metadata.Types.RelativePathFileMaterializationInfoList());
            }

            foreach (var outputHashPair in outputHashPairs)
            {
                var path = outputHashPair.Item1;
                var materializationInfo = outputHashPair.Item2;
                var outputData = allOutputData[path];

                if (outputData.HasAllFlags(OutputFlags.DeclaredFile))
                {
                    var keyedHash = new AbsolutePathFileMaterializationInfo
                    {
                        AbsolutePath = path.ToString(pathTable),
                        Info = materializationInfo.ToGrpcFileMaterializationInfo(pathTable),
                    };
                    metadata.StaticOutputHashes.Add(keyedHash);
                }

                if (outputData.HasAllFlags(OutputFlags.DynamicFile))
                {
                    // If it is a dynamic output, store the hash and relative path from the opaque directory by preserving the ordering.
                    int opaqueIndex = outputData.OpaqueDirectoryIndex;
                    Contract.Assert(process.DirectoryOutputs.Length > opaqueIndex);
                    RelativePath relativePath;
                    // Make sure we honor the case sensitivity of the relative path if available.
                    if (materializationInfo.DynamicOutputCaseSensitiveRelativeDirectory.IsValid)
                    {
                        // the final atom of the relative path is not really relevant for casing enforcement since the current machinery uses materializationInfo.FileName for that,
                        // but if the file name is available we honor it here as well for consistency. In this way the relative path stored in the cache will always honor all casing
                        // information available.
                        relativePath = materializationInfo.DynamicOutputCaseSensitiveRelativeDirectory.Combine(
                            materializationInfo.FileName.IsValid ? materializationInfo.FileName : path.GetName(pathTable));
                    }
                    else
                    {
                        var success = process.DirectoryOutputs[opaqueIndex].Path.TryGetRelative(pathTable, path, out relativePath);
                        Contract.Assert(success);
                    }
                    var keyedHash = new RelativePathFileMaterializationInfo
                    {
                        RelativePath = relativePath.ToString(pathTable.StringTable),
                        Info = materializationInfo.ToGrpcFileMaterializationInfo(pathTable),
                    };
                    metadata.DynamicOutputs[opaqueIndex].RelativePathFileMaterializationInfos.Add(keyedHash);
                }
            }
        }

        /// <summary>
        /// Returns a cache entry that can later be stored to an <see cref="ITwoPhaseFingerprintStore"/>.
        /// In prep for storing the cache entry, we first store some supporting metadata content to the CAS:
        /// - The path-set (set of additional observed inputs, used to generate the strong fingerprint)
        /// - The metadata blob (misc. fields such as number of warnings, and provenance info).
        /// Some cache implementations may enforce that this content is stored in order to accept an entry.
        /// Returns 'null' if the supporting metadata cannot be stored.
        /// </summary>
        private static async Task<TwoPhaseCachingInfo> TryCreateTwoPhaseCacheEntryAndStoreMetadataAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state,
            Process process,
            string processDescription,
            PipCacheDescriptorV2Metadata metadata,
            List<(AbsolutePath, FileMaterializationInfo)> outputHashPairs,
            ContentHash? standardOutputContentHash,
            ContentHash? standardErrorContentHash,
            ObservedInputProcessingResult observedInputProcessingResult,
            BoxRef<ProcessFingerprintComputationEventData> fingerprintComputation)
        {
            Contract.Requires(environment != null);
            Contract.Requires(metadata != null);
            Contract.Requires(outputHashPairs != null);

            var twoPhaseCache = environment.State.Cache;
            var weakFingerprint = fingerprintComputation.Value.WeakFingerprint;

            var pathSet = observedInputProcessingResult.GetPathSet(state.UnsafeOptions);
            Possible<ContentHash> maybePathSetStored = await environment.State.Cache.TryStorePathSetAsync(pathSet, process.PreservePathSetCasing);

            // Skip logging TwoPhaseFailedToStoreMetadataForCacheEntry for cancelled operations to avoid misclassifying cancelled builds in telemetry data.
            if (environment.Context.CancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (!maybePathSetStored.Succeeded)
            {
                Logger.Log.TwoPhaseFailedToStoreMetadataForCacheEntry(
                    operationContext,
                    processDescription,
                    maybePathSetStored.Failure.Annotate("Unable to store path set.").DescribeIncludingInnerFailures());
                return null;
            }

            ContentHash pathSetHash = maybePathSetStored.Result;
            StrongContentFingerprint strongFingerprint;
            using (operationContext.StartOperation(PipExecutorCounter.ComputeStrongFingerprintDuration))
            {
                strongFingerprint = observedInputProcessingResult.ComputeStrongFingerprint(
                    environment.Context.PathTable,
                    weakFingerprint,
                    pathSetHash);
                metadata.StrongFingerprint = strongFingerprint.ToString();
            }

            fingerprintComputation.Value.StrongFingerprintComputations = new[]
            {
                ProcessStrongFingerprintComputationData.CreateForExecution(
                    pathSetHash,
                    pathSet,
                    observedInputProcessingResult.ObservedInputs,
                    strongFingerprint),
            };

            Possible<ContentHash> maybeStoredMetadata;
            using (operationContext.StartOperation(PipExecutorCounter.SerializeAndStorePipMetadataDuration))
            {
                // Note that we wrap the metadata in a PipFingerprintEntry before storing it; this is symmetric with the read-side (TryCreatePipCacheDescriptorFromMetadataAndReferencedContent)
                maybeStoredMetadata = await twoPhaseCache.TryStoreMetadataAsync(metadata);

                if (environment.Context.CancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                if (!maybeStoredMetadata.Succeeded)
                {
                    Logger.Log.TwoPhaseFailedToStoreMetadataForCacheEntry(
                        operationContext,
                        processDescription,
                        maybeStoredMetadata.Failure.Annotate("Unable to store metadata blob.").DescribeIncludingInnerFailures());
                    return null;
                }
            }

            ContentHash metadataHash = maybeStoredMetadata.Result;
            twoPhaseCache.RegisterOutputContentMaterializationResult(strongFingerprint, metadataHash, true);

            // Even though, we don't store the outputs to the cache when outputs should be preserved,
            // the output hashes are still included in the referenced contents.
            // Suppose that we don't include the output hashes. Let's have a pip P whose output o should be preserved.
            // P executes, and stores #M of metadata hash with some strong fingerprint SF.
            // Before the next build, o is deleted from disk. Now, P maintains its SF because its input has not changed.
            // P gets a cache hit, but when BuildXL tries to load o with #o (stored in M), it fails because o wasn't stored in the cache
            // and o doesn't exist on disk. Thus, P executes and produces o with different hash #o'. However, the post-execution of P
            // will fail to store #M because the entry has existed.
            //
            // In the next run, P again gets a cache hit, but when BuildXL tries to load o with #o (stored in M) it fails because o wasn't stored
            // in the cache and o, even though exists on disk, has different hash (#o vs. #o'). Thus, P executes again and produces o with different hash #o''.
            // This will happen over and over again.
            //
            // If #o is also included in the reference content, then when cache cannot pin #o (because it was never stored in the cache),
            // then it removes the entry, and thus, the post-execution of P will succeed in storing (#M, #o').
            var referencedContent = new List<ContentHash>();

            Func<ContentHash?, bool> isValidContentHash = hash => hash != null && !hash.Value.IsSpecialValue();

            if (isValidContentHash(standardOutputContentHash))
            {
                referencedContent.Add(standardOutputContentHash.Value);
            }

            if (isValidContentHash(standardErrorContentHash))
            {
                referencedContent.Add(standardErrorContentHash.Value);
            }

            referencedContent.AddRange(outputHashPairs.Select(p => p.Item2).Where(fileMaterializationInfo => fileMaterializationInfo.IsCacheable).Select(fileMaterializationInfo => fileMaterializationInfo.Hash));

            return new TwoPhaseCachingInfo(
                weakFingerprint,
                pathSetHash,
                strongFingerprint,
                new CacheEntry(metadataHash, "<Unspecified>", referencedContent.ToArray()));
        }

        /// <summary>
        /// Returns true if the output is required for caching and validation is not disabled.
        /// </summary>
        private static bool IsRequiredForCaching(FileArtifactWithAttributes output)
        {
            return output.MustExist();
        }

        /// <summary>
        /// Attempts to store an already-constructed cache entry. Metadata and content should have been stored already.
        /// On any failure, this logs warnings.
        /// </summary>
        private static async Task<StoreCacheEntryResult> StoreTwoPhaseCacheEntryAsync(
            OperationContext operationContext,
            Process process,
            CacheablePip pip,
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state,
            TwoPhaseCachingInfo cachingInfo,
            ulong descriptorUniqueId)
        {
            Contract.Requires(cachingInfo != null);

            AssertContentHashValid("PathSetHash", cachingInfo.PathSetHash);
            AssertContentHashValid("CacheEntry.MetadataHash", cachingInfo.CacheEntry.MetadataHash);

            Possible<CacheEntryPublishResult> result =
                await environment.State.Cache.TryPublishCacheEntryAsync(
                    pip.UnderlyingPip,
                    cachingInfo.WeakFingerprint,
                    cachingInfo.PathSetHash,
                    cachingInfo.StrongFingerprint,
                    cachingInfo.CacheEntry);

            if (result.Succeeded)
            {
                if (result.Result.Status == CacheEntryPublishStatus.RejectedDueToConflictingEntry)
                {
                    Logger.Log.TwoPhaseCacheEntryConflict(
                        operationContext,
                        pip.Description,
                        cachingInfo.StrongFingerprint.ToString());

                    environment.Counters.IncrementCounter(PipExecutorCounter.ProcessPipDeterminismRecoveredFromCache);
                    environment.ReportCacheDescriptorHit(result.Result.ConflictingEntry.OriginatingCache);

                    CacheEntry conflictingEntry = result.Result.ConflictingEntry;
                    return await ConvergeFromCacheAsync(operationContext, pip, environment, state, cachingInfo, process, conflictingEntry);
                }

                Contract.Assert(result.Result.Status == CacheEntryPublishStatus.Published);
                Logger.Log.TwoPhaseCacheEntryPublished(
                    operationContext,
                    pip.Description,
                    cachingInfo.WeakFingerprint.ToString(),
                    cachingInfo.PathSetHash.ToHex(),
                    cachingInfo.StrongFingerprint.ToString(),
                    descriptorUniqueId);
            }
            else
            {
                // NOTE: We return success even though storing the strong fingerprint did not succeed.
                Logger.Log.TwoPhasePublishingCacheEntryFailedWarning(
                    operationContext,
                    pip.Description,
                    result.Failure.DescribeIncludingInnerFailures(),
                    cachingInfo.ToString());
            }

            return StoreCacheEntryResult.Succeeded;

            void AssertContentHashValid(string description, ContentHash hash)
            {
                if (!hash.IsValid)
                {
                    Contract.Assert(false,
                        $"Invalid '{description}' content hash for pip '{pip.Description}'. " +
                        $"Hash =  {{ type: {hash.HashType}, lenght: {hash.Length}, hex: {hash.ToHex()} }}");
                }
            }
        }

        private static async Task<StoreCacheEntryResult> ConvergeFromCacheAsync(
            OperationContext operationContext,
            CacheablePip pip,
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state,
            TwoPhaseCachingInfo cachingInfo,
            Process process,
            CacheEntry conflictingEntry)
        {
            BoxRef<PipCacheMissEventData> pipCacheMiss = new PipCacheMissEventData
            {
                PipId = pip.PipId,
                CacheMissType = PipCacheMissType.Invalid,
            };

            // Converge to the conflicting entry rather than ignoring and continuing.
            var usableDescriptor = await TryConvertToRunnableFromCacheResultAsync(
                operationContext,
                environment,
                state,
                pip,
                PublishedEntryRefLocality.Converged,
                pip.Description,
                cachingInfo.WeakFingerprint,
                cachingInfo.PathSetHash,
                cachingInfo.StrongFingerprint,
                conflictingEntry,
                null,
                pipCacheMiss);

            if (usableDescriptor == null)
            {
                // Unable to retrieve cache descriptor for strong fingerprint
                // Do nothing (just log a warning message).
                Logger.Log.ConvertToRunnableFromCacheFailed(
                    operationContext,
                    pip.Description,
                    pipCacheMiss.Value.CacheMissType.ToString());

                // Didn't converge with cache because unable to get a usable descriptor
                // But the storage of the two phase descriptor is still considered successful
                // since there is a result in the cache for the strong fingerprint
                return StoreCacheEntryResult.Succeeded;
            }

            var runnableFromCacheResult = CreateRunnableFromCacheResult(
                usableDescriptor,
                environment,
                PublishedEntryRefLocality.Converged,
                null, // Don't pass observedInputProcessingResult since this function doesn't rely on the part of the output dependent on that.
                cachingInfo.WeakFingerprint,
                PipCacheMissType.Hit);

            if (!TryGetCacheHitExecutionResult(operationContext, environment, process, runnableFromCacheResult, out var convergedExecutionResult))
            {
                // Errors should have been logged already

                // Didn't converge with cache but the storage of the two phase descriptor is still considered successful
                // since there is a result in the cache for the strong fingerprint
                return StoreCacheEntryResult.Succeeded;
            }

            // In success case, return deployed from cache status to indicate that we converged with remote cache and that
            // reporting to environment has already happened.
            return StoreCacheEntryResult.CreateConvergedResult(convergedExecutionResult);
        }

        private static StringKeyedHash GetStringKeyedHash(IPipExecutionEnvironment environment, PipExecutionState.PipScopeState state, AbsolutePath path, ContentHash hash)
        {
            return new StringKeyedHash
            {
                Key = state.PathExpander.ExpandPath(environment.Context.PathTable, path),
                ContentHash = hash.ToByteString(),
            };
        }

        private static EncodedStringKeyedHash GetOptionalEncodedStringKeyedHash(
            IPipExecutionEnvironment environment,
            PipExecutionState.PipScopeState state,
            Tuple<AbsolutePath, Encoding> encodedStandardConsoleStream,
            ContentHash? maybeHash)
        {
            Contract.Requires(encodedStandardConsoleStream == null || maybeHash.HasValue);

            if (encodedStandardConsoleStream == null)
            {
                return null;
            }

            return new EncodedStringKeyedHash
            {
                StringKeyedHash = GetStringKeyedHash(environment, state, encodedStandardConsoleStream.Item1, maybeHash.Value),
                EncodingName = encodedStandardConsoleStream.Item2.WebName,
            };
        }

        /// <summary>
        /// Hashes and stores the specified output artifact from a process.
        /// </summary>
        private static async Task<Possible<TrackedFileContentInfo>> StoreProcessOutputToCacheAsync(
            OperationContext operationContext,
            IPipExecutionEnvironment environment,
            Process process,
            FileArtifact outputFileArtifact,
            AbsolutePath outputDirectoryRoot,
            bool isUndeclaredFileRewrite,
            bool isReparsePoint,
            bool isProcessCacheable,
            bool isExecutable)
        {
            Contract.Requires(environment != null);
            Contract.Requires(process != null);
            Contract.Requires(outputFileArtifact.IsOutputFile);

            var possiblyStored =
                await
                    environment.LocalDiskContentStore.TryStoreAsync(
                        environment.Cache.ArtifactContentCache,
                        GetFileRealizationMode(environment, process, isUndeclaredFileRewrite),
                        outputFileArtifact.Path,
                        tryFlushPageCacheToFileSystem: environment.Configuration.Sandbox.FlushPageCacheToFileSystemOnStoringOutputsToCache,
                        isReparsePoint: isReparsePoint,
                        isUndeclaredFileRewrite: isUndeclaredFileRewrite,
                        isStoringCachedProcessOutput: isProcessCacheable,
                        isExecutable: isExecutable,
                        outputDirectoryRoot: outputDirectoryRoot);

            if (!possiblyStored.Succeeded)
            {
                if (possiblyStored.Failure is TryPrepareFailure)
                {
                    Logger.Log.StoragePrepareOutputFailed(
                        operationContext,
                        process.GetDescription(environment.Context),
                        outputFileArtifact.Path.ToString(environment.Context.PathTable),
                        possiblyStored.Failure.DescribeIncludingInnerFailures());
                }
                else
                {
                    Logger.Log.StorageCachePutContentFailed(
                        operationContext,
                        process.GetDescription(environment.Context),
                        outputFileArtifact.Path.ToString(environment.Context.PathTable),
                        possiblyStored.Failure.DescribeIncludingInnerFailures());
                }
            }

            return possiblyStored;
        }

        private static async Task<Possible<TrackedFileContentInfo>> TrackPipOutputAsync(
            OperationContext operationContext,
            Pip pip,
            IPipExecutionEnvironment environment,
            FileArtifact outputFileArtifact,
            AbsolutePath outputDirectoryRoot,
            bool createHandleWithSequentialScan = false,
            bool isReparsePoint = false,
            bool shouldOutputBePreserved = false,
            bool isUndeclaredFileRewrite = false,
            bool isExecutable = false)
        {
            Contract.Requires(environment != null);
            Contract.Requires(outputFileArtifact.IsOutputFile);
            // we cannot simply track rewritten files, we have to store them into cache
            // it's fine to just track rewritten symlinks though (all data required for
            // proper symlink materialization will be a part of cache metadata)
            Contract.Requires(isReparsePoint || !IsRewriteOutputFile(environment, outputFileArtifact));

            var possiblyTracked = await environment.LocalDiskContentStore.TryTrackAsync(
                outputFileArtifact,
                tryFlushPageCacheToFileSystem: environment.Configuration.Sandbox.FlushPageCacheToFileSystemOnStoringOutputsToCache,
                outputDirectoryRoot,
                // In tracking file, LocalDiskContentStore will call TryDiscoverAsync to compute the content hash of the file.
                // TryDiscoverAsync uses FileContentTable to avoid re-hashing the file if the hash is already in the FileContentTable.
                // Moreover, FileContentTable can enable so-called path mapping optimization that allows one to avoid opening handles and by-passing checking
                // of the USN. However, here we are tracking a produced output. Thus, the known content hash should be ignored, unless the output should be preserved.
                ignoreKnownContentHashOnDiscoveringContent: !shouldOutputBePreserved,
                createHandleWithSequentialScan: createHandleWithSequentialScan,
                isReparsePoint: isReparsePoint,
                isUndeclaredFileRewrite: isUndeclaredFileRewrite,
                isExecutable: isExecutable);

            if (!possiblyTracked.Succeeded)
            {
                Logger.Log.StorageTrackOutputFailed(
                    operationContext,
                    pip.GetDescription(environment.Context),
                    outputFileArtifact.Path.ToString(environment.Context.PathTable),
                    possiblyTracked.Failure.DescribeIncludingInnerFailures());
            }

            return possiblyTracked;
        }

        private static FileRealizationMode GetFileRealizationMode(IPipExecutionEnvironment environment)
        {
            return environment.Configuration.Engine.UseHardlinks
                ? FileRealizationMode.HardLinkOrCopy // Prefers hardlinks, but will fall back to copying when creating a hard link fails. (e.g. >1023 links)
                : FileRealizationMode.Copy;
        }

        private static FileRealizationMode GetFileRealizationMode(IPipExecutionEnvironment environment, Process process, bool isUndeclaredFileRewrite)
        {
            // Make sure we don't place hardlinks for undeclared file rewrites, since we want ot leave the rewrite writable
            // for future builds
            return (environment.Configuration.Engine.UseHardlinks && !process.OutputsMustRemainWritable && !isUndeclaredFileRewrite)
                ? FileRealizationMode.HardLinkOrCopy // Prefers hardlinks, but will fall back to copying when creating a hard link fails. (e.g. >1023 links)
                : FileRealizationMode.Copy;
        }

        /// <summary>
        /// Gets a PipFragmentRenderer for a WriteFile operation.
        /// </summary>
        /// <param name="options">Options to specify how the renderer should behave.</param>
        /// <param name="pathTable">Path table</param>
        /// <returns>PipFragmentRenderer</returns>
        private static PipFragmentRenderer GetPipFragmentRendererForWriteFile(WriteFile.Options options, PathTable pathTable)
        {
            PipFragmentRenderer renderer = null;
            Func<AbsolutePath, string> pathExpander;

            switch (options.PathRenderingOption)
            {
                case WriteFile.PathRenderingOption.None:
                    renderer = new PipFragmentRenderer(pathTable);
                    break;
                case WriteFile.PathRenderingOption.BackSlashes:
                    pathExpander = path => path.ToString(pathTable, PathFormat.Windows);
                    renderer = new PipFragmentRenderer(pathExpander, pathTable.StringTable, monikerRenderer: null);
                    break;
                case WriteFile.PathRenderingOption.EscapedBackSlashes:
                    pathExpander = path => path.ToString(pathTable, PathFormat.Windows).Replace(@"\", @"\\");
                    renderer = new PipFragmentRenderer(pathExpander, pathTable.StringTable, monikerRenderer: null);
                    break;
                case WriteFile.PathRenderingOption.ForwardSlashes:
                    // PathFormat.Script will use forward slashes when rendering path
                    pathExpander = path => path.ToString(pathTable, PathFormat.Script);
                    renderer = new PipFragmentRenderer(pathExpander, pathTable.StringTable, monikerRenderer: null);
                    break;
                default:
                    Contract.Assert(false, $"Invalid WriteFile.Options value specified: ${options}");
                    break;
            }

            return renderer;
        }

        /// <summary>
        /// Store pip standard output or standard error into cache for orchestrator to retrieve
        /// Return a EncondedStringKeyedHash that added into ExecutionResult later
        /// Stdout/error are stored in artifact cache without a cache descriptor or fingerprint
        /// These are only meant to be used for orchestrator to retrieve the content and save into separate log if required
        /// </summary>
        private static async Task<Possible<EncodedStringKeyedHash>> TryStorePipStandardOutputOrErrorToCache(
            Tuple<AbsolutePath, Encoding> output, 
            IPipExecutionEnvironment environment, 
            PathTable pathTable, 
            Process pip, 
            OperationContext operationContext,
            PipExecutionState.PipScopeState state)
        {
            Contract.Requires(output.Item1.IsValid && output.Item2 != null);

            var path = output.Item1;
            var artifact = FileArtifact.CreateOutputFile(path).WithAttributes(FileExistence.Required);
            Possible<ContentHash> possiblyStored = await environment.Cache.ArtifactContentCache.TryStoreAsync(GetFileRealizationMode(environment), path.Expand(pathTable));

            if (!possiblyStored.Succeeded)
            {
                if (possiblyStored.Failure is TryPrepareFailure)
                {
                    Logger.Log.StoragePrepareOutputFailed(
                        operationContext,
                        pip.GetDescription(environment.Context),
                        artifact.Path.ToString(environment.Context.PathTable),
                        possiblyStored.Failure.DescribeIncludingInnerFailures());
                }
                else
                {
                    Logger.Log.StorageCachePutContentFailed(
                        operationContext,
                        pip.GetDescription(environment.Context),
                        artifact.Path.ToString(environment.Context.PathTable),
                        possiblyStored.Failure.DescribeIncludingInnerFailures());
                }
                return possiblyStored.Failure;
            }

            return GetOptionalEncodedStringKeyedHash(environment, state, output, possiblyStored.Result);
        }

        /// <summary>
        /// Persist pip standard output and standard error in Log directory if pip has failure or warning
        /// </summary>
        public static async Task LogStandardOutputAndErrorForFailAndWarningPips(ProcessRunnablePip runnablePip, ExecutionResult executionResult)
        {
            var environment = runnablePip.Environment;
            var state = environment.State.GetScope(runnablePip.Process);
            var directoryPath = environment.Configuration.Logging.LogsDirectory.Combine(environment.Context.PathTable, SandboxedProcessPipExecutor.StdOutputsDirNameInLog).Combine(environment.Context.PathTable, $"{runnablePip.FormattedSemiStableHash}").ToString(environment.Context.PathTable);

            EncodedStringKeyedHash standardOutput = null;
            EncodedStringKeyedHash standardError = null;

            if (executionResult.StandardOutput != null)
            {
                standardOutput = executionResult.StandardOutput;
            }
            else if (executionResult.PipCacheDescriptorV2Metadata != null && executionResult.PipCacheDescriptorV2Metadata.StandardOutput != null)
            {
                standardOutput = executionResult.PipCacheDescriptorV2Metadata.StandardOutput;
            }

            if (executionResult.StandardError != null)
            {
                standardError = executionResult.StandardError;
            }
            else if (executionResult.PipCacheDescriptorV2Metadata != null && executionResult.PipCacheDescriptorV2Metadata.StandardError != null)
            {
                standardError = executionResult.PipCacheDescriptorV2Metadata.StandardError;
            }

            if (standardOutput != null)
            {
                await LoadAndPersistPipStdOutput(runnablePip.LoggingContext, environment, state, standardOutput, directoryPath, SandboxedProcessFile.StandardOutput.DefaultFileName(), runnablePip.FormattedSemiStableHash);
            }

            if (standardError != null)
            {
                await LoadAndPersistPipStdOutput(runnablePip.LoggingContext, environment, state, standardError, directoryPath, SandboxedProcessFile.StandardError.DefaultFileName(), runnablePip.FormattedSemiStableHash);
            }           
        }

        private static async Task LoadAndPersistPipStdOutput(LoggingContext loggingContext, IPipExecutionEnvironment environment, PipExecutionState.PipScopeState state, EncodedStringKeyedHash stringKeyedHash, string directoryPath, string fileName, string formattedSemiStableHash)
        {
            var filePath = Path.Combine(directoryPath, fileName);
            var absoluteFilePath = AbsolutePath.Create(environment.Context.PathTable, filePath).Expand(environment.Context.PathTable);
            Tuple<AbsolutePath, ContentHash, string> output;
            if (!TryParseOptionalStandardConsoleStreamHash(environment.Context.PathTable, state.PathExpander, stringKeyedHash, out output))
            {
                return;
            }

            if (output != null)
            {
                try
                {
                    FileUtilities.CreateDirectoryWithRetry(directoryPath);
                }
                catch (Exception ex)
                {
                    Logger.Log.UnableToWritePipStandardOutputLog(loggingContext, formattedSemiStableHash, directoryPath, ex.ToString());
                    return;
                }

                var maybeResult = await environment.Cache.ArtifactContentCache.TryMaterializeAsync(GetFileRealizationMode(environment), absoluteFilePath, output.Item2, environment.Context.CancellationToken);
                if (!maybeResult.Succeeded)
                {
                    Logger.Log.UnableToWritePipStandardOutputLog(loggingContext, formattedSemiStableHash, filePath, maybeResult.Failure.ToString());
                }
            }
            return;
        }
    }
}
