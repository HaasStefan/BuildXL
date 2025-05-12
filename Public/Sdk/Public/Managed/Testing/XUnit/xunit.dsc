// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import {Artifact, Cmd, Tool, Transformer} from "Sdk.Transformers";
import * as Deployment from "Sdk.Deployment";
import * as Managed from "Sdk.Managed";
import {isDotNetCore} from "Sdk.Managed.Shared";

export const xunitConsolePackage = importFrom("xunit.runner.console").Contents.all;

// This package is published by Dotnet Arcade and contains some important fixes we need, when
// running on .NETCoreApp 3.0, see: https://github.com/dotnet/arcade/tree/master/src/Microsoft.DotNet.XUnitConsoleRunner
// For the issue, see: https://github.com/xunit/xunit/pull/1846
export const xunitNetCoreConsolePackage = importFrom("Microsoft.DotNet.XUnitConsoleRunner").Contents.all;

/**
 * Evaluate (i.e. schedule) xUnit test runner invocation with specified arguments.
 */
@@public
export function runConsoleTest(args: TestRunArguments): Result {
    if (args.parallelGroups && args.parallelGroups.length > 0) {

        if (args.limitGroups) {
            Contract.fail("XUnit runner does not support combining parallel runs with restricting or skipping test groups");
        }

        return runMultipleConsoleTests(args);
    }

    let testDeployment = args.testDeployment;

    const tool : Transformer.ToolDefinition = Managed.Factory.createTool({
        exe: isDotNetCore(qualifier.targetFramework)
            ? testDeployment.contents.getFile(r`xunit.console.dll`)
            // Using xunit executable from different folders depending on the target framework.
            // This allow us to actually to run tests targeting different frameworks.
            : xunitConsolePackage.getFile( r`tools/${qualifier.targetFramework}/xunit.console.exe`),
        runtimeDirectoryDependencies: [
            xunitConsolePackage,
        ],
        dependsOnCurrentHostOSDirectories: true
    });

    const testMethod = args.method || Environment.getStringValue("[UnitTest]Filter.testMethod");
    const testClass  = args.className || Environment.getStringValue("[UnitTest]Filter.testClass");
    const runningInLinux = Context.getCurrentHost().os === "unix";

    if (Context.getCurrentHost().os !== "win") {
        args = args.merge<TestRunArguments>({
            noTraits: [
                "WindowsOSOnly", 
                "QTestSkip",
                "Performance",
                "SkipDotNetCore",
                ...(runningInLinux ? [ "SkipLinux" ] : []),
                ...(args.noTraits || [])
            ].unique().map(categoryToTrait)
        });
    }


    let arguments : Argument[] = CreateCommandLineArgument(testDeployment.primaryFile, args, testClass, testMethod);

    let passthroughEnvVars = args.passThroughEnvVars || [];

    let unsafeArgs: Transformer.UnsafeExecuteArguments = {
        untrackedScopes: [
            ...addIf(args.untrackTestDirectory === true, testDeployment.contents.root),
            ...((args.unsafeTestRunArguments && args.unsafeTestRunArguments.untrackedScopes) || [])
        ],
        untrackedPaths : (
            args.unsafeTestRunArguments && 
            args.unsafeTestRunArguments.untrackedPaths && 
            args.unsafeTestRunArguments.untrackedPaths.map(path => typeof(path) === "File" 
                ? <File>path 
                : File.fromPath(testDeployment.contents.root.combine(<RelativePath>path)))) 
        || [],
        // Some EBPF-related test infra makes decisions based on whether we are running on ADO or not
        // TODO: remove TF_BUILD when we retire interpose
        passThroughEnvironmentVariables: [...passthroughEnvVars, "TF_BUILD"]
    };

    let execArguments : Transformer.ExecuteArguments = {
        tool: args.tool || tool,
        tags: [
            "test", 
            ...(args.tags || [])
        ],
        arguments: arguments,
        environmentVariables: args.envVars,
        // When test directory is untracked, declare dependencies to individual files instead of the seal directory.
        // Reason: if the same directory is both untracked and declared as a dependency it's not clear which one takes
        //         precedence in terms of allowed/disallowed file accesses.
        dependencies: args.untrackTestDirectory ? testDeployment.contents.contents : [ testDeployment.contents, ...(testDeployment.targetOpaques || []) ], 
        warningRegex: "^(?=a)b", // This is a never matching warning regex. StartOfLine followed by the next char must be 'a' (look ahead), and the next char must be a 'b'.
        workingDirectory: testDeployment.contents.root,
        retryExitCodes: Environment.getFlag("RetryXunitTests") ? [1, 3] : [],
        processRetries: Environment.hasVariable("NumXunitRetries") ? Environment.getNumberValue("NumXunitRetries") : undefined,
        unsafe: unsafeArgs,
        privilegeLevel: args.privilegeLevel,
        weight: args.weight,
    };

    if (Context.getCurrentHost().os !== "win") {
        execArguments = execArguments.merge<Transformer.ExecuteArguments>({
            environmentVariables: [
                {name: "COMPlus_DefaultStackSize", value: "200000"},
            ],
            unsafe: {
                untrackedPaths: addIf(Environment.hasVariable("HOME"),
                    f`${Environment.getDirectoryValue("HOME")}/.CFUserTextEncoding`,
                    f`${Environment.getDirectoryValue("HOME")}/.sudo_as_admin_successful`
                ),
                untrackedScopes: [ d`/mnt`, d`/init`, d`/usr` ],
                passThroughEnvironmentVariables: [
                    "HOME",
                    "TMPDIR",
                    "USER"
                ]
            },
        });
    }

    // Extracting a local variable to help the typechecker to narrow the type.
    const targetFramework = qualifier.targetFramework;
    if (isDotNetCore(targetFramework)) {
        execArguments = importFrom("Sdk.Managed.Frameworks").Helpers.wrapInDotNetExeForCurrentOs(targetFramework, execArguments);
    }

    execArguments = Managed.TestHelpers.applyTestRunExecutionArgs(execArguments, args);

    const result = Transformer.execute(execArguments);

    // When running in CloudBuild, the output file that contains the results of the tests is produced in the object folder.
    // Unfortunately, that object folder is cleaned up after every build. Thus, to know whether or not the tests were run,
    // or to know the complete test results, the test output file needs to be copied to some location that is preserved
    // after the build. To this end, we copy output file to the log directory.
    // This is a temporary solution. A better solution would be to have Context.getNewOutputLogFile(...), which is similar
    // to Context.getNewOutputFile(...) but rooted at the log directory.

    const qualifierRelative = r`${qualifier.configuration}/${qualifier.targetFramework}/${qualifier.targetRuntime}`;
    const parallelRelative = args.parallelBucketIndex !== undefined ? `${args.parallelBucketIndex}` : `0`;
    const privilege = args.privilegeLevel || "standard";
    const xunitLogDir = d`${Context.getMount("LogsDirectory").path}/XUnit/${Context.getLastActiveUseModuleName()}/${Context.getLastActiveUseName()}/${qualifierRelative}/${privilege}/${parallelRelative}`;

    result.getOutputFiles().map(f => Transformer.copyFile(f, p`${xunitLogDir}/${f.name}`));
    
    return {
        xmlFile:   args.xmlFile && result.getOutputFile(args.xmlFile),
        xmlV1File: args.xmlV1File && result.getOutputFile(args.xmlV1File),
        nunitFile: args.nunitFile && result.getOutputFile(args.nunitFile),
        htmlFile:  args.htmlFile && result.getOutputFile(args.htmlFile),
    };
}

