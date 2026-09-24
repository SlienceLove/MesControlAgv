namespace MesControlAgv.Mes.Entities;

public sealed class ShineLabCommandAttempt
{
    public string StrId { get; set; } = string.Empty;
    public string EquipmentCode { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string? TaskUuid { get; set; }
    public int? Action { get; set; }
    public string RawJson { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string WireFormat { get; set; } = string.Empty;
    public long PreparedAtUtcTicks { get; set; }
    public long? WriteCompletedAtUtcTicks { get; set; }
    public string WaitOutcome { get; set; } = "Prepared";
    public string? FirstResponseJson { get; set; }
    public bool ResponseConflict { get; set; }
}

public sealed class ShineLabCommandReplyEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string StrId { get; set; } = string.Empty;
    public string EquipmentCode { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string RawJson { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public long ReceivedAtUtcTicks { get; set; }
    public string? TaskUuid { get; set; }
    public string Correlation { get; set; } = string.Empty;
}
