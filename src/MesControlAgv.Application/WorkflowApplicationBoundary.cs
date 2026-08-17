using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Application;

/// <summary>
/// Reads an immutable workflow version for application use cases. MES can back
/// this port with its persistence implementation without making the runtime
/// executor depend on a database.
/// </summary>
public interface IWorkflowVersionReader
{
    Task<WorkflowVersion?> GetVersionAsync(
        Guid workflowId,
        int version,
        CancellationToken cancellationToken);
}

/// <summary>
/// Applies deployment-specific admission rules to an immutable workflow version.
/// Policies must be side-effect free because they run before an execution is accepted.
/// </summary>
public interface IWorkflowRuntimeAdmissionPolicy
{
    IReadOnlyList<WorkflowValidationIssue> Validate(WorkflowVersion version);
}

/// <summary>
/// Application port for turning a pinned workflow version into an auditable
/// execution request. Implementations do not execute device operations.
/// </summary>
public interface IWorkflowRuntimeExecutor
{
    Task<WorkflowExecutionResult> ExecuteAsync(
        WorkflowExecutionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Application boundary for workflow drafting, validation, publication and
/// execution. Implementations own persistence and orchestration; callers always
/// address an immutable version when publishing or executing.
/// </summary>
public interface IWorkflowApplicationService : IWorkflowVersionReader, IWorkflowRuntimeExecutor
{
    Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken);
    Task<WorkflowDefinition?> GetAsync(Guid workflowId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkflowVersion>> ListVersionsAsync(Guid workflowId, CancellationToken cancellationToken);

    /// <summary>Reloads a persisted workflow execution after a MES restart.</summary>
    Task<WorkflowExecutionSnapshot?> GetExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken);

    /// <summary>Reloads a persisted admission, including rejected requests.</summary>
    Task<WorkflowExecutionSnapshot?> GetExecutionByRequestAsync(
        Guid requestId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically reserves the pending step's stable Adapter operation id.
    /// It never writes to an Adapter or device.
    /// </summary>
    Task<WorkflowExecutionSnapshot> ClaimNextStepAsync(
        Guid executionId,
        CancellationToken cancellationToken);

    /// <summary>Persists reconciled outcome evidence for a previously claimed step.</summary>
    Task<WorkflowExecutionSnapshot> CompleteClaimedStepAsync(
        Guid executionId,
        WorkflowStepCompletionRequest completion,
        CancellationToken cancellationToken);

    /// <summary>Returns non-dry-run Move steps that the Simulator worker may claim.</summary>
    Task<IReadOnlyList<WorkflowExecutionSnapshot>> ListSimulatorDispatchableExecutionsAsync(
        CancellationToken cancellationToken);

    /// <summary>Returns claimed executions that need read-only reconciliation after restart.</summary>
    Task<IReadOnlyList<WorkflowExecutionSnapshot>> ListRecoverableExecutionsAsync(
        CancellationToken cancellationToken);

    /// <summary>Returns persisted lifecycle and execution audits, newest first.</summary>
    Task<IReadOnlyList<WorkflowAuditResponse>> ListAuditsAsync(
        Guid workflowId,
        int? version,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Creates version 1 (or the next version) in Draft state.</summary>
    Task<WorkflowVersion> CreateDraftAsync(
        WorkflowDefinition definition,
        string actor,
        CancellationToken cancellationToken);

    /// <summary>Updates a draft without changing its publication state.</summary>
    Task<WorkflowVersion> UpdateDraftAsync(
        Guid workflowId,
        int version,
        WorkflowDefinition definition,
        string actor,
        CancellationToken cancellationToken);

    /// <summary>Validates an in-memory draft before it is persisted or published.</summary>
    Task<WorkflowValidationResult> ValidateAsync(
        WorkflowDefinition definition,
        CancellationToken cancellationToken);

    /// <summary>Revalidates the persisted version and records the validation result.</summary>
    Task<WorkflowValidationResult> ValidateVersionAsync(
        Guid workflowId,
        int version,
        CancellationToken cancellationToken);

    /// <summary>Publishes only a validated draft and returns the new immutable state.</summary>
    Task<WorkflowVersion> PublishAsync(
        Guid workflowId,
        int version,
        string actor,
        CancellationToken cancellationToken);

}
