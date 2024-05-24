// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Security.Cryptography.X509Certificates;
using BuildXL.Utilities.Configuration;
using Grpc.Core;

namespace BuildXL.Engine.Distribution.Grpc
{
    internal static class GrpcSettings
    {
        /// <summary>
        /// How many retry attempts when a grpc message is failed to send in the given <see cref="CallTimeout"/>
        /// </summary>
        public static int MaxAttempts => EngineEnvironmentSettings.GrpcMaxAttempts;

        /// <summary>
        /// Maximum time for a Grpc call (both orchestrator->worker and worker->orchestrator)
        /// </summary>
        /// <remarks>
        /// Default: 5 minutes
        /// </remarks>
        public static TimeSpan CallTimeout => EngineEnvironmentSettings.DistributionConnectTimeout;

        /// <summary>
        /// Whether we should use encryption in the grpc calls.
        /// </summary>
        public static bool EncryptionEnabled => EngineEnvironmentSettings.GrpcEncryptionEnabled &&
                CertificateSubjectName != null;

        /// <summary>
        /// Certificate subject name
        /// </summary>
        public static string CertificateSubjectName => EngineEnvironmentSettings.GrpcCertificateSubjectName.Value ?? EngineEnvironmentSettings.CBBuildUserCertificateName.Value;

        /// <summary>
        /// Whether we should use authentication in the grpc calls.
        /// </summary>
        /// <remarks>
        /// Authentication feature requires the encryption.
        /// </remarks>
        public static bool AuthenticationEnabled => EncryptionEnabled && EngineEnvironmentSettings.CBBuildIdentityTokenPath.Value != null;

        /// <summary>
        /// Whether we should enable heartbeat messages.
        /// </summary>
        public static bool HeartbeatEnabled => EngineEnvironmentSettings.GrpcHeartbeatEnabled;

        /// <summary>
        /// The frequency of heartbeat messages.
        /// </summary>
        public static int HeartbeatIntervalMs => EngineEnvironmentSettings.GrpcHeartbeatIntervalMs.Value ?? 60000;

        /// <summary>
        /// Maximum time to wait for the orchestrator to connect to a worker.
        /// </summary>
        /// <remarks>
        /// Default: 75 minutes
        /// </remarks>
        public static TimeSpan WorkerAttachTimeout => EngineEnvironmentSettings.WorkerAttachTimeout;

        public static void ParseHeader(Metadata header, out string sender, out DistributedInvocationId senderInvocationId, out string traceId, out string token)
        {
            sender = string.Empty;
            string relatedActivityId = string.Empty;
            string environment = string.Empty;
            string engineVersion = string.Empty;
           
            traceId = string.Empty;
            token = string.Empty;

            foreach (var kvp in header)
            {
                if (kvp.Key == GrpcMetadata.TraceIdKey)
                {
                    traceId = kvp.Value;
                }
                else if (kvp.Key == GrpcMetadata.RelatedActivityIdKey)
                {
                    relatedActivityId = kvp.Value;
                }
                else if (kvp.Key == GrpcMetadata.EnvironmentKey)
                {
                    environment = kvp.Value;
                }
                else if (kvp.Key == GrpcMetadata.EngineVersionKey)
                {
                    engineVersion = kvp.Value;
                }
                else if (kvp.Key == GrpcMetadata.SenderKey)
                {
                    sender = kvp.Value;
                }
                else if (kvp.Key == GrpcMetadata.AuthKey)
                {
                    token = kvp.Value;
                }
            }

            senderInvocationId = new DistributedInvocationId(relatedActivityId, environment, engineVersion);
        }
    }
}