using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Contracts.Experiments;

/// <summary>
/// Lifecycle of the outer experiment run. The outer run is deliberately
/// separate from the workflow execution created for each pinned step.
/// </summary>
public enum ExperimentRunStatus
{
    Prepared,
    Running,
    Paused,
    Completed,
    Failed,
    Unknown,
    Cancelled
}

/// <summary>Durable state of one immutable workflow step inside an experiment run.</summary>
public enum ExperimentStepRunStatus
{
    Pending,
    Ready,
    Running,
    Succeeded,
    Failed,
    Unknown,
    Cancelled
}

/// <summary>Result of one simulator composite-runtime reconciliation pass.</summary>
public sealed record ExperimentCompositeRuntimeProcessSummary(
    int Scanned,
    int Changed,
    int Unknown);

/// <summary>
/// Snapshot of one plan step. WorkflowRunId is the child workflow execution
/// created only when this step is started; it remains null while pending.
/// </summary>
public sealed record ExperimentStepRun
{
    public Guid StepRunId { get; init; }
    public Guid ExperimentRunId { get; init; }
    public Guid StepId { get; init; }
    public int Order { get; init; }
    public Guid WorkflowId { get; init; }
    public int WorkflowVersion { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>
    /// Parameters pinned for this step when the outer run is prepared. Keeping
    /// them in the run snapshot prevents a later plan/job edit from changing a
    /// child workflow request after restart.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public ExperimentStepRunStatus Status { get; init; } = ExperimentStepRunStatus.Pending;
    public int Attempt { get; init; } = 1;
    public Guid? WorkflowRunId { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? LastError { get; init; }
}

/// <summary>
/// Restart-safe outer experiment runtime snapshot. It contains only plan/step
/// state and child workflow identities; it never implies a device command.
/// </summary>
public sealed record ExperimentRun
{
    public Guid ExperimentRunId { get; init; }
    public Guid ExperimentJobId { get; init; }
    public Guid PlanId { get; init; }
    public int PlanVersion { get; init; }
    public Guid AdmissionRequestId { get; init; }
    public ExperimentRunStatus Status { get; init; } = ExperimentRunStatus.Prepared;
    public int CurrentStepOrder { get; init; }
    public Guid? CurrentStepRunId { get; init; }
    public IReadOnlyList<ExperimentStepRun> Steps { get; init; } = Array.Empty<ExperimentStepRun>();
    public string? LastError { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public bool IsTerminal => Status is
        ExperimentRunStatus.Completed or
        ExperimentRunStatus.Failed or
        ExperimentRunStatus.Cancelled;
}

/// <summary>
/// Explicit request to materialize a scheduled multi-step plan into an outer
/// run snapshot. Preparation is device-free; a later coordinator owns child
/// workflow admission and device-operation evidence.
/// </summary>
public sealed record PrepareExperimentRunRequest
{
    public Guid RequestId { get; init; }
    public Guid ExperimentJobId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Read-only reconciliation request for an already persisted child workflow.
/// The server loads the child by id, so callers cannot spoof its runtime status
/// or workflow identity. Unknown outcomes require a separate human reason.
/// </summary>
public sealed record ReconcileExperimentChildRequest
{
    public Guid RequestId { get; init; }
    public Guid ChildWorkflowRunId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string? UnknownResolutionReason { get; init; }
}
