using MesControlAgv.Application;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Tests;

public sealed class WorkflowRuntimePersistenceTests
{
    [Fact]
    public async Task Sqlite_backed_service_persists_version_lifecycle_runtime_idempotency_and_audit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var setup = new MesDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var workflowId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        WorkflowExecutionResult firstExecution;
        await using (var database = new MesDbContext(options))
        {
            var service = CreateService(database);
            var draft = await service.CreateDraftAsync(CreateValidWorkflow(workflowId), "planner-1", CancellationToken.None);

            Assert.Equal(1, draft.Version);
            Assert.Equal(WorkflowVersionStatus.Draft, draft.Status);
            Assert.Equal(WorkflowPublishStatus.NotPublished, draft.PublishStatus);

            var validation = await service.ValidateVersionAsync(workflowId, draft.Version, CancellationToken.None);
            Assert.True(validation.IsValid);

            var published = await service.PublishAsync(workflowId, draft.Version, "planner-1", CancellationToken.None);
            Assert.Equal(WorkflowVersionStatus.Published, published.Status);
            Assert.Equal(WorkflowPublishStatus.Published, published.PublishStatus);

            var stored = await service.GetVersionAsync(workflowId, draft.Version, CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal(draft.Version, stored!.Definition.PublishedVersion);
            Assert.NotNull(stored.Validation);

            firstExecution = await service.ExecuteAsync(new WorkflowExecutionRequest
            {
                WorkflowId = workflowId,
                Version = draft.Version,
                RequestId = requestId,
                RequestedBy = "operator-1",
                CorrelationId = "workflow-test",
                DryRun = true
            }, CancellationToken.None);

            Assert.True(firstExecution.IsAccepted);
            Assert.Equal(WorkflowNodeType.Move, firstExecution.NextStep!.NodeType);
            Assert.Single(database.WorkflowExecutions);
            Assert.Contains(database.WorkflowAudits, audit => audit.EventType == "WorkflowExecutionAccepted");
        }

        await using (var reloadedDatabase = new MesDbContext(options))
        {
            var reloadedService = CreateService(reloadedDatabase);
            var recovered = await reloadedService.GetExecutionAsync(
                firstExecution.ExecutionId,
                CancellationToken.None);
            var replay = await reloadedService.ExecuteAsync(new WorkflowExecutionRequest
            {
                WorkflowId = workflowId,
                Version = 1,
                RequestId = requestId,
                RequestedBy = "operator-1",
                CorrelationId = "workflow-test",
                DryRun = true
            }, CancellationToken.None);
            var reused = await reloadedService.ExecuteAsync(new WorkflowExecutionRequest
            {
                WorkflowId = workflowId,
                Version = 1,
                RequestId = requestId,
                RequestedBy = "operator-2",
                DryRun = true
            }, CancellationToken.None);

            Assert.NotNull(recovered);
            Assert.Equal(WorkflowRuntimeStatus.DryRunCompleted, recovered!.RuntimeStatus);
            Assert.True(recovered.IsTerminal);
            Assert.Null(recovered.PendingStepRequest);
            Assert.Equal(firstExecution.RequestId, recovered.RequestId);
            Assert.True(replay.IsAccepted);
            Assert.True(replay.IsIdempotentReplay);
            Assert.Equal(firstExecution.ExecutionId, replay.ExecutionId);
            Assert.Equal(WorkflowExecutionRejectionCodes.RequestIdReused, reused.RejectionCode);
            Assert.Single(reloadedDatabase.WorkflowExecutions);
            Assert.Contains(reloadedDatabase.WorkflowAudits, audit => audit.Code == WorkflowExecutionRejectionCodes.RequestIdReused);
        }
    }

    [Fact]
    public async Task Non_dry_run_admission_recovers_a_prepared_step_without_device_side_effects()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseSqlite(connection)
            .Options;
        var workflowId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        Guid executionId;

        await using (var database = new MesDbContext(options))
        {
            await database.Database.EnsureCreatedAsync();
            var service = CreateService(database);
            var draft = await service.CreateDraftAsync(
                CreateValidWorkflow(workflowId),
                "planner-1",
                CancellationToken.None);
            await service.ValidateVersionAsync(workflowId, draft.Version, CancellationToken.None);
            await service.PublishAsync(workflowId, draft.Version, "planner-1", CancellationToken.None);
            var admitted = await service.ExecuteAsync(new WorkflowExecutionRequest
            {
                WorkflowId = workflowId,
                Version = draft.Version,
                RequestId = requestId,
                RequestedBy = "operator-1",
                DryRun = false
            }, CancellationToken.None);
            executionId = admitted.ExecutionId;
        }

