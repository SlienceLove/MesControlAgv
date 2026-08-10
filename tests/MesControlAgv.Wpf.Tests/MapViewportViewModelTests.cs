using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class MapViewportViewModelTests
{
    [Fact]
    public void Zoom_clamps_to_bounds()
    {
        var viewport = new MapViewportViewModel();

        for (var index = 0; index < 100; index++) viewport.ZoomAt(100, 100, 120);

        Assert.Equal(MapViewportViewModel.MaxScale, viewport.Scale);

        for (var index = 0; index < 100; index++) viewport.ZoomAt(100, 100, -120);

        Assert.Equal(MapViewportViewModel.MinScale, viewport.Scale);
    }

    [Fact]
    public void ZoomAt_keeps_anchor_point_stationary()
    {
        var viewport = new MapViewportViewModel();
        viewport.Pan(20, -10);
        const double anchorX = 140;
        const double anchorY = 90;
        var worldX = (anchorX - viewport.OffsetX) / viewport.Scale;
        var worldY = (anchorY - viewport.OffsetY) / viewport.Scale;

        viewport.ZoomAt(anchorX, anchorY, 120);

        Assert.Equal(anchorX, (worldX * viewport.Scale) + viewport.OffsetX, 6);
        Assert.Equal(anchorY, (worldY * viewport.Scale) + viewport.OffsetY, 6);
    }

    [Fact]
    public void Pan_accumulates_offsets()
    {
        var viewport = new MapViewportViewModel();

        viewport.Pan(10, -5);
        viewport.Pan(2.5, 4);

        Assert.Equal(12.5, viewport.OffsetX);
        Assert.Equal(-1, viewport.OffsetY);
    }

    [Fact]
    public void Reset_restores_defaults()
    {
        var viewport = new MapViewportViewModel();
        viewport.Pan(10, -5);
        viewport.ZoomAt(100, 100, 120);

        viewport.Reset();

        Assert.Equal(1, viewport.Scale);
        Assert.Equal(0, viewport.OffsetX);
        Assert.Equal(0, viewport.OffsetY);
    }

    [Fact]
    public void FitToViewport_centers_content_within_available_space()
    {
        var viewport = new MapViewportViewModel();

        viewport.FitToViewport(1000, 700, 800, 400);

        Assert.Equal(1.19, viewport.Scale, 2);
        Assert.Equal(24, viewport.OffsetX, 6);
        Assert.Equal(111.999, viewport.OffsetY, 2);
    }

    [Fact]
    public void FitToViewport_resets_when_dimensions_are_invalid()
    {
        var viewport = new MapViewportViewModel();
        viewport.Pan(10, 20);
        viewport.FitToViewport(0, 700, 800, 400);

        Assert.Equal(1, viewport.Scale);
        Assert.Equal(0, viewport.OffsetX);
        Assert.Equal(0, viewport.OffsetY);
    }

    [Fact]
    public void FitToBounds_centers_a_subregion_and_honors_maximum_scale()
    {
        var viewport = new MapViewportViewModel();

        viewport.FitToBounds(
            1000,
            700,
            new MapViewportBounds(200, 100, 200, 100),
            margin: 50,
            maximumScale: 2.5);

        Assert.Equal(2.5, viewport.Scale);
        Assert.Equal(-250, viewport.OffsetX, 6);
        Assert.Equal(-25, viewport.OffsetY, 6);
        Assert.Equal(250, (200 * viewport.Scale) + viewport.OffsetX, 6);
        Assert.Equal(750, (400 * viewport.Scale) + viewport.OffsetX, 6);
    }

    [Fact]
    public void FitToBounds_resets_when_bounds_are_invalid()
    {
        var viewport = new MapViewportViewModel();
        viewport.Pan(10, 20);

        viewport.FitToBounds(1000, 700, new MapViewportBounds(0, 0, 0, 100));

        Assert.Equal(1, viewport.Scale);
        Assert.Equal(0, viewport.OffsetX);
        Assert.Equal(0, viewport.OffsetY);
    }
}
