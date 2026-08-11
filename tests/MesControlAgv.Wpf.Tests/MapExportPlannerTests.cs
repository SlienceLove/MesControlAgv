using MesControlAgv.Wpf;

namespace MesControlAgv.Wpf.Tests;

public sealed class MapExportPlannerTests
{
    [Theory]
    [InlineData(MapExportMode.CurrentViewport)]
    [InlineData(MapExportMode.FullMap)]
    public void Create_preserves_mode_and_does_not_enlarge_safe_dimensions(MapExportMode mode)
    {
        var plan = MapExportPlanner.Create(mode, 1600, 900);

        Assert.Equal(mode, plan.Mode);
        Assert.Equal((1600d, 900d), (plan.SourceWidth, plan.SourceHeight));
        Assert.Equal((1600, 900), (plan.PixelWidth, plan.PixelHeight));
        Assert.Equal(1d, plan.Scale);
    }

    [Fact]
    public void Create_scales_proportionally_for_the_dimension_limit()
    {
        var plan = MapExportPlanner.Create(
            MapExportMode.FullMap,
            20_000,
            2_500,
            maxDimension: 8192,
            maxPixels: 32_000_000);

        Assert.Equal(0.4096d, plan.Scale, 6);
        Assert.Equal((8192, 1024), (plan.PixelWidth, plan.PixelHeight));
        Assert.InRange(plan.PixelWidth, 1, 8192);
        Assert.InRange(plan.PixelHeight, 1, 8192);
    }

    [Fact]
    public void Create_scales_down_for_the_total_pixel_limit()
    {
        var plan = MapExportPlanner.Create(
            MapExportMode.CurrentViewport,
            4000,
            4000,
            maxDimension: 8192,
            maxPixels: 4_000_000);

        Assert.Equal(0.5d, plan.Scale, 6);
        Assert.Equal((2000, 2000), (plan.PixelWidth, plan.PixelHeight));
        Assert.Equal(4_000_000, (long)plan.PixelWidth * plan.PixelHeight);
    }

    [Fact]
    public void Create_is_deterministic_for_identical_inputs()
    {
        var first = MapExportPlanner.Create(
            MapExportMode.CurrentViewport,
            12_345,
            6_789,
            maxDimension: 4096,
            maxPixels: 12_000_000);
        var second = MapExportPlanner.Create(
            MapExportMode.CurrentViewport,
            12_345,
            6_789,
            maxDimension: 4096,
            maxPixels: 12_000_000);

        Assert.Equal(first, second);
        Assert.True((long)first.PixelWidth * first.PixelHeight <= 12_000_000);
    }

    [Fact]
    public void Create_remains_bounded_for_extreme_but_finite_dimensions()
    {
        var plan = MapExportPlanner.Create(
            MapExportMode.FullMap,
            double.MaxValue,
            double.MaxValue,
            maxDimension: 8192,
            maxPixels: 1);

        Assert.True(double.IsFinite(plan.Scale));
        Assert.True(plan.Scale > 0);
        Assert.Equal((1, 1), (plan.PixelWidth, plan.PixelHeight));
        Assert.Equal(1, (long)plan.PixelWidth * plan.PixelHeight);
    }

    [Theory]
    [InlineData(0d, 1d)]
    [InlineData(-1d, 1d)]
    [InlineData(1d, 0d)]
    public void Create_rejects_nonpositive_source_dimensions(double width, double height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MapExportPlanner.Create(MapExportMode.CurrentViewport, width, height));
    }

    [Fact]
    public void Create_rejects_nonfinite_dimensions_invalid_mode_and_invalid_limits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MapExportPlanner.Create(MapExportMode.CurrentViewport, double.NaN, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MapExportPlanner.Create(MapExportMode.CurrentViewport, 100, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MapExportPlanner.Create((MapExportMode)999, 100, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MapExportPlanner.Create(MapExportMode.CurrentViewport, 100, 100, maxDimension: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MapExportPlanner.Create(MapExportMode.CurrentViewport, 100, 100, maxPixels: 0));
    }

    [Fact]
    public void CreatePngFileName_sanitizes_the_map_name_and_uses_a_png_extension()
    {
        var timestamp = new DateTimeOffset(2026, 8, 11, 12, 34, 56, TimeSpan.Zero);

        var fileName = MapExportPlanner.CreatePngFileName(
            " ../GZ:606? -- map.smap ",
            MapExportMode.FullMap,
            timestamp);

        Assert.Equal("GZ-606-map-smap-full-map-20260811-123456.png", fileName);
        Assert.Matches("^[A-Za-z0-9_-]+\\.png$", fileName);
        Assert.Equal("map", MapExportPlanner.SanitizeFileName(" /\\:*?\"<>| "));
    }
}
