﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;

namespace BuildXL.Cache.ContentStore.Service.Grpc;

/// <nodoc />
public class ResourceWrapperAdapter<T>
    where T : IStartupShutdownSlim
{
    private readonly ResourceWrapper<T> _wrapper;

    /// <nodoc />
    public T Value => _wrapper.Value;

    /// <nodoc />
    public void Invalidate(Context context) => _wrapper.Invalidate(context);

    /// <nodoc />
    public ResourceWrapperAdapter(ResourceWrapper<T> wrapper)
    {
        _wrapper = wrapper;
    }
}

/// <summary>
/// Cache for <see cref="GrpcCopyClient"/>.
/// </summary>
public sealed class GrpcCopyClientCache : IDisposable
{
    private readonly GrpcCopyClientCacheConfiguration _configuration;
    private readonly ByteArrayPool _grpcCopyClientBufferPool;

    private readonly ResourcePool<GrpcCopyClientKey, GrpcCopyClient> _resourcePool;

    /// <summary>
    /// Cache for <see cref="GrpcCopyClient"/>.
    /// </summary>
    public GrpcCopyClientCache(Context context, GrpcCopyClientCacheConfiguration? configuration = null, IClock? clock = null)
    {
        configuration ??= new GrpcCopyClientCacheConfiguration();
        _configuration = configuration;

        _grpcCopyClientBufferPool = new ByteArrayPool(configuration.GrpcCopyClientConfiguration.ClientBufferSizeBytes);

        _resourcePool = new ResourcePool<GrpcCopyClientKey, GrpcCopyClient>(
            context,
            _configuration.ResourcePoolConfiguration,
            (key) => new GrpcCopyClient(context, key, _configuration.GrpcCopyClientConfiguration, sharedBufferPool: _grpcCopyClientBufferPool),
            clock);
    }

    /// <summary>
    /// Use an existing <see cref="GrpcCopyClient"/> if possible, else create a new one.
    /// </summary>
    public Task<TResult> UseWithInvalidationAsync<TResult>(OperationContext context, MachineLocation location, Func<OperationContext, ResourceWrapperAdapter<GrpcCopyClient>, Task<TResult>> operation)
    {
        return _resourcePool.UseAsync(
            context,
            new GrpcCopyClientKey(location),
            async resourceWrapper =>
            {
                // This ensures that the operation we want to perform conforms to the cancellation. When the
                // resource needs to be removed, the token will be cancelled. Once the operation completes, we
                // will be able to proceed with shutdown.
                using var cancellationTokenSource =
                    CancellationTokenSource.CreateLinkedTokenSource(context.Token, resourceWrapper.ShutdownToken);
                var nestedContext = new OperationContext(context, cancellationTokenSource.Token);
                return await operation(nestedContext, new ResourceWrapperAdapter<GrpcCopyClient>(resourceWrapper));
            });
    }

    /// <summary>
    /// Use an existing <see cref="GrpcCopyClient"/> if possible, else create a new one.
    /// </summary>
    public Task<TResult> UseAsync<TResult>(OperationContext context, MachineLocation location, Func<OperationContext, GrpcCopyClient, Task<TResult>> operation)
    {
        return UseWithInvalidationAsync(context, location, (context, adapter) => operation(context, adapter.Value));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _resourcePool?.Dispose();
    }
}
