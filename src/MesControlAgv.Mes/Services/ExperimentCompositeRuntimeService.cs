using System.Security.Cryptography;
using System.Text;
using MesControlAgv.Application;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Experiments;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Persists the outer snapshot for a composed experiment run. Preparation stays
/// device-free; the explicit simulator coordinator below advances one pinned
/// child workflow at a time only after durable admission/evidence is available.
/// </summary>
public sealed class ExperimentCompositeRuntimeService(
    MesDbContext database,
    ExperimentSchedulingMutationGate mutationGate,
    TimeProvider timeProvider,
    IWorkflowApplicationService workflows,
    IHostEnvironment environment) : IExperimentCompositeRuntimeService
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

    /// <summary>
    /// Advances simulator-only composite runs using existing child workflow
    /// evidence. Child admission uses a deterministic request id, so a restart
    /// after the child was accepted but before the outer snapshot was saved
    /// replays the admission instead of creating a second child. Unknown or
    /// unreadable outcomes are persisted as Unknown and are never retried.
    /// </summary>
    public async Task<ExperimentCompositeRuntimeProcessSummary> ProcessPendingAsync(
        CancellationToken cancellationToken)
    {
        // This coordinator creates child workflow executions without the full
        // runtime lease/admission boundary. Keep it fail-closed until the
        // simulator-only path is explicitly selected; production environments
        // must continue using the normal single-workflow admission gate.
        if (!environment.IsEnvironment("Testing") && !environment.IsEnvironment("FieldSimulation"))
            return new ExperimentCompositeRuntimeProcessSummary(0, 0, 0);

        var runIds = await database.ExperimentRuns.AsNoTracking()
            .Where(item => item.Status == ExperimentRunStatus.Prepared.ToString() ||
                           item.Status == ExperimentRunStatus.Running.ToString())
            .OrderBy(item => item.UpdatedAtUtc)
            .ThenBy(item => item.ExperimentRunId)
            .Select(item => item.ExperimentRunId)
            .ToListAsync(cancellationToken);

        var changed = 0;
        var unknown = 0;
        foreach (var runId in runIds)
        {
            var outcome = await ProcessOneAsync(runId, cancellationToken);
            if (outcome.Changed) changed++;
            if (outcome.Unknown) unknown++;
        }

        return new ExperimentCompositeRuntimeProcessSummary(
            runIds.Count,
            changed,
            unknown);
    }

    private async Task<ExperimentCompositeRuntimeProcessOutcome> ProcessOneAsync(
        Guid experimentRunId,
        CancellationToken cancellationToken)
    {
        await mutationGate.EnterAsync(cancellationToken);
        try
        {
            var record = await database.ExperimentRuns.SingleOrDefaultAsync(
                item => item.ExperimentRunId == experimentRunId,
                cancellationToken);
            if (record is null) return ExperimentCompositeRuntimeProcessOutcome.None;

            var run = ExperimentSchedulingPersistence.MapExperimentRun(record);
            if (run.IsTerminal || run.Status == ExperimentRunStatus.Unknown)
                return ExperimentCompositeRuntimeProcessOutcome.None;

            var now = timeProvider.GetUtcNow();
            if (run.Status == ExperimentRunStatus.Prepared)
            {
                try
                {
                    run = ExperimentCompositeRuntimeStateMachine.Start(run, now);
                }
                catch (InvalidExperimentRunTransitionException exception)
                {
                    throw new ExperimentSchedulingConflictException(
                        exception.Message,
                        ExperimentSchedulingIssueCodes.CompositeWorkflowNotSupported);
                }
                await PersistTransitionAsync(
                    record,
                    run,
                    "ExperimentCompositeRunStarted",
                    childWorkflowRunId: null,
                    "Start the next simulator composite step",
                    cancellationToken);
            }

            var current = run.Steps.SingleOrDefault(step => step.StepRunId == run.CurrentStepRunId);
            if (current is null) return ExperimentCompositeRuntimeProcessOutcome.None;

            if (current.Status == ExperimentStepRunStatus.Ready)
            {
                var job = await database.ExperimentJobs.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.JobId == run.ExperimentJobId, cancellationToken);
                if (job is null)
                {
                    var failed = ExperimentCompositeRuntimeStateMachine.FailCurrentStepBeforeChild(
                        run,
                        "The experiment job for the composite run was not found.",
                        now);
                    await PersistTransitionAsync(
                        record,
                        failed,
                        "ExperimentCompositeChildAdmissionFailed",
                        null,
                        failed.LastError!,
                        cancellationToken);
                    return new ExperimentCompositeRuntimeProcessOutcome(true, false);
                }

                WorkflowExecutionResult childResult;
                try
                {
                    var parameters = new Dictionary<string, string?>(
                        ExperimentSchedulingPersistence.Deserialize(
                            job.ParametersJson,
                            new Dictionary<string, string?>()),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var parameter in current.Parameters ??
                             new Dictionary<string, string?>())
                    {
                        parameters[parameter.Key] = parameter.Value;
                    }

                    var childRequest = new WorkflowExecutionRequest
                    {
                        WorkflowId = current.WorkflowId,
                        Version = current.WorkflowVersion,
                        Parameters = parameters,
                        RequestedBy = "experiment-composite-worker",
                        CorrelationId = $"experiment-run:{run.ExperimentRunId:N}:step:{current.StepId:N}",
                        RequestId = CreateStableChildRequestId(run.ExperimentRunId, current.StepId),
                        RequestedAt = now,
                        DryRun = false
                    };
                    childResult = await workflows.ExecuteAsync(childRequest, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var unknown = ExperimentCompositeRuntimeStateMachine.MarkCurrentStepUnknown(
                        run,
                        $"Child workflow admission outcome could not be proven: {exception.Message}",
                        now);
                    await PersistTransitionAsync(
                        record,
                        unknown,
                        "ExperimentCompositeChildAdmissionUnknown",
                        null,
                        unknown.LastError!,
                        cancellationToken);
                    return new ExperimentCompositeRuntimeProcessOutcome(true, true);
                }

                if (childResult.IsRejected)
                {
                    var failed = ExperimentCompositeRuntimeStateMachine.FailCurrentStepBeforeChild(
                        run,
                        childResult.RejectionReason ?? "Child workflow admission was rejected.",
                        now);
                    await PersistTransitionAsync(
                        record,
                        failed,
                        "ExperimentCompositeChildAdmissionFailed",
                        childResult.ExecutionId == Guid.Empty ? null : childResult.ExecutionId,
                        failed.LastError!,
                        cancellationToken);
                    return new ExperimentCompositeRuntimeProcessOutcome(true, false);
                }

                var child = await workflows.GetExecutionAsync(childResult.ExecutionId, cancellationToken);
                if (child is null)
                {
                    var unknown = ExperimentCompositeRuntimeStateMachine.MarkCurrentStepUnknown(
                        run,
                        "Child workflow was accepted but its durable snapshot could not be read.",
                        now);
                    await PersistTransitionAsync(
                        record,
                        unknown,
                        "ExperimentCompositeChildAdmissionUnknown",
                        childResult.ExecutionId,
                        unknown.LastError!,
                        cancellationToken);
                    return new ExperimentCompositeRuntimeProcessOutcome(true, true);
                }

                ExperimentRun attached;
                try
                {
                    attached = ExperimentCompositeChildWorkflowCoordinator.AttachChild(run, child, now);
                }
                catch (InvalidExperimentRunTransitionException exception)
                {
                    var unknown = ExperimentCompositeRuntimeStateMachine.MarkCurrentStepUnknown(
                        run,
                        $"Child workflow identity could not be reconciled: {exception.Message}",
                        now);
                    await PersistTransitionAsync(
                        record,
                        unknown,
                        "ExperimentCompositeChildReconciliationUnknown",
                        child.ExecutionId,
                        unknown.LastError!,
                        cancellationToken);
                    return new ExperimentCompositeRuntimeProcessOutcome(true, true);
                }

                await PersistTransitionAsync(
                    record,
                    attached,
                    "ExperimentCompositeChildAttached",
                    child.ExecutionId,
                    "Bind the deterministic child workflow execution to the current step",
                    cancellationToken);
                return new ExperimentCompositeRuntimeProcessOutcome(true, false);
            }

            if (current.Status != ExperimentStepRunStatus.Running || current.WorkflowRunId is null)
                return ExperimentCompositeRuntimeProcessOutcome.None;

            WorkflowExecutionSnapshot? childSnapshot;
            try
            {
                childSnapshot = await workflows.GetExecutionAsync(
                    current.WorkflowRunId.Value,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                childSnapshot = null;
                var unknown = ExperimentCompositeRuntimeStateMachine.MarkCurrentStepUnknown(
                    run,
                    $"Bound child workflow snapshot could not be read: {exception.Message}",
                    now);
                await PersistTransitionAsync(
                    record,
                    unknown,
                    "ExperimentCompositeChildSnapshotUnknown",
                    current.WorkflowRunId,
                    unknown.LastError!,
                    cancellationToken);
                return new ExperimentCompositeRuntimeProcessOutcome(true, true);
            }

            if (childSnapshot is null)
            {
                var unknown = ExperimentCompositeRuntimeStateMachine.MarkCurrentStepUnknown(
                    run,
                    "Bound child workflow snapshot was not found after restart.",
                    now);
                await PersistTransitionAsync(
                    record,
                    unknown,
                    "ExperimentCompositeChildSnapshotUnknown",
                    current.WorkflowRunId,
                    unknown.LastError!,
                    cancellationToken);
                return new ExperimentCompositeRuntimeProcessOutcome(true, true);
            }

            if (childSnapshot.RuntimeStatus is WorkflowRuntimeStatus.Prepared or
                WorkflowRuntimeStatus.Running or
                WorkflowRuntimeStatus.Paused)
                return ExperimentCompositeRuntimeProcessOutcome.None;

            try
            {
                var reconciled = ExperimentCompositeChildWorkflowCoordinator.ReconcileChild(
                    run,
                    childSnapshot,
                    now);
                await PersistTransitionAsync(
                    record,
                    reconciled,
                    "ExperimentCompositeChildReconciled",
                    childSnapshot.ExecutionId,
                    "Persist observed child workflow runtime evidence",
                    cancellationToken);
                return new ExperimentCompositeRuntimeProcessOutcome(
                    true,
                    reconciled.Status == ExperimentRunStatus.Unknown);
            }
            catch (InvalidExperimentRunTransitionException exception)
            {
                var unknown = ExperimentCompositeRuntimeStateMachine.MarkCurrentStepUnknown(
                    run,
                    $"Child workflow evidence could not be reconciled: {exception.Message}",
                    now);
                await PersistTransitionAsync(
                    record,
                    unknown,
                    "ExperimentCompositeChildReconciliationUnknown",
                    childSnapshot.ExecutionId,
                    unknown.LastError!,
                    cancellationToken);
                return new ExperimentCompositeRuntimeProcessOutcome(true, true);
            }
        }
        finally
        {
            mutationGate.Exit();
        }
    }

    private async Task PersistTransitionAsync(
        ExperimentRunRecord record,
        ExperimentRun updated,
        string eventType,
        Guid? childWorkflowRunId,
        string reason,
        CancellationToken cancellationToken)
    {
        ExperimentSchedulingPersistence.ApplyExperimentRun(record, updated);
        var requestId = CreateStableTransitionRequestId(
            eventType,
            updated.ExperimentRunId,
            updated.CurrentStepRunId ?? Guid.Empty,
            childWorkflowRunId ?? Guid.Empty);
        if (!await database.ExperimentSchedulingAudits.AsNoTracking()
                .AnyAsync(item => item.RequestId == requestId, cancellationToken))
        {
            database.ExperimentSchedulingAudits.Add(new ExperimentSchedulingAuditRecord
            {
                Id = Guid.NewGuid(),
                EventType = eventType,
                Outcome = updated.Status.ToString(),
                RequestId = requestId,
                RequestFingerprint = requestId.ToString("N"),
                Actor = "experiment-composite-worker",
                Reason = reason,
                PlanId = updated.PlanId,
                PlanVersion = updated.PlanVersion,
                ExperimentJobId = updated.ExperimentJobId,
                DetailsJson = ExperimentSchedulingPersistence.Serialize(new Dictionary<string, string?>
                {
                    ["experimentRunId"] = updated.ExperimentRunId.ToString(),
                    ["currentStepRunId"] = updated.CurrentStepRunId?.ToString(),
                    ["childWorkflowRunId"] = childWorkflowRunId?.ToString(),
                    ["deviceWritesAttempted"] = bool.FalseString,
                    ["automaticRetry"] = bool.FalseString
                }),
                ResultJson = ExperimentSchedulingPersistence.Serialize(updated),
                OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime
            });
        }
        await database.SaveChangesAsync(cancellationToken);
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

    private static Guid CreateStableChildRequestId(Guid experimentRunId, Guid stepId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"experiment-child-request\u001f{experimentRunId:N}\u001f{stepId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static Guid CreateStableTransitionRequestId(
        string eventType,
        Guid experimentRunId,
        Guid stepRunId,
        Guid childWorkflowRunId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"experiment-transition\u001f{eventType}\u001f{experimentRunId:N}\u001f{stepRunId:N}\u001f{childWorkflowRunId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private const string ChildReconciledEventType = "ExperimentCompositeChildReconciled";

    private sealed record ExperimentCompositeRuntimeProcessOutcome(bool Changed, bool Unknown)
    {
        public static ExperimentCompositeRuntimeProcessOutcome None { get; } = new(false, false);
    }

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
