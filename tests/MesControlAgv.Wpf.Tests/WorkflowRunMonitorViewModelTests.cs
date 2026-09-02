using System.Windows.Input;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkflowRunMonitorViewModelTests
{
    [Fact]
    public async Task Load_projects_the_pinned_graph_and_preserves_the_runtime_viewport_on_refresh()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture);
        var monitor = new WorkflowRunMonitorViewModel(client);

        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.Equal(fixture.Version.Definition.Name, monitor.RunTitle);
        Assert.Equal("运行中", monitor.RunStatusDisplay);
        Assert.Equal(WorkflowCanvasMode.Runtime, monitor.CanvasViewModel!.CanvasMode);
        Assert.False(monitor.CanvasViewModel.IsEditing);
        Assert.Equal(2, monitor.CanvasViewModel.Nodes.Single(node => node.Id == fixture.MoveNodeId).Location.X);
        Assert.Equal("Completed", monitor.CanvasViewModel.Nodes.Single(node => node.Id == fixture.StartNodeId).RuntimeState);
        Assert.Equal("Running", monitor.CanvasViewModel.Nodes.Single(node => node.Id == fixture.MoveNodeId).RuntimeState);
        Assert.Equal("NotStarted", monitor.CanvasViewModel.Nodes.Single(node => node.Id == fixture.EndNodeId).RuntimeState);
        Assert.Equal(fixture.NodeExecution.Id, monitor.SelectedNode!.Id);
        Assert.Equal(fixture.DeviceOperation.OperationId, monitor.SelectedDeviceOperation!.OperationId);
        Assert.Equal("targetStation=SAMPLE_01", monitor.SelectedNode.InputSummary);
        Assert.False(monitor.HasUnknownState);

        var canvas = monitor.CanvasViewModel;
        var publishedLayouts = fixture.Version.Definition.Layouts
            .Select(layout => (layout.NodeId, layout.X, layout.Y, layout.Width, layout.Height))
            .ToArray();
        canvas.UpdateViewport(320, 180, 0.75);
        client.Run = fixture.Run with
        {
            RuntimeStatus = WorkflowRuntimeStatus.Completed,
            CurrentNodeId = fixture.EndNodeId,
            UpdatedAt = fixture.Run.UpdatedAt.AddMinutes(1)
        };
        client.Nodes =
        [
            fixture.NodeExecution with
            {
                Status = WorkflowNodeExecutionStatus.Succeeded,
                CompletedAt = fixture.NodeExecution.UpdatedAt.AddSeconds(30),
                UpdatedAt = fixture.NodeExecution.UpdatedAt.AddSeconds(30)
            }
        ];
        client.DeviceOperations =
        [
            fixture.DeviceOperation with
            {
                Status = WorkflowDeviceOperationStatus.Succeeded,
                CompletedAt = fixture.DeviceOperation.UpdatedAt.AddSeconds(30),
                UpdatedAt = fixture.DeviceOperation.UpdatedAt.AddSeconds(30)
            }
        ];

        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.Same(canvas, monitor.CanvasViewModel);
        Assert.Equal(320, monitor.CanvasViewModel.Document.Viewport.X);
        Assert.Equal(180, monitor.CanvasViewModel.Document.Viewport.Y);
        Assert.Equal(0.75, monitor.CanvasViewModel.Document.Viewport.Zoom);
        Assert.Equal("Completed", monitor.CanvasViewModel.Nodes.Single(node => node.Id == fixture.MoveNodeId).RuntimeState);
        Assert.Equal("Completed", monitor.CanvasViewModel.Nodes.Single(node => node.Id == fixture.EndNodeId).RuntimeState);
        Assert.Equal(
            publishedLayouts,
            fixture.Version.Definition.Layouts.Select(layout =>
                (layout.NodeId, layout.X, layout.Y, layout.Width, layout.Height)));
        Assert.Equal(0, fixture.Version.Definition.Viewport.X);
    }

    [Fact]
    public async Task Unknown_node_and_device_evidence_remain_distinct_and_never_expose_a_retry_action()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Run = fixture.Run with { RuntimeStatus = WorkflowRuntimeStatus.Unknown },
            Nodes = [fixture.NodeExecution with { Status = WorkflowNodeExecutionStatus.Unknown, LastError = "response timeout" }],
            DeviceOperations =
            [
                fixture.DeviceOperation with
                {
                    Status = WorkflowDeviceOperationStatus.Unknown,
                    LastError = "response timeout"
                }
            ]
        };
        var monitor = new WorkflowRunMonitorViewModel(client);

        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.True(monitor.HasUnknownState);
        Assert.Equal("结果未知", monitor.RunStatusDisplay);
        Assert.Equal("#A30D5D", monitor.RunStatusBrush);
        Assert.Equal("结果未知", monitor.SelectedNode!.StatusDisplay);
        Assert.Equal("#A30D5D", monitor.SelectedNode.StatusBrush);
        Assert.Equal("Unknown", monitor.CanvasViewModel!.Nodes.Single(node => node.Id == fixture.MoveNodeId).RuntimeState);
        Assert.Contains("禁止自动重试", monitor.UnknownWarning, StringComparison.Ordinal);
        var commandProperties = monitor.GetType().GetProperties()
            .Where(property => typeof(ICommand).IsAssignableFrom(property.PropertyType))
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(commandProperties, name => name.Contains("Retry", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(nameof(WorkflowRunMonitorViewModel.ResolveUnknownSucceededCommand), commandProperties);
        Assert.Contains(nameof(WorkflowRunMonitorViewModel.ResolveUnknownFailedCommand), commandProperties);
        Assert.False(monitor.CanResolveUnknown);
        Assert.Contains(WorkflowRunControlPermissions.ResolveUnknown, monitor.UnknownResolutionUnavailableReason);
    }

    [Fact]
    public async Task Pause_requires_server_permission_reason_and_confirmation_and_explains_active_device_semantics()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            GrantedPermissions =
            [
                WorkflowRunControlPermissions.Pause,
                WorkflowRunControlPermissions.Cancel
            ]
        };
        var confirmation = new WorkflowRunControlConfirmationStub();
        var monitor = new WorkflowRunMonitorViewModel(client, confirmation);
        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.False(monitor.CanPause);
        Assert.Contains("原因", monitor.PauseUnavailableReason, StringComparison.Ordinal);
        monitor.ControlReason = "Hold before the next node";

        Assert.True(monitor.CanPause);
        Assert.False(monitor.CanCancel);
        Assert.Contains("仅静止", monitor.CancelUnavailableReason, StringComparison.Ordinal);
        monitor.PauseCommand.Execute(null);
        await WaitUntilAsync(() => client.PauseRequests.Count == 1 && !monitor.IsBusy);

        var request = Assert.Single(client.PauseRequests);
        Assert.Equal("local-operator", request.Actor);
        Assert.Equal("Hold before the next node", request.Reason);
        Assert.Contains("不会向设备发送暂停命令", Assert.Single(confirmation.Messages), StringComparison.Ordinal);
        Assert.Equal(WorkflowRuntimeStatus.Paused, monitor.Run!.RuntimeStatus);
        Assert.Empty(monitor.ControlReason);
    }

    [Fact]
    public async Task Unknown_resolution_has_two_explicit_conclusions_and_confirmed_success_never_calls_retry()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Run = fixture.Run with { RuntimeStatus = WorkflowRuntimeStatus.Unknown },
            Nodes = [fixture.NodeExecution with { Status = WorkflowNodeExecutionStatus.Unknown }],
            DeviceOperations = [fixture.DeviceOperation with { Status = WorkflowDeviceOperationStatus.Unknown }],
            GrantedPermissions = [WorkflowRunControlPermissions.ResolveUnknown]
        };
        var confirmation = new WorkflowRunControlConfirmationStub();
        var monitor = new WorkflowRunMonitorViewModel(client, confirmation);
        await monitor.LoadAsync(fixture.Run.ExecutionId);
        monitor.ControlReason = "Field log confirms arrival";

        Assert.True(monitor.CanResolveUnknown);
        monitor.ResolveUnknownSucceededCommand.Execute(null);
        await WaitUntilAsync(() => client.UnknownResolutionRequests.Count == 1 && !monitor.IsBusy);

        var request = Assert.Single(client.UnknownResolutionRequests);
        Assert.Equal(fixture.NodeExecution.Id, request.NodeExecutionId);
        Assert.Equal(WorkflowUnknownResolutionOutcome.ConfirmedSucceeded, request.Outcome);
        Assert.Contains("不会重发设备命令", Assert.Single(confirmation.Messages), StringComparison.Ordinal);
        Assert.Equal(WorkflowRuntimeStatus.Prepared, monitor.Run!.RuntimeStatus);
        Assert.Single(monitor.DeviceOperations);
    }

    [Fact]
    public async Task Load_rejects_device_evidence_from_another_run()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            DeviceOperations =
            [
                fixture.DeviceOperation with { WorkflowRunId = Guid.NewGuid() }
            ]
        };
        var monitor = new WorkflowRunMonitorViewModel(client);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            monitor.LoadAsync(fixture.Run.ExecutionId));

        Assert.Contains("device evidence", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(monitor.Run);
        Assert.Null(monitor.CanvasViewModel);
        Assert.Empty(monitor.Nodes);
        Assert.False(monitor.IsBusy);
    }

    [Fact]
    public async Task Load_rejects_timeline_evidence_without_a_linked_node_or_operation()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Timeline =
            [
                fixture.TimelineEntry with
                {
                    NodeExecutionId = Guid.NewGuid(),
                    DeviceOperationId = Guid.NewGuid()
                }
            ]
        };
        var monitor = new WorkflowRunMonitorViewModel(client);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            monitor.LoadAsync(fixture.Run.ExecutionId));

        Assert.Contains("timeline evidence", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(monitor.Run);
        Assert.Null(monitor.CanvasViewModel);
        Assert.Empty(monitor.Timeline);
        Assert.False(monitor.IsBusy);
    }

    [Fact]
    public async Task Selecting_device_and_timeline_evidence_navigates_to_the_linked_node_attempt()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var secondNode = fixture.NodeExecution with
        {
            Id = Guid.NewGuid(),
            StepRequestId = Guid.NewGuid(),
            NodeId = fixture.EndNodeId,
            NodeTypeId = WorkflowGraphNodeTypeIds.End,
            NodeName = "End",
            Status = WorkflowNodeExecutionStatus.Ready,
            CreatedAt = fixture.NodeExecution.CreatedAt.AddMinutes(1),
            UpdatedAt = fixture.NodeExecution.UpdatedAt.AddMinutes(1)
        };
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Run = fixture.Run with { CurrentNodeId = fixture.EndNodeId },
            Nodes = [fixture.NodeExecution, secondNode],
            Timeline =
            [
                fixture.TimelineEntry,
                fixture.TimelineEntry with
                {
                    Id = Guid.NewGuid(),
                    NodeExecutionId = secondNode.Id,
                    DeviceOperationId = null,
                    EventType = "WorkflowNodePrepared",
                    OccurredAt = fixture.TimelineEntry.OccurredAt.AddMinutes(1)
                }
            ]
        };
        var monitor = new WorkflowRunMonitorViewModel(client);
        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.Equal(secondNode.Id, monitor.SelectedNode!.Id);

        monitor.SelectedDeviceOperation = Assert.Single(monitor.DeviceOperations);

        Assert.Equal(fixture.NodeExecution.Id, monitor.SelectedNode!.Id);
        Assert.Equal(fixture.MoveNodeId, monitor.CanvasViewModel!.SelectedNode!.Id);

        monitor.SelectedTimelineEntry = monitor.Timeline[^1];

        Assert.Equal(secondNode.Id, monitor.SelectedNode!.Id);
        Assert.Equal(fixture.EndNodeId, monitor.CanvasViewModel.SelectedNode!.Id);
    }

    [Fact]
    public async Task Ready_move_can_create_and_authorize_a_linked_field_acceptance_without_dispatching()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Run = fixture.Run with { RuntimeStatus = WorkflowRuntimeStatus.Prepared },
            Nodes = [fixture.NodeExecution with
            {
                Status = WorkflowNodeExecutionStatus.Ready,
                StartedAt = null
            }],
            DeviceOperations = [],
            Timeline = []
        };
        var confirmation = new WorkflowRunControlConfirmationStub();
        var monitor = new WorkflowRunMonitorViewModel(client, confirmation);
        await monitor.LoadAsync(fixture.Run.ExecutionId);
        monitor.FieldAgvId = "AGV-01";
        monitor.FieldSourceStationId = "LM1";
        monitor.FieldSafetyObserverName = "safety-observer";
        monitor.FieldPermitId = "permit-ui-1";
        monitor.FieldPermitMinutes = "30";

        Assert.True(monitor.CanCreateAndAuthorizeFieldMove);
        monitor.CreateAndAuthorizeFieldMoveCommand.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (client.AuthorizedAcceptances.Count == 0 &&
               !monitor.StatusMessage.Contains("失败", StringComparison.Ordinal) &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(client.AuthorizedAcceptances.Count == 1, monitor.StatusMessage);
        await WaitUntilAsync(() => !monitor.IsBusy);

        var request = Assert.Single(client.CreatedAcceptanceRequests);
        Assert.Equal(fixture.Run.ExecutionId, request.WorkflowRunId);
        Assert.Equal(fixture.NodeExecution.Id, request.WorkflowNodeExecutionId);
        Assert.Equal("SAMPLE_01", request.TargetStationId);
        Assert.Equal("permit-ui-1", Assert.Single(client.AuthorizationRequests).PermitId);
        Assert.Contains("不会由 WPF 直接派发 AGV", Assert.Single(confirmation.Messages), StringComparison.Ordinal);
        Assert.True(monitor.HasFieldAcceptance);
        Assert.Equal(FieldNavigationAcceptanceStatuses.Authorized, monitor.SelectedFieldAcceptance!.Status);
        Assert.Contains("未直接发送 AGV 命令", monitor.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_projection_reports_progress_failure_evidence_and_cancel_semantics()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var failedNode = fixture.NodeExecution with
        {
            Status = WorkflowNodeExecutionStatus.Failed,
            LastError = "simulator rejected the command"
        };
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Run = fixture.Run with
            {
                RuntimeStatus = WorkflowRuntimeStatus.Failed,
                LastError = "workflow stopped"
            },
            Nodes = [failedNode],
            DeviceOperations =
            [
                fixture.DeviceOperation with
                {
                    Status = WorkflowDeviceOperationStatus.Failed,
                    LastError = "adapter returned rejected"
                }
            ],
            Timeline =
            [
                fixture.TimelineEntry with
                {
                    Outcome = "Failed",
                    Reason = "operator acknowledgement required"
                }
            ]
        };
        var monitor = new WorkflowRunMonitorViewModel(client);

        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.Equal(1, monitor.TotalNodeCount);
        Assert.Equal(0, monitor.CompletedNodeCount);
        Assert.Equal(1, monitor.FailedNodeCount);
        Assert.Equal(1, monitor.TerminalNodeCount);
        Assert.Equal(100, monitor.ProgressPercent);
        Assert.Contains("1/1", monitor.ProgressDisplay, StringComparison.Ordinal);
        Assert.Contains("workflow stopped", monitor.FailureReasonDisplay, StringComparison.Ordinal);
        Assert.Contains("simulator rejected the command", monitor.FailureReasonDisplay, StringComparison.Ordinal);
        Assert.Contains("adapter returned rejected", monitor.FailureReasonDisplay, StringComparison.Ordinal);
        Assert.Equal("错误=adapter returned rejected", monitor.DeviceOperations.Single().ResultOrErrorSummary);
        Assert.False(monitor.IsCancelled);

        client.Run = client.Run with { RuntimeStatus = WorkflowRuntimeStatus.Cancelled };
        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.True(monitor.IsCancelled);
        Assert.Contains("不会自动撤销", monitor.CancellationStatusDisplay, StringComparison.Ordinal);
        Assert.Equal("流程已终态，自动刷新已暂停", monitor.AutoRefreshStatusDisplay);
    }

    [Fact]
    public async Task Auto_refresh_updates_the_run_and_stops_after_terminal_state()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture);
        var monitor = new WorkflowRunMonitorViewModel(client)
        {
            AutoRefreshInterval = TimeSpan.FromMilliseconds(25)
        };
        monitor.StartAutoRefresh();

        await monitor.LoadAsync(fixture.Run.ExecutionId);
        var initialReads = client.ExecutionReadCount;
        client.Run = fixture.Run with
        {
            RuntimeStatus = WorkflowRuntimeStatus.Completed,
            CurrentNodeId = fixture.EndNodeId
        };
        client.Nodes =
        [
            fixture.NodeExecution with
            {
                Status = WorkflowNodeExecutionStatus.Succeeded,
                CompletedAt = fixture.NodeExecution.UpdatedAt.AddSeconds(1),
                UpdatedAt = fixture.NodeExecution.UpdatedAt.AddSeconds(1)
            }
        ];

        await WaitUntilAsync(() => client.ExecutionReadCount > initialReads &&
                                   monitor.Run?.RuntimeStatus == WorkflowRuntimeStatus.Completed);

        Assert.Equal(100, monitor.ProgressPercent);
        Assert.False(monitor.IsAutoRefreshRunning);
        Assert.Equal("流程已终态，自动刷新已暂停", monitor.AutoRefreshStatusDisplay);
        monitor.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), "The workflow run control command did not complete in time.");
    }
}

