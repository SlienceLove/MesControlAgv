using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf;

/// <summary>Converts one bounded Gray8 raster layer into a frozen WPF image source.</summary>
public sealed class MapRasterImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MapRasterLayerViewModel { PixelWidth: > 0, PixelHeight: > 0 } layer ||
            layer.PixelData.Length != layer.PixelWidth * layer.PixelHeight)
        {
            return null;
        }

        var image = BitmapSource.Create(
            layer.PixelWidth,
            layer.PixelHeight,
            96,
            96,
            PixelFormats.Gray8,
            null,
            layer.PixelData.ToArray(),
            layer.PixelStride);
        image.Freeze();
        return image;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
