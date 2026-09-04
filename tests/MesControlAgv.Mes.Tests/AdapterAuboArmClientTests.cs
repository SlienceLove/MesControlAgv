using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Tests;

public sealed class AdapterAuboArmClientTests
{
    [Fact]
    public async Task Readiness_uses_the_adapter_arm_route()
    {
        var expected = new AuboArmReadinessResponse(
            "ARM-01",
            true,
            [],
            new AuboArmStatusResponse(
                "ARM-01",
                "rob1",
                true,
                AuboArmMode.Running,
                8,
                AuboArmSafetyMode.Normal,
                1,
                AuboArmRuntimeState.Running,
                0,
                AuboArmOperationalMode.Automatic,
                1,
                DateTimeOffset.Parse("2026-08-27T03:00:00Z")),
            "mes-trigger",
            DateTimeOffset.Parse("2026-08-27T03:00:00Z"));
        var handler = new RecordingHandler(_ => JsonResponse(expected));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://adapter/") };
        var client = new AdapterAuboArmClient(httpClient);

        var result = await client.GetReadinessAsync("ARM-01", CancellationToken.None);

        Assert.True(result.Ready);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/robot-arms/ARM-01/readiness", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Dispatch_posts_operation_and_command_code()
    {
        var operationId = Guid.Parse("2e05db1e-5112-4b9a-a6bf-65b1bd3a1bd9");
        var expected = new AuboArmHandshakeResultResponse(
            operationId,
            "ARM-01",
            3,
            1,
            AuboArmHandshakeState.Completed,
            1,
            null,
            true,
            DateTimeOffset.Parse("2026-08-27T03:00:00Z"));
        var handler = new RecordingHandler(_ => JsonResponse(expected));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://adapter/") };
        var client = new AdapterAuboArmClient(httpClient);

        var result = await client.DispatchAsync("ARM-01", operationId, 3, CancellationToken.None);

        Assert.Equal(AuboArmHandshakeState.Completed, result.State);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/robot-arms/ARM-01/handshake/dispatch", request.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(3, body.RootElement.GetProperty("commandCode").GetInt32());
        Assert.Equal(operationId, body.RootElement.GetProperty("operationId").GetGuid());
    }

    [Fact]
    public async Task Program_catalog_can_request_a_fresh_scan_explicitly()
    {
        var expected = new AuboArmProgramCatalogResponse(
            "ARM-01",
            true,
            "取料盘",
            ["取料盘"],
            ["取料盘"],
            true,
            [],
            DateTimeOffset.Parse("2026-09-04T01:00:00Z"));
        var handler = new RecordingHandler(_ => JsonResponse(expected));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://adapter/") };
        var client = new AdapterAuboArmClient(httpClient);

        var result = await client.GetProgramCatalogAsync(
            "ARM-01", forceFresh: true, CancellationToken.None);

        Assert.Equal("ARM-01", result.DeviceId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/robot-arms/ARM-01/programs", request.RequestUri!.AbsolutePath);
        Assert.Equal("fresh=true", request.RequestUri.Query.TrimStart('?'));
    }

    [Fact]
    public async Task Correlated_program_write_posts_all_durable_identity_fields()
    {
        var operationId = Guid.NewGuid();
        var correlation = AuboArmOperationCorrelation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            operationId,
            Guid.NewGuid(),
            "run-correlation",
            attempt: 2);
        var expected = new AuboArmProgramOperationResponse(
            operationId,
            "ARM-01",
            "娴嬭瘯",
            "load",
            "operator",
            AuboArmProgramOperationState.Loaded,
            AuboArmRuntimeState.Stopped,
            "Stopped",
            "娴嬭瘯",
            0,
            null,
            true,
            DateTimeOffset.UtcNow)
        {
            WorkflowRunId = correlation.WorkflowRunId,
            WorkflowNodeExecutionId = correlation.WorkflowNodeExecutionId,
            DeviceOperationId = operationId,
            RequestId = correlation.RequestId,
            CorrelationId = correlation.CorrelationId,
            Attempt = correlation.Attempt
        };
        var handler = new RecordingHandler(_ => JsonResponse(expected));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://adapter/") };
        var client = new AdapterAuboArmClient(httpClient);

        await client.LoadProgramAsync(
            "ARM-01", "娴嬭瘯", "operator", operationId, correlation, CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal(correlation.WorkflowRunId, body.RootElement.GetProperty("workflowRunId").GetGuid());
        Assert.Equal(correlation.WorkflowNodeExecutionId, body.RootElement.GetProperty("workflowNodeExecutionId").GetGuid());
        Assert.Equal(operationId, body.RootElement.GetProperty("deviceOperationId").GetGuid());
        Assert.Equal(correlation.RequestId, body.RootElement.GetProperty("requestId").GetGuid());
        Assert.Equal("run-correlation", body.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("attempt").GetInt32());
    }

    private static HttpResponseMessage JsonResponse<T>(T value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri, body));
            return responseFactory(request);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri? RequestUri, string? Body);
}
