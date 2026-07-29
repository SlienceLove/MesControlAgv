namespace MesControlAgv.Domain;

public sealed class InvalidTaskTransitionException(TaskStatus status, TaskEvent taskEvent)
    : InvalidOperationException($"Task in {status} cannot process {taskEvent}.");

public static class TaskStateMachine
{
    private static readonly IReadOnlyDictionary<(TaskStatus Status, TaskEvent Event), TaskStatus> Transitions =
        new Dictionary<(TaskStatus Status, TaskEvent Event), TaskStatus>
        {
            [(TaskStatus.Created, TaskEvent.DispatchRequested)] = TaskStatus.Dispatching,
            [(TaskStatus.Dispatching, TaskEvent.PickupMoveStarted)] = TaskStatus.MovingToPickup,
            [(TaskStatus.Dispatching, TaskEvent.DropoffMoveStarted)] = TaskStatus.MovingToDropoff,
            [(TaskStatus.Dispatching, TaskEvent.DeviceFailed)] = TaskStatus.Failed,
            [(TaskStatus.MovingToPickup, TaskEvent.PickupArrived)] = TaskStatus.WaitingPickupConfirmation,
            [(TaskStatus.WaitingPickupConfirmation, TaskEvent.PickupConfirmed)] = TaskStatus.MovingToDropoff,
            [(TaskStatus.MovingToDropoff, TaskEvent.DropoffArrived)] = TaskStatus.WaitingDropoffConfirmation,
            [(TaskStatus.WaitingDropoffConfirmation, TaskEvent.DropoffConfirmed)] = TaskStatus.Completed,
            [(TaskStatus.MovingToPickup, TaskEvent.PauseRequested)] = TaskStatus.Paused,
            [(TaskStatus.MovingToDropoff, TaskEvent.PauseRequested)] = TaskStatus.Paused,
            [(TaskStatus.Paused, TaskEvent.ResumeRequested)] = TaskStatus.MovingToPickup,
            [(TaskStatus.MovingToPickup, TaskEvent.DeviceFailed)] = TaskStatus.Failed,
            [(TaskStatus.MovingToDropoff, TaskEvent.DeviceFailed)] = TaskStatus.Failed,
            [(TaskStatus.Failed, TaskEvent.RetryRequested)] = TaskStatus.Dispatching,
            [(TaskStatus.Dispatching, TaskEvent.Timeout)] = TaskStatus.Unknown,
            [(TaskStatus.MovingToPickup, TaskEvent.Timeout)] = TaskStatus.Unknown,
            [(TaskStatus.MovingToDropoff, TaskEvent.Timeout)] = TaskStatus.Unknown,
            [(TaskStatus.Unknown, TaskEvent.ReconciledMoving)] = TaskStatus.MovingToPickup,
            [(TaskStatus.Unknown, TaskEvent.ReconciledMovingToDropoff)] = TaskStatus.MovingToDropoff,
            [(TaskStatus.Unknown, TaskEvent.ReconciledPickupArrived)] = TaskStatus.WaitingPickupConfirmation,
            [(TaskStatus.Unknown, TaskEvent.ReconciledDropoffArrived)] = TaskStatus.WaitingDropoffConfirmation,
            [(TaskStatus.Unknown, TaskEvent.ReconciledCompleted)] = TaskStatus.Completed,
            [(TaskStatus.Unknown, TaskEvent.ReconciledFailed)] = TaskStatus.Failed
        };

    public static TaskStatus Transition(TaskStatus current, TaskEvent taskEvent)
    {
        if (taskEvent == TaskEvent.CancelConfirmed && current is not TaskStatus.Completed and not TaskStatus.Cancelled)
        {
            return TaskStatus.Cancelled;
        }

        return Transitions.TryGetValue((current, taskEvent), out var next)
            ? next
            : throw new InvalidTaskTransitionException(current, taskEvent);
    }
}
