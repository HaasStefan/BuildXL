// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Ipc.Common;
using BuildXL.Ipc.ExternalApi;
using BuildXL.Ipc.Interfaces;
using BuildXL.Storage;
using BuildXL.Storage.Fingerprints;
using BuildXL.Tracing.CloudBuild;
using BuildXL.Utilities.CLI;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Core.Tasks;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.Symbol.App.Core.Tracing;
using Microsoft.VisualStudio.Services.Symbol.Common;
using Microsoft.VisualStudio.Services.Symbol.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using Newtonsoft.Json;
using Tool.ServicePipDaemon;
using static BuildXL.Utilities.Core.FormattableStringEx;

namespace Tool.SymbolDaemon
{
    /// <summary>
    /// Daemon responsible for handling symbol-related requests.
    /// </summary>
    public sealed class SymbolDaemon : ServicePipDaemon.FinalizedByCreatorServicePipDaemon, IDisposable, IIpcOperationExecutor
    {
        private const string LogFileName = "SymbolDaemon";
        private const int s_servicePointParallelism = 200;
        private const char s_debugEntryDataFieldSeparator = '|';

        /// <nodoc/>
        public const string SymbolDLogPrefix = "(SymbolD) ";

        private static readonly int s_minIoThreads = Environment.ProcessorCount * 10;
        private static readonly int s_minWorkerThreads = Environment.ProcessorCount * 10;

        private Task<ISymbolClient> m_symbolServiceClientTask;
        private readonly ISymbolIndexer m_symbolIndexer;

        /// <summary>
        /// Configuration used to initialize this daemon
        /// </summary>
        public SymbolConfig SymbolConfig { get; }

        /// <nodoc />
        public string RequestName => SymbolConfig.Name;

        # region Symbol daemon options and commands

        internal static readonly List<Option> SymbolConfigOptions = new List<Option>();

        private static T RegisterSymbolConfigOption<T>(T option) where T : Option => RegisterOption(SymbolConfigOptions, option);

        internal static readonly StrOption SymbolRequestNameOption = RegisterSymbolConfigOption(new StrOption("name")
        {
            ShortName = "n",
            HelpText = "Request name",
            IsRequired = true,
        });

        internal static readonly UriOption ServiceEndpoint = RegisterSymbolConfigOption(new UriOption("service")
        {
            ShortName = "s",
            HelpText = "Symbol service endpoint URI",
            IsRequired = true,
        });

        internal static readonly StrOption DebugEntryCreateBehavior = RegisterSymbolConfigOption(new StrOption("debugEntryCreateBehavior")
        {
            ShortName = "de",
            HelpText = "Debug Entry Create Behavior in case of a collision",
            IsRequired = false,
            DefaultValue = Microsoft.VisualStudio.Services.Symbol.WebApi.DebugEntryCreateBehavior.ThrowIfExists.ToString(),
        });

        internal static readonly IntOption RetentionDays = RegisterSymbolConfigOption(new IntOption("retentionDays")
        {
            ShortName = "rt",
            HelpText = "Symbol retention time in days",
            IsRequired = false,
            DefaultValue = (int)SymbolConfig.DefaultRetention.TotalDays,
        });

        internal static readonly IntOption HttpSendTimeoutMillis = RegisterSymbolConfigOption(new IntOption("httpSendTimeoutMillis")
        {
            HelpText = "Timeout for http requests",
            IsRequired = false,
            DefaultValue = (int)SymbolConfig.DefaultHttpSendTimeout.TotalMilliseconds,
        });

        internal static readonly BoolOption EnableTelemetry = RegisterSymbolConfigOption(new BoolOption("enableTelemetry")
        {
            ShortName = "t",
            HelpText = "Verbose logging",
            IsRequired = false,
            DefaultValue = SymbolConfig.DefaultEnableTelemetry,
        });

        internal static readonly StrOption SymbolMetadataFile = new StrOption("symbolMetadata")
        {
            ShortName = "sm",
            HelpText = "Path to a file with symbols metadata.",
            IsRequired = false,
            IsMultiValue = false,
        };

        internal static readonly StrOption InputDirectoriesContent = new StrOption("inputDirectoriesContent")
        {
            ShortName = "idc",
            HelpText = "Path to a file with the content of input directories.",
            IsRequired = true,
            IsMultiValue = false,
        };

        internal static readonly NullableIntOption OptionalDomainId = RegisterSymbolConfigOption(new NullableIntOption("domainId")
        {
            ShortName = "ddid",
            HelpText = "Optional domain id setting.",
            IsRequired = false,
            DefaultValue = null,
        });

        internal static readonly IntOption BatchSize = RegisterSymbolConfigOption(new IntOption("batchSize")
        {
            ShortName = "bs",
            HelpText = "Size of batches in which to send 'associate' requests",
            IsRequired = false,
            DefaultValue = SymbolConfig.DefaultBatchSize,
        });

        internal static readonly IntOption MaxParallelUploads = RegisterSymbolConfigOption(new IntOption("maxParallelUploads")
        {
            ShortName = "mpu",
            HelpText = "Maximum number of uploads to issue to the service endpoint in parallel",
            IsRequired = false,
            DefaultValue = SymbolConfig.DefaultMaxParallelUploads,
        });

