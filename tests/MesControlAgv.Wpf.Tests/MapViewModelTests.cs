using MesControlAgv.Contracts;
using MesControlAgv.Domain.Map;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class MapViewModelTests
{
    [Fact]
    public void ApplyLayout_uses_station_coordinates_as_node_centers()
    {
        var viewModel = new MapViewModel();

        viewModel.ApplyLayout(Layout(), StationMappingConfig.Empty, 200, 200);

        Assert.True(viewModel.UsingSmapLayout);
        Assert.Equal(2, viewModel.Nodes.Count);
        Assert.Equal(0, viewModel.Nodes[0].X);
        Assert.Equal(114, viewModel.Nodes[0].Y);
        Assert.Equal(88, viewModel.Nodes[1].X);
        Assert.Equal(26, viewModel.Nodes[1].Y);
        var edge = Assert.Single(viewModel.Edges);
        Assert.Equal("LM1", edge.From);
        Assert.Equal("LM2", edge.To);
        Assert.Equal(viewModel.Nodes[0].X + 56, edge.X1);
        Assert.Equal(viewModel.Nodes[0].Y + 30, edge.Y1);
        Assert.Equal(viewModel.Nodes[1].X + 56, edge.X2);
        Assert.Equal(viewModel.Nodes[1].Y + 30, edge.Y2);
        Assert.Equal(73.6, edge.Control1X, 6);
        Assert.Equal(126.4, edge.Control1Y, 6);
        Assert.InRange(viewModel.Nodes.Min(node => node.X), 0, viewModel.CanvasWidth - 112);
        Assert.InRange(viewModel.Nodes.Max(node => node.X + 112), 112, viewModel.CanvasWidth);
        Assert.InRange(viewModel.Nodes.Min(node => node.Y), 0, viewModel.CanvasHeight - 60);
        Assert.InRange(viewModel.Nodes.Max(node => node.Y + 60), 60, viewModel.CanvasHeight);
        Assert.Equal(new MapViewportBounds(0, 26, 200, 148), viewModel.NavigationBounds);
    }

    [Fact]
    public void ApplyLayout_does_not_guess_bidirectionality_from_direction_field()
    {
        var viewModel = new MapViewModel();

        viewModel.ApplyLayout(Layout(direction: 37), StationMappingConfig.Empty, 200, 200);

        var edge = Assert.Single(viewModel.Edges);
        Assert.False(edge.Bidirectional);
        Assert.Equal("LM1", edge.From);
        Assert.Equal("LM2", edge.To);
        Assert.InRange(edge.ArrowX, Math.Min(edge.X1, edge.X2), Math.Max(edge.X1, edge.X2));
        Assert.InRange(edge.ArrowY, Math.Min(edge.Y1, edge.Y2), Math.Max(edge.Y1, edge.Y2));
        Assert.NotEqual(edge.X2, edge.ArrowX);
        Assert.NotEqual(edge.Y2, edge.ArrowY);
    }

    [Fact]
    public void Route_arrow_is_placed_inside_a_straight_edge_with_forward_heading()
    {
        var edge = new MapEdgeViewModel("A", "B", 1, false, 0, 0, 10, 0);

        Assert.Equal(6.2, edge.ArrowX, 6);
        Assert.Equal(0, edge.ArrowY, 6);
        Assert.Equal(0, edge.ArrowAngle, 6);
    }

    [Fact]
    public void ApplyLayout_keeps_explicit_forward_and_reverse_curves()
    {
        var forward = Layout().Routes[0];
        var reverse = forward with
        {
            Id = "LM2-LM1",
            FromStationId = "LM2",
            ToStationId = "LM1",
            From = forward.To,
            To = forward.From,
            Control1 = forward.Control2,
            Control2 = forward.Control1,
            Direction = 0
        };
        var layout = Layout() with { Routes = [forward, reverse] };
        var viewModel = new MapViewModel();

        viewModel.ApplyLayout(layout, StationMappingConfig.Empty, 200, 200);

        Assert.Collection(
            viewModel.Edges,
            edge =>
            {
                Assert.Equal(("LM1", "LM2"), (edge.From, edge.To));
                Assert.True(edge.Bidirectional);
            },
            edge =>
            {
                Assert.Equal(("LM2", "LM1"), (edge.From, edge.To));
                Assert.True(edge.Bidirectional);
            });
    }

    [Fact]
    public void ApplyLayout_prefers_mapping_display_name()
    {
        var viewModel = new MapViewModel();
        var mapping = new StationMappingConfig(
        [
            new StationMappingEntry("LM1", "WS-01", "上料位")
        ]);

        viewModel.ApplyLayout(Layout(), mapping, 200, 200);

        Assert.Equal("上料位", viewModel.Nodes.Single(node => node.StationId == "LM1").Name);
    }

    [Fact]
    public void ApplyLayout_builds_hidden_raster_and_station_inspector_data()
    {
        var viewModel = new MapViewModel();
        var layout = Layout() with
        {
            ScanPoints = [new MapPoint(0, 0), new MapPoint(5, 5), new MapPoint(10, 10)]
        };

        viewModel.ApplyLayout(layout, StationMappingConfig.Empty, 200, 200);

        Assert.True(viewModel.Raster.HasData);
        Assert.False(viewModel.Raster.IsVisible);
        Assert.Equal(2, viewModel.StationSelection.Stations.Count);
        Assert.True(viewModel.SelectStation("LM1"));
        Assert.Equal("LM1", viewModel.StationSelection.SelectedDetail!.SmapStationId);

        viewModel.Layers.ShowRasterBackground = true;

        Assert.True(viewModel.Raster.IsVisible);
    }

    [Fact]
    public void SetSmapOverlayAllowed_closes_runtime_layer_and_user_cannot_reenable_it()
    {
        var viewModel = new MapViewModel();
        viewModel.ApplyLayout(Layout(), StationMappingConfig.Empty, 200, 200);

        viewModel.SetSmapOverlayAllowed(false);
        viewModel.Layers.ShowRuntimeOverlays = true;

        Assert.False(viewModel.SmapOverlayAllowed);
        Assert.False(viewModel.Layers.ShowRuntimeOverlays);
        Assert.False(viewModel.Layers.RuntimeOverlayAllowed);
    }

    [Fact]
    public void Update_after_ApplyLayout_keeps_smap_positions()
    {
        var viewModel = new MapViewModel();
        viewModel.ApplyLayout(Layout(), StationMappingConfig.Empty, 200, 200);
        var before = viewModel.Nodes.Single(node => node.StationId == "LM1");

        viewModel.Update(
            Snapshot(),
            null,
            [
                new AgvFleetDashboardStatus(
                    new AgvDashboardSnapshot(true, "mes", "LM1", null, "AGV-01"),
                    new AgvActiveTaskStatus(Guid.NewGuid(), Guid.NewGuid(), "Moving", "device-1", "moving", "LM2", null, ["LM1", "LM2"]))
            ]);

        var after = viewModel.Nodes.Single(node => node.StationId == "LM1");
        Assert.Equal(before.X, after.X);
        Assert.Equal(before.Y, after.Y);
        Assert.Single(viewModel.Agvs);
        Assert.Equal("LM1", viewModel.Agvs[0].CurrentStation);
        Assert.Equal(viewModel.Nodes[0].X + 56, viewModel.Agvs[0].X);
        Assert.Equal(viewModel.Nodes[0].Y + 30, viewModel.Agvs[0].Y);
        var path = Assert.Single(viewModel.AgvPathSegments);
        var edge = Assert.Single(viewModel.Edges);
        Assert.Equal((edge.X1, edge.Y1), (path.X1, path.Y1));
        Assert.Equal((edge.X2, edge.Y2), (path.X2, path.Y2));
    }

    [Fact]
    public void Update_without_ApplyLayout_uses_autolayout()
    {
        var viewModel = new MapViewModel();

        viewModel.Update(Snapshot(), null, []);

        Assert.False(viewModel.UsingSmapLayout);
        Assert.Equal(56, viewModel.Nodes[0].X);
        Assert.Equal(54, viewModel.Nodes[0].Y);
    }

    [Fact]
    public void Identical_autolayout_refresh_does_not_report_navigation_bounds_changed()
    {
        var viewModel = new MapViewModel();
        viewModel.Update(Snapshot(), null, []);
        var changes = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MapViewModel.NavigationBounds)) changes++;
        };

        viewModel.Update(Snapshot(), null, []);

        Assert.Equal(0, changes);
    }

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

        Assert.Equal("一致", viewModel.SyncStatus);
        Assert.Equal(3, viewModel.Nodes.Count);
        Assert.Equal(2, viewModel.Edges.Count);
        Assert.Single(viewModel.Agvs);
        Assert.Equal(2, viewModel.AgvPathSegments.Count);
        Assert.Equal("A -> B -> C", viewModel.Agvs[0].PathText);
    }

    private static MapLayout Layout(int direction = 1) => new(
        new SmapHeader("2D-Map", "smap", new MapPoint(0, 0), new MapPoint(10, 10), 0.02, "1.0.6"),
        [
            new MapStationLayout("LM1", new MapPoint(0, 0)),
            new MapStationLayout("LM2", new MapPoint(10, 10))
        ],
        [
            new MapRouteLayout(
                "LM1-LM2",
                "LM1",
                "LM2",
                new MapPoint(0, 0),
                new MapPoint(10, 10),
                new MapPoint(2, 2),
                new MapPoint(8, 8),
                direction)
        ],
        new MapWallLayout([]),
        []);

    private static DashboardMapSnapshot Snapshot() => new(
        [
            new DashboardStation(1, "Alpha", "A", true),
            new DashboardStation(2, "Beta", "B", true)
        ],
        [new MapEdgeResponse("A", "B", 1, false)],
        "agv-product",
        "profile-1",
        "test-map",
        "map-7",
        "abc123");
}
