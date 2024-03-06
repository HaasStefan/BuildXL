﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BuildXL.FrontEnd.Ninja.Serialization;
using BuildXL.FrontEnd.Sdk;
using BuildXL.FrontEnd.Utilities;
using BuildXL.FrontEnd.Workspaces.Core;
using BuildXL.Native.IO;
using BuildXL.Pips;
using BuildXL.Pips.Builders;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;

namespace BuildXL.FrontEnd.Ninja
{
    internal sealed class NinjaPipConstructor
    {
        private static readonly Regex s_pdbOutputArgumentRegex = new Regex(@"\s/Z[iI](\s|$)");
        private static readonly Regex s_allMspdbsrvRelevantOptionsRegex = new Regex(@"(\s/(FS|MP\d*))+(\s|$)");
        private static readonly Regex s_allDebugOptionsRegex = new Regex(@"(\s/(Z[iI7]|FS|MP\d*|DEBUG))+(\s|$)");

        private readonly FrontEndContext m_context;
        private readonly FrontEndHost m_frontEndHost;
        private readonly string m_frontEndName;
        private readonly ModuleDefinition m_moduleDefinition;
        private readonly AbsolutePath m_projectRoot;
        private readonly AbsolutePath m_specPath;

        private readonly PipConstructionHelper m_pipConstructionHelper;
        private readonly ConcurrentDictionary<NinjaNode, ProcessOutputs> m_processOutputs = new ConcurrentDictionary<NinjaNode, ProcessOutputs>();
        private readonly ConcurrentDictionary<AbsolutePath, FileArtifact> m_outputFileArtifacts = new ConcurrentDictionary<AbsolutePath, FileArtifact>();
        private readonly ConcurrentDictionary<string, AbsolutePath> m_exeLocations = new ConcurrentDictionary<string, AbsolutePath>();
        private readonly NinjaPipConstructionSettings m_settings;
        private readonly IEnumerable<KeyValuePair<string, string>> m_userDefinedEnvironment;
        private readonly IEnumerable<string> m_userDefinedPassthroughVariables;

        public NinjaPipConstructor(FrontEndContext context, 
            FrontEndHost frontEndHost, 
            string frontEndName, 
            ModuleDefinition moduleDefinition, 
            QualifierId qualifierId, 
            AbsolutePath projectRoot, 
            AbsolutePath specPath,
            NinjaPipConstructionSettings settings)
        {
            Contract.Requires(context != null);
            Contract.Requires(frontEndHost != null);
            Contract.Requires(moduleDefinition != null);
            Contract.Requires(projectRoot.IsValid);
            Contract.Requires(specPath.IsValid);

            m_context = context;
            m_frontEndHost = frontEndHost;
            m_moduleDefinition = moduleDefinition;
            m_projectRoot = projectRoot;
            m_specPath = specPath;
            m_settings = settings;
            m_pipConstructionHelper = GetPipConstructionHelperForModule(m_projectRoot, moduleDefinition, qualifierId);
            m_frontEndName = frontEndName;
            PrepareEnvironment(settings.UserDefinedEnvironment, settings.UserDefinedPassthroughVariables, out m_userDefinedEnvironment, out m_userDefinedPassthroughVariables);
        }

        internal bool TrySchedulePip(NinjaNode node, QualifierId qualifierId, out Process process)
        {
            try
            {
                // Create command line and inputs and outputs for pipBuilder.
                if (!TryBuildProcessAndSchedulePip(
                    node,
                    qualifierId,
                    out process))
                {
                    Tracing.Logger.Log.PipSchedulingFailed(m_context.LoggingContext, Location.FromFile(m_specPath.ToString(m_context.PathTable)));
                    return false;
                }
            }
            catch (Exception e)
            {
                Tracing.Logger.Log.UnexpectedPipConstructorException(
                    m_context.LoggingContext,
                    Location.FromFile(m_specPath.ToString(m_context.PathTable)),
                    e.GetLogEventMessage(),
                    e.StackTrace);

                process = null;
                return false;
            }

            return true;
        }

