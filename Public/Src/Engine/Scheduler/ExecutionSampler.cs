// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.ContractsLight;
using BuildXL.Scheduler.Distribution;
using BuildXL.Utilities;
using BuildXL.Utilities.Instrumentation.Common;

#pragma warning disable 1591 // disabling warning about missing API documentation; TODO: Remove this line and write documentation!

namespace BuildXL.Scheduler
{
    /// <summary>
    /// Collects samples of data while the scheduler is executing pips
    /// </summary>
    public sealed class ExecutionSampler
    {
        private readonly long m_maxProcessPips;
        private DateTime m_lastSnapshotUtc = DateTime.MinValue;
        private readonly bool m_isDistributed;

        // Used to prevent a caller from fetching data while it is being updated
        private readonly object m_lock = new object();

        /// <summary>
        /// Creates an ExecutionSampler
        /// </summary>
        public ExecutionSampler(bool isDistributed, long maxProcessPips)
        {
            m_isDistributed = isDistributed;
            m_maxProcessPips = maxProcessPips;
        }

        /// <summary>
        /// Updates the limiting resource based on observed state
        /// </summary>
        /// <param name="aggregator">Performance Aggregator</param>
        /// <param name="pendingProcessPips">Process pips whose graph dependencies have been satisfied but are not currently executing</param>
        /// <param name="lastConcurrencyLimiter">The most recent limiting worker resource</param>
        internal LimitingResource OnPerfSample(PerformanceCollector.Aggregator aggregator, long pendingProcessPips, WorkerResource? lastConcurrencyLimiter)
        {
            if (m_lastSnapshotUtc == DateTime.MinValue)
            {
                // We don't have a window, so don't collect this sample and just remember when the next window starts
                m_lastSnapshotUtc = DateTime.UtcNow;
                return LimitingResource.Other;
            }

            LimitingResource limitingResource = DetermineLimitingResource(aggregator, pendingProcessPips, lastConcurrencyLimiter);
            UpdateAggregations(limitingResource);

            return limitingResource;
        }

        /// <summary>
        /// Determines what build execution is being limited by for the sample period
        /// </summary>
        private LimitingResource DetermineLimitingResource(PerformanceCollector.Aggregator aggregator, long pendingProcessPips, WorkerResource? lastConcurrencyLimiter)
        {
            // (1) We first focus on the specific limiting resources such as not launching processes due to projected memory, user-specified semaphores.

            if (lastConcurrencyLimiter.HasValue)
            {
                // Blocking on semaphore trumps all other factors
                // Some other user configured semaphore is preventing the scheduler from launching additional processes.
                if (lastConcurrencyLimiter.Value.PrecedenceType == WorkerResource.Precedence.SemaphorePrecedence)
                {
                    return LimitingResource.Semaphore;
                }

                // The scheduler has backed off on executing additional process pips because of projected memory usage,
                // even though the graph and concurrency configuration would allow it
                if (lastConcurrencyLimiter.Value == WorkerResource.AvailableMemoryMb)
                {
                    return LimitingResource.ProjectedMemory;
                }

                // Check whether any dispatcher queue is blocked due to unavailable slots.
                if (lastConcurrencyLimiter.Value == WorkerResource.AvailableProcessSlots ||
                    lastConcurrencyLimiter.Value == WorkerResource.AvailableMaterializeInputSlots ||
                    lastConcurrencyLimiter.Value == WorkerResource.AvailableCacheLookupSlots ||
                    lastConcurrencyLimiter.Value == WorkerResource.ModuleAffinity)
                {
                    return LimitingResource.ConcurrencyLimit;
                }
            }    
            
            // (2) Then, we focus on the high resource consumption.

            // High CPU trumps all other factors
            if (aggregator.MachineCpu.Latest > 98)
            {
                return LimitingResource.CPU;
            }

            // Next up is low available memory. Getting too low will cause memory paging to disk which is very bad, but
            // it will also cause more cycles to be spent in the GC and limit the effectiveness of filesystem caching.
            // Hence the number is set to a few hundred MB instead of zero
            if (aggregator.MachineAvailablePhysicalMB.Latest < 300)
            {
                return LimitingResource.Memory;
            }

            // Next we look for any disk with a relatively high percentage of active time
            foreach (var disk in aggregator.DiskStats)
            {
                if (disk.CalculateActiveTime(lastOnly: true) > 95)
                {
                    return LimitingResource.Disk;
                }
            }

            // Then we look for low-ish available ready pips. This isn't zero because we are sampling and we might
            // just hit a sample where the queue wasn't completely drained. The number 3 isn't very scientific
            if (pendingProcessPips < 3)
            {
                return LimitingResource.GraphShape;
            }

            // We really don't expect to fall through to this case. But track it separately so we know if the heuristic
            // needs to be updated.
            // DEBUGGING ONLY
            // Console.WriteLine("CPU:{0}, AvailableMB:{1}, ReadyPips:{2}, RunningPips:{3}", aggregator.MachineCpu.Latest, aggregator.MachineAvailablePhysicalMB.Latest, readyPips, runningPips);
            // Console.WriteLine();
            return LimitingResource.Other;
        }

