using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MesControlAgv.Wpf.Services;

/// <summary>Renders a WPF visual region into a scaled PNG file.</summary>
public sealed class MapPngExportService
{
    public const int MaximumPixelDimension = 8192;
    public const long MaximumPixelCount = 32_000_000;

    /// <summary>
    /// Exports <paramref name="sourceBounds"/> from <paramref name="source"/> to a PNG.
    /// This method must run on the source visual's dispatcher thread.
    /// </summary>
    public void Export(
        Visual source,
        Rect sourceBounds,
        string outputPath,
        int pixelWidth,
        int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateBounds(sourceBounds);
        ValidateOutputPath(outputPath);
        ValidatePixelSize(pixelWidth, pixelHeight);
        if (!source.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("The source visual must be rendered on its dispatcher thread.");
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new ArgumentException("The output path must include a directory.", nameof(outputPath));
        Directory.CreateDirectory(outputDirectory);

        var target = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, pixelWidth, pixelHeight));
            var brush = new VisualBrush(source)
            {
                Viewbox = sourceBounds,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, pixelWidth, pixelHeight),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill
            };
            context.DrawRectangle(brush, null, new Rect(0, 0, pixelWidth, pixelHeight));
        }

        target.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = new FileStream(fullOutputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static void ValidateBounds(Rect sourceBounds)
    {
        if (sourceBounds.IsEmpty ||
            !double.IsFinite(sourceBounds.X) ||
            !double.IsFinite(sourceBounds.Y) ||
            !double.IsFinite(sourceBounds.Width) ||
            !double.IsFinite(sourceBounds.Height) ||
            sourceBounds.Width <= 0 ||
            sourceBounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceBounds),
                "The source bounds must be finite and have positive dimensions.");
        }
    }

    private static void ValidateOutputPath(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("An output path is required.", nameof(outputPath));
        }
    }

    private static void ValidatePixelSize(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth is < 1 or > MaximumPixelDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        }

        if (pixelHeight is < 1 or > MaximumPixelDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        }

        if ((long)pixelWidth * pixelHeight > MaximumPixelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pixelHeight),
                "The requested image contains too many pixels.");
        }
    }
}
