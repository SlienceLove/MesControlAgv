using System.Text;

namespace MesControlAgv.Wpf;

/// <summary>Identifies the map area selected for a raster export.</summary>
public enum MapExportMode
{
    CurrentViewport,
    FullMap
}

/// <summary>Immutable, bounded raster export dimensions derived from a logical map area.</summary>
public sealed record MapExportPlan(
    MapExportMode Mode,
    double SourceWidth,
    double SourceHeight,
    int PixelWidth,
    int PixelHeight,
    double Scale);

/// <summary>Calculates bounded export dimensions without creating WPF visual objects.</summary>
public static class MapExportPlanner
{
    public const int DefaultMaxDimension = 8192;
    public const long DefaultMaxPixels = 32_000_000;

    /// <summary>
    /// Creates a raster export plan. The scale is never greater than one and applies equally to
    /// both logical dimensions before the required integer pixel rounding.
    /// </summary>
    public static MapExportPlan Create(
        MapExportMode mode,
        double sourceWidth,
        double sourceHeight,
        int maxDimension = DefaultMaxDimension,
        long maxPixels = DefaultMaxPixels)
    {
        ValidateMode(mode);
        ValidateSourceDimension(sourceWidth, nameof(sourceWidth));
        ValidateSourceDimension(sourceHeight, nameof(sourceHeight));

        if (maxDimension <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDimension),
                maxDimension,
                "The maximum dimension must be positive.");
        }

        if (maxPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPixels),
                maxPixels,
                "The maximum pixel count must be positive.");
        }

        var scale = Math.Min(
            1d,
            Math.Min(
                Math.Min(maxDimension / sourceWidth, maxDimension / sourceHeight),
                GetPixelLimitScale(sourceWidth, sourceHeight, maxPixels)));

        // Positive finite inputs and limits always yield a positive scale. The fallback keeps
        // extreme but valid double values bounded even if a platform underflows Math.Exp.
        if (!double.IsFinite(scale) || scale <= 0)
        {
            scale = double.Epsilon;
        }

        var pixelWidth = ToPixelDimension(sourceWidth, scale, maxDimension);
        var pixelHeight = ToPixelDimension(sourceHeight, scale, maxDimension);
        (pixelWidth, pixelHeight) = ApplyPixelLimit(pixelWidth, pixelHeight, maxPixels);

        return new MapExportPlan(
            mode,
            sourceWidth,
            sourceHeight,
            pixelWidth,
            pixelHeight,
            scale);
    }

    /// <summary>Creates a safe default PNG name using the current UTC time.</summary>
    public static string CreatePngFileName(string? mapName, MapExportMode mode) =>
        CreatePngFileName(mapName, mode, DateTimeOffset.UtcNow);

    /// <summary>Creates a safe, deterministic PNG name for the supplied timestamp.</summary>
    public static string CreatePngFileName(string? mapName, MapExportMode mode, DateTimeOffset timestamp)
    {
        ValidateMode(mode);
        var safeMapName = SanitizeFileName(mapName);
        var modeName = mode == MapExportMode.CurrentViewport ? "current-viewport" : "full-map";
        return $"{safeMapName}-{modeName}-{timestamp.ToUniversalTime():yyyyMMdd-HHmmss}.png";
    }

    /// <summary>
    /// Produces an ASCII-only file-name stem. It retains letters, digits, underscores, and
    /// hyphens, normalizing runs of all other characters to one hyphen.
    /// </summary>
    public static string SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "map";

        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = false;
        foreach (var character in value.Trim())
        {
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
                previousWasSeparator = false;
                continue;
            }

            if (character is '-' or '_')
            {
                if (builder.Length > 0 && !previousWasSeparator)
                {
                    builder.Append(character);
                    previousWasSeparator = true;
                }

                continue;
            }

            if (builder.Length > 0 && !previousWasSeparator)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        var stem = builder.ToString().Trim('-', '_');
        return string.IsNullOrEmpty(stem) ? "map" : stem;
    }

    private static void ValidateMode(MapExportMode mode)
    {
        if (mode != MapExportMode.CurrentViewport && mode != MapExportMode.FullMap)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The export mode is not supported.");
        }
    }

    private static void ValidateSourceDimension(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "The source dimension must be a positive finite number.");
        }
    }

    private static double GetPixelLimitScale(double sourceWidth, double sourceHeight, long maxPixels)
    {
        var sourceArea = sourceWidth * sourceHeight;
        if (double.IsFinite(sourceArea) && sourceArea > 0)
        {
            return Math.Sqrt(maxPixels / sourceArea);
        }

        // Logs avoid overflowing sourceWidth * sourceHeight for valid, very large doubles.
        var logScale = (Math.Log(maxPixels) - Math.Log(sourceWidth) - Math.Log(sourceHeight)) / 2d;
        return Math.Exp(logScale);
    }

    private static int ToPixelDimension(double sourceDimension, double scale, int maxDimension)
    {
        var scaledDimension = sourceDimension * scale;
        if (!double.IsFinite(scaledDimension) || scaledDimension >= maxDimension) return maxDimension;
        if (scaledDimension <= 1d) return 1;
        // Round up so a tiny floating-point error in a proportional scale does not
        // drop the last source pixel. ApplyPixelLimit performs the final hard cap.
        return Math.Max(1, (int)Math.Ceiling(scaledDimension));
    }

    private static (int Width, int Height) ApplyPixelLimit(int width, int height, long maxPixels)
    {
        while ((long)width * height > maxPixels)
        {
            if (width >= height && width > 1)
            {
                width = Math.Max(1, (int)Math.Min(width, maxPixels / height));
                continue;
            }

            if (height > 1)
            {
                height = Math.Max(1, (int)Math.Min(height, maxPixels / width));
                continue;
            }

            throw new InvalidOperationException("A one-pixel export unexpectedly exceeded its pixel limit.");
        }

        return (width, height);
    }
}