        private void UpdateAggregations(LimitingResource limitingResource)
        {
            lock (m_lock)
            {
                int time = (int)(DateTime.UtcNow - m_lastSnapshotUtc).TotalMilliseconds;
                m_lastSnapshotUtc = DateTime.UtcNow;

                switch (limitingResource)
                {
                    case LimitingResource.GraphShape:
                        m_blockedOnGraphMs += time;
                        break;
                    case LimitingResource.CPU:
                        m_blockedOnCpuMs += time;
                        break;
                    case LimitingResource.Disk:
                        m_blockedOnDiskMs += time;
                        break;
                    case LimitingResource.Memory:
                        m_blockedOnMemoryMs += time;
                        break;
                    case LimitingResource.ProjectedMemory:
                        m_blockedOnProjectedMemoryMs += time;
                        break;
                    case LimitingResource.Semaphore:
                        m_blockedOnSemaphoreMs += time;
                        break;
                    case LimitingResource.ConcurrencyLimit:
                        m_blockedOnConcurrencyLimit += time;
                        break;
                    case LimitingResource.Other:
                        m_blockedOnUnknownMs += time;
                        break;
                    default:
                        Contract.Assert(false, "Unexpected LimitingResource:" + limitingResource.ToString());
                        throw new NotImplementedException("Unexpected Limiting Resource:" + limitingResource.ToString());
                }
            }
        }

        /// <summary>
        /// Resource limiting execution, based on heuristic
        /// </summary>
        public enum LimitingResource
        {
            /// <summary>
            /// Not enough concurrency in the build to run more pips
            /// </summary>
            GraphShape,

            /// <summary>
            /// Not enough CPU cores to run more pips
            /// </summary>
            CPU,

            /// <summary>
            /// Disk access appears to be slowing pips down
            /// </summary>
            Disk,

            /// <summary>
            /// Available memory is low
            /// </summary>
            Memory,

            /// <summary>
            /// There is more work that can be done and more resources to do it but concurrentcy limits have been
            /// reached. This is based on unavailability of dispatcher slots for several steps: MaterializeInput, ExecuteProcess, CacheLookup
            /// </summary>
            ConcurrencyLimit,

            /// <summary>
            /// The scheduler throttling because it projects that launching an additional process would exhaust
            /// available RAM
            /// </summary>
            ProjectedMemory,

            /// <summary>
            /// A user configured semaphore is limiting concurrency
            /// </summary>
            Semaphore,

            /// <summary>
            /// Don't know what the limiting factor is
            /// </summary>
            Other,
        }

        private int m_blockedOnGraphMs = 0;
        private int m_blockedOnCpuMs = 0;
        private int m_blockedOnDiskMs = 0;
        private int m_blockedOnMemoryMs = 0;
        private int m_blockedOnConcurrencyLimit = 0;
        private int m_blockedOnProjectedMemoryMs = 0;
        private int m_blockedOnSemaphoreMs = 0;
        private int m_blockedOnUnknownMs = 0;

        private int GetPercentage(int numerator)
        {
            int totalTime = m_blockedOnGraphMs + m_blockedOnCpuMs + m_blockedOnDiskMs + m_blockedOnMemoryMs + m_blockedOnConcurrencyLimit + m_blockedOnProjectedMemoryMs + m_blockedOnSemaphoreMs + m_blockedOnUnknownMs;
            if (totalTime > 0)
            {
                // We intentionally return the floor to make sure we don't add up to more than 100
                return (int)((double)numerator / totalTime * 100);
            }

            return 0;
        }

        /// <summary>
        /// Gets the percentage of time blocked on various resources
        /// </summary>
        public LimitingResourcePercentages GetLimitingResourcePercentages()
        {
            lock (m_lock)
            {
                var result = new LimitingResourcePercentages()
                {
                    GraphShape = GetPercentage(m_blockedOnGraphMs),
                    CPU = GetPercentage(m_blockedOnCpuMs),
                    Disk = GetPercentage(m_blockedOnDiskMs),
                    Memory = GetPercentage(m_blockedOnMemoryMs),
                    ProjectedMemory = GetPercentage(m_blockedOnProjectedMemoryMs),
                    Semaphore = GetPercentage(m_blockedOnSemaphoreMs),
                    ConcurrencyLimit = GetPercentage(m_blockedOnConcurrencyLimit),
                };

                // It's possible these percentages don't add up to 100%. So we'll round everything down
                // and use "Other" as our fudge factor to make sure we add up to 100.
                result.Other = 100 - result.GraphShape - result.CPU - result.Disk - result.Memory - result.ProjectedMemory - result.Semaphore - result.ConcurrencyLimit;

                return result;
            }
        }
    }
}
