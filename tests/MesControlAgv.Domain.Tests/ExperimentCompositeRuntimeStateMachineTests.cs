using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Experiments;

namespace MesControlAgv.Domain.Tests;

public sealed class ExperimentCompositeRuntimeStateMachineTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Prepare_creates_stable_pending_step_runs_for_each_pinned_plan_step()
    {
        var runId = Guid.NewGuid();
        var plan = Plan();

        var first = ExperimentCompositeRuntimeStateMachine.Prepare(
            runId, Guid.NewGuid(), Guid.NewGuid(), plan, Start);
        var second = ExperimentCompositeRuntimeStateMachine.Prepare(
            runId, first.ExperimentJobId, first.AdmissionRequestId, plan, Start);

        Assert.Equal(ExperimentRunStatus.Prepared, first.Status);
        Assert.Equal(1, first.CurrentStepOrder);
        Assert.Null(first.CurrentStepRunId);
        Assert.Equal([ExperimentStepRunStatus.Pending, ExperimentStepRunStatus.Pending], first.Steps.Select(step => step.Status));
        Assert.Equal(first.Steps.Select(step => step.StepRunId), second.Steps.Select(step => step.StepRunId));
    }

    [Fact]
    public void Success_advances_exactly_one_step_and_completes_only_after_the_last_step()
    {
        var run = StartRun();
        var child1 = Guid.NewGuid();
        run = ExperimentCompositeRuntimeStateMachine.StartCurrentStep(run, child1, Start.AddMinutes(1));
        run = ExperimentCompositeRuntimeStateMachine.CompleteCurrentStep(
            run, WorkflowStepCompletionOutcome.Succeeded, null, Start.AddMinutes(2));

        Assert.Equal(ExperimentRunStatus.Running, run.Status);
        Assert.Equal(2, run.CurrentStepOrder);
        Assert.Equal(ExperimentStepRunStatus.Succeeded, run.Steps[0].Status);
        Assert.Equal(ExperimentStepRunStatus.Ready, run.Steps[1].Status);
        Assert.Null(run.Steps[1].WorkflowRunId);

        run = ExperimentCompositeRuntimeStateMachine.StartCurrentStep(run, Guid.NewGuid(), Start.AddMinutes(3));
        run = ExperimentCompositeRuntimeStateMachine.CompleteCurrentStep(
            run, WorkflowStepCompletionOutcome.Succeeded, null, Start.AddMinutes(4));

        Assert.Equal(ExperimentRunStatus.Completed, run.Status);
        Assert.True(run.IsTerminal);
        Assert.All(run.Steps, step => Assert.Equal(ExperimentStepRunStatus.Succeeded, step.Status));
    }

    [Fact]
    public void Pause_cancel_and_unknown_boundaries_fail_closed_without_skipping_a_step()
    {
        var prepared = ExperimentCompositeRuntimeStateMachine.Prepare(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Plan(), Start);
        Assert.Throws<InvalidExperimentRunTransitionException>(() =>
            ExperimentCompositeRuntimeStateMachine.Pause(prepared, Start.AddMinutes(1)));

        var run = ExperimentCompositeRuntimeStateMachine.Start(prepared, Start.AddMinutes(2));
        run = ExperimentCompositeRuntimeStateMachine.Pause(run, Start.AddMinutes(3));
        Assert.Equal(ExperimentRunStatus.Paused, run.Status);
        run = ExperimentCompositeRuntimeStateMachine.Resume(run, Start.AddMinutes(4));
        run = ExperimentCompositeRuntimeStateMachine.Cancel(run, Start.AddMinutes(5), "operator cancelled at boundary");
        Assert.Equal(ExperimentRunStatus.Cancelled, run.Status);
        Assert.Equal(ExperimentStepRunStatus.Cancelled, run.Steps[0].Status);
        Assert.Equal(ExperimentStepRunStatus.Cancelled, run.Steps[1].Status);

        var unknown = StartRun();
        unknown = ExperimentCompositeRuntimeStateMachine.StartCurrentStep(unknown, Guid.NewGuid(), Start.AddMinutes(6));
        unknown = ExperimentCompositeRuntimeStateMachine.CompleteCurrentStep(
            unknown, WorkflowStepCompletionOutcome.Unknown, "child result unavailable", Start.AddMinutes(7));
        Assert.Equal(ExperimentRunStatus.Unknown, unknown.Status);
        Assert.False(unknown.IsTerminal);
        Assert.Throws<InvalidExperimentRunTransitionException>(() =>
            ExperimentCompositeRuntimeStateMachine.Cancel(unknown, Start.AddMinutes(8)));
        unknown = ExperimentCompositeRuntimeStateMachine.ResolveUnknown(
            unknown,
            WorkflowStepCompletionOutcome.Succeeded,
            "operator verified the child workflow completed",
            Start.AddMinutes(9));
        Assert.Equal(ExperimentRunStatus.Running, unknown.Status);
        Assert.Equal(2, unknown.CurrentStepOrder);
    }

    [Fact]
    public void Active_step_cannot_be_started_twice_or_paused_mid_operation()
    {
        var run = StartRun();
        var child = Guid.NewGuid();
        run = ExperimentCompositeRuntimeStateMachine.StartCurrentStep(run, child, Start.AddMinutes(1));
        Assert.Throws<InvalidExperimentRunTransitionException>(() =>
            ExperimentCompositeRuntimeStateMachine.StartCurrentStep(run, Guid.NewGuid(), Start.AddMinutes(2)));
        Assert.Throws<InvalidExperimentRunTransitionException>(() =>
            ExperimentCompositeRuntimeStateMachine.Pause(run, Start.AddMinutes(2)));
    }

    [Fact]
    public void Child_admission_failure_and_missing_child_evidence_fail_closed()
    {
        var run = StartRun();
        run = ExperimentCompositeRuntimeStateMachine.FailCurrentStepBeforeChild(
            run,
            "child workflow admission was rejected",
            Start.AddMinutes(1));
        Assert.Equal(ExperimentRunStatus.Failed, run.Status);
        Assert.Equal(ExperimentStepRunStatus.Failed, run.Steps[0].Status);
        Assert.Equal(ExperimentStepRunStatus.Pending, run.Steps[1].Status);
        Assert.Equal("child workflow admission was rejected", run.LastError);

        var unknown = StartRun();
        unknown = ExperimentCompositeRuntimeStateMachine.StartCurrentStep(
            unknown,
            Guid.NewGuid(),
            Start.AddMinutes(2));
        unknown = ExperimentCompositeRuntimeStateMachine.MarkCurrentStepUnknown(
            unknown,
            "bound child could not be read after restart",
            Start.AddMinutes(3));
        Assert.Equal(ExperimentRunStatus.Unknown, unknown.Status);
        Assert.False(unknown.IsTerminal);
        Assert.Equal(ExperimentStepRunStatus.Unknown, unknown.Steps[0].Status);
        Assert.Equal(ExperimentStepRunStatus.Pending, unknown.Steps[1].Status);
    }

    private static ExperimentRun StartRun()
    {
        var prepared = ExperimentCompositeRuntimeStateMachine.Prepare(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Plan(), Start);
        return ExperimentCompositeRuntimeStateMachine.Start(prepared, Start);
    }

    private static ExperimentPlan Plan() => new()
    {
        PlanId = Guid.NewGuid(),
        Version = 3,
        Name = "Composite acceptance plan",
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
                WorkflowVersion = 5,
                Name = "检测"
            }
        ]
    };
}
