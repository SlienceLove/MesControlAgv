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

public sealed class WorkflowRunControlTests
{
    [Fact]
    public async Task Pause_blocks_ready_claims_and_resume_restores_prepared_state_with_audit_and_idempotency()
    {
        await using var fixture = await WorkflowRunControlFixture.CreateAsync();
        var executionId = await fixture.AdmitAsync(WorkflowTestDefinitions.CreateMoveWorkflow(null, "SAMPLE_01"));
        var pauseRequest = Request("operator-1", "Hold for sample verification");

        var paused = await fixture.Service.PauseRunAsync(executionId, pauseRequest, CancellationToken.None);
        var replay = await fixture.Service.PauseRunAsync(executionId, pauseRequest, CancellationToken.None);

        Assert.Equal(WorkflowRuntimeStatus.Paused, paused.Run.RuntimeStatus);
        Assert.False(paused.IsIdempotentReplay);
        Assert.True(replay.IsIdempotentReplay);
        Assert.Empty(await fixture.Service.ListSimulatorDispatchableNodesAsync(CancellationToken.None));
        var readyNode = Assert.Single(await fixture.Service.ListNodeExecutionsAsync(executionId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ClaimNodeExecutionAsync(readyNode.Id, CancellationToken.None));

        var resumed = await fixture.Service.ResumeRunAsync(
            executionId,
            Request("operator-1", "Sample verification completed"),
            CancellationToken.None);

        Assert.Equal(WorkflowRuntimeStatus.Prepared, resumed.Run.RuntimeStatus);
        Assert.Single(await fixture.Service.ListSimulatorDispatchableNodesAsync(CancellationToken.None));
        var audits = await fixture.Database.WorkflowAudits
            .Where(item => item.ExecutionId == executionId &&
                           (item.EventType == "WorkflowRunPaused" || item.EventType == "WorkflowRunResumed"))
            .OrderBy(item => item.OccurredAtUtc)
            .ToListAsync();
        Assert.Collection(
            audits,
            audit =>
            {
                Assert.Equal(pauseRequest.RequestId, audit.RequestId);
                Assert.Equal("operator-1", audit.Actor);
                Assert.Equal("Hold for sample verification", audit.Reason);
            },
            audit => Assert.Equal("Sample verification completed", audit.Reason));
    }

    [Fact]
    public async Task Pausing_a_running_node_allows_reconciliation_but_holds_the_following_node()
    {
        await using var fixture = await WorkflowRunControlFixture.CreateAsync();
        var executionId = await fixture.AdmitAsync(
            WorkflowTestDefinitions.CreateMoveWorkflow(null, "SAMPLE_01", "ST_OPEN_01"));
        var first = Assert.Single(await fixture.Service.ListSimulatorDispatchableNodesAsync(CancellationToken.None));
        var claimed = await fixture.Service.ClaimNodeExecutionAsync(first.NodeExecution.Id, CancellationToken.None);

        await fixture.Service.PauseRunAsync(
            executionId,
            Request("operator-1", "Stop before the next station"),
            CancellationToken.None);
        var reconciled = await fixture.Service.CompleteNodeExecutionAsync(
            claimed.NodeExecution.Id,
            new WorkflowNodeExecutionCompletionRequest
            {
                DeviceOperationId = claimed.DeviceOperation!.OperationId,
                Outcome = WorkflowStepCompletionOutcome.Succeeded
            },
            CancellationToken.None);

        Assert.Equal(WorkflowRuntimeStatus.Paused, reconciled.RuntimeStatus);
        Assert.Empty(await fixture.Service.ListSimulatorDispatchableNodesAsync(CancellationToken.None));
        var nodes = await fixture.Service.ListNodeExecutionsAsync(executionId, CancellationToken.None);
        Assert.Equal(WorkflowNodeExecutionStatus.Succeeded, nodes[0].Status);
        Assert.Equal(WorkflowNodeExecutionStatus.Ready, nodes[1].Status);

        var resumed = await fixture.Service.ResumeRunAsync(
            executionId,
            Request("operator-1", "Continue to the next station"),
            CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Prepared, resumed.Run.RuntimeStatus);
        Assert.Single(await fixture.Service.ListSimulatorDispatchableNodesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Physical_pause_remains_available_but_resume_is_admitted_before_state_migration()
    {
        await using var fixture = await WorkflowRunControlFixture.CreateAsync();
        var executionId = await fixture.AdmitAsync(
            WorkflowTestDefinitions.CreateMoveWorkflow(null, "SAMPLE_01"),
            physical: true);

        var paused = await fixture.Service.PauseRunAsync(
            executionId,
            Request("operator-1", "Hold physical run"),
            CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Paused, paused.Run.RuntimeStatus);

        fixture.DisablePhysicalSupervisor();
        var exception = await Assert.ThrowsAsync<PhysicalExecutionAdmissionException>(() =>
            fixture.Service.ResumeRunAsync(
                executionId,
                Request("operator-1", "Resume physical run"),
                CancellationToken.None));

        Assert.Equal(PhysicalReadinessReasonCodes.SupervisorDisabled, exception.Code);
        Assert.Equal(
            WorkflowRuntimeStatus.Paused,
            (await fixture.Service.GetExecutionAsync(executionId, CancellationToken.None))!.RuntimeStatus);
    }

    [Fact]
    public async Task Cancel_rejects_active_or_unknown_work_and_cancels_only_a_quiescent_ready_node()
    {
        await using var fixture = await WorkflowRunControlFixture.CreateAsync();
        var cancellableId = await fixture.AdmitAsync(WorkflowTestDefinitions.CreateMoveWorkflow(null, "SAMPLE_01"));

        var cancelled = await fixture.Service.CancelRunAsync(
            cancellableId,
            Request("operator-1", "Experiment withdrawn"),
            CancellationToken.None);

        Assert.Equal(WorkflowRuntimeStatus.Cancelled, cancelled.Run.RuntimeStatus);
        Assert.Null(cancelled.Run.PendingStepRequest);
        Assert.Equal(
            WorkflowNodeExecutionStatus.Cancelled,
            Assert.Single(await fixture.Service.ListNodeExecutionsAsync(cancellableId, CancellationToken.None)).Status);

        var activeId = await fixture.AdmitAsync(WorkflowTestDefinitions.CreateMoveWorkflow(null, "ST_OPEN_01"));
        var ready = Assert.Single(await fixture.Service.ListSimulatorDispatchableNodesAsync(CancellationToken.None));
        var claimed = await fixture.Service.ClaimNodeExecutionAsync(ready.NodeExecution.Id, CancellationToken.None);
        await Assert.ThrowsAsync<WorkflowRunControlConflictException>(() => fixture.Service.CancelRunAsync(
            activeId,
            Request("operator-1", "Cannot cancel active I/O"),
            CancellationToken.None));

        await fixture.Service.CompleteNodeExecutionAsync(
            claimed.NodeExecution.Id,
            new WorkflowNodeExecutionCompletionRequest
            {
                DeviceOperationId = claimed.DeviceOperation!.OperationId,
                Outcome = WorkflowStepCompletionOutcome.Unknown,
                Error = "Adapter response timeout"
            },
            CancellationToken.None);
        await Assert.ThrowsAsync<WorkflowRunControlConflictException>(() => fixture.Service.CancelRunAsync(
            activeId,
            Request("operator-1", "Unknown requires reconciliation"),
            CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_resolution_records_a_human_conclusion_without_creating_a_new_device_operation()
    {
        await using var fixture = await WorkflowRunControlFixture.CreateAsync();
        var executionId = await fixture.AdmitAsync(
            WorkflowTestDefinitions.CreateMoveWorkflow(null, "SAMPLE_01", "ST_OPEN_01"));
        var ready = Assert.Single(await fixture.Service.ListSimulatorDispatchableNodesAsync(CancellationToken.None));
        var claimed = await fixture.Service.ClaimNodeExecutionAsync(ready.NodeExecution.Id, CancellationToken.None);
        await fixture.Service.CompleteNodeExecutionAsync(
            claimed.NodeExecution.Id,
            new WorkflowNodeExecutionCompletionRequest
            {
                DeviceOperationId = claimed.DeviceOperation!.OperationId,
                Outcome = WorkflowStepCompletionOutcome.Unknown,
                Error = "Adapter response timeout"
            },
            CancellationToken.None);
        var resolution = new WorkflowUnknownResolutionRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "supervisor-1",
            Reason = "现场任务记录确认已到达 SAMPLE_01",
            NodeExecutionId = claimed.NodeExecution.Id,
            Outcome = WorkflowUnknownResolutionOutcome.ConfirmedSucceeded
        };

        var resolved = await fixture.Service.ResolveUnknownAsync(executionId, resolution, CancellationToken.None);
        var replay = await fixture.Service.ResolveUnknownAsync(executionId, resolution, CancellationToken.None);

        Assert.Equal(WorkflowRuntimeStatus.Prepared, resolved.Run.RuntimeStatus);
        Assert.True(replay.IsIdempotentReplay);
        var nodes = await fixture.Service.ListNodeExecutionsAsync(executionId, CancellationToken.None);
        Assert.Equal(WorkflowNodeExecutionStatus.Succeeded, nodes[0].Status);
        Assert.Equal(WorkflowNodeExecutionStatus.Ready, nodes[1].Status);
        var operation = Assert.Single(await fixture.Service.ListDeviceOperationsAsync(executionId, CancellationToken.None));
        Assert.Equal(WorkflowDeviceOperationStatus.Succeeded, operation.Status);
        Assert.NotNull(operation.ReconciledAt);
        Assert.Single(await fixture.Database.WorkflowDeviceOperations.ToListAsync());
        var audit = Assert.Single(await fixture.Database.WorkflowAudits
            .Where(item => item.EventType == "WorkflowUnknownResolved")
            .ToListAsync());
        Assert.Equal(resolution.RequestId, audit.RequestId);
        Assert.Equal("supervisor-1", audit.Actor);
        Assert.Equal(resolution.Reason, audit.Reason);
    }

    [Fact]
    public async Task Unknown_resolution_can_record_confirmed_failure_and_terminate_without_retry()
    {
        await using var fixture = await WorkflowRunControlFixture.CreateAsync();
        var executionId = await fixture.AdmitAsync(
            WorkflowTestDefinitions.CreateMoveWorkflow(null, "SAMPLE_01"));
        var ready = Assert.Single(await fixture.Service.ListSimulatorDispatchableNodesAsync(CancellationToken.None));
        var claimed = await fixture.Service.ClaimNodeExecutionAsync(ready.NodeExecution.Id, CancellationToken.None);
        await fixture.Service.CompleteNodeExecutionAsync(
            claimed.NodeExecution.Id,
            new WorkflowNodeExecutionCompletionRequest
            {
                DeviceOperationId = claimed.DeviceOperation!.OperationId,
                Outcome = WorkflowStepCompletionOutcome.Unknown,
                Error = "Adapter response timeout"
            },
            CancellationToken.None);
        const string reason = "Field inspection confirms the move did not complete";

        var resolved = await fixture.Service.ResolveUnknownAsync(
            executionId,
            new WorkflowUnknownResolutionRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = "supervisor-1",
                Reason = reason,
                NodeExecutionId = claimed.NodeExecution.Id,
                Outcome = WorkflowUnknownResolutionOutcome.ConfirmedFailed
            },
            CancellationToken.None);

        Assert.Equal(WorkflowRuntimeStatus.Failed, resolved.Run.RuntimeStatus);
        Assert.Equal(reason, resolved.Run.LastError);
        var node = Assert.Single(await fixture.Service.ListNodeExecutionsAsync(executionId, CancellationToken.None));
        Assert.Equal(WorkflowNodeExecutionStatus.Failed, node.Status);
        Assert.Equal(reason, node.LastError);
        var operation = Assert.Single(await fixture.Service.ListDeviceOperationsAsync(executionId, CancellationToken.None));
        Assert.Equal(WorkflowDeviceOperationStatus.Failed, operation.Status);
        Assert.Equal(reason, operation.LastError);
        Assert.Empty(await fixture.Service.ListSimulatorDispatchableNodesAsync(CancellationToken.None));
        Assert.Single(await fixture.Database.WorkflowDeviceOperations.ToListAsync());
    }

    [Fact]
    public async Task Pre_g4_unknown_projection_is_backfilled_before_manual_resolution()
    {
        await using var fixture = await WorkflowRunControlFixture.CreateAsync();
        var executionId = await fixture.AdmitAsync(
            WorkflowTestDefinitions.CreateMoveWorkflow(null, "SAMPLE_01"));
        var ready = Assert.Single(await fixture.Service.ListSimulatorDispatchableNodesAsync(CancellationToken.None));
        var claimed = await fixture.Service.ClaimNodeExecutionAsync(ready.NodeExecution.Id, CancellationToken.None);
        await fixture.Service.CompleteNodeExecutionAsync(
            claimed.NodeExecution.Id,
            new WorkflowNodeExecutionCompletionRequest
            {
                DeviceOperationId = claimed.DeviceOperation!.OperationId,
                Outcome = WorkflowStepCompletionOutcome.Unknown,
                Error = "Legacy ambiguous outcome"
            },
            CancellationToken.None);
        fixture.Database.WorkflowDeviceOperations.RemoveRange(fixture.Database.WorkflowDeviceOperations);
        fixture.Database.WorkflowNodeExecutions.RemoveRange(fixture.Database.WorkflowNodeExecutions);
        await fixture.Database.SaveChangesAsync();
        fixture.Database.ChangeTracker.Clear();
        var projected = Assert.Single(await fixture.Service.ListNodeExecutionsAsync(
            executionId,
            CancellationToken.None));
        Assert.Equal(WorkflowNodeExecutionStatus.Unknown, projected.Status);

        var resolved = await fixture.Service.ResolveUnknownAsync(
            executionId,
            new WorkflowUnknownResolutionRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = "supervisor-1",
                Reason = "Legacy field evidence confirms success",
                NodeExecutionId = projected.Id,
                Outcome = WorkflowUnknownResolutionOutcome.ConfirmedSucceeded
            },
            CancellationToken.None);

        Assert.Equal(WorkflowRuntimeStatus.Completed, resolved.Run.RuntimeStatus);
        Assert.Equal(
            WorkflowNodeExecutionStatus.Succeeded,
            Assert.Single(await fixture.Service.ListNodeExecutionsAsync(executionId, CancellationToken.None)).Status);
        Assert.Equal(
            WorkflowDeviceOperationStatus.Succeeded,
            Assert.Single(await fixture.Service.ListDeviceOperationsAsync(executionId, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Server_authorization_denies_unconfigured_permissions_without_changing_run_state()
    {
        await using var fixture = await WorkflowRunControlFixture.CreateAsync(
            new WorkflowRunControlOperatorOptions
            {
                Name = "pause-only",
                Permissions = [WorkflowRunControlPermissions.Pause]
            });
        var executionId = await fixture.AdmitAsync(WorkflowTestDefinitions.CreateMoveWorkflow(null, "SAMPLE_01"));

        await Assert.ThrowsAsync<WorkflowRunControlForbiddenException>(() => fixture.Service.CancelRunAsync(
            executionId,
            Request("pause-only", "Attempt without permission"),
            CancellationToken.None));

        Assert.Equal(
            WorkflowRuntimeStatus.Prepared,
            (await fixture.Service.GetExecutionAsync(executionId, CancellationToken.None))!.RuntimeStatus);
        Assert.DoesNotContain(
            fixture.Database.WorkflowAudits,
            item => item.EventType == "WorkflowRunCancelled");
    }

    private static WorkflowRunControlRequest Request(string actor, string reason) => new()
    {
        RequestId = Guid.NewGuid(),
        Actor = actor,
        Reason = reason
    };
}

internal sealed class WorkflowRunControlFixture : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private WorkflowRunControlFixture(
        SqliteConnection connection,
        MesDbContext database,
        WorkflowApplicationService service,
        PhysicalReadinessStateStore readiness)
    {
        _connection = connection;
        Database = database;
        Service = service;
        Readiness = readiness;
    }

    public MesDbContext Database { get; }
    public WorkflowApplicationService Service { get; }

    public static async Task<WorkflowRunControlFixture> CreateAsync(
        params WorkflowRunControlOperatorOptions[] operators)
    {
        if (operators.Length == 0)
        {
            operators =
            [
                new WorkflowRunControlOperatorOptions
                {
                    Name = "operator-1",
                    Permissions =
                    [
                        WorkflowRunControlPermissions.Pause,
                        WorkflowRunControlPermissions.Cancel,
                        WorkflowRunControlPermissions.ResolveUnknown
                    ]
                },
                new WorkflowRunControlOperatorOptions
                {
                    Name = "supervisor-1",
                    Permissions = [WorkflowRunControlPermissions.ResolveUnknown]
                }
            ];
        }

        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var database = new MesDbContext(
            new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options);
        await database.Database.EnsureCreatedAsync();
        var validator = new WorkflowValidator();
        var reader = new MesWorkflowVersionReader(database);
        var readiness = CreateReadiness();
        var profile = ProfileConfiguration.Default with
        {
            Features = ProfileConfiguration.Default.Features with { UseSimulator = false }
        };
        var authorizer = new ConfiguredWorkflowRunControlAuthorizer(Options.Create(
            new WorkflowRunControlAuthorizationOptions { Operators = [.. operators] }));
        var service = new WorkflowApplicationService(
            database,
            reader,
            new WorkflowRuntimeExecutor(
                reader,
                validator,
                admissionPolicies: [new ActiveProfileWorkflowAdmissionPolicy(ProfileConfiguration.Default)]),
            validator,
            timeProvider: null,
            controlAuthorizer: authorizer,
            physicalReadiness: readiness,
            admissionPolicy: new PhysicalExecutionAdmissionPolicy(
                profile,
                readiness,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PhysicalExecutionAdmissionPolicy>.Instance));
        return new WorkflowRunControlFixture(connection, database, service, readiness);
    }

    public async Task<Guid> AdmitAsync(WorkflowDefinition definition, bool physical = false)
    {
        var draft = await Service.CreateDraftAsync(definition, "planner", CancellationToken.None);
        Assert.True((await Service.ValidateVersionAsync(
            draft.WorkflowId,
            draft.Version,
            CancellationToken.None)).IsValid);
        await Service.PublishAsync(draft.WorkflowId, draft.Version, "planner", CancellationToken.None);
        var result = await Service.ExecuteAsync(new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "run-operator",
            PhysicalAuthorization = physical
                ? new WorkflowPhysicalRunAuthorization
                {
                    AgvId = "AGV-01",
                    OperatorName = "run-operator",
                    SafetyObserverName = "observer",
                    PermitPrefix = "control-test",
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
                }
                : null
        }, CancellationToken.None);
        Assert.True(result.IsAccepted, $"{result.RejectionCode}: {result.RejectionReason}");
        return result.ExecutionId;
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        await _connection.DisposeAsync();
    }

    public void DisablePhysicalSupervisor()
    {
        var now = DateTimeOffset.UtcNow;
        Readiness.Configure(false, [new PhysicalDeviceDescriptor("AGV-01", "agv", true)], now);
    }

    private PhysicalReadinessStateStore Readiness { get; }

    private static PhysicalReadinessStateStore CreateReadiness()
    {
        var readiness = new PhysicalReadinessStateStore(instanceId: "workflow-control-test");
        var now = DateTimeOffset.UtcNow;
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", "agv", true);
        readiness.Configure(true, [descriptor], now);
        readiness.Apply(descriptor, new PhysicalDeviceReadinessObservation
        {
            DeviceId = "AGV-01", DeviceFamily = "agv", ProbeSucceeded = true, Online = true,
            CurrentStationId = "SAMPLE_01", ControlOwner = "adapter", MapName = "test-map",
            MapVersion = "1", MapMd5 = "test-md5", VehicleModel = "test", IsFullPreflight = true,
            FullPreflightPassed = true, ObservedAtUtc = now, FullPreflightObservedAtUtc = now
        }, TimeSpan.Zero, true, now);
        return readiness;
    }
}
