using System.Collections.Concurrent;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Tests;

public sealed class PhysicalSafetyActionServiceTests
{
    [Fact]
    public async Task Prepared_actions_are_reconciled_to_unknown_without_calling_a_gateway()
    {
        await using var database = CreateDatabase();
        var record = new PhysicalSafetyActionRecord
        {
            RequestId = Guid.NewGuid(),
            Fingerprint = "fingerprint-1",
            ActionType = PhysicalSafetyActionTypes.AgvRelease,
            DeviceId = "AGV-01",
            OperatorName = "operator-a",
            Reason = "startup recovery",
            Status = PhysicalSafetyActionStatuses.Prepared
        };
        database.PhysicalSafetyActions.Add(record);
        await database.SaveChangesAsync();

        var gateway = new SafetyActionAgvGateway();
        var service = new PhysicalSafetyActionService(database, gateway, null, PhysicalProfile());

        await service.ReconcilePreparedAsync(CancellationToken.None);

        var reconciled = await database.PhysicalSafetyActions.SingleAsync();
        Assert.Equal(PhysicalSafetyActionStatuses.Unknown, reconciled.Status);
        Assert.Equal(0, gateway.ReleaseCalls);
    }

    [Fact]
    public async Task Agv_release_preflights_identity_owner_and_active_task_before_one_write()
    {
        await using var database = CreateDatabase();
        var gateway = new SafetyActionAgvGateway
        {
            Snapshot = new AgvSnapshotResponse(true, "other-client", "SAMPLE_01", null, "AGV-01")
        };
        var service = new PhysicalSafetyActionService(database, gateway, null, PhysicalProfile());

        var result = await service.ReleaseAgvAsync(
            "AGV-01",
            new PhysicalAgvReleaseRequest(Guid.NewGuid(), "operator-a", "manual release"),
            CancellationToken.None);

        Assert.Equal(PhysicalSafetyActionStatuses.Rejected, result.Status);
        Assert.Equal(0, gateway.ReleaseCalls);
        Assert.Contains("owner", result.ResultSummary!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agv_release_is_one_shot_and_same_request_replays_the_stored_result()
    {
        await using var database = CreateDatabase();
        var gateway = new SafetyActionAgvGateway();
        var service = new PhysicalSafetyActionService(database, gateway, null, PhysicalProfile());
        var requestId = Guid.NewGuid();
        var request = new PhysicalAgvReleaseRequest(requestId, "operator-a", "manual release");

        var first = await service.ReleaseAgvAsync("AGV-01", request, CancellationToken.None);
        var second = await service.ReleaseAgvAsync("AGV-01", request, CancellationToken.None);

        Assert.Equal(PhysicalSafetyActionStatuses.Succeeded, first.Status);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(1, gateway.ReleaseCalls);
    }

    [Fact]
    public async Task Final_move_release_rejects_non_normal_move_outcome_without_gateway_write()
    {
        await using var database = CreateDatabase();
        var gateway = new SafetyActionAgvGateway();
        var readiness = new AlwaysReadyPhysicalReadinessState();
        var service = new PhysicalSafetyActionService(
            database, gateway, null, PhysicalProfile(), readiness);
        var runId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        var result = await service.ReleaseFinalMoveAsync(
            runId,
            nodeId,
            operationId,
            "AGV-01",
            "operator-a",
            1,
            "supervisor-1",
            WorkflowStepCompletionOutcome.Cancelled,
            CancellationToken.None);

        Assert.Equal(PhysicalSafetyActionStatuses.Rejected, result.Status);
        Assert.Contains("verified normal Move terminal outcome", result.ResultSummary!);
        Assert.Equal(0, gateway.ReleaseCalls);
        Assert.Equal(PhysicalSafetyActionStatuses.Rejected,
            (await database.PhysicalSafetyActions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Concurrent_same_request_ids_have_one_deterministic_gateway_write()
    {
        await using var database = CreateDatabase();
        var gateway = new SafetyActionAgvGateway { ReleaseDelay = TimeSpan.FromMilliseconds(20) };
        var service = new PhysicalSafetyActionService(database, gateway, null, PhysicalProfile());
        var request = new PhysicalAgvReleaseRequest(Guid.NewGuid(), "operator-a", "manual release");

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            service.ReleaseAgvAsync("AGV-01", request, CancellationToken.None)));

        Assert.All(results, result => Assert.Equal(PhysicalSafetyActionStatuses.Succeeded, result.Status));
        Assert.Single(results.Select(result => result.Id).Distinct());
        Assert.Equal(1, gateway.ReleaseCalls);
    }

    [Fact]
    public async Task Aubo_stop_reads_fresh_status_passes_operation_and_correlation_and_records_success()
    {
        await using var database = CreateDatabase();
        var gateway = new SafetyActionAuboGateway();
        var service = new PhysicalSafetyActionService(database, null, gateway, PhysicalProfile());
        var operationId = Guid.NewGuid();
        var correlation = AuboArmOperationCorrelation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "workflow-correlation");

        var result = await service.StopAuboAsync(
            "AUBO-01",
            new PhysicalAuboStopRequest(Guid.NewGuid(), operationId, "operator-a", correlation),
            CancellationToken.None);

        Assert.Equal(PhysicalSafetyActionStatuses.Succeeded, result.Status);
        Assert.Equal(["status", "stop"], gateway.Calls);
        Assert.Equal(operationId, gateway.OperationId);
        Assert.Equal(correlation.EffectiveCorrelationId, gateway.Correlation?.EffectiveCorrelationId);
    }

    [Fact]
    public async Task Aubo_stop_write_exception_is_unknown_and_is_never_replayed()
    {
        await using var database = CreateDatabase();
        var gateway = new SafetyActionAuboGateway { StopException = new TimeoutException("ambiguous") };
        var service = new PhysicalSafetyActionService(database, null, gateway, PhysicalProfile());
        var request = new PhysicalAuboStopRequest(Guid.NewGuid(), Guid.NewGuid(), "operator-a");

        var first = await service.StopAuboAsync("AUBO-01", request, CancellationToken.None);
        var second = await service.StopAuboAsync("AUBO-01", request, CancellationToken.None);

        Assert.Equal(PhysicalSafetyActionStatuses.Unknown, first.Status);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, gateway.StopCalls);
    }

    [Fact]
    public async Task Aubo_stop_requires_an_explicit_operation_id_and_operator()
    {
        await using var database = CreateDatabase();
        var gateway = new SafetyActionAuboGateway();
        var service = new PhysicalSafetyActionService(database, null, gateway, PhysicalProfile());

        await Assert.ThrowsAsync<ArgumentException>(() => service.StopAuboAsync(
            "AUBO-01",
            new PhysicalAuboStopRequest(Guid.NewGuid(), Guid.Empty, "operator-a"),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.StopAuboAsync(
            "AUBO-01",
            new PhysicalAuboStopRequest(Guid.NewGuid(), Guid.NewGuid(), " "),
            CancellationToken.None));
        Assert.Empty(gateway.Calls);
    }

    private static MesDbContext CreateDatabase() => new(
        new DbContextOptionsBuilder<MesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ProfileConfiguration PhysicalProfile() => ProfileConfiguration.Default with
    {
        Features = ProfileConfiguration.Default.Features with { UseSimulator = false },
        PhysicalAcceptance = new PhysicalAcceptanceProfile
        {
            ExpectedControlOwner = "adapter",
            MapSnapshot = new ControllerMapSnapshot { MapName = "test", Version = "1", Md5 = "test" },
            Safety = new PhysicalAgvSafetyProfile()
        }
    };

    private sealed class SafetyActionAgvGateway : IAgvGateway, IPhysicalAgvControlGateway
    {
        public AgvSnapshotResponse Snapshot { get; init; } =
            new(true, "adapter", "SAMPLE_01", null, "AGV-01");
        public TimeSpan ReleaseDelay { get; init; }
        public int ReleaseCalls { get; private set; }
        public Task<AgvTaskResponse> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgvTaskResponse?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken) => Task.FromResult<AgvTaskResponse?>(null);
        public Task<AgvTaskResponse?> CancelAsync(Guid operationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
        public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task<bool> ReleaseControlAsync(CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            if (ReleaseDelay > TimeSpan.Zero) await Task.Delay(ReleaseDelay, cancellationToken);
            return true;
        }
    }

    private sealed class SafetyActionAuboGateway : IAuboArmProgramGateway
    {
        public List<string> Calls { get; } = [];
        public int StopCalls { get; private set; }
        public Guid OperationId { get; private set; }
        public AuboArmOperationCorrelation? Correlation { get; private set; }
        public Exception? StopException { get; init; }

        public Task<AuboArmProgramStatusResponse> GetProgramAsync(string deviceId, CancellationToken cancellationToken)
        {
            Calls.Add("status");
            return Task.FromResult(new AuboArmProgramStatusResponse(
                deviceId, true, "program.lua", AuboArmRuntimeState.Running, "Running", DateTimeOffset.UtcNow));
        }

        public Task<AuboArmProgramOperationResponse> StopProgramAsync(
            string deviceId,
            string operatorName,
            Guid operationId,
            AuboArmOperationCorrelation? correlation,
            CancellationToken cancellationToken)
        {
            Calls.Add("stop");
            StopCalls++;
            OperationId = operationId;
            Correlation = correlation;
            if (StopException is not null) return Task.FromException<AuboArmProgramOperationResponse>(StopException);
            return Task.FromResult(new AuboArmProgramOperationResponse(
                operationId, deviceId, "program.lua", "stop", operatorName,
                AuboArmProgramOperationState.Stopped, AuboArmRuntimeState.Stopped,
                "Stopped", "program.lua", 0, null, true, DateTimeOffset.UtcNow));
        }
    }

    private sealed class AlwaysReadyPhysicalReadinessState : IPhysicalReadinessState
    {
        public bool Enabled => true;

        public PhysicalReadinessResponse GetSnapshot() => new()
        {
            Enabled = true,
            SupervisorInstanceId = "supervisor-1",
            Devices =
            [
                new PhysicalDeviceReadinessSnapshot
                {
                    DeviceId = "AGV-01",
                    DeviceEpoch = 1,
                    State = PhysicalDeviceReadinessState.Ready,
                    RequiresReauthorization = false
                }
            ]
        };

        public bool TryGetDevice(string deviceId, out PhysicalDeviceReadinessSnapshot snapshot)
        {
            snapshot = GetSnapshot().Devices.Single();
            return deviceId == "AGV-01";
        }

        public bool IsCurrentAndReady(string deviceId, long? expectedEpoch, out string? validationReason) =>
            IsCurrentAndReady(deviceId, expectedEpoch, null, out validationReason);

        public bool IsCurrentAndReady(
            string deviceId,
            long? expectedEpoch,
            string? expectedSupervisorInstanceId,
            out string? validationReason)
        {
            validationReason = null;
            return deviceId == "AGV-01" &&
                (!expectedEpoch.HasValue || expectedEpoch == 1) &&
                (string.IsNullOrWhiteSpace(expectedSupervisorInstanceId) || expectedSupervisorInstanceId == "supervisor-1");
        }

        public bool AcknowledgeAuthorization(string deviceId, long expectedEpoch) => false;
    }
}
