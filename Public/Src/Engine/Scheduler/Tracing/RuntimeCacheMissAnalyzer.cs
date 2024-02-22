// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Engine.Cache;
using BuildXL.Native.IO;
using BuildXL.Pips;
using BuildXL.Pips.DirectedGraph;
using BuildXL.Pips.Graph;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.ParallelAlgorithms;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Core.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static BuildXL.Scheduler.Tracing.FingerprintStore;
using static BuildXL.Utilities.Core.FormattableStringEx;
using System.Net.Sockets;
using System.Linq;
using System.Globalization;

namespace BuildXL.Scheduler.Tracing
{
    /// <summary>
    /// Logging target for sending inputs to fingerprint computation to fingerprint input store.
    /// Encapsulates the logic for serializing entries for the fingerprint store.
    /// </summary>
    public sealed class RuntimeCacheMissAnalyzer : IDisposable
    {
        /// <summary>
        /// Initiates the task to load the fingerprint store that will be used for cache miss analysis
        /// </summary>
        public static async Task<RuntimeCacheMissAnalyzer> TryCreateAsync(
            FingerprintStoreExecutionLogTarget logTarget,
            LoggingContext loggingContext,
            PipExecutionContext context,
            IConfiguration configuration,
            EngineCache cache,
            IReadonlyDirectedGraph graph,
            IDictionary<PipId, RunnablePipPerformanceInfo> runnablePipPerformance,
            FingerprintStoreTestHooks testHooks = null)
        {
            // Unblock caller
            await Task.Yield();

            using (logTarget.Counters.StartStopwatch(FingerprintStoreCounters.InitializeCacheMissAnalysisDuration))
            {
                var option = configuration.Logging.CacheMissAnalysisOption;
                string downLoadedPriviousFingerprintStoreSavedPath = null;
                if (option.Mode == CacheMissMode.Disabled)
                {
                    return null;
                }

                // When using automatic key inference from git hashes, we want to analyze the candidate keys
                // that were succesful. We'll use this dictionary to assign a provenance to each key. 
                // The idea is to analyze if this heuristic is actually successful or not, 
                // TODO [maly] Remove this log-line after analysis
                var keyOrigin = option.Mode == CacheMissMode.AzureDevOps
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : null;

                Possible<FingerprintStore> possibleStore;

                PathTable newPathTable = new PathTable();
                if (option.Mode == CacheMissMode.Local)
                {
                    possibleStore = CreateSnapshot(logTarget.ExecutionFingerprintStore, loggingContext);
                }
                else
                {
                    string path = null;
                    if (option.Mode == CacheMissMode.CustomPath)
                    {
                        path = option.CustomPath.ToString(context.PathTable);
                    }
                    else
                    {
                        IReadOnlyList<string> keys = option.Keys;
                        Contract.Assert(option.Mode == CacheMissMode.Remote || option.Mode == CacheMissMode.AzureDevOps);
                        if (option.Mode == CacheMissMode.AzureDevOps)
                        {
                            Contract.Assert(configuration.Infra == Infra.Ado);
                            keys = GetKeysFromAdoEnvironment(keyOrigin, loggingContext);
                        }

                        if (keys == null)
                        {
                            // Details have been logged in the methods above
                            return null;
                        }

                        testHooks?.SelectedCandidateKeys(keys);
                        foreach (var key in keys)
                        {
                            var fingerprintsLogDirectoryStr = configuration.Logging.FingerprintsLogDirectory.ToString(context.PathTable);
                            var fingerprintsLogDirectory = AbsolutePath.Create(newPathTable, fingerprintsLogDirectoryStr);

                            var cacheSavePath = fingerprintsLogDirectory.Combine(newPathTable, Scheduler.FingerprintStoreDirectory + "." + key);
                            var result = await cache.TryRetrieveFingerprintStoreAsync(loggingContext, cacheSavePath, newPathTable, key, configuration, context.CancellationToken);
                            if (result.Succeeded && result.Result)
                            {
                                // TODO [maly] Remove this logging after analysis
                                if (keyOrigin?.TryGetValue(key, out var origin) == true)
                                {
                                    Logger.Log.GettingFingerprintStoreTrace(loggingContext, $"Successful key: ${key}. Origin: {origin}");
                                }

                                path = cacheSavePath.ToString(newPathTable);
                                downLoadedPriviousFingerprintStoreSavedPath = path;
                                break;
                            }
                        }

                        if (string.IsNullOrEmpty(path))
                        {
                            Logger.Log.GettingFingerprintStoreTrace(loggingContext, I($"Could not find the fingerprint store for any given key: {string.Join(",", option.Keys)}"));
                            return null;
                        }
                    }

                    possibleStore = Open(path, readOnly: true);
                }

                if (possibleStore.Succeeded)
                {
                    Logger.Log.SuccessLoadFingerprintStoreToCompare(loggingContext, option.Mode.ToString(), possibleStore.Result.StoreDirectory);
                    return new RuntimeCacheMissAnalyzer(
                        logTarget,
                        loggingContext,
                        context,
                        possibleStore.Result,
                        graph,
                        runnablePipPerformance,
                        configuration,
                        downLoadedPriviousFingerprintStoreSavedPath,
                        testHooks: testHooks);
                }

                Logger.Log.GettingFingerprintStoreTrace(loggingContext, I($"Failed to read the fingerprint store to compare. Mode: {option.Mode} Failure: {possibleStore.Failure.DescribeIncludingInnerFailures()}"));
                return null;
            }
        }

