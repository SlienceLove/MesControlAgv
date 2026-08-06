using MesControlAgv.Application;
using MesControlAgv.Contracts.Workflows;
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

            Assert.True(replay.IsAccepted);
            Assert.True(replay.IsIdempotentReplay);
            Assert.Equal(firstExecution.ExecutionId, replay.ExecutionId);
            Assert.Equal(WorkflowExecutionRejectionCodes.RequestIdReused, reused.RejectionCode);
            Assert.Single(reloadedDatabase.WorkflowExecutions);
            Assert.Contains(reloadedDatabase.WorkflowAudits, audit => audit.Code == WorkflowExecutionRejectionCodes.RequestIdReused);
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

    private static WorkflowApplicationService CreateService(MesDbContext database)
    {
        var validator = new WorkflowValidator();
        var reader = new MesWorkflowVersionReader(database);
        return new WorkflowApplicationService(
            database,
            reader,
            new WorkflowRuntimeExecutor(reader, validator),
            validator);
    }

    private static WorkflowDefinition CreateValidWorkflow(Guid workflowId)
    {
        var start = Guid.NewGuid();
        var move = Guid.NewGuid();
        var end = Guid.NewGuid();
        return new WorkflowDefinition
        {
            Id = workflowId,
            Name = "Persisted transport",
            Nodes =
            [
                new WorkflowNode { Id = start, Type = WorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [move] },
                new WorkflowNode { Id = move, Type = WorkflowNodeType.Move, Name = "Move", TargetStation = "SAMPLE_01", Order = 2, NextNodeIds = [end] },
                new WorkflowNode { Id = end, Type = WorkflowNodeType.End, Name = "End", Order = 3 }
            ]
        };
    }
}
