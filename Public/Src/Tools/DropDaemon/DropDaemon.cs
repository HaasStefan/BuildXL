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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Ipc.Common;
using BuildXL.Ipc.ExternalApi;
using BuildXL.Ipc.Interfaces;
using BuildXL.Native.IO;
using BuildXL.Storage;
using BuildXL.Storage.Fingerprints;
using BuildXL.Tracing.CloudBuild;
using BuildXL.Utilities.CLI;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Core.Tasks;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.SBOMUtilities;
using Microsoft.ManifestGenerator;
using Microsoft.Sbom.Adapters;
using Microsoft.Sbom.Adapters.Report;
using Microsoft.Sbom.Contracts;
using Microsoft.Sbom.Contracts.Entities;
using Microsoft.Sbom.Contracts.Enums;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.Drop.App.Core.Tracing;
using Microsoft.VisualStudio.Services.Drop.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using Newtonsoft.Json.Linq;
using SbomCore;
using Serilog;
using Tool.ServicePipDaemon;
using static BuildXL.Ipc.ExternalApi.Commands.GetBuildManifesFileListResult;
using static BuildXL.Utilities.Core.FormattableStringEx;
using static Tool.ServicePipDaemon.Statics;

namespace Tool.DropDaemon
{
    /// <summary>
    /// Responsible for accepting and handling TCP/IP connections from clients.
    /// </summary>
    public sealed class DropDaemon : ServicePipDaemon.FinalizedByCreatorServicePipDaemon, IDisposable, IIpcOperationExecutor
    {
        private const int ServicePointParallelismForDrop = 200;

        /// <summary>
        /// Prefix for the error message of the exception that gets thrown when a symlink is attempted to be added to drop.
        /// </summary>
        internal const string SymlinkAddErrorMessagePrefix = "SymLinks may not be added to drop: ";

        private const string LogFileName = "DropDaemon";

        private const string DropClientLogDirectoryName = "DropClient";

        /// <nodoc/>
        public const string DropDLogPrefix = "(DropD) ";

        private const string IncompleteDropMarkerFileContent = "The build that produced this drop encountered some errors. This drop might be missing some files.";
        private const string IncompleteDropMarkerFileDropPath = "_INCOMPLETE_DROP_MARKER.txt";

        private static readonly int s_minIoThreadsForDrop = Environment.ProcessorCount * 10;

        private static readonly int s_minWorkerThreadsForDrop = Environment.ProcessorCount * 10;

        internal static readonly List<Option> ConfigOptions = new();

        internal static IEnumerable<Command> SupportedCommands => Commands.Values;

        private int m_logFileNameCounter;
        private int m_artifactTracerInitialized = 0;

        /// SBOM Generation
        private readonly ISbomGenerator m_sbomGenerator;
        private BsiMetadataExtractor m_bsiMetadataExtractor;
        private readonly string m_sbomGenerationOutputDirectory;

        /// <summary>
        /// Cached content of sealed directories.
        /// </summary>
        private readonly BuildXL.Utilities.Collections.ConcurrentBigMap<DirectoryArtifact, AsyncLazy<Possible<List<SealedDirectoryFile>>>> m_directoryArtifactContent = new();

        /// <summary>
        /// A mapping between a fully-qualified drop name and a corresponding dropConfig/VsoClient
        /// </summary>
        private readonly BuildXL.Utilities.Collections.ConcurrentBigMap<string, (DropConfig dropConfig, Lazy<Task<IDropClient>> lazyVsoClientTask)> m_vsoClients = new();

        /// <summary>
        /// These loggers should only be used for drop-related messages. Daemon-related messages should be logged via <see cref="ServicePipDaemon.ServicePipDaemon.Logger"/>
        /// </summary>
        private readonly BuildXL.Utilities.Collections.ConcurrentBigMap<string, Lazy<IIpcLogger>> m_dropSpecificLoggers = new();

        private readonly string m_dropClientLogDirectory;

        private readonly CounterCollection<DropDaemonCounter> m_counters = new CounterCollection<DropDaemonCounter>();

        private enum DropDaemonCounter
        {
            [CounterType(CounterType.Stopwatch)]
            BuildManifestComponentConversionDuration
        }

        #region Options and commands

        internal static readonly StrOption DropServiceConfigFile = RegisterDaemonConfigOption(new StrOption("dropServiceConfigFile")
        {
            ShortName = "c",
            HelpText = "Drop service configuration file",
            DefaultValue = null,
            Expander = (fileName) =>
            {
                var json = System.IO.File.ReadAllText(fileName);
                var jObject = JObject.Parse(json);
                return jObject.Properties().Select(prop => new ParsedOption(PrefixKind.Long, prop.Name, prop.Value.ToString()));
            },
        });

        internal static readonly StrOption DropNameOption = RegisterConfigOption(new StrOption("name")
        {
            ShortName = "n",
            HelpText = "Drop name",
            IsRequired = true,
        });

        internal static readonly StrOption OptionalDropName = new StrOption(DropNameOption.LongName)
        {
            ShortName = DropNameOption.ShortName,
            HelpText = DropNameOption.HelpText,
            IsRequired = false,
        };

        internal static readonly UriOption DropEndpoint = RegisterConfigOption(new UriOption("service")
        {
            ShortName = "s",
            HelpText = "Drop endpoint URI",
            IsRequired = true,
        });

        internal static readonly StrOption OptionalDropEndpoint = new StrOption(DropEndpoint.LongName)
        {
            ShortName = DropEndpoint.ShortName,
            HelpText = DropEndpoint.HelpText,
            IsRequired = false,
        };

        internal static readonly IntOption BatchSize = RegisterConfigOption(new IntOption("batchSize")
        {
            ShortName = "bs",
            HelpText = "OBSOLETE due to the hardcoded config. (Size of batches in which to send 'associate' requests)",
            IsRequired = false,
            DefaultValue = DropConfig.DefaultBatchSizeForAssociate,
        });

        internal static readonly IntOption MaxParallelUploads = RegisterConfigOption(new IntOption("maxParallelUploads")
        {
            ShortName = "mpu",
            HelpText = "Maximum number of uploads to issue to drop service in parallel",
            IsRequired = false,
            DefaultValue = DropConfig.DefaultMaxParallelUploads,
        });

        internal static readonly IntOption NagleTimeMillis = RegisterConfigOption(new IntOption("nagleTimeMillis")
        {
            ShortName = "nt",
            HelpText = "OBSOLETE due to the hardcoded config. (Maximum time in milliseconds to wait before triggering a batch 'associate' request)",
            IsRequired = false,
            DefaultValue = (int)DropConfig.DefaultNagleTimeForAssociate.TotalMilliseconds,
        });

        internal static readonly IntOption RetentionDays = RegisterConfigOption(new IntOption("retentionDays")
        {
            ShortName = "rt",
            HelpText = "Drop retention time in days",
            IsRequired = false,
            DefaultValue = (int)DropConfig.DefaultRetention.TotalDays,
        });

        internal static readonly IntOption HttpSendTimeoutMillis = RegisterConfigOption(new IntOption("httpSendTimeoutMillis")
        {
            HelpText = "Timeout for http requests",
            IsRequired = false,
            DefaultValue = (int)DropConfig.DefaultHttpSendTimeout.TotalMilliseconds,
        });

        internal static readonly BoolOption EnableTelemetry = RegisterConfigOption(new BoolOption("enableTelemetry")
        {
            ShortName = "t",
            HelpText = "Verbose logging",
            IsRequired = false,
            DefaultValue = DropConfig.DefaultEnableTelemetry,
        });

        internal static readonly BoolOption EnableChunkDedup = RegisterConfigOption(new BoolOption("enableChunkDedup")
        {
            ShortName = "cd",
            HelpText = "Chunk level dedup",
            IsRequired = false,
            DefaultValue = DropConfig.DefaultEnableChunkDedup,
        });

        internal static readonly StrOption OptionalDropDomainId = RegisterConfigOption(new StrOption("domainId")
        {
            ShortName = "ddid",
            HelpText = "Optional drop domain id setting.",
            IsRequired = false,
            DefaultValue = null,
        });

        internal static readonly BoolOption GenerateBuildManifest = RegisterConfigOption(new BoolOption("generateBuildManifest")
        {
            ShortName = "gbm",
            HelpText = "Generate a Build Manifest",
            IsRequired = false,
            DefaultValue = DropConfig.DefaultGenerateBuildManifest,
        });

        internal static readonly BoolOption SignBuildManifest = RegisterConfigOption(new BoolOption("signBuildManifest")
        {
            ShortName = "sbm",
            HelpText = "Sign the Build Manifest",
            IsRequired = false,
            DefaultValue = DropConfig.DefaultSignBuildManifest,
        });

        internal static readonly StrOption SbomPackageName = RegisterConfigOption(new StrOption("sbomPackageName")
        {
            ShortName = "sbpkg",
            HelpText = "Custom SBOM PackageName",
            IsRequired = false,
        });

        internal static readonly StrOption SbomPackageVersion = RegisterConfigOption(new StrOption("sbomPackageVersion")
        {
            ShortName = "sbpkgver",
            HelpText = "Custom SBOM PackageVersion",
            IsRequired = false,
        });

        internal static readonly StrOption Repo = RegisterConfigOption(new StrOption("repo")
        {
            ShortName = "r",
            HelpText = "Repo location",
            IsRequired = false,
            DefaultValue = string.Empty,
        });

        internal static readonly StrOption Branch = RegisterConfigOption(new StrOption("branch")
        {
            ShortName = "b",
            HelpText = "Git branch name",
            IsRequired = false,
            DefaultValue = string.Empty,
        });

        internal static readonly StrOption CommitId = RegisterConfigOption(new StrOption("commitId")
        {
            ShortName = "ci",
            HelpText = "Git CommitId",
            IsRequired = false,
            DefaultValue = string.Empty,
        });

        internal static readonly StrOption CloudBuildId = RegisterConfigOption(new StrOption("cloudBuildId")
        {
            ShortName = "cbid",
            HelpText = "RelativeActivityId",
            IsRequired = false,
            DefaultValue = string.Empty,
        });

        internal static readonly StrOption BsiFileLocation = RegisterConfigOption(new StrOption("bsiFileLocation")
        {
            ShortName = "bsi",
            HelpText = "Represents the BuildSessionInfo: bsi.json file path.",
            IsRequired = false,
            DefaultValue = string.Empty,
        });

        internal static readonly StrOption MakeCatToolPath = RegisterConfigOption(new StrOption("makeCatToolPath")
        {
            ShortName = "makecat",
            HelpText = "Represents the Path to makecat.exe for Build Manifest Catalog generation.",
            IsRequired = false,
            DefaultValue = string.Empty,
        });

        internal static readonly StrOption EsrpManifestSignToolPath = RegisterConfigOption(new StrOption("esrpManifestSignToolPath")
        {
            ShortName = "esrpTool",
            HelpText = "Represents the Path to EsrpManifestSign.exe for Build Manifest Catalog Signing.",
            IsRequired = false,
            DefaultValue = string.Empty,
        });

