﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.Host.Configuration;
using BuildXL.Utilities.Core.Tasks;

#nullable enable

namespace BuildXL.Cache.ContentStore.Utils
{
    /// <summary>
    /// Checks that a copy has a minimum bandwidth, and cancels copies otherwise.
    /// </summary>
    public class BandwidthChecker
    {
        private const double BytesInMb = 1024 * 1024;

        private readonly HistoricalBandwidthLimitSource? _historicalBandwidthLimitSource;
        private readonly IBandwidthLimitSource _bandwidthLimitSource;
        private readonly Configuration _config;

        /// <nodoc />
        public BandwidthChecker(Configuration config)
        {
            _config = config;
            _bandwidthLimitSource = config.MinimumBandwidthMbPerSec == null
                ? (IBandwidthLimitSource)new HistoricalBandwidthLimitSource(config.HistoricalBandwidthRecordsStored)
                : new ConstantBandwidthLimit(config.MinimumBandwidthMbPerSec.Value);
            _historicalBandwidthLimitSource = _bandwidthLimitSource as HistoricalBandwidthLimitSource;
        }

        /// <summary>
        /// Checks that a copy has a minimum bandwidth, and cancels it otherwise.
        /// </summary>
        /// <param name="context">The context of the operation.</param>
        /// <param name="copyTaskFactory">Function that will trigger the copy.</param>
        /// <param name="options">An option instance that controls the copy operation and allows getting the progress.</param>
        /// <param name="getErrorResult">Function to get the result in case of a bandwidth timeout</param>
        public async Task<TResult> CheckBandwidthAtIntervalAsync<TResult>(
            OperationContext context,
            Func<CancellationToken, Task<TResult>> copyTaskFactory,
            CopyOptions options,
            Func<string, TResult> getErrorResult)
            where TResult : ICopyResult
        {
            if (_historicalBandwidthLimitSource != null)
            {
                var timer = Stopwatch.StartNew();
                var (result, bytesCopied) = await impl();
                timer.Stop();

                // Bandwidth checker expects speed in MiB/s, so convert it.
                var speed = bytesCopied / timer.Elapsed.TotalSeconds / BytesInMb;
                _historicalBandwidthLimitSource.AddBandwidthRecord(speed);

                return result;
            }
            else
            {
                return (await impl()).result;
            }

            async Task<(TResult result, long bytesCopied)> impl()
            {
                // This method should not fail with exceptions because the resulting task may be left unobserved causing an application to crash
                // (given that the app is configured to fail on unobserved task exceptions).
                var minimumSpeedInMbPerSec = _bandwidthLimitSource.GetMinimumSpeedInMbPerSec() * _config.BandwidthLimitMultiplier;
                minimumSpeedInMbPerSec = Math.Min(minimumSpeedInMbPerSec, _config.MaxBandwidthLimit);
                CopyStatistics previousCopyStat = options.CopyStatistics;
                long startPosition = previousCopyStat.Bytes;
                var copyCompleted = false;
                using var copyCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Token);

                Task<TResult> copyTask = copyTaskFactory(copyCancellation.Token);

                // Subscribing for potential task failure here to avoid unobserved task exceptions.
                traceCopyTaskFailures();

                while (!copyCompleted)
                {
                    // Wait some time for bytes to be copied
                    var configBandwidthCheckInterval = options.BandwidthConfiguration?.Interval ?? _config.BandwidthCheckInterval;

                    if (options.BandwidthConfiguration != null)
                    {
                        minimumSpeedInMbPerSec = options.BandwidthConfiguration.RequiredMegabytesPerSecond;
                    }

                    var firstCompletedTask = await Task.WhenAny(copyTask,
                        Task.Delay(configBandwidthCheckInterval, context.Token));

                    copyCompleted = firstCompletedTask == copyTask;
                    if (copyCompleted)
                    {
                        var result = await copyTask;
                        result.MinimumSpeedInMbPerSec = minimumSpeedInMbPerSec;
                        var bytesCopied = result.Size ?? options.CopyStatistics.Bytes;

                        TrackBytesReceived(bytesCopied - previousCopyStat.Bytes);
                        return (result, bytesCopied);
                    }
                    else if (context.Token.IsCancellationRequested)
                    {
                        context.Token.ThrowIfCancellationRequested();
                    }

                    // Copy is not completed and operation has not been canceled, perform
                    // bandwidth check
                    CopyStatistics currentCopyStat = options.CopyStatistics;
                    var position = currentCopyStat.Bytes;
                    double networkDuration = currentCopyStat.NetworkCopyDuration.TotalSeconds;

                    var bytesTransferredPerIteration = position - previousCopyStat.Bytes;
                    var networkDelay = networkDuration - previousCopyStat.NetworkCopyDuration.TotalSeconds;

                    TrackBytesReceived(bytesTransferredPerIteration);

                    var receivedMiB = bytesTransferredPerIteration / BytesInMb;
                    double currentSpeed;
                    if (options.BandwidthConfiguration?.EnableNetworkCopySpeedCalculation == true && networkDelay > 0)
                    {
                        currentSpeed = receivedMiB / networkDelay;
                    }
                    else
                    {
                        currentSpeed = receivedMiB / configBandwidthCheckInterval.TotalSeconds;
                    }

                    if (currentSpeed == 0 || currentSpeed < minimumSpeedInMbPerSec)
                    {
                        await copyCancellation.CancelTokenAsyncIfSupported();

                        var totalBytesCopied = position - startPosition;
                        var result = getErrorResult(
                            $"Average speed was {currentSpeed}MiB/s - under {minimumSpeedInMbPerSec}MiB/s requirement. Aborting copy with {totalBytesCopied} bytes copied (received {bytesTransferredPerIteration} bytes in {configBandwidthCheckInterval.TotalSeconds} seconds).");
                        return (result, totalBytesCopied);
                    }

                    previousCopyStat = currentCopyStat;
                }

                var copyFileResult = await copyTask;
                copyFileResult.MinimumSpeedInMbPerSec = minimumSpeedInMbPerSec;
                return (copyFileResult, previousCopyStat.Bytes - startPosition);

                void traceCopyTaskFailures()
                {
                    // When the operation is cancelled, it is possible for the copy operation to fail.
                    // In this case we still want to trace the failure (but just with the debug severity and not with the error),
                    // but we should exclude ObjectDisposedException completely.
                    // That's why we don't use task.FireAndForget but tracing inside the task's continuation.
                    copyTask.ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            if (!(t.Exception?.InnerException is ObjectDisposedException))
                            {
                                context.TracingContext.Debug($"Checked copy failed. {t.Exception?.DemystifyToString()}", component: nameof(BandwidthChecker), operation: nameof(CheckBandwidthAtIntervalAsync));
                            }
                        }
                    });
                }
            }
        }

        private static void TrackBytesReceived(long bytesTransferredPerIteration) => CacheActivityTracker.AddValue(CaSaaSActivityTrackingCounters.RemoteCopyBytes, bytesTransferredPerIteration);

        /// <nodoc />
        public class Configuration
        {
            /// <nodoc />
            public Configuration(TimeSpan bandwidthCheckInterval, double? minimumBandwidthMbPerSec, double? maxBandwidthLimit, double? bandwidthLimitMultiplier, int? historicalBandwidthRecordsStored)
            {
                BandwidthCheckInterval = bandwidthCheckInterval;
                MinimumBandwidthMbPerSec = minimumBandwidthMbPerSec;
                MaxBandwidthLimit = maxBandwidthLimit ?? double.MaxValue;
                BandwidthLimitMultiplier = bandwidthLimitMultiplier ?? 1;
                HistoricalBandwidthRecordsStored = historicalBandwidthRecordsStored ?? 64;

                Contract.Assert(MaxBandwidthLimit > 0);
                Contract.Assert(BandwidthLimitMultiplier > 0);
                Contract.Assert(HistoricalBandwidthRecordsStored > 0);
            }

            /// <nodoc />
            public static readonly Configuration Default = new Configuration(TimeSpan.FromSeconds(30), null, null, null, null);

            /// <nodoc />
            public static readonly Configuration Disabled = new Configuration(TimeSpan.FromMilliseconds(int.MaxValue), minimumBandwidthMbPerSec: 0, null, null, null);

            /// <nodoc />
            public static Configuration FromDistributedContentSettings(DistributedContentSettings dcs)
            {
                if (!dcs.IsBandwidthCheckEnabled)
                {
                    return Disabled;
                }

                return new Configuration(
                    TimeSpan.FromSeconds(dcs.BandwidthCheckIntervalSeconds),
                    dcs.MinimumSpeedInMbPerSec,
                    dcs.MaxBandwidthLimit,
                    dcs.BandwidthLimitMultiplier,
                    dcs.HistoricalBandwidthRecordsStored);
            }

            /// <nodoc />
            public TimeSpan BandwidthCheckInterval { get; }

            /// <nodoc />
            public double? MinimumBandwidthMbPerSec { get; }

            /// <nodoc />
            public double MaxBandwidthLimit { get; }

            /// <nodoc />
            public double BandwidthLimitMultiplier { get; }

            /// <nodoc />
            public int HistoricalBandwidthRecordsStored { get; }
        }
    }

    /// <nodoc />
    public class BandwidthTooLowException : Exception
    {
        /// <nodoc />
        public BandwidthTooLowException(string message)
            : base(message)
        {
        }
    }
}
