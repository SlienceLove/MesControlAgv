using System.Security.Cryptography;
using System.Text;
using MesControlAgv.Application;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Domain.Experiments;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Persists the outer snapshot for a composed experiment run. This first slice
/// deliberately stops at Prepared: it creates no child workflow and no device
/// operation. A later coordinator may advance the snapshot only after it has
/// durable child-workflow evidence.
/// </summary>
public sealed class ExperimentCompositeRuntimeService(
    MesDbContext database,
    ExperimentSchedulingMutationGate mutationGate,
    TimeProvider timeProvider,
    IWorkflowApplicationService workflows) : IExperimentCompositeRuntimeService
{
    private const string PreparedEventType = "ExperimentCompositeRunPrepared";

    public async Task<ExperimentRun> PrepareAsync(
        PrepareExperimentRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var metadata = NormalizeMetadata(request);
        var fingerprint = CreateFingerprint(metadata);

        await mutationGate.EnterAsync(cancellationToken);
        try
        {
            var priorAudit = await database.ExperimentSchedulingAudits.AsNoTracking()
                .SingleOrDefaultAsync(item => item.RequestId == metadata.RequestId, cancellationToken);
            if (priorAudit is not null)
            {
                if (priorAudit.EventType != PreparedEventType ||
                    !string.Equals(priorAudit.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new ExperimentSchedulingConflictException(
                        $"Request id '{metadata.RequestId}' was already used for a different composite-run payload.",
                        ExperimentSchedulingIssueCodes.AdmissionRequestIdReused);
                }

                var replay = ExperimentSchedulingPersistence.Deserialize<ExperimentRun?>(
                    priorAudit.ResultJson,
                    null);
                return replay ?? throw new InvalidOperationException(
                    $"Composite-run audit '{priorAudit.Id}' does not contain a replay snapshot.");
            }

            var existingForJob = await database.ExperimentRuns.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ExperimentJobId == metadata.ExperimentJobId, cancellationToken);
            if (existingForJob is not null)
            {
                throw new ExperimentSchedulingConflictException(
                    $"Experiment job '{metadata.ExperimentJobId}' already has composite run '{existingForJob.ExperimentRunId}'.",
                    ExperimentSchedulingIssueCodes.CompositeWorkflowNotSupported);
            }

            var job = await database.ExperimentJobs.AsNoTracking()
                .SingleOrDefaultAsync(item => item.JobId == metadata.ExperimentJobId, cancellationToken)
                ?? throw new KeyNotFoundException($"Experiment job '{metadata.ExperimentJobId}' was not found.");
            if (!string.Equals(job.Status, ExperimentJobStatus.Scheduled.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new ExperimentSchedulingConflictException(
                    $"Experiment job '{metadata.ExperimentJobId}' must be Scheduled before a composite run can be prepared.",
                    ExperimentSchedulingIssueCodes.JobNotScheduled);
            }

            var schedule = await database.ScheduleEntries.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ExperimentJobId == metadata.ExperimentJobId, cancellationToken);
            if (schedule is null ||
                !string.Equals(schedule.Status, ScheduleEntryStatus.Scheduled.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new ExperimentSchedulingConflictException(
                    "A Scheduled entry is required before preparing a composite run.",
                    ExperimentSchedulingIssueCodes.ScheduleNotReady);
            }

            var plan = await database.ExperimentPlans.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.PlanId == job.PlanId && item.Version == job.PlanVersion,
                    cancellationToken)
                ?? throw new KeyNotFoundException(
                    $"Experiment plan '{job.PlanId}/{job.PlanVersion}' was not found.");
            if (!string.Equals(plan.Status, ExperimentPlanStatus.Published.ToString(), StringComparison.OrdinalIgnoreCase) ||
                plan.WorkflowId != job.WorkflowId ||
                plan.WorkflowVersion != job.WorkflowVersion)
            {
                throw new ExperimentSchedulingConflictException(
                    "The job's pinned experiment plan is missing, unpublished, or no longer matches the job snapshot.",
                    ExperimentSchedulingIssueCodes.PlanVersionNotPublished);
            }

            var planSteps = ExperimentSchedulingPersistence.NormalizeWorkflowSteps(
                ExperimentSchedulingPersistence.Deserialize(
                    plan.WorkflowStepsJson,
                    Array.Empty<ExperimentPlanWorkflowStep>()),
                plan.WorkflowId,
                plan.WorkflowVersion,
                plan.PlanId);
            if (planSteps.Count <= 1)
            {
                throw new ExperimentSchedulingConflictException(
                    "Composite-run preparation requires at least two pinned workflow steps.",
                    ExperimentSchedulingIssueCodes.CompositeWorkflowNotSupported);
            }

            var jobSteps = ExperimentSchedulingPersistence.NormalizeWorkflowSteps(
                ExperimentSchedulingPersistence.Deserialize(
                    job.WorkflowStepsJson,
                    Array.Empty<ExperimentPlanWorkflowStep>()),
                job.WorkflowId,
                job.WorkflowVersion,
                job.JobId);
            if (!SameWorkflowSteps(planSteps, jobSteps))
            {
                throw new ExperimentSchedulingConflictException(
                    "The job workflow-step snapshot does not match the published plan snapshot.",
                    ExperimentSchedulingIssueCodes.ReservationMismatch);
            }

            var workflowVersions = await database.WorkflowVersions.AsNoTracking()
                .Where(item => planSteps.Select(step => step.WorkflowId).Contains(item.WorkflowId))
                .ToListAsync(cancellationToken);
            foreach (var step in planSteps)
            {
                var version = workflowVersions.SingleOrDefault(item =>
                    item.WorkflowId == step.WorkflowId && item.Version == step.WorkflowVersion);
                if (version is null)
                {
                    throw new ExperimentSchedulingConflictException(
                        $"Workflow version '{step.WorkflowId}/{step.WorkflowVersion}' was not found.",
                        ExperimentSchedulingIssueCodes.WorkflowVersionNotFound);
                }
                if (!string.Equals(version.Status, "Published", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(version.PublishStatus, "Published", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ExperimentSchedulingConflictException(
                        $"Workflow version '{step.WorkflowId}/{step.WorkflowVersion}' is not published.",
                        ExperimentSchedulingIssueCodes.WorkflowVersionNotPublished);
                }
            }

            var now = timeProvider.GetUtcNow();
            var runId = CreateStableRunId(metadata.RequestId, metadata.ExperimentJobId);
            var snapshot = ExperimentCompositeRuntimeStateMachine.Prepare(
                runId,
                metadata.ExperimentJobId,
                metadata.RequestId,
                new ExperimentPlan
                {
                    PlanId = plan.PlanId,
                    Version = plan.Version,
                    Name = plan.Name,
                    WorkflowId = plan.WorkflowId,
                    WorkflowVersion = plan.WorkflowVersion,
                    WorkflowSteps = planSteps
                },
                now);
            var record = new ExperimentRunRecord();
            ExperimentSchedulingPersistence.ApplyExperimentRun(record, snapshot);
            database.ExperimentRuns.Add(record);
            database.ExperimentSchedulingAudits.Add(new ExperimentSchedulingAuditRecord
            {
                Id = Guid.NewGuid(),
                EventType = PreparedEventType,
                Outcome = snapshot.Status.ToString(),
                RequestId = metadata.RequestId,
                RequestFingerprint = fingerprint,
                Actor = metadata.Actor,
                Reason = metadata.Reason,
                PlanId = plan.PlanId,
                PlanVersion = plan.Version,
                ExperimentJobId = metadata.ExperimentJobId,
                ScheduleEntryId = schedule.ScheduleEntryId,
                DetailsJson = ExperimentSchedulingPersistence.Serialize(new Dictionary<string, string?>
                {
                    ["experimentRunId"] = snapshot.ExperimentRunId.ToString(),
                    ["stepCount"] = snapshot.Steps.Count.ToString(),
                    ["deviceWritesAttempted"] = bool.FalseString
                }),
                ResultJson = ExperimentSchedulingPersistence.Serialize(snapshot),
                OccurredAtUtc = now.UtcDateTime
            });

            try
            {
                await database.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                database.ChangeTracker.Clear();
                var concurrent = await database.ExperimentSchedulingAudits.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.RequestId == metadata.RequestId, cancellationToken);
                if (concurrent is not null &&
                    concurrent.EventType == PreparedEventType &&
                    string.Equals(concurrent.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    var replay = ExperimentSchedulingPersistence.Deserialize<ExperimentRun?>(
                        concurrent.ResultJson,
                        null);
                    return replay ?? throw new InvalidOperationException("Composite-run replay audit is invalid.");
                }
                throw;
            }

            return snapshot;
        }
        finally
        {
            mutationGate.Exit();
        }
    }

    public async Task<ExperimentRun?> GetAsync(
        Guid experimentRunId,
        CancellationToken cancellationToken)
    {
        if (experimentRunId == Guid.Empty) return null;
        var record = await database.ExperimentRuns.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ExperimentRunId == experimentRunId, cancellationToken);
        return record is null ? null : ExperimentSchedulingPersistence.MapExperimentRun(record);
    }

    public async Task<ExperimentRun?> GetForJobAsync(
        Guid experimentJobId,
        CancellationToken cancellationToken)
    {
        if (experimentJobId == Guid.Empty) return null;
        var record = await database.ExperimentRuns.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ExperimentJobId == experimentJobId, cancellationToken);
        return record is null ? null : ExperimentSchedulingPersistence.MapExperimentRun(record);
    }

    public async Task<ExperimentRun> ReconcileChildAsync(
        Guid experimentRunId,
        ReconcileExperimentChildRequest request,
        CancellationToken cancellationToken)
    {
        if (experimentRunId == Guid.Empty)
            throw new ArgumentException("An experiment run id is required.", nameof(experimentRunId));
        ArgumentNullException.ThrowIfNull(request);
        var metadata = NormalizeChildMetadata(request);
        var fingerprint = CreateChildFingerprint(experimentRunId, metadata);

        await mutationGate.EnterAsync(cancellationToken);
        try
        {
            var priorAudit = await database.ExperimentSchedulingAudits.AsNoTracking()
                .SingleOrDefaultAsync(item => item.RequestId == metadata.RequestId, cancellationToken);
            if (priorAudit is not null)
            {
                if (priorAudit.EventType != ChildReconciledEventType ||
                    !string.Equals(priorAudit.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new ExperimentSchedulingConflictException(
                        $"Request id '{metadata.RequestId}' was already used for a different child-reconciliation payload.",
                        ExperimentSchedulingIssueCodes.AdmissionRequestIdReused);
                }

                var replay = ExperimentSchedulingPersistence.Deserialize<ExperimentRun?>(
                    priorAudit.ResultJson,
                    null);
                return replay ?? throw new InvalidOperationException(
                    $"Child-reconciliation audit '{priorAudit.Id}' does not contain a replay snapshot.");
            }

            var record = await database.ExperimentRuns.SingleOrDefaultAsync(
                item => item.ExperimentRunId == experimentRunId,
                cancellationToken) ?? throw new KeyNotFoundException(
                $"Experiment run '{experimentRunId}' was not found.");
            var child = await workflows.GetExecutionAsync(
                metadata.ChildWorkflowRunId,
                cancellationToken);
            if (child is null)
            {
                throw new KeyNotFoundException(
                    $"Child workflow run '{metadata.ChildWorkflowRunId}' was not found.");
            }

            var run = ExperimentSchedulingPersistence.MapExperimentRun(record);
            var now = timeProvider.GetUtcNow();
            if (run.Status == ExperimentRunStatus.Prepared)
                run = ExperimentCompositeRuntimeStateMachine.Start(run, now);

            ExperimentRun updated;
            try
            {
                updated = run.Steps.Any(step => step.WorkflowRunId == child.ExecutionId)
                    ? ExperimentCompositeChildWorkflowCoordinator.ReconcileChild(
                        run,
                        child,
                        now,
                        metadata.UnknownResolutionReason)
                    : ExperimentCompositeChildWorkflowCoordinator.AttachChild(run, child, now);
            }
            catch (InvalidExperimentRunTransitionException exception)
            {
                throw new ExperimentSchedulingConflictException(
                    exception.Message,
                    ExperimentSchedulingIssueCodes.CompositeWorkflowNotSupported);
            }
            ExperimentSchedulingPersistence.ApplyExperimentRun(record, updated);
            database.ExperimentSchedulingAudits.Add(new ExperimentSchedulingAuditRecord
            {
                Id = Guid.NewGuid(),
                EventType = ChildReconciledEventType,
                Outcome = updated.Status.ToString(),
                RequestId = metadata.RequestId,
                RequestFingerprint = fingerprint,
                Actor = metadata.Actor,
                Reason = metadata.Reason,
                PlanId = updated.PlanId,
                PlanVersion = updated.PlanVersion,
                ExperimentJobId = updated.ExperimentJobId,
                DetailsJson = ExperimentSchedulingPersistence.Serialize(new Dictionary<string, string?>
                {
                    ["experimentRunId"] = updated.ExperimentRunId.ToString(),
                    ["childWorkflowRunId"] = child.ExecutionId.ToString(),
                    ["childRuntimeStatus"] = child.RuntimeStatus.ToString(),
                    ["deviceWritesAttempted"] = bool.FalseString
                }),
                ResultJson = ExperimentSchedulingPersistence.Serialize(updated),
                OccurredAtUtc = now.UtcDateTime
            });

            try
            {
                await database.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                database.ChangeTracker.Clear();
                var concurrent = await database.ExperimentSchedulingAudits.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.RequestId == metadata.RequestId, cancellationToken);
                if (concurrent is not null &&
                    concurrent.EventType == ChildReconciledEventType &&
                    string.Equals(concurrent.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    var replay = ExperimentSchedulingPersistence.Deserialize<ExperimentRun?>(
                        concurrent.ResultJson,
                        null);
                    return replay ?? throw new InvalidOperationException(
                        "Child-reconciliation replay audit is invalid.");
                }
                throw;
            }

            return updated;
        }
        finally
        {
            mutationGate.Exit();
        }
    }

    private static bool SameWorkflowSteps(
        IReadOnlyList<ExperimentPlanWorkflowStep> left,
        IReadOnlyList<ExperimentPlanWorkflowStep> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.StepId == pair.Second.StepId &&
            pair.First.Order == pair.Second.Order &&
            pair.First.WorkflowId == pair.Second.WorkflowId &&
            pair.First.WorkflowVersion == pair.Second.WorkflowVersion &&
            pair.First.EstimatedDurationMinutes == pair.Second.EstimatedDurationMinutes &&
            pair.First.Name == pair.Second.Name &&
            SameParameters(pair.First.Parameters, pair.Second.Parameters));

    private static bool SameParameters(
        IReadOnlyDictionary<string, string?> left,
        IReadOnlyDictionary<string, string?> right) =>
        left.Count == right.Count &&
        left.All(item => right.TryGetValue(item.Key, out var value) && value == item.Value);

    private static (Guid RequestId, Guid ExperimentJobId, string Actor, string Reason) NormalizeMetadata(
        PrepareExperimentRunRequest request)
    {
        if (request.RequestId == Guid.Empty) throw new ArgumentException("A preparation request id is required.", nameof(request));
        if (request.ExperimentJobId == Guid.Empty) throw new ArgumentException("An experiment job id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Actor)) throw new ArgumentException("A preparation actor is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ArgumentException("A preparation reason is required.", nameof(request));
        var actor = request.Actor.Trim();
        var reason = request.Reason.Trim();
        if (actor.Length > 256) throw new ArgumentException("The preparation actor cannot exceed 256 characters.", nameof(request));
        if (reason.Length > 2048) throw new ArgumentException("The preparation reason cannot exceed 2048 characters.", nameof(request));
        return (request.RequestId, request.ExperimentJobId, actor, reason);
    }

    private static string CreateFingerprint((Guid RequestId, Guid ExperimentJobId, string Actor, string Reason) metadata) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"prepare-composite\u001f{metadata.RequestId:N}\u001f{metadata.ExperimentJobId:N}\u001f{metadata.Actor}\u001f{metadata.Reason}")));

    private static Guid CreateStableRunId(Guid requestId, Guid jobId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"experiment-run\u001f{requestId:N}\u001f{jobId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private const string ChildReconciledEventType = "ExperimentCompositeChildReconciled";

    private static (Guid RequestId, Guid ChildWorkflowRunId, string Actor, string Reason, string? UnknownResolutionReason)
        NormalizeChildMetadata(ReconcileExperimentChildRequest request)
    {
        if (request.RequestId == Guid.Empty) throw new ArgumentException("A reconciliation request id is required.", nameof(request));
        if (request.ChildWorkflowRunId == Guid.Empty) throw new ArgumentException("A child workflow run id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Actor)) throw new ArgumentException("A reconciliation actor is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ArgumentException("A reconciliation reason is required.", nameof(request));
        var actor = request.Actor.Trim();
        var reason = request.Reason.Trim();
        var unknownReason = string.IsNullOrWhiteSpace(request.UnknownResolutionReason)
            ? null
            : request.UnknownResolutionReason.Trim();
        if (actor.Length > 256) throw new ArgumentException("The reconciliation actor cannot exceed 256 characters.", nameof(request));
        if (reason.Length > 2048) throw new ArgumentException("The reconciliation reason cannot exceed 2048 characters.", nameof(request));
        if (unknownReason?.Length > 2048) throw new ArgumentException("The unknown-resolution reason cannot exceed 2048 characters.", nameof(request));
        return (request.RequestId, request.ChildWorkflowRunId, actor, reason, unknownReason);
    }

    private static string CreateChildFingerprint(
        Guid experimentRunId,
        (Guid RequestId, Guid ChildWorkflowRunId, string Actor, string Reason, string? UnknownResolutionReason) metadata) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"reconcile-composite-child\u001f{experimentRunId:N}\u001f{metadata.RequestId:N}\u001f{metadata.ChildWorkflowRunId:N}\u001f{metadata.Actor}\u001f{metadata.Reason}\u001f{metadata.UnknownResolutionReason}")));
}
