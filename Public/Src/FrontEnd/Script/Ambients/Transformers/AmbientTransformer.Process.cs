// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using BuildXL.FrontEnd.Script.Evaluator;
using BuildXL.FrontEnd.Script.Types;
using BuildXL.FrontEnd.Script.Values;
using BuildXL.Ipc.Common;
using BuildXL.Pips;
using BuildXL.Pips.Builders;
using BuildXL.Pips.Operations;
using BuildXL.Utilities;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using static BuildXL.Utilities.Core.FormattableStringEx;
using BuildXL.Utilities.Configuration.Mutable;

namespace BuildXL.FrontEnd.Script.Ambients.Transformers
{
    /// <summary>
    /// Ambient definition for namespace Transformer.
    /// </summary>
    public partial class AmbientTransformerBase : AmbientDefinitionBase
    {
        private static readonly Dictionary<string, FileExistence> s_fileExistenceKindMap = new Dictionary<string, FileExistence>(StringComparer.Ordinal)
        {
            ["required"] = FileExistence.Required,
            ["optional"] = FileExistence.Optional,
            ["temporary"] = FileExistence.Temporary,
        };

        private static readonly ISet<string> s_outputDirectoryKind = new HashSet<string>(StringComparer.Ordinal)
        {
            "shared",
            "exclusive",
        };

        // Keep in sync with Transformer.Execute.dsc DoubleWritePolicy definition
        private static readonly Dictionary<string, RewritePolicy> s_doubleWritePolicyMap = new Dictionary<string, RewritePolicy>(StringComparer.Ordinal)
        {
            ["doubleWritesAreErrors"] = RewritePolicy.DoubleWritesAreErrors,
            ["allowSameContentDoubleWrites"] = RewritePolicy.AllowSameContentDoubleWrites,
            ["unsafeFirstDoubleWriteWins"] = RewritePolicy.UnsafeFirstDoubleWriteWins,
        };

        // Keep in sync with Transformer.Execute.dsc DoubleWritePolicy definition
        private static readonly Dictionary<string, RewritePolicy> s_fileRewritePolicyMap = new Dictionary<string, RewritePolicy>(StringComparer.Ordinal)
        {
            ["sourceRewritesAreErrors"] = RewritePolicy.SourceRewritesAreErrors,
            ["safeSourceRewritesAreAllowed"] = RewritePolicy.SafeSourceRewritesAreAllowed,
        };

        private static readonly Dictionary<string, bool> s_privilegeLevel = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["standard"] = false,
            ["admin"]    = true,
        };

        // these values must be kept in sync with the ones defined on the BuildXL Script side
        private static readonly Dictionary<string, Process.AbsentPathProbeInUndeclaredOpaquesMode> s_absentPathProbeModes = new Dictionary<string, Process.AbsentPathProbeInUndeclaredOpaquesMode>(StringComparer.Ordinal)
        {
            ["unsafe"] = Process.AbsentPathProbeInUndeclaredOpaquesMode.Unsafe,
            ["strict"] = Process.AbsentPathProbeInUndeclaredOpaquesMode.Strict,
            ["relaxed"] = Process.AbsentPathProbeInUndeclaredOpaquesMode.Relaxed,
        };

        internal const string ExecuteFunctionName = "execute";
        internal const string CreateServiceFunctionName = "createService";

        private SymbolAtom m_executeTool;
        private SymbolAtom m_executeArguments;
        private SymbolAtom m_executeWorkingDirectory;
        private SymbolAtom m_executeDependencies;
        private SymbolAtom m_executeImplicitOutputs;
        private SymbolAtom m_executeOptionalImplicitOutputs;
        private SymbolAtom m_executeOutputs;
        private SymbolAtom m_executeDirectoryOutputKind;
        private SymbolAtom m_executeDirectoryOutputDirectory;
        private SymbolAtom m_executeFileOrPathOutputExistence;
        private SymbolAtom m_executeFileOrPathOutputArtifact;
        private SymbolAtom m_executeConsoleInput;
        private SymbolAtom m_executeConsoleOutput;
        private SymbolAtom m_executeConsoleError;
        private SymbolAtom m_executeTraceFile;
        private SymbolAtom m_executeEnvironmentVariables;
        private SymbolAtom m_executeAcquireSemaphores;
        private SymbolAtom m_executeReclassificationRules;
        private SymbolAtom m_executeAcquireMutexes;
        private SymbolAtom m_executeSuccessExitCodes;
        private SymbolAtom m_executeRetryExitCodes;
        private SymbolAtom m_retryAttemptEnvironmentVariable;
        private SymbolAtom m_executeTempDirectory;
        private SymbolAtom m_executeUnsafe;
        private SymbolAtom m_executeIsLight;
        private SymbolAtom m_executeDoubleWritePolicy;
        private SymbolAtom m_executeSourceRewritePolicy;
        private SymbolAtom m_executeAllowUndeclaredSourceReads;
        private SymbolAtom m_preservePathSetCasing;
        private SymbolAtom m_enforceWeakFingerprintAugmentation;
        private SymbolAtom m_processRetries;
        private SymbolAtom m_executeKeepOutputsWritable;
        private SymbolAtom m_succeedFastExitCodes;
        private SymbolAtom m_privilegeLevel;
        private SymbolAtom m_disableCacheLookup;
        private SymbolAtom m_uncancellable;
        private SymbolAtom m_outputDirectoryExclusions;
        private SymbolAtom m_writingToStandardErrorFailsExecution;
        private SymbolAtom m_executeWarningRegex;
        private SymbolAtom m_executeErrorRegex;
        private SymbolAtom m_executeEnableMultiLineErrorScanning;
        private SymbolAtom m_executeTags;
        private SymbolAtom m_executeServiceShutdownCmd;
        private SymbolAtom m_executeServiceFinalizationCmds;
        private SymbolAtom m_executeServicePipDependencies;
        private SymbolAtom m_executeServiceTrackableTag;
        private SymbolAtom m_executeServiceTrackableTagDisplayName;
        private SymbolAtom m_executeServiceMoniker;
        private SymbolAtom m_executeDescription;
        private SymbolAtom m_executeAbsentPathProbeInUndeclaredOpaqueMode;
        private SymbolAtom m_executeAdditionalTempDirectories;
        private SymbolAtom m_executeAllowedSurvivingChildProcessNames;
        private SymbolAtom m_executeNestedProcessTerminationTimeoutMs;
        private SymbolAtom m_executeDependsOnCurrentHostOSDirectories;
        private SymbolAtom m_toolTimeoutInMilliseconds;
        private SymbolAtom m_toolWarningTimeoutInMilliseconds;
        private SymbolAtom m_argN;
        private SymbolAtom m_argV;
        private SymbolAtom m_argValues;
        private SymbolAtom m_argArgs;
        private StringId m_argResponseFileForRemainingArgumentsForce;
        private SymbolAtom m_envName;
        private SymbolAtom m_envValue;
        private SymbolAtom m_envSeparator;
        private SymbolAtom m_priority;
        private SymbolAtom m_toolExe;
        private SymbolAtom m_toolNestedTools;
        private SymbolAtom m_toolRuntimeDependencies;
        private SymbolAtom m_toolRuntimeDirectoryDependencies;
        private SymbolAtom m_toolRuntimeEnvironment;
        private SymbolAtom m_toolUntrackedDirectories;
        private SymbolAtom m_toolUntrackedDirectoryScopes;
        private SymbolAtom m_toolUntrackedFiles;
        private SymbolAtom m_toolDependsOnWindowsDirectories;
        private SymbolAtom m_toolDependsOnCurrentHostOSDirectories;
        private SymbolAtom m_toolDependsOnAppDataDirectory;
        private SymbolAtom m_toolPrepareTempDirectory;
        private SymbolAtom m_toolDescription;
        private SymbolAtom m_weight;
        private SymbolAtom m_changeAffectedInputListWrittenFile;

        private SymbolAtom m_runtimeEnvironmentClrOverride;
        private SymbolAtom m_clrConfigInstallRoot;
        private SymbolAtom m_clrConfigVersion;
        private SymbolAtom m_clrConfigGuiFromShim;
        private SymbolAtom m_clrConfigDbgJitDebugLaunchSetting;
        private SymbolAtom m_clrConfigOnlyUseLatestClr;
        private SymbolAtom m_clrConfigDefaultVersion;
        private StringId m_clrConfigComplusInstallRoot;
        private StringId m_clrConfigComplusVersion;
        private StringId m_clrConfigComplusNoGuiFromShim;
        private StringId m_clrConfigComplusDefaultVersion;
        private StringId m_clrConfigComplusDbgJitDebugLaunchSetting;
        private StringId m_clrConfigComplusOnlyUseLatestClr;
        private SymbolAtom m_unsafeUntrackedPaths;
        private SymbolAtom m_unsafeUntrackedScopes;
        private SymbolAtom m_unsafeHasUntrackedChildProcesses;
        private SymbolAtom m_unsafeAllowPreservedOutputs;
        private SymbolAtom m_unsafePassThroughEnvironmentVariables;
        private SymbolAtom m_unsafePreserveOutputAllowlist;
        private SymbolAtom m_unsafeIncrementalTool;
        private SymbolAtom m_unsafeRequireGlobalDependencies;
        private SymbolAtom m_unsafeChildProcessesToBreakawayFromSandbox;
        private SymbolAtom m_unsafeTrustStaticallyDeclaredAccesses;
        private SymbolAtom m_unsafeDisableFullReparsePointResolving;
        private SymbolAtom m_unsafeDisableSandboxing;
        private SymbolAtom m_semaphoreInfoLimit;
        private SymbolAtom m_semaphoreInfoName;
        private SymbolAtom m_semaphoreInfoIncrementBy;
        private SymbolAtom m_reclassificationRuleName;
        private SymbolAtom m_reclassificationRulePathRegex;
        private SymbolAtom m_reclassificationRuleResolvedObservationTypes;
        private SymbolAtom m_reclassificationRuleReclassifyTo;
        // Explicitly not exposed in DScript since bypassing the salts is not officially supported, and only used for internally scheduled pips
        private SymbolAtom m_unsafeBypassFingerprintSalt;

