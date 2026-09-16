using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MesControlAgv.Mes.Tests;

public sealed partial class WorkflowFieldNavigationWorkerTests
{
    // Physical worker paths and the real readiness store, but only in-memory
    // gateways/SQLite. No HTTP client, TCP socket, production DB or controller.
    // Adapter task responses are in-memory, but MES arrival reconciliation uses
    // its actual scoped service. This is not a physical/controller-protocol test.
    [Theory]
    [InlineData("success")]
    [InlineData("arm-unknown")]
    [InlineData("agv-disconnect")]
    [InlineData("arrival-mismatch")]
    [InlineData("arrival-observation-first")]
    [InlineData("arrival-during-preflight")]
    public async Task Offline_homing_cycle_uses_real_readiness_gates_without_device_connections(string scenario)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = new MesDbContext(new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        var clock = new OfflineCycleClock();
        var profile = OfflineCycleProfile();
        var agv = new OfflineCycleAgv(profile, clock);
        var arm = new CompletingArmGateway();
        var store = new PhysicalReadinessStateStore(clock, "offline-cycle");
        var agvDescriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);
        var armDescriptor = new PhysicalDeviceDescriptor("ARM-01", WorkflowDeviceFamilyIds.RobotArm, true, ControlEnabled: true);
        store.Configure(true, [agvDescriptor, armDescriptor], clock.GetUtcNow());

        void ObserveArm(string runtime = "Stopped") => store.Apply(armDescriptor, new PhysicalDeviceReadinessObservation
        {
            DeviceId = "ARM-01", DeviceFamily = WorkflowDeviceFamilyIds.RobotArm,
            ProbeSucceeded = true, Online = true, IsFullPreflight = true, FullPreflightPassed = true,
            RobotMode = "Running", SafetyMode = "Normal", OperationalMode = "Automatic",
            RuntimeState = runtime, LoadedProgram = "回原点", ObservedAtUtc = clock.GetUtcNow()
        }, TimeSpan.FromSeconds(5), true, clock.GetUtcNow());

        async Task ObserveAsync()
        {
            var observation = await new PhysicalAgvReadinessProbe(agv, profile, clock)
                .ProbeAsync(agvDescriptor, true, CancellationToken.None);
            store.Apply(agvDescriptor, observation, TimeSpan.FromSeconds(5), true, clock.GetUtcNow());
            ObserveArm();
        }
        await ObserveAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        await ObserveAsync();
        Assert.All(store.GetSnapshot().Devices, device => Assert.Equal(PhysicalDeviceReadinessState.Ready, device.State));
        var epochs = store.GetSnapshot().Devices.ToDictionary(device => device.DeviceId, device => device.DeviceEpoch);
        foreach (var device in store.GetSnapshot().Devices)
            Assert.True(store.AcknowledgeAuthorization(device.DeviceId, device.DeviceEpoch, "offline-cycle"));

