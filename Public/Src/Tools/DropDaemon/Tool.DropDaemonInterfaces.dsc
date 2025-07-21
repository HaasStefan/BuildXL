// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import {Transformer} from "Sdk.Transformers";

/**
 * Common arguments for all DropDaemon operations
 */
@@public
export interface CommonArguments extends Transformer.RunnerArguments {
    /** Optional additional dependencies. */
    dependencies?: Transformer.InputArtifact[];
    
    /** Environment variables to forward to DropDaemon.exe */
    forwardEnvironmentVars?: string[];
    
    /** Additional environment variables to set before invoking DropDaemon.exe */
    additionalEnvironmentVars?: Transformer.EnvironmentVariable[];
    
    /** Directories to untrack. */
    untrackedDirectoryScopes?: Directory[];
    
    /** Process execution timeout, before BuildXL automatically kills it. */
    timeoutInMilliseconds?: number;
    
    /** Process execution warning timeout, before BuildXL issues a warning. */
    warningTimeoutInMilliseconds?: number;
}

/**
 * VSO Drop settings.
 */
@@public
export interface DropSettings {
    /** Service URL. */
    service?: string;
    
    /** Size of batches in which to send 'associate' requests to drop service. */
    batchSize?: number;
    
    /** Maximum time in milliseconds before triggering a batch 'associate' request. */
    nagleTimeMillis?: number;
    
    /** Maximum number of uploads to issue to drop service in parallel. */
    maxParallelUploads?: number;
    
    /** Retention period in days for uploaded drops. */
    retentionDays?: number;
    
    /** Enable drop telemetry. */
    enableTelemetry?: boolean;

    /** Optional domain id. If no value specified, a default domain id is used. Must have a non-negative value. */
    dropDomainId? : number;

    /** Session guid to use when e.g. calling Azure DevOps. */
    sessionId?: string;

    /** Name of an environment variable that contains a PAT. If a value is provided and 
     *  the environment variable contains data, DropDaemon will only use PAT-based authentication.
     *  
     *  Note: This env variable must also be forwarded by BuildXL to the daemon, i.e., it must be
     *  included in forwardEnvironmentVars. */
     patEnvironmentVariable?: string;
}

/**
 * DropDaemon settings
 */
@@public
export interface DaemonSettings {
    /** Maximum number of clients DropDaemon should process concurrently. */
    maxConcurrentClients?: number;
    
    /** Whether DropDaemon should send ETW events about the progress of drop
     *  operations for the purpose of integration with CloudBuild. */
    enableCloudBuildIntegration?: boolean;
    
    /** Verbose logging. */
    verbose?: boolean;

    /** Optional timeout on the DropClient in minutes. */
    operationTimeoutMinutes?: number;

    /** Optional number of retries to perform if a DropClient fails an operation. */
    maxOperationRetries?: number;
}

/** 
 * Common properties for all 'result' types.
 */
@@public
export interface Result {
    /** All required outputs (so that other pips can take those as dependencies). */
    outputs: DerivedFile[];
}

/**
 * Arguments for starting the DropDaemon service.
 */
@@public
export interface ServiceStartArguments extends DaemonSettings, CommonArguments {}

/**
 * Information about started dropd service (returned as part of ServiceStartResult)
 */
@@public
export interface ServiceStartResult extends Result {
    dropDaemonId: Transformer.ServiceId;
    ipcMoniker: IpcMoniker;
}

/**
 * Common arguments for Drop operations. 
 */
@@public
export interface DropOperationArguments extends CommonArguments {
    /** Number of retries to connect to a running DropDaemon process. */
    maxConnectRetries?: number;
    
    /** Delay between retries to connect to a running DropDaemon process. */
    connectRetryDelayMillis?: number;
    
    /** Request name. */
    name?: string;
    
    /** Drop service config file. */
    dropServiceConfigFile?: File;
    
    /** Where to save console output. */
    consoleOutput?: Path;
}

/**
 * Arguments for the 'dropd create' operation.
 */
@@public
export interface DropCreateArguments extends DropSettings, DaemonSettings, DropOperationArguments {
    /** Should build manifests (SBOMs) be created for this drop. */
    generateBuildManifest?: boolean;
 
    /** Should the generated SBOMs be signed. */
    signBuildManifest?: boolean;

    /** Should the BCDE file (component Detection output file) be uploaded to the drop. */
    uploadBcdeFileToDrop?: boolean;
}

/**
 * Result for the 'dropd create' operation.
 */
@@public
export interface DropCreateResult extends Result {
    /** Info about the started service */
    serviceStartInfo: ServiceStartResult;

    /** Arguments used to create a drop */
    dropConfig: DropCreateArguments;
}

/**
 * Arguments for the 'dropd addartifacts' operation.
 */

interface DropArtifactInfoBase {
    /** Relative path in drop. */
    dropPath: RelativePath;
}

/**
 * Arguments for the 'dropd addartifacts' operation for dropping file.
 */
@@public
export interface DropFileInfo extends DropArtifactInfoBase {
    /** Artifact kind */
    kind: "file";
    
    /** Input file to add to drop. */
    file: File;
}

/** Arguments for changing a relative path of a file before adding it to drop. */
@@public
export interface RelativePathReplacementArguments {

    /** string to search for */
    oldValue: string;

    /** string to replace with */
    newValue: string;
}

/**
 * Arguments for the 'dropd addartifacts' operation for dropping directory.
 */
@@public
export interface DropDirectoryInfo extends DropArtifactInfoBase {
    /** Artifact kind */
    kind: "directory";

    /** Input directory to add to drop. */
    directory: StaticDirectory;

    /** 
     * Optional file path regex pattern that specifies which files from this
     * directory should be processed. 
     * 
     * (The filter is applied to the original file name)
     */
    contentFilter?: string;

