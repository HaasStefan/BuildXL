import {Artifact, Cmd, Transformer} from "Sdk.Transformers";
import {DotNetCoreVersion, Assembly, Factory} from "Sdk.Managed.Shared";

namespace Helpers {
    export declare const qualifier : {};

    const assemblyNamesToExclude = Set.create(
        a`System.IO.FileSystem.AccessControl`,
        a`System.Security.AccessControl`,
        a`System.Security.Principal.Windows`
    );

    // We skip deploying those files from the .NET Core package as we need those very assemblies from their dedicated package
    // to compile our platform abstraction layer, which depends on datatypes present only in the dedicated packages
    const assembliesToExclude = Set.create(...assemblyNamesToExclude.toArray().map(f => a`${f}.dll`));

    @@public
    export function ignoredAssemblyFile(file: File): boolean {
        return assembliesToExclude.contains(file.name);
    }

    @@public
    export function ignoredAssembly(assembly: Assembly): boolean {
        return ignoreAssemblyFileName(assembly.name);
    }

    @@public
    export function ignoreAssemblyFileName(file: PathAtom): boolean {
        return assemblyNamesToExclude.contains(file);
    }

    @@public
    export function createDefaultAssemblies(netCoreRuntimeContents: StaticDirectory, targetFramework: string, includeAllAssemblies: boolean) : Assembly[] {
        const netcoreAppPackageContents = netCoreRuntimeContents.contents;
        const dlls = netcoreAppPackageContents.filter(file => file.hasExtension && file.extension === a`.dll`);
        return dlls
            .map(file  => Factory.createAssembly(netCoreRuntimeContents, file, targetFramework, [], true))
            .filter(a => includeAllAssemblies || !ignoreAssemblyFileName(a.name));
    }

    @@public
    export function macOSRuntimeExtensions(file: File): boolean {
        return file.extension === a`.dylib` || file.extension === a`.a` || file.extension === a`.dll`;
    }
    
    @@public
    export function linuxRuntimeExtensions(file: File): boolean {
        return file.extension === a`.so` || file.extension === a`.o` || file.extension === a`.dll`;
    }

    @@public
    export function getDotNetCoreToolTemplate(version: DotNetCoreVersion) : Transformer.ExecuteArgumentsComposible {
        const host = Context.getCurrentHost();
        
        Contract.assert(host.cpuArchitecture === "x64", "The current DotNetCore Runtime package only has x64 version of Node. Ensure this runs on a 64-bit OS -or- update PowerShell.Core package to have other architectures embedded and fix this logic");

        const executable = host.os === 'win' ? r`dotnet.exe` : r`dotnet`;
        const pkgContents  = getRuntimePackagesContent(version, host);

        return {
            tool: {
                exe: pkgContents.assertExistence(executable), 
                dependsOnCurrentHostOSDirectories: true
            },
            dependencies: [
                pkgContents
            ],
            environmentVariables: [
                // Make sure DotNet core runs isolated from the framework your build selected and doesn't go off reading registry and dependd on globally installed tools to make the build unreliable
                // https://docs.microsoft.com/en-us/dotnet/core/tools/dotnet?tabs=netcore21
                { name: "DOTNET_MULTILEVEL_LOOKUP", value: "0" }, 

                // Speed up dotnet core by preventing it from doing all kinds of startup logic like pulling packages.
                // https://docs.microsoft.com/en-us/dotnet/core/tools/global-tools
                { name: "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", value: "1" } 
            ]
        };
    }

    function getRuntimePackagesContent(version: DotNetCoreVersion, host: Context.CurrentHostInformation) : StaticDirectory {
        if (version === 'net6.0')
        {
            switch (host.os) {
                case "win":
                    return importFrom("DotNet-Runtime-6.win-x64").extracted;
                case "macOS":
                    return importFrom("DotNet-Runtime-6.osx-x64").extracted;
                case "unix":
                    return importFrom("DotNet-Runtime-6.linux-x64").extracted;
                default:
                    Contract.fail(`The current DotNetCore Runtime package doesn't support the current target runtime: ${host.os}. Ensure you run on a supported OS -or- update the DotNet-Runtime package to have the version embdded.`);
            }
        }
        else if (version === 'net7.0')
        {
            switch (host.os) {
                case "win":
                    return importFrom("DotNet-Runtime-7.win-x64").extracted;
                case "macOS":
                    return importFrom("DotNet-Runtime-7.osx-x64").extracted;
                case "unix":
                    return importFrom("DotNet-Runtime-7.linux-x64").extracted;
                default:
                    Contract.fail(`The current DotNetCore Runtime package doesn't support the current target runtime: ${host.os}. Ensure you run on a supported OS -or- update the DotNet-Runtime package to have the version embdded.`);
            }
        }
        
        Contract.fail(`Unsupport .NET Core version ${version}.`);
    }

    @@public
    export function getDotNetToolTemplate(version: DotNetCoreVersion) : Transformer.ExecuteArgumentsComposible {
        return getDotNetCoreToolTemplate(version);
    }

    const tool6Template = getDotNetCoreToolTemplate("net6.0");
    const tool7Template = getDotNetCoreToolTemplate("net7.0");

    function getCachedDotNetCoreToolTemplate(dotNetCoreVersion: DotNetCoreVersion) {
        switch (dotNetCoreVersion) {
            case "net6.0": return tool6Template;
            case "net7.0": return tool7Template;
            default: Contract.fail(`Unknown .NET Core version '${dotNetCoreVersion}'.`);
        }
    }

    @@public
    export function wrapInDotNetExeForCurrentOs(dotNetCoreVersion: DotNetCoreVersion, args: Transformer.ExecuteArguments) : Transformer.ExecuteArguments {
        return Object.merge<Transformer.ExecuteArguments>(
            args,
            getCachedDotNetCoreToolTemplate(dotNetCoreVersion),
            {
                arguments: [
                    Cmd.argument(Artifact.input(args.tool.exe))
                ].prependWhenMerged()
            });
    }
}