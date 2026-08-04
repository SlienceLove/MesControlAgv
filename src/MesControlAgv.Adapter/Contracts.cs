namespace MesControlAgv.Adapter.Contracts;

public sealed record AdapterTaskResponse(
    Guid TaskId,
    string DeviceTaskId,
    string TargetStationId,
    string State,
    string? LastError,
    string AgvId = "AGV-01",
    IReadOnlyList<string>? Path = null);

public sealed record AgvSnapshotResponse(
    bool Online,
    string ControlOwner,
    string? CurrentStationId,
    Guid? CurrentTaskId,
    string AgvId = "AGV-01");