    /**
     * Whether to apply content filter to file's relative path instead of the full path.
     * Defaults to 'false'.
     * 
     * Note: relative path does not start with directory separator character, i.e., given 
     * a directory "C:\a\" and a file "C:\a\b.txt", the provided regex will be matched
     * against "b.txt" and not "\b.txt".
     * 
     * If set to true, use \G anchor instead of ^ anchor to match the beginning of a relative path.
     */
    applyContentFilterToRelativePath?: boolean;

    /** 
     * Optional relative path replace arguments.
     * 
     * If specified, the replacement is performed on a relative path of
     * each file that is being added to drop when the daemon calculates
     * the final drop path.
     * 
     * For example:
     *             directory: C:\a\
     *                 files: C:\a\1.txt
     *                        C:\a\b\2.txt
     *                        C:\a\c\3.txt
     *  replacementArguments: "b\" -> "c\"
     *              dropPath: "b"
     * 
     *         files in drop: b/1.txt    <- "b" is not a part of file's ('C:\a\1.txt') relative path ('1.txt'), 
     *                                      so it's not affected by the replacement
     *                        b/c/2.txt  <- file's relative path ('b\2.txt') was changed
     *                        b/c/3.txt  <- file's relative path ('c\2.txt') did not match the search pattern,
     *                                      so it was not modified
     */
    relativePathReplacementArguments?: RelativePathReplacementArguments;   
}

@@public
export type DropArtifactInfo = DropFileInfo | DropDirectoryInfo;

//////////// Legacy types, preserved to maintain back compatibility
/**
 * Base interface for drop artifact info.
 */
@@public
export interface ArtifactInfo {

    /** Relative path in drop. */
    dropPath: RelativePath;
}

/**
 * Arguments for the 'dropd addfile' operation.
 */
@@public
export interface FileInfo extends ArtifactInfo {
    
    /** Input file to add to drop. */
    file: File;
}

/**
 * Arguments for the 'dropd adddirectories' operation.
 */
@@public
export interface DirectoryInfo extends ArtifactInfo {

    /** Input directory to add to drop. */
    directory: StaticDirectory;

    /**
     * Optional file path regex pattern that specifies which files from this
     * directory should be processed. 
     * 
     * (The filter is applied to the original file name)
     */
    contentFilter?: string;

    /**
     * Whether to apply content filter to file's relative path instead of the full path.
     * Defaults to 'false'.
     */
    applyContentFilterToRelativePath?: boolean;

    /** Optional relative path replace arguments. */
    relativePathReplacementArguments?: RelativePathReplacementArguments; 
}

//////////// Legacy types, preserved to maintain back compatibility

/**
 * Operations provided by a runner.
 */
@@public
export interface DropRunner {
    /** Invokes 'dropc create'. */
    createDrop: (args: DropCreateArguments) => DropCreateResult;

    /** Starts a shared service that can be used to process multiple drops. */
    startService: (args: ServiceStartArguments) => ServiceStartResult;

    /** Uses an existing service to create a drop. */
    createDropUnderService: (serviceStartResult: ServiceStartResult, args: DropCreateArguments) => DropCreateResult;

    /** 
     * Adds files to drop. 
     * Preferred method is to use addArtifactsToDrop.
     */
    addFilesToDrop: (createResult: DropCreateResult, args: DropOperationArguments, fileInfos: FileInfo[], tags?: string[]) => Result;

    /** 
     * Adds directories to drop. 
     * Preferred method is to use addArtifactsToDrop.
     */
    addDirectoriesToDrop: (createResult: DropCreateResult, args: DropOperationArguments, directories: DirectoryInfo[], tags?: string[]) => Result;

    /** 
     * Adds artifacts to drop.
     */
    addArtifactsToDrop: (createResult: DropCreateResult, args: DropOperationArguments, artifacts: DropArtifactInfo[], tags?: string[]) => Result;

    /**
     * Triggers finalization of a drop. Results of all add* operations associated with the drop must be provided.
     * Calling this API is optional. At the end of a build, all drops that have not been finalized, will be automatically finalized.
     */
    finalizeDrop: (createResult: DropCreateResult, args: DropOperationArguments, addOperationResults: Result[]) => Result;

    // ------------------------------- for legacy type conversion --------------------------

    /** Converts file info to drop file info. */
    fileInfoToDropFileInfo: (fileInfo: FileInfo) => DropFileInfo;

    /** Converts directory info to drop directory info. */
    directoryInfoToDropDirectoryInfo: (directoryInfo: DirectoryInfo) => DropDirectoryInfo;

    // ------------------------------- for testing only ------------------------------------
    
    /** Attempts to start a DropDaemon which doesn't connect to a drop service (useful for testing). */
    startDaemonNoDrop: (args: ServiceStartArguments) => ServiceStartResult;
    
    /** Pings the daemon process (connects to it and waits for a response before exiting). */
    pingDaemon: (serviceInfo: ServiceStartResult, args: DropOperationArguments) => Result;
    
    /** Reads the content of a file */
    testReadFile: (serviceInfo: ServiceStartResult, file: File, args: DropOperationArguments) => Result;
}

/**
 * DropDaemon tool definition template. 
 * (added to minimize code duplication between 'Tool.DropDaemonTool.dsc' and 'LiteralFiles/Tool.DropDaemonTool.dsc.literal')
 */
@@public
export const toolTemplate = <Transformer.ToolDefinition>{
    exe: undefined,
    untrackedDirectoryScopes: [
        Context.getUserHomeDirectory(),
        d`${Context.getMount("ProgramData").path}`,
    ],
    dependsOnWindowsDirectories: true,
    dependsOnAppDataDirectory: true,
    prepareTempDirectory: true,
};
