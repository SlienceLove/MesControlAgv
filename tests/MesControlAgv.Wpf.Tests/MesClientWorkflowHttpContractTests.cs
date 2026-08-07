using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class MesClientWorkflowHttpContractTests
{
    [Fact]
    public async Task Workflow_lifecycle_and_dry_run_use_expected_http_contracts()
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
                new WorkflowNode { Id = startId, Type = WorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [moveId] },
                new WorkflowNode { Id = moveId, Type = WorkflowNodeType.Move, Name = "Move", TargetStation = "SAMPLE_01", Order = 2, NextNodeIds = [endId] },
                new WorkflowNode { Id = endId, Type = WorkflowNodeType.End, Name = "End", Order = 3 }
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
        var execution = new DashboardWorkflowExecution(
            true,
            false,
            Guid.NewGuid(),
            Guid.NewGuid(),
            workflowId,
            1,
            true,
            null,
            null,
            new DashboardWorkflowNextStep(
                Guid.NewGuid(),
                Guid.NewGuid(),
                workflowId,
                1,
                moveId,
                (int)WorkflowNodeType.Move,
                "Move",
                "SAMPLE_01",
                true,
                new Dictionary<string, string?>()));

        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/workflows" && request.Method == HttpMethod.Get)
                return JsonResponse(new[] { definition });
            if (path == $"/api/workflows/{workflowId}/versions" && request.Method == HttpMethod.Get)
                return JsonResponse(new[] { draft });
            if (path == $"/api/workflows/{workflowId}/versions/1" && request.Method == HttpMethod.Get)
                return JsonResponse(draft);
            if (path == "/api/workflows" && request.Method == HttpMethod.Post)
                return JsonResponse(draft, HttpStatusCode.Created);
            if (path == $"/api/workflows/{workflowId}/versions/1/draft" && request.Method == HttpMethod.Put)
                return JsonResponse(draft);
            if (path == "/api/workflows/validate" && request.Method == HttpMethod.Post)
                return JsonResponse(validation);
            if (path == $"/api/workflows/{workflowId}/versions/1/validate" && request.Method == HttpMethod.Post)
                return JsonResponse(validation);
            if (path == $"/api/workflows/{workflowId}/versions/1/publish" && request.Method == HttpMethod.Post)
                return JsonResponse(published);
            if (path == "/api/workflows/execute" && request.Method == HttpMethod.Post)
                return JsonResponse(execution, HttpStatusCode.Accepted);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://mes.local/") };
        var client = new MesClient(httpClient);

        Assert.Single(await client.GetWorkflowsAsync(CancellationToken.None));
        Assert.Single(await client.GetWorkflowVersionsAsync(workflowId, CancellationToken.None));
        Assert.Equal(1, (await client.GetWorkflowVersionAsync(workflowId, 1, CancellationToken.None))!.Version);
        Assert.Equal(1, (await client.CreateWorkflowDraftAsync(definition, "planner api", CancellationToken.None)).Version);
        Assert.Equal(1, (await client.UpdateWorkflowDraftAsync(workflowId, 1, definition, "planner api", CancellationToken.None)).Version);
        Assert.True((await client.ValidateWorkflowAsync(definition, CancellationToken.None)).IsValid);
        Assert.True((await client.ValidateWorkflowVersionAsync(workflowId, 1, CancellationToken.None)).IsValid);
        Assert.Equal(WorkflowPublishStatus.Published, (await client.PublishWorkflowAsync(workflowId, 1, "planner api", CancellationToken.None)).PublishStatus);
        var result = await client.ExecuteWorkflowAsync(new WorkflowExecutionRequest
        {
            WorkflowId = workflowId,
            Version = 1,
            RequestedBy = "operator api",
            DryRun = true
        }, CancellationToken.None);
        Assert.True(result.IsAccepted);
        Assert.Equal("Move", result.NextStep!.NodeName);

        Assert.Equal(9, handler.Requests.Count);
        Assert.Equal("actor=planner%20api", handler.Requests[3].Uri.Query.TrimStart('?'));
        Assert.Equal("actor=planner%20api", handler.Requests[4].Uri.Query.TrimStart('?'));
        Assert.Equal("actor=planner%20api", handler.Requests[7].Uri.Query.TrimStart('?'));

        using var createBody = JsonDocument.Parse(handler.Requests[3].Body!);
        Assert.Equal(workflowId, createBody.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("http-workflow", createBody.RootElement.GetProperty("name").GetString());
        using var executeBody = JsonDocument.Parse(handler.Requests[8].Body!);
        Assert.Equal(workflowId, executeBody.RootElement.GetProperty("workflowId").GetGuid());
        Assert.Equal(1, executeBody.RootElement.GetProperty("version").GetInt32());
        Assert.True(executeBody.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal("operator api", executeBody.RootElement.GetProperty("requestedBy").GetString());
    }

    private static HttpResponseMessage JsonResponse(object value, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = JsonContent.Create(value) };

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body);

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
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, body));
            return responseFactory(request);
        }
    }
}
