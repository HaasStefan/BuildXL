// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Specialized;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Threading.Tasks;
using BuildXL.Engine.Cache.Artifacts;
using BuildXL.Native.IO;
using BuildXL.Pips.Graph;
using BuildXL.Processes;
using BuildXL.Storage.ChangeTracking;
using BuildXL.Utilities;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Tracing;
using static BuildXL.Utilities.Core.FormattableStringEx;

namespace BuildXL.Scheduler.FileSystem
{
    /// <summary>
    /// Tracks and caches existence of files/directories in real/graph filesystems.
    /// 
    /// This class tracks three independent file systems based on <see cref="FileSystemViewMode"/>:
    /// Real - the actual file system on disk. See note below about caveats of caching real file system existence.
    /// FullGraph - the file system as seen by declared files/directories in the pip graph
    /// OutputGraph - the file system as seen by declared OUTPUT files/directories in the pip graph
    /// 
    /// Local file system state is mutable so technically caching of local file system state can always become out of date.
    /// It is the responsiblity of the caller to call this class in a way that minimizes this possibility. Namely, querying the
    /// output graph file system first as an overlay, gives a view of all known produced files/directories.
    /// </summary>
    public class FileSystemView : ILocalDiskFileSystemExistenceView, ISandboxFileSystemView
    {
        /// <summary>
        /// The local disk file system for querying real file system state
        /// </summary>
        private ILocalDiskFileSystemView LocalDiskFileSystem { get; set; }

        /// <summary>
        /// Underlying pip graph file system view for querying associated file artifacts for path
        /// </summary>
        private IPipGraphFileSystemView PipGraph { get; set; }

        /// <summary>
        /// Counters for the file system view
        /// </summary>
        public CounterCollection<FileSystemViewCounters> Counters { get; } = new CounterCollection<FileSystemViewCounters>();

        private PathTable PathTable { get; }

        private ConcurrentBigMap<AbsolutePath, FileSystemEntry> PathExistenceCache { get; }

        private readonly bool m_inferNonExistenceBasedOnParentPathInRealFileSystem;

        /// <summary>
        /// Creates a new file system view
        /// </summary>
        public FileSystemView(
            PathTable pathTable, 
            IPipGraphFileSystemView pipGraph, 
            ILocalDiskFileSystemView localDiskContentStore,
            bool inferNonExistenceBasedOnParentPathInRealFileSystem = true)
        {
            PathTable = pathTable;
            PipGraph = pipGraph;
            LocalDiskFileSystem = localDiskContentStore;
            PathExistenceCache = new ConcurrentBigMap<AbsolutePath, FileSystemEntry>();
            m_inferNonExistenceBasedOnParentPathInRealFileSystem = inferNonExistenceBasedOnParentPathInRealFileSystem;
        }

        /// <summary>
        /// Constructs a file system view for the given pip graph
        /// </summary>
        public static FileSystemView Create(
            PathTable pathTable, 
            PipGraph pipGraph, 
            ILocalDiskFileSystemView localDiskContentStore, 
            int maxInitializationDegreeOfParallelism,
            bool inferNonExistenceBasedOnParentPathInRealFileSystem = true)
        {
            var fileSystemView = new FileSystemView(pathTable, pipGraph, localDiskContentStore, inferNonExistenceBasedOnParentPathInRealFileSystem);

            Parallel.For(0, pipGraph.ArtifactContentCount,
                new ParallelOptions { MaxDegreeOfParallelism = maxInitializationDegreeOfParallelism },
                contentIndex =>
                {
                    fileSystemView.AddArtifact(pipGraph.GetArtifactContent(contentIndex));
                });

            return fileSystemView;
        }

