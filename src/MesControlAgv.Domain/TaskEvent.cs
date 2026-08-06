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
    ResumeDropoffRequested,
    DeviceFailed,
    Timeout,
    RetryRequested,
    CancelConfirmed,
    ReconciledMoving,
    ReconciledMovingToDropoff,
    ReconciledPickupArrived,
    ReconciledDropoffArrived,
    ReconciledCompleted,
    ReconciledFailed
}
