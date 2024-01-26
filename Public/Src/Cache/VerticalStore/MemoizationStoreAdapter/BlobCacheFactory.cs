// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.Blob;
using BuildXL.Cache.ContentStore.Interfaces.Auth;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.Interfaces;
using BuildXL.Cache.MemoizationStore.Distributed.Stores;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Core;
using AbsolutePath = BuildXL.Cache.ContentStore.Interfaces.FileSystem.AbsolutePath;

namespace BuildXL.Cache.MemoizationStoreAdapter
{
    /// <summary>
    /// Cache Factory for BuildXL as a cache user for dev machines using Azure blob
    /// </summary>
    /// <remarks>
    /// This is the class responsible for creating the BuildXL to Cache adapter in the Selfhost builds. It configures
    /// the cache on the BuildXL process.
    ///
    /// Current limitations while we flesh things out:
    /// 1) APIs around tracking named sessions are not implemented
    /// </remarks>
    public class BlobCacheFactory : ICacheFactory
    {
        /// <summary>
        /// Inheritable configuration settings for cache factories that wish to configure a connection to a blob cache
        /// </summary>
        public abstract class BlobCacheConfig
        {
            /// <summary>
            /// Authenticate by using a single or an array of connection strings inside of an environment variable.
            /// </summary>
            /// <remarks>
            /// This is not a good authentication method because in many cases environment variables are logged and not
            /// encrypted.
            ///
            /// The preferred authentication method is to use a managed identity (<see cref="StorageAccountEndpoint"/>
            /// and <see cref="ManagedIdentityId"/>). However, this is unsupported for sharded scenarios and isn't
            /// available outside of Azure. Use <see cref="ConnectionStringFileEnvironmentVariableName"/> if that's
            /// your use-case.
            /// </remarks>
            [DefaultValue("BlobCacheFactoryConnectionString")]
            public string ConnectionStringEnvironmentVariableName { get; set; }

            /// <summary>
            /// Authenticate by using a file that contains a single or an array of connection strings.
            /// </summary>
            /// <remarks>
            /// The preferred authentication method is to use a managed identity (<see cref="StorageAccountEndpoint"/>
            /// and <see cref="ManagedIdentityId"/>). However, this is unsupported for sharded scenarios and isn't
            /// available outside of Azure. Use <see cref="ConnectionStringFileEnvironmentVariableName"/> if that's
            /// your use-case.
            /// </remarks>
            [DefaultValue("BlobCacheFactoryConnectionStringFile")]
            public string ConnectionStringFileEnvironmentVariableName { get; set; }

            /// <summary>
            /// Whether the connection string file should be considered to be DPAPI encrypted.
            /// </summary>
            [DefaultValue(true)]
            public bool ConnectionStringFileDataProtectionEncrypted { get; set; } = true;

            /// <summary>
            /// URI of the storage account endpoint to be used for this cache when authenticating using managed
            /// identities (e.g: https://mystorageaccount.blob.core.windows.net).
            /// </summary>
            [DefaultValue(null)]
            public string StorageAccountEndpoint { get; set; }

            /// <summary>
            /// The client id for the managed identity that will be used to authenticate against the storage account
            /// specified in <see cref="StorageAccountEndpoint"/>.
            /// </summary>
            [DefaultValue(null)]
            public string ManagedIdentityId { get; set; }

            /// <summary>
            /// The configured number of days the storage account will retain blobs before deleting (or soft deleting)
            /// them based on last access time. If content and metadata have different retention policies, the shortest
            /// retention period is expected here.
            /// </summary>
            /// <remarks>
            /// This setting should only be used when utilizing service-less GC (i.e., GC is performed via Azure
            /// Storage's lifecycle management feature).
            /// 
            /// By setting this value to reflect the storage account life management configuration policy, pin
            /// operations can be elided if we know a fingerprint got a cache hit within the retention policy period.
            /// 
            /// When enabled (a positive value), every time that a content hash list is stored, a last upload time is
            /// associated to it and stored as well.
            /// This last upload time is deemed very close to the one used for storing all the corresponding content
            /// for that content hash (since typically that's the immediate step prior to storing the fingerprint).
            /// Whenever a content hash list is retrieved and has a last upload time associated to it, the metadata
            /// store notifies the cache of it. The cache then uses that information to determine whether the content
            /// associated to that fingerprint can be elided, based on the provided configured blob retention policy of
            /// the blob storage account.
            /// </remarks>
            [DefaultValue(-1)]
            public int RetentionPolicyInDays { get; set; } = -1;

