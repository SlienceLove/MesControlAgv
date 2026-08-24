namespace MesControlAgv.Contracts.Workflows;

/// <summary>Durable lifecycle of one node attempt within a pinned workflow run.</summary>
public enum WorkflowNodeExecutionStatus
{
    Pending,
    Ready,
    WaitingForResource,
    Claimed,
    Running,
    WaitingForSignal,
    Succeeded,
    Failed,
    TimedOut,
    Unknown,
    Blocked,
    Cancelled,
    Skipped
}

/// <summary>Normalized lifecycle of an external device operation.</summary>
public enum WorkflowDeviceOperationStatus
{
    Prepared,
    Accepted,
    Running,
    Succeeded,
    Rejected,
    Failed,
    Cancelled,
    Unknown
}

/// <summary>
/// Read-only record for one node attempt. Inputs and outputs are whitelisted
/// string summaries so monitoring clients do not receive vendor payloads.
/// </summary>
public sealed record WorkflowNodeExecutionSnapshot
{
    public Guid Id { get; init; }
    public Guid WorkflowRunId { get; init; }
    public Guid WorkflowId { get; init; }
    public int Version { get; init; }
    public Guid StepRequestId { get; init; }
    public Guid NodeId { get; init; }
    public string NodeTypeId { get; init; } = string.Empty;
    public string NodeName { get; init; } = string.Empty;
    public int Attempt { get; init; }
    public WorkflowNodeExecutionStatus Status { get; init; }
    public IReadOnlyDictionary<string, string?> Inputs { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string?> Outputs { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Read-only evidence for one device operation. OperationId is also the stable
/// Adapter idempotency identifier used by the current Simulator bridge.
/// </summary>
public sealed record WorkflowDeviceOperationSnapshot
{
    public Guid OperationId { get; init; }
    public Guid WorkflowRunId { get; init; }
    public Guid NodeExecutionId { get; init; }
    public Guid RequestId { get; init; }
    public int Attempt { get; init; }
    public string CapabilityId { get; init; } = string.Empty;
    public string? DeviceId { get; init; }
    public string IdempotencyKey { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
    public WorkflowDeviceOperationStatus Status { get; init; }
    public IReadOnlyDictionary<string, string?> RequestSummary { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string?> ResultSummary { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? ReconciledAt { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>One chronological, read-only workflow-run event.</summary>
public sealed record WorkflowRunTimelineEntry
{
    public Guid Id { get; init; }
    public Guid WorkflowRunId { get; init; }
    public Guid? NodeExecutionId { get; init; }
    public Guid? DeviceOperationId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string? Code { get; init; }
    public string? Reason { get; init; }
    public string? Actor { get; init; }
    public string? CorrelationId { get; init; }
    public IReadOnlyDictionary<string, string?> Details { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// Durable work item consumed by trusted runtime workers. The node execution is
/// the orchestration source; a device operation is present only for nodes that
/// actually cross a device boundary.
/// </summary>
public sealed record WorkflowNodeExecutionWorkItem
{
    public WorkflowNodeExecutionSnapshot NodeExecution { get; init; } = new();
    public WorkflowDeviceOperationSnapshot? DeviceOperation { get; init; }
}

/// <summary>
/// Normalized completion evidence for a node work item. Timed waits leave
/// DeviceOperationId null because they do not perform device I/O.
/// </summary>
public sealed record WorkflowNodeExecutionCompletionRequest
{
    public Guid? DeviceOperationId { get; init; }
    public WorkflowStepCompletionOutcome Outcome { get; init; }
    public string? Error { get; init; }
    public IReadOnlyDictionary<string, string?> Outputs { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}
