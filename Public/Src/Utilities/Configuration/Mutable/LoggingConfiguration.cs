// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Diagnostics.Tracing;
using BuildXL.Utilities.Core;

namespace BuildXL.Utilities.Configuration.Mutable
{
    /// <nodoc />
    public sealed class LoggingConfiguration : WarningHandling, ILoggingConfiguration
    {
        /// <nodoc />
        public LoggingConfiguration()
        {
            CustomLog = new Dictionary<AbsolutePath, (IReadOnlyList<int>, EventLevel?)>();
            CustomLogEtwKinds = new Dictionary<AbsolutePath, string>();
            NoLog = new List<int>();
            NoExecutionLog = new List<int>();
            ForwardableWorkerEvents = new List<int>();
            ConsoleVerbosity = VerbosityLevel.Informational;
            FileVerbosity = VerbosityLevel.Verbose;
            TraceInfo = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            Color = true;
            AnimateTaskbar = true;
            LogExecution = true;
            FingerprintStoreMode = FingerprintStoreMode.Default;
            FingerprintStoreMaxEntryAgeMinutes = 4320; // 3 days
            FingerprintStoreBulkLoad = false;
            EngineCacheLogDirectory = AbsolutePath.Invalid;
            EngineCacheCorruptFilesLogDirectory = AbsolutePath.Invalid;
            FingerprintsLogDirectory = AbsolutePath.Invalid;
            ExecutionFingerprintStoreLogDirectory = AbsolutePath.Invalid;
            CacheLookupFingerprintStoreLogDirectory = AbsolutePath.Invalid;
            HistoricMetadataCacheLogDirectory = AbsolutePath.Invalid;
            SubstSource = AbsolutePath.Invalid;
            SubstTarget = AbsolutePath.Invalid;
            FancyConsole = true;
            FancyConsoleMaxStatusPips = 5;
            FailPipOnFileAccessError = true;
            UseCustomPipDescriptionOnConsole = true;
            AdoConsoleMaxIssuesToLog = 100;
            CacheMissAnalysisOption = CacheMissAnalysisOption.Disabled();
            CacheMissDiffFormat = CacheMissDiffFormat.CustomJsonDiff;
            AriaIndividualMessageSizeLimitBytes = (int)(0.8 * 1024 * 1024); // 0.8Mb out of Aria's current 1Mb max limit
            MaxNumPipTelemetryBatches = 1;
            CacheMissBatch = true;
            RedirectedLogsDirectory = AbsolutePath.Invalid;
            DumpFailedPips = true;
            DumpFailedPipsLogLimit = 50;
            DumpFailedPipsWithDynamicData = false;
            LogCachedPipOutputs = false;
            // Telemetry is enabled by default when the build has access to the Aria telemetry client. This is a Microsoft
            // internal only package so this corresponds to versions of BuildXL used internally within Microsoft. This define
            // controls whether we attempt to reference the telemetry utilities. It will fail at runtime if that client library
            // package is not available.
#if FEATURE_ARIA_TELEMETRY
            RemoteTelemetry = Configuration.RemoteTelemetry.EnabledAndNotify;
#endif

            PerfCollectorFrequencyMs = 5_000;
            LogToKusto = false;
            LogEventsToConsole = new List<int>();
            DisplayWarningErrorTime = false;
            EnableCloudBuildEtwLoggingIntegration = false;
        }

