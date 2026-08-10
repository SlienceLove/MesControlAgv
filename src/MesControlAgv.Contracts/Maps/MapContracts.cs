namespace MesControlAgv.Contracts;

/// <summary>
/// Read-only map/profile data exposed to operators for readiness comparison.
/// </summary>
public sealed record MapEdgeResponse(
    string From,
    string To,
    double Cost,
    bool Bidirectional);

public sealed record MapSnapshotResponse(
    IReadOnlyList<StationResponse> Stations,
    IReadOnlyList<MapEdgeResponse> Edges,
    string? ProfileProductId = null,
    string? ProfileVersion = null,
    string? ProfileMapName = null,
    string? ProfileMapVersion = null,
    string? ProfileMapMd5 = null);