        private PipConstructionHelper GetPipConstructionHelperForModule(AbsolutePath projectRoot, ModuleDefinition moduleDefinition, QualifierId qualifierId)
        {
            // One and only one AbsolutePath in the specs (corresponding to the build.ninja
            // If this changed, this method would be out of here, as we would need a different PipConstructionHelper
            // for each spec
            Contract.Requires(moduleDefinition.Specs.Count == 1);

            // Get a symbol that is unique for this particular instance
            var fullSymbol = FullSymbol.Create(m_context.SymbolTable, $"ninja.{PipConstructionUtilities.SanitizeStringForSymbol(moduleDefinition.Descriptor.Name)}"); // TODO: Figure this out, complete
            AbsolutePath pathToSpec = moduleDefinition.Specs.First();
            if (!projectRoot.TryGetRelative(m_context.PathTable, pathToSpec, out var specRelativePath))
            {
                // Issue a warning and continue with Invalid path. PipConstructionHelper will just ignore
                Tracing.Logger.Log.CouldNotComputeRelativePathToSpec(m_context.LoggingContext,
                    Location.FromFile(projectRoot.ToString(m_context.PathTable)),
                    projectRoot.ToString(m_context.PathTable),
                    pathToSpec.ToString(m_context.PathTable));
                specRelativePath = RelativePath.Invalid;
            }

            var pipConstructionHelper = PipConstructionHelper.Create(
                m_context,
                m_frontEndHost.Engine.Layout.ObjectDirectory,
                m_frontEndHost.Engine.Layout.RedirectedDirectory,
                m_frontEndHost.Engine.Layout.TempDirectory,
                m_frontEndHost.PipGraph,
                moduleDefinition.Descriptor.Id,
                moduleDefinition.Descriptor.Name,
                specRelativePath,
                fullSymbol,
                new LocationData(pathToSpec, 0, 0), // TODO: This is the location of the value (that is scheduling pips through this helper) in its corresponding spec.
                                                    // Since we are not exposing any public value yet that represents this symbol, this location is fine.
                qualifierId);

            return pipConstructionHelper;
        }

        private bool TryBuildProcessAndSchedulePip(NinjaNode node, QualifierId qualifierId, out Process process)
        {
            process = null;
            if (node.Rule.Equals("phony"))
            {
                // For now, we are including phony rules ONLY as the final 'node', this is, the one that
                // corresponds to the target. TODO: Get rid of all phonies.
                // We can safely skip these, they only represent a rename in the graph.
                return true;
            }
            using (var processBuilder = ProcessBuilder.Create(
                m_context.PathTable,
                m_context.GetPipDataBuilder(),
                m_context.CredentialScanner,
                m_context.LoggingContext))
            {
                if (!TryConfigureProcessBuilder(processBuilder, node, qualifierId))
                {
                    // Error has been logged
                    return false;
                }

                // Process all outputs and inputs
                AddOutputs(node, processBuilder);
                AddInputs(node, processBuilder);

                // Try to schedule the process pip
                if (!m_pipConstructionHelper.TryAddProcess(processBuilder, out ProcessOutputs outputs, out process))
                {
                    // Error has been logged
                    return false;
                }

                // Add the computed outputs for this project, so dependencies can consume it
                m_processOutputs[node] = outputs;
                foreach (var output in outputs.GetOutputFiles())
                {
                    m_outputFileArtifacts[output.Path] = output;
                }

                return true;
            }

        }

        private void AddInputs(NinjaNode node, ProcessBuilder processBuilder)
        {

            foreach (AbsolutePath input in node.Inputs.Where(i => !m_outputFileArtifacts.ContainsKey(i))) // We only want source files. If the file is in m_outputFileArtifacts then it was output by someone else
            {                                                                                       // and we have that dependency expressed in node.
                processBuilder.AddInputFile(FileArtifact.CreateSourceFile(input));
            }

            foreach (NinjaNode dependency in node.Dependencies)
            {

                bool outputsPresent = m_processOutputs.TryGetValue(dependency, out ProcessOutputs processOutputs);

                string ListOutputs(NinjaNode n) => string.Join(" ", n.Outputs.Select(x => x.GetName(m_context.PathTable).ToString(m_context.StringTable)).ToList());
                Contract.Assert(outputsPresent, $"Pips must have been presented in dependency order: [build { ListOutputs(dependency) }] missing, dependency of [build { ListOutputs(node)} ]");

                foreach (FileArtifact output in processOutputs.GetOutputFiles())
                {
                    if (node.Inputs.Contains(output.Path))
                    {
                        processBuilder.AddInputFile(output);
                    }
                }
            }
        }


