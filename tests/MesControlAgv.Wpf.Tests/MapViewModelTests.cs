using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class MapViewModelTests
{
    [Fact]
    public void Builds_directed_geometry_and_live_path_overlay_from_read_only_snapshots()
    {
        var map = new DashboardMapSnapshot(
            [
                new DashboardStation(1, "Alpha", "A", true),
                new DashboardStation(2, "Beta", "B", true),
                new DashboardStation(3, "Gamma", "C", true)
            ],
            [
                new MapEdgeResponse("A", "B", 1.5, false),
                new MapEdgeResponse("B", "C", 2, false)
            ],
            "agv-product",
            "profile-1",
            "test-map",
            "map-7",
            "abc123");
        var readiness = new PhysicalAgvPreflightResponse(
            new AgvSnapshotResponse(true, "mes", "A", null, "AGV-01"),
            new AgvSafetyReadinessResponse(
                "automatic", "controller", "test-map", "abc123", true, 1, false, true,
                false, false, 0, 0, 1, 0.99, DateTimeOffset.UtcNow),
            true,
            []);
        var fleet = new[]
        {
            new AgvFleetDashboardStatus(
                new AgvDashboardSnapshot(true, "mes", "A", null),
                new AgvActiveTaskStatus(Guid.NewGuid(), Guid.NewGuid(), "Moving", "device-1", "moving", "C", null, ["A", "B", "C"]))
        };

        var viewModel = new MapViewModel();
        viewModel.Update(map, readiness, fleet);

        Assert.Equal("SYNC", viewModel.SyncStatus);
        Assert.Equal(3, viewModel.Nodes.Count);
        Assert.Equal(2, viewModel.Edges.Count);
        Assert.Single(viewModel.Agvs);
        Assert.Equal(2, viewModel.AgvPathSegments.Count);
        Assert.Equal("A -> B -> C", viewModel.Agvs[0].PathText);
    }
}
