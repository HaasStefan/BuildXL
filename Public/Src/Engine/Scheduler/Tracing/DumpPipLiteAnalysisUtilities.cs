﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text.Json;
using BuildXL.Pips;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Native.IO;
using BuildXL.Pips.Graph;
using BuildXL.Processes;
using BuildXL.Utilities.Collections;

namespace BuildXL.Scheduler.Tracing
{
    /// <summary>
    /// Common functions used by both the runtime and post-build dump pip analyzers
    /// </summary>
    public static class DumpPipLiteAnalysisUtilities
    {
        /// <summary>
        /// Dumps the specified Pip to a json file named {pip.SemiStableHash}.json.
        /// </summary>
        /// <param name="pip"> Pip to be dumped. </param>
        /// <param name="logPath"> Directory where the pip dump will be written. </param>
        /// <param name="pathTable"> Path table. </param>
        /// <param name="stringTable"> String table. </param>
        /// <param name="symbolTable"> Symbol table. </param>
        /// <param name="pipGraph"> Pip graph for resolving qualifier ids. </param>
        /// <param name="loggingContext"> Logging context for logging any potential errors (can be null for post build). </param>
        /// <returns> True if log file was written successfully. </returns>
        /// <remarks> An error will be logged if this function returns false. </remarks>
        public static bool DumpPip(Pip pip, string logPath, PathTable pathTable, StringTable stringTable, SymbolTable symbolTable, PipGraph pipGraph, LoggingContext loggingContext)
        {
            return DumpPip(pip, dynamicData: null, logPath, pathTable, stringTable, symbolTable, pipGraph, loggingContext);
        }

        /// <summary>
        /// Dumps the specified Pip to a json file named {pip.SemiStableHash}.json.
        /// </summary>
        /// <param name="pip"> Pip to be dumped. </param>
        /// <param name="dynamicData"> Contains dynamic observations to be dumped. </param>
        /// <param name="logPath"> Directory where the pip dump will be written. </param>
        /// <param name="pathTable"> Path table. </param>
        /// <param name="stringTable"> String table. </param>
        /// <param name="symbolTable"> Symbol table. </param>
        /// <param name="pipGraph"> Pip graph for resolving qualifier ids. </param>
        /// <param name="loggingContext"> Logging context for logging any potential errors (can be null for post build). </param>
        /// <returns> True if log file was written successfully. </returns>
        /// <remarks> An error will be logged if this function returns false. </remarks>
        public static bool DumpPip(Pip pip, ProcessExecutionMonitoringReportedEventData? dynamicData, string logPath, PathTable pathTable, StringTable stringTable, SymbolTable symbolTable, PipGraph pipGraph, LoggingContext loggingContext)
        {
            var outputFilePath = Path.Combine(logPath, $"{pip.FormattedSemiStableHash}.json");
            var serializerOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
#if NET5_0_OR_GREATER
// .NET 5 and 6 have a different way of dealing with null values
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
#else
                IgnoreNullValues = true,
#endif
            };

            try
            {
                var pipToBeLogged = CreateObjectForSerialization(pip, dynamicData, pathTable, stringTable, symbolTable, pipGraph);
                var dumpContents = JsonSerializer.SerializeToUtf8Bytes(pipToBeLogged, serializerOptions);
                File.WriteAllBytes(outputFilePath, dumpContents);
            }
            catch (Exception ex)
            {
                // This may occur for a number of reasons including bad path, i/o errors, or improper permissions
                if (ex is ArgumentException || ex is ArgumentNullException)
                {
                    // Serializer or file writer failed due to a bad argument
                    Logger.Log.DumpPipLiteUnableToSerializePipDueToBadArgument(loggingContext, pip.FormattedSemiStableHash, outputFilePath, ex.GetLogEventMessage());
                }
                else if (ex is PathTooLongException || ex is DirectoryNotFoundException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    // File writer most likely failed due to a bad path
                    Logger.Log.DumpPipLiteUnableToSerializePipDueToBadPath(loggingContext, pip.FormattedSemiStableHash, logPath, ex.GetLogEventMessage());
                }
                else
                {
                    // General case for any other exceptions that may occur so that we don't fail the build due to an uncaught exception
                    Logger.Log.DumpPipLiteUnableToSerializePip(loggingContext, pip.FormattedSemiStableHash, outputFilePath, ex.GetLogEventMessage());
                }

                return false;
            }

