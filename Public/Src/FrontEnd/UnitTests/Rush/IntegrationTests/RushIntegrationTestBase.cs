// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BuildXL.Engine;
using BuildXL.Processes;
using BuildXL.Utilities;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Configuration.Mutable;
using BuildXL.Utilities.Core;
using Test.BuildXL.EngineTestUtilities;
using Test.BuildXL.FrontEnd.Core;
using Test.BuildXL.TestUtilities;
using Test.DScript.Ast;
using Xunit.Abstractions;

namespace Test.BuildXL.FrontEnd.Rush
{
    /// <summary>
    /// Provides facilities to run the engine adding Rush specific artifacts.
    /// </summary>
    public abstract class RushIntegrationTestBase : DsTestWithCacheBase
    {
        /// <summary>
        /// Keep in sync with deployment.
        /// </summary>
        protected string PathToRush => Path.Combine(TestDeploymentDir, "rush", "node_modules", "@microsoft", "rush", "bin", "rush").Replace("\\", "/");

        /// <summary>
        /// Keep in sync with deployment.
        /// </summary>
        protected string PathToNodeModules => Path.Combine(TestDeploymentDir, "rush", "node_modules").Replace("\\", "/");

        /// <summary>
        /// Keep in sync with deployment.
        /// </summary>
        protected string PathToNode => Path.Combine(TestDeploymentDir, "node", OperatingSystemHelper.IsLinuxOS? "bin/node" : "node.exe").Replace("\\", "/");

        /// <nodoc/>
        protected string PathToNodeFolder => Path.GetDirectoryName(PathToNode).Replace("\\", "/");

        /// <summary>
        /// CODESYNC Public\Src\FrontEnd\UnitTests\Rush\Test.BuildXL.FrontEnd.Rush.dsc
        /// </summary>
        protected string PathToBuildGraphPluginMockTool => Path.Combine(TestDeploymentDir, "rushBuildGraphMock", OperatingSystemHelper.IsLinuxOS 
            ? "Test.BuildXL.FrontEnd.BuildGraphPluginMock" 
            : "Test.BuildXL.FrontEnd.BuildGraphPluginMock.exe")
            .Replace("\\", "/");

        /// <summary>
        /// Default out dir to use in projects
        /// </summary>
        protected string OutDir { get; }

        /// <summary>
        /// Root to the source enlistment root
        /// </summary>
        protected string SourceRoot { get; }

        // By default the engine runs e2e
        protected virtual EnginePhases Phase => EnginePhases.Execute;

        private string RushUserProfile => Path.Combine(TestRoot, OperatingSystemHelper.IsWindowsOS ? "USERPROFILE" : "HOME").Replace("\\", "/");

        private string RushTempFolder => Path.Combine(TestRoot, "RUSH_TEMP_FOLDER").Replace("\\", "/");

        protected override bool DisableDefaultSourceResolver => true;

        private readonly bool m_isInternalBuild;

        protected RushIntegrationTestBase(ITestOutputHelper output) : base(output, true)
        {
            RegisterEventSource(global::BuildXL.Engine.ETWLogger.Log);
            RegisterEventSource(global::BuildXL.Processes.ETWLogger.Log);
            RegisterEventSource(global::BuildXL.Scheduler.ETWLogger.Log);
            RegisterEventSource(global::BuildXL.Pips.ETWLogger.Log);
            RegisterEventSource(global::BuildXL.FrontEnd.Core.ETWLogger.Log);
            RegisterEventSource(global::BuildXL.FrontEnd.Script.ETWLogger.Log);
            RegisterEventSource(global::BuildXL.FrontEnd.Rush.ETWLogger.Log);
            RegisterEventSource(global::BuildXL.FrontEnd.JavaScript.ETWLogger.Log);

            SourceRoot = Path.Combine(TestRoot, RelativeSourceRoot);
            OutDir = "target";
            
            // Make sure the user profile and temp folders exist
            Directory.CreateDirectory(RushUserProfile);
            Directory.CreateDirectory(RushTempFolder);

            // Update npmrc for internal builds.
            // This variable is only set for internal builds, so we can replace the default npm registry with the internal one here
            var npmrc = Environment.GetEnvironmentVariable("UserProfileNpmRcLocation");
            if (npmrc != null)
            {
                m_isInternalBuild = true;
                File.Copy(npmrc, Path.Combine(RushUserProfile, ".npmrc"), overwrite: true);
            }
        }

