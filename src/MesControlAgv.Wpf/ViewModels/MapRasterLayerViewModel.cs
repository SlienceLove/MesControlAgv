using System.ComponentModel;
using System.Runtime.CompilerServices;
using MesControlAgv.Domain.Map;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// Optional scan-point background represented by one bounded Gray8 raster.
/// The layer deliberately has no per-point WPF elements and starts hidden.
/// </summary>
public sealed class MapRasterLayerViewModel : INotifyPropertyChanged
{
    public const int DefaultMaxRasterDimension = 2048;
    public const int MaximumMaxRasterDimension = 4096;
    public const int DefaultMaxSourcePointCount = 100_000;
    public const int MaximumMaxSourcePointCount = 1_000_000;

    private readonly int _maxRasterDimension;
    private readonly int _maxSourcePointCount;
    private bool _isVisible;
    private double _canvasWidth;
    private double _canvasHeight;
    private int _pixelWidth;
    private int _pixelHeight;
    private int _sourcePointCount;
    private int _sampledPointCount;
    private int _acceptedPointCount;
    private int _rasterPointCount;
    private MapRasterBounds _bounds = MapRasterBounds.Empty;
    private ReadOnlyMemory<byte> _pixelData = ReadOnlyMemory<byte>.Empty;

    public MapRasterLayerViewModel(
        int maxRasterDimension = DefaultMaxRasterDimension,
        int maxSourcePointCount = DefaultMaxSourcePointCount)
    {
        if (maxRasterDimension is < 1 or > MaximumMaxRasterDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRasterDimension),
                maxRasterDimension,
                $"The raster dimension must be between 1 and {MaximumMaxRasterDimension}.");
        }

        if (maxSourcePointCount is < 1 or > MaximumMaxSourcePointCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSourcePointCount),
                maxSourcePointCount,
                $"The source point limit must be between 1 and {MaximumMaxSourcePointCount}.");
        }

        _maxRasterDimension = maxRasterDimension;
        _maxSourcePointCount = maxSourcePointCount;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Whether a future Image/WriteableBitmap host should display this layer.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        private set => SetField(ref _isVisible, value);
    }

    /// <summary>Source canvas dimensions used for coordinate conversion, before pixel capping.</summary>
    public double CanvasWidth
    {
        get => _canvasWidth;
        private set => SetField(ref _canvasWidth, value);
    }

    public double CanvasHeight
    {
        get => _canvasHeight;
        private set => SetField(ref _canvasHeight, value);
    }

    /// <summary>Actual bounded raster width and height for a Gray8 image.</summary>
    public int PixelWidth
    {
        get => _pixelWidth;
        private set => SetField(ref _pixelWidth, value);
    }

    public int PixelHeight
    {
        get => _pixelHeight;
        private set => SetField(ref _pixelHeight, value);
    }

    /// <summary>One byte per pixel; zero is empty and non-zero is an occupied scan point.</summary>
    public int BytesPerPixel => 1;

    public int PixelStride => PixelWidth;

    public ReadOnlyMemory<byte> PixelData
    {
        get => _pixelData;
        private set => SetField(ref _pixelData, value);
    }

    public int SourcePointCount
    {
        get => _sourcePointCount;
        private set => SetField(ref _sourcePointCount, value);
    }

    public int SampledPointCount
    {
        get => _sampledPointCount;
        private set => SetField(ref _sampledPointCount, value);
    }

    public int AcceptedPointCount
    {
        get => _acceptedPointCount;
        private set => SetField(ref _acceptedPointCount, value);
    }

    /// <summary>Number of occupied raster cells after coordinate quantization.</summary>
    public int RasterPointCount
    {
        get => _rasterPointCount;
        private set => SetField(ref _rasterPointCount, value);
    }

    public bool HasData => RasterPointCount > 0;

    /// <summary>Canvas-space bounds of accepted points; empty when no point is renderable.</summary>
    public MapRasterBounds Bounds
    {
        get => _bounds;
        private set => SetField(ref _bounds, value);
    }

    public int MaxRasterDimension => _maxRasterDimension;
    public int MaxSourcePointCount => _maxSourcePointCount;

    /// <summary>Builds a layer without creating any WPF visual objects.</summary>
    public static MapRasterLayerViewModel Build(
        MapLayout? layout,
        double canvasWidth,
        double canvasHeight,
        bool isVisible = false,
        double padding = 0,
        int maxRasterDimension = DefaultMaxRasterDimension,
        int maxSourcePointCount = DefaultMaxSourcePointCount)
    {
        var layer = new MapRasterLayerViewModel(maxRasterDimension, maxSourcePointCount);
        layer.ApplyLayout(layout, canvasWidth, canvasHeight, padding);
        layer.SetVisible(isVisible);
        return layer;
    }

    /// <summary>
    /// Rebuilds the single raster input. Visibility is intentionally preserved across refreshes.
    /// </summary>
    public void ApplyLayout(
        MapLayout? layout,
        double canvasWidth,
        double canvasHeight,
        double padding = 0)
    {
        if (!double.IsFinite(canvasWidth) || canvasWidth <= 0) canvasWidth = 0;
        if (!double.IsFinite(canvasHeight) || canvasHeight <= 0) canvasHeight = 0;
        if (!double.IsFinite(padding) || padding < 0) padding = 0;

        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        var (pixelWidth, pixelHeight) = CalculatePixelDimensions(canvasWidth, canvasHeight);
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        OnPropertyChanged(nameof(PixelStride));
        var pixels = pixelWidth > 0 && pixelHeight > 0
            ? new byte[checked(pixelWidth * pixelHeight)]
            : Array.Empty<byte>();
        SourcePointCount = layout?.ScanPoints?.Count ?? 0;
        SampledPointCount = 0;
        AcceptedPointCount = 0;
        RasterPointCount = 0;
        Bounds = MapRasterBounds.Empty;

        if (layout?.ScanPoints is not { Count: > 0 } scanPoints || pixelWidth <= 0 || pixelHeight <= 0)
        {
            PixelData = pixels;
            OnPropertyChanged(nameof(HasData));
            return;
        }

        var sampleCount = Math.Min(scanPoints.Count, _maxSourcePointCount);
        SampledPointCount = sampleCount;
        var coordinates = new MapCoordinateSystem(layout.Header, canvasWidth, canvasHeight, padding);
        var physicalBounds = GetPhysicalBounds(layout.Header);
        var acceptedPointCount = 0;
        var rasterPointCount = 0;
        var minCanvasX = double.PositiveInfinity;
        var minCanvasY = double.PositiveInfinity;
        var maxCanvasX = double.NegativeInfinity;
        var maxCanvasY = double.NegativeInfinity;

        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var sourceIndex = sampleCount == 1
                ? 0
                : (int)(((long)sampleIndex * (scanPoints.Count - 1)) / (sampleCount - 1));
            var physical = scanPoints[sourceIndex];
            if (!IsFinite(physical) || !physicalBounds.Contains(physical)) continue;

            var canvas = coordinates.ToCanvas(physical);
            if (!IsFinite(canvas)) continue;

            acceptedPointCount++;
            minCanvasX = Math.Min(minCanvasX, canvas.X);
            minCanvasY = Math.Min(minCanvasY, canvas.Y);
            maxCanvasX = Math.Max(maxCanvasX, canvas.X);
            maxCanvasY = Math.Max(maxCanvasY, canvas.Y);

            var pixelX = ToPixel(canvas.X, canvasWidth, pixelWidth);
            var pixelY = ToPixel(canvas.Y, canvasHeight, pixelHeight);
            var offset = checked((pixelY * pixelWidth) + pixelX);
            if (pixels[offset] == 0)
            {
                pixels[offset] = byte.MaxValue;
                rasterPointCount++;
            }
        }

        AcceptedPointCount = acceptedPointCount;
        RasterPointCount = rasterPointCount;
        PixelData = pixels;
        Bounds = acceptedPointCount == 0
            ? MapRasterBounds.Empty
            : new MapRasterBounds(minCanvasX, minCanvasY, maxCanvasX, maxCanvasY);
        OnPropertyChanged(nameof(HasData));
    }

    public void SetVisible(bool visible) => IsVisible = visible;

    private (int Width, int Height) CalculatePixelDimensions(double canvasWidth, double canvasHeight)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0) return (0, 0);

        var width = (int)Math.Clamp(Math.Ceiling(canvasWidth), 1, _maxRasterDimension);
        var height = (int)Math.Clamp(Math.Ceiling(canvasHeight), 1, _maxRasterDimension);
        return (width, height);
    }

    private static int ToPixel(double coordinate, double canvasDimension, int pixelDimension)
    {
        if (pixelDimension <= 1 || canvasDimension <= 0) return 0;
        var normalized = Math.Clamp(coordinate / canvasDimension, 0, 1);
        return Math.Clamp((int)Math.Floor(normalized * pixelDimension), 0, pixelDimension - 1);
    }

    private static MapRasterBounds GetPhysicalBounds(SmapHeader header) =>
        IsFinite(header.MinPos) && IsFinite(header.MaxPos)
            ? new MapRasterBounds(
                Math.Min(header.MinPos.X, header.MaxPos.X),
                Math.Min(header.MinPos.Y, header.MaxPos.Y),
                Math.Max(header.MinPos.X, header.MaxPos.X),
                Math.Max(header.MinPos.Y, header.MaxPos.Y))
            : MapRasterBounds.Empty;

    private static bool IsFinite(MapPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>Canvas-space bounds used by the raster layer without WPF dependencies.</summary>
public readonly record struct MapRasterBounds(
    double MinX,
    double MinY,
    double MaxX,
    double MaxY)
{
    public static MapRasterBounds Empty => new(double.NaN, double.NaN, double.NaN, double.NaN);

    public bool IsEmpty =>
        !double.IsFinite(MinX) ||
        !double.IsFinite(MinY) ||
        !double.IsFinite(MaxX) ||
        !double.IsFinite(MaxY) ||
        MaxX < MinX ||
        MaxY < MinY;

    public double Width => IsEmpty ? 0 : MaxX - MinX;
    public double Height => IsEmpty ? 0 : MaxY - MinY;

    public bool Contains(MapPoint point) =>
        !IsEmpty &&
        point.X >= MinX && point.X <= MaxX &&
        point.Y >= MinY && point.Y <= MaxY;
}
