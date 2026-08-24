namespace MesControlAgv.Contracts.Workflows;

public static class WorkflowRunControlPermissions
{
    public const string Pause = "workflow.pause";
    public const string Cancel = "workflow.cancel";
    public const string ResolveUnknown = "workflow.resolve-unknown";
    public const string SubmitSignal = "workflow.submit-signal";
    public const string CompleteManualTask = "workflow.complete-manual-task";
}

public enum WorkflowRunControlAction
{
    Pause,
    Resume,
    Cancel,
    ResolveUnknown
}

public enum WorkflowUnknownResolutionOutcome
{
    ConfirmedSucceeded,
    ConfirmedFailed
}

/// <summary>
/// Operator request for pause, resume, or cancel. Permissions are deliberately
/// absent: MES resolves them from its trusted operator configuration.
/// </summary>
public sealed record WorkflowRunControlRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Explicit human reconciliation of one Unknown node attempt. This contract
/// records an observed conclusion and never requests a device retry.
/// </summary>
public sealed record WorkflowUnknownResolutionRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public Guid NodeExecutionId { get; init; }
    public WorkflowUnknownResolutionOutcome Outcome { get; init; }
}

public sealed record WorkflowRunControlResult
{
    public Guid RequestId { get; init; }
    public Guid WorkflowRunId { get; init; }
    public WorkflowRunControlAction Action { get; init; }
    public bool IsIdempotentReplay { get; init; }
    public WorkflowExecutionSnapshot Run { get; init; } = new();
}

public sealed record WorkflowRunControlPermissionsSnapshot
{
    public string Actor { get; init; } = string.Empty;
    public IReadOnlyList<string> Permissions { get; init; } = [];
}
