// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Text;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Engine.Cache;
using BuildXL.Engine.Cache.Fingerprints;
using BuildXL.Native.IO;
using BuildXL.Pips;
using BuildXL.Processes;
using BuildXL.ProcessPipExecutor;
using BuildXL.Scheduler.Fingerprints;
using BuildXL.Storage;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;

namespace BuildXL.Scheduler.Distribution
{
    /// <summary>
    /// Handles serialization of process execution results for distributed builds
    /// </summary>
    internal sealed class ExecutionResultSerializer
    {
        private readonly int m_maxSerializableAbsolutePathIndex;
        private readonly PipExecutionContext m_executionContext;

        private readonly Func<BuildXLReader, AbsolutePath> m_readPath;
        private readonly Action<BuildXLWriter, AbsolutePath> m_writePath;

        #region Reader Pool

        private readonly ObjectPool<BuildXLReader> m_readerPool = new ObjectPool<BuildXLReader>(CreateReader, (Action<BuildXLReader>)CleanupReader);

        private static void CleanupReader(BuildXLReader reader)
        {
            reader.BaseStream.SetLength(0);
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:DisposeObjectsBeforeLosingScope", Justification = "Disposal is not needed for memory stream")]
        private static BuildXLReader CreateReader()
        {
            return new BuildXLReader(
                debug: false,
                stream: new MemoryStream(),
                leaveOpen: false);
        }

        #endregion Reader Pool

        private static readonly ObjectPool<Dictionary<ReportedProcess, int>> s_reportedProcessMapPool = new ObjectPool<Dictionary<ReportedProcess, int>>(
            creator: () => new Dictionary<ReportedProcess, int>(),
            cleanup: d => d.Clear());

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="maxSerializableAbsolutePathIndex">the minimum absolute path that can be shared amongst participants in a build</param>
        /// <param name="executionContext">the execution context</param>
        public ExecutionResultSerializer(int maxSerializableAbsolutePathIndex, PipExecutionContext executionContext)
        {
            m_maxSerializableAbsolutePathIndex = maxSerializableAbsolutePathIndex;
            m_executionContext = executionContext;

            m_readPath = ReadAbsolutePath;
            m_writePath = WriteAbsolutePath;
        }

        /// <summary>
        /// Deserialize result from the blob data
        /// </summary>
        public ExecutionResult DeserializeFromBlob(ReadOnlySpan<byte> blobData, uint workerId)
        {
            using (var pooledReader = m_readerPool.GetInstance())
            {
                var reader = pooledReader.Instance;
#if NETCOREAPP
                reader.BaseStream.Write(blobData);
#else
                reader.BaseStream.Write(blobData.ToArray(), 0, blobData.Length);
#endif
                reader.BaseStream.Position = 0;

                return Deserialize(reader, workerId);
            }
        }

        /// <summary>
        /// Deserialize result from reader
        /// </summary>
        public ExecutionResult Deserialize(BuildXLReader reader, uint workerId)
        {
            int minAbsolutePathValue = reader.ReadInt32();
            Contract.Assert(
                minAbsolutePathValue == m_maxSerializableAbsolutePathIndex,
                "When deserializing for distribution, the minimum absolute path value must match the serialized minimum absolute path value");

            var result = (PipResultStatus)reader.ReadByte();
            var converged = reader.ReadBoolean();
            var numberOfWarnings = reader.ReadInt32Compact();
            var weakFingerprint = reader.ReadNullableStruct(r => r.ReadWeakFingerprint());
            ProcessPipExecutionPerformance performanceInformation;

            if (reader.ReadBoolean())
            {
                performanceInformation = ProcessPipExecutionPerformance.Deserialize(reader);

                // TODO: It looks like this is the wrong class for WorkerId, because the serialized object has always WorkerId == 0.
                performanceInformation.WorkerId = workerId;
            }
            else
            {
                performanceInformation = null;
            }

            var outputContent = ReadOnlyArray<(FileArtifact, FileMaterializationInfo, PipOutputOrigin)>.FromWithoutCopy(ReadOutputContent(reader));
            var directoryOutputs = ReadOnlyArray<(DirectoryArtifact, ReadOnlyArray<FileArtifactWithAttributes>)>.FromWithoutCopy(ReadDirectoryOutputs(reader));
            var mustBeConsideredPerpetuallyDirty = reader.ReadBoolean();
            var dynamicObservations = reader.ReadReadOnlyArray(ReadDynamicObservation);
            var allowedUndeclaredSourceReads = reader.ReadArray(ReadAllowedUndeclaredRead).ToDictionary(kvp => kvp.Item1, kvp => kvp.Item2);

            ReportedFileAccess[] fileAccessViolationsNotAllowlisted;
            ReportedFileAccess[] allowlistedFileAccessViolations;
            ReportedProcess[] reportedProcesses;
            ReadReportedProcessesAndFileAccesses(reader, out fileAccessViolationsNotAllowlisted, out allowlistedFileAccessViolations, out reportedProcesses, readPath: m_readPath);

            var twoPhaseCachingInfo = ReadTwoPhaseCachingInfo(reader);
            var cacheDescriptor = ReadPipCacheDescriptor(reader);

            PipCachePerfInfo cacheLookupCounters = null;
            if (reader.ReadBoolean())
            {
                cacheLookupCounters = PipCachePerfInfo.Deserialize(reader);
            }

            if (!result.IndicatesNoOutput())
            {
                // Successful result needs to be changed to not materialized because
                // the process outputs are not materialized on the local machine.
                result = PipResultStatus.NotMaterialized;
            }

            var pipProperties = ReadPipProperties(reader);
            var hasUserRetries = reader.ReadBoolean();
            var exitCode = reader.ReadInt32Compact();

            RetryInfo pipRetryInfo = null;
            if (reader.ReadBoolean())
            {
                pipRetryInfo = RetryInfo.Deserialize(reader);
            }

            var createdDirectories = reader.ReadReadOnlySet(ReadAbsolutePath);

            PipCacheMissType? cacheMissType = null;
            if (reader.ReadBoolean())
            {
                cacheMissType = (PipCacheMissType)reader.ReadByte();
            }

            var processExecutionResult = ExecutionResult.CreateSealed(
                result,
                numberOfWarnings,
                outputContent,
                directoryOutputs,
                performanceInformation,
                weakFingerprint,
                fileAccessViolationsNotAllowlisted,
                allowlistedFileAccessViolations,
                mustBeConsideredPerpetuallyDirty,
                dynamicObservations,
                allowedUndeclaredSourceReads,
                twoPhaseCachingInfo,
                cacheDescriptor,
                converged,
                cacheLookupCounters,
                pipProperties,
                hasUserRetries,
                exitCode,
                createdDirectories,
                cacheMissType,
                pipRetryInfo);

            return processExecutionResult;
        }

        /// <summary>
        /// Serialize result to writer
        /// </summary>
        public void Serialize(BuildXLWriter writer, ExecutionResult result, bool preservePathCasing)
        {
            writer.Write(m_maxSerializableAbsolutePathIndex);

            writer.Write((byte)result.Result);
            writer.Write(result.Converged);
            writer.WriteCompact(result.NumberOfWarnings);
            writer.Write(result.WeakFingerprint, (w, weak) => w.Write(weak));

            var performanceInformation = result.PerformanceInformation;

            if (performanceInformation != null)
            {
                writer.Write(true);
                performanceInformation.Serialize(writer);
            }
            else
            {
                writer.Write(false);
            }

            WriteOutputContent(writer, result.OutputContent);
            WriteDirectoryOutputs(writer, result.DirectoryOutputs);
            writer.Write(result.MustBeConsideredPerpetuallyDirty);
            writer.Write(result.DynamicObservations, WriteDynamicObservation);
            writer.Write(result.AllowedUndeclaredReads.ToArray(), WriteAllowedUndeclaredRead);
            WriteReportedProcessesAndFileAccesses(
                writer,
                result.FileAccessViolationsNotAllowlisted,
                result.AllowlistedFileAccessViolations,
                writePath: m_writePath);

            WriteTwoPhaseCachingInfo(writer, result.TwoPhaseCachingInfo);
            WritePipCacheDescriptor(writer, result.PipCacheDescriptorV2Metadata);

            bool sendCacheLookupCounters = result.CacheLookupPerfInfo != null;
            writer.Write(sendCacheLookupCounters);

            if (sendCacheLookupCounters)
            {
                result.CacheLookupPerfInfo.Serialize(writer);
            }

            WritePipProperties(writer, result.PipProperties);
            writer.Write(result.HasUserRetries);
            writer.WriteCompact(result.ExitCode);

            writer.Write(result.RetryInfo, (w, ri) => ri.Serialize(w));
            writer.Write(result.CreatedDirectories, WriteAbsolutePath);

            if (result.CacheMissType.HasValue)
            {
                writer.Write(true);
                writer.Write((byte)result.CacheMissType.Value);
            }
            else
            {
                writer.Write(false);
            }
        }

        private static TwoPhaseCachingInfo ReadTwoPhaseCachingInfo(BuildXLReader reader)
        {
            if (reader.ReadBoolean())
            {
                return TwoPhaseCachingInfo.Deserialize(reader);
            }
            else
            {
                return null;
            }
        }

        private static void WriteTwoPhaseCachingInfo(BuildXLWriter writer, TwoPhaseCachingInfo twoPhaseCachingInfo)
        {
            if (twoPhaseCachingInfo != null)
            {
                writer.Write(true);
                twoPhaseCachingInfo.Serialize(writer);
            }
            else
            {
                writer.Write(false);
            }
        }

        /// <summary>
        /// Serialize metadata to writer
        /// </summary>
        public static void WritePipCacheDescriptor(BuildXLWriter writer, PipCacheDescriptorV2Metadata metadata)
        {
            if (metadata != null)
            {
                writer.Write(true);
                var blob = CacheGrpcExtensions.Serialize(metadata);
                writer.WriteCompact(blob.Count);
                writer.Write(blob.Array, blob.Offset, blob.Count);
            }
            else
            {
                writer.Write(false);
            }
        }

        /// <summary>
        /// Deserialize metadata from reader
        /// </summary>
        public static PipCacheDescriptorV2Metadata ReadPipCacheDescriptor(BuildXLReader reader)
        {
            if (reader.ReadBoolean())
            {
                var length = reader.ReadInt32Compact();
                var blob = new ArraySegment<byte>(reader.ReadBytes(length));
                var possibleResult = CacheGrpcExtensions.Deserialize<PipCacheDescriptorV2Metadata>(blob);
                if (!possibleResult.Succeeded)
                {
                    return null;
                }
                return possibleResult.Result;
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Reads reported processes and file accesses
        /// </summary>
        public static void ReadReportedProcessesAndFileAccesses(
            BuildXLReader reader,
            out ReportedFileAccess[] reportedFileAccesses,
            out ReportedFileAccess[] allowlistedReportedFileAccesses,
            out ReportedProcess[] reportedProcesses,
            Func<BuildXLReader, AbsolutePath> readPath = null)
        {
            readPath = readPath ?? (r => r.ReadAbsolutePath());

            bool hasReportedFileAccessesOrProcesses = reader.ReadBoolean();

            if (!hasReportedFileAccessesOrProcesses)
            {
                reportedProcesses = CollectionUtilities.EmptyArray<ReportedProcess>();
                reportedFileAccesses = CollectionUtilities.EmptyArray<ReportedFileAccess>();
                allowlistedReportedFileAccesses = CollectionUtilities.EmptyArray<ReportedFileAccess>();
            }
            else
            {
                reportedProcesses = ReadReportedProcesses(reader);

                int reportedFileAccessCount = reader.ReadInt32Compact();
                reportedFileAccesses = new ReportedFileAccess[reportedFileAccessCount];
                for (int i = 0; i < reportedFileAccessCount; i++)
                {
                    reportedFileAccesses[i] = ReadReportedFileAccess(reader, reportedProcesses, readPath);
                }

                int allowlistedReportedFileAccessCount = reader.ReadInt32Compact();
                allowlistedReportedFileAccesses = new ReportedFileAccess[allowlistedReportedFileAccessCount];
                for (int i = 0; i < allowlistedReportedFileAccessCount; i++)
                {
                    allowlistedReportedFileAccesses[i] = ReadReportedFileAccess(reader, reportedProcesses, readPath);
                }
            }
        }

        /// <summary>
        /// Writes reported processes and file accesses
        /// </summary>
        public static void WriteReportedProcessesAndFileAccesses(
            BuildXLWriter writer,
            IReadOnlyCollection<ReportedFileAccess> reportedFileAccesses,
            IReadOnlyCollection<ReportedFileAccess> allowlistedReportedFileAccesses,
            IReadOnlyCollection<ReportedProcess> reportedProcesses = null,
            Action<BuildXLWriter, AbsolutePath> writePath = null)
        {
            writePath = writePath ?? ((w, path) => w.Write(path));

            bool hasReportedFileAccessesOrProcesses = (reportedFileAccesses != null && reportedFileAccesses.Count != 0)
                || (reportedProcesses != null && reportedProcesses.Count != 0)
                || (allowlistedReportedFileAccesses != null && allowlistedReportedFileAccesses.Count != 0);

            if (!hasReportedFileAccessesOrProcesses)
            {
                writer.Write(false);
            }
            else
            {
                writer.Write(true);

                reportedFileAccesses = reportedFileAccesses ?? CollectionUtilities.EmptyArray<ReportedFileAccess>();
                allowlistedReportedFileAccesses = allowlistedReportedFileAccesses ?? CollectionUtilities.EmptyArray<ReportedFileAccess>();
                reportedProcesses = reportedProcesses ?? CollectionUtilities.EmptyArray<ReportedProcess>();

                using (var pooledProcessMap = s_reportedProcessMapPool.GetInstance())
                {
                    Dictionary<ReportedProcess, int> processMap = pooledProcessMap.Instance;
                    var allReportedProcesses = reportedProcesses.Concat(reportedFileAccesses.Select(rfa => rfa.Process)).Concat(allowlistedReportedFileAccesses.Select(rfa => rfa.Process));

                    foreach (var reportedProcess in allReportedProcesses)
                    {
                        int index;
                        if (!processMap.TryGetValue(reportedProcess, out index))
                        {
                            index = processMap.Count;
                            processMap[reportedProcess] = index;
                        }
                    }

                    WriteReportedProcesses(writer, processMap);
                    writer.WriteCompact(reportedFileAccesses.Count);
                    foreach (var reportedFileAccess in reportedFileAccesses)
                    {
                        WriteReportedFileAccess(writer, reportedFileAccess, processMap, writePath);
                    }

                    writer.WriteCompact(allowlistedReportedFileAccesses.Count);
                    foreach (var allowlistedReportedFileAccess in allowlistedReportedFileAccesses)
                    {
                        WriteReportedFileAccess(writer, allowlistedReportedFileAccess, processMap, writePath);
                    }
                }
            }
        }

        private static ReportedProcess[] ReadReportedProcesses(BuildXLReader reader)
        {
            int count = reader.ReadInt32Compact();
            ReportedProcess[] processes = new ReportedProcess[count];
            for (int i = 0; i < count; i++)
            {
                processes[i] = ReportedProcess.Deserialize(reader);
            }

            return processes;
        }

        private static void WriteReportedProcesses(BuildXLWriter writer, Dictionary<ReportedProcess, int> processMap)
        {
            writer.WriteCompact(processMap.Count);
            ReportedProcess[] processes = new ReportedProcess[processMap.Count];
            foreach (var process in processMap)
            {
                processes[process.Value] = process.Key;
            }

            for (int i = 0; i < processes.Length; i++)
            {
                processes[i].Serialize(writer);
            }
        }

        private static ReportedFileAccess ReadReportedFileAccess(BuildXLReader reader, ReportedProcess[] processes, Func<BuildXLReader, AbsolutePath> readPath)
        {
            return ReportedFileAccess.Deserialize(reader, processes, readPath);
        }

        private static void WriteReportedFileAccess(
            BuildXLWriter writer,
            ReportedFileAccess reportedFileAccess,
            Dictionary<ReportedProcess, int> processMap,
            Action<BuildXLWriter, AbsolutePath> writePath)
        {
            reportedFileAccess.Serialize(writer, processMap, writePath);
        }

        private static string ReadNullableString(BuildXLReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            return reader.ReadString();
        }

        private static void WriteNullableString(BuildXLWriter writer, string value)
        {
            writer.Write(value != null);
            if (value != null)
            {
                writer.Write(value);
            }
        }

        private (FileArtifact, FileMaterializationInfo, PipOutputOrigin)[] ReadOutputContent(BuildXLReader reader)
        {
            int count = reader.ReadInt32Compact();

            (FileArtifact, FileMaterializationInfo, PipOutputOrigin)[] outputContent;

            if (count != 0)
            {
                outputContent = new (FileArtifact, FileMaterializationInfo, PipOutputOrigin)[count];
                for (int i = 0; i < count; i++)
                {
                    var file = ReadFileArtifact(reader);
                    var length = reader.ReadInt64Compact();
                    var hashBytes = reader.ReadBytes(ContentHashingUtilities.HashInfo.ByteLength);
                    var hash = ContentHashingUtilities.CreateFrom(hashBytes);
                    var fileName = ReadPathAtom(reader);
                    var reparsePointType = (ReparsePointType)reader.ReadByte();
                    var reparsePointTarget = ReadNullableString(reader);
                    var isAllowedFileRewrite = reader.ReadBoolean();
                    var isExecutable = reader.ReadBoolean();
                    var opaqueDirectoryRoot = ReadAbsolutePath(reader);
                    var relativePathList = reader.ReadNullableReadOnlyList((reader) => reader.ReadString());

                    var relativePath = relativePathList == null 
                        ? RelativePath.Invalid 
                        : RelativePath.Create(relativePathList.Select(atom => PathAtom.Create(m_executionContext.StringTable, atom)).ToArray());

                    outputContent[i] = (
                        file,
                        new FileMaterializationInfo(
                            new FileContentInfo(hash, FileContentInfo.LengthAndExistence.Deserialize(length)), 
                            fileName, 
                            opaqueDirectoryRoot, 
                            relativePath, 
                            ReparsePointInfo.Create(reparsePointType, reparsePointTarget), 
                            isAllowedFileRewrite, 
                            isExecutable),
                        PipOutputOrigin.NotMaterialized);
                }
            }
            else
            {
                outputContent = CollectionUtilities.EmptyArray<(FileArtifact, FileMaterializationInfo, PipOutputOrigin)>();
            }

            return outputContent;
        }

        private void WriteOutputContent(BuildXLWriter writer, IReadOnlyList<(FileArtifact fileArtifact, FileMaterializationInfo fileMaterializationInfo, PipOutputOrigin pipOutputOrgin)> outputContent)
        {
            int count = outputContent.Count;
            writer.WriteCompact(count);
            using (var pooledByteArray = Pools.GetByteArray(ContentHashingUtilities.HashInfo.ByteLength))
            {
                var byteBuffer = pooledByteArray.Instance;
                for (int i = 0; i < count; i++)
                {
                    var output = outputContent[i];
                    WriteFileArtifact(writer, output.fileArtifact);
                    writer.WriteCompact(output.fileMaterializationInfo.FileContentInfo.SerializedLengthAndExistence);
                    output.Item2.Hash.SerializeHashBytes(byteBuffer, 0);
                    writer.Write(byteBuffer, 0, ContentHashingUtilities.HashInfo.ByteLength);
                    WritePathAtom(writer, output.fileMaterializationInfo.FileName);
                    writer.Write((byte)output.fileMaterializationInfo.ReparsePointInfo.ReparsePointType);
                    WriteNullableString(writer, output.fileMaterializationInfo.ReparsePointInfo.GetReparsePointTarget());
                    writer.Write(output.fileMaterializationInfo.IsUndeclaredFileRewrite);
                    writer.Write(output.fileMaterializationInfo.IsExecutable);
                    WriteAbsolutePath(writer, output.fileMaterializationInfo.OpaqueDirectoryRoot);

                    // Do not send the path atoms id directly as this belongs to dynamic outputs and string tables may differ
                    IReadOnlyList<string> relativePath = output.fileMaterializationInfo.DynamicOutputCaseSensitiveRelativeDirectory.IsValid
                        ? output.fileMaterializationInfo.DynamicOutputCaseSensitiveRelativeDirectory.GetAtoms().Select(atom => atom.ToString(m_executionContext.StringTable)).ToList()
                        : null;

                    writer.WriteNullableReadOnlyList(relativePath, (writer, atom) => writer.Write(atom));
                }
            }
        }

        private (DirectoryArtifact, ReadOnlyArray<FileArtifactWithAttributes>)[] ReadDirectoryOutputs(BuildXLReader reader)
        {
            int count = reader.ReadInt32Compact();
            (DirectoryArtifact, ReadOnlyArray<FileArtifactWithAttributes>)[] directoryOutputs = count > 0
                ? new (DirectoryArtifact, ReadOnlyArray<FileArtifactWithAttributes>)[count]
                : CollectionUtilities.EmptyArray<(DirectoryArtifact, ReadOnlyArray<FileArtifactWithAttributes>)>();

            for (int i = 0; i < count; ++i)
            {
                var directory = reader.ReadDirectoryArtifact();
                var length = reader.ReadInt32Compact();
                var members = length > 0 ? new FileArtifactWithAttributes[length] : CollectionUtilities.EmptyArray<FileArtifactWithAttributes>();

                for (int j = 0; j < length; ++j)
                {
                    members[j] = ReadFileArtifactWithAttributes(reader);
                }

                directoryOutputs[i] = (directory, ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(members));
            }

            return directoryOutputs;
        }

        private void WriteDirectoryOutputs(
            BuildXLWriter writer,
            IReadOnlyList<(DirectoryArtifact directoryArtifact, ReadOnlyArray<FileArtifactWithAttributes> fileArtifactArray)> directoryOutputs)
        {
            writer.WriteCompact(directoryOutputs.Count);
            foreach (var directoryOutput in directoryOutputs)
            {
                writer.Write(directoryOutput.directoryArtifact);
                writer.WriteCompact(directoryOutput.fileArtifactArray.Length);
                foreach (var member in directoryOutput.fileArtifactArray)
                {
                    WriteFileArtifactWithAttributes(writer, member);
                }
            }
        }

        [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "To be used when implementing arbitrary path serialization.")]
        private Tuple<AbsolutePath, Encoding> ReadPathAndEncoding(BuildXLReader reader)
        {
            return Tuple.Create(ReadAbsolutePath(reader), Encoding.GetEncoding(reader.ReadInt32()));
        }

        [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "To be used when implementing arbitrary path serialization.")]
        private Tuple<AbsolutePath, Encoding> WritePathAndEncoding(BuildXLReader reader)
        {
            return Tuple.Create(ReadAbsolutePath(reader), Encoding.GetEncoding(reader.ReadInt32()));
        }

        private PathAtom ReadPathAtom(BuildXLReader reader)
        {
            string pathAtomString = reader.ReadString();
            return string.IsNullOrEmpty(pathAtomString) ?
                PathAtom.Invalid :
                PathAtom.Create(m_executionContext.StringTable, pathAtomString);
        }

        private void WritePathAtom(BuildXLWriter writer, PathAtom pathAtom)
        {
            writer.Write(pathAtom.IsValid ?
                pathAtom.ToString(m_executionContext.StringTable) :
                string.Empty);
        }

        [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "To be used when implementing arbitrary path serialization.")]
        private AbsolutePath ReadAbsolutePath(BuildXLReader reader)
        {
            if (reader.ReadBoolean())
            {
                return reader.ReadAbsolutePath();
            }
            else
            {
                return AbsolutePath.Create(m_executionContext.PathTable, reader.ReadString());
            }
        }