        /// <summary>
        /// Gets the existence of a path for the given file system view mode
        /// </summary>
        /// <remarks>
        /// The option to include outputs reported before caching allows to get the reported existence
        /// of just-produced outputs that are not cached yet. These outputs may or may not become legit outputs
        /// since cache convergence may happen afterwards and may change the final outputs.
        /// </remarks>
        public Possible<PathExistence> GetExistence(AbsolutePath path, FileSystemViewMode mode, bool? isReadOnly = default, bool cachePathExistence = true, bool includeOutputsProducedBeforeCaching = false)
        {
            PathExistence existence;
            if (TryGetKnownPathExistence(path, mode, out existence, includeOutputsProducedBeforeCaching))
            {
                return existence;
            }

            if (mode == FileSystemViewMode.Real)
            {
                // Compute and cache the real file system existence so subsequent calls
                // do not have to query file system
                if (cachePathExistence)
                {
                    return ComputeAndAddCacheRealFileSystemExistence(path, mode, isReadOnly);
                }

                var possibleExistence = FileUtilities.TryProbePathExistence(path.Expand(PathTable).ExpandedPath, followSymlink: false);
                return possibleExistence.Succeeded ? new Possible<PathExistence>(possibleExistence.Result) : new Possible<PathExistence>(possibleExistence.Failure);
            }
            else
            {
                // All graph filesystem path existences are statically known so just return NonExistent
                // if not found in the existence cache or underlying graph filesystem view
                return PathExistence.Nonexistent;
            }
        }

        /// <summary>
        /// Gets the existence of a path for the given file system view mode if set
        /// </summary>
        private bool TryGetKnownPathExistence(AbsolutePath path, FileSystemViewMode mode, out PathExistence existence, bool includeOutputsProducedBeforeCaching = false)
        {
            if (mode == FileSystemViewMode.FullGraph || mode == FileSystemViewMode.Output)
            {
                if (ExistsInGraphFileSystem(PipGraph.TryGetLatestFileArtifactForPath(path), mode))
                {
                    existence = PathExistence.ExistsAsFile;
                    return true;
                }
            }

            // For graph file systems, we query the path existence cache for existence of directories. Files are tracked
            // by the pip graph. For dynamic files, we need to check PathExistenceCache.
            existence = PathExistence.Nonexistent;
            FileSystemEntry entry;

            var result = PathExistenceCache.TryGetValue(path, out entry) && entry.TryGetExistence(mode, out existence);

            // If we found an entry that is a directory created by a pip with no known output files underneath, we return nonexistent.
            // Observe in this case bxl won't replay the directory creation. So we pretend the directory is not part of the output file system to improve cache stability
            // This only applies to the Output view mode, where the IsDirectoryCreatedByPip is meaningful.
            if (mode == FileSystemViewMode.Output &&
                existence == PathExistence.ExistsAsDirectory && 
                entry.HasFlag(FileSystemEntryFlags.IsDirectoryCreatedByPip) &&
                !entry.HasFlag(FileSystemEntryFlags.DirectoryContainingFiles))
            {
                existence = PathExistence.Nonexistent;
            }

            // If we find an existing entry that is reported before caching happens, we consider that as absent
            // unless specified otherwise
            if (!includeOutputsProducedBeforeCaching && 
                existence == PathExistence.ExistsAsFile && 
                entry.HasFlag(FileSystemEntryFlags.OutputProducedBeforeCaching) && 
                !entry.HasFlag(FileSystemEntryFlags.OutputProducedAfterCaching))
            {
                existence = PathExistence.Nonexistent;
            }

            return result;
        }

