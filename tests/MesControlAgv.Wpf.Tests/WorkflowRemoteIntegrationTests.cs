using System.Net;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Workflows;

using ContractWorkflowDefinition = MesControlAgv.Contracts.Workflows.WorkflowDefinition;
using ContractWorkflowExecutionRequest = MesControlAgv.Contracts.Workflows.WorkflowExecutionRequest;
using ContractWorkflowExecutionResult = MesControlAgv.Contracts.Workflows.WorkflowExecutionResult;
using ContractWorkflowExecutionSnapshot = MesControlAgv.Contracts.Workflows.WorkflowExecutionSnapshot;
using ContractWorkflowExecutionStatus = MesControlAgv.Contracts.Workflows.WorkflowExecutionStatus;
using ContractWorkflowVersion = MesControlAgv.Contracts.Workflows.WorkflowVersion;
using ContractWorkflowRuntimeStatus = MesControlAgv.Contracts.Workflows.WorkflowRuntimeStatus;
using ContractWorkflowAuditResponse = MesControlAgv.Contracts.Workflows.WorkflowAuditResponse;
using ContractWorkflowVersionStatus = MesControlAgv.Contracts.Workflows.WorkflowVersionStatus;
using ContractWorkflowPublishStatus = MesControlAgv.Contracts.Workflows.WorkflowPublishStatus;
using ContractWorkflowValidationResult = MesControlAgv.Contracts.Workflows.WorkflowValidationResult;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkflowRemoteIntegrationTests
{
    [Fact]
    public async Task Accepted_simulator_execution_is_loaded_into_the_run_monitor()
    {
        using var fixture = new TempWorkflowFile();
        var client = new FakeWorkflowMesClient
        {
            ExecutionSnapshot = null
        };
        var startup = new StartupConfigurationReport
        {
            DiagnosticRuleVersion = "test",
            RuntimeMode = "simulator",
            ManageLocalServices = false,
            MesBaseUrl = new Uri("http://127.0.0.1:5045/"),
            SimulatorBaseUrl = new Uri("http://127.0.0.1:5183/"),
            AdapterBaseUrl = new Uri("http://127.0.0.1:5041/"),
            AdapterDriver = "simulator",
            AdapterRunMode = "standard",
            RealWriteAccess = "simulator-only",
            Items = []
        };
        using var main = new MainViewModel(
            client,
            workflowStore: new WorkflowStore(fixture.Path),
            startupConfiguration: startup);

        await main.WorkflowEditor.LoadRemoteAsync();
        await main.WorkflowEditor.SaveDraftAsync();
        await main.WorkflowEditor.ValidateRemoteAsync();
        await main.WorkflowEditor.PublishRemoteAsync();
        await main.WorkflowEditor.ExecuteSimulatorAsync();

        await WaitUntilAsync(() => client.LastExecutionId is not null &&
                                    main.WorkflowRunMonitor.Run?.ExecutionId == client.LastExecutionId &&
                                    !main.WorkflowRunMonitor.IsBusy);

        var executionId = client.LastExecutionId ?? throw new InvalidOperationException("The test execution was not accepted.");
        Assert.Equal(executionId, main.WorkflowRunMonitor.Run!.ExecutionId);
        Assert.Equal(executionId.ToString("D"), main.WorkflowRunMonitor.RunIdText);
        Assert.Equal("等待执行", main.WorkflowRunMonitor.RunStatusDisplay);
        Assert.Contains("运行监控已自动加载", main.WorkflowEditor.Message, StringComparison.Ordinal);
    }

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
        Assert.Contains("本地 JSON 仍可用", viewModel.Message);
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
        Assert.Contains("已取消", viewModel.Message, StringComparison.Ordinal);
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
        var client = new FakeWorkflowMesClient(workflow)
        {
            ExecutionSnapshot = new ContractWorkflowExecutionSnapshot
            {
                RuntimeStatus = ContractWorkflowRuntimeStatus.DryRunCompleted,
                Attempt = 0
            },
            WorkflowAudits = [new ContractWorkflowAuditResponse { EventType = "WorkflowExecutionAccepted" }]
        };
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
        Assert.Equal(1, client.ExecutionSnapshotCallCount);
        Assert.Equal(ContractWorkflowRuntimeStatus.DryRunCompleted, viewModel.ExecutionSnapshot!.RuntimeStatus);
        Assert.Contains("模拟运行完成", viewModel.ExecutionRuntimeSummary, StringComparison.Ordinal);
        Assert.Contains("WorkflowExecutionAccepted", viewModel.ExecutionAuditSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remote_validation_result_populates_the_locatable_problem_projection()
    {
        using var fixture = new TempWorkflowFile();
        var targetNodeId = Guid.NewGuid();
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "invalid-remote-workflow",
            Nodes =
            [
                new WorkflowNode { Type = WorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [targetNodeId] },
                new WorkflowNode { Id = targetNodeId, Type = WorkflowNodeType.End, Name = "End", Order = 2 }
            ]
        };
        var client = new FakeWorkflowMesClient(workflow)
        {
            ValidationResult = new ContractWorkflowValidationResult
            {
                ValidatorVersion = "workflow-publication-v2",
                CatalogVersion = "catalog-v1",
                Issues =
                [
                    new MesControlAgv.Contracts.Workflows.WorkflowValidationIssue
                    {
                        Code = "NODE_CONFIGURATION_REQUIRED",
                        Message = "A required configuration value is missing.",
                        NodeId = targetNodeId,
                        ConfigurationKey = "timeoutSeconds"
                    }
                ]
            }
        };
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path), client);

        await viewModel.LoadRemoteAsync();
        await viewModel.ValidateRemoteAsync();

        Assert.Equal(WorkflowRemoteState.ValidationFailed, viewModel.RemoteState);
        var issue = Assert.Single(viewModel.Validation.AllIssues);
        Assert.Equal("NODE_CONFIGURATION_REQUIRED", issue.Code);
        Assert.Equal(targetNodeId, issue.NodeId);
        Assert.Equal("节点：End", issue.Location);
        Assert.Contains("workflow-publication-v2", viewModel.Validation.ValidatorMetadata, StringComparison.Ordinal);
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("The accepted simulator execution was not loaded into the run monitor.");
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
    public int ExecutionSnapshotCallCount { get; private set; }
    public ContractWorkflowExecutionSnapshot? ExecutionSnapshot { get; set; }
    public Guid? LastExecutionId { get; private set; }
    public IReadOnlyList<ContractWorkflowAuditResponse> WorkflowAudits { get; init; } = [];
    public ContractWorkflowValidationResult ValidationResult { get; init; } =
        ContractWorkflowValidationResult.Valid("fake");

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

    public Task<ContractWorkflowVersion?> GetWorkflowVersionAsync(
        Guid workflowId,
        int version,
        CancellationToken cancellationToken) =>
        Task.FromResult<ContractWorkflowVersion?>(_version.WorkflowId == workflowId && _version.Version == version
            ? _version
            : null);

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
        Task.FromResult(ValidationResult);

    public Task<ContractWorkflowValidationResult> ValidateWorkflowVersionAsync(
        Guid workflowId,
        int version,
        CancellationToken cancellationToken)
    {
        ValidateVersionCallCount++;
        _version = _version with { Validation = ValidationResult };
        return Task.FromResult(ValidationResult);
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
        var executionId = Guid.NewGuid();
        LastExecutionId = executionId;
        ExecutionSnapshot ??= new ContractWorkflowExecutionSnapshot
        {
            RequestId = request.RequestId,
            ExecutionId = executionId,
            WorkflowId = request.WorkflowId,
            Version = request.Version,
            RuntimeStatus = ContractWorkflowRuntimeStatus.Prepared,
            DryRun = request.DryRun,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return Task.FromResult(new ContractWorkflowExecutionResult
        {
            Status = ContractWorkflowExecutionStatus.Accepted,
            RequestId = request.RequestId,
            ExecutionId = executionId,
            WorkflowId = request.WorkflowId,
            Version = request.Version,
            RequestedAt = request.RequestedAt,
            DryRun = request.DryRun
        });
    }

    public Task<ContractWorkflowExecutionSnapshot?> GetWorkflowExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        ExecutionSnapshotCallCount++;
        return Task.FromResult(ExecutionSnapshot);
    }

    public Task<IReadOnlyList<ContractWorkflowAuditResponse>> GetWorkflowAuditsAsync(
        Guid workflowId,
        int? version,
        int limit,
        CancellationToken cancellationToken) => Task.FromResult(WorkflowAudits);

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
