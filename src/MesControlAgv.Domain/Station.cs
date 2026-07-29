namespace MesControlAgv.Domain;

public sealed record Station(int Code, string Name, string AgvStationId, bool Enabled);