        /// <summary>
        /// Queries the existence and members of a directory in the specified file system mode
        /// </summary>
        public Possible<PathExistence> TryEnumerateDirectory(AbsolutePath path, FileSystemViewMode mode, Action<string, AbsolutePath, PathExistence> handleEntry, bool cachePathExistence = true)
        {
            FileSystemEntry entry;
            PathExistence existence;
            if (PathExistenceCache.TryGetValue(path, out entry) && entry.TryGetExistence(mode, out existence))
            {
                if (existence == PathExistence.Nonexistent)
                {
                    return existence;
                }

                if (existence == PathExistence.ExistsAsFile)
                {
                    bool isDirectorySymlinkOrJunction = false;

                    if (entry.HasFlag(FileSystemEntryFlags.CheckedIsDirectorySymlink))
                    {
                        isDirectorySymlinkOrJunction = entry.HasFlag(FileSystemEntryFlags.IsDirectorySymlink);
                    }
                    else
                    {
                        isDirectorySymlinkOrJunction = FileUtilities.IsDirectorySymlinkOrJunction(path.ToString(PathTable));
                        PathExistenceCache.AddOrUpdate(path, false,
                            (key, data) => { throw Contract.AssertFailure("Entry should already be added for path"); },
                            (key, data, oldValue) => oldValue.SetFlag(
                                FileSystemEntryFlags.CheckedIsDirectorySymlink 
                                | (isDirectorySymlinkOrJunction ? FileSystemEntryFlags.IsDirectorySymlink : FileSystemEntryFlags.None)));
                    }

                    if (!isDirectorySymlinkOrJunction)
                    {
                        return existence;
                    }
                }

                // For graph file systems, directory members can be determined by overlaying path table with existence state in-memory
                // For real file system, this same is true if the directory has already been enumerated
                if (mode != FileSystemViewMode.Real || ((entry.Flags & FileSystemEntryFlags.IsRealFileSystemEnumerated) != 0))
                {
                    foreach (var childPathValue in PathTable.EnumerateImmediateChildren(path.Value))
                    {
                        var childPath = new AbsolutePath(childPathValue);

                        PathExistence childExistence;
                        if (TryGetKnownPathExistence(childPath, mode, out childExistence) && childExistence != PathExistence.Nonexistent)
                        {
                            var entryName = childPath.GetName(PathTable).ToString(PathTable.StringTable);
                            handleEntry(entryName, childPath, childExistence);
                        }
                    }

                    return existence;
                }
            }

            if (mode == FileSystemViewMode.Real)
            {
                var handleDirectoryEntry = new Action<string, FileAttributes>((entryName, entryAttributes) =>
                {
                    // Reparse points are always treated as files. Otherwise, honor the directory attribute to determine 
                    // existence
                    var childExistence = (entryAttributes & FileAttributes.ReparsePoint) != 0 ?
                        PathExistence.ExistsAsFile :
                        (entryAttributes & FileAttributes.Directory) != 0 ? 
                            PathExistence.ExistsAsDirectory : 
                            PathExistence.ExistsAsFile;
                    
                    var childPath = path.Combine(PathTable, entryName);

                    childExistence = GetOrAddExistence(childPath, mode, childExistence, updateParents: false);

                    // NOTE: Because we are caching file system state in memory, it is possible that the existence state of
                    // files does not match the state from the file system.
                    if (childExistence != PathExistence.Nonexistent)
                    {
                        handleEntry(entryName, childPath, childExistence);
                    }
                });

                Counters.IncrementCounter(FileSystemViewCounters.RealFileSystemEnumerations);

                if (cachePathExistence)
                {
                    Possible<PathExistence> possibleExistence;
                    using (Counters.StartStopwatch(FileSystemViewCounters.RealFileSystemEnumerationsDuration))
                    {

                        possibleExistence = LocalDiskFileSystem.TryEnumerateDirectoryAndTrackMembership(
                            path,
                            handleDirectoryEntry,
                            // This method is called during observed input processing. Currently, we simply include all entries.
                            // TODO: In the future, we may want to restrict it based on pip's untracked scopes/paths.
                            shouldIncludeEntry: null /* include all entries */);
                    }

                    if (possibleExistence.Succeeded)
                    {
                        existence = GetOrAddExistence(path, mode, possibleExistence.Result);

                        PathExistenceCache.AddOrUpdate(path, false,
                            (key, data) => { throw Contract.AssertFailure("Entry should already be added for path"); },
                            (key, data, oldValue) => oldValue.SetFlag(FileSystemEntryFlags.IsRealFileSystemEnumerated));

                        return existence;
                    }

                    return possibleExistence;
                }

                using (Counters.StartStopwatch(FileSystemViewCounters.RealFileSystemEnumerationsDuration))
                {
                    var possibleFingerprintResult = DirectoryMembershipTrackingFingerprinter.ComputeFingerprint(
                        path.Expand(PathTable).ExpandedPath, 
                        handleEntry: handleDirectoryEntry);

                    return possibleFingerprintResult.Succeeded 
                        ? new Possible<PathExistence>(possibleFingerprintResult.Result.PathExistence) 
                        : new Possible<PathExistence>(possibleFingerprintResult.Failure);
                }
            }
            else if (ExistsInGraphFileSystem(PipGraph.TryGetLatestFileArtifactForPath(path), mode))
            {
                return PathExistence.ExistsAsFile;
            }

            return PathExistence.Nonexistent;
        }

