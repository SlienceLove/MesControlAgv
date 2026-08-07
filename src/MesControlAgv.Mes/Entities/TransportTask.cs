using MesControlAgv.Domain;

namespace MesControlAgv.Mes.Entities;

public sealed class TransportTask
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public int SourceStationCode { get; init; }

    public int TargetStationCode { get; init; }

    public int Priority { get; set; }

    public string? Description { get; set; }

    public string? ExternalId { get; set; }

    public MesControlAgv.Domain.TaskStatus Status { get; set; } = MesControlAgv.Domain.TaskStatus.Created;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public DateTime? EndedAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public int RetryCount { get; set; }

    public string? ActiveTargetStationId { get; set; }

    public string? ActiveAgvId { get; set; }

    public string? ActiveDeviceTaskId { get; set; }

    public string? ActivePathJson { get; set; }

    public string? LastError { get; set; }
}
