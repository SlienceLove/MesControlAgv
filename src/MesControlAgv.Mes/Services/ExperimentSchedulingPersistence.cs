using System.Security.Cryptography;
using System.Text;
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
        var workflowSteps = NormalizeWorkflowSteps(
            Deserialize(record.WorkflowStepsJson, Array.Empty<ExperimentPlanWorkflowStep>()),
            record.WorkflowId,
            record.WorkflowVersion,
            record.PlanId);
        return new ExperimentPlan
        {
            PlanId = record.PlanId,
            Version = record.Version,
            Name = record.Name,
            Description = record.Description,
            WorkflowId = record.WorkflowId,
            WorkflowVersion = record.WorkflowVersion,
            WorkflowSteps = workflowSteps,
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
        var workflowSteps = NormalizeWorkflowSteps(
            Deserialize(record.WorkflowStepsJson, Array.Empty<ExperimentPlanWorkflowStep>()),
            record.WorkflowId,
            record.WorkflowVersion,
            record.JobId);
        return new ExperimentJob
        {
            JobId = record.JobId,
            PlanId = record.PlanId,
            PlanVersion = record.PlanVersion,
            WorkflowId = record.WorkflowId,
            WorkflowVersion = record.WorkflowVersion,
            WorkflowSteps = workflowSteps,
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

    public static IReadOnlyList<ExperimentPlanWorkflowStep> NormalizeWorkflowSteps(
        IReadOnlyList<ExperimentPlanWorkflowStep>? steps,
        Guid legacyWorkflowId,
        int legacyWorkflowVersion,
        Guid fallbackStepId)
    {
        var source = (steps ?? [])
            .Where(step => step is not null)
            .ToList();
        if (source.Count == 0 && legacyWorkflowId != Guid.Empty && legacyWorkflowVersion > 0)
        {
            source.Add(new ExperimentPlanWorkflowStep
            {
                StepId = fallbackStepId,
                Order = 1,
                WorkflowId = legacyWorkflowId,
                WorkflowVersion = legacyWorkflowVersion
            });
        }

        return source
            .OrderBy(step => step.Order <= 0 ? int.MaxValue : step.Order)
            .ThenBy(step => step.StepId)
            .Select((step, index) =>
            {
                var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var parameter in (step.Parameters ?? new Dictionary<string, string?>())
                             .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(item => item.Key, StringComparer.Ordinal))
                {
                    var name = parameter.Key?.Trim() ?? string.Empty;
                    parameters[name] = parameter.Value;
                }

                return new ExperimentPlanWorkflowStep
                {
                    StepId = step.StepId == Guid.Empty
                        ? CreateDeterministicStepId(fallbackStepId, step, index)
                        : step.StepId,
                    Order = index + 1,
                    WorkflowId = step.WorkflowId,
                    WorkflowVersion = step.WorkflowVersion,
                    Name = step.Name?.Trim() ?? string.Empty,
                    EstimatedDurationMinutes = Math.Max(0, step.EstimatedDurationMinutes),
                    Parameters = parameters
                };
            })
            .ToArray();
    }

    private static Guid CreateDeterministicStepId(
        Guid fallbackStepId,
        ExperimentPlanWorkflowStep step,
        int index)
    {
        // A missing StepId is common when older clients send a composed draft.
        // Derive it from the persisted owner, ordered position and immutable
        // workflow reference so retries and read projections do not mutate the
        // request fingerprint or create a new identity on every read.
        var seed = string.Join(
            "|",
            fallbackStepId.ToString("N"),
            index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            step.Order.ToString(System.Globalization.CultureInfo.InvariantCulture),
            step.WorkflowId.ToString("N"),
            step.WorkflowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, bytes.Length).CopyTo(bytes);
        // Mark the value as a UUID v5-style name-derived identifier while
        // keeping the implementation independent of a particular UUID package.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

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