        private CallSignature ExecuteSignature => CreateSignature(
            required: RequiredParameters(AmbientTypes.ExecuteArgumentsType),
            returnType: AmbientTypes.ExecuteResultType);

        private CallSignature CreateServiceSignature => CreateSignature(
            required: RequiredParameters(AmbientTypes.CreateServiceArgumentsType),
            returnType: AmbientTypes.CreateServiceResultType);

        private EvaluationResult Execute(Context context, ModuleLiteral env, EvaluationStackFrame args)
        {
            return ScheduleProcessPip(context, env, args, ServicePipKind.None);
        }

        private EvaluationResult CreateService(Context context, ModuleLiteral env, EvaluationStackFrame args)
        {
            return ScheduleProcessPip(context, env, args, ServicePipKind.Service);
        }

        private EvaluationResult ScheduleProcessPip(Context context, ModuleLiteral env, EvaluationStackFrame args, ServicePipKind serviceKind)
        {
            var obj = Args.AsObjectLiteral(args, 0);

            if (!TryScheduleProcessPip(context, obj, serviceKind, out var processOutputs, out _))
            {
                // Error has been logged
                return EvaluationResult.Error;
            }

            return EvaluationResult.Create(BuildExecuteOutputs(context, env, processOutputs, serviceKind != ServicePipKind.None));
        }

        private void InitializeProcessNames()
        {
            // Execute.
            m_executeTool = Symbol("tool");
            m_executeArguments = Symbol("arguments");
            m_executeWorkingDirectory = Symbol("workingDirectory");
            m_executeDependencies = Symbol("dependencies");
            m_executeImplicitOutputs = Symbol("implicitOutputs");
            m_executeOptionalImplicitOutputs = Symbol("optionalImplicitOutputs");
            m_executeOutputs = Symbol("outputs");
            m_executeDirectoryOutputKind = Symbol("kind");
            m_executeDirectoryOutputDirectory = Symbol("directory");
            m_executeFileOrPathOutputExistence = Symbol("existence");
            m_executeFileOrPathOutputArtifact = Symbol("artifact");
            m_executeConsoleInput = Symbol("consoleInput");
            m_executeConsoleOutput = Symbol("consoleOutput");
            m_executeConsoleError = Symbol("consoleError");
            m_executeTraceFile = Symbol("fileAccessTraceFile");
            m_executeEnvironmentVariables = Symbol("environmentVariables");
            m_executeAcquireSemaphores = Symbol("acquireSemaphores");
            m_executeReclassificationRules = Symbol("reclassificationRules");
            m_executeAcquireMutexes = Symbol("acquireMutexes");
            m_executeSuccessExitCodes = Symbol("successExitCodes");
            m_executeRetryExitCodes = Symbol("retryExitCodes");
            m_retryAttemptEnvironmentVariable = Symbol("retryAttemptEnvironmentVariable");
            m_executeTempDirectory = Symbol("tempDirectory");
            m_executeUnsafe = Symbol("unsafe");
            m_executeIsLight = Symbol("isLight");
            m_executeDoubleWritePolicy = Symbol("doubleWritePolicy");
            m_executeSourceRewritePolicy = Symbol("sourceRewritePolicy");
            m_executeAllowUndeclaredSourceReads = Symbol("allowUndeclaredSourceReads");
            m_preservePathSetCasing = Symbol("preservePathSetCasing");
            m_enforceWeakFingerprintAugmentation = Symbol("enforceWeakFingerprintAugmentation");
            m_processRetries = Symbol("processRetries");
            m_executeAbsentPathProbeInUndeclaredOpaqueMode = Symbol("absentPathProbeInUndeclaredOpaquesMode");

            m_executeKeepOutputsWritable = Symbol("keepOutputsWritable");
            m_succeedFastExitCodes = Symbol("succeedFastExitCodes");
            m_privilegeLevel = Symbol("privilegeLevel");
            m_disableCacheLookup = Symbol("disableCacheLookup");
            m_uncancellable = Symbol("uncancellable");
            m_outputDirectoryExclusions = Symbol("outputDirectoryExclusions");
            m_writingToStandardErrorFailsExecution = Symbol("writingToStandardErrorFailsExecution");
            m_executeTags = Symbol("tags");
            m_executeServiceShutdownCmd = Symbol("serviceShutdownCmd");
            m_executeServiceFinalizationCmds = Symbol("serviceFinalizationCmds");
            m_executeServicePipDependencies = Symbol("servicePipDependencies");
            m_executeServiceTrackableTag = Symbol("serviceTrackableTag");
            m_executeServiceTrackableTagDisplayName = Symbol("serviceTrackableTagDisplayName");
            m_executeServiceMoniker = Symbol("moniker");
            m_executeDescription = Symbol("description");
            m_executeAdditionalTempDirectories = Symbol("additionalTempDirectories");
            m_executeWarningRegex = Symbol("warningRegex");
            m_executeErrorRegex = Symbol("errorRegex");
            m_executeEnableMultiLineErrorScanning = Symbol("enableMultiLineErrorScanning");
            m_executeAllowedSurvivingChildProcessNames = Symbol("allowedSurvivingChildProcessNames");
            m_executeNestedProcessTerminationTimeoutMs = Symbol("nestedProcessTerminationTimeoutMs");
            m_executeDependsOnCurrentHostOSDirectories = Symbol("dependsOnCurrentHostOSDirectories");
            m_weight = Symbol("weight");
            m_priority = Symbol("priority");
            m_changeAffectedInputListWrittenFile = Symbol("changeAffectedInputListWrittenFile");

            m_argN = Symbol("n");
            m_argV = Symbol("v");
            m_argValues = Symbol("values");
            m_argArgs = Symbol("args");
            m_argResponseFileForRemainingArgumentsForce = StringId.Create(StringTable, "__force");

            // Environment variable.
            m_envName = Symbol("name");
            m_envValue = Symbol("value");
            m_envSeparator = Symbol("separator");

            // Tool.
            m_toolExe = Symbol("exe");
            m_toolNestedTools = Symbol("nestedTools");
            m_toolRuntimeDependencies = Symbol("runtimeDependencies");
            m_toolRuntimeDirectoryDependencies = Symbol("runtimeDirectoryDependencies");
            m_toolRuntimeEnvironment = Symbol("runtimeEnvironment");
            m_toolUntrackedDirectories = Symbol("untrackedDirectories");
            m_toolUntrackedDirectoryScopes = Symbol("untrackedDirectoryScopes");
            m_toolUntrackedFiles = Symbol("untrackedFiles");
            m_toolDependsOnWindowsDirectories = Symbol("dependsOnWindowsDirectories");
            m_toolDependsOnAppDataDirectory = Symbol("dependsOnAppDataDirectory");
            m_toolDependsOnCurrentHostOSDirectories = Symbol("dependsOnCurrentHostOSDirectories");
            m_toolPrepareTempDirectory = Symbol("prepareTempDirectory");
            m_toolTimeoutInMilliseconds = Symbol("timeoutInMilliseconds");
            m_toolWarningTimeoutInMilliseconds = Symbol("warningTimeoutInMilliseconds");
            m_toolDescription = Symbol("description");

            m_runtimeEnvironmentClrOverride = Symbol("clrOverride");

            // m_clrConfig
            m_clrConfigInstallRoot = Symbol("installRoot");
            m_clrConfigVersion = Symbol("version");
            m_clrConfigGuiFromShim = Symbol("guiFromShim");
            m_clrConfigDbgJitDebugLaunchSetting = Symbol("dbgJitDebugLaunchSetting");
            m_clrConfigDefaultVersion = Symbol("defaultVersion");
            m_clrConfigOnlyUseLatestClr = Symbol("onlyUseLatestClr");
            m_clrConfigComplusInstallRoot = StringId.Create(StringTable, "COMPLUS_InstallRoot");
            m_clrConfigComplusVersion = StringId.Create(StringTable, "COMPLUS_Version");
            m_clrConfigComplusNoGuiFromShim = StringId.Create(StringTable, "COMPLUS_NoGuiFromShim");
            m_clrConfigComplusDbgJitDebugLaunchSetting = StringId.Create(StringTable, "COMPLUS_DbgJitDebugLaunchSetting");
            m_clrConfigComplusDefaultVersion = StringId.Create(StringTable, "COMPLUS_DefaultVersion");
            m_clrConfigComplusOnlyUseLatestClr = StringId.Create(StringTable, "COMPLUS_OnlyUseLatestClr");

            // Unsafe.
            m_unsafeUntrackedPaths = Symbol("untrackedPaths");
            m_unsafeUntrackedScopes = Symbol("untrackedScopes");
            m_unsafeHasUntrackedChildProcesses = Symbol("hasUntrackedChildProcesses");
            m_unsafeAllowPreservedOutputs = Symbol("allowPreservedOutputs");
            m_unsafePassThroughEnvironmentVariables = Symbol("passThroughEnvironmentVariables");
            m_unsafePreserveOutputAllowlist = Symbol("preserveOutputWhitelist"); // compatibility
            m_unsafePreserveOutputAllowlist = Symbol("preserveOutputAllowlist");
            m_unsafeIncrementalTool = Symbol("incrementalTool");
            m_unsafeRequireGlobalDependencies = Symbol("requireGlobalDependencies");
            m_unsafeChildProcessesToBreakawayFromSandbox = Symbol("childProcessesToBreakawayFromSandbox");
            m_unsafeTrustStaticallyDeclaredAccesses = Symbol("trustStaticallyDeclaredAccesses");
            m_unsafeDisableFullReparsePointResolving = Symbol("disableFullReparsePointResolving");
            m_unsafeDisableSandboxing = Symbol("disableSandboxing");
            m_unsafeBypassFingerprintSalt = Symbol("bypassFingerprintSalt");

            // Semaphore info.
            m_semaphoreInfoLimit = Symbol("limit");
            m_semaphoreInfoName = Symbol("name");
            m_semaphoreInfoIncrementBy = Symbol("incrementBy");

            // Reclassification rules
            m_reclassificationRuleName = Symbol("name");
            m_reclassificationRulePathRegex = Symbol("pathRegex");
            m_reclassificationRuleResolvedObservationTypes = Symbol("resolvedObservationTypes");
            m_reclassificationRuleReclassifyTo = Symbol("reclassifyTo");
        }