        internal static readonly BoolOption ReportIndidualDropTelemetry = RegisterConfigOption(new BoolOption("reportTelemetry")
        {
            ShortName = "rdt",
            HelpText = "Whether to report individual drop telemetry",
            IsRequired = false,
            DefaultValue = true,
        });

        // The environment variable for pat should match the env passed through to create drop in dsc
        internal static readonly StrOption PersonalAccessTokenEnv = RegisterConfigOption(new StrOption("PersonalAccessTokenEnv")
        {
            ShortName = "patenv",
            HelpText = "personal access token environment",
            IsRequired = false,
            DefaultValue = string.Empty,
        });

        internal static readonly BoolOption EnableArtifactTracer = RegisterConfigOption(new BoolOption("enableArtifactTracer")
        {
            ShortName = "eat",
            HelpText = "Enables Drop App Tracer",
            IsRequired = false,
            DefaultValue = DropConfig.DefaultEnableArtifactTracer,
        });

        internal static readonly BoolOption UploadBcdeFileToDrop = RegisterConfigOption(new BoolOption("uploadBcdeFileToDrop")
        {
            ShortName = "ubcdef",
            HelpText = "Upload BCDE file to Drop",
            IsRequired = false,
            DefaultValue = DropConfig.DefaultUploadBcdeFileToDrop,
        });

        internal static readonly StrOption SessionId = RegisterConfigOption(new StrOption("sessionId")
        {
            ShortName = "sid",
            HelpText = "Optional guid to use as a session id when communicating to AzDO.",
            IsRequired = false,
            // TODO: Remove after golden update (#2104026)
            DefaultValue = Environment.GetEnvironmentVariable("Q_SESSION_GUID")
        });

        // ==============================================================================
        // 'addfile' and 'addartifacts' parameters
        // ==============================================================================
        internal static readonly StrOption RelativeDropPath = new StrOption("dropPath")
        {
            ShortName = "d",
            HelpText = "Relative drop path",
            IsRequired = false,
            IsMultiValue = true,
        };

        internal static readonly StrOption RelativeDirectoryDropPath = new StrOption("directoryDropPath")
        {
            ShortName = "dird",
            HelpText = "Relative drop path for directory",
            IsRequired = false,
            IsMultiValue = true,
        };

        internal static readonly StrOption DirectoryContentFilter = new StrOption("directoryFilter")
        {
            ShortName = "dcfilter",
            HelpText = "Directory content filter (only files that match the filter will be added to drop).",
            DefaultValue = null,
            IsRequired = false,
            IsMultiValue = true,
        };

        internal static readonly BoolOption DirectoryFilterUseRelativePath = new BoolOption("directoryFilterUseRelativePath")
        {
            ShortName = "dfurp",
            HelpText = "Whether to apply regex to file's relative path instead of a full path.",
            DefaultValue = false,
            IsRequired = false,
            IsMultiValue = true,
        };

        internal static readonly StrOption DirectoryRelativePathReplace = new StrOption("directoryRelativePathReplace")
        {
            ShortName = "drpr",
            HelpText = "Relative path replace arguments.",
            DefaultValue = null,
            IsRequired = false,
            IsMultiValue = true,
        };

        internal static readonly Command StartNoDropCmd = RegisterCommand(
            name: "start-nodrop",
            description: @"Starts a server process without a backing VSO drop client (useful for testing/pinging the daemon).",
            options: new Option[] { IpcServerMonikerOptional },
            needsIpcClient: false,
            clientAction: (conf, _) =>
            {
                var daemonConfig = CreateDaemonConfig(conf);
                var dropServiceConfig = CreateDropServiceConfig(conf);
                using (var client = CreateClient(conf.Get(IpcServerMonikerOptional), daemonConfig))
                using (var daemon = new DropDaemon(conf.Config.Parser, daemonConfig, dropServiceConfig, client: client))
                {
                    daemon.Start();
                    daemon.Completion.GetAwaiter().GetResult();
                    return 0;
                }
            });

        /// The start command does not take name and service endpoint options, but we have to recognize them
        /// for backwards compatibility so we take them optional instead.
        private static List<Option> StartConfigOptions
            => ConfigOptions
                .Except(new Option[] { DropNameOption, DropEndpoint })
                .Union(new[] { OptionalDropName, OptionalDropEndpoint, IpcServerMonikerOptional })
                .ToList();

        internal static readonly Command StartCmd = RegisterCommand(
           name: "start",
           description: "Starts the server process.",
           options: StartConfigOptions,
           needsIpcClient: false,
           clientAction: (conf, _) =>
           {
               SetupThreadPoolAndServicePoint(s_minWorkerThreadsForDrop, s_minIoThreadsForDrop, ServicePointParallelismForDrop);
               var daemonConf = CreateDaemonConfig(conf);
               var serviceConf = CreateDropServiceConfig(conf);

               if (daemonConf.MaxConcurrentClients <= 1)
               {
                   conf.Logger.Error($"Must specify at least 2 '{nameof(DaemonConfig.MaxConcurrentClients)}' when running DropDaemon to avoid deadlock when stopping this daemon from a different client");
                   return -1;
               }

               using (var client = CreateClient(conf.Get(IpcServerMonikerOptional), daemonConf))
               using (var daemon = new DropDaemon(
                   parser: conf.Config.Parser,
                   daemonConfig: daemonConf,
                   serviceConfig: serviceConf,
                   client: client))
               {
                   daemon.Start();
                   daemon.Completion.GetAwaiter().GetResult();
                   return 0;
               }
           });

        internal static readonly Command StartDaemonCmd = RegisterCommand(
           name: "start-daemon",
           description: "Starts the server process in background (as daemon).",
           options: ConfigOptions,
           needsIpcClient: false,
           clientAction: (conf, _) =>
           {
               using (var daemon = new Process())
               {
                   bool shellExecute = conf.Get(ShellExecute);
                   daemon.StartInfo.FileName = AssemblyHelper.GetAssemblyLocation(System.Reflection.Assembly.GetEntryAssembly());
                   daemon.StartInfo.Arguments = "start " + conf.Config.Render();
                   daemon.StartInfo.LoadUserProfile = false;
                   daemon.StartInfo.UseShellExecute = shellExecute;
                   daemon.StartInfo.CreateNoWindow = !shellExecute;
                   daemon.Start();
               }

               return 0;
           });

        internal static readonly Command CreateDropCmd = RegisterCommand(
           name: "create",
           description: "[RPC] Invokes the 'create' operation.",
           options: ConfigOptions,
           clientAction: SyncRPCSend,
           serverAction: async (conf, dropDaemon) =>
           {
               var dropConfig = CreateDropConfig(conf);
               var daemon = dropDaemon as DropDaemon;
               var name = FullyQualifiedDropName(dropConfig);
               var logger = daemon.GetDropSpecificLogger(dropConfig);

               logger.Info($"[CREATE]: Started at '{name}'");
               if (!BuildManifestHelper.VerifyBuildManifestRequirements(dropConfig, daemon.DropServiceConfig, out string errMessage))
               {
                   logger.Error($"[CREATE]: Cannot create drop due to an invalid build manifest configuration: {errMessage}");
                   return new IpcResult(IpcResultStatus.InvalidInput, errMessage);
               }

               daemon.EnsureVsoClientIsCreated(dropConfig);
               IIpcResult result = await daemon.CreateAsync(name);
               logger.Info($"[CREATE]: {result}");
               return result;
           });

        internal static readonly Command FinalizeCmd = RegisterCommand(
            name: "finalize",
            description: "[RPC] Invokes the 'finalize' operation for all drops",
            clientAction: SyncRPCSend,
            serverAction: async (conf, dropDaemon) =>
            {
                var daemon = dropDaemon as DropDaemon;
                daemon.Logger.Info("[FINALIZE] Started finalizing all running drops.");

                // If a drop daemon was started but no drops were produced, there will be nothing for us to finalize.
                if (daemon.m_vsoClients.Count == 0)
                {
                    daemon.Logger.Info("There are no active drops to finalize.");
                    return IpcResult.Success();
                }

                // Build manifest logic is not a part of the FinalizeAsync/DoFinalize because daemon-wide finalization can be either triggered
                // by a finalize call from BuildXL or by the logic in FinalizedByCreatorServicePipDaemon. We do not want to create manifests if
                // some of the upstream drop operations failed or did not run at all (manifests created in such cases might not represent drops
                // that a build was expected to produce).
                // If we receive a finalize command, we are guaranteed that all upstream drop operations were successful (or in case of finalizeDrop,
                // all operations for a particular drop were successful).
                // Note: if there is a finalizeDrop call for drop_A, that call is successfully executed, and an operation for another drop (drop_B)
                // fails, both drops (drop_A and drop_B) will be finalized, but only drop_A will contain build manifest.
                var buildManifestResult = await daemon.ProcessBuildManifestsAsync();
                if (!buildManifestResult.Succeeded)
                {
                    // drop-specific error is already logged
                    daemon.Logger.Info($"[FINALIZE] Operation failed while processing a build manifest.");
                    return buildManifestResult;
                }

                IIpcResult result = await daemon.FinalizeAsync();
                LogIpcResult(daemon.Logger, LogLevel.Info, "[FINALIZE] ", result);
                return result;
            });

        internal static readonly Command FinalizeDropCmd = RegisterCommand(
           name: "finalizeDrop",
           description: "[RPC] Invokes the 'finalize' operation for a particular drop",
           options: ConfigOptions.Union(new[] { IpcServerMonikerOptional }),
           clientAction: SyncRPCSend,
           serverAction: async (conf, dropDaemon) =>
           {
               var daemon = dropDaemon as DropDaemon;
               var dropConfig = CreateDropConfig(conf);
               var logger = daemon.GetDropSpecificLogger(dropConfig);
               logger.Info($"[FINALIZE] Started finalizing '{dropConfig.Name}'.");

               var buildManifestResult = await daemon.ProcessBuildManifestForDropAsync(dropConfig);
               if (!buildManifestResult.Succeeded)
               {
                   logger.Info($"[FINALIZE] Operation failed while processing a build manifest.");
                   return buildManifestResult;
               }

               IIpcResult result = await daemon.FinalizeSingleDropAsync(dropConfig);
               LogIpcResult(logger, LogLevel.Info, "[FINALIZE] ", result);
               return result;
           });

        internal static readonly Command FinalizeDropAndStopDaemonCmd = RegisterCommand(
            name: "finalize-and-stop",
            description: "[RPC] Invokes the 'finalize' operation; then stops the daemon.",
            clientAction: SyncRPCSend,
            serverAction: Command.Compose(FinalizeCmd.ServerAction, StopDaemonCmd.ServerAction));

