namespace MesControlAgv.Domain.Profiles;

/// <summary>
/// Immutable acceptance constraints for a physical AGV deployment. A missing value
/// keeps the profile on the existing simulator-compatible path.
/// </summary>
public sealed record PhysicalAcceptanceProfile
{
    public string ExpectedControlOwner { get; init; } = string.Empty;
    public ControllerMapSnapshot MapSnapshot { get; init; } = null!;
    public PhysicalAgvSafetyProfile Safety { get; init; } = null!;
}

/// <summary>
/// Controller-derived map facts captured during an on-site, read-only preflight.
/// </summary>
public sealed record ControllerMapSnapshot
{
    public string MapName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Md5 { get; init; } = string.Empty;
    public DateTimeOffset CapturedAtUtc { get; init; }
    public IReadOnlyList<string> StationIds { get; init; } = [];
    public IReadOnlyList<DirectedMapEdgeProfile> DirectedEdges { get; init; } = [];
}

public sealed record DirectedMapEdgeProfile
{
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
}

/// <summary>
/// Site-approved dispatch gates. Every gate is required for a physical profile.
/// </summary>
public sealed record PhysicalAgvSafetyProfile
{
    public double MinimumLocalizationConfidence { get; init; }
    public double MaximumDispatchSpeedMetersPerSecond { get; init; }
    public bool RequireControlOwnership { get; init; }
    public bool RequireNoEmergency { get; init; }
    public bool RequireNoBlocked { get; init; }
    public bool RequireNoFaults { get; init; }
    public bool RequireAutomaticMode { get; init; }
}