        internal static readonly IntOption NagleTimeMs = RegisterSymbolConfigOption(new IntOption("nagleTimeMillis")
        {
            ShortName = "nt",
            HelpText = "Maximum time in milliseconds to wait before triggering a batch 'associate' request",
            IsRequired = false,
            DefaultValue = (int)SymbolConfig.DefaultNagleTime.TotalMilliseconds,
        });

        internal static readonly BoolOption ReportTelemetry = RegisterSymbolConfigOption(new BoolOption("reprotTelemetry")
        {
            ShortName = "rst",
            HelpText = "Whether to report collected telemetry",
            IsRequired = false,
            DefaultValue = true,
        });

        internal static readonly StrOption PersonalAccessTokenEnv = RegisterSymbolConfigOption(new StrOption("PersonalAccessTokenEnv")
        {
            ShortName = "patenv",
            HelpText = "Personal access token environment variable",
            IsRequired = false,
            DefaultValue = string.Empty,
        });

        internal static readonly StrOption SessionId = RegisterSymbolConfigOption(new StrOption("sessionId")
        {
            ShortName = "sid",
            HelpText = "Optional guid to use as a session id when communicating to AzDO.",
            IsRequired = false,
            // TODO: Remove after golden update (#2104026)
            DefaultValue = Environment.GetEnvironmentVariable("Q_SESSION_GUID")
        });

        internal static SymbolConfig CreateSymbolConfig(ConfiguredCommand conf)
        {
            byte? domainId;
            checked
            {
                domainId = (byte?)conf.Get(OptionalDomainId);
            }

            Guid? sessionId = null;
            if (Guid.TryParse(conf.Get(SessionId), out var parsedSessionId))
            {
                sessionId = parsedSessionId;
            }

            return new SymbolConfig(
                requestName: conf.Get(SymbolRequestNameOption),
                serviceEndpoint: conf.Get(ServiceEndpoint),
                debugEntryCreateBehaviorStr: conf.Get(DebugEntryCreateBehavior),
                retention: TimeSpan.FromDays(conf.Get(RetentionDays)),
                httpSendTimeout: TimeSpan.FromMilliseconds(conf.Get(HttpSendTimeoutMillis)),
                verbose: conf.Get(Verbose),
                enableTelemetry: conf.Get(EnableTelemetry),
                logDir: conf.Get(LogDir),
                domainId: domainId,
                batchSize: conf.Get(BatchSize),
                maxParallelUploads: conf.Get(MaxParallelUploads),
                nagleTimeMs: conf.Get(NagleTimeMs),
                reportTelemetry: conf.Get(ReportTelemetry),
                personalAccessTokenEnv: conf.Get(PersonalAccessTokenEnv),
                sessionId: sessionId);
        }

        private static Client CreateClient(string serverMoniker, IClientConfig config)
        {
            return serverMoniker != null
                ? Client.Create(IpcProvider, serverMoniker, config)
                : null;
        }

        internal static readonly Command StartNoServiceCmd = RegisterCommand(
            name: "start-noservice",
            description: @"Starts a server process without a backing symbol service client (useful for testing/pinging the daemon).",
            needsIpcClient: false,
            clientAction: (conf, _) =>
            {
                var symbolConfig = new SymbolConfig(string.Empty, new Uri("file://xyz"));
                var daemonConfig = CreateDaemonConfig(conf);
                var vsoClientTask = TaskSourceSlim.Create<ISymbolClient>();
                vsoClientTask.SetException(new NotSupportedException());
                using (var daemon = new SymbolDaemon(conf.Config.Parser, daemonConfig, symbolConfig, vsoClientTask.Task))
                {
                    daemon.Start();
                    daemon.Completion.GetAwaiter().GetResult();
                    return 0;
                }
            });

        internal static readonly Command StartCmd = RegisterCommand(
           name: "start",
           description: "Starts the server process.",
           options: SymbolConfigOptions.Union(new[] { IpcServerMonikerOptional }),
           needsIpcClient: false,
           clientAction: (conf, _) =>
           {
               // This command is used when BXL creates a ServicePip for SymbolDaemon.
               SetupThreadPoolAndServicePoint(s_minWorkerThreads, s_minIoThreads, s_servicePointParallelism);
               var symbolConfig = CreateSymbolConfig(conf);
               var daemonConf = CreateDaemonConfig(conf);

               if (daemonConf.MaxConcurrentClients <= 1)
               {
                   conf.Logger.Error($"Must specify at least 2 '{nameof(DaemonConfig.MaxConcurrentClients)}' when running SymbolDaemon to avoid deadlock when stopping this daemon from a different client");
                   return -1;
               }

               using (var client = CreateClient(conf.Get(IpcServerMonikerOptional), daemonConf))
               using (var daemon = new SymbolDaemon(
                   parser: conf.Config.Parser,
                   daemonConfig: daemonConf,
                   symbolConfig: symbolConfig,
                   symbolServiceClientTask: null,
                   bxlClient: client))
               {
                   daemon.Start();
                   // We are blocking the thread here and waiting for the SymbolDaemon to process all the requests.
                   // Once the daemon receives 'stop' command, GetResult will return, and we'll leave this method
                   // (i.e., ServicePip will finish).
                   daemon.Completion.GetAwaiter().GetResult();
                   return 0;
               }
           });

