using MesControlAgv.Domain;

namespace MesControlAgv.Mes.Entities;

public sealed class TransportTask
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public int SourceStationCode { get; init; }

    public int TargetStationCode { get; init; }

    public MesControlAgv.Domain.TaskStatus Status { get; set; } = MesControlAgv.Domain.TaskStatus.Created;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public int RetryCount { get; set; }

    public string? ActiveTargetStationId { get; set; }

    public string? LastError { get; set; }
}
