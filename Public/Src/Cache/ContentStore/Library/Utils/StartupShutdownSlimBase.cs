﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.ContractsLight;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;

namespace BuildXL.Cache.ContentStore.Utils
{
    /// <summary>
    /// Base implementation of <see cref="IStartupShutdownSlim"/> interface.
    /// </summary>
    public abstract class StartupShutdownSlimBase : IStartupShutdownSlim
    {
        // Tracking instance id to simplify debugging of double shutdown issues.
        private static int CurrentInstanceId;
        private static int GetCurrentInstanceId() => Interlocked.Increment(ref CurrentInstanceId);
        private readonly int _instanceId = GetCurrentInstanceId();

        protected int InstanceId => _instanceId;

        private readonly CancellationTokenSource _shutdownStartedCancellationTokenSource = new CancellationTokenSource();

        /// <nodoc />
        protected abstract Tracer Tracer { get; }

        /// <summary>
        /// Indicates whether the component supports multiple startup and shutdown calls. If true,
        /// component is ref counted (incremented on startup and decremented on shutdown). When the ref count reaches 0,
        /// the component will actually shutdown. NOTE: Multiple or concurrent startup calls are elided into a single execution
        /// of startup where no startup calls will return until the operation is complete.
        /// </summary>
        public virtual bool AllowMultipleStartupAndShutdowns => false;

        /// <summary>
        /// Indicates whether the shutdowns should be traced or not.
        /// </summary>
        public virtual bool TraceShutdown => true;

        private int _refCount = 0;
        private Lazy<Task<BoolResult>>? _lazyStartupTask;

        /// <inheritdoc />
        public virtual bool StartupCompleted { get; private set; }

        /// <inheritdoc />
        public bool StartupStarted { get; private set; }

        /// <inheritdoc />
        public virtual bool ShutdownCompleted { get; private set; }

        /// <nodoc />
        protected virtual string? GetArgumentsMessage()
        {
            return null;
        }

        /// <inheritdoc />
        public bool ShutdownStarted => _shutdownStartedCancellationTokenSource.Token.IsCancellationRequested;

        /// <summary>
        /// Returns a cancellation token that is triggered when <see cref="ShutdownAsync"/> method is called.
        /// </summary>
        public CancellationToken ShutdownStartedCancellationToken => _shutdownStartedCancellationTokenSource.Token;

        /// <summary>
        /// Creates a cancellable operation context with a cancellation token from the context is canceled or shutdown is requested.
        /// </summary>
        protected CancellableOperationContext TrackShutdown(OperationContext context)
            => new CancellableOperationContext(new OperationContext(context, context.Token), ShutdownStartedCancellationToken);

        /// <summary>
        /// Creates a cancellable operation context with a cancellation token that is triggered when a given token is canceled or shutdown is requested.
        /// </summary>
        protected CancellableOperationContext TrackShutdown(Context context, CancellationToken token = default)
            => new CancellableOperationContext(new OperationContext(context, token), ShutdownStartedCancellationToken);

        /// <summary>
        /// Creates a cancellable operation context that allows running the cancellation is triggered via the shutdown or by `context.Token`.
        /// </summary>
        protected CancellableOperationContext TrackShutdownWithDelayedCancellation(OperationContext context, TimeSpan? delay)
        {
            if (delay == null || delay.Value == TimeSpan.Zero)
            {
                return TrackShutdown(context, context.Token);
            }

            return new CancellableOperationContext(context, ShutdownStartedCancellationToken, delay.Value);
        }

        private string GetComponentMessage()
        {
            var argumentMessage = GetArgumentsMessage();
            var idPart = $"Id={_instanceId}.";
            return argumentMessage == null ? idPart : $"{idPart} {argumentMessage}";
        }

        /// <inheritdoc />
        public virtual Task<BoolResult> StartupAsync(Context context)
        {
            if (AllowMultipleStartupAndShutdowns)
            {
                Interlocked.Increment(ref _refCount);
            }
            else 
            {
                Contract.Assert(!StartupStarted, $"Cannot start '{Tracer.Name}' because StartupAsync method was already called on this instance.");
            }
            StartupStarted = true;

            LazyInitializer.EnsureInitialized(ref _lazyStartupTask, () =>
                new Lazy<Task<BoolResult>>(async () =>
                {
                    var operationContext = OperationContext(context);
                    var result = await operationContext.PerformInitializationAsync(
                        Tracer,
                        () => StartupCoreAsync(operationContext),
                        endMessageFactory: r => GetComponentMessage());
                    StartupCompleted = true;

                    return result;
                }));

            return _lazyStartupTask!.Value;
        }

        /// <inheritdoc />
        public async Task<BoolResult> ShutdownAsync(Context context)
        {
            if (AllowMultipleStartupAndShutdowns)
            {
                var refCount = Interlocked.Decrement(ref _refCount);
                if (refCount > 0)
                {
                    return BoolResult.Success;
                }
            }

            Contract.Assert(!ShutdownStarted, $"Cannot shut down '{Tracer.Name}' because ShutdownAsync method was already called on the instance with Id={_instanceId}.");
            TriggerShutdownStarted();

            if (ShutdownCompleted)
            {
                return BoolResult.Success;
            }

            var operationContext = new OperationContext(context);
            var result = await operationContext.PerformOperationAsync(
                Tracer,
                () => ShutdownCoreAsync(operationContext),
                extraEndMessage: r => GetComponentMessage(),
                traceOperationStarted: TraceShutdown,
                traceOperationFinished: TraceShutdown);
            ShutdownCompleted = true;

            return result;
        }

        /// <nodoc />
        protected void TriggerShutdownStarted()
        {
            _shutdownStartedCancellationTokenSource.Cancel();
        }

        /// <summary>
        /// Starts up the service asynchronously after setting some important invariants in the base implementation of <see cref="StartupAsync"/> method.
        /// </summary>
        /// <remarks>
        /// One notable difference between <see cref="StartupAsync"/> is that <paramref name="context"/> already linked to
        /// the instance's lifetime. It means that the <code>context.Token</code> will be triggered on the instance shutdown.
        /// </remarks>
        protected virtual Task<BoolResult> StartupCoreAsync(OperationContext context) => BoolResult.SuccessTask;

        /// <nodoc />
        protected OperationContext OperationContext(Context context)
        {
            return new OperationContext(context, ShutdownStartedCancellationToken);
        }

        /// <summary>
        /// Runs a given function within a newly created operation context.
        /// </summary>
        public async Task<T> WithOperationContext<T>(Context context, CancellationToken token, Func<OperationContext, Task<T>> func)
        {
            using (var operationContext = TrackShutdown(context, token))
            {
                return await func(operationContext);
            }
        }

        /// <nodoc />
        protected virtual Task<BoolResult> ShutdownCoreAsync(OperationContext context) => BoolResult.SuccessTask;

        /// <nodoc />
        protected virtual void ThrowIfInvalid([CallerMemberName]string? operation = null)
        {
            if (!StartupCompleted)
            {
                throw new InvalidOperationException($"The component {Tracer.Name} is not initialized for '{operation}'. Did you forget to call 'Initialize' method?");
            }

            if (ShutdownStarted)
            {
                throw new InvalidOperationException($"The component {Tracer.Name} is shut down for '{operation}'.");
            }
        }
    }
}
