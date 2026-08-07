using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Workflows;

using ContractWorkflowDefinition = MesControlAgv.Contracts.Workflows.WorkflowDefinition;
using ContractWorkflowExecutionRequest = MesControlAgv.Contracts.Workflows.WorkflowExecutionRequest;
using ContractWorkflowNode = MesControlAgv.Contracts.Workflows.WorkflowNode;
using ContractWorkflowNodeType = MesControlAgv.Contracts.Workflows.WorkflowNodeType;
using ContractWorkflowPublishStatus = MesControlAgv.Contracts.Workflows.WorkflowPublishStatus;
using ContractWorkflowValidationResult = MesControlAgv.Contracts.Workflows.WorkflowValidationResult;
using ContractWorkflowVersion = MesControlAgv.Contracts.Workflows.WorkflowVersion;
using ContractWorkflowVersionStatus = MesControlAgv.Contracts.Workflows.WorkflowVersionStatus;
using WorkflowNodeType = MesControlAgv.Contracts.Workflows.WorkflowNodeType;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkflowEditorRemoteTests
{
    [Fact]
    public async Task Editor_runs_remote_draft_validation_publish_and_dry_run_flow()
    {
        using var fixture = new TempWorkflowFile();
        var client = new WorkflowEditorClientStub(CreateContractWorkflow());
        var editor = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path), client, () => "operator-remote");

        editor.LoadFromMesCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.SelectedRemoteVersion is not null);
        Assert.Equal("remote-transport", editor.SelectedWorkflow!.Name);
        Assert.Equal(1, editor.SelectedRemoteVersion!.Version);
        Assert.Equal(ContractWorkflowVersionStatus.Draft, editor.SelectedRemoteVersion.Status);

        editor.SaveDraftCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && client.UpdateDraftCallCount == 1);
        Assert.Equal("operator-remote", client.LastActor);

        editor.ValidateCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.LastValidation is not null);
        Assert.True(editor.LastValidation!.IsValid);
        Assert.True(editor.PublishCommand.CanExecute(null));

        editor.PublishCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.SelectedRemoteVersion?.PublishStatus == ContractWorkflowPublishStatus.Published);
        Assert.Equal(ContractWorkflowVersionStatus.Published, editor.SelectedRemoteVersion!.Status);
        Assert.True(editor.DryRunCommand.CanExecute(null));

        editor.DryRunCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.LastExecution is not null);
        Assert.True(editor.LastExecution!.IsAccepted);
        Assert.True(editor.LastExecution.DryRun);
        Assert.Equal("Move", editor.LastExecution.NextStep!.NodeName);
        Assert.True(client.LastExecutionRequest!.DryRun);
        Assert.Equal(1, client.LastExecutionRequest.Version);
    }

    [Fact]
    public void Local_clone_remaps_edges_and_preserves_node_parameters()
    {
        var startId = Guid.NewGuid();
        var endId = Guid.NewGuid();
        var workflow = new WorkflowDefinition
        {
            Name = "local",
            Nodes =
            [
                new WorkflowNode { Id = startId, Type = MesControlAgv.Wpf.Workflows.WorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [endId] },
                new WorkflowNode
                {
                    Id = endId,
                    Type = MesControlAgv.Wpf.Workflows.WorkflowNodeType.End,
                    Name = "End",
                    Order = 2,
                    Parameters = [new WorkflowNodeParameter { Name = "batch", Value = "B-1", IsRequired = true }]
                }
            ]
        };

        var copy = workflow.Clone();

        Assert.NotEqual(workflow.Id, copy.Id);
        var copiedStart = Assert.Single(copy.Nodes.Where(node => node.Type == MesControlAgv.Wpf.Workflows.WorkflowNodeType.Start));
        var copiedEnd = Assert.Single(copy.Nodes.Where(node => node.Type == MesControlAgv.Wpf.Workflows.WorkflowNodeType.End));
        Assert.Equal(copiedEnd.Id, Assert.Single(copiedStart.NextNodeIds));
        var parameter = Assert.Single(copiedEnd.Parameters);
        Assert.Equal("batch", parameter.Name);
        Assert.Equal("B-1", parameter.Value);
    }

    private static ContractWorkflowDefinition CreateContractWorkflow()
    {
        var startId = Guid.NewGuid();
        var moveId = Guid.NewGuid();
        var endId = Guid.NewGuid();
        return new ContractWorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "remote-transport",
            Description = "Remote test workflow",
            Nodes =
            [
                new ContractWorkflowNode { Id = startId, Type = ContractWorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [moveId] },
                new ContractWorkflowNode { Id = moveId, Type = ContractWorkflowNodeType.Move, Name = "Move", TargetStation = "SAMPLE_01", Order = 2, NextNodeIds = [endId] },
                new ContractWorkflowNode { Id = endId, Type = ContractWorkflowNodeType.End, Name = "End", Order = 3 }
            ]
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("The asynchronous workflow editor action did not complete.");
    }

    private sealed class WorkflowEditorClientStub(ContractWorkflowDefinition definition) : IMesClient
    {
        private ContractWorkflowVersion _version = new()
        {
            WorkflowId = definition.Id,
            Version = 1,
            Definition = definition,
            Status = ContractWorkflowVersionStatus.Draft,
            PublishStatus = ContractWorkflowPublishStatus.NotPublished
        };

        public string? LastActor { get; private set; }
        public int UpdateDraftCallCount { get; private set; }
        public ContractWorkflowExecutionRequest? LastExecutionRequest { get; private set; }

        public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DashboardTask>>([]);
        public Task<KpiDashboard> GetKpiDashboardAsync(DateOnly date, CancellationToken cancellationToken) => Task.FromResult(new KpiDashboard(date, new KpiTaskSummary(0, 0, 0, 0, 0), [], new KpiSampleSummary(0, 0, 0, 0, 0, 0, "test"), [], []));
        public Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult<DashboardTaskDetail?>(null);
        public Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new AgvDashboardSnapshot(false, "none", null, null));
        public Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken) => Task.FromException<DashboardTask>(new NotSupportedException());
        public Task<DashboardTask> CreateTaskAsync(int sourceStationCode, int targetStationCode, int priority, string? description, string? externalId, CancellationToken cancellationToken) => Task.FromException<DashboardTask>(new NotSupportedException());
        public Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromException<DashboardTask>(new NotSupportedException());
        public Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromException<DashboardTask>(new NotSupportedException());
        public Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromException<DashboardTask>(new NotSupportedException());
        public Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromException<DashboardTask>(new NotSupportedException());
        public Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromException<DashboardTask>(new NotSupportedException());
        public Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromException<DashboardTask>(new NotSupportedException());

        public Task<IReadOnlyList<ContractWorkflowDefinition>> GetWorkflowsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContractWorkflowDefinition>>([definition]);
        public Task<IReadOnlyList<ContractWorkflowVersion>> GetWorkflowVersionsAsync(Guid workflowId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContractWorkflowVersion>>([_version]);
        public Task<ContractWorkflowVersion?> GetWorkflowVersionAsync(Guid workflowId, int version, CancellationToken cancellationToken) => Task.FromResult<ContractWorkflowVersion?>(_version);
        public Task<ContractWorkflowVersion> CreateWorkflowDraftAsync(ContractWorkflowDefinition value, string actor, CancellationToken cancellationToken) => Update(value, actor);

        public Task<ContractWorkflowVersion> UpdateWorkflowDraftAsync(Guid workflowId, int version, ContractWorkflowDefinition value, string actor, CancellationToken cancellationToken)
        {
            UpdateDraftCallCount++;
            return Update(value, actor);
        }

        public Task<ContractWorkflowValidationResult> ValidateWorkflowAsync(ContractWorkflowDefinition value, CancellationToken cancellationToken) => Task.FromResult(ContractWorkflowValidationResult.Valid("stub"));

        public Task<ContractWorkflowValidationResult> ValidateWorkflowVersionAsync(Guid workflowId, int version, CancellationToken cancellationToken)
        {
            var result = ContractWorkflowValidationResult.Valid("stub");
            _version = _version with { Status = ContractWorkflowVersionStatus.Validated, Validation = result };
            return Task.FromResult(result);
        }

        public Task<ContractWorkflowVersion> PublishWorkflowAsync(Guid workflowId, int version, string actor, CancellationToken cancellationToken)
        {
            LastActor = actor;
            _version = _version with
            {
                Status = ContractWorkflowVersionStatus.Published,
                PublishStatus = ContractWorkflowPublishStatus.Published,
                Definition = _version.Definition with { PublishedVersion = version },
                PublishedBy = actor,
                PublishedAt = DateTimeOffset.UtcNow
            };
            return Task.FromResult(_version);
        }

        public Task<DashboardWorkflowExecution> ExecuteWorkflowAsync(ContractWorkflowExecutionRequest request, CancellationToken cancellationToken)
        {
            LastExecutionRequest = request;
            return Task.FromResult(new DashboardWorkflowExecution(
                true,
                false,
                request.RequestId,
                Guid.NewGuid(),
                request.WorkflowId,
                request.Version,
                request.DryRun,
                null,
                null,
                new DashboardWorkflowNextStep(Guid.NewGuid(), Guid.NewGuid(), request.WorkflowId, request.Version, Guid.NewGuid(), (int)WorkflowNodeType.Move, "Move", "SAMPLE_01", true, new Dictionary<string, string?>())));
        }

        private Task<ContractWorkflowVersion> Update(ContractWorkflowDefinition value, string actor)
        {
            LastActor = actor;
            _version = _version with { Definition = value, CreatedBy = actor };
            return Task.FromResult(_version);
        }
    }

    private sealed class TempWorkflowFile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MesControlAgv.WorkflowRemoteTests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "workflows.json");

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
