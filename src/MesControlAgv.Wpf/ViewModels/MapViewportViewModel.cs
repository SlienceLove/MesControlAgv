using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class MapViewportViewModel : INotifyPropertyChanged
{
    private double _scale = 1;
    private double _offsetX;
    private double _offsetY;

    public const double MinScale = 0.2;
    public const double MaxScale = 8.0;

    public event PropertyChangedEventHandler? PropertyChanged;

    public double Scale
    {
        get => _scale;
        private set => SetField(ref _scale, Math.Clamp(value, MinScale, MaxScale));
    }

    public double OffsetX
    {
        get => _offsetX;
        private set => SetField(ref _offsetX, value);
    }

    public double OffsetY
    {
        get => _offsetY;
        private set => SetField(ref _offsetY, value);
    }

    public void ZoomAt(double anchorX, double anchorY, double delta)
    {
        var oldScale = Scale;
        var zoomFactor = delta > 0 ? 1.2 : 1 / 1.2;
        var newScale = Math.Clamp(oldScale * zoomFactor, MinScale, MaxScale);
        if (Math.Abs(newScale - oldScale) < double.Epsilon) return;

        var worldX = (anchorX - OffsetX) / oldScale;
        var worldY = (anchorY - OffsetY) / oldScale;
        Scale = newScale;
        OffsetX = anchorX - (worldX * newScale);
        OffsetY = anchorY - (worldY * newScale);
    }

    public void Pan(double dx, double dy)
    {
        OffsetX += dx;
        OffsetY += dy;
    }

    public void Reset()
    {
        Scale = 1;
        OffsetX = 0;
        OffsetY = 0;
    }

    public void FitToViewport(
        double viewportWidth,
        double viewportHeight,
        double contentWidth,
        double contentHeight,
        double margin = 24)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || contentWidth <= 0 || contentHeight <= 0)
        {
            Reset();
            return;
        }

        var availableWidth = Math.Max(1, viewportWidth - (2 * margin));
        var availableHeight = Math.Max(1, viewportHeight - (2 * margin));
        Scale = Math.Clamp(
            Math.Min(availableWidth / contentWidth, availableHeight / contentHeight),
            MinScale,
            MaxScale);
        OffsetX = (viewportWidth - (contentWidth * Scale)) / 2;
        OffsetY = (viewportHeight - (contentHeight * Scale)) / 2;
    }

    public void FitToBounds(
        double viewportWidth,
        double viewportHeight,
        MapViewportBounds bounds,
        double margin = 32,
        double maximumScale = MaxScale)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || !bounds.IsValid)
        {
            Reset();
            return;
        }

        var availableWidth = Math.Max(1, viewportWidth - (2 * margin));
        var availableHeight = Math.Max(1, viewportHeight - (2 * margin));
        var upperScale = Math.Clamp(maximumScale, MinScale, MaxScale);
        Scale = Math.Clamp(
            Math.Min(availableWidth / bounds.Width, availableHeight / bounds.Height),
            MinScale,
            upperScale);
        OffsetX = ((viewportWidth - (bounds.Width * Scale)) / 2) - (bounds.X * Scale);
        OffsetY = ((viewportHeight - (bounds.Height * Scale)) / 2) - (bounds.Y * Scale);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public readonly record struct MapViewportBounds(double X, double Y, double Width, double Height)
{
    public bool IsValid =>
        double.IsFinite(X) &&
        double.IsFinite(Y) &&
        double.IsFinite(Width) &&
        double.IsFinite(Height) &&
        Width > 0 &&
        Height > 0;
}
