// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Ipc;
using BuildXL.Ipc.Common;
using BuildXL.Ipc.ExternalApi;
using BuildXL.Ipc.ExternalApi.Commands;
using BuildXL.Ipc.Interfaces;
using BuildXL.Storage;
using BuildXL.Storage.Fingerprints;
using BuildXL.Tracing.CloudBuild;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.CLI;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.Drop.WebApi;
using Test.BuildXL.TestUtilities.Xunit;
using Tool.DropDaemon;
using Tool.ServicePipDaemon;
using Xunit;
using Xunit.Abstractions;

namespace Test.Tool.DropDaemon
{
    public sealed class DropOperationTests : BuildXL.TestUtilities.Xunit.XunitBuildXLTest
    {
        public enum DropOp { Create, Finalize }

        public DropOperationTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TestCreate(bool shouldSucceed)
        {
            var dropClient = new MockDropClient(createSucceeds: shouldSucceed);
            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                var dropName = GetDropFullName(dropConfig);
                AssertRpcResult(shouldSucceed, daemon.Create(dropName));
                AssertDequeueEtwEvent(etwListener, shouldSucceed, EventKind.DropCreation);
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TestFinalize(bool shouldSucceed)
        {
            var dropClient = new MockDropClient(finalizeSucceeds: shouldSucceed);
            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                var dropName = GetDropFullName(dropConfig);
                daemon.Create(dropName);     // We can only finalize if we created
                AssertDequeueEtwEvent(etwListener, true, EventKind.DropCreation);

                AssertRpcResult(shouldSucceed, daemon.Finalize());
                AssertDequeueEtwEvent(etwListener, shouldSucceed, EventKind.DropFinalization);
            });
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        // If 'create' fails, finalize should not be called and the daemon just returns success (so that's why there is no false-false case).
        [InlineData(false, true)]
        public void TestCreateFinalize(bool shouldCreateSucceed, bool shouldFinalizeSucceed)
        {
            var dropClient = new MockDropClient(createSucceeds: shouldCreateSucceed, finalizeSucceeds: shouldFinalizeSucceed);
            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                var dropName = GetDropFullName(dropConfig);
                var rpcResult = daemon.Create(dropName);
                AssertRpcResult(shouldCreateSucceed, rpcResult);
                rpcResult = daemon.Finalize();
                AssertRpcResult(shouldFinalizeSucceed, rpcResult);

                // There should always be an etw event for create, but the corresponding finalize event
                // should only be present if create was successful.
                AssertDequeueEtwEvent(etwListener, shouldCreateSucceed, EventKind.DropCreation);
                if (shouldCreateSucceed)
                {
                    AssertDequeueEtwEvent(etwListener, shouldFinalizeSucceed, EventKind.DropFinalization);
                }
                else
                {
                    Assert.True(etwListener.IsEmpty);
                }
            });
        }

        [Fact]
        public void TestNonCreatorCantFinalize()
        {
            var dropClient = new MockDropClient(createSucceeds: true, finalizeSucceeds: false);
            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                // This daemon is not the creator of the drop so it can't finalize it
                AssertRpcResult(false, daemon.Finalize());
                Assert.True(etwListener.IsEmpty);
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TestUrlAndErrorMessageFields(bool shouldSucceed)
        {
            var url = "xya://drop.url";
            var errorMessage = shouldSucceed ? string.Empty : "err";
            var createFunc = shouldSucceed
                ? new MockDropClient.CreateDelegate(() => Task.FromResult(new DropItem()))
                : new MockDropClient.CreateDelegate(() => MockDropClient.FailTask<DropItem>(errorMessage));
            var dropClient = new MockDropClient(dropUrl: url, createFunc: createFunc);
            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                var dropName = GetDropFullName(dropConfig);
                var rpcResult = daemon.Create(dropName);
                AssertRpcResult(shouldSucceed, rpcResult);
                var dropEvent = AssertDequeueEtwEvent(etwListener, shouldSucceed, EventKind.DropCreation);
                Assert.Equal(url, dropEvent.DropUrl);
                Assert.True(dropEvent.ErrorMessage.Contains(errorMessage));
            });
        }

        [Fact]
        public void TestFailingWithDropErrorDoesNotThrow()
        {
            var dropClient = GetFailingMockDropClient(() => new DropServiceException("injected drop service failure"));
            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                var dropName = GetDropFullName(dropConfig);

                // create
                {
                    var rpcResult = daemon.Create(dropName);
                    AssertRpcResult(shouldSucceed: false, rpcResult: rpcResult);
                    AssertDequeueEtwEvent(etwListener, succeeded: false, kind: EventKind.DropCreation);
                }

                // add file
                {
                    var rpcResult = daemon.AddFileAsync(new DropItemForFile(dropName, "file.txt")).Result;
                    AssertRpcResult(shouldSucceed: false, rpcResult: rpcResult);
                }
            });

            // Finalize is only called if create was successful, so we need a new daemon that does not fail create call.
            dropClient = GetFailingMockDropClient(
                createExceptionFactory: null,
                addExceptionFactory: null,
                finalizeExceptionFactory: () => new DropServiceException("injected drop service failure"));
            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                var dropName = GetDropFullName(dropConfig);
                // create
                {
                    var rpcResult = daemon.Create(dropName);
                    AssertRpcResult(shouldSucceed: true, rpcResult: rpcResult);
                    AssertDequeueEtwEvent(etwListener, succeeded: true, kind: EventKind.DropCreation);
                }
                // finalize
                {
                    var rpcResult = daemon.Finalize();
                    AssertRpcResult(shouldSucceed: false, rpcResult: rpcResult);
                    AssertDequeueEtwEvent(etwListener, succeeded: false, kind: EventKind.DropFinalization);
                }
            });
        }

        [Fact]
        public void TestFailingWithGenericErrorThrows()
        {
            const string ExceptionMessage = "injected generic failure";
            var dropClient = GetFailingMockDropClient(() => new Exception(ExceptionMessage));
            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                var dropName = GetDropFullName(dropConfig);

                // create
                {
                    Assert.Throws<Exception>(() => daemon.Create(dropName));

                    // etw event must nevertheless be received
                    AssertDequeueEtwEvent(etwListener, succeeded: false, kind: EventKind.DropCreation);
                }

                // add file
                {
                    Assert.Throws<Exception>(() => daemon.AddFileAsync(new DropItemForFile(dropName, "file.txt")).GetAwaiter().GetResult());
                }


            });

            // Finalize is only called if create was successful, so we need a new daemon that does not fail create call.
            dropClient = GetFailingMockDropClient(
                createExceptionFactory: null,
                addExceptionFactory: null,
                finalizeExceptionFactory: () => new Exception(ExceptionMessage));
            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                var dropName = GetDropFullName(dropConfig);
                // create
                {
                    var rpcResult = daemon.Create(dropName);
                    AssertRpcResult(shouldSucceed: true, rpcResult: rpcResult);
                    AssertDequeueEtwEvent(etwListener, succeeded: true, kind: EventKind.DropCreation);
                }

                // finalize
                {
                    // due to SafeWhenAll, we will be returning an AggregateException
                    var aggregateException = Assert.Throws<AggregateException>(() => daemon.Finalize());
                    Assert.Equal(1, aggregateException.InnerExceptions.Count);
                    Assert.Equal(ExceptionMessage, aggregateException.InnerExceptions[0].Message);

                    // etw event must nevertheless be received
                    AssertDequeueEtwEvent(etwListener, succeeded: false, kind: EventKind.DropFinalization);
                }
            });
        }

        [Fact]
        public void TestMultipleFinalizationBehavior()
        {
            var dropClient = new MockDropClient(createSucceeds: true, finalizeSucceeds: true);
            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                var dropName = GetDropFullName(dropConfig);

                daemon.Create(dropName);
                AssertDequeueEtwEvent(etwListener, succeeded: true, kind: EventKind.DropCreation);

                AssertRpcResult(true, daemon.Finalize());
                AssertDequeueEtwEvent(etwListener, succeeded: true, kind: EventKind.DropFinalization);

                // Subsequent finalizations are ignored by the daemon 
                AssertRpcResult(false, daemon.Finalize());
                AssertRpcResult(false, daemon.Finalize());
                Assert.True(etwListener.IsEmpty);            // We failed by design, there's no ETW event
            });
        }

        [Fact]
        public void FinalizeIsCalledOnStop()
        {
            var dropClient = new MockDropClient(createSucceeds: true, finalizeSucceeds: true);
            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                var dropName = GetDropFullName(dropConfig);

                daemon.Create(dropName);
                AssertDequeueEtwEvent(etwListener, succeeded: true, kind: EventKind.DropCreation);

                daemon.RequestStop();

                // Stop called for finalization
                AssertDequeueEtwEvent(etwListener, succeeded: true, kind: EventKind.DropFinalization);

                AssertRpcResult(false, daemon.Finalize());
                Assert.True(etwListener.IsEmpty);            // We failed by design, there's no ETW event
            });
        }

        [Fact]
        public void FinalizeOnStopAfterNormalFinalizationIsOk()
        {
            var dropClient = new MockDropClient(createSucceeds: true, finalizeSucceeds: true);
            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                var dropName = GetDropFullName(dropConfig);

                daemon.Create(dropName);
                etwListener.DequeueDropEvent(); // Dequeue create

                AssertRpcResult(true, daemon.Finalize());
                AssertDequeueEtwEvent(etwListener, succeeded: true, kind: EventKind.DropFinalization);

                daemon.RequestStop();

                // Stop called for finalization, 
                // but nothing happens because finalize was called before 
                Assert.True(etwListener.IsEmpty);
            });
        }

        [Fact]
        public void TestFinalizeOnStopErrorsAreQueued()
        {
            var dropClient = new MockDropClient(createSucceeds: true, finalizeSucceeds: false);
            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                var dropName = GetDropFullName(dropConfig);
                daemon.Create(dropName);
                etwListener.DequeueDropEvent(); // Dequeue create
                daemon.RequestStop();
                AssertDequeueEtwEvent(etwListener, succeeded: false, kind: EventKind.DropFinalization);
            });
        }


        [Fact]
        public void TestNonCreatorDoesntFinalizeOnStop()
        {
            var dropClient = new MockDropClient(createSucceeds: true, finalizeSucceeds: false);
            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                // We don't call create for this daemon
                daemon.RequestStop();
                Assert.True(etwListener.IsEmpty);
            });
        }

        [Fact]
        public void TestAddFile_AssociateDoesntNeedServer()
        {
            // this client only touches item.BlobIdentifier and returns 'Associated'
            var dropClient = new MockDropClient(addFileFunc: (item) =>
            {
                Assert.NotNull(item.BlobIdentifier);
                return Task.FromResult(AddFileResult.Associated);
            });

            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                var dropName = GetDropFullName(dropConfig);

                var provider = IpcFactory.GetProvider();
                var connStr = provider.CreateNewConnectionString();
                var contentInfo = new FileContentInfo(new ContentHash(HashType.Vso0), length: 123456);

                var client = new Client(provider.GetClient(connStr, new ClientConfig()));
                var addFileItem = new DropItemForBuildXLFile(client, dropName, filePath: "file-which-doesnt-exist.txt", fileId: "23423423:1", fileContentInfo: contentInfo);

                // addfile succeeds without needing BuildXL server nor the file on disk
                IIpcResult result = daemon.AddFileAsync(addFileItem).GetAwaiter().GetResult();
                XAssert.IsTrue(result.Succeeded);

                // calling MaterializeFile fails because no BuildXL server is running
                Assert.Throws<DaemonException>(() => addFileItem.EnsureMaterialized().GetAwaiter().GetResult());
            });
        }

        private const string TestFileContent = "hi";
        private static readonly FileContentInfo TestFileContentInfo = new FileContentInfo(
            ParseContentHash("VSO0:C8DE9915376DBAC9F79AD7888D3C9448BE0F17A0511004F3D4A470F9E94B9F2E00"),
            length: TestFileContent.Length);

        private static ContentHash ParseContentHash(string v)
        {
            ContentHash result;
            XAssert.IsTrue(ContentHash.TryParse(v, out result));
            return result;
        }

        [Fact]
        public void TestAddBuildXLFile_UploadCallsBuildXLServer()
        {
            string fileId = "142342:2";
            string filePath = Path.Combine(TestOutputDirectory, nameof(TestAddBuildXLFile_UploadCallsBuildXLServer) + "-test.txt");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            // this client wants to read the file
            var dropClient = new MockDropClient(addFileFunc: (item) =>
            {
                Assert.NotNull(item.BlobIdentifier);
                var fileInfo = item.EnsureMaterialized().GetAwaiter().GetResult();
                XAssert.IsTrue(fileInfo != null && fileInfo.Exists);
                XAssert.AreEqual(TestFileContent, File.ReadAllText(fileInfo.FullName));
                return Task.FromResult(AddFileResult.UploadedAndAssociated);
            });

            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                var dropName = GetDropFullName(dropConfig);
                var ipcProvider = IpcFactory.GetProvider();
                var ipcExecutor = new LambdaIpcOperationExecutor(op =>
                {
                    var cmd = ReceiveMaterializeFileCmdAndCheckItMatchesFileId(op.Payload, fileId);
                    File.WriteAllText(filePath, TestFileContent);
                    return IpcResult.Success(cmd.RenderResult(true));
                });
                WithIpcServer(
                    ipcProvider,
                    ipcExecutor,
                    new ServerConfig(),
                    (moniker, mockServer) =>
                    {
                        var client = new Client(ipcProvider.GetClient(ipcProvider.RenderConnectionString(moniker), new ClientConfig()));
                        var addFileItem = new DropItemForBuildXLFile(client, dropName, filePath, fileId, fileContentInfo: TestFileContentInfo);

                        // addfile succeeds
                        IIpcResult result = daemon.AddFileAsync(addFileItem).GetAwaiter().GetResult();
                        XAssert.IsTrue(result.Succeeded, result.Payload);
                    });
            });
        }

        [Fact]
        public void TestLazilyMaterializedSymlinkRejected()
        {
            string fileId = "142342:3";
            string filePath = Path.Combine(TestOutputDirectory, nameof(TestLazilyMaterializedSymlinkRejected) + "-test.txt");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            // this client wants to read the file
            var dropClient = new MockDropClient(addFileFunc: (item) =>
            {
                Assert.NotNull(item.BlobIdentifier);
                var ex = Assert.Throws<DaemonException>(() => item.EnsureMaterialized().GetAwaiter().GetResult());

                // rethrowing because that's what a real IDropClient would do (then Daemon is expected to handle it)
                throw ex;
            });

            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                var dropName = GetDropFullName(dropConfig);
                var ipcProvider = IpcFactory.GetProvider();
                var ipcExecutor = new LambdaIpcOperationExecutor(op =>
                {
                    // this mock BuildXL server materializes a regular file, which we will treat as a symlink in this test
                    var cmd = ReceiveMaterializeFileCmdAndCheckItMatchesFileId(op.Payload, fileId);
                    File.WriteAllText(filePath, TestFileContent);
                    return IpcResult.Success(cmd.RenderResult(true));
                });
                WithIpcServer(
                    ipcProvider,
                    ipcExecutor,
                    new ServerConfig(),
                    (moniker, mockServer) =>
                    {
                        var client = new Client(ipcProvider.GetClient(ipcProvider.RenderConnectionString(moniker), new ClientConfig()));
                        var addFileItem = new DropItemForBuildXLFile(
                            symlinkTester: (file) => file == filePath ? true : false,
                            client: client,
                            fullDropName: dropName,
                            filePath: filePath,
                            fileId: fileId,
                            fileContentInfo: TestFileContentInfo);

                        // addfile files
                        IIpcResult result = daemon.AddFileAsync(addFileItem).GetAwaiter().GetResult();
                        XAssert.IsFalse(result.Succeeded, "expected addfile to fail; instead it succeeded and returned payload: " + result.Payload);
                        XAssert.IsTrue(result.Payload.Contains(Statics.MaterializationResultIsSymlinkErrorPrefix));
                    });
            });
        }

        [Fact]
        public void TestDropDaemonOutrightRejectSymlinks()
        {
            // create a regular file that mocks a symlink (because creating symlinks on Windows is difficult?!?)
            var targetFile = Path.Combine(TestOutputDirectory, nameof(TestDropDaemonOutrightRejectSymlinks) + "-test.txt");
            File.WriteAllText(targetFile, "drop symlink test file");

            // check that drop daemon rejects it outright
            var dropClient = new MockDropClient(addFileSucceeds: true);
            WithSetup(dropClient, (daemon, etwListener, dropConfig) =>
            {
                var dropName = GetDropFullName(dropConfig);
                var ipcResult = daemon.AddFileAsync(new DropItemForFile(dropName, targetFile), symlinkTester: (file) => file == targetFile ? true : false).GetAwaiter().GetResult();
                Assert.False(ipcResult.Succeeded, "adding symlink to drop succeeded while it was expected to fail");
                Assert.True(ipcResult.Payload.Contains(global::Tool.DropDaemon.DropDaemon.SymlinkAddErrorMessagePrefix));
            });
        }

        [Theory]
        [InlineData(".*")]              // all files
        [InlineData(".*a\\.txt$")]      // only a.txt
        [InlineData(".*(a|c)\\.txt$")]  // a.txt and c.txt
        [InlineData(".*d\\.txt$")]      // no files
        public void TestAddDirectoryToDropWithFilters(string filter)
        {
            // TestOutputDirectory
            // |- foo <directory>  <- 'uploading' this directory
            //    |- a.txt
            //    |- b.txt
            //    |- bar <directory>
            //       |- c.txt

            string remoteDirectoryPath = "remoteDirectory";
            string fakeDirectoryId = "123:1:12345";
            var directoryPath = Path.Combine(TestOutputDirectory, "foo");

            var files = new List<(string fileName, string remoteFileName)>
            {
                (Path.Combine(directoryPath, "a.txt"), $"{remoteDirectoryPath}/a.txt"),
                (Path.Combine(directoryPath, "b.txt"), $"{remoteDirectoryPath}/b.txt"),
                (Path.Combine(directoryPath, "bar", "c.txt"), $"{remoteDirectoryPath}/bar/c.txt"),
            };

            var dropPaths = new List<string>();
            var expectedDropPaths = new HashSet<string>();
            var regex = new Regex(filter, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            expectedDropPaths.AddRange(files.Where(a => regex.IsMatch(a.fileName)).Select(a => a.remoteFileName));

            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var dropClient = new MockDropClient(addFileFunc: (item) =>
            {
                dropPaths.Add(item.RelativeDropPath);
                return Task.FromResult(AddFileResult.UploadedAndAssociated);
            });

            var ipcProvider = IpcFactory.GetProvider();

            // this lambda mocks BuildXL server receiving 'GetSealedDirectoryContent' API call and returning a response
            var ipcExecutor = new LambdaIpcOperationExecutor(op =>
            {
                var cmd = ReceiveGetSealedDirectoryContentCommandAndCheckItMatchesDirectoryId(op.Payload, fakeDirectoryId);

                // Now 'fake' the response - here we only care about the 'FileName' field.
                // In real life it's not the case, but this is a test and our custom addFileFunc
                // in dropClient simply collects the drop file names.
                var result = files.Select(a => CreateFakeSealedDirectoryFile(a.fileName)).ToList();

                return IpcResult.Success(cmd.RenderResult(result));
            });

            WithIpcServer(
                ipcProvider,
                ipcExecutor,
                new ServerConfig(),
                (moniker, mockServer) =>
                {
                    var bxlApiClient = CreateDummyBxlApiClient(ipcProvider, moniker);
                    WithSetup(
                        dropClient,
                        (daemon, etwListener, dropConfig) =>
                        {
                            var addArtifactsCommand = global::Tool.ServicePipDaemon.ServicePipDaemon.ParseArgs(
                                $"addartifacts --ipcServerMoniker {moniker.Id} --service {dropConfig.Service} --name {dropConfig.Name} --directory {directoryPath} --directoryId {fakeDirectoryId} --directoryDropPath {remoteDirectoryPath} --directoryFilter {filter} --directoryRelativePathReplace ## --directoryFilterUseRelativePath false",
                                new UnixParser());
                            var ipcResult = addArtifactsCommand.Command.ServerAction(addArtifactsCommand, daemon).GetAwaiter().GetResult();

                            XAssert.IsTrue(ipcResult.Succeeded, ipcResult.Payload);
                            XAssert.AreSetsEqual(expectedDropPaths, dropPaths, expectedResult: true);
                        },
                        bxlApiClient);
                    return Task.CompletedTask;
                }).GetAwaiter().GetResult();
        }

        [Fact]
        public void TestAddDirectoryToDropWithEmptyRelativePath()
        {
            string fakeDirectoryId = "123:1:12345";
            var directoryPath = Path.Combine(TestOutputDirectory, "foo");

            var files = new List<(string fileName, string remoteFileName)>
            {
                (Path.Combine(directoryPath, "a.txt"), "a.txt"),
                (Path.Combine(directoryPath, "b.txt"), "b.txt"),
                (Path.Combine(directoryPath, "bar", "c.txt"), "bar/c.txt"),
            };

            var dropPaths = new List<string>();
            var expectedDropPaths = new HashSet<string>();
            var regex = new Regex(".*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            expectedDropPaths.AddRange(files.Where(a => regex.IsMatch(a.fileName)).Select(a => a.remoteFileName));

            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var dropClient = new MockDropClient(addFileFunc: (item) =>
            {
                dropPaths.Add(item.RelativeDropPath);
                return Task.FromResult(AddFileResult.UploadedAndAssociated);
            });

            var ipcProvider = IpcFactory.GetProvider();

            // this lambda mocks BuildXL server receiving 'GetSealedDirectoryContent' API call and returning a response
            var ipcExecutor = new LambdaIpcOperationExecutor(op =>
            {
                var cmd = ReceiveGetSealedDirectoryContentCommandAndCheckItMatchesDirectoryId(op.Payload, fakeDirectoryId);

                // Now 'fake' the response - here we only care about the 'FileName' field.
                // In real life it's not the case, but this is a test and our custom addFileFunc
                // in dropClient simply collects the drop file names.
                var result = files.Select(a => CreateFakeSealedDirectoryFile(a.fileName)).ToList();

                return IpcResult.Success(cmd.RenderResult(result));
            });

            WithIpcServer(
                ipcProvider,
                ipcExecutor,
                new ServerConfig(),
                (moniker, mockServer) =>
                {
                    var bxlApiClient = CreateDummyBxlApiClient(ipcProvider, moniker);
                    WithSetup(
                        dropClient,
                        (daemon, etwListener, dropConfig) =>
                        {
                            var addArtifactsCommand = global::Tool.ServicePipDaemon.ServicePipDaemon.ParseArgs(
                                $"addartifacts --ipcServerMoniker {moniker.Id} --service {dropConfig.Service} --name {dropConfig.Name} --directory {directoryPath} --directoryId {fakeDirectoryId} --directoryDropPath . --directoryFilter .* --directoryRelativePathReplace ## --directoryFilterUseRelativePath false",
                                new UnixParser());
                            var ipcResult = addArtifactsCommand.Command.ServerAction(addArtifactsCommand, daemon).GetAwaiter().GetResult();

                            XAssert.IsTrue(ipcResult.Succeeded, ipcResult.Payload);
                            XAssert.AreSetsEqual(expectedDropPaths, dropPaths, expectedResult: true);
                        },
                        bxlApiClient);
                    return Task.CompletedTask;
                }).GetAwaiter().GetResult();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TestAlwaysFailWhenAddingStaticallyListedFilesWithAbsentFileHash(bool isSourceFile)
        {
            var dropPaths = new List<string>();

            var dropClient = new MockDropClient(addFileFunc: (item) =>
            {
                dropPaths.Add(item.RelativeDropPath);
                return Task.FromResult(AddFileResult.Associated);
            });

            var ipcProvider = IpcFactory.GetProvider();
            var bxlApiClient = CreateDummyBxlApiClient(ipcProvider);

            WithSetup(
                dropClient,
                (daemon, etwListener, dropConfig) =>
                {
                    // only hash and file rewrite count are important here; the rest are just fake values
                    var hash = FileContentInfo.CreateWithUnknownLength(ContentHashingUtilities.CreateSpecialValue(1)).Render();
                    var addArtifactsCommand = global::Tool.ServicePipDaemon.ServicePipDaemon.ParseArgs(
                        $"addartifacts --ipcServerMoniker {daemon.Config.Moniker} --service {dropConfig.Service} --name {dropConfig.Name} --file non-existent-file.txt --dropPath remote-file-name.txt --hash {hash} --fileId 12345:{(isSourceFile ? 0 : 1)}",
                        new UnixParser());
                    var ipcResult = addArtifactsCommand.Command.ServerAction(addArtifactsCommand, daemon).GetAwaiter().GetResult();

                    XAssert.IsTrue(dropPaths.Count == 0);
                    XAssert.IsFalse(ipcResult.Succeeded);
                    XAssert.AreEqual(IpcResultStatus.InvalidInput, ipcResult.ExitCode);
                },
                bxlApiClient);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TestAddingDirectoryContainingFilesWithAbsentFileHash(bool isSourceFile)
        {
            string remoteDirectoryPath = "remoteDirectory";
            string fakeDirectoryId = "123:1:12345";
            var directoryPath = Path.Combine(TestOutputDirectory, "foo");

            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var dropPaths = new List<string>();

            var dropClient = new MockDropClient(addFileFunc: (item) =>
            {
                dropPaths.Add(item.RelativeDropPath);
                return Task.FromResult(AddFileResult.Associated);
            });

            var ipcProvider = IpcFactory.GetProvider();

            // this lambda mocks BuildXL server receiving 'GetSealedDirectoryContent' API call and returning a response
            var ipcExecutor = new LambdaIpcOperationExecutor(op =>
            {
                var cmd = ReceiveGetSealedDirectoryContentCommandAndCheckItMatchesDirectoryId(op.Payload, fakeDirectoryId);

                var file = new SealedDirectoryFile(
                    Path.Combine(directoryPath, "file.txt"),
                    new FileArtifact(new AbsolutePath(1), isSourceFile ? 0 : 1),
                    FileContentInfo.CreateWithUnknownLength(WellKnownContentHashes.AbsentFile));

                return IpcResult.Success(cmd.RenderResult(new List<SealedDirectoryFile> { file }));
            });

            WithIpcServer(
                ipcProvider,
                ipcExecutor,
                new ServerConfig(),
                (moniker, mockServer) =>
                {
                    var bxlApiClient = CreateDummyBxlApiClient(ipcProvider, moniker);
                    WithSetup(
                        dropClient,
                        (daemon, etwListener, dropConfig) =>
                        {
                            var addArtifactsCommand = global::Tool.ServicePipDaemon.ServicePipDaemon.ParseArgs(
                                $"addartifacts --ipcServerMoniker {moniker.Id} --service {dropConfig.Service} --name {dropConfig.Name} --directory {directoryPath} --directoryId {fakeDirectoryId} --directoryDropPath {remoteDirectoryPath} --directoryFilter .* --directoryRelativePathReplace ## --directoryFilterUseRelativePath false",
                                new UnixParser());
                            var ipcResult = addArtifactsCommand.Command.ServerAction(addArtifactsCommand, daemon).GetAwaiter().GetResult();

                            XAssert.IsTrue(dropPaths.Count == 0);

                            // if an absent file is a source file, drop operation should have failed; otherwise, we simply skip it
                            XAssert.AreEqual(!isSourceFile, ipcResult.Succeeded);
                            XAssert.AreEqual(isSourceFile ? IpcResultStatus.InvalidInput : IpcResultStatus.Success, ipcResult.ExitCode);
                        },
                        bxlApiClient);
                    return Task.CompletedTask;
                }).GetAwaiter().GetResult();
        }

        [Theory]
        [InlineData(@"C:\dir\", @"C:\dir\foo.txt", null, null, "foo.txt")]
        [InlineData(@"C:\dir", @"C:\dir\foo.txt", null, null, "foo.txt")]
        [InlineData(@"C:\dir", @"C:\dir\dir2\foo.txt", null, null, @"dir2\foo.txt")]
        [InlineData(@"C:\dir", @"C:\dir\dir2\foo.txt", @"dir3", "", @"dir2\foo.txt")] // no match
        [InlineData(@"C:\dir", @"C:\dir\dir2\foo.txt", @"dir2\", "", @"foo.txt")]
        [InlineData(@"C:\dir", @"C:\dir\dir2\foo.txt", @"dir2", "", @"\foo.txt")]
        [InlineData(@"C:\dir", @"C:\dir\dir2\dir2\foo.txt", @"dir2", "", @"\dir2\foo.txt")] // replacing only the first match
        [InlineData(@"C:\dir", @"C:\dir\Dir2\dir2\foo.txt", @"dir2", "", @"\dir2\foo.txt")] // on Windows, search is case-insensitive
        [InlineData(@"C:\dir", @"C:\dir\dir2\dir2\foo.txt", @"dir2\dir2", "dir3", @"dir3\foo.txt")]
        public void TestGetRelativePath(string root, string filePath, string oldValue, string newValue, string expectedPath)
        {
            var replacementArgs = new global::Tool.DropDaemon.DropDaemon.RelativePathReplacementArguments(oldValue, newValue);
            XAssert.ArePathEqual(expectedPath, global::Tool.DropDaemon.DropDaemon.GetRelativePath(root, filePath, replacementArgs));
        }

        [Fact]
        public void TestFilterDirectoryContent()
        {
            const string DirPath = @"c:\a";
            var files = new List<SealedDirectoryFile>
            {
                new SealedDirectoryFile(@"c:\a\1.txt", FileArtifact.Invalid, FileContentInfo.CreateWithUnknownLength(ContentHashingUtilities.EmptyHash)),
                new SealedDirectoryFile(@"c:\a\dir\foo.txt", FileArtifact.Invalid, FileContentInfo.CreateWithUnknownLength(ContentHashingUtilities.EmptyHash)),
                new SealedDirectoryFile(@"c:\a\foo.txt", FileArtifact.Invalid, FileContentInfo.CreateWithUnknownLength(ContentHashingUtilities.EmptyHash)),
            };

            var regex = new Regex(@".*\.txt", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            var expectedResult = new List<SealedDirectoryFile>
            {
                new SealedDirectoryFile(@"c:\a\1.txt", FileArtifact.Invalid, FileContentInfo.CreateWithUnknownLength(ContentHashingUtilities.EmptyHash)),
                new SealedDirectoryFile(@"c:\a\dir\foo.txt", FileArtifact.Invalid, FileContentInfo.CreateWithUnknownLength(ContentHashingUtilities.EmptyHash)),
                new SealedDirectoryFile(@"c:\a\foo.txt", FileArtifact.Invalid, FileContentInfo.CreateWithUnknownLength(ContentHashingUtilities.EmptyHash)),
            };

            var result = global::Tool.DropDaemon.DropDaemon.FilterDirectoryContent(DirPath, files, regex, applyFilterToRelativePath: false);
            XAssert.AreSetsEqual(expectedResult, result, true);
            result = global::Tool.DropDaemon.DropDaemon.FilterDirectoryContent(DirPath, files, regex, applyFilterToRelativePath: true);
            XAssert.AreSetsEqual(expectedResult, result, true);

            regex = new Regex(@"c:\\a\\1\.txt", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            expectedResult = new List<SealedDirectoryFile>
            {
                new SealedDirectoryFile(@"c:\a\1.txt", FileArtifact.Invalid, FileContentInfo.CreateWithUnknownLength(ContentHashingUtilities.EmptyHash)),
            };

            result = global::Tool.DropDaemon.DropDaemon.FilterDirectoryContent(DirPath, files, regex, applyFilterToRelativePath: false);
            XAssert.AreSetsEqual(expectedResult, result, true);
            result = global::Tool.DropDaemon.DropDaemon.FilterDirectoryContent(DirPath, files, regex, applyFilterToRelativePath: true);
            XAssert.AreSetsEqual(expectedResult, result, false);

            regex = new Regex(@"\Gfoo.txt", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            expectedResult = new List<SealedDirectoryFile>
            {
                new SealedDirectoryFile(@"c:\a\foo.txt", FileArtifact.Invalid, FileContentInfo.CreateWithUnknownLength(ContentHashingUtilities.EmptyHash)),
            };

            result = global::Tool.DropDaemon.DropDaemon.FilterDirectoryContent(DirPath, files, regex, applyFilterToRelativePath: false);
            XAssert.AreSetsEqual(expectedResult, result, false);
            result = global::Tool.DropDaemon.DropDaemon.FilterDirectoryContent(DirPath, files, regex, applyFilterToRelativePath: true);
            XAssert.AreSetsEqual(expectedResult, result, true);
        }

        [Theory]
        [InlineData(".", null, null, "a", "dir/b", "dir/dir/c", "dir/dir2/d", "dir3/e")]
        [InlineData("x", null, null, "x/a", "x/dir/b", "x/dir/dir/c", "x/dir/dir2/d", "x/dir3/e")]
        [InlineData("x", "x", "", "x/a", "x/dir/b", "x/dir/dir/c", "x/dir/dir2/d", "x/dir3/e")] // replacement is not applied to the dropPath
        [InlineData("x", @"dir\", "", "x/a", "x/b", "x/dir/c", "x/dir2/d", "x/dir3/e")]
        [InlineData(".", @"dir\", "", "a", "b", "dir/c", "dir2/d", "dir3/e")]
        [InlineData("x", @"dir\dir2", "newDir", "x/a", "x/dir/b", "x/dir/dir/c", "x/newDir/d", "x/dir3/e")]
        public void TestAdddingDirectoryToDropWithSpecifiedRelativePathReplacement(string dropPath, string replaceOldValue, string replaceNewValue, params string[] expectedFiles)
        {
            /*
             * Directory content:
             *  a
             *  dir\b
             *  dir\dir\c
             *  dir\dir2\d
             *  dir3\e
             */

            var expectedDropPaths = new HashSet<string>(expectedFiles);
            var dropPaths = new List<string>();
            var fakeDirectoryId = "123:1:12345";
            var directoryPath = Path.Combine(TestOutputDirectory, "foo");
            var files = new List<string>
            {
                Path.Combine(directoryPath, "a"),
                Path.Combine(directoryPath, "dir","b"),
                Path.Combine(directoryPath, "dir","dir", "c"),
                Path.Combine(directoryPath, "dir","dir2", "d"),
                Path.Combine(directoryPath, "dir3","e"),
            };

            var dropClient = new MockDropClient(addFileFunc: (item) =>
            {
                dropPaths.Add(item.RelativeDropPath);
                return Task.FromResult(AddFileResult.UploadedAndAssociated);
            });

            var ipcProvider = IpcFactory.GetProvider();

            // this lambda mocks BuildXL server receiving 'GetSealedDirectoryContent' API call and returning a response
            var ipcExecutor = new LambdaIpcOperationExecutor(op =>
            {
                var cmd = ReceiveGetSealedDirectoryContentCommandAndCheckItMatchesDirectoryId(op.Payload, fakeDirectoryId);

                // Now 'fake' the response - here we only care about the 'FileName' field.
                // In real life it's not the case, but this is a test and our custom addFileFunc
                // in dropClient simply collects the drop file names.
                var result = files.Select(file => CreateFakeSealedDirectoryFile(file)).ToList();

                return IpcResult.Success(cmd.RenderResult(result));
            });

            WithIpcServer(
                ipcProvider,
                ipcExecutor,
                new ServerConfig(),
                (moniker, mockServer) =>
                {
                    var bxlApiClient = CreateDummyBxlApiClient(ipcProvider, moniker);
                    WithSetup(
                        dropClient,
                        (daemon, etwListener, dropConfig) =>
                        {
                            var addArtifactsCommand = global::Tool.ServicePipDaemon.ServicePipDaemon.ParseArgs(
                                $"addartifacts --ipcServerMoniker {moniker.Id} --service {dropConfig.Service} --name {dropConfig.Name} --directory {directoryPath} --directoryId {fakeDirectoryId} --directoryDropPath {dropPath} --directoryFilter .* --directoryRelativePathReplace {serializeReplaceArgument(replaceOldValue, replaceNewValue)} --directoryFilterUseRelativePath false",
                                new UnixParser());
                            var ipcResult = addArtifactsCommand.Command.ServerAction(addArtifactsCommand, daemon).GetAwaiter().GetResult();

                            XAssert.IsTrue(ipcResult.Succeeded, ipcResult.Payload);
                            XAssert.AreSetsEqual(expectedDropPaths, dropPaths, expectedResult: true);
                        },
                        bxlApiClient);
                    return Task.CompletedTask;
                }).GetAwaiter().GetResult();

            string serializeReplaceArgument(string oldValue, string newValue)
            {
                if (oldValue != null || newValue != null)
                {
                    return $"#{oldValue}#{newValue}#";
                }

                return "##";
            }
        }

        private static SealedDirectoryFile CreateFakeSealedDirectoryFile(string fileName)
        {
            return new SealedDirectoryFile(fileName, new FileArtifact(new AbsolutePath(1), 1), FileContentInfo.CreateWithUnknownLength(ContentHash.Random()));
        }

        private static Client CreateDummyBxlApiClient(IIpcProvider ipcProvider, IpcMoniker moniker)
        {
            return new Client(new MockClient(ipcProvider.GetClient(ipcProvider.RenderConnectionString(moniker), new ClientConfig())));
        }

        private static Client CreateDummyBxlApiClient(IIpcProvider ipcProvider)
        {
            return new Client(new MockClient(ipcProvider.GetClient(ipcProvider.CreateNewConnectionString(), new ClientConfig())));
        }

        private MaterializeFileCommand ReceiveMaterializeFileCmdAndCheckItMatchesFileId(string operationPayload, string expectedFileId)
        {
            var cmd = global::BuildXL.Ipc.ExternalApi.Commands.Command.Deserialize(operationPayload);
            XAssert.AreEqual(typeof(MaterializeFileCommand), cmd.GetType());
            var materializeFileCmd = (MaterializeFileCommand)cmd;
            XAssert.AreEqual(expectedFileId, FileId.ToString(materializeFileCmd.File));
            return materializeFileCmd;
        }

        private GetSealedDirectoryContentCommand ReceiveGetSealedDirectoryContentCommandAndCheckItMatchesDirectoryId(string payload, string expectedDirectoryId)
        {
            var cmd = global::BuildXL.Ipc.ExternalApi.Commands.Command.Deserialize(payload);
            XAssert.AreEqual(typeof(GetSealedDirectoryContentCommand), cmd.GetType());
            var getSealedDirectoryCmd = (GetSealedDirectoryContentCommand)cmd;
            XAssert.AreEqual(expectedDirectoryId, DirectoryId.ToString(getSealedDirectoryCmd.Directory));
            return getSealedDirectoryCmd;
        }

        private MockDropClient GetFailingMockDropClient(Func<Exception> exceptionFactory)
         => GetFailingMockDropClient(
            createExceptionFactory: exceptionFactory,
            addExceptionFactory: exceptionFactory,
            finalizeExceptionFactory: exceptionFactory);

        private MockDropClient GetFailingMockDropClient(Func<Exception> createExceptionFactory, Func<Exception> addExceptionFactory, Func<Exception> finalizeExceptionFactory)
        {
            return new MockDropClient(
                createFunc: () => createExceptionFactory != null ? MockDropClient.CreateFailingTask<DropItem>(createExceptionFactory) : Task.FromResult(new DropItem()),
                addFileFunc: (item) => addExceptionFactory != null ? MockDropClient.CreateFailingTask<AddFileResult>(addExceptionFactory) : Task.FromResult(AddFileResult.Associated),
                finalizeFunc: () => finalizeExceptionFactory != null ? MockDropClient.CreateFailingTask<FinalizeResult>(finalizeExceptionFactory) : Task.FromResult(new FinalizeResult()));
        }

        private DropOperationBaseEvent AssertDequeueEtwEvent(DropEtwListener etwListener, bool succeeded, EventKind kind)
        {
            var dropEvent = etwListener.DequeueDropEvent();
            Assert.Equal(succeeded, dropEvent.Succeeded);
            Assert.Equal(kind, dropEvent.Kind);
            return dropEvent;
        }

        private void AssertRpcResult(bool shouldSucceed, IIpcResult rpcResult)
        {
            Assert.NotNull(rpcResult);
            Assert.Equal(shouldSucceed, rpcResult.Succeeded);
        }


        /// <remarks>
        /// If an apiClient is not passed (i.e., null by default), we create a new Client that returns success for any bool command called.
        /// </remarks>
        private void WithSetup(IDropClient dropClient, Action<global::Tool.DropDaemon.DropDaemon, DropEtwListener, DropConfig> action, Client apiClient = null)
        {
            var etwListener = ConfigureEtwLogging();
            string moniker = ServicePipDaemon.IpcProvider.RenderConnectionString(IpcMoniker.CreateNew());
            var daemonConfig = new DaemonConfig(VoidLogger.Instance, moniker: moniker, enableCloudBuildIntegration: false);
            var dropConfig = new DropConfig("test", new Uri("file://xyz"));
            var dropServiceConfig = new DropServiceConfig();
            if (apiClient == null)
            {
                apiClient = new Client(new MockClient(ipcOperation => IpcResult.Success("true")));
            }
            var daemon = new global::Tool.DropDaemon.DropDaemon(UnixParser.Instance, daemonConfig, dropServiceConfig, client: apiClient);
            daemon.RegisterDropClientForTesting(dropConfig, dropClient);
            action(daemon, etwListener, dropConfig);
        }

        private string GetDropFullName(DropConfig config) => global::Tool.DropDaemon.DropDaemon.FullyQualifiedDropName(config);

        private DropEtwListener ConfigureEtwLogging()
        {
            return new DropEtwListener();
        }

        internal class MockClient : IClient
        {
            public IClient InternalClient { get; set; }
            public Task Completion => Task.CompletedTask;

            public IClientConfig Config { get; set; } = new ClientConfig();
            public Func<IIpcOperation, IIpcResult> SendFn { get; set; }

            public void Dispose() { }

            public void RequestStop() { }

            public MockClient(IClient client)
            {
                InternalClient = client;
                SendFn = ipcOperation => IpcResult.Success("true");
            }

            public MockClient(Func<IIpcOperation, IIpcResult> sendFn)
            {
                InternalClient = null;
                SendFn = sendFn;
            }

            Task<IIpcResult> IClient.Send(IIpcOperation operation)
            {
                Contract.Requires(operation != null);
                if (InternalClient != null)
                {
                    if (global::BuildXL.Ipc.ExternalApi.Commands.Command.Deserialize(operation.Payload) is RegisterFilesForBuildManifestCommand)
                    {
                        // Override for RegisterFileForBuildManifestCommand (Always true)
                        return Task.FromResult(SendFn(operation));
                    }
                    else
                    {
                        return InternalClient.Send(operation);
                    }
                }
                return Task.FromResult(SendFn(operation));
            }
        }
    }
}
