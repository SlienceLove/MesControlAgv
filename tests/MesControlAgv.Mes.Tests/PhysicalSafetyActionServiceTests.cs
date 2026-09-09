using System.Text.Json;
using System.Text.Json.Serialization;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MesControlAgv.Mes.Tests;

public sealed class PhysicalSafetyActionServiceTests
{
    private static readonly JsonSerializerOptions WorkflowJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

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
    public async Task Agv_release_rechecks_readiness_after_prepared_reservation_before_gateway_write()
    {
        await using var database = CreateDatabase();
        var gateway = new SafetyActionAgvGateway();
        var readiness = new ChangesAfterFirstCheckReadinessState();
        var service = new PhysicalSafetyActionService(
            database,
            gateway,
            null,
            PhysicalProfile(),
            readiness);

        var result = await service.ReleaseAgvAsync(
            "AGV-01",
            new PhysicalAgvReleaseRequest(
                Guid.NewGuid(),
                "operator-a",
                "workflow final release",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                "supervisor-before-change"),
            CancellationToken.None);

        Assert.Equal(PhysicalSafetyActionStatuses.Rejected, result.Status);
        Assert.Contains(PhysicalReadinessReasonCodes.EpochMismatch, result.ResultSummary);
        Assert.Equal(2, readiness.CheckCount);
        Assert.Equal(0, gateway.ReleaseCalls);
        var persisted = await database.PhysicalSafetyActions.SingleAsync();
        Assert.Equal(PhysicalSafetyActionStatuses.Rejected, persisted.Status);
    }

