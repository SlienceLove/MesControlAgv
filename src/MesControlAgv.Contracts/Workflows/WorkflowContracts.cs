namespace MesControlAgv.Contracts.Workflows;

/// <summary>
/// The node kinds understood by the first workflow contract slice.
/// The numeric order intentionally matches the existing WPF WorkflowNodeType enum so
/// legacy JSON can be mapped without changing the WPF editor.
/// </summary>
public enum WorkflowNodeType
{
    Start,
    Move,
    Wait,
    Pickup,
    Dropoff,
    End,
    Custom
}

/// <summary>
/// The lifecycle state of an immutable workflow version.
/// A draft may be edited, a validated version is ready for publishing, and a
/// published version is immutable and executable.
/// </summary>
public enum WorkflowVersionStatus
{
    Draft,
    Validated,
    Published,
    Archived
}

/// <summary>
/// Publication state is kept separate from the version lifecycle so an adapter can
/// represent an in-flight or failed publication without pretending the version is published.
/// </summary>
public enum WorkflowPublishStatus
{
    NotPublished,
    Pending,
    Published,
    Failed,
    Superseded,
    Withdrawn
}

/// <summary>Severity of a workflow validation issue.</summary>
public enum WorkflowValidationSeverity
{
    Warning,
    Error
}

/// <summary>
/// A named value carried by a workflow node. Values remain strings at the boundary;
/// the node implementation owns conversion to the declared data type.
/// </summary>
public sealed record WorkflowParameter
{
    public string Name { get; init; } = string.Empty;
    public string? Value { get; init; }
    public string DataType { get; init; } = "string";
    public bool IsRequired { get; init; }
}

/// <summary>
/// A serializable workflow node. Id, Name, Description, TargetStation, X, Y and
/// Order intentionally mirror the existing WPF model. Parameters and NextNodeIds
/// extend that shape without requiring a WPF reference from the contracts assembly.
/// </summary>
public sealed record WorkflowNode
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public WorkflowNodeType Type { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? TargetStation { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public int Order { get; init; }
    public IReadOnlyList<WorkflowParameter> Parameters { get; init; } = Array.Empty<WorkflowParameter>();
    public IReadOnlyList<Guid> NextNodeIds { get; init; } = Array.Empty<Guid>();
}

/// <summary>
/// The editable, version-independent workflow shape. It is deliberately close to
/// the WPF WorkflowDefinition so an application adapter can map between the two
/// models while keeping the contract boundary independent of WPF.
/// </summary>
public sealed record WorkflowDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsPreset { get; init; }
    public IReadOnlyList<WorkflowNode> Nodes { get; init; } = Array.Empty<WorkflowNode>();
    public int? PublishedVersion { get; init; }
}

/// <summary>A validation issue associated with a workflow or one of its nodes.</summary>
public sealed record WorkflowValidationIssue
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public WorkflowValidationSeverity Severity { get; init; } = WorkflowValidationSeverity.Error;
    public Guid? NodeId { get; init; }
    public string? ParameterName { get; init; }
}

/// <summary>
/// Result of validating a draft or version. A result is publishable only when it
/// contains no error-severity issues; warnings are retained for the operator.
/// </summary>
public sealed record WorkflowValidationResult
{
    public bool IsValid => Issues.All(issue => issue.Severity != WorkflowValidationSeverity.Error);
    public bool HasWarnings => Issues.Any(issue => issue.Severity == WorkflowValidationSeverity.Warning);
    public IReadOnlyList<WorkflowValidationIssue> Issues { get; init; } = Array.Empty<WorkflowValidationIssue>();
    public DateTimeOffset ValidatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? ValidatorVersion { get; init; }

    public static WorkflowValidationResult Valid(string? validatorVersion = null) => new()
    {
        ValidatorVersion = validatorVersion
    };
}

