namespace MesControlAgv.Domain.Profiles;

/// <summary>
/// Station identity and capabilities used by routing and dispatch configuration.
/// </summary>
public sealed record StationProfile
{
    public int Code { get; init; }
    public string StationId { get; init; } = string.Empty;
    public string AgvStationId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public int Capacity { get; init; } = 1;
    public bool Enabled { get; init; } = true;
}
