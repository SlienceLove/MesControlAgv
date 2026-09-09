using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MesControlAgv.Mes.Tests;

public sealed class WorkflowFieldNavigationWorkerTests
{
    [Fact]
    public async Task Authorized_acceptance_advances_the_same_run_from_move_to_aubo_completion()
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
        var definition = CreateMoveThenAuboWorkflow();
        var draft = await workflows.CreateDraftAsync(definition, "test", CancellationToken.None);
        var validation = await workflows.ValidateVersionAsync(
            draft.WorkflowId,
            draft.Version,
            CancellationToken.None);
        Assert.True(validation.IsValid, string.Join("; ", validation.Issues.Select(issue => issue.Message)));
        await workflows.PublishAsync(draft.WorkflowId, draft.Version, "test", CancellationToken.None);
        var execution = await workflows.ExecuteAsync(new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "test",
            PhysicalAuthorization = new WorkflowPhysicalRunAuthorization
            {
                AgvId = "AGV-01",
                OperatorName = "test",
                SafetyObserverName = "observer",
                PermitPrefix = "worker-test",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
                DeviceEpochs = new Dictionary<string, long> { ["AGV-01"] = 1 },
                ReadinessSupervisorInstanceId = "test"
            }
        }, CancellationToken.None);
        Assert.True(execution.IsAccepted);

        var move = Assert.Single(await workflows.ListFieldNavigationDispatchableNodesAsync(
            CancellationToken.None));
        var adapter = new FieldAcceptanceAdapter();
        var repository = new FieldNavigationAcceptanceRepository(database);
        var acceptanceService = new FieldNavigationAcceptanceService(
            repository,
            adapter,
            profile,
            new PathPlanner(AgvMap.FromProfile(profile.Map)),
            workflows: workflows);
        var created = await acceptanceService.CreateAsync(
            new CreateFieldNavigationAcceptanceRequest("AGV-01", "LM1", "LM4", "workflow move")
            {
                WorkflowRunId = execution.ExecutionId,
                WorkflowNodeExecutionId = move.NodeExecution.Id
            },
            CancellationToken.None);
        Assert.True(created.IsWorkflowLinked);
        await acceptanceService.AuthorizeAsync(
            created.Id,
            new AuthorizeFieldNavigationAcceptanceRequest(
                "operator",
                "observer",
                "permit-workflow-1",
                DateTimeOffset.UtcNow.AddHours(1)),
            CancellationToken.None);
        var bypass = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            acceptanceService.DispatchAsync(created.Id, CancellationToken.None));
        Assert.Contains("workflow worker", bypass.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, adapter.DispatchCalls);

        var disabledDispatcher = new WorkflowFieldNavigationDispatcher(
            workflows,
            acceptanceService,
            repository,
            profile,
            new WorkflowFieldNavigationWorkerOptions { Enabled = false });
        await disabledDispatcher.ProcessAsync(CancellationToken.None);
        Assert.Equal(0, adapter.DispatchCalls);
        Assert.Equal(
            WorkflowNodeExecutionStatus.Ready,
            Assert.Single(await workflows.ListFieldNavigationDispatchableNodesAsync(CancellationToken.None))
                .NodeExecution.Status);

        var dispatcher = new WorkflowFieldNavigationDispatcher(
            workflows,
            acceptanceService,
            repository,
            profile,
            new WorkflowFieldNavigationWorkerOptions { Enabled = true },
            physicalReadiness: new TestPhysicalReadinessState(
                enabled: true,
                supervisorInstanceId: "test",
                deviceEpoch: 1,
                reason: "test readiness failure"));
        await dispatcher.ProcessAsync(CancellationToken.None);

        var movingAcceptance = await repository.GetAsync(created.Id, CancellationToken.None);
        Assert.NotNull(movingAcceptance!.WorkflowDeviceOperationId);
        Assert.Equal(FieldNavigationAcceptanceStatuses.Moving, movingAcceptance.Status);
        Assert.Equal(WorkflowRuntimeStatus.Running,
            (await workflows.GetExecutionAsync(execution.ExecutionId, CancellationToken.None))!.RuntimeStatus);

        movingAcceptance.Status = FieldNavigationAcceptanceStatuses.Arrived;
        movingAcceptance.DeviceTaskId = "field-device-task-1";
        await repository.SaveWithAuditAsync(
            movingAcceptance,
            "AdapterStateReconciled",
            new { state = "arrived" },
            CancellationToken.None);
        await dispatcher.ProcessAsync(CancellationToken.None);

        var afterMove = await workflows.GetExecutionAsync(execution.ExecutionId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Prepared, afterMove!.RuntimeStatus);
        Assert.Equal(WorkflowNodeType.RobotProgram, afterMove.PendingStepRequest!.NodeType);
        var completedMove = (await workflows.ListNodeExecutionsAsync(
            execution.ExecutionId,
            CancellationToken.None)).Single(item => item.Id == move.NodeExecution.Id);
        Assert.Equal(WorkflowNodeExecutionStatus.Succeeded, completedMove.Status);
        Assert.Equal(created.Id.ToString(), completedMove.Outputs["acceptanceId"]);

        var arm = new CompletingArmGateway();
        var aubo = new WorkflowAuboProgramDispatcher(
            workflows,
            arm,
            profile with { Features = profile.Features with { UseSimulator = true } },
            new WorkflowAuboProgramWorkerOptions
            {
                Enabled = true,
                PollIntervalMs = 10,
                CompletionTimeoutMs = 1000
            });
        await aubo.ProcessAsync(CancellationToken.None);

        var final = await workflows.GetExecutionAsync(execution.ExecutionId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Completed, final!.RuntimeStatus);
        Assert.Equal(1, adapter.DispatchCalls);
        Assert.Equal(["测试1"], arm.RunPrograms);
        Assert.All(
            await workflows.ListDeviceOperationsAsync(execution.ExecutionId, CancellationToken.None),
            operation => Assert.Equal(WorkflowDeviceOperationStatus.Succeeded, operation.Status));
    }

    [Fact]
    public async Task Explicit_batch_authorization_creates_one_unique_permit_for_a_ready_move()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<MesControlAgv.Mes.Data.MesDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new MesControlAgv.Mes.Data.MesDbContext(dbOptions);
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
        var draft = await workflows.CreateDraftAsync(CreateMoveThenAuboWorkflow(), "test", CancellationToken.None);
        Assert.True((await workflows.ValidateVersionAsync(draft.WorkflowId, draft.Version, CancellationToken.None)).IsValid);
        await workflows.PublishAsync(draft.WorkflowId, draft.Version, "test", CancellationToken.None);
        var execution = await workflows.ExecuteAsync(new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "test",
            PhysicalAuthorization = new WorkflowPhysicalRunAuthorization
            {
                AgvId = "AGV-01",
                OperatorName = "test",
                SafetyObserverName = "observer",
                PermitPrefix = "batch-test",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
            }
        }, CancellationToken.None);
        var move = Assert.Single(await workflows.ListFieldNavigationDispatchableNodesAsync(CancellationToken.None));
        var adapter = new FieldAcceptanceAdapter();
        var repository = new FieldNavigationAcceptanceRepository(database);
        var acceptanceService = new FieldNavigationAcceptanceService(
            repository,
            adapter,
            profile,
            new PathPlanner(AgvMap.FromProfile(profile.Map)),
            workflows: workflows);
        var dispatcher = new WorkflowFieldNavigationDispatcher(
            workflows,
            acceptanceService,
            repository,
            profile,
            new WorkflowFieldNavigationWorkerOptions
            {
                Enabled = true,
                AutoAuthorizeFromRunRequest = true
            },
            agv: adapter,
            physicalReadiness: new TestPhysicalReadinessState(
                enabled: true,
                supervisorInstanceId: "test",
                deviceEpoch: 1,
                reason: "test readiness failure"));

        await dispatcher.ProcessAsync(CancellationToken.None);

        var acceptance = Assert.Single(await repository.ListForWorkflowRunAsync(execution.ExecutionId, CancellationToken.None));
        Assert.Equal(FieldNavigationAcceptanceStatuses.Moving, acceptance.Status);
        Assert.StartsWith("batch-test-", acceptance.PermitId, StringComparison.Ordinal);
        Assert.Equal("test", acceptance.OperatorName);
        Assert.Equal("observer", acceptance.SafetyObserverName);
        Assert.Equal(1, adapter.DispatchCalls);
        Assert.Equal(WorkflowNodeExecutionStatus.Running,
            (await workflows.ListNodeExecutionsAsync(execution.ExecutionId, CancellationToken.None)).Single().Status);
    }

    [Fact]
    public async Task Final_arrived_move_completes_run_and_releases_adapter_control_once()
    {
        await using var fixture = await FinalMoveFixture.CreateAsync();

        await fixture.CompleteAsync();

        Assert.Equal(WorkflowRuntimeStatus.Completed,
            (await fixture.Workflows.GetExecutionAsync(fixture.ExecutionId, CancellationToken.None))!.RuntimeStatus);
        Assert.Equal(1, fixture.Adapter.ReleaseControlCalls);
        var safetyAction = Assert.Single(await fixture.Database.PhysicalSafetyActions.AsNoTracking().ToListAsync());
        Assert.Equal(PhysicalSafetyActionTypes.AgvRelease, safetyAction.ActionType);
        Assert.Equal(PhysicalSafetyActionStatuses.Succeeded, safetyAction.Status);
        Assert.Equal(fixture.ExecutionId, safetyAction.WorkflowRunId);

        var (_, details) = await AssertFinalMoveReleaseAuditAsync(
            fixture,
            PhysicalSafetyActionStatuses.Succeeded,
            PhysicalSafetyActionStatuses.Succeeded);
        Assert.Equal(safetyAction.Id.ToString(), details["safetyActionId"]);
        Assert.Equal(PhysicalSafetyActionStatuses.Succeeded, details["safetyActionStatus"]);
        Assert.Null(details["exceptionType"]);
        Assert.Null(details["exceptionMessage"]);
    }

    [Fact]
    public async Task Final_move_release_rejection_is_audited_without_blocking_workflow_completion()
    {
        await using var fixture = await FinalMoveFixture.CreateAsync();
        fixture.Adapter.ReleaseControlResult = false;

        await fixture.CompleteAsync();

        Assert.Equal(WorkflowRuntimeStatus.Completed,
            (await fixture.Workflows.GetExecutionAsync(fixture.ExecutionId, CancellationToken.None))!.RuntimeStatus);
        Assert.Equal(1, fixture.Adapter.ReleaseControlCalls);
        var safetyAction = Assert.Single(await fixture.Database.PhysicalSafetyActions.AsNoTracking().ToListAsync());
        Assert.Equal(PhysicalSafetyActionStatuses.Rejected, safetyAction.Status);
        var (_, details) = await AssertFinalMoveReleaseAuditAsync(
            fixture,
            PhysicalSafetyActionStatuses.Rejected,
            PhysicalSafetyActionStatuses.Rejected);
        Assert.Equal(safetyAction.Id.ToString(), details["safetyActionId"]);
        Assert.Equal(PhysicalSafetyActionStatuses.Rejected, details["safetyActionStatus"]);
        Assert.Null(details["exceptionType"]);
        Assert.Null(details["exceptionMessage"]);
    }

    [Fact]
    public async Task Ambiguous_final_move_release_is_audited_as_unknown()
    {
        await using var fixture = await FinalMoveFixture.CreateAsync();
        fixture.Adapter.ReleaseControlException = new TimeoutException("ambiguous release response");

        await fixture.CompleteAsync();

        Assert.Equal(WorkflowRuntimeStatus.Completed,
            (await fixture.Workflows.GetExecutionAsync(fixture.ExecutionId, CancellationToken.None))!.RuntimeStatus);
        Assert.Equal(1, fixture.Adapter.ReleaseControlCalls);
        var safetyAction = Assert.Single(await fixture.Database.PhysicalSafetyActions.AsNoTracking().ToListAsync());
        Assert.Equal(PhysicalSafetyActionStatuses.Unknown, safetyAction.Status);
        var (_, details) = await AssertFinalMoveReleaseAuditAsync(
            fixture,
            PhysicalSafetyActionStatuses.Unknown,
            PhysicalSafetyActionStatuses.Unknown);
        Assert.Equal(safetyAction.Id.ToString(), details["safetyActionId"]);
        Assert.Equal(PhysicalSafetyActionStatuses.Unknown, details["safetyActionStatus"]);
        Assert.Contains(
            "TimeoutException",
            details["safetyActionResultSummary"] ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Null(details["exceptionType"]);
        Assert.Null(details["exceptionMessage"]);
    }

    [Fact]
    public async Task Throwing_final_move_safety_action_is_audited_and_does_not_crash_worker()
    {
        await using var fixture = await FinalMoveFixture.CreateAsync();
        var disposedDatabase = new MesDbContext(
            new DbContextOptionsBuilder<MesDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options);
        await disposedDatabase.DisposeAsync();
        var throwingSafetyActions = new PhysicalSafetyActionService(
            disposedDatabase,
            fixture.Adapter,
            null,
            fixture.Profile,
            fixture.Readiness);

        await fixture.CompleteAsync(throwingSafetyActions);

        Assert.Equal(WorkflowRuntimeStatus.Completed,
            (await fixture.Workflows.GetExecutionAsync(fixture.ExecutionId, CancellationToken.None))!.RuntimeStatus);
        Assert.Equal(0, fixture.Adapter.ReleaseControlCalls);
        Assert.Empty(await fixture.Database.PhysicalSafetyActions.AsNoTracking().ToListAsync());
        var (_, details) = await AssertFinalMoveReleaseAuditAsync(
            fixture,
            PhysicalSafetyActionStatuses.Unknown,
            "Exception");
        Assert.Null(details["safetyActionId"]);
        Assert.Null(details["safetyActionStatus"]);
        Assert.Equal(typeof(ObjectDisposedException).FullName, details["exceptionType"]);
        Assert.False(string.IsNullOrWhiteSpace(details["exceptionMessage"]));
    }

    [Fact]
    public async Task Final_move_audit_failure_is_detached_and_does_not_retry_release_or_crash_worker()
    {
        await using var fixture = await FinalMoveFixture.CreateAsync();
        await fixture.Database.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER FailFinalMoveReleaseAudit
            BEFORE INSERT ON WorkflowAudits
            WHEN NEW.EventType = 'WorkflowFinalMoveRelease'
            BEGIN
                SELECT RAISE(FAIL, 'simulated final Move release audit failure');
            END;
            """);

        await fixture.CompleteAsync();
        await fixture.CompleteAsync();

        Assert.Equal(WorkflowRuntimeStatus.Completed,
            (await fixture.Workflows.GetExecutionAsync(fixture.ExecutionId, CancellationToken.None))!.RuntimeStatus);
        Assert.Equal(1, fixture.Adapter.ReleaseControlCalls);
        Assert.Single(await fixture.Database.PhysicalSafetyActions.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Database.WorkflowAudits
            .AsNoTracking()
            .Where(audit => audit.EventType == "WorkflowFinalMoveRelease")
            .ToListAsync());
        Assert.DoesNotContain(
            fixture.Database.ChangeTracker.Entries<WorkflowAuditRecord>(),
            entry => entry.Entity.EventType == "WorkflowFinalMoveRelease");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_after_readiness_supervisor_restart_marks_stale_arrival_unknown_without_writes(
        bool missingSupervisor)
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
            new WorkflowRuntimeExecutor(reader, validator),
            validator);
        var draft = await workflows.CreateDraftAsync(CreateSingleMoveWorkflow(), "test", CancellationToken.None);
        Assert.True((await workflows.ValidateVersionAsync(draft.WorkflowId, draft.Version, CancellationToken.None)).IsValid);
        await workflows.PublishAsync(draft.WorkflowId, draft.Version, "test", CancellationToken.None);
        var execution = await workflows.ExecuteAsync(new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "operator",
            PhysicalAuthorization = new WorkflowPhysicalRunAuthorization
            {
                AgvId = "AGV-01",
                OperatorName = "operator",
                SafetyObserverName = "observer",
                PermitPrefix = "restart-recovery",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
                ReadinessSupervisorInstanceId = "supervisor-before-restart",
                DeviceEpochs = new Dictionary<string, long> { ["AGV-01"] = 7 }
            }
        }, CancellationToken.None);
        var move = Assert.Single(await workflows.ListFieldNavigationDispatchableNodesAsync(CancellationToken.None));
        var adapter = new FieldAcceptanceAdapter();
        var repository = new FieldNavigationAcceptanceRepository(database);
        var acceptanceService = new FieldNavigationAcceptanceService(
            repository,
            adapter,
            profile,
            new PathPlanner(AgvMap.FromProfile(profile.Map)),
            workflows: workflows);
        var acceptance = await acceptanceService.CreateAsync(
            new CreateFieldNavigationAcceptanceRequest("AGV-01", "LM1", "LM4")
            {
                WorkflowRunId = execution.ExecutionId,
                WorkflowNodeExecutionId = move.NodeExecution.Id
            },
            CancellationToken.None);
        await acceptanceService.AuthorizeAsync(
            acceptance.Id,
            new AuthorizeFieldNavigationAcceptanceRequest(
                "operator", "observer", "permit-restart-recovery", DateTimeOffset.UtcNow.AddHours(1)),
            CancellationToken.None);
        var dispatcher = new WorkflowFieldNavigationDispatcher(
            workflows,
            acceptanceService,
            repository,
            profile,
            new WorkflowFieldNavigationWorkerOptions { Enabled = true },
            agv: adapter,
            physicalReadiness: new TestPhysicalReadinessState(
                enabled: true,
                supervisorInstanceId: "supervisor-before-restart",
                deviceEpoch: 7,
                reason: "test readiness failure"));
        await dispatcher.ProcessAsync(CancellationToken.None);

        var arrived = await repository.GetAsync(acceptance.Id, CancellationToken.None);
        arrived!.Status = FieldNavigationAcceptanceStatuses.Arrived;
        arrived.DeviceTaskId = "field-device-task-1";
        await repository.SaveWithAuditAsync(
            arrived,
            "AdapterStateReconciled",
            new { state = "arrived" },
            CancellationToken.None);
        var releaseCallsBeforeRecovery = adapter.ReleaseControlCalls;
        var dispatchCallsBeforeRecovery = adapter.DispatchCalls;
        IPhysicalReadinessState? restartedReadiness = missingSupervisor
            ? null
            : new TestPhysicalReadinessState(
                enabled: true,
                supervisorInstanceId: "supervisor-after-restart",
                deviceEpoch: 8,
                reason: PhysicalReadinessReasonCodes.SupervisorInstanceMismatch);
        var recovery = new WorkflowFieldNavigationDispatcher(
            workflows,
            acceptanceService,
            repository,
            profile,
            new WorkflowFieldNavigationWorkerOptions { Enabled = true },
            agv: adapter,
            physicalReadiness: restartedReadiness);

        await recovery.ProcessAsync(CancellationToken.None);

        var run = await workflows.GetExecutionAsync(execution.ExecutionId, CancellationToken.None);
        var node = Assert.Single(await workflows.ListNodeExecutionsAsync(execution.ExecutionId, CancellationToken.None));
        Assert.Equal(WorkflowRuntimeStatus.Unknown, run!.RuntimeStatus);
        Assert.Equal(WorkflowNodeExecutionStatus.Unknown, node.Status);
        Assert.Contains("Manual reconciliation required", node.LastError, StringComparison.Ordinal);
        Assert.Equal(releaseCallsBeforeRecovery, adapter.ReleaseControlCalls);
        Assert.Equal(dispatchCallsBeforeRecovery, adapter.DispatchCalls);
        if (!missingSupervisor)
        {
            var readiness = Assert.IsType<TestPhysicalReadinessState>(restartedReadiness);
            Assert.Equal(1, readiness.ValidationCalls);
            Assert.Equal("AGV-01", readiness.LastDeviceId);
            Assert.Equal(7, readiness.LastExpectedEpoch);
            Assert.Equal("supervisor-before-restart", readiness.LastExpectedSupervisorInstanceId);
        }
    }

    [Fact]
    public async Task Cancelled_one_click_move_does_not_release_control()
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
            new WorkflowRuntimeExecutor(reader, validator),
            validator);
        var draft = await workflows.CreateDraftAsync(CreateSingleMoveWorkflow(), "test", CancellationToken.None);
        Assert.True((await workflows.ValidateVersionAsync(draft.WorkflowId, draft.Version, CancellationToken.None)).IsValid);
        await workflows.PublishAsync(draft.WorkflowId, draft.Version, "test", CancellationToken.None);
        var execution = await workflows.ExecuteAsync(new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "operator",
            PhysicalAuthorization = new WorkflowPhysicalRunAuthorization
            {
                AgvId = "AGV-01",
                OperatorName = "operator",
                SafetyObserverName = "observer",
                PermitPrefix = "cancelled-move",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
            }
        }, CancellationToken.None);
        var move = Assert.Single(await workflows.ListFieldNavigationDispatchableNodesAsync(CancellationToken.None));
        var adapter = new FieldAcceptanceAdapter();
        var repository = new FieldNavigationAcceptanceRepository(database);
        var acceptanceService = new FieldNavigationAcceptanceService(
            repository,
            adapter,
            profile,
            new PathPlanner(AgvMap.FromProfile(profile.Map)),
            workflows: workflows);
        var draftAcceptance = await acceptanceService.CreateAsync(
            new CreateFieldNavigationAcceptanceRequest("AGV-01", "LM1", "LM4")
            {
                WorkflowRunId = execution.ExecutionId,
                WorkflowNodeExecutionId = move.NodeExecution.Id
            },
            CancellationToken.None);
        await acceptanceService.AuthorizeAsync(
            draftAcceptance.Id,
            new AuthorizeFieldNavigationAcceptanceRequest(
                "operator", "observer", "permit-cancelled-move", DateTimeOffset.UtcNow.AddHours(1)),
            CancellationToken.None);
        var dispatcher = new WorkflowFieldNavigationDispatcher(
            workflows,
            acceptanceService,
            repository,
            profile,
            new WorkflowFieldNavigationWorkerOptions { Enabled = true },
            agv: adapter,
            physicalReadiness: new TestPhysicalReadinessState(
                enabled: true,
                supervisorInstanceId: "test",
                deviceEpoch: 1,
                reason: "test readiness failure"));

        await dispatcher.ProcessAsync(CancellationToken.None);
        var acceptance = await repository.GetAsync(draftAcceptance.Id, CancellationToken.None);
        Assert.Equal(FieldNavigationAcceptanceStatuses.Moving, acceptance!.Status);
        acceptance.Status = FieldNavigationAcceptanceStatuses.Cancelled;
        await repository.SaveWithAuditAsync(
            acceptance,
            "AdapterStateReconciled",
            new { state = "cancelled" },
            CancellationToken.None);

        await dispatcher.ProcessAsync(CancellationToken.None);
        await dispatcher.ProcessAsync(CancellationToken.None);

        Assert.Equal(WorkflowRuntimeStatus.Cancelled,
            (await workflows.GetExecutionAsync(execution.ExecutionId, CancellationToken.None))!.RuntimeStatus);
        Assert.Equal(WorkflowNodeExecutionStatus.Cancelled,
            Assert.Single(await workflows.ListNodeExecutionsAsync(execution.ExecutionId, CancellationToken.None)).Status);
        Assert.Equal(0, adapter.ReleaseControlCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Physical_move_stays_ready_without_an_enabled_supervisor(bool useDisabledSupervisor)
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
            new WorkflowRuntimeExecutor(reader, validator),
            validator);
        var draft = await workflows.CreateDraftAsync(CreateSingleMoveWorkflow(), "test", CancellationToken.None);
        Assert.True((await workflows.ValidateVersionAsync(
            draft.WorkflowId,
            draft.Version,
            CancellationToken.None)).IsValid);
        await workflows.PublishAsync(draft.WorkflowId, draft.Version, "test", CancellationToken.None);
        var execution = await workflows.ExecuteAsync(new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "operator",
            PhysicalAuthorization = new WorkflowPhysicalRunAuthorization
            {
                AgvId = "AGV-01",
                OperatorName = "operator",
                SafetyObserverName = "observer",
                PermitPrefix = "missing-supervisor",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
                DeviceEpochs = new Dictionary<string, long> { ["AGV-01"] = 1 },
                ReadinessSupervisorInstanceId = "test"
            }
        }, CancellationToken.None);
        Assert.True(execution.IsAccepted);

        var adapter = new FieldAcceptanceAdapter();
        var repository = new FieldNavigationAcceptanceRepository(database);
        var readiness = useDisabledSupervisor
            ? new TestPhysicalReadinessState(
                enabled: false,
                supervisorInstanceId: "test",
                deviceEpoch: 1,
                reason: PhysicalReadinessReasonCodes.SupervisorDisabled)
            : null;
        var dispatcher = new WorkflowFieldNavigationDispatcher(
            workflows,
            new FieldNavigationAcceptanceService(
                repository,
                adapter,
                profile,
                new PathPlanner(AgvMap.FromProfile(profile.Map)),
                workflows: workflows),
            repository,
            profile,
            new WorkflowFieldNavigationWorkerOptions
            {
                Enabled = true,
                AutoAuthorizeFromRunRequest = true
            },
            agv: adapter,
            physicalReadiness: readiness);

        await dispatcher.ProcessAsync(CancellationToken.None);

        var run = await workflows.GetExecutionAsync(execution.ExecutionId, CancellationToken.None);
        var node = Assert.Single(await workflows.ListNodeExecutionsAsync(execution.ExecutionId, CancellationToken.None));
        Assert.Equal(WorkflowRuntimeStatus.Prepared, run!.RuntimeStatus);
        Assert.Equal(WorkflowNodeExecutionStatus.Ready, node.Status);
        Assert.Empty(await repository.ListForWorkflowRunAsync(execution.ExecutionId, CancellationToken.None));
        Assert.Equal(0, adapter.DispatchCalls);
        Assert.Equal(0, adapter.ReleaseControlCalls);
    }

    [Fact]
    public async Task Acceptance_link_rejects_a_node_from_another_run()
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
            new WorkflowRuntimeExecutor(reader, validator),
            validator);
        var repository = new FieldNavigationAcceptanceRepository(database);
        var service = new FieldNavigationAcceptanceService(
            repository,
            new FieldAcceptanceAdapter(),
            profile,
            new PathPlanner(AgvMap.FromProfile(profile.Map)),
            workflows: workflows);

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CreateAsync(
            new CreateFieldNavigationAcceptanceRequest("AGV-01", "LM1", "LM4")
            {
                WorkflowRunId = Guid.NewGuid(),
                WorkflowNodeExecutionId = Guid.NewGuid()
            },
            CancellationToken.None));

        Assert.Contains("Workflow run", exception.Message, StringComparison.Ordinal);
        Assert.Empty(database.FieldNavigationAcceptances);
    }

    private static async Task<(WorkflowAuditRecord Audit, Dictionary<string, string?> Details)>
        AssertFinalMoveReleaseAuditAsync(
            FinalMoveFixture fixture,
            string expectedOutcome,
            string expectedReleaseResult)
    {
        var audit = Assert.Single(await fixture.Database.WorkflowAudits
            .AsNoTracking()
            .Where(item => item.EventType == "WorkflowFinalMoveRelease")
            .ToListAsync());
        Assert.Equal(expectedOutcome, audit.Outcome);
        Assert.Equal(fixture.WorkflowId, audit.WorkflowId);
        Assert.Equal(fixture.Version, audit.Version);
        Assert.Equal(fixture.RequestId, audit.RequestId);
        Assert.Equal(fixture.ExecutionId, audit.ExecutionId);
        Assert.Equal("operator", audit.Actor);

        var details = JsonSerializer.Deserialize<Dictionary<string, string?>>(audit.DetailsJson)
            ?? throw new InvalidOperationException("The final Move release audit details were empty.");
        Assert.Equal(fixture.ExecutionId.ToString(), details["workflowRunId"]);
        Assert.Equal(fixture.NodeExecutionId.ToString(), details["nodeExecutionId"]);
        Assert.Equal(fixture.DeviceOperationId.ToString(), details["deviceOperationId"]);
        Assert.Equal("true", details["releaseAttempted"]);
        Assert.Equal(expectedReleaseResult, details["releaseResult"]);
        Assert.Equal(WorkflowStepCompletionOutcome.Succeeded.ToString(), details["terminalOutcome"]);
        Assert.Equal("workflow_final_move_completed", details["terminalReason"]);
        Assert.Equal("completed its final Move node", details["terminalContext"]);
        return (audit, details);
    }

    private sealed class FinalMoveFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private FinalMoveFixture(
            SqliteConnection connection,
            MesDbContext database,
            ProfileConfiguration profile,
            WorkflowApplicationService workflows,
            FieldNavigationAcceptanceRepository repository,
            FieldNavigationAcceptanceService acceptanceService,
            FieldAcceptanceAdapter adapter,
            TestPhysicalReadinessState readiness,
            Guid requestId,
            Guid executionId,
            Guid workflowId,
            int version,
            Guid nodeExecutionId,
            Guid deviceOperationId)
        {
            _connection = connection;
            Database = database;
            Profile = profile;
            Workflows = workflows;
            Repository = repository;
            AcceptanceService = acceptanceService;
            Adapter = adapter;
            Readiness = readiness;
            RequestId = requestId;
            ExecutionId = executionId;
            WorkflowId = workflowId;
            Version = version;
            NodeExecutionId = nodeExecutionId;
            DeviceOperationId = deviceOperationId;
        }

        public MesDbContext Database { get; }
        public ProfileConfiguration Profile { get; }
        public WorkflowApplicationService Workflows { get; }
        public FieldNavigationAcceptanceRepository Repository { get; }
        public FieldNavigationAcceptanceService AcceptanceService { get; }
        public FieldAcceptanceAdapter Adapter { get; }
        public TestPhysicalReadinessState Readiness { get; }
        public Guid RequestId { get; }
        public Guid ExecutionId { get; }
        public Guid WorkflowId { get; }
        public int Version { get; }
        public Guid NodeExecutionId { get; }
        public Guid DeviceOperationId { get; }

        public static async Task<FinalMoveFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var database = new MesDbContext(
                new DbContextOptionsBuilder<MesDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await database.Database.EnsureCreatedAsync();
            var profile = CreateProfile();
            var validator = new WorkflowValidator(
                BuiltInWorkflowCatalog.Create(),
                WorkflowPublicationContext.FromProfile(profile));
            var reader = new MesWorkflowVersionReader(database);
            var workflows = new WorkflowApplicationService(
                database,
                reader,
                new WorkflowRuntimeExecutor(reader, validator),
                validator);
            var draft = await workflows.CreateDraftAsync(
                CreateSingleMoveWorkflow(),
                "test",
                CancellationToken.None);
            Assert.True((await workflows.ValidateVersionAsync(
                draft.WorkflowId,
                draft.Version,
                CancellationToken.None)).IsValid);
            await workflows.PublishAsync(
                draft.WorkflowId,
                draft.Version,
                "test",
                CancellationToken.None);
            var requestId = Guid.NewGuid();
            var execution = await workflows.ExecuteAsync(new WorkflowExecutionRequest
            {
                WorkflowId = draft.WorkflowId,
                Version = draft.Version,
                RequestId = requestId,
                RequestedBy = "operator",
                PhysicalAuthorization = new WorkflowPhysicalRunAuthorization
                {
                    AgvId = "AGV-01",
                    OperatorName = "operator",
                    SafetyObserverName = "observer",
                    PermitPrefix = "final-move",
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
                    DeviceEpochs = new Dictionary<string, long> { ["AGV-01"] = 1 },
                    ReadinessSupervisorInstanceId = "test"
                }
            }, CancellationToken.None);
            Assert.True(execution.IsAccepted);

            var move = Assert.Single(await workflows.ListFieldNavigationDispatchableNodesAsync(
                CancellationToken.None));
            var adapter = new FieldAcceptanceAdapter();
            var repository = new FieldNavigationAcceptanceRepository(database);
            var acceptanceService = new FieldNavigationAcceptanceService(
                repository,
                adapter,
                profile,
                new PathPlanner(AgvMap.FromProfile(profile.Map)),
                workflows: workflows);
            var draftAcceptance = await acceptanceService.CreateAsync(
                new CreateFieldNavigationAcceptanceRequest("AGV-01", "LM1", "LM4")
                {
                    WorkflowRunId = execution.ExecutionId,
                    WorkflowNodeExecutionId = move.NodeExecution.Id
                },
                CancellationToken.None);
            await acceptanceService.AuthorizeAsync(
                draftAcceptance.Id,
                new AuthorizeFieldNavigationAcceptanceRequest(
                    "operator", "observer", "permit-final-move", DateTimeOffset.UtcNow.AddHours(1))
                {
                    DeviceEpoch = 1,
                    ReadinessSupervisorInstanceId = "test"
                },
                CancellationToken.None);
            var readiness = new TestPhysicalReadinessState(
                enabled: true,
                supervisorInstanceId: "test",
                deviceEpoch: 1,
                reason: "test readiness failure");
            var dispatcher = new WorkflowFieldNavigationDispatcher(
                workflows,
                acceptanceService,
                repository,
                profile,
                new WorkflowFieldNavigationWorkerOptions { Enabled = true },
                agv: adapter,
                physicalReadiness: readiness);

            await dispatcher.ProcessAsync(CancellationToken.None);
            var acceptance = await repository.GetAsync(draftAcceptance.Id, CancellationToken.None)
                ?? throw new InvalidOperationException("The final Move acceptance was not found.");
            Assert.True(acceptance.WorkflowDeviceOperationId.HasValue);
            acceptance.Status = FieldNavigationAcceptanceStatuses.Arrived;
            await repository.SaveWithAuditAsync(
                acceptance,
                "AdapterStateReconciled",
                new { state = "arrived" },
                CancellationToken.None);

            return new FinalMoveFixture(
                connection,
                database,
                profile,
                workflows,
                repository,
                acceptanceService,
                adapter,
                readiness,
                requestId,
                execution.ExecutionId,
                draft.WorkflowId,
                draft.Version,
                move.NodeExecution.Id,
                acceptance.WorkflowDeviceOperationId.Value);
        }

        public Task CompleteAsync(PhysicalSafetyActionService? safetyActions = null) =>
            new WorkflowFieldNavigationDispatcher(
                Workflows,
                AcceptanceService,
                Repository,
                Profile,
                new WorkflowFieldNavigationWorkerOptions { Enabled = true },
                agv: Adapter,
                physicalReadiness: Readiness,
                safetyActions: safetyActions)
            .ProcessAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private static ProfileConfiguration CreateProfile() => new()
    {
        Product = new ProductProfile { ProductId = "MES-AGV", DisplayName = "test", Version = "1.0" },
        Agvs =
        [
            new AgvProfile
            {
                AgvId = "AGV-01",
                Model = "physical-test",
                Driver = "vendor-tcp",
                Endpoint = "tcp://controller.invalid:19206",
                MaxLoadKg = 200,
                MaxSpeedMetersPerSecond = 0.3,
                HomeStationId = "LM1",
                Enabled = true
            }
        ],
        WorkflowDevices =
        [
            new WorkflowDeviceProfile
            {
                DeviceId = "ARM-01",
                DeviceFamily = WorkflowDeviceFamilyIds.RobotArm,
                CapabilityIds = [WorkflowCapabilityIds.RobotExecuteProgram],
                Enabled = true,
                ControlEnabled = true
            }
        ],
        Stations =
        [
            new StationProfile { Code = 1, StationId = "LM1", AgvStationId = "LM1", Name = "LM1", Type = "Charge" },
            new StationProfile { Code = 4, StationId = "LM4", AgvStationId = "LM4", Name = "LM4", Type = "Station" }
        ],
        Map = new MapProfile
        {
            StationIds = ["LM1", "LM4"],
            Edges = [new MapEdgeProfile { From = "LM1", To = "LM4", Cost = 1 }]
        },
        PhysicalAcceptance = new PhysicalAcceptanceProfile
        {
            ExpectedControlOwner = "adapter",
            MapSnapshot = new ControllerMapSnapshot
            {
                MapName = "test-map",
                Version = "1.0",
                Md5 = "0123456789abcdef0123456789abcdef",
                CapturedAtUtc = DateTimeOffset.UtcNow,
                StationIds = ["LM1", "LM4"],
                DirectedEdges = [new DirectedMapEdgeProfile { From = "LM1", To = "LM4" }]
            },
            Safety = new PhysicalAgvSafetyProfile
            {
                MinimumLocalizationConfidence = 0.95,
                MaximumDispatchSpeedMetersPerSecond = 0.3,
                RequireControlOwnership = true,
                RequireNoEmergency = true,
                RequireNoBlocked = true,
                RequireNoFaults = true,
                VehicleOperatingModePolicy = VehicleOperatingModePolicies.NotExposedByApprovedModel
            }
        },
        Features = new FeatureFlags
        {
            UseSimulator = false,
            EnableAutomaticDispatch = false,
            EnableFieldNavigationAcceptance = true
        },
        Timeouts = new TimeoutOptions()
    };

    private static WorkflowDefinition CreateMoveThenAuboWorkflow()
    {
        var start = Node(WorkflowNodeType.Start, WorkflowGraphNodeTypeIds.Start, "开始", 1);
        var move = Node(
            WorkflowNodeType.Move,
            WorkflowGraphNodeTypeIds.Move,
            "到 LM4",
            2,
            new Dictionary<string, string?>
            {
                [WorkflowNodeConfigurationKeys.TargetStation] = "LM4",
                [WorkflowNodeConfigurationKeys.TimeoutSeconds] = "300",
                [WorkflowNodeConfigurationKeys.RetryCount] = "0"
            });
        var robot = Node(
            WorkflowNodeType.RobotProgram,
            WorkflowGraphNodeTypeIds.RobotExecuteProgram,
            "运行 测试1",
            3,
            new Dictionary<string, string?>
            {
                [WorkflowNodeConfigurationKeys.DeviceId] = "ARM-01",
                [WorkflowNodeConfigurationKeys.ProgramName] = "测试1"
            });
        var end = Node(WorkflowNodeType.End, WorkflowGraphNodeTypeIds.End, "结束", 4);
        start = start with { NextNodeIds = [move.Id] };
        move = move with { NextNodeIds = [robot.Id] };
        robot = robot with { NextNodeIds = [end.Id] };
        return new WorkflowDefinition
        {
            Name = "field move then aubo",
            SchemaVersion = WorkflowGraphDocument.CurrentSchemaVersion,
            Nodes = [start, move, robot, end],
            Edges = CreateEdges(start, move, robot, end)
        };
    }

    private static WorkflowDefinition CreateSingleMoveWorkflow()
    {
        var start = Node(WorkflowNodeType.Start, WorkflowGraphNodeTypeIds.Start, "开始", 1);
        var move = Node(
            WorkflowNodeType.Move,
            WorkflowGraphNodeTypeIds.Move,
            "到 LM4",
            2,
            new Dictionary<string, string?>
            {
                [WorkflowNodeConfigurationKeys.TargetStation] = "LM4",
                [WorkflowNodeConfigurationKeys.TimeoutSeconds] = "300",
                [WorkflowNodeConfigurationKeys.RetryCount] = "0"
            });
        var end = Node(WorkflowNodeType.End, WorkflowGraphNodeTypeIds.End, "结束", 3);
        start = start with { NextNodeIds = [move.Id] };
        move = move with { NextNodeIds = [end.Id] };
        return new WorkflowDefinition
        {
            Name = "single field move",
            SchemaVersion = WorkflowGraphDocument.CurrentSchemaVersion,
            Nodes = [start, move, end],
            Edges = CreateEdges(start, move, end)
        };
    }

    private static WorkflowNode Node(
        WorkflowNodeType type,
        string nodeTypeId,
        string name,
        int order,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        configuration ??= new Dictionary<string, string?>();
        configuration.TryGetValue(WorkflowNodeConfigurationKeys.TargetStation, out var targetStation);
        return new WorkflowNode
        {
            Type = type,
            NodeTypeId = nodeTypeId,
            Name = name,
            Order = order,
            TargetStation = targetStation,
            Ports = WorkflowGraphContractAdapter.CreatePorts(type),
            Configuration = configuration
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

    private sealed class FieldAcceptanceAdapter : IAgvGateway, IFieldNavigationAcceptanceGateway, IPhysicalAgvControlGateway
    {
        public int DispatchCalls { get; private set; }
        public int ReleaseControlCalls { get; private set; }
        public bool ReleaseControlResult { get; set; } = true;
        public Exception? ReleaseControlException { get; set; }

        public Task<AgvTaskResponse> DispatchFieldNavigationAcceptanceAsync(
            Guid acceptanceId,
            FieldNavigationDispatchCommand command,
            CancellationToken cancellationToken)
        {
            DispatchCalls++;
            return Task.FromResult(new AgvTaskResponse(
                acceptanceId,
                "field-device-task-1",
                command.TargetStationId,
                "moving",
                null,
                command.AgvId,
                command.PlannedPath));
        }

        public Task<PhysicalAgvPreflightResponse> GetPhysicalPreflightAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PhysicalAgvPreflightResponse(
                new AgvSnapshotResponse(true, "adapter", "LM1", null, "AGV-01"),
                null,
                true,
                []));

        public Task<AgvTaskResponse> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The workflow field worker must use the acceptance dispatch boundary.");
        public Task<AgvTaskResponse?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult<AgvTaskResponse?>(null);
        public Task<AgvTaskResponse?> CancelAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult<AgvTaskResponse?>(null);
        public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgvSnapshotResponse(true, "adapter", "LM1", null, "AGV-01"));
        public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(
            string agvId,
            string command,
            Guid? taskId,
            CancellationToken cancellationToken) => Task.FromResult<AgvTaskResponse?>(null);

        public Task<bool> ReleaseControlAsync(CancellationToken cancellationToken)
        {
            ReleaseControlCalls++;
            if (ReleaseControlException is not null)
                return Task.FromException<bool>(ReleaseControlException);
            return Task.FromResult(ReleaseControlResult);
        }
    }

    private sealed class TestPhysicalReadinessState(
        bool enabled,
        string supervisorInstanceId,
        long deviceEpoch,
        string reason) : IPhysicalReadinessState
    {
        public bool Enabled { get; } = enabled;
        public int ValidationCalls { get; private set; }
        public string? LastDeviceId { get; private set; }
        public long? LastExpectedEpoch { get; private set; }
        public string? LastExpectedSupervisorInstanceId { get; private set; }

        public PhysicalReadinessResponse GetSnapshot() => new()
        {
            Enabled = this.Enabled,
            SupervisorInstanceId = supervisorInstanceId,
            Devices =
            [
                new PhysicalDeviceReadinessSnapshot
                {
                    DeviceId = "AGV-01",
                    DeviceEpoch = deviceEpoch,
                    State = PhysicalDeviceReadinessState.Ready,
                    RequiresReauthorization = false
                }
            ]
        };

        public bool TryGetDevice(string deviceId, out PhysicalDeviceReadinessSnapshot snapshot)
        {
            snapshot = GetSnapshot().Devices.Single();
            return string.Equals(deviceId, snapshot.DeviceId, StringComparison.Ordinal);
        }

        public bool IsCurrentAndReady(string deviceId, long? expectedEpoch, out string? validationReason) =>
            IsCurrentAndReady(deviceId, expectedEpoch, null, out validationReason);

        public bool IsCurrentAndReady(
            string deviceId,
            long? expectedEpoch,
            string? expectedSupervisorInstanceId,
            out string? validationReason)
        {
            ValidationCalls++;
            LastDeviceId = deviceId;
            LastExpectedEpoch = expectedEpoch;
            LastExpectedSupervisorInstanceId = expectedSupervisorInstanceId;

            var current = GetSnapshot();
            var currentDevice = current.Devices.SingleOrDefault(
                item => string.Equals(item.DeviceId, deviceId, StringComparison.Ordinal));
            var isCurrent = currentDevice is not null &&
                (!expectedEpoch.HasValue || expectedEpoch == currentDevice.DeviceEpoch) &&
                (string.IsNullOrWhiteSpace(expectedSupervisorInstanceId) ||
                 string.Equals(
                     expectedSupervisorInstanceId,
                     current.SupervisorInstanceId,
                     StringComparison.Ordinal));
            validationReason = isCurrent ? null : reason;
            return isCurrent;
        }

        public bool AcknowledgeAuthorization(string deviceId, long expectedEpoch) => false;
    }

    private sealed class CompletingArmGateway : IAuboArmGateway
    {
        private string? _loaded;
        public List<string> RunPrograms { get; } = [];

        public Task<AuboArmProgramStatusResponse> GetProgramAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new AuboArmProgramStatusResponse(
                deviceId,
                true,
                _loaded,
                AuboArmRuntimeState.Stopped,
                "Stopped",
                DateTimeOffset.UtcNow)
            {
                RobotMode = AuboArmMode.Running,
                SafetyMode = AuboArmSafetyMode.Normal,
                OperationalMode = AuboArmOperationalMode.Automatic,
                ControlEnabled = true
            });
        public Task<AuboArmProgramOperationResponse> LoadProgramAsync(
            string deviceId, string programName, string operatorName, Guid operationId, CancellationToken cancellationToken)
        {
            _loaded = programName;
            return Task.FromResult(Operation(operationId, deviceId, programName, "load", operatorName, AuboArmProgramOperationState.Loaded));
        }
        public Task<AuboArmProgramOperationResponse> RunProgramAsync(
            string deviceId, string? programName, string operatorName, Guid operationId, CancellationToken cancellationToken)
        {
            RunPrograms.Add(programName ?? _loaded ?? string.Empty);
            return Task.FromResult(Operation(operationId, deviceId, programName ?? _loaded ?? string.Empty, "run", operatorName, AuboArmProgramOperationState.Running));
        }
        public Task<AuboArmProgramOperationResponse> StopProgramAsync(
            string deviceId, string operatorName, Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(Operation(operationId, deviceId, _loaded ?? string.Empty, "stop", operatorName, AuboArmProgramOperationState.Stopped));
        public Task<AuboArmStatusResponse> GetStatusAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new AuboArmStatusResponse(
                deviceId, "rob1", true, AuboArmMode.Running, 8, AuboArmSafetyMode.Normal, 1,
                AuboArmRuntimeState.Stopped, 6, AuboArmOperationalMode.Automatic, 1, DateTimeOffset.UtcNow));
        public async Task<AuboArmReadinessResponse> GetReadinessAsync(string deviceId, CancellationToken cancellationToken) =>
            new(deviceId, true, [], await GetStatusAsync(deviceId, cancellationToken), _loaded, DateTimeOffset.UtcNow);
        public Task<AuboArmVariableResponse> GetVariableAsync(string deviceId, string key, CancellationToken cancellationToken) =>
            Task.FromResult(new AuboArmVariableResponse(deviceId, key, false, null, null, null, null, null, DateTimeOffset.UtcNow));
        public Task<AuboArmHandshakeSnapshotResponse> GetHandshakeSnapshotAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new AuboArmHandshakeSnapshotResponse(
                deviceId, AuboArmHandshakeState.Idle, null, null, null, null, null, DateTimeOffset.UtcNow));
        public Task<AuboArmHandshakeResultResponse> DispatchAsync(
            string deviceId, Guid operationId, int commandCode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static AuboArmProgramOperationResponse Operation(
            Guid id,
            string device,
            string program,
            string operation,
            string actor,
            AuboArmProgramOperationState state) => new(
                id,
                device,
                program,
                operation,
                actor,
                state,
                AuboArmRuntimeState.Stopped,
                "Stopped",
                program,
                0,
                null,
                true,
                DateTimeOffset.UtcNow);
    }
}
