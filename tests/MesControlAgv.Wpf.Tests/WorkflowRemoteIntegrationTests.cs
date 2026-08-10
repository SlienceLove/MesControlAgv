using System.Net;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Workflows;

using ContractWorkflowDefinition = MesControlAgv.Contracts.Workflows.WorkflowDefinition;
using ContractWorkflowExecutionRequest = MesControlAgv.Contracts.Workflows.WorkflowExecutionRequest;
using ContractWorkflowExecutionResult = MesControlAgv.Contracts.Workflows.WorkflowExecutionResult;
using ContractWorkflowExecutionStatus = MesControlAgv.Contracts.Workflows.WorkflowExecutionStatus;
using ContractWorkflowVersion = MesControlAgv.Contracts.Workflows.WorkflowVersion;
using ContractWorkflowVersionStatus = MesControlAgv.Contracts.Workflows.WorkflowVersionStatus;
using ContractWorkflowPublishStatus = MesControlAgv.Contracts.Workflows.WorkflowPublishStatus;
using ContractWorkflowValidationResult = MesControlAgv.Contracts.Workflows.WorkflowValidationResult;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkflowRemoteIntegrationTests
{
    [Fact]
    public async Task Save_draft_persists_local_json_when_mes_is_unavailable()
    {
        using var fixture = new TempWorkflowFile();
        var client = new FakeWorkflowMesClient
        {
            CreateDraftException = new HttpRequestException("MES is offline", null, HttpStatusCode.ServiceUnavailable)
        };
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path), client, "test-operator");

        await viewModel.SaveDraftAsync();

        Assert.True(File.Exists(fixture.Path));
        Assert.NotEmpty(new WorkflowStore(fixture.Path).Load());
        Assert.Equal(WorkflowRemoteState.ServiceUnavailable, viewModel.RemoteState);
        Assert.Contains("local JSON remains active", viewModel.Message);
        Assert.Equal(1, client.CreateDraftCallCount);
    }

    [Fact]
    public async Task Publish_does_not_use_a_stale_remote_version_when_current_draft_save_fails()
    {
        using var fixture = new TempWorkflowFile();
        var client = new FakeWorkflowMesClient
        {
            CreateDraftException = new HttpRequestException("MES is offline")
        };
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path), client, "test-operator");

        await viewModel.PublishRemoteAsync();

        Assert.Equal(WorkflowRemoteState.ServiceUnavailable, viewModel.RemoteState);
        Assert.Equal(0, client.PublishCallCount);
    }

    [Fact]
    public async Task Cancelled_draft_request_keeps_local_json_and_reports_cancelled_state()
    {
        using var fixture = new TempWorkflowFile();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new FakeWorkflowMesClient
        {
            CreateDraftException = new OperationCanceledException(cancellation.Token)
        };
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path), client);

        await viewModel.SaveDraftAsync(cancellation.Token);

        Assert.True(File.Exists(fixture.Path));
        Assert.Equal(WorkflowRemoteState.Cancelled, viewModel.RemoteState);
        Assert.Contains("cancelled", viewModel.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task Remote_lifecycle_loads_version_validates_publishes_and_dry_runs()
    {
        using var fixture = new TempWorkflowFile();
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "remote-workflow",
            Nodes =
            [
                new WorkflowNode { Type = WorkflowNodeType.Start, Name = "Start", Order = 1 },
                new WorkflowNode { Type = WorkflowNodeType.End, Name = "End", Order = 2 }
            ]
        };
        var client = new FakeWorkflowMesClient(workflow);
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path), client, "test-operator");

        await viewModel.LoadRemoteAsync();
        Assert.Equal(workflow.Id, viewModel.SelectedWorkflow!.Id);
        Assert.Equal(1, viewModel.RemoteVersion!.Version);
        Assert.Equal(WorkflowRemoteState.DraftSaved, viewModel.RemoteState);
        Assert.Equal(0, client.CreateDraftCallCount);

        await viewModel.SaveDraftAsync();
        Assert.Equal(WorkflowRemoteState.DraftSaved, viewModel.RemoteState);
        Assert.Equal(1, client.CreateDraftCallCount);
        Assert.Equal(ContractWorkflowVersionStatus.Draft, viewModel.RemoteVersion!.Status);
        Assert.Equal(ContractWorkflowPublishStatus.NotPublished, viewModel.RemoteVersion.PublishStatus);
        Assert.Equal(viewModel.SelectedWorkflow!.Id, viewModel.RemoteVersion.WorkflowId);

        await viewModel.ValidateRemoteAsync();
        Assert.Equal(WorkflowRemoteState.Validated, viewModel.RemoteState);
        Assert.True(viewModel.ValidationResult!.IsValid);
        Assert.Equal(1, client.CreateDraftCallCount);
        Assert.Equal(ContractWorkflowVersionStatus.Draft, viewModel.RemoteVersion!.Status);
        Assert.Equal(ContractWorkflowPublishStatus.NotPublished, viewModel.RemoteVersion.PublishStatus);

        await viewModel.PublishRemoteAsync();
        Assert.Equal(WorkflowRemoteState.Published, viewModel.RemoteState);
        Assert.Equal(ContractWorkflowPublishStatus.Published, viewModel.RemoteVersion!.PublishStatus);
        Assert.Equal(2, client.CreateDraftCallCount);

        await viewModel.ExecuteDryRunAsync();
        Assert.Equal(WorkflowRemoteState.DryRunAccepted, viewModel.RemoteState);
        Assert.True(viewModel.DryRunResult!.IsAccepted);
        Assert.Equal(1, viewModel.DryRunResult.Version);
        Assert.Equal(2, client.CreateDraftCallCount);
        Assert.Equal(2, client.ValidateVersionCallCount);
        Assert.Equal(1, client.PublishCallCount);
        Assert.Equal(1, client.ExecuteCallCount);
    }

    [Fact]
    public async Task Dry_run_without_confirmed_published_version_is_rejected_locally()
    {
        using var fixture = new TempWorkflowFile();
        var client = new FakeWorkflowMesClient();
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path), client);

        await viewModel.ExecuteDryRunAsync();

        Assert.Equal(WorkflowRemoteState.DryRunRejected, viewModel.RemoteState);
        Assert.Equal("WORKFLOW_VERSION_NOT_PUBLISHED", viewModel.DryRunResult!.RejectionCode);
        Assert.Equal(0, client.ExecuteCallCount);
    }

    private sealed class TempWorkflowFile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "MesControlAgv.WorkflowRemoteTests",
            Guid.NewGuid().ToString("N"));

        public string Path => System.IO.Path.Combine(_directory, "workflows.json");

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}

