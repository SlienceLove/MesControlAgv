using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Domain.Experiments;

/// <summary>
/// Device-free bridge between one outer experiment step and its child workflow
/// execution. It accepts only a matching immutable workflow identity and maps
/// child runtime evidence into the outer state machine; it never creates or
/// retries a child execution.
/// </summary>
public static class ExperimentCompositeChildWorkflowCoordinator
{
    public static ExperimentRun AttachChild(
        ExperimentRun run,
        WorkflowExecutionSnapshot child,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(child);
        EnsureChildIdentity(run, child);
        if (child.RuntimeStatus is WorkflowRuntimeStatus.Rejected or WorkflowRuntimeStatus.DryRunCompleted)
            throw Invalid(run, "A rejected or dry-run child cannot be attached to a composite step.");
        if (child.RuntimeStatus is WorkflowRuntimeStatus.Completed or
            WorkflowRuntimeStatus.Failed or
            WorkflowRuntimeStatus.Cancelled or
            WorkflowRuntimeStatus.Unknown)
        {
            // A child may have reached a terminal state before the coordinator
            // observed it. Bind it once, then reconcile the evidence below.
            if (run.Status == ExperimentRunStatus.Running &&
                run.Steps.Single(step => step.StepRunId == run.CurrentStepRunId).Status == ExperimentStepRunStatus.Ready)
            {
                run = ExperimentCompositeRuntimeStateMachine.StartCurrentStep(run, child.ExecutionId, now);
            }
            return ReconcileChild(run, child, now, "Child terminal state was observed before outer binding.");
        }

        return ExperimentCompositeRuntimeStateMachine.StartCurrentStep(run, child.ExecutionId, now);
    }

    public static ExperimentRun ReconcileChild(
        ExperimentRun run,
        WorkflowExecutionSnapshot child,
        DateTimeOffset now,
        string? unknownResolutionReason = null)
    {
        ArgumentNullException.ThrowIfNull(child);
        EnsureChildIdentity(run, child);
        var outcome = child.RuntimeStatus switch
        {
            WorkflowRuntimeStatus.Completed => WorkflowStepCompletionOutcome.Succeeded,
            WorkflowRuntimeStatus.Failed => WorkflowStepCompletionOutcome.Failed,
            WorkflowRuntimeStatus.Cancelled => WorkflowStepCompletionOutcome.Cancelled,
            WorkflowRuntimeStatus.Unknown => WorkflowStepCompletionOutcome.Unknown,
            WorkflowRuntimeStatus.Prepared or
            WorkflowRuntimeStatus.Running or
            WorkflowRuntimeStatus.Paused => (WorkflowStepCompletionOutcome?)null,
            WorkflowRuntimeStatus.Rejected or
            WorkflowRuntimeStatus.DryRunCompleted => throw Invalid(
                run,
                "A rejected or dry-run child is not valid execution evidence."),
            _ => throw Invalid(run, "The child workflow runtime status is unknown.")
        };
        if (outcome is null) return run;

        var error = child.LastError;
        if (run.Status == ExperimentRunStatus.Unknown)
        {
            if (string.IsNullOrWhiteSpace(unknownResolutionReason))
                throw Invalid(run, "An explicit reason is required to resolve an Unknown child workflow.");
            if (outcome == WorkflowStepCompletionOutcome.Unknown)
                throw Invalid(run, "An Unknown child result cannot resolve an Unknown outer run without an explicit outcome.");
            return ExperimentCompositeRuntimeStateMachine.ResolveUnknown(
                run,
                outcome.Value,
                unknownResolutionReason,
                now);
        }

        return ExperimentCompositeRuntimeStateMachine.CompleteCurrentStep(
            run,
            outcome.Value,
            error,
            now);
    }

    private static void EnsureChildIdentity(
        ExperimentRun run,
        WorkflowExecutionSnapshot child)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (child.ExecutionId == Guid.Empty)
            throw Invalid(run, "A child workflow execution id is required.");
        var current = run.CurrentStepRunId is { } id
            ? run.Steps.SingleOrDefault(step => step.StepRunId == id)
            : run.Steps.SingleOrDefault(step => step.Order == run.CurrentStepOrder);
        if (current is null)
            throw Invalid(run, "The outer run does not have a current step.");
        if (current.WorkflowId != child.WorkflowId || current.WorkflowVersion != child.Version)
            throw Invalid(run, "The child workflow identity does not match the pinned current step.");
        if (current.WorkflowRunId is { } bound && bound != child.ExecutionId)
            throw Invalid(run, "The current step is already bound to a different child workflow execution.");
        if (run.Status is not (ExperimentRunStatus.Running or ExperimentRunStatus.Unknown))
            throw Invalid(run, "Child workflow evidence is not accepted in the current outer run state.");
    }

    private static InvalidExperimentRunTransitionException Invalid(
        ExperimentRun run,
        string message) => new(
        run.Status,
        run.CurrentStepRunId is { } id
            ? run.Steps.SingleOrDefault(step => step.StepRunId == id)?.Status
            : null,
        message);
}
