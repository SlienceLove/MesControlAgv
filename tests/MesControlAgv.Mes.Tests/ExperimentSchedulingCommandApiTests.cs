using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Materials;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Mes.Tests;

public sealed class ExperimentSchedulingCommandApiTests : IClassFixture<MesWebApplicationFactory>
{
    private readonly MesWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ExperimentSchedulingCommandApiTests(MesWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Plan_job_and_manual_schedule_lifecycle_is_audited_without_starting_a_workflow()
    {
        var workflow = await PublishWorkflowAsync();
        string workflowJsonBefore;
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            workflowJsonBefore = (await database.WorkflowVersions.AsNoTracking().SingleAsync(
                item => item.WorkflowId == workflow.WorkflowId && item.Version == workflow.Version)).DefinitionJson;
        }

        var planRequest = CreatePlanRequest(workflow, "Create the anion experiment plan");
        var createPlan = await _client.PostAsJsonAsync("/api/experiment-plans", planRequest);
        Assert.Equal(HttpStatusCode.Created, createPlan.StatusCode);
        var draft = await createPlan.Content.ReadFromJsonAsync<ExperimentPlan>();
        Assert.Equal(ExperimentPlanStatus.Draft, draft!.Status);
        Assert.Equal("MES-AGV", draft.ProfileProductId);

        var validate = await _client.PostAsJsonAsync(
            $"/api/experiment-plans/{draft.PlanId}/versions/{draft.Version}/validate",
            Action("Validate plan completeness"));
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        var validated = await validate.Content.ReadFromJsonAsync<ExperimentPlan>();
        Assert.Equal(ExperimentPlanStatus.Validated, validated!.Status);
        Assert.True(validated.Validation!.IsValid);

        var publish = await _client.PostAsJsonAsync(
            $"/api/experiment-plans/{draft.PlanId}/versions/{draft.Version}/publish",
            Action("Publish approved plan"));
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        var published = await publish.Content.ReadFromJsonAsync<ExperimentPlan>();
        Assert.Equal(ExperimentPlanStatus.Published, published!.Status);

        var overwrite = await _client.PutAsJsonAsync(
            $"/api/experiment-plans/{draft.PlanId}/versions/{draft.Version}/draft",
            CreatePlanRequest(workflow, "Attempt to overwrite published plan") with
            {
                RequestId = Guid.NewGuid()
            });
        Assert.Equal(HttpStatusCode.Conflict, overwrite.StatusCode);

        var nextDraftResponse = await _client.PostAsJsonAsync(
            $"/api/experiment-plans/{draft.PlanId}/versions/{draft.Version}/next-draft",
            Action("Prepare the next revision"));
        Assert.Equal(HttpStatusCode.Created, nextDraftResponse.StatusCode);
        var nextDraft = await nextDraftResponse.Content.ReadFromJsonAsync<ExperimentPlan>();
        Assert.Equal(2, nextDraft!.Version);
        Assert.Equal(ExperimentPlanStatus.Draft, nextDraft.Status);
        Assert.Null(nextDraft.Validation);

        var createJobRequest = new CreateExperimentJobRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "planner-api-test",
            Reason = "Add batch B-1042 to the task pool",
            PlanId = published.PlanId,
            PlanVersion = published.Version,
            SampleBatchId = "B-1042",
            Parameters = new Dictionary<string, string?>
            {
                ["sampleCount"] = "12"
            }
        };
        var createJob = await _client.PostAsJsonAsync("/api/experiment-jobs", createJobRequest);
        Assert.Equal(HttpStatusCode.Created, createJob.StatusCode);
        var job = await createJob.Content.ReadFromJsonAsync<ExperimentJob>();
        Assert.Equal(ExperimentJobStatus.Ready, job!.Status);
        Assert.Equal((published.PlanId, published.Version), (job.PlanId, job.PlanVersion));
        Assert.Equal((workflow.WorkflowId, workflow.Version), (job.WorkflowId, job.WorkflowVersion));
        Assert.Equal("anion", job.Parameters["method"]);
        Assert.Equal("12", job.Parameters["sampleCount"]);

        var replay = await _client.PostAsJsonAsync("/api/experiment-jobs", createJobRequest);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(job.JobId, (await replay.Content.ReadFromJsonAsync<ExperimentJob>())!.JobId);
        var reusedRequest = await _client.PostAsJsonAsync(
            "/api/experiment-jobs",
            createJobRequest with { SampleBatchId = "B-OTHER" });
        Assert.Equal(HttpStatusCode.Conflict, reusedRequest.StatusCode);

        var start = new DateTimeOffset(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);
        var scheduleResponse = await _client.PutAsJsonAsync(
            $"/api/experiment-jobs/{job.JobId}/schedule",
            Schedule(start, start.AddHours(1), 80, "Schedule the approved batch"));
        Assert.Equal(HttpStatusCode.OK, scheduleResponse.StatusCode);
        var schedule = await scheduleResponse.Content.ReadFromJsonAsync<ScheduleEntry>();
        Assert.Equal(ScheduleEntryStatus.Scheduled, schedule!.Status);
        Assert.Empty(schedule.BlockingReasons);
        Assert.Single(schedule.Reservations);
        Assert.Equal("AGV-01", Assert.Single(schedule.RequestedResources).ResourceId);

