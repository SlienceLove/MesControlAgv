using System.Security.Cryptography;
using System.Text;
using MesControlAgv.Application;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Owns experiment planning state only. It cannot acquire runtime leases,
/// create workflow runs, advance nodes, or contact device adapters.
/// </summary>
public sealed class ExperimentSchedulingCommandService(
    MesDbContext database,
    TimeProvider timeProvider,
    ProfileConfiguration profile,
    ExperimentResourceCatalog resourceCatalog,
    ExperimentSchedulingMutationGate mutationGate) : IExperimentSchedulingCommandService
{
    public const string PlanValidatorVersion = "1.0";

    public Task<ExperimentPlan> CreatePlanDraftAsync(
        SaveExperimentPlanDraftRequest request,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(
            () => CreatePlanDraftCoreAsync(request, cancellationToken),
            cancellationToken);

    private async Task<ExperimentPlan> CreatePlanDraftCoreAsync(
        SaveExperimentPlanDraftRequest request,
        CancellationToken cancellationToken)
    {
        var metadata = NormalizeMetadata(request.RequestId, request.Actor, request.Reason);
        var draft = NormalizeDraft(request.Draft);
        var fingerprint = CreateFingerprint(
            "CreatePlanDraft",
            metadata,
            CanonicalizeDraft(draft));
        var replay = await TryReplayAsync<ExperimentPlan>(
            metadata.RequestId,
            "ExperimentPlanDraftCreated",
            fingerprint,
            cancellationToken);
        if (replay is not null) return replay;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var record = new ExperimentPlanRecord
        {
            PlanId = Guid.NewGuid(),
            Version = 1,
            Status = ExperimentPlanStatus.Draft.ToString(),
            CreatedBy = metadata.Actor,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        ApplyDraft(record, draft);
        database.ExperimentPlans.Add(record);

        var result = ExperimentSchedulingPersistence.MapPlan(record);
        AddAudit(
            metadata,
            fingerprint,
            "ExperimentPlanDraftCreated",
            result.Status.ToString(),
            result,
            planId: record.PlanId,
            planVersion: record.Version,
            details: new Dictionary<string, string?>
            {
                ["workflowId"] = record.WorkflowId.ToString(),
                ["workflowVersion"] = record.WorkflowVersion.ToString()
            });
        await database.SaveChangesAsync(cancellationToken);
        return result;
    }

    public Task<ExperimentPlan> UpdatePlanDraftAsync(
        Guid planId,
        int version,
        SaveExperimentPlanDraftRequest request,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(
            () => UpdatePlanDraftCoreAsync(planId, version, request, cancellationToken),
            cancellationToken);

    private async Task<ExperimentPlan> UpdatePlanDraftCoreAsync(
        Guid planId,
        int version,
        SaveExperimentPlanDraftRequest request,
        CancellationToken cancellationToken)
    {
        RequireIdentity(planId, nameof(planId));
        RequirePositive(version, nameof(version));
        var metadata = NormalizeMetadata(request.RequestId, request.Actor, request.Reason);
        var draft = NormalizeDraft(request.Draft);
        var fingerprint = CreateFingerprint(
            "UpdatePlanDraft",
            metadata,
            new { planId, version, Draft = CanonicalizeDraft(draft) });
        var replay = await TryReplayAsync<ExperimentPlan>(
            metadata.RequestId,
            "ExperimentPlanDraftUpdated",
            fingerprint,
            cancellationToken);
        if (replay is not null) return replay;

        var record = await FindPlanAsync(planId, version, cancellationToken);
        var status = ExperimentSchedulingPersistence.ParseStatus<ExperimentPlanStatus>(record.Status);
        if (status is not ExperimentPlanStatus.Draft and not ExperimentPlanStatus.Validated)
        {
            throw new ExperimentSchedulingConflictException(
                $"A plan version in '{status}' state cannot be edited. Create a new draft version instead.");
        }

        ApplyDraft(record, draft);
        record.Status = ExperimentPlanStatus.Draft.ToString();
        record.ValidationJson = null;
        record.ValidatedBy = null;
        record.ValidatedAtUtc = null;
        record.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        var result = ExperimentSchedulingPersistence.MapPlan(record);
        AddAudit(
            metadata,
            fingerprint,
            "ExperimentPlanDraftUpdated",
            result.Status.ToString(),
            result,
            planId: planId,
            planVersion: version,
            details: new Dictionary<string, string?>
            {
                ["workflowId"] = record.WorkflowId.ToString(),
                ["workflowVersion"] = record.WorkflowVersion.ToString(),
                ["validationInvalidated"] = "true"
            });
        await database.SaveChangesAsync(cancellationToken);
        return result;
    }

    public Task<ExperimentPlan> ValidatePlanAsync(
        Guid planId,
        int version,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(
            () => ValidatePlanCoreAsync(planId, version, request, cancellationToken),
            cancellationToken);

    private async Task<ExperimentPlan> ValidatePlanCoreAsync(
        Guid planId,
        int version,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken)
    {
        RequireIdentity(planId, nameof(planId));
        RequirePositive(version, nameof(version));
        var metadata = NormalizeMetadata(request.RequestId, request.Actor, request.Reason);
        var fingerprint = CreateFingerprint("ValidatePlan", metadata, new { planId, version });
        var replay = await TryReplayAsync<ExperimentPlan>(
            metadata.RequestId,
            "ExperimentPlanValidated",
            fingerprint,
            cancellationToken);
        if (replay is not null) return replay;

        var record = await FindPlanAsync(planId, version, cancellationToken);
        var status = ExperimentSchedulingPersistence.ParseStatus<ExperimentPlanStatus>(record.Status);
        if (status is not ExperimentPlanStatus.Draft and not ExperimentPlanStatus.Validated)
        {
            throw new ExperimentSchedulingConflictException(
                $"A plan version in '{status}' state cannot be validated.");
        }

        var validation = await ValidatePlanRecordAsync(record, metadata.Actor, cancellationToken);
        record.Status = (validation.IsValid
            ? ExperimentPlanStatus.Validated
            : ExperimentPlanStatus.Draft).ToString();
        record.ValidationJson = ExperimentSchedulingPersistence.Serialize(validation);
        record.ValidatedBy = metadata.Actor;
        record.ValidatedAtUtc = validation.ValidatedAt.UtcDateTime;
        record.UpdatedAtUtc = validation.ValidatedAt.UtcDateTime;

        var result = ExperimentSchedulingPersistence.MapPlan(record);
        AddAudit(
            metadata,
            fingerprint,
            "ExperimentPlanValidated",
            validation.IsValid ? "Valid" : "Invalid",
            result,
            code: validation.IsValid ? null : "EXP-PLAN-INVALID",
            planId: planId,
            planVersion: version,
            details: new Dictionary<string, string?>
            {
                ["issueCount"] = validation.Issues.Count.ToString(),
                ["validatorVersion"] = validation.ValidatorVersion
            });
        await database.SaveChangesAsync(cancellationToken);
        return result;
    }

    public Task<ExperimentPlan> PublishPlanAsync(
        Guid planId,
        int version,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(
            () => PublishPlanCoreAsync(planId, version, request, cancellationToken),
            cancellationToken);

    private async Task<ExperimentPlan> PublishPlanCoreAsync(
        Guid planId,
        int version,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken)
    {
        RequireIdentity(planId, nameof(planId));
        RequirePositive(version, nameof(version));
        var metadata = NormalizeMetadata(request.RequestId, request.Actor, request.Reason);
        var fingerprint = CreateFingerprint("PublishPlan", metadata, new { planId, version });
        var replay = await TryReplayAsync<ExperimentPlan>(
            metadata.RequestId,
            "ExperimentPlanPublished",
            fingerprint,
            cancellationToken);
        if (replay is not null) return replay;

        var record = await FindPlanAsync(planId, version, cancellationToken);
        var status = ExperimentSchedulingPersistence.ParseStatus<ExperimentPlanStatus>(record.Status);
        if (status != ExperimentPlanStatus.Validated)
        {
            throw new ExperimentSchedulingConflictException(
                $"Only a validated plan can be published; current state is '{status}'.");
        }

        var validation = await ValidatePlanRecordAsync(record, metadata.Actor, cancellationToken);
        if (!validation.IsValid)
        {
            throw new ExperimentPlanValidationException(
                "The experiment plan no longer passes publication validation.",
                validation);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        record.Status = ExperimentPlanStatus.Published.ToString();
        record.ValidationJson = ExperimentSchedulingPersistence.Serialize(validation);
        record.ValidatedBy = metadata.Actor;
        record.ValidatedAtUtc = validation.ValidatedAt.UtcDateTime;
        record.PublishedBy = metadata.Actor;
        record.PublishedAtUtc = now;
        record.UpdatedAtUtc = now;

        var result = ExperimentSchedulingPersistence.MapPlan(record);
        AddAudit(
            metadata,
            fingerprint,
            "ExperimentPlanPublished",
            result.Status.ToString(),
            result,
            planId: planId,
            planVersion: version,
            details: new Dictionary<string, string?>
            {
                ["workflowId"] = record.WorkflowId.ToString(),
                ["workflowVersion"] = record.WorkflowVersion.ToString(),
                ["profileProductId"] = record.ProfileProductId,
                ["profileVersion"] = record.ProfileVersion
            });
        await database.SaveChangesAsync(cancellationToken);
        return result;
    }

    public Task<ExperimentPlan> CreateNextPlanDraftAsync(
        Guid planId,
        int sourceVersion,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(
            () => CreateNextPlanDraftCoreAsync(planId, sourceVersion, request, cancellationToken),
            cancellationToken);

    private async Task<ExperimentPlan> CreateNextPlanDraftCoreAsync(
        Guid planId,
        int sourceVersion,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken)
    {
        RequireIdentity(planId, nameof(planId));
        RequirePositive(sourceVersion, nameof(sourceVersion));
        var metadata = NormalizeMetadata(request.RequestId, request.Actor, request.Reason);
        var fingerprint = CreateFingerprint(
            "CreateNextPlanDraft",
            metadata,
            new { planId, sourceVersion });
        var replay = await TryReplayAsync<ExperimentPlan>(
            metadata.RequestId,
            "ExperimentPlanDraftVersionCreated",
            fingerprint,
            cancellationToken);
        if (replay is not null) return replay;

        var source = await FindPlanAsync(planId, sourceVersion, cancellationToken);
        var sourceStatus = ExperimentSchedulingPersistence.ParseStatus<ExperimentPlanStatus>(source.Status);
        if (sourceStatus != ExperimentPlanStatus.Published)
        {
            throw new ExperimentSchedulingConflictException(
                $"Only a published plan can be copied to a new draft; current state is '{sourceStatus}'.");
        }

        var nextVersion = (await database.ExperimentPlans
            .Where(plan => plan.PlanId == planId)
            .MaxAsync(plan => (int?)plan.Version, cancellationToken) ?? 0) + 1;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var record = new ExperimentPlanRecord
        {
            PlanId = planId,
            Version = nextVersion,
            Name = source.Name,
            Description = source.Description,
            WorkflowId = source.WorkflowId,
            WorkflowVersion = source.WorkflowVersion,
            WorkflowStepsJson = source.WorkflowStepsJson,
            Status = ExperimentPlanStatus.Draft.ToString(),
            MaterialRequirementsJson = source.MaterialRequirementsJson,
            DefaultParametersJson = source.DefaultParametersJson,
            ResourceRequirementsJson = source.ResourceRequirementsJson,
            ProfileProductId = source.ProfileProductId,
            ProfileVersion = source.ProfileVersion,
            LayoutId = source.LayoutId,
            CreatedBy = metadata.Actor,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        database.ExperimentPlans.Add(record);

        var result = ExperimentSchedulingPersistence.MapPlan(record);
        AddAudit(
            metadata,
            fingerprint,
            "ExperimentPlanDraftVersionCreated",
            result.Status.ToString(),
            result,
            planId: planId,
            planVersion: nextVersion,
            details: new Dictionary<string, string?>
            {
                ["sourceVersion"] = sourceVersion.ToString(),
                ["newVersion"] = nextVersion.ToString()
            });
        await database.SaveChangesAsync(cancellationToken);
        return result;
    }

    public Task<ExperimentJob> CreateJobAsync(
        CreateExperimentJobRequest request,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(
            () => CreateJobCoreAsync(request, cancellationToken),
            cancellationToken);

    private async Task<ExperimentJob> CreateJobCoreAsync(
        CreateExperimentJobRequest request,
        CancellationToken cancellationToken)
    {
        var metadata = NormalizeMetadata(request.RequestId, request.Actor, request.Reason);
        RequireIdentity(request.PlanId, nameof(request.PlanId));
        RequirePositive(request.PlanVersion, nameof(request.PlanVersion));
        var sampleBatchId = RequireText(request.SampleBatchId, nameof(request.SampleBatchId));
        var sampleId = NormalizeOptionalText(request.SampleId);
        var overrides = NormalizeParameters(request.Parameters, nameof(request.Parameters));
        var fingerprint = CreateFingerprint(
            "CreateExperimentJob",
            metadata,
            new
            {
                request.PlanId,
                request.PlanVersion,
                SampleBatchId = sampleBatchId,
                SampleId = sampleId,
                Parameters = overrides.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray()
            });
        var replay = await TryReplayAsync<ExperimentJob>(
            metadata.RequestId,
            "ExperimentJobCreated",
            fingerprint,
            cancellationToken);
        if (replay is not null) return replay;

        var plan = await FindPlanAsync(request.PlanId, request.PlanVersion, cancellationToken);
        var planStatus = ExperimentSchedulingPersistence.ParseStatus<ExperimentPlanStatus>(plan.Status);
        if (planStatus != ExperimentPlanStatus.Published)
        {
            throw new ExperimentSchedulingConflictException(
                $"Experiment jobs require a published plan; current plan state is '{planStatus}'.");
        }
        var workflowSteps = ExperimentSchedulingPersistence.NormalizeWorkflowSteps(
            ExperimentSchedulingPersistence.Deserialize(
                plan.WorkflowStepsJson,
                Array.Empty<ExperimentPlanWorkflowStep>()),
            plan.WorkflowId,
            plan.WorkflowVersion,
            plan.PlanId);
        await EnsureWorkflowStepsPublishedAsync(workflowSteps, cancellationToken);

        var parameters = ExperimentSchedulingPersistence.Deserialize(
            plan.DefaultParametersJson,
            new Dictionary<string, string?>());
        var mergedParameters = new Dictionary<string, string?>(parameters, StringComparer.OrdinalIgnoreCase);
        foreach (var item in overrides) mergedParameters[item.Key] = item.Value;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var record = new ExperimentJobRecord
        {
            JobId = Guid.NewGuid(),
            PlanId = plan.PlanId,
            PlanVersion = plan.Version,
            WorkflowId = plan.WorkflowId,
            WorkflowVersion = plan.WorkflowVersion,
            WorkflowStepsJson = ExperimentSchedulingPersistence.Serialize(workflowSteps),
            SampleBatchId = sampleBatchId,
            SampleId = sampleId,
            ParametersJson = ExperimentSchedulingPersistence.Serialize(mergedParameters),
            Status = ExperimentJobStatus.Ready.ToString(),
            CreatedBy = metadata.Actor,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        database.ExperimentJobs.Add(record);

        var result = ExperimentSchedulingPersistence.MapJob(record);
        AddAudit(
            metadata,
            fingerprint,
            "ExperimentJobCreated",
            result.Status.ToString(),
            result,
            planId: plan.PlanId,
            planVersion: plan.Version,
            experimentJobId: record.JobId,
            details: new Dictionary<string, string?>
            {
                ["workflowId"] = plan.WorkflowId.ToString(),
                ["workflowVersion"] = plan.WorkflowVersion.ToString(),
                ["sampleBatchId"] = sampleBatchId
            });
        await database.SaveChangesAsync(cancellationToken);
        return result;
    }

    public Task<ScheduleEntry> ScheduleJobAsync(
        Guid experimentJobId,
        ScheduleExperimentJobRequest request,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(
            () => ScheduleJobCoreAsync(experimentJobId, request, cancellationToken),
            cancellationToken);

    private async Task<ScheduleEntry> ScheduleJobCoreAsync(
        Guid experimentJobId,
        ScheduleExperimentJobRequest request,
        CancellationToken cancellationToken)
    {
        RequireIdentity(experimentJobId, nameof(experimentJobId));
        var metadata = NormalizeMetadata(request.RequestId, request.Actor, request.Reason);
        if (request.PlannedEnd <= request.PlannedStart)
            throw new ArgumentException("Planned end must be later than planned start.", nameof(request));
        if (request.Priority is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(request), "Priority must be between 0 and 100.");
        var resources = NormalizeResources(request.Resources);
        var fingerprint = CreateFingerprint(
            "ScheduleExperimentJob",
            metadata,
            new
            {
                experimentJobId,
                PlannedStart = request.PlannedStart.ToUniversalTime(),
                PlannedEnd = request.PlannedEnd.ToUniversalTime(),
                request.Priority,
                Resources = resources
                    .Select(resource => ExperimentResourceKeys.Create(resource.ResourceType, resource.ResourceId))
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray()
            });
        var replay = await TryReplayAsync<ScheduleEntry>(
            metadata.RequestId,
            "ExperimentJobScheduled",
            fingerprint,
            cancellationToken);
        if (replay is not null) return replay;

        var job = await FindJobAsync(experimentJobId, cancellationToken);
        var jobStatus = ExperimentSchedulingPersistence.ParseStatus<ExperimentJobStatus>(job.Status);
        if (jobStatus is not ExperimentJobStatus.Ready and
            not ExperimentJobStatus.Scheduled and
            not ExperimentJobStatus.Blocked)
        {
            throw new ExperimentSchedulingConflictException(
                $"A job in '{jobStatus}' state cannot be manually scheduled.");
        }
        var plan = await FindPlanAsync(job.PlanId, job.PlanVersion, cancellationToken);

        var entry = await database.ScheduleEntries
            .SingleOrDefaultAsync(item => item.ExperimentJobId == experimentJobId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (entry is null)
        {
            entry = new ScheduleEntryRecord
            {
                ScheduleEntryId = Guid.NewGuid(),
                ExperimentJobId = experimentJobId,
                CreatedBy = metadata.Actor,
                CreatedAtUtc = now
            };
            database.ScheduleEntries.Add(entry);
        }

        var priorReservations = await database.ResourceReservations
            .Where(reservation =>
                reservation.ScheduleEntryId == entry.ScheduleEntryId &&
                reservation.Status == ResourceReservationStatus.Planned.ToString())
            .ToListAsync(cancellationToken);
        foreach (var reservation in priorReservations)
        {
            reservation.Status = ResourceReservationStatus.Released.ToString();
            reservation.UpdatedAtUtc = now;
        }

        var blockingReasons = await EvaluateScheduleAsync(
            plan,
            entry.ScheduleEntryId,
            resources,
            request.PlannedStart.UtcDateTime,
            request.PlannedEnd.UtcDateTime,
            cancellationToken);
        var scheduleStatus = blockingReasons.Count == 0
            ? ScheduleEntryStatus.Scheduled
            : ScheduleEntryStatus.Blocked;
        var jobResultStatus = blockingReasons.Count == 0
            ? ExperimentJobStatus.Scheduled
            : ExperimentJobStatus.Blocked;

        entry.PlannedStartUtc = request.PlannedStart.UtcDateTime;
        entry.PlannedEndUtc = request.PlannedEnd.UtcDateTime;
        entry.Priority = request.Priority;
        entry.Status = scheduleStatus.ToString();
        entry.RequestedResourcesJson = ExperimentSchedulingPersistence.Serialize(resources);
        entry.BlockingReasonsJson = ExperimentSchedulingPersistence.Serialize(blockingReasons);
        entry.UpdatedAtUtc = now;
        job.Status = jobResultStatus.ToString();
        job.UpdatedAtUtc = now;

        var newReservations = new List<ResourceReservationRecord>();
        if (blockingReasons.Count == 0)
        {
            foreach (var resource in resources)
            {
                var reservation = new ResourceReservationRecord
                {
                    ReservationId = Guid.NewGuid(),
                    ScheduleEntryId = entry.ScheduleEntryId,
                    ResourceType = resource.ResourceType,
                    ResourceId = resource.ResourceId,
                    ResourceKey = ExperimentResourceKeys.Create(resource.ResourceType, resource.ResourceId),
                    StartsAtUtc = request.PlannedStart.UtcDateTime,
                    EndsAtUtc = request.PlannedEnd.UtcDateTime,
                    Status = ResourceReservationStatus.Planned.ToString(),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                database.ResourceReservations.Add(reservation);
                newReservations.Add(reservation);
            }
        }

        var result = ExperimentSchedulingPersistence.MapScheduleEntry(
            entry,
            newReservations.Select(ExperimentSchedulingPersistence.MapReservation).ToArray());
        AddAudit(
            metadata,
            fingerprint,
            "ExperimentJobScheduled",
            result.Status.ToString(),
            result,
            code: blockingReasons.Count == 0 ? null : "EXP-SCHEDULE-BLOCKED",
            planId: job.PlanId,
            planVersion: job.PlanVersion,
            experimentJobId: job.JobId,
            scheduleEntryId: entry.ScheduleEntryId,
            details: new Dictionary<string, string?>
            {
                ["priority"] = request.Priority.ToString(),
                ["resourceCount"] = resources.Count.ToString(),
                ["resourceKeys"] = FormatResourceKeys(resources),
                ["blockingReasonCount"] = blockingReasons.Count.ToString(),
                ["previousReservationCount"] = priorReservations.Count.ToString(),
                ["previousResourceKeys"] = FormatResourceKeys(priorReservations)
            });
        await database.SaveChangesAsync(cancellationToken);
        return result;
    }

    public Task<ExperimentJob> UnscheduleJobAsync(
        Guid experimentJobId,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(
            () => UnscheduleJobCoreAsync(experimentJobId, request, cancellationToken),
            cancellationToken);

    private async Task<ExperimentJob> UnscheduleJobCoreAsync(
        Guid experimentJobId,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken)
    {
        RequireIdentity(experimentJobId, nameof(experimentJobId));
        var metadata = NormalizeMetadata(request.RequestId, request.Actor, request.Reason);
        var fingerprint = CreateFingerprint("UnscheduleExperimentJob", metadata, new { experimentJobId });
        var replay = await TryReplayAsync<ExperimentJob>(
            metadata.RequestId,
            "ExperimentJobUnscheduled",
            fingerprint,
            cancellationToken);
        if (replay is not null) return replay;

        var job = await FindJobAsync(experimentJobId, cancellationToken);
        var status = ExperimentSchedulingPersistence.ParseStatus<ExperimentJobStatus>(job.Status);
        if (status is not ExperimentJobStatus.Scheduled and not ExperimentJobStatus.Blocked)
        {
            throw new ExperimentSchedulingConflictException(
                $"A job in '{status}' state cannot be removed from the schedule.");
        }
        var entry = await database.ScheduleEntries.SingleOrDefaultAsync(
            item => item.ExperimentJobId == experimentJobId,
            cancellationToken) ?? throw new ExperimentSchedulingConflictException(
            "The scheduled job has no schedule entry.");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var released = await ReleaseReservationsAsync(entry.ScheduleEntryId, now, cancellationToken);
        entry.Status = ScheduleEntryStatus.Draft.ToString();
        entry.RequestedResourcesJson = "[]";
        entry.BlockingReasonsJson = "[]";
        entry.UpdatedAtUtc = now;
        job.Status = ExperimentJobStatus.Ready.ToString();
        job.UpdatedAtUtc = now;

        var result = ExperimentSchedulingPersistence.MapJob(job);
        AddAudit(
            metadata,
            fingerprint,
            "ExperimentJobUnscheduled",
            result.Status.ToString(),
            result,
            planId: job.PlanId,
            planVersion: job.PlanVersion,
            experimentJobId: job.JobId,
            scheduleEntryId: entry.ScheduleEntryId,
            details: new Dictionary<string, string?>
            {
                ["releasedReservationCount"] = released.Count.ToString(),
                ["releasedResourceKeys"] = FormatResourceKeys(released)
            });
        await database.SaveChangesAsync(cancellationToken);
        return result;
    }

    public Task<ExperimentJob> CancelJobAsync(
        Guid experimentJobId,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(
            () => CancelJobCoreAsync(experimentJobId, request, cancellationToken),
            cancellationToken);

    private async Task<ExperimentJob> CancelJobCoreAsync(
        Guid experimentJobId,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken)
    {
        RequireIdentity(experimentJobId, nameof(experimentJobId));
        var metadata = NormalizeMetadata(request.RequestId, request.Actor, request.Reason);
        var fingerprint = CreateFingerprint("CancelExperimentJob", metadata, new { experimentJobId });
        var replay = await TryReplayAsync<ExperimentJob>(
            metadata.RequestId,
            "ExperimentJobCancelled",
            fingerprint,
            cancellationToken);
        if (replay is not null) return replay;

        var job = await FindJobAsync(experimentJobId, cancellationToken);
        var status = ExperimentSchedulingPersistence.ParseStatus<ExperimentJobStatus>(job.Status);
        if (status is not ExperimentJobStatus.Draft and
            not ExperimentJobStatus.Ready and
            not ExperimentJobStatus.Scheduled and
            not ExperimentJobStatus.Blocked)
        {
            throw new ExperimentSchedulingConflictException(
                $"A job in '{status}' state cannot be cancelled by the planner.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entry = await database.ScheduleEntries.SingleOrDefaultAsync(
            item => item.ExperimentJobId == experimentJobId,
            cancellationToken);
        IReadOnlyList<ResourceReservationRecord> released = [];
        if (entry is not null)
        {
            released = await ReleaseReservationsAsync(entry.ScheduleEntryId, now, cancellationToken);
            entry.Status = ScheduleEntryStatus.Cancelled.ToString();
            entry.BlockingReasonsJson = "[]";
            entry.UpdatedAtUtc = now;
        }
        job.Status = ExperimentJobStatus.Cancelled.ToString();
        job.CompletedAtUtc = now;
        job.UpdatedAtUtc = now;

        var result = ExperimentSchedulingPersistence.MapJob(job);
        AddAudit(
            metadata,
            fingerprint,
            "ExperimentJobCancelled",
            result.Status.ToString(),
            result,
            planId: job.PlanId,
            planVersion: job.PlanVersion,
            experimentJobId: job.JobId,
            scheduleEntryId: entry?.ScheduleEntryId,
            details: new Dictionary<string, string?>
            {
                ["previousStatus"] = status.ToString(),
                ["releasedReservationCount"] = released.Count.ToString(),
                ["releasedResourceKeys"] = FormatResourceKeys(released)
            });
        await database.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<ExperimentPlanValidationResult> ValidatePlanRecordAsync(
        ExperimentPlanRecord record,
        string actor,
        CancellationToken cancellationToken)
    {
        var issues = new List<ExperimentPlanValidationIssue>();
        if (string.IsNullOrWhiteSpace(record.Name))
        {
            issues.Add(PlanIssue(
                ExperimentSchedulingIssueCodes.PlanNameRequired,
                "Experiment plan name is required.",
                "name"));
        }
        var workflowSteps = ExperimentSchedulingPersistence.NormalizeWorkflowSteps(
            ExperimentSchedulingPersistence.Deserialize(
                record.WorkflowStepsJson,
                Array.Empty<ExperimentPlanWorkflowStep>()),
            record.WorkflowId,
            record.WorkflowVersion,
            record.PlanId);
        if (workflowSteps.Count == 0)
        {
            issues.Add(PlanIssue(
                ExperimentSchedulingIssueCodes.WorkflowReferenceRequired,
                "At least one immutable workflow template reference is required.",
                "workflowSteps"));
        }
        else
        {
            var stepIds = new HashSet<Guid>();
            for (var index = 0; index < workflowSteps.Count; index++)
            {
                var step = workflowSteps[index];
                var field = $"workflowSteps[{index}]";
                if (step.WorkflowId == Guid.Empty || step.WorkflowVersion <= 0 ||
                    !stepIds.Add(step.StepId))
                {
                    issues.Add(PlanIssue(
                        ExperimentSchedulingIssueCodes.WorkflowStepInvalid,
                        "Each workflow step needs a unique id and a positive immutable workflow version reference.",
                        field));
                    continue;
                }

                if (workflowSteps.Count > 1 && step.EstimatedDurationMinutes <= 0)
                {
                    issues.Add(PlanIssue(
                        ExperimentSchedulingIssueCodes.WorkflowStepInvalid,
                        "Composed workflow steps require a positive estimated duration for deterministic scheduling.",
                        $"{field}.estimatedDurationMinutes"));
                }

                if (step.Parameters.Keys.Any(key => string.IsNullOrWhiteSpace(key)))
                {
                    issues.Add(PlanIssue(
                        ExperimentSchedulingIssueCodes.ParameterNameInvalid,
                        "Workflow-step parameter names must not be empty.",
                        $"{field}.parameters"));
                }

                var workflow = await database.WorkflowVersions.AsNoTracking().SingleOrDefaultAsync(
                    item => item.WorkflowId == step.WorkflowId && item.Version == step.WorkflowVersion,
                    cancellationToken);
                if (workflow is null)
                {
                    issues.Add(PlanIssue(
                        ExperimentSchedulingIssueCodes.WorkflowVersionNotFound,
                        $"Workflow version '{step.WorkflowId}/{step.WorkflowVersion}' was not found.",
                        field));
                }
                else if (!string.Equals(workflow.PublishStatus, "Published", StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(workflow.Status, "Published", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(PlanIssue(
                        ExperimentSchedulingIssueCodes.WorkflowVersionNotPublished,
                        $"Workflow version '{step.WorkflowId}/{step.WorkflowVersion}' is not published.",
                        field));
                }
            }
        }

        if (!string.Equals(record.ProfileProductId, profile.Product.ProductId, StringComparison.Ordinal) ||
            !string.Equals(record.ProfileVersion, profile.Product.Version, StringComparison.Ordinal))
        {
            issues.Add(PlanIssue(
                ExperimentSchedulingIssueCodes.ProfileMismatch,
                $"Plan Profile '{record.ProfileProductId}/{record.ProfileVersion}' does not match active Profile '{profile.Product.ProductId}/{profile.Product.Version}'.",
                "profile"));
        }

        var materials = ExperimentSchedulingPersistence.Deserialize(
            record.MaterialRequirementsJson,
            Array.Empty<ExperimentMaterialRequirement>());
        foreach (var material in materials)
        {
            if (string.IsNullOrWhiteSpace(material.MaterialId) ||
                string.IsNullOrWhiteSpace(material.Name) ||
                material.Quantity is <= 0)
            {
                issues.Add(PlanIssue(
                    ExperimentSchedulingIssueCodes.MaterialInvalid,
                    "Each material requires an id, a name, and a positive quantity when quantity is specified.",
                    "materialRequirements"));
            }
        }

        var parameters = ExperimentSchedulingPersistence.Deserialize(
            record.DefaultParametersJson,
            new Dictionary<string, string?>());
        if (parameters.Keys.Any(string.IsNullOrWhiteSpace))
        {
            issues.Add(PlanIssue(
                ExperimentSchedulingIssueCodes.ParameterNameInvalid,
                "Default parameter names must not be empty.",
                "defaultParameters"));
        }

        var requirements = ExperimentSchedulingPersistence.Deserialize(
            record.ResourceRequirementsJson,
            Array.Empty<ExperimentResourceRequirement>());
        var exactKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in requirements)
        {
            var type = NormalizeOptionalText(requirement.ResourceType);
            var resourceId = NormalizeOptionalText(requirement.ResourceId);
            var capabilityId = NormalizeOptionalText(requirement.CapabilityId);
            if (type is null || requirement.Quantity <= 0 ||
                (resourceId is null && capabilityId is null))
            {
                issues.Add(PlanIssue(
                    ExperimentSchedulingIssueCodes.ResourceRequirementInvalid,
                    "Each resource requirement needs a type, a positive quantity, and a resource id or capability.",
                    "resourceRequirements"));
                continue;
            }
            if (resourceId is not null && requirement.Quantity != 1)
            {
                issues.Add(PlanIssue(
                    ExperimentSchedulingIssueCodes.ResourceRequirementInvalid,
                    "A requirement for one concrete resource must have quantity 1.",
                    "resourceRequirements",
                    new ExperimentResourceReference { ResourceType = type, ResourceId = resourceId }));
                continue;
            }

            if (resourceId is not null)
            {
                var reference = new ExperimentResourceReference
                {
                    ResourceType = type,
                    ResourceId = resourceId
                };
                var key = ExperimentResourceKeys.Create(type, resourceId);
                if (!exactKeys.Add(key))
                {
                    issues.Add(PlanIssue(
                        ExperimentSchedulingIssueCodes.ResourceRequirementInvalid,
                        $"Resource '{type}/{resourceId}' is required more than once.",
                        "resourceRequirements",
                        reference));
                }
                if (!resourceCatalog.TryGet(reference, out var resource))
                {
                    issues.Add(PlanIssue(
                        ExperimentSchedulingIssueCodes.ResourceNotConfigured,
                        $"Resource '{type}/{resourceId}' is not configured by the active Profile.",
                        "resourceRequirements",
                        reference));
                }
                else
                {
                    var configuredResource = resource!;
                    if (!configuredResource.Enabled)
                    {
                        issues.Add(PlanIssue(
                            ExperimentSchedulingIssueCodes.ResourceDisabled,
                            $"Resource '{type}/{resourceId}' is disabled by the active Profile.",
                            "resourceRequirements",
                            configuredResource.Resource));
                    }
                    if (capabilityId is not null && !configuredResource.Provides(capabilityId))
                    {
                        issues.Add(PlanIssue(
                            ExperimentSchedulingIssueCodes.ResourceCapabilityMissing,
                            $"Resource '{type}/{resourceId}' does not provide capability '{capabilityId}'.",
                            "resourceRequirements",
                            configuredResource.Resource));
                    }
                }
            }
            else
            {
                var candidates = resourceCatalog.Resources
                    .Where(resource =>
                        string.Equals(resource.Resource.ResourceType, type, StringComparison.OrdinalIgnoreCase) &&
                        resource.Enabled &&
                        resource.Provides(capabilityId!))
                    .ToArray();
                if (candidates.Length < requirement.Quantity)
                {
                    issues.Add(PlanIssue(
                        ExperimentSchedulingIssueCodes.ResourceCapacityInsufficient,
                        $"Only {candidates.Length} enabled '{type}' resources provide capability '{capabilityId}', but {requirement.Quantity} are required.",
                        "resourceRequirements"));
                }
            }
        }

        return new ExperimentPlanValidationResult
        {
            Issues = issues
                .OrderBy(issue => issue.Code, StringComparer.Ordinal)
                .ThenBy(issue => issue.Field, StringComparer.Ordinal)
                .ThenBy(issue => issue.Resource?.ResourceType, StringComparer.Ordinal)
                .ThenBy(issue => issue.Resource?.ResourceId, StringComparer.Ordinal)
                .ThenBy(issue => issue.Message, StringComparer.Ordinal)
                .ToArray(),
            ValidatorVersion = PlanValidatorVersion,
            ValidatedAt = timeProvider.GetUtcNow(),
            ValidatedBy = actor
        };
    }

    private async Task<IReadOnlyList<ScheduleBlockReason>> EvaluateScheduleAsync(
        ExperimentPlanRecord plan,
        Guid scheduleEntryId,
        IReadOnlyList<ExperimentResourceReference> resources,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        CancellationToken cancellationToken)
    {
        var reasons = new List<ScheduleBlockReason>();
        var selected = new Dictionary<string, ConfiguredExperimentResource?>(StringComparer.Ordinal);
        foreach (var reference in resources)
        {
            var key = ExperimentResourceKeys.Create(reference.ResourceType, reference.ResourceId);
            if (!resourceCatalog.TryGet(reference, out var resource))
            {
                selected[key] = null;
                reasons.Add(BlockReason(
                    ExperimentSchedulingIssueCodes.ResourceNotConfigured,
                    $"Resource '{reference.ResourceType}/{reference.ResourceId}' is not configured by the active Profile.",
                    reference));
                continue;
            }

            var configuredResource = resource!;
            selected[key] = configuredResource;
            if (!configuredResource.Enabled)
            {
                reasons.Add(BlockReason(
                    ExperimentSchedulingIssueCodes.ResourceDisabled,
                    $"Resource '{reference.ResourceType}/{reference.ResourceId}' is disabled by the active Profile.",
                    configuredResource.Resource));
            }
        }

        var requirements = ExperimentSchedulingPersistence.Deserialize(
            plan.ResourceRequirementsJson,
            Array.Empty<ExperimentResourceRequirement>());
        foreach (var requirement in requirements)
        {
            var type = requirement.ResourceType.Trim();
            if (!string.IsNullOrWhiteSpace(requirement.ResourceId))
            {
                var requiredReference = new ExperimentResourceReference
                {
                    ResourceType = type,
                    ResourceId = requirement.ResourceId.Trim()
                };
                var key = ExperimentResourceKeys.Create(type, requiredReference.ResourceId);
                if (!selected.ContainsKey(key))
                {
                    reasons.Add(BlockReason(
                        ExperimentSchedulingIssueCodes.ResourceSelectionMissing,
                        $"Required resource '{type}/{requiredReference.ResourceId}' was not selected.",
                        requiredReference));
                }
                continue;
            }

            var matchingCount = selected.Values.Count(resource =>
                resource is not null &&
                resource.Enabled &&
                string.Equals(resource.Resource.ResourceType, type, StringComparison.OrdinalIgnoreCase) &&
                resource.Provides(requirement.CapabilityId!));
            if (matchingCount < requirement.Quantity)
            {
                reasons.Add(BlockReason(
                    ExperimentSchedulingIssueCodes.ResourceSelectionMissing,
                    $"Select {requirement.Quantity} enabled '{type}' resources with capability '{requirement.CapabilityId}'; {matchingCount} were selected."));
            }
        }

        var selectedKeys = selected
            .Where(item => item.Value?.Enabled == true)
            .Select(item => item.Key)
            .ToArray();
        if (selectedKeys.Length > 0)
        {
            var overlaps = await database.ResourceReservations
                .AsNoTracking()
                .Where(reservation =>
                    reservation.ScheduleEntryId != scheduleEntryId &&
                    reservation.Status == ResourceReservationStatus.Planned.ToString() &&
                    selectedKeys.Contains(reservation.ResourceKey) &&
                    reservation.EndsAtUtc > startsAtUtc &&
                    reservation.StartsAtUtc < endsAtUtc)
                .ToListAsync(cancellationToken);
            foreach (var group in overlaps.GroupBy(item => item.ResourceKey))
            {
                var configured = selected[group.Key]!;
                var peak = ExperimentSchedulingPersistence.GetPeakConcurrentReservationCount(
                    group,
                    startsAtUtc,
                    endsAtUtc);
                if (peak < configured.Capacity) continue;
                var conflicts = group
                    .Select(item => item.ScheduleEntryId)
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray();
                reasons.Add(new ScheduleBlockReason
                {
                    Code = ExperimentSchedulingIssueCodes.ResourceReservationConflict,
                    Message = $"Resource '{configured.Resource.ResourceId}' has no capacity in the requested half-open time window.",
                    Resource = configured.Resource,
                    ConflictingScheduleEntryIds = conflicts
                });
            }
        }

        return reasons
            .GroupBy(reason => new
            {
                reason.Code,
                ResourceType = reason.Resource?.ResourceType,
                ResourceId = reason.Resource?.ResourceId,
                reason.Message
            })
            .Select(group => group.First())
            .OrderBy(reason => reason.Code, StringComparer.Ordinal)
            .ThenBy(reason => reason.Resource?.ResourceType, StringComparer.Ordinal)
            .ThenBy(reason => reason.Resource?.ResourceId, StringComparer.Ordinal)
            .ThenBy(reason => reason.Message, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyList<ResourceReservationRecord>> ReleaseReservationsAsync(
        Guid scheduleEntryId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var reservations = await database.ResourceReservations
            .Where(reservation =>
                reservation.ScheduleEntryId == scheduleEntryId &&
                reservation.Status == ResourceReservationStatus.Planned.ToString())
            .ToListAsync(cancellationToken);
        foreach (var reservation in reservations)
        {
            reservation.Status = ResourceReservationStatus.Released.ToString();
            reservation.UpdatedAtUtc = now;
        }
        return reservations;
    }

    private async Task EnsureWorkflowVersionPublishedAsync(
        Guid workflowId,
        int workflowVersion,
        CancellationToken cancellationToken)
    {
        var workflow = await database.WorkflowVersions.AsNoTracking().SingleOrDefaultAsync(
            item => item.WorkflowId == workflowId && item.Version == workflowVersion,
            cancellationToken) ?? throw new ExperimentSchedulingConflictException(
            $"Workflow version '{workflowId}/{workflowVersion}' no longer exists.");
        if (!string.Equals(workflow.Status, "Published", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(workflow.PublishStatus, "Published", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExperimentSchedulingConflictException(
                $"Workflow version '{workflowId}/{workflowVersion}' is not published.");
        }
    }

    private async Task EnsureWorkflowStepsPublishedAsync(
        IReadOnlyList<ExperimentPlanWorkflowStep> steps,
        CancellationToken cancellationToken)
    {
        if (steps.Count == 0)
        {
            throw new ExperimentSchedulingConflictException(
                "The published experiment plan does not contain a workflow template reference.");
        }

        foreach (var step in steps)
        {
            await EnsureWorkflowVersionPublishedAsync(
                step.WorkflowId,
                step.WorkflowVersion,
                cancellationToken);
        }
    }

    private async Task<ExperimentPlanRecord> FindPlanAsync(
        Guid planId,
        int version,
        CancellationToken cancellationToken)
    {
        return await database.ExperimentPlans.SingleOrDefaultAsync(
            plan => plan.PlanId == planId && plan.Version == version,
            cancellationToken) ?? throw new KeyNotFoundException(
            $"Experiment plan '{planId}/{version}' was not found.");
    }

    private async Task<ExperimentJobRecord> FindJobAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        return await database.ExperimentJobs.SingleOrDefaultAsync(
            job => job.JobId == jobId,
            cancellationToken) ?? throw new KeyNotFoundException(
            $"Experiment job '{jobId}' was not found.");
    }

    private async Task<T?> TryReplayAsync<T>(
        Guid requestId,
        string eventType,
        string fingerprint,
        CancellationToken cancellationToken)
        where T : class
    {
        var audit = await database.ExperimentSchedulingAudits
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken);
        if (audit is null) return null;
        if (!string.Equals(audit.EventType, eventType, StringComparison.Ordinal) ||
            !string.Equals(audit.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new ExperimentSchedulingConflictException(
                $"Request id '{requestId}' was already used for a different scheduling action or payload.");
        }

        return ExperimentSchedulingPersistence.Deserialize<T?>(audit.ResultJson, null) ??
            throw new InvalidOperationException(
                $"Scheduling audit '{audit.Id}' does not contain a replay result.");
    }

    private void AddAudit<T>(
        NormalizedMetadata metadata,
        string fingerprint,
        string eventType,
        string outcome,
        T result,
        string? code = null,
        Guid? planId = null,
        int? planVersion = null,
        Guid? experimentJobId = null,
        Guid? scheduleEntryId = null,
        IReadOnlyDictionary<string, string?>? details = null)
    {
        database.ExperimentSchedulingAudits.Add(new ExperimentSchedulingAuditRecord
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            Outcome = outcome,
            Code = code,
            RequestId = metadata.RequestId,
            RequestFingerprint = fingerprint,
            Actor = metadata.Actor,
            Reason = metadata.Reason,
            PlanId = planId,
            PlanVersion = planVersion,
            ExperimentJobId = experimentJobId,
            ScheduleEntryId = scheduleEntryId,
            DetailsJson = ExperimentSchedulingPersistence.Serialize(details ??
                new Dictionary<string, string?>()),
            ResultJson = ExperimentSchedulingPersistence.Serialize(result),
            OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime
        });
    }

    private static void ApplyDraft(ExperimentPlanRecord record, ExperimentPlanDraft draft)
    {
        record.Name = draft.Name;
        record.Description = draft.Description;
        var workflowSteps = ExperimentSchedulingPersistence.NormalizeWorkflowSteps(
            draft.WorkflowSteps,
            draft.WorkflowId,
            draft.WorkflowVersion,
            record.PlanId);
        var firstStep = workflowSteps.FirstOrDefault();
        record.WorkflowId = firstStep?.WorkflowId ?? draft.WorkflowId;
        record.WorkflowVersion = firstStep?.WorkflowVersion ?? draft.WorkflowVersion;
        record.WorkflowStepsJson = ExperimentSchedulingPersistence.Serialize(workflowSteps);
        record.MaterialRequirementsJson = ExperimentSchedulingPersistence.Serialize(draft.MaterialRequirements);
        record.DefaultParametersJson = ExperimentSchedulingPersistence.Serialize(draft.DefaultParameters);
        record.ResourceRequirementsJson = ExperimentSchedulingPersistence.Serialize(draft.ResourceRequirements);
        record.ProfileProductId = draft.ProfileProductId;
        record.ProfileVersion = draft.ProfileVersion;
        record.LayoutId = draft.LayoutId;
    }

    private ExperimentPlanDraft NormalizeDraft(ExperimentPlanDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var materials = (draft.MaterialRequirements ?? [])
            .Select(material => new ExperimentMaterialRequirement
            {
                MaterialId = material.MaterialId?.Trim() ?? string.Empty,
                Name = material.Name?.Trim() ?? string.Empty,
                Quantity = material.Quantity,
                Unit = NormalizeOptionalText(material.Unit),
                Specification = NormalizeOptionalText(material.Specification)
            })
            .ToArray();
        var requirements = (draft.ResourceRequirements ?? [])
            .Select(requirement => new ExperimentResourceRequirement
            {
                ResourceType = requirement.ResourceType?.Trim() ?? string.Empty,
                ResourceId = NormalizeOptionalText(requirement.ResourceId),
                CapabilityId = NormalizeOptionalText(requirement.CapabilityId),
                Quantity = requirement.Quantity,
                Exclusive = requirement.Exclusive
            })
            .ToArray();
        var workflowSteps = ExperimentSchedulingPersistence.NormalizeWorkflowSteps(
            draft.WorkflowSteps,
            draft.WorkflowId,
            draft.WorkflowVersion,
            draft.WorkflowId);
        var firstStep = workflowSteps.FirstOrDefault();
        return new ExperimentPlanDraft
        {
            Name = draft.Name?.Trim() ?? string.Empty,
            Description = draft.Description?.Trim() ?? string.Empty,
            WorkflowId = firstStep?.WorkflowId ?? draft.WorkflowId,
            WorkflowVersion = firstStep?.WorkflowVersion ?? draft.WorkflowVersion,
            WorkflowSteps = workflowSteps,
            MaterialRequirements = materials,
            DefaultParameters = NormalizeParameters(draft.DefaultParameters, nameof(draft.DefaultParameters)),
            ResourceRequirements = requirements,
            ProfileProductId = NormalizeOptionalText(draft.ProfileProductId) ?? profile.Product.ProductId,
            ProfileVersion = NormalizeOptionalText(draft.ProfileVersion) ?? profile.Product.Version,
            LayoutId = NormalizeOptionalText(draft.LayoutId)
        };
    }

    private static object CanonicalizeDraft(ExperimentPlanDraft draft) => new
    {
        draft.Name,
        draft.Description,
        draft.WorkflowId,
        draft.WorkflowVersion,
        WorkflowSteps = draft.WorkflowSteps
            .OrderBy(item => item.Order)
            .ThenBy(item => item.StepId)
            .ToArray(),
        Materials = draft.MaterialRequirements
            .OrderBy(item => item.MaterialId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        Parameters = draft.DefaultParameters
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        Requirements = draft.ResourceRequirements
            .OrderBy(item => item.ResourceType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ResourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.CapabilityId, StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        draft.ProfileProductId,
        draft.ProfileVersion,
        draft.LayoutId
    };

    private static IReadOnlyList<ExperimentResourceReference> NormalizeResources(
        IReadOnlyList<ExperimentResourceReference>? resources)
    {
        var result = new List<ExperimentResourceReference>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in resources ?? [])
        {
            var type = RequireText(resource.ResourceType, nameof(resource.ResourceType));
            var id = RequireText(resource.ResourceId, nameof(resource.ResourceId));
            var key = ExperimentResourceKeys.Create(type, id);
            if (!keys.Add(key))
                throw new ArgumentException($"Resource '{type}/{id}' was selected more than once.", nameof(resources));
            result.Add(new ExperimentResourceReference { ResourceType = type, ResourceId = id });
        }
        return result
            .OrderBy(resource => ExperimentResourceKeys.Create(resource.ResourceType, resource.ResourceId), StringComparer.Ordinal)
            .ToArray();
    }

    private static string FormatResourceKeys(IEnumerable<ExperimentResourceReference> resources) =>
        string.Join(",", resources
            .Select(resource => FormatResourceKey(resource.ResourceType, resource.ResourceId))
            .OrderBy(value => value, StringComparer.Ordinal));

    private static string FormatResourceKeys(IEnumerable<ResourceReservationRecord> reservations) =>
        string.Join(",", reservations
            .Select(reservation => FormatResourceKey(reservation.ResourceType, reservation.ResourceId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal));

    private static string FormatResourceKey(string resourceType, string resourceId) =>
        $"{resourceType.Trim().ToUpperInvariant()}/{resourceId.Trim().ToUpperInvariant()}";

    private static IReadOnlyDictionary<string, string?> NormalizeParameters(
        IReadOnlyDictionary<string, string?>? parameters,
        string parameterName)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in parameters ?? new Dictionary<string, string?>())
        {
            var key = RequireText(item.Key, parameterName);
            if (!result.TryAdd(key, item.Value))
                throw new ArgumentException($"Parameter '{key}' was provided more than once.", parameterName);
        }
        return result;
    }

    private static NormalizedMetadata NormalizeMetadata(Guid requestId, string actor, string reason)
    {
        RequireIdentity(requestId, nameof(requestId));
        return new NormalizedMetadata(
            requestId,
            RequireText(actor, nameof(actor)),
            RequireText(reason, nameof(reason)));
    }

    private static string CreateFingerprint(string action, NormalizedMetadata metadata, object payload)
    {
        var canonical = ExperimentSchedulingPersistence.Serialize(new
        {
            Action = action,
            metadata.Actor,
            metadata.Reason,
            Payload = payload
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static ExperimentPlanValidationIssue PlanIssue(
        string code,
        string message,
        string field,
        ExperimentResourceReference? resource = null) => new()
        {
            Code = code,
            Message = message,
            Field = field,
            Resource = resource
        };

    private static ScheduleBlockReason BlockReason(
        string code,
        string message,
        ExperimentResourceReference? resource = null) => new()
        {
            Code = code,
            Message = message,
            Resource = resource
        };

    private static void RequireIdentity(Guid value, string parameterName)
    {
        if (value == Guid.Empty) throw new ArgumentException("A non-empty id is required.", parameterName);
    }

    private static void RequirePositive(int value, string parameterName)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(parameterName, "A positive value is required.");
    }

    private static string RequireText(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<T> ExecuteMutationAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        await mutationGate.EnterAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            mutationGate.Exit();
        }
    }

    private sealed record NormalizedMetadata(Guid RequestId, string Actor, string Reason);
}
