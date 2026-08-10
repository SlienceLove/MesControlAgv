namespace MesControlAgv.Domain.Map;

public sealed record MapStationLayout(string Id, MapPoint Physical);

public sealed record MapRouteLayout(
    string Id,
    string FromStationId,
    string ToStationId,
    MapPoint From,
    MapPoint To,
    MapPoint Control1,
    MapPoint Control2,
    int Direction);

public sealed record MapWallLayout(IReadOnlyList<SmapFeatureLine> Lines);

public sealed record MapLayout(
    SmapHeader Header,
    IReadOnlyList<MapStationLayout> Stations,
    IReadOnlyList<MapRouteLayout> Routes,
    MapWallLayout Walls,
    IReadOnlyList<MapPoint> ScanPoints);
