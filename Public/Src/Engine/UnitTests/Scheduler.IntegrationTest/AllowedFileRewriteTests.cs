// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildXL.Pips.Builders;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Configuration;
using Test.BuildXL.Executables.TestProcess;
using Test.BuildXL.Scheduler;
using Test.BuildXL.TestUtilities;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;
using Xunit.Abstractions;
using LogEventId = BuildXL.Scheduler.Tracing.LogEventId;
using BuildXL.Cache.ContentStore.UtilitiesCore.Internal;

namespace IntegrationTest.BuildXL.Scheduler
{
    [Trait("Category", "SharedOpaqueDirectoryTests")]
    [Feature(Features.SharedOpaqueDirectory)]
    public class AllowedFileRewriteTests : SchedulerIntegrationTestBase
    {
        public AllowedFileRewriteTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public void AllowedRewriteCachingBehavior()
        {
            string sharedOpaqueDir = Path.Combine(ObjectRoot, "sod");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            string writtenContent = "content";
            FileArtifact source = CreateSourceFile(sharedOpaqueDirPath);
            File.WriteAllText(source.Path.ToString(Context.PathTable), writtenContent);

            // A rewrite with no readers is always allowed, so the content actually doesn't matter here.    
            var writerBuilder = CreateWriter(writtenContent, source);
            var writer = SchedulePipBuilder(writerBuilder);

            // Run should succeed
            RunScheduler().AssertCacheMiss(writer.Process.PipId);
            // Double check the same content rewrite was detected and allowed
            AssertVerboseEventLogged(LogEventId.AllowedRewriteOnUndeclaredFile);

            // Run again. We should get a cache hit
            RunScheduler().AssertCacheHit(writer.Process.PipId);
            // Double check the same content rewrite was detected and allowed
            AssertVerboseEventLogged(LogEventId.AllowedRewriteOnUndeclaredFile);
        }

        [Fact]
        public void SameContentReadersAreAllowedWithAnyOrdering()
        {
            string sharedOpaqueDir = Path.Combine(ObjectRoot, "sod");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);
            string writtenContent = "content";

            FileArtifact source = CreateSourceFile(sharedOpaqueDirPath);
            File.WriteAllText(source.Path.ToString(Context.PathTable), writtenContent);

            // A reader before the writer
            var beforeReader = SchedulePipBuilder(CreateReader(source));

            // The writer
            var writerBuilder = CreateWriter(writtenContent, source);
            writerBuilder.AddInputFile(beforeReader.ProcessOutputs.GetOutputFiles().Single());
            var writer = SchedulePipBuilder(writerBuilder);

            // A reader after the writer
            var afterReaderBuilder = CreateReader(source);
            afterReaderBuilder.AddInputDirectory(writer.ProcessOutputs.GetOutputDirectories().Single().Root);

            // An unordered reader
            SchedulePipBuilder(CreateReader(source));

            // Run should succeed
            RunScheduler().AssertSuccess();
            // Double check the same content rewrite was detected and allowed
            AssertVerboseEventLogged(LogEventId.AllowedRewriteOnUndeclaredFile);
        }

        [Fact]
        public void DifferentContentIsAllowedWhenSafe()
        {
            // Ordered readers on a different-content rewrite should be allowed
            string sharedOpaqueDir = Path.Combine(ObjectRoot, "sod");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            FileArtifact source = CreateSourceFile(sharedOpaqueDirPath);
            File.WriteAllText(source.Path.ToString(Context.PathTable), "content");

            // A reader before the writer
            var beforeReader = SchedulePipBuilder(CreateReader(source));

            // The writer writes different content
            var writerBuilder = CreateWriter("different content", source);
            writerBuilder.AddInputFile(beforeReader.ProcessOutputs.GetOutputFiles().Single());
            var writer = SchedulePipBuilder(writerBuilder);

            // A reader after the writer
            var afterReaderBuilder = CreateReader(source);
            afterReaderBuilder.AddInputDirectory(writer.ProcessOutputs.GetOutputDirectories().Single().Root);
            SchedulePipBuilder(afterReaderBuilder);

            // Run should succeed. All readers are guaranteed to see the same content across the build.
            RunScheduler().AssertSuccess();
            // Double check the same content rewrite was detected and allowed
            AssertVerboseEventLogged(LogEventId.AllowedRewriteOnUndeclaredFile);
        }

