namespace MesControlAgv.Contracts;

/// <summary>
/// One digital I/O point as reported by the AGV controller's API 1013.
/// <see cref="Valid"/> is supplied for DI points and is normally absent for DO
/// points, so it remains nullable in the normalized contract.
/// </summary>
public sealed record AgvIoPointResponse(
    int Id,
    string Source,
    bool Status,
    bool? Valid = null);

/// <summary>
/// Controller-authoritative digital I/O snapshot.
/// </summary>
public sealed record AgvIoSnapshotResponse(
    IReadOnlyList<AgvIoPointResponse> DigitalInputs,
    IReadOnlyList<AgvIoPointResponse> DigitalOutputs,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Result of a vendor API 6001 single-DO write. The command acknowledgement
/// proves acceptance by the controller; a subsequent API 1013 read is required
/// to confirm the controller state and an external DI read is required to prove
/// physical wiring to another device.
/// </summary>
public sealed record AgvDoWriteResponse(
    int Id,
    bool Status,
    int ReturnCode,
    string? ErrorMessage,
    DateTimeOffset ObservedAtUtc);

public sealed record AgvDoWriteRequest(bool Status);
