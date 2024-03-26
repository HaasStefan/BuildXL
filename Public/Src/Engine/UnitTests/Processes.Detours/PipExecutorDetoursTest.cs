// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BuildXL.Native.IO;
using BuildXL.Native.IO.Windows;
using BuildXL.Pips;
using BuildXL.ProcessPipExecutor;
using BuildXL.Pips.Operations;
using BuildXL.Processes;
using BuildXL.Utilities;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Configuration.Mutable;
using Test.BuildXL.TestUtilities;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;
using AssemblyHelper = BuildXL.Utilities.Core.AssemblyHelper;
using ProcessesLogEventId = BuildXL.Processes.Tracing.LogEventId;
using Microsoft.Win32.SafeHandles;

#pragma warning disable AsyncFixer02

namespace Test.BuildXL.Processes.Detours
{
    // TODO: This class tests symlink / junction behavior from before /unsafe_IgnoreFullReparsePointResolving was introduced and makes
    //       sure we don't break compatibility for our partners relying on this. Once directory symlink support is the default,
    //       these tests need to be adjusted.

    [TestClassIfSupported(requiresWindowsBasedOperatingSystem: true)]
    public sealed partial class SandboxedProcessPipExecutorTest
    {
        private const string DetoursTestsExe = "DetoursTests.exe";
        private const int ErrorPrivilegeNotHeld = 1314;
        private const string ExtraFileNameInDirectory = "foo.txt";

        private bool IsNotEnoughPrivilegesError(SandboxedProcessPipExecutionResult result)
        {
            if (result.Status == SandboxedProcessPipExecutionStatus.ExecutionFailed && result.ExitCode == ErrorPrivilegeNotHeld)
            {
                SetExpectedFailures(1, 0);
                return true;
            }

            return false;
        }

        private static void EstablishJunction(string junctionPath, string targetPath)
        {
            if (!Directory.Exists(junctionPath))
            {
                Directory.CreateDirectory(junctionPath);
            }

            if (!Directory.Exists(targetPath))
            {
                Directory.CreateDirectory(targetPath);
            }

            FileUtilities.CreateJunction(junctionPath, targetPath);
        }

        private Task<SandboxedProcessPipExecutionResult> RunProcessAsync(
            PathTable pathTable,
            bool ignoreSetFileInformationByHandle,
            bool ignoreZwRenameFileInformation,
            bool monitorNtCreate,
            bool ignoreReparsePoints,
            BuildXLContext context,
            Process pip,
            out string errorString,
            IDetoursEventListener detoursListener = null,
            bool existingDirectoryProbesAsEnumerations = false,
            bool disableDetours = false,
            AbsolutePath binDirectory = default,
            bool unexpectedFileAccessesAreErrors = true,
            List<TranslateDirectoryData> directoriesToTranslate = null,
            bool ignoreGetFinalPathNameByHandle = false,
            bool ignoreZwOtherFileInformation = true,
            bool monitorZwCreateOpenQueryFile = false,
            bool ignoreNonCreateFileReparsePoints = true,
            bool ignorePreloadedDlls = true,
            bool enforceAccessPoliciesOnDirectoryCreation = false,
            bool probeDirectorySymlinkAsDirectory = false,
            bool ignoreFullReparsePointResolving = true,
            List<AbsolutePath> directoriesToEnableFullReparsePointParsing = null,
            bool preserveFileSharingBehaviour = false,
            bool ignoreDeviceIoControlGetReparsePoint = true,
            DirectoryTranslator directoryTranslator = null)
        {
            errorString = null;
            directoryTranslator ??= CreateDirectoryTranslator(context, directoriesToTranslate);

            directoryTranslator.Seal();

            var sandboxConfiguration = new SandboxConfiguration
            {
                FileAccessIgnoreCodeCoverage = true,
                LogFileAccessTables = true,
                LogObservedFileAccesses = true,
                UnsafeSandboxConfigurationMutable =
                {
                    UnexpectedFileAccessesAreErrors = unexpectedFileAccessesAreErrors,
                    IgnoreReparsePoints = ignoreReparsePoints,
                    IgnoreFullReparsePointResolving = ignoreFullReparsePointResolving,
                    ExistingDirectoryProbesAsEnumerations = existingDirectoryProbesAsEnumerations,
                    IgnoreZwRenameFileInformation = ignoreZwRenameFileInformation,
                    IgnoreZwOtherFileInformation = ignoreZwOtherFileInformation,
                    IgnoreNonCreateFileReparsePoints = ignoreNonCreateFileReparsePoints,
                    IgnoreSetFileInformationByHandle = ignoreSetFileInformationByHandle,
                    SandboxKind = disableDetours ? SandboxKind.None : SandboxKind.Default,
                    MonitorNtCreateFile = monitorNtCreate,
                    IgnoreGetFinalPathNameByHandle = ignoreGetFinalPathNameByHandle,
                    MonitorZwCreateOpenQueryFile = monitorZwCreateOpenQueryFile,
                    IgnorePreloadedDlls = ignorePreloadedDlls,
                    ProbeDirectorySymlinkAsDirectory = probeDirectorySymlinkAsDirectory,
                },
                EnforceAccessPoliciesOnDirectoryCreation = enforceAccessPoliciesOnDirectoryCreation,
                FailUnexpectedFileAccesses = unexpectedFileAccessesAreErrors,
                DirectoriesToEnableFullReparsePointParsing = directoriesToEnableFullReparsePointParsing,
                PreserveFileSharingBehaviour = preserveFileSharingBehaviour,
                IgnoreDeviceIoControlGetReparsePoint = ignoreDeviceIoControlGetReparsePoint,
            };

            var loggingContext = CreateLoggingContextForTest();

            var configuration = new ConfigurationImpl()
            {
                Sandbox = sandboxConfiguration,
                Distribution = new DistributionConfiguration { ValidateDistribution = false },
                Engine = new EngineConfiguration { DisableConHostSharing = false },
                Layout = new LayoutConfiguration { ObjectDirectory = AbsolutePath.Create(context.PathTable, TemporaryDirectory) }
            };

            return new SandboxedProcessPipExecutor(
                context,
                loggingContext,
                pip,
                configuration,
                new Dictionary<string, string>(),
                null,
                null,
                null,
                SemanticPathExpander.Default,
                sidebandState: null,
                pipEnvironment: new PipEnvironment(loggingContext),
                directoryArtifactContext: TestDirectoryArtifactContext.Empty,
                buildEngineDirectory: binDirectory,
                directoryTranslator: directoryTranslator,
                tempDirectoryCleaner: MoveDeleteCleaner,
                detoursListener: detoursListener).RunAsync(sandboxConnection: GetSandboxConnection());
        }

        private DirectoryTranslator CreateDirectoryTranslator(BuildXLContext context, List<TranslateDirectoryData> directoriesToTranslate)
        {
            var directoryTranslator = new DirectoryTranslator();

            if (TryGetSubstSourceAndTarget(out string substSource, out string substTarget))
            {
                directoryTranslator.AddTranslation(substSource, substTarget);
            }

            directoryTranslator.AddDirectoryTranslationFromEnvironment();

            if (directoriesToTranslate != null)
            {
                foreach (var translateDirectoryData in directoriesToTranslate)
                {
                    directoryTranslator.AddTranslation(translateDirectoryData.FromPath, translateDirectoryData.ToPath, context.PathTable);
                }
            }

            return directoryTranslator;
        }

        [Fact]
        public async Task CallCreateFileOnNtEscapedPath()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
                string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
                XAssert.IsTrue(File.Exists(executable));
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                string workingDirectory = tempFiles.RootDirectory;
                Contract.Assume(workingDirectory != null);
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                string testFilePath = Path.Combine(workingDirectory, "input");
                tempFiles.GetDirectory(Path.GetDirectoryName(testFilePath));
                WriteFile(testFilePath);

                var arguments = new PipDataBuilder(pathTable.StringTable);
                arguments.Add("CallCreateFileOnNtEscapedPath");

                var environmentVariables = new List<EnvironmentVariable>();

                var untrackedPaths = CmdHelper.GetCmdDependencies(pathTable);
                var untrackedScopes = CmdHelper.GetCmdDependencyScopes(pathTable);
                var pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(pathTable),
                    null,
                    null,
                    dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy([executableFileArtifact]),
                    outputs: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    // We want to have accessed under the working directory explicitly reported. The process will acces \\?\<working directory here>\input
                    directoryDependencies: ReadOnlyArray<DirectoryArtifact>.FromWithoutCopy([DirectoryArtifact.CreateWithZeroPartialSealId(workingDirectoryAbsolutePath)]),
                    directoryOutputs: ReadOnlyArray<DirectoryArtifact>.Empty,
                    orderDependencies: ReadOnlyArray<PipId>.Empty,
                    untrackedPaths: ReadOnlyArray<AbsolutePath>.From(untrackedPaths),
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.From(untrackedScopes),
                    tags: ReadOnlyArray<StringId>.Empty,
                    successExitCodes: ReadOnlyArray<int>.Empty,
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                await AssertProcessSucceedsAsync(
                    context,
                    new SandboxConfiguration { FileAccessIgnoreCodeCoverage = true, FailUnexpectedFileAccesses = true },
                    pip);
            }

