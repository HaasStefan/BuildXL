// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.Tracing;
using BuildXL.Cache.ContentStore.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Native.IO;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.ParallelAlgorithms;
using BuildXL.Utilities.Core.Tasks;
using BuildXL.Utilities.Core.Tracing;
using BuildXL.Utilities.Tracing;
using Azure.Messaging.EventHubs;
using static BuildXL.Cache.ContentStore.Distributed.NuCache.EventStreaming.ContentLocationEventStoreCounters;
using System.IO;
using System.Net.Sockets;

#nullable enable

namespace BuildXL.Cache.ContentStore.Distributed.NuCache.EventStreaming
{
    /// <summary>
    /// Event store that uses Azure Event Hub for event propagation.
    /// </summary>
    public class EventHubContentLocationEventStore : ContentLocationEventStore
    {
        private const string EventProcessingDelayInSecondsMetricName = nameof(EventProcessingDelayInSecondsMetricName);

        private readonly ContentLocationEventStoreConfiguration _configuration;
        private readonly string _localMachineName;

        private const string SenderMachineEventKey = "SenderMachine";
        private const string EpochEventKey = "Epoch";
        private const string OperationIdEventKey = "OperationId";

        private readonly IEventHubClient _eventHubClient;
        private readonly IRetryPolicy _extraEventHubClientRetryPolicy;

        private Processor? _currentEventProcessor;
        private readonly ActionBlockSlim<ProcessEventsInput>[]? _eventProcessingBlocks;

        private EventSequencePoint? _lastProcessedSequencePoint;

        private int _updatingPendingEventProcessingStates = 0;

        /// <summary>
        /// We use a queue to ensure that <see cref="_lastProcessedSequencePoint"/> is updated in such a way that
        /// it is never set to a value where messages prior to that sequence number have not been processed. Naively,
        /// setting this value, as messages are processed could break this criteria because of concurrent event processing.
        /// Given that, message batch state (with associated sequence number) are put into queue in order messages are received,
        /// and only dequeued (and used to update <see cref="_lastProcessedSequencePoint"/>) when all messages associated with the
        /// batch have been processed. Thereby, ensuring <see cref="_lastProcessedSequencePoint"/> is updated in correct order.
        /// </summary>
        private ConcurrentQueue<SharedEventProcessingState> _pendingEventProcessingStates = new ConcurrentQueue<SharedEventProcessingState>();

        private long _queueSize;

        /// <inheritdoc />
        public EventHubContentLocationEventStore(
            ContentLocationEventStoreConfiguration configuration,
            IContentLocationEventHandler eventHandler,
            string localMachineName,
            CentralStorage centralStorage,
            Interfaces.FileSystem.AbsolutePath workingDirectory,
            IClock clock)
            : base(configuration, nameof(EventHubContentLocationEventStore), eventHandler, centralStorage, workingDirectory, clock)
        {
            Contract.Requires(configuration.MaxEventProcessingConcurrency >= 1);

            _configuration = configuration;
            _localMachineName = localMachineName;
            _eventHubClient = CreateEventHubClient(configuration);
            _extraEventHubClientRetryPolicy = CreateEventHubClientRetryPolicy();

            if (configuration.MaxEventProcessingConcurrency > 1)
            {
                _eventProcessingBlocks =
                    Enumerable.Range(1, configuration.MaxEventProcessingConcurrency)
                        .Select(
                            (_, index) =>
                            {
                                ValidationMode validationMode = configuration.SelfCheckSerialization
                                    ? (configuration.SelfCheckSerializationShouldFail ? ValidationMode.Fail : ValidationMode.Trace)
                                    : ValidationMode.Off;
                                SerializationMode serializationMode = configuration.UseSpanBasedSerialization
                                    ? SerializationMode.SpanBased
                                    : SerializationMode.Legacy;
                                var serializer = new ContentLocationEventDataSerializer(FileSystem, serializationMode, validationMode);
                                return ActionBlockSlim.CreateWithAsyncAction<ProcessEventsInput>(
                                    new ActionBlockSlimConfiguration(
                                        DegreeOfParallelism: 1,
                                        CapacityLimit: configuration.EventProcessingMaxQueueSize,
                                        SingleProducerConstrained: true,
                                        UseLongRunningTasks: true),
                                    t => ProcessEventsCoreAsync(t, serializer));
                            })
                        .ToArray();
            }
        }

