using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Mes.Tests;

public sealed class ExperimentRuntimeAdmissionApiTests
{
    [Fact]
    public async Task Scheduled_job_admission_is_atomic_audited_and_idempotent()
    {
        using var factory = new MesWebApplicationFactory();
        using var client = factory.CreateClient();
        var plan = await CreatePublishedPlanAsync(client);
        var scheduled = await CreateScheduledJobAsync(
            client,
            plan,
            "G5C-ADMIT",
            new DateTimeOffset(2026, 8, 22, 9, 0, 0, TimeSpan.Zero));
        var request = Admission("Admit the approved experiment window");

        var response = await client.PostAsJsonAsync(
            $"/api/experiment-jobs/{scheduled.Job.JobId}/admit",
            request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var admitted = await response.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>();
        Assert.True(admitted!.IsAdmitted);
        Assert.False(admitted.IsIdempotentReplay);
        Assert.NotNull(admitted.WorkflowRunId);
        Assert.Equal(ExperimentJobStatus.Admitted, admitted.Job!.Status);
        Assert.Equal(ScheduleEntryStatus.Admitted, admitted.ScheduleEntry!.Status);
        Assert.Equal(ResourceReservationStatus.Released, Assert.Single(admitted.ScheduleEntry.Reservations).Status);
        Assert.Equal(ResourceLeaseStatus.Active, Assert.Single(admitted.Leases).Status);
        Assert.Equal(admitted.WorkflowRunId, admitted.Leases[0].WorkflowRunId);

        var replayResponse = await client.PostAsJsonAsync(
            $"/api/experiment-jobs/{scheduled.Job.JobId}/admit",
            request);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.True((await replayResponse.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>())!
            .IsIdempotentReplay);

        var reusedResponse = await client.PostAsJsonAsync(
            $"/api/experiment-jobs/{scheduled.Job.JobId}/admit",
            request with { Reason = "Change the payload for the same request id" });
        Assert.Equal(HttpStatusCode.Conflict, reusedResponse.StatusCode);
        var reused = await reusedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            ExperimentSchedulingIssueCodes.AdmissionRequestIdReused,
            reused.GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Single(await database.WorkflowExecutions.AsNoTracking().ToListAsync());
        Assert.Single(await database.WorkflowNodeExecutions.AsNoTracking().ToListAsync());
        Assert.Empty(await database.WorkflowDeviceOperations.AsNoTracking().ToListAsync());
        Assert.Single(await database.WorkflowResourceLeases.AsNoTracking()
            .Where(lease => lease.ActiveResourceKey != null)
            .ToListAsync());
        Assert.Single(await database.ExperimentSchedulingAudits.AsNoTracking()
            .Where(audit => audit.RequestId == request.RequestId &&
                            audit.EventType == "ExperimentJobAdmitted")
            .ToListAsync());
    }

    [Fact]
    public async Task Two_scheduled_jobs_competing_for_one_runtime_resource_admit_exactly_one()
    {
        using var factory = new MesWebApplicationFactory();
        using var client = factory.CreateClient();
        var plan = await CreatePublishedPlanAsync(client);
        var first = await CreateScheduledJobAsync(
            client,
            plan,
            "G5C-FIRST",
            new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero));
        var second = await CreateScheduledJobAsync(
            client,
            plan,
            "G5C-SECOND",
            new DateTimeOffset(2026, 8, 22, 11, 0, 0, TimeSpan.Zero));

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(
                $"/api/experiment-jobs/{first.Job.JobId}/admit",
                Admission("Admit first resource contender")),
            client.PostAsJsonAsync(
                $"/api/experiment-jobs/{second.Job.JobId}/admit",
                Admission("Admit second resource contender")));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        var conflictResponse = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var conflict = await conflictResponse.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>();
        Assert.True(conflict!.IsRejected);
        Assert.Equal(ExperimentSchedulingIssueCodes.ResourceLeaseActive, conflict.RejectionCode);
        Assert.Equal("AGV-01", Assert.Single(conflict.ConflictingResources).ResourceId);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Equal(1, await database.WorkflowExecutions.CountAsync());
        Assert.Equal(1, await database.WorkflowResourceLeases.CountAsync(
            lease => lease.ActiveResourceKey != null));
        Assert.Equal(1, await database.ExperimentJobs.CountAsync(
            job => job.Status == ExperimentJobStatus.Admitted.ToString()));
        Assert.Equal(1, await database.ExperimentJobs.CountAsync(
            job => job.Status == ExperimentJobStatus.Scheduled.ToString()));
    }

    [Fact]
    public async Task Workflow_runtime_rejection_rolls_back_run_and_leases_but_keeps_admission_evidence()
    {
        using var factory = new MesWebApplicationFactory();
        using var client = factory.CreateClient();
        var plan = await CreatePublishedPlanAsync(client);
        var scheduled = await CreateScheduledJobAsync(
            client,
            plan,
            "G5C-RUNTIME-REJECT",
            new DateTimeOffset(2026, 8, 22, 11, 30, 0, TimeSpan.Zero));
        using (var mutateScope = factory.Services.CreateScope())
        {
            var database = mutateScope.ServiceProvider.GetRequiredService<MesDbContext>();
            var version = await database.WorkflowVersions.SingleAsync(item =>
                item.WorkflowId == plan.WorkflowId && item.Version == plan.WorkflowVersion);
            version.DefinitionJson = JsonSerializer.Serialize(
                WorkflowTestDefinitions.CreateMoveWorkflow(plan.WorkflowId, "NOT-IN-ACTIVE-PROFILE"));
            await database.SaveChangesAsync();
        }
        var request = Admission("Exercise transaction rollback after runtime Profile rejection");

        var response = await client.PostAsJsonAsync(
            $"/api/experiment-jobs/{scheduled.Job.JobId}/admit",
            request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var rejection = await response.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>();
        Assert.True(rejection!.IsRejected);
        Assert.Equal(ExperimentSchedulingIssueCodes.WorkflowAdmissionRejected, rejection.RejectionCode);
        Assert.Null(rejection.WorkflowRunId);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Empty(await verifyDatabase.WorkflowExecutions.ToListAsync());
        Assert.Empty(await verifyDatabase.WorkflowResourceLeases.ToListAsync());
        Assert.Equal(
            ExperimentJobStatus.Scheduled.ToString(),
            (await verifyDatabase.ExperimentJobs.SingleAsync(
                job => job.JobId == scheduled.Job.JobId)).Status);
        Assert.Equal(
            ResourceReservationStatus.Planned.ToString(),
            (await verifyDatabase.ResourceReservations.SingleAsync(
                reservation => reservation.ScheduleEntryId == scheduled.Schedule.ScheduleEntryId)).Status);
        Assert.Single(await verifyDatabase.ExperimentSchedulingAudits.Where(audit =>
            audit.RequestId == request.RequestId &&
            audit.EventType == "ExperimentJobAdmissionRejected").ToListAsync());
    }

    [Fact]
    public async Task Paused_and_unknown_runs_retain_lease_until_terminal_resolution()
    {
        using var factory = new MesWebApplicationFactory();
        using var client = factory.CreateClient();
        var plan = await CreatePublishedPlanAsync(client);
        var scheduled = await CreateScheduledJobAsync(
            client,
            plan,
            "G5C-UNKNOWN",
            new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero));
        var admitted = await AdmitAsync(client, scheduled.Job.JobId, "Admit Unknown lifecycle test");

        using var scope = factory.Services.CreateScope();
        var workflows = scope.ServiceProvider.GetRequiredService<IWorkflowApplicationService>();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        await workflows.PauseRunAsync(
            admitted.WorkflowRunId!.Value,
            Control("Pause before resource-backed execution"),
            CancellationToken.None);
        Assert.Equal(1, await database.WorkflowResourceLeases.CountAsync(
            lease => lease.ActiveResourceKey != null));

        await workflows.ResumeRunAsync(
            admitted.WorkflowRunId.Value,
            Control("Resume after operator inspection"),
            CancellationToken.None);
        var ready = Assert.Single(await workflows.ListSimulatorDispatchableNodesAsync(CancellationToken.None));
        var claimed = await workflows.ClaimNodeExecutionAsync(
            ready.NodeExecution.Id,
            CancellationToken.None);
        await workflows.CompleteNodeExecutionAsync(
            claimed.NodeExecution.Id,
            new WorkflowNodeExecutionCompletionRequest
            {
                DeviceOperationId = claimed.DeviceOperation!.OperationId,
                Outcome = WorkflowStepCompletionOutcome.Unknown,
                Error = "Adapter outcome could not be proven"
            },
            CancellationToken.None);
        Assert.Equal(1, await database.WorkflowResourceLeases.CountAsync(
            lease => lease.ActiveResourceKey != null));
        Assert.Equal(
            ExperimentJobStatus.Running.ToString(),
            (await database.ExperimentJobs.SingleAsync(job => job.JobId == scheduled.Job.JobId)).Status);

        var resolved = await workflows.ResolveUnknownAsync(
            admitted.WorkflowRunId.Value,
            new WorkflowUnknownResolutionRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = "local-operator",
                Reason = "Persisted field evidence confirms the operation succeeded",
                NodeExecutionId = claimed.NodeExecution.Id,
                Outcome = WorkflowUnknownResolutionOutcome.ConfirmedSucceeded
            },
            CancellationToken.None);

        Assert.Equal(WorkflowRuntimeStatus.Completed, resolved.Run.RuntimeStatus);
        database.ChangeTracker.Clear();
        var job = await database.ExperimentJobs.SingleAsync(item => item.JobId == scheduled.Job.JobId);
        var lease = await database.WorkflowResourceLeases.SingleAsync(
            item => item.WorkflowRunId == admitted.WorkflowRunId);
        Assert.Equal(ExperimentJobStatus.Completed.ToString(), job.Status);
        Assert.Null(lease.ActiveResourceKey);
        Assert.Equal(ResourceLeaseStatus.Released.ToString(), lease.Status);
        Assert.Contains(await database.ExperimentSchedulingAudits.ToListAsync(),
            audit => audit.EventType == "ExperimentJobCompleted" &&
                     audit.ExperimentJobId == scheduled.Job.JobId);
    }

    [Fact]
    public async Task Failed_and_safely_cancelled_runs_release_leases_without_device_commands()
    {
        using var factory = new MesWebApplicationFactory();
        using var client = factory.CreateClient();
        var plan = await CreatePublishedPlanAsync(client);
        var failedJob = await CreateScheduledJobAsync(
            client,
            plan,
            "G5C-FAILED",
            new DateTimeOffset(2026, 8, 22, 13, 0, 0, TimeSpan.Zero));
        var failedAdmission = await AdmitAsync(client, failedJob.Job.JobId, "Admit failure lifecycle test");

        using (var scope = factory.Services.CreateScope())
        {
            var workflows = scope.ServiceProvider.GetRequiredService<IWorkflowApplicationService>();
            var ready = Assert.Single(await workflows.ListSimulatorDispatchableNodesAsync(CancellationToken.None));
            var claimed = await workflows.ClaimNodeExecutionAsync(ready.NodeExecution.Id, CancellationToken.None);
            await workflows.CompleteNodeExecutionAsync(
                claimed.NodeExecution.Id,
                new WorkflowNodeExecutionCompletionRequest
                {
                    DeviceOperationId = claimed.DeviceOperation!.OperationId,
                    Outcome = WorkflowStepCompletionOutcome.Failed,
                    Error = "Normalized worker failure evidence"
                },
                CancellationToken.None);
        }

        var cancelledJob = await CreateScheduledJobAsync(
            client,
            plan,
            "G5C-CANCELLED",
            new DateTimeOffset(2026, 8, 22, 14, 0, 0, TimeSpan.Zero));
        var cancelledAdmission = await AdmitAsync(
            client,
            cancelledJob.Job.JobId,
            "Admit cancellation lifecycle test");
        using (var scope = factory.Services.CreateScope())
        {
            var workflows = scope.ServiceProvider.GetRequiredService<IWorkflowApplicationService>();
            await workflows.CancelRunAsync(
                cancelledAdmission.WorkflowRunId!.Value,
                Control("Withdraw the quiescent experiment"),
                CancellationToken.None);
        }

        using var verifyScope = factory.Services.CreateScope();
        var database = verifyScope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Equal(
            ExperimentJobStatus.Failed.ToString(),
            (await database.ExperimentJobs.SingleAsync(job => job.JobId == failedJob.Job.JobId)).Status);
        Assert.Equal(
            ExperimentJobStatus.Cancelled.ToString(),
            (await database.ExperimentJobs.SingleAsync(job => job.JobId == cancelledJob.Job.JobId)).Status);
        Assert.Empty(await database.WorkflowResourceLeases
            .Where(lease => lease.ActiveResourceKey != null)
            .ToListAsync());
        Assert.Empty(await database.WorkflowDeviceOperations
            .Where(operation => operation.WorkflowRunId == cancelledAdmission.WorkflowRunId)
            .ToListAsync());
        Assert.NotEqual(failedAdmission.WorkflowRunId, cancelledAdmission.WorkflowRunId);
    }

    [Fact]
    public async Task Recovery_releases_only_unowned_or_terminal_leases_and_legacy_execute_stays_independent()
    {
        using var factory = new MesWebApplicationFactory();
        using var client = factory.CreateClient();
        var workflow = await PublishWorkflowAsync(client);
        var direct = await client.PostAsJsonAsync("/api/workflows/execute", new WorkflowExecutionRequest
        {
            WorkflowId = workflow.WorkflowId,
            Version = workflow.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "legacy-direct-test"
        });
        Assert.Equal(HttpStatusCode.Accepted, direct.StatusCode);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Empty(await database.ExperimentJobs.ToListAsync());
        Assert.Empty(await database.WorkflowResourceLeases.ToListAsync());

        var now = DateTime.UtcNow;
        var missingRunId = Guid.NewGuid();
        var terminalRun = CreateAcceptedRun(WorkflowRuntimeStatus.Completed, now);
        var unknownRun = CreateAcceptedRun(WorkflowRuntimeStatus.Unknown, now);
        var recoveryJob = new ExperimentJobRecord
        {
            JobId = Guid.NewGuid(),
            PlanId = Guid.NewGuid(),
            PlanVersion = 1,
            WorkflowId = Guid.NewGuid(),
            WorkflowVersion = 1,
            SampleBatchId = "RECOVERY-MISSING-RUN",
            Status = ExperimentJobStatus.Running.ToString(),
            WorkflowRunId = missingRunId,
            CreatedBy = "recovery-test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var recoverySchedule = new ScheduleEntryRecord
        {
            ScheduleEntryId = Guid.NewGuid(),
            ExperimentJobId = recoveryJob.JobId,
            PlannedStartUtc = now.AddHours(-1),
            PlannedEndUtc = now,
            Status = ScheduleEntryStatus.Admitted.ToString(),
            CreatedBy = "recovery-test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        database.ExperimentJobs.Add(recoveryJob);
        database.ScheduleEntries.Add(recoverySchedule);
        database.WorkflowExecutions.AddRange(terminalRun, unknownRun);
        database.WorkflowResourceLeases.AddRange(
            CreateLease(missingRunId, recoverySchedule.ScheduleEntryId, "agv", "AGV-01", now.AddMinutes(-5)),
            CreateLease(terminalRun.ExecutionId, null, "station", "SAMPLE_01", now.AddMinutes(-5)),
            CreateLease(unknownRun.ExecutionId, null, "station", "ST_OPEN_01", now.AddMinutes(-5)));
        await database.SaveChangesAsync();

        var summary = await scope.ServiceProvider
            .GetRequiredService<ExperimentRuntimeRecoveryCoordinator>()
            .ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, summary.RecoveryFailures);
        Assert.Equal(1, summary.ReleasedDetachedLeases);
        database.ChangeTracker.Clear();
        Assert.Equal(
            ExperimentJobStatus.Failed.ToString(),
            (await database.ExperimentJobs.SingleAsync(job => job.JobId == recoveryJob.JobId)).Status);
        Assert.Null((await database.WorkflowResourceLeases.SingleAsync(
            lease => lease.WorkflowRunId == missingRunId)).ActiveResourceKey);
        Assert.Null((await database.WorkflowResourceLeases.SingleAsync(
            lease => lease.WorkflowRunId == terminalRun.ExecutionId)).ActiveResourceKey);
        var retained = await database.WorkflowResourceLeases.SingleAsync(
            lease => lease.WorkflowRunId == unknownRun.ExecutionId);
        Assert.NotNull(retained.ActiveResourceKey);
        Assert.True(retained.ExpiresAtUtc < DateTime.UtcNow);
    }

    private static async Task<ExperimentPlan> CreatePublishedPlanAsync(HttpClient client)
    {
        var workflow = await PublishWorkflowAsync(client);
        var create = await client.PostAsJsonAsync("/api/experiment-plans", new SaveExperimentPlanDraftRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "g5c-test",
            Reason = "Create G5-C runtime admission plan",
            Draft = new ExperimentPlanDraft
            {
                Name = "G5-C runtime plan",
                Description = "Pinned plan used by runtime lease lifecycle tests.",
                WorkflowId = workflow.WorkflowId,
                WorkflowVersion = workflow.Version,
                DefaultParameters = new Dictionary<string, string?>
                {
                    ["method"] = "anion"
                },
                ResourceRequirements =
                [
                    new ExperimentResourceRequirement
                    {
                        ResourceType = ExperimentResourceTypeIds.Agv,
                        ResourceId = "AGV-01"
                    }
                ],
                ProfileProductId = "MES-AGV",
                ProfileVersion = "1.0"
            }
        });
        create.EnsureSuccessStatusCode();
        var draft = (await create.Content.ReadFromJsonAsync<ExperimentPlan>())!;
        (await client.PostAsJsonAsync(
            $"/api/experiment-plans/{draft.PlanId}/versions/{draft.Version}/validate",
            Action("Validate G5-C plan"))).EnsureSuccessStatusCode();
        var publish = await client.PostAsJsonAsync(
            $"/api/experiment-plans/{draft.PlanId}/versions/{draft.Version}/publish",
            Action("Publish G5-C plan"));
        publish.EnsureSuccessStatusCode();
        return (await publish.Content.ReadFromJsonAsync<ExperimentPlan>())!;
    }

    private static async Task<WorkflowVersion> PublishWorkflowAsync(HttpClient client)
    {
        var create = await client.PostAsJsonAsync(
            "/api/workflows?actor=g5c-test",
            WorkflowTestDefinitions.CreateMoveWorkflow());
        create.EnsureSuccessStatusCode();
        var draft = (await create.Content.ReadFromJsonAsync<WorkflowVersion>())!;
        (await client.PostAsync(
            $"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/validate",
            null)).EnsureSuccessStatusCode();
        var publish = await client.PostAsync(
            $"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/publish?actor=g5c-test",
            null);
        publish.EnsureSuccessStatusCode();
        return (await publish.Content.ReadFromJsonAsync<WorkflowVersion>())!;
    }

    private static async Task<ScheduledJob> CreateScheduledJobAsync(
        HttpClient client,
        ExperimentPlan plan,
        string batchId,
        DateTimeOffset start)
    {
        var create = await client.PostAsJsonAsync("/api/experiment-jobs", new CreateExperimentJobRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "g5c-test",
            Reason = $"Create {batchId}",
            PlanId = plan.PlanId,
            PlanVersion = plan.Version,
            SampleBatchId = batchId
        });
        create.EnsureSuccessStatusCode();
        var job = (await create.Content.ReadFromJsonAsync<ExperimentJob>())!;
        var schedule = await client.PutAsJsonAsync(
            $"/api/experiment-jobs/{job.JobId}/schedule",
            new ScheduleExperimentJobRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = "g5c-test",
                Reason = $"Schedule {batchId}",
                PlannedStart = start,
                PlannedEnd = start.AddHours(1),
                Priority = 50,
                Resources =
                [
                    new ExperimentResourceReference
                    {
                        ResourceType = ExperimentResourceTypeIds.Agv,
                        ResourceId = "AGV-01"
                    }
                ]
            });
        schedule.EnsureSuccessStatusCode();
        var entry = (await schedule.Content.ReadFromJsonAsync<ScheduleEntry>())!;
        Assert.Equal(ScheduleEntryStatus.Scheduled, entry.Status);
        return new ScheduledJob(job, entry);
    }

    private static async Task<ExperimentJobAdmissionResult> AdmitAsync(
        HttpClient client,
        Guid jobId,
        string reason)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/experiment-jobs/{jobId}/admit",
            Admission(reason));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>())!;
    }

    private static ExperimentSchedulingActionRequest Action(string reason) => new()
    {
        RequestId = Guid.NewGuid(),
        Actor = "g5c-test",
        Reason = reason
    };

    private static AdmitExperimentJobRequest Admission(string reason) => new()
    {
        RequestId = Guid.NewGuid(),
        Actor = "g5c-test",
        Reason = reason
    };

    private static WorkflowRunControlRequest Control(string reason) => new()
    {
        RequestId = Guid.NewGuid(),
        Actor = "local-operator",
        Reason = reason
    };

    private static WorkflowExecutionRecord CreateAcceptedRun(
        WorkflowRuntimeStatus status,
        DateTime now)
    {
        var requestId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        return new WorkflowExecutionRecord
        {
            RequestId = requestId,
            Fingerprint = requestId.ToString("N"),
            WorkflowId = workflowId,
            Version = 1,
            ExecutionId = executionId,
            Outcome = WorkflowExecutionStatus.Accepted.ToString(),
            RequestJson = JsonSerializer.Serialize(new WorkflowExecutionRequest
            {
                WorkflowId = workflowId,
                Version = 1,
                RequestId = requestId,
                RequestedBy = "recovery-test"
            }),
            ResultJson = JsonSerializer.Serialize(new WorkflowExecutionResult
            {
                Status = WorkflowExecutionStatus.Accepted,
                RequestId = requestId,
                ExecutionId = executionId,
                WorkflowId = workflowId,
                Version = 1
            }),
            RuntimeStatus = status.ToString(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    private static WorkflowResourceLeaseRecord CreateLease(
        Guid workflowRunId,
        Guid? scheduleEntryId,
        string resourceType,
        string resourceId,
        DateTime expiresAt)
    {
        var key = ExperimentResourceKeys.Create(resourceType, resourceId);
        return new WorkflowResourceLeaseRecord
        {
            LeaseId = Guid.NewGuid(),
            ScheduleEntryId = scheduleEntryId,
            WorkflowRunId = workflowRunId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            ResourceKey = key,
            ActiveResourceKey = key,
            Status = ResourceLeaseStatus.Active.ToString(),
            AcquiredBy = "recovery-test",
            AcquiredAtUtc = expiresAt.AddHours(-1),
            ExpiresAtUtc = expiresAt,
            UpdatedAtUtc = expiresAt.AddHours(-1)
        };
    }

    private sealed record ScheduledJob(ExperimentJob Job, ScheduleEntry Schedule);
}
