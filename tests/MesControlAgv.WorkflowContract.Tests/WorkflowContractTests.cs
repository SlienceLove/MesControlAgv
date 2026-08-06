using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;

using MesControlAgv.Application;

namespace MesControlAgv.WorkflowContract.Tests;

public sealed class WorkflowContractTests
{
    [Fact]
    public void Contract_shape_preserves_legacy_wpf_node_fields_and_adds_parameters()
    {
        var nodeId = Guid.NewGuid();
        var node = new WorkflowNode
        {
            Id = nodeId,
            Type = WorkflowNodeType.Move,
            Name = "Move to pickup",
            Description = "Move the AGV",
            TargetStation = "SAMPLE_01",
            X = 180,
            Y = 100,
            Order = 2,
            Parameters =
            [
                new WorkflowParameter { Name = "speed", Value = "0.5", DataType = "decimal" }
            ]
        };

        Assert.Equal(nodeId, node.Id);
        Assert.Equal(WorkflowNodeType.Move, node.Type);
        Assert.Equal("SAMPLE_01", node.TargetStation);
        Assert.Equal("speed", Assert.Single(node.Parameters).Name);
    }

    [Fact]
    public void Validator_accepts_an_ordered_legacy_compatible_workflow()
    {
        var definition = CreateValidWorkflow();

        var result = new WorkflowValidator().Validate(definition);

        Assert.True(result.IsValid);
        Assert.False(result.HasWarnings);
        Assert.Empty(result.Issues);
        Assert.Equal(WorkflowValidator.ValidatorVersion, result.ValidatorVersion);
    }