/// <summary>
/// An immutable snapshot of a workflow definition at a particular version.
/// Version numbers are scoped to WorkflowId and must not be reused.
/// </summary>
public sealed record WorkflowVersion
{
    public Guid WorkflowId { get; init; }
    public int Version { get; init; }
    public WorkflowDefinition Definition { get; init; } = new();
    public WorkflowVersionStatus Status { get; init; } = WorkflowVersionStatus.Draft;
    public WorkflowPublishStatus PublishStatus { get; init; } = WorkflowPublishStatus.NotPublished;
    public WorkflowValidationResult? Validation { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? ChangeSummary { get; init; }
    public string? PublishedBy { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}

/// <summary>
/// Requests execution of a pinned published version. Callers must provide a
/// positive version; executing an unversioned mutable draft is intentionally not
/// part of this contract.
/// </summary>
public sealed record WorkflowExecutionRequest
{
    public Guid WorkflowId { get; init; }
    public int Version { get; init; }
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public string? RequestedBy { get; init; }
    public string? CorrelationId { get; init; }
    public Guid RequestId { get; init; } = Guid.NewGuid();
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool DryRun { get; init; }
}

/// <summary>The admission outcome returned by the workflow runtime.</summary>
public enum WorkflowExecutionStatus
{
    Rejected,
    Accepted
}

/// <summary>The first workflow step prepared for a later application/device adapter.</summary>
public sealed record WorkflowNextStepRequest
{
    public Guid StepRequestId { get; init; }
    public Guid ExecutionId { get; init; }
    public Guid WorkflowId { get; init; }
    public int Version { get; init; }
    public Guid NodeId { get; init; }
    public WorkflowNodeType NodeType { get; init; }
    public string NodeName { get; init; } = string.Empty;
    public string? TargetStation { get; init; }
    public bool DryRun { get; init; }
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Audit information attached to every workflow admission decision.</summary>
public sealed record WorkflowExecutionAuditEntry
{
    public Guid EventId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string? Code { get; init; }
    public string? Reason { get; init; }
    public Guid RequestId { get; init; }
    public Guid ExecutionId { get; init; }
    public Guid WorkflowId { get; init; }
    public int Version { get; init; }
    public string? RequestedBy { get; init; }
    public string? CorrelationId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string? WorkflowName { get; init; }
    public Guid? NextNodeId { get; init; }
    public IReadOnlyDictionary<string, string?> Details { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The stable HTTP result for dry-run and execution admission. A rejected result
/// carries a stable code and validation issues; an accepted result may include
/// the first step request, but never performs device I/O itself.
/// </summary>
public sealed record WorkflowExecutionResult
{
    public WorkflowExecutionStatus Status { get; init; }
    public bool IsAccepted => Status == WorkflowExecutionStatus.Accepted;
    public bool IsRejected => Status == WorkflowExecutionStatus.Rejected;
    public bool IsIdempotentReplay { get; init; }
    public Guid RequestId { get; init; }
    public Guid ExecutionId { get; init; }
    public Guid WorkflowId { get; init; }
    public int Version { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public bool DryRun { get; init; }
    public string? RejectionCode { get; init; }
    public string? RejectionReason { get; init; }
    public IReadOnlyList<WorkflowValidationIssue> ValidationIssues { get; init; } =
        Array.Empty<WorkflowValidationIssue>();
    public WorkflowNextStepRequest? NextStepRequest { get; init; }
    public WorkflowNextStepRequest? NextStep => NextStepRequest;
    public bool HasNextStep => NextStepRequest is not null;
    public WorkflowExecutionAuditEntry Audit { get; init; } = new();
}

/// <summary>
/// A persisted workflow lifecycle or execution audit entry exposed for
/// operational traceability. Details remain string-valued at the HTTP boundary
/// so older records and vendor-specific metadata can be displayed safely.
/// </summary>
public sealed record WorkflowAuditResponse
{
    public Guid Id { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string? Code { get; init; }
    public string? Reason { get; init; }
    public Guid WorkflowId { get; init; }
    public int Version { get; init; }
    public Guid? RequestId { get; init; }
    public Guid? ExecutionId { get; init; }
    public string? Actor { get; init; }
    public string? CorrelationId { get; init; }
    public IReadOnlyDictionary<string, string?> Details { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset OccurredAt { get; init; }
}
