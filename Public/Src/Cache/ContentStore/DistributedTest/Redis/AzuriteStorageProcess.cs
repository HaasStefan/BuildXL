// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using BuildXL.Cache.ContentStore.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Auth;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Utils;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Native.IO;
using ContentStoreTest.Extensions;

namespace ContentStoreTest.Distributed.Redis
{
    /// <summary>
    /// Wrapper around local storage instance.
    /// </summary>
    public sealed class AzuriteStorageProcess : IDisposable
    {
        private DisposableDirectory _tempDirectory;
        private PassThroughFileSystem _fileSystem;
        private ILogger _logger;

        private ProcessUtility _process;
        public string ConnectionString { get; private set; }

        private bool _disposed;
        private LocalRedisFixture _storageFixture;
        private int _portNumber;

        internal bool Closed { get; private set; }

        internal bool Initialized => _fileSystem != null;

        internal AzuriteStorageProcess()
        {
        }

        private void Init(ILogger logger, LocalRedisFixture storageFixture)
        {
            _fileSystem = new PassThroughFileSystem(logger);
            _logger = logger;
            _tempDirectory = new DisposableDirectory(_fileSystem);
            _storageFixture = storageFixture;
            _disposed = false;

            // The instance is re-initialized, so we need to re-register it for finalization to detect resource leaks.
            GC.ReRegisterForFinalize(this);
        }

        public override string ToString()
        {
            return ConnectionString;
        }

        /// <summary>
        /// Creates an empty instance of a database.
        /// </summary>
        public static AzuriteStorageProcess CreateAndStartEmpty(
            LocalRedisFixture storageFixture,
            ILogger logger)
        {
            return CreateAndStart(storageFixture, logger);
        }

        /// <summary>
        /// Creates an instance of a database with a given data.
        /// </summary>
        public static AzuriteStorageProcess CreateAndStart(
            LocalRedisFixture storageFixture,
            ILogger logger,
            List<string> accounts = null)
        {
            logger.Debug($"Fixture '{storageFixture.Id}' has {storageFixture.EmulatorPool.ObjectsInPool} available storage databases.");
            var instance = accounts is null ? storageFixture.EmulatorPool.GetInstance().Instance : new AzuriteStorageProcess();
            var oldOrNew = instance._process != null ? "an old" : "a new";
            logger.Debug($"LocalStorageProcessDatabase: got {oldOrNew} instance from the pool.");

            if (instance.Closed)
            {
                throw new ObjectDisposedException("instance", "The instance is already closed!");
            }

            instance.Init(logger, storageFixture);
            try
            {
                instance.Start(accounts);
                return instance;
            }
            catch (Exception e)
            {
                logger.Error("Failed to start a local database. Exception=" + e);
                instance.Dispose();
                throw;
            }
        }

        /// <inheritdoc />
        public void Dispose() => Dispose(close: false);

        /// <nodoc />
        public void Dispose(bool close)
        {
            if (close)
            {
                // Closing the instance and not returning it back to the pool.
                Close();
                return;
            }

            if (_disposed)
            {
                // The type should be safe for double dispose.
                return;
            }

            try
            {
                // Clear the containers in the storage account to allow reuse
                ClearAsync().GetAwaiter().GetResult();

            }
            catch (Exception ex)
            {
                _logger.Error(
                    $"Exception connecting to clear storage process {_process.Id} with port {_portNumber}: {ex.ToString()}. Has process exited {_process.HasExited} with output {_process.GetLogs()}");
                Close();
            }

            _logger.Debug($"Returning database to pool in fixture '{_storageFixture.Id}'");
            _storageFixture.EmulatorPool.PutInstance(this);
            _disposed = true;
        }

        public async Task ClearAsync(string prefix = null)
        {
            var creds = new SecretBasedAzureStorageCredentials(ConnectionString);
            var blobClient = creds.CreateBlobServiceClient();
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var token = cts.Token;

            await foreach (var container in blobClient.GetBlobContainersAsync(cancellationToken: token))
            {
                await creds.CreateContainerClient(container.Name).DeleteIfExistsAsync(cancellationToken: token);
            }
        }

        ~AzuriteStorageProcess()
        {
            // If the emulator is not gracefully closed,
            // then BuildXL will fail because surviving blob.exe instance.
            // So we're failing fast instead and will print the process Id that caused the issue.
            // This may happen only if the database is not disposed gracefully.
            if (Initialized && !Closed)
            {
                string message = $"Storage process {_process?.Id} was not closed correctly.";

                _logger.Debug(message);
                throw new InvalidOperationException(message);
            }
        }