        private void AddOutputs(NinjaNode node, ProcessBuilder processBuilder)
        {
            foreach (AbsolutePath output in node.Outputs)
            {
                // TODO: outputs should be optional/required depending on the Ninja graph semantics instead of always optional
                FileArtifact file;
                if (m_outputFileArtifacts.TryGetValue(output, out file))
                {
                    processBuilder.AddOutputFile(file, FileExistence.Optional);
                }
                else
                {
                    processBuilder.AddOutputFile(output, FileExistence.Optional);
                }
            }

            processBuilder.AddOutputDirectory(DirectoryArtifact.CreateWithZeroPartialSealId(m_projectRoot), SealDirectoryKind.SharedOpaque);
            
            // Add additional output directories configured in the main config file
            PipConstructionUtilities.AddAdditionalOutputDirectories(processBuilder, m_settings.AdditionalOutputDirectories, m_projectRoot, m_context.PathTable);
        }

        private bool TryConfigureProcessBuilder(ProcessBuilder processBuilder, NinjaNode node, QualifierId qualifierId)
        {
            SeparateExecutableFromCommands(node.Command, out string executable, out string args);

            if (!TryFindExecutablePath(executable, out AbsolutePath exePath))
            {
                Tracing.Logger.Log.InvalidExecutablePath(m_context.LoggingContext, Location.FromFile(m_specPath.ToString(m_context.PathTable)), node.Command);
                return false;
            }

            FileArtifact prExeArtifact = FileArtifact.CreateSourceFile(exePath);
            processBuilder.Executable = prExeArtifact;
            processBuilder.AddInputFile(prExeArtifact);

            using (var pipDataBuilderWrapper = m_context.GetPipDataBuilder())
            {
                var pipDataBuilder = pipDataBuilderWrapper.Instance;
                pipDataBuilder.Add(args);
                processBuilder.ArgumentsBuilder.Add(pipDataBuilder.ToPipData(string.Empty, PipDataFragmentEscaping.NoEscaping));
            }


            if (node.ResponseFile.HasValue)
            {
                using (var pipDataBuilderWrapper = m_context.GetPipDataBuilder())
                {
                    var pipDataBuilder = pipDataBuilderWrapper.Instance;
                    pipDataBuilder.Add(node.ResponseFile?.Content);
                    PipData responseFileData = pipDataBuilder.ToPipData(string.Empty, PipDataFragmentEscaping.NoEscaping);

                    // We tell the process builder to write the response file but to not add any arguments to the process (requiresExplicitArgument = false)
                    // because that information is already in node.Command
                    var rspFileSpec = ResponseFileSpecification.Builder()
                        .ExplicitData(responseFileData)
                        .RequiresArgument(false)
                        .ExplicitPath((AbsolutePath)node.ResponseFile?.Path)
                        .Build();

                    processBuilder.SetResponseFileSpecification(rspFileSpec);
                }
            }

            processBuilder.ToolDescription = StringId.Create(m_context.StringTable, GetNodeDescription(node));
            processBuilder.Options |= Process.Options.AllowUndeclaredSourceReads;
            processBuilder.Options |= Process.Options.RequireGlobalDependencies;
            processBuilder.RewritePolicy = RewritePolicy.DefaultSafe;
            processBuilder.EnableTempDirectory();

            // Working directory - the directory containing the ninja spec file 
            // Ninja generators may express paths relative to this
            processBuilder.WorkingDirectory = DirectoryArtifact.CreateWithZeroPartialSealId(m_specPath.GetParent(m_context.PathTable));

            // Untrack directories
            UntrackFilesAndDirectories(processBuilder.WorkingDirectory, processBuilder);

            // Allow some surviving child process
            AddRequiredSurvivingChildren(processBuilder);

            // Environment variables
            SetEnvironmentVariables(processBuilder, node);

            return true;
        }

