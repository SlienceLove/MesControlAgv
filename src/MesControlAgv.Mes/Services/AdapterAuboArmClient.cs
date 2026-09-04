using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// MES projection of the AUBO arm Adapter routes. No vendor method or TCP detail
/// leaks into the MES process; the optional dispatch route is available only when
/// the Adapter was started with AuboArm ControlEnabled=true.
/// </summary>
public sealed class AdapterAuboArmClient(HttpClient client) : IAuboArmGateway
{
    public async Task<AuboArmStatusResponse> GetStatusAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        await GetAsync<AuboArmStatusResponse>(
            $"api/robot-arms/{Uri.EscapeDataString(deviceId)}/status",
            "Adapter returned no AUBO status.",
            cancellationToken);

    public async Task<AuboArmReadinessResponse> GetReadinessAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        await GetAsync<AuboArmReadinessResponse>(
            $"api/robot-arms/{Uri.EscapeDataString(deviceId)}/readiness",
            "Adapter returned no AUBO readiness result.",
            cancellationToken);

    public async Task<AuboArmVariableResponse> GetVariableAsync(
        string deviceId,
        string key,
        CancellationToken cancellationToken) =>
        await GetAsync<AuboArmVariableResponse>(
            $"api/robot-arms/{Uri.EscapeDataString(deviceId)}/variables/{Uri.EscapeDataString(key)}",
            "Adapter returned no AUBO variable result.",
            cancellationToken);

    public async Task<AuboArmHandshakeSnapshotResponse> GetHandshakeSnapshotAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        await GetAsync<AuboArmHandshakeSnapshotResponse>(
            $"api/robot-arms/{Uri.EscapeDataString(deviceId)}/handshake",
            "Adapter returned no AUBO handshake snapshot.",
            cancellationToken);

