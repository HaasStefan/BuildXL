// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using BuildXL.Cache.ContentStore.Grpc;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.Host.Configuration;
using static BuildXL.Utilities.ConfigurationHelper;

namespace BuildXL.Cache.ContentStore.Service.Grpc
{
    /// <summary>
    /// Configuration class for <see cref="GrpcCopyClient"/>
    /// </summary>
    public class GrpcCopyClientConfiguration
    {
        /// <nodoc />
        public int ClientBufferSizeBytes { get; set; } = ContentStore.Grpc.GrpcConstants.DefaultBufferSizeBytes;

        /// <summary>
        /// A timeout that Grpc Client is allowed to wait when closing the connection with another side.
        /// </summary>
        /// <remarks>
        /// We noticed that in some cases closing the channel may hang, so this timeout is protecting us from waiting indefinitely in application shutdown.
        /// </remarks>
        public TimeSpan DisconnectionTimeout { get; set; } = ContentStore.Grpc.GrpcConstants.DefaultTimeout;

        /// <summary>
        /// A timeout to establish a connection.
        /// </summary>
        public TimeSpan ConnectionTimeout { get; set; } = ContentStore.Grpc.GrpcConstants.DefaultTimeout;

        /// <summary>
        /// When the connection is established in StartupAsync method, this configuration determines "time to first byte" timeout.
        /// </summary>
        public TimeSpan TimeToFirstByteTimeout { get; set; } = ContentStore.Grpc.GrpcConstants.DefaultTimeout;

        /// <nodoc />
        public TimeSpan OperationDeadline { get; set; } = TimeSpan.FromHours(2);

        /// <nodoc />
        public GrpcCoreClientOptions? GrpcCoreClientOptions { get; set; }

        /// <nodoc />
        public GrpcDotNetClientOptions? GrpcDotNetClientOptions { get; set; }

        /// <nodoc />
        public bool UseGrpcDotNetVersion { get; set; }

        /// <nodoc />
        public BandwidthChecker.Configuration BandwidthCheckerConfiguration { get; set; } = BandwidthChecker.Configuration.Disabled;

        /// <nodoc />
        public static GrpcCopyClientConfiguration FromDistributedContentSettings(DistributedContentSettings dcs)
        {
            var grpcCopyClientConfiguration = new GrpcCopyClientConfiguration();
            ApplyIfNotNull(dcs.GrpcCopyClientBufferSizeBytes, v => grpcCopyClientConfiguration.ClientBufferSizeBytes = v);
            ApplyIfNotNull(dcs.GrpcCopyClientDisconnectionTimeoutSeconds, v => grpcCopyClientConfiguration.DisconnectionTimeout = TimeSpan.FromSeconds(v));
            ApplyIfNotNull(dcs.GrpcCopyClientConnectionTimeoutSeconds, v => grpcCopyClientConfiguration.ConnectionTimeout = TimeSpan.FromSeconds(v));
            ApplyIfNotNull(dcs.TimeToFirstByteTimeoutInSeconds, v => grpcCopyClientConfiguration.TimeToFirstByteTimeout = TimeSpan.FromSeconds(v));
            ApplyIfNotNull(dcs.GrpcCopyClientOperationDeadlineSeconds, v => grpcCopyClientConfiguration.OperationDeadline = TimeSpan.FromSeconds(v));
            ApplyIfNotNull(dcs.GrpcCopyClientGrpcCoreClientOptions, v => grpcCopyClientConfiguration.GrpcCoreClientOptions = v);
            ApplyIfNotNull(dcs.GrpcCopyClientGrpcDotNetClientOptions, v => grpcCopyClientConfiguration.GrpcDotNetClientOptions = v);
            grpcCopyClientConfiguration.BandwidthCheckerConfiguration = BandwidthChecker.Configuration.FromDistributedContentSettings(dcs);
            grpcCopyClientConfiguration.UseGrpcDotNetVersion = dcs.UseGrpcDotNetForCopies();
            return grpcCopyClientConfiguration;
        }
    }
}