        /// <summary>
        /// Computes the existence of a path when not cached. For graph file systems the existence state of all paths
        /// is statically known, so non-existent is returned here as not finding a cached path means it does not appear
        /// in graph file system.
        /// </summary>
        private Possible<PathExistence> ComputeAndAddCacheRealFileSystemExistence(AbsolutePath path, FileSystemViewMode mode, bool? isReadOnly = default)
        {
            Contract.Requires(mode == FileSystemViewMode.Real);

            // Optimization. Check if the path can be determined to not exist based on a parent path without
            // checking file system
            if (m_inferNonExistenceBasedOnParentPathInRealFileSystem &&
                TryInferNonExistenceBasedOnParentPathInRealFileSystem(path, out var trackedParentPath, out var intermediateParentPath) &&
                TrackRealFileSystemAbsentChildPath(trackedParentPath, descendantPath: path))
            {
                return PathExistence.Nonexistent;
            }

            // TODO: Some kind of strategy to trigger enumerating directories with commonly probed members 
            // TODO: Perhaps probabilistically enumerate based on hash of path and some counter
            var possibleExistence = LocalDiskFileSystem.TryProbeAndTrackPathForExistence(path.Expand(PathTable), isReadOnly);

            Counters.IncrementCounter(FileSystemViewCounters.RealFileSystemDiskProbes);
            if (possibleExistence.Succeeded)
            {
                GetOrAddExistence(path, mode, possibleExistence.Result);
            }

            return possibleExistence;
        }

        private bool TrackRealFileSystemAbsentChildPath(AbsolutePath trackedParentPath, AbsolutePath descendantPath)
        {
            var path = descendantPath;
            while (path.IsValid && path != trackedParentPath)
            {
                GetOrAddExistence(path, FileSystemViewMode.Real, PathExistence.Nonexistent);
                path = path.GetParent(PathTable);
            }

            return LocalDiskFileSystem.TrackAbsentPath(trackedParentPath, descendantPath);
        }

        /// <summary>
        /// Infers if a path is non-existent based on existence state of parent paths.
        /// </summary>
        private bool TryInferNonExistenceBasedOnParentPathInRealFileSystem(
            AbsolutePath path,
            out AbsolutePath trackedParentPath,
            out AbsolutePath lastChildPath)
        {
            var mode = FileSystemViewMode.Real;

            lastChildPath = path;
            var parentPath = path.GetParent(PathTable);

            while (parentPath.IsValid)
            {
                PathExistence existence;
                FileSystemEntry entry;
                if (PathExistenceCache.TryGetValue(parentPath, out entry) && entry.TryGetExistence(mode, out existence))
                {
                    if (existence == PathExistence.Nonexistent || existence == PathExistence.ExistsAsFile)
                    {
                        // Parent path is non-existent or file, so the probe path cannot exist
                        Counters.IncrementCounter(existence == PathExistence.Nonexistent ?
                            FileSystemViewCounters.InferredNonExistentPaths_NonExistentParent :
                            FileSystemViewCounters.InferredNonExistentPaths_FileParent);
                        trackedParentPath = parentPath;
                        return true;
                    }
                    else if (existence == PathExistence.ExistsAsDirectory)
                    {
                        trackedParentPath = parentPath;

                        if (entry.HasFlag(FileSystemEntryFlags.IsRealFileSystemEnumerated))
                        {
                            // Enumerated directory parent, in which case we know the existence state of the child path
                            // 1. If the child path is not in the existence map, the queried path does not exist because all existent paths are added after enumeration
                            // 2. If the child path is non-existent in the existence map, the queried path does not exist because child path is 
                            //    the queried path or a parent path
                            // 3. If the child path is a file in the existence map, the queried path does not exist IF child path is a parent of the queried path
                            bool foundChildPathEntry;
                            if (!(foundChildPathEntry = TryGetKnownPathExistence(lastChildPath, mode, out existence))
                                || existence == PathExistence.Nonexistent
                                || (existence == PathExistence.ExistsAsFile && lastChildPath != path))
                            {
                                trackedParentPath = (foundChildPathEntry && lastChildPath != path) ? lastChildPath : parentPath;
                                Counters.IncrementCounter(lastChildPath == path ?
                                    FileSystemViewCounters.InferredNonExistentPaths_EnumeratedDirectParent :
                                    FileSystemViewCounters.InferredNonExistentPaths_EnumeratedAncestor);
                                return true;
                            }
                        }

                        return false;
                    }

                    trackedParentPath = AbsolutePath.Invalid;
                    return false;
                }

                lastChildPath = parentPath;
                parentPath = parentPath.GetParent(PathTable);
            }

            trackedParentPath = AbsolutePath.Invalid;
            return false;
        }