        /// <nodoc />
        public LoggingConfiguration(ILoggingConfiguration template, PathRemapper pathRemapper)
            : base(template)
        {
            Contract.Assume(template != null);
            Contract.Assume(pathRemapper != null);

            LogsDirectory = pathRemapper.Remap(template.LogsDirectory);
            RedirectedLogsDirectory = pathRemapper.Remap(template.RedirectedLogsDirectory);
            LogPrefix = template.LogPrefix;
            Log = pathRemapper.Remap(template.Log);
            ErrorLog = pathRemapper.Remap(template.ErrorLog);
            WarningLog = pathRemapper.Remap(template.WarningLog);
            LogExecution = template.LogExecution;
            LogPackedExecution = template.LogPackedExecution;
            ExecutionLog = pathRemapper.Remap(template.ExecutionLog);
            StoreFingerprints = template.StoreFingerprints;
            SaveFingerprintStoreToLogs = template.SaveFingerprintStoreToLogs;
            FingerprintStoreMode = template.FingerprintStoreMode;
            FingerprintStoreMaxEntryAgeMinutes = template.FingerprintStoreMaxEntryAgeMinutes;
            FingerprintStoreBulkLoad = template.FingerprintStoreBulkLoad;
            FingerprintsLogDirectory = pathRemapper.Remap(template.FingerprintsLogDirectory);
            ExecutionFingerprintStoreLogDirectory = pathRemapper.Remap(template.ExecutionFingerprintStoreLogDirectory);
            CacheLookupFingerprintStoreLogDirectory = pathRemapper.Remap(template.CacheLookupFingerprintStoreLogDirectory);
            HistoricMetadataCacheLogDirectory = pathRemapper.Remap(template.HistoricMetadataCacheLogDirectory);
            EngineCacheLogDirectory = pathRemapper.Remap(template.EngineCacheLogDirectory);
            EngineCacheCorruptFilesLogDirectory = pathRemapper.Remap(template.EngineCacheCorruptFilesLogDirectory);
            CustomLog = new Dictionary<AbsolutePath, (IReadOnlyList<int>, EventLevel?)>();
            foreach (var kv in template.CustomLog)
            {
                CustomLog.Add(pathRemapper.Remap(kv.Key), kv.Value);
            }

            CustomLogEtwKinds = new Dictionary<AbsolutePath, string>();
            foreach (var kv in template.CustomLogEtwKinds)
            {
                CustomLogEtwKinds.Add(pathRemapper.Remap(kv.Key), kv.Value);
            }

            NoLog = new List<int>(template.NoLog);
            NoExecutionLog = new List<int>(template.NoExecutionLog);
            ForwardableWorkerEvents = new List<int>(template.ForwardableWorkerEvents);
            Diagnostic = template.Diagnostic;
            ConsoleVerbosity = template.ConsoleVerbosity;
            FileVerbosity = template.FileVerbosity;
            EnableAsyncLogging = template.EnableAsyncLogging;
            StatsLog = pathRemapper.Remap(template.StatsLog);
            StatsPrfLog = pathRemapper.Remap(template.StatsPrfLog);
            EventSummaryLog = pathRemapper.Remap(template.EventSummaryLog);
            Environment = template.Environment;
            RemoteTelemetry = template.RemoteTelemetry;
            TraceInfo = new Dictionary<string, string>();
            foreach (var kv in template.TraceInfo)
            {
                TraceInfo.Add(kv.Key, kv.Value);
            }

            Color = template.Color;
            AnimateTaskbar = template.AnimateTaskbar;
            RelatedActivityId = template.RelatedActivityId;
            LogsToRetain = template.LogsToRetain;
            FancyConsole = template.FancyConsole;
            FancyConsoleMaxStatusPips = template.FancyConsoleMaxStatusPips;
            SubstSource = pathRemapper.Remap(template.SubstSource);
            SubstTarget = pathRemapper.Remap(template.SubstTarget);
            DisableLoggedPathTranslation = template.DisableLoggedPathTranslation;
            StatusFrequencyMs = template.StatusFrequencyMs;
            PerfCollectorFrequencyMs = template.PerfCollectorFrequencyMs;
            StatusLog = pathRemapper.Remap(template.StatusLog);
            TraceLog = pathRemapper.Remap(template.TraceLog);
            CacheMissLog = pathRemapper.Remap(template.CacheMissLog);
            DevLog = pathRemapper.Remap(template.DevLog);
            RpcLog = pathRemapper.Remap(template.RpcLog);
            PipOutputLog = pathRemapper.Remap(template.PipOutputLog);
            FailPipOnFileAccessError = template.FailPipOnFileAccessError;
            LogMemory = template.LogMemory;
            ReplayWarnings = template.ReplayWarnings;
            UseCustomPipDescriptionOnConsole = template.UseCustomPipDescriptionOnConsole;
            AdoConsoleMaxIssuesToLog = template.AdoConsoleMaxIssuesToLog;
            CacheMissAnalysisOption = new CacheMissAnalysisOption(
                template.CacheMissAnalysisOption.Mode,
                new List<string>(template.CacheMissAnalysisOption.Keys),
                pathRemapper.Remap(template.CacheMissAnalysisOption.CustomPath));
            CacheMissDiffFormat = template.CacheMissDiffFormat;
            CacheMissBatch = template.CacheMissBatch;
            OptimizeConsoleOutputForAzureDevOps = template.OptimizeConsoleOutputForAzureDevOps;
            InvocationExpandedCommandLineArguments = template.InvocationExpandedCommandLineArguments;
            OptimizeProgressUpdatingForAzureDevOps = template.OptimizeProgressUpdatingForAzureDevOps;
            OptimizeVsoAnnotationsForAzureDevOps = template.OptimizeVsoAnnotationsForAzureDevOps;
            AriaIndividualMessageSizeLimitBytes = template.AriaIndividualMessageSizeLimitBytes;
            MaxNumPipTelemetryBatches = template.MaxNumPipTelemetryBatches;
            DumpFailedPips = template.DumpFailedPips;
            DumpFailedPipsLogLimit = template.DumpFailedPipsLogLimit;
            DumpFailedPipsWithDynamicData = template.DumpFailedPipsWithDynamicData;
            LogCachedPipOutputs = template.LogCachedPipOutputs;
            LogToKusto = template.LogToKusto;
            LogToKustoBlobUri = template.LogToKustoBlobUri;
            LogToKustoIdentityId = template.LogToKustoIdentityId;
            LogEventsToConsole = new List<int>(template.LogEventsToConsole);
            RemoteTelemetryFlushTimeout = template.RemoteTelemetryFlushTimeout;
            DisplayWarningErrorTime = template.DisplayWarningErrorTime;
            EnableCloudBuildEtwLoggingIntegration = template.EnableCloudBuildEtwLoggingIntegration;
        }