        private bool TryScheduleProcessPip(Context context, ObjectLiteral obj, ServicePipKind serviceKind, out ProcessOutputs processOutputs, out Process pip)
        {
            using (var processBuilder = ProcessBuilder.Create(
                context.PathTable,
                context.FrontEndContext.GetPipDataBuilder(),
                context.FrontEndContext.CredentialScanner,
                context.FrontEndContext.LoggingContext))
            {
                ProcessExecuteArguments(context, obj, processBuilder, serviceKind);

                if (!context.GetPipConstructionHelper().TryAddProcess(processBuilder, out processOutputs, out pip))
                {
                    // Error has been logged
                    return false;
                }

                return true;
            }
        }

        private void ProcessExecuteArguments(Context context, ObjectLiteral obj, ProcessBuilder processBuilder, ServicePipKind serviceKind)
        {
            // Tool.
            var tool = Converter.ExtractObjectLiteral(obj, m_executeTool);
            ProcessTool(context, tool, processBuilder);

            // Description.
            var description = Converter.ExtractString(obj, m_executeDescription, allowUndefined: true);
            processBuilder.Usage = string.IsNullOrEmpty(description)
                ? PipData.Invalid
                : PipDataBuilder.CreatePipData(context.StringTable, string.Empty, PipDataFragmentEscaping.NoEscaping, description);

            // Arguments.
            var arguments = Converter.ExtractArrayLiteral(obj, m_executeArguments);
            TransformerExecuteArgumentsProcessor.ProcessArguments(context, processBuilder, arguments);

            // Working directory.
            processBuilder.WorkingDirectory = Converter.ExtractDirectory(obj, m_executeWorkingDirectory);

            // Dependencies.
            var dependencies = Converter.ExtractArrayLiteral(obj, m_executeDependencies, allowUndefined: true);
            if (dependencies != null)
            {
                for (var i = 0; i < dependencies.Length; i++)
                {
                    var value = dependencies[i];
                    if (!value.IsUndefined)
                    {
                        var oneInputFileConvContext = new ConversionContext(pos: i, objectCtx: dependencies);
                        ProcessImplicitDependency(processBuilder, value, convContext: oneInputFileConvContext);
                    }
                }
            }

            // TODO: remove in favor of 'outputs' when the corresponding obsolete field is removed
            // Implicit outputs.
            var implicitOutputs = Converter.ExtractArrayLiteral(obj, m_executeImplicitOutputs, allowUndefined: true);
            if (implicitOutputs != null)
            {
                ProcessImplicitOutputs(processBuilder, implicitOutputs, FileExistence.Required);
            }

            // TODO: remove in favor of 'outputs' when the corresponding obsolete field is removed
            // Optional implicit outputs.
            var optionalImplicitOutputs = Converter.ExtractArrayLiteral(obj, m_executeOptionalImplicitOutputs, allowUndefined: true);
            if (optionalImplicitOutputs != null)
            {
                ProcessImplicitOutputs(processBuilder, optionalImplicitOutputs, FileExistence.Temporary);
            }

            // Tool outputs
            var outputs = Converter.ExtractArrayLiteral(obj, m_executeOutputs, allowUndefined: true);
            if (outputs != null)
            {
                ProcessOutputs(context, processBuilder, outputs);
            }

            // Console input.
            var consoleInput = obj[m_executeConsoleInput];
            if (!consoleInput.IsUndefined)
            {
                if (consoleInput.Value is FileArtifact stdInFile)
                {
                    processBuilder.StandardInput = StandardInput.CreateFromFile(stdInFile);
                    processBuilder.AddInputFile(stdInFile);
                }
                else
                {
                    var pipData = ProcessData(context, consoleInput, new ConversionContext(name: m_executeConsoleInput, allowUndefined: false, objectCtx: obj));
                    processBuilder.StandardInput = StandardInput.CreateFromData(pipData);
                }
            }

            // Console output.
            var consoleOutput = Converter.ExtractPath(obj, m_executeConsoleOutput, allowUndefined: true);
            if (consoleOutput.IsValid)
            {
                processBuilder.SetStandardOutputFile(consoleOutput);
            }

            // Console error
            var consoleErrorOutput = Converter.ExtractPath(obj, m_executeConsoleError, allowUndefined: true);
            if (consoleErrorOutput.IsValid)
            {
                processBuilder.SetStandardErrorFile(consoleErrorOutput);
            }

            // Trace file
            var traceFileOutput = Converter.ExtractPath(obj, m_executeTraceFile, allowUndefined: true);
            if (traceFileOutput.IsValid)
            {
                processBuilder.SetTraceFile(traceFileOutput);
            }

            var changeAffectedInputListWrittenFile = Converter.ExtractPath(obj, m_changeAffectedInputListWrittenFile, allowUndefined: true);
            if (changeAffectedInputListWrittenFile.IsValid)
            {
                processBuilder.SetChangeAffectedInputListWrittenFile(changeAffectedInputListWrittenFile);
            }

            // Environment variables.
            var environmentVariables = Converter.ExtractArrayLiteral(obj, m_executeEnvironmentVariables, allowUndefined: true);
            if (environmentVariables != null)
            {
                using (var pipDataBuilderWrapper = context.FrontEndContext.GetPipDataBuilder())
                {
                    var pipDataBuilder = pipDataBuilderWrapper.Instance;

                    for (var i = 0; i < environmentVariables.Length; i++)
                    {
                        var environmentVariable = Converter.ExpectObjectLiteral(
                            environmentVariables[i],
                            new ConversionContext(pos: i, objectCtx: environmentVariables));
                        ProcessEnvironmentVariable(context, processBuilder, pipDataBuilder, environmentVariable, isPassThrough: false);
                        pipDataBuilder.Clear();
                    }
                }
            }

            // TODO: Regex. Should we follow ECMA, C#, JavaScript?

            // Weight.
            var weight = Converter.ExtractOptionalInt(obj, m_weight);
            if (weight != null)
            {
                processBuilder.Weight = weight.Value;
            }

            // Priority.
            var priority = Converter.ExtractOptionalInt(obj, m_priority);
            if (priority != null)
            {
                processBuilder.Priority = priority.Value;
            }

            // Acquired semaphores.
            var acquireSemaphores = Converter.ExtractArrayLiteral(obj, m_executeAcquireSemaphores, allowUndefined: true);
            if (acquireSemaphores != null)
            {
                ProcessAcquireSemaphores(context, processBuilder, acquireSemaphores);
            }

            // Acquired mutexes.
            var acquireMutexes = Converter.ExtractArrayLiteral(obj, m_executeAcquireMutexes, allowUndefined: true);
            if (acquireMutexes != null)
            {
                ProcessAcquireMutexes(processBuilder, acquireMutexes);
            }

            // Exit Codes
            // If a process exits with one of these codes, skip downstream pips but treat it as a success.
            processBuilder.SucceedFastExitCodes = ProcessOptionalIntArray(obj, m_succeedFastExitCodes);
            processBuilder.SuccessExitCodes = ReadOnlyArray<int>.FromWithoutCopy(ProcessOptionalIntArray(obj, m_executeSuccessExitCodes).Concat(processBuilder.SucceedFastExitCodes.ToArray()).ToArray());
            processBuilder.RetryExitCodes = ProcessOptionalIntArray(obj, m_executeRetryExitCodes);

            // Retry attempt environment variable.
            string retryAttemptEnvVar = Converter.ExtractString(obj, m_retryAttemptEnvironmentVariable, allowUndefined: true);
            if (!string.IsNullOrWhiteSpace(retryAttemptEnvVar))
            {
                processBuilder.SetRetryAttemptEnvironmentVariable(StringId.Create(StringTable, retryAttemptEnvVar));
            }

            // Temporary directory.
            var tempDirectory = Converter.ExtractDirectory(obj, m_executeTempDirectory, allowUndefined: true);
            if (tempDirectory.IsValid)
            {
                processBuilder.SetTempDirectory(tempDirectory);
            }
            processBuilder.AdditionalTempDirectories = ProcessOptionalPathArray(obj, m_executeAdditionalTempDirectories, strict: false, skipUndefined: true);


            // Set the default value before processing unsafeOption in case unsafeOption doesn't exist.
            processBuilder.Options |= Process.Options.RequireGlobalDependencies;
            // Unsafe options.
            var unsafeOptions = Converter.ExtractObjectLiteral(obj, m_executeUnsafe, allowUndefined: true);
            if (unsafeOptions != null)
            {
                ProcessUnsafeOptions(context, processBuilder, unsafeOptions);
            }

            // Set outputs to remain writable.
            var keepOutputsWritable = Converter.ExtractOptionalBoolean(obj, m_executeKeepOutputsWritable);
            if (keepOutputsWritable == true)
            {
                processBuilder.Options |= Process.Options.OutputsMustRemainWritable;
            }

            // Set outputs to remain writable.
            var privilegeLevel = Converter.ExtractStringLiteral(obj, m_privilegeLevel, s_privilegeLevel.Keys, allowUndefined: true);
            if (privilegeLevel != null && s_privilegeLevel.TryGetValue(privilegeLevel, out bool level) && level)
            {
                processBuilder.Options |= Process.Options.RequiresAdmin;
            }

            var absentPathProbeMode = Converter.ExtractStringLiteral(obj, m_executeAbsentPathProbeInUndeclaredOpaqueMode, s_absentPathProbeModes.Keys, allowUndefined: true);
            if (absentPathProbeMode != null)
            {
                processBuilder.AbsentPathProbeUnderOpaquesMode = s_absentPathProbeModes[absentPathProbeMode];
            }

            // Set custom warning regex.
            var warningRegex = Converter.ExtractString(obj, m_executeWarningRegex, allowUndefined: true);
            if (warningRegex != null)
            {
                processBuilder.WarningRegex = new RegexDescriptor(StringId.Create(context.StringTable, warningRegex), RegexOptions.None);
            }

            var errorRegex = Converter.ExtractString(obj, m_executeErrorRegex, allowUndefined: true);
            if (errorRegex != null)
            {
                processBuilder.ErrorRegex = new RegexDescriptor(StringId.Create(context.StringTable, errorRegex), RegexOptions.None);
            }

            var enableMultiLineErrorScanning = Converter.ExtractOptionalBoolean(obj, m_executeEnableMultiLineErrorScanning);
            if (enableMultiLineErrorScanning != null)
            {
                processBuilder.EnableMultiLineErrorScanning = enableMultiLineErrorScanning.Value;
            }

            // Tags.
            ProcessTags(context, obj, processBuilder);

            // service pip dependencies (only if this pip is not a service)
            processBuilder.ServiceKind = serviceKind;
            if (serviceKind != ServicePipKind.Service)
            {
                var servicePipDependencies = Converter.ExtractArrayLiteral(obj, m_executeServicePipDependencies, allowUndefined: true);
                if (servicePipDependencies != null)
                {
                    for (var i = 0; i < servicePipDependencies.Length; i++)
                    {
                        var value = servicePipDependencies[i];
                        if (!value.IsUndefined)
                        {
                            var servicePip = Converter.ExpectPipId(value, new ConversionContext(pos: i, objectCtx: servicePipDependencies));
                            processBuilder.AddServicePipDependency(servicePip);
                        }
                    }
                }
            }
            else
            {
                var shutdownCommand = Converter.ExtractObjectLiteral(obj, m_executeServiceShutdownCmd, allowUndefined: false);
                TryScheduleProcessPip(context, shutdownCommand, ServicePipKind.ServiceShutdown, out _, out var shutdownProcessPip);

                processBuilder.ShutDownProcessPipId = shutdownProcessPip.PipId;

                var finalizationCommands = Converter.ExtractArrayLiteral(obj, m_executeServiceFinalizationCmds, allowUndefined: true);
                if (finalizationCommands != null)
                {
                    var finalizationPipIds = new PipId[finalizationCommands.Count];
                    for (var i = 0; i < finalizationCommands.Count; i++)
                    {
                        var executePipArgs = Converter.ExpectObjectLiteral(finalizationCommands[i], new ConversionContext(pos: i, objectCtx: finalizationCommands));
                        finalizationPipIds[i] = InterpretFinalizationPipArguments(context, executePipArgs);
                    }

                    processBuilder.FinalizationPipIds = ReadOnlyArray<PipId>.FromWithoutCopy(finalizationPipIds);
                }

                var trackableTagString = Converter.ExtractString(obj, m_executeServiceTrackableTag, allowUndefined: true);
                var trackableTag = string.IsNullOrEmpty(trackableTagString)
                    ? StringId.Invalid
                    : StringId.Create(context.StringTable, trackableTagString);
                var trackableTagDisplayNameString = Converter.ExtractString(obj, m_executeServiceTrackableTagDisplayName, allowUndefined: true);
                var trackableTagDisplayName = string.IsNullOrEmpty(trackableTagDisplayNameString)
                    ? StringId.Invalid
                    : StringId.Create(context.StringTable, trackableTagDisplayNameString);
                processBuilder.SetServiceTrackableTag(trackableTag, trackableTagDisplayName);
                processBuilder.ServiceMoniker = Converter.ExtractValue<IpcMoniker>(obj, m_executeServiceMoniker, allowUndefined: false);
            }

            // Light process flag.
            if (Converter.ExtractOptionalBoolean(obj, m_executeIsLight) == true)
            {
                processBuilder.Options |= Process.Options.IsLight;
            }

            // Double write policy
            // The value is set based on the default but overridden if the field is explicitly defined for the pip
            var doubleWritePolicyString = Converter.ExtractStringLiteral(obj, m_executeDoubleWritePolicy, s_doubleWritePolicyMap.Keys, allowUndefined: true);
            var doubleWritePolicy = doubleWritePolicyString != null ?
                    s_doubleWritePolicyMap[doubleWritePolicyString] :
                    context.FrontEndHost.Configuration.Sandbox.UnsafeSandboxConfiguration.DoubleWritePolicy();

            // Source rewrite write policy
            // The value is set based on the default but overridden if the field is explicitly defined for the pip
            var sourceRewritePolicyString = Converter.ExtractStringLiteral(obj, m_executeSourceRewritePolicy, s_fileRewritePolicyMap.Keys, allowUndefined: true);
            
            var sourceRewritePolicy = sourceRewritePolicyString != null ?
                    s_fileRewritePolicyMap[sourceRewritePolicyString] :
                    context.FrontEndHost.Configuration.Sandbox.UnsafeSandboxConfiguration.SourceWritePolicy();

            processBuilder.RewritePolicy = doubleWritePolicy | sourceRewritePolicy;

            // Allow undeclared source reads flag
            if (Converter.ExtractOptionalBoolean(obj, m_executeAllowUndeclaredSourceReads) == true)
            {
                processBuilder.Options |= Process.Options.AllowUndeclaredSourceReads;
            }

            // Preserve path set casing flag
            if (Converter.ExtractOptionalBoolean(obj, m_preservePathSetCasing) == true)
            {
                processBuilder.Options |= Process.Options.PreservePathSetCasing;
            }

            // Enforce weak fingerprint augmentation.
            if (Converter.ExtractOptionalBoolean(obj, m_enforceWeakFingerprintAugmentation) == true)
            {
                processBuilder.Options |= Process.Options.EnforceWeakFingerprintAugmentation;
            }

            // Process retries
            int? retries = Converter.ExtractOptionalInt(obj, m_processRetries);
            if (retries.HasValue)
            {
                processBuilder.SetProcessRetries(retries.Value);
            }

            // disableCacheLookup flag
            if (Converter.ExtractOptionalBoolean(obj, m_disableCacheLookup) == true)
            {
                processBuilder.Options |= Process.Options.DisableCacheLookup;
            }

            // uncancellable flag
            if (Converter.ExtractOptionalBoolean(obj, m_uncancellable) == true)
            {
                processBuilder.Options |= Process.Options.Uncancellable;
            }

            // outputDirectoryExclusions
            var outputDirectoryExclusions = Converter.ExtractOptionalArrayLiteral(obj, m_outputDirectoryExclusions, allowUndefined: true);
            if (outputDirectoryExclusions != null)
            {
                ProcessOutputDirectoryExclusions(context, processBuilder, outputDirectoryExclusions);
            }

            // writingToStandardErrorFailsExecution flag
            if (Converter.ExtractOptionalBoolean(obj, m_writingToStandardErrorFailsExecution) == true)
            {
                processBuilder.Options |= Process.Options.WritingToStandardErrorFailsExecution;
            }

            // Surviving process settings.
            var allowedSurvivingChildProcessNames = Converter.ExtractArrayLiteral(obj, m_executeAllowedSurvivingChildProcessNames, allowUndefined: true);
            if (allowedSurvivingChildProcessNames != null && allowedSurvivingChildProcessNames.Count > 0)
            {
                var processNameAtoms = new PathAtom[allowedSurvivingChildProcessNames.Count];
                for (var i = 0; i < allowedSurvivingChildProcessNames.Count; i++)
                {
                    processNameAtoms[i] = Converter.ExpectPathAtomFromStringOrPathAtom(
                        context.StringTable,
                        allowedSurvivingChildProcessNames[i],
                        context: new ConversionContext(pos: i, objectCtx: allowedSurvivingChildProcessNames));
                }
                processBuilder.AllowedSurvivingChildProcessNames = ReadOnlyArray<PathAtom>.FromWithoutCopy(processNameAtoms);
            }

            var nestedProcessTerminationTimeoutMs = Converter.ExtractNumber(obj, m_executeNestedProcessTerminationTimeoutMs, allowUndefined: true);
            if (nestedProcessTerminationTimeoutMs != null)
            {
                processBuilder.NestedProcessTerminationTimeout = TimeSpan.FromMilliseconds(nestedProcessTerminationTimeoutMs.Value);
            }

            var executeDependsOnCurrentHostOSDirectories = Converter.ExtractOptionalBoolean(obj, m_executeDependsOnCurrentHostOSDirectories);
            if (executeDependsOnCurrentHostOSDirectories == true)
            {
                processBuilder.AddCurrentHostOSDirectories();
            }
        }

