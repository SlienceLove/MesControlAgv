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

public sealed partial class WorkflowAuboProgramWorkerTests
{
    // Physical dispatcher and real readiness state, with only an in-memory
    // gateway/database. The gateway advances the observation clock explicitly;
    // no wall-clock timing assertion or controller connection is involved.
    [Theory]
    [InlineData("healthy", 3, true)]
    [InlineData("read-error", 5, true)]
    [InlineData("offline", 5, true)]
    [InlineData("unknown", 5, true)]
    [InlineData("permanent-read-error", 4, false)]
    [InlineData("paused-stopped", 3, false)]
    [InlineData("paused-running-stopped", 6, true)]
    [InlineData("safety-stopped", 2, false)]
    [InlineData("wrong-program", 3, false)]
    [InlineData("wrong-device", 2, false)]
    [InlineData("readiness-probe-outage", 3, true)]
    public async Task Physical_homing_completion_requires_continuous_observed_stopped_without_replaying_commands(
        string scenario, int expectedObservations, bool succeeds)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = new MesDbContext(new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var profile = CreateProfile() with { Features = new FeatureFlags { UseSimulator = false } };
        var state = new PhysicalReadinessStateStore(clock, "offline-arm-completion");
        var descriptor = new PhysicalDeviceDescriptor("ARM-01", WorkflowDeviceFamilyIds.RobotArm, true, ControlEnabled: true);
        var agvDescriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);
        state.Configure(true, [descriptor, agvDescriptor], clock.GetUtcNow());
        state.Apply(descriptor, ReadyArmObservation(clock.GetUtcNow()), TimeSpan.Zero, true, clock.GetUtcNow());
        state.Apply(agvDescriptor, new PhysicalDeviceReadinessObservation
        {
            DeviceId = "AGV-01", DeviceFamily = WorkflowDeviceFamilyIds.Agv, Online = true, ProbeSucceeded = true,
            CurrentStationId = "LM1", IsFullPreflight = true, FullPreflightPassed = true,
            ObservedAtUtc = clock.GetUtcNow(), FullPreflightObservedAtUtc = clock.GetUtcNow()
        }, TimeSpan.Zero, true, clock.GetUtcNow());
        var epochs = state.GetSnapshot().Devices.ToDictionary(device => device.DeviceId, device => device.DeviceEpoch);
        foreach (var device in state.GetSnapshot().Devices)
            Assert.True(state.AcknowledgeAuthorization(device.DeviceId, device.DeviceEpoch, state.GetSnapshot().SupervisorInstanceId));

