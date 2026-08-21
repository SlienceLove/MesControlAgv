namespace MesControlAgv.Contracts.Experiments;

/// <summary>Stable resource type identifiers shared by planning and runtime leases.</summary>
public static class ExperimentResourceTypeIds
{
    public const string Agv = "agv";
    public const string RouteSegment = "route-segment";
    public const string Station = "station";
    public const string RobotArm = "robot-arm";
    public const string Instrument = "instrument";
    public const string Workstation = "workstation";
    public const string Carrier = "carrier";
    public const string OperatorStation = "operator-station";
}

/// <summary>Creates a case-insensitive identity used by database uniqueness constraints.</summary>
public static class ExperimentResourceKeys
{
    public static string Create(string resourceType, string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceType))
            throw new ArgumentException("A resource type is required.", nameof(resourceType));
        if (string.IsNullOrWhiteSpace(resourceId))
            throw new ArgumentException("A resource id is required.", nameof(resourceId));

        return $"{resourceType.Trim().ToUpperInvariant()}\u001f{resourceId.Trim().ToUpperInvariant()}";
    }
}

public enum ExperimentPlanStatus
{
    Unknown,
    Draft,
    Validated,
    Published,
    Archived
}

public enum ExperimentJobStatus
{
    Unknown,
    Draft,
    Ready,
    Scheduled,
    Admitted,
    Running,
    Blocked,
    Cancelled,
    Completed,
    Failed
}

public enum ScheduleEntryStatus
{
    Unknown,
    Draft,
    Scheduled,
    Blocked,
    Admitted,
    Cancelled,
    Completed
}

public enum ResourceReservationStatus
{
    Unknown,
    Planned,
    Released,
    Cancelled
}

public enum ResourceLeaseStatus
{
    Unknown,
    Active,
    Released,
    Expired
}

/// <summary>A protocol-neutral resource identity.</summary>
public sealed record ExperimentResourceReference
{
    public string ResourceType { get; init; } = string.Empty;
    public string ResourceId { get; init; } = string.Empty;
}

public sealed record ExperimentMaterialRequirement
{
    public string MaterialId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public decimal? Quantity { get; init; }
    public string? Unit { get; init; }
    public string? Specification { get; init; }
}

/// <summary>
/// A plan may request a specific resource id or any resource that provides the
/// declared capability. Resolution to a concrete resource belongs to scheduling.
/// </summary>
public sealed record ExperimentResourceRequirement
{
    public string ResourceType { get; init; } = string.Empty;
    public string? ResourceId { get; init; }
    public string? CapabilityId { get; init; }
    public int Quantity { get; init; } = 1;
    public bool Exclusive { get; init; } = true;
}

/// <summary>
/// Versioned description of what an experiment does. WorkflowId and
/// WorkflowVersion always identify an immutable workflow version.
/// </summary>
public sealed record ExperimentPlan
{
    public Guid PlanId { get; init; }
    public int Version { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public Guid WorkflowId { get; init; }
    public int WorkflowVersion { get; init; }
    public ExperimentPlanStatus Status { get; init; }
    public IReadOnlyList<ExperimentMaterialRequirement> MaterialRequirements { get; init; } =
        Array.Empty<ExperimentMaterialRequirement>();
    public IReadOnlyDictionary<string, string?> DefaultParameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<ExperimentResourceRequirement> ResourceRequirements { get; init; } =
        Array.Empty<ExperimentResourceRequirement>();
    public string? ProfileProductId { get; init; }
    public string? ProfileVersion { get; init; }
    public string? LayoutId { get; init; }
    public ExperimentPlanValidationResult? Validation { get; init; }
    public string? ValidatedBy { get; init; }
    public DateTimeOffset? ValidatedAt { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public string? PublishedBy { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// One experiment request. It pins both the plan version and the workflow
/// version so later plan edits cannot change an admitted run.
/// </summary>
public sealed record ExperimentJob
{
    public Guid JobId { get; init; }
    public Guid PlanId { get; init; }
    public int PlanVersion { get; init; }
    public Guid WorkflowId { get; init; }
    public int WorkflowVersion { get; init; }
    public string SampleBatchId { get; init; } = string.Empty;
    public string? SampleId { get; init; }
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public ExperimentJobStatus Status { get; init; }
    public Guid? WorkflowRunId { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? LastError { get; init; }
}

public sealed record ScheduleBlockReason
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public ExperimentResourceReference? Resource { get; init; }
    public IReadOnlyList<Guid> ConflictingScheduleEntryIds { get; init; } = Array.Empty<Guid>();
}

/// <summary>
/// Mutable planning-window reservation. It is not proof that a workflow run
/// owns the resource and must never be treated as a runtime lease.
/// </summary>
public sealed record ResourceReservation
{
    public Guid ReservationId { get; init; }
    public Guid ScheduleEntryId { get; init; }
    public ExperimentResourceReference Resource { get; init; } = new();
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
    public ResourceReservationStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>When an experiment is planned; it never advances workflow nodes.</summary>
public sealed record ScheduleEntry
{
    public Guid ScheduleEntryId { get; init; }
    public Guid ExperimentJobId { get; init; }
    public DateTimeOffset PlannedStart { get; init; }
    public DateTimeOffset PlannedEnd { get; init; }
    public int Priority { get; init; }
    public ScheduleEntryStatus Status { get; init; }
    public IReadOnlyList<ExperimentResourceReference> RequestedResources { get; init; } =
        Array.Empty<ExperimentResourceReference>();
    public IReadOnlyList<ScheduleBlockReason> BlockingReasons { get; init; } =
        Array.Empty<ScheduleBlockReason>();
    public IReadOnlyList<ResourceReservation> Reservations { get; init; } =
        Array.Empty<ResourceReservation>();
    public string CreatedBy { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Actual runtime ownership of one mutually exclusive resource. An active lease
/// is distinct from a schedule reservation and is always tied to a workflow run.
/// </summary>
public sealed record ResourceLease
{
    public Guid LeaseId { get; init; }
    public Guid? ScheduleEntryId { get; init; }
    public Guid WorkflowRunId { get; init; }
    public Guid? NodeExecutionId { get; init; }
    public ExperimentResourceReference Resource { get; init; } = new();
    public ResourceLeaseStatus Status { get; init; }
    public string AcquiredBy { get; init; } = string.Empty;
    public DateTimeOffset AcquiredAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string? ReleasedBy { get; init; }
    public string? ReleaseReason { get; init; }
    public DateTimeOffset? ReleasedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Read-only projection used by the scheduling board.</summary>
public sealed record ExperimentScheduleSnapshot
{
    public DateTimeOffset GeneratedAt { get; init; }
    public IReadOnlyList<ScheduleEntry> Entries { get; init; } = Array.Empty<ScheduleEntry>();
    public IReadOnlyList<ResourceLease> ActiveLeases { get; init; } = Array.Empty<ResourceLease>();
}