        private void ProcessTags(Context context, ObjectLiteral obj, ProcessBuilder processBuilder)
        {
            QualifierValue currentQualifierValue = context.LastActiveModuleQualifier;
            ObjectLiteral currentQualifier = currentQualifierValue.Qualifier;

            var userDefinedTags = Converter.ExtractArrayLiteral(obj, m_executeTags, allowUndefined: true);

            // Shortcut processing if there are no tags to set
            int userTagCount = userDefinedTags == null ? 0 : userDefinedTags.Count;
            if (userTagCount == 0 && currentQualifierValue.IsEmpty())
            {
                return;
            }

            var tagIds = new StringId[userTagCount + currentQualifier.Count];

            // Add tags for each qualifier entry with shape 'key=value'. This is just to facilitate pip filtering for some scenarios.
            int i = 0;
            foreach(StringId key in currentQualifier.Keys)
            {
                tagIds[i] = StringId.Create(context.StringTable, $"{key.ToString(context.StringTable)}={currentQualifier[key].Value}");
                i++;
            }

            // Add now the user defined tags
            for (int j = 0; j < userTagCount; j++)
            {
                var tag = Converter.ExpectString(userDefinedTags[j], context: new ConversionContext(pos: j, objectCtx: userDefinedTags));
                tagIds[i] = StringId.Create(context.StringTable, tag);
                i++;
            }

            processBuilder.Tags = ReadOnlyArray<StringId>.FromWithoutCopy(tagIds);
        }

