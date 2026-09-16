using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Devices;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Tests;

public sealed class WorkflowSampleWorkstationWorkerTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{")]
    public async Task Unreadable_query_payload_is_a_read_error_not_a_command_outcome(string payload)
    {
        // Injected handler: no socket, Adapter service or physical device.
        using var client = new HttpClient(new QueryPayloadHandler(payload)) { BaseAddress = new Uri("http://127.0.0.1/") };
        var reader = new SampleWorkstationAdapterClient(client);
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() =>
            reader.GetTaskStateAsync("WS-01", "task-01", CancellationToken.None));
    }

    [Theory]
    [InlineData("healthy", true, 4)]
    [InlineData("state-read-error", true, 4)]
    [InlineData("details-read-error", true, 4)]
    [InlineData("state-json-error", true, 4)]
    [InlineData("details-json-error", true, 4)]
    [InlineData("permanent-json-error", false, 4)]
    [InlineData("details-lag", true, 4)]
    [InlineData("metadata-read-error", true, 4)]
    [InlineData("permanent-read-error", false, 4)]
    [InlineData("unknown-state", false, 4)]
    [InlineData("wrong-task", false, 4)]
    [InlineData("wrong-details", false, 4)]
    [InlineData("unknown-write", false, 1)]
    [InlineData("wrong-write-identity", false, 1)]
    public async Task Commissioning_workstation_observes_one_task_without_replaying_stages(
        string scenario, bool succeeds, int expectedWrites)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = new MesDbContext(new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var profile = ProfileConfiguration.Default with
        {
            Features = ProfileConfiguration.Default.Features with { UseSimulator = false },
            WorkflowDevices = [new WorkflowDeviceProfile
            {
                DeviceId = "WS-01", DeviceFamily = WorkflowDeviceFamilyIds.SampleWorkstation,
                Enabled = true, ControlEnabled = true, CapabilityIds = [WorkflowCapabilityIds.SampleWorkstationExecute]
            }]
        };
        var store = new PhysicalReadinessStateStore(clock, "offline-workstation");
        var descriptors = new[]
        {
            new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true),
            new PhysicalDeviceDescriptor("WS-01", WorkflowDeviceFamilyIds.SampleWorkstation, true, ControlEnabled: true)
        };
        store.Configure(true, descriptors, clock.GetUtcNow());
        foreach (var descriptor in descriptors)
        {
            store.Apply(descriptor, new PhysicalDeviceReadinessObservation
            {
                DeviceId = descriptor.DeviceId, DeviceFamily = descriptor.DeviceFamily,
                Online = true, ProbeSucceeded = true, IsFullPreflight = true, FullPreflightPassed = true,
                ObservedAtUtc = clock.GetUtcNow()
            }, TimeSpan.Zero, true, clock.GetUtcNow());
            var snapshot = store.GetSnapshot();
            var device = snapshot.Devices.Single(item => item.DeviceId == descriptor.DeviceId);
            Assert.True(store.AcknowledgeAuthorization(device.DeviceId, device.DeviceEpoch, snapshot.SupervisorInstanceId));
        }
        var catalog = BuiltInWorkflowCatalog.Create();
        var validator = new WorkflowValidator(catalog, WorkflowPublicationContext.FromProfile(profile));
        var versionReader = new MesWorkflowVersionReader(database);
        var workflows = new WorkflowApplicationService(database, versionReader,
            new WorkflowRuntimeExecutor(versionReader, validator), validator, timeProvider: clock, physicalReadiness: store);
        var definition = WorkflowTestDefinitions.CreateTimedWaitWorkflow("1");
        var nodeType = catalog.NodeTypes.GetLatest(WorkflowGraphNodeTypeIds.SampleWorkstationExecuteTemplate)!;
        definition = definition with
        {
            Nodes = definition.Nodes.Select(node => node.NodeTypeId == WorkflowGraphNodeTypeIds.TimedWait ? node with
            {
                Type = WorkflowNodeType.Custom, NodeTypeId = nodeType.NodeTypeId,
                SchemaVersion = nodeType.SchemaVersion, Ports = nodeType.Ports,
                Configuration = new Dictionary<string, string?>
                {
                    [WorkflowNodeConfigurationKeys.DeviceId] = "WS-01",
                    [WorkflowNodeConfigurationKeys.TemplateVersion] = "offline-v1"
                }
            } : node).ToArray()
        };
        var draft = await workflows.CreateDraftAsync(definition, "admin", CancellationToken.None);
        var validation = await workflows.ValidateVersionAsync(draft.WorkflowId, draft.Version, CancellationToken.None);
        Assert.True(validation.IsValid, string.Join(";", validation.Issues.Select(issue => issue.Message)));
        await workflows.PublishAsync(draft.WorkflowId, draft.Version, "admin", CancellationToken.None);
        var run = await workflows.ExecuteAsync(new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId, Version = draft.Version, RequestId = Guid.NewGuid(), RequestedBy = "admin",
            PhysicalAuthorization = new WorkflowPhysicalRunAuthorization
            {
                AgvId = "AGV-01", OperatorName = "admin", SafetyObserverName = "admin", PermitPrefix = "offline-ws",
                ExpiresAtUtc = clock.GetUtcNow().AddHours(1), ReadinessSupervisorInstanceId = store.GetSnapshot().SupervisorInstanceId,
                DeviceEpochs = store.GetSnapshot().Devices.ToDictionary(device => device.DeviceId, device => device.DeviceEpoch)
            }
        }, CancellationToken.None);
        Assert.True(run.IsAccepted, run.RejectionReason);
        var pending = await workflows.ListSampleWorkstationDispatchableNodesAsync(CancellationToken.None);
        var persistedNodes = await workflows.ListNodeExecutionsAsync(run.ExecutionId, CancellationToken.None);
        Assert.True(pending.Count == 1, $"Next={run.NextStepRequest?.NodeTypeId}; Nodes={string.Join(",", persistedNodes.Select(node => node.NodeTypeId + ":" + node.Status))}");
        Assert.True(store.IsCurrentAndReady("WS-01", store.GetSnapshot().Devices.Single(device => device.DeviceId == "WS-01").DeviceEpoch,
            store.GetSnapshot().SupervisorInstanceId, out var readinessReason), readinessReason);
        var gateway = new InMemoryWorkstation(clock, scenario);
        var dispatcher = new WorkflowSampleWorkstationDispatcher(workflows, gateway, gateway, profile,
            new WorkflowSampleWorkstationWorkerOptions
            {
                Enabled = true, PollIntervalMs = 100, CompletionTimeoutMs = 5000,
                Templates = new() { ["offline-v1"] = new SampleWorkstationTemplateOptions
                {
                    LiquidCode = "water", LiquidName = "water", WarehouseLocation = "A1", TransferVolume = 10
                } }
            }, physicalReadiness: store, timeProvider: clock);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await dispatcher.ProcessAsync(cancellation.Token);
        await dispatcher.ProcessAsync(cancellation.Token);
        var diagnostic = (await workflows.GetExecutionAsync(run.ExecutionId, CancellationToken.None))!;
        Assert.True(gateway.Writes.Count > 0, $"Run={diagnostic.RuntimeStatus}; Error={diagnostic.LastError}; statusReads={gateway.StatusReads}");
        Assert.Equal(expectedWrites, gateway.Writes.Count);
        Assert.Equal(expectedWrites, gateway.Writes.Select(write => write.OperationId).Distinct().Count());
        Assert.All(gateway.Writes, write => Assert.Equal(run.ExecutionId, write.RunId));
        Assert.Equal(1, gateway.StatusReads); // no metadata query after completion
        if (scenario == "permanent-json-error") Assert.True(gateway.TaskReads >= 5);
        var final = (await workflows.GetExecutionAsync(run.ExecutionId, CancellationToken.None))!;
        Assert.Equal(succeeds ? WorkflowRuntimeStatus.Completed : WorkflowRuntimeStatus.Unknown, final.RuntimeStatus);
        var operation = Assert.Single(await workflows.ListDeviceOperationsAsync(run.ExecutionId, CancellationToken.None));
        Assert.Equal(succeeds ? WorkflowDeviceOperationStatus.Succeeded : WorkflowDeviceOperationStatus.Unknown, operation.Status);
        if (succeeds)
        {
            var node = Assert.Single(await workflows.ListNodeExecutionsAsync(run.ExecutionId, CancellationToken.None));
            Assert.Equal("equipment-01", node.Outputs["equipmentNo"]);
            Assert.Equal(gateway.TaskNo, node.Outputs["taskNo"]);
            Assert.Equal(gateway.TaskNo, operation.VendorTaskId);
        }
    }

    [Theory]
    [InlineData(SampleWorkstationDeviceState.Running, false)]
    [InlineData(SampleWorkstationDeviceState.Initializing, false)]
    [InlineData(SampleWorkstationDeviceState.Paused, false)]
    [InlineData(SampleWorkstationDeviceState.Faulted, true)]
    [InlineData(SampleWorkstationDeviceState.Offline, true)]
    [InlineData(SampleWorkstationDeviceState.Unknown, true)]
    public async Task Workstation_busy_blocks_new_writes_without_invalidating_the_run_authorization(
        SampleWorkstationDeviceState busy, bool invalidates)
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var gateway = new InMemoryWorkstation(clock, "healthy");
        var probe = new PhysicalSampleWorkstationReadinessProbe(gateway, clock);
        var store = new PhysicalReadinessStateStore(clock);
        var descriptor = new PhysicalDeviceDescriptor("WS-01", WorkflowDeviceFamilyIds.SampleWorkstation, true, ControlEnabled: true);
        store.Configure(true, [descriptor], clock.GetUtcNow());
        store.Apply(descriptor, await probe.ProbeAsync(descriptor, true, CancellationToken.None), TimeSpan.Zero, true, clock.GetUtcNow());
        var initial = store.GetSnapshot();
        var epoch = Assert.Single(initial.Devices).DeviceEpoch;
        Assert.True(store.AcknowledgeAuthorization("WS-01", epoch, initial.SupervisorInstanceId));
        gateway.DeviceState = busy;
        store.Apply(descriptor, await probe.ProbeAsync(descriptor, true, CancellationToken.None), TimeSpan.Zero, true, clock.GetUtcNow());
        var blocked = Assert.Single(store.GetSnapshot().Devices);
        Assert.NotEqual(PhysicalDeviceReadinessState.Ready, blocked.State);
        Assert.Equal(invalidates, blocked.RequiresReauthorization);
        Assert.Equal(invalidates, blocked.DeviceEpoch != epoch);
        gateway.DeviceState = SampleWorkstationDeviceState.Idle;
        store.Apply(descriptor, await probe.ProbeAsync(descriptor, true, CancellationToken.None), TimeSpan.Zero, true, clock.GetUtcNow());
        Assert.Equal(!invalidates, store.IsCurrentAndReady("WS-01", epoch, initial.SupervisorInstanceId, out _));
        Assert.Empty(gateway.Writes);
    }

    private sealed class InMemoryWorkstation(AdjustableTimeProvider clock, string scenario) : ISampleWorkstationController, ISampleWorkstationReader
    {
        public List<SampleWorkstationOperationRequest> Writes { get; } = [];
        public string TaskNo { get; private set; } = "";
        public int StatusReads { get; private set; }
        public SampleWorkstationDeviceState DeviceState { get; set; } = SampleWorkstationDeviceState.Idle;
        private int _taskReads;
        public int TaskReads => _taskReads;
        private int _detailReads;
        public Task<SampleWorkstationStatusResponse> GetStatusAsync(string deviceId, CancellationToken cancellationToken)
        {
            StatusReads++;
            if (Writes.Count == 4 && scenario == "metadata-read-error") throw new HttpRequestException("metadata unavailable");
            return Task.FromResult(new SampleWorkstationStatusResponse(deviceId, "equipment-01", DeviceState != SampleWorkstationDeviceState.Offline,
                DeviceState, (int)DeviceState, clock.GetUtcNow()));
        }
        public Task<SampleWorkstationTaskStateResponse> GetTaskStateAsync(string deviceId, string taskNo, CancellationToken cancellationToken)
        {
            Assert.Equal(TaskNo, taskNo);
            _taskReads++;
            clock.Advance(TimeSpan.FromSeconds(1));
            if (scenario == "permanent-read-error" || (scenario == "state-read-error" && _taskReads == 2))
                throw new HttpRequestException("temporary read failure");
            if (scenario == "permanent-json-error" || (scenario == "state-json-error" && _taskReads == 2))
                throw new System.Text.Json.JsonException("unreadable task payload");
            var state = _taskReads == 1 ? SampleWorkstationTaskState.Waiting : _taskReads == 2
                ? SampleWorkstationTaskState.Running : SampleWorkstationTaskState.Completed;
            if (scenario == "unknown-state" && _taskReads == 2) state = SampleWorkstationTaskState.Unknown;
            return Task.FromResult(new SampleWorkstationTaskStateResponse(deviceId,
                scenario == "wrong-task" ? "unrelated-task" : taskNo, state, state.ToString(), clock.GetUtcNow()));
        }
        public Task<SampleWorkstationTaskDetailsResponse> GetTaskDetailsAsync(string deviceId, string taskNo, CancellationToken cancellationToken)
        {
            Assert.Equal(TaskNo, taskNo);
            _detailReads++;
            if (scenario == "details-read-error" && _detailReads == 1) throw new TimeoutException("details not yet readable");
            if (scenario == "details-json-error" && _detailReads == 1) throw new System.Text.Json.JsonException("unreadable details payload");
            var state = scenario == "details-lag" && _detailReads == 1 ? SampleWorkstationTaskState.Running : SampleWorkstationTaskState.Completed;
            return Task.FromResult(new SampleWorkstationTaskDetailsResponse(1,
                scenario == "wrong-details" ? "unrelated-task" : taskNo, "sample", state, state.ToString(), "", null,
                clock.GetUtcNow().ToString("O"), "admin", "", null));
        }
        public Task<SampleWorkstationOperationResponse> InitializeAsync(string deviceId, SampleWorkstationOperationRequest request, CancellationToken token) => Write(deviceId, request);
        public Task<SampleWorkstationOperationResponse> CreateTaskAsync(string deviceId, SampleWorkstationTaskCreateRequest request, CancellationToken token)
        { TaskNo = request.TaskNo; return Write(deviceId, request); }
        public Task<SampleWorkstationOperationResponse> AddTrajectoryAsync(string deviceId, SampleWorkstationTrajectoryRequest request, CancellationToken token) => Write(deviceId, request);
        public Task<SampleWorkstationOperationResponse> StartExperimentAsync(string deviceId, SampleWorkstationStartRequest request, CancellationToken token) => Write(deviceId, request);
        private Task<SampleWorkstationOperationResponse> Write(string deviceId, SampleWorkstationOperationRequest request)
        {
            Writes.Add(request);
            return Task.FromResult(new SampleWorkstationOperationResponse(deviceId,
                scenario == "wrong-write-identity" ? Guid.NewGuid() : request.OperationId, request.RunId, request.NodeExecutionId,
                scenario == "unknown-write" ? DeviceOperationLifecycle.Unknown : DeviceOperationLifecycle.Completed,
                null, TaskNo, null, clock.GetUtcNow()));
        }
        public Task<SampleWorkstationErrorResponse> GetErrorsAsync(string deviceId, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<SampleWorkstationTaskSummaryResponse>> GetTasksAsync(string deviceId, SampleWorkstationTaskQuery query, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class QueryPayloadHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