        /// <inheritdoc />
        public AbsolutePath LogsDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath RedirectedLogsDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath LogsRootDirectory(PathTable table)
        {
            return LogsToRetain > 0 ? LogsDirectory.GetParent(table) : LogsDirectory;
        }

        /// <inheritdoc />
        public string LogPrefix { get; set; }

        /// <inheritdoc />
        public AbsolutePath Log { get; set; }

        /// <inheritdoc />
        public AbsolutePath ErrorLog { get; set; }

        /// <inheritdoc />
        public AbsolutePath WarningLog { get; set; }

        /// <inheritdoc />
        public bool LogExecution { get; set; }

        /// <inheritdoc />
        public bool LogPackedExecution { get; set; }

        /// <inheritdoc />
        public AbsolutePath ExecutionLog { get; set; }

        /// <inheritdoc />
        public AbsolutePath EngineCacheLogDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath EngineCacheCorruptFilesLogDirectory { get; set; }

        /// <inheritdoc />
        public bool? StoreFingerprints { get; set; }

        /// <inheritdoc />
        public FingerprintStoreMode FingerprintStoreMode { get; set; }

        /// <inheritdoc />
        public bool? SaveFingerprintStoreToLogs { get; set; }

        /// <inheritdoc />
        public int FingerprintStoreMaxEntryAgeMinutes { get; set; }

        /// <inheritdoc />
        public bool FingerprintStoreBulkLoad { get; set; }

        /// <inheritdoc />
        public AbsolutePath FingerprintsLogDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath ExecutionFingerprintStoreLogDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath CacheLookupFingerprintStoreLogDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath HistoricMetadataCacheLogDirectory { get; set; }

        /// <nodoc />
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public Dictionary<AbsolutePath, (IReadOnlyList<int>, EventLevel?)> CustomLog { get; set; }

        /// <inheritdoc />
        IReadOnlyDictionary<AbsolutePath, (IReadOnlyList<int>, EventLevel?)> ILoggingConfiguration.CustomLog => CustomLog;

        /// <nodoc />
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public Dictionary<AbsolutePath, string> CustomLogEtwKinds { get; set; }

        /// <inheritdoc />
        IReadOnlyDictionary<AbsolutePath, string> ILoggingConfiguration.CustomLogEtwKinds => CustomLogEtwKinds;

        /// <nodoc />
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public List<int> NoLog { get; set; }

        /// <inheritdoc />
        IReadOnlyList<int> ILoggingConfiguration.NoLog => NoLog;

        /// <nodoc />
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public List<int> NoExecutionLog { get; set; }

        
        /// <nodoc />
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public List<int> ForwardableWorkerEvents { get; set; }

        /// <inheritdoc />
        IReadOnlyList<int> ILoggingConfiguration.NoExecutionLog => NoExecutionLog;
        
        /// <inheritdoc />
        IReadOnlyList<int> ILoggingConfiguration.ForwardableWorkerEvents => ForwardableWorkerEvents;

        /// <inheritdoc />
        public DiagnosticLevels Diagnostic { get; set; }

        /// <inheritdoc />
        public VerbosityLevel ConsoleVerbosity { get; set; }

        /// <inheritdoc />
        public VerbosityLevel FileVerbosity { get; set; }

        /// <inheritdoc />
        public bool LogMemory { get; set; }

        /// <inheritdoc />
        public bool? EnableAsyncLogging { get; set; }

        /// <inheritdoc />
        public AbsolutePath StatsLog { get; set; }

        /// <inheritdoc />
        public AbsolutePath StatsPrfLog { get; set; }