        private static IReadOnlyList<string> GetKeysFromAdoEnvironment(Dictionary<string, string> keyOrigin, LoggingContext loggingContext)
        {
            var candidateList = new List<string>();
            // Retrieve contextual information about the branch the build is running from the Azure DevOps environment variables, and use those values as keys.
            // We consider two scenarios: builds running from a PR trigger and builds that don't.
            // For builds running on a PR, these are the candidate keys we use:
            //  1. The PR merge-branch name (e.g., /refs/pull/1234/merge)
            //  2. The source branch for the PR (e.g., /refs/dev/chelo/myFeatureBranch)
            //  3. The target branch for the PR (e.g., /refs/heads/main)
            // The rationale is that we want to get the latest fingerprint store pushed by other iterations from 'this' PR,
            // which will share the PR branch name, and if we can't find that (for example, in the first build for that PR)
            // we fall back to the closest branches that might have been built before in a non-PR build
            // (and thus pushed a fingerprint store using their branch names).
            // For builds triggered outside of PRs, we just use the branch name as the single candidate key.
            //
            // This strategy is effective assuming the target branches are built regularly and that FP stores from those branches
            // will become accessible from the PR builds (e.g., assuming PRs target 'main', we assume that the 'main' branch
            // is built with a regular cadence, and contributes to the same FP store universe so that the FP store is available
            // for PRs to consume when resolving the SYSTEM_PULLREQUEST_TARGETBRANCH key below).
            addCandidateFromAdoVariable("BUILD_SOURCEBRANCH");
            addCandidateFromAdoVariable("SYSTEM_PULLREQUEST_SOURCEBRANCH");
            addCandidateFromAdoVariable("SYSTEM_PULLREQUEST_TARGETBRANCH");

            if (candidateList.Count == 0)
            {
                Logger.Log.GettingFingerprintStoreTrace(loggingContext, $"Expected ADO predefined variables to be defined to retrieve fingerprint store keys on an ADO Build, but none were.");
                return null;
            }

            return candidateList;

            void addCandidateFromAdoVariable(string variableName)
            {
				// PR variables are not defined when the build is not triggered by a PR
				var maybeVariableValue = GetKeyFromAdoEnvironment(variableName);
                if (maybeVariableValue != null)
                {
                    candidateList.Add(maybeVariableValue);
                    keyOrigin[maybeVariableValue] = variableName;   // 'The key comes from this environment variable'
                }
            }

        }

