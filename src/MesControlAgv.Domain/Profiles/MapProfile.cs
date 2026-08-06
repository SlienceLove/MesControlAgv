namespace MesControlAgv.Domain.Profiles;

/// <summary>
/// Routing graph configuration. Map station ids are AGV-facing station ids.
/// </summary>
public sealed record MapProfile
{
    public IReadOnlyList<string> StationIds { get; init; } = [];
    public IReadOnlyList<MapEdgeProfile> Edges { get; init; } = [];
}

public sealed record MapEdgeProfile
{
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
    public double Cost { get; init; } = 1;
    public bool Bidirectional { get; init; } = true;
}
