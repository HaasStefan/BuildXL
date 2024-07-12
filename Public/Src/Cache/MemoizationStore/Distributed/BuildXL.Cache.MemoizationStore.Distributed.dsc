// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Distributed {
    export declare const qualifier : BuildXLSdk.DefaultQualifierWithNet472AndNetStandard20;
    
    @@public
    export const dll = BuildXLSdk.library({
        assemblyName: "BuildXL.Cache.MemoizationStore.Distributed",
        sources: globR(d`.`,"*.cs"),
        references: [
            ContentStore.Distributed.dll,
            ContentStore.UtilitiesCore.dll,
            ContentStore.Hashing.dll,
            ContentStore.Interfaces.dll,
            ContentStore.Library.dll,
            ContentStore.Grpc.dll,
            Interfaces.dll,
            Library.dll,

            importFrom("BuildXL.Cache.DistributedCache.Host").Configuration.dll,
            
            importFrom("BuildXL.Utilities").dll,
            importFrom("BuildXL.Utilities").Utilities.Core.dll,
            importFrom("BuildXL.Utilities").Branding.dll,
            ...BuildXLSdk.bclAsyncPackages,
            ...importFrom("BuildXL.Cache.ContentStore").getProtobufNetPackages(true),
            ...importFrom("BuildXL.Cache.ContentStore").getSerializationPackages(true),
            importFrom("BuildXL.Cache.BuildCacheResource").Helper.dll,
        ],
        allowUnsafeBlocks: true,
        internalsVisibleTo: [
            "BuildXL.Cache.MemoizationStore.Distributed.Test",
            "BuildXL.Cache.ContentStore.Distributed.Test",
        ],
    });
}
