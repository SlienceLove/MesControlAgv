namespace MesControlAgv.Domain.Map;

public sealed class MapCoordinateSystem
{
    private readonly SmapHeader _header;
    private readonly double _padding;

    public MapCoordinateSystem(SmapHeader header, double canvasWidth, double canvasHeight, double padding = 24)
    {
        _header = header;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        _padding = padding;
        Scale = CalculateScale(header, canvasWidth, canvasHeight, padding);
    }

    public double CanvasWidth { get; }
    public double CanvasHeight { get; }
    public double Scale { get; }

    public MapPoint ToCanvas(MapPoint physical) => new(
        _padding + (physical.X - _header.MinPos.X) * Scale,
        CanvasHeight - _padding - (physical.Y - _header.MinPos.Y) * Scale);

    public MapPoint ToPhysical(MapPoint canvas) => new(
        ((canvas.X - _padding) / Scale) + _header.MinPos.X,
        ((CanvasHeight - _padding - canvas.Y) / Scale) + _header.MinPos.Y);

    private static double CalculateScale(SmapHeader header, double canvasWidth, double canvasHeight, double padding)
    {
        var availableWidth = Math.Max(0, canvasWidth - (2 * padding));
        var availableHeight = Math.Max(0, canvasHeight - (2 * padding));
        var rangeX = header.MaxPos.X - header.MinPos.X;
        var rangeY = header.MaxPos.Y - header.MinPos.Y;

        if (rangeX <= 0 && rangeY <= 0) return 1.0;
        if (rangeX <= 0) return availableHeight > 0 && rangeY > 0 ? availableHeight / rangeY : 1.0;
        if (rangeY <= 0) return availableWidth > 0 ? availableWidth / rangeX : 1.0;

        var scaleX = availableWidth > 0 ? availableWidth / rangeX : double.PositiveInfinity;
        var scaleY = availableHeight > 0 ? availableHeight / rangeY : double.PositiveInfinity;
        var scale = Math.Min(scaleX, scaleY);
        return double.IsFinite(scale) && scale > 0 ? scale : 1.0;
    }
}