            /// <nodoc />
            [DefaultValue("default")]
            public string Universe { get; set; }

            /// <nodoc />
            [DefaultValue("default")]
            public string Namespace { get; set; }
        }

        /// <summary>
        /// Configuration for <see cref="MemoizationStoreCacheFactory"/>.
        /// </summary>
        public sealed class Config : BlobCacheConfig
        {
            /// <summary>
            /// The Id of the cache instance
            /// </summary>
            [DefaultValue(typeof(CacheId))]
            public CacheId CacheId { get; set; }

            /// <summary>
            /// Path to the log file for the cache.
            /// </summary>
            [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
            public string CacheLogPath { get; set; }

            /// <summary>
            /// Duration to wait for exclusive access to the cache directory before timing out.
            /// </summary>
            [DefaultValue(0)]
            public uint LogFlushIntervalSeconds { get; set; }

            /// <nodoc />
            public Config()
            {
                CacheId = new CacheId("BlobCache");
            }
        }

        /// <inheritdoc />
        public async Task<Possible<ICache, Failure>> InitializeCacheAsync(
            ICacheConfigData cacheData,
            Guid activityId,
            ICacheConfiguration cacheConfiguration = null)
        {
            Contract.Requires(cacheData != null);

            var possibleCacheConfig = cacheData.Create<Config>();
            if (!possibleCacheConfig.Succeeded)
            {
                return possibleCacheConfig.Failure;
            }

            return await InitializeCacheAsync(possibleCacheConfig.Result);
        }

        /// <summary>
        /// Create cache using configuration
        /// </summary>
        public async Task<Possible<ICache, Failure>> InitializeCacheAsync(Config configuration)
        {
            Contract.Requires(configuration != null);
            if (string.IsNullOrEmpty(configuration.Universe))
            {
                configuration.Universe = "default";
            }

            if (string.IsNullOrEmpty(configuration.Namespace))
            {
                configuration.Namespace = "default";
            }

            try
            {
                var logPath = new AbsolutePath(configuration.CacheLogPath);

                // If the retention period is not set, this is not a blocker for constructing the cache, but performance can be degraded. Report it.
                var failures = new List<Failure>();

                var logger = new DisposeLogger(() => new EtwFileLog(logPath.Path, configuration.CacheId), configuration.LogFlushIntervalSeconds);
                var cache = new MemoizationStoreAdapterCache(
                    cacheId: configuration.CacheId,
                    innerCache: CreateCache(logger, configuration).Cache,
                    logger: logger,
                    statsFile: new AbsolutePath(logPath.Path + ".stats"),
                    implicitPin: ImplicitPin.None,
                    precedingStateDegradationFailures: failures);

                var startupResult = await cache.StartupAsync();
                if (!startupResult.Succeeded)
                {
                    return startupResult.Failure;
                }

                return cache;
            }
            catch (Exception e)
            {
                return new CacheConstructionFailure(configuration.CacheId, e);
            }
        }

        internal static AzureBlobStorageCacheFactory.CreateResult CreateCache(ILogger logger, BlobCacheConfig configuration)
        {
            var tracingContext = new Context(logger);
            var context = new OperationContext(tracingContext);

            context.TracingContext.Info($"Creating blob cache. Universe=[{configuration.Universe}] Namespace=[{configuration.Namespace}] RetentionPolicyInDays=[{configuration.RetentionPolicyInDays}]", nameof(EphemeralCacheFactory));

            var credentials = LoadAzureCredentials(configuration);

            var factoryConfiguration = new AzureBlobStorageCacheFactory.Configuration(
                ShardingScheme: new ShardingScheme(ShardingAlgorithm.JumpHash, credentials.Keys.ToList()),
                Universe: configuration.Universe,
                Namespace: configuration.Namespace,
                RetentionPolicyInDays: configuration.RetentionPolicyInDays <= 0 ? null : configuration.RetentionPolicyInDays);

            return AzureBlobStorageCacheFactory.Create(context, factoryConfiguration, new StaticBlobCacheSecretsProvider(credentials));
        }