        var validator = new WorkflowValidator(BuiltInWorkflowCatalog.Create(), WorkflowPublicationContext.FromProfile(profile));
        var reader = new MesWorkflowVersionReader(database);
        var workflows = new WorkflowApplicationService(database, reader, new WorkflowRuntimeExecutor(reader, validator),
            validator, timeProvider: clock, physicalReadiness: store);
        var draft = await workflows.CreateDraftAsync(OfflineCycleWorkflow(), "admin", CancellationToken.None);
        var validation = await workflows.ValidateVersionAsync(draft.WorkflowId, draft.Version, CancellationToken.None);
        Assert.True(validation.IsValid, string.Join("; ", validation.Issues.Select(issue => issue.Message)));
        await workflows.PublishAsync(draft.WorkflowId, draft.Version, "admin", CancellationToken.None);
        var run = await workflows.ExecuteAsync(new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId, Version = draft.Version, RequestId = Guid.NewGuid(), RequestedBy = "admin",
            PhysicalAuthorization = new WorkflowPhysicalRunAuthorization
            {
                AgvId = "AGV-01", OperatorName = "admin", SafetyObserverName = "admin",
                PermitPrefix = "offline-only", ExpiresAtUtc = clock.GetUtcNow().AddHours(1),
                DeviceEpochs = epochs, ReadinessSupervisorInstanceId = "offline-cycle"
            }
        }, CancellationToken.None);
        Assert.True(run.IsAccepted, run.RejectionReason);
        var repository = new FieldNavigationAcceptanceRepository(database);
        var services = new ServiceCollection();
        services.AddDbContext<MesDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<FieldNavigationAcceptanceRepository>();
        services.AddSingleton<IAgvGateway>(agv);
        await using var reconciliationProvider = services.BuildServiceProvider();
        using var reconciliation = new FieldNavigationAcceptanceRecoveryService(
            reconciliationProvider.GetRequiredService<IServiceScopeFactory>(), profile,
            NullLogger<FieldNavigationAcceptanceRecoveryService>.Instance);
        var acceptanceService = new FieldNavigationAcceptanceService(repository, agv, profile,
            new PathPlanner(AgvMap.FromProfile(profile.Map)), clock, workflows, store);
        var moveWorker = new WorkflowFieldNavigationDispatcher(workflows, acceptanceService, repository, profile,
            new WorkflowFieldNavigationWorkerOptions
            {
                Enabled = true, AutoAuthorizeFromRunRequest = true, ReadyStabilityWindow = TimeSpan.Zero
            }, timeProvider: clock, agv: agv, physicalReadiness: store);
        var armWorker = new WorkflowAuboProgramDispatcher(workflows, arm, profile,
            new WorkflowAuboProgramWorkerOptions { Enabled = true, CompletionTimeoutMs = 1000, TerminalStabilityWindowMs = 0 },
            timeProvider: clock, physicalReadiness: store);
        arm.OnRun = () => ObserveArm("Running");
        if (scenario == "arm-unknown") arm.RunException = new TimeoutException("in-memory ambiguous run acknowledgement");

        var targets = new[] { "LM7", "LM2", "LM7", "LM1" };
        for (var leg = 0; leg < targets.Length; leg++)
        {
            await moveWorker.ProcessAsync(CancellationToken.None);
            Assert.Equal(leg + 1, agv.Commands.Count);
            var command = agv.Commands[^1];
            Assert.Equal(targets[leg], command.TargetStationId);
            await ObserveAsync();
            Assert.Equal(PhysicalDeviceReadinessState.Blocked,
                store.GetSnapshot().Devices.Single(device => device.DeviceId == "AGV-01").State);
            await moveWorker.ProcessAsync(CancellationToken.None);
            Assert.Equal(WorkflowRuntimeStatus.Running,
                (await workflows.GetExecutionAsync(run.ExecutionId, CancellationToken.None))!.RuntimeStatus);
            if (scenario == "agv-disconnect")
            {
                agv.Online = false;
                await ObserveAsync();
                await moveWorker.ProcessAsync(CancellationToken.None);
                agv.Online = true;
                clock.Advance(TimeSpan.FromSeconds(1));
                await ObserveAsync();
                clock.Advance(TimeSpan.FromSeconds(5));
                await ObserveAsync();
                await moveWorker.ProcessAsync(CancellationToken.None);
                await armWorker.ProcessAsync(CancellationToken.None);
                Assert.Equal(WorkflowRuntimeStatus.Running,
                    (await workflows.GetExecutionAsync(run.ExecutionId, CancellationToken.None))!.RuntimeStatus);
                Assert.Single(agv.Commands);
                Assert.Empty(arm.RunPrograms);
                Assert.Equal(0, agv.ReleaseCalls);
                var reconnected = store.GetSnapshot().Devices.Single(device => device.DeviceId == "AGV-01");
                Assert.True(reconnected.DeviceEpoch > epochs["AGV-01"]);
                Assert.True(reconnected.RequiresReauthorization);
                return;
            }

            var acceptance = (await repository.GetAsync(agv.ActiveTask!.TaskId, CancellationToken.None))!;
            if (scenario == "arrival-during-preflight")
            {
                agv.ArriveOnNextPreflight = true;
                await ObserveAsync();
            }
            else
                agv.Arrive();
            if (scenario is "arrival-observation-first" or "arrival-during-preflight")
            {
                if (scenario == "arrival-observation-first") await ObserveAsync();
                Assert.Equal(FieldNavigationAcceptanceStatuses.Moving, acceptance.Status);
                await moveWorker.ProcessAsync(CancellationToken.None);
                await moveWorker.ProcessAsync(CancellationToken.None);
                var awaitingArrival = (await workflows.GetExecutionAsync(run.ExecutionId, CancellationToken.None))!;
                Assert.True(awaitingArrival.RuntimeStatus == WorkflowRuntimeStatus.Running,
                    $"Leg {leg + 1}: should wait for durable arrival, got {awaitingArrival.RuntimeStatus}: {awaitingArrival.LastError}");
                Assert.Equal(leg + 1, agv.Commands.Count);
                Assert.Equal(leg, arm.RunPrograms.Count);
                Assert.Equal(0, agv.ReleaseCalls);
            }
            if (scenario == "arrival-mismatch")
                agv.TransformTaskRead = task => task with { TargetStationId = "wrong-target" };
            await reconciliation.ReconcileOnceAsync(CancellationToken.None);
            // The hosted reconciler writes in its own DbContext/scope. Refresh
            // this test's long-lived context to observe the committed record.
            await database.Entry(acceptance).ReloadAsync();
            await ObserveAsync();
            if (scenario == "arrival-mismatch")
            {
                Assert.Equal(FieldNavigationAcceptanceStatuses.Unknown, acceptance.Status);
                await moveWorker.ProcessAsync(CancellationToken.None);
                agv.TransformTaskRead = null;
                await reconciliation.ReconcileOnceAsync(CancellationToken.None);
                clock.Advance(TimeSpan.FromSeconds(5));
                await ObserveAsync();
                await moveWorker.ProcessAsync(CancellationToken.None);
                await armWorker.ProcessAsync(CancellationToken.None);
                Assert.Equal(WorkflowRuntimeStatus.Unknown,
                    (await workflows.GetExecutionAsync(run.ExecutionId, CancellationToken.None))!.RuntimeStatus);
                Assert.Single(agv.Commands);
                Assert.Empty(arm.RunPrograms);
                Assert.Equal(0, agv.ReleaseCalls);
                Assert.Single((await repository.ListAuditsAsync(acceptance.Id, CancellationToken.None))
                    .Where(audit => audit.EventType == "AdapterTaskIdentityMismatch"));
                return;
            }
            Assert.Equal(FieldNavigationAcceptanceStatuses.Arrived, acceptance.Status);
            Assert.Single((await repository.ListAuditsAsync(acceptance.Id, CancellationToken.None))
                .Where(audit => audit.EventType == "AdapterStateReconciled"));
            Assert.Equal(PhysicalDeviceReadinessState.Stabilizing,
                store.GetSnapshot().Devices.Single(device => device.DeviceId == "AGV-01").State);
            await moveWorker.ProcessAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(4));
            await ObserveAsync();
            await moveWorker.ProcessAsync(CancellationToken.None);
            var waitingRun = (await workflows.GetExecutionAsync(run.ExecutionId, CancellationToken.None))!;
            var expectedStatus = leg < 3 ? WorkflowRuntimeStatus.Prepared : WorkflowRuntimeStatus.Running;
            Assert.True(waitingRun.RuntimeStatus == expectedStatus,
                $"Leg {leg + 1}: expected {expectedStatus} from the matched arrival, got {waitingRun.RuntimeStatus}: {waitingRun.LastError}");
            Assert.Equal(leg, arm.RunPrograms.Count);
            Assert.Equal(0, agv.ReleaseCalls);
            clock.Advance(TimeSpan.FromSeconds(1));
            await ObserveAsync();
            await moveWorker.ProcessAsync(CancellationToken.None);
            if (leg < 3)
            {
                await armWorker.ProcessAsync(CancellationToken.None);
                await armWorker.ProcessAsync(CancellationToken.None);
                if (scenario == "arm-unknown")
                {
                    await moveWorker.ProcessAsync(CancellationToken.None);
                    Assert.Equal(WorkflowRuntimeStatus.Unknown,
                        (await workflows.GetExecutionAsync(run.ExecutionId, CancellationToken.None))!.RuntimeStatus);
                    Assert.Single(agv.Commands);
                    Assert.Single(arm.RunPrograms);
                    Assert.Equal(0, agv.ReleaseCalls);
                    return;
                }
                Assert.Equal(leg + 1, arm.RunPrograms.Count);
                await ObserveAsync();
            }
        }
        await moveWorker.ProcessAsync(CancellationToken.None);
        await armWorker.ProcessAsync(CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Completed,
            (await workflows.GetExecutionAsync(run.ExecutionId, CancellationToken.None))!.RuntimeStatus);
        Assert.Equal(targets, agv.Commands.Select(command => command.TargetStationId));
        Assert.Equal(new[] { "回原点", "回原点", "回原点" }, arm.RunPrograms);
        Assert.Equal(1, agv.ReleaseCalls);
        var operations = await workflows.ListDeviceOperationsAsync(run.ExecutionId, CancellationToken.None);
        Assert.Equal(7, operations.Count);
        Assert.All(operations, operation => Assert.Equal(WorkflowDeviceOperationStatus.Succeeded, operation.Status));
        Assert.All(store.GetSnapshot().Devices, device => Assert.Equal(epochs[device.DeviceId], device.DeviceEpoch));
        Assert.Equal(4, (await repository.ListForWorkflowRunAsync(run.ExecutionId, CancellationToken.None)).Count);
    }

    private static WorkflowDefinition OfflineCycleWorkflow()
    {
        var nodes = new List<WorkflowNode> { Node(WorkflowNodeType.Start, WorkflowGraphNodeTypeIds.Start, "开始", 1) };
        foreach (var target in new[] { "LM7", "LM2", "LM7", "LM1" })
        {
            nodes.Add(Node(WorkflowNodeType.Move, WorkflowGraphNodeTypeIds.Move, "到 " + target, nodes.Count + 1,
                new Dictionary<string, string?>
                {
                    [WorkflowNodeConfigurationKeys.TargetStation] = target,
                    [WorkflowNodeConfigurationKeys.TimeoutSeconds] = "300",
                    [WorkflowNodeConfigurationKeys.RetryCount] = "0"
                }));
            if (target != "LM1") nodes.Add(Node(WorkflowNodeType.RobotProgram, WorkflowGraphNodeTypeIds.RobotExecuteProgram,
                "回原点.pro", nodes.Count + 1, new Dictionary<string, string?>
                {
                    [WorkflowNodeConfigurationKeys.DeviceId] = "ARM-01",
                    [WorkflowNodeConfigurationKeys.ProgramName] = "回原点.pro"
                }));
        }
        nodes.Add(Node(WorkflowNodeType.End, WorkflowGraphNodeTypeIds.End, "结束", nodes.Count + 1));
        for (var i = 0; i < nodes.Count - 1; i++) nodes[i] = nodes[i] with { NextNodeIds = [nodes[i + 1].Id] };
        return new WorkflowDefinition { Name = "offline four-Move three-homing cycle", Nodes = nodes,
            SchemaVersion = WorkflowGraphDocument.CurrentSchemaVersion, Edges = CreateEdges(nodes.ToArray()) };
    }

    private static ProfileConfiguration OfflineCycleProfile()
    {
        var baseline = CreateProfile();
        var ids = new[] { "LM1", "LM7", "LM2" };
        var edges = new[] { ("LM1", "LM7"), ("LM7", "LM2"), ("LM2", "LM7"), ("LM7", "LM1") };
        return baseline with
        {
            Stations = ids.Select((id, index) => new StationProfile
            { Code = index + 1, StationId = id, AgvStationId = id, Name = id, Type = "Station" }).ToArray(),
            Map = new MapProfile { StationIds = ids, Edges = edges.Select(edge =>
                new MapEdgeProfile { From = edge.Item1, To = edge.Item2, Cost = 1 }).ToArray() },
            PhysicalAcceptance = baseline.PhysicalAcceptance! with
            {
                MapSnapshot = baseline.PhysicalAcceptance.MapSnapshot with
                { StationIds = ids, DirectedEdges = edges.Select(edge => new DirectedMapEdgeProfile { From = edge.Item1, To = edge.Item2 }).ToArray() }
            }
        };
    }

    private sealed class OfflineCycleClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }

    private sealed class OfflineCycleAgv(ProfileConfiguration profile, TimeProvider clock)
        : IAgvGateway, IFieldNavigationAcceptanceGateway, IPhysicalAgvControlGateway
    {
        public List<FieldNavigationDispatchCommand> Commands { get; } = [];
        public AgvTaskResponse? ActiveTask { get; private set; }
        public bool Online { get; set; } = true;
        public int ReleaseCalls { get; private set; }
        public Func<AgvTaskResponse, AgvTaskResponse>? TransformTaskRead { get; set; }
        public bool ArriveOnNextPreflight { get; set; }
        private readonly Dictionary<Guid, AgvTaskResponse> _tasks = [];
        private string _station = "LM1";
        private string _owner = "adapter";
        public void Arrive()
        {
            _station = ActiveTask!.TargetStationId;
            _tasks[ActiveTask.TaskId] = ActiveTask with { State = "arrived" };
            ActiveTask = null;
        }
        public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgvSnapshotResponse(Online, _owner, _station, ActiveTask?.TaskId, "AGV-01"));
        public async Task<PhysicalAgvPreflightResponse> GetPhysicalPreflightAsync(CancellationToken cancellationToken)
        {
            if (ArriveOnNextPreflight)
            {
                ArriveOnNextPreflight = false;
                Arrive();
            }
            var map = profile.PhysicalAcceptance!.MapSnapshot;
            var safety = new AgvSafetyReadinessResponse("automatic", "offline-fake", map.MapName, map.Md5,
                true, 0, false, false, false, false, 0, 0, 1, 0.99, clock.GetUtcNow(), "physical-test", "offline-1");
            var reasons = new List<string>();
            if (!Online) reasons.Add("agv_offline");
            if (ActiveTask is not null) reasons.Add("agv_has_active_task");
            return new PhysicalAgvPreflightResponse(await GetSnapshotAsync(cancellationToken), safety, reasons.Count == 0, reasons,
                new ControllerMapEvidenceResponse(true, "offline-in-memory", map.MapName, map.Version, map.Md5,
                    map.StationIds, map.DirectedEdges.Select(edge => new ControllerDirectedEdgeResponse(edge.From, edge.To)).ToArray(), map.CapturedAtUtc));
        }
        public Task<AgvTaskResponse> DispatchFieldNavigationAcceptanceAsync(Guid id, FieldNavigationDispatchCommand command, CancellationToken cancellationToken)
        {
            Assert.Null(ActiveTask);
            Assert.Equal(_station, command.SourceStationId);
            Commands.Add(command);
            ActiveTask = new AgvTaskResponse(id, id.ToString("N"), command.TargetStationId, "moving", null, "AGV-01", command.PlannedPath);
            _tasks[id] = ActiveTask;
            return Task.FromResult(ActiveTask);
        }
        public Task<AgvTaskResponse?> GetTaskAsync(Guid id, CancellationToken cancellationToken)
        {
            var task = _tasks.GetValueOrDefault(id);
            return Task.FromResult(task is not null && TransformTaskRead is not null ? TransformTaskRead(task) : task);
        }
        public Task<AgvTaskResponse> DispatchAsync(Guid id, string target, CancellationToken cancellationToken) => throw new NotSupportedException("No legacy dispatch in this test");
        public Task<AgvTaskResponse?> CancelAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException("No automatic cancellation");
        public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("No generic device commands in this offline cycle");
        public Task<bool> ReleaseControlAsync(CancellationToken cancellationToken)
        { Assert.Null(ActiveTask); ReleaseCalls++; _owner = "none"; return Task.FromResult(true); }
    }
}
