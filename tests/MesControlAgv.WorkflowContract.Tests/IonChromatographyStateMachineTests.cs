using MesControlAgv.Application;

namespace MesControlAgv.WorkflowContract.Tests;

public sealed class IonChromatographyStateMachineTests
{
    [Fact]
    public void ReadOnlyPolicy_EnablesOnlyObservedReadCapabilities()
    {
        Assert.True(IonChromatographyReadOnlyPolicy.IsEnabled(IonChromatographyOperation.Identify));
        Assert.True(IonChromatographyReadOnlyPolicy.IsEnabled(IonChromatographyOperation.ReadStatus));
        Assert.False(IonChromatographyReadOnlyPolicy.IsEnabled(IonChromatographyOperation.LoadApprovedMethod));
        Assert.False(IonChromatographyReadOnlyPolicy.IsEnabled(IonChromatographyOperation.StartRun));
        Assert.False(IonChromatographyReadOnlyPolicy.IsEnabled(IonChromatographyOperation.StopRun));
        Assert.False(IonChromatographyReadOnlyPolicy.TaskAdmissionEnabled);
    }

    [Fact]
    public void ReadOnlyStateMachine_StopsAtPreflight()
    {
        Assert.True(IonChromatographyTaskStateMachine.CanTransition(
            IonChromatographyTaskState.Queued,
            IonChromatographyTaskState.Connecting));
        Assert.True(IonChromatographyTaskStateMachine.CanTransition(
            IonChromatographyTaskState.Identifying,
            IonChromatographyTaskState.Preflight));
        Assert.False(IonChromatographyTaskStateMachine.CanTransition(
            IonChromatographyTaskState.Preflight,
            IonChromatographyTaskState.Equilibrating));
        Assert.True(IonChromatographyTaskStateMachine.CanTransition(
            IonChromatographyTaskState.Preflight,
            IonChromatographyTaskState.ManualInterventionRequired));
    }

    [Fact]
    public void FutureControlTransition_RequiresAnExplicitlyEnabledPolicy()
    {
        Assert.True(IonChromatographyTaskStateMachine.CanTransition(
            IonChromatographyTaskState.Preflight,
            IonChromatographyTaskState.Equilibrating,
            controlOperationsEnabled: true));
        Assert.Throws<InvalidOperationException>(() =>
            IonChromatographyTaskStateMachine.EnsureTransition(
                IonChromatographyTaskState.Preflight,
                IonChromatographyTaskState.Equilibrating));
    }
}