        var validator = new WorkflowValidator(BuiltInWorkflowCatalog.Create(), WorkflowPublicationContext.FromProfile(profile));
        var reader = new MesWorkflowVersionReader(database);
        var workflows = new WorkflowApplicationService(database, reader, new WorkflowRuntimeExecutor(reader, validator),
            validator, timeProvider: clock, physicalReadiness: state);
        var definition = CreateRobotOnlyWorkflow();
        definition = definition with
        {
            Nodes = definition.Nodes.Select(node => node.Type == WorkflowNodeType.RobotProgram
                ? node with { Configuration = new Dictionary<string, string?>
                    {
                        [WorkflowNodeConfigurationKeys.DeviceId] = "ARM-01",
                        [WorkflowNodeConfigurationKeys.ProgramName] = "回原点.pro"
                    } }
                : node).ToArray()
        };
        var draft = await workflows.CreateDraftAsync(definition, "admin", CancellationToken.None);
        Assert.True((await workflows.ValidateVersionAsync(draft.WorkflowId, draft.Version, CancellationToken.None)).IsValid);
        await workflows.PublishAsync(draft.WorkflowId, draft.Version, "admin", CancellationToken.None);
        var run = await workflows.ExecuteAsync(new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId, Version = draft.Version, RequestId = Guid.NewGuid(), RequestedBy = "admin",
            PhysicalAuthorization = new WorkflowPhysicalRunAuthorization
            {
                AgvId = "AGV-01", OperatorName = "admin", SafetyObserverName = "admin", PermitPrefix = "offline-arm-only",
                ExpiresAtUtc = clock.GetUtcNow().AddHours(1), DeviceEpochs = epochs,
                ReadinessSupervisorInstanceId = state.GetSnapshot().SupervisorInstanceId
            }
        }, CancellationToken.None);
        Assert.True(run.IsAccepted, run.RejectionReason);

        var observations = 0;
        var arm = new RecordingArmGateway
        {
            ObserveAfterRun = () =>
            {
                observations++;
                if (observations > 1) clock.Advance(TimeSpan.FromMilliseconds(600));
                // Observer epoch is not a task receipt. Its failed probe must
                // not override the direct, healthy program status below.
                if (scenario == "readiness-probe-outage" && observations == 2)
                    state.Apply(descriptor, ReadyArmObservation(clock.GetUtcNow()) with { Online = false },
                        TimeSpan.Zero, true, clock.GetUtcNow());
                if ((scenario == "read-error" && observations == 2) ||
                    (scenario == "permanent-read-error" && observations >= 2))
                    throw new HttpRequestException("in-memory observation outage");
                var runtime = observations == 2 ? scenario switch
                {
                    "unknown" => AuboArmRuntimeState.Unknown,
                    "paused-stopped" or "paused-running-stopped" => AuboArmRuntimeState.Paused,
                    _ => AuboArmRuntimeState.Stopped
                } : AuboArmRuntimeState.Stopped;
                if (scenario == "paused-running-stopped" && observations == 3) runtime = AuboArmRuntimeState.Running;
                return new AuboArmProgramStatusResponse(scenario == "wrong-device" && observations == 2 ? "ARM-OTHER" : "ARM-01", !(scenario == "offline" && observations == 2),
                    scenario == "wrong-program" && observations == 3 ? "another-program" : "回原点",
                    runtime, runtime.ToString(), clock.GetUtcNow())
                {
                    RobotMode = AuboArmMode.Running,
                    SafetyMode = scenario == "safety-stopped" && observations == 2 ? AuboArmSafetyMode.ProtectiveStop : AuboArmSafetyMode.Normal,
                    OperationalMode = AuboArmOperationalMode.Automatic, ControlEnabled = true
                };
            }
        };
        var worker = new WorkflowAuboProgramDispatcher(workflows, arm, profile,
            new WorkflowAuboProgramWorkerOptions
            {
                Enabled = true, PollIntervalMs = 50, CompletionTimeoutMs = 5000,
                StatusReadRetryWindowMs = 1000, TerminalStabilityWindowMs = 1000
            }, timeProvider: clock, physicalReadiness: state);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.ProcessAsync(cancellation.Token);
        await worker.ProcessAsync(cancellation.Token);

        Assert.Equal(expectedObservations, observations);
        Assert.Equal(new[] { "回原点" }, arm.RunPrograms);
        Assert.Equal(Assert.Single(arm.LoadOperationIds), Assert.Single(arm.RunOperationIds));
        var final = (await workflows.GetExecutionAsync(run.ExecutionId, CancellationToken.None))!;
        Assert.Equal(succeeds ? WorkflowRuntimeStatus.Completed : WorkflowRuntimeStatus.Unknown, final.RuntimeStatus);
        var operation = Assert.Single(await workflows.ListDeviceOperationsAsync(run.ExecutionId, CancellationToken.None));
        Assert.Equal(succeeds ? WorkflowDeviceOperationStatus.Succeeded : WorkflowDeviceOperationStatus.Unknown, operation.Status);
        if (succeeds)
        {
            var node = Assert.Single(await workflows.ListNodeExecutionsAsync(run.ExecutionId, CancellationToken.None));
            Assert.Equal("回原点", node.Outputs[WorkflowNodeConfigurationKeys.ProgramName]);
            Assert.Equal(clock.GetUtcNow(), DateTimeOffset.Parse(node.Outputs["completedAtUtc"]!));
        }
        else Assert.NotNull(final.LastError);
    }
}