        /// <summary>
        /// Adds the existence state of the path if not present or returns the current cached existence state of the path
        /// </summary>
        private PathExistence GetOrAddExistence(AbsolutePath path, FileSystemViewMode mode, PathExistence existence, bool updateParents = true, FileSystemEntryFlags flags = FileSystemEntryFlags.None)
        {
            return GetOrAddExistence(path, mode, existence, out var added, updateParents, flags);
        }

        /// <summary>
        /// Adds the existence state of the path if not present or returns the current cached existence state of the path
        /// </summary>
        /// <remarks>
        /// If flags are provided, those are added to potentially existing flags
        /// </remarks>
        private PathExistence GetOrAddExistence(AbsolutePath path, FileSystemViewMode mode, PathExistence existence, out bool added, bool updateParents = true, FileSystemEntryFlags flags = FileSystemEntryFlags.None)
        {
            PathExistence result = existence;
            var originalPath = path;
            while (path.IsValid)
            {
                var getOrAddResult = PathExistenceCache.GetOrAdd(path, FileSystemEntry.Create(mode, existence).SetFlag(flags));
                if (getOrAddResult.IsFound)
                {
                    var currentExistence = getOrAddResult.Item.Value.GetExistence(mode);
                    var currentFlags = getOrAddResult.Item.Value.Flags;
                    if (currentExistence != null && currentFlags.HasFlag(flags))
                    {
                        if (originalPath != path)
                        {
                            // Parent entry with existence already exists, just return the result
                            added = true;
                            return result;
                        }
                        else
                        {
                            // Entry with existence already exists, just return the value
                            added = false;
                            return currentExistence.Value;
                        }
                    }

                    // found entry for the 'path', but it does not contain existence for the specified 'mode'
                    var updateResult = PathExistenceCache.AddOrUpdate(path, (mode, existence),
                        (key, data) => FileSystemEntry.Create(data.mode, data.existence).SetFlag(flags),
                        (key, data, oldValue) => oldValue.TryUpdate(data.mode, data.existence).SetFlag(flags));

                    // the same entry might be updated concurrently; check whether we lost the race, and if we did, stop further processing 
                    currentExistence = updateResult.OldItem.Value.GetExistence(mode);
                    currentFlags = updateResult.OldItem.Value.Flags;
                    if (currentExistence != null && currentFlags.HasFlag(flags))
                    {
                        if (originalPath != path)
                        {
                            // Parent entry with existence already exists, just return the result
                            added = true;
                            return result;
                        }
                        else
                        {
                            // Entry with existence already exists, just return the value
                            added = false;
                            return currentExistence.Value;
                        }
                    }
                }

                // Only register parents as directories if path exists AND updateParents=true
                if (existence == PathExistence.Nonexistent || !updateParents)
                {
                    break;
                }

                // If the artifact being added is a file, then make sure we flag all upstream directories as containing files
                if (existence == PathExistence.ExistsAsFile)
                {
                    flags |= FileSystemEntryFlags.DirectoryContainingFiles;
                }

                // Set ancestor paths existence to directory existent for existent paths
                existence = PathExistence.ExistsAsDirectory;
                path = path.GetParent(PathTable);
            }

            added = true;
            return result;
        }