            return true;
        }

        /// <summary>
        /// Create a logging directory under a specified path.
        /// </summary>
        /// <param name="logPath"> Path to the directory to be used by the analyzer. </param>
        /// <param name="loggingContext"> Logging context to log if a failure occurs when creating the directory. </param>
        public static bool CreateLoggingDirectory(string logPath, LoggingContext loggingContext)
        {
            bool success = true;

            try
            {
                FileUtilities.CreateDirectoryWithRetry(logPath);
            }
            catch (Exception ex)
            {
                // If log directory creation fails, then disable the runtime analyzer for this build and log the exception.
                Logger.Log.DumpPipLiteUnableToCreateLogDirectory(loggingContext, logPath, ex.GetLogEventMessage());

                success = false;
            }

            return success;
        }


        #region SerializationHelperFunctions
        /// <summary>
        /// Prepares a pip to be serialized.
        /// </summary>
        /// <param name="pip"></param>
        /// <param name="dynamicData"></param>
        /// <param name="pathTable"></param>
        /// <param name="stringTable"></param>
        /// <param name="symbolTable"></param>
        /// <param name="pipGraph"></param>
        /// <returns>Serialized Pip object.</returns>
        public static SerializedPip CreateObjectForSerialization(Pip pip, ProcessExecutionMonitoringReportedEventData? dynamicData, PathTable pathTable, StringTable stringTable, SymbolTable symbolTable, PipGraph pipGraph)
        {
            SerializedPip serializedPip = new SerializedPip
            {
                PipMetaData = CreatePipMetadata(pip, pathTable, stringTable, pipGraph, symbolTable)
            };

            switch (pip.PipType)
            {
                case PipType.CopyFile:
                    serializedPip.CopyFileSpecificDetails = CreateCopyFileSpecificDetails((CopyFile)pip, pathTable);
                    break;
                case PipType.Process:
                    serializedPip.ProcessSpecificDetails = CreateProcessSpecificDetails((Process)pip, pathTable, stringTable, pipGraph);
                    break;
                case PipType.Ipc:
                    serializedPip.IpcSpecificDetails = CreateIpcSpecificDetails((IpcPip)pip, pathTable, stringTable);
                    break;
                case PipType.Value:
                    serializedPip.ValueSpecificDetails = CreateValueSpecificDetails((ValuePip)pip, pathTable, symbolTable);
                    break;
                case PipType.SpecFile:
                    serializedPip.SpecFileSpecificDetails = CreateSpecFileSpecificDetails((SpecFilePip)pip, pathTable);
                    break;
                case PipType.Module:
                    serializedPip.ModuleSpecificDetails = CreateModuleSpecificDetails((ModulePip)pip, pathTable, stringTable);
                    break;
                case PipType.HashSourceFile:
                    serializedPip.HashSourceFileSpecificDetails = CreateHashSourceFileSpecificDetails((HashSourceFile)pip, pathTable);
                    break;
                case PipType.SealDirectory:
                    serializedPip.SealDirectorySpecificDetails = CreateSealDirectorySpecificDetails((SealDirectory)pip, pathTable);
                    break;
                case PipType.WriteFile:
                    serializedPip.WriteFileSpecificDetails = CreateWriteFileSpecificDetails((WriteFile)pip, pathTable);
                    break;
                default:
                    Contract.Assert(false, $"Specified pip type '{pip.PipType}' does not match any known pip types.");
                    break;
            }

            if (dynamicData.HasValue)
            {
                if (dynamicData.Value.ReportedProcesses != null && dynamicData.Value.ReportedProcesses.Count > 0)
                {
                    serializedPip.ReportedProcesses = new List<ReportedProcessData>();
                    foreach (var reportedProcess in dynamicData.Value.ReportedProcesses)
                    {
                        serializedPip.ReportedProcesses.Add(CreateReportedProcessData(reportedProcess));
                    }
                }

                if (dynamicData.Value.ReportedFileAccesses != null && dynamicData.Value.ReportedFileAccesses.Count > 0)
                {
                    serializedPip.ReportedFileAccesses = new List<ReportedFileAccessData>();
                    foreach (var reportedFileAccess in dynamicData.Value.ReportedFileAccesses)
                    {
                        serializedPip.ReportedFileAccesses.Add(CreateReportedFileAccessData(reportedFileAccess, pathTable));
                    }
                }

                if (dynamicData.Value.ProcessDetouringStatuses != null && dynamicData.Value.ProcessDetouringStatuses.Count > 0)
                {
                    serializedPip.ProcessDetouringStatuses = new List<ProcessDetouringStatusDataJson>();
                    foreach (var processDetouringStatus in dynamicData.Value.ProcessDetouringStatuses)
                    {
                        serializedPip.ProcessDetouringStatuses.Add(CreateProcessDetouringStatusDataJson(processDetouringStatus));
                    }
                }
            }

            return serializedPip;
        }

