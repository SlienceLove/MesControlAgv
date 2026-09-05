using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Experiments;

namespace MesControlAgv.Domain.Tests;

public sealed class ExperimentCompositeChildWorkflowCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 5, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Matching_child_is_bound_once_and_completion_advances_only_the_next_step()
    {
        var run = StartRun();
        var child = Child(run.Steps[0], WorkflowRuntimeStatus.Running);
        run = ExperimentCompositeChildWorkflowCoordinator.AttachChild(run, child, Start.AddMinutes(1));
        Assert.Equal(ExperimentStepRunStatus.Running, run.Steps[0].Status);
        Assert.Equal(child.ExecutionId, run.Steps[0].WorkflowRunId);

        run = ExperimentCompositeChildWorkflowCoordinator.ReconcileChild(
            run,
            child with { RuntimeStatus = WorkflowRuntimeStatus.Completed },
            Start.AddMinutes(2));
        Assert.Equal(ExperimentRunStatus.Running, run.Status);
        Assert.Equal(ExperimentStepRunStatus.Succeeded, run.Steps[0].Status);
        Assert.Equal(ExperimentStepRunStatus.Ready, run.Steps[1].Status);
        Assert.Null(run.Steps[1].WorkflowRunId);
    }

    [Fact]
    public void Mismatched_child_or_duplicate_binding_is_rejected_before_state_changes()
    {
        var run = StartRun();
        var mismatched = Child(run.Steps[0], WorkflowRuntimeStatus.Running) with
        {
            WorkflowId = Guid.NewGuid()
        };
        Assert.Throws<InvalidExperimentRunTransitionException>(() =>
            ExperimentCompositeChildWorkflowCoordinator.AttachChild(run, mismatched, Start));

        var child = Child(run.Steps[0], WorkflowRuntimeStatus.Running);
        run = ExperimentCompositeChildWorkflowCoordinator.AttachChild(run, child, Start.AddMinutes(1));
        var duplicate = child with { ExecutionId = Guid.NewGuid() };
        Assert.Throws<InvalidExperimentRunTransitionException>(() =>
            ExperimentCompositeChildWorkflowCoordinator.ReconcileChild(run, duplicate, Start.AddMinutes(2)));
        Assert.Equal(child.ExecutionId, run.Steps[0].WorkflowRunId);
    }

    [Fact]
    public void Unknown_child_requires_explicit_resolution_reason_and_never_skips_the_step()
    {
        var run = StartRun();
        var child = Child(run.Steps[0], WorkflowRuntimeStatus.Running);
        run = ExperimentCompositeChildWorkflowCoordinator.AttachChild(run, child, Start.AddMinutes(1));
        run = ExperimentCompositeChildWorkflowCoordinator.ReconcileChild(
            run,
            child with { RuntimeStatus = WorkflowRuntimeStatus.Unknown, LastError = "No terminal evidence" },
            Start.AddMinutes(2));
        Assert.Equal(ExperimentRunStatus.Unknown, run.Status);
        Assert.False(run.IsTerminal);
        Assert.Throws<InvalidExperimentRunTransitionException>(() =>
            ExperimentCompositeChildWorkflowCoordinator.ReconcileChild(
                run,
                child with { RuntimeStatus = WorkflowRuntimeStatus.Completed },
                Start.AddMinutes(3)));

        run = ExperimentCompositeChildWorkflowCoordinator.ReconcileChild(
            run,
            child with { RuntimeStatus = WorkflowRuntimeStatus.Completed },
            Start.AddMinutes(4),
            "Operator verified child completion evidence");
        Assert.Equal(ExperimentRunStatus.Running, run.Status);
        Assert.Equal(2, run.CurrentStepOrder);
    }

    [Fact]
    public void Failed_or_cancelled_child_terminates_outer_run_without_marking_pending_steps_succeeded()
    {
        var failed = StartRun();
        var failedChild = Child(failed.Steps[0], WorkflowRuntimeStatus.Running);
        failed = ExperimentCompositeChildWorkflowCoordinator.AttachChild(failed, failedChild, Start);
        failed = ExperimentCompositeChildWorkflowCoordinator.ReconcileChild(
            failed,
            failedChild with { RuntimeStatus = WorkflowRuntimeStatus.Failed, LastError = "child failed" },
            Start.AddMinutes(1));
        Assert.Equal(ExperimentRunStatus.Failed, failed.Status);
        Assert.Equal(ExperimentStepRunStatus.Failed, failed.Steps[0].Status);
        Assert.Equal(ExperimentStepRunStatus.Pending, failed.Steps[1].Status);

        var cancelled = StartRun();
        var cancelledChild = Child(cancelled.Steps[0], WorkflowRuntimeStatus.Running);
        cancelled = ExperimentCompositeChildWorkflowCoordinator.AttachChild(cancelled, cancelledChild, Start);
        cancelled = ExperimentCompositeChildWorkflowCoordinator.ReconcileChild(
            cancelled,
            cancelledChild with { RuntimeStatus = WorkflowRuntimeStatus.Cancelled },
            Start.AddMinutes(1));
        Assert.Equal(ExperimentRunStatus.Cancelled, cancelled.Status);
        Assert.Equal(ExperimentStepRunStatus.Cancelled, cancelled.Steps[0].Status);
        Assert.Equal(ExperimentStepRunStatus.Pending, cancelled.Steps[1].Status);
    }

    private static ExperimentRun StartRun()
    {
        var plan = new ExperimentPlan
        {
            PlanId = Guid.NewGuid(),
            Version = 1,
            WorkflowSteps =
            [
                new ExperimentPlanWorkflowStep
                {
                    StepId = Guid.NewGuid(),
                    Order = 1,
                    WorkflowId = Guid.NewGuid(),
                    WorkflowVersion = 2,
                    Name = "搬运"
                },
                new ExperimentPlanWorkflowStep
                {
                    StepId = Guid.NewGuid(),
                    Order = 2,
                    WorkflowId = Guid.NewGuid(),
                    WorkflowVersion = 3,
                    Name = "检测"
                }
            ]
        };
        var prepared = ExperimentCompositeRuntimeStateMachine.Prepare(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), plan, Start);
        return ExperimentCompositeRuntimeStateMachine.Start(prepared, Start);
    }

    private static WorkflowExecutionSnapshot Child(
        ExperimentStepRun step,
        WorkflowRuntimeStatus status) => new()
        {
            ExecutionId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            WorkflowId = step.WorkflowId,
            Version = step.WorkflowVersion,
            RuntimeStatus = status,
            CreatedAt = Start,
            UpdatedAt = Start
        };
}
