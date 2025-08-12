// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Ipc;
using BuildXL.Ipc.Common;
using BuildXL.Ipc.ExternalApi;
using BuildXL.Ipc.Interfaces;
using BuildXL.Storage;
using BuildXL.Utilities;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.CLI;
using BuildXL.Utilities.Tracing;
using Newtonsoft.Json.Linq;
using static BuildXL.Utilities.Core.FormattableStringEx;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Tool.ServicePipDaemon
{
    /// <summary>
    /// Responsible for accepting and handling TCP/IP connections from clients.
    /// </summary>
    public abstract class ServicePipDaemon : IDisposable, IIpcOperationExecutor
    {
        /// <nodoc/>
        public const string LogPrefix = "(SPD) ";

        /// <nodoc/>
        protected const string IncludeAllFilter = ".*";

        private const string LogFileName = "ServiceDaemon";
        private const char ResponseFilePrefix = '@';

        /// <nodoc/>
        protected internal static readonly IIpcProvider IpcProvider = IpcFactory.GetProvider();

        /// <summary>Initialized commands</summary>
        protected static readonly Dictionary<string, Command> Commands = new Dictionary<string, Command>();

        private static readonly List<Option> s_daemonConfigOptions = new List<Option>();

        private const HashType DefaultHashTypeForRecomputingContentHash = HashType.Vso0;

        /// <summary>Daemon configuration.</summary>
        protected internal DaemonConfig Config { get; }

        /// <summary>Task to wait on for the completion the underlying server (i.e., all commands are handled, 'stop' was issued).</summary>
        protected Task Completion => m_server.Completion;

        /// <summary>Client for talking to BuildXL.</summary>
        [AllowNull]
        protected Client ApiClient { get; }

        /// <nodoc />
        protected readonly ICloudBuildLogger m_etwLogger;

        private readonly IIpcProvider m_ipcProvider;

        /// <nodoc />
        private IServer m_server;

        /// <nodoc />
        protected readonly IParser m_parser;

        /// <nodoc />
        protected readonly IIpcLogger m_logger;

        /// <nodoc />
        private readonly CounterCollection<DaemonCounter> m_counters = new CounterCollection<DaemonCounter>();

        /// <nodoc />
        private enum DaemonCounter
        {
            /// <nodoc/>
            [CounterType(CounterType.Stopwatch)]
            ParseArgsDuration,

            /// <nodoc/>
            [CounterType(CounterType.Stopwatch)]
            ServerActionDuration,

            /// <nodoc/>
            QueueDurationMs,
        }

        /// <nodoc />
        public IIpcLogger Logger => m_logger;

        #region Options and commands 

        internal static readonly StrOption ConfigFile = RegisterDaemonConfigOption(new StrOption("configFile")
        {
            ShortName = "cf",
            HelpText = "Configuration file",
            DefaultValue = null,
            Expander = (fileName) =>
            {
                var json = System.IO.File.ReadAllText(fileName);
                var jObject = JObject.Parse(json);
                return jObject.Properties().Select(prop => new ParsedOption(PrefixKind.Long, prop.Name, prop.Value.ToString()));
            },
        });

        /// <nodoc />
        public static readonly StrOption Moniker = RegisterDaemonConfigOption(new StrOption("moniker")
        {
            ShortName = "m",
            HelpText = "Moniker to identify client/server communication",
        });

        /// <nodoc />
        public static readonly IntOption MaxConcurrentClients = RegisterDaemonConfigOption(new IntOption("maxConcurrentClients")
        {
            HelpText = "OBSOLETE due to the hardcoded config. (Maximum number of clients to serve concurrently)",
            DefaultValue = DaemonConfig.DefaultMaxConcurrentClients,
        });

        /// <nodoc />
        public static readonly IntOption MaxConnectRetries = RegisterDaemonConfigOption(new IntOption("maxConnectRetries")
        {
            HelpText = "Maximum number of retries to establish a connection with a running daemon",
            DefaultValue = DaemonConfig.DefaultMaxConnectRetries,
        });

        /// <nodoc />
        public static readonly IntOption ConnectRetryDelayMillis = RegisterDaemonConfigOption(new IntOption("connectRetryDelayMillis")
        {
            HelpText = "Delay between consecutive retries to establish a connection with a running daemon",
            DefaultValue = (int)DaemonConfig.DefaultConnectRetryDelay.TotalMilliseconds,
        });

        /// <nodoc />
        public static readonly BoolOption ShellExecute = RegisterDaemonConfigOption(new BoolOption("shellExecute")
        {
            HelpText = "Use shell execute to start the daemon process (a shell window will be created and displayed)",
            DefaultValue = false,
        });

        /// <nodoc />
        public static readonly BoolOption StopOnFirstFailure = RegisterDaemonConfigOption(new BoolOption("stopOnFirstFailure")
        {
            HelpText = "Daemon process should terminate after first failed operation (e.g., 'drop create' fails because the drop already exists).",
            DefaultValue = DaemonConfig.DefaultStopOnFirstFailure,
        });

        /// <nodoc />
        public static readonly BoolOption EnableCloudBuildIntegration = RegisterDaemonConfigOption(new BoolOption("enableCloudBuildIntegration")
        {
            ShortName = "ecb",
            HelpText = "Enable logging ETW events for CloudBuild to pick up",
            DefaultValue = DaemonConfig.DefaultEnableCloudBuildIntegration,
        });

        /// <nodoc />
        public static readonly BoolOption Verbose = RegisterDaemonConfigOption(new BoolOption("verbose")
        {
            ShortName = "v",
            HelpText = "Verbose logging",
            IsRequired = false,
            DefaultValue = false,
        });

        /// <nodoc />
        public static readonly StrOption LogDir = RegisterDaemonConfigOption(new StrOption("logDir")
        {
            ShortName = "log",
            HelpText = "Log directory",
            IsRequired = false
        });

        /// <nodoc />
        public static readonly StrOption File = new StrOption("file")
        {
            ShortName = "f",
            HelpText = "File path",
            IsRequired = false,
            IsMultiValue = true,
        };

        /// <nodoc/>
        public static readonly StrOption HashOptional = new StrOption("hash")
        {
            ShortName = "h",
            HelpText = "VSO file hash",
            IsRequired = false,
            IsMultiValue = true,
        };

        /// <nodoc/>
        public static readonly StrOption FileId = new StrOption("fileId")
        {
            ShortName = "fid",
            HelpText = "BuildXL file identifier",
            IsRequired = false,
            IsMultiValue = true,
        };

        /// <nodoc />
        public static readonly StrOption IpcServerMonikerRequired = new StrOption("ipcServerMoniker")
        {
            ShortName = "dm",
            HelpText = "IPC moniker identifying a running BuildXL IPC server",
            IsRequired = true,
        };

        /// <nodoc />
        public static readonly StrOption HelpNoNameOption = new StrOption(string.Empty)
        {
            HelpText = "Command name",
        };

        /// <nodoc />
        public static readonly StrOption IpcServerMonikerOptional = new StrOption(longName: IpcServerMonikerRequired.LongName)
        {
            ShortName = IpcServerMonikerRequired.ShortName,
            HelpText = IpcServerMonikerRequired.HelpText,
            IsRequired = false,
        };


        /// <nodoc />
        public static readonly StrOption Directory = new StrOption("directory")
        {
            ShortName = "dir",
            HelpText = "Directory path",
            IsRequired = false,
            IsMultiValue = true,
        };

        /// <nodoc />
        public static readonly StrOption DirectoryId = new StrOption("directoryId")
        {
            ShortName = "dirid",
            HelpText = "BuildXL directory identifier",
            IsRequired = false,
            IsMultiValue = true,
        };

        /// <nodoc />
        public static readonly BoolOption DirectoryFilterUseRelativePath = new BoolOption("directoryFilterUseRelativePath")
        {
            ShortName = "dfurp",
            HelpText = "Whether to apply regex to file's relative path instead of a full path.",
            DefaultValue = false,
            IsRequired = false,
            IsMultiValue = true,
        };

        /// <nodoc />
        public static readonly StrOption DirectoryRelativePathReplace = new StrOption("directoryRelativePathReplace")
        {
            ShortName = "drpr",
            HelpText = "Relative path replace arguments.",
            DefaultValue = null,
            IsRequired = false,
            IsMultiValue = true,
        };

        /// <nodoc />
        public static readonly IntOption OperationTimeoutMinutes = RegisterDaemonConfigOption(new IntOption("operationTimeoutMinutes")
        {
            ShortName = "ot",
            HelpText = "Optional timeout on the Client in minutes.",
            IsRequired = false,
            DefaultValue = (int)DaemonConfig.DefaultOperationTimeoutMinutes.TotalMinutes,
        });

        /// <nodoc />
        public static readonly IntOption MaxOperationRetries = RegisterDaemonConfigOption(new IntOption("maxOperationRetries")
        {
            ShortName = "mor",
            HelpText = "Optional number of retries to perform if the Client fails an operation.",
            IsRequired = false,
            DefaultValue = DaemonConfig.DefaultMaxOperationRetries
        });

        /// <nodoc />
        protected static T RegisterOption<T>(List<Option> options, T option) where T : Option
        {
            options.Add(option);
            return option;
        }

        /// <nodoc />
        protected static T RegisterDaemonConfigOption<T>(T option) where T : Option => RegisterOption(s_daemonConfigOptions, option);

        /// <remarks>
        /// The <see cref="s_daemonConfigOptions"/> options are added to every command.
        /// A non-mandatory string option "name" is added as well, which operation
        /// commands may want to use to explicitly specify a particular end point name
        /// (e.g., the target drop name).
        /// </remarks>
        public static Command RegisterCommand(
            string name,
            IEnumerable<Option> options = null,
            ServerAction serverAction = null,
            ClientAction clientAction = null,
            string description = null,
            bool needsIpcClient = true,
            bool addDaemonConfigOptions = true)
        {
            var opts = (options ?? new Option[0]).ToList();
            if (addDaemonConfigOptions)
            {
                opts.AddRange(s_daemonConfigOptions);
            }

            if (!opts.Exists(opt => opt.LongName == "name"))
            {
                opts.Add(new Option(longName: "name")
                {
                    ShortName = "n",
                });
            }

            var cmd = new Command(name, opts, serverAction, clientAction, description, needsIpcClient);
            Commands[cmd.Name] = cmd;
            return cmd;
        }

        /// <nodoc />
        protected static readonly Command HelpCmd = RegisterCommand(
            name: "help",
            description: "Prints a help message (usage).",
            options: new[] { HelpNoNameOption },
            needsIpcClient: false,
            clientAction: (conf, rpc) =>
            {
                string cmdName = conf.Get(HelpNoNameOption);
                bool cmdNotSpecified = string.IsNullOrWhiteSpace(cmdName);
                if (cmdNotSpecified)
                {
                    Console.WriteLine(Usage());
                    return 0;
                }

                Command requestedHelpForCommand;
                var requestedCommandFound = Commands.TryGetValue(cmdName, out requestedHelpForCommand);
                if (requestedCommandFound)
                {
                    Console.WriteLine(requestedHelpForCommand.Usage(conf.Config.Parser));
                    return 0;
                }
                else
                {
                    Console.WriteLine(Usage());
                    return 1;
                }
            });

        /// <nodoc />
        public static readonly Command StopDaemonCmd = RegisterCommand(
            name: "stop",
            description: "[RPC] Stops the daemon process running on specified port; fails if no such daemon is running.",
            clientAction: AsyncRPCSend,
            serverAction: (conf, daemon) =>
            {
                daemon.Logger.Info("[STOP] requested");
                daemon.RequestStop();
                return Task.FromResult(IpcResult.Success());
            });

        /// <nodoc />
        public static readonly Command CrashDaemonCmd = RegisterCommand(
            name: "crash",
            description: "[RPC] Stops the server process by crashing it.",
            clientAction: AsyncRPCSend,
            serverAction: (conf, daemon) =>
            {
                daemon.Logger.Info("[CRASH] requested");
                Environment.Exit(-1);
                return Task.FromResult(IpcResult.Success());
            });

        /// <nodoc />
        public static readonly Command PingDaemonCmd = RegisterCommand(
            name: "ping",
            description: "[RPC] Pings the daemon process.",
            clientAction: SyncRPCSend,
            serverAction: (conf, daemon) =>
            {
                daemon.Logger.Info("[PING] received");
                return Task.FromResult(IpcResult.Success("Alive!"));
            });

        /// <nodoc />
        public static readonly Command TestReadFile = RegisterCommand(
            name: "test-readfile",
            description: "[RPC] Sends a request to the daemon to read a file.",
            options: new Option[] { File },
            clientAction: SyncRPCSend,
            serverAction: (conf, daemon) =>
            {
                daemon.Logger.Info("[READFILE] received");
                var result = IpcResult.Success(System.IO.File.ReadAllText(conf.Get(File)));
                daemon.Logger.Info("[READFILE] succeeded");
                return Task.FromResult(result);
            });

        #endregion

        /// <nodoc />
        public ServicePipDaemon(IParser parser, DaemonConfig daemonConfig, IIpcLogger logger, IIpcProvider rpcProvider = null, Client client = null)
        {
            Contract.Requires(daemonConfig != null);

            Config = daemonConfig;
            m_parser = parser;
            ApiClient = client;
            m_logger = logger;

            m_ipcProvider = rpcProvider ?? IpcProvider;
            m_server = m_ipcProvider.GetServer(Config.Moniker, Config);

            m_etwLogger = new BuildXLBasedCloudBuildLogger(Config.Logger, Config.EnableCloudBuildIntegration);
        }

        /// <summary>
        /// Starts to listen for client connections.  As soon as a connection is received,
        /// it is placed in an action block from which it is picked up and handled asynchronously
        /// (in the <see cref="ParseAndExecuteCommandAsync"/> method).
        /// </summary>
        public void Start()
        {
            string connectionString = null;

            startServerWithRetry();

            // some tests run without API Client, so there is no one to notify that the service is ready
            if (ApiClient != null)
            {
                var process = Process.GetCurrentProcess();
                m_logger.Info($"Reporting to BuildXL that the service is ready (pid: {process.Id}, processName: '{process.ProcessName}', newConnectionString: {connectionString ?? "null"})");
                var possibleResult = ApiClient.ReportServicePipIsReady(process.Id, process.ProcessName, connectionString).GetAwaiter().GetResult();
                if (!possibleResult.Succeeded)
                {
                    m_logger.Error("Failed to notify BuildXL that the service is ready.");
                }
                else
                {
                    m_logger.Info("Successfully notified BuildXL that the service is ready.");
                }
            }

            void startServerWithRetry()
            {
                try
                {
                    m_server.Start(this);
                }
                catch (IOException e)
                {
                    // Grpc server throws an IOException if it fails to bind to the specified port.
                    // We can attempt to start a server on an unused port in this case. However, we can only do this
                    // if there is a valid API Client, i.e., we can communicate this back to the BuildXL process.
                    if (ApiClient != null)
                    {
                        try
                        {
                            m_logger.Warning($"Could not start a server using connection string '{Config.Moniker}', will attempt to start a server using a different connection string. Exception: {e}");
                            m_server = m_ipcProvider.GetServer(m_server.Config);
                            m_server.Start(this);
                            // Success. Record the new connection string (the task has completed already because the server is running).
                            connectionString = m_server.ConnectionString.GetAwaiter().GetResult();
                            return;
                        }
                        catch (NotSupportedException)
                        {
                            // This provider requires a connection string to create a server. There is no successful code path at this point.
                            // Log an error and swallow the exception, so the outer exception remains the true reason for a failure.
                            m_logger.Error("Cannot retry server creation because the provider does not support this.");
                        }
                    }

                    // If we are here, something went wrong (no api client, no provider support, etc.). Re-throw the original exception.
                    throw;
                }
            }
        }

        /// <summary>
        /// Requests shut down, causing this daemon to immediately stop listening for TCP/IP
        /// connections. Any pending requests, however, will be processed to completion.
        /// </summary>
        public virtual void RequestStop()
        { 
            m_server.RequestStop();
        }

        /// <summary>
        /// Calls <see cref="RequestStop"/> then waits for <see cref="Completion"/>.
        /// </summary>
        public Task RequestStopAndWaitForCompletionAsync()
        {
            RequestStop();
            return Completion;
        }

        /// <inheritdoc />
        public virtual void Dispose()
        {
            m_server.Dispose();
            ApiClient?.Dispose();
            m_logger.Dispose();
        }

        private async Task<IIpcResult> ParseAndExecuteCommandAsync(int id, IIpcOperation operation)
        {
            string cmdLine = operation.Payload;
            m_logger.Verbose($"Command received. Request #{id}, CommandLine: {cmdLine}");
            ConfiguredCommand conf;
            using (m_counters.StartStopwatch(DaemonCounter.ParseArgsDuration))
            {
                conf = ParseArgs(cmdLine, m_parser);
            }

            IIpcResult result;
            using (var duration = m_counters.StartStopwatch(DaemonCounter.ServerActionDuration))
            {
                result = await conf.Command.ServerAction(conf, this);
                result.ActionDuration = duration.Elapsed;
            }

            TimeSpan queueDuration = operation.Timestamp.Daemon_BeforeExecuteTime - operation.Timestamp.Daemon_AfterReceivedTime;
            m_counters.AddToCounter(DaemonCounter.QueueDurationMs, (long)queueDuration.TotalMilliseconds);

            m_logger.Verbose($"Request #{id} processed in {queueDuration}, Result: {result.ExitCode}");
            return result;
        }

        Task<IIpcResult> IIpcOperationExecutor.ExecuteAsync(int id, IIpcOperation operation)
        {
            Contract.Requires(operation != null);

            return ParseAndExecuteCommandAsync(id, operation);
        }

        /// <summary>
        /// Parses a string and returns a ConfiguredCommand.
        /// </summary>        
        public static ConfiguredCommand ParseArgs(string allArgs, IParser parser, IIpcLogger logger = null, bool ignoreInvalidOptions = false)
        {
            return ParseArgs(parser.SplitArgs(allArgs), parser, logger, ignoreInvalidOptions);
        }

        /// <summary>
        /// Parses a list of arguments and returns a ConfiguredCommand.
        /// </summary>  
        public static ConfiguredCommand ParseArgs(string[] args, IParser parser, IIpcLogger logger = null, bool ignoreInvalidOptions = false)
        {
            var usageMessage = Lazy.Create(() => "Usage:" + Environment.NewLine + Usage());

            if (args.Length == 0)
            {
                throw new ArgumentException(I($"Command is required. {usageMessage.Value}"));
            }

            var argsQueue = new Queue<string>(args.Length);
            foreach (var arg in args)
            {
                if (arg[0] == ResponseFilePrefix)
                {
                    foreach (var argFromFile in ProcessResponseFile(arg, parser))
                    {
                        argsQueue.Enqueue(argFromFile);
                    }
                }
                else
                {
                    argsQueue.Enqueue(arg);
                }
            }

            string cmdName = argsQueue.Dequeue();
            if (!Commands.TryGetValue(cmdName, out Command cmd))
            {
                throw new ArgumentException(I($"No command '{cmdName}' is found. {usageMessage.Value}"));
            }

            var sw = Stopwatch.StartNew();
            Config conf = BuildXL.Utilities.CLI.Config.ParseCommandLineArgs(cmd.Options, argsQueue, parser, caseInsensitive: true, ignoreInvalidOptions: ignoreInvalidOptions);
            var parseTime = sw.Elapsed;

            logger = logger ?? new ConsoleLogger(Verbose.GetValue(conf), ServicePipDaemon.LogPrefix);
            logger.Verbose("Parsing command line arguments done in {0}", parseTime);
            return new ConfiguredCommand(cmd, conf, logger);
        }

        private static IEnumerable<string> ProcessResponseFile(string responseFileArgument, IParser parser)
        {
            Contract.Requires(!string.IsNullOrEmpty(responseFileArgument));
            Contract.Requires(responseFileArgument[0] == ResponseFilePrefix);

            string path = responseFileArgument.Substring(1);
            string content;
            try
            {
                content = System.IO.File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                throw new ArgumentException(I($"Error while reading the response file '{path}': {ex.Message}."), ex);
            }

            // The arguments inside of the response file might be escaped.
            // We need to pass them through the parser to properly handle such cases.
            return parser.SplitArgs(content.Replace(Environment.NewLine, " "));
        }

        /// <summary>
        /// Creates DaemonConfig using the values specified on the ConfiguredCommand
        /// </summary>        
        public static DaemonConfig CreateDaemonConfig(ConfiguredCommand conf)
        {
            return new DaemonConfig(
                logger: conf.Logger,
                moniker: conf.Get(Moniker),
                maxConnectRetries: conf.Get(MaxConnectRetries),
                connectRetryDelay: TimeSpan.FromMilliseconds(conf.Get(ConnectRetryDelayMillis)),
                stopOnFirstFailure: conf.Get(StopOnFirstFailure),
                enableCloudBuildIntegration: conf.Get(EnableCloudBuildIntegration),
                logDir: conf.Get(LogDir),
                verbose: conf.Get(Verbose),
                operationTimeout: TimeSpan.FromMinutes(conf.Get(OperationTimeoutMinutes)),
                maxOperationRetries: conf.Get(MaxOperationRetries));
        }

        private static string Usage()
        {
            var builder = new StringBuilder();
            var len = Commands.Keys.Max(cmdName => cmdName.Length);
            foreach (var cmd in Commands.Values)
            {
                builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,-" + len + "} : {1}", cmd.Name, cmd.Description));
            }

            return builder.ToString();
        }

        /// <nodoc />
        protected static int SyncRPCSend(ConfiguredCommand conf, IClient rpc) => RPCSend(conf, rpc, true);

        /// <nodoc />
        protected static int AsyncRPCSend(ConfiguredCommand conf, IClient rpc) => RPCSend(conf, rpc, false);

        /// <nodoc />
        protected static int RPCSend(ConfiguredCommand conf, IClient rpc, bool isSync)
        {
            var rpcResult = RPCSendCore(conf, rpc, isSync);
            conf.Logger.Info(
                "Command '{0}' {1} (exit code: {2}). {3}",
                conf.Command.Name,
                rpcResult.Succeeded ? "succeeded" : "failed",
                (int)rpcResult.ExitCode,
                rpcResult.Payload);
            return (int)rpcResult.ExitCode;
        }

        private static IIpcResult RPCSendCore(ConfiguredCommand conf, IClient rpc, bool isSync)
        {
            string operationPayload = ToPayload(conf);
            var operation = new IpcOperation(operationPayload, waitForServerAck: isSync);
            return rpc.Send(operation).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Reconstructs a full command line from a command name (<paramref name="commandName"/>)
        /// and a configuration (<paramref name="config"/>).
        /// </summary>
        internal static string ToPayload(string commandName, Config config)
        {
            return commandName + " " + config.Render();
        }

        /// <summary>
        /// Reconstructs a full command line corresponding to a <see cref="ConfiguredCommand"/>.
        /// </summary>
        private static string ToPayload(ConfiguredCommand cmd) => ToPayload(cmd.Command.Name, cmd.Config);

        /// <nodoc/>
        protected static void SetupThreadPoolAndServicePoint(int minWorkerThreads, int minIoThreads, int? minServicePointParallelism)
        {
            int workerThreads, ioThreads;
            ThreadPool.GetMinThreads(out workerThreads, out ioThreads);

            workerThreads = Math.Max(workerThreads, minWorkerThreads);
            ioThreads = Math.Max(ioThreads, minIoThreads);
            ThreadPool.SetMinThreads(workerThreads, ioThreads);

            if (minServicePointParallelism.HasValue)
            {
#pragma warning disable SYSLIB0014 // Type or member is obsolete
                ServicePointManager.DefaultConnectionLimit = Math.Max(minServicePointParallelism.Value, ServicePointManager.DefaultConnectionLimit);
#pragma warning restore SYSLIB0014 // Type or member is obsolete
            }
        }

        /// <summary>
        /// Takes a list of strings and returns a list of initialized regular expressions
        /// </summary>
        protected static Possible<Regex[]> InitializeFilters(string[] filters, RegexOptions additionalRegexOptions = RegexOptions.None)
        {
            try
            {
                var initializedFilters = filters.Select(
                    filter => filter == IncludeAllFilter
                        ? null
                        // Starting .NET 7, the .NET team made timeouts more accurate. So, with a shorter timeout, prior to .NET 7, we
                        // may have a match that takes much longer than the timeout and does not time out, and now in .NET 7, it will.
                        // One situation where the timeout occurs is when the machine is very busy and the OS does not let the thread to run.
                        // To this end, we set timeout for the regex with a generous value, e.g., 10 minutes.
                        //
                        // One can think of setting the timeout to infinite, but that is not a good idea because the regex is user specified, and
                        // without timeout, we can end up with ReDoS.
                        // For more information, read https://devblogs.microsoft.com/dotnet/regular-expression-improvements-in-dotnet-7/
                        : new Regex(filter, RegexOptions.IgnoreCase | additionalRegexOptions, TimeSpan.FromMinutes(10)));

                return initializedFilters.ToArray();
            }
            catch (Exception e)
            {
                return new Failure<string>(e.DemystifyToString());
            }
        }

        /// <nodoc />
        protected IDictionary<string, long> GetDaemonStats(string prefix = null) => m_counters.AsStatistics(prefix);

        /// <summary>
        /// Logs IpcResult by wrapping it into a StringBuilder and passing to a provided logger. 
        /// This method wraps IpcResult into a pooled StringBuilder to reduce the number of string allocations that might happen 
        /// </summary>
        /// <remarks>
        /// IpcResult is essentially a string, so at first, it might seem counter-intuitive to put it inside of a StringBuilder.
        /// However, the Payload of IpcResult can be very big (i.e., big enough to be located on the Large object heap), so all the
        /// prefixing / timestamping that loggers might be doing will result in more 'copies' of that string added to LOH.
        /// StringBuilder will still be on the LOH if the Payload string is too big, but we won't be creating any additional copies
        /// while it goes through the logger.
        /// </remarks>
        protected static void LogIpcResult(IIpcLogger logger, LogLevel level, string prefix, IIpcResult result)
        {
            using var pooledBuilder = Pools.GetStringBuilder();
            var sb = pooledBuilder.Instance;
            sb.Append(prefix);
            if (result is IpcResult ipcResult)
            {
                ipcResult.ToString(sb);
            }
            else
            {
                sb.Append(result.ToString());
            }

            logger.Log(level, sb);
        }

        /// <summary>
        /// Recomputes the hash of the FileContentInfo using the <see cref="DefaultHashTypeForRecomputingContentHash"/>  hash if the original hash is not compatible with drop
        /// </summary>
        protected static async Task<Possible<FileContentInfo>> ParseFileContentAsync(ServicePipDaemon daemon, string serialized, string fileId, string filePath)
        {
            var contentInfo = FileContentInfo.Parse(serialized);
            var file = BuildXL.Ipc.ExternalApi.FileId.Parse(fileId);
            Possible<RecomputeContentHashEntry> hash;
            if (!IsDropCompatibleHashing(contentInfo.Hash.HashType))
            {
                hash = await daemon.ApiClient.RecomputeContentHashFiles(file, DefaultHashTypeForRecomputingContentHash.ToString(), new RecomputeContentHashEntry(filePath, contentInfo.Hash));
                if (!hash.Succeeded)
                {
                    return new Failure<string>(hash.Failure?.Describe() ?? "Response to send recompute content hash indicates a failure");
                }

                return new FileContentInfo(hash.Result.Hash, contentInfo.Length);
            }
            else
            {
                return contentInfo;
            }
        }

        private static bool IsDropCompatibleHashing(HashType hashType)
        {
            switch (hashType)
            {
                case HashType.Vso0:
                case HashType.Dedup64K:
                case HashType.Dedup1024K:
                case HashType.DedupNode:
                case HashType.DedupSingleChunk:
                case HashType.MD5:
                case HashType.SHA1:
                case HashType.SHA256:
                    return true;
                default:
                    return false;
            }
        }

        /// <nodoc />
        protected static IpcResultStatus ParseIpcStatus(string statusString, IpcResultStatus defaultValue = IpcResultStatus.ExecutionError)
        {
            return Enum.TryParse<IpcResultStatus>(statusString, out var value)
                ? value
                : defaultValue;
        }

        /// <summary>
        /// Creates a result with a limited payload that can be sent back to the caller (BuildXL).
        /// In a successful case, the caller does not care about detailed summary, and in a failed case, the first
        /// error should be enough for logging (other errors are still discoverable in the daemon's log).
        /// </summary>
        protected static IIpcResult SuccessOrFirstError(IIpcResult result)
        {
            if (result.Succeeded)
            {
                return new IpcResult(IpcResultStatus.Success, "Success", result.ActionDuration);
            }

            if (result is IpcResult ipcResult)
            {
                return ipcResult.GetFirstErrorResult();
            }
            else
            {
                return result;
            }
        }

        /// <nodoc/>
        protected static Possible<RelativePathReplacementArguments[]> InitializeRelativePathReplacementArguments(string[] serializedValues)
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

        /// <nodoc/>
        protected internal static List<SealedDirectoryFile> FilterDirectoryContent(string directoryPath, List<SealedDirectoryFile> directoryContent, Regex contentFilter, bool applyFilterToRelativePath)
        {
            var endsWithSlash = directoryPath[directoryPath.Length - 1] == Path.DirectorySeparatorChar || directoryPath[directoryPath.Length - 1] == Path.AltDirectorySeparatorChar;
            var startPosition = applyFilterToRelativePath ? (directoryPath.Length + (endsWithSlash ? 0 : 1)) : 0;
            // Note: if startPosition is not 0, and a regular expression uses ^ anchor to match the beginning of a relative path, no files will be matched.
            // In such cases, one must use \G anchor instead.
            // https://docs.microsoft.com/en-us/dotnet/api/system.text.regularexpressions.regex.match
            return directoryContent.Where(file => contentFilter.IsMatch(file.FileName, startPosition)).ToList();
        }

        /// <nodoc/>
        protected internal static string GetRelativePath(string root, string file, RelativePathReplacementArguments pathReplacementArgs)
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
    }
}
