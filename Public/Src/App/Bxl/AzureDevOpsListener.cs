﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;
using System.Threading;
using BuildXL.Pips.Operations;
using BuildXL.Processes.Tracing;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Core.Tracing;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Tracing;
using BuildXL.ViewModel;

namespace BuildXL
{
    /// <summary>
    /// This event listener should only be hooked up when AzureDevOps optimized UI is requested by
    /// the user via the /ado command-line flag.
    /// </summary>
    /// <remarks>
    /// Capturing the information for the build summary in azure devops is best done in the code
    /// where the values are computed. Unfortunately this is not always possible, for example in the 
    /// Cache miss analyzer case, there is would be prohibitive to pass through the BuildSummary class
    /// through all the abstraction layers and passing it down through many levels. Therefore this is
    /// a way to still collect the information.
    /// </remarks>
    public sealed class AzureDevOpsListener : FormattingEventListener
    {
        /// <nodoc />
        public const int MaxErrorsToIncludeInSummary = 50;

        /// <summary>
        /// The maximum number of AzureDevOps issues to log. Builds with too many issues can cause the UI to bog down.
        /// </summary>
        private readonly int m_adoConsoleMaxIssuesToLog;
        private readonly IConsole m_console;
        private readonly BuildViewModel m_buildViewModel;

        /// <summary>
        /// The last reported percentage. To avoid double reporting the same percentage over and over
        /// </summary>
        private int m_lastReportedProgress = -1;
        private int m_warningCount;
        private int m_errorCount;
        private readonly StatusMessageThrottler m_statusMessageThrottler;
        /// <nodoc />
        public AzureDevOpsListener(
            Events eventSource,
            IConsole console,
            DateTime baseTime,
            BuildViewModel buildViewModel,
            bool useCustomPipDescription,
            [AllowNull] WarningMapper warningMapper,
            int adoConsoleMaxIssuesToLog)
            : base(eventSource, baseTime, warningMapper: warningMapper, level: EventLevel.Verbose, captureAllDiagnosticMessages: false, timeDisplay: TimeDisplay.Seconds, useCustomPipDescription: useCustomPipDescription)
        {
            Contract.RequiresNotNull(console);
            Contract.RequiresNotNull(buildViewModel);

            m_console = console;
            m_buildViewModel = buildViewModel;
            m_adoConsoleMaxIssuesToLog = adoConsoleMaxIssuesToLog;
            m_statusMessageThrottler = new StatusMessageThrottler(baseTime);
        }

        /// <inheritdoc />
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1063:ImplementIDisposableCorrectly")]
        public override void Dispose()
        {
            m_console.Dispose();
            base.Dispose();
        }

        /// <inheritdoc />
        protected override void OnInformational(EventWrittenEventArgs eventData)
        {
            switch (eventData.EventId)
            {
                case (int)SharedLogEventId.PipStatus:
                case (int)BuildXL.Scheduler.Tracing.LogEventId.PipStatusNonOverwriteable:
                {
                    var payload = eventData.Payload;

                    var executing = (long)payload[10];
                    var succeeded = (long)payload[11];
                    var failed = (long)payload[12];
                    var skipped = (long)payload[13];
                    var pending = (long)payload[14];
                    var waiting = (long)payload[15];

                    var done = succeeded + failed + skipped;
                    var total = done + pending + waiting + executing;

                    var processPercent = (100.0 * done) / (total * 1.0);
                    var currentProgress = Convert.ToInt32(Math.Floor(processPercent));

                    if (currentProgress > m_lastReportedProgress && !m_statusMessageThrottler.ShouldThrottleStatusUpdate())
                    {
                        m_lastReportedProgress = currentProgress;
                        m_console.WriteOutputLine(MessageLevel.Info, $"##vso[task.setprogress value={currentProgress};]Pip Execution phase");
                    }

                    break;
                }
            }
        }

        /// <inheritdoc />
        protected override void OnAlways(EventWrittenEventArgs eventData)
        {
        }

        /// <inheritdoc />
        protected override void OnVerbose(EventWrittenEventArgs eventData)
        {
            switch (eventData.EventId)
            {
                case (int)SharedLogEventId.CacheMissAnalysis:
                {
                    var payload = eventData.Payload;

                    m_buildViewModel.BuildSummary.CacheSummary.Entries.Add(
                        new CacheMissSummaryEntry
                        {
                            PipDescription = (string)payload[0],
                            Reason = (string)payload[1],
                            FromCacheLookup = (bool)payload[2],
                        }
                    );
                }
                break;
                case (int)SharedLogEventId.CacheMissAnalysisBatchResults:
                {
                    m_buildViewModel.BuildSummary.CacheSummary.BatchEntries.Add((string)eventData.Payload[0]);
                }
                break;
            }
        }

        /// <inheritdoc />
        protected override void OnCritical(EventWrittenEventArgs eventData)
        {
            LogAzureDevOpsIssue(eventData, "error");
        }

        /// <inheritdoc />
        protected override void OnError(EventWrittenEventArgs eventData)
        {
            LogIssueWithLimit(ref m_errorCount, eventData, "error");

            CloudBuildListener.ApplyIfPipError(eventData, (pipProcessErrorEventFields, workerId) =>
            {
                string semiStableHash = Pip.FormatSemiStableHash(pipProcessErrorEventFields.PipSemiStableHash);
                m_buildViewModel.BuildSummary.AddPipError(new BuildSummaryPipDiagnostic
                {
                    SemiStablePipId = semiStableHash,
                    PipDescription = pipProcessErrorEventFields.PipDescription,
                    SpecPath = pipProcessErrorEventFields.PipSpecPath,
                    ToolName = pipProcessErrorEventFields.PipExe,
                    ExitCode = pipProcessErrorEventFields.ExitCode,
                    Output = pipProcessErrorEventFields.OutputToLog,

                },
                MaxErrorsToIncludeInSummary);
            });
        }

