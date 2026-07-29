namespace MesControlAgv.Adapter.Entities;

public sealed class AdapterTask
{
    public Guid TaskId { get; init; }
    public string DeviceTaskId { get; init; } = string.Empty;
    public string TargetStationId { get; init; } = string.Empty;
    public string State { get; set; } = "moving";
    public string? LastError { get; set; }
}