        protected SpecEvaluationBuilder Build(
            Dictionary<string, string> environment = null,
            string executeCommands = null,
            string customRushCommands = null,
            string rushBaseLibLocation = "",
            string rushExports = null,
            string moduleName = "Test",
            bool addDScriptResolver = false,
            string commonTempFolder = null,
            string schedulingCallback = null,
            string customScripts = null,
            string additionalDependencies = null,
            string additionalOutputDirectories = null,
            bool? enableFullReparsePointResolving = null,
            string nodeExeLocation = null,
            string rushLocation = null,
            bool? enforceSourceReadsUnderPackageRoots = null,
            string additionalSourceReadsScopes = null,
            string childProcessesToBreakawayFromSandbox = null,
            string timeouts = null,
            string warningRegex = null,
            string errorRegex = null)
        {
            environment ??= new Dictionary<string, string> { 
                ["PATH"] = PathToNodeFolder,
                ["RUSH_TEMP_FOLDER"] = commonTempFolder ?? RushTempFolder,
            };

            return Build(
                environment.ToDictionary(kvp => kvp.Key, kvp => new DiscriminatingUnion<string, UnitValue>(kvp.Value)),
                executeCommands,
                customRushCommands,
                rushBaseLibLocation,
                rushExports,
                moduleName,
                addDScriptResolver,
                commonTempFolder,
                schedulingCallback,
                customScripts,
                additionalDependencies,
                additionalOutputDirectories,
                enableFullReparsePointResolving,
                nodeExeLocation,
                rushLocation,
                enforceSourceReadsUnderPackageRoots,
                additionalSourceReadsScopes,
                childProcessesToBreakawayFromSandbox,
                timeouts,
                warningRegex,
                errorRegex);
        }

        /// <inheritdoc/>
        protected SpecEvaluationBuilder Build(
            Dictionary<string, DiscriminatingUnion<string, UnitValue>> environment,
            string executeCommands = null,
            string customRushCommands = null,
            string rushBaseLibLocation = "",
            string rushExports = null,
            string moduleName = "Test",
            bool addDScriptResolver = false,
            string commonTempFolder = null,
            string schedulingCallback = null,
            string customScripts = null,
            string additionalDependencies = null,
            string additionalOutputDirectories = null,
            bool? enableFullReparsePointResolving = null,
            string nodeExeLocation = null,
            string rushLocation = null,
            bool? enforceSourceReadsUnderPackageRoots = null,
            string additionalSourceReadsScopes = null,
            string childProcessesToBreakawayFromSandbox = null,
            string timeouts = null,
            string warningRegex = null,
            string errorRegex = null)
        {
            environment ??= new Dictionary<string, DiscriminatingUnion<string, UnitValue>> { 
                ["PATH"] = new DiscriminatingUnion<string, UnitValue>(PathToNodeFolder),
                ["RUSH_TEMP_FOLDER"] = new DiscriminatingUnion<string, UnitValue>(commonTempFolder ?? RushTempFolder),
            };

            // We reserve the null string for a true undefined.
            // rush-lib is part of node_modules deployment, so use that by default
            if (rushBaseLibLocation == string.Empty)
            {
                rushBaseLibLocation = PathToNodeModules;
            }

            if (nodeExeLocation == null)
            {
                nodeExeLocation = $"f`{PathToNode}`";
            }

            // Let's explicitly pass an environment, so the process environment won't affect tests by default
            return base.Build().Configuration(
                DefaultRushPrelude(
                    environment: environment,
                    executeCommands: executeCommands,
                    customRushCommands: customRushCommands,
                    rushBaseLibLocation: rushBaseLibLocation,
                    rushExports: rushExports,
                    moduleName: moduleName,
                    addDScriptResolver: addDScriptResolver,
                    // Let's assume for simplicity that if a custom common temp folder is passed, that means
                    // we want to use the shrinkwrap-deps file to track dependencies
                    trackDependenciesWithShrinkwrapDepsFile: commonTempFolder != null,
                    schedulingCallback: schedulingCallback,
                    customScripts: customScripts,
                    additionalDependencies,
                    additionalOutputDirectories,
                    enableFullReparsePointResolving,
                    nodeExeLocation,
                    rushLocation,
                    enforceSourceReadsUnderPackageRoots,
                    additionalSourceReadsScopes,
                    childProcessesToBreakawayFromSandbox,
                    timeouts,
                    warningRegex,
                    errorRegex));
        }