        /// <summary>
        /// Adds a file/directory artifact to the graph file system views. This method only adds the parent directory
        /// for added file artifacts. The pip graph file system is expected to track existence of directories.
        /// </summary>
        internal void AddArtifact(in FileOrDirectoryArtifact artifact)
        {
            var parentPath = artifact.Path.GetParent(PathTable);
            if (!parentPath.IsValid)
            {
                // Adding artifact only tracks parent directory paths
                // Other paths can be queried directly from pip graph maps
                // Skip if parent path is invalid
                return;
            }

            if (artifact.IsDirectory)
            {
                // NOTE: Only output directories are included in graph file systems
                // For other directory kinds, the file contents of the directory will ensure
                // existence of the directory in the graph file system. This maintains consistency
                // with the pip graph file system used before the introduction of this change
                if (artifact.DirectoryArtifact.IsOutputDirectory())
                {
                    // Only output files/directories are considered in output/full graph file system
                    GetOrAddExistence(artifact.Path, FileSystemViewMode.FullGraph, PathExistence.ExistsAsDirectory);
                    GetOrAddExistence(artifact.Path, FileSystemViewMode.Output, PathExistence.ExistsAsDirectory);
                }
            }
            else
            {
                // Add the parent directory to the full graph file system
                GetOrAddExistence(parentPath, FileSystemViewMode.FullGraph, PathExistence.ExistsAsDirectory);

                if (artifact.FileArtifact.IsOutputFile)
                {
                    // Only output files/directories are considered in output graph file system
                    // Because we are declaring them as parents of output files, we should mark them as containing files
                    GetOrAddExistence(parentPath, FileSystemViewMode.Output, PathExistence.ExistsAsDirectory, flags: FileSystemEntryFlags.DirectoryContainingFiles);
                }
            }
        }

        /// <summary>
        /// Reports existence of path in real file system
        /// </summary>
        public void ReportRealFileSystemExistence(AbsolutePath path, PathExistence existence)
        {
            GetOrAddExistence(path, FileSystemViewMode.Real, existence);
        }

        /// <summary>
        /// Reports existence of path in output file system
        /// </summary>
        public void ReportOutputFileSystemExistence(AbsolutePath path, PathExistence existence)
        {
            // Make sure we flag the output as reported after caching, so it becomes a 'legit' output and existence
            // is reported accordingly
            GetOrAddExistence(path, FileSystemViewMode.Output, existence, updateParents: true, flags: FileSystemEntryFlags.OutputProducedAfterCaching);
        }

        /// <inheritdoc/>
        public void ReportOutputFileSystemDirectoryCreated(AbsolutePath path)
        {
            ReportOutputFileSystemDirectoryOperation(path, FileSystemEntryFlags.IsDirectoryCreatedByPip);
        }

        /// <inheritdoc/>
        public void ReportOutputFileSystemDirectoryRemoved(AbsolutePath path)
        {
            ReportOutputFileSystemDirectoryOperation(path, FileSystemEntryFlags.IsDirectoryRemovedByPip);
        }

        private void ReportOutputFileSystemDirectoryOperation(AbsolutePath path, FileSystemEntryFlags flag)
        {
            // We want to be able to later identify if the directory was created or removed by a pip, as opposed to being
            // part of the output file system just because a file was created underneath. We flag the directory
            // with the given flag for that purpose
            Contract.Requires(flag == FileSystemEntryFlags.IsDirectoryCreatedByPip || flag == FileSystemEntryFlags.IsDirectoryRemovedByPip);
            Contract.Requires(path.IsValid);
            GetOrAddExistence(path, FileSystemViewMode.Output, PathExistence.ExistsAsDirectory, updateParents: false, flag);
        }

        /// <summary>
        /// Reports that a shared opaque output was just produced by an executing pip before the output is cached
        /// </summary>
        /// <remarks>
        /// This output may not become a legit output if cache convergence happens and the final
        /// result does not contain this particular path
        /// </remarks>
        public void ReportSharedOpaqueOutputProducedBeforeCaching(AbsolutePath path)
        {
            Contract.Requires(path.IsValid);
            GetOrAddExistence(path, FileSystemViewMode.Output, PathExistence.ExistsAsFile, updateParents: false, FileSystemEntryFlags.OutputProducedBeforeCaching);
        }