            // The \\?\ escaped path should not have failed parsing.
            AssertWarningEventLogged(ProcessesLogEventId.PipProcessFailedToParsePathOfFileAccess, count: 0);
        }

        [Flags]
        private enum AddFileOrDirectoryKinds
        {
            None,
            AsDependency,
            AsOutput
        }

        // TODO: This setup method is so convoluted because many hands have touched it and simply add things on top of the others. Need a task to tidy it up.
        private static Process SetupDetoursTests(
            BuildXLContext context,
            TempFileStorage tempFileStorage,
            PathTable pathTable,
            string firstFileName,
            string secondFileOrDirectoryName,
            string nativeTestName,
            bool isDirectoryTest = false,
            bool createSymlink = false,
            bool addCreateFileInDirectoryToDependencies = false,
            bool createFileInDirectory = false,
            AddFileOrDirectoryKinds addFirstFileKind = AddFileOrDirectoryKinds.AsOutput,
            AddFileOrDirectoryKinds addSecondFileOrDirectoryKind = AddFileOrDirectoryKinds.AsDependency,
            bool makeSecondUntracked = false,
            Dictionary<string, AbsolutePath> createdInputPaths = null,
            List<AbsolutePath> untrackedPaths = null,
            List<AbsolutePath> additionalTempDirectories = null,
            List<DirectoryArtifact> outputDirectories = null)
        {
            // Get the executable DetoursTestsExe.
            string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
            string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
            XAssert.IsTrue(File.Exists(executable));
            FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

            // Get the working directory.
            AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, tempFileStorage.RootDirectory);

            // Create a clean test directory.
            AbsolutePath testDirectoryAbsolutePath = tempFileStorage.GetDirectory(pathTable, "input");
            string testDirectoryExpandedPath = testDirectoryAbsolutePath.ToString(pathTable);

            XAssert.IsTrue(Directory.Exists(testDirectoryExpandedPath), "Test directory must successfully be created");
            XAssert.IsFalse(Directory.EnumerateFileSystemEntries(testDirectoryExpandedPath).Any(), "Test directory must be empty");

            // Create a file artifact for the the first file, and ensure that the first file does not exist.
            AbsolutePath firstFileOrDirectoryAbsolutePath = tempFileStorage.GetFileName(pathTable, testDirectoryAbsolutePath, firstFileName);
            FileArtifact firstFileArtifact = FileArtifact.CreateSourceFile(firstFileOrDirectoryAbsolutePath);
            DirectoryArtifact firstDirectoryArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(firstFileOrDirectoryAbsolutePath);

            string firstFileExpandedPath = firstFileOrDirectoryAbsolutePath.ToString(pathTable);

            if (File.Exists(firstFileExpandedPath))
            {
                File.Delete(firstFileExpandedPath);
            }

            if (Directory.Exists(firstFileExpandedPath))
            {
                Directory.Delete(firstFileExpandedPath, true);
            }

            if (createdInputPaths != null)
            {
                createdInputPaths[firstFileName] = firstFileOrDirectoryAbsolutePath;
            }

            // Set second artifact, depending on whether we are testing directory or not.
            FileArtifact secondFileArtifact = FileArtifact.Invalid;
            FileArtifact extraFileArtifact = FileArtifact.Invalid;
            DirectoryArtifact secondDirectoryArtifact = DirectoryArtifact.Invalid;
            string secondFileOrDirectoryExpandedPath = null;
            AbsolutePath secondFileOrDirectoryAbsolutePath = AbsolutePath.Invalid;

            if (!string.IsNullOrWhiteSpace(secondFileOrDirectoryName))
            {
                if (isDirectoryTest)
                {
                    secondFileOrDirectoryAbsolutePath = tempFileStorage.GetDirectory(pathTable, testDirectoryAbsolutePath, secondFileOrDirectoryName);
                    secondFileOrDirectoryExpandedPath = secondFileOrDirectoryAbsolutePath.ToString(pathTable);

                    secondDirectoryArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(secondFileOrDirectoryAbsolutePath);
                }
                else
                {
                    secondFileOrDirectoryAbsolutePath = tempFileStorage.GetFileName(pathTable, testDirectoryAbsolutePath, secondFileOrDirectoryName);
                    secondFileOrDirectoryExpandedPath = secondFileOrDirectoryAbsolutePath.ToString(pathTable);

                    if (File.Exists(secondFileOrDirectoryExpandedPath))
                    {
                        File.Delete(secondFileOrDirectoryExpandedPath);
                    }

                    if (Directory.Exists(secondFileOrDirectoryExpandedPath))
                    {
                        Directory.Delete(secondFileOrDirectoryExpandedPath, true);
                    }

                    XAssert.IsFalse(File.Exists(secondFileOrDirectoryExpandedPath));
                    XAssert.IsFalse(Directory.Exists(secondFileOrDirectoryExpandedPath));

                    secondFileArtifact = FileArtifact.CreateSourceFile(secondFileOrDirectoryAbsolutePath);
                    WriteFile(secondFileOrDirectoryExpandedPath);
                }

                if (createdInputPaths != null)
                {
                    createdInputPaths[secondFileOrDirectoryName] = secondFileOrDirectoryAbsolutePath;
                }
            }

            bool addCreatedFileToDirectory = false;

            if (isDirectoryTest && createFileInDirectory && secondFileOrDirectoryAbsolutePath.IsValid)
            {
                XAssert.IsTrue(!string.IsNullOrWhiteSpace(secondFileOrDirectoryExpandedPath));

                AbsolutePath extraFileAbsolutePath = tempFileStorage.GetFileName(pathTable, secondFileOrDirectoryAbsolutePath, ExtraFileNameInDirectory);
                extraFileArtifact = FileArtifact.CreateSourceFile(extraFileAbsolutePath);

                string extraFileExtendedPath = extraFileAbsolutePath.ToString(pathTable);
                if (File.Exists(extraFileExtendedPath))
                {
                    File.Delete(extraFileExtendedPath);
                }

                WriteFile(extraFileExtendedPath);

                addCreatedFileToDirectory = true;

                if (createdInputPaths != null)
                {
                    Contract.Assert(!string.IsNullOrWhiteSpace(secondFileOrDirectoryName));
                    createdInputPaths[Path.Combine(secondFileOrDirectoryName, ExtraFileNameInDirectory)] = extraFileAbsolutePath;
                    createdInputPaths[Path.Combine(firstFileName, ExtraFileNameInDirectory)] = firstFileOrDirectoryAbsolutePath.Combine(
                        pathTable,
                        ExtraFileNameInDirectory);
                }
            }

            var arguments = new PipDataBuilder(pathTable.StringTable);
            arguments.Add(nativeTestName);

            var untrackedList = new List<AbsolutePath>(CmdHelper.GetCmdDependencies(pathTable));

            if (untrackedPaths != null)
            {
                foreach (AbsolutePath up in untrackedPaths)
                {
                    untrackedList.Add(up);
                }
            }

            var untrackedScopes = CmdHelper.GetCmdDependencyScopes(pathTable);

            if (makeSecondUntracked && secondFileOrDirectoryAbsolutePath.IsValid)
            {
                untrackedList.Add(secondFileOrDirectoryAbsolutePath);
            }

            if (createSymlink && secondFileOrDirectoryAbsolutePath.IsValid)
            {
                XAssert.IsTrue(!string.IsNullOrWhiteSpace(secondFileOrDirectoryExpandedPath));
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(firstFileExpandedPath, secondFileOrDirectoryExpandedPath, !isDirectoryTest));
            }

            var allDependencies = new List<FileArtifact> { executableFileArtifact };
            var allDirectoryDependencies = new List<DirectoryArtifact>(2);
            var allOutputs = new List<FileArtifactWithAttributes>(2);
            var allDirectoryOutputs = new List<DirectoryArtifact>();

            if (secondFileOrDirectoryAbsolutePath.IsValid)
            {
                if (addSecondFileOrDirectoryKind.HasFlag(AddFileOrDirectoryKinds.AsDependency))
                {
                    if (isDirectoryTest)
                    {
                        allDirectoryDependencies.Add(secondDirectoryArtifact);
                    }
                    else
                    {
                        allDependencies.Add(secondFileArtifact);
                    }
                }

                if (addSecondFileOrDirectoryKind.HasFlag(AddFileOrDirectoryKinds.AsOutput) && !isDirectoryTest)
                {
                    // Rewrite.
                    allOutputs.Add(secondFileArtifact.CreateNextWrittenVersion().WithAttributes(FileExistence.Required));
                }
            }

            if (addCreatedFileToDirectory && addCreateFileInDirectoryToDependencies)
            {
                allDependencies.Add(extraFileArtifact);

                if (createSymlink)
                {
                    // If symlink is created, then add the symlink via first directory as dependency.
                    allDependencies.Add(FileArtifact.CreateSourceFile(firstFileOrDirectoryAbsolutePath.Combine(pathTable, ExtraFileNameInDirectory)));
                }
            }

            if (addFirstFileKind.HasFlag(AddFileOrDirectoryKinds.AsDependency))
            {
                if (isDirectoryTest)
                {
                    allDirectoryDependencies.Add(firstDirectoryArtifact);
                }
                else
                {
                    allDependencies.Add(firstFileArtifact);
                }
            }

            if (addFirstFileKind.HasFlag(AddFileOrDirectoryKinds.AsOutput))
            {
                if (isDirectoryTest)
                {
                    allDirectoryOutputs.Add(firstDirectoryArtifact);
                }
                else
                {
                    allOutputs.Add(firstFileArtifact.CreateNextWrittenVersion().WithAttributes(FileExistence.Required));
                }
            }

            if (outputDirectories != null)
            {
                foreach (DirectoryArtifact da in outputDirectories)
                {
                    allDirectoryOutputs.Add(da);
                }
            }

            return new Process(
                executableFileArtifact,
                workingDirectoryAbsolutePath,
                arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                FileArtifact.Invalid,
                PipData.Invalid,
                ReadOnlyArray<EnvironmentVariable>.Empty,
                FileArtifact.Invalid,
                FileArtifact.Invalid,
                FileArtifact.Invalid,
                tempFileStorage.GetUniqueDirectory(pathTable),
                null,
                null,
                dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy(allDependencies.ToArray<FileArtifact>()),
                outputs: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(allOutputs.ToArray()),
                directoryDependencies:
                    isDirectoryTest
                        ? ReadOnlyArray<DirectoryArtifact>.FromWithoutCopy(allDirectoryDependencies.ToArray<DirectoryArtifact>())
                        : ReadOnlyArray<DirectoryArtifact>.Empty,
                directoryOutputs: ReadOnlyArray<DirectoryArtifact>.FromWithoutCopy(allDirectoryOutputs.ToArray<DirectoryArtifact>()),
                orderDependencies: ReadOnlyArray<PipId>.Empty,
                untrackedPaths: ReadOnlyArray<AbsolutePath>.From(untrackedList),
                untrackedScopes: ReadOnlyArray<AbsolutePath>.From(untrackedScopes),
                tags: ReadOnlyArray<StringId>.Empty,

                // We expect the CreateFile call to fail, but with no monitoring error logged.
                successExitCodes: ReadOnlyArray<int>.FromWithoutCopy(0),
                semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                provenance: PipProvenance.CreateDummy(context),
                toolDescription: StringId.Invalid,
                additionalTempDirectories: additionalTempDirectories == null ? ReadOnlyArray<AbsolutePath>.Empty : ReadOnlyArray<AbsolutePath>.From(additionalTempDirectories));
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public async Task CallDetouredSetFileInformationFileLink(bool ignorePreloadedDlls, bool useFileLinkInformationEx)
        {
            if (useFileLinkInformationEx)
            {
                // FileLinkInformationEx is only available starting RS5 (ver 1809, OS build 17763)
                // skip the test if it's running on a machine that does not support it.
                var versionString = OperatingSystemHelperExtension.GetOSVersion();

                int build = 0;
                var match = Regex.Match(versionString, @"^Windows\s\d+\s\w+\s(?<buildId>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    build = Convert.ToInt32(match.Groups["buildId"].Value);
                }

                if (build < 17763)
                {
                    return;
                }
            }

            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "SetFileInformationFileLinkTest1.txt",
                    "SetFileInformationFileLinkTest2.txt",
                    useFileLinkInformationEx
                        ? "CallDetouredSetFileInformationFileLinkEx"
                        : "CallDetouredSetFileInformationFileLink",
                    isDirectoryTest: false,
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: true,
                    ignoreZwRenameFileInformation: true,
                    monitorNtCreate: true,
                    ignoreReparsePoints: true,
                    ignoreZwOtherFileInformation: false,
                    ignorePreloadedDlls: ignorePreloadedDlls,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                if (!ignorePreloadedDlls)
                {
                    // The count includes extra preloaded Dlls.
                    // The preloaded Dlls should be bigger or equal to 5.
                    // In the cloud, the extra preloaded Dlls may not be included.
                    XAssert.IsTrue(
                        result.AllReportedFileAccesses.Count >= 5,
                        "Number of reported accesses: " + result.AllReportedFileAccesses.Count + Environment.NewLine
                        + string.Join(Environment.NewLine + "\t", result.AllReportedFileAccesses.Select(rfs => rfs.Describe())));
                }

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["SetFileInformationFileLinkTest2.txt"], RequestedAccess.Read, FileAccessStatus.Allowed),
                        (createdInputPaths["SetFileInformationFileLinkTest1.txt"], RequestedAccess.Write, FileAccessStatus.Allowed),
                    });
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CallDeleteFileWithoutClosingHandle(bool preserveFileSharingBehaviour)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                AbsolutePath deletedFile = tempFiles.GetFileName(pathTable, "testFile.txt");

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDeleteFileWithoutClosingHandle",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(
                        FileArtifactWithAttributes.FromFileArtifact(FileArtifact.CreateSourceFile(deletedFile), FileExistence.Optional)),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _,
                    preserveFileSharingBehaviour: preserveFileSharingBehaviour);

                if (preserveFileSharingBehaviour)
                {
                    // The pip is expected to fail with ERROR_SHARING_VIOLATION (32)
                    XAssert.IsTrue(result.ExitCode == 32, $"Exit code: {result.ExitCode}");

                    SetExpectedFailures(1, 0);
                }
                else
                {
                    VerifyNormalSuccess(context, result);

                    XAssert.IsTrue(result.ExitCode == 0, $"Exit code: {result.ExitCode}");
                }
            }
        }

        [Fact]
        public async Task CallDeleteFileOnSharedDeleteOpenedFile()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string inputDir = tempFiles.GetDirectory("input");
                string testFile = tempFiles.GetFileName(inputDir, "Test1.txt");

                WriteFile(testFile);

                OpenFileResult openTestFileResult = FileUtilities.TryCreateOrOpenFile(
                    testFile,
                    FileDesiredAccess.GenericRead,
                    FileShare.ReadWrite | FileShare.Delete,
                    FileMode.Open, FileFlagsAndAttributes.FileAttributeNormal,
                    out SafeFileHandle fileHandle);

                XAssert.IsTrue(openTestFileResult.Succeeded);
                XAssert.IsFalse(fileHandle.IsInvalid);

                using (fileHandle)
                {
                    AbsolutePath deletedFile = AbsolutePath.Create(pathTable, testFile);
                    var process = CreateDetourProcess(
                        context,
                        pathTable,
                        tempFiles,
                        argumentStr: "CallDeleteFileTest",
                        inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                        inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                        outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(
                            FileArtifactWithAttributes.FromFileArtifact(FileArtifact.CreateSourceFile(deletedFile), FileExistence.Optional)),
                        outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                        untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                    SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                        pathTable: pathTable,
                        ignoreSetFileInformationByHandle: false,
                        ignoreZwRenameFileInformation: false,
                        monitorNtCreate: true,
                        ignoreReparsePoints: false,
                        disableDetours: false,
                        context: context,
                        pip: process,
                        errorString: out _);

                    VerifyNormalSuccess(context, result);

                    // Ensure there is not reported write to the staging $Extend\$Deleted directory.
                    XAssert.IsFalse(result.AllReportedFileAccesses.Select(rfa => rfa.GetPath(pathTable)).Any(p => p.Contains("$Extend\\$Deleted")));
                }
            }
        }

        /// <summary>
        /// Intentionally call Windows API erroneously before successfully deleting a file.
        /// </summary>
        /// <remarks>
        /// DeleteFileW apparently does not set last error to ERROR_SUCCESS on successful call.
        /// Detours before Commit 6f91c0bf corrected it. With that commit, Detours simply sets it with whatever the last error in the system.
        /// Consider the following scenario where a call to DeleteFileW follows an erroneous API call. 
        ///
        ///     HANDLE hFile = CreateFileW("nonexistent-file"); // last error code should be 2 after this call
        ///     BOOL result = DeleteFileW("existing-file");       // successful
        ///     wprintf(L"Result: %d, Last error: %d", GetLastError());
        ///
        /// A. Without Detours: "Result: 1, Last error: 2" -- file not found, due to CreateFileW("nonexistent-file");
        /// B. With Detours before commit 6f91c0bf: "Result: 1, Last error: 0" -- Detours corrected the error by setting ERROR_SUCCESS
        /// C. With Detours with commit 6f91c0bf: "Result: 1, Last error: 2" -- Detours simply set with whatever last error is.
        ///
        /// Unfortunately, customers are relying on the fact that if an API call is successful, which in this case `result != 0`, then the expected error code is ERROR_SUCCESS.
        /// </remarks>
        [Fact]
        public async Task CallCreateErrorBeforeDeleteFile()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string testFile = tempFiles.GetFileName("toDelete.txt");

                WriteFile(testFile);
                AbsolutePath deletedFile = AbsolutePath.Create(pathTable, testFile);
                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallCreateErrorBeforeDeleteFileTest",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(
                        FileArtifactWithAttributes.FromFileArtifact(FileArtifact.CreateSourceFile(deletedFile), FileExistence.Optional)),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);
            }
        }

        [Fact]
        public async Task CallDetouredZwCreateFile()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "ZwCreateFileTest1.txt",
                    "ZwCreateFileTest2.txt",
                    "CallDetouredZwCreateFile",
                    isDirectoryTest: false,
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    // The second file will be opened with a write access in order for SetFileInformationByHandle works.
                    // However, the second file will be renamed into the first file, and so the second file does not fall into
                    // rewrite category, and thus cannot be specified as output. This forces us to make it untracked.
                    makeSecondUntracked: true,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: true,
                    ignoreZwRenameFileInformation: true,
                    monitorNtCreate: true,
                    ignoreReparsePoints: true,
                    ignoreZwOtherFileInformation: false,
                    monitorZwCreateOpenQueryFile: true,
                    context: context,
                    pip: pip,
                    errorString: out _);

                if (result.Status == SandboxedProcessPipExecutionStatus.ExecutionFailed)
                {
                    // When we build in the cloud or in the release pipeline, this test can suffer from 'unclear' file system limitation or returns weird error.
                    SetExpectedFailures(1, 0);
                }
                else
                {
                    VerifyNormalSuccess(context, result);
                }

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["ZwCreateFileTest1.txt"], RequestedAccess.Write, FileAccessStatus.Allowed),
                    });
            }
        }

        [Fact]
        public async Task CallDetouredCreateFileWWithGenericAllAccess()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateFileWWithGenericAllAccess1.txt",
                    "CreateFileWWithGenericAllAccess2.txt",
                    "CallDetouredCreateFileWWithGenericAllAccess",
                    isDirectoryTest: false,
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: true,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: true,
                    ignoreZwRenameFileInformation: true,
                    monitorNtCreate: true,
                    ignoreReparsePoints: true,
                    ignoreZwOtherFileInformation: true,
                    monitorZwCreateOpenQueryFile: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["CreateFileWWithGenericAllAccess1.txt"], RequestedAccess.Write, FileAccessStatus.Allowed),
                    });
            }
        }

        [Fact]
        public async Task CallDetouredZwOpenFile()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "ZwOpenFileTest1.txt",
                    "ZwOpenFileTest2.txt",
                    "CallDetouredZwOpenFile",
                    isDirectoryTest: false,
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsDependency,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: true,
                    ignoreZwRenameFileInformation: true,
                    monitorNtCreate: true,
                    ignoreReparsePoints: true,
                    ignoreZwOtherFileInformation: false,
                    monitorZwCreateOpenQueryFile: true,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["ZwOpenFileTest2.txt"], RequestedAccess.Read, FileAccessStatus.Allowed),
                    });
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CallDetouredSetFileInformationByHandle(bool ignoreSetFileInformationByHandle)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "SetFileInformationByHandleTest1.txt",
                    "SetFileInformationByHandleTest2.txt",
                    "CallDetouredSetFileInformationByHandle",
                    isDirectoryTest: false,
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: true,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    // The second file will be opened with a write access in order for SetFileInformationByHandle works.
                    // However, the second file will be renamed into the first file, and so the second file does not fall into
                    // rewrite category, and thus cannot be specified as output. This forces us to make it untracked.
                    makeSecondUntracked: true,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: ignoreSetFileInformationByHandle,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: true,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                var accesses = new List<(AbsolutePath, RequestedAccess, FileAccessStatus)>
                {
                    // Although ignored, we still have write request on SetFileInformationByHandleTest2.txt because we open handle of it by calling CreateFile.
                    (createdInputPaths["SetFileInformationByHandleTest2.txt"], RequestedAccess.Write, FileAccessStatus.Allowed)
                };

                if (!ignoreSetFileInformationByHandle)
                {
                    accesses.Add((createdInputPaths["SetFileInformationByHandleTest1.txt"], RequestedAccess.Write, FileAccessStatus.Allowed));
                }

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    accesses.ToArray());
            }
        }

        [Fact]
        public async Task CallDetouredSetFileInformationByHandleWithIncorrectFileNameLength()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "SetFileInformationByHandleTest1.txt",
                    "SetFileInformationByHandleTest2.txt",
                    "CallDetouredSetFileInformationByHandle_IncorrectFileNameLength",
                    isDirectoryTest: false,
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: true,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    // The second file will be opened with a write access in order for SetFileInformationByHandle works.
                    // However, the second file will be renamed into the first file, and so the second file does not fall into
                    // rewrite category, and thus cannot be specified as output. This forces us to make it untracked.
                    makeSecondUntracked: true,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: true,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                var accesses = new List<(AbsolutePath, RequestedAccess, FileAccessStatus)>
                {
                    // Although ignored, we still have write request on SetFileInformationByHandleTest2.txt because we open handle of it by calling CreateFile.
                    (createdInputPaths["SetFileInformationByHandleTest2.txt"], RequestedAccess.Write, FileAccessStatus.Allowed),
                    (createdInputPaths["SetFileInformationByHandleTest1.txt"], RequestedAccess.Write, FileAccessStatus.Allowed)
                };

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    accesses.ToArray());
            }
        }

        [Theory]
        [InlineData("CallDetouredSetFileDispositionByHandle")]
        [InlineData("CallDetouredSetFileDispositionByHandleEx", Skip = "Undocumented API, and keeps returning incorrect parameter")]
        [InlineData("CallDetouredZwSetFileDispositionByHandle")]
        [InlineData("CallDetouredZwSetFileDispositionByHandleEx")]
        public async Task CallDetouredSetFileDispositionByHandle(string functionName)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
                string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
                XAssert.IsTrue(File.Exists(executable));
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                string workingDirectory = tempFiles.RootDirectory;
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                string testDirPath = Path.Combine(workingDirectory, "input");
                tempFiles.GetDirectory("input");

                string firstTestFile = Path.Combine(testDirPath, "SetFileDisposition.txt");
                AbsolutePath firstAbsPath = AbsolutePath.Create(pathTable, firstTestFile);
                FileArtifact firstFileArtifact = FileArtifact.CreateSourceFile(firstAbsPath);
                WriteFile(firstTestFile);

                var allDependencies = new List<FileArtifact>(2)
                {
                    executableFileArtifact
                };

                var arguments = new PipDataBuilder(pathTable.StringTable);
                arguments.Add(functionName);

                var environmentVariables = new List<EnvironmentVariable>();
                Process pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(pathTable),
                    null,
                    null,
                    dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy(allDependencies.ToArray<FileArtifact>()),
                    outputs: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    directoryDependencies: ReadOnlyArray<DirectoryArtifact>.Empty,
                    directoryOutputs: ReadOnlyArray<DirectoryArtifact>.Empty,
                    orderDependencies: ReadOnlyArray<PipId>.Empty,
                    untrackedPaths: ReadOnlyArray<AbsolutePath>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty,
                    tags: ReadOnlyArray<StringId>.Empty,
                    // We expect the CreateFile call to fail, but with no monitoring error logged.
                    successExitCodes: ReadOnlyArray<int>.FromWithoutCopy([0]),
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    ignoreZwOtherFileInformation: false,
                    unexpectedFileAccessesAreErrors: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                XAssert.IsFalse(File.Exists(firstTestFile));

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (firstAbsPath, RequestedAccess.Write, FileAccessStatus.Denied),
                    });

                bool zwFunction =
                    string.Equals(functionName, "CallDetouredZwSetFileDispositionByHandle")
                    || string.Equals(functionName, "CallDetouredZwSetFileDispositionByHandleEx");

                ReportedFileOperation op = zwFunction
                    ? ReportedFileOperation.ZwSetDispositionInformationFile
                    : ReportedFileOperation.SetFileInformationByHandleSource;

                bool foundDelete = false;
                foreach (var rfa in result.AllReportedFileAccesses)
                {
                    var path = !string.IsNullOrEmpty(rfa.Path) ? AbsolutePath.Create(context.PathTable, rfa.Path) : rfa.ManifestPath;
                    if (path == firstAbsPath
                        && rfa.DesiredAccess == DesiredAccess.DELETE
                        && rfa.Operation == op)
                    {
                        foundDelete = true;
                        break;
                    }
                }

                XAssert.IsTrue(foundDelete);
            }
        }

        [TheoryIfSupported(requiresSymlinkPermission: true)]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CallDetouredGetFinalPathNameByHandle(bool ignoreGetFinalPathNameByHandle)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
                string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
                XAssert.IsTrue(File.Exists(executable));
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                string workingDirectory = tempFiles.RootDirectory;
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                string testDirPath = tempFiles.GetDirectory("input");
                string testTargetDirPath = tempFiles.GetDirectory("inputTarget");

                // Create file input\GetFinalPathNameByHandleTest.txt, which will point to inputTarget\GetFinalPathNameByHandleTest.txt
                string testFile = Path.Combine(testDirPath, "GetFinalPathNameByHandleTest.txt");
                AbsolutePath firstAbsPath = AbsolutePath.Create(pathTable, testFile);
                FileArtifact testFileArtifact = FileArtifact.CreateSourceFile(firstAbsPath);

                string testTargetFile = Path.Combine(testTargetDirPath, "GetFinalPathNameByHandleTest.txt");
                AbsolutePath firstTargetAbsPath = AbsolutePath.Create(pathTable, testTargetFile);
                WriteFile(testTargetFile);

                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(testFile, testTargetFile, true));

                var allDependencies = new List<FileArtifact>(2)
                {
                    executableFileArtifact,
                    // Only add input\GetFinalPathNameByHandleTest.txt as dependency.
                    testFileArtifact
                };

                var arguments = new PipDataBuilder(pathTable.StringTable);
                arguments.Add("CallDetouredGetFinalPathNameByHandle");

                var environmentVariables = new List<EnvironmentVariable>();
                Process pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(pathTable),
                    null,
                    null,
                    dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy(allDependencies.ToArray<FileArtifact>()),
                    outputs: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    directoryDependencies: ReadOnlyArray<DirectoryArtifact>.Empty,
                    directoryOutputs: ReadOnlyArray<DirectoryArtifact>.Empty,
                    orderDependencies: ReadOnlyArray<PipId>.Empty,
                    untrackedPaths: ReadOnlyArray<AbsolutePath>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty,
                    tags: ReadOnlyArray<StringId>.Empty,
                    // We expect the CreateFile call to fail, but with no monitoring error logged.
                    successExitCodes: ReadOnlyArray<int>.FromWithoutCopy([0]),
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _,
                    ignoreGetFinalPathNameByHandle: ignoreGetFinalPathNameByHandle,
                    directoriesToTranslate:
                        new List<TranslateDirectoryData>
                        {
                            new TranslateDirectoryData(
                                testTargetDirPath + @"\<" + testDirPath + @"\",
                                AbsolutePath.Create(context.PathTable, testTargetDirPath),
                                AbsolutePath.Create(context.PathTable, testDirPath))
                        });

                VerifyExecutionStatus(
                    context,
                    result,
                    ignoreGetFinalPathNameByHandle
                        ? SandboxedProcessPipExecutionStatus.ExecutionFailed
                        : SandboxedProcessPipExecutionStatus.Succeeded);
                VerifyExitCode(context, result, ignoreGetFinalPathNameByHandle ? -1 : 0);

                SetExpectedFailures(ignoreGetFinalPathNameByHandle ? 1 : 0, 0);
            }
        }

        [Fact]
        public async Task TestDeleteTempDirectory()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
                string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
                XAssert.IsTrue(File.Exists(executable));
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                string workingDirectory = tempFiles.RootDirectory;
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                string testDirPath = tempFiles.GetDirectory("input");
                AbsolutePath inputDirPath = AbsolutePath.Create(pathTable, testDirPath);

                var allDependencies = new List<FileArtifact>(2)
                {
                    executableFileArtifact
                };

                var arguments = new PipDataBuilder(pathTable.StringTable);
                arguments.Add("CallDeleteDirectoryTest");

                var environmentVariables = new List<EnvironmentVariable>();
                Process pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(pathTable),
                    null,
                    null,
                    dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy(allDependencies.ToArray<FileArtifact>()),
                    outputs: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    directoryDependencies: ReadOnlyArray<DirectoryArtifact>.Empty,
                    directoryOutputs: ReadOnlyArray<DirectoryArtifact>.Empty,
                    orderDependencies: ReadOnlyArray<PipId>.Empty,
                    untrackedPaths: ReadOnlyArray<AbsolutePath>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.FromWithoutCopy([inputDirPath]),
                    tags: ReadOnlyArray<StringId>.Empty,
                    // We expect the CreateFile call to fail, but with no monitoring error logged.
                    successExitCodes: ReadOnlyArray<int>.FromWithoutCopy([0]),
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.FromWithoutCopy([inputDirPath]));

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (inputDirPath, RequestedAccess.Write, FileAccessStatus.Allowed),
                    });
            }
        }

        [Fact]
        public async Task TestDeleteTempDirectoryNoFileAccessError()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
                string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
                XAssert.IsTrue(File.Exists(executable));
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                string workingDirectory = tempFiles.RootDirectory;
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                string testDirPath = tempFiles.GetDirectory("input");
                AbsolutePath inputDirPath = AbsolutePath.Create(pathTable, testDirPath);

                var allDependencies = new List<FileArtifact>(2)
                {
                    executableFileArtifact
                };

                var arguments = new PipDataBuilder(pathTable.StringTable);
                arguments.Add("CallDeleteDirectoryTest");

                var environmentVariables = new List<EnvironmentVariable>();
                Process pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(pathTable),
                    null,
                    null,
                    dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy(allDependencies.ToArray<FileArtifact>()),
                    outputs: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    directoryDependencies: ReadOnlyArray<DirectoryArtifact>.Empty,
                    directoryOutputs: ReadOnlyArray<DirectoryArtifact>.Empty,
                    orderDependencies: ReadOnlyArray<PipId>.Empty,
                    untrackedPaths: ReadOnlyArray<AbsolutePath>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.FromWithoutCopy([inputDirPath]),
                    tags: ReadOnlyArray<StringId>.Empty,
                    // We expect the CreateFile call to fail, but with no monitoring error logged.
                    successExitCodes: ReadOnlyArray<int>.FromWithoutCopy([0]),
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.FromWithoutCopy([inputDirPath]));

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    unexpectedFileAccessesAreErrors: false,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (inputDirPath, RequestedAccess.Write, FileAccessStatus.Allowed),
                    });
            }
        }


        [Fact]
        public async Task TestCreateExistingDirectoryFileAccessError()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
                string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
                XAssert.IsTrue(File.Exists(executable));
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                string workingDirectory = tempFiles.RootDirectory;
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                string testDirPath = tempFiles.GetDirectory("input");
                AbsolutePath inputDirPath = AbsolutePath.Create(pathTable, testDirPath);

                var allDependencies = new List<FileArtifact>(2)
                {
                    executableFileArtifact
                };

                var arguments = new PipDataBuilder(pathTable.StringTable);
                arguments.Add("CallCreateDirectoryTest");

                var environmentVariables = new List<EnvironmentVariable>();
                Process pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(pathTable),
                    null,
                    null,
                    dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy(allDependencies.ToArray<FileArtifact>()),
                    outputs: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    directoryDependencies: ReadOnlyArray<DirectoryArtifact>.Empty,
                    directoryOutputs: ReadOnlyArray<DirectoryArtifact>.Empty,
                    orderDependencies: ReadOnlyArray<PipId>.Empty,
                    untrackedPaths: ReadOnlyArray<AbsolutePath>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty,
                    tags: ReadOnlyArray<StringId>.Empty,
                    successExitCodes: ReadOnlyArray<int>.FromWithoutCopy([0]),
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _,
                    // with this flag set to 'true', Detours should not interpret CreateDirectory as a read-only probe => CreateDirectory should be denied
                    enforceAccessPoliciesOnDirectoryCreation: true);

                SetExpectedFailures(1, 0);
                AssertVerboseEventLogged(ProcessesLogEventId.PipProcessDisallowedFileAccess);

                VerifyAccessDenied(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (inputDirPath, RequestedAccess.Write, FileAccessStatus.Denied),
                    });
            }
        }

        /// <summary>
        /// Tests that directories of output files are created.
        /// </summary>
        [Fact]
        public async Task CreateDirectoriesNoAllow()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string executable = CmdHelper.CmdX64;
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(context.PathTable, executable));

                string workingDirectory = tempFiles.RootDirectory;
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(context.PathTable, workingDirectory);

                string destination = tempFiles.RootDirectory;
                AbsolutePath destinationAbsolutePath = AbsolutePath.Create(context.PathTable, destination);
                DirectoryArtifact destinationFileArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(destinationAbsolutePath);

                string envVarName = "ENV" + Guid.NewGuid().ToString().Replace("-", string.Empty);

                string destFile = Path.Combine(destination, "Foo", "bar.txt");
                string destDirectory = Path.Combine(destination, "Foo");
                AbsolutePath destFileAbsolutePath = AbsolutePath.Create(context.PathTable, destFile);
                FileArtifact destFileArtifact = FileArtifact.CreateOutputFile(destFileAbsolutePath);

                if (File.Exists(destFile))
                {
                    File.Delete(destFile);
                }

                var arguments = new PipDataBuilder(context.PathTable.StringTable);
                arguments.Add("/d");
                arguments.Add("/c");
                using (arguments.StartFragment(PipDataFragmentEscaping.CRuntimeArgumentRules, " "))
                {
                    arguments.Add("mkdir");
                    arguments.Add(destinationAbsolutePath);
                    arguments.Add("&");
                    arguments.Add("echo");
                    arguments.Add("aaaaa");
                    arguments.Add(">");
                    arguments.Add(destFileAbsolutePath);
                }

                var untrackedPaths = new List<AbsolutePath>(CmdHelper.GetCmdDependencies(context.PathTable));

                var pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.FromWithoutCopy(
                        [
                            new EnvironmentVariable(
                                StringId.Create(context.PathTable.StringTable, envVarName),
                                PipDataBuilder.CreatePipData(
                                    context.PathTable.StringTable,
                                    " ",
                                    PipDataFragmentEscaping.CRuntimeArgumentRules,
                                    "Success"))
                        ]),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(context.PathTable),
                    null,
                    null,
                    ReadOnlyArray<FileArtifact>.FromWithoutCopy(executableFileArtifact),
                    ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy([destFileArtifact.WithAttributes()]),
                    ReadOnlyArray<DirectoryArtifact>.Empty,
                    ReadOnlyArray<DirectoryArtifact>.Empty,
                    ReadOnlyArray<PipId>.Empty,
                    ReadOnlyArray<AbsolutePath>.From(untrackedPaths),
                    ReadOnlyArray<AbsolutePath>.From(CmdHelper.GetCmdDependencyScopes(context.PathTable)),
                    ReadOnlyArray<StringId>.Empty,
                    ReadOnlyArray<int>.Empty,
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: true,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyExecutionStatus(context, result, SandboxedProcessPipExecutionStatus.Succeeded);

                // TODO(imnarasa): Check the exit code.
                XAssert.IsFalse(result.ExitCode == 1);

                XAssert.IsTrue(File.Exists(destFile));
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDetouredFileCreateWithSymlinkAndIgnore()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateSymbolicLinkTest1.txt",
                    "CreateSymbolicLinkTest2.txt",
                    "CallDetouredFileCreateWithSymlink",
                    isDirectoryTest: false,

                    // Setup doesn't create symlink, but the C++ method CallDetouredFileCreateWithSymlink does.
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: true,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,

                    // Ignore reparse point.
                    ignoreReparsePoints: true,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        // CallDetouredFileCreateWithSymlink calls CreateFileW with Read access on CreateSymbolicLinkTest2.txt.
                        (createdInputPaths["CreateSymbolicLinkTest2.txt"], RequestedAccess.Read, FileAccessStatus.Allowed),
                    });
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDetouredFileCreateWithSymlinkAndIgnoreFail()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateSymbolicLinkTest1.txt",
                    "CreateSymbolicLinkTest2.txt",
                    "CallDetouredFileCreateWithSymlink",
                    isDirectoryTest: false,
                    // Setup doesn't create symlink, but the C++ method CallDetouredFileCreateWithSymlink does.
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: true,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,

                    // Ignore reparse point.
                    ignoreReparsePoints: true,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        // CallDetouredFileCreateWithSymlink calls CreateFileW with Read access on CreateSymbolicLinkTest2.txt.
                        (createdInputPaths["CreateSymbolicLinkTest2.txt"], RequestedAccess.Read, FileAccessStatus.Allowed),
                    });
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDetouredFileCreateWithSymlinkAndNoIgnore()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateSymbolicLinkTest1.txt",
                    "CreateSymbolicLinkTest2.txt",
                    "CallDetouredFileCreateWithSymlink",
                    isDirectoryTest: false,
                    // Setup doesn't create symlink, but the C++ method CallDetouredFileCreateWithSymlink does.
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: true,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    // Don't ignore reparse point.
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["CreateSymbolicLinkTest2.txt"], RequestedAccess.Read, FileAccessStatus.Allowed),
                        (createdInputPaths["CreateSymbolicLinkTest1.txt"], RequestedAccess.Write, FileAccessStatus.Allowed)
                    });
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDetouredProcessCreateWithDirectorySymlinkAndNoIgnore()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                AbsolutePath createdDirSymlink = tempFiles.GetFileName(pathTable, "CreateSymLinkOnDirectories1.dir");
                AbsolutePath createdFile = tempFiles.GetFileName(pathTable, "CreateFile");

                string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
                string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
                AbsolutePath detoursPath = AbsolutePath.Create(pathTable, executable);

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDetouredProcessCreateWithDirectorySymlink",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(
                        FileArtifactWithAttributes.FromFileArtifact(FileArtifact.CreateSourceFile(createdFile), FileExistence.Required),
                        FileArtifactWithAttributes.FromFileArtifact(FileArtifact.CreateSourceFile(createdDirSymlink), FileExistence.Required)),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    ignoreFullReparsePointResolving: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);
                VerifyProcessCreations(context, result.AllReportedFileAccesses, [DetoursTestsExe]);
                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdDirSymlink, RequestedAccess.Write, FileAccessStatus.Allowed),
                        (detoursPath, RequestedAccess.Read, FileAccessStatus.Allowed)
                    });
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDetouredProcessCreateWithSymlinkAndNoIgnore()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                AbsolutePath createdExe = tempFiles.GetFileName(pathTable, "CreateSymbolicLinkTest2.exe");
                AbsolutePath createdFile = tempFiles.GetFileName(pathTable, "CreateFile");

                string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
                string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
                AbsolutePath detoursPath = AbsolutePath.Create(pathTable, executable);

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDetouredProcessCreateWithSymlink",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(
                        FileArtifactWithAttributes.FromFileArtifact(FileArtifact.CreateSourceFile(createdFile), FileExistence.Required),
                        FileArtifactWithAttributes.FromFileArtifact(FileArtifact.CreateSourceFile(createdExe), FileExistence.Required)),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);
                VerifyProcessCreations(context, result.AllReportedFileAccesses, ["CreateSymbolicLinkTest2.exe"]);
                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdExe, RequestedAccess.Write, FileAccessStatus.Allowed),
                        (detoursPath, RequestedAccess.Read, FileAccessStatus.Allowed)
                    });
            }
        }

        [Fact]
        public async Task CallDetouredFileCreateWithNoSymlinkAndIgnore()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateNoSymbolicLinkTest1.txt",
                    "CreateNoSymbolicLinkTest2.txt",
                    "CallDetouredFileCreateWithNoSymlink",
                    isDirectoryTest: false,
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: true,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: true,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["CreateNoSymbolicLinkTest2.txt"], RequestedAccess.Read, FileAccessStatus.Allowed),
                        (createdInputPaths["CreateNoSymbolicLinkTest1.txt"], RequestedAccess.Write, FileAccessStatus.Allowed)
                    });
            }
        }

        [Fact]
        public async Task CallDetouredFileCreateWithNoSymlinkAndNoIgnore()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateNoSymbolicLinkTest1.txt",
                    "CreateNoSymbolicLinkTest2.txt",
                    "CallDetouredFileCreateWithNoSymlink",
                    isDirectoryTest: false,
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: true,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                var sandboxConfiguration = new SandboxConfiguration
                {
                    FileAccessIgnoreCodeCoverage = true,
                    UnsafeSandboxConfigurationMutable =
                    {
                        UnexpectedFileAccessesAreErrors = true,
                        IgnoreReparsePoints = false,
                        IgnoreFullReparsePointResolving = true,
                        IgnoreSetFileInformationByHandle = false,
                        IgnoreZwRenameFileInformation = false,
                        IgnoreZwOtherFileInformation = false,
                        IgnoreNonCreateFileReparsePoints = false,
                        MonitorZwCreateOpenQueryFile = false,
                        MonitorNtCreateFile = true,
                        ProbeDirectorySymlinkAsDirectory = false,
                    }
                };

                await AssertProcessSucceedsAsync(
                    context,
                    sandboxConfiguration,
                    pip);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["CreateNoSymbolicLinkTest2.txt"], RequestedAccess.Read, FileAccessStatus.Allowed),
                        (createdInputPaths["CreateNoSymbolicLinkTest1.txt"], RequestedAccess.Write, FileAccessStatus.Allowed)
                    });
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallAccessSymLinkOnFilesWithGrantedAccess()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "AccessSymLinkOnFiles1.txt",
                    "AccessSymLinkOnFiles2.txt",
                    "CallAccessSymLinkOnFiles",
                    isDirectoryTest: false,
                    createSymlink: true,
                    addCreateFileInDirectoryToDependencies: true,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsDependency,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["AccessSymLinkOnFiles2.txt"], RequestedAccess.Read, FileAccessStatus.Allowed),
                        (createdInputPaths["AccessSymLinkOnFiles1.txt"], RequestedAccess.Read, FileAccessStatus.Allowed)
                    });
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallAccessSymLinkOnFilesWithNoGrantedAccess()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "AccessSymLinkOnFiles1.txt",
                    "AccessSymLinkOnFiles2.txt",
                    "CallAccessSymLinkOnFiles",
                    isDirectoryTest: false,
                    createSymlink: true,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsDependency,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.None,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                // Error exit code and access denied.
                SetExpectedFailures(1, 0);

                VerifyAccessDenied(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["AccessSymLinkOnFiles2.txt"], RequestedAccess.Read, FileAccessStatus.Denied),
                        (createdInputPaths["AccessSymLinkOnFiles1.txt"], RequestedAccess.Read, FileAccessStatus.Allowed)
                    });
            }
        }

        [Fact]
        public async Task TestDirectoryEnumeration()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
                string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
                XAssert.IsTrue(File.Exists(executable));
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                AbsolutePath workingDirectory = AbsolutePath.Create(pathTable, tempFiles.RootDirectory);

                string testDirPath = tempFiles.GetDirectory("input");

                string firstTestFile = Path.Combine(testDirPath, "Test1.txt");
                AbsolutePath firstAbsPath = AbsolutePath.Create(pathTable, firstTestFile);
                FileArtifact firstFileArtifact = FileArtifact.CreateSourceFile(firstAbsPath);
                WriteFile(firstTestFile);

                string secondTestFile = Path.Combine(testDirPath, "Test2.txt");
                AbsolutePath secondAbsPath = AbsolutePath.Create(pathTable, secondTestFile);
                FileArtifact secondFileArtifact = FileArtifact.CreateSourceFile(secondAbsPath);
                WriteFile(secondTestFile);

                var allDependencies = new List<FileArtifact>(2)
                {
                    executableFileArtifact
                };

                var arguments = new PipDataBuilder(pathTable.StringTable);
                arguments.Add("CallDirectoryEnumerationTest");

                var environmentVariables = new List<EnvironmentVariable>();
                Process pip = new Process(
                    executableFileArtifact,
                    workingDirectory,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(pathTable),
                    null,
                    null,
                    dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy(allDependencies.ToArray<FileArtifact>()),
                    outputs: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    directoryDependencies: ReadOnlyArray<DirectoryArtifact>.Empty,
                    directoryOutputs: ReadOnlyArray<DirectoryArtifact>.Empty,
                    orderDependencies: ReadOnlyArray<PipId>.Empty,
                    untrackedPaths: ReadOnlyArray<AbsolutePath>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty,
                    tags: ReadOnlyArray<StringId>.Empty,
                    successExitCodes: ReadOnlyArray<int>.FromWithoutCopy([0]),
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                XAssert.AreEqual(1, result.ObservedFileAccesses.Count());
                XAssert.AreEqual(testDirPath, result.ObservedFileAccesses[0].Path.ToString(pathTable));
                XAssert.AreEqual(RequestedAccess.Enumerate, result.ObservedFileAccesses[0].Accesses.First().RequestedAccess);
            }
        }

        [Theory]
        [MemberData(nameof(TruthTable.GetTable), 3, MemberType = typeof(TruthTable))]
        public async Task TestDeleteFile(bool unexpectedFileAccessesAreErrors, bool useStdRemove, bool existingFile)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
                string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
                XAssert.IsTrue(File.Exists(executable));
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                AbsolutePath workingDirectory = AbsolutePath.Create(pathTable, tempFiles.RootDirectory);

                string testDirPath = tempFiles.GetDirectory("input");

                string firstTestFile = Path.Combine(testDirPath, "Test1.txt");
                AbsolutePath firstAbsPath = AbsolutePath.Create(pathTable, firstTestFile);
                FileArtifact firstFileArtifact = FileArtifact.CreateSourceFile(firstAbsPath);

                if (existingFile)
                {
                    WriteFile(firstTestFile);
                }
                else
                {
                    XAssert.IsFalse(File.Exists(firstTestFile));
                }

                var allDependencies = new List<FileArtifact>(2)
                {
                    executableFileArtifact
                };

                var arguments = new PipDataBuilder(pathTable.StringTable);
                arguments.Add(useStdRemove ? "CallDeleteFileStdRemoveTest" : "CallDeleteFileTest");

                var environmentVariables = new List<EnvironmentVariable>();
                Process pip = new Process(
                    executableFileArtifact,
                    workingDirectory,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(pathTable),
                    null,
                    null,
                    dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy(allDependencies.ToArray<FileArtifact>()),
                    outputs: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    directoryDependencies: ReadOnlyArray<DirectoryArtifact>.Empty,
                    directoryOutputs: ReadOnlyArray<DirectoryArtifact>.Empty,
                    orderDependencies: ReadOnlyArray<PipId>.Empty,
                    untrackedPaths: ReadOnlyArray<AbsolutePath>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty,
                    tags: ReadOnlyArray<StringId>.Empty,
                    successExitCodes: ReadOnlyArray<int>.FromWithoutCopy([0]),
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    unexpectedFileAccessesAreErrors: unexpectedFileAccessesAreErrors,
                    context: context,
                    pip: pip,
                    errorString: out _);

                if (existingFile)
                {
                    if (unexpectedFileAccessesAreErrors)
                    {
                        // Failed to delete existing file due to access denied.
                        AssertVerboseEventLogged(ProcessesLogEventId.PipProcessDisallowedFileAccess);
                        SetExpectedFailures(1, 0);
                        VerifyAccessDenied(context, result);

                        XAssert.IsTrue(File.Exists(firstTestFile));
                    }
                    else
                    {
                        VerifyNormalSuccess(context, result);
                        XAssert.IsFalse(File.Exists(firstTestFile));
                    }

                    // Deleting an existing file is considered a write access.
                    VerifyFileAccesses(
                        context,
                        result.AllReportedFileAccesses,
                        new[]
                        {
                            (firstAbsPath, RequestedAccess.Write, FileAccessStatus.Denied),
                        });
                }
                else
                {
                    if (unexpectedFileAccessesAreErrors)
                    {
                        // There's no disallowed access because the file doesn't exist, so the delete operation is considered a probe access.
                        SetExpectedFailures(1, 0);
                        VerifyAccessDenied(context, result);
                    }
                    else
                    {
                        SetExpectedFailures(1, 0);
                        VerifyExecutionStatus(context, result, SandboxedProcessPipExecutionStatus.ExecutionFailed);
                        VerifyExitCode(context, result, NativeIOConstants.ErrorFileNotFound);
                    }

                    XAssert.IsFalse(File.Exists(firstTestFile));

                    // Deleting an absent file is considered a probe access.
                    VerifyFileAccesses(
                        context,
                        result.AllReportedFileAccesses,
                        new[]
                        {
                            (firstAbsPath, RequestedAccess.Probe, FileAccessStatus.Allowed),
                        });
                }
            }
        }

        [Theory]
        [MemberData(nameof(TruthTable.GetTable), 1, MemberType = typeof(TruthTable))]
        public async Task TestDeleteDirectory(bool unexpectedFileAccessesAreErrors)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
                string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
                XAssert.IsTrue(File.Exists(executable));
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                AbsolutePath workingDirectory = AbsolutePath.Create(pathTable, tempFiles.RootDirectory);

                string testDirPath = tempFiles.GetDirectory("input");
                AbsolutePath inputDirPath = AbsolutePath.Create(pathTable, testDirPath);

                string firstTestFile = Path.Combine(testDirPath, "Test1.txt");
                AbsolutePath firstAbsPath = AbsolutePath.Create(pathTable, firstTestFile);
                FileArtifact firstFileArtifact = FileArtifact.CreateSourceFile(firstAbsPath);
                WriteFile(firstTestFile);

                var allDependencies = new List<FileArtifact>(2)
                {
                    executableFileArtifact
                };

                var arguments = new PipDataBuilder(pathTable.StringTable);
                arguments.Add("CallDeleteDirectoryTest");

                var environmentVariables = new List<EnvironmentVariable>();
                Process pip = new Process(
                    executableFileArtifact,
                    workingDirectory,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(pathTable),
                    null,
                    null,
                    dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy(allDependencies.ToArray<FileArtifact>()),
                    outputs: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    directoryDependencies: ReadOnlyArray<DirectoryArtifact>.Empty,
                    directoryOutputs: ReadOnlyArray<DirectoryArtifact>.Empty,
                    orderDependencies: ReadOnlyArray<PipId>.Empty,
                    untrackedPaths: ReadOnlyArray<AbsolutePath>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty,
                    tags: ReadOnlyArray<StringId>.Empty,
                    successExitCodes: ReadOnlyArray<int>.FromWithoutCopy([0]),
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    unexpectedFileAccessesAreErrors: unexpectedFileAccessesAreErrors,
                    context: context,
                    pip: pip,
                    errorString: out _);

                if (unexpectedFileAccessesAreErrors)
                {
                    SetExpectedFailures(1, 0);
                    AssertVerboseEventLogged(ProcessesLogEventId.PipProcessDisallowedFileAccess);

                    VerifyAccessDenied(context, result);

                    XAssert.IsTrue(File.Exists(firstTestFile));

                    VerifyFileAccesses(
                        context,
                        result.AllReportedFileAccesses,
                        new[]
                        {
                            (inputDirPath, RequestedAccess.Write, FileAccessStatus.Denied),
                        },
                        [
                            firstAbsPath
                        ]);
                }
                else
                {
                    SetExpectedFailures(1, 0);

                    VerifyExecutionStatus(context, result, SandboxedProcessPipExecutionStatus.ExecutionFailed);
                    VerifyExitCode(context, result, 145); // Directory is no empty.

                    XAssert.IsTrue(File.Exists(firstTestFile));

                    VerifyFileAccesses(
                        context,
                        result.AllReportedFileAccesses,
                        new[]
                        {
                            (inputDirPath, RequestedAccess.Write, FileAccessStatus.Denied),
                            (firstAbsPath, RequestedAccess.Write, FileAccessStatus.Denied),
                        });
                }
            }
        }

        [Fact]
        public async Task TestDeleteDirectoryWithAccess()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
                string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
                XAssert.IsTrue(File.Exists(executable));
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                string workingDirectory = tempFiles.RootDirectory;
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                string testDirPath = Path.Combine(workingDirectory, "input");
                tempFiles.GetDirectory("input");
                AbsolutePath inputDirPath = AbsolutePath.Create(pathTable, testDirPath);

                string firstTestFile = Path.Combine(testDirPath, "Test1.txt");
                AbsolutePath firstAbsPath = AbsolutePath.Create(pathTable, firstTestFile);
                FileArtifact firstFileArtifact = FileArtifact.CreateSourceFile(firstAbsPath);

                WriteFile(firstTestFile);

                var untrackedPaths = new List<AbsolutePath>
                {
                    inputDirPath,
                    firstAbsPath
                };

                var allDependencies = new List<FileArtifact>(2)
                {
                    executableFileArtifact
                };

                var arguments = new PipDataBuilder(pathTable.StringTable);
                arguments.Add("CallDeleteDirectoryTest");

                var environmentVariables = new List<EnvironmentVariable>();
                Process pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(pathTable),
                    null,
                    null,
                    dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy(allDependencies.ToArray<FileArtifact>()),
                    outputs: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    directoryDependencies: ReadOnlyArray<DirectoryArtifact>.Empty,
                    directoryOutputs: ReadOnlyArray<DirectoryArtifact>.Empty,
                    orderDependencies: ReadOnlyArray<PipId>.Empty,
                    untrackedPaths: ReadOnlyArray<AbsolutePath>.From(untrackedPaths),
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty,
                    tags: ReadOnlyArray<StringId>.Empty,
                    // We expect the CreateFile call to fail, but with no monitoring error logged.
                    successExitCodes: ReadOnlyArray<int>.FromWithoutCopy([0]),
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                SetExpectedFailures(1, 0);

                VerifyExecutionStatus(context, result, SandboxedProcessPipExecutionStatus.ExecutionFailed);
                VerifyExitCode(context, result, 145);

                XAssert.IsTrue(File.Exists(firstTestFile));

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (inputDirPath, RequestedAccess.Write, FileAccessStatus.Allowed),
                        (firstAbsPath, RequestedAccess.Write, FileAccessStatus.Allowed),
                    });
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallAccessSymLinkOnDirectoriesWithGrantedAccess()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "AccessSymLinkOnDirectories1.dir",
                    "AccessSymLinkOnDirectories2.dir",
                    "CallAccessSymLinkOnDirectories",
                    isDirectoryTest: true,
                    createSymlink: true,
                    addCreateFileInDirectoryToDependencies: true,
                    createFileInDirectory: true,
                    addFirstFileKind: AddFileOrDirectoryKinds.None,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.None,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        // TODO: Currently BuildXL does not handle directory junction, so the access via AccessSymLinkOnDirectories2.dir is not recognized.
                        // (createdInputPaths[Path.Combine("AccessSymLinkOnDirectories2.dir", ExtraFileNameInDirectory)], RequestedAccess.Read, FileAccessStatus.Allowed),
                        (
                            createdInputPaths[Path.Combine("AccessSymLinkOnDirectories1.dir", ExtraFileNameInDirectory)],
                            RequestedAccess.Read,
                            FileAccessStatus.Allowed)
                    });
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallAccessSymLinkOnDirectoriesWithNoGrantedAccess()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "AccessSymLinkOnDirectories1.dir",
                    "AccessSymLinkOnDirectories2.dir",
                    "CallAccessSymLinkOnDirectories",
                    isDirectoryTest: true,
                    createSymlink: true,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: true,
                    addFirstFileKind: AddFileOrDirectoryKinds.None,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.None,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                SetExpectedFailures(1, 0);

                VerifyAccessDenied(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        // TODO: Currently BuildXL does not handle directory junction, so the access via AccessSymLinkOnDirectories2.dir is not recognized.
                        // (createdInputPaths[Path.Combine("AccessSymLinkOnDirectories2.dir", ExtraFileNameInDirectory)], RequestedAccess.Read, FileAccessStatus.Denied),
                        (
                            createdInputPaths[Path.Combine("AccessSymLinkOnDirectories1.dir", ExtraFileNameInDirectory)],
                            RequestedAccess.Read,
                            FileAccessStatus.Denied)
                    });
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallCreateSymLinkOnFilesWithGrantedAccess()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateSymLinkOnFiles1.txt",
                    "CreateSymLinkOnFiles2.txt",
                    "CallCreateSymLinkOnFiles",
                    isDirectoryTest: false,
                    // The C++ part will create the symlink.
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["CreateSymLinkOnFiles1.txt"], RequestedAccess.Write, FileAccessStatus.Allowed)
                    },
                    pathsToFalsify:
                    [
                        createdInputPaths["CreateSymLinkOnFiles2.txt"]
                    ]);
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallCreateAndDeleteSymLinkOnFilesWithGrantedAccess()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "SymlinkToIrrelevantExistingFile.lnk",
                    "IrrelevantExistingFile.txt",
                    "CallCreateAndDeleteSymLinkOnFiles",
                    isDirectoryTest: false,
                    // The C++ part will create the symlink.
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.None,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                // Create target file and ensure that it exists afterwards.
                AbsolutePath targetPath = createdInputPaths["IrrelevantExistingFile.txt"];
                WriteFile(context.PathTable, targetPath);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["SymlinkToIrrelevantExistingFile.lnk"], RequestedAccess.Write, FileAccessStatus.Allowed)
                    },
                    pathsToFalsify:
                    [
                        createdInputPaths["IrrelevantExistingFile.txt"]
                    ]);
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallMoveSymLinkOnFilesWithGrantedAccess()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                // Create an irrelevant file and ensure that it exists.
                var irrelevantFilePath = tempFiles.GetFileName(pathTable, "IrrelevantExistingFile.txt");
                WriteFile(pathTable, irrelevantFilePath);

                // Create OldSymlink -> IrrelevantFile
                var oldSymlink = tempFiles.GetFileName(pathTable, "OldSymlinkToIrrelevantExistingFile.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(oldSymlink.ToString(pathTable), irrelevantFilePath.ToString(pathTable), true));

                var newSymlink = tempFiles.GetFileName(pathTable, "NewSymlinkToIrrelevantExistingFile.lnk");

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallMoveSymLinkOnFilesNotEnforceChainSymLinkAccesses",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy(FileArtifact.CreateSourceFile(oldSymlink)),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(
                        FileArtifactWithAttributes.Create(FileArtifact.CreateOutputFile(oldSymlink), FileExistence.Optional),
                        FileArtifactWithAttributes.Create(FileArtifact.CreateOutputFile(newSymlink), FileExistence.Required)),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    ignoreNonCreateFileReparsePoints: false,
                    monitorZwCreateOpenQueryFile: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                XAssert.IsTrue(File.Exists(newSymlink.ToString(pathTable)));

                var toVerify = new[]
                {
                    (oldSymlink, RequestedAccess.Read | RequestedAccess.Write, FileAccessStatus.Allowed),
                    (newSymlink, RequestedAccess.Write, FileAccessStatus.Allowed)
                };

                var toFalsify = new[] { irrelevantFilePath };

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    toVerify,
                    toFalsify);
            }
        }

        [Theory]
        [InlineData("CallDetouredMoveFileExWForRenamingDirectory")]
        [InlineData("CallDetouredSetFileInformationByHandleForRenamingDirectory")]
        [InlineData("CallDetouredZwSetFileInformationByHandleForRenamingDirectory")]
        public async Task CallMoveDirectoryReportAllAccesses(string callArgument)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                // Create OldDirectory\, OldDirectory\fileImplicit.txt, and OldDirectory\fileExplicit.txt.
                AbsolutePath oldDirectory = tempFiles.GetDirectory(pathTable, "OldDirectory");
                AbsolutePath oldDirectoryFileImplicit = oldDirectory.Combine(pathTable, "fileImplicit.txt");
                AbsolutePath oldDirectoryFileExplicit = oldDirectory.Combine(pathTable, "fileExplicit.txt");
                WriteFile(pathTable, oldDirectoryFileImplicit);
                WriteFile(pathTable, oldDirectoryFileExplicit);

                // Create OldDirectory\Nested, OldDirectory\Nested\fileImplicit.txt, OldDirectory\Nested\fileExplicit.txt.
                AbsolutePath oldDirectoryNested = tempFiles.GetDirectory(pathTable, oldDirectory, "Nested");

                AbsolutePath oldDirectoryNestedFileImplicit = oldDirectoryNested.Combine(pathTable, "fileImplicit.txt");
                AbsolutePath oldDirectoryNestedFileExplicit = oldDirectoryNested.Combine(pathTable, "fileExplicit.txt");
                WriteFile(pathTable, oldDirectoryNestedFileImplicit);
                WriteFile(pathTable, oldDirectoryNestedFileExplicit);

                // Create OldDirectory\Nested\Nested, OldDirectory\Nested\Nested\fileImplicit.txt, OldDirectory\Nested\Nested\fileExplicit.txt.
                AbsolutePath oldDirectoryNestedNested = tempFiles.GetDirectory(pathTable, oldDirectoryNested, "Nested");

                AbsolutePath oldDirectoryNestedNestedFileImplicit = oldDirectoryNestedNested.Combine(pathTable, "fileImplicit.txt");
                AbsolutePath oldDirectoryNestedNestedFileExplicit = oldDirectoryNestedNested.Combine(pathTable, "fileExplicit.txt");
                WriteFile(pathTable, oldDirectoryNestedNestedFileImplicit);
                WriteFile(pathTable, oldDirectoryNestedNestedFileExplicit);

                var oldDirectories = new AbsolutePath[]
                {
                    oldDirectory,
                    oldDirectoryNested,
                    oldDirectoryNestedNested,
                };

                var oldFiles = new AbsolutePath[]
                {
                    oldDirectoryFileExplicit,
                    oldDirectoryFileImplicit,
                    oldDirectoryNestedFileExplicit,
                    oldDirectoryNestedFileImplicit,
                    oldDirectoryNestedNestedFileExplicit,
                    oldDirectoryNestedNestedFileImplicit
                };

                AbsolutePath outputDirectory = oldDirectory.GetParent(pathTable).Combine(pathTable, "OutputDirectory");
                AbsolutePath tempDirectory = oldDirectory.GetParent(pathTable).Combine(pathTable, "TempDirectory");
                AbsolutePath newDirectory = outputDirectory.Combine(pathTable, "NewDirectory");
                var newDirectories = oldDirectories.Select(p => p.Relocate(pathTable, oldDirectory, newDirectory)).ToArray();
                var newFiles = oldFiles.Select(p => p.Relocate(pathTable, oldDirectory, newDirectory)).ToArray();

                FileUtilities.CreateDirectory(outputDirectory.ToString(pathTable));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: callArgument,
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.FromWithoutCopy(tempDirectory));

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    ignoreNonCreateFileReparsePoints: false,
                    monitorZwCreateOpenQueryFile: false,
                    enforceAccessPoliciesOnDirectoryCreation: true,
                    unexpectedFileAccessesAreErrors: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                foreach (var path in oldDirectories)
                {
                    XAssert.IsFalse(Directory.Exists(path.ToString(pathTable)));
                }

                foreach (var path in oldFiles)
                {
                    XAssert.IsFalse(File.Exists(path.ToString(pathTable)));
                }

                foreach (var path in newDirectories)
                {
                    XAssert.IsTrue(Directory.Exists(path.ToString(pathTable)));
                }

                foreach (var path in newFiles)
                {
                    XAssert.IsTrue(File.Exists(path.ToString(pathTable)));
                }

                var toVerify = newDirectories
                    .Concat(newFiles)
                    .Concat(oldDirectories)
                    .Concat(oldFiles)
                    .Select(p => (p, RequestedAccess.Write, FileAccessStatus.Denied)).ToArray();

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    toVerify,
                    []);
            }
        }

        [Theory]
        [InlineData("CallDetouredMoveFileExWForRenamingDirectory")]
        [InlineData("CallDetouredSetFileInformationByHandleForRenamingDirectory")]
        [InlineData("CallDetouredZwSetFileInformationByHandleExForRenamingDirectory")]
        [InlineData("CallDetouredZwSetFileInformationByHandleByPassForRenamingDirectory")]
        [InlineData("CallDetouredZwSetFileInformationByHandleExByPassForRenamingDirectory")]
        public async Task CallMoveDirectoryReportSelectiveAccesses(string callArgument)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                // Create OldDirectory\, OldDirectory\fileImplicit.txt, and OldDirectory\fileExplicit.txt.
                AbsolutePath oldDirectory = tempFiles.GetDirectory(pathTable, "OldDirectory");
                AbsolutePath oldDirectoryFileImplicit = oldDirectory.Combine(pathTable, "fileImplicit.txt");
                AbsolutePath oldDirectoryFileExplicit = oldDirectory.Combine(pathTable, "fileExplicit.txt");
                WriteFile(pathTable, oldDirectoryFileImplicit);
                WriteFile(pathTable, oldDirectoryFileExplicit);

                // Create OldDirectory\Nested, OldDirectory\Nested\fileImplicit.txt, OldDirectory\Nested\fileExplicit.txt.
                AbsolutePath oldDirectoryNested = tempFiles.GetDirectory(pathTable, oldDirectory, "Nested");

                AbsolutePath oldDirectoryNestedFileImplicit = oldDirectoryNested.Combine(pathTable, "fileImplicit.txt");
                AbsolutePath oldDirectoryNestedFileExplicit = oldDirectoryNested.Combine(pathTable, "fileExplicit.txt");
                WriteFile(pathTable, oldDirectoryNestedFileImplicit);
                WriteFile(pathTable, oldDirectoryNestedFileExplicit);

                // Create OldDirectory\Nested\Nested, OldDirectory\Nested\Nested\fileImplicit.txt, OldDirectory\Nested\Nested\fileExplicit.txt.
                AbsolutePath oldDirectoryNestedNested = tempFiles.GetDirectory(pathTable, oldDirectoryNested, "Nested");

                AbsolutePath oldDirectoryNestedNestedFileImplicit = oldDirectoryNestedNested.Combine(pathTable, "fileImplicit.txt");
                AbsolutePath oldDirectoryNestedNestedFileExplicit = oldDirectoryNestedNested.Combine(pathTable, "fileExplicit.txt");
                WriteFile(pathTable, oldDirectoryNestedNestedFileImplicit);
                WriteFile(pathTable, oldDirectoryNestedNestedFileExplicit);

                var oldDirectories = new AbsolutePath[]
                {
                    oldDirectory,
                    oldDirectoryNested,
                    oldDirectoryNestedNested,
                };

                var oldFiles = new AbsolutePath[]
                {
                    oldDirectoryFileExplicit,
                    oldDirectoryFileImplicit,
                    oldDirectoryNestedFileExplicit,
                    oldDirectoryNestedFileImplicit,
                    oldDirectoryNestedNestedFileExplicit,
                    oldDirectoryNestedNestedFileImplicit
                };

                AbsolutePath tempDirectory = oldDirectory.GetParent(pathTable).Combine(pathTable, "TempDirectory");
                AbsolutePath outputDirectory = oldDirectory.GetParent(pathTable).Combine(pathTable, "OutputDirectory");
                AbsolutePath newDirectory = outputDirectory.Combine(pathTable, "NewDirectory");
                var newDirectoryNested = oldDirectoryNested.Relocate(pathTable, oldDirectory, newDirectory);
                var newDirectoryNestedNested = oldDirectoryNestedNested.Relocate(pathTable, oldDirectory, newDirectory);

                var newDirectories = new AbsolutePath[]
                {
                    newDirectory,
                    newDirectoryNested,
                    newDirectoryNestedNested
                };

                var newDirectoryFileExplicit = oldDirectoryFileExplicit.Relocate(pathTable, oldDirectory, newDirectory);
                var newDirectoryNestedFileExplicit = oldDirectoryNestedFileExplicit.Relocate(pathTable, oldDirectory, newDirectory);
                var newDirectoryNestedNestedFileExplicit = oldDirectoryNestedNestedFileExplicit.Relocate(pathTable, oldDirectory, newDirectory);
                var newDirectoryFileImplicit = oldDirectoryFileImplicit.Relocate(pathTable, oldDirectory, newDirectory);
                var newDirectoryNestedFileImplicit = oldDirectoryNestedFileImplicit.Relocate(pathTable, oldDirectory, newDirectory);
                var newDirectoryNestedNestedFileImplicit = oldDirectoryNestedNestedFileImplicit.Relocate(pathTable, oldDirectory, newDirectory);

                var newExplicitFiles = new AbsolutePath[]
                {
                    newDirectoryFileExplicit,
                    newDirectoryNestedFileExplicit,
                    newDirectoryNestedNestedFileExplicit,
                };

                var newImplicitFiles = new AbsolutePath[]
                {
                    newDirectoryFileImplicit,
                    newDirectoryNestedFileImplicit,
                    newDirectoryNestedNestedFileImplicit,
                };

                var newFiles = newExplicitFiles.Concat(newImplicitFiles).ToArray();

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: callArgument,
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.From(
                        newExplicitFiles.Select(f => FileArtifactWithAttributes.Create(FileArtifact.CreateOutputFile(f), FileExistence.Required))),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.FromWithoutCopy(DirectoryArtifact.CreateWithZeroPartialSealId(outputDirectory)),
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.FromWithoutCopy(oldDirectory, tempDirectory));

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    ignoreNonCreateFileReparsePoints: false,
                    monitorZwCreateOpenQueryFile: false,
                    enforceAccessPoliciesOnDirectoryCreation: true,
                    unexpectedFileAccessesAreErrors: true,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                foreach (var path in oldDirectories)
                {
                    XAssert.IsFalse(Directory.Exists(path.ToString(pathTable)));
                }

                foreach (var path in oldFiles)
                {
                    XAssert.IsFalse(File.Exists(path.ToString(pathTable)));
                }

                foreach (var path in newDirectories)
                {
                    XAssert.IsTrue(Directory.Exists(path.ToString(pathTable)));
                }

                foreach (var path in newFiles)
                {
                    XAssert.IsTrue(File.Exists(path.ToString(pathTable)));
                }

                var toVerify = newExplicitFiles.Select(p => (p, RequestedAccess.Write, FileAccessStatus.Allowed)).ToArray();
                var toFalsify = newImplicitFiles.Concat(newDirectories).Concat(oldDirectories).Concat(oldFiles).ToArray();

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses.Where(f => f.ExplicitlyReported).ToList(),
                    toVerify,
                    toFalsify);
            }
        }

        [Fact]
        public async Task CallCreateFileWReportAllAccess()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                AbsolutePath createdFile = tempFiles.GetFileName(pathTable, "CreateFile");

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDetouredCreateFileWWrite",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    ignoreNonCreateFileReparsePoints: false,
                    monitorZwCreateOpenQueryFile: false,
                    enforceAccessPoliciesOnDirectoryCreation: true,
                    unexpectedFileAccessesAreErrors: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[] { (createdFile, RequestedAccess.Write, FileAccessStatus.Denied) },
                    []);
            }
        }

        [Fact]
        public async Task CallOpenFileById()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                // Create Directory\, Directory\fileF.txt
                AbsolutePath directory = tempFiles.GetDirectory(pathTable, "Directory");
                AbsolutePath fileF = directory.Combine(pathTable, "fileF.txt");
                AbsolutePath fileG = directory.Combine(pathTable, "fileG.txt");
                WriteFile(pathTable, fileF);

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallOpenFileById",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy(FileArtifact.CreateSourceFile(fileF)),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(
                        FileArtifactWithAttributes.FromFileArtifact(FileArtifact.CreateOutputFile(fileG), FileExistence.Required)),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    ignoreNonCreateFileReparsePoints: false,
                    monitorZwCreateOpenQueryFile: true,
                    enforceAccessPoliciesOnDirectoryCreation: true,
                    unexpectedFileAccessesAreErrors: true,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (fileF, RequestedAccess.Read, FileAccessStatus.Allowed),
                        (fileG, RequestedAccess.Write, FileAccessStatus.Allowed)
                    },
                    []);
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallCreateSymLinkOnFilesWithGrantedAccessAndIgnoreNoTempNoUntracked()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateSymLinkOnFiles1.txt",
                    "CreateSymLinkOnFiles2.txt",
                    "CallCreateSymLinkOnFiles",
                    isDirectoryTest: false,
                    // The C++ part will create the symlink.
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["CreateSymLinkOnFiles1.txt"], RequestedAccess.Write, FileAccessStatus.Allowed)
                    },
                    pathsToFalsify:
                    [
                        createdInputPaths["CreateSymLinkOnFiles2.txt"]
                    ]);
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallCreateSymLinkOnFilesWithGrantedAccessAndNoIgnoreTempNoUntracked()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);
                AbsolutePath testDirectoryAbsolutePath = tempFiles.GetDirectory(pathTable, "input");
                AbsolutePath testFilePath = tempFiles.GetFileName(pathTable, testDirectoryAbsolutePath, "CreateSymLinkOnFiles1.txt");
                List<AbsolutePath> testDirList = new List<AbsolutePath>();
                testDirList.Add(testFilePath);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateSymLinkOnFiles1.txt",
                    "CreateSymLinkOnFiles2.txt",
                    "CallCreateSymLinkOnFiles",
                    isDirectoryTest: false,
                    // The C++ part will create the symlink.
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths,
                    additionalTempDirectories: testDirList);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["CreateSymLinkOnFiles1.txt"], RequestedAccess.Write, FileAccessStatus.Allowed)
                    },
                    pathsToFalsify:
                    [
                        createdInputPaths["CreateSymLinkOnFiles2.txt"]
                    ]);
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallCreateSymLinkOnFilesWithGrantedAccessAndIgnoreTempNoUntracked()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);
                AbsolutePath testDirectoryAbsolutePath = tempFiles.GetDirectory(pathTable, "input");
                AbsolutePath testFilePath = tempFiles.GetFileName(pathTable, testDirectoryAbsolutePath, "CreateSymLinkOnFiles1.txt");
                var testDirList = new List<AbsolutePath> { testFilePath };

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateSymLinkOnFiles1.txt",
                    "CreateSymLinkOnFiles2.txt",
                    "CallCreateSymLinkOnFiles",
                    isDirectoryTest: false,
                    // The C++ part will create the symlink.
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths,
                    additionalTempDirectories: testDirList);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["CreateSymLinkOnFiles1.txt"], RequestedAccess.Write, FileAccessStatus.Allowed)
                    },
                    pathsToFalsify:
                    [
                        createdInputPaths["CreateSymLinkOnFiles2.txt"]
                    ]);
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallCreateSymLinkOnFilesWithGrantedAccessAndNoIgnoreNoTempUntracked()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);
                AbsolutePath testDirectoryAbsolutePath = tempFiles.GetDirectory(pathTable, "input");
                AbsolutePath testFilePath = tempFiles.GetFileName(pathTable, testDirectoryAbsolutePath, "CreateSymLinkOnFiles1.txt");
                var testDirList = new List<AbsolutePath> { testFilePath };

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateSymLinkOnFiles1.txt",
                    "CreateSymLinkOnFiles2.txt",
                    "CallCreateSymLinkOnFiles",
                    isDirectoryTest: false,
                    // The C++ part will create the symlink.
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths,
                    untrackedPaths: testDirList);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["CreateSymLinkOnFiles1.txt"], RequestedAccess.Write, FileAccessStatus.Allowed)
                    },
                    pathsToFalsify:
                    [
                        createdInputPaths["CreateSymLinkOnFiles2.txt"]
                    ]);
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallCreateSymLinkOnFilesWithGrantedAccessAndIgnoreNoTempUntracked()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);
                AbsolutePath testDirectoryAbsolutePath = tempFiles.GetDirectory(pathTable, "input");
                AbsolutePath testFilePath = tempFiles.GetFileName(pathTable, testDirectoryAbsolutePath, "CreateSymLinkOnFiles1.txt");
                var testDirList = new List<AbsolutePath> { testFilePath };

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateSymLinkOnFiles1.txt",
                    "CreateSymLinkOnFiles2.txt",
                    "CallCreateSymLinkOnFiles",
                    isDirectoryTest: false,
                    // The C++ part will create the symlink.
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths,
                    untrackedPaths: testDirList);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["CreateSymLinkOnFiles1.txt"], RequestedAccess.Write, FileAccessStatus.Allowed)
                    },
                    pathsToFalsify:
                    [
                        createdInputPaths["CreateSymLinkOnFiles2.txt"]
                    ]);
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallCreateSymLinkOnFilesWithGrantedAccessAndIgnoreNoTempUntrackedOpaque()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);
                AbsolutePath testDirectoryAbsolutePath = tempFiles.GetDirectory(pathTable, "input");
                AbsolutePath testFilePath = AbsolutePath.Create(pathTable, Path.Combine(testDirectoryAbsolutePath.ToString(pathTable), "CreateSymLinkOnFiles1.txt"));
                DirectoryArtifact dirArt = DirectoryArtifact.CreateWithZeroPartialSealId(testFilePath);
                var dirArtifactList = new List<DirectoryArtifact> { dirArt };

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateSymLinkOnFiles1.txt",
                    "CreateSymLinkOnFiles2.txt",
                    "CallCreateSymLinkOnFiles",
                    isDirectoryTest: false,
                    // The C++ part will create the symlink.
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.None,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths,
                    outputDirectories: dirArtifactList);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                SetExpectedFailures(1, 0);

                VerifyAccessDenied(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (createdInputPaths["CreateSymLinkOnFiles1.txt"], RequestedAccess.Write, FileAccessStatus.Allowed)
                    },
                    pathsToFalsify:
                    [
                        createdInputPaths["CreateSymLinkOnFiles2.txt"]
                    ]);
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallCreateSymLinkOnFilesWithNoGrantedAccess()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateSymLinkOnFiles1.txt",
                    "CreateSymLinkOnFiles2.txt",
                    "CallCreateSymLinkOnFiles",
                    isDirectoryTest: false,
                    // The C++ part will create the symlink.
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.None,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.None,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                SetExpectedFailures(1, 0);

                VerifyAccessDenied(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[] { (createdInputPaths["CreateSymLinkOnFiles1.txt"], RequestedAccess.Write, FileAccessStatus.Denied) },
                    pathsToFalsify: [createdInputPaths["CreateSymLinkOnFiles2.txt"]]);
            }
        }

        [Fact(Skip = "No support for directory junctions as outputs")]
        public async Task CallCreateSymLinkOnDirectoriesWithGrantedAccess()
        {
            // TODO:
            // If output directory junction is marked as an output file, then the post verification that checks for file existence will fail.
            // If output directory junction is marked as an output directory, then there's no write policy for that output directory because BuildXL
            // creates output directories prior to executing the process.
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateSymLinkOnDirectories1.dir",
                    "CreateSymLinkOnDirectories2.dir",
                    "CallCreateSymLinkOnDirectories",
                    isDirectoryTest: true,
                    // The C++ part creates the symlink.
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);
            }
        }

        [Fact(Skip = "No support for directory junctions as outputs")]
        public async Task CallCreateSymLinkOnDirectoriesWithNoGrantedAccess()
        {
            // TODO:
            // If output directory junction is marked as an output file, then the post verification that checks for file existence will fail.
            // If output directory junction is marked as an output directory, then there's no write policy for that output directory because BuildXL
            // creates output directories prior to executing the process.
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateSymLinkOnDirectories1.dir",
                    "CreateSymLinkOnDirectories2.dir",
                    "CallCreateSymLinkOnDirectories",
                    isDirectoryTest: true,
                    // The C++ part creates the symlink.
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                SetExpectedFailures(2, 0);

                VerifyAccessDenied(context, result);
            }
        }

        [Fact]
        public async Task CreateFileWithZeroAccessOnDirectory()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
                string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
                XAssert.IsTrue(File.Exists(executable));
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                string workingDirectory = tempFiles.RootDirectory;
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                string testDirPath = Path.Combine(workingDirectory, "input");
                tempFiles.GetDirectory("input");

                var arguments = new PipDataBuilder(pathTable.StringTable);
                arguments.Add("CallCreateFileWithZeroAccessOnDirectory");

                var environmentVariables = new List<EnvironmentVariable>();

                var untrackedPaths = CmdHelper.GetCmdDependencies(pathTable);
                var untrackedScopes = CmdHelper.GetCmdDependencyScopes(pathTable);
                var pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(pathTable),
                    null,
                    null,
                    dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy([executableFileArtifact]),
                    outputs: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    directoryDependencies: ReadOnlyArray<DirectoryArtifact>.Empty,
                    directoryOutputs: ReadOnlyArray<DirectoryArtifact>.Empty,
                    orderDependencies: ReadOnlyArray<PipId>.Empty,
                    untrackedPaths: ReadOnlyArray<AbsolutePath>.From(untrackedPaths),
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.From(untrackedScopes),
                    tags: ReadOnlyArray<StringId>.Empty,
                    // We expect the CreateFile call to fail, but with no monitoring error logged.
                    successExitCodes: ReadOnlyArray<int>.FromWithoutCopy([5]),
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                var sandboxConfiguration = new SandboxConfiguration
                {
                    FileAccessIgnoreCodeCoverage = true
                };

                sandboxConfiguration.UnsafeSandboxConfigurationMutable.UnexpectedFileAccessesAreErrors = true;

                await AssertProcessSucceedsAsync(context, sandboxConfiguration, pip);
            }
        }

        [Fact]
        public Task TimestampsNormalize() => Timestamps(normalize: true);

        [Fact]
        public Task TimestampsNoNormalize() => Timestamps(normalize: false);

        public async Task Timestamps(bool normalize)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
                string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
                XAssert.IsTrue(File.Exists(executable));
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                string workingDirectory = tempFiles.RootDirectory;
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                FileArtifact inputArtifact = WriteFile(pathTable, tempFiles.GetFileName(pathTable, workingDirectoryAbsolutePath, "input"));
                FileArtifact outputToRewrite = WriteFile(pathTable, tempFiles.GetFileName(pathTable, workingDirectoryAbsolutePath, "rewrittenOutput"));
                FileArtifact outputAfterRewrite = outputToRewrite.CreateNextWrittenVersion();

                string inputSubdirectoryPath = Path.Combine(workingDirectory, "subdir");
                tempFiles.GetDirectory("subdir");
                var inputSubdirectory = DirectoryArtifact.CreateWithZeroPartialSealId(AbsolutePath.Create(pathTable, inputSubdirectoryPath));

                // subdirInput1 and subdirInput2 are brought in via a directory scope (inputSubdirectory) so as to exercise extension of the
                // Read+Report policy of the directory to the wildcard matches (a 'truncated' policy search cursor; see PolicySearch.h
                FileArtifact subdirInput1 = WriteFile(pathTable, tempFiles.GetFileName(pathTable, AbsolutePath.Create(pathTable, inputSubdirectoryPath), "input1"));
                FileArtifact subdirInput2 = WriteFile(pathTable, tempFiles.GetFileName(pathTable, AbsolutePath.Create(pathTable, inputSubdirectoryPath), "input2"));
                FileArtifact subdirRewrittenOutput1BeforeWrite = WriteFile(pathTable, tempFiles.GetFileName(pathTable, AbsolutePath.Create(pathTable, inputSubdirectoryPath), "rewrittenOutput1"));
                FileArtifact subdirRewrittenOutput1AfterWrite = subdirRewrittenOutput1BeforeWrite.CreateNextWrittenVersion();
                FileArtifact subdirRewrittenOutput2BeforeWrite = WriteFile(pathTable, tempFiles.GetFileName(pathTable, AbsolutePath.Create(pathTable, inputSubdirectoryPath), "rewrittenOutput2"));
                FileArtifact subdirRewrittenOutput2AfterWrite = subdirRewrittenOutput2BeforeWrite.CreateNextWrittenVersion();

                // Create artifacts to be contained in a shared opaque

                string sharedOpaqueSubdirectoryPath = Path.Combine(workingDirectory, "sharedOpaque");
                tempFiles.GetDirectory("sharedOpaque");
                tempFiles.GetDirectory("sharedOpaque\\subdir");
                tempFiles.GetDirectory("sharedOpaque\\subdir\\nested");
                tempFiles.GetDirectory("sharedOpaque\\anothersubdir");
                tempFiles.GetDirectory("sharedOpaque\\anothersubdir\\nested");
                AbsolutePath sharedOpaqueSubdirectoryAbsolutePath = AbsolutePath.Create(pathTable, sharedOpaqueSubdirectoryPath);
                var sharedOpaqueSubdirectory = new DirectoryArtifact(AbsolutePath.Create(pathTable, sharedOpaqueSubdirectoryPath), partialSealId: 1, isSharedOpaque: true);

                // This is a directory with one source file to become a source seal under a shared opaque
                string sourceSealInsharedOpaqueSubdirectoryPath = Path.Combine(sharedOpaqueSubdirectoryPath, "sourceSealInSharedOpaque");
                tempFiles.GetDirectory("sharedOpaque\\sourceSealInSharedOpaque");
                var sourceSealSubdirectory = DirectoryArtifact.CreateWithZeroPartialSealId(AbsolutePath.Create(pathTable, sourceSealInsharedOpaqueSubdirectoryPath));
                AbsolutePath sourceSealInsharedOpaqueSubdirectoryAbsolutePath = AbsolutePath.Create(pathTable, sourceSealInsharedOpaqueSubdirectoryPath);
                FileArtifact sourceInSourceSealInSharedOpaque = WriteFile(
                    pathTable,
                    tempFiles.GetFileName(pathTable, sourceSealInsharedOpaqueSubdirectoryAbsolutePath, "inputInSourceSealInSharedOpaque"));
                // A static input in a shared opaque (that is, explicitly declared as an input even if it is under a shared opaque)
                FileArtifact staticInputInSharedOpaque = WriteFile(
                    pathTable,
                    tempFiles.GetFileName(pathTable, sharedOpaqueSubdirectoryAbsolutePath.Combine(pathTable, "subdir").Combine(pathTable, "nested"), "staticInputInSharedOpaque"));
                // Dynamic inputs in a shared opaque (that is, not explicitly declared as inputs, the shared opaque serves as the artifact that the pip depends on)
                FileArtifact dynamicInputInSharedOpaque1 = WriteFile(
                    pathTable,
                    tempFiles.GetFileName(pathTable, sharedOpaqueSubdirectoryAbsolutePath.Combine(pathTable, "anothersubdir").Combine(pathTable, "nested"), "dynamicInputInSharedOpaque1"));
                FileArtifact dynamicInputInSharedOpaque2 = WriteFile(
                    pathTable,
                    tempFiles.GetFileName(pathTable, sharedOpaqueSubdirectoryAbsolutePath.Combine(pathTable, "anothersubdir"), "dynamicInputInSharedOpaque2"));
                FileArtifact dynamicInputInSharedOpaque3 = WriteFile(
                    pathTable,
                    tempFiles.GetFileName(pathTable, sharedOpaqueSubdirectoryAbsolutePath, "dynamicInputInSharedOpaque3"));
                // A static rewritten output in a shared opaque
                FileArtifact outputInSharedOpaqueToRewrite = WriteFile(
                    pathTable,
                    tempFiles.GetFileName(pathTable, sharedOpaqueSubdirectoryAbsolutePath, "rewrittenOutputInSharedOpaque"));
                FileArtifact outputInSharedOpaqueAfterRewrite = outputInSharedOpaqueToRewrite.CreateNextWrittenVersion();

                var arguments = new PipDataBuilder(pathTable.StringTable);
                if (normalize)
                {
                    arguments.Add("TimestampsNormalize");
                }
                else
                {
                    arguments.Add("TimestampsNoNormalize");
                }

                var environmentVariables = new List<EnvironmentVariable>();

                var untrackedPaths = CmdHelper.GetCmdDependencies(pathTable);
                var untrackedScopes = CmdHelper.GetCmdDependencyScopes(pathTable);
                var pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(pathTable),
                    null,
                    null,
                    dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                        [
                            executableFileArtifact,
                            inputArtifact,
                            outputToRewrite,
                            subdirRewrittenOutput1BeforeWrite,
                            subdirRewrittenOutput2BeforeWrite,
                            staticInputInSharedOpaque, // the dynamic input is explicitly not included in this list
                            outputInSharedOpaqueToRewrite,
                        ]),
                    outputs: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(
                        [
                            outputAfterRewrite.WithAttributes(),
                            subdirRewrittenOutput1AfterWrite.WithAttributes(),
                            subdirRewrittenOutput2AfterWrite.WithAttributes(),
                            outputInSharedOpaqueAfterRewrite.WithAttributes()
                        ]),
                    directoryDependencies: ReadOnlyArray<DirectoryArtifact>.FromWithoutCopy([inputSubdirectory, sourceSealSubdirectory, sharedOpaqueSubdirectory]),
                    directoryOutputs: ReadOnlyArray<DirectoryArtifact>.FromWithoutCopy([new DirectoryArtifact(sharedOpaqueSubdirectory.Path, sharedOpaqueSubdirectory.PartialSealId + 1, isSharedOpaque: true)]),
                    orderDependencies: ReadOnlyArray<PipId>.Empty,
                    untrackedPaths: ReadOnlyArray<AbsolutePath>.From(untrackedPaths),
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.From(untrackedScopes),
                    tags: ReadOnlyArray<StringId>.Empty,
                    successExitCodes: ReadOnlyArray<int>.Empty,
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                var sandboxConfiguration = new SandboxConfiguration
                {
                    FileAccessIgnoreCodeCoverage = true,
                    NormalizeReadTimestamps = normalize
                };

                sandboxConfiguration.UnsafeSandboxConfigurationMutable.UnexpectedFileAccessesAreErrors = true;
                await AssertProcessSucceedsAsync(
                    context,
                    sandboxConfiguration,
                    pip,
                    // There is no file content manager available, we need to manually tell which files belong to the shared opaque
                    directoryArtifactContext: new TestDirectoryArtifactContext([dynamicInputInSharedOpaque1, dynamicInputInSharedOpaque2, dynamicInputInSharedOpaque3]));
            }
        }

        [Fact]
        public async Task TestUseLargeNtClosePreallocatedList()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string executable = CmdHelper.CmdX64;

                XAssert.IsTrue(File.Exists(executable));
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                string workingDirectory = tempFiles.RootDirectory;
                Contract.Assume(workingDirectory != null);
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                FileArtifact inputArtifact = WriteFile(pathTable, tempFiles.GetFileName(pathTable, workingDirectoryAbsolutePath, "input"));
                FileArtifact outputToRewrite = WriteFile(pathTable, tempFiles.GetFileName(pathTable, workingDirectoryAbsolutePath, "rewrittenOutput"));

                FileArtifact outputAfterRewrite = outputToRewrite.CreateNextWrittenVersion();

                var arguments = new PipDataBuilder(pathTable.StringTable);
                arguments.Add("echo");
                arguments.Add("bar");

                var environmentVariables = new List<EnvironmentVariable>();

                var untrackedPaths = CmdHelper.GetCmdDependencies(pathTable);
                var untrackedScopes = CmdHelper.GetCmdDependencyScopes(pathTable);
                var pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(pathTable),
                    null,
                    null,
                    dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy([ executableFileArtifact ]),
                    outputs: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    directoryDependencies: ReadOnlyArray<DirectoryArtifact>.Empty,
                    directoryOutputs: ReadOnlyArray<DirectoryArtifact>.Empty,
                    orderDependencies: ReadOnlyArray<PipId>.Empty,
                    untrackedPaths: ReadOnlyArray<AbsolutePath>.From(untrackedPaths),
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.From(untrackedScopes),
                    tags: ReadOnlyArray<StringId>.Empty,
                    successExitCodes: ReadOnlyArray<int>.Empty,
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                var sandboxConfiguration = new SandboxConfiguration
                {
                    FileAccessIgnoreCodeCoverage = true,
                    UseLargeNtClosePreallocatedList = true
                };

                sandboxConfiguration.UnsafeSandboxConfigurationMutable.UnexpectedFileAccessesAreErrors = true;

                await AssertProcessSucceedsAsync(context, sandboxConfiguration, pip);
            }
        }

        [Fact]
        public async Task TestProbeForDirectory()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string workingDirectory = tempFiles.RootDirectory;
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                AbsolutePath inputFile = tempFiles.GetFileName(pathTable, "input.txt\\");
                FileArtifact inputArtifact = WriteFile(pathTable, inputFile);
                var environmentVariables = new List<EnvironmentVariable>();
                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallProbeForDirectory",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyExitCode(context, result, 267); // Invalid directory name

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (inputFile, RequestedAccess.Probe, FileAccessStatus.Allowed),
                    });

                SetExpectedFailures(1, 0);
            }
        }

        [Fact]
        public async Task TestGetFileAttributeOnFileWithPipeChar()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallFileAttributeOnFileWithPipeChar",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNoFileAccesses(result);
            }
        }

        [Fact]
        public async Task TestGetFileAttributeQuestion()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallGetAttributeQuestion",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNoFileAccesses(result);
            }
        }

        [Fact]
        public async Task TestGetAttributeNonExistentDeclared()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string workingDirectory = tempFiles.RootDirectory;

                string inputFile = Path.Combine(workingDirectory, "GetAttributeNonExistent.txt");
                FileArtifact inputArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, inputFile));
                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallGetAttributeNonExistent",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy(inputArtifact),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (inputArtifact.Path, RequestedAccess.Probe, FileAccessStatus.Allowed)
                    });
            }
        }

        /// <summary>
        /// Test accessing undeclared network path.
        /// </summary>
        /// <remarks>
        /// This test tries to access a network path that is not declared in the manifest.
        /// This test tries to exercise the scenario where the path exists and where the path does not exist.
        /// The issue with this test is when network is slow, the test takes a lot of time and may time out due to
        /// the timeout time set in our XUnit test runner.
        ///
        /// TODO: Figure out a way to test accesses to network paths without relying on network.
        /// </remarks>
        [Fact]
        public async Task TestNetworkPathNotDeclared()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string workingDirectory = tempFiles.RootDirectory;

                string inputFile = @"\\daddev\office\16.0\7923.1000\shadow\store\X64\Debug\airspace\x-none\inc\airspace.etw.man";
                FileArtifact inputArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, inputFile));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallAccessNetworkDrive",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                bool inputNotFound = result.AllReportedFileAccesses.Any(rfa =>
                    string.Equals(rfa.GetPath(pathTable), inputFile, OperatingSystemHelper.PathComparison)
                    && rfa.Error == NativeIOConstants.ErrorPathNotFound);

                // Detours allows file accesses for non existing files.
                // This could happen if the network acts weird and the file is not accessible through the network.
                if (!inputNotFound)
                {
                    VerifyNoObservedFileAccessesAndUnexpectedFileAccesses(
                        result,
                        [ inputFile ],
                        pathTable);
                }
            }
        }

        [Fact]
        public async Task TestInvalidFileNameNotDeclared()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string workingDirectory = tempFiles.RootDirectory;

                // Input file on the native side is: @"@:\office\16.0\7923.1000\shadow\store\X64\Debug\airspace\x-none\inc\airspace.etw.man";
                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallAccessInvalidFile",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                SetExpectedFailures(1, 1);

                VerifyExitCode(context, result, 3);
            }
        }

        [Fact]
        public async Task TestNetworkPathDeclared()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string workingDirectory = tempFiles.RootDirectory;

                string inputFile = @"\\daddev\office\16.0\7923.1000\shadow\store\X64\Debug\airspace\x-none\inc\airspace.etw.man";
                FileArtifact inputArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, inputFile));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallAccessNetworkDrive",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy(inputArtifact),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNoFileAccesses(result);
            }
        }

        [Fact]
        public async Task TestDisableDetours()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string workingDirectory = tempFiles.RootDirectory;

                string inputFile = Path.Combine(workingDirectory, "CallGetAttributeNonExistent.txt");

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallGetAttributeNonExistent",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: true,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNoFileAccesses(result);
            }
        }

        [Fact]
        public async Task TestDisableDetoursViaProcessOptions()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string workingDirectory = tempFiles.RootDirectory;

                string inputFile = Path.Combine(workingDirectory, "CallGetAttributeNonExistent.txt");

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallGetAttributeNonExistent",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty,
                    // Disable the sandbox via process options:
                    sandboxDisabled: true);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,  // Pip configuration takes precedence
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNoFileAccesses(result);
            }
        }

        [Fact]
        public async Task TestGetAttributeNonExistentNotDeclared()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string workingDirectory = tempFiles.RootDirectory;

                string inputFile = Path.Combine(workingDirectory, "GetAttributeNonExistent.txt");

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallGetAttributeNonExistent",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                XAssert.AreEqual(inputFile, result.ObservedFileAccesses[0].Path.ToString(pathTable));
                XAssert.AreEqual(1, result.ObservedFileAccesses.Length);
                VerifyNoFileAccessViolation(result);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task TestGetAttributeNonExistentUnderDeclaredDirectoryDependency(bool declareNonExistentFile)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string workingDirectory = tempFiles.RootDirectory;
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                string depDirFilePath = Path.Combine(workingDirectory, "input");
                tempFiles.GetDirectory("input");

                string inputFile = Path.Combine(workingDirectory, "input", "GetAttributeNonExistent.txt");
                FileArtifact inputArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, inputFile));

                AbsolutePath depDirAbsolutePath = AbsolutePath.Create(pathTable, depDirFilePath);
                DirectoryArtifact depDirArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(depDirAbsolutePath);

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallGetAttributeNonExistentInDepDirectory",
                    inputFiles: !declareNonExistentFile ? ReadOnlyArray<FileArtifact>.Empty : ReadOnlyArray<FileArtifact>.FromWithoutCopy(inputArtifact),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.FromWithoutCopy(depDirArtifact),
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                if (!declareNonExistentFile)
                {
                    XAssert.AreEqual(1, result.ObservedFileAccesses.Length);
                    VerifyNoFileAccessViolation(result);
                }
                else
                {
                    VerifyNoFileAccesses(result);
                }

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (inputArtifact.Path, RequestedAccess.Probe, FileAccessStatus.Allowed)
                    });
            }
        }

        [Fact]
        public async Task TestUseExtraThreadToDrainNtClose()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string executable = CmdHelper.CmdX64;

                XAssert.IsTrue(File.Exists(executable));
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                string workingDirectory = tempFiles.RootDirectory;
                Contract.Assume(workingDirectory != null);
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                FileArtifact inputArtifact = WriteFile(pathTable, tempFiles.GetFileName(pathTable, workingDirectoryAbsolutePath, "input"));
                FileArtifact outputToRewrite = WriteFile(pathTable, tempFiles.GetFileName(pathTable, workingDirectoryAbsolutePath, "rewrittenOutput"));

                FileArtifact outputAfterRewrite = outputToRewrite.CreateNextWrittenVersion();

                var arguments = new PipDataBuilder(pathTable.StringTable);
                arguments.Add("echo");
                arguments.Add("bar");

                var environmentVariables = new List<EnvironmentVariable>();

                var untrackedPaths = CmdHelper.GetCmdDependencies(pathTable);
                var untrackedScopes = CmdHelper.GetCmdDependencyScopes(pathTable);
                var pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(pathTable),
                    null,
                    null,
                    dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy([ executableFileArtifact ]),
                    outputs: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    directoryDependencies: ReadOnlyArray<DirectoryArtifact>.Empty,
                    directoryOutputs: ReadOnlyArray<DirectoryArtifact>.Empty,
                    orderDependencies: ReadOnlyArray<PipId>.Empty,
                    untrackedPaths: ReadOnlyArray<AbsolutePath>.From(untrackedPaths),
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.From(untrackedScopes),
                    tags: ReadOnlyArray<StringId>.Empty,
                    successExitCodes: ReadOnlyArray<int>.Empty,
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                var sandboxConfiguration = new SandboxConfiguration
                {
                    FileAccessIgnoreCodeCoverage = true,
                    UseExtraThreadToDrainNtClose = false
                };

                sandboxConfiguration.UnsafeSandboxConfigurationMutable.UnexpectedFileAccessesAreErrors = true;

                await AssertProcessSucceedsAsync(context, sandboxConfiguration, pip);
            }
        }

        [Fact]
        public async Task TestUseLargeNtClosePreallocatedListAndUseExtraThreadToDrainNtClose()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string executable = CmdHelper.CmdX64;

                XAssert.IsTrue(File.Exists(executable));
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                string workingDirectory = tempFiles.RootDirectory;
                Contract.Assume(workingDirectory != null);
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                FileArtifact inputArtifact = WriteFile(pathTable, tempFiles.GetFileName(pathTable, workingDirectoryAbsolutePath, "input"));
                FileArtifact outputToRewrite = WriteFile(pathTable, tempFiles.GetFileName(pathTable, workingDirectoryAbsolutePath, "rewrittenOutput"));
                FileArtifact outputAfterRewrite = outputToRewrite.CreateNextWrittenVersion();

                var arguments = new PipDataBuilder(pathTable.StringTable);
                arguments.Add("echo");
                arguments.Add("bar");

                var environmentVariables = new List<EnvironmentVariable>();

                var untrackedPaths = CmdHelper.GetCmdDependencies(pathTable);
                var untrackedScopes = CmdHelper.GetCmdDependencyScopes(pathTable);
                var pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(pathTable),
                    null,
                    null,
                    dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy([ executableFileArtifact ]),
                    outputs: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    directoryDependencies: ReadOnlyArray<DirectoryArtifact>.Empty,
                    directoryOutputs: ReadOnlyArray<DirectoryArtifact>.Empty,
                    orderDependencies: ReadOnlyArray<PipId>.Empty,
                    untrackedPaths: ReadOnlyArray<AbsolutePath>.From(untrackedPaths),
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.From(untrackedScopes),
                    tags: ReadOnlyArray<StringId>.Empty,
                    successExitCodes: ReadOnlyArray<int>.Empty,
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                var sandboxConfiguration = new SandboxConfiguration
                {
                    FileAccessIgnoreCodeCoverage = true,
                    UseExtraThreadToDrainNtClose = false,
                    UseLargeNtClosePreallocatedList = true
                };

                sandboxConfiguration.UnsafeSandboxConfigurationMutable.UnexpectedFileAccessesAreErrors = true;

                await AssertProcessSucceedsAsync(context, sandboxConfiguration, pip);
            }
        }

        /// <summary>
        /// Tests that short names of files cannot be discovered with e.g. <c>GetShortPathName</c> or <c>FindFirstFile</c>.
        /// </summary>
        [Fact]
        public async Task CmdMove()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string executable = CmdHelper.CmdX64;
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                string workingDirectory = Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(context.PathTable, workingDirectory);

                string destination = tempFiles.RootDirectory;
                string envVarName = "ENV" + Guid.NewGuid().ToString().Replace("-", string.Empty);

                // Create the input file.
                AbsolutePath sourceAbsolutePath = tempFiles.GetFileName(pathTable, "a1.txt");
                string sourceExpandedPath = sourceAbsolutePath.ToString(pathTable);
                WriteFile(sourceExpandedPath);

                // Set up target path.
                AbsolutePath targetAbsolutePath = tempFiles.GetFileName(pathTable, "a2.txt");
                string targetExpandedPath = targetAbsolutePath.ToString(pathTable);

                var arguments = new PipDataBuilder(context.PathTable.StringTable);
                arguments.Add("/d");
                arguments.Add("/c");
                using (arguments.StartFragment(PipDataFragmentEscaping.CRuntimeArgumentRules, " "))
                {
                    arguments.Add("move");
                    arguments.Add(sourceAbsolutePath);
                    arguments.Add(targetAbsolutePath);
                }

                var untrackedPaths = new List<AbsolutePath>(CmdHelper.GetCmdDependencies(context.PathTable)) { sourceAbsolutePath };

                var pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.FromWithoutCopy(
                        new EnvironmentVariable(
                            StringId.Create(context.PathTable.StringTable, envVarName),
                            PipDataBuilder.CreatePipData(
                                context.PathTable.StringTable,
                                " ",
                                PipDataFragmentEscaping.CRuntimeArgumentRules,
                                "Success"))),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(context.PathTable),
                    null,
                    null,
                    ReadOnlyArray<FileArtifact>.FromWithoutCopy(executableFileArtifact, FileArtifact.CreateSourceFile(sourceAbsolutePath)),
                    ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    ReadOnlyArray<DirectoryArtifact>.Empty,
                    ReadOnlyArray<DirectoryArtifact>.Empty,
                    ReadOnlyArray<PipId>.Empty,
                    ReadOnlyArray<AbsolutePath>.From(untrackedPaths),
                    ReadOnlyArray<AbsolutePath>.From(CmdHelper.GetCmdDependencyScopes(context.PathTable)),
                    ReadOnlyArray<StringId>.Empty,
                    ReadOnlyArray<int>.Empty,
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: true,
                    context: context,
                    pip: pip,
                    errorString: out _);

                AssertVerboseEventLogged(ProcessesLogEventId.PipProcessDisallowedFileAccess);
                SetExpectedFailures(1, 0);

                VerifyExecutionStatus(context, result, SandboxedProcessPipExecutionStatus.ExecutionFailed);
                VerifyExitCode(context, result, 1);

                XAssert.IsFalse(File.Exists(targetExpandedPath));
            }
        }

        /// <summary>
        /// This test makes sure we are adding AllowRead access to the directory that contains the current executable. Negative case.
        /// </summary>
        [Fact]
        public async Task TestAccessToExecutableTestNoAccess()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string executable = CmdHelper.CmdX64;
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                string exeAssembly = AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly());
                string outsidePath = Path.Combine(Path.GetDirectoryName(exeAssembly), "TestProcess", "Win", "Test.BuildXL.Executables.TestProcess.exe");

                XAssert.IsTrue(AbsolutePath.TryCreate(pathTable, outsidePath, out AbsolutePath outsideAbsPath));

                var arguments = new PipDataBuilder(context.PathTable.StringTable);
                arguments.Add("/d");
                arguments.Add("/c");
                using (arguments.StartFragment(PipDataFragmentEscaping.CRuntimeArgumentRules, " "))
                {
                    arguments.Add("dir ");
                    arguments.Add(outsideAbsPath);
                }

                string workingDirectory = tempFiles.RootDirectory;
                Contract.Assume(workingDirectory != null);
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                var pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.Empty,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(context.PathTable),
                    null,
                    null,
                    ReadOnlyArray<FileArtifact>.FromWithoutCopy([executableFileArtifact]),
                    ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    ReadOnlyArray<DirectoryArtifact>.Empty,
                    ReadOnlyArray<DirectoryArtifact>.Empty,
                    ReadOnlyArray<PipId>.Empty,
                    ReadOnlyArray<AbsolutePath>.From(CmdHelper.GetCmdDependencies(pathTable)),
                    ReadOnlyArray<AbsolutePath>.From(CmdHelper.GetCmdDependencyScopes(context.PathTable)),
                    ReadOnlyArray<StringId>.Empty,
                    ReadOnlyArray<int>.Empty,
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);
                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    // Use unsafe probe by default because this test probes parent directories that can be directory symlinks or junctions.
                    // E.g., 'd:\dbs\el\bxlint\Out' with [C:\Windows\system32\cmd.exe:52040](Probe) FindFirstFileEx(...)
                    probeDirectorySymlinkAsDirectory: true,
                    errorString: out _);

                SetExpectedFailures(1, 0);
                AssertVerboseEventLogged(ProcessesLogEventId.PipProcessDisallowedFileAccess);

                VerifyExecutionStatus(context, result, SandboxedProcessPipExecutionStatus.ExecutionFailed);
                VerifyExitCode(context, result, 1);
            }
        }

        /// <summary>
        /// This test makes sure we are adding AllowRead access to the directory that contains the current executable.
        /// </summary>
        [Fact]
        public async Task TestAccessToExecutableTest()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string executable = CmdHelper.CmdX64;
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                AbsolutePath exePath;
                string localExePath = string.Empty;
                try
                {
                    localExePath = new Uri((Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).Location).LocalPath;
                }
