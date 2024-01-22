// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Library {

    export declare const qualifier : BuildXLSdk.AllSupportedQualifiers;

    @@public
    export const systemMemoryPackage = 
        BuildXLSdk.isFullFramework  ? importFrom("System.Memory").withQualifier({ targetFramework: "netstandard2.0" }).pkg : importFrom("System.Memory").pkg;

    @@public
    export const dll = BuildXLSdk.library({
        assemblyName: "BuildXL.Cache.ContentStore",
        sources: globR(d`.`,"*.cs"),
        references: [
            ...addIf(BuildXLSdk.isFullFramework,
                NetFx.System.Data.dll,
                NetFx.System.Runtime.Serialization.dll,
                NetFx.Netstandard.dll,
                NetFx.System.Security.dll
            ),

            ...BuildXLSdk.systemThreadingTasksDataflowPackageReference,
            systemMemoryPackage,
            
            ...importFrom("BuildXL.Utilities").Native.securityDlls,
            UtilitiesCore.dll,
            Hashing.dll,
            Interfaces.dll,
            Grpc.dll,
            // TODO: This needs to be renamed to just utilities... but it is in a package in public/src
            importFrom("BuildXL.Utilities").dll,
            importFrom("BuildXL.Utilities").Utilities.Core.dll,
            importFrom("BuildXL.Utilities").KeyValueStore.dll,
            ...importFrom("Sdk.Selfhost.RocksDbSharp").pkgs,
            importFrom("BuildXL.Utilities").Native.dll,
            importFrom("BuildXL.Utilities").Native.Extensions.dll,
            importFrom("BuildXL.Utilities").Configuration.dll,
            importFrom("BuildXL.Cache.DistributedCache.Host").Configuration.dll,
            ...getGrpcPackages(true),
            ...getGrpcDotNetPackages(),

            importFrom("Microsoft.Extensions.Logging.Abstractions.v6.0.3").pkg, // required by grpc.net packages
            ...BuildXLSdk.bclAsyncPackages,

            importFrom("Polly").pkg,
            importFrom("Polly.Contrib.WaitAndRetry").pkg,

            ...importFrom("BuildXL.Utilities").Native.securityDlls,

            ...getSystemTextJson(/*includeNetStandard*/true),
            ...BuildXLSdk.systemMemoryDeployment,
            ...BuildXLSdk.systemThreadingChannelsPackages,
            importFrom("System.Threading.Tasks.Extensions").pkg,
        ],
        runtimeContent: [
            importFrom("Sdk.Protocols.Grpc").Deployment.runtimeContent,
        ],
        // Expcluding the following dll from being deployed to avoid double write issues during evaluation.
        runtimeContentToSkip: [importFrom("Microsoft.Extensions.Logging.Abstractions.v6.0.3").pkg],
        allowUnsafeBlocks: true,
        skipDocumentationGeneration: true,
        nullable: true,
        // Should explicitly avoiding adding a file with non-nullable attributes,
        // because this project has internals visibility into Interfaces.dll that already contains
        // such attributes.
        addNotNullAttributeFile: false,
        internalsVisibleTo: [
            "BuildXL.Cache.ContentStore.Test",
            "BuildXL.Cache.ContentStore.Distributed.Test",
            "BuildXL.Cache.Host.Test",
            "BuildXL.Cache.ContentStore.Distributed.Test.LongRunning",
            "BuildXL.Cache.MemoizationStore.Distributed.Test",
        ],
    });
}
