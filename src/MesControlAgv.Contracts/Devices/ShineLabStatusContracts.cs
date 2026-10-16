namespace MesControlAgv.Contracts;

/// <summary>
/// Normalized status pushed by the resident ShineLab TCP client and exposed to
/// WPF through MES.  The central server does not read a serial port.
/// </summary>
public sealed record ShineLabDeviceStatusResponse(
    string EquipmentCode,
    string DeviceName,
    bool Online,
    int Status,
    string State,
    bool HasActiveTask,
    string? TaskUuid,
    string? SampleId,
    string? SampleName,
    string? Channel,
    int? Position,
    string? Stage,
    int? Progress,
    string? AlarmCode,
    string? AlarmMessage,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset? TaskStartedAtUtc = null,
    DateTimeOffset? TaskFinishedAtUtc = null,
    ShineLabProtocolEventObservation? LastProtocolEvent = null)
{
    /// <summary>Receipt time of the last Device/UpdateInfo with a status code; heartbeats do not advance it.</summary>
    public DateTimeOffset? LastDeviceStatusAtUtc { get; init; }
}

/// <summary>Historical protocol evidence, not a declaration of task completion.</summary>
public sealed record ShineLabProtocolEventObservation(
    string StrId,
    string Method,
    string? SampleId,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? ReportedFinishAtUtc);
