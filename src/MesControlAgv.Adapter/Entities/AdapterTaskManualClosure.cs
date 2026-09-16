namespace MesControlAgv.Adapter.Entities;

public sealed class AdapterTaskManualClosure
{
    public Guid TaskId { get; init; }
    public Guid RequestId { get; init; }
    public string RequestJson { get; init; } = string.Empty;
    public string ResultJson { get; init; } = string.Empty;
}