    [Fact]
    public void Validator_rejects_duplicate_nodes_and_missing_target_station()
    {
        var duplicateId = Guid.NewGuid();
        var definition = new WorkflowDefinition
        {
            Name = "Invalid",
            Nodes =
            [
                new WorkflowNode { Id = duplicateId, Type = WorkflowNodeType.Start, Name = "Start", Order = 1 },
                new WorkflowNode { Id = duplicateId, Type = WorkflowNodeType.Move, Name = "Move", Order = 2 },
                new WorkflowNode { Id = Guid.NewGuid(), Type = WorkflowNodeType.End, Name = "End", Order = 3 }
            ]
        };

        var result = new WorkflowValidator().Validate(definition);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "WF003");
        Assert.Contains(result.Issues, issue => issue.Code == "WF012");
    }

    [Fact]
    public void Version_and_execution_request_pin_the_immutable_version()
    {
        var workflowId = Guid.NewGuid();
        var version = new WorkflowVersion
        {
            WorkflowId = workflowId,
            Version = 3,
            Definition = CreateValidWorkflow() with { Id = workflowId },
            Status = WorkflowVersionStatus.Published,
            PublishStatus = WorkflowPublishStatus.Published
        };
        var request = new WorkflowExecutionRequest
        {
            WorkflowId = workflowId,
            Version = version.Version,
            Parameters = new Dictionary<string, string?> { ["batch"] = "B-001" }
        };

        Assert.Equal(workflowId, request.WorkflowId);
        Assert.Equal(3, request.Version);
        Assert.Equal(WorkflowPublishStatus.Published, version.PublishStatus);
        Assert.Equal("B-001", request.Parameters["batch"]);
    }

    [Fact]
    public async Task Runtime_rejects_unpublished_and_unvalidated_versions()
    {
        var workflowId = Guid.NewGuid();
        var definition = CreateValidWorkflow() with { Id = workflowId };
        var reader = new InMemoryVersionReader(new WorkflowVersion
        {
            WorkflowId = workflowId,
            Version = 1,
            Definition = definition,
            Status = WorkflowVersionStatus.Draft,
            PublishStatus = WorkflowPublishStatus.NotPublished
        });
        var executor = new WorkflowRuntimeExecutor(reader);

        var unpublished = await executor.ExecuteAsync(CreateRequest(workflowId), CancellationToken.None);

        Assert.True(unpublished.IsRejected);
        Assert.Equal(WorkflowExecutionRejectionCodes.VersionNotPublished, unpublished.RejectionCode);
        Assert.Null(unpublished.NextStep);
        Assert.Equal("WorkflowExecutionRejected", unpublished.Audit.EventType);

        reader.Version = reader.Version with
        {
            Status = WorkflowVersionStatus.Published,
            PublishStatus = WorkflowPublishStatus.Published
        };
        var unvalidated = await new WorkflowRuntimeExecutor(reader)
            .ExecuteAsync(CreateRequest(workflowId), CancellationToken.None);

        Assert.Equal(WorkflowExecutionRejectionCodes.VersionNotValidated, unvalidated.RejectionCode);
    }

    [Fact]
    public async Task Runtime_requires_a_successful_validation_before_execution()
    {
        var workflowId = Guid.NewGuid();
        var definition = CreateValidWorkflow() with { Id = workflowId };
        var version = CreatePublishedVersion(workflowId, definition) with
        {
            Validation = new WorkflowValidationResult
            {
                Issues =
                [new WorkflowValidationIssue
                {
                    Code = "WF-TEST",
                    Message = "invalid",
                    Severity = WorkflowValidationSeverity.Error
                }]
            }
        };

        var result = await new WorkflowRuntimeExecutor(new InMemoryVersionReader(version))
            .ExecuteAsync(CreateRequest(workflowId), CancellationToken.None);

        Assert.Equal(WorkflowExecutionRejectionCodes.VersionNotValidated, result.RejectionCode);
        Assert.Contains(result.ValidationIssues, issue => issue.Code == "WF-TEST");
    }

    [Fact]
    public async Task Runtime_resolves_next_step_parameters_and_audit_without_side_effects()
    {
        var workflowId = Guid.NewGuid();
        var definition = CreateValidWorkflow(includeRequiredParameter: true) with { Id = workflowId };
        var reader = new InMemoryVersionReader(CreatePublishedVersion(workflowId, definition));
        var request = CreateRequest(workflowId) with
        {
            RequestedBy = "operator-1",
            CorrelationId = "corr-1",
            Parameters = new Dictionary<string, string?> { ["batch"] = "B-001" },
            DryRun = true
        };

        var result = await new WorkflowRuntimeExecutor(reader)
            .ExecuteAsync(request, CancellationToken.None);

        Assert.True(result.IsAccepted);
        Assert.NotEqual(Guid.Empty, result.ExecutionId);
        Assert.NotNull(result.NextStep);
        Assert.Equal(WorkflowNodeType.Move, result.NextStep!.NodeType);
        Assert.Equal("SAMPLE_01", result.NextStep.TargetStation);
        Assert.Equal("B-001", result.NextStep.Parameters["batch"]);
        Assert.Equal(result.ExecutionId, result.NextStep.ExecutionId);
        Assert.Equal("WorkflowExecutionAccepted", result.Audit.EventType);
        Assert.Equal(request.RequestId, result.Audit.RequestId);
        Assert.Equal("operator-1", result.Audit.RequestedBy);
        Assert.Equal("corr-1", result.Audit.CorrelationId);
        Assert.Equal("True", result.Audit.Details["dryRun"]);
        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public async Task Runtime_rejects_missing_required_parameter()
    {
        var workflowId = Guid.NewGuid();
        var definition = CreateValidWorkflow(includeRequiredParameter: true) with { Id = workflowId };
        var result = await new WorkflowRuntimeExecutor(
                new InMemoryVersionReader(CreatePublishedVersion(workflowId, definition)))
            .ExecuteAsync(CreateRequest(workflowId), CancellationToken.None);

        Assert.Equal(WorkflowExecutionRejectionCodes.RequiredParameter, result.RejectionCode);
        Assert.Contains(result.ValidationIssues, issue => issue.ParameterName == "batch");
        Assert.Null(result.NextStep);
    }

    [Fact]
    public async Task Runtime_is_idempotent_for_same_request_and_rejects_request_id_reuse()
    {
        var workflowId = Guid.NewGuid();
        var reader = new InMemoryVersionReader(
            CreatePublishedVersion(workflowId, CreateValidWorkflow() with { Id = workflowId }));
        var executor = new WorkflowRuntimeExecutor(reader);
        var request = CreateRequest(workflowId);

        var first = await executor.ExecuteAsync(request, CancellationToken.None);
        var replay = await executor.ExecuteAsync(request, CancellationToken.None);
        var reused = await executor.ExecuteAsync(request with { Version = 2 }, CancellationToken.None);

        Assert.True(first.IsAccepted);
        Assert.True(replay.IsAccepted);
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(first.ExecutionId, replay.ExecutionId);
        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(WorkflowExecutionRejectionCodes.RequestIdReused, reused.RejectionCode);
    }

    private static WorkflowExecutionRequest CreateRequest(Guid workflowId) => new()
    {
        WorkflowId = workflowId,
        Version = 1,
        RequestId = Guid.NewGuid()
    };

    private static WorkflowVersion CreatePublishedVersion(Guid workflowId, WorkflowDefinition definition) => new()
    {
        WorkflowId = workflowId,
        Version = 1,
        Definition = definition,
        Status = WorkflowVersionStatus.Published,
        PublishStatus = WorkflowPublishStatus.Published,
        Validation = new WorkflowValidator().Validate(definition)
    };

    private static WorkflowDefinition CreateValidWorkflow(bool includeRequiredParameter = false)
    {
        var startId = Guid.NewGuid();
        var moveId = Guid.NewGuid();
        var endId = Guid.NewGuid();
        return new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Transport",
            Nodes =
            [
                new WorkflowNode { Id = startId, Type = WorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [moveId] },
                new WorkflowNode
                {
                    Id = moveId,
                    Type = WorkflowNodeType.Move,
                    Name = "Move",
                    TargetStation = "SAMPLE_01",
                    Order = 2,
                    NextNodeIds = [endId],
                    Parameters = includeRequiredParameter
                        ? [new WorkflowParameter { Name = "batch", IsRequired = true }]
                        : Array.Empty<WorkflowParameter>()
                },
                new WorkflowNode { Id = endId, Type = WorkflowNodeType.End, Name = "End", Order = 3 }
            ]
        };
    }

    private sealed class InMemoryVersionReader(WorkflowVersion version) : IWorkflowVersionReader
    {
        public WorkflowVersion Version { get; set; } = version;
        public int ReadCount { get; private set; }

        public Task<WorkflowVersion?> GetVersionAsync(
            Guid workflowId,
            int version,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult<WorkflowVersion?>(
                Version.WorkflowId == workflowId && Version.Version == version ? Version : null);
        }
    }
}