internal sealed class WorkflowRunMonitorClientStub : IMesClient
{
    private readonly WorkflowRunMonitorFixture _fixture;

    public WorkflowRunMonitorClientStub(WorkflowRunMonitorFixture fixture)
    {
        _fixture = fixture;
        Run = fixture.Run;
        Nodes = [fixture.NodeExecution];
        DeviceOperations = [fixture.DeviceOperation];
        Timeline = [fixture.TimelineEntry];
    }

    public WorkflowExecutionSnapshot? Run { get; set; }
    public IReadOnlyList<WorkflowNodeExecutionSnapshot> Nodes { get; set; }
    public IReadOnlyList<WorkflowDeviceOperationSnapshot> DeviceOperations { get; set; }
    public IReadOnlyList<WorkflowRunTimelineEntry> Timeline { get; set; }
    public IReadOnlyList<FieldNavigationAcceptanceResponse> FieldAcceptances { get; set; } = [];
    public IReadOnlyList<string> GrantedPermissions { get; set; } = [];
    public List<WorkflowRunControlRequest> PauseRequests { get; } = [];
    public List<WorkflowRunControlRequest> ResumeRequests { get; } = [];
    public List<WorkflowRunControlRequest> CancelRequests { get; } = [];
    public List<WorkflowUnknownResolutionRequest> UnknownResolutionRequests { get; } = [];
    public List<CreateFieldNavigationAcceptanceRequest> CreatedAcceptanceRequests { get; } = [];
    public List<AuthorizeFieldNavigationAcceptanceRequest> AuthorizationRequests { get; } = [];
    public List<FieldNavigationAcceptanceResponse> AuthorizedAcceptances { get; } = [];
    public int ExecutionReadCount { get; private set; }