        internal static readonly Command CreateSymbolRequestCmd = RegisterCommand(
           name: "create",
           description: "[RPC] Invokes the 'create' operation.",
           options: SymbolConfigOptions,
           clientAction: SyncRPCSend,
           serverAction: async (conf, daemon) =>
           {
               var symbolDaemon = daemon as SymbolDaemon;
               symbolDaemon.Logger.Info("[CREATE]: Started at " + symbolDaemon.SymbolConfig.Service + "/" + symbolDaemon.SymbolConfig.Name);
               IIpcResult result = await symbolDaemon.CreateAsync();
               daemon.Logger.Info("[CREATE]: " + result);
               return result;
           });

        internal static readonly Command FinalizeSymbolRequestCmd = RegisterCommand(
            name: "finalize",
            description: "[RPC] Invokes the 'finalize' operation.",
            clientAction: SyncRPCSend,
            serverAction: async (conf, daemon) =>
            {
                var symbolDaemon = daemon as SymbolDaemon;
                symbolDaemon.Logger.Info("[FINALIZE] Started at " + symbolDaemon.SymbolConfig.Service + "/" + symbolDaemon.SymbolConfig.Name);
                IIpcResult result = await symbolDaemon.FinalizeAsync();
                daemon.Logger.Info("[FINALIZE] " + result);
                return result;
            });

        internal static readonly Command FinalizeSymbolRequestAndStopDaemonCmd = RegisterCommand(
            name: "finalize-and-stop",
            description: "[RPC] Invokes the 'finalize' operation; then stops the daemon.",
            clientAction: SyncRPCSend,
            serverAction: Command.Compose(FinalizeSymbolRequestCmd.ServerAction, StopDaemonCmd.ServerAction));

        internal static readonly Command AddSymbolFilesCmd = RegisterCommand(
            name: "addsymbolfiles",
            description: "[RPC] invokes the 'addsymbolfiles' operation.",
            options: new Option[] { IpcServerMonikerRequired, File, FileId, HashOptional, SymbolMetadataFile },
            clientAction: SyncRPCSend,
            serverAction: async (conf, daemon) =>
            {
                var symbolDaemon = daemon as SymbolDaemon;
                symbolDaemon.Logger.Verbose("[ADDSYMBOLS] Started");

                var files = File.GetValues(conf.Config).ToArray();
                var fileIds = FileId.GetValues(conf.Config).ToArray();
                var hashes = HashOptional.GetValues(conf.Config).ToArray();
                var symbolMetadataFile = SymbolMetadataFile.GetValue(conf.Config);

                var result = await AddSymbolFilesInternalAsync(files, fileIds, hashes, symbolMetadataFile, symbolDaemon);
                LogIpcResult(symbolDaemon.Logger, LogLevel.Verbose, "[ADDSYMBOLS] ", result);
                // Trim the payload before sending the result.
                return SuccessOrFirstError(result);
            });

        internal static readonly Command IndexFilesCmd = RegisterCommand(
            name: "indexFiles",
            description: "Indexes the specified files and saves SymbolData into a file.",
            options: new Option[] { File, HashOptional, SymbolMetadataFile },
            needsIpcClient: false,
            clientAction: (ConfiguredCommand conf, IClient rpc) =>
            {
                var files = File.GetValues(conf.Config).ToArray();

                // hashes are sent from BXL by serializing FileContentInfo
                var hashesWithLength = HashOptional.GetValues(conf.Config);
                var hashesOnly = hashesWithLength.Select(h => FileContentInfo.Parse(h).Hash).ToArray();

                var outputFile = SymbolMetadataFile.GetValue(conf.Config);

                IndexFilesAndStoreMetadataToFile(files, hashesOnly, outputFile);

                return 0;
            });

        internal static readonly Command GetDirectoriesContentCmd = RegisterCommand(
            name: "getDirectoriesContent",
            description: "[RPC] invokes the 'GetDirectoriesContentAsync' operation.",
            options: new Option[] { IpcServerMonikerRequired, Directory, DirectoryId },
            clientAction: SyncRPCSend,
            serverAction: async (conf, daemon) =>
            {
                var symbolDaemon = daemon as SymbolDaemon;
                symbolDaemon.Logger.Verbose("[GetDirectoriesContentAsync] Started");
                var result = await GetDirectoriesContentAsync(conf, symbolDaemon);
                LogIpcResult(symbolDaemon.Logger, LogLevel.Verbose, "[GetDirectoriesContentAsync] ", result);
                // Do not change this result (it contains a list of files under a dir artifact). The result will
                // become a real build artifact when this pip completes. The "symbol indexer" pip consumes this
                // artifact (it's a part of its fingerprint) and uses it to learn which files under a root are
                // a part of a given SOD it needs to index (this is done this was so the pip could benefit from
                // caching).
                return result;
            });

        internal static readonly Command IndexDirectoriesCmd = RegisterCommand(
            name: "indexDirectories",
            description: "Indexes files in the specified directories and saves SymbolData into a file.",
            options: new Option[] { Directory, InputDirectoriesContent, SymbolMetadataFile },
            needsIpcClient: false,
            clientAction: (ConfiguredCommand conf, IClient rpc) =>
            {
                var dirContentFile = InputDirectoriesContent.GetValue(conf.Config);
                var dirContent = System.IO.File.ReadLines(dirContentFile);
                var files = new List<string>();
                var hashes = new List<ContentHash>();
                foreach (var line in dirContent.Where(line => line.Length > 0))
                {
                    var pathAndHash = line.Split(s_debugEntryDataFieldSeparator);
                    Contract.Assert(pathAndHash.Length == 2, "Input directories content file has a wrong format");

                    files.Add(pathAndHash[0]);
                    hashes.Add(FileContentInfo.Parse(pathAndHash[1]).Hash);
                }

                var outputFile = SymbolMetadataFile.GetValue(conf.Config);

                IndexFilesAndStoreMetadataToFile(files.ToArray(), hashes.ToArray(), outputFile);

                return 0;
            });