        private void ProcessOutputDirectoryExclusions(Context context, ProcessBuilder processBuilder, ArrayLiteral outputDirectoryExclusions)
        {
            Contract.AssertNotNull(outputDirectoryExclusions);
            Contract.Assert(context != null);
            Contract.Assert(processBuilder != null);

            for (var i = 0; i < outputDirectoryExclusions.Length; ++i)
            {
                var outputDirectoryExclusion = outputDirectoryExclusions[i];
                if (outputDirectoryExclusion.IsUndefined)
                {
                    continue;
                }

                var directory = Converter.ExpectDirectory(
                    outputDirectoryExclusion, 
                    new ConversionContext(pos: i, objectCtx: outputDirectoryExclusions));
                
                processBuilder.AddOutputDirectoryExclusion(directory.Path);
            }
        }

        private void ProcessTool(Context context, ObjectLiteral tool, ProcessBuilder processBuilder)
        {
            var cachedTool = context.ContextTree.ToolDefinitionCache.GetOrAdd(
                tool,
                obj =>
                {
                    var cacheEntry = new CachedToolDefinition();
                    ProcessCachedTool(context, tool, cacheEntry);
                    // Timeouts.
                    var timeout = Converter.ExtractOptionalInt(tool, m_toolTimeoutInMilliseconds);
                    if (timeout.HasValue)
                    {
                        cacheEntry.Timeout = TimeSpan.FromMilliseconds(timeout.Value);
                    }
                    var warningTimeout = Converter.ExtractOptionalInt(tool, m_toolWarningTimeoutInMilliseconds);
                    if (warningTimeout.HasValue)
                    {
                        cacheEntry.WarningTimeout = TimeSpan.FromMilliseconds(warningTimeout.Value);
                    }
                    return cacheEntry;
                });

            processBuilder.Timeout = cachedTool.Timeout;
            processBuilder.WarningTimeout = cachedTool.WarningTimeout;

            processBuilder.Executable = cachedTool.Executable;
            processBuilder.ToolDescription = cachedTool.ToolDescription;

            foreach (var inputFile in cachedTool.InputFiles)
            {
                processBuilder.AddInputFile(inputFile);
            }

            foreach (var inputDirectory in cachedTool.InputDirectories)
            {
                processBuilder.AddInputDirectory(inputDirectory);
            }

            foreach (var untrackedFile in cachedTool.UntrackedFiles)
            {
                processBuilder.AddUntrackedFile(untrackedFile);
            }

            foreach (var untrackedDirectory in cachedTool.UntrackedDirectories)
            {
                processBuilder.AddUntrackedDirectoryScope(untrackedDirectory);
            }

            foreach (var untracedDirectoryScope in cachedTool.UntrackedDirectoryScopes)
            {
                processBuilder.AddUntrackedDirectoryScope(untracedDirectoryScope);
            }

            foreach (var kv in cachedTool.EnvironmentVariables)
            {
                processBuilder.SetEnvironmentVariable(kv.Key, kv.Value, isPassThrough: false);
            }

            if (cachedTool.DependsOnCurrentHostOSDirectories)
            {
                processBuilder.AddCurrentHostOSDirectories();
            }

            if (cachedTool.UntrackedWindowsDirectories)
            {
                if (!OperatingSystemHelper.IsUnixOS)
                {
                    processBuilder.AddCurrentHostOSDirectories();
                }
            }

            if (cachedTool.UntrackedAppDataDirectories)
            {
                processBuilder.AddUntrackedAppDataDirectories();
            }

            if (cachedTool.EnableTempDirectory)
            {
                processBuilder.EnableTempDirectory();
            }

        }

        private void ProcessCachedTool(Context context, ObjectLiteral tool, CachedToolDefinition cachedTool)
        {
            // TODO: Handle ToolCache again

            // Do the nested tools recursively first, so the outer most tool wins for settings
            var nestedTools = Converter.ExtractArrayLiteral(tool, m_toolNestedTools, allowUndefined: true);
            if (nestedTools != null)
            {
                for (var i = 0; i < nestedTools.Length; i++)
                {
                    var nestedTool = Converter.ExpectObjectLiteral(nestedTools[i], new ConversionContext(pos: i, objectCtx: nestedTools));
                    ProcessCachedTool(context, nestedTool, cachedTool);
                }
            }

            var executable = Converter.ExpectFile(tool[m_toolExe], strict: true, context: new ConversionContext(allowUndefined: false, name: m_toolExe, objectCtx: tool));
            cachedTool.Executable = executable;
            cachedTool.InputFiles.Add(executable);
            var toolDescription = Converter.ExpectString(tool[m_toolDescription], new ConversionContext(allowUndefined: true, name: m_toolDescription, objectCtx: tool));
            if (!string.IsNullOrEmpty(toolDescription))
            {
                cachedTool.ToolDescription = StringId.Create(context.StringTable, toolDescription);
            }

            ProcessOptionalFileArray(tool, m_toolRuntimeDependencies, file => cachedTool.InputFiles.Add(file), skipUndefined: false);
            ProcessOptionalStaticDirectoryArray(tool, m_toolRuntimeDirectoryDependencies, dir => cachedTool.InputDirectories.Add(dir.Root), skipUndefined: false);

            // TODO: Fix all callers, in the api this is limited to Directory.
            ProcessOptionalDirectoryOrPathArray(tool, m_toolUntrackedDirectoryScopes, dir => cachedTool.UntrackedDirectoryScopes.Add(dir), skipUndefined: false);
            ProcessOptionalDirectoryArray(tool, m_toolUntrackedDirectories, dir => cachedTool.UntrackedDirectories.Add(dir), skipUndefined: false);
            ProcessOptionalFileArray(tool, m_toolUntrackedFiles, file => cachedTool.UntrackedFiles.Add(file.Path), skipUndefined: false);

            var runtimeEnvironment = Converter.ExtractObjectLiteral(tool, m_toolRuntimeEnvironment, allowUndefined: true);
            if (runtimeEnvironment != null)
            {
                var clrOverride = Converter.ExtractObjectLiteral(runtimeEnvironment, m_runtimeEnvironmentClrOverride, allowUndefined: true);
                if (clrOverride != null)
                {
                    var installRoot = Converter.ExtractPathLike(clrOverride, m_clrConfigInstallRoot, allowUndefined: true);
                    if (installRoot.IsValid)
                    {
                        cachedTool.EnvironmentVariables[m_clrConfigComplusInstallRoot] = installRoot;
                    }

                    var version = Converter.ExtractString(clrOverride, m_clrConfigVersion, allowUndefined: true);
                    if (version != null)
                    {
                        cachedTool.EnvironmentVariables[m_clrConfigComplusVersion] = version;
                    }

                    var noGuiFromShim = Converter.ExtractOptionalBoolean(clrOverride, m_clrConfigGuiFromShim);
                    if (noGuiFromShim != null)
                    {
                        cachedTool.EnvironmentVariables[m_clrConfigComplusNoGuiFromShim] = noGuiFromShim.Value ? "1" : "0";
                    }

                    var dbgJitDebugLaunch = Converter.ExtractOptionalBoolean(clrOverride, m_clrConfigDbgJitDebugLaunchSetting);
                    if (dbgJitDebugLaunch != null)
                    {
                        cachedTool.EnvironmentVariables[m_clrConfigComplusDbgJitDebugLaunchSetting] = dbgJitDebugLaunch.Value ? "1" : "0";
                    }

                    var defaultVersion = Converter.ExtractString(clrOverride, m_clrConfigDefaultVersion);
                    if (defaultVersion != null)
                    {
                        cachedTool.EnvironmentVariables[m_clrConfigComplusDefaultVersion] = defaultVersion;
                    }

                    var onlyUseLatestClr = Converter.ExtractOptionalBoolean(clrOverride, m_clrConfigOnlyUseLatestClr);
                    if (onlyUseLatestClr != null)
                    {
                        cachedTool.EnvironmentVariables[m_clrConfigComplusOnlyUseLatestClr] = onlyUseLatestClr.Value ? "1" : "0";
                    }
                }
            }

            if (Converter.ExtractOptionalBoolean(tool, m_toolDependsOnWindowsDirectories) == true)
            {
                cachedTool.UntrackedWindowsDirectories = true;
            }

            if (Converter.ExtractOptionalBoolean(tool, m_toolDependsOnCurrentHostOSDirectories) == true)
            {
                cachedTool.DependsOnCurrentHostOSDirectories = true;
            }

            if (Converter.ExtractOptionalBoolean(tool, m_toolDependsOnAppDataDirectory) == true)
            {
                cachedTool.UntrackedAppDataDirectories = true;
            }

            if (Converter.ExtractOptionalBoolean(tool, m_toolPrepareTempDirectory) == true)
            {
                cachedTool.EnableTempDirectory = true;
            }
        }

        private static void ProcessOptionalFileArray(ObjectLiteral obj, SymbolAtom fieldName, Action<FileArtifact> addItem, bool skipUndefined)
        {
            var array = Converter.ExtractArrayLiteral(obj, fieldName, allowUndefined: true);
            if (array != null)
            {
                for (var i = 0; i < array.Length; i++)
                {
                    if (skipUndefined && array[i].IsUndefined)
                    {
                        continue;
                    }

                    var file = Converter.ExpectFile(array[i], context: new ConversionContext(pos: i, objectCtx: array, name: fieldName));
                    addItem(file);
                }
            }
        }

