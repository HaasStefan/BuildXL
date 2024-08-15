// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.Linq;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Pips.Filter;
using BuildXL.Pips.Graph;
using BuildXL.Scheduler.Fingerprints;
using BuildXL.Storage;
using BuildXL.Storage.Fingerprints;
using BuildXL.Utilities;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Instrumentation.Common;

namespace BuildXL.Engine
{
    /// <summary>
    /// Computes a fingerprint for looking up a reloadable build graph.
    /// </summary>
    /// <remarks>
    /// This is analogous to a content fingerprint for a pip. Like pip caching, we first compute a precise-enough
    /// fingerprint which might point to a descriptor that contains additional assertions / required values.
    /// A graph fingerprint is the hash of the following values:
    /// - A version number (for invalidating fingerprints as graph changes are made).
    /// - The set of values to be built (TODO: This is relevant for the original value filtering, which is at evaluation time; all other filters apply after graph reload).
    /// - The set of *top level* config files and their hashes
    /// - The desired qualifiers (affects evaluation time and thus the produced graph)
    /// - The machine name (graphs are not currently machine independent)
    /// - The hashes of all BuildXL assemblies; some provide build logic or otherwise affect the spec -> graph evaluation.
    /// In short, this fingerprint should account for any readily-available (especially command-line / invocation sourced) data
    /// that affects what graph would be produced, holding actual spec file contents equal.
    /// </remarks>
    public static class GraphFingerprinter
    {
        /// <summary>
        /// Graph fingerprint version.
        /// </summary>
        /// <remarks>
        /// 10: Adding top-level hash in graph fingerprint.
        /// </remarks>
        internal const int GraphFingerprintVersion = 10;

        /// <summary>
        /// Computes a fingerprint for looking up a reloadable build graph. Null indicates failure
        /// </summary>
        internal static GraphFingerprint TryComputeFingerprint(
            LoggingContext loggingContext,
            IStartupConfiguration startUpConfig,
            IConfiguration config,
            PathTable pathTable,
            IEvaluationFilter evaluationFilter,
            FileContentTable fileContentTable,
            string commitId,
            [AllowNull] PipSpecificPropertiesConfig pipSpecificPropertiesConfig,
            EngineTestHooksData testHooks)
        {
            Contract.Requires(pathTable != null);
            Contract.Requires(startUpConfig != null);
            Contract.Requires(fileContentTable != null);
            Contract.Requires(evaluationFilter != null);

            Optional<CompositeGraphFingerprint> exactFingerprint = GenerateHash(
                loggingContext,
                startUpConfig,
                config,
                pathTable,
                evaluationFilter,
                fileContentTable,
                commitId,
                pipSpecificPropertiesConfig,
                testHooks);
            Optional<CompositeGraphFingerprint> compatibleFingerprint;
            if (!exactFingerprint.HasValue)
            {
                return null;
            }

            if (evaluationFilter.CanPerformPartialEvaluation)
            {
                compatibleFingerprint = GenerateHash(
                    loggingContext,
                    startUpConfig,
                    config,
                    pathTable,
                    null,
                    fileContentTable,
                    commitId,
                    pipSpecificPropertiesConfig,
                    testHooks);

                if (!compatibleFingerprint.HasValue)
                {
                    return null;
                }
            }
            else
            {
                compatibleFingerprint = exactFingerprint;
            }

            return new GraphFingerprint(
                exactFingerprint.Value.WithEvaluationFilter(evaluationFilter),
                compatibleFingerprint.Value.WithEvaluationFilter(evaluationFilter));
        }