        /// <inheritdoc />
        protected override void OnWarning(EventWrittenEventArgs eventData)
        {
            LogIssueWithLimit(ref m_warningCount, eventData, "warning");
        }

        /// <inheritdoc />
        protected override void Output(EventLevel level, EventWrittenEventArgs eventData, string text, bool doNotTranslatePaths = false)
        {
        }

        private void LogAzureDevOpsIssue(EventWrittenEventArgs eventData, string eventType)
        {
            int dxCode = eventData.EventId;
            var message = eventData.Message;
            var args = eventData.Payload == null ? CollectionUtilities.EmptyArray<object>() : eventData.Payload.ToArray();

            // construct a short message for ADO console
            if ((eventData.EventId == (int)LogEventId.PipProcessError)
                || (eventData.EventId == (int)SharedLogEventId.DistributionWorkerForwardedError && (int)args[1] == (int)LogEventId.PipProcessError))
            {
                var pipProcessError = new PipProcessEventFields(eventData.Payload, forwardedPayload: eventData.EventId != (int)LogEventId.PipProcessError, isPipProcessError: true);
                var pipSemiStableHash = Pip.FormatSemiStableHash(pipProcessError.PipSemiStableHash);
                var formattedDXCode = FormatErrorCode((int)LogEventId.PipProcessError);

                // Construct PipProcessError message.
                LogIssue(eventType, string.Join(Environment.NewLine, 
                    new[] {
                        @$"{formattedDXCode}[{pipSemiStableHash}, {pipProcessError.ShortPipDescription}, {pipProcessError.PipSpecPath}] - failed with exit code {pipProcessError.ExitCode}, {pipProcessError.OptionalMessage}",
                        pipProcessError.OutputToLog,
                        pipProcessError.MessageAboutPathsToLog,
                        pipProcessError.PathsToLog
                    }.Where(s => !string.IsNullOrWhiteSpace(s))));
            }
            else if ((eventData.EventId == (int)LogEventId.PipProcessWarning)
                || (eventData.EventId == (int)SharedLogEventId.DistributionWorkerForwardedWarning && (int)args[1] == (int)LogEventId.PipProcessWarning))
            {
                var pipProcessWarning = new PipProcessEventFields(eventData.Payload, forwardedPayload: eventData.EventId != (int)LogEventId.PipProcessWarning, isPipProcessError: false);
                var formattedDXCode = FormatErrorCode((int)LogEventId.PipProcessWarning);
                var pipSemiStableHash = Pip.FormatSemiStableHash(pipProcessWarning.PipSemiStableHash);

                // Construct PipProcessWarning message.
                LogIssue(eventType, string.Join(Environment.NewLine,
                    new[] {
                        @$"{formattedDXCode}[{pipSemiStableHash}, {pipProcessWarning.PipDescription}, {pipProcessWarning.PipSpecPath}] - warnings",
                        pipProcessWarning.OutputToLog,
                        pipProcessWarning.MessageAboutPathsToLog,
                        pipProcessWarning.PathsToLog
                    }.Where(s => !string.IsNullOrWhiteSpace(s))));
            }
            else
            {
                if (eventData.EventId == (int)SharedLogEventId.DistributionWorkerForwardedError || eventData.EventId == (int)SharedLogEventId.DistributionWorkerForwardedWarning)
                {
                    message = "{0}";
                }

                string messageBodyWithAppendedDXCode = string.Concat(FormatErrorCode(dxCode), message);
                var messageBody = string.Format(CultureInfo.CurrentCulture, messageBodyWithAppendedDXCode, args);
                LogIssue(eventType, messageBody);
            }
        }

        private void LogIssue(string eventType, string message)
        {
            // This method logs message body which needs to be highlighted.
            m_console.WriteOutputLine(MessageLevel.Info, $"##vso[task.logIssue type={eventType};]{ReplaceNewLinesWithADOFormattingCommands(message, eventType)}");
        }

        private string FormatErrorCode(int dxCode)
        {
            // Construct DX code for the error and append it at the beginning of the error message.
            return string.Concat("DX", dxCode.ToString("D4"), ' ');
        }

        private string ReplaceNewLinesWithADOFormattingCommands(string body, string eventType)
        {
            return body.Replace("\r\n", $"%0D%0A##[{eventType}]")
                                  .Replace("\r", $"%0D##[{eventType}]")
                                  .Replace("\n", $"%0A##[{eventType}]");
        }

        private void LogIssueWithLimit(ref int counter, EventWrittenEventArgs eventData, string level)
        {
            int errorCount = Interlocked.Increment(ref counter);
            if (errorCount < m_adoConsoleMaxIssuesToLog + 1)
            {
                LogAzureDevOpsIssue(eventData, level);
            }
            else if (errorCount == m_adoConsoleMaxIssuesToLog + 1)
            {
                m_console.WriteOutputLine(MessageLevel.Info, $"##vso[task.logIssue type={level};] Future messages of this level are truncated");
            }
        }
    }
}
