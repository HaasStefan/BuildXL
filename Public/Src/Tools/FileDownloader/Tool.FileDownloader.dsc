// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import * as Managed from "Sdk.Managed";
import * as BuildXLSdk from "Sdk.BuildXL";
import {Transformer} from "Sdk.Transformers";
import * as Deployment from "Sdk.Deployment";
import * as MSBuild from "Sdk.Selfhost.MSBuild";
import * as Frameworks from "Sdk.Managed.Frameworks";
import * as Shared from "Sdk.Managed.Shared";

namespace FileDownloader {

    export declare const qualifier: BuildXLSdk.Net6PlusQualifier;
    
    @@public
    export const downloader = BuildXLSdk.executable({
        assemblyName: "Downloader",
        skipDocumentationGeneration: true,
        skipDefaultReferences: true,
        sources: globR(d`.`, "Downloader*.cs"),
        references:[
            importFrom("BuildXL.Utilities").VstsAuthentication.dll,
            importFrom("BuildXL.Utilities").Native.dll,
            importFrom("BuildXL.Utilities").ToolSupport.dll,
            importFrom("BuildXL.Utilities").Storage.dll,
            importFrom("BuildXL.Utilities").dll,
            importFrom("BuildXL.Utilities").Utilities.Core.dll,
            importFrom("BuildXL.Cache.ContentStore").Hashing.dll,
        ],
    });

    @@public
    export const extractor = BuildXLSdk.executable({
        assemblyName: "Extractor",
        skipDocumentationGeneration: true,
        skipDefaultReferences: true,
        sources: globR(d`.`, "Extractor*.cs"),
        references:[
            importFrom("BuildXL.Utilities").Native.dll,
            importFrom("BuildXL.Utilities").Configuration.dll,
            importFrom("BuildXL.Utilities").ToolSupport.dll,
            importFrom("BuildXL.Utilities").Storage.dll,
            importFrom("BuildXL.Utilities").dll,
            importFrom("BuildXL.Utilities").Utilities.Core.dll,
            importFrom("BuildXL.Cache.ContentStore").Hashing.dll,
            importFrom("SharpZipLib").pkg,
        ],
    });

    @@public export const deployment : Deployment.Definition = {
        contents: [downloader, extractor]
    };
}
