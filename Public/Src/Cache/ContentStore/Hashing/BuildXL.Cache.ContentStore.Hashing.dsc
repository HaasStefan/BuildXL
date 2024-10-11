// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import * as Deployment from "Sdk.Deployment";
import * as ILRepack from "Sdk.Managed.Tools.ILRepack";
import * as Shared from "Sdk.Managed.Shared";

namespace Hashing {
    export declare const qualifier : BuildXLSdk.AllSupportedQualifiers;

    @@public
    export const dll = BuildXLSdk.library({
        assemblyName: "BuildXL.Cache.ContentStore.Hashing",
        sources: [
            ...globR(d`.`, "*.cs"),
            f`../../../Utilities/Utilities.Core/ActionBlockSlim.cs`
        ],
        references: [
            UtilitiesCore.dll,
            
            ...getSystemTextJson(/*includeNetStandard*/true),
            ...addIfLazy(BuildXLSdk.isFullFramework, () => [
                NetFx.System.Runtime.Serialization.dll,
                NetFx.System.Xml.dll,
            ]),
            ...BuildXLSdk.systemMemoryDeployment,
            ...BuildXLSdk.systemThreadingChannelsPackages,
            importFrom("System.Threading.Tasks.Extensions").pkg,
            ...BuildXLSdk.bclAsyncPackages,
        ],
        runtimeContent: (!BuildXLSdk.Flags.isMicrosoftInternal || Context.getCurrentHost().os !== "win") ? [] : [
            {
                subfolder: "x64",
                contents: [
                    BuildXLSdk.Factory.createBinary(importFrom("DeduplicationSigned").pkg.contents, r`build/net45/x64/ddpchunk.dll`),
                    BuildXLSdk.Factory.createBinary(importFrom("DeduplicationSigned").pkg.contents, r`build/net45/x64/ddptrace.dll`)
                ]
            },
        ],
        nullable: true,
        allowUnsafeBlocks: true,
        // We reference 'ActionBlockSlim' here as sources to avoid runtime dependency to BuildXL.Utilities.
        // But we don't want to have two dlls with the same public type, so we make it internal in for this case.
        defineConstants: ["DO_NOT_EXPOSE_ACTIONBLOCKSLIM"],
        
        // The public surface of this project must be stable since any breakages are very expensive to integrate.
        usePublicApiAnalyzer: true,
        publicApiFiles: BuildXLSdk.getFrameworkSpecificPublicApiFiles(p`PublicAPI`),
    });
}
