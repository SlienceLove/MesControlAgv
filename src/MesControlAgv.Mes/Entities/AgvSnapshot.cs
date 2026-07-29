namespace MesControlAgv.Mes.Entities;

public sealed class AgvSnapshot
{
    public string AgvId { get; init; } = "agv-01";

    public bool Online { get; set; }

    public string ControlOwner { get; set; } = "unknown";

    public string? CurrentStationId { get; set; }

    public Guid? CurrentTaskId { get; set; }

    public string RawStatus { get; set; } = "{}";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
