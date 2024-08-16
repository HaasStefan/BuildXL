// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using BuildXL.Cache.ContentStore.Service;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using ContentStoreTest.Extensions;
using ContentStoreTest.Stores;
using Xunit;
using BuildXL.Utilities;

#pragma warning disable SA1402 // File may only contain a single class
#pragma warning disable IDE0040 // Accessibility modifiers required

namespace ContentStoreTest.Sessions
{
    [Trait("Category", "Integration1")]
    [Trait("Category", "QTestSkip")]
    public class InProcessServiceClientContentSessionTests : ServiceClientContentSessionTests
    {
        public InProcessServiceClientContentSessionTests()
            : base(nameof(InProcessServiceClientContentSessionTests))
        {
        }

        public bool LocalOnlyClient { get; set; }

        public static IDisposable CreateBackingContentStore(out IContentStore backingContentStore, bool localOnlyClient = false)
        {
            var tests = new InProcessServiceClientContentSessionTests()
            {
                LocalOnlyClient = localOnlyClient
            };

            var directory = new DisposableDirectory(tests.FileSystem);

            backingContentStore = tests.CreateStore(directory, tests.CreateStoreConfiguration());

            return Disposable.Create(tests, directory);
        }

        protected override IContentStore CreateStore(DisposableDirectory testDirectory, ContentStoreConfiguration configuration)
        {
            var rootPath = testDirectory.Path;
            configuration.Write(FileSystem, rootPath);
            
            var grpcPortFileName = Guid.NewGuid().ToString();

            var serviceConfiguration = new ServiceConfiguration(
                new Dictionary<string, AbsolutePath> { { CacheName, rootPath } },
                rootPath,
                GracefulShutdownSeconds,
                PortExtensions.GetNextAvailablePort(),
                grpcPortFileName);

            return new TestInProcessServiceClientContentStore(
                FileSystem,
                Logger,
                CacheName,
                Scenario,
                null,
                serviceConfiguration,
                localOnly: LocalOnlyClient
                );
        }

        protected override IStartupShutdown CreateServer(ServiceConfiguration serviceConfiguration)
        {
            return new LocalContentServer(
                Logger,
                FileSystem,
                grpcHost: null,
                Scenario,
                path =>
                    new FileSystemContentStore(FileSystem, SystemClock.Instance, path),
                TestConfigurationHelper.CreateLocalContentServerConfiguration(serviceConfiguration));
        }
    }
}
