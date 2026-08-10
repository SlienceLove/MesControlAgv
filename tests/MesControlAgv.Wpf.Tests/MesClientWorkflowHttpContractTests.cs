using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class MesClientWorkflowHttpContractTests
{
    [Fact]
    public async Task Workflow_lifecycle_uses_expected_routes_queries_and_contract_payloads()
    {
        var workflowId = Guid.NewGuid();
        var startId = Guid.NewGuid();
        var moveId = Guid.NewGuid();
        var endId = Guid.NewGuid();
        var definition = new WorkflowDefinition
        {
            Id = workflowId,
            Name = "http-workflow",
            Description = "HTTP contract",
            Nodes =
            [
                new WorkflowNode
                {
                    Id = startId,
                    Type = WorkflowNodeType.Start,
                    Name = "Start",
                    Order = 1,
                    NextNodeIds = [moveId]
                },
                new WorkflowNode
                {
                    Id = moveId,
                    Type = WorkflowNodeType.Move,
                    Name = "Move",
                    TargetStation = "SAMPLE_01",
                    Order = 2,
                    NextNodeIds = [endId],
                    Parameters = [new WorkflowParameter { Name = "batch", Value = "B-1", IsRequired = true }]
                },
                new WorkflowNode
                {
                    Id = endId,
                    Type = WorkflowNodeType.End,
                    Name = "End",
                    Order = 3
                }
            ]
        };
        var draft = new WorkflowVersion
        {
            WorkflowId = workflowId,
            Version = 1,
            Definition = definition,
            Status = WorkflowVersionStatus.Draft,
            PublishStatus = WorkflowPublishStatus.NotPublished
        };
        var validation = WorkflowValidationResult.Valid("workflow-contract-v1");
        var published = draft with
        {
            Status = WorkflowVersionStatus.Published,
            PublishStatus = WorkflowPublishStatus.Published,
            Validation = validation,
            Definition = definition with { PublishedVersion = 1 }
        };

        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/workflows")
                return JsonResponse(new[] { definition });
            if (request.Method == HttpMethod.Get && path == $"/api/workflows/{workflowId}")
                return JsonResponse(definition);
            if (request.Method == HttpMethod.Get && path == $"/api/workflows/{workflowId}/versions")
                return JsonResponse(new[] { draft });
            if (request.Method == HttpMethod.Get && path == $"/api/workflows/{workflowId}/versions/1")
                return JsonResponse(draft);
            if (request.Method == HttpMethod.Post && path == "/api/workflows")
                return JsonResponse(draft, HttpStatusCode.Created);
            if (request.Method == HttpMethod.Put && path == $"/api/workflows/{workflowId}/versions/1/draft")
                return JsonResponse(draft);
            if (request.Method == HttpMethod.Post && path == "/api/workflows/validate")
                return JsonResponse(validation);
            if (request.Method == HttpMethod.Post && path == $"/api/workflows/{workflowId}/versions/1/validate")
                return JsonResponse(validation);
            if (request.Method == HttpMethod.Post && path == $"/api/workflows/{workflowId}/versions/1/publish")
                return JsonResponse(published);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://mes.local/") };
        var client = new MesClient(httpClient);

        Assert.Equal(workflowId, Assert.Single(await client.GetWorkflowsAsync(CancellationToken.None)).Id);
        Assert.Equal(workflowId, (await client.GetWorkflowAsync(workflowId, CancellationToken.None))!.Id);
        Assert.Equal(1, Assert.Single(await client.GetWorkflowVersionsAsync(workflowId, CancellationToken.None)).Version);
        Assert.Equal(1, (await client.GetWorkflowVersionAsync(workflowId, 1, CancellationToken.None))!.Version);
        Assert.Equal(1, (await client.CreateWorkflowDraftAsync(definition, "planner api", CancellationToken.None)).Version);
        Assert.Equal(1, (await client.UpdateWorkflowDraftAsync(workflowId, 1, definition, "planner api", CancellationToken.None)).Version);
        Assert.True((await client.ValidateWorkflowAsync(definition, CancellationToken.None)).IsValid);
        Assert.True((await client.ValidateWorkflowVersionAsync(workflowId, 1, CancellationToken.None)).IsValid);
        Assert.Equal(
            WorkflowPublishStatus.Published,
            (await client.PublishWorkflowAsync(workflowId, 1, "planner api", CancellationToken.None)).PublishStatus);

        Assert.Equal(9, handler.Requests.Count);
        Assert.Equal("actor=planner%20api", handler.Requests[4].Uri.Query.TrimStart('?'));
        Assert.Equal("actor=planner%20api", handler.Requests[5].Uri.Query.TrimStart('?'));
        Assert.Equal("actor=planner%20api", handler.Requests[8].Uri.Query.TrimStart('?'));

        using var createBody = JsonDocument.Parse(handler.Requests[4].Body!);
        var createRoot = createBody.RootElement;
        Assert.Equal(workflowId, createRoot.GetProperty("id").GetGuid());
        Assert.Equal("http-workflow", createRoot.GetProperty("name").GetString());
        var moveBody = createRoot.GetProperty("nodes").EnumerateArray().ElementAt(1);
        Assert.Equal("SAMPLE_01", moveBody.GetProperty("targetStation").GetString());
        Assert.Equal("B-1", moveBody.GetProperty("parameters").EnumerateArray().Single().GetProperty("value").GetString());

        Assert.Null(handler.Requests[7].Body);
        Assert.Null(handler.Requests[8].Body);
    }

    [Fact]
    public async Task Missing_workflow_and_version_are_mapped_to_null()
    {
        var workflowId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://mes.local/") };
        var client = new MesClient(httpClient);

        Assert.Null(await client.GetWorkflowAsync(workflowId, CancellationToken.None));
        Assert.Null(await client.GetWorkflowVersionAsync(workflowId, 4, CancellationToken.None));
        Assert.Equal($"/api/workflows/{workflowId}", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal($"/api/workflows/{workflowId}/versions/4", handler.Requests[1].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Execute_workflow_posts_dry_run_request_and_maps_accepted_result()
    {
        var workflowId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var stepRequestId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var requestedAt = DateTimeOffset.Parse("2026-08-07T03:04:05Z");
        var result = new WorkflowExecutionResult
        {
            Status = WorkflowExecutionStatus.Accepted,
            RequestId = requestId,
            ExecutionId = executionId,
            WorkflowId = workflowId,
            Version = 3,
            RequestedAt = requestedAt,
            DryRun = true,
            NextStepRequest = new WorkflowNextStepRequest
            {
                StepRequestId = stepRequestId,
                ExecutionId = executionId,
                WorkflowId = workflowId,
                Version = 3,
                NodeId = nodeId,
                NodeType = WorkflowNodeType.Move,
                NodeName = "Move to sample",
                TargetStation = "SAMPLE_01",
                DryRun = true,
                Parameters = new Dictionary<string, string?> { ["batch"] = "B-42" }
            },
            Audit = new WorkflowExecutionAuditEntry
            {
                RequestId = requestId,
                ExecutionId = executionId,
                WorkflowId = workflowId,
                Version = 3,
                EventType = "WorkflowExecutionAccepted",
                Outcome = "Accepted"
            }
        };
        var request = new WorkflowExecutionRequest
        {
            WorkflowId = workflowId,
            Version = 3,
            RequestId = requestId,
            RequestedBy = "planner api",
            CorrelationId = "corr-42",
            RequestedAt = requestedAt,
            DryRun = true,
            Parameters = new Dictionary<string, string?> { ["batch"] = "B-42", ["optional"] = null }
        };
        var handler = new RecordingHandler(_ => JsonResponse(result, HttpStatusCode.Accepted));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://mes.local/") };
        var client = new MesClient(httpClient);

        var actual = await client.ExecuteWorkflowAsync(request, CancellationToken.None);

        Assert.True(actual.IsAccepted);
        Assert.True(actual.DryRun);
        Assert.Equal(executionId, actual.ExecutionId);
        Assert.Equal("SAMPLE_01", actual.NextStep!.TargetStation);
        Assert.Equal("B-42", actual.NextStep.Parameters["batch"]);

        var captured = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal("/api/workflows/execute", captured.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(captured.Body!);
        var root = body.RootElement;
        Assert.Equal(workflowId, root.GetProperty("workflowId").GetGuid());
        Assert.Equal(3, root.GetProperty("version").GetInt32());
        Assert.Equal(requestId, root.GetProperty("requestId").GetGuid());
        Assert.Equal("planner api", root.GetProperty("requestedBy").GetString());
        Assert.Equal("corr-42", root.GetProperty("correlationId").GetString());
        Assert.True(root.GetProperty("dryRun").GetBoolean());
        Assert.Equal("B-42", root.GetProperty("parameters").GetProperty("batch").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("parameters").GetProperty("optional").ValueKind);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "WORKFLOW_VERSION_NOT_FOUND")]
    [InlineData(HttpStatusCode.Conflict, "WORKFLOW_REQUEST_ID_REUSED")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "WORKFLOW_VERSION_NOT_PUBLISHED")]
    public async Task Execute_workflow_maps_business_rejection_statuses_to_result(
        HttpStatusCode statusCode,
        string rejectionCode)
    {
        var result = new WorkflowExecutionResult
        {
            Status = WorkflowExecutionStatus.Rejected,
            RequestId = Guid.NewGuid(),
            WorkflowId = Guid.NewGuid(),
            Version = 1,
            DryRun = true,
            RejectionCode = rejectionCode,
            RejectionReason = "workflow cannot be admitted",
            Audit = new WorkflowExecutionAuditEntry { EventType = "WorkflowExecutionRejected", Code = rejectionCode }
        };
        var handler = new RecordingHandler(_ => JsonResponse(result, statusCode));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://mes.local/") };
        var client = new MesClient(httpClient);

        var actual = await client.ExecuteWorkflowAsync(
            new WorkflowExecutionRequest
            {
                WorkflowId = result.WorkflowId,
                Version = result.Version,
                RequestId = result.RequestId,
                DryRun = true
            },
            CancellationToken.None);

        Assert.True(actual.IsRejected);
        Assert.Equal(rejectionCode, actual.RejectionCode);
        Assert.Equal(statusCode, Assert.Single(handler.Requests).ResponseStatus);
    }

    private static HttpResponseMessage JsonResponse(object value, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = JsonContent.Create(value) };

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body, HttpStatusCode ResponseStatus);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = responseFactory(request);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, body, response.StatusCode));
            return response;
        }
    }
}