        private static PipMetaData CreatePipMetadata(Pip pip, PathTable pathTable, StringTable stringTable, PipGraph pipGraph, SymbolTable symbolTable)
        {
            PipMetaData pipMetaData = new PipMetaData
            {
                PipId = pip.PipId.Value.ToString(CultureInfo.InvariantCulture) + " (" + pip.PipId.Value.ToString("X16", CultureInfo.InvariantCulture) + ")",
                SemiStableHash = pip.SemiStableHash.ToString("X16"),
                PipType = pip.PipType.ToString(),
                Tags = pip.Tags.IsValid ? GetJsonFriendlyList(pip.Tags.Select(tag => tag.ToString(stringTable))) : null,
            };

            var provenance = pip.Provenance;
            if (provenance != null)
            {
                pipMetaData.Qualifier = provenance.QualifierId.IsValid ? pipGraph.Context.QualifierTable.GetCanonicalDisplayString(provenance.QualifierId) : null;
                pipMetaData.Usage = CreateString(provenance.Usage, pathTable);
                pipMetaData.Spec = CreateString(provenance.Token.Path, pathTable);
                pipMetaData.Location = provenance.Token.IsValid ? provenance.Token.ToString(pathTable) : null;
                pipMetaData.Thunk = provenance.OutputValueSymbol.IsValid ? provenance.OutputValueSymbol.ToString(symbolTable) : null;
                pipMetaData.ModuleId = provenance.ModuleId.IsValid ? provenance.ModuleId.Value.Value.ToString() : null;
            }

            return pipMetaData;
        }

        #region CopyFileSpecificDetails
        private static CopyFileSpecificDetails CreateCopyFileSpecificDetails(CopyFile pip, PathTable pathTable)
        {
            return new CopyFileSpecificDetails
            {
                Source = CreateString(pip.Source.Path, pathTable),
                Destination = CreateString(pip.Destination.Path, pathTable)
            };
        }
        #endregion CopyFileSpecificDetails

        #region ProcessSpecificDetails
        private static ProcessSpecificDetails CreateProcessSpecificDetails(Process pip, PathTable pathTable, StringTable stringTable, PipGraph pipGraph)
        {
            return new ProcessSpecificDetails
            {
                ProcessInvocationDetails = CreateProcessInvocationDetails(pip, pathTable, stringTable),
                ProcessIoHandling = CreateProcessIoHandling(pip, pathTable, stringTable),
                ProcessDirectories = CreateProcessDirectories(pip, pathTable),
                ProcessAdvancedOptions = CreateProcessAdvancedOptions(pip, pathTable, stringTable),
                ProcessInputOutput = CreateProcessInputOutput(pip, pathTable, pipGraph),
                ServiceDetails = CreateServiceDetails(pip),
            };
        }

