using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace MesControlAgv.Adapter.Modules.SampleWorkstation;

public sealed record VendorSampleWorkstationResponse(int Code, JsonElement Data);

public sealed class SampleWorkstationProtocolException(string message) : InvalidOperationException(message);

public sealed class VendorSampleWorkstationHttpClient(HttpClient client)
{
    public Task<VendorSampleWorkstationResponse> GetAsync(
        string path,
        IReadOnlyDictionary<string, string?>? query,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, path, query, content: null, cancellationToken);

    public Task<VendorSampleWorkstationResponse> ExecuteCommandAsync(
        string path,
        IReadOnlyDictionary<string, string?>? query,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, path, query, content: null, cancellationToken);

    public Task<VendorSampleWorkstationResponse> PostAsync(
        string path,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        return SendAsync(HttpMethod.Post, path, query: null, content, cancellationToken);
    }

    internal async Task<VendorSampleWorkstationResponse> SendAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string?>? query,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var requestPath = query is null
            ? path
            : QueryHelpers.AddQueryString(path, query);
        using var request = new HttpRequestMessage(method, requestPath) { Content = content };
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Sample workstation returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new SampleWorkstationProtocolException(
                $"Sample workstation returned an empty or invalid JSON payload: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetProperty(root, "Code", out var codeElement)
                || !TryReadCode(codeElement, out var code)
                || !TryGetProperty(root, "Data", out var dataElement))
            {
                throw new SampleWorkstationProtocolException(
                    "Sample workstation returned an invalid Code/Data envelope.");
            }

            if (code != 200)
            {
                var detail = dataElement.ValueKind == JsonValueKind.String
                    ? dataElement.GetString()
                    : null;
                throw new SampleWorkstationProtocolException(
                    string.IsNullOrWhiteSpace(detail)
                        ? $"Sample workstation returned business code {code}."
                        : $"Sample workstation returned business code {code}: {detail.Trim()}");
            }

            return new VendorSampleWorkstationResponse(code, dataElement.Clone());
        }
    }

    private static bool TryReadCode(JsonElement element, out int code)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out code)) return true;
        if (element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
        {
            return true;
        }

        code = default;
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