    public Task<WorkflowExecutionSnapshot?> GetWorkflowExecutionAsync(Guid executionId, CancellationToken cancellationToken) =>
        ReadExecution();

    private Task<WorkflowExecutionSnapshot?> ReadExecution()
    {
        ExecutionReadCount++;
        return Task.FromResult(Run);
    }

    public Task<WorkflowVersion?> GetWorkflowVersionAsync(Guid workflowId, int version, CancellationToken cancellationToken) =>
        Task.FromResult<WorkflowVersion?>(_fixture.Version);

    public Task<IReadOnlyList<WorkflowNodeExecutionSnapshot>> GetWorkflowNodeExecutionsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken) => Task.FromResult(Nodes);

    public Task<IReadOnlyList<WorkflowDeviceOperationSnapshot>> GetWorkflowDeviceOperationsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken) => Task.FromResult(DeviceOperations);

    public Task<IReadOnlyList<WorkflowRunTimelineEntry>> GetWorkflowRunTimelineAsync(
        Guid workflowRunId,
        int limit,
        CancellationToken cancellationToken) => Task.FromResult(Timeline);

    public Task<IReadOnlyList<FieldNavigationAcceptanceResponse>> GetWorkflowFieldNavigationAcceptancesAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken) => Task.FromResult(FieldAcceptances);

    public Task<FieldNavigationAcceptanceResponse> CreateFieldNavigationAcceptanceAsync(
        CreateFieldNavigationAcceptanceRequest request,
        CancellationToken cancellationToken)
    {
        CreatedAcceptanceRequests.Add(request);
        return Task.FromResult(CreateAcceptance(request, FieldNavigationAcceptanceStatuses.Draft));
    }

    public Task<FieldNavigationAcceptanceResponse> AuthorizeFieldNavigationAcceptanceAsync(
        Guid acceptanceId,
        AuthorizeFieldNavigationAcceptanceRequest request,
        CancellationToken cancellationToken)
    {
        AuthorizationRequests.Add(request);
        var created = CreatedAcceptanceRequests[^1];
        var authorized = CreateAcceptance(created, FieldNavigationAcceptanceStatuses.Authorized, acceptanceId) with
        {
            OperatorName = request.OperatorName,
            SafetyObserverName = request.SafetyObserverName,
            PermitId = request.PermitId,
            AuthorizedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = request.ExpiresAtUtc
        };
        AuthorizedAcceptances.Add(authorized);
        FieldAcceptances = [authorized];
        return Task.FromResult(authorized);
    }

    private static FieldNavigationAcceptanceResponse CreateAcceptance(
        CreateFieldNavigationAcceptanceRequest request,
        string status,
        Guid? acceptanceId = null) => new(
            acceptanceId ?? Guid.NewGuid(),
            status,
            request.AgvId,
            request.SourceStationId,
            request.TargetStationId,
            "test-map",
            "0123456789abcdef0123456789abcdef",
            [request.SourceStationId, request.TargetStationId],
            request.Description,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow)
        {
            WorkflowRunId = request.WorkflowRunId,
            WorkflowNodeExecutionId = request.WorkflowNodeExecutionId
        };

    public Task<WorkflowRunControlPermissionsSnapshot> GetWorkflowRunControlPermissionsAsync(
        string actor,
        CancellationToken cancellationToken) => Task.FromResult(new WorkflowRunControlPermissionsSnapshot
    {
        Actor = actor,
        Permissions = GrantedPermissions
    });

    public Task<WorkflowRunControlResult> PauseWorkflowRunAsync(
        Guid workflowRunId,
        WorkflowRunControlRequest request,
        CancellationToken cancellationToken)
    {
        PauseRequests.Add(request);
        Run = Run! with { RuntimeStatus = WorkflowRuntimeStatus.Paused };
        return Task.FromResult(ControlResult(request.RequestId, WorkflowRunControlAction.Pause));
    }

    public Task<WorkflowRunControlResult> ResumeWorkflowRunAsync(
        Guid workflowRunId,
        WorkflowRunControlRequest request,
        CancellationToken cancellationToken)
    {
        ResumeRequests.Add(request);
        Run = Run! with { RuntimeStatus = WorkflowRuntimeStatus.Prepared };
        return Task.FromResult(ControlResult(request.RequestId, WorkflowRunControlAction.Resume));
    }

    public Task<WorkflowRunControlResult> CancelWorkflowRunAsync(
        Guid workflowRunId,
        WorkflowRunControlRequest request,
        CancellationToken cancellationToken)
    {
        CancelRequests.Add(request);
        Run = Run! with { RuntimeStatus = WorkflowRuntimeStatus.Cancelled };
        return Task.FromResult(ControlResult(request.RequestId, WorkflowRunControlAction.Cancel));
    }

    public Task<WorkflowRunControlResult> ResolveWorkflowRunUnknownAsync(
        Guid workflowRunId,
        WorkflowUnknownResolutionRequest request,
        CancellationToken cancellationToken)
    {
        UnknownResolutionRequests.Add(request);
        Run = Run! with { RuntimeStatus = WorkflowRuntimeStatus.Prepared };
        Nodes = Nodes.Select(node => node.Id == request.NodeExecutionId
            ? node with { Status = WorkflowNodeExecutionStatus.Succeeded }
            : node).ToArray();
        DeviceOperations = DeviceOperations.Select(operation => operation.NodeExecutionId == request.NodeExecutionId
            ? operation with { Status = WorkflowDeviceOperationStatus.Succeeded }
            : operation).ToArray();
        return Task.FromResult(ControlResult(request.RequestId, WorkflowRunControlAction.ResolveUnknown));
    }

    private WorkflowRunControlResult ControlResult(Guid requestId, WorkflowRunControlAction action) => new()
    {
        RequestId = requestId,
        WorkflowRunId = Run!.ExecutionId,
        Action = action,
        Run = Run
    };

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

    public Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken) => UnsupportedTask();

    public Task<DashboardTask> CreateTaskAsync(
        int sourceStationCode,
        int targetStationCode,
        int priority,
        string? description,
        string? externalId,
        CancellationToken cancellationToken) => UnsupportedTask();

    public Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken) => UnsupportedTask();
    public Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => UnsupportedTask();
    public Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => UnsupportedTask();
    public Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken) => UnsupportedTask();
    public Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken) => UnsupportedTask();
    public Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => UnsupportedTask();

    private static Task<DashboardTask> UnsupportedTask() =>
        Task.FromException<DashboardTask>(new NotSupportedException());
}