        private static void ProcessOptionalStaticDirectoryArray(ObjectLiteral obj, SymbolAtom fieldName, Action<StaticDirectory> addItem, bool skipUndefined)
        {
            var array = Converter.ExtractArrayLiteral(obj, fieldName, allowUndefined: true);
            if (array != null)
            {
                for (var i = 0; i < array.Length; i++)
                {
                    if (skipUndefined && array[i].IsUndefined)
                    {
                        continue;
                    }

                    var staticDir = Converter.ExpectStaticDirectory(array[i], context: new ConversionContext(pos: i, objectCtx: array, name: fieldName));
                    addItem(staticDir);
                }
            }
        }

        private static void ProcessOptionalDirectoryArray(ObjectLiteral obj, SymbolAtom fieldName, Action<DirectoryArtifact> addItem, bool skipUndefined)
        {
            var array = Converter.ExtractArrayLiteral(obj, fieldName, allowUndefined: true);
            if (array != null)
            {
                for (var i = 0; i < array.Length; i++)
                {
                    if (skipUndefined && array[i].IsUndefined)
                    {
                        continue;
                    }

                    var directory = Converter.ExpectDirectory(array[i], context: new ConversionContext(pos: i, objectCtx: array, name: fieldName));
                    addItem(directory);
                }
            }
        }

        private static void ProcessOptionalDirectoryOrPathArray(ObjectLiteral obj, SymbolAtom fieldName, Action<DirectoryArtifact> addItem, bool skipUndefined)
        {
            var array = Converter.ExtractArrayLiteral(obj, fieldName, allowUndefined: true);
            if (array != null)
            {
                for (var i = 0; i < array.Length; i++)
                {
                    if (skipUndefined && array[i].IsUndefined)
                    {
                        continue;
                    }

                    Converter.ExpectPathOrDirectory(array[i], out var path, out var directory, new ConversionContext(pos: i, objectCtx: array, name: fieldName));
                    if (directory.IsValid)
                    {
                        addItem(directory);
                    }
                    else if (path.IsValid)
                    {
                        addItem(DirectoryArtifact.CreateWithZeroPartialSealId(path));
                    }
                }
            }
        }

        private static ReadOnlyArray<AbsolutePath> ProcessOptionalPathArray(ObjectLiteral obj, SymbolAtom fieldName, bool strict, bool skipUndefined)
        {
            var array = Converter.ExtractArrayLiteral(obj, fieldName, allowUndefined: true);
            if (array != null && array.Length > 0)
            {
                var items = new AbsolutePath[array.Length];

                for (var i = 0; i < array.Length; i++)
                {
                    if (skipUndefined && array[i].IsUndefined)
                    {
                        continue;
                    }

                    var path = Converter.ExpectPath(array[i], strict: strict, context: new ConversionContext(pos: i, objectCtx: array, name: fieldName));
                    items[i] = path;
                }

                return ReadOnlyArray<AbsolutePath>.FromWithoutCopy(items);
            }

            return ReadOnlyArray<AbsolutePath>.Empty;
        }


        private static ReadOnlyArray<int> ProcessOptionalIntArray(ObjectLiteral obj, SymbolAtom fieldName)
        {
            var array = Converter.ExtractArrayLiteral(obj, fieldName, allowUndefined: true);
            if (array != null && array.Length > 0)
            {
                var items = new int[array.Length];

                for (var i = 0; i < array.Length; i++)
                {
                    var value = Converter.ExpectNumber(array[i], context: new ConversionContext(pos: i, objectCtx: array, name: fieldName));
                    items[i] = value;
                }

                return ReadOnlyArray<int>.FromWithoutCopy(items);
            }

            return ReadOnlyArray<int>.Empty;
        }

        private static ReadOnlyArray<string> ProcessOptionalStringArray(ObjectLiteral obj, SymbolAtom fieldName)
        {
            var array = Converter.ExtractArrayLiteral(obj, fieldName, allowUndefined: true);
            if (array != null && array.Length > 0)
            {
                var items = new string[array.Length];

                for (var i = 0; i < array.Length; i++)
                {
                    var value = Converter.ExpectString(array[i], context: new ConversionContext(pos: i, objectCtx: array, name: fieldName));
                    items[i] = value;
                }

                return ReadOnlyArray<string>.FromWithoutCopy(items);
            }

            return ReadOnlyArray<string>.Empty;
        }

        private static ReadOnlyArray<T> ProcessOptionalValueArray<T>(ObjectLiteral obj, SymbolAtom fieldName, bool skipUndefined) where T : struct
        {
            var array = Converter.ExtractArrayLiteral(obj, fieldName, allowUndefined: true);
            if (array != null && array.Length > 0)
            {
                var items = new T[array.Length];

                for (var i = 0; i < array.Length; i++)
                {
                    if (skipUndefined && array[i].IsUndefined)
                    {
                        continue;
                    }

                    var value = Converter.ExpectValue<T>(array[i], context: new ConversionContext(pos: i, objectCtx: array, name: fieldName));
                    items[i] = value;
                }

                return ReadOnlyArray<T>.FromWithoutCopy(items);
            }

            return ReadOnlyArray<T>.Empty;
        }

        /// <summary>
        /// Process tool outputs. Declared in DScript as:
        /// export type Output = Path | File | Directory | DirectoryOutput | FileOrPathOutput;
        /// </summary>
        private void ProcessOutputs(Context context, ProcessBuilder processBuilder, ArrayLiteral outputs)
        {
            for (var i = 0; i < outputs.Length; ++i)
            {
                var output = outputs[i];
                if (output.IsUndefined)
                {
                    continue;
                }

                Contract.Assert(!output.IsUndefined);
                Contract.Assert(context != null);
                Contract.Assert(processBuilder != null);

                // A file artifact (path or file) is interpreted as a required output by default
                if (output.Value is FileArtifact fileArtifact)
                {
                    processBuilder.AddOutputFile(fileArtifact, FileExistence.Required);
                }
                else if (output.Value is AbsolutePath absolutePath)
                {
                    processBuilder.AddOutputFile(absolutePath, FileExistence.Required);
                }
                // A directory artifact is interpreted as a regular (exclusive) opaque directory
                else if (output.Value is DirectoryArtifact directoryArtifact)
                {
                    processBuilder.AddOutputDirectory(directoryArtifact, SealDirectoryKind.Opaque);
                }
                else
                {
                    // Here we should find DirectoryOutput or FileOrPathOutput, object literals in both cases
                    var objectLiteral = Converter.ExpectObjectLiteral(output, context: new ConversionContext(pos: i, objectCtx: outputs));

                    var artifact = Converter.ExtractFileLike(objectLiteral, m_executeFileOrPathOutputArtifact, allowUndefined: true);
                    // If 'artifact' is defined, then we are in the FileOrPathOutput case, and therefore we expect 'existence' to be defined as well
                    if (artifact.IsValid)
                    {
                        var existence = Converter.ExtractStringLiteral(objectLiteral, m_executeFileOrPathOutputExistence, s_fileExistenceKindMap.Keys, allowUndefined: false);
                        processBuilder.AddOutputFile(artifact, s_fileExistenceKindMap[existence]);
                    }
                    else
                    {
                        // This should be the DirectoryOutput case then, and both fields should be defined
                        var directory = Converter.ExtractDirectory(objectLiteral, m_executeDirectoryOutputDirectory, allowUndefined: false);
                        var outputDirectoryKind = Converter.ExtractStringLiteral(objectLiteral, m_executeDirectoryOutputKind, s_outputDirectoryKind, allowUndefined: false);

                        if (outputDirectoryKind == "shared")
                        {
                            var reservedDirectory = context.GetPipConstructionHelper().ReserveSharedOpaqueDirectory(directory);
                            processBuilder.AddOutputDirectory(reservedDirectory, SealDirectoryKind.SharedOpaque);
                        }
                        else
                        {
                            processBuilder.AddOutputDirectory(directory, SealDirectoryKind.Opaque);
                        }
                    }
                }
            }
        }

        private void ProcessEnvironmentVariable(Context context, ProcessBuilder processBuilder, PipDataBuilder pipDataBuilder, ObjectLiteral obj, bool isPassThrough)
        {
            // Name of the environment variable.
            var n = obj[m_envName].Value as string;

            if (string.IsNullOrWhiteSpace(n))
            {
                throw new InputValidationException(
                    I($"Invalid environment variable name '{n}'"),
                    new ErrorContext(name: m_envName, objectCtx: obj));
            }

            if (BuildParameters.DisallowedTempVariables.Contains(n))
            {
                throw new InputValidationException(
                    I($"Setting the '{n}' environment variable is disallowed"),
                    new ErrorContext(name: m_envName, objectCtx: obj));
            }

            var property = obj[m_envValue];
            if (property.IsUndefined)
            {
                throw new InputValidationException(
                    I($"Value of the '{n}' environment variable is undefined"),
                    new ErrorContext(name: m_envValue, objectCtx: obj));
            }

            var convContext = new ConversionContext(name: m_argV, objectCtx: obj);
            var sepId = context.FrontEndContext.StringTable.Empty;

            var v = property.Value;
            switch (ArgTypeOf(context, property, ref convContext))
            {
                case ArgType.IsString:
                    var vStr = v as string;
                    Contract.Assume(vStr != null);
                    pipDataBuilder.Add(vStr);
                    break;
                case ArgType.IsBoolean:
                    Contract.Assume(v is bool);
                    pipDataBuilder.Add(((bool)v).ToString());
                    break;
                case ArgType.IsNumber:
                    Contract.Assume(v is int);
                    pipDataBuilder.Add(((int)v).ToString(CultureInfo.InvariantCulture));
                    break;
                case ArgType.IsFile:
                    var path = Converter.ExpectPath(property, strict: false, context: convContext);
                    pipDataBuilder.Add(path);
                    break;
                case ArgType.IsArrayOfFile:
                    var pathArr = v as ArrayLiteral;
                    Contract.Assume(pathArr != null);

                    for (var j = 0; j < pathArr.Length; j++)
                    {
                        pipDataBuilder.Add(Converter.ExpectPath(pathArr[j], strict: false, context: new ConversionContext(pos: j, objectCtx: pathArr)));
                    }

                    var sep = obj[m_envSeparator].Value as string;

                    if (sep != null && string.IsNullOrWhiteSpace(sep))
                    {
                        throw new InputValidationException(
                            I($"Path separator for the '{n}' environment variable is empty"),
                            new ErrorContext(name: m_envSeparator, objectCtx: obj));
                    }

                    sep = sep ?? System.IO.Path.PathSeparator.ToString();
                    sepId = StringId.Create(context.FrontEndContext.StringTable, sep);
                    break;
            }

            processBuilder.SetEnvironmentVariable(
                StringId.Create(context.StringTable, n),
                pipDataBuilder.ToPipData(sepId, PipDataFragmentEscaping.NoEscaping),
                isPassThrough);
        }

