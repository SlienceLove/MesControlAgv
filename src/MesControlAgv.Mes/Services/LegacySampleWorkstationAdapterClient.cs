using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Devices;
using Microsoft.AspNetCore.WebUtilities;

namespace MesControlAgv.Mes.Services;

public sealed class LegacySampleWorkstationAdapterClient(HttpClient client) : ISampleWorkstationController, ISampleWorkstationReader
{
    public Task<SampleWorkstationProtocolResponse> GetProtocolReadAsync(
        string deviceId, SampleWorkstationProtocolOperation operation,
        SampleWorkstationProtocolReadQuery query, CancellationToken cancellationToken) =>
        new SampleWorkstationAdapterClient(client).GetProtocolReadAsync(deviceId, operation, query, cancellationToken);
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

    public Task<SampleWorkstationOperationResponse> InitializeAsync(
        string deviceId,
        SampleWorkstationOperationRequest request,
        CancellationToken cancellationToken) =>
        PostAsync(
            $"api/workstations/{EscapeRequired(deviceId, nameof(deviceId))}/legacy/initialize",
            request,
            cancellationToken);

    public Task<SampleWorkstationOperationResponse> CreateTaskAsync(
        string deviceId,
        SampleWorkstationTaskCreateRequest request,
        CancellationToken cancellationToken) =>
        PostAsync(
            $"api/workstations/{EscapeRequired(deviceId, nameof(deviceId))}/legacy/tasks",
            request,
            cancellationToken);

    public Task<SampleWorkstationOperationResponse> AddTrajectoryAsync(
        string deviceId,
        SampleWorkstationTrajectoryRequest request,
        CancellationToken cancellationToken) =>
        PostAsync(
            $"api/workstations/{EscapeRequired(deviceId, nameof(deviceId))}/legacy/tasks/{EscapeRequired(request.TaskNo, nameof(request.TaskNo))}/trajectory",
            request,
            cancellationToken);

    public Task<SampleWorkstationOperationResponse> StartExperimentAsync(
        string deviceId,
        SampleWorkstationStartRequest request,
        CancellationToken cancellationToken) =>
        PostAsync(
            $"api/workstations/{EscapeRequired(deviceId, nameof(deviceId))}/legacy/tasks/{EscapeRequired(request.TaskNo, nameof(request.TaskNo))}/start",
            request,
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
            ?? throw new JsonException("Adapter returned no sample workstation payload.");
    }

    private async Task<SampleWorkstationOperationResponse> PostAsync<T>(
        string path,
        T body,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(path, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var payload = await TryReadUnknownAsync(response, cancellationToken);
            if (payload is not null)
            {
                throw new SampleWorkstationAdapterUnknownException(
                    response.StatusCode,
                    payload.Detail,
                    payload.UnknownReason,
                    payload.Operation,
                    payload.OperationId,
                    payload.RunId,
                    payload.NodeExecutionId,
                    payload.VendorTaskId);
            }
            var detail = await TryReadDetailAsync(response, cancellationToken);
            throw new AdapterHttpException(response.StatusCode, detail);
        }

        return await response.Content.ReadFromJsonAsync<SampleWorkstationOperationResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Adapter returned no sample workstation operation payload.");
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

    private static async Task<UnknownPayload?> TryReadUnknownAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.Conflict) return null;
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<UnknownPayload>(cancellationToken);
            return payload?.State == DeviceOperationLifecycle.Unknown ? payload : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    private sealed record ProblemDetail(string? Detail);

    private sealed record UnknownPayload(
        string? Detail,
        DeviceOperationLifecycle State,
        UnknownReason? UnknownReason,
        string? Operation,
        Guid OperationId,
        Guid RunId,
        Guid NodeExecutionId,
        string? VendorTaskId);
}

public sealed class SampleWorkstationAdapterUnknownException(
    HttpStatusCode statusCode,
    string? detail,
    UnknownReason? unknownReason,
    string? operation,
    Guid operationId,
    Guid runId,
    Guid nodeExecutionId,
    string? vendorTaskId)
    : AdapterHttpException(statusCode, detail)
{
    public UnknownReason? UnknownReason { get; } = unknownReason;
    public string? Operation { get; } = operation;
    public Guid OperationId { get; } = operationId;
    public Guid RunId { get; } = runId;
    public Guid NodeExecutionId { get; } = nodeExecutionId;
    public string? VendorTaskId { get; } = vendorTaskId;
}
