// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BuildXL.Utilities.Instrumentation.Common;

#pragma warning disable 1591
#nullable enable

namespace BuildXL.Engine.Cache.Tracing
{
    /// <summary>
    /// Logging
    /// </summary>
    [EventKeywordsType(typeof(Keywords))]
    [EventTasksType(typeof(Tasks))]
    [LoggingDetails("EngineCacheLogger")]

    internal abstract partial class Logger
    {
        /// <summary>
        /// Returns the logger instance
        /// </summary>
        public static Logger Log => m_log;

        /*
        [GeneratedEvent(
            (int)LogEventId.StorageCacheCopyLocalError,
            EventGenerators = EventGenerators.LocalOnly,
            EventLevel = Level.Warning,
            Keywords = (int)Keywords.UserMessage,
            EventTask = (int)Tasks.Storage,
            Message = "While bringing {0} local, the cache reported error: {1}")]
        public abstract void StorageCacheCopyLocalError(LoggingContext context, string contentHash, string errorMessage);
        */
        [GeneratedEvent(
            (int)LogEventId.StorageFailureToOpenFileForFlushOnIngress,
            EventGenerators = EventGenerators.LocalOnly,
            EventLevel = Level.Verbose,
            Keywords = (int)Keywords.UserMessage,
            EventTask = (int)Tasks.Storage,
            Message = "The path '{0}' could not be opened to be flushed (in preparation for cache-ingress). This file may subsequently be treated as out-of-date. Open failure: {1}")]
        public abstract void StorageFailureToOpenFileForFlushOnIngress(LoggingContext context, string path, string errorMessage);

        [GeneratedEvent(
            (int)LogEventId.StorageFailureToFlushFileOnDisk,
            EventGenerators = EventGenerators.LocalOnly,
            EventLevel = Level.Verbose,
            Keywords = (int)Keywords.UserMessage,
            EventTask = (int)Tasks.Storage,
            Message = "The path '{0}' could not be flushed (in preparation for cache-ingress). NtStatus code: {1}")]
        public abstract void StorageFailureToFlushFileOnDisk(LoggingContext context, string path, string errorCode);

        [GeneratedEvent(
            (int)LogEventId.ClosingFileStreamAfterHashingFailed,
            EventGenerators = EventGenerators.LocalOnly,
            EventLevel = Level.Warning,
            Keywords = (int)Keywords.UserMessage,
            EventTask = (int)Tasks.Storage,
            Message = "Closing the stream to the path '{0}' thrown a native ERROR_INCORRECT_FUNCTION exception. exception message: {1}. NtStatus code: {2}.")]
        public abstract void ClosingFileStreamAfterHashingFailed(LoggingContext context, string path, string message, string errorCode);

        [GeneratedEvent(
            (int)LogEventId.FailedOpenHandleToGetKnownHashDuringMaterialization,
            EventGenerators = EventGenerators.LocalOnly,
            EventLevel = Level.Verbose,
            Keywords = (int)Keywords.UserMessage,
            EventTask = (int)Tasks.Storage,
            Message = "The path '{0}' could not be opened to get known hash during materialization: {1}")]
        public abstract void FailedOpenHandleToGetKnownHashDuringMaterialization(LoggingContext context, string path, string message);

        [GeneratedEvent(
            (int)LogEventId.OpeningFileFailedForHashing,
            EventGenerators = EventGenerators.LocalOnly,
            EventLevel = Level.Verbose,
            Keywords = (int)Keywords.UserMessage,
            EventTask = (int)Tasks.Storage,
            Message = "The path '{0}' could not be opened for hashing because the filesystem returned {error}.")]
        public abstract void OpeningFileFailedForHashing(LoggingContext context, string path, string error);


        [GeneratedEvent(
            (int)LogEventId.HashedReparsePointAsTargetPath,
            EventGenerators = EventGenerators.LocalOnly,
            EventLevel = Level.Verbose,
            Keywords = (int)Keywords.Diagnostics,
            EventTask = (int)Tasks.Storage,
            Message = "The path '{0}' was a reparse point, and it will be hashed based on its target's path, i.e., '{1}', rather than the target's content.")]
        public abstract void HashedReparsePointAsTargetPath(LoggingContext context, string path, string targetPath);

        [GeneratedEvent(
            (int)LogEventId.TemporalCacheEntryTrace,
            EventGenerators = EventGenerators.LocalOnly,
            EventLevel = Level.Verbose,
            Keywords = (int)Keywords.UserMessage,
            EventTask = (int)Tasks.Storage,
            Message = "{message}")]
        public abstract void TemporalCacheEntryTrace(LoggingContext context, string message);

        [GeneratedEvent(
            (ushort)LogEventId.SerializingToPipFingerprintEntryResultInCorruptedData,
            EventGenerators = EventGenerators.LocalOnly,
            EventLevel = Level.Verbose,
            Keywords = (int)Keywords.UserMessage,
            EventTask = (ushort)Tasks.Storage,
            Message = "Serializing to pip fingerprint entry results in corrupted data: Kind: {kind} | Data blob: {blob}")]
        internal abstract void SerializingToPipFingerprintEntryResultInCorruptedData(LoggingContext loggingContext, string kind, string blob);

        [GeneratedEvent(
            (ushort)LogEventId.DeserializingCorruptedPipFingerprintEntry,
            EventGenerators = EventGenerators.LocalOnly,
            EventLevel = Level.Verbose,
            Keywords = (int)Keywords.UserMessage,
            EventTask = (ushort)Tasks.Storage,
            Message = "Deserializing corrupted pip fingerprint entry: \r\n\t Kind: {kind}\r\n\t Weak fingerprint: {weakFingerprint}\r\n\t Path set hash: {pathSetHash}\r\n\t Strong fingerprint: {strongFingerprint}\r\n\t Expected pip fingerprint entry hash: {expectedHash}\r\n\t Re-computed pip fingerprint entry hash: {hash}\r\n\t Data blob: {blob}\r\n\t Actual pip fingerprint entry hash: {actualHash}\r\n\t Actual pip fingerprint entry blob: {actualEntryBlob}")]
        internal abstract void DeserializingCorruptedPipFingerprintEntry(LoggingContext loggingContext, string kind, string weakFingerprint, string pathSetHash, string strongFingerprint, string expectedHash, string hash, string blob, string actualHash, string actualEntryBlob);

        [GeneratedEvent(
            (ushort)LogEventId.RetryOnLoadingAndDeserializingMetadata,
            EventGenerators = EventGenerators.LocalOnly,
            EventLevel = Level.Verbose,
            Keywords = (int)Keywords.UserMessage,
            EventTask = (ushort)Tasks.Storage,
            Message = "Retry on loading and deserializing metadata: Succeeded: {succeeded} | Retry count: {retryCount}")]
        internal abstract void RetryOnLoadingAndDeserializingMetadata(LoggingContext loggingContext, bool succeeded, int retryCount);
    }
}
