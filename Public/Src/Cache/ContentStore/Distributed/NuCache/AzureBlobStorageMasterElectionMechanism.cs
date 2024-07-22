﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using BuildXL.Cache.ContentStore.Distributed.Blob;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Auth;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.Host.Configuration;
using BuildXL.Utilities.Core.Tasks;
using OperationContext = BuildXL.Cache.ContentStore.Tracing.Internal.OperationContext;

#nullable enable

namespace BuildXL.Cache.ContentStore.Distributed.NuCache
{
    public record AzureBlobStorageMasterElectionMechanismConfiguration()
    {
        public record StorageSettings(IAzureStorageCredentials Credentials, string ContainerName = "checkpoints", string FolderName = "masterElection")
            : AzureBlobStorageFolder.Configuration(Credentials, ContainerName, FolderName);

        public required StorageSettings Storage { get; init; }

        public BlobFolderStorageConfiguration BlobFolderStorageConfiguration { get; set; } = new();

        public string FileName { get; set; } = "master.json";

        public bool IsMasterEligible { get; set; } = true;

        /// <summary>
        /// Maximum lease duration when not refreshing.
        /// </summary>
        /// <remarks>
        /// WARNING: must be longer than the heartbeat interval (<see cref="DistributedContentSettings.HeartbeatIntervalMinutes"/>)
        ///
        /// The value is set to 10m because that's rough worst-case estimate of how long it takes CASaaS to reboot in
        /// highly-loaded production stamps, including offline time (i.e., the maximum tolerated offline time).
        /// </remarks>
        public TimeSpan LeaseExpiryTime { get; set; } = TimeSpan.FromMinutes(10);

        public bool ReleaseLeaseOnShutdown { get; set; } = false;
    }

    public class AzureBlobStorageMasterElectionMechanism : StartupShutdownComponentBase, IMasterElectionMechanism
    {
        protected override Tracer Tracer { get; } = new Tracer(nameof(AzureBlobStorageMasterElectionMechanism));

        private readonly AzureBlobStorageMasterElectionMechanismConfiguration _configuration;
        private readonly MachineLocation _primaryMachineLocation;
        private readonly IClock _clock;

        private readonly BlobStorageClientAdapter _storageClientAdapter;
        private readonly BlobClient _client;

        private MasterElectionState _lastElection = MasterElectionState.DefaultWorker;

        private readonly SemaphoreSlim _roleMutex = TaskUtilities.CreateMutex();

        public AzureBlobStorageMasterElectionMechanism(
            AzureBlobStorageMasterElectionMechanismConfiguration configuration,
            MachineLocation primaryMachineLocation,
            IClock? clock = null)
        {
            _configuration = configuration;
            _primaryMachineLocation = primaryMachineLocation;
            _clock = clock ?? SystemClock.Instance;

            _storageClientAdapter = new BlobStorageClientAdapter(Tracer, _configuration.BlobFolderStorageConfiguration);

            var storageFolder = _configuration.Storage.Create();
            _client = storageFolder.GetBlobClient(new BlobPath(_configuration.FileName, relative: true));
        }

        public MachineLocation Master => _lastElection.Master;

        public Role Role => _lastElection.Role;

        protected override async Task<BoolResult> StartupComponentAsync(OperationContext context)
        {
            await _storageClientAdapter.EnsureContainerExists(context, _client.GetParentBlobContainerClient()).ThrowIfFailureAsync();
            return BoolResult.Success;
        }

        protected override async Task<BoolResult> ShutdownComponentAsync(OperationContext context)
        {
            if (_configuration.ReleaseLeaseOnShutdown)
            {
                await ReleaseRoleIfNecessaryAsync(context).IgnoreFailure();
            }

            return BoolResult.Success;
        }

        public async Task<Result<MasterElectionState>> GetRoleAsync(OperationContext context)
        {
            using var releaser = await _roleMutex.AcquireAsync(context.Token);

            var r = await UpdateRoleAsync(context, tryUpdateLease: TryCreateOrExtendLease);

            if (r.Succeeded)
            {
                _lastElection = r.Value;
            }

            return r;
        }

