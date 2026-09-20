using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Tests;

public sealed class WorkflowSampleWorkstationWorkerTests
{
    [Fact]
    public async Task Existing_task_dispatcher_leaves_legacy_template_nodes_for_the_legacy_worker()
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        await fixture.PublishExecuteAndConfirmAsync();
        var ready = Assert.Single(await fixture.Workflows.ListSampleWorkstationDispatchableNodesAsync(CancellationToken.None));
        var record = await fixture.Database.WorkflowNodeExecutions.SingleAsync(item => item.Id == ready.NodeExecution.Id);
        record.NodeTypeId = WorkflowGraphNodeTypeIds.SampleWorkstationExecuteTemplate;
        await fixture.Database.SaveChangesAsync();
        var gateway = fixture.CreateGateway(Observation.Idle(SampleWorkstationTaskState.Completed));

        await fixture.CreateDispatcher(gateway).ProcessAsync(CancellationToken.None);

        Assert.Equal(0, gateway.StartCalls);
        await fixture.Database.Entry(record).ReloadAsync();
        Assert.Equal(WorkflowNodeExecutionStatus.Ready.ToString(), record.Status);
        Assert.Empty(await fixture.Workflows.ListDeviceOperationsAsync(record.WorkflowRunId, CancellationToken.None));
    }

    [Fact]
    public async Task Unconfirmed_manual_gate_never_exposes_or_starts_the_workstation_node()
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        var runId = await fixture.PublishAndExecuteAsync();
        var gateway = fixture.CreateGateway(Observation.Idle(SampleWorkstationTaskState.Completed));

        await fixture.CreateDispatcher(gateway).ProcessAsync(CancellationToken.None);

        Assert.Equal(0, gateway.StartCalls);
        Assert.Empty(await fixture.Workflows.ListSampleWorkstationDispatchableNodesAsync(CancellationToken.None));
        var nodes = await fixture.Workflows.ListNodeExecutionsAsync(runId, CancellationToken.None);
        Assert.Contains(nodes, item =>
            item.NodeTypeId == WorkflowGraphNodeTypeIds.ManualConfirmation &&
            item.Status == WorkflowNodeExecutionStatus.WaitingForSignal);
    }

    [Fact]
    public async Task Normal_execution_starts_once_observes_running_and_completes()
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        var runId = await fixture.PublishExecuteAndConfirmAsync();
        var gateway = fixture.CreateGateway(
            Observation.Idle(SampleWorkstationTaskState.Completed),
            Observation.Running(),
            Observation.Idle(SampleWorkstationTaskState.Completed));

        await fixture.CreateDispatcher(gateway).ProcessAsync(CancellationToken.None);

        Assert.Equal(1, gateway.StartCalls);
        Assert.Equal(0, gateway.InitializeCalls);
        var run = await fixture.Workflows.GetExecutionAsync(runId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Completed, run!.RuntimeStatus);
        var node = (await fixture.Workflows.ListNodeExecutionsAsync(runId, CancellationToken.None))
            .Single(item => item.NodeTypeId == WorkflowGraphNodeTypeIds.SampleWorkstationExecuteExistingTask);
        Assert.Equal(WorkflowNodeExecutionStatus.Succeeded, node.Status);
        Assert.Equal("TEST-001", node.Outputs["taskNo"]);
        Assert.Equal("Completed", node.Outputs["taskState"]);
        Assert.False(string.IsNullOrWhiteSpace(node.Outputs["runningObservedAtUtc"]));
        Assert.False(string.IsNullOrWhiteSpace(node.Outputs["completedAtUtc"]));
        var operation = Assert.Single(await fixture.Workflows.ListDeviceOperationsAsync(runId, CancellationToken.None));
        Assert.Equal(WorkflowCapabilityIds.SampleWorkstationStartExistingTask, operation.CapabilityId);
        Assert.Equal(WorkflowDeviceOperationStatus.Succeeded, operation.Status);
        Assert.Equal("TEST-001", operation.VendorTaskId);
    }

    [Fact]
    public async Task Readiness_failure_leaves_node_ready_without_any_command()
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        var runId = await fixture.PublishExecuteAndConfirmAsync();
        var gateway = fixture.CreateGateway(Observation.Initializing());

        await fixture.CreateDispatcher(gateway).ProcessAsync(CancellationToken.None);

        Assert.Equal(0, gateway.StartCalls);
        Assert.Equal(0, gateway.InitializeCalls);
        var node = (await fixture.Workflows.ListNodeExecutionsAsync(runId, CancellationToken.None))
            .Single(item => item.NodeTypeId == WorkflowGraphNodeTypeIds.SampleWorkstationExecuteExistingTask);
        Assert.Equal(WorkflowNodeExecutionStatus.Ready, node.Status);
        Assert.Empty(await fixture.Workflows.ListDeviceOperationsAsync(runId, CancellationToken.None));
    }

    [Fact]
    public async Task Old_completed_state_without_running_evidence_becomes_unknown_without_retrying_start()
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        var runId = await fixture.PublishExecuteAndConfirmAsync();
        var gateway = fixture.CreateGateway(
            Observation.Idle(SampleWorkstationTaskState.Completed),
            Observation.Idle(SampleWorkstationTaskState.Completed));

        await fixture.CreateDispatcher(gateway, startObservationTimeoutMs: 25)
            .ProcessAsync(CancellationToken.None);

        Assert.Equal(1, gateway.StartCalls);
        var run = await fixture.Workflows.GetExecutionAsync(runId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Unknown, run!.RuntimeStatus);
        var operation = Assert.Single(await fixture.Workflows.ListDeviceOperationsAsync(runId, CancellationToken.None));
        Assert.Equal(WorkflowDeviceOperationStatus.Unknown, operation.Status);
    }

    [Fact]
    public async Task Explicit_start_rejection_fails_once_without_observation_or_retry()
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        var runId = await fixture.PublishExecuteAndConfirmAsync();
        var gateway = fixture.CreateGateway(Observation.Idle(SampleWorkstationTaskState.Completed));
        gateway.Acknowledged = false;

        await fixture.CreateDispatcher(gateway).ProcessAsync(CancellationToken.None);

        Assert.Equal(1, gateway.StartCalls);
        Assert.Equal(1, gateway.ObservationCount);
        var run = await fixture.Workflows.GetExecutionAsync(runId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Failed, run!.RuntimeStatus);
    }

    [Fact]
    public async Task Ambiguous_start_transport_failure_becomes_unknown_without_retry()
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        var runId = await fixture.PublishExecuteAndConfirmAsync();
        var gateway = fixture.CreateGateway(Observation.Idle(SampleWorkstationTaskState.Completed));
        gateway.StartException = new SampleWorkstationGatewayException(
            504,
            SampleWorkstationErrorCodes.Timeout,
            "start response timed out",
            outcomeUnknown: true);

        await fixture.CreateDispatcher(gateway).ProcessAsync(CancellationToken.None);

        Assert.Equal(1, gateway.StartCalls);
        var run = await fixture.Workflows.GetExecutionAsync(runId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Unknown, run!.RuntimeStatus);
    }

    [Fact]
    public async Task Known_adapter_start_rejection_fails_without_retry()
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        var runId = await fixture.PublishExecuteAndConfirmAsync();
        var gateway = fixture.CreateGateway(Observation.Idle(SampleWorkstationTaskState.Completed));
        gateway.StartException = new SampleWorkstationGatewayException(
            502,
            SampleWorkstationErrorCodes.CommandUnconfirmed,
            "启动失败",
            outcomeUnknown: false,
            vendorCode: 200);

        await fixture.CreateDispatcher(gateway).ProcessAsync(CancellationToken.None);

        Assert.Equal(1, gateway.StartCalls);
        var run = await fixture.Workflows.GetExecutionAsync(runId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Failed, run!.RuntimeStatus);
    }

    [Fact]
    public async Task Post_start_read_outage_retries_reads_only_and_never_replays_start()
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        var runId = await fixture.PublishExecuteAndConfirmAsync();
        var gateway = fixture.CreateGateway(Observation.Idle(SampleWorkstationTaskState.Completed));
        gateway.FailReadsAfterStart = true;

        await fixture.CreateDispatcher(gateway, startObservationTimeoutMs: 25)
            .ProcessAsync(CancellationToken.None);

        Assert.Equal(1, gateway.StartCalls);
        Assert.True(gateway.FailedStatusReads >= 2);
        var run = await fixture.Workflows.GetExecutionAsync(runId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Unknown, run!.RuntimeStatus);
    }

    [Fact]
    public async Task Unknown_vendor_task_state_stops_the_flow_without_retrying_start()
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        var runId = await fixture.PublishExecuteAndConfirmAsync();
        var gateway = fixture.CreateGateway(
            Observation.Idle(SampleWorkstationTaskState.Completed),
            Observation.Idle(SampleWorkstationTaskState.Unknown));

        await fixture.CreateDispatcher(gateway).ProcessAsync(CancellationToken.None);

        Assert.Equal(1, gateway.StartCalls);
        var run = await fixture.Workflows.GetExecutionAsync(runId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Unknown, run!.RuntimeStatus);
    }

    [Fact]
    public async Task Restart_with_only_accepted_evidence_becomes_unknown_and_never_replays_start()
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        var runId = await fixture.PublishExecuteAndConfirmAsync();
        var ready = Assert.Single(await fixture.Workflows.ListSampleWorkstationDispatchableNodesAsync(CancellationToken.None));
        var claimed = await fixture.Workflows.ClaimNodeExecutionAsync(ready.NodeExecution.Id, CancellationToken.None);
        await fixture.Workflows.RecordDeviceOperationProgressAsync(
            claimed.NodeExecution.Id,
            claimed.DeviceOperation!.OperationId,
            WorkflowDeviceOperationStatus.Accepted,
            null,
            CancellationToken.None);
        var gateway = fixture.CreateGateway(Observation.Idle(SampleWorkstationTaskState.Completed));
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1001));

        await fixture.CreateDispatcher(gateway).RecoverAsync(CancellationToken.None);

        Assert.Equal(0, gateway.StartCalls);
        Assert.Equal(0, gateway.ObservationCount);
        var run = await fixture.Workflows.GetExecutionAsync(runId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Unknown, run!.RuntimeStatus);
    }

    [Fact]
    public async Task Restart_with_durable_running_evidence_completes_by_reads_only()
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        var runId = await fixture.PublishExecuteAndConfirmAsync();
        var ready = Assert.Single(await fixture.Workflows.ListSampleWorkstationDispatchableNodesAsync(CancellationToken.None));
        var claimed = await fixture.Workflows.ClaimNodeExecutionAsync(ready.NodeExecution.Id, CancellationToken.None);
        await fixture.Workflows.RecordDeviceOperationProgressAsync(
            claimed.NodeExecution.Id,
            claimed.DeviceOperation!.OperationId,
            WorkflowDeviceOperationStatus.Accepted,
            null,
            CancellationToken.None);
        await fixture.Workflows.RecordDeviceOperationProgressAsync(
            claimed.NodeExecution.Id,
            claimed.DeviceOperation.OperationId,
            WorkflowDeviceOperationStatus.Running,
            null,
            CancellationToken.None);
        var gateway = fixture.CreateGateway(Observation.Idle(SampleWorkstationTaskState.Completed));

        await fixture.CreateDispatcher(gateway).RecoverAsync(CancellationToken.None);

        Assert.Equal(0, gateway.StartCalls);
        Assert.Equal(1, gateway.ObservationCount);
        var run = await fixture.Workflows.GetExecutionAsync(runId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Completed, run!.RuntimeStatus);
    }

    [Fact]
    public async Task One_shot_start_boundary_can_be_acquired_only_once()
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        await fixture.PublishExecuteAndConfirmAsync();
        var ready = Assert.Single(
            await fixture.Workflows.ListSampleWorkstationDispatchableNodesAsync(CancellationToken.None));
        var claimed = await fixture.Workflows.TryClaimSampleWorkstationNodeExecutionAsync(
            ready.NodeExecution.Id,
            CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.True(await fixture.Workflows.TryMarkDeviceOperationStartPendingAsync(
            claimed!.NodeExecution.Id,
            claimed.DeviceOperation!.OperationId,
            CancellationToken.None));
        Assert.False(await fixture.Workflows.TryMarkDeviceOperationStartPendingAsync(
            claimed.NodeExecution.Id,
            claimed.DeviceOperation.OperationId,
            CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_workstation_operation_blocks_a_second_run_on_the_same_device(bool legacyOwner)
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        await fixture.PublishExecuteAndConfirmAsync();
        await fixture.PublishExecuteAndConfirmAsync("sample-workstation-01");
        var ready = await fixture.Workflows.ListSampleWorkstationDispatchableNodesAsync(CancellationToken.None);
        Assert.Equal(2, ready.Count);

        var first = await fixture.Workflows.TryClaimSampleWorkstationNodeExecutionAsync(
            ready[0].NodeExecution.Id,
            CancellationToken.None);
        Assert.NotNull(first);
        if (legacyOwner)
        {
            var operation = await fixture.Database.WorkflowDeviceOperations.SingleAsync(
                item => item.OperationId == first.DeviceOperation!.OperationId);
            operation.CapabilityId = WorkflowCapabilityIds.SampleWorkstationExecute;
            await fixture.Database.SaveChangesAsync();
        }
        var second = await fixture.Workflows.TryClaimSampleWorkstationNodeExecutionAsync(
            ready[1].NodeExecution.Id,
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(await fixture.Workflows.ListDeviceOperationsAsync(
            first!.NodeExecution.WorkflowRunId,
            CancellationToken.None));
    }

    [Fact]
    public async Task Recovery_does_not_terminalize_a_fresh_start_pending_owner()
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        var runId = await fixture.PublishExecuteAndConfirmAsync();
        var ready = Assert.Single(
            await fixture.Workflows.ListSampleWorkstationDispatchableNodesAsync(CancellationToken.None));
        var claimed = await fixture.Workflows.TryClaimSampleWorkstationNodeExecutionAsync(
            ready.NodeExecution.Id,
            CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.True(await fixture.Workflows.TryMarkDeviceOperationStartPendingAsync(
            claimed!.NodeExecution.Id,
            claimed.DeviceOperation!.OperationId,
            CancellationToken.None));
        var gateway = fixture.CreateGateway(Observation.Idle(SampleWorkstationTaskState.Completed));
        var dispatcher = fixture.CreateDispatcher(gateway);

        await dispatcher.RecoverAsync(CancellationToken.None);

        Assert.Equal(
            WorkflowRuntimeStatus.Running,
            (await fixture.Workflows.GetExecutionAsync(runId, CancellationToken.None))!.RuntimeStatus);
        Assert.Equal(
            WorkflowDeviceOperationStatus.StartPending,
            Assert.Single(await fixture.Workflows.ListDeviceOperationsAsync(runId, CancellationToken.None)).Status);
        Assert.Equal(0, gateway.ObservationCount);

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1001));
        await dispatcher.RecoverAsync(CancellationToken.None);
        Assert.Equal(
            WorkflowRuntimeStatus.Unknown,
            (await fixture.Workflows.GetExecutionAsync(runId, CancellationToken.None))!.RuntimeStatus);
    }

    [Fact]
    public async Task Profile_drift_during_restart_marks_running_unknown_without_reads_or_writes()
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        var runId = await fixture.PublishExecuteAndConfirmAsync();
        var ready = Assert.Single(
            await fixture.Workflows.ListSampleWorkstationDispatchableNodesAsync(CancellationToken.None));
        var claimed = await fixture.Workflows.ClaimNodeExecutionAsync(
            ready.NodeExecution.Id,
            CancellationToken.None);
        await fixture.Workflows.RecordDeviceOperationProgressAsync(
            claimed.NodeExecution.Id,
            claimed.DeviceOperation!.OperationId,
            WorkflowDeviceOperationStatus.Running,
            null,
            CancellationToken.None);
        var disabledProfile = fixture.Profile with
        {
            WorkflowDevices = fixture.Profile.WorkflowDevices.Select(device =>
                string.Equals(device.DeviceId, "SAMPLE-WORKSTATION-01", StringComparison.OrdinalIgnoreCase)
                    ? device with { Enabled = false, ControlEnabled = false }
                    : device).ToArray()
        };
        var gateway = fixture.CreateGateway(Observation.Idle(SampleWorkstationTaskState.Completed));

        await fixture.CreateDispatcher(gateway, profile: disabledProfile)
            .RecoverAsync(CancellationToken.None);

        Assert.Equal(0, gateway.StartCalls);
        Assert.Equal(0, gateway.ObservationCount);
        var run = await fixture.Workflows.GetExecutionAsync(runId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Unknown, run!.RuntimeStatus);
    }

    [Fact]
    public async Task Host_shutdown_preserves_running_for_read_only_restart_recovery()
    {
        await using var fixture = await WorkstationFixture.CreateAsync();
        var runId = await fixture.PublishExecuteAndConfirmAsync();
        var gateway = fixture.CreateGateway(
            Observation.Idle(SampleWorkstationTaskState.Completed),
            Observation.Running());
        gateway.BlockStatusReadNumber = 3;
        using var shutdown = new CancellationTokenSource();
        var dispatch = fixture.CreateDispatcher(gateway).ProcessAsync(shutdown.Token);
        await gateway.BlockedStatusRead.Task.WaitAsync(TimeSpan.FromSeconds(5));

        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);

        var interrupted = Assert.Single(
            await fixture.Workflows.ListDeviceOperationsAsync(runId, CancellationToken.None));
        Assert.Equal(WorkflowDeviceOperationStatus.Running, interrupted.Status);
        var interruptedNode = (await fixture.Workflows.ListNodeExecutionsAsync(runId, CancellationToken.None))
            .Single(item => item.NodeTypeId == WorkflowGraphNodeTypeIds.SampleWorkstationExecuteExistingTask);
        Assert.Equal(WorkflowNodeExecutionStatus.Running, interruptedNode.Status);

        var recoveryGateway = fixture.CreateGateway(
            Observation.Idle(SampleWorkstationTaskState.Completed));
        await fixture.CreateDispatcher(recoveryGateway).RecoverAsync(CancellationToken.None);

        Assert.Equal(0, recoveryGateway.StartCalls);
        Assert.Equal(
            WorkflowRuntimeStatus.Completed,
            (await fixture.Workflows.GetExecutionAsync(runId, CancellationToken.None))!.RuntimeStatus);
    }

    private sealed record Observation(
        SampleWorkstationDeviceState DeviceState,
        int RawDeviceState,
        int ErrorCode,
        SampleWorkstationTaskState TaskState,
        string RawTaskState)
    {
        public static Observation Idle(SampleWorkstationTaskState taskState) =>
            new(SampleWorkstationDeviceState.Idle, 0, 0, taskState, taskState.ToString());

        public static Observation Running() =>
            new(SampleWorkstationDeviceState.Running, 1, 3, SampleWorkstationTaskState.Running, "Running");

        public static Observation Initializing() =>
            new(SampleWorkstationDeviceState.Initializing, 2, 0, SampleWorkstationTaskState.Completed, "Completed");
    }

    private sealed class RecordingWorkstation(
        AdjustableTimeProvider clock,
        IEnumerable<Observation> observations) : ISampleWorkstationReader, ISampleWorkstationCommands
    {
        private readonly Queue<Observation> _observations = new(observations);
        private Observation? _current;

        public int InitializeCalls { get; private set; }
        public int StartCalls { get; private set; }
        public int ObservationCount { get; private set; }
        public bool Acknowledged { get; set; } = true;
        public Exception? StartException { get; set; }
        public bool FailReadsAfterStart { get; set; }
        public int FailedStatusReads { get; private set; }
        public int? BlockStatusReadNumber { get; set; }
        public int StatusReadCalls { get; private set; }
        public TaskCompletionSource<bool> BlockedStatusRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SampleWorkstationStatusResponse> GetStatusAsync(
            string deviceId,
            CancellationToken cancellationToken)
        {
            StatusReadCalls++;
            if (BlockStatusReadNumber == StatusReadCalls)
            {
                BlockedStatusRead.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (FailReadsAfterStart && StartCalls > 0)
            {
                FailedStatusReads++;
                clock.Advance(TimeSpan.FromMilliseconds(10));
                throw new HttpRequestException("workstation status unavailable");
            }
            _current = _observations.Count > 0 ? _observations.Dequeue() : _current
                ?? throw new InvalidOperationException("No workstation observation was configured.");
            return new SampleWorkstationStatusResponse(
                deviceId,
                "CYC-001-1000",
                _current.DeviceState != SampleWorkstationDeviceState.Offline,
                _current.DeviceState,
                _current.RawDeviceState,
                clock.GetUtcNow());
        }

        public Task<SampleWorkstationErrorResponse> GetErrorsAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new SampleWorkstationErrorResponse(
                deviceId,
                Current.ErrorCode,
                Current.ErrorCode switch { 0 => "TaskCompleted", 3 => "ExperimentStarted", _ => "Error" },
                true,
                clock.GetUtcNow()));

        public Task<SampleWorkstationTaskStateResponse> GetTaskStateAsync(
            string deviceId,
            string taskNo,
            CancellationToken cancellationToken)
        {
            var current = Current;
            ObservationCount++;
            clock.Advance(TimeSpan.FromMilliseconds(10));
            return Task.FromResult(new SampleWorkstationTaskStateResponse(
                deviceId,
                taskNo,
                current.TaskState,
                current.RawTaskState,
                clock.GetUtcNow()));
        }

        public Task<SampleWorkstationCommandResponse> InitializeAsync(
            string deviceId,
            CancellationToken cancellationToken)
        {
            InitializeCalls++;
            throw new InvalidOperationException("The formal workflow must never initialize the workstation.");
        }

        public Task<SampleWorkstationCommandResponse> StartTaskAsync(
            string deviceId,
            string taskNo,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            if (StartException is not null) throw StartException;
            return Task.FromResult(new SampleWorkstationCommandResponse(
                deviceId,
                SampleWorkstationCommandOperation.StartTask,
                Acknowledged ? 0 : 1,
                default,
                clock.GetUtcNow())
            {
                TaskNo = taskNo,
                Acknowledged = Acknowledged
            });
        }

        public Task<IReadOnlyList<SampleWorkstationTaskSummaryResponse>> GetTasksAsync(
            string deviceId,
            SampleWorkstationTaskQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SampleWorkstationTaskDetailsResponse> GetTaskDetailsAsync(
            string deviceId,
            string taskNo,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SampleWorkstationProtocolResponse> GetProtocolReadAsync(
            string deviceId,
            SampleWorkstationProtocolOperation operation,
            SampleWorkstationProtocolReadQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        private Observation Current => _current
            ?? throw new InvalidOperationException("Status must be read before the rest of the snapshot.");
    }

    private sealed class WorkstationFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private WorkstationFixture(
            SqliteConnection connection,
            MesDbContext database,
            WorkflowApplicationService workflows,
            ProfileConfiguration profile,
            AdjustableTimeProvider clock)
        {
            _connection = connection;
            Database = database;
            Workflows = workflows;
            Profile = profile;
            Clock = clock;
        }

        public MesDbContext Database { get; }
        public WorkflowApplicationService Workflows { get; }
        public ProfileConfiguration Profile { get; }
        public AdjustableTimeProvider Clock { get; }

        public static async Task<WorkstationFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var database = new MesDbContext(
                new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options);
            await database.Database.EnsureCreatedAsync();
            var clock = new AdjustableTimeProvider(
                new DateTimeOffset(2026, 9, 16, 1, 0, 0, TimeSpan.Zero));
            var profile = ProfileConfiguration.Default with
            {
                Features = ProfileConfiguration.Default.Features with { UseSimulator = true },
                WorkflowDevices = ProfileConfiguration.Default.WorkflowDevices.Concat([new WorkflowDeviceProfile
                {
                    DeviceId = "SAMPLE-WORKSTATION-01",
                    DeviceFamily = WorkflowDeviceFamilyIds.SampleWorkstation,
                    CapabilityIds = [WorkflowCapabilityIds.SampleWorkstationStartExistingTask],
                    Enabled = true,
                    ControlEnabled = true
                }]).ToArray()
            };
            var validator = new WorkflowValidator(
                BuiltInWorkflowCatalog.Create(),
                WorkflowPublicationContext.FromProfile(profile));
            var reader = new MesWorkflowVersionReader(database);
            var authorizer = new ConfiguredWorkflowRunControlAuthorizer(Options.Create(
                new WorkflowRunControlAuthorizationOptions
                {
                    Operators = [new WorkflowRunControlOperatorOptions
                    {
                        Name = "operator-1",
                        Permissions = [WorkflowRunControlPermissions.CompleteManualTask]
                    }]
                }));
            var workflows = new WorkflowApplicationService(
                database,
                reader,
                new WorkflowRuntimeExecutor(
                    reader,
                    validator,
                    admissionPolicies: [new ActiveProfileWorkflowAdmissionPolicy(profile)]),
                validator,
                clock,
                authorizer);
            return new WorkstationFixture(connection, database, workflows, profile, clock);
        }

        public RecordingWorkstation CreateGateway(params Observation[] observations) =>
            new(Clock, observations);

        public WorkflowSampleWorkstationDispatcher CreateDispatcher(
            RecordingWorkstation gateway,
            int startObservationTimeoutMs = 100,
            ProfileConfiguration? profile = null) => new(
            Workflows,
            gateway,
            gateway,
            profile ?? Profile,
            new WorkflowSampleWorkstationWorkerOptions
            {
                Enabled = true,
                PollIntervalMs = 1,
                ReadinessRetryIntervalMs = 1,
                StartObservationTimeoutMs = startObservationTimeoutMs,
                CompletionTimeoutMs = 100
            },
            Clock);

        public async Task<Guid> PublishExecuteAndConfirmAsync(
            string deviceId = "SAMPLE-WORKSTATION-01")
        {
            var runId = await PublishAndExecuteAsync(deviceId);
            await ConfirmAsync(runId);
            return runId;
        }

        public async Task<Guid> PublishAndExecuteAsync(
            string deviceId = "SAMPLE-WORKSTATION-01")
        {
            var definition = CreateWorkflow(deviceId);
            var draft = await Workflows.CreateDraftAsync(definition, "test", CancellationToken.None);
            var validation = await Workflows.ValidateVersionAsync(
                draft.WorkflowId,
                draft.Version,
                CancellationToken.None);
            Assert.True(validation.IsValid, string.Join("; ", validation.Issues.Select(issue => issue.Message)));
            await Workflows.PublishAsync(draft.WorkflowId, draft.Version, "test", CancellationToken.None);
            var execution = await Workflows.ExecuteAsync(new WorkflowExecutionRequest
            {
                WorkflowId = draft.WorkflowId,
                Version = draft.Version,
                RequestId = Guid.NewGuid(),
                RequestedBy = "test"
            }, CancellationToken.None);
            Assert.True(execution.IsAccepted, execution.RejectionReason);
            return execution.ExecutionId;
        }

        public async Task ConfirmAsync(Guid runId)
        {
            var manual = Assert.Single(
                await Workflows.ListNodeExecutionsAsync(runId, CancellationToken.None),
                item => item.NodeTypeId == WorkflowGraphNodeTypeIds.ManualConfirmation);
            Assert.Equal(WorkflowNodeExecutionStatus.WaitingForSignal, manual.Status);
            await Workflows.CompleteManualConfirmationAsync(
                runId,
                manual.Id,
                new WorkflowManualConfirmationRequest
                {
                    RequestId = Guid.NewGuid(),
                    Actor = "operator-1",
                    Reason = "厂家主程序已完成整机初始化",
                    Outcome = WorkflowManualConfirmationOutcome.Confirmed
                },
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static WorkflowDefinition CreateWorkflow(string deviceId)
        {
            var catalog = BuiltInWorkflowCatalog.Create();
            var start = Node(catalog, WorkflowGraphNodeTypeIds.Start, "开始", 1);
            var manual = Node(
                catalog,
                WorkflowGraphNodeTypeIds.ManualConfirmation,
                "整机初始化确认",
                2,
                new Dictionary<string, string?>
                {
                    [WorkflowNodeConfigurationKeys.Prompt] = "已在厂家主程序完成整机初始化",
                    [WorkflowNodeConfigurationKeys.TimeoutSeconds] = "3600",
                    [WorkflowNodeConfigurationKeys.RequireComment] = "false"
                });
            var workstation = Node(
                catalog,
                WorkflowGraphNodeTypeIds.SampleWorkstationExecuteExistingTask,
                "开盖分液",
                3,
                new Dictionary<string, string?>
                {
                    [WorkflowNodeConfigurationKeys.DeviceId] = deviceId,
                    [WorkflowNodeConfigurationKeys.TaskNo] = "TEST-001"
                });
            var end = Node(catalog, WorkflowGraphNodeTypeIds.End, "结束", 4);
            var edges = new[]
            {
                Edge(start, "success", manual),
                Edge(manual, "success", workstation),
                Edge(manual, "timeout", end, WorkflowEdgeKind.Timeout),
                Edge(manual, "cancelled", end, WorkflowEdgeKind.Cancelled),
                Edge(workstation, "success", end)
            };
            start = start with { NextNodeIds = [manual.Id] };
            manual = manual with { NextNodeIds = [workstation.Id, end.Id] };
            workstation = workstation with { NextNodeIds = [end.Id] };
            return new WorkflowDefinition
            {
                Id = Guid.NewGuid(),
                Name = "正式开盖分液测试",
                SchemaVersion = WorkflowGraphDocument.CurrentSchemaVersion,
                Nodes = [start, manual, workstation, end],
                Edges = edges
            };
        }

        private static WorkflowNode Node(
            WorkflowCatalogSet catalog,
            string nodeTypeId,
            string name,
            int order,
            IReadOnlyDictionary<string, string?>? configuration = null)
        {
            var definition = catalog.NodeTypes.GetLatest(nodeTypeId)!;
            return new WorkflowNode
            {
                Type = WorkflowGraphNodeTypeIds.ToContractType(nodeTypeId),
                NodeTypeId = nodeTypeId,
                SchemaVersion = definition.SchemaVersion,
                Name = name,
                Order = order,
                Ports = definition.Ports,
                Configuration = configuration ?? new Dictionary<string, string?>()
            };
        }

        private static WorkflowEdgeDefinition Edge(
            WorkflowNode source,
            string sourcePort,
            WorkflowNode target,
            WorkflowEdgeKind kind = WorkflowEdgeKind.Success) => new()
        {
            SourceNodeId = source.Id,
            SourcePort = sourcePort,
            TargetNodeId = target.Id,
            TargetPort = "in",
            Kind = kind
        };
    }
}
