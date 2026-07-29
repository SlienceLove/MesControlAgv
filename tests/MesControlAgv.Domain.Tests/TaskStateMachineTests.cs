namespace MesControlAgv.Domain.Tests;

public class TaskStateMachineTests
{
    [Fact]
    public void Pickup_arrival_waits_for_operator_confirmation()
    {
        var status = TaskStateMachine.Transition(
            TaskStatus.MovingToPickup,
            TaskEvent.PickupArrived);

        Assert.Equal(TaskStatus.WaitingPickupConfirmation, status);
    }

    [Fact]
    public void Two_confirmations_complete_the_transport()
    {
        var movingToDropoff = TaskStateMachine.Transition(
            TaskStatus.WaitingPickupConfirmation,
            TaskEvent.PickupConfirmed);
        var waitingForDropoff = TaskStateMachine.Transition(
            movingToDropoff,
            TaskEvent.DropoffArrived);
        var completed = TaskStateMachine.Transition(
            waitingForDropoff,
            TaskEvent.DropoffConfirmed);

        Assert.Equal(TaskStatus.Completed, completed);
    }

    [Fact]
    public void Unknown_requires_a_reconciliation_event()
    {
        Assert.Throws<InvalidTaskTransitionException>(() => TaskStateMachine.Transition(
            TaskStatus.Unknown,
            TaskEvent.DispatchRequested));
    }

    [Fact]
    public void Cancellation_is_available_from_active_task_state()
    {
        var cancelled = TaskStateMachine.Transition(
            TaskStatus.MovingToPickup,
            TaskEvent.CancelConfirmed);

        Assert.Equal(TaskStatus.Cancelled, cancelled);
    }
}