        /// <summary>
        /// Factory method for creating an instance of <see cref="ContentLocationEventStore"/> based on <paramref name="configuration"/>.
        /// </summary>
        public static IEventHubClient CreateEventHubClient(ContentLocationEventStoreConfiguration configuration)
        {
            Contract.Requires(configuration != null);

            switch (configuration)
            {
                case MemoryContentLocationEventStoreConfiguration memoryConfig:
                    return new MemoryEventHubClient(memoryConfig);
                case NullContentLocationEventStoreConfiguration:
                    return new MemoryEventHubClient(new MemoryContentLocationEventStoreConfiguration());
                default:
                    throw new InvalidOperationException($"Unknown EventStore type '{configuration.GetType()}'.");
            }
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> StartupCoreAsync(OperationContext context)
        {
            Tracer.Info(context, $"Initializing Event Hub-based content location event store with epoch '{_configuration.Epoch}', UseSpanBasedSerialization={_configuration.UseSpanBasedSerialization}.");

            var baseInitializeResult = await base.StartupCoreAsync(context);
            if (!baseInitializeResult)
            {
                return baseInitializeResult;
            }

            _currentEventProcessor = new Processor(context, this);

            await _eventHubClient.StartupAsync(context).ThrowIfFailure();

            return BoolResult.Success;
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> ShutdownCoreAsync(OperationContext context)
        {
            // Need to dispose nagle queue first to ensure last batch is processed before buffers are disposed
            var result = await base.ShutdownCoreAsync(context);

            await _eventHubClient.ShutdownAsync(context).ThrowIfFailure();

            if (_eventProcessingBlocks != null)
            {
                foreach (var eventProcessingBlock in _eventProcessingBlocks)
                {
                    eventProcessingBlock.Complete();
                    await eventProcessingBlock.Completion;
                }
            }

            return result;
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> SendEventsCoreAsync(
            OperationContext context,
            ContentLocationEventData[] events,
            CounterCollection<ContentLocationEventStoreCounters> counters)
        {
            IReadOnlyList<EventData> eventDatas;
            using (counters[Serialization].Start())
            {
                eventDatas = SerializeEventData(context, events);
            }

            var operationId = context.TracingContext.TraceId;

            for (var eventNumber = 0; eventNumber < eventDatas.Count; eventNumber++)
            {
                var eventData = eventDatas[eventNumber];
                eventData.Properties[EpochEventKey] = _configuration.Epoch;
                eventData.Properties[SenderMachineEventKey] = _localMachineName;
                counters[SentEventBatchCount].Increment();

                Tracer.Info(
                    context,
                    $"{Tracer.Name}: Sending {eventNumber}/{events.Length} event. OpId={operationId}, Epoch='{_configuration.Epoch}', Size={eventData.Body.Length}.");
                counters[SentMessagesTotalSize].Add(eventData.Body.Length);
                eventData.Properties[OperationIdEventKey] = operationId.ToString();

                // Even though event hub client has it's own built-in retry strategy, we have to wrap all the calls into a separate
                // one to cover a few more important cases that the default strategy misses.
                await _extraEventHubClientRetryPolicy.ExecuteAsync(async () =>
                {
                    try
                    {
                        await _eventHubClient.SendAsync(context, eventData);
                    }
                    catch (EventHubsException exception) when (exception.Reason == EventHubsException.FailureReason.ServiceBusy)
                    {
                        // TODO: Verify that the HResult is 50002. Documentation shows that this should be the error code for throttling,
                        // but documentation is done for Microsoft.ServiceBus.Messaging.ServerBusyException and not Microsoft.Azure.EventHubs.ServerBusyException
                        // https://docs.microsoft.com/en-us/azure/event-hubs/event-hubs-messaging-exceptions#serverbusyexception
                        Tracer.Debug(context, $"{Tracer.Name}: OpId={operationId} was throttled by EventHub. HResult={exception.HResult}");
                        Tracer.TrackMetric(context, "EventHubThrottle", 1);

                        throw;
                    }
                    catch (Exception e)
                    {
                        // If the error is not retryable, then the entire operation will fail and we don't need to double trace the error.
                        if (TransientEventHubErrorDetectionStrategy.IsRetryable(e))
                        {
                            Tracer.Debug(context, $"{Tracer.Name}.{nameof(SendEventsCoreAsync)} failed with retryable error=[{e.ToStringDemystified()}]");
                        }

                        throw;
                    }
                }, context.Token);
            }

            return BoolResult.Success;
        }

        private IReadOnlyList<EventData> SerializeEventData(OperationContext context, ContentLocationEventData[] events)
        {
            return EventDataSerializer.Serialize(context, events);
        }

        private async Task ProcessEventsAsync(OperationContext context, List<EventData> messages)
        {
            // Tracking raw messages count.
            Counters[ReceivedEventHubEventsCount].Add(messages.Count);
            CacheActivityTracker.AddValue(CaSaaSActivityTrackingCounters.ReceivedEventHubMessages, messages.Count);

            // Creating nested context for all the processing operations.
            context = context.CreateNested(nameof(EventHubContentLocationEventStore));

            if (messages.Count == 0)
            {
                // This probably does not actually occur, but just in case, ignore empty message batch.
                // NOTE: We do this after logging to ensure we notice if the we are getting empty message batches.
                return;
            }

            var state = new SharedEventProcessingState(context, this, messages);

            if (_eventProcessingBlocks != null)
            {
                await context
                    .CreateOperation(Tracer, () => sendToActionBlockAsync(state))
                    .WithOptions(traceOperationStarted: false, endMessageFactory: r => $"TotalQueueSize={Interlocked.Read(ref _queueSize)}")
                    .RunAsync(caller: "SendToActionBlockAsync")
                    .TraceIfFailure(context);
            }
            else
            {
                await ProcessEventsCoreAsync(new ProcessEventsInput(state, messages, actionBlockIndex: -1, store: this), EventDataSerializer);
            }

            async Task<BoolResult> sendToActionBlockAsync(SharedEventProcessingState st)
            {
                // This local function "sends" a message into an action block based on the sender's hash code to process events in parallel from different machines.
                // (keep in mind, that the data from the same machine should be processed sequentially, because events order matters).
                // Then, it creates a local counter for each processing operation to track the results for the entire batch.
                foreach (var messageGroup in messages.GroupBy(GetProcessingIndex))
                {
                    int actionBlockIndex = messageGroup.Key;
                    var eventProcessingBlock = _eventProcessingBlocks![actionBlockIndex];
                    var input = new ProcessEventsInput(st, messageGroup, actionBlockIndex, this);

                    await eventProcessingBlock.PostAsync(input);
                }

                return BoolResult.Success;
            }
        }

        private int GetProcessingIndex(EventData message)
        {
            var sender = TryGetMessageSender(message);
            if (message == null)
            {
                Counters[MessagesWithoutSenderMachine].Increment();
            }

            sender ??= string.Empty;

            return Math.Abs(sender.GetHashCode()) % _eventProcessingBlocks!.Length;
        }

        private string? TryGetMessageSender(EventData message)
        {
            message.Properties.TryGetValue(SenderMachineEventKey, out var sender);
            return sender?.ToString();
        }

        /// <nodoc />
        protected virtual async Task ProcessEventsCoreAsync(ProcessEventsInput input, ContentLocationEventDataSerializer eventDataSerializer)
        {
            // When shutdown begins, we won't be creating any more checkpoints. This means that all processing that
            // happens after shutdown starts will actually be lost, because whoever takes over processing won't have
            // seen that the current master processed them. Moreover, shutdown will block until we have completed all
            // processing. Here, we force processing to close as soon as possible so as to avoid delaying shutdown.
            if (ShutdownStarted)
            {
                return;
            }

            var context = input.State.Context;
            var counters = input.State.EventStoreCounters;
            var updatedHashesVisitor = input.State.UpdatedHashesVisitor;

            try
            {
                var result = await context.PerformOperationAsync(
                    Tracer,
                    async () =>
                    {
                        foreach (var message in input.Messages)
                        {
                            // Extracting information from the message
                            var foundEpochFilter = message.Properties.TryGetValue(EpochEventKey, out var eventFilter);

                            message.Properties.TryGetValue(OperationIdEventKey, out var operationId);

                            var sender = TryGetMessageSender(message) ?? "Unknown sender";

                            var eventTimeUtc = message.EnqueuedTime.UtcDateTime;
                            var eventProcessingDelay = DateTime.UtcNow - eventTimeUtc;
                            EventQueueDelays[input.EventQueueDelayIndex] = eventProcessingDelay; // Need to check if index is valid and has entry in list

                            // Creating nested context with operationId as a guid. This helps to correlate operations on a worker and a master machines.
                            context = CreateNestedContext(context, operationId?.ToString());

                            Tracer.Info(context, $"{Tracer.Name}.ReceivedEvent: ProcessingDelay={eventProcessingDelay}, Sender={sender}, OpId={operationId}, SeqNo={message.SequenceNumber}, EQT={eventTimeUtc}, Filter={eventFilter}, Size={message.Body.Length}.");

                            Tracer.TrackMetric(context, EventProcessingDelayInSecondsMetricName, (long)eventProcessingDelay.TotalSeconds);

                            counters[ReceivedMessagesTotalSize].Add(message.Body.Length);
                            counters[ReceivedEventBatchCount].Increment();
                            CacheActivityTracker.AddValue(CaSaaSActivityTrackingCounters.ProcessedEventHubMessages, value: 1);

                            if (!foundEpochFilter || !string.Equals(eventFilter as string, _configuration.Epoch))
                            {
                                counters[FilteredEvents].Increment();
                                continue;
                            }

                            // Deserializing a message
                            IReadOnlyList<ContentLocationEventData> eventDatas;

                            using (counters[Deserialization].Start())
                            {
                                eventDatas = eventDataSerializer.DeserializeEvents(context, message);
                            }

                            counters[ReceivedEventsCount].Add(eventDatas.Count);
                            
                            // Dispatching deserialized events data
                            using (counters[DispatchEvents].Start())
                            {
                                foreach (var eventData in eventDatas)
                                {
                                    // An event processor may fail to process the event, but we will save the sequence point anyway.
                                    await DispatchAsync(context, eventData, counters, updatedHashesVisitor);
                                }
                            }
                        }

                        // Create a new result so duration is captured by PerformOperation.
                        return Result.Success(input);
                    },
                    counters[ProcessEvents],
                    extraStartMessage: $"QueueIdx={input.ActionBlockIndex}, QueueSize={input.EventProcessingBlock?.PendingWorkItems}",
                    extraEndMessage: _ => $"QueueIdx={input.ActionBlockIndex}, QueueSize={input.EventProcessingBlock?.PendingWorkItems}, LocalDelay={DateTime.UtcNow - input.LocalEnqueueTime}",
                    isCritical: true);

                Tracer.TrackMetric(context, $"QueueSize_{input.ActionBlockIndex}", input.EventProcessingBlock?.PendingWorkItems ?? 0);

                // The error is logged
                result.IgnoreFailure();
            }
            finally
            {
                // Complete the operation
                input.Complete();
            }
        }

        private static OperationContext CreateNestedContext(OperationContext context, string? operationId, [CallerMemberName]string? caller = null)
        {
            operationId ??= Guid.NewGuid().ToString();
            
            return context.CreateNested(operationId, nameof(EventHubContentLocationEventStore), caller);
        }

        /// <inheritdoc />
        public override EventSequencePoint? GetLastProcessedSequencePoint()
        {
            UpdatingPendingEventProcessingStates();
            return _lastProcessedSequencePoint;
        }

        private void UpdatingPendingEventProcessingStates()
        {
            // Prevent concurrent access to dequeuing from the queue and updating the last processed sequence point
            ConcurrencyHelper.RunOnceIfNeeded(
                ref _updatingPendingEventProcessingStates,
                () =>
                {
                    var pendingEventProcessingStates = _pendingEventProcessingStates;

                    // Look at top event on queue, to see if it is complete, and dequeue and set as last processed event if it is. Otherwise,
                    // just exit.
                    while (pendingEventProcessingStates.TryPeek(out var peekPendingEventProcessingState))
                    {
                        if (peekPendingEventProcessingState.IsComplete)
                        {
                            bool found = pendingEventProcessingStates.TryDequeue(out var pendingEventProcessingState);
                            Contract.Assert(
                                found,
                                "There should be no concurrent access to _pendingEventProcessingStates, so after peek a state should be dequeued.");
                            Contract.Assert(
                                peekPendingEventProcessingState == pendingEventProcessingState,
                                "There should be no concurrent access to _pendingEventProcessingStates, so the state for peek and dequeue should be the same.");

                            _lastProcessedSequencePoint = new EventSequencePoint(pendingEventProcessingState!.SequenceNumber);
                        }
                        else
                        {
                            // Top event batch on queue is not complete, no need to continue.
                            break;
                        }
                    }

                    Volatile.Write(ref _updatingPendingEventProcessingStates, 0);
                });
        }

        /// <inheritdoc />
        protected override BoolResult DoStartProcessing(OperationContext context, EventSequencePoint sequencePoint)
        {
            _pendingEventProcessingStates = new ConcurrentQueue<SharedEventProcessingState>();
            _lastProcessedSequencePoint = sequencePoint;

            _eventHubClient.StartProcessing(context, sequencePoint, _currentEventProcessor).ThrowIfFailure();
            return BoolResult.Success;
        }

        /// <inheritdoc />
        protected override BoolResult DoSuspendProcessing(OperationContext context)
        {
            // TODO: Make these async (bug 1365340)
            _eventHubClient.SuspendProcessing(context).ThrowIfFailure();
            _pendingEventProcessingStates = new ConcurrentQueue<SharedEventProcessingState>();
            return BoolResult.Success;
        }

        private IRetryPolicy CreateEventHubClientRetryPolicy() => RetryPolicyFactory.GetExponentialPolicy(TransientEventHubErrorDetectionStrategy.IsRetryable);

        private class Processor : IPartitionReceiveHandler
        {
            private readonly EventHubContentLocationEventStore _store;
            private readonly OperationContext _context;

            public Processor(OperationContext context, EventHubContentLocationEventStore store)
            {
                _store = store;
                _context = context;

                MaxBatchSize = 100;
            }

            /// <inheritdoc />
            public Task ProcessEventsAsync(IEnumerable<EventData> events)
            {
                return _store.ProcessEventsAsync(_context, events.ToList());
            }

            /// <inheritdoc />
            public Task ProcessErrorAsync(Exception error)
            {
                _store.Tracer.Error(_context, $"EventHubProcessor.ProcessErrorAsync: error=[{error}].");
                return BoolTask.True;
            }

            /// <inheritdoc />
            public int MaxBatchSize { get; set; }
        }

        /// <nodoc />
        protected class SharedEventProcessingState
        {
            private int _remainingMessageCount;
            private readonly StopwatchSlim _stopwatch = StopwatchSlim.Start();

            // Counters are quite large in terms of memory and creating them lazily can save more then 2Gb of memory
            // when the master is busy processing events.
            private readonly Lazy<CounterCollection<ContentLocationEventStoreCounters>> _counters = new Lazy<CounterCollection<ContentLocationEventStoreCounters>>(() => new CounterCollection<ContentLocationEventStoreCounters>());

            /// <nodoc />
            public long SequenceNumber { get; }

            /// <nodoc />
            public OperationContext Context { get; }

            /// <nodoc />
            public EventHubContentLocationEventStore Store { get; }

            /// <nodoc />
            public CounterCollection<ContentLocationEventStoreCounters> EventStoreCounters => _counters.Value;

            /// <nodoc />
            public UpdatedHashesVisitor UpdatedHashesVisitor { get; } = new UpdatedHashesVisitor();

            /// <nodoc />
            public bool IsComplete => _remainingMessageCount == 0;

            /// <nodoc />
            public SharedEventProcessingState(
                OperationContext context,
                EventHubContentLocationEventStore store,
                List<EventData> messages)
            {
                Context = context;
                Store = store;
                SequenceNumber = messages[messages.Count - 1].SequenceNumber;
                _remainingMessageCount = messages.Count;
                store._pendingEventProcessingStates.Enqueue(this);
            }

            /// <nodoc />
            public void Complete(int messageCount, int actionBlockIndex)
            {
                if (Interlocked.Add(ref _remainingMessageCount, -messageCount) == 0)
                {
                    int duration = (int)_stopwatch.Elapsed.TotalMilliseconds;
                    Store.UpdatingPendingEventProcessingStates();
                    Context.LogProcessEventsOverview(EventStoreCounters, duration, actionBlockIndex, UpdatedHashesVisitor);

                    Store.Counters.Append(EventStoreCounters);
                }
            }
        }

        /// <nodoc />
        protected class ProcessEventsInput
        {
            private readonly EventHubContentLocationEventStore _store;

            /// <nodoc />
            public DateTime LocalEnqueueTime { get; } = DateTime.UtcNow;

            /// <nodoc />
            public SharedEventProcessingState State { get; }

            /// <nodoc />
            public IEnumerable<EventData> Messages { get; }

            /// <nodoc />
            public int ActionBlockIndex { get; }

            /// <nodoc />
            public int EventQueueDelayIndex => ActionBlockIndex == -1 ? 0 : ActionBlockIndex;

            /// <nodoc />
            public ActionBlockSlim<ProcessEventsInput>? EventProcessingBlock =>
                ActionBlockIndex != -1 ? _store._eventProcessingBlocks![ActionBlockIndex] : null;

            /// <nodoc />
            public ProcessEventsInput(
                SharedEventProcessingState state,
                IEnumerable<EventData> messages,
                int actionBlockIndex,
                EventHubContentLocationEventStore store)
            {
                State = state;
                Messages = messages;
                ActionBlockIndex = actionBlockIndex;
                _store = store;
                Interlocked.Increment(ref store._queueSize);
            }

            /// <nodoc />
            public void Complete()
            {
                Interlocked.Decrement(ref _store._queueSize);
                State.Complete(Messages.Count(), ActionBlockIndex);
            }
        }

        private static class TransientEventHubErrorDetectionStrategy
        {
            public static bool IsRetryable(Exception? exception)
            {
                if (exception is AggregateException ae)
                {
                    return ae.InnerExceptions.All(e => IsRetryable(e));
                }

                if (exception is TimeoutException || (exception is EventHubsException eh && eh.Reason == EventHubsException.FailureReason.ServiceBusy))
                {
                    return true;
                }

                if (exception is OperationCanceledException)
                {
                    exception = exception.InnerException;
                }

                switch (exception)
                {
                    case null:
                        return false;

                    case EventHubsException ex:
                        return ex.IsTransient;

                    case TimeoutException _:
                    case SocketException _:
                    case IOException _:
                    case UnauthorizedAccessException _:
                        return true;

                    default:
                        return false;
                }
            }
        }
    }
}