        internal static readonly Command AddSymbolFilesFromDirectoriesCmd = RegisterCommand(
            name: "addSymbolFilesFromDirectories",
            description: "[RPC] invokes the 'addSymbolFilesFromDirectories' operation.",
            options: new Option[] { IpcServerMonikerRequired, Directory, DirectoryId, SymbolMetadataFile },
            clientAction: SyncRPCSend,
            serverAction: async (conf, daemon) =>
            {
                var symbolDaemon = daemon as SymbolDaemon;
                symbolDaemon.Logger.Verbose("[ADDSYMBOLSFROMDIRECTORIES] Started");
                var result = await AddDirectoriesInternalAsync(conf, symbolDaemon);
                LogIpcResult(symbolDaemon.Logger, LogLevel.Verbose, "[ADDSYMBOLSFROMDIRECTORIES] ", result);
                // Trim the payload before sending the result.
                return SuccessOrFirstError(result);
            });

        private static void IndexFilesAndStoreMetadataToFile(string[] files, ContentHash[] hashes, string outputFile)
        {
            Contract.Assert(files.Length == hashes.Length, "Array lengths must match.");
            Contract.Assert(!string.IsNullOrEmpty(outputFile), "Output file path must be provided.");

            var indexer = new SymbolIndexer(SymbolAppTraceSource.SingleInstance);
            var symbolsMetadata = new Dictionary<ContentHash, HashSet<DebugEntryData>>(files.Length);

            for (int i = 0; i < files.Length; i++)
            {
                HashSet<DebugEntryData> symbols;
                if (!symbolsMetadata.TryGetValue(hashes[i], out symbols))
                {
                    symbols = new HashSet<DebugEntryData>(DebugEntryDataComparer.Instance);
                    symbolsMetadata.Add(hashes[i], symbols);
                }

                // SharedOpaque directories might contain absent files (these are so called temporary files, and we keep track
                // of them mainly for DFA purposes). Since there is no actual file, there is nothing for us to index.
                if (!WellKnownContentHashUtilities.IsAbsentFileHash(hashes[i]))
                {
                    // Index the file. It might not contain any symbol data. In this case, we will have an empty set.
                    symbols.UnionWith(indexer.GetDebugEntries(new System.IO.FileInfo(files[i]), calculateBlobId: false));
                }
            }

            SerializeSymbolsMetadata(symbolsMetadata, outputFile);
        }

        /// <summary>
        /// Serializes DebugEntryData's into a file.
        /// </summary>        
        public static void SerializeSymbolsMetadata(Dictionary<ContentHash, HashSet<DebugEntryData>> symbolsMetadata, string outputFile)
        {
            /*                
                <number of hashes>
                for each hash:
                    <hash>
                    <number of debug entries>
                    [<debug entry>]
             */

            using (var writer = new StreamWriter(outputFile))
            {
                writer.WriteLine(symbolsMetadata.Count);

                foreach (var kvp in symbolsMetadata)
                {
                    writer.WriteLine(kvp.Key.Serialize());
                    writer.WriteLine(kvp.Value.Count);

                    foreach (var debugEntry in kvp.Value)
                    {
                        writer.WriteLine(SerializeDebugEntryData(debugEntry));
                    }
                }
            }
        }

        /// <summary>
        /// Deserializes DebugEntryData's from a file.
        /// </summary>
        public static Dictionary<ContentHash, HashSet<DebugEntryData>> DeserializeSymbolsMetadata(string fileName)
        {
            Dictionary<ContentHash, HashSet<DebugEntryData>> result;
            using (var reader = new StreamReader(fileName))
            {
                int hashCount = int.Parse(reader.ReadLine());
                result = new Dictionary<ContentHash, HashSet<DebugEntryData>>(hashCount);

                for (int i = 0; i < hashCount; i++)
                {
                    ContentHash.TryParse(reader.ReadLine(), out var hash);
                    var blobIdentifier = new BlobIdentifier(hash.ToHashByteArray());
                    int debugEntryCount = int.Parse(reader.ReadLine());

                    var symbols = new HashSet<DebugEntryData>(debugEntryCount, DebugEntryDataComparer.Instance);
                    result.Add(hash, symbols);

                    for (int j = 0; j < debugEntryCount; j++)
                    {
                        var entry = DeserializeDebugEntryData(reader.ReadLine());
                        if (entry.BlobIdentifier == null)
                        {
                            entry.BlobIdentifier = blobIdentifier;
                        }
                        symbols.Add(entry);
                    }
                }
            }

            return result;
        }

