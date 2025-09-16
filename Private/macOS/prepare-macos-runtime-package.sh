#!/bin/bash
# Prepares the BuildXL macOS native library into the nuget package template
# After running this script, call nuget pack on the output directory to create the nuget package

# 'Microsoft.BuildXL' prefix used here because this library does not have any microsoft internal dependencies
# so a separate internal version is not needed. 
readonly PKG_BASE_NAME=Microsoft.BuildXL.Interop.Runtime.osx-x64
readonly INTEROP_DYLIB_NAME=libBuildXLInterop.dylib

arg_packageVersion=""
arg_interopBuildDirectory=""
arg_outputDirectory=""

parseArgs() {
    while [[ $# -gt 0 ]]; do
        cmd="$1"
        case $cmd in
        --packageVersion)
            arg_packageVersion="$2"
            shift
            ;;
        --interopBuildDirectory)
            arg_interopBuildDirectory="$2"
            shift
            ;;
        --outputDirectory)
            arg_outputDirectory="$2"
            shift
            ;;
        *)
            shift
            ;;
        esac
    done
}

parseArgs $@

if [[ "$arg_packageVersion" == "" || "$arg_interopBuildDirectory"  == "" || "$arg_outputDirectory"  == "" ]]; then
    echo "Usage: $0 --packageVersion <version> --interopBuildDirectory <path> --outputDirectory <path>"
    exit 1
fi

# Create the output directory (and any intermediate directories) if it doesn't already exist (-p flag will not error if it already exists)
mkdir -p $arg_outputDirectory

# Clean output directory if it already contains files
rm -rf $arg_outputDirectory/*

# Create directory structure
mkdir -p $arg_outputDirectory/runtimes/osx-x64/native

# Create an empty csproj so that dotnet pack can be called on this nuget package
mkdir -p $arg_interopBuildDirectory/dotnet
rm -rf $arg_interopBuildDirectory/dotnet/*

# Write an empty csproj and call dotnet restore on it
tee $arg_interopBuildDirectory/dotnet/empty.csproj <<EOF
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net9.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
    </PropertyGroup>
</Project>
EOF

dotnet restore $arg_interopBuildDirectory/dotnet/empty.csproj

# Copy the interop dylib to the output directory
cp $arg_interopBuildDirectory/Build/Products/release/$INTEROP_DYLIB_NAME $arg_outputDirectory/runtimes/osx-x64/native/$INTEROP_DYLIB_NAME

# Write nuspec file
tee $arg_outputDirectory/$PKG_BASE_NAME.nuspec <<EOF
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/01/nuspec.xsd">
  <metadata minClientVersion="2.12">
    <id>$PKG_BASE_NAME</id>
    <version>$arg_packageVersion</version>
    <title>$PKG_BASE_NAME</title>
    <authors>Microsoft</authors>
    <owners>microsoft,buildxl,bxl</owners>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <description>The BuildXL interop runtime library for macOS. The package contains release binaries.</description>
    <copyright>© Microsoft Corporation. All rights reserved.</copyright>
    <serviceable>true</serviceable>
  </metadata>
</package>
EOF

# Call dotnet pack on the empty csproj to create the nuget package
# Adding the --no-build flag here because there's nothing to build and dotnet will fail if we don't have it
dotnet pack $arg_interopBuildDirectory/dotnet/empty.csproj --no-build -p:NuspecFile=$arg_outputDirectory/$PKG_BASE_NAME.nuspec -p:PackageOutputPath=$arg_outputDirectory