using System.Text.Json.Serialization;

namespace MesControlAgv.Contracts;

/// <summary>
/// Normalized AUBO controller mode, mapped from ENUM_RobotModeType_DECLARES.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuboArmMode
{
    Unknown,
    NoController,
    Disconnected,
    ConfirmSafety,
    Booting,
    PowerOff,
    PowerOn,
    Idle,
    BrakeReleasing,
    BackDrive,
    Running,
    Maintenance,
    Error,
    PowerOffing
}

/// <summary>
/// Normalized AUBO safety mode, mapped from ENUM_SafetyModeType_DECLARES.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuboArmSafetyMode
{
    Unknown,
    Undefined,
    Normal,
    ReducedMode,
    Recovery,
    Violation,
    ProtectiveStop,
    SafeguardStop,
    SystemEmergencyStop,
    RobotEmergencyStop,
    Fault
}

/// <summary>
/// Normalized AUBO interpreter state, mapped from ENUM_RuntimeState_DECLARES.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuboArmRuntimeState
{
    Unknown,
    Running,
    Retracting,
    Pausing,
    Paused,
    Stepping,
    Stopping,
    Stopped,
    Aborting
}

/// <summary>
/// Normalized AUBO operational mode, mapped from ENUM_OperationalModeType_DECLARES.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuboArmOperationalMode
{
    Unknown,
    Disabled,
    Automatic,
    Manual
}

/// <summary>
/// Outcome of one MES-to-Lua variable handshake as observed by the control centre.
/// Unknown is a terminal observation, never a reason to resend the command.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuboArmHandshakeState
{
    Unknown,
    Idle,
    Dispatched,
    Acknowledged,
    Running,
    Completed,
    Failed
}

public sealed record AuboArmStatusResponse(
    string DeviceId,
    string RobotName,
    bool Online,
    AuboArmMode Mode,
    int? RawMode,
    AuboArmSafetyMode SafetyMode,
    int? RawSafetyMode,
    AuboArmRuntimeState RuntimeState,
    int? RawRuntimeState,
    AuboArmOperationalMode OperationalMode,
    int? RawOperationalMode,
    DateTimeOffset ObservedAtUtc)
{
    /// <summary>Original string observations, when the controller returns names rather than enum integers.</summary>
    public string? RawModeText { get; init; }
    public string? RawSafetyModeText { get; init; }
    public string? RuntimeStatus { get; init; }
    public string? OperationalModeText { get; init; }
}