        public void Close()
        {
            if (Closed)
            {
                return;
            }

            GC.SuppressFinalize(this);

            if (_process != null)
            {
                _logger.Debug($"Killing the storage process {_process?.Id}...");
                SafeKillProcess();
            }

            _tempDirectory.Dispose();
            _fileSystem.Dispose();
            Closed = true;
        }

        private void SafeKillProcess()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(5000);
                    _logger.Debug("The storage process is killed");
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void Start(List<string> accounts = null)
        {
            // Can reuse an existing process only when this instance successfully created a connection to it.
            // Otherwise the test will fail with NRE.
            if (_process != null)
            {
                _logger.Debug("Storage process is already running. Reusing an existing instance.");
                return;
            }

            _logger.Debug("Starting a storage server.");


            var storageName = (OperatingSystemHelper.IsWindowsOS ? "tools/win-x64/blob.exe"
                : (OperatingSystemHelper.IsLinuxOS ? "tools/linux-x64/blob"
                : "tools/osx-x64/blob"));
            string storageServerPath = Path.GetFullPath(Path.Combine("azurite", storageName));
            if (!File.Exists(storageServerPath))
            {
                throw new InvalidOperationException($"Could not find {storageName} at {storageServerPath}");
            }

            _ = FileUtilities.SetExecutePermissionIfNeeded(storageServerPath);

            _portNumber = 0;

            const int maxRetries = 10;
            for (int i = 0; i < maxRetries; i++)
            {
                var storageServerWorkspacePath = _tempDirectory.CreateRandomFileName();
                _fileSystem.CreateDirectory(storageServerWorkspacePath);
                _portNumber = PortExtensions.GetNextAvailablePort();

                var args = $"--blobPort {_portNumber} --location {storageServerWorkspacePath} --skipApiVersionCheck --silent --loose";
                _logger.Debug($"Running cmd=[{storageServerPath} {args}]");

                _process = new ProcessUtility(storageServerPath, args, createNoWindow: true, workingDirectory: Path.GetDirectoryName(storageServerPath), environment: CreateEnvironment(accounts));

                _process.Start();

                string processOutput;
                if (_process == null)
                {
                    processOutput = "[Process could not start]";
                    throw new InvalidOperationException(processOutput);
                }

                if (_process.HasExited)
                {
                    if (_process.WaitForExit(5000))
                    {
                        throw new InvalidOperationException(_process.GetLogs());
                    }

                    throw new InvalidOperationException("Process or either wait handle timed out. " + _process.GetLogs());
                }

                processOutput = $"[Process {_process.Id} is still running]";

                _logger.Debug("Process output: " + processOutput);

                ConnectionString = $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:{_portNumber}/devstoreaccount1;";

                var creds = new SecretBasedAzureStorageCredentials(ConnectionString);
                try
                {
                    bool exists = creds.CreateContainerClient("test").Exists();
                    break;
                }
                catch (RequestFailedException ex)
                {
                    SafeKillProcess();
                    _logger.Debug($"Retrying for exception connecting to storage process {_process.Id} with port {_portNumber}: {ex.ToString()}. Has process exited {_process.HasExited} with output {_process.GetLogs()}");

                    if (i != maxRetries - 1)
                    {
                        Thread.Sleep(300);
                    }
                    else
                    {
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    SafeKillProcess();
                    _logger.Error(
                        $"Exception connecting to storage process {_process.Id} with port {_portNumber}: {ex.ToString()}. Has process exited {_process.HasExited} with output {_process.GetLogs()}");
                    throw;
                }
            }

            _logger.Debug($"Storage server {_process.Id} is up and running at port {_portNumber}.");
        }

        private Dictionary<string, string> CreateEnvironment(List<string> accounts = null)
        {
            if (accounts == null)
            {
                return null;
            }

            // See: https://github.com/Azure/Azurite#customized-storage-accounts--keys
            var dictionary = new Dictionary<string, string>();

            // Ensure the default account still exists
            accounts.Add("devstoreaccount1");

            // We use the same password for all storage accounts in the emulator
            dictionary["AZURITE_ACCOUNTS"] = string.Join(
                ";",
                accounts.Select(
                    name => $"{name}:Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="));

            return dictionary;
        }
    }
}
