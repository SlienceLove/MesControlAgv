using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using Microsoft.AspNetCore.WebUtilities;

namespace MesControlAgv.Mes.Services;

public sealed class SampleWorkstationAdapterClient(HttpClient client)
    : ISampleWorkstationReader, ISampleWorkstationCommands, ISampleWorkstationCapabilityReader
{
    public Task<SampleWorkstationCapabilitiesResponse> GetCapabilitiesAsync(
        string deviceId, CancellationToken cancellationToken) =>
        GetAsync<SampleWorkstationCapabilitiesResponse>(
            $"api/workstations/{EscapeRequired(deviceId, nameof(deviceId))}/capabilities", cancellationToken);

    public Task<SampleWorkstationCommandResponse> InitializeAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        PostAsync<SampleWorkstationCommandResponse>(
            $"api/workstations/{EscapeRequired(deviceId, nameof(deviceId))}/initialize",
            cancellationToken);

    public Task<SampleWorkstationCommandResponse> StartTaskAsync(
        string deviceId,
        string taskNo,
        CancellationToken cancellationToken) =>
        PostAsync<SampleWorkstationCommandResponse>(
            $"api/workstations/{EscapeRequired(deviceId, nameof(deviceId))}/tasks/{EscapeRequired(taskNo, nameof(taskNo))}/start",
            cancellationToken);

    public Task<SampleWorkstationStatusResponse> GetStatusAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        GetAsync<SampleWorkstationStatusResponse>(
            $"api/workstations/{EscapeRequired(deviceId, nameof(deviceId))}/status",
            cancellationToken);

    public Task<SampleWorkstationErrorResponse> GetErrorsAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        GetAsync<SampleWorkstationErrorResponse>(
            $"api/workstations/{EscapeRequired(deviceId, nameof(deviceId))}/errors",
            cancellationToken);

    public Task<IReadOnlyList<SampleWorkstationTaskSummaryResponse>> GetTasksAsync(
        string deviceId,
        SampleWorkstationTaskQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var path = QueryHelpers.AddQueryString(
            $"api/workstations/{EscapeRequired(deviceId, nameof(deviceId))}/tasks",
            new Dictionary<string, string?>
            {
                ["state"] = query.State,
                ["startDate"] = query.StartDate,
                ["endDate"] = query.EndDate,
                ["startNo"] = query.StartNo.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["recordNum"] = query.RecordNum.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        return GetAsync<IReadOnlyList<SampleWorkstationTaskSummaryResponse>>(path, cancellationToken);
    }

    public Task<SampleWorkstationTaskDetailsResponse> GetTaskDetailsAsync(
        string deviceId,
        string taskNo,
        CancellationToken cancellationToken) =>
        GetAsync<SampleWorkstationTaskDetailsResponse>(
            $"api/workstations/{EscapeRequired(deviceId, nameof(deviceId))}/tasks/{EscapeRequired(taskNo, nameof(taskNo))}",
            cancellationToken);

    public Task<SampleWorkstationTaskStateResponse> GetTaskStateAsync(
        string deviceId,
        string taskNo,
        CancellationToken cancellationToken) =>
        GetAsync<SampleWorkstationTaskStateResponse>(
            $"api/workstations/{EscapeRequired(deviceId, nameof(deviceId))}/tasks/{EscapeRequired(taskNo, nameof(taskNo))}/state",
            cancellationToken);

    public Task<SampleWorkstationProtocolResponse> GetProtocolReadAsync(
        string deviceId,
        SampleWorkstationProtocolOperation operation,
        SampleWorkstationProtocolReadQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var path = QueryHelpers.AddQueryString(
            $"api/workstations/{EscapeRequired(deviceId, nameof(deviceId))}/protocol/{operation}",
            new Dictionary<string, string?>
            {
                ["key"] = query.Key,
                ["startDate"] = query.StartDate,
                ["endDate"] = query.EndDate,
                ["startNo"] = query.StartNo.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["recordNum"] = query.RecordNum.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        return GetAsync<SampleWorkstationProtocolResponse>(path, cancellationToken);
    }

    private Task<T> GetAsync<T>(string path, CancellationToken cancellationToken) =>
        SendAsync<T>(HttpMethod.Get, path, content: null, cancellationToken);

    private Task<T> PostAsync<T>(string path, CancellationToken cancellationToken) =>
        SendAsync<T>(HttpMethod.Post, path, content: null, cancellationToken);

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await TryReadProblemAsync(response, cancellationToken);
            throw new SampleWorkstationGatewayException(
                (int)response.StatusCode,
                problem?.ErrorCode ?? SampleWorkstationErrorCodes.Unavailable,
                problem?.Detail ?? "The sample workstation Adapter returned an error.",
                problem?.OutcomeUnknown ?? method == HttpMethod.Post,
                problem?.VendorCode, problem?.VendorData);
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidOperationException("Adapter returned no sample workstation payload.");
    }

    private static string EscapeRequired(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : Uri.EscapeDataString(value.Trim());

    private static async Task<ProblemDetail?> TryReadProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetail>(cancellationToken);
            return problem;
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record ProblemDetail(
        string? Detail, string? ErrorCode, bool? OutcomeUnknown, int? VendorCode, JsonElement? VendorData);
}