        [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "To be used when implementing arbitrary path serialization.")]
        private void WriteAbsolutePath(BuildXLWriter writer, AbsolutePath path)
        {
            if (path.Value.Index <= m_maxSerializableAbsolutePathIndex)
            {
                writer.Write(true);
                writer.Write(path);
            }
            else
            {
                writer.Write(false);
                writer.Write(path.ToString(m_executionContext.PathTable));
            }
        }

        [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "To be used when implementing arbitrary path serialization.")]
        private (AbsolutePath, DynamicObservationKind) ReadDynamicObservation(BuildXLReader reader)
        {
            AbsolutePath path = ReadAbsolutePath(reader);
            return (path, (DynamicObservationKind)reader.ReadInt32());
        }

        [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "To be used when implementing arbitrary path serialization.")]
        private (AbsolutePath, ObservedInputType) ReadAllowedUndeclaredRead(BuildXLReader reader)
        {
            AbsolutePath path = ReadAbsolutePath(reader);
            return (path, (ObservedInputType)reader.ReadInt32());
        }

        [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "To be used when implementing arbitrary path serialization.")]
        private void WriteDynamicObservation(BuildXLWriter writer, (AbsolutePath Path, DynamicObservationKind Kind) observation)
        {
            WriteAbsolutePath(writer, observation.Path);
            writer.Write((int)observation.Kind);
        }

        [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "To be used when implementing arbitrary path serialization.")]
        private void WriteAllowedUndeclaredRead(BuildXLWriter writer, KeyValuePair<AbsolutePath, ObservedInputType> observation)
        {
            WriteAbsolutePath(writer, observation.Key);
            writer.Write((int)observation.Value);
        }

        private FileArtifact ReadFileArtifact(BuildXLReader reader)
        {
            var path = ReadAbsolutePath(reader);
            var rewriteCount = reader.ReadInt32Compact();
            return new FileArtifact(path, rewriteCount);
        }

        private FileArtifactWithAttributes ReadFileArtifactWithAttributes(BuildXLReader reader)
        {
            var path = ReadAbsolutePath(reader);
            var rewriteCountAndFileExistenceAndFileRewrite = reader.ReadUInt32();
            return new FileArtifactWithAttributes(path, rewriteCountAndFileExistenceAndFileRewrite);
        }

        private void WriteFileArtifact(BuildXLWriter writer, FileArtifact file)
        {
            WriteAbsolutePath(writer, file.Path);
            writer.WriteCompact(file.RewriteCount);
        }

        private void WriteFileArtifactWithAttributes(BuildXLWriter writer, FileArtifactWithAttributes file)
        {
            WriteAbsolutePath(writer, file.Path);
            writer.Write(file.RewriteCountAndFileExistenceAndFileRewrite);
        }

        private static IReadOnlyDictionary<string, int> ReadPipProperties(BuildXLReader reader)
        {
            bool hasPipProperties = reader.ReadBoolean();

            if (!hasPipProperties)
            {
                return null;
            }
            else
            {
                int count = reader.ReadInt32Compact();

                var pipProperties = new Dictionary<string, int>();

                for (int i = 0; i < count; i++)
                {
                    string key = reader.ReadString();
                    pipProperties[key] = reader.ReadInt32Compact();
                }

                return pipProperties;
            }
        }

        private static void WritePipProperties(BuildXLWriter writer, IReadOnlyDictionary<string, int> pipProperties)
        {
            bool hasPipProperties = pipProperties != null && pipProperties.Count != 0;

            if (!hasPipProperties)
            {
                writer.Write(false);
            }
            else
            {
                writer.Write(true);

                writer.WriteCompact(pipProperties.Count);

                foreach (string key in pipProperties.Keys)
                {
                    writer.Write(key);
                    writer.WriteCompact(pipProperties[key]);
                }
            }
        }
    }
}
