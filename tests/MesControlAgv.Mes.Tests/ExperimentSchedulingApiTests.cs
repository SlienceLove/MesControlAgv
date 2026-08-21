using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Mes.Tests;

public sealed class ExperimentSchedulingApiTests : IClassFixture<MesWebApplicationFactory>
{
    private readonly MesWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ExperimentSchedulingApiTests(MesWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Read_endpoints_project_pinned_plan_job_reservation_and_active_lease()
    {
        var ids = await SeedSchedulingRecordsAsync();

        var plans = await _client.GetFromJsonAsync<IReadOnlyList<ExperimentPlan>>("/api/experiment-plans");
        var versions = await _client.GetFromJsonAsync<IReadOnlyList<ExperimentPlan>>(
            $"/api/experiment-plans/{ids.PlanId}/versions");
        var plan = await _client.GetFromJsonAsync<ExperimentPlan>(
            $"/api/experiment-plans/{ids.PlanId}/versions/2");
        var jobs = await _client.GetFromJsonAsync<IReadOnlyList<ExperimentJob>>(
            "/api/experiment-jobs?status=Scheduled");
        var job = await _client.GetFromJsonAsync<ExperimentJob>(
            $"/api/experiment-jobs/{ids.JobId}");
        var schedule = await _client.GetFromJsonAsync<ExperimentScheduleSnapshot>(
            $"/api/schedule?from={Uri.EscapeDataString(ids.From.ToString("O"))}&to={Uri.EscapeDataString(ids.To.ToString("O"))}");

        Assert.Contains(plans!, item => item.PlanId == ids.PlanId && item.Version == 2);
        Assert.Equal(2, versions!.Count);
        Assert.Equal(2, plan!.Version);
        Assert.Equal(ids.WorkflowId, plan.WorkflowId);
        Assert.Equal(5, plan.WorkflowVersion);
        Assert.Equal("anion", plan.DefaultParameters["Method"]);
        Assert.Contains(jobs!, item => item.JobId == ids.JobId);
        Assert.Equal((ids.PlanId, 2), (job!.PlanId, job.PlanVersion));
        Assert.Equal((ids.WorkflowId, 5), (job.WorkflowId, job.WorkflowVersion));

        var entry = Assert.Single(schedule!.Entries, item => item.ScheduleEntryId == ids.ScheduleEntryId);
        Assert.Equal(80, entry.Priority);
        Assert.Empty(entry.BlockingReasons);
        var reservation = Assert.Single(entry.Reservations);
        Assert.Equal(ResourceReservationStatus.Planned, reservation.Status);
        Assert.Equal("AGV-01", reservation.Resource.ResourceId);
        var lease = Assert.Single(schedule.ActiveLeases, item => item.LeaseId == ids.LeaseId);
        Assert.Equal(ids.WorkflowRunId, lease.WorkflowRunId);
        Assert.Equal(ResourceLeaseStatus.Active, lease.Status);
    }

    [Fact]
    public async Task Read_endpoints_return_not_found_and_reject_an_inverted_schedule_range()
    {
        var missingPlan = await _client.GetAsync(
            $"/api/experiment-plans/{Guid.NewGuid()}/versions/1");
        var missingJob = await _client.GetAsync($"/api/experiment-jobs/{Guid.NewGuid()}");
        var invalidRange = await _client.GetAsync(
            "/api/schedule?from=2026-08-22T12:00:00Z&to=2026-08-22T11:00:00Z");

        Assert.Equal(HttpStatusCode.NotFound, missingPlan.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingJob.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidRange.StatusCode);
    }

    private async Task<SeedIds> SeedSchedulingRecordsAsync()
    {
        var planId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var scheduleEntryId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var workflowRunId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var plannedStart = now.AddHours(1);
        var plannedEnd = now.AddHours(2);
        var resourceKey = ExperimentResourceKeys.Create(ExperimentResourceTypeIds.Agv, "AGV-01");

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        database.ExperimentPlans.AddRange(
            CreatePlan(planId, workflowId, version: 1, now.AddDays(-1), ExperimentPlanStatus.Archived),
            CreatePlan(planId, workflowId, version: 2, now, ExperimentPlanStatus.Published));
        database.ExperimentJobs.Add(new ExperimentJobRecord
        {
            JobId = jobId,
            PlanId = planId,
            PlanVersion = 2,
            WorkflowId = workflowId,
            WorkflowVersion = 5,
            SampleBatchId = "B-1042",
            ParametersJson = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["sampleCount"] = "12"
            }),
            Status = ExperimentJobStatus.Scheduled.ToString(),
            CreatedBy = "planner-api-test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        database.ScheduleEntries.Add(new ScheduleEntryRecord
        {
            ScheduleEntryId = scheduleEntryId,
            ExperimentJobId = jobId,
            PlannedStartUtc = plannedStart,
            PlannedEndUtc = plannedEnd,
            Priority = 80,
            Status = ScheduleEntryStatus.Scheduled.ToString(),
            RequestedResourcesJson = JsonSerializer.Serialize(new[]
            {
                new ExperimentResourceReference
                {
                    ResourceType = ExperimentResourceTypeIds.Agv,
                    ResourceId = "AGV-01"
                }
            }),
            BlockingReasonsJson = "[]",
            CreatedBy = "planner-api-test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        database.ResourceReservations.Add(new ResourceReservationRecord
        {
            ReservationId = Guid.NewGuid(),
            ScheduleEntryId = scheduleEntryId,
            ResourceType = ExperimentResourceTypeIds.Agv,
            ResourceId = "AGV-01",
            ResourceKey = resourceKey,
            StartsAtUtc = plannedStart,
            EndsAtUtc = plannedEnd,
            Status = ResourceReservationStatus.Planned.ToString(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        database.WorkflowResourceLeases.Add(new WorkflowResourceLeaseRecord
        {
            LeaseId = leaseId,
            ScheduleEntryId = scheduleEntryId,
            WorkflowRunId = workflowRunId,
            ResourceType = ExperimentResourceTypeIds.Agv,
            ResourceId = "AGV-01",
            ResourceKey = resourceKey,
            ActiveResourceKey = resourceKey,
            Status = ResourceLeaseStatus.Active.ToString(),
            AcquiredBy = "runtime-api-test",
            AcquiredAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(5),
            UpdatedAtUtc = now
        });
        await database.SaveChangesAsync();

        return new SeedIds(
            planId,
            workflowId,
            jobId,
            scheduleEntryId,
            leaseId,
            workflowRunId,
            new DateTimeOffset(plannedStart.AddMinutes(-1), TimeSpan.Zero),
            new DateTimeOffset(plannedEnd.AddMinutes(1), TimeSpan.Zero));
    }

    private static ExperimentPlanRecord CreatePlan(
        Guid planId,
        Guid workflowId,
        int version,
        DateTime now,
        ExperimentPlanStatus status) => new()
    {
        PlanId = planId,
        Version = version,
        Name = "Anion batch",
        Description = "Pinned plan for API projection tests.",
        WorkflowId = workflowId,
        WorkflowVersion = version == 1 ? 4 : 5,
        Status = status.ToString(),
        MaterialRequirementsJson = "[]",
        DefaultParametersJson = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["Method"] = "anion"
        }),
        ResourceRequirementsJson = JsonSerializer.Serialize(new[]
        {
            new ExperimentResourceRequirement
            {
                ResourceType = ExperimentResourceTypeIds.Agv,
                ResourceId = "AGV-01"
            }
        }),
        ProfileProductId = "MES-AGV",
        ProfileVersion = "1.0",
        CreatedBy = "planner-api-test",
        CreatedAtUtc = now,
        PublishedBy = status == ExperimentPlanStatus.Published ? "planner-api-test" : null,
        PublishedAtUtc = status == ExperimentPlanStatus.Published ? now : null,
        UpdatedAtUtc = now
    };

    private sealed record SeedIds(
        Guid PlanId,
        Guid WorkflowId,
        Guid JobId,
        Guid ScheduleEntryId,
        Guid LeaseId,
        Guid WorkflowRunId,
        DateTimeOffset From,
        DateTimeOffset To);
}
