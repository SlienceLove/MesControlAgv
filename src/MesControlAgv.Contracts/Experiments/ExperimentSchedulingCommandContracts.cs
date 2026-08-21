namespace MesControlAgv.Contracts.Experiments;

public static class ExperimentSchedulingIssueCodes
{
    public const string AdmissionRequestIdReused = "EXP-ADMISSION-REQUEST-ID-REUSED";
    public const string JobNotScheduled = "EXP-JOB-NOT-SCHEDULED";
    public const string ScheduleNotReady = "EXP-SCHEDULE-NOT-READY";
    public const string PlanVersionNotPublished = "EXP-PLAN-VERSION-NOT-PUBLISHED";
    public const string ReservationMismatch = "EXP-RESERVATION-MISMATCH";
    public const string WorkflowAdmissionRejected = "EXP-WORKFLOW-ADMISSION-REJECTED";
    public const string RuntimeRecoveryFailed = "EXP-RUNTIME-RECOVERY-FAILED";
    public const string PlanNameRequired = "EXP-PLAN-NAME-REQUIRED";
    public const string WorkflowReferenceRequired = "EXP-WORKFLOW-REFERENCE-REQUIRED";
    public const string WorkflowVersionNotFound = "EXP-WORKFLOW-VERSION-NOT-FOUND";
    public const string WorkflowVersionNotPublished = "EXP-WORKFLOW-VERSION-NOT-PUBLISHED";
    public const string ProfileMismatch = "EXP-PROFILE-MISMATCH";
    public const string MaterialInvalid = "EXP-MATERIAL-INVALID";
    public const string ParameterNameInvalid = "EXP-PARAMETER-NAME-INVALID";
    public const string ResourceRequirementInvalid = "EXP-RESOURCE-REQUIREMENT-INVALID";
    public const string ResourceNotConfigured = "EXP-RESOURCE-NOT-CONFIGURED";
    public const string ResourceDisabled = "EXP-RESOURCE-DISABLED";
    public const string ResourceCapabilityMissing = "EXP-RESOURCE-CAPABILITY-MISSING";
    public const string ResourceSelectionMissing = "EXP-RESOURCE-SELECTION-MISSING";
    public const string ResourceCapacityInsufficient = "EXP-RESOURCE-CAPACITY-INSUFFICIENT";
    public const string ResourceReservationConflict = "EXP-RESOURCE-RESERVATION-CONFLICT";
    public const string ResourceLeaseActive = "EXP-RESOURCE-LEASE-ACTIVE";
}

public enum ExperimentPlanValidationSeverity
{
    Warning,
    Error
}

public sealed record ExperimentPlanValidationIssue
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public ExperimentPlanValidationSeverity Severity { get; init; } =
        ExperimentPlanValidationSeverity.Error;
    public string? Field { get; init; }
    public ExperimentResourceReference? Resource { get; init; }
}

public sealed record ExperimentPlanValidationResult
{
    public bool IsValid => Issues.All(issue =>
        issue.Severity != ExperimentPlanValidationSeverity.Error);
    public IReadOnlyList<ExperimentPlanValidationIssue> Issues { get; init; } =
        Array.Empty<ExperimentPlanValidationIssue>();
    public string ValidatorVersion { get; init; } = string.Empty;
    public DateTimeOffset ValidatedAt { get; init; }
    public string ValidatedBy { get; init; } = string.Empty;
}

/// <summary>Editable fields for one experiment-plan draft version.</summary>
public sealed record ExperimentPlanDraft
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public Guid WorkflowId { get; init; }
    public int WorkflowVersion { get; init; }
    public IReadOnlyList<ExperimentMaterialRequirement> MaterialRequirements { get; init; } =
        Array.Empty<ExperimentMaterialRequirement>();
    public IReadOnlyDictionary<string, string?> DefaultParameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<ExperimentResourceRequirement> ResourceRequirements { get; init; } =
        Array.Empty<ExperimentResourceRequirement>();
    public string? ProfileProductId { get; init; }
    public string? ProfileVersion { get; init; }
    public string? LayoutId { get; init; }
}

/// <summary>Creates or replaces editable fields on a draft plan version.</summary>
public sealed record SaveExperimentPlanDraftRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public ExperimentPlanDraft Draft { get; init; } = new();
}

/// <summary>Metadata required by a plan, job, or schedule lifecycle action.</summary>
public sealed record ExperimentSchedulingActionRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed record CreateExperimentJobRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public Guid PlanId { get; init; }
    public int PlanVersion { get; init; }
    public string SampleBatchId { get; init; } = string.Empty;
    public string? SampleId { get; init; }
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Manual placement of a job into a concrete time and resource window.</summary>
public sealed record ScheduleExperimentJobRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset PlannedStart { get; init; }
    public DateTimeOffset PlannedEnd { get; init; }
    public int Priority { get; init; }
    public IReadOnlyList<ExperimentResourceReference> Resources { get; init; } =
        Array.Empty<ExperimentResourceReference>();
}

/// <summary>
/// Admits one manually scheduled job into its pinned workflow version. Admission
/// creates no device operation and requires explicit operator evidence.
/// </summary>
public sealed record AdmitExperimentJobRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public enum ExperimentJobAdmissionStatus
{
    Rejected,
    Admitted
}

/// <summary>
/// Durable outcome of scheduled-job admission. A rejected outcome never leaves
/// a workflow run or runtime lease behind.
/// </summary>
public sealed record ExperimentJobAdmissionResult
{
    public Guid RequestId { get; init; }
    public ExperimentJobAdmissionStatus Status { get; init; }
    public bool IsAdmitted => Status == ExperimentJobAdmissionStatus.Admitted;
    public bool IsRejected => Status == ExperimentJobAdmissionStatus.Rejected;
    public bool IsIdempotentReplay { get; init; }
    public Guid? WorkflowRunId { get; init; }
    public ExperimentJob? Job { get; init; }
    public ScheduleEntry? ScheduleEntry { get; init; }
    public IReadOnlyList<ResourceLease> Leases { get; init; } = Array.Empty<ResourceLease>();
    public string? RejectionCode { get; init; }
    public string? RejectionReason { get; init; }
    public IReadOnlyList<ExperimentResourceReference> ConflictingResources { get; init; } =
        Array.Empty<ExperimentResourceReference>();
}

/// <summary>Profile and persisted-load projection for one planning window.</summary>
public sealed record ExperimentResourceAvailability
{
    public ExperimentResourceReference Resource { get; init; } = new();
    public string DisplayName { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public int Capacity { get; init; } = 1;
    public IReadOnlyList<string> CapabilityIds { get; init; } = Array.Empty<string>();
    public int PlannedReservationCount { get; init; }
    public int AvailableCapacity { get; init; }
    public bool HasActiveLease { get; init; }
    public bool IsAvailable => Enabled && AvailableCapacity > 0 && !HasActiveLease;
    public IReadOnlyList<ScheduleBlockReason> BlockingReasons { get; init; } =
        Array.Empty<ScheduleBlockReason>();
}

/// <summary>Append-only audit projection for plan, job, and schedule changes.</summary>
public sealed record ExperimentSchedulingAuditEntry
{
    public Guid Id { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string? Code { get; init; }
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public Guid? PlanId { get; init; }
    public int? PlanVersion { get; init; }
    public Guid? ExperimentJobId { get; init; }
    public Guid? ScheduleEntryId { get; init; }
    public IReadOnlyDictionary<string, string?> Details { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset OccurredAt { get; init; }
}