/// <summary>
/// Whether the controller is in a state where a Lua-side handshake could run at all.
/// Read-only: the control centre evaluates this before it is ever allowed to write.
/// </summary>
public sealed record AuboArmReadinessResponse(
    string DeviceId,
    bool Ready,
    IReadOnlyList<string> BlockingReasons,
    AuboArmStatusResponse Status,
    string? LoadedProgram,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// One named variable as read back from RegisterControl. RawType is the vendor
/// string from getNamedVariableType so an unexpected type never silently coerces.
/// </summary>
public sealed record AuboArmVariableResponse(
    string DeviceId,
    string Key,
    bool Exists,
    string? RawType,
    int? Int32Value,
    bool? BoolValue,
    double? DoubleValue,
    string? StringValue,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// The command/ack/result variable triple that carries one grasp request.
/// </summary>
public sealed record AuboArmHandshakeSnapshotResponse(
    string DeviceId,
    AuboArmHandshakeState State,
    int? CommandCode,
    int? Sequence,
    int? AcknowledgedSequence,
    int? ResultCode,
    string? ResultDetail,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Result of a control-side handshake dispatch. Included so MES and WPF share one
/// shape once control is authorized; no endpoint returns it while control is off.
/// </summary>
public sealed record AuboArmHandshakeResultResponse(
    Guid OperationId,
    string DeviceId,
    int CommandCode,
    int Sequence,
    AuboArmHandshakeState State,
    int? ResultCode,
    string? ErrorMessage,
    bool MayHaveWritten,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Explicit request to dispatch one allow-listed command branch in the resident
/// AUBO Lua project. OperationId is optional at the HTTP boundary; an empty value
/// is replaced by a new id by the Adapter so retries remain distinguishable.
/// </summary>
public sealed record AuboArmHandshakeDispatchRequest(
    int CommandCode,
    Guid? OperationId = null);

/// <summary>
/// Normalized state of one explicit AUBO project operation.  The Adapter maps the
/// vendor return code and the follow-up runtime observation to this small state set;
/// callers must treat Unknown as requiring reconciliation rather than a retry.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuboArmProgramOperationState
{
    Unknown,
    Accepted,
    Loading,
    Loaded,
    Running,
    Stopped,
    Completed,
    Failed
}

/// <summary>
/// Read-only project/runtime projection used by the MES and WPF program panel.
/// RuntimeStatus is retained as the controller's string observation so a newly
/// introduced vendor state is not silently converted to an integer.
/// </summary>
public sealed record AuboArmProgramStatusResponse(
    string DeviceId,
    bool Online,
    string? LoadedProgram,
    AuboArmRuntimeState RuntimeState,
    string? RuntimeStatus,
    DateTimeOffset ObservedAtUtc)
{
    public string? ProgramName => LoadedProgram;
    public string? CurrentProgram => LoadedProgram;
    public string Runtime => RuntimeStatus ?? RuntimeState.ToString();

    // These normalized controller facts are additive properties so older clients
    // deserializing the six-field response remain source compatible.
    public AuboArmMode RobotMode { get; init; } = AuboArmMode.Unknown;
    public AuboArmSafetyMode SafetyMode { get; init; } = AuboArmSafetyMode.Unknown;
    public AuboArmOperationalMode OperationalMode { get; init; } = AuboArmOperationalMode.Unknown;
    public bool ControlEnabled { get; init; }
}

/// <summary>
/// Read-only program inventory. AUBO's portable RPC surface exposes preloaded
/// project slots (0..99), not a filesystem directory listing; both the source
/// and the configured approval allowlist are retained explicitly.
/// </summary>
public sealed record AuboArmProgramCatalogResponse(
    string DeviceId,
    bool Online,
    string? CurrentProgram,
    IReadOnlyList<string> PreloadedPrograms,
    IReadOnlyList<string> AllowedProgramNames,
    bool IsComplete,
    IReadOnlyList<string> ReadErrors,
    DateTimeOffset ObservedAtUtc)
{
    public IReadOnlyList<AuboArmProgramSlot> Slots { get; init; } = Array.Empty<AuboArmProgramSlot>();
    public IReadOnlyList<string> AvailablePrograms =>
        (CurrentProgram is { } loadedProgram ? [loadedProgram] : Array.Empty<string>())
            .Concat(PreloadedPrograms)
            .Concat(AllowedProgramNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public string Source => "Dashboard Server get loaded program + RuntimeMachine.getPreloadProgram(0..99) + configured allowlist";
}

public sealed record AuboArmProgramSlot(int Index, string ProgramName);

/// <summary>
/// Explicit request for one program operation.  The operator is intentionally
/// required at the business boundary even though authorization is still a later
/// production gate.
/// </summary>
public sealed record AuboArmProgramRequest(
    string ProgramName,
    string OperatorName,
    Guid? OperationId = null)
{
    [JsonPropertyName("program")]
    public string? Program { get; init; }

    [JsonPropertyName("operator")]
    public string? Operator { get; init; }

    [JsonIgnore]
    public string EffectiveProgramName => string.IsNullOrWhiteSpace(Program) ? ProgramName : Program!;

    [JsonIgnore]
    public string EffectiveOperatorName => string.IsNullOrWhiteSpace(Operator) ? OperatorName : Operator!;
}

public sealed record AuboArmProgramRunRequest(
    string? ProgramName,
    string OperatorName,
    Guid? OperationId = null)
{
    [JsonPropertyName("program")]
    public string? Program { get; init; }

    [JsonPropertyName("operator")]
    public string? Operator { get; init; }

    [JsonIgnore]
    public string? EffectiveProgramName => string.IsNullOrWhiteSpace(Program) ? ProgramName : Program;

    [JsonIgnore]
    public string EffectiveOperatorName => string.IsNullOrWhiteSpace(Operator) ? OperatorName : Operator!;
}

public sealed record AuboArmProgramStopRequest(
    string OperatorName,
    Guid? OperationId = null)
{
    [JsonPropertyName("operator")]
    public string? Operator { get; init; }

    [JsonIgnore]
    public string EffectiveOperatorName => string.IsNullOrWhiteSpace(Operator) ? OperatorName : Operator!;
}

/// <summary>
/// Result of loading, starting, or stopping one explicitly named project.
/// MayHaveWritten is true once a mutating RPC might have reached the controller;
/// such a result must never be automatically replayed.
/// </summary>
public sealed record AuboArmProgramOperationResponse(
    Guid OperationId,
    string DeviceId,
    string ProgramName,
    string Operation,
    string OperatorName,
    AuboArmProgramOperationState State,
    AuboArmRuntimeState RuntimeState,
    string? RuntimeStatus,
    string? LoadedProgram,
    int? VendorResultCode,
    string? ErrorMessage,
    bool MayHaveWritten,
    DateTimeOffset CompletedAtUtc)
{
    public bool Succeeded => State is
        AuboArmProgramOperationState.Accepted or
        AuboArmProgramOperationState.Loaded or
        AuboArmProgramOperationState.Running or
        AuboArmProgramOperationState.Stopped or
        AuboArmProgramOperationState.Completed;

    public string Status => State.ToString();
    public string? Program => ProgramName;
    public int? ResultCode => VendorResultCode;
    public string? Error => ErrorMessage;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgvAuboSequenceState
{
    Unknown,
    WaitingForArrival,
    LoadingProgram,
    StartingProgram,
    Running,
    Completed,
    Failed,
    ManualInterventionRequired
}

/// <summary>Explicit request to continue one already-created AGV task with one arm project.</summary>
public sealed record AgvAuboSequenceRequest(
    Guid TransportTaskId,
    string ArmId,
    string ProgramName,
    string OperatorName,
    Guid? SequenceId = null)
{
    [JsonPropertyName("taskId")]
    public Guid? TaskId { get; init; }

    [JsonIgnore]
    public Guid EffectiveTaskId => TaskId.GetValueOrDefault(TransportTaskId);
}

/// <summary>
/// Auditable projection of the ordered AGV→AUBO action.  It is intentionally an
/// in-memory MVP record; a later production gate can persist the same shape.
/// </summary>
public sealed record AgvAuboSequenceResponse(
    Guid SequenceId,
    Guid TransportTaskId,
    string ArmId,
    string ProgramName,
    string OperatorName,
    AgvAuboSequenceState State,
    string? Message,
    AuboArmProgramOperationResponse? ProgramResult,
    DateTimeOffset UpdatedAtUtc)
{
    public bool IsTerminal => State is
        AgvAuboSequenceState.Completed or
        AgvAuboSequenceState.Failed or
        AgvAuboSequenceState.ManualInterventionRequired or
        AgvAuboSequenceState.Unknown;
}
