using System.Text.Json;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Mes.Entities;

namespace MesControlAgv.Mes.Services;

internal static class ExperimentSchedulingPersistence
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, SerializerOptions);

    public static T Deserialize<T>(string? json, T fallback)
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

    public static ExperimentPlan MapPlan(ExperimentPlanRecord record)
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
            Validation = Deserialize<ExperimentPlanValidationResult?>(record.ValidationJson, null),
            ValidatedBy = record.ValidatedBy,
            ValidatedAt = ToOffset(record.ValidatedAtUtc),
            CreatedBy = record.CreatedBy,
            CreatedAt = ToOffset(record.CreatedAtUtc),
            PublishedBy = record.PublishedBy,
            PublishedAt = ToOffset(record.PublishedAtUtc),
            UpdatedAt = ToOffset(record.UpdatedAtUtc)
        };
    }

    public static ExperimentJob MapJob(ExperimentJobRecord record)
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

    public static ScheduleEntry MapScheduleEntry(
        ScheduleEntryRecord record,
        IReadOnlyList<ResourceReservation> reservations) => new()
        {
            ScheduleEntryId = record.ScheduleEntryId,
            ExperimentJobId = record.ExperimentJobId,
            PlannedStart = ToOffset(record.PlannedStartUtc),
            PlannedEnd = ToOffset(record.PlannedEndUtc),
            Priority = record.Priority,
            Status = ParseStatus<ScheduleEntryStatus>(record.Status),
            RequestedResources = Deserialize(
            record.RequestedResourcesJson,
            Array.Empty<ExperimentResourceReference>()),
            BlockingReasons = Deserialize(
            record.BlockingReasonsJson,
            Array.Empty<ScheduleBlockReason>()),
            Reservations = reservations,
            CreatedBy = record.CreatedBy,
            CreatedAt = ToOffset(record.CreatedAtUtc),
            UpdatedAt = ToOffset(record.UpdatedAtUtc)
        };

    public static ResourceReservation MapReservation(ResourceReservationRecord record) => new()
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

    public static ResourceLease MapLease(WorkflowResourceLeaseRecord record) => new()
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

    public static ExperimentSchedulingAuditEntry MapAudit(ExperimentSchedulingAuditRecord record)
    {
        var details = Deserialize(record.DetailsJson, new Dictionary<string, string?>());
        return new ExperimentSchedulingAuditEntry
        {
            Id = record.Id,
            EventType = record.EventType,
            Outcome = record.Outcome,
            Code = record.Code,
            RequestId = record.RequestId,
            Actor = record.Actor,
            Reason = record.Reason,
            PlanId = record.PlanId,
            PlanVersion = record.PlanVersion,
            ExperimentJobId = record.ExperimentJobId,
            ScheduleEntryId = record.ScheduleEntryId,
            Details = new Dictionary<string, string?>(details, StringComparer.OrdinalIgnoreCase),
            OccurredAt = ToOffset(record.OccurredAtUtc)
        };
    }

    public static TStatus ParseStatus<TStatus>(string value)
        where TStatus : struct, Enum =>
        Enum.TryParse<TStatus>(value, ignoreCase: true, out var parsed) ? parsed : default;

    public static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static DateTimeOffset? ToOffset(DateTime? value) =>
        value is null ? null : ToOffset(value.Value);

    public static int GetPeakConcurrentReservationCount(
        IEnumerable<ResourceReservationRecord> reservations,
        DateTime windowStartUtc,
        DateTime windowEndUtc)
    {
        if (windowEndUtc <= windowStartUtc)
            throw new ArgumentException("Reservation window end must be later than its start.");

        var deltas = new SortedDictionary<DateTime, int>();
        foreach (var reservation in reservations)
        {
            var startsAt = reservation.StartsAtUtc < windowStartUtc
                ? windowStartUtc
                : reservation.StartsAtUtc;
            var endsAt = reservation.EndsAtUtc > windowEndUtc
                ? windowEndUtc
                : reservation.EndsAtUtc;
            if (endsAt <= startsAt) continue;

            deltas[startsAt] = deltas.GetValueOrDefault(startsAt) + 1;
            deltas[endsAt] = deltas.GetValueOrDefault(endsAt) - 1;
        }

        var active = 0;
        var peak = 0;
        foreach (var delta in deltas.Values)
        {
            active += delta;
            peak = Math.Max(peak, active);
        }
        return peak;
    }
}
