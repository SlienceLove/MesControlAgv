namespace MesControlAgv.Application;

/// <summary>
/// Normalized operations exposed to MES/WPF. Register addresses and raw
/// Modbus frames must remain inside the instrument driver implementation.
/// </summary>
public enum IonChromatographyOperation
{
    Identify,
    ReadStatus,
    ReadPressure,
    ReadTemperature,
    ReadPumpStatus,
    ReadDetectorStatus,
    ReadAutosamplerStatus,
    LoadApprovedMethod,
    StartRun,
    PauseRun,
    ResumeRun,
    StopRun,
    GetResult
}

public enum IonChromatographyTaskState
{
    Queued,
    Connecting,
    Identifying,
    Preflight,
    Equilibrating,
    PreparingSample,
    Injecting,
    Running,
    AcquiringResult,
    SavingResult,
    Completed,
    Failed,
    Cancelled,
    ManualInterventionRequired
}

public sealed record IonChromatographyTaskRequest(
    string InstrumentId,
    string MethodId,
    string MethodVersion,
    string SampleBatchId,
    IReadOnlyList<string> SamplePositions,
    bool RequireStartApproval = true,
    string? IdempotencyKey = null);

public sealed record IonChromatographyTaskSnapshot(
    Guid TaskId,
    string InstrumentId,
    IonChromatographyTaskState State,
    string CurrentStep,
    DateTimeOffset UpdatedAtUtc,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record IonChromatographyStatusSnapshot(
    string InstrumentId,
    string Model,
    string SerialNumber,
    bool Online,
    string DeviceState,
    bool PortOwned,
    DateTimeOffset ObservedAtUtc,
    double? Pressure = null,
    double? ColumnTemperature = null,
    double? DetectorTemperature = null,
    string? Alarm = null);

/// <summary>
/// Application boundary for direct instrument control. Implementations live
/// in the local instrument gateway and must enforce port ownership,
/// preflight checks, idempotency and write-command safety.
/// </summary>
public interface IIonChromatographyGateway
{
    Task<IonChromatographyStatusSnapshot> GetStatusAsync(string instrumentId, CancellationToken cancellationToken);
    Task<IonChromatographyTaskSnapshot> CreateTaskAsync(IonChromatographyTaskRequest request, CancellationToken cancellationToken);
    Task<IonChromatographyTaskSnapshot?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken);
    Task<IonChromatographyTaskSnapshot> ApproveStartAsync(Guid taskId, string approverId, CancellationToken cancellationToken);
    Task<IonChromatographyTaskSnapshot> ExecuteOperationAsync(Guid taskId, IonChromatographyOperation operation, CancellationToken cancellationToken);
    Task<IonChromatographyTaskSnapshot> CancelTaskAsync(Guid taskId, string operatorId, CancellationToken cancellationToken);
}
