// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace VstsTest {
    export declare const qualifier : BuildXLSdk.DefaultQualifierWithNet472;
    
    @@public
    export const dll = !BuildXLSdk.Flags.isVstsArtifactsEnabled || BuildXLSdk.isDotNetCoreOrStandard ? undefined : BuildXLSdk.cacheTest({
        assemblyName: "BuildXL.Cache.MemoizationStore.Vsts.Test",
        sources: globR(d`.`,"*.cs"),
        appConfig: f`App.config`,
        skipTestRun: BuildXLSdk.restrictTestRunToSomeQualifiers,
        references: [
            ...addIfLazy(BuildXLSdk.isFullFramework, () => [
                NetFx.System.Xml.dll,
                NetFx.System.Xml.Linq.dll,
            ]),
            ContentStore.Distributed.dll,
            ContentStore.DistributedTest.dll,
            ContentStore.UtilitiesCore.dll,
            ContentStore.Hashing.dll,
            ContentStore.Interfaces.dll,
            ContentStore.InterfacesTest.dll,
            ContentStore.Library.dll,
            ContentStore.Test.dll,
            ContentStore.Vsts.dll,
            Distributed.dll,
            InterfacesTest.dll,
            Interfaces.dll,
            Library.dll,
            VstsInterfaces.dll,
            Vsts.dll,

            importFrom("Newtonsoft.Json").pkg,
            importFrom("Microsoft.VisualStudio.Services.Client").pkg,
            ...BuildXLSdk.visualStudioServicesArtifactServicesWorkaround,
            ...BuildXLSdk.fluentAssertionsWorkaround,
            
            importFrom("BuildXL.Utilities").Authentication.dll,
            importFrom("BuildXL.Utilities").dll,
        ],
        runtimeContent: [
            ...addIf(BuildXLSdk.isFullFramework,
                importFrom("Microsoft.VisualStudio.Services.BlobStore.Client").pkg
            ),
        ],
    });
}