        var availability = await _client.GetFromJsonAsync<IReadOnlyList<ExperimentResourceAvailability>>(
            $"/api/resources/availability?from={Uri.EscapeDataString(start.ToString("O"))}&to={Uri.EscapeDataString(start.AddHours(1).ToString("O"))}");
        var agvAvailability = Assert.Single(
            availability!,
            item => item.Resource.ResourceType == ExperimentResourceTypeIds.Agv &&
                    item.Resource.ResourceId == "AGV-01");
        Assert.Equal(1, agvAvailability.PlannedReservationCount);
        Assert.Equal(0, agvAvailability.AvailableCapacity);
        Assert.Contains(
            agvAvailability.BlockingReasons,
            reason => reason.Code == ExperimentSchedulingIssueCodes.ResourceCapacityInsufficient);

        var unscheduleResponse = await _client.PostAsJsonAsync(
            $"/api/experiment-jobs/{job.JobId}/unschedule",
            Action("Return the batch to the task pool"));
        var ready = await unscheduleResponse.Content.ReadFromJsonAsync<ExperimentJob>();
        Assert.Equal(ExperimentJobStatus.Ready, ready!.Status);
        var cancelResponse = await _client.PostAsJsonAsync(
            $"/api/experiment-jobs/{job.JobId}/cancel",
            Action("Sample batch was withdrawn"));
        var cancelled = await cancelResponse.Content.ReadFromJsonAsync<ExperimentJob>();
        Assert.Equal(ExperimentJobStatus.Cancelled, cancelled!.Status);

