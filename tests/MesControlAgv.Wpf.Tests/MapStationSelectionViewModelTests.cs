using MesControlAgv.Domain.Map;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class MapStationSelectionViewModelTests
{
    [Fact]
    public void Builds_read_only_details_with_mapping_and_related_routes()
    {
        var selection = new MapStationSelectionViewModel();
        selection.ApplyLayout(Layout(), new StationMappingConfig(
        [new StationMappingEntry("LM1", "WS-01", "Load") ]));

        Assert.True(selection.Select("LM1"));
        Assert.NotNull(selection.SelectedDetail);
        var detail = selection.SelectedDetail!;
        Assert.Equal("LM1", detail.SmapStationId);
        Assert.Equal("WS-01", detail.MesStationId);
        Assert.Equal("Load", detail.DisplayName);
        Assert.Equal("(0, 0)", detail.CoordinateText);
        Assert.Equal(["LM1-LM2"], detail.RelatedRouteIds);
        Assert.True(detail.Enabled);
    }

    [Fact]
    public void Select_accepts_mes_alias_and_unknown_station_is_ignored()
    {
        var selection = new MapStationSelectionViewModel();
        selection.ApplyLayout(Layout(), new StationMappingConfig(
        [new StationMappingEntry("LM1", "WS-01", "Load") ]));

        Assert.True(selection.Select("WS-01"));
        Assert.Equal("LM1", selection.SelectedStationId);
        Assert.False(selection.Select("WS-99"));
        Assert.Equal("LM1", selection.SelectedStationId);
    }

    [Fact]
    public void Update_status_changes_enabled_state_without_changing_mapping()
    {
        var selection = new MapStationSelectionViewModel();
        selection.ApplyLayout(Layout(), new StationMappingConfig(
        [new StationMappingEntry("LM1", "WS-01", "Load") ]));

        Assert.True(selection.Select("LM1"));
        Assert.True(selection.UpdateStatus("WS-01", enabled: false));
        Assert.False(selection.SelectedDetail!.Enabled);
        Assert.Equal("Load", selection.SelectedDetail.DisplayName);
    }

    [Fact]
    public void Clear_removes_selection_and_layout_reset_removes_details()
    {
        var selection = new MapStationSelectionViewModel();
        selection.ApplyLayout(Layout(), StationMappingConfig.Empty);
        Assert.True(selection.Select("LM2"));

        selection.Clear();
        Assert.False(selection.HasSelection);
        selection.ApplyLayout(Layout() with { Stations = [] }, StationMappingConfig.Empty);
        Assert.Empty(selection.Stations);
    }

    private static MapLayout Layout() => new(
        new SmapHeader("2D-Map", "test-map", new MapPoint(0, 0), new MapPoint(10, 10), 0.02, "1.0"),
        [
            new MapStationLayout("LM1", new MapPoint(0, 0)),
            new MapStationLayout("LM2", new MapPoint(10, 10))
        ],
        [new MapRouteLayout(
            "LM1-LM2", "LM1", "LM2", new MapPoint(0, 0), new MapPoint(10, 10),
            new MapPoint(2, 2), new MapPoint(8, 8), 1)],
        new MapWallLayout([]),
        []);
}