        private static ProcessInvocationDetails CreateProcessInvocationDetails(Process pip, PathTable pathTable, StringTable stringTable)
        {
            return new ProcessInvocationDetails
            {
                Executable = CreateString(pip.Executable, pathTable),
                ToolDescription = CreateString(pip.ToolDescription, stringTable),
                Arguments = CreateString(pip.Arguments, pathTable),
                ResponseFilePath = CreateString(pip.ResponseFile, pathTable),
                ReponseFileContents = CreateString(pip.ResponseFileData, pathTable),
                EnvironmentVariables = GetJsonFriendlyList(pip.EnvironmentVariables.Select(envVar => new SerializedEnvironmentVariable(envVar.Name.ToString(stringTable), (envVar.Value.IsValid ? envVar.Value.ToString(pathTable) : "[Passthrough Environment Variable]"), envVar.IsPassThrough)))
            };
        }

        private static ProcessIoHandling CreateProcessIoHandling(Process pip, PathTable pathTable, StringTable stringTable)
        {
            return new ProcessIoHandling
            {
                StdInFile = CreateString(pip.StandardInput.File.Path, pathTable),
                StdInFileData = CreateString(pip.StandardInput.Data, pathTable),
                StdOut = CreateString(pip.StandardOutput, pathTable),
                StdErr = CreateString(pip.StandardError, pathTable),
                StdDirectory = CreateString(pip.StandardDirectory, pathTable),
                WarningRegex = CreateString(pip.WarningRegex.Pattern, stringTable),
                ErrorRegex = CreateString(pip.ErrorRegex.Pattern, stringTable),
            };
        }

        private static ProcessDirectories CreateProcessDirectories(Process pip, PathTable pathTable)
        {
            return new ProcessDirectories
            {
                WorkingDirectory = CreateString(pip.WorkingDirectory, pathTable),
                UniqueOutputDirectory = CreateString(pip.UniqueOutputDirectory, pathTable),
                TempDirectory = CreateString(pip.TempDirectory, pathTable),
                AdditionalTempDirectories = CreateString(pip.AdditionalTempDirectories, pathTable),
            };
        }

        private static ProcessAdvancedOptions CreateProcessAdvancedOptions(Process pip, PathTable pathTable, StringTable stringTable)
        {
            return new ProcessAdvancedOptions
            {
                WarningTimeout = CreateNumeric(pip.WarningTimeout),
                ErrorTimeout = CreateNumeric(pip.Timeout),
                SuccessCodes = pip.SuccessExitCodes.IsValid ? GetJsonFriendlyList(pip.SuccessExitCodes) : null,
                Semaphores = CreateString(pip.Semaphores, stringTable),
                PreserveOutputTrustLevel = pip.PreserveOutputsTrustLevel,
                PreserveOutputsAllowlist = CreateString(pip.PreserveOutputAllowlist, pathTable),
                ProcessOptions = pip.ProcessOptions.ToString(),
                RetryExitCodes = pip.RetryExitCodes.IsValid ? GetJsonFriendlyList(pip.RetryExitCodes) : null,
                ProcessRetries = pip.ProcessRetries,
            };
        }

        private static ProcessInputOutput CreateProcessInputOutput(Process pip, PathTable pathTable, PipGraph pipGraph)
        {
            return new ProcessInputOutput
            {
                FileDependencies = CreateString(pip.Dependencies, pathTable),
                DirectoryDependencies = GetDirectoryDependencies(pip.DirectoryDependencies, pathTable, pipGraph),
                PipDependencies = GetJsonFriendlyList(pip.OrderDependencies.Select(dep => dep.Value.ToString())),
                FileOutputs = CreateString(pip.FileOutputs, pathTable),
                DirectoryOutputs = CreateString(pip.DirectoryOutputs, pathTable),
                UntrackedPaths = CreateString(pip.UntrackedPaths, pathTable),
                UntrackedScopes = CreateString(pip.UntrackedScopes, pathTable),
            };
        }

        private static ServiceDetails CreateServiceDetails(Process pip)
        {
            return new ServiceDetails
            {
                IsService = pip.IsService,
                ShutdownProcessPipId = CreateString(pip.ShutdownProcessPipId),
                ServicePipDependencies = CreateString(pip.ServicePipDependencies),
                IsStartOrShutdownKind = pip.IsStartOrShutdownKind,
            };
        }
        #endregion ProcessSpecificDetails