        private static string GetKeyFromAdoEnvironment(string variableName)
        {
            var variableValue = Environment.GetEnvironmentVariable(variableName);

			if (string.IsNullOrEmpty(variableValue))
			{
				return null;
			}

            // Sanitize - we need this to be a path atom down the line
            var chars = variableValue.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (!PathAtom.IsValidPathAtomChar(chars[i]))
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        private readonly FingerprintStoreExecutionLogTarget m_logTarget;
        private CounterCollection<FingerprintStoreCounters> Counters => m_logTarget.Counters;
        private readonly LoggingContext m_loggingContext;
        private readonly NodeVisitor m_visitor;
        private readonly VisitationTracker m_changedPips;
        private readonly IDictionary<PipId, RunnablePipPerformanceInfo> m_runnablePipPerformance;
        private readonly PipExecutionContext m_context;

        private static int MaxCacheMissCanPerform => EngineEnvironmentSettings.MaxNumPipsForCacheMissAnalysis.Value;
        private int m_numCacheMissPerformed = 0;

        private readonly string m_downLoadedPreviousFingerprintStoreSavedPath = null;

        /// <summary>
        /// Dictionary of cache misses for runtime cache miss analysis.
        /// </summary>
        private readonly ConcurrentDictionary<PipId, PipCacheMissInfo> m_pipCacheMissesDict;

        private readonly NagleQueue<JProperty> m_batchLoggingQueue;

        private readonly IConfiguration m_configuration;

        /// <summary>
        /// A previous build's <see cref="FingerprintStore"/> that can be used for cache miss comparison.
        /// This may also be a snapshot of the current build's main <see cref="FingerprintStore"/> at the beginning of the build.
        /// </summary>
        public FingerprintStore PreviousFingerprintStore { get; }

        private CacheMissDiffFormat CacheMissDiffFormat => m_configuration.Logging.CacheMissDiffFormat;
        private readonly FingerprintStoreTestHooks m_testHooks;

        private RuntimeCacheMissAnalyzer(
            FingerprintStoreExecutionLogTarget logTarget,
            LoggingContext loggingContext,
            PipExecutionContext context,
            FingerprintStore previousFingerprintStore,
            IReadonlyDirectedGraph graph,
            IDictionary<PipId, RunnablePipPerformanceInfo> runnablePipPerformance,
            IConfiguration configuration,
            string downLoadedPreviousFingerprintStoreSavedPath,
            FingerprintStoreTestHooks testHooks = null)
        {
            m_loggingContext = loggingContext;
            m_logTarget = logTarget;
            m_context = context;
            PreviousFingerprintStore = previousFingerprintStore;
            m_visitor = new NodeVisitor(graph);
            m_changedPips = new VisitationTracker(graph);
            m_pipCacheMissesDict = new ConcurrentDictionary<PipId, PipCacheMissInfo>();
            m_runnablePipPerformance = runnablePipPerformance;

            m_batchLoggingQueue = configuration.Logging.CacheMissBatch ? NagleQueue<JProperty>.Create(
                BatchLogging,
                maxDegreeOfParallelism: 1,
                interval: TimeSpan.FromMinutes(5),
                batchSize: 100) : null;


            m_testHooks = testHooks;
            m_testHooks?.InitRuntimeCacheMisses();
            m_configuration = configuration;
            m_downLoadedPreviousFingerprintStoreSavedPath = downLoadedPreviousFingerprintStoreSavedPath;
        }

        /// <summary>
        /// The batch log payload example: 
        /// {"CacheMissAnalysisResults":
        ///     {
        ///         Pip123: {
        ///             Description:
        ///             FromCacheLookUp:
        ///             Detail: {
        ///                ActualMissType: ...
        ///                ReasonFromAnalysis: ...
        ///                Info: ...
        ///             }
        ///         },
        ///         Pip345: {
        ///             Description:
        ///             FromCacheLookUp:
        ///             Detail: {
        ///                ActualMissType: ...
        ///                ReasonFromAnalysis: ...
        ///                Info: ...
        ///             }
        ///         },
        ///     }
        ///}
        /// </summary>
        internal Task<Unit> BatchLogging(List<JProperty> results)
        {
            // Use JsonTextWritter for 2 reasons:
            // 1. easily control when to start a new log event and when to end it.
            // 2. according to some research, manually serialization with JsonTextWritter can improve performance.
            using (Counters.StartStopwatch(FingerprintStoreCounters.CacheMissBatchLoggingTime))
            {
                ProcessResults(results, m_configuration, m_loggingContext);
                Counters.AddToCounter(FingerprintStoreCounters.CacheMissBatchingDequeueCount, results.Count);
                return Unit.VoidTask;
            }
        }

        internal static void ProcessResults(List<JProperty> results, IConfiguration configuration, LoggingContext loggingContext)
        {
            int maxLogSize = configuration.Logging.AriaIndividualMessageSizeLimitBytes;

            using var sbPool = Pools.GetStringBuilder();
            var sb = sbPool.Instance;
            using var sw = new StringWriter(sb);
            using var writer = new JsonTextWriter(sw);

            var logStarted = false;
            var hasProperty = false;
            var lenSum = 0;
            for (int i = 0; i < results.Count; i++)
            {
                startLoggingIfNot();

                var name = results[i].Name.ToString();
                var value = results[i].Value.ToString();
                lenSum += name.Length + value.Length;
                if (lenSum < maxLogSize)
                {
                    writeProperty(name, value);
                }
                else
                {
                    // End the current batch before start a new one.
                    endLoggingIfStarted();

                    // Log a single event, if this single result itself is too big.
                    if ((name.Length + value.Length) >= maxLogSize)
                    {
                        // Have to shorten the result to fit the telemetry.
                        var marker = "[...]";
                        var prefix = value.Substring(0, maxLogSize / 2);
                        var suffix = value.Substring(value.Length - maxLogSize / 2);
                        logAsSingle(name, prefix + marker + suffix);
                    }
                    else
                    {
                        // Start a new batch.
                        startLoggingIfNot();
                        writeProperty(name, value);
                        lenSum = name.Length + value.Length;
                    }
                }
            }

            endLoggingIfStarted();

            void writeProperty(string name, string value)
            {
                writer.WritePropertyName(name);
                writer.WriteRawValue(value);
                hasProperty = true;
            }

            void endLogging()
            {
                writer.WriteEndObject();
                writer.WriteEndObject();
                // Only log when has result in it.
                if (hasProperty)
                {
                    Logger.Log.CacheMissAnalysisBatchResults(loggingContext, sw.ToString());
                }

                logStarted = false;
                hasProperty = false;
                lenSum = 0;
                writer.Flush();
                sb.Clear();
            }

            void endLoggingIfStarted()
            {
                // Only log when at least one result has been written to the Json string
                if (logStarted)
                {
                    endLogging();
                }
            }

            void startLogging()
            {
                writer.Flush();
                sb.Clear();
                writer.WriteStartObject();
                writer.WritePropertyName("CacheMissAnalysisResults");
                writer.WriteStartObject();
                logStarted = true;
            }

            void startLoggingIfNot()
            {
                // Only log when at least one result has been written to the Json string
                if (!logStarted)
                {
                    startLogging();
                }
            }

            void logAsSingle(string name, string value)
            {
                startLogging();
                writeProperty(name, value);
                endLogging();
            }
        }

        /// <summary>
        /// Consider we may get multiple cache miss info for the same pip id when cache lookup fails/timeouts and it is retried
        /// </summary>
        internal void AddCacheMiss(PipCacheMissInfo cacheMissInfo) => m_pipCacheMissesDict[cacheMissInfo.PipId] = cacheMissInfo;

        internal void AnalyzeForCacheLookup(FingerprintStoreEntry newEntry, Process pip) => Analyze(newEntry, pip, fromCacheLookup: true);

        internal void AnalyzeForExecution(FingerprintStoreEntry newEntry, Process pip) => Analyze(newEntry, pip, fromCacheLookup: false);

        private void Analyze(FingerprintStoreEntry newEntry, Process pip, bool fromCacheLookup)
        {
            Contract.Requires(pip != null);

            using var watch = new CacheMissTimer(pip.PipId, this);

            if (!IsCacheMissEligible(pip.PipId))
            {
                return;
            }

            TryGetFingerprintStoreEntry(pip, out FingerprintStoreEntry oldEntry);
            PerformCacheMissAnalysis(pip, oldEntry, newEntry, fromCacheLookup);
        }

        private void PerformCacheMissAnalysis(Process pip, FingerprintStoreEntry oldEntry, FingerprintStoreEntry newEntry, bool fromCacheLookup)
        {
            Contract.Requires(pip != null);
            string pipDescription = pip.GetDescription(m_context);

            try
            {
                if (!m_pipCacheMissesDict.TryRemove(pip.PipId, out PipCacheMissInfo missInfo))
                {
                    return;
                }

                if (missInfo.CacheMissType != PipCacheMissType.MissForProcessConfiguredUncacheable)
                {
                    // If the pip is configured uncacheable, we want to show that the pip has a cache miss
                    // due to being configured uncacheable. Additionally, we still want to see the cache miss reason
                    // for its downstream pips as well. In other words, we want to expand the frontier pips that are
                    // eligible for cache miss analysis when there are uncacheable parent pips. To that end,
                    // we will not mark the pip as changed if it is configured uncacheable.
                    MarkPipAsChanged(pip.PipId);
                }

                if (fromCacheLookup)
                {
                    Counters.IncrementCounter(FingerprintStoreCounters.CacheMissAnalysisAnalyzeCacheLookUpCount);
                }
                else
                {
                    Counters.IncrementCounter(FingerprintStoreCounters.CacheMissAnalysisAnalyzeExecutionCount);
                }

                using (var pool = Pools.StringBuilderPool.GetInstance())
                using (Counters.StartStopwatch(FingerprintStoreCounters.CacheMissAnalysisAnalyzeDuration))
                {
                    var resultAndDetail = CacheMissAnalysisUtilities.AnalyzeCacheMiss(
                        missInfo,
                        () => new FingerprintStoreReader.PipRecordingSession(PreviousFingerprintStore, oldEntry),
                        () => new FingerprintStoreReader.PipRecordingSession(m_logTarget.ExecutionFingerprintStore, newEntry),
                        CacheMissDiffFormat);

                    pipDescription = pip.GetDescription(m_context);

                    if (m_batchLoggingQueue != null)
                    {
                        Counters.IncrementCounter(FingerprintStoreCounters.CacheMissBatchingEnqueueCount);
                        m_batchLoggingQueue.Enqueue(resultAndDetail.Detail.ToJObjectWithPipInfo(pip.FormattedSemiStableHash, pipDescription, fromCacheLookup));
                    }
                    else
                    {
                        var detail = new JObject(
                            new JProperty(nameof(resultAndDetail.Detail.ActualMissType), resultAndDetail.Detail.ActualMissType), 
                            new JProperty(nameof(resultAndDetail.Detail.ReasonFromAnalysis), resultAndDetail.Detail.ReasonFromAnalysis), 
                            new JProperty(nameof(resultAndDetail.Detail.Info), resultAndDetail.Detail.Info)).ToString();
                        Logger.Log.CacheMissAnalysis(m_loggingContext, pipDescription, detail, fromCacheLookup);
                    }

                    m_testHooks?.AddCacheMiss(
                        pip.PipId,
                        new FingerprintStoreTestHooks.CacheMissData
                        {
                            DetailAndResult = resultAndDetail,
                            IsFromCacheLookUp = fromCacheLookup
                        });
                }
            }
            catch (Exception ex)
            {
                // Cache miss analysis shouldn't fail the build
                Logger.Log.CacheMissAnalysisException(m_loggingContext, pipDescription, ex.ToString(), oldEntry?.PipToFingerprintKeys.ToString(), newEntry?.PipToFingerprintKeys.ToString());
            }
        }

        private bool IsCacheMissEligible(PipId pipId)
        {
            if ((Interlocked.Increment(ref m_numCacheMissPerformed) - 1) >= MaxCacheMissCanPerform)
            {
                Counters.IncrementCounter(FingerprintStoreCounters.CacheMissAnalysisExceedMaxNumAndCannotPerformCount);
                return false;
            }

            if (!m_pipCacheMissesDict.ContainsKey(pipId))
            {
                return false;
            }

            if (!EngineEnvironmentSettings.RuntimeCacheMissAllPips
                && m_changedPips.WasVisited(pipId.ToNodeId()))
            {
                return false;
            }

            return true;
        }

        private void MarkPipAsChanged(PipId pipId) => m_visitor.VisitTransitiveDependents(pipId.ToNodeId(), m_changedPips, n => true);

        private bool TryGetFingerprintStoreEntry(Process process, out FingerprintStoreEntry entry)
        {
            using (Counters.StartStopwatch(FingerprintStoreCounters.CacheMissFindOldEntriesTime))
            {
                string pipUniqueOutputHashStr = process.TryComputePipUniqueOutputHash(m_context.PathTable, out var pipUniqueOutputHash, m_logTarget.PipContentFingerprinter.PathExpander)
                    ? pipUniqueOutputHash.ToString()
                    : null;

                return PreviousFingerprintStore.TryGetFingerprintStoreEntry(pipUniqueOutputHashStr, process.FormattedSemiStableHash, out entry);
            }
        }

        /// <nodoc/>
        public void Dispose()
        {
            using (Counters.StartStopwatch(FingerprintStoreCounters.PreviousFingerprintStoreDisposeDuration))
            {
                PreviousFingerprintStore.Dispose();
                DeletePreviousFingerprintStoreDirectory();
            }

            using (Counters.StartStopwatch(FingerprintStoreCounters.RuntimeCacheMissBatchLoggingQueueDisposeDuration))
            {
                m_batchLoggingQueue?.Dispose();
            }
        }

        private void DeletePreviousFingerprintStoreDirectory()
        {
            if (!string.IsNullOrEmpty(m_downLoadedPreviousFingerprintStoreSavedPath) && FileUtilities.Exists(m_downLoadedPreviousFingerprintStoreSavedPath))
            {
                FileUtilities.DeleteDirectoryContents(m_downLoadedPreviousFingerprintStoreSavedPath, true);
            }
        }

        /// <summary>
        /// Choose a key with which to store the fingerprint store for this build so as to be consistent with subsequent retrieval attempts.
        /// </summary>
        internal static string GetStoreKeyForCurrentBuild(CacheMissAnalysisOption option, LoggingContext loggingContext)
        {
            if (option.Mode == CacheMissMode.AzureDevOps)
            {
                return GetKeyFromAdoEnvironment("BUILD_SOURCEBRANCH");
            }
            else
            {
                // Use the first key as a store key.
                return option.Keys.FirstOrDefault();
            }
        }

        private readonly struct CacheMissTimer : IDisposable
        {
            private readonly RuntimeCacheMissAnalyzer m_analyzer;
            private readonly PipId m_pipId;
            private readonly CounterCollection.Stopwatch m_watch;

            public CacheMissTimer(PipId pipId, RuntimeCacheMissAnalyzer analyzer)
            {
                m_analyzer = analyzer;
                m_pipId = pipId;
                m_watch = m_analyzer.Counters.StartStopwatch(FingerprintStoreCounters.CacheMissAnalysisTime);
            }

            public void Dispose()
            {
                if (m_analyzer.m_runnablePipPerformance?.TryGetValue(m_pipId, out RunnablePipPerformanceInfo performance) == true)
                {
                    performance.PerformedCacheMissAnalysis(m_watch.Elapsed);
                }

                m_watch.Dispose();
            }
        }
    }
}
