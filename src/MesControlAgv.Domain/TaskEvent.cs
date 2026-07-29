namespace MesControlAgv.Domain;

public enum TaskEvent
{
    DispatchRequested,
    PickupMoveStarted,
    PickupArrived,
    PickupConfirmed,
    DropoffMoveStarted,
    DropoffArrived,
    DropoffConfirmed,
    PauseRequested,
    ResumeRequested,
    DeviceFailed,
    Timeout,
    CancelConfirmed,
    ReconciledMoving,
    ReconciledPickupArrived,
    ReconciledDropoffArrived,
    ReconciledCompleted
}
