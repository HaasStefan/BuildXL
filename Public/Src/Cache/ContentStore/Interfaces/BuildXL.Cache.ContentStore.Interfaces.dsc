// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import * as Deployment from "Sdk.Deployment";
import * as ILRepack from "Sdk.Managed.Tools.ILRepack";
import * as Shared from "Sdk.Managed.Shared";

namespace Interfaces {
    export declare const qualifier : BuildXLSdk.AllSupportedQualifiers;

    @@public
    export const dll = BuildXLSdk.library({
        assemblyName: "BuildXL.Cache.ContentStore.Interfaces",
        sources: globR(d`.`, "*.cs"),
        addStackTraceHiddenAttribute: true,
        references: [
            importFrom("BuildXL.Utilities").Utilities.Core.dll,
            Hashing.dll,
            UtilitiesCore.dll,
            ...addIfLazy(BuildXLSdk.isFullFramework, () => [
                NetFx.System.Runtime.Serialization.dll,
                NetFx.System.Linq.dll,
                NetFx.System.Xml.dll,
                NetFx.System.Net.Http.dll,
            ]),
            ...addIf(qualifier.targetFramework === "netstandard2.0",
                importFrom("System.Threading.Tasks.Dataflow").pkg
            ),
            ...BuildXLSdk.bclAsyncPackages,

            ...BuildXLSdk.systemMemoryDeployment,
            ...getAzureBlobStorageSdkPackages(true),
        ],
        nullable: true,
        allowUnsafeBlocks: true,
        internalsVisibleTo: [
            "BuildXL.Cache.ContentStore",
            "BuildXL.Cache.ContentStore.Test",
            "BuildXL.Cache.ContentStore.Distributed",
            "BuildXL.Cache.ContentStore.Distributed.Test",
            "BuildXL.Cache.ContentStore.Interfaces.Test",
        ]
    });
}
