// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Ipc {
   export declare const qualifier: BuildXLSdk.DefaultQualifierWithNet472;

    @@public
    export const dll = BuildXLSdk.library({
        assemblyName: "BuildXL.Ipc",
        sources: globR(d`.`, "*.cs"),
        addNotNullAttributeFile: true,
        references: [
            $.dll,
            $.Storage.dll,
            Utilities.Core.dll,
            importFrom("BuildXL.Cache.ContentStore").Hashing.dll,
            ...BuildXLSdk.systemMemoryDeployment,
            ...BuildXLSdk.systemThreadingTasksDataflowPackageReference,
        ],
        internalsVisibleTo: [
            "Test.BuildXL.Ipc",
            "BuildXL.Ipc.Providers",
        ],

        runtimeContentToSkip : [
            importFrom("Microsoft.Extensions.Logging.Abstractions.v6.0.3").pkg,
        ],
    });
}