        private static string SerializeDebugEntryData(DebugEntryData entry)
        {
            var sb = new StringBuilder(entry.ClientKey.Length * 2);

            sb.Append(entry.BlobIdentifier == null ? "" : entry.BlobIdentifier.ValueString);
            sb.Append(s_debugEntryDataFieldSeparator);
            sb.Append(entry.ClientKey);
            sb.Append(s_debugEntryDataFieldSeparator);
            sb.Append((int)entry.InformationLevel);

            return sb.ToString();
        }

        private static DebugEntryData DeserializeDebugEntryData(string serializedEntry)
        {
            const int DebugEntryFieldCount = 3;

            var blocks = serializedEntry.Split(s_debugEntryDataFieldSeparator);
            Contract.Assert(blocks.Length == DebugEntryFieldCount, $"Expected to find {DebugEntryFieldCount} fields in the serialized string, but found {blocks.Length}.");

            var result = new DebugEntryData()
            {
                BlobIdentifier = blocks[0].Length == 0 ? null : BlobIdentifier.Deserialize(blocks[0]),
                ClientKey = string.IsNullOrEmpty(blocks[1]) ? null : blocks[1],
                InformationLevel = (DebugInformationLevel)int.Parse(blocks[2])
            };

            return result;
        }

        private static async Task<IIpcResult> GetDirectoriesContentAsync(ConfiguredCommand conf, SymbolDaemon daemon)
        {
            var directoryPaths = Directory.GetValues(conf.Config).ToArray();
            var directoryIds = DirectoryId.GetValues(conf.Config).ToArray();

            if (directoryPaths.Length != directoryIds.Length)
            {
                return new IpcResult(
                    IpcResultStatus.GenericError,
                    I($"Directory counts don't match: #directories = {directoryPaths.Length}, #directoryIds = {directoryIds.Length}"));
            }

            if (daemon.ApiClient == null)
            {
                return new IpcResult(IpcResultStatus.GenericError, "ApiClient is not initialized");
            }

            var maybeResult = await GetDedupedDirectoriesContentAsync(directoryIds, directoryPaths, daemon.ApiClient);
            if (!maybeResult.Succeeded)
            {
                return new IpcResult(
                    IpcResultStatus.GenericError,
                    "could not get the directory content from BuildXL server: " + maybeResult.Failure.Describe());
            }

            var files = maybeResult.Result
                .Select(file => $"{file.FileName.ToCanonicalizedPath()}{s_debugEntryDataFieldSeparator}{file.ContentInfo.Render()}")
                .ToList();
            files.Sort();

            return new IpcResult(IpcResultStatus.Success, string.Join(Environment.NewLine, files));
        }

        private static async Task<Possible<HashSet<SealedDirectoryFile>>> GetDedupedDirectoriesContentAsync(string[] directoryIds, string[] directoryPaths, Client apiClient)
        {
            Contract.Requires(apiClient != null);
            Contract.Requires(directoryIds.Length == directoryIds.Length);

            var files = new HashSet<SealedDirectoryFile>();
            for (int i = 0; i < directoryIds.Length; i++)
            {
                DirectoryArtifact directoryArtifact = BuildXL.Ipc.ExternalApi.DirectoryId.Parse(directoryIds[i]);
                var maybeResult = await apiClient.GetSealedDirectoryContent(directoryArtifact, directoryPaths[i]);
                if (!maybeResult.Succeeded)
                {
                    return maybeResult.Failure;
                }

                files.UnionWith(maybeResult.Result);
            }

            return files;
        }

        private static async Task<IIpcResult> AddSymbolFilesInternalAsync(
            string[] files,
            string[] fileIds,
            string[] hashes,
            string symbolMetadataFile,
            SymbolDaemon daemon)
        {
            if (files.Length != fileIds.Length || files.Length != hashes.Length)
            {
                return new IpcResult(
                    IpcResultStatus.GenericError,
                    I($"File counts don't match: #files = {files.Length}, #fileIds = {fileIds.Length}, #hashes = {hashes.Length}"));
            }

            if (string.IsNullOrEmpty(symbolMetadataFile))
            {
                return new IpcResult(
                    IpcResultStatus.GenericError,
                    "Invalid path to symbol metadata file.");
            }

            Dictionary<ContentHash, HashSet<DebugEntryData>> symbolMetadata;
            try
            {
                symbolMetadata = DeserializeSymbolsMetadata(symbolMetadataFile);
            }
            catch (Exception e)
            {
                return new IpcResult(
                   IpcResultStatus.GenericError,
                   I($"Failed to deserialize symbol metadata file: {e.DemystifyToString()}"));
            }

            List<SymbolFile> symbolFiles = new List<SymbolFile>(files.Length);
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    var fci = FileContentInfo.Parse(hashes[i]);
                    var hash = fci.Hash;
                    if (!symbolMetadata.TryGetValue(hash, out var debugEntries))
                    {
                        daemon.Logger.Verbose("Symbol metadata file - {0}{1}{2}",
                            symbolMetadataFile,
                            Environment.NewLine,
                            await System.IO.File.ReadAllTextAsync(symbolMetadataFile));

                        return new IpcResult(
                            IpcResultStatus.GenericError,
                            I($"Hash '{hash}' (file: '{files[i]}') was not found in the metadata file '{symbolMetadataFile}'."));
                    }

                    // Ensure HashType is VsoHash which is required for BlobIdentifiers
                    Possible<FileContentInfo> parsedResult = await ParseFileContentAsync(daemon, hashes[i], fileIds[i], files[i]);
                    var fileContentInfo = parsedResult.Result;
                    var blobIdentifier = new BlobIdentifier(fileContentInfo.Hash.ToHashByteArray());
                    debugEntries = new HashSet<DebugEntryData>(debugEntries.Select(e => { e.BlobIdentifier = blobIdentifier; return e; }));

                    symbolFiles.Add(new SymbolFile(
                        daemon.ApiClient,
                        files[i],
                        fileIds[i],
                        fileContentInfo.Hash,
                        fileContentInfo.Length,
                        debugEntries));
                }
                catch (Exception e)
                {
                    return new IpcResult(
                        IpcResultStatus.GenericError,
                        e.DemystifyToString());
                }
            }

