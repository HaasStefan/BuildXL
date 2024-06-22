// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.


using System.Threading.Tasks;
using BuildXL.Engine.Distribution;
using BuildXL.Utilities;

namespace Test.BuildXL.Distribution
{
    internal sealed class DistributionServiceMock : DistributionService
    {
        public bool Initialized => InitializedCalls > 0;
        public int InitializedCalls;

        public bool Exited => ExitCalls > 0;
        public int ExitCalls;

        public Optional<string> Failure = default;

        public DistributionServiceMock(DistributedInvocationId invocationId) : base(invocationId)
        {
        }

        public override void Dispose() { }

        public override Task<bool> ExitAsync(Optional<string> failure, bool isUnexpected)
        {
            ExitCalls++;
            Failure = failure;
            return Task.FromResult(true);
        }

        public override bool Initialize()
        {
            InitializedCalls++;
            return true;
        }
    }
}