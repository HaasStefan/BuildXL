// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DistributedBuildRunner {
    export declare const qualifier : BuildXLSdk.DefaultQualifier;
    
    @@public
    export const exe = BuildXLSdk.executable({
        assemblyName: "DistributedBuildRunner",
        rootNamespace: "Tool.DistributedBuildRunner",
        skipDocumentationGeneration: true,
        sources: globR(d`.`, "*.cs"),
        references: [
            importFrom("BuildXL.Utilities").Native.dll,
        ],
        embeddedResources: [
            {
                resX: f`Properties/Resources.resx`,
            }
        ]
    });
}
