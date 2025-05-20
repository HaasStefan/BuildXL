
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import * as Managed  from "Sdk.Managed";

namespace PackedExecution {

    export declare const qualifier: BuildXLSdk.Net8PlusQualifier;

    @@public
    export const dll = BuildXLSdk.library({
        assemblyName: "BuildXL.PackedExecution",
        generateLogs: false,
        sources: [
            ...globR(d`.`, "*.cs"),
        ],
        references: [
            Utilities.Core.dll,
            PackedTable.dll,
            Native.dll,
        ],
    });
}
