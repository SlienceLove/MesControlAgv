using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MesControlAgv.Domain.Map;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class MapRasterImageSourceConverterTests
{
    [Fact]
    public void Convert_uses_a_transparent_background_and_dark_obstacle_palette()
    {
        var image = RunInSta(() =>
        {
            var layer = MapRasterLayerViewModel.Build(
                new MapLayout(
                    new SmapHeader("2D-Map", "test", new MapPoint(0, 0), new MapPoint(10, 10), 0.02, "1"),
                    [],
                    [],
                    new MapWallLayout([]),
                    [new MapPoint(5, 5)]),
                100,
                100,
                maxRasterDimension: 100);

            return Assert.IsAssignableFrom<BitmapSource>(
                new MapRasterImageSourceConverter().Convert(layer, typeof(ImageSource), null, CultureInfo.InvariantCulture));
        });

        Assert.Equal(PixelFormats.Indexed8, image.Format);
        var palette = image.Palette;
        Assert.NotNull(palette);
        Assert.Equal(0, palette!.Colors[0].A);
        Assert.Equal(Color.FromRgb(30, 41, 59), palette.Colors[byte.MaxValue]);
    }

    private static T RunInSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception error)
            {
                exception = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null) ExceptionDispatchInfo.Capture(exception).Throw();
        return result!;
    }
}
