using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MesControlAgv.Mes.Tests;

public sealed class PhysicalExecutionAdmissionBoundaryTests
{
    [Fact]
    public async Task Task_dispatch_retry_and_confirm_pickup_fail_before_lookup_or_adapter_write()
    {
        var adapter = new CountingAgvAdapter();
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var repository = new TaskRepository(new MesDbContext(options));
        var service = new TaskService(repository, adapter, PhysicalProfile(), new PathPlanner(AgvMap.Default),
            admissionPolicy: DisabledPhysicalPolicy());
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<PhysicalExecutionAdmissionException>(() => service.DispatchAsync(id, CancellationToken.None));
        await Assert.ThrowsAsync<PhysicalExecutionAdmissionException>(() => service.RetryAsync(id, CancellationToken.None));
        await Assert.ThrowsAsync<PhysicalExecutionAdmissionException>(() => service.ConfirmPickupAsync(id, "operator", CancellationToken.None));

        Assert.Equal(0, adapter.DispatchCalls);
        Assert.Equal(0, adapter.CancelCalls);
        Assert.Null(await repository.GetAsync(id, CancellationToken.None));
    }

    [Fact]
    public void Physical_policy_reason_priority_is_stable_for_direct_writes()
    {
        var supervisorOff = DisabledPhysicalPolicy();
        var exception = Assert.Throws<PhysicalExecutionAdmissionException>(() =>
            supervisorOff.RejectUnboundPhysicalWrite("test.direct-write"));

        Assert.Equal(PhysicalReadinessReasonCodes.SupervisorDisabled, exception.Code);
        Assert.Contains("readiness supervisor", exception.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static PhysicalExecutionAdmissionPolicy DisabledPhysicalPolicy() =>
        new(PhysicalProfile(), new DisabledReadiness(), NullLogger<PhysicalExecutionAdmissionPolicy>.Instance);

    private static ProfileConfiguration PhysicalProfile() =>
        ProfileConfiguration.Default with
        {
            Features = ProfileConfiguration.Default.Features with { UseSimulator = false }
        };

    private sealed class DisabledReadiness : IPhysicalReadinessState
    {
        public bool Enabled => false;
        public PhysicalReadinessResponse GetSnapshot() => new() { Enabled = false };
        public bool TryGetDevice(string deviceId, out PhysicalDeviceReadinessSnapshot snapshot) { snapshot = null!; return false; }
        public bool IsCurrentAndReady(string deviceId, long? expectedEpoch, out string? reason) { reason = PhysicalReadinessReasonCodes.DeviceNotReady; return false; }
        public bool IsCurrentAndReady(string deviceId, long? expectedEpoch, string? expectedSupervisorInstanceId, out string? reason) { reason = PhysicalReadinessReasonCodes.DeviceNotReady; return false; }
        public bool AcknowledgeAuthorization(
            string deviceId,
            long expectedEpoch,
            string? expectedSupervisorInstanceId) => false;
    }

    private sealed class CountingAgvAdapter : IAgvGateway
    {
        public int DispatchCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public Task<AgvTaskResponse> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken) { DispatchCalls++; throw new InvalidOperationException("Adapter dispatch must not be called."); }
        public Task<AgvTaskResponse?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken) => Task.FromResult<AgvTaskResponse?>(null);
        public Task<AgvTaskResponse?> CancelAsync(Guid operationId, CancellationToken cancellationToken) { CancelCalls++; throw new InvalidOperationException("Adapter cancel must not be called."); }
        public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new AgvSnapshotResponse(false, "none", null, null));
        public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken cancellationToken) => Task.FromResult<AgvTaskResponse?>(null);
    }
}
