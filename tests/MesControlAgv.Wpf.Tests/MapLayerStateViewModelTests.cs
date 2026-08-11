using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class MapLayerStateViewModelTests
{
    [Fact]
    public void Defaults_show_static_layers_and_obstacle_scan()
    {
        var layers = new MapLayerStateViewModel();

        Assert.True(layers.ShowWalls);
        Assert.True(layers.ShowRoutes);
        Assert.True(layers.ShowStationLabels);
        Assert.True(layers.ShowRuntimeOverlays);
        Assert.True(layers.ShowRasterBackground);
        Assert.True(layers.RuntimeOverlayAllowed);
    }

    [Fact]
    public void Runtime_overlay_is_fail_closed_when_layout_is_not_verified()
    {
        var layers = new MapLayerStateViewModel();

        layers.SetRuntimeOverlayAllowed(false);
        layers.ShowRuntimeOverlays = true;

        Assert.False(layers.ShowRuntimeOverlays);
        Assert.False(layers.IsVisible(MapLayerKind.RuntimeOverlays));
        Assert.True(layers.ShowRoutes);
        Assert.False(layers.RuntimeOverlayAllowed);
    }

    [Fact]
    public void Other_layers_can_toggle_independently()
    {
        var layers = new MapLayerStateViewModel();

        layers.SetVisible(MapLayerKind.Walls, false);
        layers.SetVisible(MapLayerKind.RasterBackground, true);
        layers.SetVisible(MapLayerKind.StationLabels, false);

        Assert.False(layers.ShowWalls);
        Assert.True(layers.ShowRoutes);
        Assert.False(layers.ShowStationLabels);
        Assert.True(layers.ShowRasterBackground);
    }

    [Fact]
    public void Restoring_permission_does_not_force_a_user_disabled_overlay_on()
    {
        var layers = new MapLayerStateViewModel();
        layers.ShowRuntimeOverlays = false;

        layers.SetRuntimeOverlayAllowed(false);
        layers.SetRuntimeOverlayAllowed(true);

        Assert.False(layers.ShowRuntimeOverlays);
    }
}
