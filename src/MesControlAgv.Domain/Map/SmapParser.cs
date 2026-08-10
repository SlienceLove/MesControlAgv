using System.Text.Json;

namespace MesControlAgv.Domain.Map;

public static class SmapParser
{
    public static SmapDocument Parse(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseDocument(document.RootElement);
        }
        catch (SmapParseException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new SmapParseException("The smap document is not valid JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new SmapParseException("The smap document uses an unsupported JSON shape.", exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new SmapParseException("The smap document is missing a required JSON property.", exception);
        }
    }

    public static async Task<SmapDocument> ParseFileAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        try
        {
            using var document = await JsonDocument.ParseAsync(source, cancellationToken: ct);
            return ParseDocument(document.RootElement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SmapParseException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new SmapParseException("The smap document is not valid JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new SmapParseException("The smap document uses an unsupported JSON shape.", exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new SmapParseException("The smap document is missing a required JSON property.", exception);
        }
    }

    private static SmapDocument ParseDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new SmapParseException("The smap document root must be a JSON object.");
        }

        var header = root.TryGetProperty("header", out var headerElement)
            ? ParseHeader(headerElement)
            : throw new KeyNotFoundException("header");

        return new SmapDocument(
            header,
            ParsePoints(root, "normalPosList"),
            ParseMarks(root),
            ParseLines(root),
            ParseCurves(root));
    }

    private static SmapHeader ParseHeader(JsonElement header) => new(
        String(header, "mapType"),
        String(header, "mapName"),
        Point(PropertyOrEmpty(header, "minPos")),
        Point(PropertyOrEmpty(header, "maxPos")),
        Number(header, "resolution"),
        String(header, "version"));

    private static IReadOnlyList<MapPoint> ParsePoints(JsonElement root, string propertyName)
    {
        if (!TryArray(root, propertyName, out var array)) return [];

        var points = new List<MapPoint>();
        foreach (var item in array.EnumerateArray())
        {
            points.Add(Point(item));
        }

        return points;
    }

    private static IReadOnlyList<SmapLocationMark> ParseMarks(JsonElement root)
    {
        if (!TryArray(root, "advancedPointList", out var array)) return [];

        var marks = new List<SmapLocationMark>();
        foreach (var item in array.EnumerateArray())
        {
            marks.Add(new SmapLocationMark(
                String(item, "instanceName"),
                Point(PropertyOrEmpty(item, "pos"))));
        }

        return marks;
    }

    private static IReadOnlyList<SmapFeatureLine> ParseLines(JsonElement root)
    {
        if (!TryArray(root, "advancedLineList", out var array)) return [];

        var lines = new List<SmapFeatureLine>();
        foreach (var item in array.EnumerateArray())
        {
            var line = item.TryGetProperty("line", out var lineElement) ? lineElement : item;
            lines.Add(new SmapFeatureLine(
                Point(PropertyOrEmpty(line, "startPos")),
                Point(PropertyOrEmpty(line, "endPos"))));
        }

        return lines;
    }

    private static IReadOnlyList<SmapCurve> ParseCurves(JsonElement root)
    {
        if (!TryArray(root, "advancedCurveList", out var array)) return [];

        var curves = new List<SmapCurve>();
        foreach (var item in array.EnumerateArray())
        {
            var start = PropertyOrEmpty(item, "startPos");
            var end = PropertyOrEmpty(item, "endPos");
            curves.Add(new SmapCurve(
                String(item, "instanceName"),
                String(start, "instanceName"),
                Point(PropertyOrEmpty(start, "pos")),
                String(end, "instanceName"),
                Point(PropertyOrEmpty(end, "pos")),
                Point(PropertyOrEmpty(item, "controlPos1")),
                Point(PropertyOrEmpty(item, "controlPos2")),
                PropertyInt(item, "direction"),
                PropertyInt(item, "movestyle")));
        }

        return curves;
    }

    private static int PropertyInt(JsonElement item, string key)
    {
        if (!TryArray(item, "property", out var array)) return 0;

        foreach (var property in array.EnumerateArray())
        {
            if (!String(property, "key").Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return Int(property, "int32Value");
        }

        return 0;
    }

    private static MapPoint Point(JsonElement element) => new(Coord(element, "x"), Coord(element, "y"));

    private static double Coord(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var result))
        {
            return 0.0;
        }

        return result;
    }

    private static JsonElement PropertyOrEmpty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property)
            ? property
            : default;

    private static string String(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static double Number(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetDouble(out var result)
            ? result
            : 0.0;

    private static int Int(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var result)
            ? result
            : 0;

    private static bool TryArray(JsonElement element, string propertyName, out JsonElement array)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        array = default;
        return false;
    }
}

public sealed class SmapParseException : Exception
{
    public SmapParseException(string message) : base(message)
    {
    }

    public SmapParseException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
