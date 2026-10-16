namespace MesControlAgv.Mes.Services;

public sealed record ShineLabPushContext(
    string RawJson,
    string ConnectionId,
    string WireFormat,
    DateTimeOffset ReceivedAtUtc);
