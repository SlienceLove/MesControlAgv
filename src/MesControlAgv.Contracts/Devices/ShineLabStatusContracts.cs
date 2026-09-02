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
    DateTimeOffset? TaskFinishedAtUtc = null);
