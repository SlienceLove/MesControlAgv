namespace MesControlAgv.Contracts.Workflows;

public static class WorkflowAdvancedRuntimeCodes
{
    public const string RequestIdReused = "WORKFLOW_INTERACTION_REQUEST_ID_REUSED";
    public const string RunNotActive = "WORKFLOW_INTERACTION_RUN_NOT_ACTIVE";
    public const string ManualTaskNotWaiting = "WORKFLOW_MANUAL_TASK_NOT_WAITING";
    public const string ManualCommentRequired = "WORKFLOW_MANUAL_COMMENT_REQUIRED";
    public const string SignalCorrelationMissing = "WORKFLOW_SIGNAL_CORRELATION_MISSING";
    public const string ConditionValueMissing = "WORKFLOW_CONDITION_VALUE_MISSING";
    public const string OutcomePathUnavailable = "WORKFLOW_OUTCOME_PATH_UNAVAILABLE";
}

public enum WorkflowRuntimeInteractionType
{
    ExternalSignal,
    ManualConfirmation
}

public enum WorkflowRuntimeInteractionStatus
{
    Pending,
    Applied
}

public enum WorkflowManualConfirmationOutcome
{
    Confirmed,
    Cancelled
}

/// <summary>
/// One idempotent external signal addressed to a pinned workflow run. Data is a
/// string-only, whitelisted summary; arbitrary vendor payloads are not accepted.
/// </summary>
public sealed record WorkflowExternalSignalRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string SignalName { get; init; } = string.Empty;
    public string CorrelationValue { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string?> Data { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Audited operator outcome for one waiting Manual Confirmation node.</summary>
public sealed record WorkflowManualConfirmationRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public WorkflowManualConfirmationOutcome Outcome { get; init; }
    public string? Comment { get; init; }
}

public sealed record WorkflowRuntimeInteractionResult
{
    public Guid RequestId { get; init; }
    public Guid WorkflowRunId { get; init; }
    public Guid? NodeExecutionId { get; init; }
    public WorkflowRuntimeInteractionType InteractionType { get; init; }
    public WorkflowRuntimeInteractionStatus Status { get; init; }
    public bool IsIdempotentReplay { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public DateTimeOffset? AppliedAt { get; init; }
    public WorkflowExecutionSnapshot Run { get; init; } = new();
}

/// <summary>Read-only durable interaction evidence for monitoring and recovery.</summary>
public sealed record WorkflowRuntimeInteractionSnapshot
{
    public Guid RequestId { get; init; }
    public Guid WorkflowRunId { get; init; }
    public Guid? NodeExecutionId { get; init; }
    public WorkflowRuntimeInteractionType InteractionType { get; init; }
    public WorkflowRuntimeInteractionStatus Status { get; init; }
    public string? SignalName { get; init; }
    public string? CorrelationValue { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string?> Data { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset ReceivedAt { get; init; }
    public DateTimeOffset? AppliedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
