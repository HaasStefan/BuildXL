// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildXL.Engine;
using BuildXL.Native.IO;
using BuildXL.Pips;
using BuildXL.Pips.Builders;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Configuration.Mutable;
using Test.BuildXL.Executables.TestProcess;
using Test.BuildXL.Scheduler;
using Test.BuildXL.TestUtilities;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace IntegrationTest.BuildXL.Scheduler
{
    [Trait("Category", "PreserveOutputsTests")]
    public class PreserveOutputsTests : SchedulerIntegrationTestBase
    {
        // Content we will be testing below in the output file
        internal const string CONTENT = "A";
        internal const string CONTENT_TWICE = CONTENT + CONTENT;
        internal const string CONTENT_THRICE = CONTENT_TWICE + CONTENT;

        public PreserveOutputsTests(ITestOutputHelper output) : base(output)
        {
        }

        // Helper method to schedule and create a pip
        private ProcessWithOutputs ScheduleAndGetPip(out FileArtifact input, out FileArtifact output, bool opaque, bool pipPreserveOutputsFlag)
        {
            input = CreateSourceFile();
            string opaqueStrPath = string.Empty;
            if (opaque)
            {
                opaqueStrPath = Path.Combine(ObjectRoot, "opaqueDir");
                output = CreateOutputFileArtifact(opaqueStrPath);
            }
            else
            {
                output = CreateOutputFileArtifact();
            }

            // ...........PIP A...........
            var builderA = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(input),
                // Appends to output unless output does not exist.
                // If output does not exist, then output is created and then written.
                Operation.WriteFile(output, CONTENT, doNotInfer:opaque)
            });

            if (opaque)
            {
                AbsolutePath opaqueDirPath = AbsolutePath.Create(Context.PathTable, opaqueStrPath);
                // Cache will materialize a file into an opaque dir or leave it there with preserve outputs.
                builderA.AddOutputDirectory(opaqueDirPath);
            }

            if (pipPreserveOutputsFlag)
            {
                builderA.Options |= Process.Options.AllowPreserveOutputs;
            }

            return SchedulePipBuilder(builderA);
        }

        private void ScheduleProcessAndCopy(out FileArtifact input, out FileArtifact preservedOutput, out FileArtifact copiedOutput, out Process preservingProcess)
        {
            // input -> ProcessPip -> preservedOutput -> CopyPip -> copiedOutput

            input = CreateSourceFile();
            preservedOutput = CreateOutputFileArtifact();

            var builder = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(input),
                // Appends to output unless output does not exist.
                // If output does not exist, then output is created and then written.
                Operation.WriteFile(preservedOutput, CONTENT, doNotInfer: false)
            });

            builder.Options |= Process.Options.AllowPreserveOutputs;

            var processOutputs = SchedulePipBuilder(builder);
            preservingProcess = processOutputs.Process;
            copiedOutput = CopyFile(preservedOutput, CreateOutputFileArtifact().Path);
        }

        private void ScheduleProcessConsumingDynamicOutput(
            out FileArtifact input,
            out DirectoryArtifact outputDirectory,
            out FileArtifact preservedOutput,
            out Process dynamicOutputProducer,
            out Process preservingProcess)
        {
            // dummyInput -> Process A -> opaqueDir -> Process B -> preservedOutput
            //                                            ^
            //                                            |
            //                                 input -----+

            var dummyInput = CreateSourceFile();
            string opaqueStrPath = string.Empty;
            opaqueStrPath = Path.Combine(ObjectRoot, "opaqueDir");
            AbsolutePath opaqueDirPath = AbsolutePath.Create(Context.PathTable, opaqueStrPath);
            var dynamicOutput = CreateOutputFileArtifact(opaqueStrPath);

            // Pip A
            var builderA = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(dummyInput),
                Operation.WriteFile(dynamicOutput, CONTENT, doNotInfer: true)
            });

            builderA.AddOutputDirectory(opaqueDirPath);

            builderA.Options |= Process.Options.AllowPreserveOutputs;

            var processAndOutputsA = SchedulePipBuilder(builderA);
            outputDirectory = processAndOutputsA.ProcessOutputs.GetOpaqueDirectory(opaqueDirPath);
            dynamicOutputProducer = processAndOutputsA.Process;

            // Pip B
            input = CreateSourceFile();
            preservedOutput = CreateOutputFileArtifact();

            var builderB = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(input),
                Operation.WriteFile(preservedOutput, CONTENT, doNotInfer: false)
            });

            builderB.AddInputDirectory(outputDirectory);

            builderB.Options |= Process.Options.AllowPreserveOutputs;
            var processAndOutputsB = SchedulePipBuilder(builderB);
            preservingProcess = processAndOutputsB.Process;
        }

        private void ScheduleProcessConsumingPreservedOutput(
            out FileArtifact preservedOutput,
            out FileArtifact input,
            out Process preservingProcess,
            out Process consumingProcess)
        {
            // dummyInput -> Process A -> preservedOutput -> Process B -> output
            //                                                  ^
            //                                                  |
            //                                 input -----------+

            var dummyInput = CreateSourceFile();
            preservedOutput = CreateOutputFileArtifact();

            // Pip A
            var builderA = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(dummyInput),
                Operation.WriteFile(preservedOutput, CONTENT)
            });

            builderA.Options |= Process.Options.AllowPreserveOutputs;

            var processAndOutputsA = SchedulePipBuilder(builderA);
            preservingProcess = processAndOutputsA.Process;

            // Pip B
            input = CreateSourceFile();
            var dummyOutput = CreateOutputFileArtifact();

            var builderB = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(preservedOutput),
                Operation.ReadFile(input),
                Operation.WriteFile(dummyOutput)
            });

            builderB.Options |= Process.Options.AllowPreserveOutputs;
            var processAndOutputsB = SchedulePipBuilder(builderB);
            consumingProcess = processAndOutputsB.Process;
        }

        private void ScheduleRewriteProcess(out FileArtifact rewrittenOutput, out Process preservingProcessA, out Process preservingProcessB)
        {
            // dummyInput -> Process A -> rewrittenOutput -> Process B -> rewrittenOutput

            var dummyInput = CreateSourceFile();
            var rewrittenOutputRc1 = CreateOutputFileArtifact();

            // Pip A
            var builderA = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(dummyInput),
                Operation.WriteFile(rewrittenOutputRc1, CONTENT, doNotInfer: false)
            });

            builderA.Options |= Process.Options.AllowPreserveOutputs;

            var processAndOutputsA = SchedulePipBuilder(builderA);
            preservingProcessA = processAndOutputsA.Process;

            // Pip B
            rewrittenOutput = rewrittenOutputRc1.CreateNextWrittenVersion();

            var builderB = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(rewrittenOutputRc1),
                Operation.WriteFile(rewrittenOutput, CONTENT, doNotInfer: false)
            });

            builderB.Options |= Process.Options.AllowPreserveOutputs;
            var processAndOutputsB = SchedulePipBuilder(builderB);
            preservingProcessB = processAndOutputsB.Process;
        }

        private string RunSchedulerAndGetOutputContents(FileArtifact output, bool cacheHitAssert, PipId id)
        {
            if (cacheHitAssert)
            {
                RunScheduler().AssertCacheHit(id);
            }
            else
            {
                RunScheduler().AssertCacheMiss(id);
            }

            return File.ReadAllText(ArtifactToString(output));
        }

        [Fact]
        public void PreserveOutputsTest()
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;
            var input = CreateSourceFile();
            var output = CreateOutputFileArtifact(Path.Combine(ObjectRoot, @"nested\out\file"));

            var builder = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(input),
                Operation.WriteFile(output, CONTENT)
            });

            builder.Options |= Process.Options.AllowPreserveOutputs;
            var processAndOutputs = SchedulePipBuilder(builder);

            var outputContent = RunSchedulerAndGetOutputContents(output, false, processAndOutputs.Process.PipId);
            XAssert.AreEqual(CONTENT, outputContent);

            ModifyFile(input);

            outputContent = RunSchedulerAndGetOutputContents(output, false, processAndOutputs.Process.PipId);
            XAssert.AreEqual(CONTENT_TWICE, outputContent);
        }

        [Fact]
        public void PreserveOutputsTestWithAllowlist()
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;
            var input = CreateSourceFile();
            var outputPreserved = CreateOutputFileArtifact(Path.Combine(ObjectRoot, @"nested\out\filePreserved"));
            var outputUnpreserved = CreateOutputFileArtifact(Path.Combine(ObjectRoot, @"nested\out\fileUnpreserved"));

            var builder = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(input),
                Operation.WriteFile(outputPreserved, CONTENT),
                Operation.WriteFile(outputUnpreserved, CONTENT)
            });

            builder.Options |= Process.Options.AllowPreserveOutputs;
            builder.PreserveOutputAllowlist = ReadOnlyArray<AbsolutePath>.FromWithoutCopy(outputPreserved);
            var processAndOutputs = SchedulePipBuilder(builder);

            var outputContent = RunSchedulerAndGetOutputContents(outputPreserved, false, processAndOutputs.Process.PipId);
            XAssert.AreEqual(CONTENT, outputContent);
            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputUnpreserved)));

            ModifyFile(input);

            outputContent = RunSchedulerAndGetOutputContents(outputPreserved, false, processAndOutputs.Process.PipId);
            XAssert.AreEqual(CONTENT_TWICE, outputContent);
            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputUnpreserved)));

            outputContent = RunSchedulerAndGetOutputContents(outputPreserved, true, processAndOutputs.Process.PipId);
            XAssert.AreEqual(CONTENT_TWICE, outputContent);
            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputUnpreserved)));
        }

        [Fact]
        public void PreserveOutputsTestWithTrustLevel()
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputsTrustLevel = 2;
            var input = CreateSourceFile();
            var outputPreservedA = CreateOutputFileArtifact(Path.Combine(ObjectRoot, @"nested\out\filePreservedA"));
            var outputPreservedB = CreateOutputFileArtifact(Path.Combine(ObjectRoot, @"nested\out\filePreservedB"));

            var builderA = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(input),
                Operation.WriteFile(outputPreservedA, CONTENT),
            });

            var builderB = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(input),
                Operation.WriteFile(outputPreservedB, CONTENT),
            });

            // processA will not perserve outputs because its trust level is lower than global setting of preserve output trust level
            // but processB will preserve output
            builderA.Options |= Process.Options.AllowPreserveOutputs;
            builderA.PreserveOutputsTrustLevel = 1;
            var processAndOutputsA = SchedulePipBuilder(builderA);

            builderB.Options |= Process.Options.AllowPreserveOutputs;
            builderB.PreserveOutputsTrustLevel = 2;
            var processAndOutputsB = SchedulePipBuilder(builderB);

            RunScheduler().AssertCacheMiss(processAndOutputsA.Process.PipId, processAndOutputsB.Process.PipId);

            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputPreservedA)));
            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputPreservedB)));

            ModifyFile(input);

            RunScheduler().AssertCacheMiss(processAndOutputsA.Process.PipId, processAndOutputsB.Process.PipId);

            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputPreservedA)));
            XAssert.AreEqual(CONTENT_TWICE, File.ReadAllText(ArtifactToString(outputPreservedB)));

            RunScheduler().AssertCacheHit(processAndOutputsA.Process.PipId, processAndOutputsB.Process.PipId);

            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputPreservedA)));
            XAssert.AreEqual(CONTENT_TWICE, File.ReadAllText(ArtifactToString(outputPreservedB)));
        }

        [Fact]
        public void PreserveOutputsTestWithTrustLevelUpgrade()
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputsTrustLevel = 1;
            var input = CreateSourceFile();
            var outputPreservedA = CreateOutputFileArtifact(Path.Combine(ObjectRoot, @"nested\out\filePreservedA"));

            var builderA = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(input),
                Operation.WriteFile(outputPreservedA, CONTENT),
            });

            // processA will perserve outputs
            builderA.Options |= Process.Options.AllowPreserveOutputs;
            builderA.PreserveOutputsTrustLevel = 1;
            var processAndOutputsA = SchedulePipBuilder(builderA);
            RunSchedulerAndGetOutputContents(outputPreservedA, false, processAndOutputsA.Process.PipId);
            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputPreservedA)));

            ModifyFile(input);

            RunSchedulerAndGetOutputContents(outputPreservedA, false, processAndOutputsA.Process.PipId);
            XAssert.AreEqual(CONTENT_TWICE, File.ReadAllText(ArtifactToString(outputPreservedA)));

            //increase preserve output trust level to 2 and process A will not preserve outputs anymore
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputsTrustLevel = 2;

            RunSchedulerAndGetOutputContents(outputPreservedA, false, processAndOutputsA.Process.PipId);
            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputPreservedA)));
        }

        [Fact]
        public void PreserveOutputsTestWithTrustLevelDowngrade()
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputsTrustLevel = 3;
            var input = CreateSourceFile();
            var outputPreservedA = CreateOutputFileArtifact(Path.Combine(ObjectRoot, @"nested\out\filePreservedA"));

            var builderA = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(input),
                Operation.WriteFile(outputPreservedA, CONTENT),
            });

            // processA won't perserve outputs
            builderA.Options |= Process.Options.AllowPreserveOutputs;
            builderA.PreserveOutputsTrustLevel = 2;
            var processAndOutputsA = SchedulePipBuilder(builderA);
            RunSchedulerAndGetOutputContents(outputPreservedA, false, processAndOutputsA.Process.PipId);
            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputPreservedA)));

            ModifyFile(input);
            RunSchedulerAndGetOutputContents(outputPreservedA, false, processAndOutputsA.Process.PipId);
            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputPreservedA)));

            ModifyFile(input);
            //after decreasing global preserve output trust level to 1, process A will preserve outputs
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputsTrustLevel = 1;
            RunSchedulerAndGetOutputContents(outputPreservedA, false, processAndOutputsA.Process.PipId);
            XAssert.AreEqual(CONTENT_TWICE, File.ReadAllText(ArtifactToString(outputPreservedA)));

        }

        public void PreserveOutputsWithTrustLevelModification()
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputsTrustLevel = 3;
            var input = CreateSourceFile();
            var outputPreservedA = CreateOutputFileArtifact(Path.Combine(ObjectRoot, @"nested\out\filePreservedA"));

            var builderA = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(input),
                Operation.WriteFile(outputPreservedA, CONTENT),
            });

            // processA won't perserve outputs
            builderA.Options |= Process.Options.AllowPreserveOutputs;
            builderA.PreserveOutputsTrustLevel = 2;
            var processAndOutputsA = SchedulePipBuilder(builderA);
            RunSchedulerAndGetOutputContents(outputPreservedA, false, processAndOutputsA.Process.PipId);
            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputPreservedA)));

            // after decreasing global preserve output trust level to 1, process A will not be scheduled
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputsTrustLevel = 1;
            ScheduleRunResult result = RunScheduler();
            if (Configuration.Schedule.IncrementalScheduling)
            {
                result.AssertNotScheduled(processAndOutputsA.Process.PipId);
            }
            else
            {
                result.AssertCacheHit(processAndOutputsA.Process.PipId);
            }
            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputPreservedA)));

            // once we change back global TL to 3, process A will be scheduled again
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputsTrustLevel = 3;
            result = RunScheduler();
            if (Configuration.Schedule.IncrementalScheduling)
            {
                result.AssertScheduled(processAndOutputsA.Process.PipId);
            }
            else
            {
                result.AssertCacheMiss(processAndOutputsA.Process.PipId);
            }
            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputPreservedA)));
        }
        /// <summary>
        /// Testing preserve outputs in an opaque dir
        /// </summary>
        [Theory]
        [MemberData(nameof(TruthTable.GetTable), 2, MemberType = typeof(TruthTable))]
        public void PreserveOutputsOpaqueTest(bool storeOutputsToCache, bool ignorePrivatization)
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;
            Configuration.Schedule.StoreOutputsToCache = storeOutputsToCache;
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.IgnorePreserveOutputsPrivatization = ignorePrivatization;

            if (storeOutputsToCache && ignorePrivatization)
            {
                // Invalid configuration.
                var config = new CommandLineConfiguration(Configuration);
                BuildXLEngine.PopulateLoggingAndLayoutConfiguration(config, Context.PathTable, bxlExeLocation: null, inTestMode: true);
                XAssert.IsFalse(BuildXLEngine.PopulateAndValidateConfiguration(config, config, Context.PathTable, LoggingContext));
                AssertErrorEventLogged(global::BuildXL.Engine.Tracing.LogEventId.ConfigIncompatibleOptionIgnorePreserveOutputsPrivatization);
                return;
            }

            // Output is in opaque dir and Unsafe.AllowPreservedOutputs = true
            var pipA = ScheduleAndGetPip(out var input, out var output, opaque: true, pipPreserveOutputsFlag: true);

            // No cache hit
            string outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);
            XAssert.AreEqual(CONTENT, outputContents);

            // Change input
            ModifyFile(input);

            // No cache hit
            outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);

            // As the opaque output is preserved, the pip appended the existing file.
            XAssert.AreEqual(CONTENT_TWICE, outputContents);

            if (ignorePrivatization)
            {
                AssertVerboseEventLogged(global::BuildXL.Processes.Tracing.LogEventId.PipProcessPreserveOutputDirectorySkipMakeFilesPrivate);
            }

            outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: true, id: pipA.Process.PipId);

            // Cache hit and the appended file (CONTENT_TWICE) should remain the same.
            XAssert.AreEqual(CONTENT_TWICE, outputContents);
        }

        /// <summary>
        /// Testing preserve outputs in an opaque dir with preserveoutputallowlist
        /// </summary>
        [Fact]
        [Feature(Features.OpaqueDirectory)]
        public void PreserveOutputsOpaqueTestWithAllowlist()
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;

            var input = CreateSourceFile();
            var opaquePreservedPath = AbsolutePath.Create(Context.PathTable, Path.Combine(ObjectRoot, "opaquePreservedDir"));
            var outputUnderPreservedOpaque = CreateOutputFileArtifact(opaquePreservedPath);
            var createdDirectoryUnderPreservedOpaque = DirectoryArtifact.CreateWithZeroPartialSealId(opaquePreservedPath.Combine(Context.PathTable, "CreatedDir"));

            var opaqueUnpreservedPath = AbsolutePath.Create(Context.PathTable, Path.Combine(ObjectRoot, "opaqueUnpreservedDir"));
            var outputUnderUnpreservedOpaque = CreateOutputFileArtifact(opaqueUnpreservedPath);
            var createdDirectoryUnderUnpreservedOpaque = DirectoryArtifact.CreateWithZeroPartialSealId(opaqueUnpreservedPath.Combine(Context.PathTable, "CreatedDir"));

            var builder = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(input),
                Operation.WriteFile(outputUnderPreservedOpaque, CONTENT, doNotInfer: true),
                Operation.CreateDir(createdDirectoryUnderPreservedOpaque, doNotInfer: true),
                Operation.WriteFile(outputUnderUnpreservedOpaque, CONTENT, doNotInfer: true),
                Operation.CreateDir(createdDirectoryUnderUnpreservedOpaque, doNotInfer: true),
            });

            builder.AddOutputDirectory(opaquePreservedPath);
            builder.AddOutputDirectory(opaqueUnpreservedPath);
            builder.Options |= Process.Options.AllowPreserveOutputs;
            builder.PreserveOutputAllowlist = ReadOnlyArray<AbsolutePath>.FromWithoutCopy(opaquePreservedPath);
            var processAndOutputs = SchedulePipBuilder(builder);

            // No cache hit
            string outputContents = RunSchedulerAndGetOutputContents(outputUnderPreservedOpaque, cacheHitAssert: false, id: processAndOutputs.Process.PipId);
            XAssert.AreEqual(CONTENT, outputContents);
            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputUnderUnpreservedOpaque)));

            // Change input
            ModifyFile(input);

            // No cache hit
            outputContents = RunSchedulerAndGetOutputContents(outputUnderPreservedOpaque, cacheHitAssert: false, id: processAndOutputs.Process.PipId);

            // As the opaque output is preserved, the pip appended the existing file.
            XAssert.AreEqual(CONTENT_TWICE, outputContents);
            // For the file under unpreserved opaque directory, the file was created, so we did not append.
            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputUnderUnpreservedOpaque)));

            // Cache hit
            outputContents = RunSchedulerAndGetOutputContents(outputUnderPreservedOpaque, cacheHitAssert: true, id: processAndOutputs.Process.PipId);
            XAssert.IsTrue(Directory.Exists(createdDirectoryUnderPreservedOpaque.Path.ToString(Context.PathTable)), "Empty directory under preserved opaque should have existed.");
            // Incremental scheduling doesn't replay the pip from cache and just leaves the filesystem as-is
            if (!Configuration.Schedule.IncrementalScheduling)
            {
                XAssert.IsFalse(Directory.Exists(createdDirectoryUnderUnpreservedOpaque.Path.ToString(Context.PathTable)), "Empty directory under non-preserved opaque should not exist.");
            }

            // The appended file (CONTENT_TWICE) should remain the same.
            XAssert.AreEqual(CONTENT_TWICE, outputContents);
            XAssert.AreEqual(CONTENT, File.ReadAllText(ArtifactToString(outputUnderUnpreservedOpaque)));
        }

        /// <summary>
        /// Testing behavior of enabling preserve outputs and then reseting
        /// </summary>
        /// <param name="reset">true will reset preserve outputs after it is enabled, false will not reset preserve outputs</param>
        [Theory]
        [MemberData(nameof(TruthTable.GetTable), 1, MemberType = typeof(TruthTable))]
        public void PreserveOutputResetTest(bool reset)
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;

            var pipA = ScheduleAndGetPip(out var input, out var output, opaque: false, pipPreserveOutputsFlag: true);

            // No cache hit
            RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);

            // Change the input 
            ModifyFile(input);

            // No cache hit
            string outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);
            XAssert.AreEqual(CONTENT_TWICE, outputContents);

            // ... RESET PRESERVE OUTPUTS ...
            if (reset)
            {
                Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Reset;

                // No cache hit
                outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);
                XAssert.AreEqual(CONTENT_THRICE, outputContents);
            }
            else
            {
                // Cache hit
                outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: true, id: pipA.Process.PipId);
                XAssert.AreEqual(CONTENT_TWICE, outputContents);

                // Change the input again
                ModifyFile(input);

                // No cache hit
                outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);
                XAssert.AreEqual(CONTENT_THRICE, outputContents);
            }
        }

        /// <summary>
        /// Testing behavior of when UnsafeSandboxConfigurationMutable.PreserveOutputs or
        /// Unsafe.AllowPreservedOutputs do not agree
        /// </summary>
        /// <param name="buildPreserve">UnsafeSandboxConfigurationMutable.PreserveOutputs value</param>
        /// <param name="pipPreserve">Unsafe.AllowPreservedOutputs value</param>
        [Theory]
        [MemberData(nameof(TruthTable.GetTable), 2, MemberType = typeof(TruthTable))]
        public void BuildAndPipFlagTest(bool buildPreserve, bool pipPreserve)
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = buildPreserve
                ? PreserveOutputsMode.Enabled
                : PreserveOutputsMode.Disabled;

            var pipA = ScheduleAndGetPip(out var input, out var output, false, pipPreserve);

            // No cache hit
            RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);

            // Change the input
            ModifyFile(input);
            string outputContents;
            if (!buildPreserve || !pipPreserve)
            {
                // No cache hit
                outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);
                XAssert.AreEqual(CONTENT, outputContents);
            }
            else
            {
                // No cache hit
                outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);
                XAssert.AreEqual(CONTENT_TWICE, outputContents);
            }
        }

        /// <summary>
        /// Testing preserve outputs enabled or not and disabling after enabling
        /// </summary>
        /// <param name="preserveOutputs">value is used for config: UnsafeSandboxConfigurationMutable.PreserveOutputs and Unsafe.AllowPreservedOutputs</param>
        [Theory]
        [MemberData(nameof(TruthTable.GetTable), 1, MemberType = typeof(TruthTable))]
        public void PreserveOutputsTest2(bool preserveOutputs)
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = preserveOutputs
                ? PreserveOutputsMode.Enabled
                : PreserveOutputsMode.Disabled;

            var pipA = ScheduleAndGetPip(out var input, out var output, false, preserveOutputs);

            // No cache hit
            string outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);
            XAssert.AreEqual(CONTENT, outputContents);

            // Change the input
            ModifyFile(input);

            if (preserveOutputs)
            {
                // No cache hit
                outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);
                XAssert.AreEqual(CONTENT_TWICE, outputContents);

                // Cache hit
                outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: true, id: pipA.Process.PipId);
                XAssert.AreEqual(CONTENT_TWICE, outputContents);

                // Turning off preserve outputs now should cause the fingerprint to change (even though input is same)
                // which causes a cache miss and the pip rerun after BuildXL deletes the file
                Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Disabled;
            }

            // No cache hit preserve outputs is disabled by default or disabled after it is enabled (as in if statement above)
            outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);
            XAssert.AreEqual(CONTENT, outputContents);
        }

        /// <summary>
        /// Testing that preserve outputs do not store outputs to cache.
        /// </summary>
        /// <param name="preserveOutputs">value is used for config: UnsafeSandboxConfigurationMutable.PreserveOutputs and Unsafe.AllowPreservedOutputs</param>
        [Theory]
        [MemberData(nameof(TruthTable.GetTable), 1, MemberType = typeof(TruthTable))]
        public void PreserveOutputsDoNotStoreOutputsToCacheTest(bool preserveOutputs)
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs =
                preserveOutputs
                ? PreserveOutputsMode.Enabled
                : PreserveOutputsMode.Disabled;

            var pipA = ScheduleAndGetPip(out var input, out var output, opaque: false, pipPreserveOutputsFlag: true);

            // No cache hit
            string outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);
            XAssert.AreEqual(CONTENT, outputContents);

            // Output is in artifact cache iff preserve output is off.
            XAssert.AreEqual(!preserveOutputs, FileContentExistsInArtifactCache(output));

            // Delete the output.
            FileUtilities.DeleteFile(ArtifactToString(output));

            // Cache hit only if preserved output is disabled.
            outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: !preserveOutputs, id: pipA.Process.PipId);
            XAssert.AreEqual(CONTENT, outputContents);
        }

        /// <summary>
        /// Testing that copying a preserved output using copy-file pip works.
        /// </summary>
        [Fact]
        public void CopyPreservedOutputTest()
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;

            FileArtifact input;
            FileArtifact preservedOutput;
            FileArtifact copiedOutput;
            Process preservingProcess;

            ScheduleProcessAndCopy(out input, out preservedOutput, out copiedOutput, out preservingProcess);
            RunScheduler().AssertCacheMiss(preservingProcess.PipId);

            // Due to copy, the preserved output needs to be restored to the cache.
            XAssert.IsTrue(FileContentExistsInArtifactCache(copiedOutput));

            // Change the input
            ModifyFile(input);

            RunScheduler().AssertCacheMiss(preservingProcess.PipId);

            var preservedOutputContent = File.ReadAllText(ArtifactToString(preservedOutput));
            var copiedOutputContent = File.ReadAllText(ArtifactToString(copiedOutput));

            XAssert.AreEqual(CONTENT_TWICE, preservedOutputContent);
            XAssert.AreEqual(CONTENT_TWICE, copiedOutputContent);
        }

        /// <summary>
        /// IncrementalTool. 
        /// </summary>
        [Fact]
        [Trait("Category", "SkipLinux")] // TODO: bug
        public void IncrementalPreserveOutputTool()
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;
            Configuration.IncrementalTools = new List<RelativePath>
            {
                RelativePath.Create(Context.StringTable, TestProcessToolName)
            };

            AbsolutePath.TryCreate(Context.PathTable, ReadonlyRoot, out AbsolutePath readonlyRootPath);

            // Create /readonly/a.txt
            FileArtifact aTxtFile = CreateFileArtifactWithName("a.txt", ReadonlyRoot);
            WriteSourceFile(aTxtFile);

            DirectoryArtifact readonlyRootDir = SealDirectory(readonlyRootPath, SealDirectoryKind.SourceAllDirectories);

            var builder = CreatePipBuilder(new Operation[]
            {
                Operation.Probe(aTxtFile, doNotInfer: true),
                Operation.WriteFile(CreateOutputFileArtifact())
            });

            builder.AddInputDirectory(readonlyRootDir);

            builder.Options |= Process.Options.AllowPreserveOutputs;
            builder.Options |= Process.Options.IncrementalTool;

            var pip = SchedulePipBuilder(builder).Process;

            RunScheduler().AssertCacheMiss(pip.PipId);
            RunScheduler().AssertCacheHit(pip.PipId);

            WriteSourceFile(aTxtFile);
            RunScheduler().AssertCacheMiss(pip.PipId);
        }

        /// <summary>
        /// Test Incremental Tool with Directory.EnumerateFileSystemEntries used on Windows
        /// </summary>
        [TheoryIfSupported(requiresWindowsBasedOperatingSystem: true)]
        [MemberData(nameof(TruthTable.GetTable), 2, MemberType = typeof(TruthTable))]
        public void IncrementalToolEnumerateTest(bool enableIncrementalTool, bool useDotNetEnumerationOnWindows)
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;
            Configuration.IncrementalTools = new List<RelativePath>
            {
                RelativePath.Create(Context.StringTable, TestProcessToolName)
            };

            // Create directory and source files
            var directory = CreateUniqueDirectory();
            string dirString = directory.ToString(Context.PathTable);
            FileArtifact aCFile = CreateFileArtifactWithName("a.c", dirString);
            WriteSourceFile(aCFile);

            DirectoryArtifact sealedDir = CreateAndScheduleSealDirectoryArtifact(directory, SealDirectoryKind.SourceAllDirectories);

            var builder = CreatePipBuilder(new Operation[]
            {
                // When useDotNetEnumerationOnWindows is set to true, the enumeration is performed using .NET Directory.EnumerateFileSystemEntries
                // instead of FileSystemWin.EnumerateWinFileSystemEntriesForTest. The former, since net5, calls NtQueryDirectoryFile API, while the latter
                // calls FindFirstFile/FindNextFile API. Most modern tools (e.g., later version of Gradle) enumerate directory for caching purpose by calling
                // NtQueryDirectoryFile API.
                Operation.EnumerateDir(sealedDir, useDotNetEnumerationOnWindows, doNotInfer: true),
                Operation.WriteFile(CreateOutputFileArtifact())
            });

            builder.AddInputDirectory(sealedDir);

            if (enableIncrementalTool)
            {
                builder.Options |= Process.Options.AllowPreserveOutputs;
                builder.Options |= Process.Options.IncrementalTool;
            }

            var pip = SchedulePipBuilder(builder).Process;

            RunScheduler().AssertCacheMiss(pip.PipId);
            RunScheduler().AssertCacheHit(pip.PipId);

            WriteSourceFile(aCFile);
            if (enableIncrementalTool)
            {
                // Pip should cache miss because incremental tool observed the file change
                RunScheduler().AssertCacheMiss(pip.PipId);
            }

            // Pip should cache hit because incremental tool is disabled, the file change is not observed
            RunScheduler().AssertCacheHit(pip.PipId);

            // Pip should cache miss because adding file is observed
            FileArtifact aHFile = CreateFileArtifactWithName("a.h", dirString);
            WriteSourceFile(aHFile);
            RunScheduler().AssertCacheMiss(pip.PipId);
            RunScheduler().AssertCacheHit(pip.PipId);
        }

        [TheoryIfSupported(requiresWindowsBasedOperatingSystem: true)]
        [MemberData(nameof(TruthTable.GetTable), 2, MemberType = typeof(TruthTable))]
        public void IncrementalToolEnumerateNonSealedDirectoryTest(bool hasExplicitMemberDependency, bool useDotNetEnumerationOnWindows)
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;
            Configuration.IncrementalTools = new List<RelativePath>
            {
                RelativePath.Create(Context.StringTable, TestProcessToolName)
            };

            // Create directory and source files
            var directory = CreateUniqueDirectoryArtifact(prefix: "dir");
            FileArtifact aCFile = CreateFileArtifactWithName("a.c", directory.Path.ToString(Context.PathTable));
            WriteSourceFile(aCFile);

            var builder = CreatePipBuilder(new Operation[]
            {
                hasExplicitMemberDependency ? Operation.ReadFile(aCFile) : Operation.Echo("no op"),
                Operation.EnumerateDir(directory, useDotNetEnumerationOnWindows: useDotNetEnumerationOnWindows, doNotInfer: true),
                Operation.WriteFile(CreateOutputFileArtifact())
            });

            builder.Options |= Process.Options.AllowPreserveOutputs;
            builder.Options |= Process.Options.IncrementalTool;

            var pip = SchedulePipBuilder(builder).Process;

            RunScheduler().AssertCacheMiss(pip.PipId);
            RunScheduler().AssertCacheHit(pip.PipId);

            WriteSourceFile(aCFile);

            if (hasExplicitMemberDependency)
            {
                // Pip should cache miss because of explicit dependency.
                // When there is no such an explicit dependency, the file a.c does not have
                // ReportAccess policy and is not reported as a file read. Therefore, cache hit.
                RunScheduler().AssertCacheMiss(pip.PipId);
            }

            // Pip should cache hit because incremental tool is disabled, the file change is not observed
            RunScheduler().AssertCacheHit(pip.PipId);

            // Pip should cache miss because adding file is observed
            FileArtifact aHFile = CreateFileArtifactWithName("a.h", directory.Path.ToString(Context.PathTable));
            WriteSourceFile(aHFile);

            // Because the enumerated directory is not under any source sealed directory, the enumeration
            // in the observed input processor will use the minimal graph mode. In this case the minimal
            // graph will only include a.c because it is specified as file dependency in the graph. Thus,
            // adding another file will not affect the directory fingerprint.
            RunScheduler().AssertCacheHit(pip.PipId);
        }

        /// <summary>
        /// Testing that preserved output can be consumed.
        /// </summary>
        [Fact]
        public void ProcessConsumingPreservedOutputTest()
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;


            ScheduleProcessConsumingPreservedOutput(out FileArtifact preservedOutput, out FileArtifact input, out Process preservingProcess, out Process consumingProcess);
            RunScheduler().AssertCacheMiss(preservingProcess.PipId, consumingProcess.PipId);

            // Preserved output is not in the cache.
            XAssert.IsFalse(FileContentExistsInArtifactCache(preservedOutput));

            // Change the input
            ModifyFile(input);

            var result = RunScheduler();
            result.AssertCacheMiss(consumingProcess.PipId);
            result.AssertCacheHit(preservingProcess.PipId);

            // Preserved output is not in the cache.
            XAssert.IsFalse(FileContentExistsInArtifactCache(preservedOutput));
        }

        /// <summary>
        /// Testing that preserve output pips can live with dynamic outputs.
        /// </summary>
        [Fact]
        public void PreservingProcessConsumingDynamicOutputTest()
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;

            ScheduleProcessConsumingDynamicOutput(
                out FileArtifact input,
                out DirectoryArtifact outputDirectory,
                out FileArtifact preservedOutput,
                out Process dynamicOutputProducer,
                out Process preservingProcess);

            RunScheduler().AssertCacheMiss(preservingProcess.PipId, dynamicOutputProducer.PipId);

            // Delete dynamic output.
            FileUtilities.DeleteDirectoryContents(ArtifactToString(outputDirectory), deleteRootDirectory: true);

            // Cache miss as the output is gone.
            RunScheduler().AssertCacheMiss(dynamicOutputProducer.PipId);

            // Dynamic output producer should result in cache hit.
            RunScheduler().AssertCacheHit(dynamicOutputProducer.PipId);
            var preservedOutputContent = File.ReadAllText(ArtifactToString(preservedOutput));
            XAssert.AreEqual(CONTENT, preservedOutputContent);

            // Modify input to preserving process.
            ModifyFile(input);

            var schedulerResult = RunScheduler();
            schedulerResult.AssertCacheHit(dynamicOutputProducer.PipId);
            schedulerResult.AssertCacheMiss(preservingProcess.PipId);
            preservedOutputContent = File.ReadAllText(ArtifactToString(preservedOutput));
            XAssert.AreEqual(CONTENT_TWICE, preservedOutputContent);
        }

        /// <summary>
        /// Testing the switch from disabling to enabling preserve output mode.
        /// </summary>
        [Fact]
        public void PreserveOutputsOffThenOn()
        {
            // Turn off.
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Disabled;

            var pipA = ScheduleAndGetPip(out var input, out var output, false, true);

            // No cache hit.
            string outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);
            XAssert.AreEqual(CONTENT, outputContents);

            // Cache hit.
            outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: true, id: pipA.Process.PipId);
            XAssert.AreEqual(CONTENT, outputContents);

            // Turn on.
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;

            // Cache hit.
            outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: true, id: pipA.Process.PipId);
            XAssert.AreEqual(CONTENT, outputContents);

            // Change the input.
            ModifyFile(input);

            // This should be the only cache miss.
            outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);
            XAssert.AreEqual(CONTENT_TWICE, outputContents);

            // Turn off again.
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Disabled;

            // This hould result in cache miss.
            outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);
            XAssert.AreEqual(CONTENT, outputContents);

            // Cache hit.
            outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: true, id: pipA.Process.PipId);
            XAssert.AreEqual(CONTENT, outputContents);
        }

        /// <summary>
        /// Validates that the command line preserveoutputs setting doesn't impact caching
        /// if the pip doesn't allow preserveoutputs
        /// </summary>
        [Fact]
        public void PreserveOutputsOnlyAppliesToSpecificPips()
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;
            var pipA = ScheduleAndGetPip(out _, out var output, opaque: false, pipPreserveOutputsFlag: false);

            // No cache hit.
            string outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: false, id: pipA.Process.PipId);
            XAssert.AreEqual(CONTENT, outputContents);

            // Cache hit.
            outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: true, id: pipA.Process.PipId);
            XAssert.AreEqual(CONTENT, outputContents);

            // Disabling preserve outputs should have no impact because it was not enabled for this pip in the first run
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Disabled;
            outputContents = RunSchedulerAndGetOutputContents(output, cacheHitAssert: true, id: pipA.Process.PipId);
            XAssert.AreEqual(CONTENT, outputContents);
        }

        /// <summary>
        /// Testing that rewritten preserved outputs are stored in cache.
        /// </summary>
        [Fact]
        public void RewrittenPreservedOutputsAreStoredInCache()
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;

            ScheduleRewriteProcess(out FileArtifact rewrittenOutput, out Process preservingProcessA, out Process preservingProcessB);

            // No cache hit
            RunScheduler().AssertCacheMiss(preservingProcessA.PipId, preservingProcessB.PipId);
            string outputContents = File.ReadAllText(ArtifactToString(rewrittenOutput));
            XAssert.AreEqual(CONTENT_TWICE, outputContents);

            File.Delete(ArtifactToString(rewrittenOutput));

            // Cache hit
            RunScheduler().AssertCacheHit(preservingProcessA.PipId, preservingProcessB.PipId);
            outputContents = File.ReadAllText(ArtifactToString(rewrittenOutput));
            XAssert.AreEqual(CONTENT_TWICE, outputContents);
        }


        [Feature(Features.OpaqueDirectory)]
        [Feature(Features.PreserveOutputs)]
        [FactIfSupported(requiresSymlinkPermission: true)]
        public void OpaqueDirectoryCleanupWithPreserveOutputs()
        {
            // Create a pip with the following:
            // 1. An opaque directory output "opaqueDir"
            // 2. Produces a symlink (of any kind) at path "opaqueDir\A\AA\B"
            // 3. Utilized PreservedOutputs
            var opaqueDir = FileOrDirectoryArtifact.Create(DirectoryArtifact.CreateWithZeroPartialSealId(CreateUniquePath("opaqueDir", ObjectRoot)));
            var dirA = FileOrDirectoryArtifact.Create(DirectoryArtifact.CreateWithZeroPartialSealId(opaqueDir.Path.Combine(Context.PathTable, "A")));
            string dirAPathString = dirA.Path.ToString(Context.PathTable);
            var dirAA = FileOrDirectoryArtifact.Create(DirectoryArtifact.CreateWithZeroPartialSealId(dirA.Path.Combine(Context.PathTable, "AA")));
            var fileB = new FileArtifact(dirAA.Path.Combine(Context.PathTable, "B"));

            var pip1 = CreatePipBuilder(new Operation[]
            {
                Operation.CreateDir(opaqueDir),
                Operation.CreateDir(dirA),
                Operation.CreateDir(dirAA),
                Operation.CreateSymlink(fileB, @"c:\anotherDummy", symLinkFlag: Operation.SymbolicLinkFlag.DIRECTORY, doNotInfer:true),
            });
            pip1.AddOutputDirectory(opaqueDir.Path);
            pip1.Options |= Process.Options.AllowPreserveOutputs;

            var process = SchedulePipBuilder(pip1);
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = PreserveOutputsMode.Enabled;
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.IgnoreFullReparsePointResolving = false;

            // The first build will pass without any problems
            RunScheduler().AssertSuccess();

            // Next, we externally mess with the state of the opaque directory. We replace "opaqueDir\A" with a directory symlink that is dangling
            FileUtilities.DeleteDirectoryContents(dirAPathString, deleteRootDirectory: true);
            XAssert.IsTrue(FileUtilities.TryCreateSymbolicLink(dirAPathString, @"c:\dummyPath", isTargetFile: false).Succeeded);
            // Should not be able to successfully enumerate this dangling symlink
            Assert.Throws<DirectoryNotFoundException>(() => Directory.EnumerateFileSystemEntries(dirAPathString));

            // Also add some other outputs to ensure they are not altered
            string extraFile = Path.Combine(opaqueDir.Path.ToString(Context.PathTable), "extraFile.txt");
            File.WriteAllText(extraFile, "blah");

            string extraDirSymlink = Path.Combine(opaqueDir.Path.ToString(Context.PathTable), "extraDirSymlink");
            XAssert.IsTrue(FileUtilities.TryCreateSymbolicLink(extraDirSymlink, @"c:\dummyPath", isTargetFile: false).Succeeded);

            // Rerun the build. This should be a cache hit against the first build and re-create the same state on disk
            RunScheduler().AssertCacheHit(process.Process.PipId);

            // We should now be able to successfully enumerate what was a dangling synlink
            XAssert.AreEqual(1, Directory.EnumerateFileSystemEntries(dirAPathString).Count());
            // Other entries should be left as-is
            XAssert.IsTrue(File.Exists(extraFile));
            Assert.Throws<DirectoryNotFoundException>(() => Directory.EnumerateFileSystemEntries(extraDirSymlink));
        }

        [Feature(Features.OpaqueDirectory)]
        [Feature(Features.PreserveOutputs)]
        [TheoryIfSupported(requiresSymlinkPermission: true)]
        [MemberData(nameof(TruthTable.GetTable), 1, MemberType = typeof(TruthTable))]
        public void OpaqueDirectoryNotCleanUpWithPreserveOutputs(bool enablePreserveOutputs)
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = enablePreserveOutputs
                ? PreserveOutputsMode.Enabled
                : PreserveOutputsMode.Disabled;
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.IgnoreFullReparsePointResolving = false;

            // Producer creates an opaque directory:
            //   opaqueDir
            //   +- fileUnderOpaqueDir.txt
            var opaqueDir = CreateUniqueDirectoryArtifact(prefix: "opaqueDir", root: ObjectRoot);
            var fileUnderOpaqueDir = CreateFileArtifactWithName("fileUnderOpaqueDir.txt", opaqueDir.Path.ToString(Context.PathTable));

            var producerBuilder = CreatePipBuilder(new Operation[]
            {
                Operation.WriteFile(fileUnderOpaqueDir, doNotInfer: true)
            });
            producerBuilder.AddOutputDirectory(opaqueDir.Path);
            producerBuilder.Options |= Process.Options.AllowPreserveOutputs;

            var producer = SchedulePipBuilder(producerBuilder).Process;

            // Consumer reads non-existing symlink under opaque directory produced by producer.
            var symlinkUnderOpaqueDir = CreateFileArtifactWithName("symlinkUnderOpaqueDir.lnk", opaqueDir.Path.ToString(Context.PathTable));
            var consumerInput = CreateSourceFile();

            var consumerBuilder = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(consumerInput),
                Operation.ReadFile(symlinkUnderOpaqueDir, doNotInfer: true),
                Operation.WriteFile(CreateOutputFileArtifact())
            });
            consumerBuilder.AddInputDirectory(opaqueDir);

            var consumer = SchedulePipBuilder(consumerBuilder).Process;

            RunScheduler(runNameOrDescription: "First run").AssertCacheMiss(producer.PipId, consumer.PipId);

            // Create a dangling symlink under opaque directory.
            XAssert.IsTrue(FileUtilities.TryCreateSymbolicLink(symlinkUnderOpaqueDir.Path.ToString(Context.PathTable), @"c:\dummyPath", isTargetFile: true).Succeeded);

            // Modify consumer input so that consumer needs to rerun.
            ModifyFile(consumerInput);
            ScheduleRunResult result = RunScheduler(runNameOrDescription: "Second run");

            if (enablePreserveOutputs)
            {
                result.AssertFailure();

                // DFA on reading the dangling symlink that is not cleaned up because preserve outputs is enabled.
                AssertErrorEventLogged(global::BuildXL.Scheduler.Tracing.LogEventId.FileMonitoringError);
                AssertWarningEventLogged(global::BuildXL.Scheduler.Tracing.LogEventId.ProcessNotStoredToCacheDueToFileMonitoringViolations);
            }
            else
            {
                result
                    .AssertCacheHit(producer.PipId)
                    .AssertCacheMiss(consumer.PipId);

                // The dangling symlink should be removed.
                XAssert.IsFalse(File.Exists(symlinkUnderOpaqueDir.Path.ToString(Context.PathTable)));
            }
        }

        private void ModifyFile(FileArtifact file, string content = null)
        {
            File.WriteAllText(ArtifactToString(file), content ?? Guid.NewGuid().ToString());
        }
    }
}