        /// <inheritdoc/>
        public bool ExistCreatedDirectoryInOutputFileSystem(AbsolutePath path) => ExistDirectoryInOutputFileSystem(path, FileSystemEntryFlags.IsDirectoryCreatedByPip);

        /// <inheritdoc/>
        public bool ExistRemovedDirectoryInOutputFileSystem(AbsolutePath path) => ExistDirectoryInOutputFileSystem(path, FileSystemEntryFlags.IsDirectoryRemovedByPip);

        private bool ExistDirectoryInOutputFileSystem(AbsolutePath path, FileSystemEntryFlags flag)
        {
            if (PathExistenceCache.TryGetValue(path, out var entry) && entry.TryGetExistence(FileSystemViewMode.Output, out var existence) && entry.HasFlag(flag))
            {
                return existence == PathExistence.ExistsAsDirectory;
            }

            return false;
        }

        private static bool ExistsInGraphFileSystem(FileArtifact artifact, FileSystemViewMode mode)
        {
            return artifact.IsValid && (mode == FileSystemViewMode.FullGraph || artifact.IsOutputFile);
        }

        Possible<PathExistence, Failure> ILocalDiskFileSystemExistenceView.TryProbeAndTrackPathForExistence(ExpandedAbsolutePath path, bool? isReadOnly)
        {
            // NOTE: We don't use cached existence state as this is called by FileContentManager which needs
            // accurate existence. That said, FileContentManager keeps caches of file content so we don't expect
            // this to incur must redundant queries. Further, this informs the existence cache so that
            // queries from ObservedInputProcessor can utilize the cached existence state.
            var possibleExistence = LocalDiskFileSystem.TryProbeAndTrackPathForExistence(path, isReadOnly);
            Counters.IncrementCounter(FileSystemViewCounters.RealFileSystemDiskProbes_FileContentManager);

            if (possibleExistence.Succeeded)
            {
                var cachedExistence = GetOrAddExistence(path.Path, FileSystemViewMode.Real, possibleExistence.Result, out var added);
                if (added)
                {
                    // Existence was added
                    Counters.IncrementCounter(FileSystemViewCounters.RealFileSystemDiskProbes_FileContentManager_Added);
                }
                else if (cachedExistence == possibleExistence.Result)
                {
                    if (cachedExistence == PathExistence.Nonexistent)
                    {
                        // Existence result matches for non-existent path
                        Counters.IncrementCounter(FileSystemViewCounters.RealFileSystemDiskProbes_FileContentManager_UpToDateNonExistence);
                    }
                    else
                    {
                        // Existence result matches for existent path
                        Counters.IncrementCounter(FileSystemViewCounters.RealFileSystemDiskProbes_FileContentManager_UpToDateExists);
                    }
                }
                else
                {
                    if (cachedExistence == PathExistence.Nonexistent)
                    {
                        // Does not exist in cached file system but exists in actual file system
                        Counters.IncrementCounter(FileSystemViewCounters.RealFileSystemDiskProbes_FileContentManager_StaleNonExistence);
                    }
                    else if (possibleExistence.Result == PathExistence.Nonexistent)
                    {
                        // Does exists in cached file system but does not exist in actual file system
                        Counters.IncrementCounter(FileSystemViewCounters.RealFileSystemDiskProbes_FileContentManager_StaleExists);
                    }
                    else
                    {
                        // Exists in both file systems, but kind (file or directory) does not match
                        Counters.IncrementCounter(FileSystemViewCounters.RealFileSystemDiskProbes_FileContentManager_StaleExistenceMismatch);
                    }
                }
            }

            return possibleExistence;
        }

        [Flags]
        private enum FileSystemEntryFlags
        {
            None,
            IsRealFileSystemEnumerated = 1 << 0,
            IsDirectorySymlink = 1 << 1,
            CheckedIsDirectorySymlink = 1 << 2,
            // This means the pip created a directory that was non-existent before the pip ran
            // The flag is present for directories that are created during pip execution and
            // also for directories that are created by BuildXL as part of 'preparation' for the pip's outputs
            IsDirectoryCreatedByPip = 1 << 3, 
            DirectoryContainingFiles = 1 << 4,
            OutputProducedBeforeCaching = 1 << 5,
            OutputProducedAfterCaching = 1 << 6,
            IsDirectoryRemovedByPip = 1 << 7, // This means a directory was deleted by a given pip
        }