            var result = await daemon.AddSymbolFilesAsync(symbolFiles);

            return result;
        }

        private static async Task<IIpcResult> AddDirectoriesInternalAsync(ConfiguredCommand conf, SymbolDaemon daemon)
        {
            var directoryPaths = Directory.GetValues(conf.Config).ToArray();
            var directoryIds = DirectoryId.GetValues(conf.Config).ToArray();
            var symbolMetadataFile = SymbolMetadataFile.GetValue(conf.Config);

            if (directoryPaths.Length != directoryIds.Length)
            {
                return new IpcResult(
                    IpcResultStatus.GenericError,
                    I($"Directory counts don't match: #directories = {directoryPaths.Length}, #directoryIds = {directoryIds.Length}"));
            }

            if (daemon.ApiClient == null)
            {
                return new IpcResult(IpcResultStatus.GenericError, "ApiClient is not initialized");
            }

            var maybeResult = await GetDedupedDirectoriesContentAsync(directoryIds, directoryPaths, daemon.ApiClient);
            if (!maybeResult.Succeeded)
            {
                return new IpcResult(
                    IpcResultStatus.GenericError,
                    "could not get the directory content from BuildXL server: " + maybeResult.Failure.Describe());
            }

            if (maybeResult.Result.Count == 0)
            {
                return IpcResult.Success($"Directories ({string.Join(",", directoryIds)}) have no content.");
            }

            var filesPaths = new List<string>();
            var filesIds = new List<string>();
            var hashes = new List<string>();
            foreach (var file in maybeResult.Result)
            {
                filesPaths.Add(file.FileName);
                filesIds.Add(BuildXL.Ipc.ExternalApi.FileId.ToString(file.Artifact));
                hashes.Add(file.ContentInfo.Render());
            }

            return await AddSymbolFilesInternalAsync(filesPaths.ToArray(), filesIds.ToArray(), hashes.ToArray(), symbolMetadataFile, daemon);
        }

        #endregion

        static SymbolDaemon()
        {
            // noop
        }

        /// <nodoc/>
        public SymbolDaemon(
            IParser parser,
            DaemonConfig daemonConfig,
            SymbolConfig symbolConfig,
            Task<ISymbolClient> symbolServiceClientTask,
            IIpcProvider rpcProvider = null,
            Client bxlClient = null)
                : base(parser,
                      daemonConfig,
                      !string.IsNullOrWhiteSpace(symbolConfig.LogDir) ? new FileLogger(symbolConfig.LogDir, LogFileName, daemonConfig.Moniker, symbolConfig.Verbose, SymbolDLogPrefix) : daemonConfig.Logger,
                      rpcProvider,
                      bxlClient)
        {
            Contract.Requires(symbolConfig != null);

            SymbolConfig = symbolConfig;
            m_logger.Info(I($"Using {nameof(DaemonConfig)}: {JsonConvert.SerializeObject(daemonConfig)}"));
            m_logger.Info(I($"Using {nameof(SymbolConfig)}: {JsonConvert.SerializeObject(symbolConfig)}"));

            // if no ISymbolServiceClient has been provided, create VsoSymbolClient using the provided SymbolConfig
            m_symbolServiceClientTask = symbolServiceClientTask ?? Task.Run(() => (ISymbolClient)new VsoSymbolClient(m_logger, daemonConfig, symbolConfig, bxlClient));

            m_symbolIndexer = new SymbolIndexer(SymbolAppTraceSource.SingleInstance);
        }

        internal static void EnsureCommandsInitialized()
        {
            Contract.Assert(Commands != null);

            // these operations are quite expensive, however, we expect to call this method only once per symbol request, so it should cause any perf downgrade
            var numCommandsBase = typeof(ServicePipDaemon.ServicePipDaemon).GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(Command)).Count();
            var numCommandsSymbolD = typeof(SymbolDaemon).GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(Command)).Count();

            if (Commands.Count != numCommandsBase + numCommandsSymbolD)
            {
                Contract.Assert(false, $"The list of commands was not properly initialized (# of initialized commands = {Commands.Count}; # of ServicePipDaemon commands = {numCommandsBase}; # of SymbolDaemon commands = {numCommandsSymbolD})");
            }
        }

        /// <summary>
        /// Creates a symbol request.
        /// </summary>
        protected override async Task<IIpcResult> DoCreateAsync(string name = null)
        {
            var dropCreationEvent = await HandleResultAndSendSymbolEtwEventAsync(InternalCreateAsync());

            return dropCreationEvent.Succeeded
                ? IpcResult.Success(I($"Symbol request '{RequestName}' created (url: '{dropCreationEvent.DropUrl}')."))
                : new IpcResult(ParseIpcStatus(dropCreationEvent.AdditionalInformation), dropCreationEvent.ErrorMessage);
        }

        /// <summary>
        /// Indexes the files and adds symbol data to the request.
        /// </summary>       
        public async Task<IIpcResult> AddSymbolFilesAsync(List<SymbolFile> files)
        {
            var addFileTasks = files.Select(f => AddSymbolFileAsync(f));
            var ipcResults = await TaskUtilities.SafeWhenAll(addFileTasks);

            return IpcResult.Merge(ipcResults);
        }

        private async Task<IIpcResult> AddSymbolFileAsync(SymbolFile file)
        {
            try
            {
                var symbolClient = await m_symbolServiceClientTask;
                var result = await symbolClient.AddFileAsync(file);

                return IpcResult.Success(I($"File '{file.FullFilePath}' {result} in request '{RequestName}'."));
            }
            // Because we use TaskUtilities.SafeWhenAll in multiple places in VsoSymbolClient, exceptions
            // will be wrapped in AggregateException (even if it's a single exception).
            catch (AggregateException e)
            {
                var result = IpcResult.Success();

                // AggregateException can contain any number of different exceptions. However, in this particular case,
                // all inner exceptions are expected to be of the same type. For example, if we hit materialization issues
                // we will likely have several materialization-related inner exceptions, but they all will be of the same type
                // and they represent the same issue.
                //
                // Exceptions here come from the same batch; so, while it's more than fine to do the merging of IpcResults here,
                // we might end up with IpcResultStatus.GenericError when we merge results for a single pip. This might happen if
                // pip's files were processed in different batches and batches failed for different reasons. This will throw off
                // our classification, but this should be a rather rare event.
                e.Handle(x => 
                {
                    if (x is DaemonException daemonException)
                    {
                        var status = IpcResultStatus.ExecutionError;
                        if (daemonException.Message.Contains(Statics.MaterializationResultIsSymlinkErrorPrefix))
                        {
                            // Symlinks are not supported and any attempts to upload one are user errors.
                            status = IpcResultStatus.InvalidInput;
                        }
                        else if (daemonException.Message.Contains(Statics.MaterializationResultFileNotFoundErrorPrefix)
                            || daemonException.Message.Contains(Statics.MaterializationResultMaterializationFailedErrorPrefix))
                        {
                            status = IpcResultStatus.ApiServerError;
                        }

                        result = IpcResult.Merge(result, new IpcResult(status, $"[DAEMON ERROR] {daemonException.Message}"));
                    }
                    else if (x is TimeoutException timeoutException)
                    {
                        // TimeoutException can only originate from ReloadingSymbolClient.
                        result = IpcResult.Merge(result, new IpcResult(IpcResultStatus.ExternalServiceError, $"[SYMBOL SERVICE ERROR] {timeoutException.Message}"));
                    }
                    else if (x is RequestSealedException requestSealedException)
                    {
                        // RequestSealedException happens when we try to add a file after the request was finalized. Our graph construction ensures that finalize pip
                        // is the last pip to run; so, when we hit this exception, it's likely caused by an error on the symbol service side.
                        result = IpcResult.Merge(result, new IpcResult(IpcResultStatus.ExternalServiceError, requestSealedException.Message));
                    }
                    else if (x is DebugEntryExistsException debugEntryExistsException)
                    {
                        result = IpcResult.Merge(result, new IpcResult(IpcResultStatus.InvalidInput, debugEntryExistsException.Message));
                    }
                    else
                    {
                        // All possible exceptions that can happen during AddFileAsync are listed above.
                        // This block is mainly to prevent Handle from throwing.
                        result = IpcResult.Merge(result, new IpcResult(IpcResultStatus.ExecutionError, x.DemystifyToString()));
                    }

                    return true;
                });

                Contract.Assert(result.ExitCode != IpcResultStatus.Success, $"Processed an AggregateException, but IpcResultStatus is 'Success'. Exception: {e.DemystifyToString()}");
                return result;
            }
            catch (DebugEntryExistsException e)
            {
                return new IpcResult(IpcResultStatus.InvalidInput, e.Message);
            }
            catch (Exception e)
            {
                return new IpcResult(IpcResultStatus.ExecutionError, e.DemystifyToString());
            }
        }

        /// <summary>
        /// Finalizes the symbol request. 
        /// </summary>
        protected override async Task<IIpcResult> DoFinalizeAsync(bool isFinalizeOnStop)
        {
            var dropFinalizationEvent = await HandleResultAndSendSymbolEtwEventAsync(InternalFinalizeAsync());

            return dropFinalizationEvent.Succeeded
               ? IpcResult.Success(I($"Symbol request '{RequestName}' finalized."))
               : new IpcResult(ParseIpcStatus(dropFinalizationEvent.AdditionalInformation), dropFinalizationEvent.ErrorMessage);
        }

        /// <nodoc />
        public override void Dispose()
        {
            if (m_symbolServiceClientTask.IsCompleted && !m_symbolServiceClientTask.IsFaulted)
            {
                ReportStatisticsAsync().GetAwaiter().GetResult();

                m_symbolServiceClientTask.Result.Dispose();
            }

            base.Dispose();
        }

        private async Task<DropCreationEvent> InternalCreateAsync()
        {
            var symbolClient = await m_symbolServiceClientTask;
            var result = await symbolClient.CreateAsync();

            Contract.Assert(result.Status == RequestStatus.Created);

            var serializedResult = SymbolRequesToString(result);
            m_logger.Info($"CreateAsync completed:{Environment.NewLine}{serializedResult}");

            return new DropCreationEvent()
            {
                Succeeded = true,
                DropUrl = result.Url.ToString(),
                // For Symbols, expiration is set during finalization, so we are using a value we will be assigning later.
                DropExpirationInDays = (int)SymbolConfig.Retention.TotalDays,
                AdditionalInformation = serializedResult
            };
        }

        private async Task<DropFinalizationEvent> InternalFinalizeAsync()
        {
            var symbolClient = await m_symbolServiceClientTask;
            var result = await symbolClient.FinalizeAsync();

            Contract.Assert(result.Status == RequestStatus.Sealed);
            Contract.Assert(result.ExpirationDate.HasValue);

            var serializedResult = SymbolRequesToString(result);
            m_logger.Info($"FinalizeAsync completed:{Environment.NewLine}{serializedResult}");

            return new DropFinalizationEvent()
            {
                Succeeded = true,
                DropUrl = result.Url.ToString(),
            };
        }

        private async Task ReportStatisticsAsync()
        {
            var symbolClient = await m_symbolServiceClientTask;
            var stats = symbolClient.GetStats();
            if (stats != null && stats.Any())
            {
                // log stats
                stats.AddRange(GetDaemonStats("SymbolDaemon"));
                m_logger.Info($"Statistics:{string.Join(string.Empty, stats.Select(s => $"{Environment.NewLine}{s.Key}={s.Value}"))}");

                // report stats to BuildXL
                if (ApiClient != null)
                {
                    var possiblyReported = await ApiClient.ReportStatistics(stats);
                    if (possiblyReported.Succeeded && possiblyReported.Result)
                    {
                        m_logger.Info("Statistics successfully reported to BuildXL.");
                    }
                    else
                    {
                        var errorDescription = possiblyReported.Succeeded ? string.Empty : possiblyReported.Failure.Describe();
                        m_logger.Warning("Reporting stats to BuildXL failed. " + errorDescription);
                    }

                    if (symbolClient is VsoSymbolClient client)
                    {
                        possiblyReported = await client.ReportSymbolTelemetryDataAsync("SymbolDaemon");
                        if (possiblyReported.Succeeded && possiblyReported.Result)
                        {
                            m_logger.Info($"Telemetry for symbol request '{client.RequestName}' successfully reported to BuildXL.");
                        }
                        else
                        {
                            var errorDescription = possiblyReported.Succeeded ? string.Empty : possiblyReported.Failure.Describe();
                            m_logger.Warning($"Reporting telemetry for symbol request '{client.RequestName}' to BuildXL failed. {errorDescription}");
                        }
                    }
                }
            }
            else
            {
                m_logger.Warning("No stats recorded by symbol client of type " + symbolClient.GetType().Name);
            }
        }

        private async Task<T> HandleResultAndSendSymbolEtwEventAsync<T>(Task<T> task) where T : DropOperationBaseEvent
        {
            var sw = Stopwatch.StartNew();
            T dropEvent;
            try
            {
                dropEvent = await task;
            }
            catch (Exception e)
            {
                dropEvent = Activator.CreateInstance<T>();
                dropEvent.Succeeded = false;
                // For symbols, url is something that is only defined for successful operations
                // (it's based on a requestId which is not available until successful execution of 'symbol create').
                dropEvent.DropUrl = null;
                classifyException(e, dropEvent);
            }

            // common properties: execution time, drop type
            dropEvent.ElapsedTimeTicks = sw.ElapsedTicks;
            dropEvent.DropType = "SymbolIndex";

            // send event
            m_etwLogger.Log(dropEvent);

            return dropEvent;

            static void classifyException(Exception e, T dropEvent)
            {
                // Classify both auth and 'request already exists' errors as InvalidInput, so BuildXL would
                // treat them as user errors.
                if (e is VssUnauthorizedException)
                {
                    dropEvent.ErrorMessage = $"[SYMBOL AUTH ERROR] {e.Message}";
                    dropEvent.AdditionalInformation = IpcResultStatus.InvalidInput.ToString();
                }
                else if (e is RequestExistsException)
                {
                    dropEvent.ErrorMessage = $"[SYMBOL SERVICE ERROR] {e.Message}";
                    dropEvent.AdditionalInformation = IpcResultStatus.InvalidInput.ToString();
                }
                else if (e is VssResourceNotFoundException)
                {
                    dropEvent.ErrorMessage = $"[SYMBOL SERVICE ERROR] {e.Message}";
                    dropEvent.AdditionalInformation = IpcResultStatus.TransmissionError.ToString();
                }
                else
                {
                    dropEvent.ErrorMessage = e.DemystifyToString();
                    dropEvent.AdditionalInformation = IpcResultStatus.GenericError.ToString();
                }
            }
        }

        private static string SymbolRequesToString(Request request)
        {
            try
            {
                return request.ToJson();
            }
            catch (Exception e)
            {
                // The value is only used for debugging, so it's not a big deal if we fail to create the string.
                return $"Failed to serialized Request. Exception: {e}";
            }
        }
    }
}
