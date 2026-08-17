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
    string? Alarm = null,
    double? Conductivity = null,
    double? TotalConductivity = null,
    double? Flow = null,
    string? MappingConfidence = null);

public interface IIonChromatographyStatusReader
{
    Task<IonChromatographyStatusSnapshot> GetStatusAsync(
        string instrumentId,
        CancellationToken cancellationToken);
}

public static class IonChromatographyReadOnlyPolicy
{
    private static readonly HashSet<IonChromatographyOperation> Enabled =
    [
        IonChromatographyOperation.Identify,
        IonChromatographyOperation.ReadStatus
    ];

    public static bool TaskAdmissionEnabled => false;

    public static IReadOnlyList<IonChromatographyOperation> EnabledOperations { get; } =
        Enabled.OrderBy(operation => operation).ToArray();

    public static bool IsEnabled(IonChromatographyOperation operation) => Enabled.Contains(operation);

    public static void EnsureEnabled(IonChromatographyOperation operation)
    {
        if (!IsEnabled(operation))
            throw new InvalidOperationException($"Ion chromatography operation '{operation}' is not authorized in read-only mode.");
    }
}

public static class IonChromatographyTaskStateMachine
{
    private static readonly HashSet<(IonChromatographyTaskState From, IonChromatographyTaskState To)> Transitions =
    [
        (IonChromatographyTaskState.Queued, IonChromatographyTaskState.Connecting),
        (IonChromatographyTaskState.Connecting, IonChromatographyTaskState.Identifying),
        (IonChromatographyTaskState.Identifying, IonChromatographyTaskState.Preflight),
        (IonChromatographyTaskState.Preflight, IonChromatographyTaskState.Equilibrating),
        (IonChromatographyTaskState.Equilibrating, IonChromatographyTaskState.PreparingSample),
        (IonChromatographyTaskState.PreparingSample, IonChromatographyTaskState.Injecting),
        (IonChromatographyTaskState.Injecting, IonChromatographyTaskState.Running),
        (IonChromatographyTaskState.Running, IonChromatographyTaskState.AcquiringResult),
        (IonChromatographyTaskState.AcquiringResult, IonChromatographyTaskState.SavingResult),
        (IonChromatographyTaskState.SavingResult, IonChromatographyTaskState.Completed)
    ];

    public static bool CanTransition(
        IonChromatographyTaskState from,
        IonChromatographyTaskState to,
        bool controlOperationsEnabled = false)
    {
        if (IsTerminal(from)) return false;
        if (to is IonChromatographyTaskState.Failed or
            IonChromatographyTaskState.Cancelled or
            IonChromatographyTaskState.ManualInterventionRequired)
        {
            return true;
        }

        return Transitions.Contains((from, to)) &&
               (controlOperationsEnabled || !RequiresInstrumentMutation(to));
    }

    public static void EnsureTransition(
        IonChromatographyTaskState from,
        IonChromatographyTaskState to,
        bool controlOperationsEnabled = false)
    {
        if (!CanTransition(from, to, controlOperationsEnabled))
            throw new InvalidOperationException($"Ion chromatography transition '{from}' -> '{to}' is not permitted.");
    }

    private static bool RequiresInstrumentMutation(IonChromatographyTaskState state) => state is
        IonChromatographyTaskState.Equilibrating or
        IonChromatographyTaskState.PreparingSample or
        IonChromatographyTaskState.Injecting or
        IonChromatographyTaskState.Running or
        IonChromatographyTaskState.AcquiringResult or
        IonChromatographyTaskState.SavingResult or
        IonChromatographyTaskState.Completed;

    private static bool IsTerminal(IonChromatographyTaskState state) => state is
        IonChromatographyTaskState.Completed or
        IonChromatographyTaskState.Failed or
        IonChromatographyTaskState.Cancelled or
        IonChromatographyTaskState.ManualInterventionRequired;
}

/// <summary>
/// Application boundary for direct instrument control. Implementations live
/// in the local instrument gateway and must enforce port ownership,
/// preflight checks, idempotency and write-command safety.
/// </summary>
public interface IIonChromatographyGateway : IIonChromatographyStatusReader
{
    Task<IonChromatographyTaskSnapshot> CreateTaskAsync(IonChromatographyTaskRequest request, CancellationToken cancellationToken);
    Task<IonChromatographyTaskSnapshot?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken);
    Task<IonChromatographyTaskSnapshot> ApproveStartAsync(Guid taskId, string approverId, CancellationToken cancellationToken);
    Task<IonChromatographyTaskSnapshot> ExecuteOperationAsync(Guid taskId, IonChromatographyOperation operation, CancellationToken cancellationToken);
    Task<IonChromatographyTaskSnapshot> CancelTaskAsync(Guid taskId, string operatorId, CancellationToken cancellationToken);
}
