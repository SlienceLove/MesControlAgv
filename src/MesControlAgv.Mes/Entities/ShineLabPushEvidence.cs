namespace MesControlAgv.Mes.Entities;

/// <summary>DCSeq lifecycle evidence retained even when no MES task can be identified.</summary>
public sealed class ShineLabPushEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EquipmentCode { get; set; } = string.Empty;
    public string StrId { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string BodyJson { get; set; } = "{}";
    // Exact decoded JSON envelope; null for legacy/body-only records.
    // This is not reconstructed from BodyJson, nor a raw TCP packet capture.
    public string? RawJson { get; set; }
    public string? ConnectionId { get; set; }
    public string? WireFormat { get; set; }
    public string? TaskUuid { get; set; }
    public string Correlation { get; set; } = string.Empty;
    public string? DeduplicationKey { get; set; }
    public bool IsDuplicate { get; set; }
    public long ReceivedAtUtcTicks { get; set; } = DateTimeOffset.UtcNow.UtcTicks;
}
