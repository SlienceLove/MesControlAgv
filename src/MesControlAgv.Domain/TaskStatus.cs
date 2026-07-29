namespace MesControlAgv.Domain;

public enum TaskStatus
{
    Created,
    Dispatching,
    MovingToPickup,
    WaitingPickupConfirmation,
    MovingToDropoff,
    WaitingDropoffConfirmation,
    Completed,
    Paused,
    Failed,
    Unknown,
    Cancelled
}