        var audits = await _client.GetFromJsonAsync<IReadOnlyList<ExperimentSchedulingAuditEntry>>(
            $"/api/experiment-scheduling/audits?planId={published.PlanId}&limit=50");
        Assert.Contains(audits!, item => item.EventType == "ExperimentPlanDraftCreated");
        Assert.Contains(audits!, item => item.EventType == "ExperimentPlanValidated");
        Assert.Contains(audits!, item => item.EventType == "ExperimentPlanPublished");
        var scheduleAudit = Assert.Single(audits!, item => item.EventType == "ExperimentJobScheduled");
        Assert.Equal("AGV/AGV-01", scheduleAudit.Details["resourceKeys"]);
        Assert.Contains(audits!, item =>
            item.EventType == "ExperimentJobCancelled" &&
            item.Actor == "planner-api-test" &&
            item.Reason == "Sample batch was withdrawn");
        Assert.Single(audits!, item => item.RequestId == createJobRequest.RequestId);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<MesDbContext>();
        var workflowRecord = await verifyDatabase.WorkflowVersions.AsNoTracking().SingleAsync(
            item => item.WorkflowId == workflow.WorkflowId && item.Version == workflow.Version);
        Assert.Equal(workflowJsonBefore, workflowRecord.DefinitionJson);
        Assert.Equal("Published", workflowRecord.Status);
        Assert.Equal("Published", workflowRecord.PublishStatus);
        Assert.Empty(await verifyDatabase.WorkflowExecutions.AsNoTracking().ToListAsync());
        Assert.Empty(await verifyDatabase.WorkflowNodeExecutions.AsNoTracking().ToListAsync());
        Assert.Empty(await verifyDatabase.WorkflowDeviceOperations.AsNoTracking().ToListAsync());
        Assert.Empty(await verifyDatabase.WorkflowResourceLeases.AsNoTracking().ToListAsync());
        var jobScheduleEntryIds = await verifyDatabase.ScheduleEntries
            .AsNoTracking()
            .Where(entry => entry.ExperimentJobId == job.JobId)
            .Select(entry => entry.ScheduleEntryId)
            .ToArrayAsync();
        Assert.All(
            await verifyDatabase.ResourceReservations
                .AsNoTracking()
                .Where(reservation => jobScheduleEntryIds.Contains(reservation.ScheduleEntryId))
                .ToListAsync(),
            reservation => Assert.Equal(ResourceReservationStatus.Released.ToString(), reservation.Status));
    }

    [Fact]
    public async Task Composed_plan_roundtrips_ordered_workflow_steps_and_blocks_unsafe_single_run_admission()
    {
        var first = await PublishWorkflowAsync();
        var second = await PublishWorkflowAsync();
        var request = CreatePlanRequest(first, "Create a composed workflow plan") with
        {
            Draft = CreatePlanRequest(first, "Create a composed workflow plan").Draft with
            {
                WorkflowSteps =
                [
                    new ExperimentPlanWorkflowStep
                    {
                        StepId = Guid.NewGuid(),
                        Order = 1,
                        WorkflowId = first.WorkflowId,
                        WorkflowVersion = first.Version,
                        Name = "搬运到检测位",
                        EstimatedDurationMinutes = 12
                    },
                    new ExperimentPlanWorkflowStep
                    {
                        StepId = Guid.NewGuid(),
                        Order = 2,
                        WorkflowId = second.WorkflowId,
                        WorkflowVersion = second.Version,
                        Name = "执行检测模板",
                        EstimatedDurationMinutes = 30
                    }
                ]
            }
        };

        var create = await _client.PostAsJsonAsync("/api/experiment-plans", request);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var draft = await create.Content.ReadFromJsonAsync<ExperimentPlan>();
        Assert.Equal(2, draft!.WorkflowSteps.Count);
        Assert.Equal("搬运到检测位", draft.WorkflowSteps[0].Name);

        var validate = await _client.PostAsJsonAsync(
            $"/api/experiment-plans/{draft.PlanId}/versions/{draft.Version}/validate",
            Action("Validate composed workflow plan"));
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        var validated = await validate.Content.ReadFromJsonAsync<ExperimentPlan>();
        Assert.True(validated!.Validation!.IsValid, string.Join("; ", validated.Validation.Issues.Select(issue => issue.Message)));

        var publish = await _client.PostAsJsonAsync(
            $"/api/experiment-plans/{draft.PlanId}/versions/{draft.Version}/publish",
            Action("Publish composed workflow plan"));
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        var published = await publish.Content.ReadFromJsonAsync<ExperimentPlan>();
        Assert.Equal(2, published!.WorkflowSteps.Count);

        var job = await CreateJobAsync(published, "B-COMPOSED");
        Assert.Equal(2, job.WorkflowSteps.Count);
        Assert.Equal("执行检测模板", job.WorkflowSteps[1].Name);

        var scheduled = await _client.PutAsJsonAsync(
            $"/api/experiment-jobs/{job.JobId}/schedule",
            Schedule(
                new DateTimeOffset(2026, 8, 23, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero),
                70,
                "Schedule composed workflow plan"));
        Assert.Equal(HttpStatusCode.OK, scheduled.StatusCode);

        var admission = await _client.PostAsJsonAsync(
            $"/api/experiment-jobs/{job.JobId}/admit",
            new AdmitExperimentJobRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = "planner-api-test",
                Reason = "Verify composite runtime safety gate"
            });
        Assert.Equal(HttpStatusCode.Conflict, admission.StatusCode);
        var rejection = await admission.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>();
        Assert.Equal(ExperimentSchedulingIssueCodes.CompositeWorkflowNotSupported, rejection!.RejectionCode);
        Assert.Null(rejection.WorkflowRunId);
    }

    [Fact]
    public async Task Missing_composed_step_ids_are_deterministic_across_idempotent_replay_and_read()
    {
        var first = await PublishWorkflowAsync();
        var second = await PublishWorkflowAsync();
        var request = CreatePlanRequest(first, "Verify deterministic step identities") with
        {
            Draft = CreatePlanRequest(first, "Verify deterministic step identities").Draft with
            {
                WorkflowSteps =
                [
                    new ExperimentPlanWorkflowStep
                    {
                        Order = 1,
                        WorkflowId = first.WorkflowId,
                        WorkflowVersion = first.Version,
                        Name = "准备样品",
                        EstimatedDurationMinutes = 10
                    },
                    new ExperimentPlanWorkflowStep
                    {
                        Order = 2,
                        WorkflowId = second.WorkflowId,
                        WorkflowVersion = second.Version,
                        Name = "执行检测",
                        EstimatedDurationMinutes = 20
                    }
                ]
            }
        };

        var create = await _client.PostAsJsonAsync("/api/experiment-plans", request);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var firstRead = await create.Content.ReadFromJsonAsync<ExperimentPlan>();
        var ids = firstRead!.WorkflowSteps.Select(step => step.StepId).ToArray();
        Assert.Equal(2, ids.Length);
        Assert.All(ids, id => Assert.NotEqual(Guid.Empty, id));
        Assert.Equal(2, ids.Distinct().Count());

        var replay = await _client.PostAsJsonAsync("/api/experiment-plans", request);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var replayRead = await replay.Content.ReadFromJsonAsync<ExperimentPlan>();
        Assert.Equal(ids, replayRead!.WorkflowSteps.Select(step => step.StepId).ToArray());

        var persisted = await _client.GetFromJsonAsync<ExperimentPlan>(
            $"/api/experiment-plans/{firstRead.PlanId}/versions/{firstRead.Version}");
        Assert.Equal(ids, persisted!.WorkflowSteps.Select(step => step.StepId).ToArray());
    }

    [Fact]
    public async Task Overlapping_resource_is_blocked_and_boundary_reschedule_is_deterministic()
    {
        var workflow = await PublishWorkflowAsync();
        var plan = await CreatePublishedPlanAsync(workflow);
        var firstJob = await CreateJobAsync(plan, "B-FIRST");
        var secondJob = await CreateJobAsync(plan, "B-SECOND");
        var start = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);

        var firstResponse = await _client.PutAsJsonAsync(
            $"/api/experiment-jobs/{firstJob.JobId}/schedule",
            Schedule(start, start.AddHours(1), 50, "Place first batch"));
        var first = await firstResponse.Content.ReadFromJsonAsync<ScheduleEntry>();
        Assert.Equal(ScheduleEntryStatus.Scheduled, first!.Status);

        var blockedResponse = await _client.PutAsJsonAsync(
            $"/api/experiment-jobs/{secondJob.JobId}/schedule",
            Schedule(start.AddMinutes(15), start.AddMinutes(45), 90, "Place urgent overlapping batch"));
        var blocked = await blockedResponse.Content.ReadFromJsonAsync<ScheduleEntry>();
        Assert.Equal(ScheduleEntryStatus.Blocked, blocked!.Status);
        Assert.Empty(blocked.Reservations);
        var conflict = Assert.Single(
            blocked.BlockingReasons,
            reason => reason.Code == ExperimentSchedulingIssueCodes.ResourceReservationConflict);
        Assert.Contains(first.ScheduleEntryId, conflict.ConflictingScheduleEntryIds);

        var scheduleSnapshot = await _client.GetFromJsonAsync<ExperimentScheduleSnapshot>(
            $"/api/schedule?from={Uri.EscapeDataString(start.ToString("O"))}&to={Uri.EscapeDataString(start.AddHours(2).ToString("O"))}");
        Assert.Empty(Assert.Single(
            scheduleSnapshot!.Entries,
            entry => entry.ScheduleEntryId == blocked.ScheduleEntryId).Reservations);

        var boundaryResponse = await _client.PutAsJsonAsync(
            $"/api/experiment-jobs/{secondJob.JobId}/schedule",
            Schedule(start.AddHours(1), start.AddHours(2), 95, "Move urgent batch to the next window"));
        var boundary = await boundaryResponse.Content.ReadFromJsonAsync<ScheduleEntry>();
        Assert.Equal(ScheduleEntryStatus.Scheduled, boundary!.Status);
        Assert.Equal(95, boundary.Priority);
        Assert.Empty(boundary.BlockingReasons);
        Assert.Single(boundary.Reservations);

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Equal(2, await database.ResourceReservations.CountAsync(
            reservation => reservation.Status == ResourceReservationStatus.Planned.ToString()));
        Assert.Equal(ExperimentJobStatus.Scheduled.ToString(),
            (await database.ExperimentJobs.SingleAsync(job => job.JobId == secondJob.JobId)).Status);
        Assert.Empty(await database.WorkflowExecutions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Invalid_plan_and_unknown_resource_return_stable_explanations()
    {
        var invalidRequest = new SaveExperimentPlanDraftRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "planner-api-test",
            Reason = "Capture incomplete plan for validation",
            Draft = new ExperimentPlanDraft()
        };
        var created = await _client.PostAsJsonAsync("/api/experiment-plans", invalidRequest);
        var draft = await created.Content.ReadFromJsonAsync<ExperimentPlan>();
        var validate = await _client.PostAsJsonAsync(
            $"/api/experiment-plans/{draft!.PlanId}/versions/{draft.Version}/validate",
            Action("Run completeness check"));
        var invalid = await validate.Content.ReadFromJsonAsync<ExperimentPlan>();

        Assert.Equal(ExperimentPlanStatus.Draft, invalid!.Status);
        Assert.False(invalid.Validation!.IsValid);
        Assert.Contains(invalid.Validation.Issues,
            issue => issue.Code == ExperimentSchedulingIssueCodes.PlanNameRequired);
        Assert.Contains(invalid.Validation.Issues,
            issue => issue.Code == ExperimentSchedulingIssueCodes.WorkflowReferenceRequired);

        var publish = await _client.PostAsJsonAsync(
            $"/api/experiment-plans/{draft.PlanId}/versions/{draft.Version}/publish",
            Action("Attempt invalid publication"));
        Assert.Equal(HttpStatusCode.Conflict, publish.StatusCode);

        var workflow = await PublishWorkflowAsync();
        var plan = await CreatePublishedPlanAsync(workflow);
        var job = await CreateJobAsync(plan, "B-UNKNOWN");
        var start = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);
        var blockedResponse = await _client.PutAsJsonAsync(
            $"/api/experiment-jobs/{job.JobId}/schedule",
            new ScheduleExperimentJobRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = "planner-api-test",
                Reason = "Demonstrate unavailable resource explanation",
                PlannedStart = start,
                PlannedEnd = start.AddHours(1),
                Priority = 40,
                Resources =
                [
                    new ExperimentResourceReference
                    {
                        ResourceType = ExperimentResourceTypeIds.Agv,
                        ResourceId = "AGV-NOT-CONFIGURED"
                    }
                ]
            });
        var blocked = await blockedResponse.Content.ReadFromJsonAsync<ScheduleEntry>();
        Assert.Equal(ScheduleEntryStatus.Blocked, blocked!.Status);
        Assert.Contains(blocked.BlockingReasons,
            reason => reason.Code == ExperimentSchedulingIssueCodes.ResourceNotConfigured);
        Assert.Contains(blocked.BlockingReasons,
            reason => reason.Code == ExperimentSchedulingIssueCodes.ResourceSelectionMissing);
    }

    [Fact]
    public async Task Concurrent_manual_requests_cannot_both_reserve_the_same_resource_window()
    {
        var workflow = await PublishWorkflowAsync();
        var plan = await CreatePublishedPlanAsync(workflow);
        var firstJob = await CreateJobAsync(plan, "B-CONCURRENT-1");
        var secondJob = await CreateJobAsync(plan, "B-CONCURRENT-2");
        var start = new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

        var requests = new[]
        {
            _client.PutAsJsonAsync(
                $"/api/experiment-jobs/{firstJob.JobId}/schedule",
                Schedule(start, start.AddHours(1), 60, "Concurrent placement one")),
            _client.PutAsJsonAsync(
                $"/api/experiment-jobs/{secondJob.JobId}/schedule",
                Schedule(start, start.AddHours(1), 60, "Concurrent placement two"))
        };
        var responses = await Task.WhenAll(requests);
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var entries = await Task.WhenAll(responses.Select(response =>
            response.Content.ReadFromJsonAsync<ScheduleEntry>()));

        Assert.Single(entries, entry => entry!.Status == ScheduleEntryStatus.Scheduled);
        Assert.Single(entries, entry => entry!.Status == ScheduleEntryStatus.Blocked);
        Assert.Single(entries.Where(entry => entry!.Status == ScheduleEntryStatus.Scheduled)
            .SelectMany(entry => entry!.Reservations));

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Equal(1, await database.ResourceReservations.CountAsync(reservation =>
            reservation.Status == ResourceReservationStatus.Planned.ToString() &&
            reservation.StartsAtUtc == start.UtcDateTime &&
            reservation.EndsAtUtc == start.AddHours(1).UtcDateTime));
    }

    [Fact]
    public async Task Concurrent_identical_job_requests_replay_one_committed_result()
    {
        var workflow = await PublishWorkflowAsync();
        var plan = await CreatePublishedPlanAsync(workflow);
        var request = new CreateExperimentJobRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "planner-api-test",
            Reason = "Create one idempotent batch",
            PlanId = plan.PlanId,
            PlanVersion = plan.Version,
            SampleBatchId = $"B-IDEMPOTENT-{Guid.NewGuid():N}"
        };

        var responses = await Task.WhenAll(
            _client.PostAsJsonAsync("/api/experiment-jobs", request),
            _client.PostAsJsonAsync("/api/experiment-jobs", request));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var jobs = await Task.WhenAll(responses.Select(response =>
            response.Content.ReadFromJsonAsync<ExperimentJob>()));
        Assert.Equal(jobs[0]!.JobId, jobs[1]!.JobId);

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Equal(1, await database.ExperimentJobs.CountAsync(job =>
            job.PlanId == plan.PlanId && job.SampleBatchId == request.SampleBatchId));
        Assert.Equal(1, await database.ExperimentSchedulingAudits.CountAsync(audit =>
            audit.RequestId == request.RequestId));
    }

    [Fact]
    public async Task Capacity_uses_peak_half_open_load_instead_of_total_overlaps()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new MesDbContext(options);
        await database.Database.EnsureCreatedAsync();

        const string stationId = "SAMPLE_01";
        var profile = ProfileConfiguration.Default;
        profile = profile with
        {
            Stations = profile.Stations.Select(station =>
                station.StationId == stationId ? station with { Capacity = 2 } : station).ToArray()
        };
        var catalog = new ExperimentResourceCatalog(profile);
        var planId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
        var now = start.AddDays(-1).UtcDateTime;
        var resource = new ExperimentResourceReference
        {
            ResourceType = ExperimentResourceTypeIds.Station,
            ResourceId = stationId
        };

        database.ExperimentPlans.Add(new ExperimentPlanRecord
        {
            PlanId = planId,
            Version = 1,
            Name = "Capacity-two station plan",
            WorkflowId = Guid.NewGuid(),
            WorkflowVersion = 1,
            Status = ExperimentPlanStatus.Published.ToString(),
            MaterialRequirementsJson = "[]",
            DefaultParametersJson = "{}",
            ResourceRequirementsJson = JsonSerializer.Serialize(new[]
            {
                new ExperimentResourceRequirement
                {
                    ResourceType = ExperimentResourceTypeIds.Station,
                    ResourceId = stationId,
                    Quantity = 1
                }
            }),
            ProfileProductId = profile.Product.ProductId,
            ProfileVersion = profile.Product.Version,
            CreatedBy = "capacity-test",
            CreatedAtUtc = now,
            PublishedBy = "capacity-test",
            PublishedAtUtc = now,
            UpdatedAtUtc = now
        });
        database.ExperimentJobs.Add(new ExperimentJobRecord
        {
            JobId = jobId,
            PlanId = planId,
            PlanVersion = 1,
            WorkflowId = Guid.NewGuid(),
            WorkflowVersion = 1,
            SampleBatchId = "B-CAPACITY-NEW",
            Status = ExperimentJobStatus.Ready.ToString(),
            CreatedBy = "capacity-test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });

        for (var index = 0; index < 2; index++)
        {
            var existingJobId = Guid.NewGuid();
            var entryId = Guid.NewGuid();
            var reservationStart = start.AddHours(index);
            var reservationEnd = reservationStart.AddHours(1);
            database.ExperimentJobs.Add(new ExperimentJobRecord
            {
                JobId = existingJobId,
                PlanId = planId,
                PlanVersion = 1,
                WorkflowId = Guid.NewGuid(),
                WorkflowVersion = 1,
                SampleBatchId = $"B-CAPACITY-{index}",
                Status = ExperimentJobStatus.Scheduled.ToString(),
                CreatedBy = "capacity-test",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            database.ScheduleEntries.Add(new ScheduleEntryRecord
            {
                ScheduleEntryId = entryId,
                ExperimentJobId = existingJobId,
                PlannedStartUtc = reservationStart.UtcDateTime,
                PlannedEndUtc = reservationEnd.UtcDateTime,
                Priority = 50,
                Status = ScheduleEntryStatus.Scheduled.ToString(),
                RequestedResourcesJson = JsonSerializer.Serialize(new[] { resource }),
                BlockingReasonsJson = "[]",
                CreatedBy = "capacity-test",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            database.ResourceReservations.Add(new ResourceReservationRecord
            {
                ReservationId = Guid.NewGuid(),
                ScheduleEntryId = entryId,
                ResourceType = resource.ResourceType,
                ResourceId = resource.ResourceId,
                ResourceKey = ExperimentResourceKeys.Create(resource.ResourceType, resource.ResourceId),
                StartsAtUtc = reservationStart.UtcDateTime,
                EndsAtUtc = reservationEnd.UtcDateTime,
                Status = ResourceReservationStatus.Planned.ToString(),
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }
        await database.SaveChangesAsync();

        var queryService = new ExperimentSchedulingQueryService(
            database,
            TimeProvider.System,
            catalog);
        var availability = await queryService.ListResourceAvailabilityAsync(
            start,
            start.AddHours(2),
            CancellationToken.None);
        var station = Assert.Single(availability, item =>
            item.Resource.ResourceType == ExperimentResourceTypeIds.Station &&
            item.Resource.ResourceId == stationId);
        Assert.Equal(1, station.PlannedReservationCount);
        Assert.Equal(1, station.AvailableCapacity);

        var commandService = new ExperimentSchedulingCommandService(
            database,
            TimeProvider.System,
            profile,
            catalog,
            new ExperimentSchedulingMutationGate(),
            new MaterialManagementService(database, new MaterialOperationCoordinator()));
        var scheduled = await commandService.ScheduleJobAsync(
            jobId,
            new ScheduleExperimentJobRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = "capacity-test",
                Reason = "Use the second station slot across adjacent reservations",
                PlannedStart = start,
                PlannedEnd = start.AddHours(2),
                Priority = 60,
                Resources = [resource]
            },
            CancellationToken.None);

        Assert.Equal(ScheduleEntryStatus.Scheduled, scheduled.Status);
        Assert.Empty(scheduled.BlockingReasons);
        Assert.Single(scheduled.Reservations);
    }

    [Fact]
    public async Task Scheduling_reserves_job_sample_and_unscheduling_releases_it()
    {
        var workflow = await PublishWorkflowAsync();
        var plan = await CreatePublishedPlanAsync(workflow);
        var sampleId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var now = DateTime.UtcNow;
            database.SampleMaterials.Add(new SampleMaterialRecord
            {
                SampleId = sampleId,
                Barcode = "SCHED-SAMPLE-" + Guid.NewGuid().ToString("N"),
                SampleBatchId = "SCHED-BATCH",
                Status = SampleLifecycleStatus.Available.ToString(),
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await database.SaveChangesAsync();
        }

        var jobResponse = await _client.PostAsJsonAsync("/api/experiment-jobs", new CreateExperimentJobRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "sample-schedule-test",
            Reason = "Create sample-backed job",
            PlanId = plan.PlanId,
            PlanVersion = plan.Version,
            SampleBatchId = "SCHED-BATCH",
            SampleId = sampleId.ToString()
        });
        jobResponse.EnsureSuccessStatusCode();
        var job = (await jobResponse.Content.ReadFromJsonAsync<ExperimentJob>())!;

        var start = new DateTimeOffset(2035, 1, 2, 9, 0, 0, TimeSpan.Zero);
        var scheduleResponse = await _client.PutAsJsonAsync(
            $"/api/experiment-jobs/{job.JobId}/schedule",
            Schedule(start, start.AddHours(1), 10, "Reserve the scheduled sample"));
        scheduleResponse.EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var binding = await database.ExperimentJobMaterialBindings.SingleAsync(
                item => item.ExperimentJobId == job.JobId);
            Assert.Equal(sampleId, binding.SampleId);
            Assert.Equal(MaterialBindingStatus.Reserved.ToString(), binding.Status);
            Assert.Equal(SampleLifecycleStatus.Reserved.ToString(),
                await database.SampleMaterials.Where(item => item.SampleId == sampleId)
                    .Select(item => item.Status).SingleAsync());
        }

        var unschedule = await _client.PostAsJsonAsync(
            $"/api/experiment-jobs/{job.JobId}/unschedule",
            Action("Release the scheduled sample"));
        unschedule.EnsureSuccessStatusCode();
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            Assert.Equal(SampleLifecycleStatus.Available.ToString(),
                await database.SampleMaterials.Where(item => item.SampleId == sampleId)
                    .Select(item => item.Status).SingleAsync());
            Assert.Equal(MaterialBindingStatus.Released.ToString(),
                await database.ExperimentJobMaterialBindings
                    .Where(item => item.ExperimentJobId == job.JobId)
                    .Select(item => item.Status).SingleAsync());
        }
    }

    [Fact]
    public async Task Scheduling_rejects_unregistered_job_sample_without_changing_job_state()
    {
        var workflow = await PublishWorkflowAsync();
        var plan = await CreatePublishedPlanAsync(workflow);
        var jobResponse = await _client.PostAsJsonAsync("/api/experiment-jobs", new CreateExperimentJobRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "sample-schedule-test",
            Reason = "Create missing-sample job",
            PlanId = plan.PlanId,
            PlanVersion = plan.Version,
            SampleBatchId = "MISSING-SAMPLE-BATCH",
            SampleId = Guid.NewGuid().ToString()
        });
        jobResponse.EnsureSuccessStatusCode();
        var job = (await jobResponse.Content.ReadFromJsonAsync<ExperimentJob>())!;

        var start = new DateTimeOffset(2036, 1, 2, 9, 0, 0, TimeSpan.Zero);
        var response = await _client.PutAsJsonAsync(
            $"/api/experiment-jobs/{job.JobId}/schedule",
            Schedule(start, start.AddHours(1), 10, "Reject missing sample"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(MaterialIssueCodes.SampleNotFound, error.GetProperty("code").GetString());

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Equal(ExperimentJobStatus.Ready.ToString(),
            await database.ExperimentJobs.Where(item => item.JobId == job.JobId)
                .Select(item => item.Status).SingleAsync());
    }

    [Fact]
    public async Task Scheduling_rolls_back_when_material_inventory_is_insufficient()
    {
        var workflow = await PublishWorkflowAsync();
        var materialCode = ("SCHED-INSUFFICIENT-" + Guid.NewGuid().ToString("N")).ToUpperInvariant();
        var materialId = Guid.NewGuid();
        var lotId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            database.MaterialCatalog.Add(new MaterialCatalogRecord
            {
                MaterialId = materialId,
                MaterialCode = materialCode,
                Name = "Insufficient scheduling material",
                Kind = "Consumable",
                Unit = "EA",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            database.WarehouseLocations.Add(new WarehouseLocationRecord
            {
                LocationId = locationId,
                WarehouseCode = "TEST",
                WarehouseName = "Test warehouse",
                LocationCode = "TEST-01",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            database.MaterialLots.Add(new MaterialLotRecord
            {
                LotId = lotId,
                MaterialId = materialId,
                MaterialCode = materialCode,
                LotCode = "LOT-INSUFFICIENT",
                Unit = "EA",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            database.InventoryBalances.Add(new InventoryBalanceRecord
            {
                BalanceId = Guid.NewGuid(),
                LotId = lotId,
                LocationId = locationId,
                OnHand = 0,
                Reserved = 0,
                UpdatedAtUtc = now
            });
            await database.SaveChangesAsync();
        }

        var planRequest = CreatePlanRequest(
            workflow,
            "Create insufficient material plan",
            [new ExperimentMaterialRequirement
            {
                MaterialId = materialCode,
                Name = "Insufficient scheduling material",
                Quantity = 1,
                Unit = "EA"
            }]);
        var createPlan = await _client.PostAsJsonAsync("/api/experiment-plans", planRequest);
        createPlan.EnsureSuccessStatusCode();
        var draft = (await createPlan.Content.ReadFromJsonAsync<ExperimentPlan>())!;
        (await _client.PostAsJsonAsync(
            $"/api/experiment-plans/{draft.PlanId}/versions/{draft.Version}/validate",
            Action("Validate insufficient material plan"))).EnsureSuccessStatusCode();
        var publish = await _client.PostAsJsonAsync(
            $"/api/experiment-plans/{draft.PlanId}/versions/{draft.Version}/publish",
            Action("Publish insufficient material plan"));
        publish.EnsureSuccessStatusCode();
        var plan = (await publish.Content.ReadFromJsonAsync<ExperimentPlan>())!;
        var job = await CreateJobAsync(plan, "INSUFFICIENT-BATCH-" + Guid.NewGuid().ToString("N"));

        var start = new DateTimeOffset(2037, 1, 2, 9, 0, 0, TimeSpan.Zero);
        var response = await _client.PutAsJsonAsync(
            $"/api/experiment-jobs/{job.JobId}/schedule",
            Schedule(start, start.AddHours(1), 10, "Reject insufficient material"));
        var errorBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, errorBody);
        var error = JsonSerializer.Deserialize<JsonElement>(errorBody);
        Assert.Equal(MaterialIssueCodes.InventoryInsufficient, error.GetProperty("code").GetString());

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Equal(ExperimentJobStatus.Ready.ToString(),
            await verifyDatabase.ExperimentJobs.Where(item => item.JobId == job.JobId)
                .Select(item => item.Status).SingleAsync());
        var scheduleEntryIds = await verifyDatabase.ScheduleEntries
            .Where(item => item.ExperimentJobId == job.JobId)
            .Select(item => item.ScheduleEntryId)
            .ToArrayAsync();
        Assert.Empty(scheduleEntryIds);
        Assert.Empty(await verifyDatabase.ResourceReservations
            .Where(item => scheduleEntryIds.Contains(item.ScheduleEntryId)).ToListAsync());
        Assert.Empty(await verifyDatabase.ExperimentJobMaterialBindings
            .Where(item => item.ExperimentJobId == job.JobId).ToListAsync());
        Assert.Equal(0m, await verifyDatabase.InventoryBalances
            .Where(item => item.LotId == lotId).Select(item => item.Reserved).SingleAsync());
    }

    private async Task<WorkflowVersion> PublishWorkflowAsync()
    {
        var definition = WorkflowTestDefinitions.CreateMoveWorkflow();
        var create = await _client.PostAsJsonAsync("/api/workflows?actor=g5b-api-test", definition);
        create.EnsureSuccessStatusCode();
        var draft = await create.Content.ReadFromJsonAsync<WorkflowVersion>();
        (await _client.PostAsync(
            $"/api/workflows/{draft!.WorkflowId}/versions/{draft.Version}/validate",
            null)).EnsureSuccessStatusCode();
        var publish = await _client.PostAsync(
            $"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/publish?actor=g5b-api-test",
            null);
        publish.EnsureSuccessStatusCode();
        return (await publish.Content.ReadFromJsonAsync<WorkflowVersion>())!;
    }

    private async Task<ExperimentPlan> CreatePublishedPlanAsync(WorkflowVersion workflow)
    {
        var request = CreatePlanRequest(workflow, "Create plan for scheduling test");
        var create = await _client.PostAsJsonAsync("/api/experiment-plans", request);
        var draft = await create.Content.ReadFromJsonAsync<ExperimentPlan>();
        (await _client.PostAsJsonAsync(
            $"/api/experiment-plans/{draft!.PlanId}/versions/{draft.Version}/validate",
            Action("Validate scheduling test plan"))).EnsureSuccessStatusCode();
        var publish = await _client.PostAsJsonAsync(
            $"/api/experiment-plans/{draft.PlanId}/versions/{draft.Version}/publish",
            Action("Publish scheduling test plan"));
        publish.EnsureSuccessStatusCode();
        return (await publish.Content.ReadFromJsonAsync<ExperimentPlan>())!;
    }

    private async Task<ExperimentJob> CreateJobAsync(ExperimentPlan plan, string batchId)
    {
        var response = await _client.PostAsJsonAsync("/api/experiment-jobs", new CreateExperimentJobRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "planner-api-test",
            Reason = $"Create {batchId}",
            PlanId = plan.PlanId,
            PlanVersion = plan.Version,
            SampleBatchId = batchId
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExperimentJob>())!;
    }

    private static SaveExperimentPlanDraftRequest CreatePlanRequest(
        WorkflowVersion workflow,
        string reason,
        IReadOnlyList<ExperimentMaterialRequirement>? materialRequirements = null) => new()
        {
            RequestId = Guid.NewGuid(),
            Actor = "planner-api-test",
            Reason = reason,
            Draft = new ExperimentPlanDraft
            {
                Name = "Anion experiment",
                Description = "Pinned experiment plan for G5-B tests.",
                WorkflowId = workflow.WorkflowId,
                WorkflowVersion = workflow.Version,
                DefaultParameters = new Dictionary<string, string?>
                {
                    ["method"] = "anion"
                },
                // Material reservation is covered by the material-management
                // integration tests; these scheduling tests exercise resource
                // lifecycle behavior without requiring inventory fixtures.
                MaterialRequirements = materialRequirements ?? [],
                ResourceRequirements =
            [
                new ExperimentResourceRequirement
                {
                    ResourceType = ExperimentResourceTypeIds.Agv,
                    ResourceId = "AGV-01",
                    Quantity = 1
                }
            ]
            }
        };

    private static ExperimentSchedulingActionRequest Action(string reason) => new()
    {
        RequestId = Guid.NewGuid(),
        Actor = "planner-api-test",
        Reason = reason
    };

    private static ScheduleExperimentJobRequest Schedule(
        DateTimeOffset start,
        DateTimeOffset end,
        int priority,
        string reason) => new()
        {
            RequestId = Guid.NewGuid(),
            Actor = "planner-api-test",
            Reason = reason,
            PlannedStart = start,
            PlannedEnd = end,
            Priority = priority,
            Resources =
        [
            new ExperimentResourceReference
            {
                ResourceType = ExperimentResourceTypeIds.Agv,
                ResourceId = "AGV-01"
            }
        ]
        };
}
