using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Mes.Tests;

public sealed class WorkflowApiTests : IClassFixture<MesWebApplicationFactory>
{
    private readonly HttpClient _client;

    public WorkflowApiTests(MesWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Workflow_endpoints_create_validate_publish_read_and_admit_execution()
    {
        var definition = CreateValidWorkflow();
        var create = await _client.PostAsJsonAsync("/api/workflows?actor=planner-api", definition);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var draft = await create.Content.ReadFromJsonAsync<WorkflowVersion>();
        Assert.NotNull(draft);

        var validation = await _client.PostAsync(
            $"/api/workflows/{draft!.WorkflowId}/versions/{draft.Version}/validate",
            content: null);
        Assert.Equal(HttpStatusCode.OK, validation.StatusCode);
        var validationResult = await validation.Content.ReadFromJsonAsync<WorkflowValidationResult>();
        Assert.True(validationResult!.IsValid);

        var publish = await _client.PostAsync(
            $"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/publish?actor=planner-api",
            content: null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        var version = await _client.GetFromJsonAsync<WorkflowVersion>(
            $"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}");
        Assert.NotNull(version);
        Assert.Equal(WorkflowPublishStatus.Published, version!.PublishStatus);
        Assert.Equal(draft.Version, version.Definition.PublishedVersion);

        var request = new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "operator-api",
            DryRun = true
        };
        var execute = await _client.PostAsJsonAsync("/api/workflows/execute", request);
        Assert.Equal(HttpStatusCode.Accepted, execute.StatusCode);
        var accepted = await execute.Content.ReadFromJsonAsync<WorkflowExecutionResult>();
        Assert.True(accepted!.IsAccepted);
        Assert.Equal(WorkflowNodeType.Move, accepted.NextStep!.NodeType);

        var replay = await _client.PostAsJsonAsync("/api/workflows/execute", request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayResult = await replay.Content.ReadFromJsonAsync<WorkflowExecutionResult>();
        Assert.True(replayResult!.IsIdempotentReplay);
        Assert.Equal(accepted.ExecutionId, replayResult.ExecutionId);
    }

    private static WorkflowDefinition CreateValidWorkflow()
    {
        var start = Guid.NewGuid();
        var move = Guid.NewGuid();
        var end = Guid.NewGuid();
        return new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "API workflow",
            Nodes =
            [
                new WorkflowNode { Id = start, Type = WorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [move] },
                new WorkflowNode { Id = move, Type = WorkflowNodeType.Move, Name = "Move", TargetStation = "SAMPLE_01", Order = 2, NextNodeIds = [end] },
                new WorkflowNode { Id = end, Type = WorkflowNodeType.End, Name = "End", Order = 3 }
            ]
        };
    }
}