        protected BuildXLEngineResult RunRushProjects(
            ICommandLineConfiguration config,
            (string, string)[] rushPathAndProjectNames,
            TestCache testCache = null, 
            IDetoursEventListener detoursListener = null,
            bool overrideDisableReparsePointResolution = true)
        {
            // The out folder is junctioned in some environments, so disable full reparse point resolving unless specified otherwise
            if (overrideDisableReparsePointResolution)
            {
                ((UnsafeSandboxConfiguration)(config.Sandbox.UnsafeSandboxConfiguration)).EnableFullReparsePointResolving = false;
            }

            // Run 'rush init'. This bootstraps the 'repo' with rush template files, including rush.json
            if (!RushInit(config))
            {
                throw new InvalidOperationException("Rush init failed.");
            }

            // Update rush.json with the projects that need to be part of the 'repo'
            AddProjectsToRushConfig(config, rushPathAndProjectNames);

            // Run 'rush update' so dependencies are configured
            if (!RushUpdate(config))
            {
                throw new InvalidOperationException("Rush update failed.");
            }

            return RunEngine(config, testCache, detoursListener);
        }

        protected BuildXLEngineResult RunEngine(ICommandLineConfiguration config, TestCache testCache = null, IDetoursEventListener detoursListener = null)
        {
            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TestOutputDirectory))
            {
                var appDeployment = CreateAppDeployment(tempFiles);

                ((CommandLineConfiguration)config).Engine.Phase = Phase;
                ((CommandLineConfiguration)config).Sandbox.FileSystemMode = FileSystemMode.RealAndMinimalPipGraph;

                var engineResult = CreateAndRunEngine(
                    config,
                    appDeployment,
                    testRootDirectory: null,
                    rememberAllChangedTrackedInputs: true,
                    engine: out var engine,
                    testCache: testCache,
                    detoursListener: detoursListener);

                return engineResult;
            }
        }

        private void AddProjectsToRushConfig(ICommandLineConfiguration config, (string, string)[] rushProjectsPathAndName)
        {
            var pathToRushJson = config.Layout.SourceDirectory.Combine(PathTable, "rush.json").ToString(PathTable);
            string rushJson = File.ReadAllText(pathToRushJson);

            // Update the initial template created by 'rush init' with the projects that need to be part of the build
            var updatedRushJson = rushJson.Replace("\"projects\": [", "\"projects\": [" + string.Join(",", 
                rushProjectsPathAndName.Select(pathAndName => $"{{\"packageName\": \"{pathAndName.Item2}\", \"projectFolder\": \"{pathAndName.Item1}\"}}")));

            File.WriteAllText(pathToRushJson, updatedRushJson);
        }

        public static string CreatePackageJson(
            string projectName, 
            (string, string)[] scriptCommands = null, 
            (string dependency, string version)[] dependenciesWithVersions = null)
        {
            scriptCommands ??= new[] { ("build", "node ./main.js") };
            dependenciesWithVersions ??= new (string, string)[] { };

            return $@"
{{
  ""name"": ""{projectName}"",
  ""version"": ""0.0.1"",
  ""description"": ""Test project {projectName}"",
  ""scripts"": {{
        {string.Join(",", scriptCommands.Select(kvp => $"\"{kvp.Item1}\": \"{kvp.Item2}\""))}
  }},
  ""main"": ""main.js"",
  ""dependencies"": {{
        {string.Join(",", dependenciesWithVersions.Select(depAndVer => $"\"{depAndVer.dependency}\":\"{depAndVer.version}\""))}
    }}
}}
";
        }

        private string DefaultRushPrelude(
            Dictionary<string, DiscriminatingUnion<string, UnitValue>> environment,
            string executeCommands,
            string customRushCommands,
            string rushBaseLibLocation,
            string rushExports,
            string moduleName,
            bool addDScriptResolver,
            bool trackDependenciesWithShrinkwrapDepsFile,
            string schedulingCallback,
            string customScripts,
            string additionalDependencies,
            string additionalOutputDirectories,
            bool? enableFullReparsePointResolving = null,
            string nodeExeLocation = null,
            string rushLocation = null,
            bool? enforceSourceReadsUnderPackageRoots = null,
            string additionalSourceReadScopes = null,
            string childProcessesToBreakawayFromSandbox = null,
            string timeouts = null,
            string warningRegex = null,
            string errorRegex = null) => $@"
