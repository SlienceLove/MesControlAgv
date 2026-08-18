using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using Microsoft.AspNetCore.WebUtilities;

namespace MesControlAgv.Mes.Services;

public sealed class SampleWorkstationAdapterClient(HttpClient client) : ISampleWorkstationReader
{
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

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await TryReadDetailAsync(response, cancellationToken);
            throw new AdapterHttpException(response.StatusCode, detail);
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidOperationException("Adapter returned no sample workstation payload.");
    }

    private static string EscapeRequired(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : Uri.EscapeDataString(value.Trim());

    private static async Task<string?> TryReadDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetail>(cancellationToken);
            return problem?.Detail;
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record ProblemDetail(string? Detail);
}
