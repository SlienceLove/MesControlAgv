namespace MesControlAgv.Adapter.Contracts;

public sealed record AdapterTaskResponse(Guid TaskId, string DeviceTaskId, string TargetStationId, string State, string? LastError);
public sealed record AgvSnapshotResponse(bool Online, string ControlOwner, string? CurrentStationId, Guid? CurrentTaskId);