    public async Task<AuboArmHandshakeResultResponse> DispatchAsync(
        string deviceId,
        Guid operationId,
        int commandCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (operationId == Guid.Empty) throw new ArgumentException("An operation id is required.", nameof(operationId));

        using var response = await client.PostAsJsonAsync(
            $"api/robot-arms/{Uri.EscapeDataString(deviceId)}/handshake/dispatch",
            new AuboArmHandshakeDispatchRequest(commandCode, operationId),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AuboArmHandshakeResultResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Adapter returned no AUBO handshake result.");
    }

    public async Task<AuboArmProgramStatusResponse> GetProgramAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        await GetAsync<AuboArmProgramStatusResponse>(
            $"api/robot-arms/{Uri.EscapeDataString(deviceId)}/program",
            "Adapter returned no AUBO program status.",
            cancellationToken);

    public Task<AuboArmProgramCatalogResponse> GetProgramCatalogAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        GetProgramCatalogAsync(deviceId, forceFresh: false, cancellationToken);

    public async Task<AuboArmProgramCatalogResponse> GetProgramCatalogAsync(
        string deviceId,
        bool forceFresh,
        CancellationToken cancellationToken)
    {
        var path = $"api/robot-arms/{Uri.EscapeDataString(deviceId)}/programs";
        if (forceFresh) path += "?fresh=true";
        return await GetAsync<AuboArmProgramCatalogResponse>(
            path,
            "Adapter returned no AUBO program catalog.",
            cancellationToken).ConfigureAwait(false);
    }

    public Task<AuboArmProgramOperationResponse> LoadProgramAsync(
        string deviceId,
        string programName,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        LoadProgramAsync(deviceId, programName, operatorName, operationId, correlation: null, cancellationToken);

    public Task<AuboArmProgramOperationResponse> LoadProgramAsync(
        string deviceId,
        string programName,
        string operatorName,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken) =>
        SendProgramAsync(
            $"api/robot-arms/{Uri.EscapeDataString(deviceId)}/program/load",
            ApplyCorrelation(new AuboArmProgramRequest(
                RequireText(programName, nameof(programName)),
                RequireText(operatorName, nameof(operatorName)),
                operationId), correlation, operationId),
            correlation,
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> RunProgramAsync(
        string deviceId,
        string? programName,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        RunProgramAsync(deviceId, programName, operatorName, operationId, correlation: null, cancellationToken);

    public Task<AuboArmProgramOperationResponse> RunProgramAsync(
        string deviceId,
        string? programName,
        string operatorName,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken) =>
        SendProgramAsync(
            $"api/robot-arms/{Uri.EscapeDataString(deviceId)}/program/run",
            ApplyCorrelation(new AuboArmProgramRunRequest(
                programName,
                RequireText(operatorName, nameof(operatorName)),
                operationId), correlation, operationId),
            correlation,
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> StopProgramAsync(
        string deviceId,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        StopProgramAsync(deviceId, operatorName, operationId, correlation: null, cancellationToken);

    public Task<AuboArmProgramOperationResponse> StopProgramAsync(
        string deviceId,
        string operatorName,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken) =>
        SendProgramAsync(
            $"api/robot-arms/{Uri.EscapeDataString(deviceId)}/program/stop",
            ApplyCorrelation(new AuboArmProgramStopRequest(RequireText(operatorName, nameof(operatorName)), operationId), correlation, operationId),
            correlation,
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> LoadProgramAsync(
        string deviceId,
        AuboArmProgramRequest request,
        CancellationToken cancellationToken) =>
        LoadProgramAsync(
            deviceId,
            request.EffectiveProgramName,
            request.EffectiveOperatorName,
            request.OperationId.GetValueOrDefault(Guid.NewGuid()),
            request.WorkflowCorrelation,
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> RunProgramAsync(
        string deviceId,
        AuboArmProgramRunRequest request,
        CancellationToken cancellationToken) =>
        RunProgramAsync(
            deviceId,
            request.EffectiveProgramName,
            request.EffectiveOperatorName,
            request.OperationId.GetValueOrDefault(Guid.NewGuid()),
            request.WorkflowCorrelation,
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> StopProgramAsync(
        string deviceId,
        AuboArmProgramStopRequest request,
        CancellationToken cancellationToken) =>
        StopProgramAsync(
            deviceId,
            request.EffectiveOperatorName,
            request.OperationId.GetValueOrDefault(Guid.NewGuid()),
            request.WorkflowCorrelation,
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> LoadProgramAsync(
        string deviceId,
        string programName,
        string operatorName,
        CancellationToken cancellationToken) =>
        LoadProgramAsync(deviceId, programName, operatorName, Guid.NewGuid(), cancellationToken);

    public Task<AuboArmProgramOperationResponse> RunProgramAsync(
        string deviceId,
        string? programName,
        string operatorName,
        CancellationToken cancellationToken) =>
        RunProgramAsync(deviceId, programName, operatorName, Guid.NewGuid(), cancellationToken);

    public Task<AuboArmProgramOperationResponse> StopProgramAsync(
        string deviceId,
        string operatorName,
        CancellationToken cancellationToken) =>
        StopProgramAsync(deviceId, operatorName, Guid.NewGuid(), cancellationToken);

    private async Task<T> GetAsync<T>(string path, string emptyMessage, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidOperationException(emptyMessage);
    }

    private async Task<AuboArmProgramOperationResponse> SendProgramAsync(
        string path,
        object request,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(path, request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<AuboArmProgramOperationResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Adapter returned no AUBO program operation result.");
        return correlation is null
            ? result
            : result with
            {
                WorkflowRunId = correlation.WorkflowRunId,
                WorkflowNodeExecutionId = correlation.WorkflowNodeExecutionId,
                DeviceOperationId = correlation.DeviceOperationId,
                RequestId = correlation.RequestId,
                CorrelationId = correlation.EffectiveCorrelationId,
                Attempt = correlation.Attempt
            };
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new AdapterHttpException(response.StatusCode, ExtractDetail(body));
    }

    private static string? ExtractDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String
                    ? detail.GetString()
                    : body;
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static string RequireText(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value.Trim();
    }

    private static object ApplyCorrelation<TRequest>(
        TRequest request,
        AuboArmOperationCorrelation? correlation,
        Guid operationId)
        where TRequest : notnull
    {
        if (correlation is null) return request;
        if (!correlation.IsComplete)
            throw new ArgumentException(
                $"AUBO workflow correlation is incomplete: {correlation.ValidationError}",
                nameof(correlation));
        if (correlation.DeviceOperationId != operationId)
            throw new ArgumentException(
                "AUBO workflow correlation DeviceOperationId must equal operationId.",
                nameof(correlation));

        return request switch
        {
            AuboArmProgramRequest value => value with
            {
                WorkflowRunId = correlation.WorkflowRunId,
                WorkflowNodeExecutionId = correlation.WorkflowNodeExecutionId,
                DeviceOperationId = correlation.DeviceOperationId,
                RequestId = correlation.RequestId,
                CorrelationId = correlation.EffectiveCorrelationId,
                Attempt = correlation.Attempt
            },
            AuboArmProgramRunRequest value => value with
            {
                WorkflowRunId = correlation.WorkflowRunId,
                WorkflowNodeExecutionId = correlation.WorkflowNodeExecutionId,
                DeviceOperationId = correlation.DeviceOperationId,
                RequestId = correlation.RequestId,
                CorrelationId = correlation.EffectiveCorrelationId,
                Attempt = correlation.Attempt
            },
            AuboArmProgramStopRequest value => value with
            {
                WorkflowRunId = correlation.WorkflowRunId,
                WorkflowNodeExecutionId = correlation.WorkflowNodeExecutionId,
                DeviceOperationId = correlation.DeviceOperationId,
                RequestId = correlation.RequestId,
                CorrelationId = correlation.EffectiveCorrelationId,
                Attempt = correlation.Attempt
            },
            _ => throw new ArgumentException("Unsupported AUBO program request type.", nameof(request))
        };
    }
}