        internal static readonly Command AddFileToDropCmd = RegisterCommand(
            name: "addfile",
            description: "[RPC] invokes the 'addfile' operation.",
            options: ConfigOptions.Union(new Option[] { File, RelativeDropPath, HashOptional }),
            clientAction: SyncRPCSend,
            serverAction: async (conf, dropDaemon) =>
            {
                var daemon = dropDaemon as DropDaemon;
                var dropConfig = CreateDropConfig(conf);
                var logger = daemon.GetDropSpecificLogger(dropConfig);
                logger.Info("[ADDFILE] Started");
                string filePath = conf.Get(File);
                string hashValue = conf.Get(HashOptional);
                var contentInfo = string.IsNullOrEmpty(hashValue) ? null : (FileContentInfo?)FileContentInfo.Parse(hashValue);
                var dropItem = new DropItemForFile(DropDaemon.FullyQualifiedDropName(dropConfig), filePath, conf.Get(RelativeDropPath), contentInfo);
                IIpcResult result = System.IO.File.Exists(filePath)
                    ? await daemon.AddFileAsync(dropItem)
                    : new IpcResult(IpcResultStatus.InvalidInput, "file '" + filePath + "' does not exist");
                LogIpcResult(logger, LogLevel.Info, "[ADDFILE] ", result);
                return result;
            });

        internal static readonly Command AddArtifactsToDropCmd = RegisterCommand(
            name: "addartifacts",
            description: "[RPC] invokes the 'addartifacts' operation.",
            options: ConfigOptions.Union(new Option[] { IpcServerMonikerRequired, File, FileId, HashOptional, RelativeDropPath, Directory, DirectoryId, RelativeDirectoryDropPath, DirectoryContentFilter, DirectoryFilterUseRelativePath, DirectoryRelativePathReplace }),
            clientAction: SyncRPCSend,
            serverAction: async (conf, dropDaemon) =>
            {
                var daemon = dropDaemon as DropDaemon;
                var dropConfig = CreateDropConfig(conf);
                var logger = daemon.GetDropSpecificLogger(dropConfig);
                var commandId = Guid.NewGuid();
                logger.Info($"[command:{commandId}] [ADDARTIFACTS] Started");
                daemon.EnsureVsoClientIsCreated(dropConfig);

                var result = await AddArtifactsToDropInternalAsync(conf, daemon, logger, commandId);

                LogIpcResult(logger, LogLevel.Info, $"[command:{commandId}] [ADDARTIFACTS]: ", result);
                // Trim the payload before sending the result.
                return SuccessOrFirstError(result);
            });

        #endregion

        /// <summary>
        /// Drop daemon service configuration
        /// </summary>
        public DropServiceConfig DropServiceConfig { get; }

        /// <summary>
        /// Creates DropServiceConfig using the values specified on the ConfiguredCommand
        /// </summary>        
        public static DropServiceConfig CreateDropServiceConfig(ConfiguredCommand conf)
        {
            return new DropServiceConfig(bsiFileLocation: conf.Get(BsiFileLocation),
                makeCatToolPath: conf.Get(MakeCatToolPath),
                esrpManifestSignToolPath: conf.Get(EsrpManifestSignToolPath));
        }

        /// <summary>
        /// The purpose of this ctor is to force 'predictable' initialization of static fields.
        /// </summary>
        static DropDaemon()
        {
            // noop
        }

        /// <nodoc />
        public DropDaemon(IParser parser, DaemonConfig daemonConfig, DropServiceConfig serviceConfig, IIpcProvider rpcProvider = null, Client client = null)
            : base(parser,
                   daemonConfig,
                   !string.IsNullOrWhiteSpace(daemonConfig?.LogDir) ? new FileLogger(daemonConfig.LogDir, LogFileName, daemonConfig.Moniker, daemonConfig.Verbose, DropDLogPrefix) : daemonConfig.Logger,
                   rpcProvider,
                   client)
        {
            DropServiceConfig = serviceConfig;
            m_sbomGenerator = new SbomGenerator(logger: new SBOMLoggingWrapper(Logger));
            m_sbomGenerationOutputDirectory = !string.IsNullOrWhiteSpace(daemonConfig?.LogDir) ? daemonConfig.LogDir : Path.GetTempPath();
            m_logFileNameCounter = 0;
            if (!string.IsNullOrWhiteSpace(daemonConfig?.LogDir))
            {
                m_dropClientLogDirectory = Path.Combine(daemonConfig.LogDir, DropClientLogDirectoryName);
                if (!System.IO.Directory.Exists(m_dropClientLogDirectory))
                {
                    System.IO.Directory.CreateDirectory(Path.Combine(daemonConfig.LogDir, DropClientLogDirectoryName));
                }
            }
        }

        internal static void EnsureCommandsInitialized()
        {
            Contract.Assert(Commands != null);

            // these operations are quite expensive, however, we expect to call this method only once per drop, so it should cause any perf downgrade
            var numCommandsBase = typeof(ServicePipDaemon.ServicePipDaemon).GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(Command)).Count();
            var numCommandsDropD = typeof(DropDaemon).GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(Command)).Count();