        private void ProcessAcquireSemaphores(Context context, ProcessBuilder processBuilder, ArrayLiteral semaphores)
        {
            for (var i = 0; i < semaphores.Length; ++i)
            {
                var semaphore = Converter.ExpectObjectLiteral(semaphores[i], new ConversionContext(pos: i, objectCtx: semaphores, name: m_executeAcquireSemaphores));
                var name = Converter.ExpectString(
                    semaphore[m_semaphoreInfoName],
                    new ConversionContext(name: m_semaphoreInfoName, objectCtx: semaphore));
                var limit = Converter.ExpectNumber(
                    semaphore[m_semaphoreInfoLimit],
                    context: new ConversionContext(name: m_semaphoreInfoLimit, objectCtx: semaphore));
                var incrementBy = Converter.ExpectNumber(
                    semaphore[m_semaphoreInfoIncrementBy],
                    context: new ConversionContext(name: m_semaphoreInfoIncrementBy, objectCtx: semaphore));

                processBuilder.SetSemaphore(name, limit, incrementBy);
            }
        }

        private static void ProcessAcquireMutexes(ProcessBuilder processBuilder, ArrayLiteral mutexes)
        {
            for (var i = 0; i < mutexes.Length; ++i)
            {
                var name = Converter.ExpectString(mutexes[i], new ConversionContext(pos: i, objectCtx: mutexes));
                processBuilder.SetSemaphore(name, 1, 1);
            }
        }

        private static void ProcessExitCodes(ProcessBuilder processBuilder, ArrayLiteral exitCodesLiteral, Action<ProcessBuilder, int[]> processBuilderAction)
        {
            var exitCodes = new int[exitCodesLiteral.Length];

            for (var i = 0; i < exitCodesLiteral.Length; ++i)
            {
                var exitCode = Converter.ExpectNumber(
                    exitCodesLiteral[i],
                    strict: true,
                    context: new ConversionContext(pos: i, objectCtx: exitCodesLiteral));
                exitCodes[i] = exitCode;
            }

            processBuilderAction(processBuilder, exitCodes);
        }

        private void ProcessUnsafeOptions(Context context, ProcessBuilder processBuilder, ObjectLiteral unsafeOptionsObjLit)
        {
            // UnsafeExecuteArguments.untrackedPaths
            // TODO: Fix all callers, in the api this is limited to Directory.
            ProcessOptionalDirectoryOrPathArray(unsafeOptionsObjLit, m_unsafeUntrackedScopes, dir => processBuilder.AddUntrackedDirectoryScope(dir), skipUndefined: true);

            var untrackedPaths = Converter.ExtractArrayLiteral(unsafeOptionsObjLit, m_unsafeUntrackedPaths, allowUndefined: true);
            if (untrackedPaths != null)
            {
                for (var i = 0; i < untrackedPaths.Length; i++)
                {
                    var value = untrackedPaths[i];
                    if (!value.IsUndefined)
                    {
                        Converter.ExpectPathOrFileOrDirectory(untrackedPaths[i], out var untrackedFile, out var untrackedDirectory, out var untrackedPath, new ConversionContext(pos: i, objectCtx: untrackedPaths, name: m_unsafeUntrackedPaths));
                        if (untrackedFile.IsValid)
                        {
                            processBuilder.AddUntrackedFile(untrackedFile);
                        }
                        if (untrackedPath.IsValid)
                        {
                            processBuilder.AddUntrackedFile(untrackedPath);
                        }
                        if (untrackedDirectory.IsValid)
                        {
                            processBuilder.AddUntrackedDirectoryScope(untrackedDirectory);
                        }
                    }
                }
            }

            // UnsafeExecuteArguments.hasUntrackedChildProcesses
            if (Converter.ExtractOptionalBoolean(unsafeOptionsObjLit, m_unsafeHasUntrackedChildProcesses) == true)
            {
                processBuilder.Options |= Process.Options.HasUntrackedChildProcesses;
            }

            // UnsafeExecuteArguments.allowPreservedOutputs
            var unsafePreserveOutput = Converter.ExtractOptionalBooleanOrInt(unsafeOptionsObjLit, m_unsafeAllowPreservedOutputs);
            if (unsafePreserveOutput.HasValue)
            {
                bool? enabled = unsafePreserveOutput.Value.Item1;
                int?  trustLevel = unsafePreserveOutput.Value.Item2;

                if(trustLevel.HasValue && trustLevel.Value < 0)
                {
                    throw new InputValidationException(I($"Expected '{m_unsafeAllowPreservedOutputs.ToString(StringTable)}' to be boolean or >= 0"), new ErrorContext(name: m_unsafeAllowPreservedOutputs,  objectCtx: unsafeOptionsObjLit));
                }
                if ((enabled.HasValue && enabled.Value) || (trustLevel.HasValue && trustLevel.Value > 0))
                {
                    processBuilder.Options |= Process.Options.AllowPreserveOutputs;
                    processBuilder.PreserveOutputsTrustLevel = enabled.HasValue ? (int)PreserveOutputsTrustValue.Lowest : trustLevel.Value;

                    if (context.FrontEndHost.Configuration.Sandbox.PreserveOutputsForIncrementalTool)
                    {
                        processBuilder.Options |= Process.Options.IncrementalTool;
                    }
                }
            }

            // UnsafeExecuteArguments.incrementalTool
            if (Converter.ExtractOptionalBoolean(unsafeOptionsObjLit, m_unsafeIncrementalTool) == true)
            {
                processBuilder.Options |= Process.Options.IncrementalTool;
            }

            // UnsafeExecuteArguments.requireGlobalDependencies
            if (Converter.ExtractOptionalBoolean(unsafeOptionsObjLit, m_unsafeRequireGlobalDependencies) == false)
            {
                processBuilder.Options &= ~Process.Options.RequireGlobalDependencies;
            }

            // UnsafeExecuteArguments.passThroughEnvironmentVariables
            var passThroughEnvironmentVariables = Converter.ExtractArrayLiteral(unsafeOptionsObjLit, m_unsafePassThroughEnvironmentVariables, allowUndefined: true);
            if (passThroughEnvironmentVariables != null)
            {
                using (var pipDataBuilderWrapper = context.FrontEndContext.GetPipDataBuilder())
                {
                    var pipDataBuilder = pipDataBuilderWrapper.Instance;

                    for (var i = 0; i < passThroughEnvironmentVariables.Length; i++)
                    {
                        var passThroughEnvironmentVariableElem = passThroughEnvironmentVariables[i];
                        if (passThroughEnvironmentVariableElem.IsUndefined || passThroughEnvironmentVariableElem.Value is string)
                        {
                            var passThroughEnvironmentVariable = Converter.ExpectString(passThroughEnvironmentVariableElem, new ConversionContext(pos: i, objectCtx: passThroughEnvironmentVariables));
                            processBuilder.SetPassthroughEnvironmentVariable(StringId.Create(context.StringTable, passThroughEnvironmentVariable));
                        }
                        else
                        {
                            var passThroughEnvironmentVariable = Converter.ExpectObjectLiteral(passThroughEnvironmentVariableElem, new ConversionContext(pos: i, objectCtx: passThroughEnvironmentVariables));
                            ProcessEnvironmentVariable(context, processBuilder, pipDataBuilder, passThroughEnvironmentVariable, isPassThrough: true);
                            
                            pipDataBuilder.Clear();
                        }
                    }
                }
            }

            processBuilder.PreserveOutputAllowlist = ProcessOptionalPathArray(unsafeOptionsObjLit, m_unsafePreserveOutputAllowlist, strict: false, skipUndefined: true);

            var reclassificationRules = Converter.ExtractArrayLiteral(unsafeOptionsObjLit, m_executeReclassificationRules, allowUndefined: true);
            if (reclassificationRules != null)
            {
                ProcessReclassificationRules(context, processBuilder, reclassificationRules);
            }

            // UnsafeExecuteArguments.childProcessesToBreakawayFromSandbox
            // TODO: expose BreakawayChildProcess to DScript
            processBuilder.ChildProcessesToBreakawayFromSandbox = 
                ProcessOptionalValueArray<PathAtom>(unsafeOptionsObjLit, m_unsafeChildProcessesToBreakawayFromSandbox, skipUndefined: true)
                .Select<PathAtom, IBreakawayChildProcess>(atom => new BreakawayChildProcess() { ProcessName = atom })
                .ToReadOnlyArray();

            // UnsafeExecuteArguments.trustStaticallyDeclaredAccesses
            if (Converter.ExtractOptionalBoolean(unsafeOptionsObjLit, m_unsafeTrustStaticallyDeclaredAccesses) == true)
            {
                processBuilder.Options |= Process.Options.TrustStaticallyDeclaredAccesses;
            }

            // UnsafeExecuteArguments.disableFullReparsePointResolving
            if (Converter.ExtractOptionalBoolean(unsafeOptionsObjLit, m_unsafeDisableFullReparsePointResolving) == true)
            {
                processBuilder.Options |= Process.Options.DisableFullReparsePointResolving;
            }

            // UnsafeExecuteArguments.disableSandboxing
            if (Converter.ExtractOptionalBoolean(unsafeOptionsObjLit, m_unsafeDisableSandboxing) == true)
            {
                processBuilder.Options |= Process.Options.DisableSandboxing;
            }

            // UnsafeExecuteArguments.bypassFingerprintSalt
            if (Converter.ExtractOptionalBoolean(unsafeOptionsObjLit, m_unsafeBypassFingerprintSalt) == true)
            {
                processBuilder.Options |= Process.Options.BypassFingerprintSalt;
            }
        }