        public async Task<Result<Role>> ReleaseRoleIfNecessaryAsync(OperationContext context)
        {
            if (!_configuration.IsMasterEligible)
            {
                return Result.Success(Role.Worker);
            }

            using var releaser = await _roleMutex.AcquireAsync(context.Token);

            var r = await UpdateRoleAsync(context, tryUpdateLease: TryReleaseLeaseIfHeld);
            if (r.Succeeded)
            {
                // We don't know who the master is any more
                _lastElection = MasterElectionState.DefaultWorker;
            }

            return Result.Success(Role.Worker);
        }

        private delegate bool TryUpdateLease(MasterLease current, out MasterLease next);

        private class MasterLease
        {
            public MachineLocation Master { get; init; }

            public DateTime CreationTimeUtc { get; init; }

            public DateTime LastUpdateTimeUtc { get; init; }

            public DateTime LeaseExpiryTimeUtc { get; init; }
        }

        private bool TryReleaseLeaseIfHeld(MasterLease current, out MasterLease next)
        {
            next = current;

            var now = _clock.UtcNow;

            bool isMaster = IsCurrentMaster(current);
            if (!isMaster)
            {
                // We can't release a lease we do not hold
                return false;
            }

            if (IsLeaseExpired(current, now))
            {
                // We don't need to release a expired lease
                return false;
            }

            next = new MasterLease
                   {
                       CreationTimeUtc = isMaster ? current!.CreationTimeUtc : now,
                       LastUpdateTimeUtc = now,
                       // The whole point of this method is to basically set the lease expiry time as now
                       LeaseExpiryTimeUtc = now,
                       Master = _primaryMachineLocation,
                   };

            return true;
        }

        private bool TryCreateOrExtendLease(MasterLease current, out MasterLease next)
        {
            next = current;

            if (!_configuration.IsMasterEligible || ShutdownStarted)
            {
                // Not eligible to take master lease.
                return false;
            }

            var now = _clock.UtcNow;
            var isMaster = IsCurrentMaster(current);
            if (!IsLeaseExpired(current, now) && !isMaster)
            {
                // We only want to update the lease if it's either expired, or we are the master machine
                return false;
            }

            next = new MasterLease
                   {
                       CreationTimeUtc = isMaster ? current!.CreationTimeUtc : now,
                       LastUpdateTimeUtc = now,
                       LeaseExpiryTimeUtc = now + _configuration.LeaseExpiryTime,
                       Master = _primaryMachineLocation
                   };

            return true;
        }

        private bool IsLeaseExpired([NotNullWhen(false)] MasterLease? lease, DateTime? now = null)
        {
            return lease == null || lease.LeaseExpiryTimeUtc <= (now ?? _clock.UtcNow);
        }

        private bool IsCurrentMaster([NotNullWhen(true)] MasterLease? lease)
        {
            return lease != null && lease.Master.Equals(_primaryMachineLocation);
        }

        private MasterElectionState GetElectionState(MasterLease lease)
        {
            var master = lease?.Master ?? default(MachineLocation);
            if (IsLeaseExpired(lease))
            {
                // Lease is expired. Lease creator is no longer considerd master machine.
                master = default(MachineLocation);
            }

            return new MasterElectionState(master, master.Equals(_primaryMachineLocation) ? Role.Master : Role.Worker, MasterLeaseExpiryUtc: lease?.LeaseExpiryTimeUtc);
        }

        private Task<Result<MasterElectionState>> UpdateRoleAsync(OperationContext context, TryUpdateLease tryUpdateLease)
        {
            return context.PerformOperationAsync(
                Tracer,
                async () =>
                {
                    var result = await _storageClientAdapter.ReadModifyWriteAsync<MasterLease, MasterLease>(context, _client,
                        current =>
                        {
                            var updated = tryUpdateLease(current, out var next);
                            return (NextState: next, Result: next, Updated: updated);
                        },
                        defaultValue: () => new MasterLease()).ThrowIfFailureAsync();

                    return Result.Success(GetElectionState(result.Result));
                },
                extraEndMessage: r => $"{r!.GetValueOrDefault()} IsMasterEligible=[{_configuration.IsMasterEligible}]");
        }
    }
}
