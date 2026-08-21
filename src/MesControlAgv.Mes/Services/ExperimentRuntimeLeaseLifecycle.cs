using System.Security.Cryptography;
using System.Text;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Projects a linked workflow run into experiment state and owns explicit
/// release of run-wide leases. Expiry is evidence only; it never releases a
/// resource while an accepted non-terminal run may still own it.
/// </summary>
public sealed class ExperimentRuntimeLeaseLifecycle(
    MesDbContext database,
    TimeProvider timeProvider)
{
    public async Task SynchronizeRunStateAsync(
        WorkflowExecutionRecord run,
        string actor,
        string reason,
        CancellationToken cancellationToken)
    {
        var job = await database.ExperimentJobs.SingleOrDefaultAsync(
            item => item.WorkflowRunId == run.ExecutionId,
            cancellationToken);
        if (job is null) return;

        var runtimeStatus = ParseRuntimeStatus(run.RuntimeStatus);
        if (runtimeStatus == WorkflowRuntimeStatus.Running)
        {
            await MarkRunningAsync(job, run, actor, reason, cancellationToken);
            return;
        }

        if (runtimeStatus is WorkflowRuntimeStatus.Completed or
            WorkflowRuntimeStatus.Failed or
            WorkflowRuntimeStatus.Cancelled)
        {
            await MarkTerminalAsync(job, run, runtimeStatus, actor, reason, cancellationToken);
        }
    }

    public async Task MarkRecoveryFailureAsync(
        ExperimentJobRecord job,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var schedule = await FindScheduleAsync(job.JobId, cancellationToken);
        var leases = await database.WorkflowResourceLeases
            .Where(lease => lease.ActiveResourceKey != null &&
                            ((job.WorkflowRunId != null && lease.WorkflowRunId == job.WorkflowRunId) ||
                             (schedule != null && lease.ScheduleEntryId == schedule.ScheduleEntryId)))
            .ToListAsync(cancellationToken);
        ReleaseLeases(leases, "workflow-recovery", reason, now);
        await ReleasePlannedReservationsAsync(schedule, now, cancellationToken);

        job.Status = ExperimentJobStatus.Failed.ToString();
        job.LastError = reason;
        job.CompletedAtUtc ??= now;
        job.UpdatedAtUtc = now;
        if (schedule is not null)
        {
            schedule.Status = ScheduleEntryStatus.Completed.ToString();
            schedule.UpdatedAtUtc = now;
        }

        await AddAuditIfMissingAsync(
            "ExperimentRuntimeRecoveryFailed",
            ExperimentSchedulingIssueCodes.RuntimeRecoveryFailed,
            job,
            schedule,
            job.WorkflowRunId,
            "workflow-recovery",
            reason,
            leases.Count,
            ExperimentSchedulingPersistence.MapJob(job),
            cancellationToken);
    }

    public async Task<int> ReleaseDetachedLeasesAsync(
        Guid workflowRunId,
        string reason,
        CancellationToken cancellationToken)
    {
        var leases = await database.WorkflowResourceLeases
            .Where(lease => lease.WorkflowRunId == workflowRunId && lease.ActiveResourceKey != null)
            .ToListAsync(cancellationToken);
        if (leases.Count == 0) return 0;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        ReleaseLeases(leases, "workflow-recovery", reason, now);
        var scheduleEntryId = leases.Select(lease => lease.ScheduleEntryId).FirstOrDefault(id => id != null);
        await AddAuditIfMissingAsync(
            "ExperimentDetachedLeasesReleased",
            ExperimentSchedulingIssueCodes.RuntimeRecoveryFailed,
            job: null,
            schedule: null,
            workflowRunId,
            "workflow-recovery",
            reason,
            leases.Count,
            leases.Select(ExperimentSchedulingPersistence.MapLease).ToArray(),
            cancellationToken,
            scheduleEntryId);
        return leases.Count;
    }

    private async Task MarkRunningAsync(
        ExperimentJobRecord job,
        WorkflowExecutionRecord run,
        string actor,
        string reason,
        CancellationToken cancellationToken)
    {
        var status = ExperimentSchedulingPersistence.ParseStatus<ExperimentJobStatus>(job.Status);
        if (status == ExperimentJobStatus.Running) return;
        if (status != ExperimentJobStatus.Admitted) return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        job.Status = ExperimentJobStatus.Running.ToString();
        job.StartedAtUtc ??= now;
        job.UpdatedAtUtc = now;
        var schedule = await FindScheduleAsync(job.JobId, cancellationToken);
        await AddAuditIfMissingAsync(
            "ExperimentJobRunning",
            code: null,
            job,
            schedule,
            run.ExecutionId,
            actor,
            reason,
            releasedLeaseCount: 0,
            ExperimentSchedulingPersistence.MapJob(job),
            cancellationToken);
    }

    private async Task MarkTerminalAsync(
        ExperimentJobRecord job,
        WorkflowExecutionRecord run,
        WorkflowRuntimeStatus runtimeStatus,
        string actor,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var schedule = await FindScheduleAsync(job.JobId, cancellationToken);
        var leases = await database.WorkflowResourceLeases
            .Where(lease => lease.WorkflowRunId == run.ExecutionId && lease.ActiveResourceKey != null)
            .ToListAsync(cancellationToken);
        var releaseReason = runtimeStatus switch
        {
            WorkflowRuntimeStatus.Completed => "Workflow run completed.",
            WorkflowRuntimeStatus.Failed => run.LastError ?? reason,
            WorkflowRuntimeStatus.Cancelled => reason,
            _ => throw new ArgumentOutOfRangeException(nameof(runtimeStatus))
        };
        ReleaseLeases(leases, actor, releaseReason, now);
        await ReleasePlannedReservationsAsync(schedule, now, cancellationToken);

        var jobStatus = runtimeStatus switch
        {
            WorkflowRuntimeStatus.Completed => ExperimentJobStatus.Completed,
            WorkflowRuntimeStatus.Failed => ExperimentJobStatus.Failed,
            WorkflowRuntimeStatus.Cancelled => ExperimentJobStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(runtimeStatus))
        };
        job.Status = jobStatus.ToString();
        job.StartedAtUtc ??= now;
        job.CompletedAtUtc ??= now;
        job.LastError = runtimeStatus == WorkflowRuntimeStatus.Failed ? run.LastError ?? reason : null;
        job.UpdatedAtUtc = now;
        if (schedule is not null)
        {
            schedule.Status = (runtimeStatus == WorkflowRuntimeStatus.Cancelled
                ? ScheduleEntryStatus.Cancelled
                : ScheduleEntryStatus.Completed).ToString();
            schedule.UpdatedAtUtc = now;
        }

        var eventType = runtimeStatus switch
        {
            WorkflowRuntimeStatus.Completed => "ExperimentJobCompleted",
            WorkflowRuntimeStatus.Failed => "ExperimentJobFailed",
            WorkflowRuntimeStatus.Cancelled => "ExperimentJobCancelledByRuntime",
            _ => throw new ArgumentOutOfRangeException(nameof(runtimeStatus))
        };
        await AddAuditIfMissingAsync(
            eventType,
            code: null,
            job,
            schedule,
            run.ExecutionId,
            actor,
            releaseReason,
            leases.Count,
            ExperimentSchedulingPersistence.MapJob(job),
            cancellationToken);
    }

    private async Task<ScheduleEntryRecord?> FindScheduleAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        await database.ScheduleEntries
            .Where(entry => entry.ExperimentJobId == jobId)
            .OrderByDescending(entry => entry.UpdatedAtUtc)
            .ThenBy(entry => entry.ScheduleEntryId)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task ReleasePlannedReservationsAsync(
        ScheduleEntryRecord? schedule,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (schedule is null) return;
        var reservations = await database.ResourceReservations
            .Where(reservation => reservation.ScheduleEntryId == schedule.ScheduleEntryId &&
                                  reservation.Status == ResourceReservationStatus.Planned.ToString())
            .ToListAsync(cancellationToken);
        foreach (var reservation in reservations)
        {
            reservation.Status = ResourceReservationStatus.Released.ToString();
            reservation.UpdatedAtUtc = now;
        }
    }

    private static void ReleaseLeases(
        IEnumerable<WorkflowResourceLeaseRecord> leases,
        string actor,
        string reason,
        DateTime now)
    {
        foreach (var lease in leases)
        {
            lease.ActiveResourceKey = null;
            lease.Status = ResourceLeaseStatus.Released.ToString();
            lease.ReleasedBy = actor;
            lease.ReleaseReason = reason;
            lease.ReleasedAtUtc = now;
            lease.UpdatedAtUtc = now;
        }
    }

    private async Task AddAuditIfMissingAsync<T>(
        string eventType,
        string? code,
        ExperimentJobRecord? job,
        ScheduleEntryRecord? schedule,
        Guid? workflowRunId,
        string actor,
        string reason,
        int releasedLeaseCount,
        T result,
        CancellationToken cancellationToken,
        Guid? scheduleEntryId = null)
    {
        var stableSubjectId = workflowRunId ?? job?.JobId ?? scheduleEntryId ?? Guid.Empty;
        var requestId = CreateDeterministicRequestId(eventType, stableSubjectId);
        if (database.ExperimentSchedulingAudits.Local.Any(audit => audit.RequestId == requestId) ||
            await database.ExperimentSchedulingAudits.AsNoTracking().AnyAsync(
                audit => audit.RequestId == requestId,
                cancellationToken))
        {
            return;
        }

        database.ExperimentSchedulingAudits.Add(new ExperimentSchedulingAuditRecord
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            Outcome = job?.Status ?? ResourceLeaseStatus.Released.ToString(),
            Code = code,
            RequestId = requestId,
            RequestFingerprint = CreateFingerprint(eventType, stableSubjectId),
            Actor = string.IsNullOrWhiteSpace(actor) ? "workflow-runtime" : actor.Trim(),
            Reason = reason,
            PlanId = job?.PlanId,
            PlanVersion = job?.PlanVersion,
            ExperimentJobId = job?.JobId,
            ScheduleEntryId = schedule?.ScheduleEntryId ?? scheduleEntryId,
            DetailsJson = ExperimentSchedulingPersistence.Serialize(new Dictionary<string, string?>
            {
                ["workflowRunId"] = workflowRunId?.ToString(),
                ["runtimeStatus"] = job?.Status,
                ["releasedLeaseCount"] = releasedLeaseCount.ToString()
            }),
            ResultJson = ExperimentSchedulingPersistence.Serialize(result),
            OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime
        });
    }

    private static WorkflowRuntimeStatus ParseRuntimeStatus(string? value) =>
        Enum.TryParse<WorkflowRuntimeStatus>(value, ignoreCase: true, out var status)
            ? status
            : WorkflowRuntimeStatus.Unknown;

    private static Guid CreateDeterministicRequestId(string eventType, Guid subjectId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{eventType}\u001f{subjectId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string CreateFingerprint(string eventType, Guid subjectId) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{eventType}\u001f{subjectId:N}")));
}