        private static Optional<CompositeGraphFingerprint> GenerateHash(
            LoggingContext loggingContext,
            IStartupConfiguration startUpConfig,
            IConfiguration config,
            PathTable pathTable,
            IEvaluationFilter partialEvaluationData,
            FileContentTable fileContentTable,
            string commitId,
            [AllowNull] PipSpecificPropertiesConfig pipSpecificPropertiesConfig,
            EngineTestHooksData testHooks)
        {
            ILayoutConfiguration layout = config.Layout;
            ILoggingConfiguration logging = config.Logging;
            var fingerprintTextElements = new List<(string, string)>();

            using (var hasher = new CoreHashingHelper(recordFingerprintString: true))
            {
                CompositeGraphFingerprint fingerprint = CompositeGraphFingerprint.Zero;

                using (var qualifierHasher = new CoreHashingHelper(recordFingerprintString: false))
                {
                    foreach (string qualifier in startUpConfig.QualifierIdentifiers.OrderBy(q => q))
                    {
                        AddText(qualifierHasher, "qualifier", qualifier);
                    }

                    fingerprint.QualifierHash = qualifierHasher.GenerateHash();
                    AddFingerprint(hasher, "Qualifiers", fingerprint.QualifierHash);
                }

                if (partialEvaluationData != null && partialEvaluationData.CanPerformPartialEvaluation)
                {
                    using (var evaluationFilterHasher = new CoreHashingHelper(recordFingerprintString: false))
                    {
                        AddOrderedTextValues(evaluationFilterHasher, "valueName", partialEvaluationData.ValueNamesToResolveAsStrings);
                        AddOrderedTextValues(evaluationFilterHasher, "valuePath", partialEvaluationData.ValueDefinitionRootsToResolveAsStrings);
                        AddOrderedTextValues(evaluationFilterHasher, "moduleName", partialEvaluationData.ModulesToResolveAsStrings);

                        fingerprint.FilterHash = evaluationFilterHasher.GenerateHash();
                        AddFingerprint(hasher, "Values", fingerprint.FilterHash);
                    }
                }

                using (var topLevelHasher = new CoreHashingHelper(recordFingerprintString: false))
                {
                    AddInt(topLevelHasher, "version", GraphFingerprintVersion);
                    // These paths get embedded in the result of evaluation. So if any change we must re-evaluate
                    AddText(topLevelHasher, "ObjectDirectoryPath", layout.ObjectDirectory.IsValid ? layout.ObjectDirectory.ToString(pathTable) : "::null::");
                    AddText(topLevelHasher, "TempDirectoryPath", layout.TempDirectory.IsValid ? layout.TempDirectory.ToString(pathTable) : "::null::");
                    AddText(topLevelHasher, "SourceDirectoryPath", layout.SourceDirectory.IsValid ? layout.SourceDirectory.ToString(pathTable) : "::null::");

                    // Build engine directory is included because user builds can use SDKs that are shipped with the build engine.
                    // These SDKs mention relative paths pointing to the files (e.g., executables) in those SDKs.
                    // A build break (or unexpected process execution) can occur if the build engine directory is not included in the graph fingerprint.
                    // Consider the following scenario:
                    // - A user has a build engine directory in the path "C:\BuildEngine\net7" and runs a build that reads
                    //   the spec "C:\BuildEngine\net7\Sdk\Sdk.Symbols\Tool.SymbolDaemonTool.dsc". This spec has a relative path `f`bin/SymbolDaemon.exe` to
                    //   the executable "SymbolDaemon.exe" in the SDK.
                    // - The user runs another build with engine in "C:\BuildEngine\net8". This engine has the same commit id as the previous build engine, but has
                    //   a different target framework, i.e., net7 vs. net8.
                    // - If build engine directory is not included in the graph fingeerprint, the second build will get a graph cache hit and will use the graph.
                    //   One of the graph cache key is the path "C:\BuildEngine\net7\Sdk\Sdk.Symbols\Tool.SymbolDaemonTool.dsc" and its content hash. The path still
                    //   exists, and the content hash is the same. Thus, the build will have a graph cache hit.
                    // - Due to graph cache hit, the second build will execute "bin\SymbolDaemon.exe" in "C:\BuildEngine\net7\Sdk\Sdk.Symbols\" because that path is
                    //   in the cached graph. This is unexpected because the user expects to use the net8 version of SymbolDaemon.exe, but the one executed is the net7 one.
                    AddText(topLevelHasher, "BuildEngineDirectory", layout.BuildEngineDirectory.IsValid ? layout.BuildEngineDirectory.ToString(pathTable) : "::null::");

                    // All paths in the graph are relative to 'substTarget' (hence, 'substTarget' must be a part of the fingerprint, but 'substSource' need not be).
                    AddText(topLevelHasher, "substTarget", logging.SubstTarget.IsValid ? logging.SubstTarget.ToString(pathTable) : "::null::");
                    AddText(topLevelHasher, "IsCompressed", config.Engine.CompressGraphFiles ? "true" : "false");
                    AddText(topLevelHasher, "IsSkipHashSourceFile", config.Schedule.SkipHashSourceFile ? "true" : "false");

                    // Pip static fingerprints are not always computed because computing them slows down the graph construction by 10%-13%. 
                    // Thus, the pip graph may and may not contain pip static fingerprints. To avoid unexpected result due to graph cache hit, 
                    // we temporarily add the option for computing pip static fingerprints as part of our graph fingerprint until the fingerprints 
                    // are always computed; see Task 1291638.
                    AddText(topLevelHasher, "ComputePipStaticFingerprints", config.Schedule.ComputePipStaticFingerprints.ToString());

                    AddText(topLevelHasher, "HostOS", startUpConfig.CurrentHost.CurrentOS.ToString());
                    AddText(topLevelHasher, "HostCpuArchitecture", startUpConfig.CurrentHost.CpuArchitecture.ToString());
                    AddText(topLevelHasher, "HostIsElevated", CurrentProcess.IsElevated.ToString());

                    var salt = string.Empty;

                    if (testHooks?.GraphFingerprintSalt != null)
                    {
                        salt += testHooks.GraphFingerprintSalt.Value.ToString();
                    }

                    salt += EngineEnvironmentSettings.DebugGraphFingerprintSalt;

                    if (!string.IsNullOrEmpty(salt))
                    {
                        AddText(topLevelHasher, "GraphFingerprintSalt", salt);
                    }

                    if (config.Schedule.ComputePipStaticFingerprints)
                    {
                        // Pip static fingerprints are part of the graph and include the extra fingerprint salts. 
                        // Thus, when pip static fingerprints are computed, any change to the salt will invalidate the graph because
                        // the pip static fingerprints will no longer be valid. Reusing the graph when the salt changes can result in
                        // underbuild.
                        var extraFingerprintSalts = new ExtraFingerprintSalts(
                            config,
                            config.Cache.CacheSalt ?? string.Empty,
                            new Scheduler.DirectoryMembershipFingerprinterRuleSet(config, pathTable.StringTable).ComputeSearchPathToolsHash(),
                            ObservationReclassifier.ComputeObservationReclassificationRulesHash(config));

                        AddFingerprint(topLevelHasher, "ExtraFingerprintSalts", extraFingerprintSalts.CalculatedSaltsFingerprint);

                        // If the pip has been passed with a specific fingerprint salt, those salt values should be used to see if the graph can be invalidated.
                        var pipSemiStableHashesAndValues = pipSpecificPropertiesConfig?.RetrievePipSemistableHashesWithValues(PipSpecificPropertiesConfig.PipSpecificProperty.PipFingerprintSalt);
                        if (pipSemiStableHashesAndValues?.Any() == true)
                        {
                            var pipSpecificComputedSalt = string.Join(";", pipSemiStableHashesAndValues.OrderBy(pipSemiStableHashesAndValue => pipSemiStableHashesAndValue.Key)
                                                                                                      .Select(pipSemiStableHashesAndValue => $"{pipSemiStableHashesAndValue.Key}={pipSemiStableHashesAndValue.Value}"));
                            AddText(topLevelHasher, "PipSpecificComputedSalt", pipSpecificComputedSalt);
                        }
                    }

                    fingerprint.TopLevelHash = topLevelHasher.GenerateHash();
                    AddFingerprint(hasher, "TopLevelHash", fingerprint.TopLevelHash);
                }

                // Config files
                // Caution: Including the content hash of the config file is how changes to the default pip filter
                // invalidate a cached graph. If the config file content hash is removed, the values that get
                // evaluated because of it must be reflected in the values passed in to this method.
                using (var configHasher = new CoreHashingHelper(recordFingerprintString: false))
                {
                    var configFiles = new List<AbsolutePath> { startUpConfig.ConfigFile };
                    if (startUpConfig.AdditionalConfigFiles != null)
                    {
                        configFiles.AddRange(startUpConfig.AdditionalConfigFiles);
                    }

                    try
                    {
                        foreach (var configPath in configFiles
                            .Select(path => path.ToString(pathTable))
                            .OrderBy(c => c, OperatingSystemHelper.PathComparer))
                        {
                            AddContentHash(
                                configHasher,
                                configPath.ToCanonicalizedPath(),
                                fileContentTable.GetAndRecordContentHashAsync(configPath)
                                    .GetAwaiter()
                                    .GetResult()
                                    .VersionedFileIdentityAndContentInfo.FileContentInfo.Hash);
                        }

                        fingerprint.ConfigFileHash = configHasher.GenerateHash();
                        AddFingerprint(hasher, "ConfigFiles", fingerprint.ConfigFileHash);
                    }
                    catch (BuildXLException ex)
                    {
                        return LogAndReturnFailure(loggingContext, ex);
                    }
                }

                if (!string.IsNullOrEmpty(commitId))
                {
                    using (var commitHasher = new CoreHashingHelper(recordFingerprintString: false))
                    {
                        commitHasher.Add("Commit", commitId);
                        fingerprint.BuildEngineHash = commitHasher.GenerateHash();
                    }

                    AddFingerprint(hasher, "BuildEngine", fingerprint.BuildEngineHash);
                }
                else
                {
                    // BuildXL assemblies. This will invalidate the cached graph if build files change
                    // or if the serialization format changes.
                    try
                    {
                        Action<string, ContentHash> handleBuildFileAndHash = (buildFile, buildFileHash) =>
                        {
                            // Directly add to fingerprint elements for logging, but the hash is represented in the build engine hash
                            fingerprintTextElements.Add((buildFile, buildFileHash.ToString()));
                        };

                        var deployment = testHooks?.AppDeployment ?? AppDeployment.ReadDeploymentManifestFromRunningApp();
                        fingerprint.BuildEngineHash = deployment.ComputeContentHashBasedFingerprint(fileContentTable, handleBuildFileAndHash);
                        AddFingerprint(hasher, "BuildEngine", fingerprint.BuildEngineHash);
                    }
                    catch (BuildXLException ex)
                    {
                        Tracing.Logger.Log.FailedToComputeHashFromDeploymentManifest(loggingContext);
                        Tracing.Logger.Log.FailedToComputeHashFromDeploymentManifestReason(loggingContext, ex.Message);
                        return default(Optional<CompositeGraphFingerprint>);
                    }
                }

                fingerprint.OverallFingerprint = new ContentFingerprint(hasher.GenerateHash());
                Tracing.Logger.Log.ElementsOfConfigurationFingerprint(
                    loggingContext,
                    fingerprint.OverallFingerprint.ToString(),
                    string.Join(Environment.NewLine, fingerprintTextElements.Select(kvp => "\t" + kvp.Item1 + " : " + kvp.Item2)));

                return new Optional<CompositeGraphFingerprint>(fingerprint);
            }

            // Local functions
            void AddInt(CoreHashingHelper hasher, string key, int value)
            {
                hasher.Add(key, value);
                fingerprintTextElements.Add(
                   (key, value.ToString(CultureInfo.InvariantCulture)));
            }

            void AddOrderedTextValues(CoreHashingHelper hasher, string key, IReadOnlyList<string> values)
            {
                // Limit the number of printed values to 50 to make logging more manageable. NOTE:
                // values still go into fingerprint even if they are not printed.
                const int maxValuesToPrint = 50;
                var unprintedValueCount = values.Count - maxValuesToPrint;

                int i = 0;
                foreach (var value in values.OrderBy(s => s))
                {
                    hasher.Add(key, value);

                    if (i < maxValuesToPrint)
                    {
                        fingerprintTextElements.Add((key, value));
                    }
                    else if (i == maxValuesToPrint)
                    {
                        fingerprintTextElements.Add((key, $"[+{unprintedValueCount} more]"));
                    }

                    i++;
                }
            }

            void AddText(CoreHashingHelper hasher, string key, string value)
            {
                hasher.Add(key, value);
                fingerprintTextElements.Add((key, value));
            }

            void AddFingerprint(CoreHashingHelper hasher, string key, Fingerprint value)
            {
                hasher.Add(key, value);
                fingerprintTextElements.Add((key, value.ToString()));
            }

            void AddContentHash(CoreHashingHelper hasher, string key, ContentHash value)
            {
                hasher.Add(key, value);
                fingerprintTextElements.Add((key, value.ToString()));
            }
        }

        private static Optional<CompositeGraphFingerprint> LogAndReturnFailure(LoggingContext loggingContext, BuildXLException ex)
        {
            Tracing.Logger.Log.FailedToComputeGraphFingerprint(loggingContext, ex.LogEventMessage);
            return Optional<CompositeGraphFingerprint>.Empty;
        }
    }
}