        [Fact]
        public void NonExistentAllowedSourceReadIsASafeRewrite()
        {
            string sharedOpaqueDir = Path.Combine(ObjectRoot, "sod");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            FileArtifact source = FileArtifact.CreateSourceFile(sharedOpaqueDirPath.Combine(Context.PathTable, "source.txt")); 

            // A reader before the writer. The reader tries to read a non-existent file, which gets classified as an undeclared source read
            var reader = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(source, doNotInfer:true), // non-existent file
                Operation.WriteFile(CreateOutputFileArtifact()) // dummy output
            });

            reader.Options |= Process.Options.AllowUndeclaredSourceReads;
            reader.RewritePolicy = RewritePolicy.SafeSourceRewritesAreAllowed;

            var beforeReader = SchedulePipBuilder(reader);

            // The writer writes and delete the file
            var sourceAsOutput = source.CreateNextWrittenVersion();
            var writerBuilder = CreatePipBuilder(new Operation[]
            {
                Operation.WriteFile(sourceAsOutput, doNotInfer: true),
                Operation.DeleteFile(sourceAsOutput, doNotInfer: true),
            });

            writerBuilder.Options |= Process.Options.AllowUndeclaredSourceReads;
            writerBuilder.RewritePolicy = RewritePolicy.SafeSourceRewritesAreAllowed;
            writerBuilder.AddOutputDirectory(sharedOpaqueDirPath, kind: SealDirectoryKind.SharedOpaque);

            writerBuilder.AddInputFile(beforeReader.ProcessOutputs.GetOutputFiles().Single());
            var writer = SchedulePipBuilder(writerBuilder);

            // An unordered reader
            var unorderedReader = SchedulePipBuilder(CreateReader(source));

            // Run should succeed. All readers are guaranteed to see the same content across the build.
            // Make sure the unordered reader runs after the writer just to avoid write locks.
            RunScheduler(constraintExecutionOrder: new[] { ((Pip)writer.Process, (Pip)unorderedReader.Process) }).AssertSuccess();
            // Double check the same content rewrite was detected and allowed
            AssertVerboseEventLogged(LogEventId.AllowedRewriteOnUndeclaredFile);
        }

        [Fact]
        public void RacyReadersOnDifferentContentAreBlocked()
        {
            string sharedOpaqueDir = Path.Combine(ObjectRoot, "sod");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            FileArtifact source = CreateSourceFile(sharedOpaqueDirPath);
            File.WriteAllText(source.Path.ToString(Context.PathTable), "content");

            // The writer writes different content
            var writerBuilder = CreateWriter("different content", source);
            var writer = SchedulePipBuilder(writerBuilder);

            // An unordered reader
            var reader = SchedulePipBuilder(CreateReader(source));

            // Run should fail because read content is not guaranteed to be consistent
            // We pin the order at execution time since otherwise the read may fail if the writer is locking the file
            RunScheduler(constraintExecutionOrder: new List<(Pip, Pip)> { (reader.Process, writer.Process) }).AssertFailure();
            AssertVerboseEventLogged(LogEventId.DisallowedRewriteOnUndeclaredFile);
            AssertErrorEventLogged(LogEventId.DependencyViolationWriteInUndeclaredSourceRead);
        }

        /// <summary>
        /// Enabling undeclared source reads interplays with same content double writes: the second writer sees the first write before the second write takes place and the sandbox
        /// signals that as a violation based on file existence. We should be able to recognize this case and ignore the sandbox bound violation.
        /// </summary>
        [Fact]
        public void SameContentDoubleWriteIsAllowedWhenUndeclaredSourceReadsAreEnabled()
        {
            string sharedOpaqueDir = Path.Combine(ObjectRoot, "sod");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            var output = CreateOutputFileArtifact(sharedOpaqueDir);
            var content = "content";

            // Writes a file under a shared opaque
            var writerBuilder1 = CreatePipBuilder(new Operation[]
            {
                Operation.WriteFile(output, content: content, doNotInfer: true) 
            });

            writerBuilder1.Options |= Process.Options.AllowUndeclaredSourceReads;
            writerBuilder1.RewritePolicy = RewritePolicy.DefaultSafe;
            writerBuilder1.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);

            var writer1 = SchedulePipBuilder(writerBuilder1);

            // Writes the same file (with the same content) to a shared opaque
            var writerBuilder2 = CreatePipBuilder(new Operation[]
            {
                // Write implies append, so make sure we delete it first to write the same content
                Operation.DeleteFile(output, doNotInfer: true),
                Operation.WriteFile(output, content: content, doNotInfer: true)
            });

            writerBuilder2.Options |= Process.Options.AllowUndeclaredSourceReads;
            writerBuilder2.RewritePolicy = RewritePolicy.DefaultSafe;
            writerBuilder2.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);

            var writer2 = SchedulePipBuilder(writerBuilder2);

            RunScheduler(constraintExecutionOrder: new List<(Pip, Pip)> { (writer1.Process, writer2.Process) }).AssertSuccess();
        }

        [Theory]
        [InlineData(true)]  // read before write
        [InlineData(false)] // write without reading first
        public void OriginalContentLostIsFlagged(bool readBeforeWrite)
        {
            string sharedOpaqueDir = Path.Combine(ObjectRoot, "sod");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            FileArtifact source = CreateSourceFile(sharedOpaqueDirPath);
            File.WriteAllText(source.Path.ToString(Context.PathTable), "content");

            ProcessBuilder pipBuilder;
            if (readBeforeWrite)
            {
                // The writer reads before writing.
                pipBuilder = CreateReaderAndWriter(source, readAndWrite: true);
            }
            else
            {
                // The writer writes without reading first.
                pipBuilder = CreateWriter("content", source);
            }
            var pip = SchedulePipBuilder(pipBuilder);

            // Run should succeed. All readers are guaranteed to see the same content across the build.
            RunScheduler().AssertSuccess();

            // Content lost is logged depending on whether the pip read before writing.
            if (readBeforeWrite)
            {
                AssertVerboseEventLogged(LogEventId.SourceRewrittenOriginalContentLost, count: 1);
            }
            else
            {
                AssertVerboseEventLogged(LogEventId.SourceRewrittenOriginalContentLost, count: 0);
            }
                
        }

        [Fact]
        public void OriginalContentLostIsNotFlaggedAfterRewrite()
        {
            string sharedOpaqueDir = Path.Combine(ObjectRoot, "sod");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            FileArtifact source = CreateSourceFile(sharedOpaqueDirPath);
            File.WriteAllText(source.Path.ToString(Context.PathTable), "content");

            // Schedule a writer that rewrites the content of the source file and only after that reads it.
            ProcessBuilder pipBuilder = CreateReaderAndWriter(source, readAndWrite: false);
            var pip = SchedulePipBuilder(pipBuilder);

            // Run should succeed. All readers are guaranteed to see the same content across the build.
            RunScheduler().AssertSuccess();
            // Double check the same content rewrite was detected and allowed
            AssertVerboseEventLogged(LogEventId.AllowedRewriteOnUndeclaredFile);

            // Content lost is not logged: the pip read the file after the rewrite.
            AssertVerboseEventLogged(LogEventId.SourceRewrittenOriginalContentLost, count: 0);
        }

        private ProcessBuilder CreateWriter(string rewrittenContent, FileArtifact source)
        {
            var writer = CreatePipBuilder(new Operation[]
            {
                // Operation.WriteFile appends content, so delete it first to guarantee we are writing the specified content
                Operation.DeleteFile(source, doNotInfer: true),
                Operation.WriteFile(source, content: rewrittenContent, doNotInfer: true)
            });

            writer.Options |= Process.Options.AllowUndeclaredSourceReads;
            writer.RewritePolicy = RewritePolicy.SafeSourceRewritesAreAllowed;

            writer.AddOutputDirectory(source.Path.GetParent(Context.PathTable), SealDirectoryKind.SharedOpaque);

            return writer;
        }

        private ProcessBuilder CreateReader(FileArtifact source)
        {
            var reader = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(source, doNotInfer:true),
                Operation.WriteFile(CreateOutputFileArtifact()) // dummy output
            });

            reader.Options |= Process.Options.AllowUndeclaredSourceReads;
            reader.RewritePolicy = RewritePolicy.SafeSourceRewritesAreAllowed;

            return reader;
        }

        private ProcessBuilder CreateReaderAndWriter(FileArtifact source, bool readAndWrite)
        {
            Operation[] operations = [
                (readAndWrite ? Operation.ReadFile(source, doNotInfer: true) : Operation.WriteFile(source, doNotInfer: true)),
                (readAndWrite ? Operation.WriteFile(source, doNotInfer: true) : Operation.ReadFile(source, doNotInfer: true))
            ];

            var writer = CreatePipBuilder(operations);

            writer.Options |= Process.Options.AllowUndeclaredSourceReads;
            writer.RewritePolicy = RewritePolicy.SafeSourceRewritesAreAllowed;

            writer.AddOutputDirectory(source.Path.GetParent(Context.PathTable), SealDirectoryKind.SharedOpaque);

            return writer;
        }
    }
}