        private void ProcessReclassificationRules(Context context, ProcessBuilder processBuilder, ArrayLiteral reclassificationRules)
        {
            var rules = new ReclassificationRule[reclassificationRules.Length];
            for (var i = 0; i < reclassificationRules.Length; ++i)
            {
                var rule = Converter.ExpectObjectLiteral(reclassificationRules[i], new ConversionContext(pos: i, objectCtx: reclassificationRules, name: m_executeReclassificationRules));
                var name = Converter.ExtractString(rule, m_reclassificationRuleName, allowUndefined: true);
                var pathRegex = Converter.ExtractString(rule, m_reclassificationRulePathRegex, allowUndefined: true);
                var resolvedObservationTypes = Converter.ExtractArrayLiteral(rule, m_reclassificationRuleResolvedObservationTypes, allowUndefined: true);
                var resolvedTypes = resolvedObservationTypes == null ? null : new ObservationType[resolvedObservationTypes.Length];
                if (resolvedObservationTypes != null)
                {
                    for (int j = 0; j < resolvedObservationTypes.Length; j++)
                    {
                        resolvedTypes[j] = (ObservationType)Enum.Parse(typeof(ObservationType), Converter.ExpectString(resolvedObservationTypes[j]));
                    }
                }

                DiscriminatingUnion<ObservationType,UnitValue> reclassifyTo = null;

                var reclassifyToVal = rule[m_reclassificationRuleReclassifyTo];
                if (!reclassifyToVal.IsUndefined)
                {
                    if (reclassifyToVal.Value is string enumAsString)
                    {
                        reclassifyTo = new DiscriminatingUnion<ObservationType,UnitValue>((ObservationType)Enum.Parse(typeof(ObservationType), enumAsString));
                    }
                    else // it's Unit
                    {
                        reclassifyTo = new DiscriminatingUnion<ObservationType, UnitValue>(UnitValue.Unit);
                    }
                }

                rules[i] = new ReclassificationRule()
                {
                    Name = name,
                    ReclassifyTo = reclassifyTo,
                    PathRegex = pathRegex,
                    ResolvedObservationTypes = resolvedTypes
                };
            }

            processBuilder.ReclassificationRules = rules;
        }

        private PipId InterpretFinalizationPipArguments(Context context, ObjectLiteral obj)
        {
            var tool = Converter.ExtractObjectLiteral(obj, m_executeTool, allowUndefined: true);
            var moniker = Converter.ExtractValue<IpcMoniker>(obj, m_ipcSendMoniker, allowUndefined: true);

            if ((tool == null && !moniker.HasValue) || (tool != null && moniker.HasValue))
            {
                throw new InputValidationException(
                    I($"Expected exactly one of the '{m_executeTool.ToString(StringTable)}' and '{m_ipcSendMoniker.ToString(StringTable)}' fields to be defined."),
                    new ErrorContext(objectCtx: obj));
            }

            if (tool != null)
            {
                TryScheduleProcessPip(context, obj, ServicePipKind.ServiceFinalization, out _, out var finalizationPip);
                return finalizationPip.PipId;
            }
            else
            {
                TryScheduleIpcPip(context, obj, allowUndefinedTargetService: true, isServiceFinalization: true, out _, out var ipcPipId);
                return ipcPipId;
            }
        }

        private static void IfPropertyDefined<TState>(TState state, ObjectLiteral obj, SymbolAtom propertyName, Action<TState, EvaluationResult, ConversionContext> callback)
        {
            var val = obj[propertyName];
            if (!val.IsUndefined)
            {
                callback(state, val, new ConversionContext(objectCtx: obj, name: propertyName));
            }
        }

        private static void IfIntPropertyDefined<TState>(TState state, ObjectLiteral obj, SymbolAtom propertyName, Action<TState, int> callback)
        {
            IfPropertyDefined(
                (state, callback),
                obj,
                propertyName,
                (tpl, val, convContext) => tpl.Item2(tpl.Item1, Converter.ExpectNumber(val, strict: true, context: convContext)));
        }

        /// <summary>
        /// Argument type.
        /// </summary>
        private enum ArgType
        {
            IsString,
            IsFile,
            IsBoolean,
            IsNumber,
            IsUndefined,

            // IsArray
            IsEmptyArray,
            IsArrayOfString,
            IsArrayOfFile,

            // IsObjectLiteral
            IsNamedArgument, // <string | string[] | File | File[] | boolean | number> |
            IsMultiArgument, // <string|File>
            IsArguments, // <string|File> |
            IsResponseFilePlaceholder,
        }

        private ArgType ArgTypeOf(Context context, EvaluationResult obj, ref ConversionContext convContext)
        {
            if (obj.IsUndefined)
            {
                return ArgType.IsUndefined;
            }

            var objValue = obj.Value;
            if ((objValue as string) != null)
            {
                return ArgType.IsString;
            }

            if (objValue is ArrayLiteral arr)
            {
                if (arr.Length > 0)
                {
                    var value = arr[0].Value;
                    if (value is string)
                    {
                        return ArgType.IsArrayOfString;
                    }

                    if (value is FileArtifact
                        || value is AbsolutePath
                        || value is DirectoryArtifact
                        || value is StaticDirectory)
                    {
                        return ArgType.IsArrayOfFile;
                    }

                    throw Converter.CreateException(
                        new[]
                        {
                            typeof(string),
                            typeof(AbsolutePath),
                            typeof(FileArtifact),
                            typeof(DirectoryArtifact),
                            typeof(StaticDirectory),
                        },
                        arr[0],
                        convContext);
                }

                return ArgType.IsEmptyArray;
            }

            if (objValue is ObjectLiteral me)
            {
                // IsMultiArgument, // <string|File>
                var isMultiArgument = !me[m_argValues].IsUndefined;

                if (isMultiArgument)
                {
                    return ArgType.IsMultiArgument;
                }

                // IsNamedArgument, // <string | string[] | File | File[] | boolean | number> |
                var isNamedArgument = !me[m_argN].IsUndefined;
                if (isNamedArgument)
                {
                    return ArgType.IsNamedArgument;
                }

                // IsArguments, // <string|File> |
                var isArguments = !me[m_argArgs].IsUndefined;
                if (isArguments)
                {
                    return ArgType.IsArguments;
                }

                var isResponseFilePlaceholder = !me[m_argResponseFileForRemainingArgumentsForce].IsUndefined;
                if (isResponseFilePlaceholder)
                {
                    return ArgType.IsResponseFilePlaceholder;
                }

                throw Converter.CreateException(
                    new[] { typeof(MultiArgument), typeof(NamedArgument), typeof(string), typeof(FileArtifact), typeof(ResponseFilePlaceHolder) },
                    obj,
                    convContext);
            }

            if (objValue is AbsolutePath || objValue is FileArtifact || objValue is DirectoryArtifact || objValue is StaticDirectory)
            {
                return ArgType.IsFile;
            }

            if (objValue is bool)
            {
                return ArgType.IsBoolean;
            }

            if (objValue is int)
            {
                return ArgType.IsNumber;
            }

            throw Converter.CreateException(
                new[]
                {
                    typeof(string), typeof(ArrayLiteral), typeof(ObjectLiteral), typeof(AbsolutePath), typeof(FileArtifact),
                    typeof(bool), typeof(int), typeof(DirectoryArtifact), typeof(StaticDirectory),
                },
                obj,
                convContext);
        }

        [SuppressMessage("Microsoft.Performance", "CA1800:DoNotCastUnnecessarily")]
        private static void ProcessImplicitDependency(ProcessBuilder processBuilder, EvaluationResult fileOrStaticDirectory, in ConversionContext convContext)
        {
            // In the past we allow AbsolutePath and DirectoryArtifact as well.
            // For AbsolutePath, one doesn't know whether the user intention is File or Directory, but our conversion will treat it as a source file.
            // Then someone who thinks of it as a Directory will wonder how things could work downstream.
            // For DirectoryArtifact, we simply get an exception because the ObservedInputProcessor expects it to be obtained from sealing a directory.
            Converter.ExpectFileOrStaticDirectory(fileOrStaticDirectory, out var file, out var staticDirectory, convContext);

            if (staticDirectory != null)
            {
                processBuilder.AddInputDirectory(staticDirectory.Root);
            }
            else
            {
                processBuilder.AddInputFile(file);
            }
        }

        private static void ProcessImplicitOutputs(ProcessBuilder processBuilder, ArrayLiteral implicitOutputs, FileExistence fileExistence)
        {
            for (var i = 0; i < implicitOutputs.Length; ++i)
            {
                var implicitOutput = implicitOutputs[i];
                if (implicitOutput.IsUndefined)
                {
                    continue;
                }

                if (implicitOutput.Value is DirectoryArtifact)
                {
                    var dir = Converter.ExpectDirectory(implicitOutput, new ConversionContext(pos: i, objectCtx: implicitOutputs));
                    processBuilder.AddOutputDirectory(dir, SealDirectoryKind.Opaque);
                }
                else
                {
                    var file = Converter.ExpectFile(implicitOutput, strict: false, context: new ConversionContext(pos: i, objectCtx: implicitOutputs));
                    processBuilder.AddOutputFile(file, fileExistence);
                }
            }
        }
    }
}
