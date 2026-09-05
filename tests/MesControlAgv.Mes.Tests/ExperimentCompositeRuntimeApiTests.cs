using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Mes.Tests;

public sealed class ExperimentCompositeRuntimeApiTests
{
    [Fact]
    public async Task Prepare_persists_pinned_steps_without_creating_child_workflows_or_device_operations()
    {
        using var factory = new MesWebApplicationFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedScheduledCompositeJobAsync(factory);
        var request = new PrepareExperimentRunRequest
        {
            RequestId = Guid.NewGuid(),
            ExperimentJobId = fixture.JobId,
            Actor = "composite-planner",
            Reason = "Prepare the approved multi-step plan"
        };

        var response = await client.PostAsJsonAsync("/api/experiment-runs/prepare", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = await response.Content.ReadFromJsonAsync<ExperimentRun>();
        Assert.NotNull(run);
        Assert.Equal(ExperimentRunStatus.Prepared, run!.Status);
        Assert.Equal(fixture.JobId, run.ExperimentJobId);
        Assert.Equal(fixture.PlanId, run.PlanId);
        Assert.Equal(2, run.Steps.Count);
        Assert.All(run.Steps, step =>
        {
            Assert.Equal(ExperimentStepRunStatus.Pending, step.Status);
            Assert.Null(step.WorkflowRunId);
            Assert.NotEqual(Guid.Empty, step.StepRunId);
        });
        Assert.Equal(run.Steps[0].StepRunId, StableStepRunId(run.ExperimentRunId, run.Steps[0].StepId));
        Assert.Null(run.CurrentStepRunId);

        var get = await client.GetFromJsonAsync<ExperimentRun>(
            $"/api/experiment-runs/{run.ExperimentRunId}");
        Assert.NotNull(get);
        Assert.Equal(run.ExperimentRunId, get!.ExperimentRunId);
        Assert.Equal(run.ExperimentJobId, get.ExperimentJobId);
        Assert.Equal(run.PlanId, get.PlanId);
        Assert.Equal(run.Status, get.Status);
        Assert.Equal(run.Steps.Select(step => step.StepRunId), get.Steps.Select(step => step.StepRunId));
        Assert.Equal(run.Steps.Select(step => step.Status), get.Steps.Select(step => step.Status));

        var getByJob = await client.GetFromJsonAsync<ExperimentRun>(
            $"/api/experiment-jobs/{fixture.JobId}/experiment-run");
        Assert.NotNull(getByJob);
        Assert.Equal(run.ExperimentRunId, getByJob!.ExperimentRunId);
        Assert.Equal(run.ExperimentJobId, getByJob.ExperimentJobId);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Single(await database.ExperimentRuns.AsNoTracking().ToListAsync());
        Assert.Empty(await database.WorkflowExecutions.AsNoTracking().ToListAsync());
        Assert.Empty(await database.WorkflowDeviceOperations.AsNoTracking().ToListAsync());
        var audit = await database.ExperimentSchedulingAudits.AsNoTracking()
            .SingleAsync(item => item.RequestId == request.RequestId);
        Assert.Equal("ExperimentCompositeRunPrepared", audit.EventType);
        Assert.Contains("deviceWritesAttempted", audit.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_is_idempotent_for_the_same_request_and_rejects_payload_reuse()
    {
        using var factory = new MesWebApplicationFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedScheduledCompositeJobAsync(factory);
        var request = new PrepareExperimentRunRequest
        {
            RequestId = Guid.NewGuid(),
            ExperimentJobId = fixture.JobId,
            Actor = "composite-planner",
            Reason = "Prepare once"
        };

        var firstResponse = await client.PostAsJsonAsync("/api/experiment-runs/prepare", request);
        var first = await firstResponse.Content.ReadFromJsonAsync<ExperimentRun>();
        var replayResponse = await client.PostAsJsonAsync("/api/experiment-runs/prepare", request);
        var replay = await replayResponse.Content.ReadFromJsonAsync<ExperimentRun>();

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.NotNull(first);
        Assert.NotNull(replay);
        Assert.Equal(first!.ExperimentRunId, replay!.ExperimentRunId);
        Assert.Equal(first.ExperimentJobId, replay.ExperimentJobId);
        Assert.Equal(first.Steps.Select(step => step.StepRunId), replay.Steps.Select(step => step.StepRunId));

        var reusedResponse = await client.PostAsJsonAsync(
            "/api/experiment-runs/prepare",
            request with { Reason = "Different payload" });
        Assert.Equal(HttpStatusCode.Conflict, reusedResponse.StatusCode);
        var body = await reusedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ExperimentSchedulingIssueCodes.AdmissionRequestIdReused, body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Prepare_rejects_single_step_or_unscheduled_jobs_before_creating_a_snapshot()
    {
        using var factory = new MesWebApplicationFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedScheduledCompositeJobAsync(factory, stepCount: 1);
        var request = new PrepareExperimentRunRequest
        {
            RequestId = Guid.NewGuid(),
            ExperimentJobId = fixture.JobId,
            Actor = "composite-planner",
            Reason = "Reject single step"
        };

        var singleResponse = await client.PostAsJsonAsync("/api/experiment-runs/prepare", request);
        Assert.Equal(HttpStatusCode.Conflict, singleResponse.StatusCode);
        var single = await singleResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ExperimentSchedulingIssueCodes.CompositeWorkflowNotSupported, single.GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        var job = await database.ExperimentJobs.SingleAsync(item => item.JobId == fixture.JobId);
        job.Status = ExperimentJobStatus.Draft.ToString();
        await database.SaveChangesAsync();

        var unscheduledResponse = await client.PostAsJsonAsync(
            "/api/experiment-runs/prepare",
            request with { RequestId = Guid.NewGuid(), Reason = "Reject unscheduled" });
        Assert.Equal(HttpStatusCode.Conflict, unscheduledResponse.StatusCode);
        var unscheduled = await unscheduledResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ExperimentSchedulingIssueCodes.JobNotScheduled, unscheduled.GetProperty("code").GetString());
        Assert.Empty(await database.ExperimentRuns.ToListAsync());
    }

    [Fact]
    public async Task Reconcile_child_requires_matching_identity_and_advances_one_step_at_a_time()
    {
        using var factory = new MesWebApplicationFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedScheduledCompositeJobAsync(factory);
        var prepared = await PrepareAsync(client, fixture.JobId, "Prepare for child reconciliation");
        var childId = Guid.NewGuid();
        await SeedChildWorkflowAsync(factory, prepared.Steps[0], childId, WorkflowRuntimeStatus.Running);

        var bindResponse = await client.PostAsJsonAsync(
            $"/api/experiment-runs/{prepared.ExperimentRunId}/reconcile-child",
            new ReconcileExperimentChildRequest
            {
                RequestId = Guid.NewGuid(),
                ChildWorkflowRunId = childId,
                Actor = "composite-coordinator",
                Reason = "Bind the first child workflow"
            });
        Assert.Equal(HttpStatusCode.OK, bindResponse.StatusCode);
        var bound = await bindResponse.Content.ReadFromJsonAsync<ExperimentRun>();
        Assert.Equal(ExperimentRunStatus.Running, bound!.Status);
        Assert.Equal(ExperimentStepRunStatus.Running, bound.Steps[0].Status);
        Assert.Equal(childId, bound.Steps[0].WorkflowRunId);

        await SetChildRuntimeStatusAsync(factory, childId, WorkflowRuntimeStatus.Completed);
        var completeResponse = await client.PostAsJsonAsync(
            $"/api/experiment-runs/{prepared.ExperimentRunId}/reconcile-child",
            new ReconcileExperimentChildRequest
            {
                RequestId = Guid.NewGuid(),
                ChildWorkflowRunId = childId,
                Actor = "composite-coordinator",
                Reason = "Reconcile completed first child"
            });
        var advanced = await completeResponse.Content.ReadFromJsonAsync<ExperimentRun>();
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        Assert.Equal(ExperimentRunStatus.Running, advanced!.Status);
        Assert.Equal(2, advanced.CurrentStepOrder);
        Assert.Equal(ExperimentStepRunStatus.Succeeded, advanced.Steps[0].Status);
        Assert.Equal(ExperimentStepRunStatus.Ready, advanced.Steps[1].Status);
        Assert.Null(advanced.Steps[1].WorkflowRunId);
    }

    [Fact]
    public async Task Reconcile_unknown_child_requires_human_reason_and_rejects_mismatched_workflow()
    {
        using var factory = new MesWebApplicationFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedScheduledCompositeJobAsync(factory);
        var prepared = await PrepareAsync(client, fixture.JobId, "Prepare for unknown reconciliation");
        var childId = Guid.NewGuid();
        await SeedChildWorkflowAsync(factory, prepared.Steps[0], childId, WorkflowRuntimeStatus.Unknown);

        var unknownResponse = await client.PostAsJsonAsync(
            $"/api/experiment-runs/{prepared.ExperimentRunId}/reconcile-child",
            new ReconcileExperimentChildRequest
            {
                RequestId = Guid.NewGuid(),
                ChildWorkflowRunId = childId,
                Actor = "composite-coordinator",
                Reason = "Record unknown child outcome"
            });
        var unknown = await unknownResponse.Content.ReadFromJsonAsync<ExperimentRun>();
        Assert.Equal(HttpStatusCode.OK, unknownResponse.StatusCode);
        Assert.Equal(ExperimentRunStatus.Unknown, unknown!.Status);
        Assert.False(unknown.IsTerminal);

        await SetChildRuntimeStatusAsync(factory, childId, WorkflowRuntimeStatus.Completed);
        var missingReason = await client.PostAsJsonAsync(
            $"/api/experiment-runs/{prepared.ExperimentRunId}/reconcile-child",
            new ReconcileExperimentChildRequest
            {
                RequestId = Guid.NewGuid(),
                ChildWorkflowRunId = childId,
                Actor = "composite-coordinator",
                Reason = "Attempt resolution without evidence reason"
            });
        Assert.Equal(HttpStatusCode.Conflict, missingReason.StatusCode);

        var resolvedResponse = await client.PostAsJsonAsync(
            $"/api/experiment-runs/{prepared.ExperimentRunId}/reconcile-child",
            new ReconcileExperimentChildRequest
            {
                RequestId = Guid.NewGuid(),
                ChildWorkflowRunId = childId,
                Actor = "composite-coordinator",
                Reason = "Record operator reconciliation",
                UnknownResolutionReason = "Operator verified the child terminal evidence"
            });
        var resolved = await resolvedResponse.Content.ReadFromJsonAsync<ExperimentRun>();
        Assert.Equal(HttpStatusCode.OK, resolvedResponse.StatusCode);
        Assert.Equal(ExperimentRunStatus.Running, resolved!.Status);
        Assert.Equal(ExperimentStepRunStatus.Ready, resolved.Steps[1].Status);

        var wrongChildId = Guid.NewGuid();
        await SeedChildWorkflowAsync(factory, resolved.Steps[1], wrongChildId, WorkflowRuntimeStatus.Running,
            workflowIdOverride: Guid.NewGuid());
        var mismatch = await client.PostAsJsonAsync(
            $"/api/experiment-runs/{prepared.ExperimentRunId}/reconcile-child",
            new ReconcileExperimentChildRequest
            {
                RequestId = Guid.NewGuid(),
                ChildWorkflowRunId = wrongChildId,
                Actor = "composite-coordinator",
                Reason = "Reject mismatched child"
            });
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
    }

    private static async Task<ExperimentRun> PrepareAsync(HttpClient client, Guid jobId, string reason)
    {
        var response = await client.PostAsJsonAsync(
            "/api/experiment-runs/prepare",
            new PrepareExperimentRunRequest
            {
                RequestId = Guid.NewGuid(),
                ExperimentJobId = jobId,
                Actor = "composite-planner",
                Reason = reason
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExperimentRun>())!;
    }

    private static async Task SeedChildWorkflowAsync(
        MesWebApplicationFactory factory,
        ExperimentStepRun step,
        Guid executionId,
        WorkflowRuntimeStatus status,
        Guid? workflowIdOverride = null)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        var now = DateTime.UtcNow;
        var requestId = Guid.NewGuid();
        var workflowId = workflowIdOverride ?? step.WorkflowId;
        var request = new WorkflowExecutionRequest
        {
            WorkflowId = workflowId,
            Version = step.WorkflowVersion,
            RequestId = requestId,
            RequestedBy = "composite-test",
            RequestedAt = new DateTimeOffset(now, TimeSpan.Zero)
        };
        var result = new WorkflowExecutionResult
        {
            Status = WorkflowExecutionStatus.Accepted,
            RequestId = requestId,
            ExecutionId = executionId,
            WorkflowId = workflowId,
            Version = step.WorkflowVersion,
            RequestedAt = request.RequestedAt,
            DryRun = false
        };
        database.WorkflowExecutions.Add(new WorkflowExecutionRecord
        {
            RequestId = requestId,
            Fingerprint = requestId.ToString("N"),
            WorkflowId = workflowId,
            Version = step.WorkflowVersion,
            ExecutionId = executionId,
            Outcome = WorkflowExecutionStatus.Accepted.ToString(),
            RequestJson = JsonSerializer.Serialize(request),
            ResultJson = JsonSerializer.Serialize(result),
            RuntimeStatus = status.ToString(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastError = status == WorkflowRuntimeStatus.Unknown ? "child outcome unknown" : null
        });
        await database.SaveChangesAsync();
    }

    private static async Task SetChildRuntimeStatusAsync(
        MesWebApplicationFactory factory,
        Guid executionId,
        WorkflowRuntimeStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        var child = await database.WorkflowExecutions.SingleAsync(item => item.ExecutionId == executionId);
        child.RuntimeStatus = status.ToString();
        child.LastError = null;
        child.UpdatedAtUtc = DateTime.UtcNow;
        await database.SaveChangesAsync();
    }

    private static async Task<SeededCompositeJob> SeedScheduledCompositeJobAsync(
        MesWebApplicationFactory factory,
        int stepCount = 2)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        var now = DateTime.UtcNow;
        var planId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var steps = Enumerable.Range(1, stepCount)
            .Select(order => new ExperimentPlanWorkflowStep
            {
                StepId = Guid.NewGuid(),
                Order = order,
                WorkflowId = Guid.NewGuid(),
                WorkflowVersion = order,
                Name = order == 1 ? "搬运" : "检测",
                EstimatedDurationMinutes = 10
            })
            .ToArray();
        foreach (var step in steps)
        {
            var definition = WorkflowTestDefinitions.CreateMoveWorkflow(step.WorkflowId, "SAMPLE_01");
            database.WorkflowVersions.Add(new WorkflowVersionRecord
            {
                WorkflowId = step.WorkflowId,
                Version = step.WorkflowVersion,
                DefinitionJson = JsonSerializer.Serialize(definition),
                Status = WorkflowVersionStatus.Published.ToString(),
                PublishStatus = WorkflowPublishStatus.Published.ToString(),
                CreatedBy = "composite-test",
                CreatedAtUtc = now,
                PublishedAtUtc = now,
                UpdatedAtUtc = now
            });
        }

        var first = steps[0];
        database.ExperimentPlans.Add(new ExperimentPlanRecord
        {
            PlanId = planId,
            Version = 1,
            Name = "Composite plan",
            Description = "Composite runtime preparation test",
            WorkflowId = first.WorkflowId,
            WorkflowVersion = first.WorkflowVersion,
            WorkflowStepsJson = JsonSerializer.Serialize(steps),
            Status = ExperimentPlanStatus.Published.ToString(),
            MaterialRequirementsJson = "[]",
            DefaultParametersJson = "{}",
            ResourceRequirementsJson = "[]",
            ProfileProductId = "MES-AGV",
            ProfileVersion = "1.0",
            CreatedBy = "composite-test",
            CreatedAtUtc = now,
            PublishedAtUtc = now,
            UpdatedAtUtc = now
        });
        database.ExperimentJobs.Add(new ExperimentJobRecord
        {
            JobId = jobId,
            PlanId = planId,
            PlanVersion = 1,
            WorkflowId = first.WorkflowId,
            WorkflowVersion = first.WorkflowVersion,
            WorkflowStepsJson = JsonSerializer.Serialize(steps),
            SampleBatchId = "COMPOSITE-1",
            ParametersJson = "{}",
            Status = ExperimentJobStatus.Scheduled.ToString(),
            CreatedBy = "composite-test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        database.ScheduleEntries.Add(new ScheduleEntryRecord
        {
            ScheduleEntryId = Guid.NewGuid(),
            ExperimentJobId = jobId,
            PlannedStartUtc = now,
            PlannedEndUtc = now.AddHours(1),
            Priority = 10,
            Status = ScheduleEntryStatus.Scheduled.ToString(),
            RequestedResourcesJson = "[]",
            BlockingReasonsJson = "[]",
            CreatedBy = "composite-test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await database.SaveChangesAsync();
        return new SeededCompositeJob(planId, jobId);
    }

    private static Guid StableStepRunId(Guid runId, Guid stepId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"experiment-step-run\u001f{runId:N}\u001f{stepId:N}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private sealed record SeededCompositeJob(Guid PlanId, Guid JobId);
}
