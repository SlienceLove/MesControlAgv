using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using MesControlAgv.Domain.Profiles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MesControlAgv.Mes.Tests;

public sealed class ExperimentSchedulingPersistenceTests
{
    [Fact]
    public async Task Active_resource_key_prevents_double_lease_and_allows_released_history()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new MesDbContext(options);
        await database.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        var resourceKey = ExperimentResourceKeys.Create(ExperimentResourceTypeIds.Instrument, "CIC-D160-01");
        database.WorkflowResourceLeases.Add(CreateLease(resourceKey, now));
        var released = CreateLease(resourceKey, now);
        released.ActiveResourceKey = null;
        released.Status = ResourceLeaseStatus.Released.ToString();
        released.ReleasedAtUtc = now.AddMinutes(-1);
        database.WorkflowResourceLeases.Add(released);
        await database.SaveChangesAsync();

        database.WorkflowResourceLeases.Add(CreateLease(
            ExperimentResourceKeys.Create("INSTRUMENT", "cic-d160-01"),
            now.AddSeconds(1)));

        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        Assert.Equal(2, await database.WorkflowResourceLeases.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Schedule_query_projects_actual_device_activity_for_the_linked_job()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new MesDbContext(options);
        await database.Database.EnsureCreatedAsync();

        var start = new DateTime(2026, 8, 24, 8, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(2);
        var jobId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var resource = new ExperimentResourceReference
        {
            ResourceType = ExperimentResourceTypeIds.Agv,
            ResourceId = "AGV-01"
        };
        var steps = new[]
        {
            new ExperimentPlanWorkflowStep
            {
                StepId = Guid.NewGuid(),
                Order = 1,
                WorkflowId = workflowId,
                WorkflowVersion = 1,
                Name = "移动到检测位",
                EstimatedDurationMinutes = 30
            }
        };
        database.ExperimentJobs.Add(new ExperimentJobRecord
        {
            JobId = jobId,
            PlanId = Guid.NewGuid(),
            PlanVersion = 1,
            WorkflowId = workflowId,
            WorkflowVersion = 1,
            WorkflowStepsJson = JsonSerializer.Serialize(steps),
            SampleBatchId = "B-ACTIVITY",
            Status = ExperimentJobStatus.Running.ToString(),
            WorkflowRunId = runId,
            CreatedBy = "test",
            CreatedAtUtc = start,
            UpdatedAtUtc = start
        });
        database.ScheduleEntries.Add(new ScheduleEntryRecord
        {
            ScheduleEntryId = entryId,
            ExperimentJobId = jobId,
            PlannedStartUtc = start,
            PlannedEndUtc = end,
            Priority = 50,
            Status = ScheduleEntryStatus.Admitted.ToString(),
            RequestedResourcesJson = JsonSerializer.Serialize(new[] { resource }),
            BlockingReasonsJson = "[]",
            CreatedBy = "test",
            CreatedAtUtc = start,
            UpdatedAtUtc = start
        });
        database.WorkflowNodeExecutions.Add(new WorkflowNodeExecutionRecord
        {
            Id = nodeId,
            WorkflowRunId = runId,
            WorkflowId = workflowId,
            Version = 1,
            StepRequestId = Guid.NewGuid(),
            NodeId = Guid.NewGuid(),
            NodeTypeId = "agv.move",
            NodeName = "移动到检测位",
            Attempt = 1,
            Status = WorkflowNodeExecutionStatus.Running.ToString(),
            StartedAtUtc = start.AddMinutes(15),
            CreatedAtUtc = start,
            UpdatedAtUtc = start.AddMinutes(15)
        });
        database.WorkflowDeviceOperations.Add(new WorkflowDeviceOperationRecord
        {
            OperationId = operationId,
            WorkflowRunId = runId,
            NodeExecutionId = nodeId,
            RequestId = Guid.NewGuid(),
            Attempt = 1,
            CapabilityId = "agv.navigate",
            DeviceId = "AGV-01",
            IdempotencyKey = operationId.ToString("N"),
            Status = WorkflowDeviceOperationStatus.Running.ToString(),
            RequestedAtUtc = start.AddMinutes(15),
            UpdatedAtUtc = start.AddMinutes(15)
        });
        await database.SaveChangesAsync();

        var service = new ExperimentSchedulingQueryService(
            database,
            TimeProvider.System,
            new ExperimentResourceCatalog(ProfileConfiguration.Default));
        var snapshot = await service.GetScheduleAsync(
            new DateTimeOffset(start),
            new DateTimeOffset(end),
            CancellationToken.None);

        var activity = Assert.Single(snapshot.Activities);
        Assert.Equal(operationId, activity.ActivityId);
        Assert.Equal(entryId, activity.ScheduleEntryId);
        Assert.Equal("AGV-01", activity.Resource.ResourceId);
        Assert.Equal("移动到检测位", activity.ActivityName);
        Assert.Equal("Running", activity.Status);
        Assert.Equal(steps[0].StepId, activity.WorkflowStepId);
        Assert.Equal(new DateTimeOffset(start.AddMinutes(15)), activity.ActualStart);
    }

    private static WorkflowResourceLeaseRecord CreateLease(string resourceKey, DateTime now) => new()
    {
        LeaseId = Guid.NewGuid(),
        WorkflowRunId = Guid.NewGuid(),
        ResourceType = ExperimentResourceTypeIds.Instrument,
        ResourceId = "CIC-D160-01",
        ResourceKey = resourceKey,
        ActiveResourceKey = resourceKey,
        Status = ResourceLeaseStatus.Active.ToString(),
        AcquiredBy = "test-runtime",
        AcquiredAtUtc = now,
        ExpiresAtUtc = now.AddMinutes(5),
        UpdatedAtUtc = now
    };
}
