namespace MesControlAgv.Adapter.Entities;

public sealed class AdapterTask
{
    public Guid TaskId { get; init; }
    public string AgvId { get; set; } = "AGV-01";
    public string DeviceTaskId { get; set; } = string.Empty;
    public string TargetStationId { get; set; } = string.Empty;
    public string State { get; set; } = "moving";
    public string? LastError { get; set; }
    public string? PathJson { get; set; }
}
