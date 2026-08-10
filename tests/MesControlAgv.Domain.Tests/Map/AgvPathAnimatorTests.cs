using MesControlAgv.Domain.Map;

namespace MesControlAgv.Domain.Tests.Map;

public sealed class AgvPathAnimatorTests
{
    [Fact]
    public void PointOnCurve_at_t0_is_start_and_t1_is_end()
    {
        var start = new MapPoint(0, 0);
        var end = new MapPoint(10, 5);

        Assert.Equal(start, AgvPathAnimator.PointOnCurve(start, new MapPoint(1, 2), new MapPoint(3, 4), end, 0));
        Assert.Equal(end, AgvPathAnimator.PointOnCurve(start, new MapPoint(1, 2), new MapPoint(3, 4), end, 1));
    }

    [Fact]
    public void Straight_line_midpoint_when_controls_on_line()
    {
        var point = AgvPathAnimator.PointOnCurve(
            new MapPoint(0, 0),
            new MapPoint(0, 0),
            new MapPoint(10, 0),
            new MapPoint(10, 0),
            0.5);

        Assert.Equal(5, point.X, 6);
        Assert.Equal(0, point.Y, 6);
    }

    [Fact]
    public void Heading_of_horizontal_line_is_zero()
    {
        var heading = AgvPathAnimator.HeadingRadians(
            new MapPoint(0, 0),
            new MapPoint(0, 0),
            new MapPoint(10, 0),
            new MapPoint(10, 0),
            0.5);

        Assert.Equal(0, heading, 6);
    }
}