config({{
    resolvers: [
        {{
            kind: 'Rush',
            moduleName: '{moduleName}',
            root: d`.`,
            {(nodeExeLocation != null ? $"nodeExeLocation: {nodeExeLocation}," : string.Empty)}
            {DictionaryToExpression("environment", environment)}
            {(executeCommands != null? $"execute: {executeCommands}," : string.Empty)}
            {(customRushCommands != null ? $"customCommands: {customRushCommands}," : string.Empty)}
            {(rushBaseLibLocation != null ? $"rushLibBaseLocation: d`{rushBaseLibLocation}`," : string.Empty)}
            {(rushExports != null ? $"exports: {rushExports}," : string.Empty)}
            {(trackDependenciesWithShrinkwrapDepsFile ? $"trackDependenciesWithShrinkwrapDepsFile: true," : string.Empty)}
            {(schedulingCallback != null? $"customScheduling: {schedulingCallback}," : string.Empty)}
            {(customScripts != null ? $"customScripts: {customScripts}," : string.Empty)}
            {(additionalDependencies != null ? $"additionalDependencies: {additionalDependencies}," : string.Empty)}
            {(additionalOutputDirectories != null ? $"additionalOutputDirectories: {additionalOutputDirectories}," : string.Empty)}
            {(rushLocation != null ? $"rushLocation: f`{rushLocation}`," : string.Empty)}
            {(enforceSourceReadsUnderPackageRoots != null ? $"enforceSourceReadsUnderPackageRoots: {enforceSourceReadsUnderPackageRoots.Value.ToString().ToLowerInvariant()}," : string.Empty)}
            {(additionalSourceReadScopes != null ? $"additionalSourceReadsScopes: {additionalSourceReadScopes}," : string.Empty)}
            {(childProcessesToBreakawayFromSandbox != null ? $"childProcessesToBreakawayFromSandbox: {childProcessesToBreakawayFromSandbox}," : string.Empty)}
            {(timeouts != null ? $"timeouts: {timeouts}," : string.Empty)}
            {(warningRegex != null ? $"warningRegex: {warningRegex}," : string.Empty)}
            {(errorRegex != null ? $"errorRegex: {errorRegex}," : string.Empty)}
        }},
        {(addDScriptResolver? "{kind: 'DScript', modules: [f`module.config.dsc`, f`${Context.getBuildEngineDirectory()}/Sdk/Sdk.Transformers/package.config.dsc`]}" : string.Empty)}
    ],
    engine: {{unsafeAllowOutOfMountWrites: true}},
    {(enableFullReparsePointResolving != null ? $"sandbox: {{unsafeSandboxConfiguration: {{enableFullReparsePointResolving: {(enableFullReparsePointResolving.Value ? "true" : "false")}}}}}" : string.Empty)}
}});";

        private static string DictionaryToExpression(string memberName, Dictionary<string, DiscriminatingUnion<string, UnitValue>> dictionary)
        {
            return (dictionary == null ?
                string.Empty :
                $"{memberName}: Map.empty<string, (PassthroughEnvironmentVariable | string)>(){ string.Join(string.Empty, dictionary.Select(property => $".add('{property.Key}', {(property.Value?.GetValue() is UnitValue ? "Unit.unit()" : $"'{property.Value?.GetValue()}'")})")) },");
        }

        private bool RushInit(ICommandLineConfiguration config)
        {
            var result = RushRun(config, "init --overwrite-existing");

            if (result)
            {
                var pathToRushJson = config.Layout.SourceDirectory.Combine(PathTable, "rush.json").ToString(PathTable);
                string rushJson = File.ReadAllText(pathToRushJson);

                // Update the initial template created by 'rush init' to accept a higher version of node
                // Also update the pnpm version to make it work correctly with node
                var updatedRushJson = rushJson
                    .Replace(
                    "\"nodeSupportedVersionRange\": \">=12.13.0 <13.0.0 || >=14.15.0 <15.0.0\"",
                    "\"nodeSupportedVersionRange\": \">=10.13.0 <=18.6.0\"")
                    .Replace(
                    "\"pnpmVersion\": \"2.15.1\"",
                    "\"pnpmVersion\": \"5.0.2\"");

                File.WriteAllText(pathToRushJson, updatedRushJson);

                // Update npmrc for internal builds.
                if (m_isInternalBuild)
                {
                    // For more information on where we get this path and what is written to it by rush init see: https://rushjs.io/pages/configs/npmrc/
                    var defaultNpmRcPath = Path.Combine(config.Layout.SourceDirectory.ToString(PathTable), "common", "config", "rush", ".npmrc");
                    if (File.Exists(defaultNpmRcPath))
                    {
                        File.Delete(defaultNpmRcPath);
                    }

                    File.WriteAllText(defaultNpmRcPath, $"registry=https://cloudbuild.pkgs.visualstudio.com/_packaging/BuildXL.Selfhost/npm/registry/{Environment.NewLine}always-auth=true");
                }
            }

            return result;
        }

        private bool RushUpdate(ICommandLineConfiguration config)
        {
            return RushRun(config, "update");
        }

        private bool RushRun(ICommandLineConfiguration config, string rushArgs)
        {
            string arguments = $"{PathToRush} {rushArgs}";
            string filename = PathToNode;

            // Unfortunately, capturing standard out/err non-deterministically hangs node.exe on exit when 
            // concurrent npm install operations happen. Found reported bugs about this that look similar enough
            // to the problem that manifested here.
            // So we just report exit codes.
            var startInfo = new ProcessStartInfo
            {
                FileName = filename,
                Arguments = arguments,
                WorkingDirectory = config.Layout.SourceDirectory.ToString(PathTable),
                RedirectStandardError = false,
                RedirectStandardOutput = false,
                UseShellExecute = false,
            };

            // The default pip on Linux adds some common paths to PATH (e.g. /usr/bin). In some scenarios
            // node can be installed in those folders, so make sure the version we want takes precedence
            startInfo.Environment["PATH"] = $"{PathToNodeFolder}{Path.PathSeparator}{startInfo.Environment["PATH"]}";
            startInfo.Environment["NODE_PATH"] = PathToNodeModules;
            if (OperatingSystemHelper.IsWindowsOS)
            {
                startInfo.Environment["USERPROFILE"] = RushUserProfile;
            }
            else 
            {
                startInfo.Environment["HOME"] = RushUserProfile;
            }
            startInfo.Environment["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            startInfo.Environment["RUSH_TEMP_FOLDER"] = RushTempFolder;
            startInfo.Environment["RUSH_ABSOLUTE_SYMLINKS"] = "1";

            // Passthrough the PAT for internal builds (only set for ADO builds)
            // CODESYNC: Keep this variable name in sync with RunBxlWithPAT.ps1
            var npmFeedPat = Environment.GetEnvironmentVariable("CLOUDBUILD_BUILDXL_SELFHOST_FEED_PAT_B64");
            if (npmFeedPat != null)
            {
                startInfo.Environment["CLOUDBUILD_BUILDXL_SELFHOST_FEED_PAT_B64"] = npmFeedPat;
            }

            var runRush = Process.Start(startInfo);
            runRush.WaitForExit();

            return runRush.ExitCode == 0;
        }
    }
}
