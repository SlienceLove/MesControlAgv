using System.Text.Json;

namespace MesControlAgv.Domain.Map;

public static class StationMappingLoader
{
    public static async Task<StationMappingConfig> LoadFileAsync(string? path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return StationMappingConfig.Empty;
        }

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
            return Parse(document.RootElement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new SmapParseException("The station mapping document is not valid JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new SmapParseException("The station mapping document uses an unsupported JSON shape.", exception);
        }
    }

    private static StationMappingConfig Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new SmapParseException("The station mapping document root must be a JSON object.");
        }

        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return StationMappingConfig.Empty;
        }

        var result = new List<StationMappingEntry>();
        foreach (var item in entries.EnumerateArray())
        {
            result.Add(new StationMappingEntry(
                String(item, "smapMark"),
                NullableString(item, "mesAgvStationId"),
                NullableString(item, "displayName")));
        }

        return new StationMappingConfig(result);
    }

    private static string String(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static string? NullableString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