        await using (var reloadedDatabase = new MesDbContext(options))
        {
            var service = CreateService(reloadedDatabase);
            var recovered = await service.GetExecutionAsync(executionId, CancellationToken.None);
            var byRequest = await service.GetExecutionByRequestAsync(requestId, CancellationToken.None);

            Assert.NotNull(recovered);
            Assert.Equal(WorkflowRuntimeStatus.Prepared, recovered!.RuntimeStatus);
            Assert.False(recovered.IsTerminal);
            Assert.Equal(WorkflowNodeType.Move, recovered.PendingStepRequest!.NodeType);
            Assert.Equal(recovered.PendingStepRequest.NodeId, recovered.CurrentNodeId);
            Assert.Null(recovered.TransportOperationId);
            Assert.Equal(0, recovered.Attempt);
            var persisted = await reloadedDatabase.WorkflowExecutions.SingleAsync();
            Assert.Equal(WorkflowRuntimeStatus.Prepared.ToString(), persisted.RuntimeStatus);
            Assert.Equal(recovered.CurrentNodeId, persisted.CurrentNodeId);
            Assert.False(string.IsNullOrWhiteSpace(persisted.DefinitionSnapshotJson));
            Assert.False(string.IsNullOrWhiteSpace(persisted.PendingStepJson));
            Assert.NotNull(byRequest);
            Assert.Equal(recovered.ExecutionId, byRequest!.ExecutionId);
            Assert.Equal(recovered.RequestId, byRequest.RequestId);
            Assert.Equal(recovered.RuntimeStatus, byRequest.RuntimeStatus);
            Assert.Equal(recovered.PendingStepRequest.NodeId, byRequest.PendingStepRequest!.NodeId);
            Assert.Single(reloadedDatabase.WorkflowExecutions);
        }
    }

    [Fact]
    public async Task Durable_runtime_fields_override_admission_state_after_a_restart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseSqlite(connection)
            .Options;
        var workflowId = Guid.NewGuid();
        Guid executionId;
        Guid operationId;

        await using (var database = new MesDbContext(options))
        {
            await database.Database.EnsureCreatedAsync();
            var service = CreateService(database);
            var draft = await service.CreateDraftAsync(
                CreateValidWorkflow(workflowId),
                "planner-1",
                CancellationToken.None);
            await service.ValidateVersionAsync(workflowId, draft.Version, CancellationToken.None);
            await service.PublishAsync(workflowId, draft.Version, "planner-1", CancellationToken.None);
            var admitted = await service.ExecuteAsync(new WorkflowExecutionRequest
            {
                WorkflowId = workflowId,
                Version = draft.Version,
                RequestId = Guid.NewGuid(),
                RequestedBy = "operator-1"
            }, CancellationToken.None);

            executionId = admitted.ExecutionId;
            operationId = Guid.NewGuid();
            var persisted = await database.WorkflowExecutions.SingleAsync();
            persisted.RuntimeStatus = WorkflowRuntimeStatus.Paused.ToString();
            persisted.TransportOperationId = operationId;
            persisted.Attempt = 2;
            persisted.LastError = "adapter_ack_pending";
            persisted.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
            await database.SaveChangesAsync();
        }

        await using (var reloadedDatabase = new MesDbContext(options))
        {
            var service = CreateService(reloadedDatabase);
            var recovered = await service.GetExecutionAsync(executionId, CancellationToken.None);

            Assert.NotNull(recovered);
            Assert.Equal(WorkflowRuntimeStatus.Paused, recovered!.RuntimeStatus);
            Assert.False(recovered.IsTerminal);
            Assert.NotNull(recovered.PendingStepRequest);
            Assert.Equal(operationId, recovered.TransportOperationId);
            Assert.Equal(2, recovered.Attempt);
            Assert.Equal("adapter_ack_pending", recovered.LastError);
        }
    }

    [Fact]
    public async Task Claimed_steps_keep_a_stable_operation_id_and_advance_only_after_success_evidence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseSqlite(connection)
            .Options;
        var workflowId = Guid.NewGuid();
        Guid executionId;

        await using (var database = new MesDbContext(options))
        {
            await database.Database.EnsureCreatedAsync();
            var service = CreateService(database);
            var draft = await service.CreateDraftAsync(CreateValidWorkflow(workflowId), "planner-1", CancellationToken.None);
            await service.ValidateVersionAsync(workflowId, draft.Version, CancellationToken.None);
            await service.PublishAsync(workflowId, draft.Version, "planner-1", CancellationToken.None);
            var admitted = await service.ExecuteAsync(new WorkflowExecutionRequest
            {
                WorkflowId = workflowId,
                Version = draft.Version,
                RequestId = Guid.NewGuid(),
                RequestedBy = "operator-1"
            }, CancellationToken.None);

            executionId = admitted.ExecutionId;
            var claimed = await service.ClaimNextStepAsync(executionId, CancellationToken.None);
            var idempotentClaim = await service.ClaimNextStepAsync(executionId, CancellationToken.None);

            Assert.Equal(WorkflowRuntimeStatus.Running, claimed.RuntimeStatus);
            Assert.Equal(1, claimed.Attempt);
            Assert.NotNull(claimed.TransportOperationId);
            Assert.Equal(claimed.TransportOperationId, idempotentClaim.TransportOperationId);
            Assert.Equal(claimed.Attempt, idempotentClaim.Attempt);
            Assert.Single(await service.ListRecoverableExecutionsAsync(CancellationToken.None));

            var advanced = await service.CompleteClaimedStepAsync(executionId, new WorkflowStepCompletionRequest
            {
                TransportOperationId = claimed.TransportOperationId!.Value,
                Outcome = WorkflowStepCompletionOutcome.Succeeded
            }, CancellationToken.None);

            Assert.Equal(WorkflowRuntimeStatus.Prepared, advanced.RuntimeStatus);
            Assert.Equal(WorkflowNodeType.Wait, advanced.PendingStepRequest!.NodeType);
            Assert.Null(advanced.TransportOperationId);
            Assert.Equal(0, advanced.Attempt);

            var secondClaim = await service.ClaimNextStepAsync(executionId, CancellationToken.None);
            Assert.NotEqual(claimed.TransportOperationId, secondClaim.TransportOperationId);
            var completed = await service.CompleteClaimedStepAsync(executionId, new WorkflowStepCompletionRequest
            {
                TransportOperationId = secondClaim.TransportOperationId!.Value,
                Outcome = WorkflowStepCompletionOutcome.Succeeded
            }, CancellationToken.None);

            Assert.Equal(WorkflowRuntimeStatus.Completed, completed.RuntimeStatus);
            Assert.True(completed.IsTerminal);
            Assert.Null(completed.PendingStepRequest);
            Assert.Empty(await service.ListRecoverableExecutionsAsync(CancellationToken.None));
            Assert.Contains(database.WorkflowAudits, audit => audit.EventType == "WorkflowStepClaimed");
            Assert.Equal(2, database.WorkflowAudits.Count(audit => audit.EventType == "WorkflowStepCompleted"));
        }

        await using (var reloadedDatabase = new MesDbContext(options))
        {
            var recovered = await CreateService(reloadedDatabase).GetExecutionAsync(executionId, CancellationToken.None);
            Assert.Equal(WorkflowRuntimeStatus.Completed, recovered!.RuntimeStatus);
        }
    }

    [Fact]
    public async Task Publish_requires_persisted_successful_validation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new MesDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var service = CreateService(database);
        var draft = await service.CreateDraftAsync(CreateValidWorkflow(Guid.NewGuid()), "planner-1", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PublishAsync(draft.WorkflowId, draft.Version, "planner-1", CancellationToken.None));

        Assert.Contains("validated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Runtime_persists_profile_mismatch_rejection_for_a_disabled_station()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new MesDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var profile = ProfileConfiguration.Default;
        var disabledProfile = profile with
        {
            Stations = profile.Stations
                .Select(station => station.StationId == "SAMPLE_01" ? station with { Enabled = false } : station)
                .ToArray()
        };
        var service = CreateService(database, disabledProfile);
        var workflowId = Guid.NewGuid();
        var draft = await service.CreateDraftAsync(CreateValidWorkflow(workflowId), "planner-1", CancellationToken.None);
        await service.ValidateVersionAsync(workflowId, draft.Version, CancellationToken.None);
        await service.PublishAsync(workflowId, draft.Version, "planner-1", CancellationToken.None);

        var result = await service.ExecuteAsync(new WorkflowExecutionRequest
        {
            WorkflowId = workflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "operator-1",
            DryRun = true
        }, CancellationToken.None);

        Assert.Equal(WorkflowExecutionRejectionCodes.ProfileMismatch, result.RejectionCode);
        Assert.Null(result.NextStep);
        Assert.Single(database.WorkflowExecutions);
        Assert.Contains(database.WorkflowAudits, audit => audit.Code == WorkflowExecutionRejectionCodes.ProfileMismatch);
    }

    private static WorkflowApplicationService CreateService(
        MesDbContext database,
        ProfileConfiguration? profile = null)
    {
        var validator = new WorkflowValidator();
        var reader = new MesWorkflowVersionReader(database);
        var activeProfile = profile ?? ProfileConfiguration.Default;
        return new WorkflowApplicationService(
            database,
            reader,
            new WorkflowRuntimeExecutor(
                reader,
                validator,
                admissionPolicies: [new ActiveProfileWorkflowAdmissionPolicy(activeProfile)]),
            validator);
    }

    private static WorkflowDefinition CreateValidWorkflow(Guid workflowId)
    {
        var start = Guid.NewGuid();
        var move = Guid.NewGuid();
        var wait = Guid.NewGuid();
        var end = Guid.NewGuid();
        return new WorkflowDefinition
        {
            Id = workflowId,
            Name = "Persisted transport",
            Nodes =
            [
                new WorkflowNode { Id = start, Type = WorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [move] },
                new WorkflowNode { Id = move, Type = WorkflowNodeType.Move, Name = "Move", TargetStation = "SAMPLE_01", Order = 2, NextNodeIds = [wait] },
                new WorkflowNode { Id = wait, Type = WorkflowNodeType.Wait, Name = "Wait", Order = 3, NextNodeIds = [end] },
                new WorkflowNode { Id = end, Type = WorkflowNodeType.End, Name = "End", Order = 4 }
            ]
        };
    }
}
