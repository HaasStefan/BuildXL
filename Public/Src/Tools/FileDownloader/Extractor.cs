// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.ContractsLight;
using System.IO;
using BuildXL.Native.IO;
using BuildXL.ToolSupport;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Configuration;
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.Zip;
using static BuildXL.Interop.Unix.IO;
using System.Collections.Generic;

namespace Tool.Download
{
    /// <summary>
    /// Extracts a given file with different formats (zip, tar, etc.)
    /// </summary>
    internal sealed class Extractor : ToolProgram<ExtractorArgs>
    {
        private static readonly Dictionary<string, string> s_packagesToBeChecked = new Dictionary<string, string>
        {
            {"NodeJs.linux-x64",  "node-v18.6.0-linux-x64/bin/node"},
            {"DotNet-Runtime.linux", "dotnet"}
        };

        private Extractor() : base("Extractor")
        {
        }

        /// <nodoc />
        public static int Main(string[] arguments)
        {
            return new Extractor().MainHandler(arguments);
        }

        /// <inheritdoc />
        public override bool TryParse(string[] rawArgs, out ExtractorArgs arguments)
        {
            try
            {
                arguments = new ExtractorArgs(rawArgs);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetLogEventMessage());
                arguments = null;
                return false;
            }
        }

        /// <inheritdoc />
        public override int Run(ExtractorArgs arguments)
        {
            return TryExtractToDisk(arguments) ? 0 : 1;
        }

        private bool TryExtractToDisk(ExtractorArgs arguments)
        {
            var archive = arguments.PathToFileToExtract;
            var target = arguments.ExtractDirectory;

            try
            {
                FileUtilities.DeleteDirectoryContents(target, false);
                FileUtilities.CreateDirectory(target);
            }
            catch (BuildXLException e)
            {
                ErrorExtractingArchive(archive, target, e.Message);
                return false;
            }

            switch (arguments.ArchiveType)
            {
                case DownloadArchiveType.Zip:
                    try
                    {
                        // SharpZipLib does not work well on mac and nested files are not properly handled when the zip file is constructed on Windows (with backslashes)
                        System.IO.Compression.ZipFile.ExtractToDirectory(archive, target);
                    }
                    catch (Exception e) when (e is IOException || e is DirectoryNotFoundException || e is PathTooLongException)
                    {
                        ErrorExtractingArchive(archive, target, e.Message);
                        return false;
                    }

                    break;
                case DownloadArchiveType.Gzip:
                    try
                    {
                        var targetFile = Path.Combine(target, Path.GetFileNameWithoutExtension(arguments.PathToFileToExtract));

                        using (var reader = new StreamReader(arguments.PathToFileToExtract))
                        using (var gzipStream = new GZipInputStream(reader.BaseStream))
                        using (var output = FileUtilities.CreateFileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.Read))
                        {
                            byte[] buffer = new byte[4096];
                            StreamUtils.Copy(gzipStream, output, buffer);
                        }
                    }
                    catch (GZipException e)
                    {
                        ErrorExtractingArchive(archive, target, e.Message);
                        return false;
                    }

                    break;
                case DownloadArchiveType.Tar:
                    try
                    {
                        using (var reader = new StreamReader(arguments.PathToFileToExtract))
                        using (var tar = TarArchive.CreateInputTarArchive(reader.BaseStream, nameEncoding: null))
                        {
                            tar.ExtractContents(target);
                        }
                    }
                    catch (TarException e)
                    {
                        ErrorExtractingArchive(archive, target, e.Message);
                        return false;
                    }

                    break;
                case DownloadArchiveType.Tgz:
                    try
                    {
                        using (var reader = new StreamReader(arguments.PathToFileToExtract))
                        using (var gzipStream = new GZipInputStream(reader.BaseStream))
                        using (var tar = TarArchive.CreateInputTarArchive(gzipStream, nameEncoding: null))
                        {
                            tar.ExtractContents(target);
                            if (OperatingSystemHelper.IsLinuxOS)
                            {
                                foreach (var packageName in s_packagesToBeChecked.Keys)
                                {
                                    if (target.Contains(packageName))
                                    {
                                        if (!SetExecutePermissionsForExtractedFiles(target, s_packagesToBeChecked[packageName]))
                                        {
                                            return false;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (GZipException e)
                    {
                        ErrorExtractingArchive(archive, target, e.Message);
                        return false;
                    }
                    catch (TarException e)
                    {
                        ErrorExtractingArchive(archive, target, e.Message);
                        return false;
                    }

                    break;
                case DownloadArchiveType.File:
                    Console.WriteLine("Specified download archive type is 'File'. Nothing to extract.");
                    return true;
                default:
                    throw Contract.AssertFailure($"Unexpected archive type '{arguments.ArchiveType}'");
            }

            try
            {
                if (!FileUtilities.DirectoryExistsNoFollow(target))
                {
                    ErrorNothingExtracted(archive, target);
                    return false;
                }
            }
            catch (BuildXLException e)
            {
                ErrorExtractingArchive(archive, target, e.Message);
                return false;
            }

            return true;
        }

        private void ErrorExtractingArchive(string archive, string target, string message)
        {
            Console.Error.WriteLine($"Error occured trying to extract archive  '{archive}' to '{target}': {message}.");
        }

        private void ErrorNothingExtracted(string archive, string target)
        {
            Console.Error.WriteLine($"Error occured trying to extract archive. Nothing was extracted from '{archive}' to '{target}.'");
        }

        /// <summary>
        /// This method is used to set the execute permissions bit for the extracted files.
        /// </summary>
        /// <remarks>
        /// In the method below we are adding the bit specifically for Node and Dotnet package in linux, as they are causing the issue.
        /// This is only set for the BuildXL.Internal repo build and is not expected to kick in for end user builds.
        /// It is expected to be shortlived and probably generalized to setting the execute permission for all extractor output.
        /// TODO: Need to remove this hack once the bug is fixed. Refer bug https://dev.azure.com/mseng/1ES/_workitems/edit/2073919 for further information.
        /// </remarks>
        private bool SetExecutePermissionsForExtractedFiles(string target, string relativePath)
        {
            string fullPathForExecutableFile = Path.Combine(target, relativePath);

            if (File.Exists(fullPathForExecutableFile))
            {
                _ = FileUtilities.SetExecutePermissionIfNeeded(fullPathForExecutableFile);
            }

            return true;
        }
    }
}

