using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Tests;

public sealed class PhysicalReadinessWorkflowAdmissionTests
{
    [Fact]
    public async Task Admission_captures_current_epoch_and_rejects_it_after_reconnect()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new MesDbContext(options);
        await database.Database.EnsureCreatedAsync();

        var profile = ProfileConfiguration.Default;
        var validator = new WorkflowValidator(
            BuiltInWorkflowCatalog.Create(),
            WorkflowPublicationContext.FromProfile(profile));
        var reader = new MesWorkflowVersionReader(database);
        var readiness = ReadyStore();
        var service = new WorkflowApplicationService(
            database,
            reader,
            new WorkflowRuntimeExecutor(
                reader,
                validator,
                admissionPolicies: [new ActiveProfileWorkflowAdmissionPolicy(profile)]),
            validator,
            physicalReadiness: readiness);

        var workflowId = Guid.NewGuid();
        var draft = await service.CreateDraftAsync(
            WorkflowTestDefinitions.CreateMoveWorkflow(workflowId, "SAMPLE_01"),
            "operator",
            CancellationToken.None);
        Assert.True((await service.ValidateVersionAsync(
            workflowId,
            draft.Version,
            CancellationToken.None)).IsValid);
        await service.PublishAsync(workflowId, draft.Version, "operator", CancellationToken.None);

        var request = new WorkflowExecutionRequest
        {
            WorkflowId = workflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "operator",
            PhysicalAuthorization = new WorkflowPhysicalRunAuthorization
            {
                AgvId = "AGV-01",
                OperatorName = "operator",
                SafetyObserverName = "observer",
                PermitPrefix = "epoch-test",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
            }
        };

        var accepted = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.True(accepted.IsAccepted);
        var persisted = await service.GetExecutionRequestAsync(
            accepted.ExecutionId,
            CancellationToken.None);
        var oldEpoch = persisted!.PhysicalAuthorization!.GetDeviceEpoch("AGV-01");
        Assert.True(oldEpoch > 0);
        Assert.Equal(
            readiness.GetSnapshot().SupervisorInstanceId,
            persisted.PhysicalAuthorization.ReadinessSupervisorInstanceId);
        Assert.True(readiness.GetSnapshot().SchedulingPermitted);

        var descriptor = new PhysicalDeviceDescriptor("AGV-01", "agv", true);
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        readiness.Apply(
            descriptor,
            Observation(online: false, now),
            TimeSpan.Zero,
            requireFullPreflight: true,
            now);
        now = now.AddSeconds(1);
        readiness.Apply(
            descriptor,
            Observation(online: true, now),
            TimeSpan.Zero,
            requireFullPreflight: true,
            now);
        var newEpoch = Assert.Single(readiness.GetSnapshot().Devices).DeviceEpoch;
        Assert.NotEqual(oldEpoch, newEpoch);

        var stale = await service.ExecuteAsync(
            request with
            {
                RequestId = Guid.NewGuid(),
                CorrelationId = "stale-epoch",
                PhysicalAuthorization = request.PhysicalAuthorization with
                {
                    PermitPrefix = "stale-epoch",
                    DeviceEpochs = new Dictionary<string, long>
                    {
                        ["AGV-01"] = oldEpoch!.Value
                    }
                }
            },
            CancellationToken.None);

        Assert.False(stale.IsAccepted);
        Assert.Equal(
            WorkflowExecutionRejectionCodes.PhysicalDeviceEpochMismatch,
            stale.RejectionCode);
    }

    private static PhysicalReadinessStateStore ReadyStore()
    {
        var store = new PhysicalReadinessStateStore(instanceId: "workflow-admission-test");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", "agv", true);
        var now = DateTimeOffset.UtcNow;
        store.Configure(true, [descriptor], now);
        store.Apply(
            descriptor,
            Observation(online: true, now),
            TimeSpan.Zero,
            requireFullPreflight: true,
            now);
        return store;
    }

    private static PhysicalDeviceReadinessObservation Observation(
        bool online,
        DateTimeOffset observedAt) => new()
        {
            DeviceId = "AGV-01",
            DeviceFamily = "agv",
            ProbeSucceeded = true,
            Online = online,
            CurrentStationId = online ? "SAMPLE_01" : null,
            ControlOwner = online ? "adapter" : "unknown",
            MapName = "test-map",
            MapVersion = "1",
            MapMd5 = "test-md5",
            VehicleModel = "test",
            IsFullPreflight = true,
            FullPreflightPassed = online,
            BlockingReasons = online ? [] : [PhysicalReadinessReasonCodes.DeviceOffline],
            FullPreflightBlockingReasons = online ? [] : [PhysicalReadinessReasonCodes.DeviceOffline],
            ObservedAtUtc = observedAt,
            FullPreflightObservedAtUtc = observedAt
        };
}
