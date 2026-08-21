using MesControlAgv.Application;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// G5-A read model over the new planning tables. This service never schedules a
/// job, acquires a lease, starts a workflow run, or contacts a device.
/// </summary>
public sealed class ExperimentSchedulingQueryService(
    MesDbContext database,
    TimeProvider timeProvider,
    ExperimentResourceCatalog resourceCatalog) : IExperimentSchedulingQueryService
{
    public async Task<IReadOnlyList<ExperimentPlan>> ListPlansAsync(
        CancellationToken cancellationToken)
    {
        var records = await database.ExperimentPlans
            .AsNoTracking()
            .OrderBy(plan => plan.PlanId)
            .ThenByDescending(plan => plan.Version)
            .ToListAsync(cancellationToken);

        return records
            .GroupBy(plan => plan.PlanId)
            .Select(group => ExperimentSchedulingPersistence.MapPlan(group.First()))
            .OrderBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.PlanId)
            .ToArray();
    }

    public async Task<IReadOnlyList<ExperimentPlan>> ListPlanVersionsAsync(
        Guid planId,
        CancellationToken cancellationToken) =>
        (await database.ExperimentPlans
            .AsNoTracking()
            .Where(plan => plan.PlanId == planId)
            .OrderByDescending(plan => plan.Version)
            .ToListAsync(cancellationToken))
        .Select(ExperimentSchedulingPersistence.MapPlan)
        .ToArray();

    public async Task<ExperimentPlan?> GetPlanAsync(
        Guid planId,
        int version,
        CancellationToken cancellationToken)
    {
        var record = await database.ExperimentPlans
            .AsNoTracking()
            .SingleOrDefaultAsync(
                plan => plan.PlanId == planId && plan.Version == version,
                cancellationToken);
        return record is null ? null : ExperimentSchedulingPersistence.MapPlan(record);
    }

    public async Task<IReadOnlyList<ExperimentJob>> ListJobsAsync(
        ExperimentJobStatus? status,
        CancellationToken cancellationToken)
    {
        var query = database.ExperimentJobs.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(job => job.Status == status.Value.ToString());
        }

        return (await query
                .OrderByDescending(job => job.CreatedAtUtc)
                .ThenBy(job => job.JobId)
                .ToListAsync(cancellationToken))
            .Select(ExperimentSchedulingPersistence.MapJob)
            .ToArray();
    }

    public async Task<ExperimentJob?> GetJobAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var record = await database.ExperimentJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(job => job.JobId == jobId, cancellationToken);
        return record is null ? null : ExperimentSchedulingPersistence.MapJob(record);
    }

    public async Task<ExperimentScheduleSnapshot> GetScheduleAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        if (from is not null && to is not null && to <= from)
            throw new ArgumentException("Schedule range end must be later than its start.", nameof(to));

        var query = database.ScheduleEntries.AsNoTracking();
        if (from is not null)
        {
            var fromUtc = from.Value.UtcDateTime;
            query = query.Where(entry => entry.PlannedEndUtc > fromUtc);
        }
        if (to is not null)
        {
            var toUtc = to.Value.UtcDateTime;
            query = query.Where(entry => entry.PlannedStartUtc < toUtc);
        }

        var entries = await query
            .OrderBy(entry => entry.PlannedStartUtc)
            .ThenByDescending(entry => entry.Priority)
            .ThenBy(entry => entry.ScheduleEntryId)
            .ToListAsync(cancellationToken);
        var entryIds = entries.Select(entry => entry.ScheduleEntryId).ToArray();
        List<ResourceReservationRecord> reservations;
        if (entryIds.Length == 0)
        {
            reservations = [];
        }
        else
        {
            reservations = await database.ResourceReservations
                .AsNoTracking()
                .Where(reservation =>
                    entryIds.Contains(reservation.ScheduleEntryId) &&
                    reservation.Status == ResourceReservationStatus.Planned.ToString())
                .OrderBy(reservation => reservation.StartsAtUtc)
                .ThenBy(reservation => reservation.ResourceKey)
                .ToListAsync(cancellationToken);
        }
        var reservationsByEntry = reservations
            .GroupBy(reservation => reservation.ScheduleEntryId)
            .ToDictionary(group => group.Key, group => group.Select(MapReservation).ToArray());

        var activeLeases = await database.WorkflowResourceLeases
            .AsNoTracking()
            .Where(lease => lease.ActiveResourceKey != null)
            .OrderBy(lease => lease.ResourceType)
            .ThenBy(lease => lease.ResourceId)
            .ThenBy(lease => lease.AcquiredAtUtc)
            .ToListAsync(cancellationToken);

        return new ExperimentScheduleSnapshot
        {
            GeneratedAt = timeProvider.GetUtcNow(),
            Entries = entries
                .Select(entry => MapScheduleEntry(
                    entry,
                    reservationsByEntry.GetValueOrDefault(entry.ScheduleEntryId) ??
                    Array.Empty<ResourceReservation>()))
                .ToArray(),
            ActiveLeases = activeLeases.Select(ExperimentSchedulingPersistence.MapLease).ToArray()
        };
    }

    public async Task<IReadOnlyList<ExperimentResourceAvailability>> ListResourceAvailabilityAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var rangeStart = from ?? timeProvider.GetUtcNow();
        var rangeEnd = to ?? rangeStart.AddHours(24);
        if (rangeEnd <= rangeStart)
            throw new ArgumentException("Availability range end must be later than its start.", nameof(to));

        var startUtc = rangeStart.UtcDateTime;
        var endUtc = rangeEnd.UtcDateTime;
        var reservations = await database.ResourceReservations
            .AsNoTracking()
            .Where(reservation =>
                reservation.Status == ResourceReservationStatus.Planned.ToString() &&
                reservation.EndsAtUtc > startUtc &&
                reservation.StartsAtUtc < endUtc)
            .ToListAsync(cancellationToken);
        var reservationsByResource = reservations
            .GroupBy(reservation => reservation.ResourceKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var activeLeaseKeys = (await database.WorkflowResourceLeases
                .AsNoTracking()
                .Where(lease => lease.ActiveResourceKey != null)
                .Select(lease => lease.ActiveResourceKey!)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        return resourceCatalog.Resources.Select(resource =>
        {
            var resourceReservations = reservationsByResource.GetValueOrDefault(resource.ResourceKey) ?? [];
            var count = ExperimentSchedulingPersistence.GetPeakConcurrentReservationCount(
                resourceReservations,
                startUtc,
                endUtc);
            var hasLease = activeLeaseKeys.Contains(resource.ResourceKey);
            var reasons = new List<ScheduleBlockReason>();
            if (!resource.Enabled)
            {
                reasons.Add(new ScheduleBlockReason
                {
                    Code = ExperimentSchedulingIssueCodes.ResourceDisabled,
                    Message = $"Resource '{resource.Resource.ResourceId}' is disabled by the active Profile.",
                    Resource = resource.Resource
                });
            }
            if (count >= resource.Capacity)
            {
                reasons.Add(new ScheduleBlockReason
                {
                    Code = ExperimentSchedulingIssueCodes.ResourceCapacityInsufficient,
                    Message = $"Resource '{resource.Resource.ResourceId}' has no planning capacity in the requested window.",
                    Resource = resource.Resource
                });
            }
            if (hasLease)
            {
                reasons.Add(new ScheduleBlockReason
                {
                    Code = ExperimentSchedulingIssueCodes.ResourceLeaseActive,
                    Message = $"Resource '{resource.Resource.ResourceId}' currently has an active runtime lease.",
                    Resource = resource.Resource
                });
            }

            return new ExperimentResourceAvailability
            {
                Resource = resource.Resource,
                DisplayName = resource.DisplayName,
                Enabled = resource.Enabled,
                Capacity = resource.Capacity,
                CapabilityIds = resource.CapabilityIds,
                PlannedReservationCount = count,
                AvailableCapacity = Math.Max(0, resource.Capacity - count),
                HasActiveLease = hasLease,
                BlockingReasons = reasons
            };
        }).ToArray();
    }

    public async Task<IReadOnlyList<ExperimentSchedulingAuditEntry>> ListAuditsAsync(
        Guid? planId,
        Guid? experimentJobId,
        Guid? scheduleEntryId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Audit limit must be between 1 and 1000.");

        var query = database.ExperimentSchedulingAudits.AsNoTracking();
        if (planId is not null) query = query.Where(audit => audit.PlanId == planId);
        if (experimentJobId is not null)
            query = query.Where(audit => audit.ExperimentJobId == experimentJobId);
        if (scheduleEntryId is not null)
            query = query.Where(audit => audit.ScheduleEntryId == scheduleEntryId);

        return (await query
                .OrderByDescending(audit => audit.OccurredAtUtc)
                .ThenByDescending(audit => audit.Id)
                .Take(limit)
                .ToListAsync(cancellationToken))
            .Select(ExperimentSchedulingPersistence.MapAudit)
            .ToArray();
    }

    private static ScheduleEntry MapScheduleEntry(
        ScheduleEntryRecord record,
        IReadOnlyList<ResourceReservation> reservations) =>
        ExperimentSchedulingPersistence.MapScheduleEntry(record, reservations);

    private static ResourceReservation MapReservation(ResourceReservationRecord record) =>
        ExperimentSchedulingPersistence.MapReservation(record);
}
