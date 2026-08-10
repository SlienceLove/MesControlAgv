using MesControlAgv.Domain.Map;

namespace MesControlAgv.Domain.Tests.Map;

public sealed class SmapMapLayoutBuilderTests
{
    [Fact]
    public void Maps_location_marks_to_stations()
    {
        var layout = SmapMapLayoutBuilder.Build(Document());

        Assert.Equal(2, layout.Stations.Count);
        Assert.Equal("LM1", layout.Stations[0].Id);
        Assert.Equal(new MapPoint(1, 2), layout.Stations[0].Physical);
        Assert.Equal("LM2", layout.Stations[1].Id);
        Assert.Equal(new MapPoint(8, 9), layout.Stations[1].Physical);
    }

    [Fact]
    public void Maps_curves_to_routes_referencing_stations()
    {
        var layout = SmapMapLayoutBuilder.Build(Document());

        var route = Assert.Single(layout.Routes);
        Assert.Equal("LM1-LM2", route.Id);
        Assert.Equal("LM1", route.FromStationId);
        Assert.Equal("LM2", route.ToStationId);
        Assert.Equal(new MapPoint(1, 2), route.From);
        Assert.Equal(new MapPoint(8, 9), route.To);
        Assert.Equal(new MapPoint(3, 4), route.Control1);
        Assert.Equal(new MapPoint(5, 6), route.Control2);
        Assert.Equal(1, route.Direction);
    }

    [Fact]
    public void Carries_walls_and_scan_points_through()
    {
        var layout = SmapMapLayoutBuilder.Build(Document());

        var wall = Assert.Single(layout.Walls.Lines);
        Assert.Equal(new MapPoint(-1, -2), wall.Start);
        Assert.Equal(new MapPoint(11, 12), wall.End);
        Assert.Equal([new MapPoint(0, 0), new MapPoint(0.5, 0.5)], layout.ScanPoints);
    }

    private static SmapDocument Document() => new(
        new SmapHeader("2D-Map", "test-map", new MapPoint(0, 0), new MapPoint(10, 10), 0.02, "1.0.6"),
        [new MapPoint(0, 0), new MapPoint(0.5, 0.5)],
        [
            new SmapLocationMark("LM1", new MapPoint(1, 2)),
            new SmapLocationMark("LM2", new MapPoint(8, 9))
        ],
        [new SmapFeatureLine(new MapPoint(-1, -2), new MapPoint(11, 12))],
        [
            new SmapCurve(
                "LM1-LM2",
                "LM1",
                new MapPoint(1, 2),
                "LM2",
                new MapPoint(8, 9),
                new MapPoint(3, 4),
                new MapPoint(5, 6),
                1,
                2)
        ]);
}
