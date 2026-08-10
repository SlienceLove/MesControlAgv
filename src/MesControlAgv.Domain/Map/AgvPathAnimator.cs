namespace MesControlAgv.Domain.Map;

public static class AgvPathAnimator
{
    public static MapPoint PointOnCurve(MapPoint p0, MapPoint c1, MapPoint c2, MapPoint p3, double t)
    {
        var clamped = Math.Clamp(t, 0, 1);
        var u = 1 - clamped;
        var uu = u * u;
        var tt = clamped * clamped;
        var uuu = uu * u;
        var ttt = tt * clamped;

        return new MapPoint(
            (uuu * p0.X) + (3 * uu * clamped * c1.X) + (3 * u * tt * c2.X) + (ttt * p3.X),
            (uuu * p0.Y) + (3 * uu * clamped * c1.Y) + (3 * u * tt * c2.Y) + (ttt * p3.Y));
    }

    public static double HeadingRadians(MapPoint p0, MapPoint c1, MapPoint c2, MapPoint p3, double t)
    {
        var clamped = Math.Clamp(t, 0, 1);
        var u = 1 - clamped;
        var dx =
            (3 * u * u * (c1.X - p0.X)) +
            (6 * u * clamped * (c2.X - c1.X)) +
            (3 * clamped * clamped * (p3.X - c2.X));
        var dy =
            (3 * u * u * (c1.Y - p0.Y)) +
            (6 * u * clamped * (c2.Y - c1.Y)) +
            (3 * clamped * clamped * (p3.Y - c2.Y));

        return Math.Atan2(dy, dx);
    }
}