internal sealed class WorkflowRunControlConfirmationStub(bool result = true) : IWorkflowRunControlConfirmation
{
    public List<string> Messages { get; } = [];

    public bool Confirm(string title, string message)
    {
        Messages.Add(message);
        return result;
    }
}

internal sealed record WorkflowRunMonitorFixture(
    Guid StartNodeId,
    Guid MoveNodeId,
    Guid EndNodeId,
    WorkflowExecutionSnapshot Run,
    WorkflowVersion Version,
    WorkflowNodeExecutionSnapshot NodeExecution,
    WorkflowDeviceOperationSnapshot DeviceOperation,
    WorkflowRunTimelineEntry TimelineEntry)
{
    public static WorkflowRunMonitorFixture Create()
    {
        var workflowId = Guid.NewGuid();
        var workflowRunId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var moveNodeId = Guid.NewGuid();
        var endNodeId = Guid.NewGuid();
        var nodeExecutionId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var startedAt = DateTimeOffset.Parse("2026-08-21T08:00:00Z");
        var definition = new WorkflowDefinition
        {
            Id = workflowId,
            Name = "Runtime monitor workflow",
            PublishedVersion = 3,
            Nodes =
            [
                new WorkflowNode
                {
                    Id = startNodeId,
                    Type = WorkflowNodeType.Start,
                    NodeTypeId = WorkflowGraphNodeTypeIds.Start,
                    Name = "Start",
                    Order = 1,
                    NextNodeIds = [moveNodeId]
                },
                new WorkflowNode
                {
                    Id = moveNodeId,
                    Type = WorkflowNodeType.Move,
                    NodeTypeId = WorkflowGraphNodeTypeIds.Move,
                    Name = "Move sample",
                    TargetStation = "SAMPLE_01",
                    Order = 2,
                    NextNodeIds = [endNodeId]
                },
                new WorkflowNode
                {
                    Id = endNodeId,
                    Type = WorkflowNodeType.End,
                    NodeTypeId = WorkflowGraphNodeTypeIds.End,
                    Name = "End",
                    Order = 3
                }
            ],
            Layouts =
            [
                new WorkflowNodeLayout { NodeId = startNodeId, X = 1, Y = 10, Width = 200, Height = 120 },
                new WorkflowNodeLayout { NodeId = moveNodeId, X = 2, Y = 20, Width = 200, Height = 120 },
                new WorkflowNodeLayout { NodeId = endNodeId, X = 3, Y = 30, Width = 200, Height = 120 }
            ],
            Viewport = new WorkflowCanvasViewport { X = 0, Y = 0, Zoom = 1 }
        };
        var run = new WorkflowExecutionSnapshot
        {
            ExecutionId = workflowRunId,
            RequestId = requestId,
            WorkflowId = workflowId,
            Version = 3,
            RuntimeStatus = WorkflowRuntimeStatus.Running,
            CurrentNodeId = moveNodeId,
            Attempt = 1,
            CreatedAt = startedAt,
            UpdatedAt = startedAt.AddSeconds(15)
        };
        var version = new WorkflowVersion
        {
            WorkflowId = workflowId,
            Version = 3,
            Definition = definition,
            Status = WorkflowVersionStatus.Published,
            PublishStatus = WorkflowPublishStatus.Published
        };
        var nodeExecution = new WorkflowNodeExecutionSnapshot
        {
            Id = nodeExecutionId,
            WorkflowRunId = workflowRunId,
            WorkflowId = workflowId,
            Version = 3,
            StepRequestId = Guid.NewGuid(),
            NodeId = moveNodeId,
            NodeTypeId = WorkflowGraphNodeTypeIds.Move,
            NodeName = "Move sample",
            Attempt = 1,
            Status = WorkflowNodeExecutionStatus.Running,
            Inputs = new Dictionary<string, string?> { ["targetStation"] = "SAMPLE_01" },
            StartedAt = startedAt,
            CreatedAt = startedAt,
            UpdatedAt = startedAt.AddSeconds(15)
        };
        var deviceOperation = new WorkflowDeviceOperationSnapshot
        {
            OperationId = operationId,
            WorkflowRunId = workflowRunId,
            NodeExecutionId = nodeExecutionId,
            RequestId = requestId,
            Attempt = 1,
            CapabilityId = "agv.navigate-to-station",
            IdempotencyKey = operationId.ToString("N"),
            CorrelationId = "sample-42",
            Status = WorkflowDeviceOperationStatus.Running,
            RequestSummary = new Dictionary<string, string?> { ["targetStation"] = "SAMPLE_01" },
            RequestedAt = startedAt,
            UpdatedAt = startedAt.AddSeconds(15)
        };
        var timelineEntry = new WorkflowRunTimelineEntry
        {
            Id = Guid.NewGuid(),
            WorkflowRunId = workflowRunId,
            NodeExecutionId = nodeExecutionId,
            DeviceOperationId = operationId,
            EventType = "WorkflowDeviceOperationUpdated",
            Outcome = "Running",
            Actor = "simulator-worker",
            CorrelationId = "sample-42",
            Details = new Dictionary<string, string?> { ["attempt"] = "1" },
            OccurredAt = startedAt.AddSeconds(15)
        };
        return new WorkflowRunMonitorFixture(
            startNodeId,
            moveNodeId,
            endNodeId,
            run,
            version,
            nodeExecution,
            deviceOperation,
            timelineEntry);
    }
}
