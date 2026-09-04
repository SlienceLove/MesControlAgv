using System.Security.Cryptography;
using System.Text;
using MesControlAgv.Application;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Converts one scheduled job into a workflow run and run-wide resource leases
/// in one database transaction. It creates no adapter or device operation.
/// </summary>
public sealed class ExperimentRuntimeAdmissionService(
    MesDbContext database,
    IWorkflowApplicationService workflows,
    ExperimentResourceCatalog resourceCatalog,
    ExperimentSchedulingMutationGate mutationGate,
    ExperimentRuntimeLeaseLifecycle leaseLifecycle,
    TimeProvider timeProvider) : IExperimentRuntimeAdmissionService
{
    private const string AdmittedEventType = "ExperimentJobAdmitted";
    private const string RejectedEventType = "ExperimentJobAdmissionRejected";

    public async Task<ExperimentJobAdmissionResult> AdmitJobAsync(
        Guid experimentJobId,
        AdmitExperimentJobRequest request,
        CancellationToken cancellationToken)
    {
        if (experimentJobId == Guid.Empty)
            throw new ArgumentException("An experiment job id is required.", nameof(experimentJobId));
        ArgumentNullException.ThrowIfNull(request);
        var metadata = NormalizeMetadata(request);
        var fingerprint = CreateFingerprint(experimentJobId, metadata);

        await mutationGate.EnterAsync(cancellationToken);
        try
        {
            var replay = await TryReplayAsync(metadata.RequestId, fingerprint, cancellationToken);
            if (replay is not null) return replay;

            var job = await database.ExperimentJobs.SingleOrDefaultAsync(
                item => item.JobId == experimentJobId,
                cancellationToken) ?? throw new KeyNotFoundException(
                $"Experiment job '{experimentJobId}' was not found.");
            var schedules = await database.ScheduleEntries
                .Where(entry => entry.ExperimentJobId == experimentJobId)
                .OrderByDescending(entry => entry.UpdatedAtUtc)
                .ThenBy(entry => entry.ScheduleEntryId)
                .ToListAsync(cancellationToken);
            var schedule = schedules.FirstOrDefault();
            var reservations = schedule is null
                ? []
                : await database.ResourceReservations
                    .Where(reservation => reservation.ScheduleEntryId == schedule.ScheduleEntryId)
                    .OrderBy(reservation => reservation.CreatedAtUtc)
                    .ThenBy(reservation => reservation.ReservationId)
                    .ToListAsync(cancellationToken);

            var rejection = await ValidateAdmissionAsync(
                job,
                schedules,
                schedule,
                reservations,
                metadata.RequestId,
                cancellationToken);
            if (rejection is not null)
            {
                return await PersistRejectedAsync(
                    experimentJobId,
                    metadata,
                    fingerprint,
                    rejection,
                    cancellationToken);
            }

            try
            {
                return await AdmitInTransactionAsync(
                    job,
                    schedule!,
                    reservations.Where(reservation =>
                        reservation.Status == ResourceReservationStatus.Planned.ToString()).ToArray(),
                    metadata,
                    fingerprint,
                    cancellationToken);
            }
            catch (WorkflowAdmissionRejectedSignal signal)
            {
                database.ChangeTracker.Clear();
                return await PersistRejectedAsync(
                    experimentJobId,
                    metadata,
                    fingerprint,
                    new AdmissionRejection(
                        signal.Code,
                        signal.Message,
                        Array.Empty<ExperimentResourceReference>()),
                    cancellationToken);
            }
            catch (LeaseConflictSignal signal)
            {
                database.ChangeTracker.Clear();
                return await PersistRejectedAsync(
                    experimentJobId,
                    metadata,
                    fingerprint,
                    new AdmissionRejection(
                        ExperimentSchedulingIssueCodes.ResourceLeaseActive,
                        "One or more requested resources already have an active runtime lease.",
                        signal.Resources),
                    cancellationToken);
            }
        }
        finally
        {
            mutationGate.Exit();
        }
    }

    private async Task<ExperimentJobAdmissionResult> AdmitInTransactionAsync(
        ExperimentJobRecord job,
        ScheduleEntryRecord schedule,
        IReadOnlyList<ResourceReservationRecord> plannedReservations,
        NormalizedMetadata metadata,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var workflowResult = await workflows.ExecuteAsync(new WorkflowExecutionRequest
            {
                WorkflowId = job.WorkflowId,
                Version = job.WorkflowVersion,
                Parameters = ExperimentSchedulingPersistence.Deserialize(
                    job.ParametersJson,
                    new Dictionary<string, string?>()),
                RequestedBy = metadata.Actor,
                CorrelationId = $"experiment-job:{job.JobId:N}",
                RequestId = metadata.RequestId,
                RequestedAt = ExperimentSchedulingPersistence.ToOffset(now),
                DryRun = false
            }, cancellationToken);
            if (workflowResult.IsRejected || workflowResult.IsIdempotentReplay)
            {
                var code = workflowResult.RejectionCode == WorkflowExecutionRejectionCodes.RequestIdReused ||
                           workflowResult.IsIdempotentReplay
                    ? ExperimentSchedulingIssueCodes.AdmissionRequestIdReused
                    : ExperimentSchedulingIssueCodes.WorkflowAdmissionRejected;
                throw new WorkflowAdmissionRejectedSignal(
                    code,
                    workflowResult.RejectionReason ??
                    "The pinned workflow version rejected experiment admission.");
            }

            var leaseExpiry = CalculateLeaseExpiry(schedule, now);
            var leases = plannedReservations.Select(reservation => new WorkflowResourceLeaseRecord
            {
                LeaseId = Guid.NewGuid(),
                ScheduleEntryId = schedule.ScheduleEntryId,
                WorkflowRunId = workflowResult.ExecutionId,
                NodeExecutionId = null,
                ResourceType = reservation.ResourceType,
                ResourceId = reservation.ResourceId,
                ResourceKey = reservation.ResourceKey,
                ActiveResourceKey = reservation.ResourceKey,
                Status = ResourceLeaseStatus.Active.ToString(),
                AcquiredBy = metadata.Actor,
                AcquiredAtUtc = now,
                ExpiresAtUtc = leaseExpiry,
                UpdatedAtUtc = now
            }).ToList();
            database.WorkflowResourceLeases.AddRange(leases);

            foreach (var reservation in plannedReservations)
            {
                reservation.Status = ResourceReservationStatus.Released.ToString();
                reservation.UpdatedAtUtc = now;
            }
            job.WorkflowRunId = workflowResult.ExecutionId;
            job.Status = ExperimentJobStatus.Admitted.ToString();
            job.LastError = null;
            job.UpdatedAtUtc = now;
            schedule.Status = ScheduleEntryStatus.Admitted.ToString();
            schedule.BlockingReasonsJson = "[]";
            schedule.UpdatedAtUtc = now;

            var initialResult = CreateAdmittedResult(
                metadata.RequestId,
                job,
                schedule,
                plannedReservations,
                leases);
            var audit = AddAdmissionAudit(metadata, fingerprint, initialResult, job, schedule, leases);
            await database.SaveChangesAsync(cancellationToken);

            var run = await database.WorkflowExecutions.SingleAsync(
                item => item.ExecutionId == workflowResult.ExecutionId,
                cancellationToken);
            await leaseLifecycle.SynchronizeRunStateAsync(
                run,
                metadata.Actor,
                metadata.Reason,
                cancellationToken);

            var finalResult = CreateAdmittedResult(
                metadata.RequestId,
                job,
                schedule,
                plannedReservations,
                leases);
            audit.ResultJson = ExperimentSchedulingPersistence.Serialize(finalResult);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return finalResult;
        }
        catch (DbUpdateException exception) when (IsActiveLeaseConstraintViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new LeaseConflictSignal(
                plannedReservations.Select(ToReference).ToArray(),
                exception);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<AdmissionRejection?> ValidateAdmissionAsync(
        ExperimentJobRecord job,
        IReadOnlyList<ScheduleEntryRecord> schedules,
        ScheduleEntryRecord? schedule,
        IReadOnlyList<ResourceReservationRecord> reservations,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        if (job.WorkflowRunId is not null ||
            ExperimentSchedulingPersistence.ParseStatus<ExperimentJobStatus>(job.Status) !=
            ExperimentJobStatus.Scheduled)
        {
            return new AdmissionRejection(
                ExperimentSchedulingIssueCodes.JobNotScheduled,
                $"Experiment job '{job.JobId}' is not in Scheduled state or is already linked to a workflow run.",
                Array.Empty<ExperimentResourceReference>());
        }
        if (schedules.Count != 1 || schedule is null ||
            ExperimentSchedulingPersistence.ParseStatus<ScheduleEntryStatus>(schedule.Status) !=
            ScheduleEntryStatus.Scheduled)
        {
            return new AdmissionRejection(
                ExperimentSchedulingIssueCodes.ScheduleNotReady,
                "The experiment job must have exactly one Scheduled entry before runtime admission.",
                Array.Empty<ExperimentResourceReference>());
        }
        if (await database.WorkflowExecutions.AsNoTracking().AnyAsync(
                execution => execution.RequestId == requestId,
                cancellationToken))
        {
            return new AdmissionRejection(
                ExperimentSchedulingIssueCodes.AdmissionRequestIdReused,
                $"Request id '{requestId}' was already used by a workflow execution.",
                Array.Empty<ExperimentResourceReference>());
        }

        var plan = await database.ExperimentPlans.AsNoTracking().SingleOrDefaultAsync(
            item => item.PlanId == job.PlanId && item.Version == job.PlanVersion,
            cancellationToken);
        if (plan is null ||
            ExperimentSchedulingPersistence.ParseStatus<ExperimentPlanStatus>(plan.Status) !=
            ExperimentPlanStatus.Published ||
            plan.WorkflowId != job.WorkflowId ||
            plan.WorkflowVersion != job.WorkflowVersion)
        {
            return new AdmissionRejection(
                ExperimentSchedulingIssueCodes.PlanVersionNotPublished,
                "The job's pinned experiment plan is missing, unpublished, or no longer matches its workflow reference.",
                Array.Empty<ExperimentResourceReference>());
        }
        var planWorkflowSteps = ExperimentSchedulingPersistence.NormalizeWorkflowSteps(
            ExperimentSchedulingPersistence.Deserialize(
                plan.WorkflowStepsJson,
                Array.Empty<ExperimentPlanWorkflowStep>()),
            plan.WorkflowId,
            plan.WorkflowVersion,
            plan.PlanId);
        if (planWorkflowSteps.Count > 1)
        {
            return new AdmissionRejection(
                ExperimentSchedulingIssueCodes.CompositeWorkflowNotSupported,
                "This plan contains multiple workflow templates. Runtime composition must be enabled before admission.",
                Array.Empty<ExperimentResourceReference>());
        }
        var workflow = await database.WorkflowVersions.AsNoTracking().SingleOrDefaultAsync(
            item => item.WorkflowId == job.WorkflowId && item.Version == job.WorkflowVersion,
            cancellationToken);
        if (workflow is null ||
            !string.Equals(workflow.Status, "Published", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(workflow.PublishStatus, "Published", StringComparison.OrdinalIgnoreCase))
        {
            return new AdmissionRejection(
                ExperimentSchedulingIssueCodes.WorkflowVersionNotPublished,
                "The job's pinned workflow version is not published.",
                Array.Empty<ExperimentResourceReference>());
        }

        var requestedResources = ExperimentSchedulingPersistence.Deserialize(
            schedule.RequestedResourcesJson,
            Array.Empty<ExperimentResourceReference>());
        var requestedKeys = requestedResources
            .Select(reference => ExperimentResourceKeys.Create(reference.ResourceType, reference.ResourceId))
            .ToArray();
        if (requestedKeys.Distinct(StringComparer.Ordinal).Count() != requestedKeys.Length)
        {
            return new AdmissionRejection(
                ExperimentSchedulingIssueCodes.ReservationMismatch,
                "The scheduled resource list contains duplicate identities.",
                requestedResources);
        }
        foreach (var resource in requestedResources)
        {
            if (!resourceCatalog.TryGet(resource, out var configured))
            {
                return new AdmissionRejection(
                    ExperimentSchedulingIssueCodes.ResourceNotConfigured,
                    $"Resource '{resource.ResourceType}/{resource.ResourceId}' is not configured by the active Profile.",
                    [resource]);
            }
            if (!configured!.Enabled)
            {
                return new AdmissionRejection(
                    ExperimentSchedulingIssueCodes.ResourceDisabled,
                    $"Resource '{resource.ResourceType}/{resource.ResourceId}' is disabled by the active Profile.",
                    [resource]);
            }
        }

        var plannedReservations = reservations.Where(reservation =>
            reservation.Status == ResourceReservationStatus.Planned.ToString()).ToArray();
        var reservationKeys = plannedReservations.Select(reservation => reservation.ResourceKey).ToArray();
        if (plannedReservations.Any(reservation =>
                reservation.StartsAtUtc != schedule.PlannedStartUtc ||
                reservation.EndsAtUtc != schedule.PlannedEndUtc) ||
            !requestedKeys.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(
                reservationKeys.OrderBy(value => value, StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            return new AdmissionRejection(
                ExperimentSchedulingIssueCodes.ReservationMismatch,
                "Active planned reservations do not exactly match the scheduled resources and time window.",
                requestedResources);
        }

        var conflictingKeys = await database.WorkflowResourceLeases.AsNoTracking()
            .Where(lease => lease.ActiveResourceKey != null && requestedKeys.Contains(lease.ActiveResourceKey))
            .Select(lease => lease.ActiveResourceKey!)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (conflictingKeys.Count > 0)
        {
            return new AdmissionRejection(
                ExperimentSchedulingIssueCodes.ResourceLeaseActive,
                "One or more requested resources already have an active runtime lease.",
                requestedResources.Where(reference => conflictingKeys.Contains(
                    ExperimentResourceKeys.Create(reference.ResourceType, reference.ResourceId),
                    StringComparer.Ordinal)).ToArray());
        }
        return null;
    }

    private async Task<ExperimentJobAdmissionResult?> TryReplayAsync(
        Guid requestId,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var audit = await database.ExperimentSchedulingAudits.AsNoTracking().SingleOrDefaultAsync(
            item => item.RequestId == requestId,
            cancellationToken);
        if (audit is null) return null;
        if ((audit.EventType != AdmittedEventType && audit.EventType != RejectedEventType) ||
            !string.Equals(audit.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new ExperimentSchedulingConflictException(
                $"Request id '{requestId}' was already used for a different scheduling action or payload.",
                ExperimentSchedulingIssueCodes.AdmissionRequestIdReused);
        }

        var result = ExperimentSchedulingPersistence.Deserialize<ExperimentJobAdmissionResult?>(
            audit.ResultJson,
            null) ?? throw new InvalidOperationException(
            $"Experiment admission audit '{audit.Id}' does not contain a replay result.");
        return result with { IsIdempotentReplay = true };
    }

    private async Task<ExperimentJobAdmissionResult> PersistRejectedAsync(
        Guid experimentJobId,
        NormalizedMetadata metadata,
        string fingerprint,
        AdmissionRejection rejection,
        CancellationToken cancellationToken)
    {
        var (job, schedule) = await LoadProjectionAsync(experimentJobId, cancellationToken);
        var result = new ExperimentJobAdmissionResult
        {
            RequestId = metadata.RequestId,
            Status = ExperimentJobAdmissionStatus.Rejected,
            Job = job,
            ScheduleEntry = schedule,
            RejectionCode = rejection.Code,
            RejectionReason = rejection.Message,
            ConflictingResources = rejection.Resources
        };
        AddAdmissionAudit(metadata, fingerprint, result, job, schedule);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            var replay = await TryReplayAsync(metadata.RequestId, fingerprint, cancellationToken);
            if (replay is not null) return replay;
            throw;
        }
    }

    private ExperimentSchedulingAuditRecord AddAdmissionAudit(
        NormalizedMetadata metadata,
        string fingerprint,
        ExperimentJobAdmissionResult result,
        ExperimentJobRecord job,
        ScheduleEntryRecord schedule,
        IReadOnlyList<WorkflowResourceLeaseRecord> leases)
    {
        var audit = AddAdmissionAudit(
            metadata,
            fingerprint,
            result,
            ExperimentSchedulingPersistence.MapJob(job),
            ExperimentSchedulingPersistence.MapScheduleEntry(
                schedule,
                Array.Empty<ResourceReservation>()));
        audit.DetailsJson = ExperimentSchedulingPersistence.Serialize(new Dictionary<string, string?>
        {
            ["workflowRunId"] = result.WorkflowRunId?.ToString(),
            ["leaseCount"] = leases.Count.ToString(),
            ["resourceKeys"] = string.Join(
                ",",
                leases.Select(lease => $"{lease.ResourceType.ToUpperInvariant()}/{lease.ResourceId.ToUpperInvariant()}")
                    .OrderBy(value => value, StringComparer.Ordinal))
        });
        return audit;
    }

    private ExperimentSchedulingAuditRecord AddAdmissionAudit(
        NormalizedMetadata metadata,
        string fingerprint,
        ExperimentJobAdmissionResult result,
        ExperimentJob? job,
        ScheduleEntry? schedule)
    {
        var audit = new ExperimentSchedulingAuditRecord
        {
            Id = Guid.NewGuid(),
            EventType = result.IsAdmitted ? AdmittedEventType : RejectedEventType,
            Outcome = result.Status.ToString(),
            Code = result.RejectionCode,
            RequestId = metadata.RequestId,
            RequestFingerprint = fingerprint,
            Actor = metadata.Actor,
            Reason = metadata.Reason,
            PlanId = job?.PlanId,
            PlanVersion = job?.PlanVersion,
            ExperimentJobId = job?.JobId,
            ScheduleEntryId = schedule?.ScheduleEntryId,
            DetailsJson = ExperimentSchedulingPersistence.Serialize(new Dictionary<string, string?>
            {
                ["workflowRunId"] = result.WorkflowRunId?.ToString(),
                ["rejectionCode"] = result.RejectionCode,
                ["conflictingResourceCount"] = result.ConflictingResources.Count.ToString()
            }),
            ResultJson = ExperimentSchedulingPersistence.Serialize(result),
            OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        database.ExperimentSchedulingAudits.Add(audit);
        return audit;
    }

    private async Task<(ExperimentJob Job, ScheduleEntry? Schedule)> LoadProjectionAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var job = await database.ExperimentJobs.AsNoTracking().SingleAsync(
            item => item.JobId == jobId,
            cancellationToken);
        var schedule = await database.ScheduleEntries.AsNoTracking()
            .Where(entry => entry.ExperimentJobId == jobId)
            .OrderByDescending(entry => entry.UpdatedAtUtc)
            .ThenBy(entry => entry.ScheduleEntryId)
            .FirstOrDefaultAsync(cancellationToken);
        if (schedule is null) return (ExperimentSchedulingPersistence.MapJob(job), null);
        var reservations = await database.ResourceReservations.AsNoTracking()
            .Where(reservation => reservation.ScheduleEntryId == schedule.ScheduleEntryId)
            .OrderBy(reservation => reservation.CreatedAtUtc)
            .ThenBy(reservation => reservation.ReservationId)
            .ToListAsync(cancellationToken);
        return (
            ExperimentSchedulingPersistence.MapJob(job),
            ExperimentSchedulingPersistence.MapScheduleEntry(
                schedule,
                reservations.Select(ExperimentSchedulingPersistence.MapReservation).ToArray()));
    }

    private static ExperimentJobAdmissionResult CreateAdmittedResult(
        Guid requestId,
        ExperimentJobRecord job,
        ScheduleEntryRecord schedule,
        IReadOnlyList<ResourceReservationRecord> reservations,
        IReadOnlyList<WorkflowResourceLeaseRecord> leases) => new()
        {
            RequestId = requestId,
            Status = ExperimentJobAdmissionStatus.Admitted,
            WorkflowRunId = job.WorkflowRunId,
            Job = ExperimentSchedulingPersistence.MapJob(job),
            ScheduleEntry = ExperimentSchedulingPersistence.MapScheduleEntry(
            schedule,
            reservations.Select(ExperimentSchedulingPersistence.MapReservation).ToArray()),
            Leases = leases.Select(ExperimentSchedulingPersistence.MapLease).ToArray()
        };

    private static DateTime CalculateLeaseExpiry(ScheduleEntryRecord schedule, DateTime acquiredAt)
    {
        var plannedDuration = schedule.PlannedEndUtc - schedule.PlannedStartUtc;
        var durationDeadline = acquiredAt + plannedDuration;
        return schedule.PlannedEndUtc > durationDeadline ? schedule.PlannedEndUtc : durationDeadline;
    }

    private static ExperimentResourceReference ToReference(ResourceReservationRecord reservation) => new()
    {
        ResourceType = reservation.ResourceType,
        ResourceId = reservation.ResourceId
    };

    private static bool IsActiveLeaseConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 } sqlite &&
        (sqlite.Message.Contains("WorkflowResourceLeases.ActiveResourceKey", StringComparison.Ordinal) ||
         sqlite.Message.Contains("IX_WorkflowResourceLeases_ActiveResourceKey", StringComparison.Ordinal));

    private static NormalizedMetadata NormalizeMetadata(AdmitExperimentJobRequest request)
    {
        if (request.RequestId == Guid.Empty)
            throw new ArgumentException("An admission request id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Actor))
            throw new ArgumentException("An admission actor is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("An admission reason is required.", nameof(request));
        var actor = request.Actor.Trim();
        var reason = request.Reason.Trim();
        if (actor.Length > 256)
            throw new ArgumentException("The admission actor cannot exceed 256 characters.", nameof(request));
        if (reason.Length > 2048)
            throw new ArgumentException("The admission reason cannot exceed 2048 characters.", nameof(request));
        return new NormalizedMetadata(request.RequestId, actor, reason);
    }

    private static string CreateFingerprint(Guid jobId, NormalizedMetadata metadata)
    {
        var canonical = ExperimentSchedulingPersistence.Serialize(new
        {
            Action = "AdmitExperimentJob",
            ExperimentJobId = jobId,
            metadata.Actor,
            metadata.Reason
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record NormalizedMetadata(Guid RequestId, string Actor, string Reason);

    private sealed record AdmissionRejection(
        string Code,
        string Message,
        IReadOnlyList<ExperimentResourceReference> Resources);

    private sealed class WorkflowAdmissionRejectedSignal(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private sealed class LeaseConflictSignal(
        IReadOnlyList<ExperimentResourceReference> resources,
        Exception innerException) : Exception("A runtime lease conflict was detected.", innerException)
    {
        public IReadOnlyList<ExperimentResourceReference> Resources { get; } = resources;
    }
}
