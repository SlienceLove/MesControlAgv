namespace MesControlAgv.Domain.Tests;

public class StationsTests
{
    [Fact]
    public void Sample_station_maps_to_ascii_machine_id()
    {
        var station = Stations.Get(2);

        Assert.Equal("样品位", station.Name);
        Assert.Equal("SAMPLE_01", station.AgvStationId);
    }

    [Fact]
    public void Station_catalog_contains_all_seven_fixed_stations()
    {
        Assert.Equal(7, Stations.All.Count);
        Assert.All(Stations.All, station => Assert.True(station.AgvStationId.All(char.IsAscii)));
    }
}