        #region IpcSpecificDetails
        private static IpcSpecificDetails CreateIpcSpecificDetails(IpcPip pip, PathTable pathTable, StringTable stringTable)
        {
            return new IpcSpecificDetails
            {
                IpcMonikerId = CreateString(pip.IpcInfo.IpcMonikerId, stringTable),
                MessageBody = CreateString(pip.MessageBody, pathTable),
                OutputFile = CreateString(pip.OutputFile.Path, pathTable),
                ServicePipDependencies = CreateString(pip.ServicePipDependencies),
                FileDependencies = CreateString(pip.FileDependencies, pathTable),
                DirectoryDependencies = CreateString(pip.DirectoryDependencies, pathTable),
                LazilyMaterializedFileDependencies = GetJsonFriendlyList(pip.LazilyMaterializedDependencies.Where(a => a.IsFile && a.IsValid).Select(a => a.FileArtifact.Path.ToString(pathTable))),
                LazilyMaterializedDirectoryDependencies = GetJsonFriendlyList(pip.LazilyMaterializedDependencies.Where(a => a.IsDirectory && a.IsValid).Select(a => a.DirectoryArtifact.Path.ToString(pathTable))),
                IsServiceFinalization = pip.IsServiceFinalization,
                MustRunOnOrchestrator = pip.MustRunOnOrchestrator,
            };
        }
        #endregion IpcSpecificDetails

        #region ValueSpecificDetails
        private static ValueSpecificDetails CreateValueSpecificDetails(ValuePip pip, PathTable pathTable, SymbolTable symbolTable)
        {
            return new ValueSpecificDetails
            {
                Symbol = CreateString(pip.Symbol, symbolTable),
                Qualifier = pip.Qualifier.IsValid ? pip.Qualifier.ToString() : null,
                SpecFile = CreateString(pip.LocationData.Path, pathTable),
                Location = CreateString(pip.LocationData),
            };
        }
        #endregion ValueSpecificDetails

        #region SpecFileSpecificDetails
        private static SpecFileSpecificDetails CreateSpecFileSpecificDetails(SpecFilePip pip, PathTable pathTable)
        {
            return new SpecFileSpecificDetails
            {
                SpecFile = CreateString(pip.SpecFile.Path, pathTable),
                DefinitionFile = CreateString(pip.DefinitionLocation.Path, pathTable),
                Definition = CreateString(pip.DefinitionLocation),
                Module = pip.OwningModule.Value.Value,
            };
        }
        #endregion SpecFileSpecificDetails

        #region ModuleSpecificDetails
        private static ModuleSpecificDetails CreateModuleSpecificDetails(ModulePip pip, PathTable pathTable, StringTable stringTable)
        {
            return new ModuleSpecificDetails
            {
                Identity = CreateString(pip.Identity, stringTable),
                DestinationFile = CreateString(pip.Location.Path, pathTable),
                Definition = CreateString(pip.Location),
            };
        }
        #endregion ModuleSpecificDetails

        #region HashSourceFileSpecificDetails
        private static HashSourceFileSpecificDetails CreateHashSourceFileSpecificDetails(HashSourceFile pip, PathTable pathTable)
        {
            return new HashSourceFileSpecificDetails
            {
                Artifact = CreateString(pip.Artifact.Path, pathTable)
            };
        }
        #endregion HashSourceFileSpecificDetails

        #region SealDirectorySpecificDetails
        private static SealDirectorySpecificDetails CreateSealDirectorySpecificDetails(SealDirectory pip, PathTable pathTable)
        {
            return new SealDirectorySpecificDetails
            {
                Kind = Enum.Format(typeof(SealDirectoryKind), pip.Kind, "f"),
                Scrub = pip.Scrub,
                DirectoryRoot = CreateString(pip.DirectoryRoot, pathTable),
                DirectoryArtifact = CreateString(pip.Directory),
                ComposedDirectories = CreateString(pip.ComposedDirectories, pathTable),
                ContentFilter = CreateString(pip.ContentFilter),
            };
        }
        #endregion SealDirectorySpecificDetails

