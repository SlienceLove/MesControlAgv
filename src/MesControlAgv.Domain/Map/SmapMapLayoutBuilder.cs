namespace MesControlAgv.Domain.Map;

public static class SmapMapLayoutBuilder
{
    public static MapLayout Build(SmapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var stations = doc.LocationMarks
            .Select(mark => new MapStationLayout(mark.InstanceName, mark.Pos))
            .ToArray();

        var routes = doc.Curves
            .Select(curve => new MapRouteLayout(
                curve.InstanceName,
                curve.StartMark,
                curve.EndMark,
                curve.Start,
                curve.End,
                curve.Control1,
                curve.Control2,
                curve.Direction))
            .ToArray();

        return new MapLayout(
            doc.Header,
            stations,
            routes,
            new MapWallLayout(doc.FeatureLines.ToArray()),
            doc.NormalPosList.ToArray());
    }
}
