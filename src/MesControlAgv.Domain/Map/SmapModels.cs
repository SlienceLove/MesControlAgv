namespace MesControlAgv.Domain.Map;

public readonly record struct MapPoint(double X, double Y);

public sealed record SmapHeader(
    string MapType,
    string MapName,
    MapPoint MinPos,
    MapPoint MaxPos,
    double Resolution,
    string Version);

public sealed record SmapLocationMark(string InstanceName, MapPoint Pos);

public sealed record SmapFeatureLine(MapPoint Start, MapPoint End);

public sealed record SmapCurve(
    string InstanceName,
    string StartMark,
    MapPoint Start,
    string EndMark,
    MapPoint End,
    MapPoint Control1,
    MapPoint Control2,
    int Direction,
    int MoveStyle);

public sealed record SmapDocument(
    SmapHeader Header,
    IReadOnlyList<MapPoint> NormalPosList,
    IReadOnlyList<SmapLocationMark> LocationMarks,
    IReadOnlyList<SmapFeatureLine> FeatureLines,
    IReadOnlyList<SmapCurve> Curves);