        #region WriteFileSpecificDetails
        private static WriteFileSpecificDetails CreateWriteFileSpecificDetails(WriteFile pip, PathTable pathTable)
        {
            return new WriteFileSpecificDetails
            {
                Contents = CreateString(pip.Contents, pathTable),
                FileEncoding = pip.Encoding.ToString(),
            };
        }
        #endregion WriteFileSpecificDetails

        #region ObservedFileAccesses
        private static ReportedProcessData CreateReportedProcessData(ReportedProcess data)
        {
            return new ReportedProcessData
            {
                ProcessId = data.ProcessId,
                ParentProcessId = data.ParentProcessId,
                Path = data.Path,
                ProcessArgs = data.ProcessArgs,
                CreationTime = CreateString(data.CreationTime),
                ExitTime = CreateString(data.ExitTime),
                ExitCode = data.ExitCode,
                KernelTime = CreateNumeric(data.KernelTime),
                UserTime = CreateNumeric(data.UserTime),
                IOCountersRead = CreateString(data.IOCounters.ReadCounters),
                IOCountersWrite = CreateString(data.IOCounters.WriteCounters),
                IOCountersOther = CreateString(data.IOCounters.OtherCounters)
            };
        }

        private static ReportedFileAccessData CreateReportedFileAccessData(ReportedFileAccess data, PathTable pathTable)
        {
            return new ReportedFileAccessData
            {
                Path = string.IsNullOrWhiteSpace(data.Path) ? CreateString(data.ManifestPath, pathTable) : data.Path,
                RequestedAccess = CreateString(data.RequestedAccess),
                CreationDisposition = CreateString(data.CreationDisposition),
                DesiredAccess = CreateString(data.DesiredAccess),
                ShareMode = CreateString(data.ShareMode),
                Status = CreateString(data.Status),
                Operation = CreateString(data.Operation),
                FlagsAndAttributes = CreateString(data.FlagsAndAttributes),
                Error = data.Error,
                Usn = data.Usn.Value,
                ManifestPath = CreateString(data.ManifestPath, pathTable),
                Process = data.Process.ProcessId,
                ExplicitlyReported = data.ExplicitlyReported,
                EnumeratePattern = data.EnumeratePattern,
                IsOpenedHandleDirectory = data.IsOpenedHandleDirectory(),
                OpenedFileOrDirectoryAttributes = CreateString(data.OpenedFileOrDirectoryAttributes)
            };
        }

        private static ProcessDetouringStatusDataJson CreateProcessDetouringStatusDataJson(ProcessDetouringStatusData data)
        {
            return new ProcessDetouringStatusDataJson
            {
                ProcessId = data.ProcessId,
                JobId = data.Job,
                ReportStatus = data.ReportStatus,
                ProcessName = data.ProcessName,
                StartApplicationName = data.StartApplicationName,
                StartCommandLine = data.StartCommandLine,
                NeedsInjection = data.NeedsInjection,
                NeedsRemoteInjection = data.NeedsRemoteInjection,
                IsCurrent64BitProcess = data.IsCurrent64BitProcess,
                IsCurrentWow64Process = data.IsCurrentWow64Process,
                IsProcessWow64 = data.IsProcessWow64,
                DisableDetours = data.DisableDetours,
                CreationFlags = data.CreationFlags.ToString(CultureInfo.InvariantCulture),
                Detoured = data.Detoured,
                Error = data.Error,
                CreateProcessStatusReturn = data.CreateProcessStatusReturn,
            };
        }
        #endregion ObservedFileAccesses

        #region StringHelperFunctions
        private static string CreateString(AbsolutePath value, PathTable pathTable)
        {
            return (value.IsValid ? value.ToString(pathTable) : null);
        }

        private static string CreateString(StringId value, StringTable stringTable)
        {
            return (value.IsValid ? value.ToString(stringTable) : null);
        }

        private static string CreateString(PipData value, PathTable pathTable)
        {
            return (value.IsValid ? value.ToString(pathTable) : null);
        }

        private static string CreateString(PipId value)
        {
            return (value.IsValid ? value.ToString() : null);
        }

        private static string CreateString(FullSymbol value, SymbolTable symbolTable)
        {
            return (value.IsValid ? value.ToString(symbolTable) : null);
        }

