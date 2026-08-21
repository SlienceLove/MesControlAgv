using System.Text.Json;
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
    TimeProvider timeProvider) : IExperimentSchedulingQueryService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

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
            .Select(group => MapPlan(group.First()))
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
        .Select(MapPlan)
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
        return record is null ? null : MapPlan(record);
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
            .Select(MapJob)
            .ToArray();
    }

    public async Task<ExperimentJob?> GetJobAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var record = await database.ExperimentJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(job => job.JobId == jobId, cancellationToken);
        return record is null ? null : MapJob(record);
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
                .Where(reservation => entryIds.Contains(reservation.ScheduleEntryId))
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
            ActiveLeases = activeLeases.Select(MapLease).ToArray()
        };
    }

    private static ExperimentPlan MapPlan(ExperimentPlanRecord record)
    {
        var parameters = Deserialize(
            record.DefaultParametersJson,
            new Dictionary<string, string?>());
        return new ExperimentPlan
        {
            PlanId = record.PlanId,
            Version = record.Version,
            Name = record.Name,
            Description = record.Description,
            WorkflowId = record.WorkflowId,
            WorkflowVersion = record.WorkflowVersion,
            Status = ParseStatus<ExperimentPlanStatus>(record.Status),
            MaterialRequirements = Deserialize(
                record.MaterialRequirementsJson,
                Array.Empty<ExperimentMaterialRequirement>()),
            DefaultParameters = new Dictionary<string, string?>(
                parameters,
                StringComparer.OrdinalIgnoreCase),
            ResourceRequirements = Deserialize(
                record.ResourceRequirementsJson,
                Array.Empty<ExperimentResourceRequirement>()),
            ProfileProductId = record.ProfileProductId,
            ProfileVersion = record.ProfileVersion,
            LayoutId = record.LayoutId,
            CreatedBy = record.CreatedBy,
            CreatedAt = ToOffset(record.CreatedAtUtc),
            PublishedBy = record.PublishedBy,
            PublishedAt = ToOffset(record.PublishedAtUtc),
            UpdatedAt = ToOffset(record.UpdatedAtUtc)
        };
    }

    private static ExperimentJob MapJob(ExperimentJobRecord record)
    {
        var parameters = Deserialize(record.ParametersJson, new Dictionary<string, string?>());
        return new ExperimentJob
        {
            JobId = record.JobId,
            PlanId = record.PlanId,
            PlanVersion = record.PlanVersion,
            WorkflowId = record.WorkflowId,
            WorkflowVersion = record.WorkflowVersion,
            SampleBatchId = record.SampleBatchId,
            SampleId = record.SampleId,
            Parameters = new Dictionary<string, string?>(parameters, StringComparer.OrdinalIgnoreCase),
            Status = ParseStatus<ExperimentJobStatus>(record.Status),
            WorkflowRunId = record.WorkflowRunId,
            CreatedBy = record.CreatedBy,
            CreatedAt = ToOffset(record.CreatedAtUtc),
            UpdatedAt = ToOffset(record.UpdatedAtUtc),
            StartedAt = ToOffset(record.StartedAtUtc),
            CompletedAt = ToOffset(record.CompletedAtUtc),
            LastError = record.LastError
        };
    }

    private static ScheduleEntry MapScheduleEntry(
        ScheduleEntryRecord record,
        IReadOnlyList<ResourceReservation> reservations) => new()
    {
        ScheduleEntryId = record.ScheduleEntryId,
        ExperimentJobId = record.ExperimentJobId,
        PlannedStart = ToOffset(record.PlannedStartUtc),
        PlannedEnd = ToOffset(record.PlannedEndUtc),
        Priority = record.Priority,
        Status = ParseStatus<ScheduleEntryStatus>(record.Status),
        BlockingReasons = Deserialize(
            record.BlockingReasonsJson,
            Array.Empty<ScheduleBlockReason>()),
        Reservations = reservations,
        CreatedBy = record.CreatedBy,
        CreatedAt = ToOffset(record.CreatedAtUtc),
        UpdatedAt = ToOffset(record.UpdatedAtUtc)
    };

    private static ResourceReservation MapReservation(ResourceReservationRecord record) => new()
    {
        ReservationId = record.ReservationId,
        ScheduleEntryId = record.ScheduleEntryId,
        Resource = new ExperimentResourceReference
        {
            ResourceType = record.ResourceType,
            ResourceId = record.ResourceId
        },
        StartsAt = ToOffset(record.StartsAtUtc),
        EndsAt = ToOffset(record.EndsAtUtc),
        Status = ParseStatus<ResourceReservationStatus>(record.Status),
        CreatedAt = ToOffset(record.CreatedAtUtc),
        UpdatedAt = ToOffset(record.UpdatedAtUtc)
    };

    private static ResourceLease MapLease(WorkflowResourceLeaseRecord record) => new()
    {
        LeaseId = record.LeaseId,
        ScheduleEntryId = record.ScheduleEntryId,
        WorkflowRunId = record.WorkflowRunId,
        NodeExecutionId = record.NodeExecutionId,
        Resource = new ExperimentResourceReference
        {
            ResourceType = record.ResourceType,
            ResourceId = record.ResourceId
        },
        Status = ParseStatus<ResourceLeaseStatus>(record.Status),
        AcquiredBy = record.AcquiredBy,
        AcquiredAt = ToOffset(record.AcquiredAtUtc),
        ExpiresAt = ToOffset(record.ExpiresAtUtc),
        ReleasedBy = record.ReleasedBy,
        ReleaseReason = record.ReleaseReason,
        ReleasedAt = ToOffset(record.ReleasedAtUtc),
        UpdatedAt = ToOffset(record.UpdatedAtUtc)
    };

    private static T Deserialize<T>(string? json, T fallback)
    {
        if (string.IsNullOrWhiteSpace(json)) return fallback;
        try
        {
            return JsonSerializer.Deserialize<T>(json, SerializerOptions) ?? fallback;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Persisted experiment scheduling JSON is invalid.", exception);
        }
    }

    private static TStatus ParseStatus<TStatus>(string value)
        where TStatus : struct, Enum =>
        Enum.TryParse<TStatus>(value, ignoreCase: true, out var parsed) ? parsed : default;

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is null ? null : ToOffset(value.Value);
}