internal sealed class FakeWorkflowMesClient : IMesClient
{
    private readonly WorkflowDefinition _workflow;
    private ContractWorkflowVersion _version;

    public FakeWorkflowMesClient(WorkflowDefinition? workflow = null)
    {
        _workflow = workflow ?? new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "fake-workflow",
            Nodes =
            [
                new WorkflowNode { Type = WorkflowNodeType.Start, Name = "Start", Order = 1 },
                new WorkflowNode { Type = WorkflowNodeType.End, Name = "End", Order = 2 }
            ]
        };
        _version = CreateDraftVersion(ToContractDefinition(_workflow));
    }

    public Exception? CreateDraftException { get; init; }
    public int CreateDraftCallCount { get; private set; }
    public int ValidateVersionCallCount { get; private set; }
    public int PublishCallCount { get; private set; }
    public int ExecuteCallCount { get; private set; }

    public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DashboardTask>>([]);

    public Task<KpiDashboard> GetKpiDashboardAsync(DateOnly date, CancellationToken cancellationToken) =>
        Task.FromResult(new KpiDashboard(
            date,
            new KpiTaskSummary(0, 0, 0, 0, 0),
            [],
            new KpiSampleSummary(0, 0, 0, 0, 0, 0, "test"),
            [],
            []));

    public Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult<DashboardTaskDetail?>(null);

    public Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AgvDashboardSnapshot(false, "none", null, null));

    public Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken) =>
        Task.FromException<DashboardTask>(new NotSupportedException());

    public Task<DashboardTask> CreateTaskAsync(
        int sourceStationCode,
        int targetStationCode,
        int priority,
        string? description,
        string? externalId,
        CancellationToken cancellationToken) =>
        Task.FromException<DashboardTask>(new NotSupportedException());

    public Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromException<DashboardTask>(new NotSupportedException());

    public Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) =>
        Task.FromException<DashboardTask>(new NotSupportedException());

    public Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) =>
        Task.FromException<DashboardTask>(new NotSupportedException());

    public Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromException<DashboardTask>(new NotSupportedException());

    public Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromException<DashboardTask>(new NotSupportedException());

    public Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) =>
        Task.FromException<DashboardTask>(new NotSupportedException());

    public Task<IReadOnlyList<ContractWorkflowDefinition>> GetWorkflowsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ContractWorkflowDefinition>>([ToContractDefinition(_workflow)]);

    public Task<IReadOnlyList<ContractWorkflowVersion>> GetWorkflowVersionsAsync(Guid workflowId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ContractWorkflowVersion>>([_version]);

    public Task<ContractWorkflowVersion> CreateWorkflowDraftAsync(
        ContractWorkflowDefinition definition,
        string actor,
        CancellationToken cancellationToken)
    {
        CreateDraftCallCount++;
        if (CreateDraftException is { } exception) return Task.FromException<ContractWorkflowVersion>(exception);
        _version = CreateDraftVersion(definition);
        return Task.FromResult(_version);
    }

    public Task<ContractWorkflowVersion> UpdateWorkflowDraftAsync(
        Guid workflowId,
        int version,
        ContractWorkflowDefinition definition,
        string actor,
        CancellationToken cancellationToken) =>
        CreateWorkflowDraftAsync(definition, actor, cancellationToken);

    public Task<ContractWorkflowValidationResult> ValidateWorkflowAsync(
        ContractWorkflowDefinition definition,
        CancellationToken cancellationToken) =>
        Task.FromResult(ContractWorkflowValidationResult.Valid("fake"));

    public Task<ContractWorkflowValidationResult> ValidateWorkflowVersionAsync(
        Guid workflowId,
        int version,
        CancellationToken cancellationToken)
    {
        ValidateVersionCallCount++;
        return Task.FromResult(ContractWorkflowValidationResult.Valid("fake"));
    }

    public Task<ContractWorkflowVersion> PublishWorkflowAsync(
        Guid workflowId,
        int version,
        string actor,
        CancellationToken cancellationToken)
    {
        PublishCallCount++;
        _version = _version with
        {
            Status = ContractWorkflowVersionStatus.Published,
            PublishStatus = ContractWorkflowPublishStatus.Published,
            Validation = ContractWorkflowValidationResult.Valid("fake")
        };
        return Task.FromResult(_version);
    }

    public Task<ContractWorkflowExecutionResult> ExecuteWorkflowAsync(
        ContractWorkflowExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ExecuteCallCount++;
        return Task.FromResult(new ContractWorkflowExecutionResult
        {
            Status = ContractWorkflowExecutionStatus.Accepted,
            RequestId = request.RequestId,
            ExecutionId = Guid.NewGuid(),
            WorkflowId = request.WorkflowId,
            Version = request.Version,
            RequestedAt = request.RequestedAt,
            DryRun = request.DryRun
        });
    }

    private static ContractWorkflowVersion CreateDraftVersion(ContractWorkflowDefinition workflow) => new()
    {
        WorkflowId = workflow.Id,
        Version = 1,
        Definition = new MesControlAgv.Contracts.Workflows.WorkflowDefinition
        {
            Id = workflow.Id,
            Name = workflow.Name,
            Description = workflow.Description
        },
        Status = ContractWorkflowVersionStatus.Draft,
        PublishStatus = ContractWorkflowPublishStatus.NotPublished
    };

    private static ContractWorkflowDefinition ToContractDefinition(WorkflowDefinition workflow) => new()
    {
        Id = workflow.Id,
        Name = workflow.Name,
        Description = workflow.Description,
        Nodes = workflow.Nodes.Select(node => new MesControlAgv.Contracts.Workflows.WorkflowNode
        {
            Id = node.Id,
            Type = (MesControlAgv.Contracts.Workflows.WorkflowNodeType)node.Type,
            Name = node.Name,
            Description = node.Description,
            TargetStation = node.TargetStation,
            Order = node.Order
        }).ToList()
    };
}
