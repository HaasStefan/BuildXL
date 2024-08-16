// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Sessions;
using BuildXL.Cache.ContentStore.Synchronization;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;

namespace BuildXL.Cache.ContentStore.Stores
{
    /// <summary>
    ///     An <see cref="IContentStore"/> implemented over <see cref="FileSystemContentStoreInternal"/>
    /// </summary>
    public class FileSystemContentStore : StartupShutdownBase, IContentStore, IAcquireDirectoryLock, ILocalContentStore, IStreamStore, IPushFileHandler
    {
        private const string Component = nameof(FileSystemContentStore);

        /// <nodoc />
        public override bool AllowMultipleStartupAndShutdowns => true;

        private readonly DirectoryLock _directoryLock;
        private readonly ContentStoreTracer _tracer = new(nameof(FileSystemContentStore));

        /// <summary>
        ///     Gets the underlying store implementation.
        /// </summary>
        public readonly FileSystemContentStoreInternal Store;

        /// <inheritdoc />
        protected override Tracer Tracer => _tracer;

        /// <summary>
        ///     Initializes a new instance of the <see cref="FileSystemContentStore" /> class.
        /// </summary>
        public FileSystemContentStore(
            IAbsFileSystem fileSystem,
            IClock clock,
            AbsolutePath rootPath,
            ConfigurationModel? configurationModel = null,
            IDistributedLocationStore? distributedStore = null,
            ContentStoreSettings? settings = null)
        {
            Contract.Requires(fileSystem != null);
            Contract.Requires(clock != null);
            Contract.Requires(rootPath != null);

            int singleInstanceTimeoutSeconds = ContentStoreConfiguration.DefaultSingleInstanceTimeoutSeconds;
            if (configurationModel?.InProcessConfiguration != null)
            {
                // TODO: Stop using the configurationModel's SingleInstanceTimeout (bug 1365340)
                // because FileSystemContentStore doesn't respect the config file's value
                singleInstanceTimeoutSeconds = configurationModel.InProcessConfiguration.SingleInstanceTimeoutSeconds;
            }

            // FileSystemContentStore implicitly uses a null component name for compatibility with older versions' directory locks.
            _directoryLock = new DirectoryLock(rootPath, fileSystem, TimeSpan.FromSeconds(singleInstanceTimeoutSeconds));

            Store = new FileSystemContentStoreInternal(
                fileSystem,
                clock,
                rootPath,
                configurationModel,
                settings,
                distributedStore);
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> StartupCoreAsync(OperationContext context)
        {
            BoolResult result;

            var acquireLockResult = await AcquireDirectoryLockAsync(context);
            if (acquireLockResult.Succeeded)
            {
                result = await Store.StartupAsync(context);
            }
            else
            {
                result = acquireLockResult;
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<BoolResult> AcquireDirectoryLockAsync(Context context)
        {
            var aquisitingResult = await _directoryLock.AcquireAsync(context);
            if (aquisitingResult.LockAcquired)
            {
                return BoolResult.Success;
            }

            var errorMessage = aquisitingResult.GetErrorMessage(Component);
            return new BoolResult(errorMessage);
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> ShutdownCoreAsync(OperationContext context)
        {
            var result = await Store.ShutdownAsync(context);
            _directoryLock.Dispose();

            Store.PostShutdown(context).IgnoreFailure();

            return result;
        }

        /// <inheritdoc />
        protected override void DisposeCore()
        {
            Store.Dispose();
            _directoryLock.Dispose();
        }

        /// <inheritdoc />
        public virtual CreateSessionResult<IContentSession> CreateSession(Context context, string name, ImplicitPin implicitPin)
        {
            return CreateSessionCall.Run(_tracer, OperationContext(context), name, () =>
            {
                var session = new FileSystemContentSession(name, Store, implicitPin);
                return new CreateSessionResult<IContentSession>(session);
            });
        }

        /// <inheritdoc />
        public Task<GetStatsResult> GetStatsAsync(Context context)
        {
            return GetStatsCall<ContentStoreTracer>.RunAsync(_tracer, OperationContext(context), () => Store.GetStatsAsync(context));
        }

        /// <inheritdoc />
        public async Task<IEnumerable<ContentInfo>> GetContentInfoAsync(CancellationToken token)
        {
            // TODO: add cancellation support for EnumerateContentInfoAsync
            return await Store.EnumerateContentInfoAsync();
        }

        /// <inheritdoc />
        public bool Contains(ContentHash hash)
        {
            return Store.Contains(hash);
        }

        /// <inheritdoc />
        public bool TryGetContentInfo(ContentHash hash, out ContentInfo info)
        {
            if (Store.TryGetFileInfo(hash, out var fileInfo))
            {
                info = new ContentInfo(hash, fileInfo.LogicalFileSize, fileInfo.LastAccessedTimeUtc);
                return true;
            }
            else
            {
                info = default;
                return false;
            }
        }

        /// <inheritdoc />
        public void UpdateLastAccessTimeIfNewer(ContentHash hash, DateTime newLastAccessTime)
        {
            if (Store.TryGetFileInfo(hash, out var fileInfo))
            {
                fileInfo.UpdateLastAccessed(newLastAccessTime);
            }
        }

        /// <inheritdoc />
        public Task<OpenStreamResult> StreamContentAsync(Context context, ContentHash contentHash)
        {
            return Store.OpenStreamAsync(context, contentHash, pinRequest: null);
        }

        /// <inheritdoc />
        public Task<DeleteResult> DeleteAsync(Context context, ContentHash contentHash, DeleteContentOptions? deleteOptions = null)
        { 
            return Store.DeleteAsync(context, contentHash, deleteOptions);
        }

        /// <inheritdoc />
        public void PostInitializationCompleted(Context context) { }

        /// <inheritdoc />
        public Task<PutResult> HandlePushFileAsync(Context context, ContentHash hash, FileSource source, CancellationToken token)
        {
            if (source.Path != null)
            {
                // TODO(jubayard): this can be optimized to move in some cases (i.e. GrpcContentServer creates a file just
                // for this, no need to copy it)
                return Store.PutFileAsync(context, source.Path, source.FileRealizationMode, hash, pinRequest: null);
            }
            else
            {
                return Store.PutStreamAsync(context, source.Stream!, hash, pinRequest: null);
            }
        }

        /// <inheritdoc />
        public bool CanAcceptContent(Context context, ContentHash hash, out RejectionReason rejectionReason)
        {
            if (Store.Contains(hash))
            {
                rejectionReason = RejectionReason.ContentAvailableLocally;
                return false;
            }

            rejectionReason = RejectionReason.Accepted;
            return true;
        }
    }
}
