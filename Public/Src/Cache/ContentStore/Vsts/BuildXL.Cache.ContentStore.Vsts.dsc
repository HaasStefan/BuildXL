// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Vsts {
    export declare const qualifier : BuildXLSdk.AllSupportedQualifiers;

    @@public
    export const dll = !BuildXLSdk.Flags.isVstsArtifactsEnabled ? undefined : BuildXLSdk.library({
        assemblyName: "BuildXL.Cache.ContentStore.Vsts",
        sources: globR(d`.`,"*.cs"),
        references: [
            ...addIfLazy(BuildXLSdk.isFullFramework, () => [
                NetFx.System.Net.Http.dll,
            ]),
            Hashing.dll,
            Library.dll,
            Interfaces.dll,
            UtilitiesCore.dll,
            ...BuildXLSdk.visualStudioServicesArtifactServicesWorkaround,
            importFrom("BuildXL.Utilities").dll,
            importFrom("BuildXL.Utilities").Native.dll,
            importFrom("BuildXL.Utilities").Authentication.dll,
            importFrom("BuildXL.Utilities").Utilities.Core.dll,
            ...getAzureBlobStorageSdkPackages(true),
            importFrom("Microsoft.VisualStudio.Services.BlobStore.Client").pkg,
            importFrom("Microsoft.VisualStudio.Services.Client").pkg,
            importFrom("Microsoft.VisualStudio.Services.InteractiveClient").pkg,
            ...BuildXLSdk.systemThreadingTasksDataflowPackageReference,
            ...BuildXLSdk.systemThreadingChannelsPackages,
            ...BuildXLSdk.bclAsyncPackages,
        ],
        internalsVisibleTo: [
            "BuildXL.Cache.MemoizationStore.Vsts",
            "BuildXL.Cache.MemoizationStore.Vsts.Test",
        ],
    });
}
