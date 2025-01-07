// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Linq;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Core;
using TypeScript.Net.DScript;
using TypeScript.Net.Extensions;
using TypeScript.Net.Types;
using static BuildXL.FrontEnd.Nuget.SyntaxFactoryEx;
using static TypeScript.Net.DScript.SyntaxFactory;

namespace BuildXL.FrontEnd.Nuget
{
    /// <summary>
    ///     Helper to generate DScript specs for nuget packages
    /// </summary>
    public sealed class NugetSpecGenerator
    {
        private readonly PathTable m_pathTable;
        private readonly PackageOnDisk m_packageOnDisk;
        private readonly NugetAnalyzedPackage m_analyzedPackage;
        private readonly IReadOnlyDictionary<string, string> m_repositories;
        private readonly NugetFrameworkMonikers m_nugetFrameworkMonikers;
        private readonly AbsolutePath m_sourceDirectory;
        private readonly PathAtom m_xmlExtension;
        private readonly PathAtom m_pdbExtension;
        private readonly int? m_timeoutInMinutes;

        private readonly IEsrpSignConfiguration m_esrpSignConfiguration;

        /// <summary>Current spec generation format version</summary>
        public const int SpecGenerationFormatVersion = 23;

        private readonly NugetRelativePathComparer m_nugetRelativePathComparer;

        /// <nodoc />
        public NugetSpecGenerator(
            PathTable pathTable, 
            NugetAnalyzedPackage analyzedPackage,
            INugetResolverSettings nugetResolverSettings,
            AbsolutePath sourceDirectory)
        {
            m_pathTable = pathTable;
            m_analyzedPackage = analyzedPackage;
            m_repositories = nugetResolverSettings.Repositories;
            m_packageOnDisk = analyzedPackage.PackageOnDisk;
            m_nugetFrameworkMonikers = new NugetFrameworkMonikers(pathTable.StringTable, nugetResolverSettings);
            m_sourceDirectory = sourceDirectory;
            m_timeoutInMinutes = nugetResolverSettings.Configuration?.DownloadTimeoutMin;

            m_xmlExtension = PathAtom.Create(pathTable.StringTable, ".xml");
            m_pdbExtension = PathAtom.Create(pathTable.StringTable, ".pdb");
            m_esrpSignConfiguration = nugetResolverSettings.EsrpSignConfiguration;

            m_nugetRelativePathComparer = new NugetRelativePathComparer(pathTable.StringTable);
        }