function categoryToTrait(cat: string) : NameValuePair {
    return {name: "Category", value: cat};
};

function renameOutputFile(name: string, file: Path) : Path {
    return file && file.changeExtension(a`.${name}${file.extension}`);
}

function runMultipleConsoleTests(args: TestRunArguments) : Result {
    // Run all tests with the selected traits
    for (let testGroup of args.parallelGroups)
    {
        runConsoleTest(args.override({
            // disable breaking down in groups again
            parallelGroups: undefined,

            // Avoid double-writes
            xmlFile: renameOutputFile(testGroup, args.xmlFile),
            xmlV1File: renameOutputFile(testGroup, args.xmlV1File),
            nunitFile: renameOutputFile(testGroup, args.nunitFile),
            htmlFile: renameOutputFile(testGroup, args.htmlFile),

            traits: [
                {name: "Category", value: testGroup}
            ]
        }));
    }

    // Do a single last one that passes notraits so we will run all tests without a trait.
    return runConsoleTest(args.override({
            parallelGroups: undefined,
            noTraits: args.parallelGroups.map(testGroup => <NameValuePair>{name: "Category", value: testGroup}).concat(args.noTraits || [])
        }
    ));
}

/**
 * Command line arguments that are required for running xunit.console.
 */
@@public
export interface TestRunArguments extends ConsoleArguments, Managed.TestRunArguments, Transformer.RunnerArguments {
}