        /// <nodoc />
        internal static Dictionary<BlobCacheStorageAccountName, IAzureStorageCredentials> LoadAzureCredentials(BlobCacheConfig configuration)
        {
            Dictionary<BlobCacheStorageAccountName, IAzureStorageCredentials> credentials = null;

            var connectionStringFile = Environment.GetEnvironmentVariable(configuration.ConnectionStringFileEnvironmentVariableName);
            if (!string.IsNullOrEmpty(connectionStringFile))
            {
                var encryption = configuration.ConnectionStringFileDataProtectionEncrypted
                    ? BlobCacheCredentialsHelper.FileEncryption.Dpapi
                    : BlobCacheCredentialsHelper.FileEncryption.None;
                credentials = BlobCacheCredentialsHelper.Load(new AbsolutePath(connectionStringFile), encryption);
            }

            var connectionString = Environment.GetEnvironmentVariable(configuration.ConnectionStringEnvironmentVariableName);
            if (credentials is null && !string.IsNullOrEmpty(connectionString))
            {
                credentials = BlobCacheCredentialsHelper.ParseFromEnvironmentFormat(connectionString);
            }


            if (credentials is null && configuration.ManagedIdentityId is not null && configuration.StorageAccountEndpoint is not null)
            {
                Contract.Requires(!string.IsNullOrEmpty(configuration.ManagedIdentityId));
                Contract.Requires(!string.IsNullOrEmpty(configuration.StorageAccountEndpoint));

                if (!Uri.TryCreate(configuration.StorageAccountEndpoint, UriKind.Absolute, out Uri uri))
                {
                    throw new InvalidOperationException($"'{configuration.StorageAccountEndpoint}' does not represent a valid URI.");
                }

                credentials = new Dictionary<BlobCacheStorageAccountName, IAzureStorageCredentials>();
                var credential = new ManagedIdentityAzureStorageCredentials(configuration.ManagedIdentityId, uri);
                credentials.Add(BlobCacheStorageAccountName.Parse(credential.GetAccountName()), credential);
            }

            if (credentials is null)
            {
                throw new InvalidOperationException($"Can't find credentials to authenticate against the Blob Cache. Please see documentation for the supported authentication methods and how to configure them.");
            }

            return credentials;
        }

        /// <inheritdoc />
        public IEnumerable<Failure> ValidateConfiguration(ICacheConfigData cacheData)
        {
            return CacheConfigDataValidator.ValidateConfiguration<Config>(cacheData, cacheConfig =>
            {
                var failures = new List<Failure>();
                failures.AddFailureIfNullOrWhitespace(cacheConfig.CacheLogPath, nameof(cacheConfig.CacheLogPath));
                failures.AddFailureIfNullOrWhitespace(cacheConfig.CacheId, nameof(cacheConfig.CacheId));
                failures.AddFailureIfNullOrWhitespace(cacheConfig.ConnectionStringEnvironmentVariableName, nameof(cacheConfig.ConnectionStringEnvironmentVariableName));
                failures.AddFailureIfNullOrWhitespace(cacheConfig.ConnectionStringFileEnvironmentVariableName, nameof(cacheConfig.ConnectionStringFileEnvironmentVariableName));
                failures.AddFailureIfNullOrWhitespace(cacheConfig.Universe, nameof(cacheConfig.Universe));
                failures.AddFailureIfNullOrWhitespace(cacheConfig.Namespace, nameof(cacheConfig.Namespace));
                return failures;
            });
        }
    }
}
