namespace MesControlAgv.Mes.Entities;

public sealed class TaskEventRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid TaskId { get; init; }

    public string EventType { get; init; } = string.Empty;

    public string Payload { get; init; } = "{}";

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
