// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using BuildXL.Tracing;
using BuildXL.Utilities;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Instrumentation.Common;

namespace BuildXL.Engine.Distribution
{
    /// <summary>
    /// Exposes common members between orchestrator and worker distribution services.
    /// </summary>
    public abstract class DistributionService : IDisposable
    {
        /// <summary>
        /// Counters for message verification
        /// </summary>
        public readonly CounterCollection<DistributionCounter> Counters = new CounterCollection<DistributionCounter>();

        /// <summary>
        /// Invocation id to represent a distributed build session.
        /// </summary>
        public DistributedInvocationId InvocationId { get; }

        /// <nodoc/>
        public DistributionService(DistributedInvocationId invocationId)
        {
            InvocationId = invocationId;
        }

        /// <summary>
        /// Initializes the distribution service.
        /// </summary>
        /// <returns>True if initialization completed successfully. Otherwise, false.</returns>
        public abstract bool Initialize();

        /// <summary>
        /// Exits the distribution service.
        /// </summary>
        public abstract Task<bool> ExitAsync(Optional<string> failure, bool isUnexpected);

        /// <nodoc/>
        public abstract void Dispose();

        /// <summary>
        /// Log statistics of distribution counters.
        /// </summary>
        public void LogStatistics(LoggingContext loggingContext)
        {
            Counters.LogAsStatistics("Distribution", loggingContext);
        }
    }
}
