using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MesControlAgv.Mes.Tests;

public sealed class PhysicalExecutionAdmissionPolicyTests
{
    [Fact]
    public void Simulator_bypasses_both_admission_checks()
    {
        var policy = Create(useSimulator: true, supervisorEnabled: false);

        policy.RequireSupervisedExecution("simulator execute");
        policy.RejectUnboundPhysicalWrite("simulator direct write");
    }

    [Fact]
    public void Physical_supervisor_off_returns_the_existing_disabled_reason()
    {
        var policy = Create(useSimulator: false, supervisorEnabled: false);

        var exception = Assert.Throws<PhysicalExecutionAdmissionException>(
            () => policy.RequireSupervisedExecution("physical execute"));

        Assert.Equal(PhysicalReadinessReasonCodes.SupervisorDisabled, exception.Code);
        Assert.Contains("readiness supervisor", exception.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Direct_physical_write_with_supervisor_on_requires_epoch_authorization()
    {
        var policy = Create(useSimulator: false, supervisorEnabled: true);

        var exception = Assert.Throws<PhysicalExecutionAdmissionException>(
            () => policy.RejectUnboundPhysicalWrite("AGV direct write"));

        Assert.Equal(PhysicalReadinessReasonCodes.EpochAuthorizationRequired, exception.Code);
        Assert.Contains("authorization", exception.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Direct_physical_write_keeps_supervisor_disabled_as_higher_priority()
    {
        var policy = Create(useSimulator: false, supervisorEnabled: false);

        var exception = Assert.Throws<PhysicalExecutionAdmissionException>(
            () => policy.RejectUnboundPhysicalWrite("AGV direct write"));

        Assert.Equal(PhysicalReadinessReasonCodes.SupervisorDisabled, exception.Code);
    }

    private static PhysicalExecutionAdmissionPolicy Create(bool useSimulator, bool supervisorEnabled) =>
        new(
            ProfileConfiguration.Default with
            {
                Features = ProfileConfiguration.Default.Features with { UseSimulator = useSimulator }
            },
            new TestReadinessState(supervisorEnabled),
            NullLogger<PhysicalExecutionAdmissionPolicy>.Instance);

    private sealed class TestReadinessState(bool enabled) : IPhysicalReadinessState
    {
        public bool Enabled { get; } = enabled;

        public PhysicalReadinessResponse GetSnapshot() => new() { Enabled = Enabled };

        public bool TryGetDevice(string deviceId, out PhysicalDeviceReadinessSnapshot snapshot)
        {
            snapshot = null!;
            return false;
        }

        public bool IsCurrentAndReady(string deviceId, long? expectedEpoch, out string? reason)
        {
            reason = PhysicalReadinessReasonCodes.DeviceNotReady;
            return false;
        }

        public bool IsCurrentAndReady(string deviceId, long? expectedEpoch, string? expectedSupervisorInstanceId, out string? reason)
        {
            reason = PhysicalReadinessReasonCodes.DeviceNotReady;
            return false;
        }

        public bool AcknowledgeAuthorization(string deviceId, long expectedEpoch, string? expectedSupervisorInstanceId) => false;
    }
}
