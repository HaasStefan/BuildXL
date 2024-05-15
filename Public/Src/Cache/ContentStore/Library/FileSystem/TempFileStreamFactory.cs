// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Utilities.Core;

namespace BuildXL.Cache.ContentStore.FileSystem
{
    /// <summary>
    ///     Generator of temp file streams that automatically remove their backing file on disposal.
    /// </summary>
    public sealed class TempFileStreamFactory : IDisposable
    {
        private static readonly Tracer _tracer = new Tracer(nameof(TempFileStreamFactory));
        private readonly IAbsFileSystem _fileSystem;
        private readonly DisposableDirectory _directory;

        /// <summary>
        ///     Initializes a new instance of the <see cref="TempFileStreamFactory"/> class.
        /// </summary>
        public TempFileStreamFactory(IAbsFileSystem fileSystem)
        {
            Contract.Requires(fileSystem != null);

            _fileSystem = fileSystem;
            _directory = new DisposableDirectory(_fileSystem);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _directory.Dispose();
        }

        /// <summary>
        ///     Create a new instance from content in the given input stream.
        /// </summary>
        public async Task<FileStream> CreateAsync(OperationContext context, Stream stream, long? size = null)
        {
            Contract.RequiresNotNull(stream);
            var path = _directory.CreateRandomFileName();

            try
            {
                using (var fileStream = _fileSystem.TryOpenForWrite(
                    path,
                    size,
                    FileMode.Create,
                    FileShare.None,
                    FileOptions.SequentialScan | FileOptions.Asynchronous,
                    AbsFileSystemExtension.DefaultFileStreamBufferSize))
                {
                    Contract.AssertNotNull(fileStream);
                    await stream.CopyToAsync(fileStream.Value.Stream, AbsFileSystemExtension.DefaultFileStreamBufferSize, context.Token);
                }

                // Please note, this temporary file will be deleted once it's closed (i.e., even if the program
                // crashes!)
                return new FileStream(
                    path.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    AbsFileSystemExtension.DefaultFileStreamBufferSize,
                    FileOptions.DeleteOnClose);
            }
            catch (Exception exception)
            {
                _tracer.Error(context, $"Failed to create temp file stream for path=[{path}] due to error=[{exception.GetLogEventMessage()}]");

                try
                {
                    _fileSystem.DeleteFile(path);
                }
                catch (Exception e)
                {
                    _tracer.Error(context, $"Failed to delete temp file at path=[{path}] due to error=[{e.GetLogEventMessage()}]");
                }

                throw;
            }
        }
    }
}