        private string GetNodeDescription(NinjaNode node)
        {
            // Only list up to two outputs
            var outputsSample = string.Join(", ", node.Outputs.Take(2).Select(output => output.ToString(m_context.PathTable)));
            bool truncatedOutputs = node.Outputs.Count > 2;

            return $"{m_moduleDefinition.Descriptor.Name} - {node.Rule} -> [{outputsSample}{(truncatedOutputs ? ", ..." : string.Empty)}]";
        }

        private void UntrackFilesAndDirectories(AbsolutePath projectRoot, ProcessBuilder processBuilder)
        {
            processBuilder.AddCurrentHostOSDirectories();
            processBuilder.AddUntrackedProgramDataDirectories();
            processBuilder.AddUntrackedAppDataDirectories();

            if (m_settings.UntrackingSettings != null)
            {
                PipConstructionUtilities.UntrackUserConfigurableArtifacts(m_context.PathTable, projectRoot, m_moduleDefinition.Specs.Select(spec => spec.GetParent(m_context.PathTable)), processBuilder, m_settings.UntrackingSettings);
            }

            if (OperatingSystemHelper.IsWindowsOS)
            {
                var programFilesDirectoryArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(AbsolutePath.Create(m_context.PathTable, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
                var programFilesX86DirectoryArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(AbsolutePath.Create(m_context.PathTable, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)));
                processBuilder.AddUntrackedDirectoryScope(programFilesDirectoryArtifact);
                processBuilder.AddUntrackedDirectoryScope(programFilesX86DirectoryArtifact);
                processBuilder.AddUntrackedDirectoryScope(DirectoryArtifact.CreateWithZeroPartialSealId(AbsolutePath.Create(m_context.PathTable, @"C:\PROGRA~1\"))); // TODO: This but better
                processBuilder.AddUntrackedDirectoryScope(DirectoryArtifact.CreateWithZeroPartialSealId(AbsolutePath.Create(m_context.PathTable, @"C:\PROGRA~2\"))); // TODO: This but better
                // Untrack the user profile. The corresponding mount is already configured for not tracking source files, and with allowed undeclared source reads,
                // any attempt to read into the user profile will fail to compute its corresponding hash
                // Skip this untrack for linux because Environment.SpecialFolder.UserProfile always return $HOME on linux 
                processBuilder.AddUntrackedDirectoryScope(DirectoryArtifact.CreateWithZeroPartialSealId(m_context.PathTable, SpecialFolderUtilities.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            }

            // Git accesses should be ignored if .git directory is there
            var gitDirectory = m_projectRoot.Combine(m_context.PathTable, ".git");
            if (m_frontEndHost.Engine.DirectoryExists(gitDirectory))
            {
                processBuilder.AddUntrackedDirectoryScope(DirectoryArtifact.CreateWithZeroPartialSealId(gitDirectory));
                processBuilder.AddUntrackedFile(FileArtifact.CreateSourceFile(m_projectRoot.Combine(m_context.PathTable, ".gitattributes")));
                processBuilder.AddUntrackedFile(FileArtifact.CreateSourceFile(m_projectRoot.Combine(m_context.PathTable, ".gitignore")));
            }
        }

        private void AddRequiredSurvivingChildren(ProcessBuilder processBuilder)
        {
            // mspdbsrv: 
            // This process will live beyond the cl.exe call.
            // Allow the pip job object to clean the process without complaint.
            //
            // vctip.exe: On any compile error this telemetry upload exe will be run as a detached process.
            // Just let it be killed.
            // TODO: Can we stop it running? https://stackoverflow.microsoft.com/questions/74425/how-to-disable-vctip-exe-in-vc14
            //
            // conhost.exe: This process needs a little bit more time to finish after the main process. We shouldn't be allowing
            // this one to survive, we just need the timeout to be slightly more than zero. This will also be beneficial to other 
            // arbitrary processeses that need a little bit more time. But, apparently, setting a timeout has a perf impact that is 
            // being investigated. TODO: revisit this once this is fixed.
            //
            // All child processes: Don't wait to kill the processes.
            // CODESYNC: CloudBuild repo TrackerExecutor.cs "info.NestedProcessTerminationTimeout = TimeSpan.Zero"
            processBuilder.AllowedSurvivingChildProcessNames = ReadOnlyArray<PathAtom>.FromWithoutCopy(
                PathAtom.Create(m_context.StringTable, "mspdbsrv.exe"),
                PathAtom.Create(m_context.StringTable, "vctip.exe"),
                PathAtom.Create(m_context.StringTable, "conhost.exe"));
            processBuilder.NestedProcessTerminationTimeout = TimeSpan.Zero;
        }

        private void SetEnvironmentVariables(ProcessBuilder processBuilder, NinjaNode node)
        {
            foreach (KeyValuePair<string, string> kvp in m_userDefinedEnvironment)
            {
                if (kvp.Value != null)
                {
                    var envPipData = new PipDataBuilder(m_context.StringTable);
                    envPipData.Add(kvp.Value);
                    processBuilder.SetEnvironmentVariable(StringId.Create(m_context.StringTable, kvp.Key), envPipData.ToPipData(kvp.Value, PipDataFragmentEscaping.NoEscaping), isPassThrough: false);
                }
            }

            foreach (var passthrough in m_userDefinedPassthroughVariables)
            {
                processBuilder.SetPassthroughEnvironmentVariable(StringId.Create(m_context.StringTable, passthrough));
            }

            foreach (var envVar in SpecialEnvironmentVariables.CloudBuildEnvironment)
            {
                processBuilder.SetPassthroughEnvironmentVariable(StringId.Create(m_context.StringTable, envVar));
            }

            // We will specify a different MSPDBSRV endpoint for every pip.
            // This means every pip that needs to communicate to MSPDBSRV will
            // spawn a different child process.
            // This is because if two pips use the same endpoint at the same time
            // then second one will fail after the first one finishes, because the
            // first one was the one that spawned MSPDBSRV.EXE as a child
            // (which gets killed).
            //
            // IMPORTANT: This will cause the build to fail if two pips target the same PDB file.
            // Both LINK.EXE and CL.EXE can use MSPDBSRV.EXE:
            //   - If all linkers specify a different /pdb argument (or don't specify this argument and
            //   are linking programs with different names
            //   [https://docs.microsoft.com/en-us/cpp/build/reference/debug-generate-debug-info])
            //   then this won't happen
            //
            //   - We're forcing the compiler to not output to PDBs (/Z7)
            //
            // so this should work in the normal cases.
            var mspdbsrvPipDataBuilder = new PipDataBuilder(m_context.StringTable);
            mspdbsrvPipDataBuilder.Add(PipConstructionUtilities.ComputeSha256(node.Command));   // Unique value for each pip
            processBuilder.SetEnvironmentVariable(
                    StringId.Create(m_context.StringTable, SpecialEnvironmentVariables.MsPdvSrvEndpoint),
                    mspdbsrvPipDataBuilder.ToPipData(string.Empty, PipDataFragmentEscaping.NoEscaping), 
                    isPassThrough: false);
        }

        /// <summary>
        ///  Ninja builds in CloudBuild are just a prototype and only work by carefully preparing the environment for the pips.
        ///  This method overrides the environment if we are in a cloudbuild build.
        /// </summary>
        private void PrepareEnvironment(
            IEnumerable<KeyValuePair<string, string>> userDefinedEnvironment,
            IEnumerable<string> userDefinedPassthroughs,
            out IEnumerable<KeyValuePair<string, string>> environment,
            out IEnumerable<string> passthroughs)
        {
            if (!m_frontEndHost.Configuration.InCloudBuild())
            {
                environment = userDefinedEnvironment;
                passthroughs = userDefinedPassthroughs;
                return;
            }
            else
            {
                // Filter passthroughs
                environment = userDefinedEnvironment.Where(kvp => !isCloudBuildPassthrough(kvp.Key));
                passthroughs = userDefinedPassthroughs.Union(environment.Select(kvp => kvp.Key).Where(k => isCloudBuildPassthrough(k)));

                bool isCloudBuildPassthrough(string envVar)
                {
                    return SpecialEnvironmentVariables.PassThroughPrefixes.Any(prefix => envVar.StartsWith(prefix))
                        || m_frontEndHost.Configuration.Sandbox.GlobalUnsafePassthroughEnvironmentVariables.Contains(envVar);
                }
            }
        }

        /// <summary>
        /// Split a full command line into the executable (or program name) being invoked
        /// and its arguments.
        /// </summary>con
        private void SeparateExecutableFromCommands(string command, out string executable, out string args)
        {
            if (command.StartsWith("\""))
            {
                // Executable is quoted at the beginning of command
                var split = command.Split(new char[] { '\"' }, 3);
                executable = split[1].Trim();
                args = split.Length > 2 ? split[2].Trim() : "";
            }
            else
            {
                var split = command.Split(new char[] { ' ' }, 2);
                executable = split[0].Trim();
                args = split.Length > 1 ? split[1].Trim() : "";
            }


            if (!EngineEnvironmentSettings.NinjaResolverAllowPdbOutputFlags)
            {
                // Remove /Zi, /ZI, and put /Z7 in its place
                // This will make the compiler embed the debugging information in the object file
                // instead of outputting to a PDB
                // https://docs.microsoft.com/en-us/cpp/build/reference/z7-zi-zi-debug-information-format
                args = s_pdbOutputArgumentRegex.Replace(args, " /Z7 ", 1);
            }

            if (!EngineEnvironmentSettings.NinjaResolverAllowCxxDebugFlags)
            {
                // Remove /MP and /FS, the arguments that cause MSPDBSRV.EXE to spawn and be used
                // https://docs.microsoft.com/en-us/cpp/build/reference/fs-force-synchronous-pdb-writes
                args = RemoveMspdbsrvOptions(args);
            }
        }

        /// <summary>
        /// Remove all compiler arguments which will trigger mspdbsrv to spawn
        /// </summary>
        private static string RemoveMspdbsrvOptions(string args)
        {
            return s_allMspdbsrvRelevantOptionsRegex.Replace(args, " ");
        }

        private bool TryFindExecutablePath(string executable, out AbsolutePath result)
        {
            if (m_exeLocations.TryGetValue(executable, out result))
            {
                return true;
            }


            if (AbsolutePath.TryCreate(m_context.PathTable, executable, out result))
            {
                m_exeLocations[executable] = result;
                return true;
            }

            string foundPath;

            // Check first if there is an executable with that name in the working directory
            var workingDirectory = m_specPath.GetParent(m_context.PathTable).ToString(m_context.PathTable);
            var relativeToWorkingDirectory = Path.Combine(workingDirectory, executable);
            if (File.Exists(foundPath = relativeToWorkingDirectory) || File.Exists(foundPath = relativeToWorkingDirectory + ".exe"))
            {
                return AbsolutePath.TryCreate(m_context.PathTable, foundPath, out result);
            }

            // Check the PATH
            foreach (string test in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string path = test.Trim();
                if (!string.IsNullOrEmpty(path)
                    && (File.Exists(foundPath = Path.Combine(path, executable)) || File.Exists(foundPath = Path.Combine(path, executable + ".exe")))
                    && AbsolutePath.TryCreate(m_context.PathTable, foundPath, out result))
                {
                    m_exeLocations[executable] = result;
                    return true;
                }
            }

            // Return it relative to the working directory. Fail if it's a malformed path.
            return AbsolutePath.TryCreate(m_context.PathTable, relativeToWorkingDirectory, out result);
        }
    }
}