        private readonly struct FileSystemEntry
        {
            private static readonly byte MaxPathExistenceValue = (byte)(EnumTraits<PathExistence>.MaxValue + 1);
            private static readonly BitVector32.Section RealExistenceSection = BitVector32.CreateSection(MaxPathExistenceValue);
            private static readonly BitVector32.Section OutputGraphExistenceSection = BitVector32.CreateSection(MaxPathExistenceValue, RealExistenceSection);
            private static readonly BitVector32.Section FullGraphExistenceSection = BitVector32.CreateSection(MaxPathExistenceValue, OutputGraphExistenceSection);
            private static readonly BitVector32.Section FlagsSection = BitVector32.CreateSection((byte)(EnumTraits<FileSystemEntryFlags>.MaxValue + 1), FullGraphExistenceSection);

            private readonly BitVector32 m_value;
            public PathExistence? RealExistence => GetPathExistence(RealExistenceSection);
            public PathExistence? OutputGraphExistence => GetPathExistence(OutputGraphExistenceSection);
            public PathExistence? FullGraphExistence => GetPathExistence(FullGraphExistenceSection);
            public FileSystemEntryFlags Flags => (FileSystemEntryFlags)m_value[FlagsSection];

            private FileSystemEntry(BitVector32 value)
            {
                m_value = value;
            }

            public static FileSystemEntry Create(FileSystemViewMode mode, PathExistence existence)
            {
                return new FileSystemEntry().TryUpdate(mode, existence);
            }

            public PathExistence? GetExistence(FileSystemViewMode mode)
            {
                return GetPathExistence(GetFileSystemModeSection(mode));
            }

            public bool TryGetExistence(FileSystemViewMode mode, out PathExistence existence)
            {
                var maybeExistence = GetExistence(mode);
                existence = maybeExistence ?? PathExistence.Nonexistent;
                return maybeExistence != null;
            }

            private static BitVector32.Section GetFileSystemModeSection(FileSystemViewMode mode)
            {
                switch (mode)
                {
                    case FileSystemViewMode.Real:
                        return RealExistenceSection;
                    case FileSystemViewMode.Output:
                        return OutputGraphExistenceSection;
                    case FileSystemViewMode.FullGraph:
                        return FullGraphExistenceSection;
                    default:
                        throw Contract.AssertFailure(I($"Unexpected file system mode: {mode}"));
                }
            }

            public FileSystemEntry SetFlag(FileSystemEntryFlags flags)
            {
                if (HasFlag(flags))
                {
                    return this;
                }

                var value = m_value;
                value[FlagsSection] = ((int)flags | value[FlagsSection]);
                return new FileSystemEntry(value);
            }

            internal bool HasFlag(FileSystemEntryFlags flags)
            {
                return (Flags & flags) == flags;
            }

            public FileSystemEntry TryUpdate(FileSystemViewMode mode, PathExistence existence)
            {
                if (GetExistence(mode) != null)
                {
                    return this;
                }

                var value = m_value;
                value[GetFileSystemModeSection(mode)] = (int)(existence + 1);
                return new FileSystemEntry(value);
            }

            private PathExistence? GetPathExistence(BitVector32.Section section)
            {
                var value = m_value[section];
                if (value == 0)
                {
                    return null;
                }

                return (PathExistence)(value - 1);
            }

            public static FileSystemEntry Deserialize(BuildXLReader reader)
            {
                var data = reader.ReadInt32Compact();
                return new FileSystemEntry(new BitVector32(data));
            }

            public void Serialize(BuildXLWriter writer)
            {
                var value = m_value;

                // Clear values which should not be persisted
                value[RealExistenceSection] = 0;
                value[FlagsSection] = 0;

                writer.WriteCompact(value.Data);
            }
        }
    }
}
