using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class MapPngExportServiceTests
{
    [Fact]
    public void Export_writes_a_png_at_the_requested_size_and_releases_the_file()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "MesControlAgv.Wpf.Tests", Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(outputDirectory, "map.png");
        try
        {
            var metadata = RunInSta(() =>
            {
                var source = new DrawingVisual();
                using (var context = source.RenderOpen())
                {
                    context.DrawRectangle(Brushes.OrangeRed, null, new Rect(0, 0, 10, 5));
                }

                new MapPngExportService().Export(source, new Rect(0, 0, 20, 10), outputPath, 60, 40);

                using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = Assert.Single(decoder.Frames);
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                var backgroundPixel = new byte[4];
                converted.CopyPixels(new Int32Rect(45, 30, 1, 1), backgroundPixel, 4, 0);
                return (frame.PixelWidth, frame.PixelHeight, BackgroundPixel: backgroundPixel);
            });

            Assert.Equal((60, 40), (metadata.PixelWidth, metadata.PixelHeight));
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, metadata.BackgroundPixel);
            var bytes = File.ReadAllBytes(outputPath);
            Assert.True(bytes.Length > 8);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        }
        finally
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void Export_rejects_invalid_arguments_before_rendering()
    {
        var service = new MapPngExportService();
        var source = RunInSta(() => new DrawingVisual());

        Assert.Throws<ArgumentNullException>(() => service.Export(null!, new Rect(0, 0, 1, 1), "map.png", 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Export(source, Rect.Empty, "map.png", 1, 1));
        Assert.Throws<ArgumentException>(() => service.Export(source, new Rect(0, 0, 1, 1), " ", 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Export(source, new Rect(0, 0, 1, 1), "map.png", 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Export(
            source,
            new Rect(0, 0, 1, 1),
            "map.png",
            MapPngExportService.MaximumPixelDimension,
            MapPngExportService.MaximumPixelDimension));
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
