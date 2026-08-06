namespace MesControlAgv.Domain.Profiles;

/// <summary>
/// Operational limits and identity for one AGV in the configured fleet.
/// </summary>
public sealed record AgvProfile
{
    public string AgvId { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Driver { get; init; } = "simulator";
    public string Endpoint { get; init; } = "http://localhost:5183/";
    public IReadOnlyDictionary<string, string> DeviceParameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public double MaxLoadKg { get; init; }
    public double MaxSpeedMetersPerSecond { get; init; }
    public string HomeStationId { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
}
