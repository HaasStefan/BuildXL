// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import * as Managed from "Sdk.Managed";
import * as BuildXLSdk from "Sdk.BuildXL";
import * as Deployment from "Sdk.Deployment";

namespace BuildXL {

    export declare const qualifier: BuildXLSdk.DefaultQualifier;

    /**
     * The main deployment definition
     */
    @@public
    export const deployment : Deployment.Definition = {
        contents: [
            // primary
            importFrom("BuildXL.App").deployment,
            importFrom("BuildXL.App").serverDeployment,

            // analyzers
            importFrom("BuildXL.Tools").Execution.Analyzer.exe,
            importFrom("BuildXL.Tools").BxlScriptAnalyzer.exe,
            importFrom("BuildXL.Tools").BxlPipGraphFragmentGenerator.exe,
            importFrom("BuildXL.Cache.VerticalStore").Analyzer.exe,

            importFrom("BuildXL.Tools").SandboxedProcessExecutor.exe,

            {
                subfolder: "tools/NinjaGraphBuilder",
                contents: [ importFrom("BuildXL.Tools").NinjaGraphBuilder.exe ]
            },
            {
                subfolder: "tools/Ninjson",
                contents: [ importFrom("BuildXL.Tools.Ninjson").pkg.contents ]
            }
        ]
    };

    @@public
    export const deployed = BuildXLSdk.DeploymentHelpers.deploy({
        definition: deployment,
        targetLocation: (qualifier.targetFramework === Managed.TargetFrameworks.DefaultTargetFramework) // If targetFramework is not a default one (net8.0), then we put it in a separate directory.
        ? r`${qualifier.configuration}/${qualifier.targetRuntime}`
        : r`${qualifier.configuration}/${qualifier.targetFramework}/${qualifier.targetRuntime}`,
    });
}