            Contract.Assert(Commands.Count == numCommandsBase + numCommandsDropD, $"The list of commands was not properly initialized (# of initialized commands = {Commands.Count}; # of ServicePipDaemon commands = {numCommandsBase}; # of DropDaemon commands = {numCommandsDropD})");
        }

        /// <summary>
        /// Creates the drop.  Handles drop-related exceptions by omitting their stack traces.
        /// In all cases emits an appropriate <see cref="DropCreationEvent"/> indicating the
        /// result of this operation.
        /// </summary>
        protected override async Task<IIpcResult> DoCreateAsync(string name)
        {
            if (name == null)
            {
                return new IpcResult(IpcResultStatus.ExecutionError, "Name cannot be null when creating a drop.");
            }

            if (!m_vsoClients.TryGetValue(name, out var configAndClient))
            {
                return new IpcResult(IpcResultStatus.ExecutionError, $"Could not find VsoClient for a provided drop name: '{name}'");
            }

            DropCreationEvent dropCreationEvent =
                await SendDropEtwEventAsync(
                    WrapDropErrorsIntoDropEtwEvent(() => InternalCreateAsync(configAndClient.lazyVsoClientTask.Value)),
                    configAndClient.lazyVsoClientTask.Value);

            if (dropCreationEvent.Succeeded)
            {
                return IpcResult.Success(I($"Drop '{configAndClient.dropConfig.Name}' created."));
            }
            else
            {
                // Remove the client so finalization and telemetry upload is skipped for this drop.
                m_vsoClients.RemoveKey(name);
                (await configAndClient.lazyVsoClientTask.Value).Dispose();
                return new IpcResult(ParseIpcStatus(dropCreationEvent.AdditionalInformation), dropCreationEvent.ErrorMessage);
            }
        }

        /// <summary>
        ///     Invokes the 'drop addfile' operation by delegating to <see cref="IDropClient.AddFileAsync"/>.
        ///     Handles drop-related exceptions by omitting their stack traces.
        /// </summary>
        public Task<IIpcResult> AddFileAsync(IDropItem dropItem)
        {
            return AddFileAsync(dropItem, IsSymLinkOrMountPoint);
        }

        internal async Task<IIpcResult> AddFileAsync(IDropItem dropItem, Func<string, bool> symlinkTester)
        {
            Contract.Requires(dropItem != null);

            // Check if the file is a symlink, only if the file exists on disk at this point; if it is a symlink, reject it outright.
            if (System.IO.File.Exists(dropItem.FullFilePath) && symlinkTester(dropItem.FullFilePath))
            {
                return new IpcResult(IpcResultStatus.InvalidInput, SymlinkAddErrorMessagePrefix + dropItem.FullFilePath);
            }

            if (!m_vsoClients.TryGetValue(dropItem.FullyQualifiedDropName, out var configAndClient))
            {
                return new IpcResult(IpcResultStatus.ExecutionError, $"Could not find VsoClient for a provided drop name: '{dropItem.FullyQualifiedDropName}' (file '{dropItem.FullFilePath}')");
            }

            return await WrapDropErrorsIntoIpcResult(async () =>
            {
                IDropClient dropClient = await configAndClient.lazyVsoClientTask.Value;
                AddFileResult result = await dropClient.AddFileAsync(dropItem);

                switch (result)
                {
                    case AddFileResult.Associated:
                    case AddFileResult.UploadedAndAssociated:
                    case AddFileResult.SkippedAsDuplicate:
                        return IpcResult.Success(I($"File '{dropItem.FullFilePath}' {result} under '{dropItem.RelativeDropPath}' in drop '{configAndClient.dropConfig.Name}'."));
                    case AddFileResult.RegisterFileForBuildManifestFailure:
                        return new IpcResult(IpcResultStatus.ExecutionError, $"Failure during BuildManifest Hash generation for File '{dropItem.FullFilePath}' {result} under '{dropItem.RelativeDropPath}' in drop '{configAndClient.dropConfig.Name}'.");
                    default:
                        return new IpcResult(IpcResultStatus.ExecutionError, $"Unhandled drop result: {result}");
                }
            });
        }

        private async Task<IIpcResult> ProcessBuildManifestsAsync()
        {
            var tasks = m_vsoClients.Values.Select(async client =>
                {
                    await Task.Yield();
                    var dropClient = await client.lazyVsoClientTask.Value;
                    // return early if we have already finalized this drop
                    if (dropClient.AttemptedFinalization)
                    {
                        return IpcResult.Success();
                    }

                    return await ProcessBuildManifestForDropAsync(client.dropConfig);
                }).ToArray();

            var ipcResults = await TaskUtilities.SafeWhenAll(tasks);
            return IpcResult.Merge(ipcResults);
        }

        private async Task<IIpcResult> ProcessBuildManifestForDropAsync(DropConfig dropConfig)
        {
            // Get the config saved on create, which holds the build manifest settings
            var dropName = FullyQualifiedDropName(dropConfig);
            var logger = GetDropSpecificLogger(dropConfig);
            if (m_vsoClients.TryGetValue(dropName, out var configAndClient))
            {
                dropConfig = configAndClient.dropConfig;
            }

            if (!dropConfig.GenerateBuildManifest)
            {
                return IpcResult.Success();
            }

            var bsiResult = await UploadBsiFileAsync(dropConfig);
            if (!bsiResult.Succeeded)
            {
                logger.Error($"[FINALIZE ({dropConfig.Name})] Failure occurred during BuildSessionInfo (bsi) upload: {bsiResult.Payload}");
                return bsiResult;
            }

            var buildManifestResult = await GenerateAndUploadBuildManifestFileWithSignedCatalogAsync(dropConfig);
            if (!buildManifestResult.Succeeded)
            {
                logger.Error($"[FINALIZE ({dropConfig.Name})] Failure occurred during Build Manifest upload: {buildManifestResult.Payload}");
                return buildManifestResult;
            }

            if (dropConfig.UploadBcdeFileToDrop)
            {
                var bcdeUploadResult = await UploadBcdeFileAsync(dropConfig);
                if (!bcdeUploadResult.Succeeded)
                {
                    logger.Error($"[FINALIZE ({dropConfig.Name})] Failure occurred during BCDE file upload: {bcdeUploadResult.Payload}");
                    return bcdeUploadResult;
                }
            }

            return IpcResult.Success();
        }

        /// <summary>
        /// Uploads the bsi.json for the given drop.
        /// Should be called only when DropConfig.GenerateBuildManifest is true.
        /// </summary>
        private async Task<IIpcResult> UploadBsiFileAsync(DropConfig dropConfig)
        {
            Contract.Requires(dropConfig.GenerateBuildManifest, "GenerateBuildManifestData API called even though Build Manifest Generation is Disabled in DropConfig");

            if (!System.IO.File.Exists(DropServiceConfig.BsiFileLocation))
            {
                return new IpcResult(IpcResultStatus.ManifestGenerationError, $"BuildSessionInfo not found at provided BsiFileLocation: '{DropServiceConfig.BsiFileLocation}'");
            }

            var bsiDropItem = new DropItemForFile(FullyQualifiedDropName(dropConfig), DropServiceConfig.BsiFileLocation, relativeDropPath: BuildManifestHelper.DropBsiPath);
            return await AddFileAsync(bsiDropItem);
        }

        /// <summary>
        /// Uploads the bcde-output.json(Component detection output file) for the given drop.
        /// Should be called only when DropConfig.UploadBcdeFileToDrop is true.
        /// </summary>
        private async Task<IIpcResult> UploadBcdeFileAsync(DropConfig dropConfig)
        {
            Contract.Requires(dropConfig.UploadBcdeFileToDrop, "UploadBcdeFileToDrop API called even though this feature is disabled in DropConfig");

            // Read Path for bcde output from environment, this should already be set by Cloudbuild
            var bcdeOutputJsonPath = Environment.GetEnvironmentVariable(Constants.ComponentGovernanceBCDEOutputFilePath);

            if (string.IsNullOrWhiteSpace(bcdeOutputJsonPath))
            {
                // This should only happen if CG didn't run before the build.
                return new IpcResult(IpcResultStatus.ExecutionError, "UploadBcdeFileToDrop parameter is not set. This indicates that component detection did not run before the build.");
            }
            else if (!System.IO.File.Exists(bcdeOutputJsonPath))
            {
                return new IpcResult(IpcResultStatus.ExecutionError, $"Component detection output file not found at path: '{bcdeOutputJsonPath}'");
            }

            var bcdeDropItem = new DropItemForFile(FullyQualifiedDropName(dropConfig), bcdeOutputJsonPath, relativeDropPath: BuildManifestHelper.DropBcdeFilePath);
            return await AddFileAsync(bcdeDropItem);
        }

        /// <summary>
        /// Generates and uploads the Manifest.json on the orchestrator using all file hashes computed and stored 
        /// by workers using <see cref="VsoClient.RegisterFilesForBuildManifestAsync"/> for the given drop.
        /// Should be called only when DropConfig.GenerateBuildManifest is true.
        /// </summary>
        private async Task<IIpcResult> GenerateAndUploadBuildManifestFileWithSignedCatalogAsync(DropConfig dropConfig)
        {
            Contract.Requires(dropConfig.GenerateBuildManifest, "GenerateBuildManifestData API called even though Build Manifest Generation is Disabled in DropConfig");

            const int NumberOfFilePerRequest = 10_000;
            string fullDropName = FullyQualifiedDropName(dropConfig);
            IEnumerable<BuildXL.Ipc.ExternalApi.Commands.BuildManifestFileInfo> fileList = Enumerable.Empty<BuildXL.Ipc.ExternalApi.Commands.BuildManifestFileInfo>();
            bool hasMoreData = false;

            do
            {
                m_logger.Info($"Calling GetBuildManifesFileList - drop: '{fullDropName}', count: {NumberOfFilePerRequest}.");
                var bxlResult = await ApiClient.GetBuildManifesFileList(fullDropName, NumberOfFilePerRequest);

                if (!bxlResult.Succeeded)
                {
                    return new IpcResult(IpcResultStatus.ExecutionError, $"GenerateBuildManifestData API call failed for Drop: {dropConfig.Name}. Failure: {bxlResult.Failure.DescribeIncludingInnerFailures()}");
                }

                if (bxlResult.Result.Status == OperationStatus.InternalError)
                {
                    return new IpcResult(IpcResultStatus.ExecutionError, $"Failed to create build manifest file list for Drop: {dropConfig.Name}. Failure: {bxlResult.Result.Error}");
                }
                else if (bxlResult.Result.Status == OperationStatus.UserError)
                {
                    return new IpcResult(IpcResultStatus.InvalidInput, $"Failed to create build manifest file list for Drop: {dropConfig.Name}. Failure: {bxlResult.Result.Error}");
                }

                Contract.Assert(bxlResult.Result.Status == OperationStatus.Success);

                m_logger.Info($"Received GetBuildManifesFileListResult - drop: '{fullDropName}', count: {bxlResult.Result.FileList.Count}, hasMoreData: {bxlResult.Result.HasMoreData}.");
                fileList = fileList.Concat(bxlResult.Result.FileList);
                hasMoreData = bxlResult.Result.HasMoreData;
            } while (hasMoreData);

            IEnumerable<SbomFile> manifestFileList = fileList.Select(ToSbomFile);

            string sbomGenerationRootDirectory = null;
            var logger = GetDropSpecificLogger(dropConfig);
            try
            {
                if (m_bsiMetadataExtractor == null)
                {
                    m_bsiMetadataExtractor = new BsiMetadataExtractor(DropServiceConfig.BsiFileLocation);
                }

                var sbomPackageName = string.IsNullOrEmpty(dropConfig.SbomPackageName) ? fullDropName : dropConfig.SbomPackageName;
                var metadata = m_bsiMetadataExtractor.ProduceSbomMetadata("BuildXL", sbomPackageName, dropConfig.SbomPackageVersion);

                // Create a temporary directory to be the root path of SBOM generation 
                // We should create a different directory for each drop, so we use the drop name as part of the path.
                sbomGenerationRootDirectory = Path.Combine(m_sbomGenerationOutputDirectory, dropConfig.Name);
                FileUtilities.CreateDirectory(sbomGenerationRootDirectory);

                Possible<IEnumerable<SbomPackage>> maybePackages;
                using (m_counters.StartStopwatch(DropDaemonCounter.BuildManifestComponentConversionDuration))
                {
                    maybePackages = await GetSbomPackagesAsync(logger);
                }

                if (!maybePackages.Succeeded)
                {
                    return new IpcResult(IpcResultStatus.ManifestGenerationError, $"Errors were encountered in ComponentDetectionToSbomPackageAdapter. Details: {maybePackages.Failure.Describe()}");
                }

                var packages = maybePackages.Result;
                logger.Info("Starting SBOM Generation");
                var specs = new List<SbomSpecification>() { new("SPDX", "2.2") };
                var result = await m_sbomGenerator.GenerateSbomAsync(sbomGenerationRootDirectory, manifestFileList, packages, metadata, specs);
                logger.Info("Finished SBOM Generation");

                if (!result.IsSuccessful)
                {
                    return new IpcResult(IpcResultStatus.ManifestGenerationError, $"Errors were encountered during SBOM generation. Details: {GetSbomGenerationErrorDetails(result.Errors)}");
                }
            }
            catch (Exception ex)
            {
                return new IpcResult(IpcResultStatus.ManifestGenerationError, $"Exception while generating an SBOM locally before drop upload: {ex}");
            }

            // Drop all generated files
            // TODO: The API will provide the paths of the generated files in the result in a future version 
            IList<IDropItem> dropItems = new List<IDropItem>();
            IList<(string Path, string FileName)> filesToSign = new List<(string, string)>();
            FileUtilities.EnumerateFiles(sbomGenerationRootDirectory, recursive: true, pattern: "*",
                 (directory, fileName, _, _) =>
                 {
                     // Use same directory structure as in the generated directory
                     var filePath = Path.Combine(directory, fileName);
                     if (!filePath.StartsWith(sbomGenerationRootDirectory, StringComparison.OrdinalIgnoreCase))
                     {
                         throw new InvalidOperationException($"The path {filePath} is not under {sbomGenerationRootDirectory}");
                     }
                     var relativeDropPath = filePath.Substring(sbomGenerationRootDirectory.Length);
                     var dropItem = new DropItemForFile(fullDropName, filePath, relativeDropPath);
                     dropItems.Add(dropItem);
                     filesToSign.Add((filePath, fileName));
                 });

            foreach (var item in dropItems)
            {
                var buildManifestUploadResult = await AddFileAsync(item);
                if (!buildManifestUploadResult.Succeeded)
                {
                    return new IpcResult(IpcResultStatus.ExecutionError, $"Failure occurred during Build Manifest upload: {buildManifestUploadResult.Payload}");
                }
            }

            if (!dropConfig.SignBuildManifest)
            {
                return IpcResult.Success("Unsigned Build Manifest generated and uploaded successfully");
            }

            var sw = Stopwatch.StartNew();
            var signManifestResult = await GenerateAndSignBuildManifestCatalogFileAsync(dropConfig, filesToSign);
            logger.Info($"Build Manifest signing via EsrpManifestSign completed in {sw.ElapsedMilliseconds} ms. Succeeded: {signManifestResult.Succeeded}");

            return signManifestResult;
        }

        private static string GetSbomGenerationErrorDetails(IList<EntityError> errors)
        {
            var sb = new StringBuilder();
            foreach (var error in errors)
            {
                sb.AppendLine($"Error of type {error.ErrorType} for entity {(error.Entity as FileEntity)?.Path ?? error.Entity.Id} of type {error.Entity.EntityType}:");
                sb.AppendLine(error.Details);
            }

            return sb.ToString();
        }

        private SbomFile ToSbomFile(BuildXL.Ipc.ExternalApi.Commands.BuildManifestFileInfo fileInfo)
        {
            return new()
            {
                Checksum = fileInfo.BuildManifestHashes.Select(h =>
                {
                    return new Checksum()
                    {
                        Algorithm = mapHashType(h.HashType),
                        ChecksumValue = h.ToHex()
                    };
                }),
                Path = fileInfo.RelativePath
            };

            static AlgorithmName mapHashType(HashType hashType)
            {
                return hashType switch
                {
                    HashType.SHA1 => AlgorithmName.SHA1,
                    HashType.SHA256 => AlgorithmName.SHA256,
                    HashType.Vso0 => CBHashAlgorithmName.VSO,
                    _ => throw new InvalidOperationException($"Unsupported hash type {hashType} requested in SBOM generation"),
                };
            }
        }

        /// <summary>
        /// Tries to convert output from component detection to a list of <see cref="SbomPackage"/>.
        /// </summary>
        /// <returns>
        /// A converted list of <see cref="SbomPackage"/> if successful.
        /// If not successful, errors messages will be logged and also returned as a Failure{string}.
        /// Any warnings raised by the tooling are also logged via the APIServer.
        /// </returns>
        private async Task<Possible<IEnumerable<SbomPackage>>> GetSbomPackagesAsync(IIpcLogger logger)
        {
            // Read Path for bcde output from environment, this should already be set by Cloudbuild
            var bcdeOutputJsonPath = Environment.GetEnvironmentVariable(Constants.ComponentGovernanceBCDEOutputFilePath);

            if (string.IsNullOrWhiteSpace(bcdeOutputJsonPath))
            {
                // This should only happen if CG didn't run before the build. This should be the exception, but CG can be disabled (via configuration) 
                // and the SBOM creation here can still happen without a set of packages.
                // Log a message on the ApiServer it and return an empty set.
                Analysis.IgnoreResult(await ApiClient.LogMessage($"[GetSbomPackages] The '{Constants.ComponentGovernanceBCDEOutputFilePath}' environment variable was not found. This happens when component governance on the build runner is disabled. Component detection data will not be included in build manifest.", isWarning: false));
                return new List<SbomPackage>();
            }
            else if (!System.IO.File.Exists(bcdeOutputJsonPath))
            {
                var errorMsg = $"[GetSbomPackages] Component detection output file not found at path '{bcdeOutputJsonPath}'. Component detection data will not be included in build manifest.";
                logger.Error(errorMsg);
                return new Failure<string>(errorMsg);
            }

            logger.Info($"[GetSbomPackages] Retrieving component detection package list from file at {bcdeOutputJsonPath}");

            var serilogLogger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.SbomLoggingSerilogSink(logger)
                .CreateLogger();

            var (adapterReport, packages) =
                new ComponentDetectionToSbomPackageAdapter(new Microsoft.Sbom.Common.OSUtils(serilogLogger, new Microsoft.Sbom.Common.EnvironmentWrapper()))
                .TryConvert(bcdeOutputJsonPath);

            int failureCount = 0;
            int warningCount = 0;
            StringBuilder adapterReportFailureDetails = new();
            StringBuilder adapterReportWarnings = new();
            foreach (var reportItem in adapterReport.Report)
            {
                switch (reportItem.Type)
                {
                    case AdapterReportItemType.Success:
                    {
                        if (!string.IsNullOrEmpty(reportItem.Details))
                        {
                            logger.Info("[ComponentDetectionToSbomPackageAdapter] Success. " + reportItem.Details);
                        }
                        break;
                    }
                    case AdapterReportItemType.Warning:
                    {
                        warningCount += 1;
                        if (!string.IsNullOrEmpty(reportItem.Details))
                        {
                            adapterReportWarnings.AppendLine(reportItem.Details);
                            logger.Warning("[ComponentDetectionToSbomPackageAdapter] " + reportItem.Details);
                        }
                        break;
                    }
                    case AdapterReportItemType.Failure:
                    {
                        failureCount += 1;
                        if (!string.IsNullOrEmpty(reportItem.Details))
                        {
                            adapterReportFailureDetails.AppendLine(reportItem.Details);
                            logger.Error("[ComponentDetectionToSbomPackageAdapter] " + reportItem.Details);
                        }
                        break;
                    }
                }
            }

            if (warningCount > 0)
            {
                var warningMsg = $"[GetSbomPackages] ComponentDetectionToSbomPackageAdapter executed with {warningCount} warnings. Details: {adapterReportWarnings}";
                Analysis.IgnoreResult(await ApiClient.LogMessage(warningMsg, isWarning: true));
            }

            if (failureCount > 0)
            {
                var errorMsg = $"[GetSbomPackages] ComponentDetectionToSbomPackageAdapter reported {failureCount} failures. Details: {adapterReportFailureDetails}";
                return new Failure<string>(errorMsg);
            }

            logger.Verbose($"[GetSbomPackages] Retrieved {packages.Count()} packages");
            return Possible.Create(packages);
        }

        /// <summary>
        /// Generates and uploads a catalog file for <see cref="BuildManifestHelper.BuildManifestFilename"/> and <see cref="BuildManifestHelper.BsiFilename"/>
        /// Should be called only when DropConfig.GenerateBuildManifest is true and DropConfig.SignBuildManifest is true.
        /// </summary>
        private async Task<IIpcResult> GenerateAndSignBuildManifestCatalogFileAsync(DropConfig dropConfig, IList<(string Path, string FileName)> buildManifestLocalFiles)
        {
            Contract.Requires(dropConfig.GenerateBuildManifest, "GenerateAndSignBuildManifestCatalogFileAsync API called even though Build Manifest Generation is Disabled in DropConfig");
            Contract.Requires(dropConfig.SignBuildManifest, "GenerateAndSignBuildManifestCatalogFileAsync API called even though SignBuildManifest is Disabled in DropConfig");

            var possibleGenerateCatalogResult = await BuildManifestHelper.GenerateSignedCatalogAsync(
                DropServiceConfig.MakeCatToolPath,
                DropServiceConfig.EsrpManifestSignToolPath,
                buildManifestLocalFiles,
                DropServiceConfig.BsiFileLocation);

            if (!possibleGenerateCatalogResult.Succeeded)
            {
                return new IpcResult(IpcResultStatus.SigningError, possibleGenerateCatalogResult.Failure.Describe());
            }

            string catPath = possibleGenerateCatalogResult.Result;

            var dropItem = new DropItemForFile(FullyQualifiedDropName(dropConfig), catPath, relativeDropPath: BuildManifestHelper.DropCatalogFilePath);
            var uploadCatFileResult = await AddFileAsync(dropItem);

            // Delete temporary file created during Build Manifest signing
            try
            {
                System.IO.File.Delete(catPath);
            }
            catch (IOException)
            {
                // Can be ignored
            }

            if (!uploadCatFileResult.Succeeded)
            {
                return new IpcResult(IpcResultStatus.ExecutionError, $"Failure occurred during Build Manifest CAT file upload: {uploadCatFileResult.Payload}");
            }

            return IpcResult.Success("Catalog file signed and uploaded successfully");
        }

        /// <summary>
        /// Finalizes the drop. Handles drop-related exceptions by omitting their stack traces.
        /// In all cases emits an appropriate <see cref="DropFinalizationEvent"/> indicating the
        /// result of this operation.
        /// </summary>
        protected override async Task<IIpcResult> DoFinalizeAsync(bool isFinalizeOnStop)
        {
            var finalizationTasks = m_vsoClients.Values.Select(async client =>
               {
                   var dropClient = await client.lazyVsoClientTask.Value;
                   if (dropClient.AttemptedFinalization)
                   {
                       return IpcResult.Success(I($"An attempt to finalize drop {client.dropConfig.Name} has already been made; skipping this finalization."));
                   }

                   return await FinalizeSingleDropAsync(client.dropConfig, client.lazyVsoClientTask.Value, isFinalizeOnStop);
               }).ToArray();

            var results = await TaskUtilities.SafeWhenAll(finalizationTasks);
            return results.Length > 0 
                ? IpcResult.Merge(results)
                // Ideally, we would return 'null' because we did no work, but the caller expects a result.
                // This code path is only possible if finalize is called by FinalizedByCreatorServicePipDaemon.RequestStop
                // (i.e., we did not create any drops). The result has no effect on the build status since it is a failed
                // build at this point; so Returning IpcResult.Success is appropriate here.
                : IpcResult.Success();
        }

        private async Task<IIpcResult> FinalizeSingleDropAsync(DropConfig dropConfig, Task<IDropClient> dropClientTask = null, bool isFinalizeOnStop = false)
        {
            await Task.Yield();
            if (dropClientTask == null)
            {
                var dropName = FullyQualifiedDropName(dropConfig);
                if (!m_vsoClients.TryGetValue(dropName, out var configAndClient))
                {
                    return new IpcResult(IpcResultStatus.ExecutionError, $"Could not find VsoClient for a provided drop name: '{dropName}'");
                }

                dropClientTask = configAndClient.lazyVsoClientTask.Value;
            }

            if (isFinalizeOnStop)
            {
                try
                {
                    // Finalize operation was triggered by FinalizedByCreatorServicePipDaemon and not by BuildXL.
                    // It means that the finalize command was not received (either the finalize pip did not run or
                    // the command could not be sent). In such a scenario, there is no guarantee that this particular
                    // drop contains all the files it is expected to contain. Add a marker file to signal this to a user.
                    var dropClient = await dropClientTask;
                    var markerFile = Path.GetTempFileName();
                    await System.IO.File.WriteAllTextAsync(markerFile, IncompleteDropMarkerFileContent);
                    var markerFileItem = new DropItemForFile(FullyQualifiedDropName(dropConfig), markerFile, IncompleteDropMarkerFileDropPath);
                    var uploadResult = await AddFileAsync(markerFileItem);
                    // Clean the temp file. We proceed regardless whether the file was deleted or not.
                    _ = FileUtilities.TryDeleteFile(markerFile);
                    if (uploadResult.Succeeded)
                    {
                        m_logger.Verbose(uploadResult.ToString());
                    }
                    else
                    {
                        // We failed to upload the marker file (this should be quite a rare event).
                        // We now have a choice to make - do we proceed or do we abort. If we proceed with finalization,
                        // a consumer of a drop might not be able to tell that the drop is incomplete (because the signals
                        // are misleading, i.e., the drop is finalized and the file that is meant to indicate incomplete
                        // drops is absent). It's safer to abort finalization in this case.
                        return uploadResult;
                    }
                }
                catch (Exception e)
                {
                    // No marker file - no finalization.
                    return new IpcResult(IpcResultStatus.ExecutionError, $"An error occurred while uploading an incomplete drop marker file. Error: {e.DemystifyToString()}");
                }
            }

            // We invoke 'finalize' regardless whether the drop is finalize (dropClient.IsFinalized) or not.
            var dropFinalizationEvent =
                await SendDropEtwEventAsync(
                    WrapDropErrorsIntoDropEtwEvent(() => InternalFinalizeAsync(dropClientTask)),
                    dropClientTask);

            return dropFinalizationEvent.Succeeded
                ? IpcResult.Success(I($"Drop {dropConfig.Name} finalized"))
                : new IpcResult(ParseIpcStatus(dropFinalizationEvent.AdditionalInformation), dropFinalizationEvent.ErrorMessage);
        }

        /// <nodoc />
        public override void Dispose()
        {
            ReportStatisticsAsync().GetAwaiter().GetResult();

            foreach (var kvp in m_vsoClients)
            {
                kvp.Value.lazyVsoClientTask.Value.Result.Dispose();
            }

            foreach (var dropLogger in m_dropSpecificLoggers.Values)
            {
                if (dropLogger.IsValueCreated)
                {
                    dropLogger.Value.Dispose();
                }
            }

            base.Dispose();
        }

        /// <summary>
        /// Invokes the 'drop create' operation by delegating to <see cref="IDropClient.CreateAsync"/>.
        ///
        /// If successful, returns <see cref="DropCreationEvent"/> with <see cref="DropOperationBaseEvent.Succeeded"/>
        /// set to true, <see cref="DropCreationEvent.DropExpirationInDays"/> set to drop expiration in days,
        /// and <see cref="DropOperationBaseEvent.AdditionalInformation"/> set to the textual representation
        /// of the returned <see cref="DropItem"/> object.
        ///
        /// Doesn't handle any exceptions.
        /// </summary>
        private async Task<DropCreationEvent> InternalCreateAsync(Task<IDropClient> vsoClientTask)
        {
            IDropClient dropClient = await vsoClientTask;
            DropItem dropItem = await dropClient.CreateAsync();
            return new DropCreationEvent()
            {
                Succeeded = true,
                AdditionalInformation = DropItemToString(dropItem),
                DropExpirationInDays = ComputeDropItemExpiration(dropItem),
            };
        }

        /// <summary>
        /// Invokes the 'drop finalize' operation by delegating to <see cref="IDropClient.FinalizeAsync"/>.
        ///
        /// If successful, returns <see cref="DropFinalizationEvent"/> with <see cref="DropOperationBaseEvent.Succeeded"/>
        /// set to true.
        ///
        /// Doesn't handle any exceptions.
        /// </summary>
        private async Task<DropFinalizationEvent> InternalFinalizeAsync(Task<IDropClient> dropClientTask)
        {
            var dropClient = await dropClientTask;
            await dropClient.FinalizeAsync();
            return new DropFinalizationEvent()
            {
                Succeeded = true,
            };
        }

        private async Task ReportStatisticsAsync()
        {
            var stats = m_counters.AsStatistics("DropDaemon");

            foreach (var (dropConfig, lazyVsoClientTask) in m_vsoClients.Values)
            {
                try
                {
                    var vsoClient = await lazyVsoClientTask.Value;
                    // First, get all basic stats and aggregate them. For back compat reasons, keep reporting size in megabytes.
                    var clientStats = vsoClient.GetStats(reportSizeInMegabytes: true);
                    if (clientStats == null || clientStats.Count == 0)
                    {
                        m_logger.Info("No stats recorded by drop client of type " + vsoClient.GetType().Name);
                        continue;
                    }

                    foreach (var statistic in clientStats)
                    {
                        if (!stats.ContainsKey(statistic.Key))
                        {
                            stats.Add(statistic.Key, 0);
                        }

                        stats[statistic.Key] += statistic.Value;
                    }

                    // If this is a real DropClient and ApiClient is specified, report individual drop details back to BuildXL,
                    // so they can be pushed into telemetry.
                    if (vsoClient is VsoClient client && ApiClient != null)
                    {
                        var possiblyReported = await client.ReportDropTelemetryDataAsync("DropDaemon");
                        if (possiblyReported.Succeeded && possiblyReported.Result)
                        {
                            m_logger.Info($"Telemetry for drop '{client.DropName}' successfully reported to BuildXL.");
                        }
                        else
                        {
                            var errorDescription = possiblyReported.Succeeded ? string.Empty : possiblyReported.Failure.Describe();
                            m_logger.Warning($"Reporting telemetry for drop '{client.DropName}' to BuildXL failed. {errorDescription}");
                        }
                    }
                }
                catch (Exception e)
                {
                    m_logger.Warning($"No stats collected for drop '{dropConfig.Name}' due to an error. Exception details: {e}");
                }
            }

            if (stats != null && stats.Any())
            {
                // log stats
                m_logger.Info("Statistics: ");
                m_logger.Info(string.Join(Environment.NewLine, stats.Select(s => s.Key + " = " + s.Value)));

                stats.AddRange(GetDaemonStats("DropDaemon"));

                // report stats to BuildXL (if ApiClient is specified)
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
                }
            }
        }

        private delegate TResult ErrorFactory<TResult>(string message, IpcResultStatus status);

        private static Task<IIpcResult> WrapDropErrorsIntoIpcResult(Func<Task<IIpcResult>> factory)
        {
            return HandleKnownErrorsAsync(
                factory,
                (errorMessage, status) => new IpcResult(status, errorMessage));
        }

        private static Task<TDropEvent> WrapDropErrorsIntoDropEtwEvent<TDropEvent>(Func<Task<TDropEvent>> factory) where TDropEvent : DropOperationBaseEvent
        {
            return HandleKnownErrorsAsync(
                factory,
                (errorMessage, errorKind) =>
                {
                    var dropEvent = Activator.CreateInstance<TDropEvent>();
                    dropEvent.Succeeded = false;
                    dropEvent.ErrorMessage = errorMessage;
                    dropEvent.AdditionalInformation = RenderIpcStatus(errorKind);
                    return dropEvent;
                });
        }

        private static string RenderIpcStatus(IpcResultStatus status)
        {
            return status.ToString();
        }

        /// <summary>
        /// BuildXL's classification of different <see cref="IpcResultStatus"/> values:
        ///   - <see cref="IpcResultStatus.InvalidInput"/>      --> <see cref="Keywords.UserError"/>
        ///   - <see cref="IpcResultStatus.TransmissionError"/> --> <see cref="Keywords.InfrastructureIssue"/>
        ///   - all other errors                                --> InternalError
        /// </summary>
        /// <remarks>
        /// Note that some exceptions will not be handled here. Everything that is not listed here will bubble up to
        /// IServer and will be handled there.
        /// </remarks>
        private static async Task<TResult> HandleKnownErrorsAsync<TResult>(Func<Task<TResult>> factory, ErrorFactory<TResult> errorValueFactory)
        {
            try
            {
                return await factory();
            }
            catch (VssUnauthorizedException e)
            {
                return errorValueFactory("[DROP AUTH ERROR] " + e.Message, IpcResultStatus.InvalidInput);
            }
            catch (VssResourceNotFoundException e)
            {
                return errorValueFactory("[DROP SERVICE ERROR] " + e.Message, IpcResultStatus.ExternalServiceError);
            }
            catch (DropServiceException e)
            {
                var status = e.Message.Contains("already exists")
                    ? IpcResultStatus.InvalidInput
                    : IpcResultStatus.ExternalServiceError;
                return errorValueFactory("[DROP SERVICE ERROR] " + e.Message, status);
            }
            catch (DaemonException e)
            {
                var status = IpcResultStatus.ExecutionError;
                if (e.Message.Contains(Statics.MaterializationResultIsSymlinkErrorPrefix))
                {
                    // Symlinks are not supported and any attempts to upload one are user errors.
                    status = IpcResultStatus.InvalidInput;
                }
                else if (e.Message.Contains(Statics.MaterializationResultFileNotFoundErrorPrefix)
                    || e.Message.Contains(Statics.MaterializationResultMaterializationFailedErrorPrefix))
                {
                    status = IpcResultStatus.ApiServerError;
                }

                return errorValueFactory("[DAEMON ERROR] " + e.Message, status);
            }
            catch (TimeoutException e)
            {
                // On the code path that is using this method, TimeoutException can only be thrown by
                // ReloadingDropServiceClient, i.e., a timed out call to an endpoint.
                return errorValueFactory("[DROP SERVICE ERROR] " + e.Message, IpcResultStatus.ExternalServiceError);
            }
        }

        private static string DropItemToString(DropItem dropItem)
        {
            try
            {
                return dropItem?.ToJson().ToString();
            }
#pragma warning disable ERP022 // TODO: This should really handle specific errors
            catch
            {
                return null;
            }
#pragma warning restore ERP022 // Unobserved exception in generic exception handler
        }

        private static int ComputeDropItemExpiration(DropItem dropItem)
        {
            DateTime? expirationDate;
            return dropItem.TryGetExpirationTime(out expirationDate) || expirationDate.HasValue
                ? (int)expirationDate.Value.Subtract(DateTime.UtcNow).TotalDays
                : -1;
        }

        private async Task<T> SendDropEtwEventAsync<T>(Task<T> task, Task<IDropClient> dropClient) where T : DropOperationBaseEvent
        {
            long startTime = DateTime.UtcNow.Ticks;
            T dropEvent = null;
            try
            {
                dropEvent = await task;
                return dropEvent;
            }
            finally
            {
                // if 'task' failed, create an event indicating an error
                if (dropEvent == null)
                {
                    dropEvent = Activator.CreateInstance<T>();
                    dropEvent.Succeeded = false;
                    dropEvent.ErrorMessage = "internal error";
                }

                // common properties: execution time, drop type, drop url
                dropEvent.ElapsedTimeTicks = DateTime.UtcNow.Ticks - startTime;
                dropEvent.DropType = "VsoDrop";
                dropEvent.DropUrl = (await dropClient).DropUrl;

                // send event
                m_etwLogger.Log(dropEvent);
            }
        }

        internal static DropConfig CreateDropConfig(ConfiguredCommand conf)
        {
            string domainId = (string)conf.Get(OptionalDropDomainId);

            Guid? sessionId = null;
            if (Guid.TryParse(conf.Get(SessionId), out var parsedSessionId))
            {
                sessionId = parsedSessionId;
            }

            return new DropConfig(
                dropName: conf.Get(DropNameOption),
                serviceEndpoint: conf.Get(DropEndpoint),
                maxParallelUploads: conf.Get(MaxParallelUploads),
                retention: TimeSpan.FromDays(conf.Get(RetentionDays)),
                httpSendTimeout: TimeSpan.FromMilliseconds(conf.Get(HttpSendTimeoutMillis)),
                enableTelemetry: conf.Get(EnableTelemetry),
                enableChunkDedup: conf.Get(EnableChunkDedup),
                enableArtifactTracer: conf.Get(EnableArtifactTracer),
                batchSize: conf.Get(BatchSize),
                dropDomainId: domainId,
                generateBuildManifest: conf.Get(GenerateBuildManifest),
                signBuildManifest: conf.Get(SignBuildManifest),
                sbomPackageName: conf.Get(SbomPackageName),
                sbomPackageVersion: conf.Get(SbomPackageVersion),
                reportTelemetry: conf.Get(ReportIndidualDropTelemetry),
                personalAccessTokenEnv: conf.Get(PersonalAccessTokenEnv),
                uploadBcdeFileToDrop: conf.Get(UploadBcdeFileToDrop),
                sessionId: sessionId);
        }

        private static T RegisterConfigOption<T>(T option) where T : Option => RegisterOption(ConfigOptions, option);

        private static Client CreateClient(string serverMoniker, IClientConfig config)
        {
            return serverMoniker != null
                ? Client.Create(IpcProvider, serverMoniker, config)
                : null;
        }

        private static async Task<IIpcResult> AddArtifactsToDropInternalAsync(ConfiguredCommand conf, DropDaemon daemon, IIpcLogger logger, Guid commandId)
        {
            var dropName = conf.Get(DropNameOption);
            var serviceEndpoint = conf.Get(DropEndpoint);
            var fullDropName = FullyQualifiedDropName(serviceEndpoint, dropName);

            var files = File.GetValues(conf.Config).ToArray();
            var fileIds = FileId.GetValues(conf.Config).ToArray();
            var hashes = HashOptional.GetValues(conf.Config).ToArray();
            var dropPaths = RelativeDropPath.GetValues(conf.Config).ToArray();

            if (files.Length != fileIds.Length || files.Length != hashes.Length || files.Length != dropPaths.Length)
            {
                return new IpcResult(
                    IpcResultStatus.GenericError,
                    I($"File counts don't match: #files = {files.Length}, #fileIds = {fileIds.Length}, #hashes = {hashes.Length}, #dropPaths = {dropPaths.Length}"));
            }

            var directoryPaths = Directory.GetValues(conf.Config).ToArray();
            var directoryIds = DirectoryId.GetValues(conf.Config).ToArray();
            var directoryDropPaths = RelativeDirectoryDropPath.GetValues(conf.Config).ToArray();
            var directoryFilters = DirectoryContentFilter.GetValues(conf.Config).ToArray();
            var directoryFilterUseRelativePath = DirectoryFilterUseRelativePath.GetValues(conf.Config).ToArray();
            var directoryRelativePathsReplaceSerialized = DirectoryRelativePathReplace.GetValues(conf.Config).ToArray();

            if (directoryPaths.Length != directoryIds.Length
                || directoryPaths.Length != directoryDropPaths.Length
                || directoryPaths.Length != directoryFilters.Length
                || directoryPaths.Length != directoryFilterUseRelativePath.Length
                || directoryPaths.Length != directoryRelativePathsReplaceSerialized.Length)
            {
                return new IpcResult(
                    IpcResultStatus.GenericError,
                    I($"Directory counts don't match: #directories = {directoryPaths.Length}, #directoryIds = {directoryIds.Length}, #dropPaths = {directoryDropPaths.Length}, #directoryFilters = {directoryFilters.Length}, #directoryApplyFilterToRelativePath = {directoryFilterUseRelativePath.Length}, #directoryRelativePathReplace = {directoryRelativePathsReplaceSerialized.Length}"));
            }

            FileContentInfo[] parsedContentInfos = new FileContentInfo[files.Length];

            using (var pooledSb = Pools.GetStringBuilder())
            {
                var sb = pooledSb.Instance;
                sb.AppendLine($"[command:{commandId}] Payload:");
                for (int i = 0; i < files.Length; i++)
                {
                    sb.AppendFormat("{0}|{1}|{2}|{3}{4}", files[i], fileIds[i], hashes[i], dropPaths[i], Environment.NewLine);

                    Possible<FileContentInfo> parsedResult = await ParseFileContentAsync(daemon, hashes[i], fileIds[i], files[i]);
                    if (parsedResult.Succeeded)
                    {
                        parsedContentInfos[i] = parsedResult.Result;
                    }
                    else
                    {
                        return new IpcResult(IpcResultStatus.ExecutionError, parsedResult.Failure.Describe());
                    }
                }

                for (int i = 0; i < directoryPaths.Length; i++)
                {
                    sb.AppendFormat("{0}|{1}|{2}|{3}|{4}|{5}{6}",
                        directoryPaths[i], directoryIds[i], directoryDropPaths[i], directoryFilters[i], directoryFilterUseRelativePath[i], directoryRelativePathsReplaceSerialized[i], Environment.NewLine);
                }

                logger.Verbose(sb);
            }

            var possibleFilters = InitializeFilters(directoryFilters);
            if (!possibleFilters.Succeeded)
            {
                // bad regex can only be caused by bad input
                return new IpcResult(IpcResultStatus.InvalidInput, possibleFilters.Failure.Describe());
            }

            var possibleRelativePathReplacementArguments = InitializeRelativePathReplacementArguments(directoryRelativePathsReplaceSerialized);
            if (!possibleRelativePathReplacementArguments.Succeeded)
            {
                return new IpcResult(IpcResultStatus.ExecutionError, possibleRelativePathReplacementArguments.Failure.Describe());
            }

            var dropFileItemsKeyedByIsAbsent = Enumerable
                .Range(0, files.Length)
                .Select(i => new DropItemForBuildXLFile(
                    daemon.ApiClient,
                    fullDropName,
                    filePath: files[i],
                    fileId: fileIds[i],
                    fileContentInfo: parsedContentInfos[i],
                    relativeDropPath: dropPaths[i]))
                .ToLookup(f => WellKnownContentHashUtilities.IsAbsentFileHash(f.Hash));

            // If a user specified a particular file to be added to drop, this file must be a part of drop.
            // The missing files will not get into the drop, so we emit an error.
            if (dropFileItemsKeyedByIsAbsent[true].Any())
            {
                string missingFiles = string.Join(Environment.NewLine, dropFileItemsKeyedByIsAbsent[true].Select(f => $"{f.FullFilePath} ({f})"));
                return new IpcResult(
                    IpcResultStatus.InvalidInput,
                    I($"Cannot add the following files to drop because they do not exist:{Environment.NewLine}{missingFiles}"));
            }

            (IEnumerable<DropItemForBuildXLFile> dropDirectoryMemberItems, string error) = await CreateDropItemsForDirectoriesAsync(
                daemon,
                fullDropName,
                directoryPaths,
                directoryIds,
                directoryDropPaths,
                possibleFilters.Result,
                directoryFilterUseRelativePath,
                possibleRelativePathReplacementArguments.Result,
                logger,
                commandId);

            if (error != null)
            {
                return new IpcResult(IpcResultStatus.ExecutionError, error);
            }

            var groupedDirectoriesContent = dropDirectoryMemberItems.ToLookup(f => WellKnownContentHashUtilities.IsAbsentFileHash(f.Hash));

            // we allow missing files inside of directories only if those files are output files (e.g., optional or temporary files) 
            if (groupedDirectoriesContent[true].Any(f => !f.IsOutputFile))
            {
                return new IpcResult(
                    IpcResultStatus.InvalidInput,
                    I($"Uploading missing source file(s) is not supported:{Environment.NewLine}{string.Join(Environment.NewLine, groupedDirectoriesContent[true].Where(f => !f.IsOutputFile))}"));
            }

            // return early if there is nothing to upload
            if (!dropFileItemsKeyedByIsAbsent[false].Any() && !groupedDirectoriesContent[false].Any())
            {
                return new IpcResult(IpcResultStatus.Success, string.Empty);
            }

            return await AddDropItemsAsync(daemon, dropFileItemsKeyedByIsAbsent[false].Concat(groupedDirectoriesContent[false]));
        }

        private static Possible<RelativePathReplacementArguments[]> InitializeRelativePathReplacementArguments(string[] serializedValues)
        {
            const char DelimChar = '#';
            const string NoRereplacement = "##";

            /*
                Format:
                    Replacement arguments are not specified: "##"
                    Replacement arguments are specified:     "#{searchString}#{replaceString}#"
             */

            var initializedValues = new RelativePathReplacementArguments[serializedValues.Length];
            for (int i = 0; i < serializedValues.Length; i++)
            {
                if (serializedValues[i] == NoRereplacement)
                {
                    initializedValues[i] = RelativePathReplacementArguments.Invalid;
                    continue;
                }

                var arr = serializedValues[i].Split(DelimChar);
                if (arr.Length != 4
                    || arr[0].Length != 0
                    || arr[3].Length != 0)
                {
                    return new Failure<string>($"Failed to deserialize relative path replacement arguments: '{serializedValues[i]}'.");
                }

                initializedValues[i] = new RelativePathReplacementArguments(arr[1], arr[2]);
            }

            return initializedValues;
        }

        private static async Task<(DropItemForBuildXLFile[], string error)> CreateDropItemsForDirectoryAsync(
            DropDaemon daemon,
            string fullyQualifiedDropName,
            string directoryPath,
            string directoryId,
            string dropPath,
            Regex contentFilter,
            bool applyFilterToRelativePath,
            RelativePathReplacementArguments relativePathReplacementArgs,
            IIpcLogger logger,
            Guid commandId)
        {
            Contract.Requires(!string.IsNullOrEmpty(directoryPath));
            Contract.Requires(!string.IsNullOrEmpty(directoryId));
            Contract.Requires(dropPath != null);

            if (daemon.ApiClient == null)
            {
                return (null, "ApiClient is not initialized");
            }

            DirectoryArtifact directoryArtifact = BuildXL.Ipc.ExternalApi.DirectoryId.Parse(directoryId);

            var maybeResult = await daemon.GetSealedDirectoryContentAsync(directoryArtifact, directoryPath);
            if (!maybeResult.Succeeded)
            {
                return (null, "could not get the directory content from BuildXL server: " + maybeResult.Failure.Describe());
            }

            var directoryContent = maybeResult.Result;
            logger.Log(
                LogLevel.Verbose,
                $"[command:{commandId}] (dirPath'{directoryPath}', dirId='{directoryId}') contains '{directoryContent.Count}' files:",
                directoryContent.SelectMany(f => f.RenderContent().Append(Environment.NewLine)),
                placeItemsOnSeparateLines: false);

            if (contentFilter != null)
            {
                var filteredContent = FilterDirectoryContent(directoryPath, directoryContent, contentFilter, applyFilterToRelativePath);
                logger.Verbose("{5} [dirId='{0}'] Filter '{1}' (applied to relative paths: '{4}') excluded {2} file(s) out of {3}", directoryId, contentFilter, directoryContent.Count - filteredContent.Count, directoryContent.Count, applyFilterToRelativePath, commandId);
                directoryContent = filteredContent;
            }

            List<DropItemForBuildXLFile> dropItemForBuildXLFiles = new List<DropItemForBuildXLFile>();

            var files = directoryContent
                // SharedOpaque directories might contain 'absent' output files. These are not real files, so we are excluding them.
                .Where(file => !WellKnownContentHashUtilities.IsAbsentFileHash(file.ContentInfo.Hash) || file.Artifact.IsSourceFile);

            foreach (SealedDirectoryFile file in files)
            {
                // The dropPath can be an empty relative path (i.e. '.') which we need to remove since even though it is not a valid
                // directory name for a Windows file system, it is a valid name for a drop and it doesn't get resolved properly
                var resolvedDropPath = dropPath == "." ? string.Empty : I($"{dropPath}/");
                var remoteFileName = I($"{resolvedDropPath}{GetRelativePath(directoryPath, file.FileName, relativePathReplacementArgs)}");
                var fileId = BuildXL.Ipc.ExternalApi.FileId.ToString(file.Artifact);

                var parsedFileContentInfoResult = await ParseFileContentAsync(daemon, file.ContentInfo.Render(), fileId, file.FileName);
                if (!parsedFileContentInfoResult.Succeeded)
                {
                    return (null, parsedFileContentInfoResult.Failure.Describe());
                }

                dropItemForBuildXLFiles.Add(new DropItemForBuildXLFile(
                    daemon.ApiClient,
                    fullyQualifiedDropName,
                    file.FileName,
                    fileId,
                    parsedFileContentInfoResult.Result,
                    remoteFileName));
            }

            return (dropItemForBuildXLFiles.ToArray(), null);
        }

        internal static List<SealedDirectoryFile> FilterDirectoryContent(string directoryPath, List<SealedDirectoryFile> directoryContent, Regex contentFilter, bool applyFilterToRelativePath)
        {
            var endsWithSlash = directoryPath[directoryPath.Length - 1] == Path.DirectorySeparatorChar || directoryPath[directoryPath.Length - 1] == Path.AltDirectorySeparatorChar;
            var startPosition = applyFilterToRelativePath ? (directoryPath.Length + (endsWithSlash ? 0 : 1)) : 0;
            // Note: if startPosition is not 0, and a regular expression uses ^ anchor to match the beginning of a relative path, no files will be matched.
            // In such cases, one must use \G anchor instead.
            // https://docs.microsoft.com/en-us/dotnet/api/system.text.regularexpressions.regex.match
            return directoryContent.Where(file => contentFilter.IsMatch(file.FileName, startPosition)).ToList();
        }

        internal static string GetRelativePath(string root, string file, RelativePathReplacementArguments pathReplacementArgs)
        {
            var rootEndsWithSlash =
                root[root.Length - 1] == System.IO.Path.DirectorySeparatorChar
                || root[root.Length - 1] == System.IO.Path.AltDirectorySeparatorChar;
            var relativePath = file.Substring(root.Length + (rootEndsWithSlash ? 0 : 1));
            // On Windows, file paths are case-insensitive.
            var stringCompareMode = OperatingSystemHelper.IsUnixOS ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (pathReplacementArgs.IsValid)
            {
                int searchStringPosition = relativePath.IndexOf(pathReplacementArgs.OldValue, stringCompareMode);
                if (searchStringPosition < 0)
                {
                    // no match found; return the path that we constructed so far
                    return relativePath;
                }

                // we are only replacing the first match
                return I($"{relativePath.Substring(0, searchStringPosition)}{pathReplacementArgs.NewValue}{relativePath.Substring(searchStringPosition + pathReplacementArgs.OldValue.Length)}");
            }

            return relativePath;
        }

        private static async Task<(IEnumerable<DropItemForBuildXLFile>, string error)> CreateDropItemsForDirectoriesAsync(
            DropDaemon daemon,
            string fullyQualifiedDropName,
            string[] directoryPaths,
            string[] directoryIds,
            string[] dropPaths,
            Regex[] contentFilters,
            bool[] applyFilterToRelativePath,
            RelativePathReplacementArguments[] relativePathsReplacementArgs,
            IIpcLogger logger,
            Guid commandId)
        {
            Contract.Requires(directoryPaths != null);
            Contract.Requires(directoryIds != null);
            Contract.Requires(dropPaths != null);
            Contract.Requires(contentFilters != null);
            Contract.Requires(directoryPaths.Length == directoryIds.Length);
            Contract.Requires(directoryPaths.Length == dropPaths.Length);
            Contract.Requires(directoryPaths.Length == contentFilters.Length);
            Contract.Requires(directoryPaths.Length == applyFilterToRelativePath.Length);
            Contract.Requires(directoryPaths.Length == relativePathsReplacementArgs.Length);

            var createDropItemsTasks = Enumerable
                .Range(0, directoryPaths.Length)
                .Select(i => CreateDropItemsForDirectoryAsync(
                    daemon, fullyQualifiedDropName, directoryPaths[i], directoryIds[i], dropPaths[i], contentFilters[i], applyFilterToRelativePath[i], relativePathsReplacementArgs[i], logger, commandId))
                .ToArray();

            var createDropItemsResults = await TaskUtilities.SafeWhenAll(createDropItemsTasks);

            if (createDropItemsResults.Any(r => r.error != null))
            {
                return (null, string.Join("; ", createDropItemsResults.Where(r => r.error != null).Select(r => r.error)));
            }

            return (createDropItemsResults.SelectMany(r => r.Item1), null);
        }

        private static Possible<IEnumerable<DropItemForBuildXLFile>> DedupeDropItems(IEnumerable<DropItemForBuildXLFile> dropItems)
        {
            var dropItemsByDropPaths = new Dictionary<string, DropItemForBuildXLFile>(OperatingSystemHelper.PathComparer);

            foreach (var dropItem in dropItems)
            {
                if (dropItemsByDropPaths.TryGetValue(dropItem.RelativeDropPath, out var existingDropItem))
                {
                    if (!string.Equals(dropItem.FullFilePath, existingDropItem.FullFilePath, OperatingSystemHelper.PathComparison))
                    {
                        return new Failure<string>(
                            I($"'{dropItem.FullFilePath}' cannot be added to drop because it has the same drop path '{dropItem.RelativeDropPath}' as '{existingDropItem.FullFilePath}'"));
                    }
                }
                else
                {
                    dropItemsByDropPaths.Add(dropItem.RelativeDropPath, dropItem);
                }
            }

            return dropItemsByDropPaths.Select(kvp => kvp.Value).ToArray();
        }

        private static async Task<IIpcResult> AddDropItemsAsync(DropDaemon daemon, IEnumerable<DropItemForBuildXLFile> dropItems)
        {
            var possibleItems = DedupeDropItems(dropItems);

            if (!possibleItems.Succeeded)
            {
                return new IpcResult(IpcResultStatus.InvalidInput, possibleItems.Failure.Describe());
            }

            var ipcResultTasks = possibleItems.Result.Select(daemon.AddFileAsync).ToArray();

            var ipcResults = await TaskUtilities.SafeWhenAll(ipcResultTasks);

            return IpcResult.Merge(ipcResults);
        }

        /// <summary>
        /// Creates an IPC client using the config from a ConfiguredCommand
        /// </summary>
        public static IClient CreateClient(ConfiguredCommand conf)
        {
            var daemonConfig = CreateDaemonConfig(conf);
            return IpcProvider.GetClient(daemonConfig.Moniker, daemonConfig);
        }

        /// <summary>
        /// Gets the content of a SealedDirectory from BuildXL and caches the result
        /// </summary>
        internal Task<Possible<List<SealedDirectoryFile>>> GetSealedDirectoryContentAsync(DirectoryArtifact artifact, string path)
        {
            return m_directoryArtifactContent.GetOrAdd(artifact, (daemon: this, path), static (key, tuple) =>
            {
                return new AsyncLazy<Possible<List<SealedDirectoryFile>>>(() => tuple.daemon.ApiClient.GetSealedDirectoryContent(key, tuple.path));
            }).Item.Value.GetValueAsync();
        }

        internal void RegisterDropClientForTesting(DropConfig config, IDropClient client)
        {
            var dropName = FullyQualifiedDropName(config);
            m_vsoClients.Add(dropName, (config, new Lazy<Task<IDropClient>>(() => Task.FromResult(client))));
            m_dropSpecificLoggers.Add(dropName, new Lazy<IIpcLogger>(VoidLogger.Instance));
        }

        private void EnsureVsoClientIsCreated(DropConfig dropConfig)
        {
            var name = FullyQualifiedDropName(dropConfig);
            var logger = GetDropSpecificLogger(dropConfig);
            var getOrAddResult = m_vsoClients.GetOrAdd(
                name,
                (logger: logger, apiClient: ApiClient, daemonConfig: Config, dropConfig: dropConfig),
                static (dropName, data) =>
                {
                    var tsk = new Lazy<Task<IDropClient>>(() => Task.Run(() =>
                    {
                        return (IDropClient)new VsoClient(data.logger, data.apiClient, data.daemonConfig, data.dropConfig);
                    }));
                    return (data.dropConfig, tsk);
                });

            // if it's a freshly added VsoClient, start the task
            if (!getOrAddResult.IsFound)
            {
                getOrAddResult.Item.Value.lazyVsoClientTask.Value.Forget();
            }
        }

        private IIpcLogger GetDropSpecificLogger(DropConfig dropConfig)
        {
            // If a log directory was not set, send all log messages into the main logger.
            if (m_dropClientLogDirectory == null)
            {
                return Logger;
            }

            var dropName = FullyQualifiedDropName(dropConfig);
            if (m_dropSpecificLoggers.TryGetValue(dropName, out var lazyLoggger))
            {
                return lazyLoggger.Value;
            }

            const int MaxLogFileNameLength = 200;
            var logFileNameCounter = Interlocked.Increment(ref m_logFileNameCounter).ToString();
            var logFileName = string.Join("_", dropConfig.Name.Split(Path.GetInvalidFileNameChars()));
            logFileName = $"DropClient_{logFileName.Substring(Math.Max(0, logFileName.Length - MaxLogFileNameLength))}";

            var result = m_dropSpecificLoggers.GetOrAdd(
                dropName,
                (logDirectory: m_dropClientLogDirectory, logFileName: logFileName, logFileNameCounter: logFileNameCounter, verbose: Config.Verbose),
                static (dropName, data) =>
                {
                    return new Lazy<IIpcLogger>(() => new FileLogger(data.logDirectory, data.logFileName, data.logFileNameCounter, data.verbose, DropDLogPrefix));
                });

            if (!result.IsFound)
            {
                m_logger.Info("Created a log file '{0}-{1}' for drop '{2}'", logFileName, logFileNameCounter, dropConfig.Name);

                // It is the first time we see this drop; check if it needs a drop app tracer.
                // If we need a tracer, initialize it only once (VsoClient is using the default tracer, i.e., DropAppTraceSource.SingleInstance).
                if (dropConfig.EnableArtifactTracer && Interlocked.CompareExchange(ref m_artifactTracerInitialized, 1, 0) == 0)
                {
                    if (string.IsNullOrWhiteSpace(Config.LogDir))
                    {
                        m_logger.Warning("Drop '{0}' is configured to use Artifact Tracer, but the log directory is not set.", dropConfig.Name);
                    }
                    else
                    {
                        // Place tracer log next to the main drop log.
                        var tracerFileName = Path.Combine(Config.LogDir, $"{LogFileName}-{Config.Moniker}-artifactTracer.log");
                        DropAppTraceSource.SingleInstance.SetSourceLevel(SourceLevels.Verbose);
                        DropAppTraceSource.SingleInstance.AddFileTraceListener(tracerFileName);
                        m_logger.Info("Artifact tracer will be saved to '{0}'", tracerFileName);
                    }
                }
            }

            return result.Item.Value.Value;
        }

        internal static string FullyQualifiedDropName(DropConfig dropConfig) => FullyQualifiedDropName(dropConfig.Service, dropConfig.Name);

        private static string FullyQualifiedDropName(Uri service, string dropName) => $"{service?.ToString() ?? string.Empty}/{dropName}";

        internal readonly struct RelativePathReplacementArguments
        {
            public string OldValue { get; }

            public string NewValue { get; }

            public bool IsValid => OldValue != null && NewValue != null;

            public RelativePathReplacementArguments(string oldValue, string newValue)
            {
                OldValue = oldValue;
                NewValue = newValue;
            }

            public static RelativePathReplacementArguments Invalid => new RelativePathReplacementArguments(null, null);
        }
    }
}
