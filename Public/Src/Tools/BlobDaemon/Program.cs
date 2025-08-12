// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using BuildXL.Ipc;
using BuildXL.Ipc.Interfaces;
using BuildXL.Utilities.CLI;
using Tool.ServicePipDaemon;
using static Tool.ServicePipDaemon.Statics;

namespace Tool.BlobDaemon
{
    /// <summary>
    /// BlobDaemon entry point.
    /// </summary>
    public static class Program
    {
        /// <nodoc/>        
#pragma warning disable IDE1006 // Naming Styles (ignore missing 'async' in the method name)
        public static async Task<int> Main(string[] args)
#pragma warning restore IDE1006 // Naming Styles
        {
            try
            {
                Console.WriteLine($"{nameof(BlobDaemon)} started at {DateTime.UtcNow:u}");
                Console.WriteLine($"{BlobDaemon.BlobDaemonLogPrefix}Command line arguments: ");
                Console.WriteLine($"{BlobDaemon.BlobDaemonLogPrefix}{string.Join($"{Environment.NewLine}{BlobDaemon.BlobDaemonLogPrefix}", args)}");
                Console.WriteLine();

                BlobDaemon.EnsureCommandsInitialized();

                var confCommand = ServicePipDaemon.ServicePipDaemon.ParseArgs(args, new UnixParser());
                if (confCommand.Command.NeedsIpcClient)
                {
                    using (var rpc = CreateClient(confCommand))
                    {
                        var result = confCommand.Command.ClientAction(confCommand, rpc);
                        rpc.RequestStop();
                        await rpc.Completion;
                        return result;
                    }
                }
                else
                {
                    return confCommand.Command.ClientAction(confCommand, null);
                }
            }
            catch (ArgumentException e)
            {
                Error(e.Message);
                return 3;
            }
        }

        internal static IClient CreateClient(ConfiguredCommand conf)
        {
            var daemonConfig = ServicePipDaemon.ServicePipDaemon.CreateDaemonConfig(conf);
            return IpcFactory.GetProvider().GetClient(daemonConfig.Moniker, daemonConfig);
        }
    }
}
