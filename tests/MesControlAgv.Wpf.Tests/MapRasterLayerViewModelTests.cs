using MesControlAgv.Domain.Map;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class MapRasterLayerViewModelTests
{
    [Fact]
    public void Defaults_hidden_and_builds_one_bounded_gray8_input()
    {
        var layer = MapRasterLayerViewModel.Build(
            Layout([new MapPoint(0, 0), new MapPoint(10, 10)]),
            200,
            100,
            maxRasterDimension: 64);

        Assert.False(layer.IsVisible);
        Assert.Equal((200d, 100d), (layer.CanvasWidth, layer.CanvasHeight));
        Assert.Equal((64, 64), (layer.PixelWidth, layer.PixelHeight));
        Assert.Equal(1, layer.BytesPerPixel);
        Assert.Equal(layer.PixelWidth, layer.PixelStride);
        Assert.Equal(layer.PixelWidth * layer.PixelHeight, layer.PixelData.Length);
        Assert.Equal(2, layer.SourcePointCount);
        Assert.Equal(2, layer.SampledPointCount);
        Assert.Equal(2, layer.AcceptedPointCount);
        Assert.Equal(2, layer.RasterPointCount);
        Assert.True(layer.HasData);
    }

    [Fact]
    public void Maps_physical_points_to_canvas_with_y_axis_inversion_and_bounds()
    {
        var layer = MapRasterLayerViewModel.Build(
            Layout([new MapPoint(0, 0), new MapPoint(10, 10), new MapPoint(5, 5)]),
            100,
            100,
            maxRasterDimension: 100);

        Assert.Equal(new MapRasterBounds(0, 0, 100, 100), layer.Bounds);
        Assert.Equal(3, layer.PixelData.ToArray().Count(value => value != 0));
        Assert.Equal(255, layer.PixelData.Span[(99 * 100) + 0]);
        Assert.Equal(255, layer.PixelData.Span[99]);
        Assert.Equal(255, layer.PixelData.Span[(50 * 100) + 50]);
    }

    [Fact]
    public void Empty_layout_and_invalid_canvas_are_safe_and_clear_previous_data()
    {
        var layer = MapRasterLayerViewModel.Build(Layout([new MapPoint(1, 1)]), 100, 100);
        layer.SetVisible(true);

        layer.ApplyLayout(null, double.NaN, 0);

        Assert.True(layer.IsVisible);
        Assert.False(layer.HasData);
        Assert.Equal(0, layer.SourcePointCount);
        Assert.Equal((0, 0), (layer.PixelWidth, layer.PixelHeight));
        Assert.Empty(layer.PixelData.ToArray());
        Assert.True(layer.Bounds.IsEmpty);
    }

    [Fact]
    public void Samples_large_source_deterministically_and_caps_raster_dimensions()
    {
        var points = Enumerable.Range(0, 10_000)
            .Select(index => new MapPoint(index % 101, index % 101))
            .ToArray();
        var layout = Layout(points);

        var first = MapRasterLayerViewModel.Build(
            layout,
            10_000,
            8_000,
            maxRasterDimension: 32,
            maxSourcePointCount: 100);
        var second = MapRasterLayerViewModel.Build(
            layout,
            10_000,
            8_000,
            maxRasterDimension: 32,
            maxSourcePointCount: 100);

        Assert.Equal(10_000, first.SourcePointCount);
        Assert.Equal(100, first.SampledPointCount);
        Assert.InRange(first.AcceptedPointCount, 1, 100);
        Assert.Equal((32, 32), (first.PixelWidth, first.PixelHeight));
        Assert.Equal(first.PixelData.ToArray(), second.PixelData.ToArray());
        Assert.Equal(first.Bounds, second.Bounds);
    }

    [Fact]
    public void Skips_nonfinite_and_out_of_map_points_without_expanding_bounds()
    {
        var layer = MapRasterLayerViewModel.Build(
            Layout([
                new MapPoint(double.NaN, 1),
                new MapPoint(-1, 5),
                new MapPoint(5, 5),
                new MapPoint(11, 5),
                new MapPoint(5, double.PositiveInfinity)
            ]),
            100,
            100,
            maxRasterDimension: 100);

        Assert.Equal(5, layer.SourcePointCount);
        Assert.Equal(5, layer.SampledPointCount);
        Assert.Equal(1, layer.AcceptedPointCount);
        Assert.Equal(new MapRasterBounds(50, 50, 50, 50), layer.Bounds);
        Assert.Equal(1, layer.RasterPointCount);
    }

    [Fact]
    public void Visibility_can_be_toggled_without_rebuilding_or_mutating_pixels()
    {
        var layer = MapRasterLayerViewModel.Build(
            Layout([new MapPoint(2, 3)]),
            100,
            100);
        var before = layer.PixelData.ToArray();

        layer.SetVisible(true);

        Assert.True(layer.IsVisible);
        Assert.Equal(before, layer.PixelData.ToArray());
        layer.SetVisible(false);
        Assert.False(layer.IsVisible);
    }

    [Fact]
    public void Rejects_unsafe_constructor_limits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapRasterLayerViewModel(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapRasterLayerViewModel(
            MapRasterLayerViewModel.MaximumMaxRasterDimension + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapRasterLayerViewModel(
            maxSourcePointCount: 0));
    }

    private static MapLayout Layout(IReadOnlyList<MapPoint> scanPoints) => new(
        new SmapHeader("2D-Map", "test-map", new MapPoint(0, 0), new MapPoint(10, 10), 0.02, "1.0.6"),
        [],
        [],
        new MapWallLayout([]),
        scanPoints);
}
