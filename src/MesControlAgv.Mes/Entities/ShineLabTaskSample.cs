namespace MesControlAgv.Mes.Entities;

/// <summary>Persistent MES-owned injection identity; never reuse for another run.</summary>
public sealed class ShineLabTaskSample
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TaskUuid { get; set; } = string.Empty;
    public string EquipmentCode { get; set; } = string.Empty;
    public int RowNumber { get; set; }
    public string SourceSampleId { get; set; } = string.Empty;
    public string SampleId { get; set; } = string.Empty;
    public string SampleName { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public bool ConfigAttempted { get; set; }
    public bool SampleFinishObserved { get; set; }
    public bool TaskFinishObserved { get; set; }
    public bool EndMissionObserved { get; set; }
    public string? ResultJson { get; set; }
    public bool ResultConflict { get; set; }
    public long? LastReceivedAtUtcTicks { get; set; }
}
