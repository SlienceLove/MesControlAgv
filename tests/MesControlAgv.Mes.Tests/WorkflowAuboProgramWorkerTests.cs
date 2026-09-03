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

public sealed class WorkflowAuboProgramWorkerTests
{
    [Fact]
    public async Task Workflow_runs_robot_programs_once_in_station_order()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options;
        await using var database = new MesDbContext(dbOptions);
        await database.Database.EnsureCreatedAsync();

        var profile = CreateProfile();
        var validator = new WorkflowValidator(
            BuiltInWorkflowCatalog.Create(),
            WorkflowPublicationContext.FromProfile(profile));
        var reader = new MesWorkflowVersionReader(database);
        var workflows = new WorkflowApplicationService(
            database,
            reader,
            new WorkflowRuntimeExecutor(
                reader,
                validator,
                admissionPolicies: [new ActiveProfileWorkflowAdmissionPolicy(profile)]),
            validator);
        var definition = CreateWorkflow();
        var draft = await workflows.CreateDraftAsync(definition, "test", CancellationToken.None);
        var validation = await workflows.ValidateVersionAsync(draft.WorkflowId, draft.Version, CancellationToken.None);
        Assert.True(validation.IsValid, string.Join("; ", validation.Issues.Select(issue => issue.Message)));
        await workflows.PublishAsync(draft.WorkflowId, draft.Version, "test", CancellationToken.None);
        var execution = await workflows.ExecuteAsync(new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "test"
        }, CancellationToken.None);
        Assert.True(execution.IsAccepted);

        var agv = new ImmediateArrivalAgvGateway();
        var arm = new RecordingArmGateway { FailOneObservationRead = true };
        var sim = new WorkflowSimulatorDispatcher(
            workflows,
            agv,
            profile,
            new WorkflowSimulatorWorkerOptions { Enabled = true });
        var aubo = new WorkflowAuboProgramDispatcher(
            workflows,
            arm,
            profile,
            new WorkflowAuboProgramWorkerOptions
            {
                Enabled = true,
                PollIntervalMs = 50,
                CompletionTimeoutMs = 1000
            });

        for (var index = 0; index < 8; index++)
        {
            await sim.ProcessAsync(CancellationToken.None);
            await aubo.ProcessAsync(CancellationToken.None);
        }

        var final = await workflows.GetExecutionAsync(execution.ExecutionId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Completed, final!.RuntimeStatus);
        Assert.Equal(["LM1", "LM4"], agv.Targets);
        Assert.Equal(["测试1", "测试2"], arm.RunPrograms);
        Assert.Equal(2, arm.RunCalls);
        Assert.Equal(1, arm.FailedObservationReads);
        Assert.All(
            await workflows.ListDeviceOperationsAsync(execution.ExecutionId, CancellationToken.None),
            operation => Assert.Equal(WorkflowDeviceOperationStatus.Succeeded, operation.Status));
    }

    [Fact]
    public async Task Worker_does_not_dispatch_when_profile_control_is_disabled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options;
        await using var database = new MesDbContext(dbOptions);
        await database.Database.EnsureCreatedAsync();
        var profile = CreateProfile() with
        {
            WorkflowDevices = [CreateProfile().WorkflowDevices[0] with { ControlEnabled = false }]
        };
        var validator = new WorkflowValidator();
        var reader = new MesWorkflowVersionReader(database);
        var workflows = new WorkflowApplicationService(
            database,
            reader,
            new WorkflowRuntimeExecutor(reader, validator),
            validator);
        var arm = new RecordingArmGateway();
        var dispatcher = new WorkflowAuboProgramDispatcher(
            workflows,
            arm,
            profile,
            new WorkflowAuboProgramWorkerOptions { Enabled = true });

        await dispatcher.ProcessAsync(CancellationToken.None);

        Assert.Empty(arm.RunPrograms);
    }

    [Fact]
    public async Task Restarted_stopped_robot_program_is_unknown_until_operator_reconciles()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options;
        await using var database = new MesDbContext(dbOptions);
        await database.Database.EnsureCreatedAsync();

        var profile = CreateProfile();
        var validator = new WorkflowValidator(
            BuiltInWorkflowCatalog.Create(),
            WorkflowPublicationContext.FromProfile(profile));
        var reader = new MesWorkflowVersionReader(database);
        var workflows = new WorkflowApplicationService(
            database,
            reader,
            new WorkflowRuntimeExecutor(
                reader,
                validator,
                admissionPolicies: [new ActiveProfileWorkflowAdmissionPolicy(profile)]),
            validator);
        var draft = await workflows.CreateDraftAsync(CreateWorkflow(), "test", CancellationToken.None);
        Assert.True((await workflows.ValidateVersionAsync(draft.WorkflowId, draft.Version, CancellationToken.None)).IsValid);
        await workflows.PublishAsync(draft.WorkflowId, draft.Version, "test", CancellationToken.None);
        var execution = await workflows.ExecuteAsync(new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "test"
        }, CancellationToken.None);
        Assert.True(execution.IsAccepted);

        // Advance the first Move node in the simulator, then claim the first
        // robot node to model a process restart after a possible AUBO write.
        var simulator = new WorkflowSimulatorDispatcher(
            workflows,
            new ImmediateArrivalAgvGateway(),
            profile,
            new WorkflowSimulatorWorkerOptions { Enabled = true });
        await simulator.ProcessAsync(CancellationToken.None);
        var robot = Assert.Single(await workflows.ListAuboProgramDispatchableNodesAsync(CancellationToken.None));
        var claimed = await workflows.ClaimNodeExecutionAsync(robot.NodeExecution.Id, CancellationToken.None);
        Assert.NotNull(claimed.DeviceOperation);

        var dispatcher = new WorkflowAuboProgramDispatcher(
            workflows,
            new RecordingArmGateway(),
            profile,
            new WorkflowAuboProgramWorkerOptions { Enabled = true });
        await dispatcher.RecoverAsync(CancellationToken.None);

        var snapshot = await workflows.GetExecutionAsync(execution.ExecutionId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Unknown, snapshot!.RuntimeStatus);
        var node = (await workflows.ListNodeExecutionsAsync(execution.ExecutionId, CancellationToken.None))
            .Single(item => item.Id == robot.NodeExecution.Id);
        Assert.Equal(WorkflowNodeExecutionStatus.Unknown, node.Status);
        Assert.Contains("completion cannot be proven", node.LastError, StringComparison.OrdinalIgnoreCase);

        var resumedExecution = await workflows.ExecuteAsync(new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "test"
        }, CancellationToken.None);
        await simulator.ProcessAsync(CancellationToken.None);
        var resumedRobot = (await workflows.ListAuboProgramDispatchableNodesAsync(CancellationToken.None))
            .Single(item => item.NodeExecution.WorkflowRunId == resumedExecution.ExecutionId);
        await workflows.ClaimNodeExecutionAsync(resumedRobot.NodeExecution.Id, CancellationToken.None);
        var recoveringArm = new RecordingArmGateway();
        recoveringArm.ProgramStatuses.Enqueue(new AuboArmProgramStatusResponse(
            "ARM-01", true, "测试1", AuboArmRuntimeState.Running, "Running", DateTimeOffset.UtcNow));
        recoveringArm.ProgramStatuses.Enqueue(new AuboArmProgramStatusResponse(
            "ARM-01", true, "测试1", AuboArmRuntimeState.Stopped, "Stopped", DateTimeOffset.UtcNow));
        var recoveringDispatcher = new WorkflowAuboProgramDispatcher(
            workflows,
            recoveringArm,
            profile,
            new WorkflowAuboProgramWorkerOptions
            {
                Enabled = true,
                PollIntervalMs = 10,
                CompletionTimeoutMs = 1000
            });

        await recoveringDispatcher.RecoverAsync(CancellationToken.None);

        var recovered = await workflows.GetExecutionAsync(resumedExecution.ExecutionId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Prepared, recovered!.RuntimeStatus);
        Assert.Equal(WorkflowNodeType.Move, recovered.PendingStepRequest!.NodeType);
        Assert.Equal(
            WorkflowNodeExecutionStatus.Succeeded,
            (await workflows.ListNodeExecutionsAsync(resumedExecution.ExecutionId, CancellationToken.None))
                .Single(item => item.Id == resumedRobot.NodeExecution.Id).Status);
    }

    private static ProfileConfiguration CreateProfile() => new()
    {
        Product = new ProductProfile { ProductId = "MES-AGV", DisplayName = "test", Version = "1.0" },
        Agvs = [new AgvProfile
        {
            AgvId = "AGV-01", Model = "Simulator", Driver = "simulator", Endpoint = "http://localhost:5183/",
            MaxLoadKg = 200, MaxSpeedMetersPerSecond = 1, HomeStationId = "LM1"
        }],
        Stations =
        [
            new StationProfile { Code = 1, StationId = "LM1", AgvStationId = "LM1", Name = "充电原点", Type = "Charge" },
            new StationProfile { Code = 4, StationId = "LM4", AgvStationId = "LM4", Name = "LM4", Type = "Station" }
        ],
        WorkflowDevices =
        [new WorkflowDeviceProfile
        {
            DeviceId = "ARM-01",
            DeviceFamily = WorkflowDeviceFamilyIds.RobotArm,
            CapabilityIds = [WorkflowCapabilityIds.RobotExecuteProgram],
            Enabled = true,
            ControlEnabled = true
        }],
        Map = new MapProfile
        {
            StationIds = ["LM1", "LM4"],
            Edges = [new MapEdgeProfile { From = "LM1", To = "LM4", Cost = 1 }]
        },
        Features = new FeatureFlags { UseSimulator = true },
        Timeouts = new TimeoutOptions()
    };

    private static WorkflowDefinition CreateWorkflow()
    {
        var start = Node(WorkflowNodeType.Start, WorkflowGraphNodeTypeIds.Start, "开始", 1);
        var move1 = Node(WorkflowNodeType.Move, WorkflowGraphNodeTypeIds.Move, "到 LM1", 2, "LM1");
        var robot1 = Node(WorkflowNodeType.RobotProgram, WorkflowGraphNodeTypeIds.RobotExecuteProgram, "测试1", 3,
            configuration: new Dictionary<string, string?>
            {
                [WorkflowNodeConfigurationKeys.DeviceId] = "ARM-01",
                [WorkflowNodeConfigurationKeys.ProgramName] = "测试1"
            });
        var move2 = Node(WorkflowNodeType.Move, WorkflowGraphNodeTypeIds.Move, "到 LM4", 4, "LM4");
        var robot2 = Node(WorkflowNodeType.RobotProgram, WorkflowGraphNodeTypeIds.RobotExecuteProgram, "测试2", 5,
            configuration: new Dictionary<string, string?>
            {
                [WorkflowNodeConfigurationKeys.DeviceId] = "ARM-01",
                [WorkflowNodeConfigurationKeys.ProgramName] = "测试2"
            });
        var end = Node(WorkflowNodeType.End, WorkflowGraphNodeTypeIds.End, "结束", 6);
        start = start with { NextNodeIds = [move1.Id] };
        move1 = move1 with { NextNodeIds = [robot1.Id] };
        robot1 = robot1 with { NextNodeIds = [move2.Id] };
        move2 = move2 with { NextNodeIds = [robot2.Id] };
        robot2 = robot2 with { NextNodeIds = [end.Id] };
        return new WorkflowDefinition
        {
            Name = "LM1/LM4 AUBO test",
            SchemaVersion = WorkflowGraphDocument.CurrentSchemaVersion,
            Nodes = [start, move1, robot1, move2, robot2, end],
            Edges = CreateEdges(start, move1, robot1, move2, robot2, end)
        };
    }

    private static WorkflowNode Node(
        WorkflowNodeType type,
        string typeId,
        string name,
        int order,
        string? targetStation = null,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var values = configuration is null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(configuration, StringComparer.OrdinalIgnoreCase);
        if (type == WorkflowNodeType.Move)
        {
            values[WorkflowNodeConfigurationKeys.TargetStation] = targetStation;
            values[WorkflowNodeConfigurationKeys.TimeoutSeconds] = "300";
            values[WorkflowNodeConfigurationKeys.RetryCount] = "0";
        }

        return new WorkflowNode
        {
            Type = type,
            NodeTypeId = typeId,
            Name = name,
            Order = order,
            TargetStation = targetStation,
            Ports = WorkflowGraphContractAdapter.CreatePorts(type),
            Configuration = values
        };
    }

    private static IReadOnlyList<WorkflowEdgeDefinition> CreateEdges(params WorkflowNode[] nodes) =>
        nodes.Zip(nodes.Skip(1), (source, target) => new WorkflowEdgeDefinition
        {
            SourceNodeId = source.Id,
            SourcePort = "success",
            TargetNodeId = target.Id,
            TargetPort = "in",
            Kind = WorkflowEdgeKind.Success
        }).ToArray();

    private sealed class ImmediateArrivalAgvGateway : IAgvGateway, IAdapterRuntimeIdentityGateway
    {
        public List<string> Targets { get; } = [];
        public Task<AdapterRuntimeIdentityResponse> GetRuntimeIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AdapterRuntimeIdentityResponse("adapter", "ok", "normal", "simulator"));
        public Task<AgvTaskResponse> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken)
        {
            Targets.Add(targetStationId);
            return Task.FromResult(new AgvTaskResponse(operationId, operationId.ToString("N"), targetStationId, "arrived", null));
        }
        public Task<AgvTaskResponse?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken) => Task.FromResult<AgvTaskResponse?>(null);
        public Task<AgvTaskResponse?> CancelAsync(Guid operationId, CancellationToken cancellationToken) => Task.FromResult<AgvTaskResponse?>(null);
        public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgvSnapshotResponse(true, "adapter", "LM1", null));
        public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken cancellationToken) =>
            Task.FromResult<AgvTaskResponse?>(null);
    }

    private sealed class RecordingArmGateway : IAuboArmGateway
    {
        public List<string> RunPrograms { get; } = [];
        public Queue<AuboArmProgramStatusResponse> ProgramStatuses { get; } = [];
        public int RunCalls { get; private set; }
        public bool FailOneObservationRead { get; init; }
        public int FailedObservationReads { get; private set; }
        private string? _loaded;
        public Task<AuboArmStatusResponse> GetStatusAsync(string deviceId, CancellationToken cancellationToken) => Task.FromResult(Status(deviceId));
        public Task<AuboArmReadinessResponse> GetReadinessAsync(string deviceId, CancellationToken cancellationToken) => Task.FromResult(new AuboArmReadinessResponse(deviceId, true, [], Status(deviceId), _loaded, DateTimeOffset.UtcNow));
        public Task<AuboArmVariableResponse> GetVariableAsync(string deviceId, string key, CancellationToken cancellationToken) => Task.FromResult(new AuboArmVariableResponse(deviceId, key, false, null, null, null, null, null, DateTimeOffset.UtcNow));
        public Task<AuboArmHandshakeSnapshotResponse> GetHandshakeSnapshotAsync(string deviceId, CancellationToken cancellationToken) => Task.FromResult(new AuboArmHandshakeSnapshotResponse(deviceId, AuboArmHandshakeState.Idle, null, null, null, null, null, DateTimeOffset.UtcNow));
        public Task<AuboArmHandshakeResultResponse> DispatchAsync(string deviceId, Guid operationId, int commandCode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AuboArmProgramStatusResponse> GetProgramAsync(string deviceId, CancellationToken cancellationToken)
        {
            if (ProgramStatuses.Count > 0)
                return Task.FromResult(ProgramStatuses.Dequeue());

            if (FailOneObservationRead && RunCalls > 0 && FailedObservationReads == 0)
            {
                FailedObservationReads++;
                throw new HttpRequestException("transient AUBO status outage");
            }

            return Task.FromResult(new AuboArmProgramStatusResponse(
                deviceId,
                true,
                _loaded,
                AuboArmRuntimeState.Stopped,
                "Stopped",
                DateTimeOffset.UtcNow));
        }
        public Task<AuboArmProgramOperationResponse> LoadProgramAsync(string deviceId, string programName, string operatorName, Guid operationId, CancellationToken cancellationToken)
        {
            _loaded = programName;
            return Task.FromResult(Operation(operationId, deviceId, programName, "load", operatorName, AuboArmProgramOperationState.Loaded));
        }
        public Task<AuboArmProgramOperationResponse> RunProgramAsync(string deviceId, string? programName, string operatorName, Guid operationId, CancellationToken cancellationToken)
        {
            RunCalls++;
            RunPrograms.Add(programName ?? _loaded ?? string.Empty);
            return Task.FromResult(Operation(operationId, deviceId, programName ?? _loaded ?? string.Empty, "run", operatorName, AuboArmProgramOperationState.Running));
        }
        public Task<AuboArmProgramOperationResponse> StopProgramAsync(string deviceId, string operatorName, Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(Operation(operationId, deviceId, _loaded ?? string.Empty, "stop", operatorName, AuboArmProgramOperationState.Stopped));
        private static AuboArmStatusResponse Status(string id) => new(id, "rob1", true, AuboArmMode.Running, 8, AuboArmSafetyMode.Normal, 1, AuboArmRuntimeState.Stopped, 6, AuboArmOperationalMode.Automatic, 1, DateTimeOffset.UtcNow);
        private static AuboArmProgramOperationResponse Operation(Guid id, string device, string program, string operation, string actor, AuboArmProgramOperationState state) => new(id, device, program, operation, actor, state, AuboArmRuntimeState.Stopped, "Stopped", program, 0, null, true, DateTimeOffset.UtcNow);
    }
}