    [Fact]
    public async Task Final_move_release_identity_changes_with_epoch_or_supervisor_session()
    {
        await using var database = CreateDatabase();
        var gateway = new SafetyActionAgvGateway();
        var service = new PhysicalSafetyActionService(
            database,
            gateway,
            null,
            PhysicalProfile(),
            new AnyEpochReadyPhysicalReadinessState());
        var runId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        var first = await service.ReleaseFinalMoveAsync(
            runId,
            nodeId,
            operationId,
            "AGV-01",
            "operator-a",
            11,
            "supervisor-session-1",
            WorkflowStepCompletionOutcome.Succeeded,
            CancellationToken.None);
        var epochChanged = await service.ReleaseFinalMoveAsync(
            runId,
            nodeId,
            operationId,
            "AGV-01",
            "operator-a",
            12,
            "supervisor-session-1",
            WorkflowStepCompletionOutcome.Succeeded,
            CancellationToken.None);
        var supervisorChanged = await service.ReleaseFinalMoveAsync(
            runId,
            nodeId,
            operationId,
            "AGV-01",
            "operator-a",
            11,
            "supervisor-session-2",
            WorkflowStepCompletionOutcome.Succeeded,
            CancellationToken.None);

        Assert.Equal(PhysicalSafetyActionStatuses.Succeeded, first.Status);
        Assert.Equal(PhysicalSafetyActionStatuses.Succeeded, epochChanged.Status);
        Assert.Equal(PhysicalSafetyActionStatuses.Succeeded, supervisorChanged.Status);
        Assert.Equal(
            3,
            new[] { first.RequestId, epochChanged.RequestId, supervisorChanged.RequestId }
                .Distinct()
                .Count());
        Assert.Equal(
            3,
            new[] { first.Fingerprint, epochChanged.Fingerprint, supervisorChanged.Fingerprint }
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(3, gateway.ReleaseCalls);
        Assert.Equal(3, await database.PhysicalSafetyActions.CountAsync());
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
        await using var fixture = await WorkflowCorrelationFixture.CreateAsync(
            WorkflowDeviceOperationStatus.Running,
            WorkflowNodeExecutionStatus.Running,
            WorkflowRuntimeStatus.Running);
        var gateway = new SafetyActionAuboGateway();
        var service = new PhysicalSafetyActionService(
            fixture.Database,
            null,
            gateway,
            PhysicalProfile(),
            workflows: fixture.Workflows);

        var result = await service.StopAuboAsync(
            "AUBO-01",
            new PhysicalAuboStopRequest(
                Guid.NewGuid(),
                fixture.OperationId,
                "operator-a",
                fixture.Correlation),
            CancellationToken.None);

        Assert.Equal(PhysicalSafetyActionStatuses.Succeeded, result.Status);
        Assert.Equal(["status", "stop"], gateway.Calls);
        Assert.Equal(fixture.OperationId, gateway.OperationId);
        Assert.Equal(
            fixture.Correlation.EffectiveCorrelationId,
            gateway.Correlation?.EffectiveCorrelationId);
    }

    [Fact]
    public async Task Aubo_stop_accepts_a_durable_unknown_workflow_operation_for_safety_cleanup()
    {
        await using var fixture = await WorkflowCorrelationFixture.CreateAsync(
            WorkflowDeviceOperationStatus.Unknown,
            WorkflowNodeExecutionStatus.Unknown,
            WorkflowRuntimeStatus.Unknown);
        var gateway = new SafetyActionAuboGateway();
        var service = new PhysicalSafetyActionService(
            fixture.Database,
            null,
            gateway,
            PhysicalProfile(),
            workflows: fixture.Workflows);

        var result = await service.StopAuboAsync(
            "AUBO-01",
            new PhysicalAuboStopRequest(
                Guid.NewGuid(),
                fixture.OperationId,
                "operator-a",
                fixture.Correlation),
            CancellationToken.None);

        Assert.Equal(PhysicalSafetyActionStatuses.Succeeded, result.Status);
        Assert.Equal(1, gateway.StopCalls);
    }

    [Theory]
    [InlineData("workflow-run")]
    [InlineData("node-execution")]
    [InlineData("device-operation")]
    [InlineData("request-id")]
    [InlineData("attempt")]
    [InlineData("capability")]
    [InlineData("physical-device")]
    [InlineData("correlation-id")]
    public async Task Aubo_stop_rejects_every_durable_correlation_identity_mismatch_before_gateway_write(
        string mismatch)
    {
        await using var fixture = await WorkflowCorrelationFixture.CreateAsync(
            WorkflowDeviceOperationStatus.Running,
            WorkflowNodeExecutionStatus.Running,
            WorkflowRuntimeStatus.Running);
        var correlation = fixture.Correlation;
        var operationId = fixture.OperationId;
        switch (mismatch)
        {
            case "workflow-run":
                correlation = correlation with { WorkflowRunId = Guid.NewGuid() };
                break;
            case "node-execution":
                correlation = correlation with { WorkflowNodeExecutionId = Guid.NewGuid() };
                break;
            case "device-operation":
                operationId = Guid.NewGuid();
                correlation = correlation with { DeviceOperationId = operationId };
                break;
            case "request-id":
                correlation = correlation with { RequestId = Guid.NewGuid() };
                break;
            case "attempt":
                correlation = correlation with { Attempt = correlation.Attempt + 1 };
                break;
            case "capability":
                fixture.DeviceOperation.CapabilityId = WorkflowCapabilityIds.AgvNavigateToStation;
                await fixture.Database.SaveChangesAsync();
                break;
            case "physical-device":
                fixture.DeviceOperation.DeviceId = "AUBO-OTHER";
                await fixture.Database.SaveChangesAsync();
                break;
            case "correlation-id":
                correlation = correlation with { CorrelationId = "different-correlation" };
                break;
            default:
                throw new InvalidOperationException($"Unknown test case '{mismatch}'.");
        }

        var gateway = new SafetyActionAuboGateway();
        var service = new PhysicalSafetyActionService(
            fixture.Database,
            null,
            gateway,
            PhysicalProfile(),
            workflows: fixture.Workflows);

        var exception = await Assert.ThrowsAsync<AuboArmCorrelationException>(() =>
            service.StopAuboAsync(
                "AUBO-01",
                new PhysicalAuboStopRequest(
                    Guid.NewGuid(),
                    operationId,
                    "operator-a",
                    correlation),
                CancellationToken.None));

        Assert.Equal(AuboArmWorkflowCorrelationValidator.InvalidCorrelationCode, exception.Code);
        Assert.StartsWith(
            $"{AuboArmWorkflowCorrelationValidator.InvalidCorrelationCode}:",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, gateway.StopCalls);
        Assert.Empty(gateway.Calls);
    }

    [Theory]
    [InlineData("load")]
    [InlineData("run")]
    public async Task Normal_aubo_program_writes_still_reject_unknown_durable_operations(
        string operation)
    {
        await using var fixture = await WorkflowCorrelationFixture.CreateAsync(
            WorkflowDeviceOperationStatus.Unknown,
            WorkflowNodeExecutionStatus.Running,
            WorkflowRuntimeStatus.Running);
        var gateway = new SafetyActionAuboGateway();

        var exception = await Assert.ThrowsAsync<AuboArmCorrelationException>(async () =>
        {
            var correlation = await AuboArmWorkflowCorrelationValidator.ValidateAsync(
                "AUBO-01",
                fixture.OperationId,
                fixture.Correlation,
                fixture.Workflows,
                NullLogger.Instance,
                operation,
                CancellationToken.None);
            if (operation == "load")
            {
                await gateway.LoadProgramAsync(
                    "AUBO-01",
                    "program.lua",
                    "operator-a",
                    fixture.OperationId,
                    correlation,
                    CancellationToken.None);
            }
            else
            {
                await gateway.RunProgramAsync(
                    "AUBO-01",
                    "program.lua",
                    "operator-a",
                    fixture.OperationId,
                    correlation,
                    CancellationToken.None);
            }
        });

        Assert.Equal(AuboArmWorkflowCorrelationValidator.InvalidCorrelationCode, exception.Code);
        Assert.Equal(0, gateway.LoadCalls);
        Assert.Equal(0, gateway.RunCalls);
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
        public int LoadCalls { get; private set; }
        public int RunCalls { get; private set; }
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

        public Task<AuboArmProgramOperationResponse> LoadProgramAsync(
            string deviceId,
            string programName,
            string operatorName,
            Guid operationId,
            AuboArmOperationCorrelation? correlation,
            CancellationToken cancellationToken)
        {
            LoadCalls++;
            Calls.Add("load");
            return Task.FromResult(ProgramResult(
                operationId,
                deviceId,
                programName,
                "load",
                operatorName,
                AuboArmProgramOperationState.Loaded));
        }

        public Task<AuboArmProgramOperationResponse> RunProgramAsync(
            string deviceId,
            string? programName,
            string operatorName,
            Guid operationId,
            AuboArmOperationCorrelation? correlation,
            CancellationToken cancellationToken)
        {
            RunCalls++;
            Calls.Add("run");
            return Task.FromResult(ProgramResult(
                operationId,
                deviceId,
                programName ?? "program.lua",
                "run",
                operatorName,
                AuboArmProgramOperationState.Running));
        }

        private static AuboArmProgramOperationResponse ProgramResult(
            Guid operationId,
            string deviceId,
            string programName,
            string operation,
            string operatorName,
            AuboArmProgramOperationState state) => new(
                operationId,
                deviceId,
                programName,
                operation,
                operatorName,
                state,
                state == AuboArmProgramOperationState.Running
                    ? AuboArmRuntimeState.Running
                    : AuboArmRuntimeState.Stopped,
                state.ToString(),
                programName,
                0,
                null,
                true,
                DateTimeOffset.UtcNow);
    }

    private sealed class WorkflowCorrelationFixture : IAsyncDisposable
    {
        private WorkflowCorrelationFixture(
            SqliteConnection connection,
            MesDbContext database,
            WorkflowApplicationService workflows,
            WorkflowDeviceOperationRecord deviceOperation,
            AuboArmOperationCorrelation correlation)
        {
            Connection = connection;
            Database = database;
            Workflows = workflows;
            DeviceOperation = deviceOperation;
            Correlation = correlation;
        }

        private SqliteConnection Connection { get; }

        public MesDbContext Database { get; }

        public WorkflowApplicationService Workflows { get; }

        public WorkflowDeviceOperationRecord DeviceOperation { get; }

        public AuboArmOperationCorrelation Correlation { get; }

        public Guid OperationId => DeviceOperation.OperationId;

        public static async Task<WorkflowCorrelationFixture> CreateAsync(
            WorkflowDeviceOperationStatus deviceOperationStatus,
            WorkflowNodeExecutionStatus nodeStatus,
            WorkflowRuntimeStatus runStatus)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var database = new MesDbContext(
                new DbContextOptionsBuilder<MesDbContext>()
                    .UseSqlite(connection)
                    .Options);
            try
            {
                await database.Database.EnsureCreatedAsync();
                var runId = Guid.NewGuid();
                var workflowId = Guid.NewGuid();
                var requestId = Guid.NewGuid();
                var nodeExecutionId = Guid.NewGuid();
                var nodeId = Guid.NewGuid();
                var operationId = Guid.NewGuid();
                const int attempt = 1;
                const string correlationId = "workflow-correlation";
                var now = DateTime.UtcNow;
                var executionRequest = new WorkflowExecutionRequest
                {
                    WorkflowId = workflowId,
                    Version = 1,
                    RequestId = requestId,
                    RequestedBy = "test",
                    CorrelationId = correlationId
                };
                var executionResult = new WorkflowExecutionResult
                {
                    Status = WorkflowExecutionStatus.Accepted,
                    RequestId = requestId,
                    ExecutionId = runId,
                    WorkflowId = workflowId,
                    Version = 1,
                    RequestedAt = new DateTimeOffset(now, TimeSpan.Zero)
                };

                database.WorkflowExecutions.Add(new WorkflowExecutionRecord
                {
                    RequestId = requestId,
                    Fingerprint = $"workflow-fixture-{runId:N}",
                    WorkflowId = workflowId,
                    Version = 1,
                    ExecutionId = runId,
                    Outcome = WorkflowExecutionStatus.Accepted.ToString(),
                    RequestJson = JsonSerializer.Serialize(executionRequest, WorkflowJsonOptions),
                    ResultJson = JsonSerializer.Serialize(executionResult, WorkflowJsonOptions),
                    CreatedAtUtc = now,
                    RuntimeStatus = runStatus.ToString(),
                    CurrentNodeId = nodeId,
                    TransportOperationId = operationId,
                    Attempt = attempt,
                    UpdatedAtUtc = now
                });
                database.WorkflowNodeExecutions.Add(new WorkflowNodeExecutionRecord
                {
                    Id = nodeExecutionId,
                    WorkflowRunId = runId,
                    WorkflowId = workflowId,
                    Version = 1,
                    StepRequestId = Guid.NewGuid(),
                    NodeId = nodeId,
                    NodeTypeId = WorkflowGraphNodeTypeIds.RobotExecuteProgram,
                    NodeName = "fixture robot program",
                    Attempt = attempt,
                    Status = nodeStatus.ToString(),
                    InputJson = JsonSerializer.Serialize(
                        new Dictionary<string, string?>
                        {
                            [WorkflowNodeConfigurationKeys.DeviceId] = "AUBO-01",
                            [WorkflowNodeConfigurationKeys.ProgramName] = "program.lua"
                        },
                        WorkflowJsonOptions),
                    OutputJson = "{}",
                    StartedAtUtc = now,
                    CompletedAtUtc = nodeStatus == WorkflowNodeExecutionStatus.Unknown ? now : null,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                var deviceOperation = new WorkflowDeviceOperationRecord
                {
                    OperationId = operationId,
                    WorkflowRunId = runId,
                    NodeExecutionId = nodeExecutionId,
                    RequestId = requestId,
                    Attempt = attempt,
                    CapabilityId = WorkflowCapabilityIds.RobotExecuteProgram,
                    DeviceId = "AUBO-01",
                    IdempotencyKey = operationId.ToString("N"),
                    CorrelationId = correlationId,
                    Status = deviceOperationStatus.ToString(),
                    RequestSummaryJson = "{}",
                    ResultSummaryJson = "{}",
                    RequestedAtUtc = now,
                    CompletedAtUtc = deviceOperationStatus == WorkflowDeviceOperationStatus.Unknown ? now : null,
                    ReconciledAtUtc = deviceOperationStatus == WorkflowDeviceOperationStatus.Unknown ? now : null,
                    UpdatedAtUtc = now
                };
                database.WorkflowDeviceOperations.Add(deviceOperation);
                await database.SaveChangesAsync();

                var validator = new WorkflowValidator();
                var reader = new MesWorkflowVersionReader(database);
                var workflows = new WorkflowApplicationService(
                    database,
                    reader,
                    new WorkflowRuntimeExecutor(reader, validator),
                    validator);
                return new WorkflowCorrelationFixture(
                    connection,
                    database,
                    workflows,
                    deviceOperation,
                    AuboArmOperationCorrelation.Create(
                        runId,
                        nodeExecutionId,
                        operationId,
                        requestId,
                        correlationId,
                        attempt));
            }
            catch
            {
                await database.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await Connection.DisposeAsync();
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

    private sealed class ChangesAfterFirstCheckReadinessState : IPhysicalReadinessState
    {
        public bool Enabled => true;

        public int CheckCount { get; private set; }

        public PhysicalReadinessResponse GetSnapshot() => new()
        {
            Enabled = true,
            SupervisorInstanceId = "supervisor-before-change"
        };

        public bool TryGetDevice(string deviceId, out PhysicalDeviceReadinessSnapshot snapshot)
        {
            snapshot = new PhysicalDeviceReadinessSnapshot();
            return false;
        }

        public bool IsCurrentAndReady(string deviceId, long? expectedEpoch, out string? validationReason) =>
            IsCurrentAndReady(
                deviceId,
                expectedEpoch,
                "supervisor-before-change",
                out validationReason);

        public bool IsCurrentAndReady(
            string deviceId,
            long? expectedEpoch,
            string? expectedSupervisorInstanceId,
            out string? validationReason)
        {
            CheckCount++;
            var ready = CheckCount == 1;
            validationReason = ready ? null : PhysicalReadinessReasonCodes.EpochMismatch;
            return ready;
        }

        public bool AcknowledgeAuthorization(string deviceId, long expectedEpoch) => false;
    }

    private sealed class AnyEpochReadyPhysicalReadinessState : IPhysicalReadinessState
    {
        public bool Enabled => true;

        public PhysicalReadinessResponse GetSnapshot() => new()
        {
            Enabled = true,
            SupervisorInstanceId = "any-session"
        };

        public bool TryGetDevice(string deviceId, out PhysicalDeviceReadinessSnapshot snapshot)
        {
            snapshot = new PhysicalDeviceReadinessSnapshot
            {
                DeviceId = deviceId,
                DeviceEpoch = 1,
                State = PhysicalDeviceReadinessState.Ready
            };
            return true;
        }

        public bool IsCurrentAndReady(string deviceId, long? expectedEpoch, out string? validationReason)
        {
            validationReason = null;
            return true;
        }

        public bool IsCurrentAndReady(
            string deviceId,
            long? expectedEpoch,
            string? expectedSupervisorInstanceId,
            out string? validationReason)
        {
            validationReason = null;
            return true;
        }

        public bool AcknowledgeAuthorization(string deviceId, long expectedEpoch) => true;
    }
}
