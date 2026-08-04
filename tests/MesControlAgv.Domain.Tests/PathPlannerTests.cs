using MesControlAgv.Domain;

namespace MesControlAgv.Domain.Tests;

public sealed class PathPlannerTests
{
    [Fact]
    public void Default_map_uses_the_shortest_route()
    {
        var path = new PathPlanner(AgvMap.Default).Plan("CHARGE_01", "ST_PREP_01");

        Assert.Equal(["CHARGE_01", "PICK_01", "SAMPLE_01", "ST_PREP_01"], path.Stations);
        Assert.Equal(4, path.Cost);
    }

    [Fact]
    public void Planner_avoids_reserved_station_and_reports_when_no_route_exists()
    {
        var map = new AgvMap(
            ["A", "B", "C", "D"],
            [new("A", "B", 1), new("B", "D", 1), new("A", "C", 1), new("C", "D", 1)]);
        var planner = new PathPlanner(map);

        var alternate = planner.Plan("A", "D", new HashSet<string>(["B"]));
        Assert.Equal(["A", "C", "D"], alternate.Stations);

        Assert.Throws<InvalidOperationException>(() => planner.Plan("A", "D", new HashSet<string>(["B", "C"])));
    }
}
