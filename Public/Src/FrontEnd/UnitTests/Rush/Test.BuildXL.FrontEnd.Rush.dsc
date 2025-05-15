// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import * as Managed from "Sdk.Managed";
import * as MSBuild from "Sdk.Selfhost.MSBuild";
import * as Frameworks from "Sdk.Managed.Frameworks";
import {Node} from "Sdk.NodeJs";
import {Transformer} from "Sdk.Transformers";

namespace Test.Rush {
    
    // Install Rush for tests
    const rushTest = Context.getNewOutputDirectory(a`rush-test`);
    const rush = Node.runNpmPackageInstall(rushTest, [], {name: "@microsoft/rush", version: "5.153.1"});

    const rushLibTest = Context.getNewOutputDirectory(a`rushlib-test`);
    const rushlib = Node.runNpmPackageInstall(rushLibTest, [], {name: "@microsoft/rush-lib", version: "5.153.1"});

    // TODO: to enable this, we should use an older version of NodeJs for Linux
    const isRunningOnSupportedSystem = Context.getCurrentHost().cpuArchitecture === "x64";

    // TODO: enable Rush tests for non-internal builds when we address the perf issue that make them timeout
    @@public
    export const dll = isRunningOnSupportedSystem && BuildXLSdk.Flags.isMicrosoftInternal && BuildXLSdk.test({
        // QTest is not supporting opaque directories as part of the deployment
        testFramework: importFrom("Sdk.Managed.Testing.XUnit").framework,
        runTestArgs: {
            unsafeTestRunArguments: {
                // These tests require Detours to run itself, so we won't detour the test runner process itself
                runWithUntrackedDependencies: true,
                untrackedPaths: [
                    BuildXLSdk.NpmRc.getUserNpmRc()
                ]
            },
            parallelGroups: [
                 "BxlRushConfigurationTests",
                 "RushCustomCommandsTests",
                 "RushExecuteTests",
                 "RushExecuteCommandGroupTests",
                 "RushExportsTests",
                 "RushIntegrationTests",
                 "RushLibLocationTests",
                 "RushSchedulingTests",
                 "RushCustomSchedulingTests",
                 "RushCustomScriptsTests",
                 "RushAdditionalDependenciesTests",
                 "RushBuildGraphPluginTests"
            ],
            tools: {
                exec: {
                    // Rush tests are IO heavy. Let's limit the concurrency for them to avoid timeouts (default
                    // concurrency for xunit tests is 8).
                    acquireSemaphores: [{name: "BuildXL.rush_xunit_semaphore", incrementBy: 1, limit: 4}],
                    environmentVariables: [
                        ...(BuildXLSdk.NpmRc.getUserNpmRc() !== undefined ? [ { name: "UserProfileNpmRcLocation", value: BuildXLSdk.NpmRc.getUserNpmRc() } ] : [])
                    ],
                }
            },
            passThroughEnvVars: [
                ...(BuildXLSdk.NpmRc.getNpmPasswordEnvironmentVariableName() !== undefined ? [ BuildXLSdk.NpmRc.getNpmPasswordEnvironmentVariableName() ] : [])
            ]
        },
        assemblyName: "Test.BuildXL.FrontEnd.Rush",
        sources: globR(d`.`, "*.cs"), 
        references: [
            Script.dll,
            Core.dll,
            importFrom("BuildXL.Core.UnitTests").EngineTestUtilities.dll,
            importFrom("BuildXL.Engine").Engine.dll,
            importFrom("BuildXL.Engine").Processes.dll,
            importFrom("BuildXL.Engine").Scheduler.dll,
            importFrom("BuildXL.FrontEnd").Core.dll,
            importFrom("BuildXL.FrontEnd").Script.dll,
            importFrom("BuildXL.FrontEnd").Sdk.dll,
            importFrom("BuildXL.FrontEnd").JavaScript.dll,
            importFrom("BuildXL.FrontEnd").Rush.dll,
            importFrom("BuildXL.FrontEnd").TypeScript.Net.dll,
            importFrom("BuildXL.FrontEnd").Utilities.dll,
            importFrom("BuildXL.FrontEnd").SdkProjectGraph.dll,
            importFrom("BuildXL.Pips").dll,
            importFrom("BuildXL.Utilities").dll,
            importFrom("BuildXL.Utilities").Configuration.dll,
            importFrom("BuildXL.Utilities").Native.dll,
            importFrom("BuildXL.Utilities").Script.Constants.dll,
            importFrom("BuildXL.Utilities").Utilities.Core.dll,
        ],
        runtimeContent: [
            // We need Rush and Node to run these tests
            {
                // We don't really have a proper rush installation, since we are preventing npm
                // to create symlinks by default. So 'simulate' one by placing the expected
                // rush-lib dependency in a nested location.
                subfolder: r`rush/node_modules`,
                contents: [
                    rush,
                    {
                        subfolder: r`@microsoft/rush/node_modules`,
                        contents: [rushlib]
                    }
                ]
            },
            {
                subfolder: a`node`,
                contents: [Node.nodePackage]
            },
            {
                subfolder: r`Sdk/Sdk.Managed.Tools.BinarySigner`,
                contents: glob(d`../DscLibs/BinarySigner`, "*.dsc"),
            },
            {
                subfolder: r`Sdk/Sdk.Json`,
                contents: glob(d`../DscLibs/Json`, "*.dsc"),
            },
            {
                // CODESYNC Public\Src\FrontEnd\UnitTests\Rush\IntegrationTests\RushBuildGraphPluginTests.cs
                subfolder: a`rushBuildGraphMock`,
                contents: [BuildGraphPluginMock.exe]
            }
        ],
    });
}