        private static string CreateString(LocationData value)
        {
            return (value.IsValid ? string.Format(CultureInfo.InvariantCulture, "({0},{1})", value.Line, value.Position) : null);
        }

        private static string CreateString(DirectoryArtifact value)
        {
            return (value.IsValid ? $"{value.Path.RawValue}:{value.PartialSealId}:{(value.IsSharedOpaque ? 1 : 0)}" : null);
        }

        private static string CreateString(SealDirectoryContentFilter? value)
        {
            return (value.HasValue ? $"{value.Value.Regex} (kind: {Enum.Format(typeof(SealDirectoryContentFilter.ContentFilterKind), value.Value.Kind, "f")})" : null);
        }

        private static List<string> CreateString(IEnumerable<PipId> values)
        {
            return GetJsonFriendlyList(values.Where(value => value.IsValid).Select(value => value.ToString()));
        }

        private static List<string> CreateString(IEnumerable<AbsolutePath> values, PathTable pathTable)
        {
            return GetJsonFriendlyList(values.Where(value => value.IsValid).Select(value => value.ToString(pathTable)));
        }

        private static List<string> CreateString(IEnumerable<ProcessSemaphoreInfo> values, StringTable stringTable)
        {
            return GetJsonFriendlyList(values.Where(value => value.IsValid).Select(value => string.Format(CultureInfo.InvariantCulture, "{0} (value:{1} limit:{2})", value.Name.ToString(stringTable), value.Value, value.Limit)));
        }

        private static List<string> CreateString(IEnumerable<FileArtifact> values, PathTable pathTable)
        {
            return GetJsonFriendlyList(values.Where(value => value.Path.IsValid).Select(value => value.Path.ToString(pathTable)));
        }

        private static List<string> CreateString(IEnumerable<DirectoryArtifact> values, PathTable pathTable)
        {
            return GetJsonFriendlyList(values.Where(value => value.Path.IsValid).Select(value => value.Path.ToString(pathTable)));
        }

        private static List<string> CreateString(IEnumerable<FileArtifactWithAttributes> values, PathTable pathTable)
        {
            return GetJsonFriendlyList(values.Where(value => value.Path.IsValid).Select(value => value.Path.ToString(pathTable) + " (" + Enum.Format(typeof(FileExistence), value.FileExistence, "f") + ")"));
        }

        private static string CreateString(DateTime value)
        {
            return value.ToLongTimeString();
        }

        private static string CreateString(IOTypeCounters value)
        {
            return value != null ? $"opCount: {value.OperationCount}, transferCount: {value.TransferCount}" : null;
        }

        private static string CreateString(RequestedAccess value)
        {
            return $"[{value}]";
        }

        private static string CreateString<T>(T value) where T : Enum
        {
            return value != null ? Enum.Format(typeof(T), value, "f") : null;
        }

        private static long? CreateNumeric(TimeSpan? value)
        {
            return value.HasValue ? Convert.ToInt64(value.Value.TotalMilliseconds) : null;
        }

        /// <summary>
        /// Returns a list if the value is non-empty, or null if it is empty. Returning null prevents System.Text.Json from printing out emtpy lists.
        /// </summary>
        private static List<T> GetJsonFriendlyList<T>(IEnumerable<T> value)
        {
            if (value != null && value.Any())
            {
                return value.ToList();
            }

            return null;
        }

        private static List<string> GetDirectoryDependencies(ReadOnlyArray<DirectoryArtifact> dependencies, PathTable pathTable, PipGraph graph)
        {
            return GetJsonFriendlyList(
                dependencies
                .Where(value => value.Path.IsValid)
                .Select(value => {
                    var pipHash = graph.GetFormattedSemiStableHash(graph.GetSealedDirectoryNode(value).ToPipId());
                    return $"{value.Path.ToString(pathTable)} (SealDirectorySemiStableHash: {pipHash}, PartialSealId: {value.PartialSealId}, IsSharedOpaque: {(value.IsSharedOpaque ? 1 : 0)})";
                })
            );
        }

        #endregion StringHelperFunctions

        #endregion SerializationHelperFunctions
    }
}
