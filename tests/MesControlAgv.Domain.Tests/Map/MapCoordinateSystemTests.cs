using MesControlAgv.Domain.Map;

namespace MesControlAgv.Domain.Tests.Map;

public sealed class MapCoordinateSystemTests
{
    [Fact]
    public void Origin_min_corner_maps_to_bottom_left()
    {
        var system = new MapCoordinateSystem(Header(new MapPoint(0, 0), new MapPoint(10, 10)), 200, 200, 0);

        Assert.Equal(new MapPoint(0, 200), system.ToCanvas(new MapPoint(0, 0)));
        Assert.Equal(new MapPoint(200, 0), system.ToCanvas(new MapPoint(10, 10)));
    }

    [Fact]
    public void Preserves_aspect_ratio()
    {
        var system = new MapCoordinateSystem(Header(new MapPoint(0, 0), new MapPoint(20, 10)), 200, 200, 0);

        Assert.Equal(10, system.Scale);
        Assert.Equal(100, system.ToCanvas(new MapPoint(20, 10)).Y);
    }

    [Fact]
    public void Round_trip_to_physical_is_stable()
    {
        var system = new MapCoordinateSystem(Header(new MapPoint(-5, -2), new MapPoint(15, 8)), 300, 200, 12);
        var physical = new MapPoint(4.25, 3.75);

        var actual = system.ToPhysical(system.ToCanvas(physical));

        Assert.Equal(physical.X, actual.X, 6);
        Assert.Equal(physical.Y, actual.Y, 6);
    }

    private static SmapHeader Header(MapPoint min, MapPoint max) =>
        new("2D-Map", "test-map", min, max, 0.02, "1.0.6");
}