        /// <inheritdoc />
        public AbsolutePath EventSummaryLog { get; set; }

        /// <inheritdoc />
        public ExecutionEnvironment Environment { get; set; }

        /// <inheritdoc />
        public RemoteTelemetry? RemoteTelemetry { get; set; }

        /// <nodoc />
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public Dictionary<string, string> TraceInfo { get; set; }

        IReadOnlyDictionary<string, string> ILoggingConfiguration.TraceInfo => TraceInfo;

        /// <inheritdoc />
        public bool Color { get; set; }

        /// <inheritdoc />
        public bool AnimateTaskbar { get; set; }

        /// <inheritdoc />
        public string RelatedActivityId { get; set; }

        /// <inheritdoc />
        public int LogsToRetain { get; set; }

        /// <inheritdoc />
        public bool FancyConsole { get; set; }

        /// <inheritdoc />
        public int FancyConsoleMaxStatusPips { get; set; }

        /// <inheritdoc />
        public AbsolutePath SubstSource { get; set; }

        /// <inheritdoc />
        public AbsolutePath SubstTarget { get; set; }

        /// <inheritdoc />
        public AbsolutePath StatusLog { get; set; }

        /// <inheritdoc />
        public AbsolutePath TraceLog { get; set; }

        /// <inheritdoc />
        public AbsolutePath CacheMissLog { get; set; }

        /// <inheritdoc />
        public AbsolutePath DevLog { get; set; }

        /// <inheritdoc />
        public AbsolutePath RpcLog { get; set; }

        /// <inheritdoc />
        public AbsolutePath PipOutputLog { get; set; } 
        
        /// <inheritdoc />
        public AbsolutePath PluginLog { get; set; }

        /// <inheritdoc />
        public int StatusFrequencyMs { get; set; }

        /// <inheritdoc />
        public int PerfCollectorFrequencyMs { get; set; }

        /// <inheritdoc />
        public bool FailPipOnFileAccessError { get; set; }

        /// <inheritdoc />
        public bool DisableLoggedPathTranslation { get; set; }

        /// <inheritdoc />
        public bool? ReplayWarnings { get; set; }

        /// <inheritdoc />
        public bool UseCustomPipDescriptionOnConsole { get; set; }

        /// <inheritdoc/>
        public int AdoConsoleMaxIssuesToLog { get; set; }

        /// <inheritdoc />
        public CacheMissAnalysisOption CacheMissAnalysisOption { get; set; }

        /// <inheritdoc />
        public CacheMissDiffFormat CacheMissDiffFormat { get; set; }

        /// <inheritdoc />
        public bool CacheMissBatch { get; set; }

        /// <inheritdoc />
        public bool OptimizeConsoleOutputForAzureDevOps { get; set; }

        /// <inheritdoc />
        public IReadOnlyList<string> InvocationExpandedCommandLineArguments { get; set; }

        /// <inheritdoc />
        public bool OptimizeProgressUpdatingForAzureDevOps { get; set; }

        /// <inheritdoc />
        public bool OptimizeVsoAnnotationsForAzureDevOps { get; set; }

        /// <inheritdoc />
        public bool OptimizeWarningOrErrorAnnotationsForAzureDevOps { get; set; }

        /// <inheritdoc />
        public int AriaIndividualMessageSizeLimitBytes { get; set; }

        /// <inheritdoc />
        public int MaxNumPipTelemetryBatches { get; set; }

        /// <inheritdoc/>
        public bool? DumpFailedPips { get; set; }

        /// <inheritdoc/>
        public int? DumpFailedPipsLogLimit { get; set; }

        /// <inheritdoc/>
        public bool? DumpFailedPipsWithDynamicData { get; set; }

        /// <inheritdoc/>
        public bool LogCachedPipOutputs { get; set; }

        /// <inheritdoc/>
        public bool LogToKusto { get; set; }

        /// <inheritdoc/>
        public string LogToKustoBlobUri { get; set; }

        /// <inheritdoc/>
        public string LogToKustoIdentityId { get; set; }

        /// <nodoc/>
        public List<int> LogEventsToConsole { get; set; }

        /// <inheritdoc/>
        IReadOnlyList<int> ILoggingConfiguration.LogEventsToConsole => LogEventsToConsole;

        /// <inheritdoc/>
        public TimeSpan? RemoteTelemetryFlushTimeout { get; set; }
        
        /// <inheritdoc/>
        public bool DisplayWarningErrorTime { get; set; }

        /// <inheritdoc/>
        public bool EnableCloudBuildEtwLoggingIntegration { get; set; }
    }
}