#pragma warning disable ERP022 // TODO: This should really handle specific errors
                catch
                {
                    localExePath = string.Empty;
                }
#pragma warning restore ERP022 // Unobserved exception in generic exception handler

                XAssert.IsTrue(!string.IsNullOrEmpty(localExePath));
                bool gotten = AbsolutePath.TryCreate(pathTable, localExePath, out exePath);
                XAssert.IsTrue(gotten);

                var arguments = new PipDataBuilder(context.PathTable.StringTable);
                arguments.Add("/d");
                arguments.Add("/c");
                using (arguments.StartFragment(PipDataFragmentEscaping.CRuntimeArgumentRules, " "))
                {
                    arguments.Add("dir ");
                    arguments.Add(exePath);
                }

                string workingDirectory = tempFiles.RootDirectory;
                Contract.Assume(workingDirectory != null);
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                var environmentVariables = new List<EnvironmentVariable>();
                var untrackedPaths = CmdHelper.GetCmdDependencies(pathTable);
                var untrackedScopes = CmdHelper.GetCmdDependencyScopes(pathTable);

                var pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(context.PathTable),
                    null,
                    null,
                    ReadOnlyArray<FileArtifact>.FromWithoutCopy(new[] { executableFileArtifact }),
                    ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    ReadOnlyArray<DirectoryArtifact>.Empty,
                    ReadOnlyArray<DirectoryArtifact>.Empty,
                    ReadOnlyArray<PipId>.Empty,
                    ReadOnlyArray<AbsolutePath>.From(untrackedPaths),
                    ReadOnlyArray<AbsolutePath>.From(CmdHelper.GetCmdDependencyScopes(context.PathTable)),
                    ReadOnlyArray<StringId>.Empty,
                    ReadOnlyArray<int>.Empty,
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    binDirectory: exePath,
                    errorString: out _);

                VerifyNormalSuccess(context, result);
            }
        }

        /// <summary>
        /// Tests that short names of files cannot be discovered with e.g. <c>GetShortPathName</c> or <c>FindFirstFile</c>.
        /// </summary>
        [Fact]
        public async Task ShortNames()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
                string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
                XAssert.IsTrue(File.Exists(executable));
                FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

                string workingDirectory = tempFiles.RootDirectory;
                AbsolutePath workingDirectoryAbsolutePath = AbsolutePath.Create(pathTable, workingDirectory);

                var combinedDir = Path.Combine(workingDirectory, "directoryWithAVeryLongName");
                tempFiles.GetDirectory(combinedDir);
                FileArtifact inputArtifact = WriteFile(pathTable, tempFiles.GetFileName(pathTable, AbsolutePath.Create(pathTable, combinedDir), "fileWithAVeryLongName"));

                var arguments = new PipDataBuilder(pathTable.StringTable);
                arguments.Add("ShortNames");

                var environmentVariables = new List<EnvironmentVariable>();

                var untrackedPaths = CmdHelper.GetCmdDependencies(pathTable);
                var untrackedScopes = CmdHelper.GetCmdDependencyScopes(pathTable);
                var pip = new Process(
                    executableFileArtifact,
                    workingDirectoryAbsolutePath,
                    arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                    FileArtifact.Invalid,
                    PipData.Invalid,
                    ReadOnlyArray<EnvironmentVariable>.From(environmentVariables),
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    FileArtifact.Invalid,
                    tempFiles.GetUniqueDirectory(pathTable),
                    null,
                    null,
                    dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy([ executableFileArtifact, inputArtifact, ]),
                    outputs: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    directoryDependencies: ReadOnlyArray<DirectoryArtifact>.Empty,
                    directoryOutputs: ReadOnlyArray<DirectoryArtifact>.Empty,
                    orderDependencies: ReadOnlyArray<PipId>.Empty,
                    untrackedPaths: ReadOnlyArray<AbsolutePath>.From(untrackedPaths),
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.From(untrackedScopes),
                    tags: ReadOnlyArray<StringId>.Empty,
                    successExitCodes: ReadOnlyArray<int>.Empty,
                    semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                    provenance: PipProvenance.CreateDummy(context),
                    toolDescription: StringId.Invalid,
                    additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty);

                var sandboxConfiguration = new SandboxConfiguration
                {
                    FileAccessIgnoreCodeCoverage = true,
                };

                sandboxConfiguration.UnsafeSandboxConfigurationMutable.UnexpectedFileAccessesAreErrors = true;

                await AssertProcessSucceedsAsync(context, sandboxConfiguration, pip);
            }
        }

        [Fact]
        public async Task CallDetouredAccessesChainOfJunctions()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var sourceJunction = tempFiles.GetDirectory(pathTable, "SourceJunction");
                var intermediateJunction = tempFiles.GetDirectory(pathTable, "IntermediateJunction");
                var targetDirectory = tempFiles.GetDirectory(pathTable, "TargetDirectory");

                EstablishJunction(sourceJunction.ToString(pathTable), intermediateJunction.ToString(pathTable));
                EstablishJunction(intermediateJunction.ToString(pathTable), targetDirectory.ToString(pathTable));

                var targetFileStr = Path.Combine(targetDirectory.ToString(pathTable), "target.txt");
                var targetFile = AbsolutePath.Create(pathTable, targetFileStr);
                WriteFile(pathTable, targetFile);

                var srcFileStr = Path.Combine(targetDirectory.ToString(pathTable), "target.txt");
                var srcFile = AbsolutePath.Create(pathTable, srcFileStr);

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallAccessOnChainOfJunctions",
                    inputFiles:
                        ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                            FileArtifact.CreateSourceFile(srcFile)),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    ignoreFullReparsePointResolving: false,
                    unexpectedFileAccessesAreErrors: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (sourceJunction,       RequestedAccess.Read, FileAccessStatus.Denied),       // Denied: reparse point, read for symlink resolution, being treated as a file.
                        (intermediateJunction, RequestedAccess.Read, FileAccessStatus.Denied),       // Denied: reparse point, read for symlink resolution, being treated as a file.
                        (targetDirectory,      RequestedAccess.Probe, FileAccessStatus.Allowed),     // Allowed: directory probe.
                        (srcFile,              RequestedAccess.Read, FileAccessStatus.Allowed)
                    });
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDetouredAccessesCreateSymlinkForQBuild()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateSymbolicLinkTest1.txt",
                    "CreateSymbolicLinkTest2.txt",
                    "CallDetouredAccessesCreateSymlinkForQBuild",
                    isDirectoryTest: false,

                    // Setup doesn't create symlink, but the C++ method CallDetouredFileCreateWithSymlink does.
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: true,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsOutput,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                   context,
                   result.AllReportedFileAccesses,
                   new[]
                   {
                        (createdInputPaths["CreateSymbolicLinkTest1.txt"], RequestedAccess.Write, FileAccessStatus.Allowed)
                   },
                   pathsToFalsify:
                   [
                        createdInputPaths["CreateSymbolicLinkTest2.txt"]
                   ]);
            }
        }

        [Fact]
        public async Task CallAccessJunctionOnDirectoriesWithGrantedAccess()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "AccessSymLinkOnDirectories1.dir",
                    "AccessJunctionOnDirectories2.dir",
                    "CallAccessSymLinkOnDirectories",
                    isDirectoryTest: true,
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: true,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsDependency,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                string junctionPath = createdInputPaths["AccessSymLinkOnDirectories1.dir"].ToString(pathTable);
                string targetPath = createdInputPaths["AccessJunctionOnDirectories2.dir"].ToString(pathTable);

                EstablishJunction(junctionPath, targetPath);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        // TODO: Currently BuildXL does not handle directory junction, so the access via AccessSymLinkOnDirectories2.dir is not recognized.
                        // (createdInputPaths[Path.Combine("AccessSymLinkOnDirectories2.dir", ExtraFileNameInDirectory)], RequestedAccess.Read, FileAccessStatus.Allowed),
                        (
                            createdInputPaths[Path.Combine("AccessSymLinkOnDirectories1.dir", ExtraFileNameInDirectory)],
                            RequestedAccess.Read,
                            FileAccessStatus.Allowed)
                    });
            }
        }

        [Fact]
        public async Task CallAccessJunctionOnDirectoriesWithNoGrantedAccessNoTranslation()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "AccessSymLinkOnDirectories1.dir",
                    "AccessJunctionOnDirectories2.dir",
                    "CallAccessSymLinkOnDirectories",
                    isDirectoryTest: true,
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: true,
                    addFirstFileKind: AddFileOrDirectoryKinds.None,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                string junctionPath = createdInputPaths["AccessSymLinkOnDirectories1.dir"].ToString(pathTable);
                string targetPath = createdInputPaths["AccessJunctionOnDirectories2.dir"].ToString(pathTable);

                EstablishJunction(junctionPath, targetPath);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _);

                SetExpectedFailures(1, 0);

                VerifyAccessDenied(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        // TODO: Currently BuildXL does not handle directory junction, so the access via AccessSymLinkOnDirectories2.dir is not recognized.
                        // (createdInputPaths[Path.Combine("AccessSymLinkOnDirectories2.dir", ExtraFileNameInDirectory)], RequestedAccess.Read, FileAccessStatus.Allowed),
                        (
                            createdInputPaths[Path.Combine("AccessSymLinkOnDirectories1.dir", ExtraFileNameInDirectory)],
                            RequestedAccess.Read,
                            FileAccessStatus.Denied
                        )
                    });
            }
        }

        [Fact]
        public async Task CallAccessJunctionOnDirectoriesWithGrantedAccessWithTranslation()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                // CAUTION: Unlike other tests, this test swaps the order of directories.
                // In this test, the native is accessing AccessSymLinkOnDirectories1.dir\foo, such that
                // there is a junction from AccessJunctionOnDirectories2.dir to AccessSymLinkOnDirectories1.dir,
                // and the dependency is specified in terms of AccessJunctionOnDirectories2.dir.
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "AccessJunctionOnDirectories2.dir",
                    "AccessSymLinkOnDirectories1.dir",
                    "CallAccessSymLinkOnDirectories",
                    isDirectoryTest: true,
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: true,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsDependency,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.None,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                var targetPath = createdInputPaths["AccessSymLinkOnDirectories1.dir"];
                var junctionPath = createdInputPaths["AccessJunctionOnDirectories2.dir"];
                string targetPathStr = targetPath.ToString(pathTable);
                string junctionPathStr = junctionPath.ToString(pathTable);

                EstablishJunction(junctionPathStr, targetPathStr);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _,
                    directoriesToTranslate:
                        new List<TranslateDirectoryData>
                        {
                            new TranslateDirectoryData(targetPathStr + @"\<" + junctionPathStr + @"\", targetPath, junctionPath)
                        });

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        // TODO: Currently BuildXL does not handle directory junction, so the access via AccessSymLinkOnDirectories2.dir is not recognized.
                        // (createdInputPaths[Path.Combine("AccessSymLinkOnDirectories2.dir", ExtraFileNameInDirectory)], RequestedAccess.Read, FileAccessStatus.Allowed),
                        (
                            createdInputPaths[Path.Combine("AccessJunctionOnDirectories2.dir", ExtraFileNameInDirectory)],
                            RequestedAccess.Read,
                            FileAccessStatus.Allowed
                        )
                    });
            }
        }

        [Fact]
        public async Task CallAccessJunctionOnDirectoriesWithGrantedAccessWithTranslationGetLongestPath()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                // CAUTION: Unlike other tests, this test swaps the order of directories.
                // In this test, the native is accessing AccessSymLinkOnDirectories1.dir\foo, such that
                // there is a junction from AccessJunctionOnDirectories2.dir to AccessSymLinkOnDirectories1.dir,
                // and the dependency is specified in terms of AccessJunctionOnDirectories2.dir.
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "AccessJunctionOnDirectories2.dir",
                    "AccessSymLinkOnDirectories1.dir",
                    "CallAccessSymLinkOnDirectories",
                    isDirectoryTest: true,
                    createSymlink: false,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: true,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsDependency,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.None,
                    makeSecondUntracked: false,
                    createdInputPaths: createdInputPaths);

                var targetPath = createdInputPaths["AccessSymLinkOnDirectories1.dir"];
                var junctionPath = createdInputPaths["AccessJunctionOnDirectories2.dir"];
                string targetPathStr = targetPath.ToString(pathTable);
                string junctionPathStr = junctionPath.ToString(pathTable);

                EstablishJunction(junctionPathStr, targetPathStr);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: pip,
                    errorString: out _,
                    directoriesToTranslate: new List<TranslateDirectoryData>
                                            {
                                                new TranslateDirectoryData(
                                                    targetPathStr.Substring(0, targetPathStr.Length - 4) + @"\<" + junctionPathStr + @"\",
                                                    AbsolutePath.Create(pathTable, targetPathStr.Substring(0, targetPathStr.Length - 4)),
                                                    junctionPath),
                                                new TranslateDirectoryData(targetPathStr + @"\<" + junctionPathStr + @"\", targetPath, junctionPath),
                                                new TranslateDirectoryData(
                                                    targetPathStr.Substring(0, targetPathStr.Length - 3) + @"\<" + junctionPathStr + @"\",
                                                    AbsolutePath.Create(pathTable, targetPathStr.Substring(0, targetPathStr.Length - 3)),
                                                    junctionPath),
                                            });

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        // TODO: Currently BuildXL does not handle directory junction, so the access via AccessSymLinkOnDirectories2.dir is not recognized.
                        // (createdInputPaths[Path.Combine("AccessSymLinkOnDirectories2.dir", ExtraFileNameInDirectory)], RequestedAccess.Read, FileAccessStatus.Allowed),
                        (
                            createdInputPaths[Path.Combine("AccessJunctionOnDirectories2.dir", ExtraFileNameInDirectory)],
                            RequestedAccess.Read,
                            FileAccessStatus.Allowed
                        )
                    });
            }
        }

        [TheoryIfSupported(requiresSymlinkPermission: true)]
        [MemberData(nameof(TruthTable.GetTable), 1, MemberType = typeof(TruthTable))]
        public async Task CallDetouredFileCreateThatAccessesChainOfSymlinks(bool openWithReparsePointFlag)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var sourceOfSymlink = tempFiles.GetFileName(pathTable, "SourceOfSymLink.link");
                var intermediateSymlink = tempFiles.GetFileName(pathTable, "IntermediateSymLink.link");
                var targetFile = tempFiles.GetFileName(pathTable, "Target.txt");
                WriteFile(pathTable, targetFile);

                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(intermediateSymlink.ToString(pathTable), targetFile.ToString(pathTable), true));
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(sourceOfSymlink.ToString(pathTable), intermediateSymlink.ToString(pathTable), true));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: openWithReparsePointFlag
                        ? "CallDetouredFileCreateThatDoesNotAccessChainOfSymlinks"
                        : "CallDetouredFileCreateThatAccessesChainOfSymlinks",
                    inputFiles:
                        ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                            FileArtifact.CreateSourceFile(sourceOfSymlink),
                            FileArtifact.CreateSourceFile(intermediateSymlink),
                            FileArtifact.CreateSourceFile(targetFile)),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                var accessesToVerify = new List<(AbsolutePath, RequestedAccess, FileAccessStatus)>
                {
                    (sourceOfSymlink, RequestedAccess.Read, FileAccessStatus.Allowed)
                };

                if (!openWithReparsePointFlag)
                {
                    accessesToVerify.AddRange(new[]
                    {
                        (intermediateSymlink, RequestedAccess.Read, FileAccessStatus.Allowed),
                        (targetFile, RequestedAccess.Read, FileAccessStatus.Allowed)
                    });
                }

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    accessesToVerify.ToArray());
            }
        }

        [TheoryIfSupported(requiresSymlinkPermission: true)]
        [MemberData(nameof(TruthTable.GetTable), 1, MemberType = typeof(TruthTable))]
        public async Task CallDetouredFileCreateThatCopiesChainOfSymlinks(bool followChainOfSymlinks)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var sourceOfSymlink = tempFiles.GetFileName(pathTable, "SourceOfSymLink.link");
                var intermediateSymlink = tempFiles.GetFileName(pathTable, "IntermediateSymLink.link");
                var targetFile = tempFiles.GetFileName(pathTable, "Target.txt");
                WriteFile(pathTable, targetFile);

                var copiedFile = tempFiles.GetFileName(pathTable, "CopiedFile.txt");

                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(intermediateSymlink.ToString(pathTable), targetFile.ToString(pathTable), true));
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(sourceOfSymlink.ToString(pathTable), intermediateSymlink.ToString(pathTable), true));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: followChainOfSymlinks
                    ? "CallDetouredCopyFileFollowingChainOfSymlinks"
                    : "CallDetouredCopyFileNotFollowingChainOfSymlinks",
                    inputFiles:
                        ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                            FileArtifact.CreateSourceFile(sourceOfSymlink),
                            FileArtifact.CreateSourceFile(intermediateSymlink),
                            FileArtifact.CreateSourceFile(targetFile)),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(
                        FileArtifactWithAttributes.Create(
                            FileArtifact.CreateOutputFile(copiedFile), FileExistence.Required)),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    ignoreNonCreateFileReparsePoints: false,
                    monitorZwCreateOpenQueryFile: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                if (!followChainOfSymlinks && IsNotEnoughPrivilegesError(result))
                {
                    // When followChainOfSymlinks is false, this test calls CopyFileExW with COPY_FILE_COPY_SYMLINK.
                    // With this flag, CopyFileExW essentially creates a symlink that points to the same target
                    // as the source of copy file. However, the symlink creation is not via CreateSymbolicLink, and
                    // thus SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE cannot be specified.
                    return;
                }

                VerifyNormalSuccess(context, result);

                XAssert.IsTrue(File.Exists(copiedFile.ToString(pathTable)));

                var toVerify = new List<(AbsolutePath, RequestedAccess, FileAccessStatus)>
                {
                    (sourceOfSymlink, RequestedAccess.Read, FileAccessStatus.Allowed),
                    (copiedFile, RequestedAccess.Write, FileAccessStatus.Allowed)
                };

                var toVerifyOrFalsify = new List<(AbsolutePath, RequestedAccess, FileAccessStatus)>
                {
                    (intermediateSymlink, RequestedAccess.Read, FileAccessStatus.Allowed),
                    (targetFile, RequestedAccess.Read, FileAccessStatus.Allowed)
                };

                if (followChainOfSymlinks)
                {
                    toVerify.AddRange(toVerifyOrFalsify);
                }

                var toFalsify = new List<(AbsolutePath absolutePath, RequestedAccess requestedAccess, FileAccessStatus fileAccessStatus)>();
                if (!followChainOfSymlinks)
                {
                    toFalsify.AddRange(toVerifyOrFalsify);
                }

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    toVerify.ToArray(),
                    toFalsify.Select(a => a.absolutePath).ToArray());
            }
        }

        [TheoryIfSupported(requiresSymlinkPermission: true)]
        [MemberData(nameof(TruthTable.GetTable), 1, MemberType = typeof(TruthTable))]
        public async Task CallDetouredCopyFileThatCopiesToExistingSymlink(bool followChainOfSymlinks)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var linkToSource = tempFiles.GetFileName(pathTable, "LinkToSource.link");
                var targetFile = tempFiles.GetFileName(pathTable, "Target.txt");
                WriteFile(pathTable, targetFile);

                var linkToDestination = tempFiles.GetFileName(pathTable, "LinkToDestination.link");
                var destination = tempFiles.GetFileName(pathTable, "Destination.txt");

                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(linkToSource.ToString(pathTable), targetFile.ToString(pathTable), true));

                var outputs = new List<FileArtifactWithAttributes>() { FileArtifactWithAttributes.Create(FileArtifact.CreateOutputFile(linkToDestination), FileExistence.Required) };
                if (followChainOfSymlinks)
                {
                    outputs.Add(FileArtifactWithAttributes.Create(FileArtifact.CreateOutputFile(destination), FileExistence.Required));
                }

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: followChainOfSymlinks
                    ? "CallDetouredCopyFileToExistingSymlinkFollowChainOfSymlinks"
                    : "CallDetouredCopyFileToExistingSymlinkNotFollowChainOfSymlinks",
                    inputFiles:
                        ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                            FileArtifact.CreateSourceFile(linkToSource),
                            FileArtifact.CreateSourceFile(targetFile)),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(outputs.ToArray()),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    ignoreNonCreateFileReparsePoints: false,
                    monitorZwCreateOpenQueryFile: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                if (!followChainOfSymlinks && IsNotEnoughPrivilegesError(result))
                {
                    // When followChainOfSymlinks is false, this test calls CopyFileExW with COPY_FILE_COPY_SYMLINK.
                    // With this flag, CopyFileExW essentially creates a symlink that points to the same target
                    // as the source of copy file. However, the symlink creation is not via CreateSymbolicLink, and
                    // thus SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE cannot be specified.
                    return;
                }

                VerifyNormalSuccess(context, result);

                XAssert.IsTrue(File.Exists(linkToDestination.ToString(pathTable)));

                var toVerify = new List<(AbsolutePath, RequestedAccess, FileAccessStatus)>
                {
                    (linkToSource, RequestedAccess.Read, FileAccessStatus.Allowed),
                    (linkToDestination, RequestedAccess.Write, FileAccessStatus.Allowed)
                };

                var toVerifyOrFalsify = new List<(AbsolutePath, RequestedAccess, FileAccessStatus)>
                {
                    (targetFile, RequestedAccess.Read, FileAccessStatus.Allowed),
                    (destination, RequestedAccess.Write, FileAccessStatus.Allowed)
                };

                if (followChainOfSymlinks)
                {
                    toVerify.AddRange(toVerifyOrFalsify);
                }

                var toFalsify = new List<(AbsolutePath absolutePath, RequestedAccess requestedAccess, FileAccessStatus fileAccessStatus)>();
                if (!followChainOfSymlinks)
                {
                    toFalsify.AddRange(toVerifyOrFalsify);
                }

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    toVerify.ToArray(),
                    toFalsify.Select(a => a.absolutePath).ToArray());
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDetouredFileCreateThatAccessesChainOfSymlinksFailDueToNoAccess()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var sourceOfSymlink = tempFiles.GetFileName(pathTable, "SourceOfSymLink.link");
                var intermediateSymlink = tempFiles.GetFileName(pathTable, "IntermediateSymLink.link");
                var targetFile = tempFiles.GetFileName(pathTable, "Target.txt");
                WriteFile(pathTable, targetFile);

                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(intermediateSymlink.ToString(pathTable), targetFile.ToString(pathTable), true));
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(sourceOfSymlink.ToString(pathTable), intermediateSymlink.ToString(pathTable), true));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDetouredFileCreateThatAccessesChainOfSymlinks",
                    inputFiles:
                        // Intermediate symlink is not specified as an input.
                        ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                            FileArtifact.CreateSourceFile(sourceOfSymlink),
                            FileArtifact.CreateSourceFile(targetFile)),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                SetExpectedFailures(1, 0);

                VerifyAccessDenied(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (sourceOfSymlink, RequestedAccess.Read, FileAccessStatus.Allowed),
                        (intermediateSymlink, RequestedAccess.Read, FileAccessStatus.Denied),
                        (targetFile, RequestedAccess.Read, FileAccessStatus.Allowed)
                    });
            }
        }

        [TheoryIfSupported(requiresSymlinkPermission: true)]
        [MemberData(nameof(TruthTable.GetTable), 1, MemberType = typeof(TruthTable))]
        public async Task CallDetouredNtCreateFileThatAccessesChainOfSymlinks(bool openWithReparsePointFlag)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var sourceOfSymlink = tempFiles.GetFileName(pathTable, "SourceOfSymLink.link");
                var intermediateSymlink = tempFiles.GetFileName(pathTable, "IntermediateSymLink.link");
                var targetFile = tempFiles.GetFileName(pathTable, "Target.txt");
                WriteFile(pathTable, targetFile);

                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(intermediateSymlink.ToString(pathTable), targetFile.ToString(pathTable), true));
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(sourceOfSymlink.ToString(pathTable), intermediateSymlink.ToString(pathTable), true));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: openWithReparsePointFlag
                        ? "CallDetouredNtCreateFileThatDoesNotAccessChainOfSymlinks"
                        : "CallDetouredNtCreateFileThatAccessesChainOfSymlinks",
                    inputFiles:
                        ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                            FileArtifact.CreateSourceFile(sourceOfSymlink),
                            FileArtifact.CreateSourceFile(intermediateSymlink),
                            FileArtifact.CreateSourceFile(targetFile)),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                var accessesToVerify = new List<(AbsolutePath, RequestedAccess, FileAccessStatus)>
                {
                    (sourceOfSymlink, RequestedAccess.Read, FileAccessStatus.Allowed)
                };

                if (!openWithReparsePointFlag)
                {
                    accessesToVerify.AddRange(new[]
                    {
                        (intermediateSymlink, RequestedAccess.Read, FileAccessStatus.Allowed),
                        (targetFile, RequestedAccess.Read, FileAccessStatus.Allowed)
                    });
                }

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    accessesToVerify.ToArray());
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDetouredNtCreateFileThatAccessesChainOfSymlinksFailDueToNoAccess()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var sourceOfSymlink = tempFiles.GetFileName(pathTable, "SourceOfSymLink.link");
                var intermediateSymlink = tempFiles.GetFileName(pathTable, "IntermediateSymLink.link");
                var targetFile = tempFiles.GetFileName(pathTable, "Target.txt");
                WriteFile(pathTable, targetFile);

                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(intermediateSymlink.ToString(pathTable), targetFile.ToString(pathTable), true));
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(sourceOfSymlink.ToString(pathTable), intermediateSymlink.ToString(pathTable), true));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDetouredNtCreateFileThatAccessesChainOfSymlinks",
                    inputFiles:

                        // Intermediate symlink is not specified as an input.
                        ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                            FileArtifact.CreateSourceFile(sourceOfSymlink),
                            FileArtifact.CreateSourceFile(targetFile)),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                SetExpectedFailures(1, 0);

                VerifyAccessDenied(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (sourceOfSymlink, RequestedAccess.Read, FileAccessStatus.Allowed),
                        (intermediateSymlink, RequestedAccess.Read, FileAccessStatus.Denied),
                        (targetFile, RequestedAccess.Read, FileAccessStatus.Allowed)
                    });
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallAccessNestedSiblingSymLinkOnFiles()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var sourceDir = tempFiles.GetDirectory(pathTable, @"imports\x64");
                var sourceOfSymlink = tempFiles.GetFileName(pathTable, sourceDir, "symlink.imports.link");

                var intermediateDir = tempFiles.GetDirectory(pathTable, @"icache\x64");
                var intermediateSymlink = tempFiles.GetFileName(pathTable, intermediateDir, "symlink.icache.link");

                var targetDir = tempFiles.GetDirectory(pathTable, @"targets\x64");
                var targetFile = tempFiles.GetFileName(pathTable, targetDir, "hello.txt");

                WriteFile(pathTable, targetFile);

                // Force creation of relative symlinks.
                var currentDirectory = Directory.GetCurrentDirectory();
                Directory.SetCurrentDirectory(intermediateDir.ToString(pathTable));
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink("symlink.icache.link", @"..\..\targets\x64\hello.txt", true));

                Directory.SetCurrentDirectory(sourceDir.ToString(pathTable));
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink("symlink.imports.link", @"..\..\icache\x64\symlink.icache.link", true));

                Directory.SetCurrentDirectory(currentDirectory);

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallAccessNestedSiblingSymLinkOnFiles",
                    inputFiles:
                        ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                            FileArtifact.CreateSourceFile(sourceOfSymlink),
                            FileArtifact.CreateSourceFile(intermediateSymlink),
                            FileArtifact.CreateSourceFile(targetFile)),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (sourceOfSymlink, RequestedAccess.Read, FileAccessStatus.Allowed),
                        (intermediateSymlink, RequestedAccess.Read, FileAccessStatus.Allowed),
                        (targetFile, RequestedAccess.Read, FileAccessStatus.Allowed)
                    });
            }
        }

        [TheoryIfSupported(requiresSymlinkPermission: true)]
        [InlineData(true, @"..\..\..\targets\x64\hello.txt")]
        [InlineData(false, @"..\..\..\targets\x64\hello.txt")]
        [InlineData(true, @"..\..\targets\x64\hello.txt")]
        [InlineData(false, @"..\..\targets\x64\hello.txt")]
        public async Task CallAccessNestedSiblingSymLinkOnFilesThroughDirectorySymlinkOrJunction(bool useJunction, string symlinkRelativeTarget)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                // File and directory layout:
                //    Enlist
                //    |
                //    +---icache
                //    |   \---current
                //    |       \---x64
                //    |              symlink.imports.link ==> ..\..\..\targets\x64\hello.txt, or
                //    |                                   ==> ..\..\targets\x64\hello.txt
                //    +---imports
                //    |   \---x64 ==> ..\icache\current\x64
                //    |
                //    \---targets
                //        \---x64
                //               hello.txt

                // access: imports\x64\symlink.imports.link

                var sourceDir = tempFiles.GetDirectory(pathTable, @"imports\x64");
                var sourceOfSymlink = tempFiles.GetFileName(pathTable, sourceDir, "symlink.imports.link");

                var intermediateDir = tempFiles.GetDirectory(pathTable, @"icache\current\x64");
                var intermediateSymlink = tempFiles.GetFileName(pathTable, intermediateDir, "symlink.imports.link");

                var targetDir = tempFiles.GetDirectory(pathTable, @"targets\x64");
                var targetFile = tempFiles.GetFileName(pathTable, targetDir, "hello.txt");

                WriteFile(pathTable, targetFile);

                // Force creation of relative symlinks.
                var currentDirectory = Directory.GetCurrentDirectory();

                Directory.SetCurrentDirectory(intermediateDir.ToString(pathTable));
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink("symlink.imports.link", symlinkRelativeTarget, true));

                if (useJunction)
                {
                    EstablishJunction(sourceDir.ToString(pathTable), intermediateDir.ToString(pathTable));
                }
                else
                {
                    Directory.SetCurrentDirectory(sourceDir.GetParent(pathTable).ToString(pathTable));
                    FileUtilities.DeleteDirectoryContents("x64", deleteRootDirectory: true);
                    XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink("x64", @"..\icache\current\x64", false));
                }

                Directory.SetCurrentDirectory(currentDirectory);

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallAccessNestedSiblingSymLinkOnFiles",
                    inputFiles:
                        ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                            FileArtifact.CreateSourceFile(sourceOfSymlink),
                            FileArtifact.CreateSourceFile(intermediateSymlink),
                            FileArtifact.CreateSourceFile(targetFile)),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                if (useJunction)
                {
                    // We access imports\x64\symlink.imports.link and imports\x64 --> icache\current\x64 is a junction.
                    // The access path imports\x64\symlink.imports.link is not resolved to icache\current\x64\symlink.imports.link.

                    if (string.Equals(symlinkRelativeTarget, @"..\..\..\targets\x64\hello.txt", OperatingSystemHelper.PathComparison))
                    {
                        // When symlink.imports.link is replaced with '..\..\..\targets\x64\hello.txt', the resulting path is a non-existent path.
                        VerifyExecutionStatus(context, result, SandboxedProcessPipExecutionStatus.ExecutionFailed);
                        VerifyExitCode(context, result, NativeIOConstants.ErrorPathNotFound);
                        SetExpectedFailures(1, 0);
                    }
                    else if (string.Equals(symlinkRelativeTarget, @"..\..\targets\x64\hello.txt", OperatingSystemHelper.PathComparison))
                    {
                        // When symlink.imports.link is replaced with '..\..\targets\x64\hello.txt', the resulting path is 'target\x64\hello.txt', which is an existing path.
                        VerifyNormalSuccess(context, result);

                        VerifyFileAccesses(
                            context,
                            result.AllReportedFileAccesses,
                            new[]
                            {
                                // We only report
                                // - imports\x64\symlink.import.link
                                // - target\x64\hello.txt
                                (sourceOfSymlink, RequestedAccess.Read, FileAccessStatus.Allowed),
                                (targetFile, RequestedAccess.Read, FileAccessStatus.Allowed)
                            });
                    }
                }
                else
                {
                    // We access imports\x64\symlink.imports.link and imports\x64 --> icache\current\x64 is a directory symlink.
                    // The accessed path imports\x64\symlink.imports.link will be resolved first to icache\current\x64\symlink.imports.link before
                    // symlink.import.link is replaced by the relative target. That is for directory symlink, Windows use the target path for resolution.
                    if (string.Equals(symlinkRelativeTarget, @"..\..\..\targets\x64\hello.txt", OperatingSystemHelper.PathComparison))
                    {
                        VerifyNormalSuccess(context, result);

                        VerifyFileAccesses(
                            context,
                            result.AllReportedFileAccesses,
                            new[]
                            {
                                // Since we access imports\x64\symlink.imports.link and imports\x64 --> icache\current\x64 is a directory symlink,
                                // we only report imports\x64\symlink.imports.link and targets\x64\hello.txt. Note that we do not report
                                // all possible forms of path when enforcing chain of symlinks. In this case, we do not report icache\current\x64\symlink.imports.link.
                                (sourceOfSymlink, RequestedAccess.Read, FileAccessStatus.Allowed),
                                (targetFile, RequestedAccess.Read, FileAccessStatus.Allowed)
                            });
                    }
                    else if (string.Equals(symlinkRelativeTarget, @"..\..\targets\x64\hello.txt", OperatingSystemHelper.PathComparison))
                    {
                        // When symlink.imports.link is replaced with '..\..\targets\x64\hello.txt', the resulting path is a non-existent path.
                        VerifyExecutionStatus(context, result, SandboxedProcessPipExecutionStatus.ExecutionFailed);
                        VerifyExitCode(context, result, NativeIOConstants.ErrorPathNotFound);
                        SetExpectedFailures(1, 0);
                    }
                }
            }
        }

        [TheoryIfSupported(requiresSymlinkPermission: true)]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CallAccessNestedSiblingSymLinkOnFilesThroughMixedDirectorySymlinkAndJunction(bool relativeDirectorySymlinkTarget)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                // File and directory layout:
                //    Enlist
                //    |
                //    +---icache
                //    |   \---current
                //    |       \---x64
                //    |              symlink.imports.link ==> ..\..\..\targets\x64\hello.txt
                //    +---data
                //    |   \---imports
                //    |
                //    +---imports ==> \Enlist\data\imports (junction)
                //    |   \---x64 ==> ..\icache\current\x64 (or \Enlist\icache\current\x64) (directory symlink)
                //    |
                //    \---targets
                //        \---x64
                //               hello.txt

                // access: imports\x64\symlink.imports.link

                var dataImports = tempFiles.GetDirectory(pathTable, @"data\imports");
                var imports = tempFiles.GetDirectory(pathTable, "imports");
                EstablishJunction(imports.ToString(pathTable), dataImports.ToString(pathTable));

                var sourceDir = tempFiles.GetDirectory(pathTable, @"imports\x64");
                var sourceOfSymlink = tempFiles.GetFileName(pathTable, sourceDir, "symlink.imports.link");

                var intermediateDir = tempFiles.GetDirectory(pathTable, @"icache\current\x64");
                var intermediateSymlink = tempFiles.GetFileName(pathTable, intermediateDir, "symlink.imports.link");

                var targetDir = tempFiles.GetDirectory(pathTable, @"targets\x64");
                var targetFile = tempFiles.GetFileName(pathTable, targetDir, "hello.txt");

                WriteFile(pathTable, targetFile);

                // Force creation of relative symlinks.
                string currentDirectory = Directory.GetCurrentDirectory();

                Directory.SetCurrentDirectory(intermediateDir.ToString(pathTable));
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink("symlink.imports.link", @"..\..\..\targets\x64\hello.txt", true));

                Directory.SetCurrentDirectory(currentDirectory);

                if (relativeDirectorySymlinkTarget)
                {
                    // Force creation of relative symlinks.
                    currentDirectory = Directory.GetCurrentDirectory();
                    Directory.SetCurrentDirectory(sourceDir.GetParent(pathTable).ToString(pathTable));
                    FileUtilities.DeleteDirectoryContents("x64", deleteRootDirectory: true);
                    XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink("x64", @"..\icache\current\x64", false));
                    Directory.SetCurrentDirectory(currentDirectory);
                }
                else
                {
                    FileUtilities.DeleteDirectoryContents(sourceDir.ToString(pathTable), deleteRootDirectory: true);
                    XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(sourceDir.ToString(pathTable), intermediateDir.ToString(pathTable), false));
                }

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallAccessNestedSiblingSymLinkOnFiles",
                    inputFiles:
                        ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                            FileArtifact.CreateSourceFile(sourceOfSymlink),
                            FileArtifact.CreateSourceFile(intermediateSymlink),
                            FileArtifact.CreateSourceFile(targetFile)),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        // Since we access imports\x64\symlink.imports.link and imports\x64 --> icache\current\x64 is a directory symlink,
                        // we only report imports\x64\symlink.imports.link and targets\x64\hello.txt. Note that we do not report
                        // all possible forms of path when enforcing chain of symlinks. In this case, we do not report icache\current\x64\symlink.imports.link.
                        (sourceOfSymlink, RequestedAccess.Read, FileAccessStatus.Allowed),
                        (targetFile, RequestedAccess.Read, FileAccessStatus.Allowed)
                    });
            }
        }

        private async Task AccessSymlinkAndVerify(BuildXLContext context, TempFileStorage tempFiles, List<TranslateDirectoryData> translateDirectoryData, string function, AbsolutePath[] paths)
        {
            var process = CreateDetourProcess(
                context,
                context.PathTable,
                tempFiles,
                argumentStr: function,
                inputFiles:
                    ReadOnlyArray<FileArtifact>.FromWithoutCopy(paths.Select(FileArtifact.CreateSourceFile).ToArray()),
                inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

            SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                pathTable: context.PathTable,
                ignoreSetFileInformationByHandle: false,
                ignoreZwRenameFileInformation: false,
                monitorNtCreate: true,
                ignoreReparsePoints: false,
                context: context,
                pip: process,
                errorString: out _,
                directoriesToTranslate: translateDirectoryData);

            VerifyNormalSuccess(context, result);

            VerifyFileAccesses(
                context,
                result.AllReportedFileAccesses,
                paths.Select(path => (path, RequestedAccess.Read, FileAccessStatus.Allowed)).ToArray());
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallAccessJunctionSymlink()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var realDirectory = tempFiles.GetDirectory(pathTable, @"real\subdir");
                var realTargetDirectory = tempFiles.GetDirectory(pathTable, @"real\target");
                var realTarget = tempFiles.GetFileName(pathTable, realTargetDirectory, "hello.txt");
                WriteFile(pathTable, realTarget);

                var junctionDirectory = tempFiles.GetDirectory(pathTable, @"junction\subdir");
                var junctionTargetDirectory = tempFiles.GetDirectory(pathTable, @"junction\target");
                var junctionTarget = tempFiles.GetFileName(pathTable, junctionTargetDirectory, "hello.txt");
                WriteFile(pathTable, junctionTarget);

                EstablishJunction(junctionDirectory.ToString(pathTable), realDirectory.ToString(pathTable));

                // Force creation of relative symlinks.
                var currentDirectory = Directory.GetCurrentDirectory();
                Directory.SetCurrentDirectory(realDirectory.ToString(pathTable));
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink("symlink.link", @"..\target\hello.txt", true));

                Directory.SetCurrentDirectory(currentDirectory);

                // We now have the following file structure:
                // real
                //   |
                //   +- target
                //   |   |
                //   |   + - hello.txt
                //   |
                //   +- subdir
                //        |
                //        + - symlink.link ==> ..\target\hello.txt
                // junction
                //   |
                //   +- target
                //   |   |
                //   |   + - hello.txt
                //   |
                //   +- subdir ==> real\subdir

                var realSymlink = tempFiles.GetFileName(pathTable, realDirectory, "symlink.link");
                var translateToReal = new TranslateDirectoryData(
                    $"{junctionDirectory.ToString(pathTable)}<{realDirectory.ToString(pathTable)}",
                    junctionDirectory,
                    realDirectory);

                // Access real\subdir\symlink.link
                // The final file to access is real\target\hello.txt.
                await AccessSymlinkAndVerify(context, tempFiles, new List<TranslateDirectoryData>(), "CallAccessJunctionSymlink_Real",
                    [
                        // Specify as inputs in manifest
                        // - real\subdir\symlink.link
                        // - real\target\hello.txt
                        realSymlink,
                        realTarget

                        // Chain of symlinks:
                        // 1. real\subdir\symlink.link
                        // 2. real\target\hello.txt

                        // Test does not need directory translation because all paths in the chain is covered by the manifest.
                    ]);

                // Access junction\subdir\symlink.link
                // The final file to access is junction\target\hello.txt, and not real\target\hello.txt. Although junction\subdir points to real\subdir,
                // the resolution of junction doesn't expand it to the target.
                await AccessSymlinkAndVerify(context, tempFiles, new List<TranslateDirectoryData>() { translateToReal }, "CallAccessJunctionSymlink_Junction",
                    [
                        realSymlink,
                        junctionTarget

                        // Chain of symlinks:
                        // 1. junction\subdir\symlink.link -- needs translation from junction\subdir to real\subdir
                        // 2. junction\target\hello.txt -- covered by manifest
                    ]);

                var junctionSymlink = tempFiles.GetFileName(pathTable, junctionDirectory, "symlink.link");
                var translateToJunction = new TranslateDirectoryData(
                    $"{realDirectory.ToString(pathTable)}<{junctionDirectory.ToString(pathTable)}",
                    realDirectory,
                    junctionDirectory);

                // Access real\subdir\symlink.link
                // The final file to access is real\target\hello.txt
                await AccessSymlinkAndVerify(context, tempFiles, new List<TranslateDirectoryData>() { translateToJunction }, "CallAccessJunctionSymlink_Real",
                    [
                        // Specify as inputs in manifest
                        // - junction\subdir\symlink.link
                        // - real\target\hello.txt
                        junctionSymlink,
                        realTarget

                        // Chain of symlinks:
                        // 1. real\subdir\symlink.link -- needs translation from real\subdir to junction\subdir
                        // 2. real\target\hello.txt -- covered by manifest
                    ]);

                // Access junction\subdir\symlink.link
                // The final file to access is junction\target\hello.txt; see the reason above when accessing junction\subdir\symlink.link.
                await AccessSymlinkAndVerify(context, tempFiles, new List<TranslateDirectoryData>(), "CallAccessJunctionSymlink_Junction",
                    [
                        // Specify as inputs in manifest
                        // - junction\subdir\symlink.link
                        // - junction\target\hello.txt
                        junctionSymlink,
                        junctionTarget

                        // Chain of symlinks:
                        // 1. junction\subdir\symlink.link -- covered by manifest
                        // 2. junction\target\hello.txt -- covered by manifest
                    ]);
            }
        }

        [TheoryIfSupported(requiresSymlinkPermission: true)]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ProbeDirectorySymlink(bool probeDirectorySymlinkAsDirectory)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var targetDirectory = tempFiles.GetDirectory(pathTable, @"target");
                var directoryLink = tempFiles.GetFileName("directory.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(directoryLink, targetDirectory.ToString(pathTable), isTargetFile: false));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallProbeDirectorySymlink",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    ignoreFullReparsePointResolving: false,
                    context: context,
                    pip: process,
                    probeDirectorySymlinkAsDirectory: probeDirectorySymlinkAsDirectory,
                    errorString: out _);

                if (probeDirectorySymlinkAsDirectory)
                {
                    VerifyNormalSuccess(context, result);
                    VerifyFileAccesses(
                        context,
                        result.AllReportedFileAccesses,
                        new[]
                        {
                            (AbsolutePath.Create(pathTable, directoryLink), RequestedAccess.Probe, FileAccessStatus.Allowed)
                        });
                }
                else
                {
                    SetExpectedFailures(1, 0);
                    AssertVerboseEventLogged(ProcessesLogEventId.PipProcessDisallowedFileAccess);

                    VerifyAccessDenied(context, result);
                    VerifyFileAccessViolations(result, 1);
                    VerifyFileAccesses(
                        context,
                        result.AllReportedFileAccesses,
                        new[]
                        {
                            (AbsolutePath.Create(pathTable, directoryLink), RequestedAccess.Probe, FileAccessStatus.Denied)
                        });
                }
            }
        }

        [TheoryIfSupported(requiresSymlinkPermission: true)]
        [MemberData(nameof(TruthTable.GetTable), 1, MemberType = typeof(TruthTable))]
        public async Task ReadNtPrefixedSymlinks(bool ignoreFullReparsePointResolving)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                string target = tempFiles.GetFileName("Target");
                string targetWithNtPrefix = FileSystemWin.LongPathPrefix + target;
                WriteFile(target, string.Empty);

                string intermediateSymlink = tempFiles.GetFileName("Intermediate.lnk");
                string intermediateSymlinkWithNtPrefix = FileSystemWin.LongPathPrefix + intermediateSymlink;
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(intermediateSymlinkWithNtPrefix, targetWithNtPrefix, isTargetFile: true));

                string symlinkSource = tempFiles.GetFileName("SourceOfSymLink.link");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(symlinkSource, intermediateSymlinkWithNtPrefix, isTargetFile: true));

                AbsolutePath targetPath = AbsolutePath.Create(pathTable, target);
                AbsolutePath intermediatePath = AbsolutePath.Create(pathTable, intermediateSymlink);
                AbsolutePath sourcePath = AbsolutePath.Create(pathTable, symlinkSource);

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDetouredFileCreateThatAccessesChainOfSymlinks",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                    [
                        FileArtifact.CreateSourceFile(sourcePath),
                        FileArtifact.CreateSourceFile(intermediatePath),
                        FileArtifact.CreateSourceFile(targetPath),
                    ]),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: process,
                    ignoreFullReparsePointResolving: ignoreFullReparsePointResolving,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(context, result.AllReportedFileAccesses, new[]
                {
                    (sourcePath, RequestedAccess.Read, FileAccessStatus.Allowed),
                    (intermediatePath, RequestedAccess.Read, FileAccessStatus.Allowed),
                    (targetPath, RequestedAccess.Read, FileAccessStatus.Allowed),
                });
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task ProbeDirectorySymlinkWithFullResolvingEnabledAsync()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var targetDirectory = tempFiles.GetDirectory(pathTable, @"target");
                var directoryLink = tempFiles.GetFileName("directory.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(directoryLink, targetDirectory.ToString(pathTable), isTargetFile: false));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallProbeDirectorySymlink",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy([FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, directoryLink))]),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: process,
                    ignoreFullReparsePointResolving: false,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (AbsolutePath.Create(pathTable, directoryLink), RequestedAccess.Probe, FileAccessStatus.Allowed)
                    },
                    [targetDirectory]);
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task UseCreateFileWToOpenAReparsePointNotTheTargetWithFullResolvingEnabledAsync()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var targetDirectory = tempFiles.GetDirectory(pathTable, @"target");

                var directoryLink = tempFiles.GetFileName("directory.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(directoryLink, targetDirectory.ToString(pathTable), isTargetFile: false));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallProbeDirectorySymlinkTargetWithReparsePointFlag",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    context: context,
                    pip: process,
                    ignoreFullReparsePointResolving: false,
                    unexpectedFileAccessesAreErrors: false,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (AbsolutePath.Create(pathTable, directoryLink), RequestedAccess.Probe, FileAccessStatus.Denied) // Denied, because reparse point is treated as a file.
                    },
                    [targetDirectory]);
            }
        }

        [TheoryIfSupported(requiresSymlinkPermission: true)]
        [MemberData(nameof(TruthTable.GetTable), 3, MemberType = typeof(TruthTable))]
        public async Task ProbeDirectorySymlinkTarget(bool targetExists, bool withReparsePointFlag, bool probeDirectorySymlinkAsDirectory)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var targetDirectory = tempFiles.GetDirectory(pathTable, @"target");
                if (!targetExists)
                {
                    FileUtilities.DeleteDirectoryContents(targetDirectory.ToString(pathTable), deleteRootDirectory: true);
                }

                var directoryLink = tempFiles.GetFileName("directory.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(directoryLink, targetDirectory.ToString(pathTable), isTargetFile: false));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: withReparsePointFlag
                        ? "CallProbeDirectorySymlinkTargetWithReparsePointFlag"
                        : "CallProbeDirectorySymlinkTargetWithoutReparsePointFlag",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreFullReparsePointResolving: false,
                    ignoreReparsePoints: false,
                    probeDirectorySymlinkAsDirectory: probeDirectorySymlinkAsDirectory,
                    unexpectedFileAccessesAreErrors: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                if (withReparsePointFlag)
                {
                    if (probeDirectorySymlinkAsDirectory)
                    {
                        // Probe should succeed because the directory symlink exists, regardless the existence of its target.
                        VerifyNormalSuccess(context, result);
                        VerifyFileAccesses(
                            context,
                            result.AllReportedFileAccesses,
                            new[]
                            {
                                // Access allowed on probing directory symlink because the symlink is treated as directory.
                                (AbsolutePath.Create(pathTable, directoryLink), RequestedAccess.Probe, FileAccessStatus.Allowed)
                            },
                            [
                                // Due to reparse point flag, the target directory is not followed.
                                targetDirectory
                            ]);
                    }
                    else
                    {
                        // Probe should succeed because the directory symlink exists, regardless the existence of its target.
                        // However, since the probe is treated as existing file probe, we should get access denied.
                        VerifyNormalSuccess(context, result);
                        VerifyFileAccesses(
                            context,
                            result.AllReportedFileAccesses,
                            new[]
                            {
                                // Access denied on probing directory symlink because directory symlink is treated as a file.
                                (AbsolutePath.Create(pathTable, directoryLink), RequestedAccess.Probe, FileAccessStatus.Denied)
                            },
                            [
                                // Due to reparse point flag, the target directory is not followed.
                                targetDirectory
                            ]);
                    }
                }
                else
                {
                    // No reparse point flag, i.e., we are trying to access the target directory.
                    if (targetExists)
                    {
                        VerifyNormalSuccess(context, result);
                    }
                    else
                    {
                        // Target doesn't exist, so failed with ERROR_FILE_NOT_FOUND. In some scenario, this can be ERROR_PATH_NOT_FOUND.
                        VerifyExitCode(context, result, NativeIOConstants.ErrorFileNotFound);
                        SetExpectedFailures(1, 0);
                    }

                    VerifyFileAccesses(
                        context,
                        result.AllReportedFileAccesses,
                        new[]
                        {
                            // Without reparse point, the directory symlink is read for symlink resolution.
                            (AbsolutePath.Create(pathTable, directoryLink), RequestedAccess.Read, FileAccessStatus.Denied),
                            // Without reparse point, regardless of the target's existence, the target is assumed to be probed
                            (targetDirectory, RequestedAccess.Probe, FileAccessStatus.Allowed)
                        });
                }
            }
        }

        [Theory]
        [InlineData("CallCreateNamedPipeTest")]
        [InlineData("CallCreatePipeTest")]
        public async Task TestPipeCreation(string method)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: method,
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNoFileAccesses(result);
            }
        }

        [Fact]
        public void TestPathWithTrailingPathSeparator()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            AbsolutePath path = AbsolutePath.Create(pathTable, "C:\\foo\\bar\\");
            XAssert.AreEqual(path.ToString(pathTable), "C:\\foo\\bar");
        }

        private static new void WriteFile(string path, string content = null)
        {
            content ??= Guid.NewGuid().ToString();

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.WriteAllText(path, content);
            XAssert.IsTrue(File.Exists(path));
        }

        private static FileArtifact WriteFile(PathTable pathTable, AbsolutePath filePath, string content = null)
        {
            Contract.Requires(pathTable != null);
            Contract.Requires(filePath.IsValid);

            string expandedPath = filePath.ToString(pathTable);
            WriteFile(expandedPath, content);

            return FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, expandedPath));
        }

        private static DirectoryArtifact CreateDirectory(PathTable pathTable, AbsolutePath directoryPath)
        {
            Contract.Requires(pathTable != null);
            Contract.Requires(directoryPath.IsValid);

            string expandedPath = directoryPath.ToString(pathTable);

            if (Directory.Exists(expandedPath))
            {
                Directory.Delete(expandedPath, true);
            }

            Directory.CreateDirectory(expandedPath);
            XAssert.IsTrue(Directory.Exists(expandedPath));

            return DirectoryArtifact.CreateWithZeroPartialSealId(AbsolutePath.Create(pathTable, expandedPath));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CallDeleteWithoutSharing(bool untracked)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var untrackedFile = tempFiles.GetFileName(pathTable, "untracked.txt");
                WriteFile(pathTable, untrackedFile);

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDeleteWithoutSharing",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: untracked
                        ? ReadOnlyArray<FileArtifactWithAttributes>.Empty
                        : ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(FileArtifactWithAttributes.FromFileArtifact(FileArtifact.CreateSourceFile(untrackedFile), FileExistence.Optional)),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: untracked ? ReadOnlyArray<AbsolutePath>.FromWithoutCopy(untrackedFile.GetParent(pathTable)) : ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                if (untracked)
                {
                    VerifyNoFileAccesses(result);
                    VerifySharingViolation(context, result);
                    SetExpectedFailures(1, 0);
                }
                else
                {
                    VerifyNormalSuccess(context, result);
                }
            }
        }

        [Fact]
        public async Task CallDeleteOnOpenedHardlink()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var untrackedDirectory = tempFiles.GetDirectory(pathTable, "untracked");
                WriteFile(pathTable, untrackedDirectory.Combine(pathTable, "file.txt"));
                var outputFile = tempFiles.GetFileName(pathTable, "output.txt");

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDeleteOnOpenedHardlink",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(FileArtifactWithAttributes.FromFileArtifact(FileArtifact.CreateSourceFile(outputFile), FileExistence.Required)),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.FromWithoutCopy(untrackedDirectory));

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);
            }
        }

        [TheoryIfSupported(requiresSymlinkPermission: true)]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CallDetouredCreateFileWForProbingOnly(bool withReparsePointFlag)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;
            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var createdInputPaths = new Dictionary<string, AbsolutePath>(OperatingSystemHelper.PathComparer);

                var pip = SetupDetoursTests(
                    context,
                    tempFiles,
                    pathTable,
                    "CreateFileWForProbingOnly.lnk",
                    "CreateFileWForProbingOnly.txt",
                    withReparsePointFlag
                    ? "CallDetouredCreateFileWForSymlinkProbeOnlyWithReparsePointFlag"
                    : "CallDetouredCreateFileWForSymlinkProbeOnlyWithoutReparsePointFlag",
                    isDirectoryTest: false,
                    createSymlink: true,
                    addCreateFileInDirectoryToDependencies: false,
                    createFileInDirectory: false,
                    addFirstFileKind: AddFileOrDirectoryKinds.AsDependency,
                    addSecondFileOrDirectoryKind: AddFileOrDirectoryKinds.AsDependency,
                    makeSecondUntracked: true,
                    createdInputPaths: createdInputPaths);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: true,
                    ignoreZwRenameFileInformation: true,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    ignoreZwOtherFileInformation: true,
                    monitorZwCreateOpenQueryFile: true,
                    context: context,
                    pip: pip,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                var pathsToFalsify = withReparsePointFlag
                    ? [createdInputPaths["CreateFileWForProbingOnly.txt"]]
                    : Array.Empty<AbsolutePath>();

                var observationsToVerify = new List<(AbsolutePath absolutePath, RequestedAccess requestedAccess, FileAccessStatus fileAccessStatus)>
                {
                    (createdInputPaths["CreateFileWForProbingOnly.lnk"], RequestedAccess.Probe, FileAccessStatus.Allowed)
                };

                if (!withReparsePointFlag)
                {
                    observationsToVerify.Add((createdInputPaths["CreateFileWForProbingOnly.txt"], RequestedAccess.Probe, FileAccessStatus.Allowed));
                }

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    observationsToVerify.ToArray(),
                    pathsToFalsify: pathsToFalsify);
            }
        }

        [Fact]
        public async Task CallCreateSelfForWrite()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                AbsolutePath createdFile = tempFiles.GetFileName(pathTable, "CreateFile");

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallCreateSelfForWrite",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(FileArtifactWithAttributes.FromFileArtifact(FileArtifact.CreateSourceFile(createdFile), FileExistence.Required)),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);
                VerifyProcessCreations(context, result.AllReportedFileAccesses, new[] { DetoursTestsExe });
            }
        }

        private class AccessReportAccumulator : IDetoursEventListener
        {
            public delegate void AccumulateFileAccesses(ReportedFileAccess access);

            private readonly BuildXLContext m_context;
            private readonly AbsolutePath m_executable;
            private readonly AccumulateFileAccesses m_accumulator;

            public AccessReportAccumulator(BuildXLContext context, AbsolutePath executable, AccumulateFileAccesses acc)
            {
                m_context = context;
                m_executable = executable;
                m_accumulator = acc;
                SetMessageHandlingFlags(
                    MessageHandlingFlags.FileAccessNotify
                    | MessageHandlingFlags.FileAccessCollect
                    | MessageHandlingFlags.ProcessDataCollect
                    | MessageHandlingFlags.ProcessDetoursStatusCollect);
            }

            public override void HandleFileAccess(FileAccessData fileAccessData)
            {
                var reportedAccess = new ReportedFileAccess(
                    fileAccessData.Operation,
                    new ReportedProcess(1, m_executable.ToString(m_context.PathTable)),
                    fileAccessData.RequestedAccess,
                    fileAccessData.Status,
                    fileAccessData.ExplicitlyReported,
                    fileAccessData.Error,
                    new Usn(0),
                    fileAccessData.DesiredAccess,
                    fileAccessData.ShareMode,
                    fileAccessData.CreationDisposition,
                    fileAccessData.FlagsAndAttributes,
                    m_executable,
                    fileAccessData.Path,
                    "",
                    FileAccessStatusMethod.PolicyBased);

                m_accumulator?.Invoke(reportedAccess);
            }

            public override void HandleDebugMessage(DebugData debugData) { }

            public override void HandleProcessData(ProcessData processData) { }

            public override void HandleProcessDetouringStatus(ProcessDetouringStatusData processDetouringStatusData) { }
        };

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDetoursResolvedPathCacheTests()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var targetDirectory = tempFiles.GetDirectory(pathTable, "SourceDirectory");
                var targetDirectoryArtifact = CreateDirectory(pathTable, targetDirectory);
                var expandedDirectoryPath = targetDirectory.Expand(pathTable).ToString();

                var firstDirectorySymlink = tempFiles.GetFileName(pathTable, "First_DirectorySymlink");
                var secondDirectorySymlink = tempFiles.GetFileName(pathTable, "Second_DirectorySymlink");

                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(firstDirectorySymlink.ToString(pathTable), secondDirectorySymlink.ToString(pathTable), false));
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(secondDirectorySymlink.ToString(pathTable), expandedDirectoryPath, false));

                var outputFile = tempFiles.GetFileName(pathTable, "SourceDirectory\\output.txt");

                // Process writes / reads the output file through the 'First_DirectorySymlink' chain, see  'ValidateResolvedPathCache()'
                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDetoursResolvedPathCacheTests",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy([FileArtifact.CreateSourceFile(firstDirectorySymlink), FileArtifact.CreateSourceFile(secondDirectorySymlink)]),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.FromWithoutCopy([targetDirectoryArtifact]),
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(
                        FileArtifactWithAttributes.Create(FileArtifact.CreateOutputFile(firstDirectorySymlink), FileExistence.Required),
                        FileArtifactWithAttributes.Create(FileArtifact.CreateOutputFile(secondDirectorySymlink), FileExistence.Required),
                        FileArtifactWithAttributes.Create(FileArtifact.CreateOutputFile(outputFile), FileExistence.Required)),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                var directlyReportedFileAccesses = new List<ReportedFileAccess>();
                var accumulator = new AccessReportAccumulator(context, process.Executable, directlyReportedFileAccesses.Add);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _,
                    unexpectedFileAccessesAreErrors: false,
                    ignoreFullReparsePointResolving: false,
                    detoursListener: accumulator);

                VerifyNormalSuccess(context, result);

                // Assert the initial write through the symbolic link happened and populated the resolved path cache and the subsequent read returned cached results
                VerifyFileAccesses(context, result.AllReportedFileAccesses, new[]
                {
                    // Uncached initial ReparsePointTarget reports
                    (firstDirectorySymlink, RequestedAccess.Read, FileAccessStatus.Allowed, new ReportedFileOperation?(ReportedFileOperation.ReparsePointTarget)),
                    (secondDirectorySymlink, RequestedAccess.Read, FileAccessStatus.Allowed, new ReportedFileOperation?(ReportedFileOperation.ReparsePointTarget)),
                    (outputFile, RequestedAccess.Write, FileAccessStatus.Allowed, null),

                    // There is no report with ReparsePointTargetCached because they have the same reported accesses as ReparsePointTarget ones, except the operation.
                    // (firstDirectorySymlink, RequestedAccess.Read, FileAccessStatus.Allowed, new ReportedFileOperation?(ReportedFileOperation.ReparsePointTargetCached)),
                    // (secondDirectorySymlink, RequestedAccess.Read, FileAccessStatus.Allowed, new ReportedFileOperation?(ReportedFileOperation.ReparsePointTargetCached)),
                    (outputFile, RequestedAccess.Read, FileAccessStatus.Allowed, null),
                });

                // We use a Detours event listener to get every reported file access to avoid deduplication in the sandboxed process results, if we don't the same reports after
                // invalidating the cache only show up once.
                XAssert.IsTrue(directlyReportedFileAccesses.Count > 0);

                // Assert we have at least 4 ReparsePointTarget reports, 2 before the cache got populated and 2 after the cache got invalidated.
                // Note that in the normal case the resolved path after the adjustment is a fully resolved path and so there is no enforcement of symlink chain. However,
                // in case like CB where its output folder is a reparse point (e.g., d:\dbs\el\bxlint\out), the fully resolved path gets into the enforcement of the symlink chain. Thus
                // instead of being reported with CreateFileW as its operation, the fully resolve path is reported with ReparsePointTarget.
                XAssert.IsTrue(directlyReportedFileAccesses.Where(report => report.Operation == ReportedFileOperation.ReparsePointTarget).Count() >= 4);

                // Assert we have three ReparsePointTargetCached reports, those get reported once the process tries to read the output file through the symbolic link chain and resolving
                // does not need to happen again, as the cache is populated
                XAssert.IsTrue(directlyReportedFileAccesses.Where(report => report.Operation == ReportedFileOperation.ReparsePointTargetCached).Count() >= 2);
            }
        }

        [TheoryIfSupported(requiresSymlinkPermission: true)]
        [MemberData(nameof(TruthTable.GetTable), 1, MemberType = typeof(TruthTable))]
        public async Task CallOpenNonExistentFileThroughDirectorySymlink(bool useNtOpen)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var targetDirectory = tempFiles.GetDirectory("A");
                var nestedDirectory = tempFiles.GetDirectory(@"A\B");
                var absentFile = tempFiles.GetFileName(@"A\B\absent.txt");

                var symlink = tempFiles.GetFileName("A.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(symlink, targetDirectory, false));

                var symlinkPath = AbsolutePath.Create(pathTable, symlink);
                var absentFilePath = AbsolutePath.Create(pathTable, absentFile);

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: useNtOpen ? "CallNtOpenNonExistentFileThroughDirectorySymlink" : "CallOpenNonExistentFileThroughDirectorySymlink",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy([FileArtifact.CreateSourceFile(symlinkPath)]),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                var directlyReportedFileAccesses = new List<ReportedFileAccess>();
                var accumulator = new AccessReportAccumulator(context, process.Executable, directlyReportedFileAccesses.Add);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _,
                    unexpectedFileAccessesAreErrors: false,
                    ignoreFullReparsePointResolving: false,
                    detoursListener: accumulator);

                VerifyExecutionStatus(context, result, SandboxedProcessPipExecutionStatus.ExecutionFailed);
                VerifyExitCode(context, result, NativeIOConstants.ErrorFileNotFound);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (symlinkPath, RequestedAccess.Read, FileAccessStatus.Allowed, new ReportedFileOperation?(ReportedFileOperation.ReparsePointTarget)),
                        (absentFilePath, RequestedAccess.Read, FileAccessStatus.Allowed, null),
                    },
                    pathsToFalsify:
                    [
                        // No path accesss from unresolved symlink path.
                        symlinkPath.Combine(pathTable, @"B").Combine(pathTable, "absent.txt")
                    ]);
                SetExpectedFailures(1, 0);
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDirectoryEnumerationThroughDirectorySymlink()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var targetDirectory = tempFiles.GetDirectory("Dir");
                var directoryMember = tempFiles.GetFileName(@"Dir\file.txt");
                WriteFile(directoryMember);

                var symlink = tempFiles.GetFileName("Dir.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(symlink, targetDirectory, false));

                var symlinkPath = AbsolutePath.Create(pathTable, symlink);
                var targetDirectoryPath = AbsolutePath.Create(pathTable, targetDirectory);
                var directoryMemberPath = AbsolutePath.Create(pathTable, directoryMember);

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDirectoryEnumerationThroughDirectorySymlink",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy([FileArtifact.CreateSourceFile(symlinkPath)]),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                var directlyReportedFileAccesses = new List<ReportedFileAccess>();
                var accumulator = new AccessReportAccumulator(context, process.Executable, directlyReportedFileAccesses.Add);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _,
                    unexpectedFileAccessesAreErrors: false,
                    ignoreFullReparsePointResolving: false,
                    detoursListener: accumulator);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (symlinkPath, RequestedAccess.Read, FileAccessStatus.Allowed, new ReportedFileOperation?(ReportedFileOperation.ReparsePointTarget)),
                        (targetDirectoryPath, RequestedAccess.Enumerate, FileAccessStatus.Allowed, null),
                        (directoryMemberPath, RequestedAccess.EnumerationProbe, FileAccessStatus.Allowed, null),
                    },
                    pathsToFalsify: new[]
                    {
                        symlinkPath.Combine(pathTable, "file.txt")
                    });
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDetoursResolvedPathCacheDealsWithUnicode()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var targetDirectory = tempFiles.GetDirectory(pathTable, "SourceDirectoryß");
                var targetDirectoryArtifact = CreateDirectory(pathTable, targetDirectory);
                var expandedDirectoryPath = targetDirectory.Expand(pathTable).ToString();

                var firstDirectorySymlink = tempFiles.GetFileName(pathTable, "First_DirectorySymlinkß");

                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(firstDirectorySymlink.ToString(pathTable), expandedDirectoryPath, false));

                var outputFile = tempFiles.GetFileName(pathTable, "SourceDirectoryß\\outputß.txt");

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDetoursResolvedPathCacheDealsWithUnicode",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy([FileArtifact.CreateSourceFile(firstDirectorySymlink)]),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.FromWithoutCopy([targetDirectoryArtifact]),
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(
                        FileArtifactWithAttributes.Create(FileArtifact.CreateOutputFile(firstDirectorySymlink), FileExistence.Required),
                        FileArtifactWithAttributes.Create(FileArtifact.CreateOutputFile(outputFile), FileExistence.Required)),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                var directlyReportedFileAccesses = new List<ReportedFileAccess>();
                var accumulator = new AccessReportAccumulator(context, process.Executable, directlyReportedFileAccesses.Add);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _,
                    unexpectedFileAccessesAreErrors: false,
                    ignoreFullReparsePointResolving: false,
                    detoursListener: accumulator);

                VerifyNormalSuccess(context, result);

                // Assert the initial write through the symbolic link happened and populated the resolved path cache and the subsequent read returned cached results
                VerifyFileAccesses(context, result.AllReportedFileAccesses, new[]
                {
                    // Uncached initial ReparsePointTarget reports
                    (firstDirectorySymlink, RequestedAccess.Read, FileAccessStatus.Allowed, new ReportedFileOperation?(ReportedFileOperation.ReparsePointTarget)),
                    (outputFile, RequestedAccess.Write, FileAccessStatus.Allowed, null),

                    // No ReparsePointTargetCached is reported because it has the same reported file access as the previous ReparsePointTarget, except the operation.
                    // (firstDirectorySymlink, RequestedAccess.Read, FileAccessStatus.Allowed, new ReportedFileOperation?(ReportedFileOperation.ReparsePointTargetCached)),
                    (outputFile, RequestedAccess.Read, FileAccessStatus.Allowed, null),
                });

                // We use a Detours event listener to get every reported file access to avoid deduplication in the sandboxed process results, if we don't, the same reports after
                // invalidating the cache only show up once.
                XAssert.IsTrue(directlyReportedFileAccesses.Count > 0);

                // Assert we have at least 2 ReparsePointTarget reports, 1 before the cache got populated and 1 after the cache got invalidated.
                // See CallDetoursResolvedPathCacheTests why we cannot assert the exact number.
                XAssert.IsTrue(directlyReportedFileAccesses.Where(report => report.Operation == ReportedFileOperation.ReparsePointTarget).Count() >= 2);
                XAssert.IsTrue(directlyReportedFileAccesses.Where(report => report.Operation == ReportedFileOperation.ReparsePointTargetCached).Count() >= 1);
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDetoursResolvedPathPreservingLastSegmentCacheTests()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var directory = tempFiles.GetDirectory(pathTable, "Directory");
                var symlinkSource = directory.Combine(pathTable, "FileSymlink");
                var symlinkTarget = directory.Combine(pathTable, "Target");

                WriteFile(symlinkTarget.ToString(pathTable));

                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(symlinkSource.ToString(pathTable), symlinkTarget.ToString(pathTable), true));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDetoursResolvedPathPreservingLastSegmentCacheTests",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy(new FileArtifact[] { FileArtifact.CreateSourceFile(symlinkSource), FileArtifact.CreateSourceFile(symlinkTarget) }),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                var directlyReportedFileAccesses = new List<ReportedFileAccess>();
                var accumulator = new AccessReportAccumulator(context, process.Executable, directlyReportedFileAccesses.Add);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _,
                    unexpectedFileAccessesAreErrors: false,
                    ignoreFullReparsePointResolving: false,
                    detoursListener: accumulator);

                VerifyNormalSuccess(context, result);

                // We use a Detours event listener to get every reported file access to avoid deduplication in the sandboxed process results
                // Assert we got two accesses that where cached, one for the operation that preserves the last reparse point and another for the operation that doesn't
                XAssert.IsTrue(directlyReportedFileAccesses.Any(report =>
                    report.Operation == ReportedFileOperation.ReparsePointTargetCached
                    && report.Path != null
                    && AbsolutePath.Create(pathTable, report.Path) == symlinkSource));
                XAssert.IsTrue(directlyReportedFileAccesses.Any(report =>
                    report.Operation == ReportedFileOperation.ReparsePointTargetCached
                    && report.Path != null
                    && AbsolutePath.Create(pathTable, report.Path) == symlinkTarget));
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDetoursValidateResolvedReparsePointAccesses()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var targetDirectory = tempFiles.GetDirectory(pathTable, "TargetDirectory");
                var targetDirectoryArtifact = CreateDirectory(pathTable, targetDirectory);
                var expandedDirectoryPath = targetDirectory.Expand(pathTable).ToString();

                var outputFile = tempFiles.GetFileName(pathTable, "TargetDirectory\\output.txt");
                var outputArtifact = FileArtifact.CreateSourceFile(outputFile);
                var outputPath = outputArtifact.Path.ToString(pathTable);

                var anotherDirectory = tempFiles.GetDirectory(pathTable, "AnotherDirectory");
                var anotherDirectoryArtifact = CreateDirectory(pathTable, anotherDirectory);
                var anotherExpandedDirectoryPath = anotherDirectory.Expand(pathTable).ToString();

                var directorySymlink = tempFiles.GetFileName(anotherExpandedDirectoryPath, "Target_Directory.lnk");
                var directorySymlinkAbsolutePath = AbsolutePath.Create(pathTable, directorySymlink);

                var fileSymlink = tempFiles.GetFileName(expandedDirectoryPath, "file.lnk");
                var fileSymlinkAbsolutePath = AbsolutePath.Create(pathTable, fileSymlink);

                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(directorySymlink, $"..{Path.DirectorySeparatorChar}{Path.GetFileName(expandedDirectoryPath)}", false));
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(fileSymlink, outputPath, true));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallValidateFileSymlinkAccesses",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                        [
                            FileArtifact.CreateSourceFile(directorySymlinkAbsolutePath),
                            FileArtifact.CreateSourceFile(fileSymlinkAbsolutePath)
                        ]),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(
                        FileArtifactWithAttributes.FromFileArtifact(FileArtifact.CreateOutputFile(outputFile), FileExistence.Required)),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _,
                    unexpectedFileAccessesAreErrors: false,
                    ignoreFullReparsePointResolving: false);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(context, result.AllReportedFileAccesses, new[]
                {
                    // Intermediate directory symbolic links are always reported with Read access only
                    (directorySymlinkAbsolutePath, RequestedAccess.Read, FileAccessStatus.Allowed),

                    // Symlink is read to get to the target file.
                    // Note that, although the write will be through symlink, the symlink itself is not written to.
                    (fileSymlinkAbsolutePath, RequestedAccess.Read, FileAccessStatus.Allowed),

                    // The target file is opened for read and write, but is denied by the policy because no output file is specified.
                    (outputFile, RequestedAccess.ReadWrite, FileAccessStatus.Allowed)
                });
            }
        }

        [TheoryIfSupported(requiresSymlinkPermission: true)]
        [InlineData(@"F\A.lnk\D\B.lnk\e.txt", new[] { @"F\A.lnk\D\B.lnk\e.txt" })]
        [InlineData(@"A", new[] { @"F\A.lnk\D\B.lnk\e.txt" })]
        [InlineData(@"F\A.lnk\D\B.lnk", new[] { @"A\E\B1.lnk", @"F\A.lnk\D\B.lnk", @"A\B2\e.txt" })]
        [InlineData(@"F\A.lnk\D", new[] { @"A\E\B1.lnk", @"F\A.lnk\D\B.lnk", @"A\B2\e.txt" })]
        [InlineData(@"F\A.lnk", new[] { @"F\A.lnk", @"A\E\B1.lnk", @"A\D\B.lnk", @"A\B2\e.txt" })]
        [InlineData(@"F", new[] { @"F\A.lnk", @"A\E\B1.lnk", @"A\D\B.lnk", @"A\B2\e.txt" })]
        public async Task CallOpenFileThroughDirectorySymlinksSelectivelyEnforceAsync(string directoryToEnforceReparsePointsFor, string[] accessedFiles)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var directory_A = tempFiles.GetDirectory(pathTable, "A");
                var directory_F = tempFiles.GetDirectory(pathTable, @"F");
                var directory_A_B2 = tempFiles.GetDirectory(pathTable, @"A\B2");
                var directory_A_D = tempFiles.GetDirectory(pathTable, @"A\D");
                var directory_A_E = tempFiles.GetDirectory(pathTable, @"A\E");

                // Create symlink from A.lnk -> A
                var symlink_F_ALnk = tempFiles.GetFileName(pathTable, @"F\A.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(
                    symlink_F_ALnk.Expand(pathTable).ExpandedPath,
                    directory_A.Expand(pathTable).ExpandedPath,
                    isTargetFile: false));

                // Create symlink from A\B1.lnk -> A\B2
                var symlink_A_E_B1Lnk = tempFiles.GetFileName(pathTable, @"A\E\B1.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(
                    symlink_A_E_B1Lnk.Expand(pathTable).ExpandedPath,
                    directory_A_B2.Expand(pathTable).ExpandedPath,
                    isTargetFile: false));

                // Create symlink from A\B.lnk -> A\B1.lnk
                var symlink_A_D_BLnk = tempFiles.GetFileName(pathTable, @"A\D\B.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(
                    symlink_A_D_BLnk.Expand(pathTable).ExpandedPath,
                    symlink_A_E_B1Lnk.Expand(pathTable).ExpandedPath,
                    isTargetFile: false));

                var file_Alnk_D_Blnk_eTxt = tempFiles.GetFileName(pathTable, @"F\A.lnk\D\B.lnk\e.txt");
                WriteFile(pathTable, file_Alnk_D_Blnk_eTxt);

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallOpenFileThroughDirectorySymlinksSelectivelyEnforce",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                        accessedFiles.Select(accessedFile =>
                        {
                            return FileArtifact.CreateSourceFile(tempFiles.GetFileName(pathTable, accessedFile));
                        }).ToArray()),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty); ;

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _,
                    unexpectedFileAccessesAreErrors: false,
                    ignoreFullReparsePointResolving: true,
                    directoriesToEnableFullReparsePointParsing: new List<AbsolutePath>() { tempFiles.GetFileName(pathTable, directoryToEnforceReparsePointsFor) });

                VerifyNormalSuccess(context, result);
                VerifyFileAccesses(context, result.AllReportedFileAccesses, accessedFiles.Select(accessedFile =>
                {
                    return (tempFiles.GetFileName(pathTable, accessedFile), RequestedAccess.Read, FileAccessStatus.Allowed);
                }).ToArray());
            }
        }

        [TheoryIfSupported(requiresSymlinkPermission: true)]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task CallOpenFileThroughMultipleDirectorySymlinksAsync(bool ignoreFullReparsePointResolving, bool useSelectDirectoriesForFullyResolving)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var directory_AA = tempFiles.GetDirectory(pathTable, "AA");
                var directory_A = tempFiles.GetDirectory(pathTable, "A");
                var directory_A_B2 = tempFiles.GetDirectory(pathTable, @"A\B2");
                var directory_A_B2_C = tempFiles.GetDirectory(pathTable, @"A\B2\C");
                var directory_A_B2_C_D2 = tempFiles.GetDirectory(pathTable, @"A\B2\C\D2");
                var file_A_B2_C_D2_eTxt = tempFiles.GetFileName(pathTable, @"A\B2\C\D2\e.txt");
                WriteFile(pathTable, file_A_B2_C_D2_eTxt);

                // Create symlink from A\B1.lnk -> A\B2
                var symlink_A_B1Lnk = tempFiles.GetFileName(pathTable, @"A\B1.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(symlink_A_B1Lnk.Expand(pathTable).ExpandedPath, "B2", isTargetFile: false));

                // Create symlink from A\B.lnk -> A\B1.lnk
                var symlink_A_BLnk = tempFiles.GetFileName(pathTable, @"A\B.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(symlink_A_BLnk.Expand(pathTable).ExpandedPath, "B1.lnk", isTargetFile: false));

                // Create symlink from A\B2\C\D1.lnk -> A\B2\C\D2
                var symlink_A_B2_C_D1Lnk = tempFiles.GetFileName(pathTable, @"A\B2\C\D1.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(symlink_A_B2_C_D1Lnk.Expand(pathTable).ExpandedPath, "D2", isTargetFile: false));

                // Create symlink from A\B2\C\D.lnk -> A\B2\C\D1.lnk
                var symlink_A_B2_C_DLnk = tempFiles.GetFileName(pathTable, @"A\B2\C\D.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(symlink_A_B2_C_DLnk.Expand(pathTable).ExpandedPath, "D1.lnk", isTargetFile: false));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallOpenFileThroughMultipleDirectorySymlinks",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                        [
                            FileArtifact.CreateSourceFile(symlink_A_BLnk),
                            FileArtifact.CreateSourceFile(symlink_A_B1Lnk),
                            FileArtifact.CreateSourceFile(symlink_A_B2_C_DLnk),
                            FileArtifact.CreateSourceFile(symlink_A_B2_C_D1Lnk),
                            FileArtifact.CreateSourceFile(file_A_B2_C_D2_eTxt)
                        ]),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty); ;

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _,
                    unexpectedFileAccessesAreErrors: false,
                    ignoreFullReparsePointResolving: ignoreFullReparsePointResolving,
                    directoriesToEnableFullReparsePointParsing: useSelectDirectoriesForFullyResolving
                        ? [directory_A, directory_AA]
                        : [directory_AA]);

                if (!ignoreFullReparsePointResolving || useSelectDirectoriesForFullyResolving)
                {
                    VerifyNormalSuccess(context, result);

                    VerifyFileAccesses(context, result.AllReportedFileAccesses, new[]
                    {
                        (symlink_A_BLnk, RequestedAccess.Read, FileAccessStatus.Allowed),
                        (symlink_A_B1Lnk, RequestedAccess.Read, FileAccessStatus.Allowed),
                        (symlink_A_B2_C_DLnk, RequestedAccess.Read, FileAccessStatus.Allowed),
                        (symlink_A_B2_C_D1Lnk, RequestedAccess.Read, FileAccessStatus.Allowed),
                        (file_A_B2_C_D2_eTxt, RequestedAccess.Read, FileAccessStatus.Allowed),
                    },
                    [
                        tempFiles.GetFileName(pathTable, @"A\B1\C\D1.lnk"),
                        tempFiles.GetFileName(pathTable, @"A\B1\C\D1.lnk\e.txt")
                    ]);
                }
                else
                {
                    VerifyNormalSuccess(context, result);
                    VerifyFileAccesses(context, result.AllReportedFileAccesses, new[]
                    {
                        (tempFiles.GetFileName(pathTable, @"A\B.lnk\C\D.lnk\e.txt"), RequestedAccess.Read, FileAccessStatus.Denied),
                    });
                }
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDeleteDirectorySymlinkThroughDifferentPath()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var directory_D = tempFiles.GetDirectory(pathTable, @"D");
                var directory_D_E = tempFiles.GetDirectory(pathTable, @"D\E");
                var directory_D_X = tempFiles.GetDirectory(pathTable, @"D\X");
                var file_D_E_fTxt = tempFiles.GetFileName(pathTable, @"D\E\f.txt");
                WriteFile(pathTable, file_D_E_fTxt);

                var file_D_X_fTxt = tempFiles.GetFileName(pathTable, @"D\X\f.txt");
                WriteFile(pathTable, file_D_X_fTxt);

                // Create symlink from D\E.lnk -> D\E
                var symlink_D_ELnk = tempFiles.GetFileName(pathTable, @"D\E.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(symlink_D_ELnk.Expand(pathTable).ExpandedPath, "E", isTargetFile: false));

                // Create symlink from D1.lnk -> D
                var symlink_D1Lnk = tempFiles.GetFileName(pathTable, @"D1.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(symlink_D1Lnk.Expand(pathTable).ExpandedPath, "D", isTargetFile: false));

                // Create symlink from D2.lnk -> D
                var symlink_D2Lnk = tempFiles.GetFileName(pathTable, @"D2.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(symlink_D2Lnk.Expand(pathTable).ExpandedPath, "D", isTargetFile: false));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDeleteDirectorySymlinkThroughDifferentPath",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                        [
                            FileArtifact.CreateSourceFile(file_D_E_fTxt),
                            FileArtifact.CreateSourceFile(symlink_D_ELnk),
                            FileArtifact.CreateSourceFile(symlink_D1Lnk),
                            FileArtifact.CreateSourceFile(symlink_D2Lnk),
                            FileArtifact.CreateSourceFile(file_D_X_fTxt),
                        ]),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(
                        [
                            FileArtifact.CreateOutputFile(symlink_D_ELnk).WithAttributes(FileExistence.Required),
                        ]),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty); ;

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _,
                    unexpectedFileAccessesAreErrors: false,
                    ignoreFullReparsePointResolving: false);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(context, result.AllReportedFileAccesses, new[]
                {
                    (file_D_E_fTxt, RequestedAccess.Read, FileAccessStatus.Allowed),
                    (symlink_D_ELnk, RequestedAccess.Read, FileAccessStatus.Allowed),
                    (symlink_D1Lnk, RequestedAccess.Read, FileAccessStatus.Allowed),
                    (symlink_D2Lnk, RequestedAccess.Read, FileAccessStatus.Allowed),
                    (file_D_X_fTxt, RequestedAccess.Read, FileAccessStatus.Allowed),
                });
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallModifyDirectorySymlinkThroughDifferentPathIgnoreFullyResolve()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var directory_D1 = tempFiles.GetDirectory(pathTable, @"D1");
                var directory_D2 = tempFiles.GetDirectory(pathTable, @"D2");

                // Create file symlink D1\f.lnk -> D1\x.txt
                var file_D1_xTxt = tempFiles.GetFileName(pathTable, @"D1\x.txt");
                WriteFile(pathTable, file_D1_xTxt);
                var symlink_D1_fLnk = tempFiles.GetFileName(pathTable, @"D1\f.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(symlink_D1_fLnk.Expand(pathTable).ExpandedPath, "x.txt", isTargetFile: true));

                // Create file symlink D2\f.lnk -> D2\y.txt
                var file_D2_yTxt = tempFiles.GetFileName(pathTable, @"D2\y.txt");
                WriteFile(pathTable, file_D2_yTxt);
                var symlink_D2_fLnk = tempFiles.GetFileName(pathTable, @"D2\f.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(symlink_D2_fLnk.Expand(pathTable).ExpandedPath, "y.txt", isTargetFile: true));

                // Create directory symlink D.lnk -> D1
                var symlink_DLnk = tempFiles.GetFileName(pathTable, @"D.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(symlink_DLnk.Expand(pathTable).ExpandedPath, "D1", isTargetFile: false));

                // Create directory symlink DD.lnk -> D.lnk
                var symlink_DDLnk = tempFiles.GetFileName(pathTable, @"DD.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(symlink_DDLnk.Expand(pathTable).ExpandedPath, "D.lnk", isTargetFile: false));

                var symlink_DDLnk_fLnk = tempFiles.GetFileName(pathTable, @"DD.lnk\f.lnk");

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallModifyDirectorySymlinkThroughDifferentPathIgnoreFullyResolve",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                        [
                            FileArtifact.CreateSourceFile(symlink_DDLnk_fLnk),
                            FileArtifact.CreateSourceFile(file_D1_xTxt),
                            FileArtifact.CreateSourceFile(file_D2_yTxt),
                        ]),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty); ;

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _,
                    unexpectedFileAccessesAreErrors: false,
                    ignoreFullReparsePointResolving: true);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(context, result.AllReportedFileAccesses, new[]
                {
                    (symlink_DDLnk_fLnk, RequestedAccess.Read, FileAccessStatus.Allowed),
                    (file_D1_xTxt, RequestedAccess.Read, FileAccessStatus.Allowed),
                    (file_D2_yTxt, RequestedAccess.Read, FileAccessStatus.Allowed),
                    (symlink_DLnk, RequestedAccess.Write, FileAccessStatus.Denied)
                });
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDeleteSymlinkUnderDirectorySymlinkWithFullSymlinkResolution()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var d = tempFiles.GetDirectory(pathTable, "D");

                // Create file symlink D\f.lnk -> D\f.txt
                var fTxt = tempFiles.GetFileName(pathTable, @"D\f.txt");
                WriteFile(pathTable, fTxt);
                var fLnk = tempFiles.GetFileName(pathTable, @"D\f.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(fLnk.Expand(pathTable).ExpandedPath, "f.txt", isTargetFile: true));

                // Create directory symlink D.lnk -> D
                var dLnk = tempFiles.GetFileName(pathTable, @"D.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(dLnk.Expand(pathTable).ExpandedPath, "D", isTargetFile: false));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDeleteSymlinkUnderDirectorySymlinkWithFullSymlinkResolution",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy(
                        [
                            FileArtifact.CreateSourceFile(dLnk)
                        ]),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _,
                    unexpectedFileAccessesAreErrors: false,
                    ignoreNonCreateFileReparsePoints: false,
                    ignoreFullReparsePointResolving: false);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(
                    context,
                    result.AllReportedFileAccesses,
                    new[]
                    {
                        (dLnk, RequestedAccess.Read, FileAccessStatus.Allowed),  // Read D.lnk
                        (fLnk, RequestedAccess.Write, FileAccessStatus.Denied),  // Write/Delete D\f.lnk
                    },
                    [
                        fTxt,                                                 // No reported access of D\f.txt
                        tempFiles.GetFileName(pathTable, @"D.lnk\f.lnk")      // No reported access of D.lnk\f.lnk
                    ]);

                var fTxtExistence = FileUtilities.TryProbePathExistence(fTxt.Expand(pathTable).ExpandedPath, false);
                XAssert.PossiblySucceeded(fTxtExistence);
                XAssert.AreEqual(PathExistence.ExistsAsFile, fTxtExistence.Result);

                var fLnkExistence = FileUtilities.TryProbePathExistence(fLnk.Expand(pathTable).ExpandedPath, false);
                XAssert.PossiblySucceeded(fLnkExistence);
                XAssert.AreEqual(PathExistence.Nonexistent, fLnkExistence.Result);
            }
        }

        /// <summary>
        /// Verfies that a trailing slash at the end of a directory specified in a MoveFile call does not cause the call to return name invalid.
        /// </summary>
        [Theory]
        [InlineData("CallMoveFileExWWithTrailingBackSlashNtObject")]
        [InlineData("CallMoveFileExWWithTrailingBackSlashNtEscape")]
        public async Task CallMoveFileExWWithTrailingBackSlash(string method)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var sourceDir = tempFiles.GetDirectory(pathTable, "moveFileWithTrailingSlash");
                var file = tempFiles.GetFileName(sourceDir.Expand(pathTable).ExpandedPath, "file");
                WriteFile(file);

                var destDir = AbsolutePath.Create(context.PathTable, Path.Combine(tempFiles.RootDirectory, "moveFileWithTrailingSlashCopied"));

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: method,
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.FromWithoutCopy(new[] { sourceDir, destDir }));

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                XAssert.IsTrue(!Directory.Exists(sourceDir.ToString(context.PathTable)));
                XAssert.IsTrue(Directory.Exists(destDir.ToString(context.PathTable)));
                VerifyNormalSuccess(context, result);
                VerifyFileAccesses(context, result.AllReportedFileAccesses, new[]
                {
                    (AbsolutePath.Create(pathTable, file), RequestedAccess.Write, FileAccessStatus.Allowed),
                    (destDir.Combine(pathTable, "file"), RequestedAccess.Write, FileAccessStatus.Allowed),
                });
            }
        }

        [Fact]
        public async Task CallCreateFileWithNewLineCharacters()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallCreateFileWithNewLineCharacters",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                // CODESYNC: Public/Src/Sandbox/Windows/DetoursTests/Main.cpp
                // Filenames should be the same as the ones created in CallCreateFileWithNewLineCharacters
                string[] filenames = ["testfile:test\r\nstream", "testfile:test\rstream", "testfile:test\nstream", "testfile:\rteststream\n", "testfile:\r\ntest\r\n\r\n\r\nstream\r\n"];

                foreach (var filename in filenames)
                {
                    XAssert.IsTrue(
                        result.AllReportedFileAccesses.Any(rfa => rfa.GetPath(pathTable).EndsWith(filename)),
                        $"Could not find any reported file access with filename '{filename.Replace("\n", "\\n").Replace("\r", "\\r")}'");
                }
            }
        }

        [Fact]
        public async Task CallCreateStreams()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallCreateStreams",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    unexpectedFileAccessesAreErrors: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                // CODESYNC: Public/Src/Sandbox/Windows/DetoursTests/Main.cpp
                // Filenames should be the same as the ones created in CallCreateStreams
                (string, RequestedAccess, FileAccessStatus)[] filenamesAndStatus = 
                {
                    // Default stream is not treated as a special case by Detours (see IsPathToNamedStream). However, because currently path with stream cannot be added to the manifest (due
                    // to a limitation in our AbsolutePath structure), accessing this default stream is disallowed.
                    ("testfile::$DATA", RequestedAccess.Write, FileAccessStatus.Denied),

                    // File name with stream name is treated as a special case by Detours, i.e., Detours gives AllowAll access policy for accesses to such file names.
                    ("testFile:teststream:$Data", RequestedAccess.Write, FileAccessStatus.Allowed),
                    ("testfile:teststream", RequestedAccess.Write, FileAccessStatus.Allowed)
                };

                foreach (var (filename, requestedAccess, status) in filenamesAndStatus)
                {
                    ReportedFileAccess rfa = result.AllReportedFileAccesses.FirstOrDefault(rfa =>
                        rfa.GetPath(pathTable).EndsWith(filename, OperatingSystemHelper.PathComparison)
                        && rfa.RequestedAccess == requestedAccess);
                    XAssert.IsTrue(isValidReportedFileAccess(rfa), $"Could not find any reported file access with file name '{filename}' and requested access '{requestedAccess}'");
                    XAssert.AreEqual(status, rfa.Status);
                }

                static bool isValidReportedFileAccess(ReportedFileAccess rfa) =>
                    (rfa.Path != null || rfa.ManifestPath.IsValid)
                    && rfa.RequestedAccess != RequestedAccess.None
                    && rfa.Status != FileAccessStatus.None;
            }
        }

        [Fact]
        public async Task CallFindFirstEnumerateRoot()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallFindFirstEnumerateRoot",
                    inputFiles: ReadOnlyArray<FileArtifact>.Empty,
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.Empty,
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    unexpectedFileAccessesAreErrors: false,
                    context: context,
                    pip: process,
                    errorString: out _);

                VerifyNormalSuccess(context, result);

                XAssert.IsTrue(result.AllReportedFileAccesses.Any(r =>
                    r.GetPath(pathTable).Equals(@"B:\", OperatingSystemHelper.PathComparison)
                    && r.Operation == ReportedFileOperation.FindFirstFileEx
                    && r.EnumeratePattern.Equals("*.cpp", OperatingSystemHelper.PathComparison)));
                XAssert.IsFalse(result.AllReportedFileAccesses.Any(r => r.GetPath(pathTable).Equals("B:", OperatingSystemHelper.PathComparison)));
            }
        }

        /// <summary>
        /// The test from native side is expected to read a symlink file.lnk via DeviceIoControl and write the retrieved target to an output file out.txt.
        /// </summary>
        /// <remarks>
        /// We are trying to verify that the detoured call applies a translation to the returned target.
        /// </remarks>
        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDeviceIOControlGetReparsePoint()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                // Create a symlink file.lnk -> target.txt
                var target = tempFiles.GetFileName("target.txt");
                WriteFile(target);

                var symlink = tempFiles.GetFileName("file.lnk");
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(symlink, target, true));

                // Configure a directory translation tempFiles.RootDirectory -> B:\
                string targetDirTranslation = $"B:{Path.DirectorySeparatorChar}";
                var dirTranslator = new DirectoryTranslator();
                dirTranslator.AddTranslation(tempFiles.RootDirectory, targetDirTranslation);
                dirTranslator.Seal();

                var symlinkPath = dirTranslator.Translate(AbsolutePath.Create(pathTable, symlink), pathTable);

                var output = tempFiles.GetFileName("out.txt");
                var outputPath = dirTranslator.Translate(AbsolutePath.Create(pathTable, output), pathTable);

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDeviceIOControlGetReparsePoint",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy([FileArtifact.CreateSourceFile(symlinkPath)]),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(
                        [FileArtifactWithAttributes.Create(FileArtifact.CreateOutputFile(outputPath), FileExistence.Optional)]),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    errorString: out _,
                    pip: process,
                    unexpectedFileAccessesAreErrors: false,
                    ignoreFullReparsePointResolving: false,
                    ignoreDeviceIoControlGetReparsePoint: false,
                    directoryTranslator: dirTranslator);

                VerifyNormalSuccess(context, result);

                // Retrieve the target as it was returned from the device io control call and compare it with the translated target.
                // This validates the result was actually translated.
                string getReparsePointResult = File.ReadAllText(output);
                XAssert.AreEqual(
                    AbsolutePath.Create(pathTable, getReparsePointResult),
                    dirTranslator.Translate(AbsolutePath.Create(pathTable, target), pathTable));
            }
        }

        [FactIfSupported(requiresSymlinkPermission: true)]
        public async Task CallDeviceIOControlSetReparsePoint()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pathTable = context.PathTable;

            using (var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TemporaryDirectory))
            {
                // Create a symlink file_example.lnk -> target.txt
                var target = tempFiles.GetFileName("target.txt");
                var targetPath = AbsolutePath.Create(pathTable, target);
                WriteFile(target);

                var exampleSymlink = tempFiles.GetFileName("file_example.lnk");
                var exampleSymlinkPath = AbsolutePath.Create(pathTable, exampleSymlink);
                XAssert.PossiblySucceeded(FileUtilities.TryCreateSymbolicLink(exampleSymlink, target, true));

                var symlinkToProduce = tempFiles.GetFileName("file.lnk");
                var symlinkToProducePath = AbsolutePath.Create(pathTable, symlinkToProduce);

                var process = CreateDetourProcess(
                    context,
                    pathTable,
                    tempFiles,
                    argumentStr: "CallDeviceIOControlSetReparsePoint",
                    inputFiles: ReadOnlyArray<FileArtifact>.FromWithoutCopy([FileArtifact.CreateSourceFile(exampleSymlinkPath), FileArtifact.CreateSourceFile(targetPath)]),
                    inputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    outputFiles: ReadOnlyArray<FileArtifactWithAttributes>.FromWithoutCopy(
                        [FileArtifactWithAttributes.Create(FileArtifact.CreateOutputFile(symlinkToProducePath), FileExistence.Optional)]),
                    outputDirectories: ReadOnlyArray<DirectoryArtifact>.Empty,
                    untrackedScopes: ReadOnlyArray<AbsolutePath>.Empty);

                SandboxedProcessPipExecutionResult result = await RunProcessAsync(
                    pathTable: pathTable,
                    ignoreSetFileInformationByHandle: false,
                    ignoreZwRenameFileInformation: false,
                    monitorNtCreate: true,
                    ignoreReparsePoints: false,
                    disableDetours: false,
                    context: context,
                    pip: process,
                    errorString: out _,
                    unexpectedFileAccessesAreErrors: false,
                    ignoreFullReparsePointResolving: false,
                    ignoreDeviceIoControlGetReparsePoint: false);

                var expectedFileAccesses = new List<(AbsolutePath path, RequestedAccess access, FileAccessStatus status)>
                {
                    (exampleSymlinkPath, RequestedAccess.Read, FileAccessStatus.Allowed),
                    (symlinkToProducePath, RequestedAccess.Write, FileAccessStatus.Allowed)
                };

                if (result.ExitCode == 1314 /* ERROR_PRIVILEGE_NOT_HELD */)
                {
                    // When run on CB, even under admin privileges (in VM), the test fails with ERROR_PRIVILEGE_NOT_HELD.
                    // It looks like calling DeviceIoControl with FSCTL_SET_REPARSE_POINT is not allowed in some system or requires special privileges ¯_ (ツ)_/¯.
                    VerifyFileAccesses(context, result.AllReportedFileAccesses, expectedFileAccesses.ToArray());
                    SetExpectedFailures(1, 0);
                    return;
                }

                expectedFileAccesses.AddRange(
                    [
                        // When the symlink is created, the target file is read through the symlink.
                        (symlinkToProducePath, RequestedAccess.Read, FileAccessStatus.Allowed),
                        (targetPath, RequestedAccess.Read, FileAccessStatus.Allowed)
                    ]);

                VerifyNormalSuccess(context, result);

                VerifyFileAccesses(context, result.AllReportedFileAccesses, expectedFileAccesses.ToArray());
            }
        }

        private static Process CreateDetourProcess(
            BuildXLContext context,
            PathTable pathTable,
            TempFileStorage tempFileStorage,
            string argumentStr,
            ReadOnlyArray<FileArtifact> inputFiles,
            ReadOnlyArray<DirectoryArtifact> inputDirectories,
            ReadOnlyArray<FileArtifactWithAttributes> outputFiles,
            ReadOnlyArray<DirectoryArtifact> outputDirectories,
            ReadOnlyArray<AbsolutePath> untrackedScopes,
            bool sandboxDisabled = false)
        {
            Contract.Requires(pathTable != null);
            Contract.Requires(tempFileStorage != null);
            Contract.Requires(argumentStr != null);
            Contract.Requires(Contract.ForAll(inputFiles, artifact => artifact.IsValid));
            Contract.Requires(Contract.ForAll(inputDirectories, artifact => artifact.IsValid));
            Contract.Requires(Contract.ForAll(outputFiles, artifact => artifact.IsValid));
            Contract.Requires(Contract.ForAll(outputDirectories, artifact => artifact.IsValid));

            // Get the executable DetoursTestsExe.
            string currentCodeFolder = Path.GetDirectoryName(AssemblyHelper.GetAssemblyLocation(Assembly.GetExecutingAssembly()));
            string executable = Path.Combine(currentCodeFolder, DetourTestFolder, DetoursTestsExe);
            XAssert.IsTrue(File.Exists(executable));
            FileArtifact executableFileArtifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(pathTable, executable));

            var untrackedList = new List<AbsolutePath>(CmdHelper.GetCmdDependencies(pathTable));
            var allUntrackedScopes = new List<AbsolutePath>(untrackedScopes);
            allUntrackedScopes.AddRange(CmdHelper.GetCmdDependencyScopes(pathTable));

            var inputFilesWithExecutable = new List<FileArtifact>(inputFiles) { executableFileArtifact };

            var arguments = new PipDataBuilder(pathTable.StringTable);
            arguments.Add(argumentStr);

            return new Process(
                executableFileArtifact,
                AbsolutePath.Create(pathTable, tempFileStorage.RootDirectory),
                arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping),
                FileArtifact.Invalid,
                PipData.Invalid,
                ReadOnlyArray<EnvironmentVariable>.Empty,
                FileArtifact.Invalid,
                FileArtifact.Invalid,
                FileArtifact.Invalid,
                tempFileStorage.GetUniqueDirectory(pathTable),
                null,
                null,
                dependencies: ReadOnlyArray<FileArtifact>.FromWithoutCopy(inputFilesWithExecutable.ToArray()),
                outputs: outputFiles,
                directoryDependencies: inputDirectories,
                directoryOutputs: outputDirectories,
                orderDependencies: ReadOnlyArray<PipId>.Empty,
                untrackedPaths: ReadOnlyArray<AbsolutePath>.From(untrackedList),
                untrackedScopes: ReadOnlyArray<AbsolutePath>.From(allUntrackedScopes),
                tags: ReadOnlyArray<StringId>.Empty,
                successExitCodes: ReadOnlyArray<int>.FromWithoutCopy(0),
                semaphores: ReadOnlyArray<ProcessSemaphoreInfo>.Empty,
                provenance: PipProvenance.CreateDummy(context),
                toolDescription: StringId.Invalid,
                additionalTempDirectories: ReadOnlyArray<AbsolutePath>.Empty,
                options: sandboxDisabled ? Process.Options.DisableSandboxing : default);
        }

        private static void VerifyFileAccesses(
            BuildXLContext context,
            IReadOnlyList<ReportedFileAccess> reportedFileAccesses,
            (AbsolutePath absolutePath, RequestedAccess requestedAccess, FileAccessStatus fileAccessStatus, ReportedFileOperation? operation)[] observationsToVerify,
            AbsolutePath[] pathsToFalsify = null,
            (AbsolutePath absolutePath, RequestedAccess requestedAccess, FileAccessStatus fileAccessStatus, ReportedFileOperation? operation)[] observationsToFalsify = null)
        {
            PathTable pathTable = context.PathTable;
            var pathsToReportedFileAccesses = new Dictionary<AbsolutePath, List<ReportedFileAccess>>();

            foreach (var reportedFileAccess in reportedFileAccesses)
            {
                AbsolutePath reportedPath = AbsolutePath.Invalid;

                if (reportedFileAccess.ManifestPath.IsValid)
                {
                    reportedPath = reportedFileAccess.ManifestPath;
                }

                if (!string.IsNullOrWhiteSpace(reportedFileAccess.Path))
                {

                    if (AbsolutePath.TryCreate(pathTable, reportedFileAccess.Path, out AbsolutePath temp))
                    {
                        reportedPath = temp;
                    }
                }

                if (reportedPath.IsValid)
                {

                    if (!pathsToReportedFileAccesses.TryGetValue(reportedPath, out List<ReportedFileAccess> list))
                    {
                        list = [];
                        pathsToReportedFileAccesses.Add(reportedPath, list);
                    }

                    list.Add(reportedFileAccess);
                }
            }

            foreach (var (absolutePath, requestedAccess, fileAccessStatus, operation) in observationsToVerify)
            {
                bool getFileAccess = pathsToReportedFileAccesses.TryGetValue(absolutePath, out List<ReportedFileAccess> pathSpecificAccesses);
                XAssert.IsTrue(
                    getFileAccess,
                    "Expected path '{0}' is missing from the reported file accesses; reported accesses are as follows: {1}{2}",
                    absolutePath.ToString(pathTable),
                    Environment.NewLine,
                    string.Join(Environment.NewLine, pathsToReportedFileAccesses.Keys.Select(p => "--- " + p.ToString(pathTable))));

                Contract.Assert(pathSpecificAccesses != null);

                bool foundExpectedAccess = false;

                foreach (var pathSpecificAccess in pathSpecificAccesses)
                {
                    if (pathSpecificAccess.RequestedAccess == requestedAccess && pathSpecificAccess.Status == fileAccessStatus)
                    {
                        bool operationDoesMatch = true;
                        if (operation.HasValue)
                        {
                            operationDoesMatch = pathSpecificAccess.Operation == operation.Value;
                            if (!operationDoesMatch)
                            {
                                continue; // Look at all available operations to find a match
                            }
                        }

                        foundExpectedAccess = true;
                        break;
                    }
                }

                XAssert.IsTrue(
                    foundExpectedAccess,
                    "Expected access for path '{0}' with requested access '{1}' and access status '{2}' (operation: '{3}') is missing from the reported file accesses; reported accesses are as follows: {4}{5}",
                    absolutePath.ToString(pathTable),
                    requestedAccess.ToString(),
                    fileAccessStatus.ToString(),
                    operation?.ToString() ?? string.Empty,
                    Environment.NewLine,
                    string.Join(
                        Environment.NewLine,
                        pathSpecificAccesses.Select(r => $"---  {r.RequestedAccess} | {r.Status} | {r.Operation}")));

            }

            if (pathsToFalsify != null)
            {
                foreach (var absolutePath in pathsToFalsify)
                {
                    XAssert.IsFalse(
                        pathsToReportedFileAccesses.ContainsKey(absolutePath),
                        "Unexpected path '{0}' exists in the reported file accesses",
                        absolutePath.ToString(pathTable));
                }
            }

            if (observationsToFalsify != null)
            {
                foreach (var observation in observationsToFalsify)
                {
                    List<ReportedFileAccess> pathSpecificAccesses;
                    var getFileAccess = pathsToReportedFileAccesses.TryGetValue(observation.absolutePath, out pathSpecificAccesses);
                    if (!getFileAccess)
                    {
                        continue;
                    }

                    Contract.Assert(pathSpecificAccesses != null);

                    bool foundExpectedAccess = false;

                    foreach (var pathSpecificAccess in pathSpecificAccesses)
                    {
                        if (pathSpecificAccess.RequestedAccess == observation.Item2 && pathSpecificAccess.Status == observation.Item3 && pathSpecificAccess.Operation == observation.Item4)
                        {
                            bool operationDoesMatch = true;
                            if (observation.Item4.HasValue)
                            {
                                operationDoesMatch = pathSpecificAccess.Operation == observation.Item4.Value;
                            }

                            foundExpectedAccess = true && operationDoesMatch;
                            break;
                        }
                    }

                    XAssert.IsFalse(
                        foundExpectedAccess,
                        "Unexpected access for path '{0}' with requested access '{1}' and access status '{2}' exists in the reported file accesses",
                        observation.absolutePath.ToString(pathTable),
                        observation.requestedAccess.ToString(),
                        observation.fileAccessStatus.ToString());
                }
            }
        }

        private static void VerifyFileAccesses(
            BuildXLContext context,
            IReadOnlyList<ReportedFileAccess> reportedFileAccesses,
            (AbsolutePath absolutePath, RequestedAccess requestedAccess, FileAccessStatus fileAccessStatus)[] observationsToVerify,
            AbsolutePath[] pathsToFalsify = null,
            (AbsolutePath absolutePath, RequestedAccess requestedAccess, FileAccessStatus fileAccessStatus)[] observationsToFalsify = null)
        {
            var newObservationsToVerify = observationsToVerify.Select(entry => (entry.absolutePath, entry.requestedAccess, entry.fileAccessStatus, new ReportedFileOperation?())).ToArray();
            var newObservationsToFalsify = observationsToFalsify?.Select(entry => (entry.absolutePath, entry.requestedAccess, entry.fileAccessStatus, new ReportedFileOperation?())).ToArray();
            VerifyFileAccesses(context, reportedFileAccesses, newObservationsToVerify, pathsToFalsify, newObservationsToFalsify);
        }

        private static void VerifyProcessCreations(
            BuildXLContext context,
            IReadOnlyList<ReportedFileAccess> reportedFileAccesses,
            string[] executableNames)
        {
            var executableNameSet = new HashSet<string>(executableNames, OperatingSystemHelper.PathComparer);
            var reportedProcessCreations = reportedFileAccesses
                .Where(rfa => rfa.Operation == ReportedFileOperation.CreateProcess)
                .Select(rfa => !string.IsNullOrEmpty(rfa.Path) ? AbsolutePath.Create(context.PathTable, rfa.Path) : rfa.ManifestPath)
                .Select(p => p.GetName(context.PathTable))
                .Select(a => a.ToString(context.PathTable.StringTable));
            executableNameSet.ExceptWith(reportedProcessCreations);

            var remain = string.Join(", ", executableNameSet);
            var processCreationsString = string.Join(", ", reportedProcessCreations);
            XAssert.AreEqual(0, executableNameSet.Count, $"Created processes are '{{{processCreationsString}}}'. Non created processes are '{{{remain}}}'");
        }

        private static void VerifyFileAccessViolations(SandboxedProcessPipExecutionResult result, int expectedCount)
        {
            XAssert.AreEqual(
                expectedCount,
                result.UnexpectedFileAccesses.FileAccessViolationsNotAllowlisted == null
                ? 0
                : result.UnexpectedFileAccesses.FileAccessViolationsNotAllowlisted.Count);
        }

        private static void VerifyNoFileAccessViolation(SandboxedProcessPipExecutionResult result) => VerifyFileAccessViolations(result, expectedCount: 0);

        private static void VerifyNoFileAccesses(SandboxedProcessPipExecutionResult result)
        {
            XAssert.AreEqual(0, result.ObservedFileAccesses.Length);
            VerifyNoFileAccessViolation(result);
        }

        private static void VerifyNoObservedFileAccessesAndUnexpectedFileAccesses(SandboxedProcessPipExecutionResult result, string[] unexpectedFileAccesses, PathTable pathTable)
        {
            XAssert.AreEqual(0, result.ObservedFileAccesses.Length);
            VerifyFileAccessViolations(result, result.UnexpectedFileAccesses.FileAccessViolationsNotAllowlisted.Count);

            var unexpected = new HashSet<string>(unexpectedFileAccesses, OperatingSystemHelper.PathComparer);
            unexpected.ExceptWith(result.UnexpectedFileAccesses.FileAccessViolationsNotAllowlisted.Select(u => OperatingSystemHelper.CanonicalizePath(u.GetPath(pathTable))));

            if (unexpected.Count > 0)
            {
                XAssert.Fail("No unexpected file accesses on files {0} registered.", string.Join(", ", unexpected.Select(u => "'" + u + "'")));
            }
        }
    }
}