        /// <summary>
        /// Generates a DScript spec for a given <paramref name="analyzedPackage"/>.
        /// </summary>
        /// <remarks>
        /// The generated format is:
        /// [optional] import of managed sdk core
        /// [optional] qualifier declaration
        /// @@public
        /// export const contents: StaticDirectory = NuGetDownloader.downloadPackage(
        ///    {
        ///     id: "package ID",
        ///     version: "X.XX",
        ///     ...
        ///    }
        /// @@public
        /// export const pkg: NugetPackage = {contents ...};
        /// </remarks>
        [SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly")]
        public ISourceFile CreateScriptSourceFile(NugetAnalyzedPackage analyzedPackage)
        {
            var sourceFileBuilder = new SourceFileBuilder();

            // 0. Import * as NugetDownloader from "BuildXL.Tools.NugetDownloader" to be able to download NuGet packages
            sourceFileBuilder.Statement(ImportDeclaration("NugetDownloader", "BuildXL.Tools.NugetDownloader"));

            // 1. Optional import of managed sdk.
            if (analyzedPackage.IsManagedPackage)
            {
                // import * as Managed from 'Sdk.Managed';
                sourceFileBuilder
                    .Statement(ImportDeclaration(alias: "Managed", moduleName: "Sdk.Managed"))
                    .SemicolonAndBlankLine();
            }

            // 2. Optional qualifier
            if (TryCreateQualifier(analyzedPackage, out var qualifierStatement))
            {
                sourceFileBuilder
                    .Statement(qualifierStatement)
                    .SemicolonAndBlankLine();
            }

            // Create a seal directory declaration with all the package content
            sourceFileBuilder
                .Statement(CreatePackageContents())
                .SemicolonAndBlankLine();

            // @@public export const pkg = ...
            sourceFileBuilder.Statement(CreatePackageVariableDeclaration(analyzedPackage));

            return sourceFileBuilder.Build();
        }

        /// <nodoc />
        public ISourceFile CreatePackageConfig()
        {
            var packageId = string.IsNullOrEmpty(m_packageOnDisk.Package.Alias)
                ? m_packageOnDisk.Package.Id
                : m_packageOnDisk.Package.Alias;

            return new ModuleConfigurationBuilder()
                .Name(packageId)
                .Version(m_packageOnDisk.Package.Version)
                // The generated module is V2 module.
                .NameResolution(implicitNameResolution: true)
                .Build();
        }

        private List<ICaseClause> CreateSwitchCasesForTargetFrameworks(NugetAnalyzedPackage analyzedPackage)
        {
            var cases = new List<ICaseClause>();
            Contract.Assert(analyzedPackage.TargetFrameworks.Count != 0, "Managed package must have at least one target framework.");

            var valid = analyzedPackage.TargetFrameworks.Exists(moniker => m_nugetFrameworkMonikers.FullFrameworkVersionHistory.Contains(moniker) || m_nugetFrameworkMonikers.NetCoreVersionHistory.Contains(moniker));
            Contract.Assert(valid, "Target framework monikers must exsist and be registered with internal target framework version helpers.");

            var allFullFrameworkDeps = m_nugetFrameworkMonikers.FullFrameworkVersionHistory
                .SelectMany(m =>
                    analyzedPackage.DependenciesPerFramework.TryGetValue(m, out IReadOnlyList<INugetPackage> dependencySpecificFrameworks)
                        ? dependencySpecificFrameworks
                        : new List<INugetPackage>())
                .GroupBy(pkg => pkg.Id)
                .Select(grp => grp.OrderBy(pkg => pkg.Version).Last());

            foreach (var versionHistory in new List<PathAtom>[] { m_nugetFrameworkMonikers.FullFrameworkVersionHistory, m_nugetFrameworkMonikers.NetCoreVersionHistory })
            {
                FindAllCompatibleFrameworkMonikers(analyzedPackage, (List<PathAtom> monikers) =>
                {
                    if (monikers.Count == 0)
                    {
                        return;
                    }

                    if (analyzedPackage.NeedsCompatibleFullFrameworkSupport &&
                        // Let's add the full framework compatible cases if we are in the netstandard case only
                        !m_nugetFrameworkMonikers.NetCoreAppVersionHistory.Contains(monikers.First())
                      )
                    {
                        cases.AddRange(m_nugetFrameworkMonikers.NetStandardToFullFrameworkCompatibility.Select(m => new CaseClause(new LiteralExpression(m.ToString(m_pathTable.StringTable)))));
                    }

                    cases.AddRange(monikers.Take(monikers.Count - 1).Select(m => new CaseClause(new LiteralExpression(m.ToString(m_pathTable.StringTable)))));

                    var compile = new List<IExpression>();
                    var runtime = new List<IExpression>();
                    var dependencies = new List<IExpression>();

                    // Compile items
                    if (TryGetValueForFrameworkAndFallbacks(analyzedPackage.References, new NugetTargetFramework(monikers.First()), out IReadOnlyList<RelativePath> refAssemblies))
                    {
                        compile.AddRange(refAssemblies.Select(r => CreateSimpleBinary(r)));
                    }

                    // Runtime items
                    if (TryGetValueForFrameworkAndFallbacks(analyzedPackage.Libraries, new NugetTargetFramework(monikers.First()), out IReadOnlyList<RelativePath> libAssemblies))
                    {
                        runtime.AddRange(libAssemblies.Select(l => CreateSimpleBinary(l)));
                    }

                    // For full framework dependencies we unconditionally include all the distinct dependencies from the nuspec file,
                    // .NETStandard dependencies are only included if the moniker and the parsed target framework match!
                    if (m_nugetFrameworkMonikers.IsFullFrameworkMoniker(monikers.First()))
                    {
                        dependencies.AddRange(allFullFrameworkDeps.Select(dep => CreateImportFromForDependency(dep)));
                    }
                    else
                    {
                        if (analyzedPackage.DependenciesPerFramework.TryGetValue(monikers.First(), out IReadOnlyList<INugetPackage> dependencySpecificFrameworks))
                        {
                            dependencies.AddRange(dependencySpecificFrameworks.Select(dep => CreateImportFromForDependency(dep)));
                        }
                    }

                    cases.Add(
                        new CaseClause(
                            new LiteralExpression(monikers.Last().ToString(m_pathTable.StringTable)),
                            new ReturnStatement(
                                Call(
                                    PropertyAccess("Managed", "Factory", "createNugetPackage"),
                                    new LiteralExpression(analyzedPackage.Id),
                                    new LiteralExpression(analyzedPackage.Version),
                                    PropertyAccess("Contents", "all"),
                                    Array(compile),
                                    Array(runtime),
                                    m_nugetFrameworkMonikers.IsFullFrameworkMoniker(monikers.Last())
                                        ? Array(dependencies)
                                        // For a non-full framework moniker range, add the dependencies specified.
                                        // Observe the dependency is specified in the nupkg for a particular version, e.g. <group targetFramework=X>, but the 
                                        // semantics is 'X and all versions compatible with X are supported'. In this case 'X' should be the first moniker in the range
                                        // so we could unconditionally add all dependencies. But we might have added all the full framework monikers as well, so we need to
                                        // filter on the specific moniker range we currently have.
                                        : Array(new CallExpression(new Identifier("...addIfLazy"),
                                            monikers.Skip(1).Aggregate<PathAtom, IExpression>(
                                                QualifierEqualsMonikerExpression(monikers.First()),
                                                (expression, moniker) => new BinaryExpression(
                                                    expression,
                                                    SyntaxKind.BarBarToken,
                                                    QualifierEqualsMonikerExpression(moniker))
                                            ),
                                            new ArrowFunction(
                                                CollectionUtilities.EmptyArray<IParameterDeclaration>(),
                                                Array(dependencies)
                                            )
                                        ))
                                )
                            )
                        )
                    );
                }, versionHistory);
            }

            return cases;
        }

        /// <summary>
        /// Generates the expression 'qualifier.targetFramework === moniker'
        /// </summary>
        private BinaryExpression QualifierEqualsMonikerExpression(PathAtom moniker)
        {
            return new BinaryExpression(
                new PropertyAccessExpression("qualifier", "targetFramework"),
                SyntaxKind.EqualsEqualsEqualsToken,
                new LiteralExpression(moniker.ToString(m_pathTable.StringTable)));
        }

        private bool TryGetValueForFrameworkAndFallbacks<TValue>(
            IReadOnlyDictionary<NugetTargetFramework, TValue> map,
            NugetTargetFramework framework,
            out TValue value)
        {
            if (map.TryGetValue(framework, out value))
            {
                return true;
            }

            return false;
        }


        private IStatement CreatePackageVariableDeclaration(NugetAnalyzedPackage package)
        {
            IExpression pkgExpression;
            TypeReferenceNode pkgType;
            if (package.IsManagedPackage)
            {
                // If the package is managed, it is a 'ManagedNugetPackage' and we create a switch based on the current qualifie
                // that defines contents, compile, runtime and dependency items
                pkgType = new TypeReferenceNode("Managed", "ManagedNugetPackage");

                // Computes the switch cases, based on the target framework
                List<ICaseClause> cases = CreateSwitchCasesForTargetFrameworks(package);

                pkgExpression = new CallExpression(
                    new ParenthesizedExpression(
                        new ArrowFunction(
                            CollectionUtilities.EmptyArray<IParameterDeclaration>(),
                            new SwitchStatement(
                                PropertyAccess("qualifier", "targetFramework"),
                                new DefaultClause(
                                    new ExpressionStatement(
                                        new CallExpression(
                                            PropertyAccess("Contract", "fail"),
                                            new LiteralExpression("Unsupported target framework")))),
                                cases))));
            }
            else
            {
                // If the package is not managed, it is a 'NugetPackage' with just contents and dependencies
                pkgType = new TypeReferenceNode("NugetPackage");
                pkgExpression = ObjectLiteral(
                    (name: "contents", PropertyAccess("Contents", "all")),
                    (name: "dependencies", Array(package.Dependencies.Select(CreateImportFromForDependency).ToArray())),
                    (name: "version", new LiteralExpression(package.Version)));
            }

            return
                new VariableDeclarationBuilder()
                    .Name("pkg")
                    .Visibility(Visibility.Public)
                    .Initializer(pkgExpression)
                    .Type(pkgType)
                    .Build();
        }

        private IStatement CreatePackageContents()
        {
            // Arguments for calling the nuget downloader SDK
            var downloadCallArgs = new List<(string, IExpression expression)>(4) 
            {
                    ("id", new LiteralExpression(m_analyzedPackage.Id)),
                    ("version", new LiteralExpression(m_analyzedPackage.Version)),
                    ("extractedFiles", new ArrayLiteralExpression(m_analyzedPackage.PackageOnDisk.Contents
                        .Filter(relativePath => !m_analyzedPackage.FilesToExclude.Contains(relativePath))
                        .OrderBy(relativePath => relativePath, m_nugetRelativePathComparer)
                        .Select(relativePath => PathLikeLiteral(InterpolationKind.RelativePathInterpolation, relativePath.ToString(m_pathTable.StringTable, PathFormat.Script))))),
                    ("repositories", new ArrayLiteralExpression(m_repositories
                        // TODO: Fix this potential non-determinism.
                        //
                        //       There is a chance that the generated spec is non-deterministic because of the order of the repositories in a dictionary is not guaranteed.
                        //       The problem is the set of repositories is obtained from the configuration, config.dsc, which essentially is represented as JSON object, where
                        //       the order of kvp should not be assumed. Unfortunately, the author of config.dsc seems to want to assume the order as they are written.
                        //
                        //       For example, config.dsc specifies this:
                        //       {
                        //           "buildxl-selfhost" : "https://pkgs.dev.azure.com/ms/BuildXL/_packaging/BuildXL.Selfhost/nuget/v3/index.json",
                        //           "nuget.org" : "https://api.nuget.org/v3/index.json",
                        //           "dotnet-arcade" : "https://dotnetfeed.blob.core.windows.net/dotnet-core/index.json",
                        //       },
                        //       and the author wants to use the feeds in order. However, if we sort the feeds when we generate package.dsc for downloading the NuGet package,
                        //       then "dotnet-arcade" will come before "nuget.org" in the generated ArrayLiteral, so BuildXL will use "dotnet-arcade" to download the package if the
                        //       package does not exist in "buildxl-selfhost". The NuGet resolver itself can use "nuget.org" when generating package.dsc. The issue is, the file table
                        //       obtained from "nuget.org" may be different from the one from "dotnet-arcade". For example, the file table in "nuget.org" mentions ".signature.7ps" file in the package,
                        //       but the package downloaded from "dotnet-arcade" does not have it, so we get missing output when we run the NuGet downloader pip.
                        //
                        //       The proper way of fixing this issue is to represent repositories as an array of (name, uri) so that the order is explicit, and one doesn't need to call OrderBy below.
                        // .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                        .Select(kvp => new ArrayLiteralExpression(new LiteralExpression(kvp.Key), new LiteralExpression(kvp.Value)))))
            };

            // If a credential provider was used to inspect the package, pass it as an argument to be able to retrieve it.
            if (m_analyzedPackage.CredentialProviderPath.IsValid)
            {
                // If the credential provider is within the source tree, express it in terms of a mount, so the generated
                // spec is more resilient to cache hits across machines
                IExpression path;
                if (m_sourceDirectory.TryGetRelative(m_pathTable, m_analyzedPackage.CredentialProviderPath, out var relativeCredentialProviderPath))
                {
                    path = PathLikeLiteral(
                        InterpolationKind.FileInterpolation, 
                        new PropertyAccessExpression(new CallExpression(new PropertyAccessExpression("Context", "getMount"), new LiteralExpression("SourceRoot")), "path") , 
                        "/" + relativeCredentialProviderPath.ToString(m_pathTable.StringTable, PathFormat.Script));
                }
                else
                {
                    path = PathLikeLiteral(InterpolationKind.FileInterpolation, m_analyzedPackage.CredentialProviderPath.ToString(m_pathTable, PathFormat.Script));
                }
                
                downloadCallArgs.Add(("credentialProviderPath", path));
            }

            if (m_timeoutInMinutes != null)
            {
                downloadCallArgs.Add(("timeoutInMinutes", new LiteralExpression(m_timeoutInMinutes.Value)));
            }

            if (m_esrpSignConfiguration != null)
            {
                var esrpSignArgs = new List<(string, IExpression expression)>
                {
                    ("signToolPath", PathLikeLiteral(InterpolationKind.PathInterpolation, m_esrpSignConfiguration.SignToolPath.ToString(m_pathTable, PathFormat.Script))),
                    ("signToolConfiguration", PathLikeLiteral(InterpolationKind.PathInterpolation, m_esrpSignConfiguration.SignToolConfiguration.ToString(m_pathTable, PathFormat.Script))),
                    ("signToolEsrpPolicy", PathLikeLiteral(InterpolationKind.PathInterpolation, m_esrpSignConfiguration.SignToolEsrpPolicy.ToString(m_pathTable, PathFormat.Script))),
                    ("signToolAadAuth", PathLikeLiteral(InterpolationKind.PathInterpolation, m_esrpSignConfiguration.SignToolAadAuth.ToString(m_pathTable, PathFormat.Script))),
                };

                downloadCallArgs.Add(("esrpSignConfiguration", ObjectLiteral(esrpSignArgs.ToArray())));
            }

            if (m_analyzedPackage.FilesToExclude.Any())
            {
                downloadCallArgs.Add(("excludedFiles", new ArrayLiteralExpression(m_analyzedPackage.FilesToExclude
                    .OrderBy(relativePath => relativePath, m_nugetRelativePathComparer)
                    .Select(relativePath => PathLikeLiteral(InterpolationKind.RelativePathInterpolation, relativePath.ToString(m_pathTable.StringTable, PathFormat.Script))))));
            }

            return new ModuleDeclaration(
                "Contents",

                Qualifier(new TypeLiteralNode()),
                
                new VariableDeclarationBuilder()
                    .Name("all")
                    .Visibility(Visibility.Public)
                    .Type(new TypeReferenceNode("StaticDirectory"))
                    .Initializer(
                                new CallExpression(
                                        new PropertyAccessExpression("NugetDownloader", "downloadPackage"),
                                        ObjectLiteral(downloadCallArgs.ToArray())))
                    .Build()
            );
        }

        internal static void FindAllCompatibleFrameworkMonikers(NugetAnalyzedPackage analyzedPackage, Action<List<PathAtom>> callback, params List<PathAtom>[] tfmHistory)
        {
            if (analyzedPackage.TargetFrameworks.Count > 0)
            {
                foreach (var versionHistory in tfmHistory)
                {
                    var indices = analyzedPackage.TargetFrameworks
                        .Select(moniker => versionHistory.IndexOf(moniker))
                        .Where(idx => idx != -1)
                        .OrderBy(x => x).ToList();

                    if (indices.IsNullOrEmpty())
                    {
                        continue;
                    }

                    for (int i = 0; i < indices.Count; i++)
                    {
                        int start = indices[i];
                        int count = (i + 1) > indices.Count - 1 ? versionHistory.Count - start : (indices[i + 1] - indices[i]);

                        callback(versionHistory.GetRange(start, count));
                    }
                }
            }
            else
            {
                callback(default(List<PathAtom>));
            }
        }

        private bool TryCreateQualifier(NugetAnalyzedPackage analyzedPackage, out IStatement statement)
        {
            List<PathAtom> compatibleTfms = new List<PathAtom>();

            if (analyzedPackage.NeedsCompatibleFullFrameworkSupport)
            {
                compatibleTfms.AddRange(m_nugetFrameworkMonikers.NetStandardToFullFrameworkCompatibility);
            }

            FindAllCompatibleFrameworkMonikers(analyzedPackage,
                (List<PathAtom> monikers) => compatibleTfms.AddRange(monikers),
                m_nugetFrameworkMonikers.FullFrameworkVersionHistory,
                m_nugetFrameworkMonikers.NetCoreVersionHistory);

            if (compatibleTfms.Count > 0)
            {
                // { targetFramework: 'tf1' | 'tf2' | ... }
                var qualifierType = UnionType(
                    (propertyName: "targetFramework", literalTypes: compatibleTfms.Select(m => m.ToString(m_pathTable.StringTable))),
                    (propertyName: "targetRuntime", literalTypes: m_nugetFrameworkMonikers.SupportedTargetRuntimes)
                );
                statement = Qualifier(qualifierType);
                return true;
            }

            statement = null;
            return false;
        }

        private static IPropertyAccessExpression CreateImportFromForDependency(INugetPackage dependency)
        {
            // TODO: This is a terrible hack but we have same-named incompatible modules from the cache, this was the only workaround to get it working
            string importName = string.IsNullOrEmpty(dependency.Alias) || (dependency.Id == "BuildXL.Cache.ContentStore.Interfaces")
                ? dependency.Id
                : dependency.Alias;

            // importFrom('moduleName').pkg
            return PropertyAccess(
                // TODO: Support multiple SxS versions, so this dependency would be the direct dependency.
                ImportFrom(importName),
                "pkg");
        }

        private IExpression GetFileExpressionForPath(RelativePath relativePath)
        {
            // all.assertExistence(r`relativePath`)
            return new CallExpression(new PropertyAccessExpression("Contents", "all", "getFile"), PathLikeLiteral(
                InterpolationKind.RelativePathInterpolation,
                relativePath.ToString(m_pathTable.StringTable, PathFormat.Script)));
        }

        private IExpression CreateSimpleBinary(RelativePath binaryFile)
        {
            var pdbPath = binaryFile.ChangeExtension(m_pathTable.StringTable, m_pdbExtension);
            var xmlPath = binaryFile.ChangeExtension(m_pathTable.StringTable, m_xmlExtension);

            return Call(
                PropertyAccess("Managed", "Factory", "createBinaryFromFiles"),
                GetFileExpressionForPath(binaryFile),
                m_packageOnDisk.Contents.Contains(pdbPath) ? GetFileExpressionForPath(pdbPath) : null,
                m_packageOnDisk.Contents.Contains(xmlPath) ? GetFileExpressionForPath(xmlPath) : null);
        }
    }
}
