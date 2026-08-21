using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// Read-only projection of one pinned workflow run. It only consumes MES read
/// APIs and never exposes an execution or device command.
/// </summary>
public sealed class WorkflowRunMonitorViewModel : INotifyPropertyChanged
{
    private readonly IMesClient _mes;
    private readonly AsyncCommand _refreshCommand;
    private string _runIdText = string.Empty;
    private WorkflowExecutionSnapshot? _run;
    private WorkflowVersion? _version;
    private IReadOnlyList<WorkflowRunNodeItemViewModel> _nodes = [];
    private IReadOnlyList<WorkflowRunDeviceOperationItemViewModel> _deviceOperations = [];
    private IReadOnlyList<WorkflowRunTimelineItemViewModel> _timeline = [];
    private WorkflowRunNodeItemViewModel? _selectedNode;
    private WorkflowRunDeviceOperationItemViewModel? _selectedDeviceOperation;
    private WorkflowRunTimelineItemViewModel? _selectedTimelineEntry;
    private WorkflowCanvasSpikeViewModel? _canvasViewModel;
    private string _statusMessage = "请输入流程运行 ID。";
    private bool _isBusy;
    private bool _synchronizingSelection;
    private DateTimeOffset? _refreshedAt;

    public WorkflowRunMonitorViewModel(IMesClient mes)
    {
        _mes = mes ?? throw new ArgumentNullException(nameof(mes));
        _refreshCommand = new AsyncCommand(RefreshFromInputAsync, CanRefresh);
        RefreshCommand = _refreshCommand;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string RunIdText
    {
        get => _runIdText;
        set
        {
            if (!SetField(ref _runIdText, value ?? string.Empty)) return;
            _refreshCommand.RaiseCanExecuteChanged();
        }
    }

    public ICommand RefreshCommand { get; }

    public WorkflowExecutionSnapshot? Run => _run;

    public WorkflowVersion? Version => _version;

    public WorkflowCanvasSpikeViewModel? CanvasViewModel
    {
        get => _canvasViewModel;
        private set
        {
            if (ReferenceEquals(_canvasViewModel, value)) return;
            if (_canvasViewModel is not null)
                _canvasViewModel.SelectionChanged -= CanvasSelectionChanged;
            _canvasViewModel = value;
            if (_canvasViewModel is not null)
                _canvasViewModel.SelectionChanged += CanvasSelectionChanged;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<WorkflowRunNodeItemViewModel> Nodes
    {
        get => _nodes;
        private set => SetField(ref _nodes, value);
    }

    public IReadOnlyList<WorkflowRunDeviceOperationItemViewModel> DeviceOperations
    {
        get => _deviceOperations;
        private set => SetField(ref _deviceOperations, value);
    }

    public IReadOnlyList<WorkflowRunTimelineItemViewModel> Timeline
    {
        get => _timeline;
        private set => SetField(ref _timeline, value);
    }

    public WorkflowRunNodeItemViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!SetField(ref _selectedNode, value)) return;
            OnPropertyChanged(nameof(SelectedNodeDeviceOperations));
            if (!_synchronizingSelection && value is not null)
            {
                _synchronizingSelection = true;
                try
                {
                    CanvasViewModel?.SelectNode(value.NodeId);
                }
                finally
                {
                    _synchronizingSelection = false;
                }
            }

            if (value is not null &&
                (_selectedDeviceOperation is null || _selectedDeviceOperation.NodeExecutionId != value.Id))
            {
                SelectedDeviceOperation = value.DeviceOperations.LastOrDefault();
            }
        }
    }

    public IReadOnlyList<WorkflowRunDeviceOperationItemViewModel> SelectedNodeDeviceOperations =>
        SelectedNode?.DeviceOperations ?? [];

    public WorkflowRunDeviceOperationItemViewModel? SelectedDeviceOperation
    {
        get => _selectedDeviceOperation;
        set
        {
            if (!SetField(ref _selectedDeviceOperation, value) || value is null ||
                SelectedNode?.Id == value.NodeExecutionId)
            {
                return;
            }

            SelectedNode = Nodes.FirstOrDefault(node => node.Id == value.NodeExecutionId) ?? SelectedNode;
        }
    }

    public WorkflowRunTimelineItemViewModel? SelectedTimelineEntry
    {
        get => _selectedTimelineEntry;
        set
        {
            SetField(ref _selectedTimelineEntry, value);
            if (value?.NodeExecutionId is not { } nodeExecutionId)
                return;
            SelectedNode = Nodes.FirstOrDefault(node => node.Id == nodeExecutionId) ?? SelectedNode;
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            _refreshCommand.RaiseCanExecuteChanged();
        }
    }

    public DateTimeOffset? RefreshedAt
    {
        get => _refreshedAt;
        private set => SetField(ref _refreshedAt, value);
    }

    public bool HasRun => Run is not null;

    public bool HasUnknownState =>
        Run?.RuntimeStatus == WorkflowRuntimeStatus.Unknown ||
        Nodes.Any(node => node.Status == WorkflowNodeExecutionStatus.Unknown) ||
        DeviceOperations.Any(operation => operation.Status == WorkflowDeviceOperationStatus.Unknown);

    public string RunTitle => Version?.Definition.Name ?? "流程运行监控";

    public string RunIdentity => Run is null
        ? "尚未加载运行"
        : $"{Run.ExecutionId:D}  /  v{Run.Version}";

    public string RunStatusDisplay => Run is null ? "未加载" : DescribeRunStatus(Run.RuntimeStatus);

    public string RunStatusBrush => Run is null ? "#667085" : GetRunStatusBrush(Run.RuntimeStatus);

    public string RunTimeSummary => Run is null
        ? string.Empty
        : $"开始 {Run.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  /  更新 {Run.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

    public string UnknownWarning => HasUnknownState
        ? "结果未知，禁止自动重试。请先核对设备操作和时间线证据。"
        : string.Empty;

    public async Task LoadAsync(Guid workflowRunId, CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("A workflow run id is required.", nameof(workflowRunId));

        RunIdText = workflowRunId.ToString("D");
        IsBusy = true;
        StatusMessage = "正在读取运行记录...";
        try
        {
            var run = await _mes.GetWorkflowExecutionAsync(workflowRunId, cancellationToken);
            if (run is null)
            {
                ClearLoadedRun();
                StatusMessage = $"未找到流程运行 {workflowRunId:D}。";
                return;
            }

            if (run.ExecutionId != workflowRunId)
                throw new InvalidOperationException("MES returned a different workflow run than requested.");

            var versionTask = _mes.GetWorkflowVersionAsync(run.WorkflowId, run.Version, cancellationToken);
            var nodesTask = _mes.GetWorkflowNodeExecutionsAsync(workflowRunId, cancellationToken);
            var operationsTask = _mes.GetWorkflowDeviceOperationsAsync(workflowRunId, cancellationToken);
            var timelineTask = _mes.GetWorkflowRunTimelineAsync(workflowRunId, 200, cancellationToken);
            await Task.WhenAll(versionTask, nodesTask, operationsTask, timelineTask);

            var version = await versionTask ??
                          throw new InvalidOperationException("The workflow run's pinned version was not found.");
            var nodes = await nodesTask;
            var operations = await operationsTask;
            var timeline = await timelineTask;
            ValidateReadModel(run, version, nodes, operations, timeline);
            ApplyReadModel(run, version, nodes, operations, timeline);
            RefreshedAt = DateTimeOffset.Now;
            StatusMessage = $"已读取 {Nodes.Count} 次节点执行、{DeviceOperations.Count} 次设备操作和 {Timeline.Count} 条时间线记录。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefresh() =>
        !IsBusy && Guid.TryParse(RunIdText, out var runId) && runId != Guid.Empty;

    private async Task RefreshFromInputAsync()
    {
        if (!Guid.TryParse(RunIdText, out var workflowRunId) || workflowRunId == Guid.Empty)
        {
            StatusMessage = "请输入有效的流程运行 ID。";
            return;
        }

        try
        {
            await LoadAsync(workflowRunId);
        }
        catch (Exception exception)
        {
            StatusMessage = $"运行记录读取失败：{exception.Message}";
        }
    }

    private void ApplyReadModel(
        WorkflowExecutionSnapshot run,
        WorkflowVersion version,
        IReadOnlyList<WorkflowNodeExecutionSnapshot> nodeSnapshots,
        IReadOnlyList<WorkflowDeviceOperationSnapshot> operationSnapshots,
        IReadOnlyList<WorkflowRunTimelineEntry> timelineEntries)
    {
        var previousRun = _run;
        var selectedNodeExecutionId = SelectedNode?.Id;
        var selectedOperationId = SelectedDeviceOperation?.OperationId;
        var selectedTimelineEntryId = SelectedTimelineEntry?.Id;
        var nodeNames = nodeSnapshots.ToDictionary(node => node.Id, node => node.NodeName);
        var operationRows = operationSnapshots
            .OrderBy(operation => operation.RequestedAt)
            .ThenBy(operation => operation.OperationId)
            .Select(operation => new WorkflowRunDeviceOperationItemViewModel(
                operation,
                nodeNames.GetValueOrDefault(operation.NodeExecutionId, "未知节点")))
            .ToArray();
        var operationsByNode = operationRows
            .GroupBy(operation => operation.NodeExecutionId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<WorkflowRunDeviceOperationItemViewModel>)group.ToArray());
        var nodeRows = nodeSnapshots
            .OrderBy(node => node.CreatedAt)
            .ThenBy(node => node.Attempt)
            .ThenBy(node => node.Id)
            .Select(node => new WorkflowRunNodeItemViewModel(
                node,
                operationsByNode.GetValueOrDefault(node.Id) ?? []))
            .ToArray();
        var timelineRows = timelineEntries
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Id)
            .Select(entry => new WorkflowRunTimelineItemViewModel(entry))
            .ToArray();

        _run = run;
        _version = version;
        Nodes = nodeRows;
        DeviceOperations = operationRows;
        Timeline = timelineRows;
        OnPropertyChanged(nameof(Run));
        OnPropertyChanged(nameof(Version));
        NotifyRunSummaryChanged();

        if (CanvasViewModel is null ||
            previousRun is null ||
            previousRun.ExecutionId != run.ExecutionId ||
            previousRun.WorkflowId != run.WorkflowId ||
            previousRun.Version != run.Version)
        {
            var document = WorkflowGraphContractAdapter.FromContract(version.Definition) with
            {
                PublishedVersion = version.Version
            };
            CanvasViewModel = new WorkflowCanvasSpikeViewModel(document, documentChanged: null)
            {
                CanvasMode = WorkflowCanvasMode.Runtime
            };
        }

        ApplyRuntimeOverlay(CanvasViewModel!, run, nodeSnapshots);
        SelectedNode = nodeRows.FirstOrDefault(node => node.Id == selectedNodeExecutionId) ??
                       SelectDefaultNode(run, nodeRows);
        SelectedDeviceOperation = operationRows.FirstOrDefault(operation => operation.OperationId == selectedOperationId) ??
                                  SelectedNode?.DeviceOperations.LastOrDefault();
        SetField(
            ref _selectedTimelineEntry,
            timelineRows.FirstOrDefault(entry => entry.Id == selectedTimelineEntryId) ?? timelineRows.LastOrDefault(),
            nameof(SelectedTimelineEntry));
    }

    private static WorkflowRunNodeItemViewModel? SelectDefaultNode(
        WorkflowExecutionSnapshot run,
        IReadOnlyList<WorkflowRunNodeItemViewModel> nodes) =>
        (run.CurrentNodeId is { } currentNodeId
            ? nodes.Where(node => node.NodeId == currentNodeId)
                .OrderByDescending(node => node.Attempt)
                .ThenByDescending(node => node.UpdatedAt)
                .FirstOrDefault()
            : null) ??
        nodes.OrderByDescending(node => node.UpdatedAt).FirstOrDefault();

    private static void ApplyRuntimeOverlay(
        WorkflowCanvasSpikeViewModel canvas,
        WorkflowExecutionSnapshot run,
        IReadOnlyList<WorkflowNodeExecutionSnapshot> nodes)
    {
        var latestByNode = nodes
            .GroupBy(node => node.NodeId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(node => node.Attempt)
                    .ThenByDescending(node => node.UpdatedAt)
                    .First());
        foreach (var canvasNode in canvas.Nodes)
        {
            if (latestByNode.TryGetValue(canvasNode.Id, out var execution))
            {
                canvasNode.RuntimeState = ToCanvasRuntimeState(execution.Status);
                continue;
            }

            canvasNode.RuntimeState = canvasNode.NodeTypeId switch
            {
                WorkflowGraphNodeTypeIds.Start when nodes.Count > 0 => "Completed",
                WorkflowGraphNodeTypeIds.End when run.RuntimeStatus == WorkflowRuntimeStatus.Completed => "Completed",
                _ => "NotStarted"
            };
        }
    }

    private void CanvasSelectionChanged(object? sender, Guid? nodeId)
    {
        if (_synchronizingSelection || nodeId is null) return;
        var selected = Nodes
            .Where(node => node.NodeId == nodeId.Value)
            .OrderByDescending(node => node.Attempt)
            .ThenByDescending(node => node.UpdatedAt)
            .FirstOrDefault();
        if (selected is null) return;

        _synchronizingSelection = true;
        try
        {
            SelectedNode = selected;
        }
        finally
        {
            _synchronizingSelection = false;
        }
    }

    private void ClearLoadedRun()
    {
        _run = null;
        _version = null;
        Nodes = [];
        DeviceOperations = [];
        Timeline = [];
        SelectedNode = null;
        SelectedDeviceOperation = null;
        SelectedTimelineEntry = null;
        CanvasViewModel = null;
        RefreshedAt = null;
        OnPropertyChanged(nameof(Run));
        OnPropertyChanged(nameof(Version));
        NotifyRunSummaryChanged();
    }

    private void NotifyRunSummaryChanged()
    {
        OnPropertyChanged(nameof(HasRun));
        OnPropertyChanged(nameof(HasUnknownState));
        OnPropertyChanged(nameof(RunTitle));
        OnPropertyChanged(nameof(RunIdentity));
        OnPropertyChanged(nameof(RunStatusDisplay));
        OnPropertyChanged(nameof(RunStatusBrush));
        OnPropertyChanged(nameof(RunTimeSummary));
        OnPropertyChanged(nameof(UnknownWarning));
    }

    private static void ValidateReadModel(
        WorkflowExecutionSnapshot run,
        WorkflowVersion version,
        IReadOnlyList<WorkflowNodeExecutionSnapshot> nodes,
        IReadOnlyList<WorkflowDeviceOperationSnapshot> operations,
        IReadOnlyList<WorkflowRunTimelineEntry> timeline)
    {
        if (version.WorkflowId != run.WorkflowId || version.Version != run.Version ||
            version.Definition.Id != run.WorkflowId)
        {
            throw new InvalidOperationException("The pinned workflow version does not match the workflow run.");
        }

        if (nodes.Any(node => node.WorkflowRunId != run.ExecutionId ||
                              node.WorkflowId != run.WorkflowId ||
                              node.Version != run.Version))
        {
            throw new InvalidOperationException("MES returned node evidence from a different workflow run or version.");
        }

        var nodeExecutionIds = nodes.Select(node => node.Id).ToHashSet();
        if (operations.Any(operation => operation.WorkflowRunId != run.ExecutionId ||
                                        !nodeExecutionIds.Contains(operation.NodeExecutionId)))
        {
            throw new InvalidOperationException("MES returned device evidence that is not linked to this workflow run.");
        }

        var deviceOperationIds = operations.Select(operation => operation.OperationId).ToHashSet();
        if (timeline.Any(entry =>
                entry.WorkflowRunId != run.ExecutionId ||
                (entry.NodeExecutionId is { } nodeExecutionId && !nodeExecutionIds.Contains(nodeExecutionId)) ||
                (entry.DeviceOperationId is { } deviceOperationId && !deviceOperationIds.Contains(deviceOperationId))))
        {
            throw new InvalidOperationException("MES returned timeline evidence that is not linked to this workflow run.");
        }
    }

    private static string ToCanvasRuntimeState(WorkflowNodeExecutionStatus status) => status switch
    {
        WorkflowNodeExecutionStatus.Succeeded or WorkflowNodeExecutionStatus.Skipped => "Completed",
        WorkflowNodeExecutionStatus.Claimed or WorkflowNodeExecutionStatus.Running => "Running",
        WorkflowNodeExecutionStatus.Failed or WorkflowNodeExecutionStatus.TimedOut or WorkflowNodeExecutionStatus.Cancelled => "Failed",
        WorkflowNodeExecutionStatus.Unknown => "Unknown",
        _ => "Waiting"
    };

    private static string DescribeRunStatus(WorkflowRuntimeStatus status) => status switch
    {
        WorkflowRuntimeStatus.Rejected => "已拒绝",
        WorkflowRuntimeStatus.DryRunCompleted => "试运行完成",
        WorkflowRuntimeStatus.Prepared => "等待执行",
        WorkflowRuntimeStatus.Running => "运行中",
        WorkflowRuntimeStatus.Paused => "已暂停",
        WorkflowRuntimeStatus.Completed => "已完成",
        WorkflowRuntimeStatus.Failed => "失败",
        WorkflowRuntimeStatus.Unknown => "结果未知",
        WorkflowRuntimeStatus.Cancelled => "已取消",
        _ => status.ToString()
    };

    private static string GetRunStatusBrush(WorkflowRuntimeStatus status) => status switch
    {
        WorkflowRuntimeStatus.Completed or WorkflowRuntimeStatus.DryRunCompleted => "#247A3D",
        WorkflowRuntimeStatus.Running => "#0F766E",
        WorkflowRuntimeStatus.Prepared or WorkflowRuntimeStatus.Paused => "#8A5A00",
        WorkflowRuntimeStatus.Unknown => "#A30D5D",
        WorkflowRuntimeStatus.Failed or WorkflowRuntimeStatus.Rejected or WorkflowRuntimeStatus.Cancelled => "#B42318",
        _ => "#667085"
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class WorkflowRunNodeItemViewModel
{
    public WorkflowRunNodeItemViewModel(
        WorkflowNodeExecutionSnapshot snapshot,
        IReadOnlyList<WorkflowRunDeviceOperationItemViewModel> deviceOperations)
    {
        Snapshot = snapshot;
        DeviceOperations = deviceOperations;
        Inputs = WorkflowRunFieldItemViewModel.From(snapshot.Inputs);
        Outputs = WorkflowRunFieldItemViewModel.From(snapshot.Outputs);
    }

    public WorkflowNodeExecutionSnapshot Snapshot { get; }
    public IReadOnlyList<WorkflowRunDeviceOperationItemViewModel> DeviceOperations { get; }
    public IReadOnlyList<WorkflowRunFieldItemViewModel> Inputs { get; }
    public IReadOnlyList<WorkflowRunFieldItemViewModel> Outputs { get; }
    public Guid Id => Snapshot.Id;
    public Guid NodeId => Snapshot.NodeId;
    public string NodeName => Snapshot.NodeName;
    public string NodeTypeId => Snapshot.NodeTypeId;
    public int Attempt => Snapshot.Attempt;
    public WorkflowNodeExecutionStatus Status => Snapshot.Status;
    public string StatusDisplay => DescribeStatus(Snapshot.Status);
    public string StatusBrush => GetStatusBrush(Snapshot.Status);
    public DateTimeOffset? StartedAt => Snapshot.StartedAt;
    public DateTimeOffset? CompletedAt => Snapshot.CompletedAt;
    public DateTimeOffset UpdatedAt => Snapshot.UpdatedAt;
    public string LastError => Snapshot.LastError ?? string.Empty;
    public string InputSummary => WorkflowRunFieldItemViewModel.Summarize(Inputs);
    public string OutputSummary => WorkflowRunFieldItemViewModel.Summarize(Outputs);
    public string DurationDisplay => Snapshot.StartedAt is not { } startedAt
        ? "-"
        : FormatDuration((Snapshot.CompletedAt ?? Snapshot.UpdatedAt) - startedAt);

    private static string DescribeStatus(WorkflowNodeExecutionStatus status) => status switch
    {
        WorkflowNodeExecutionStatus.Pending => "待准备",
        WorkflowNodeExecutionStatus.Ready => "就绪",
        WorkflowNodeExecutionStatus.WaitingForResource => "等待资源",
        WorkflowNodeExecutionStatus.Claimed => "已认领",
        WorkflowNodeExecutionStatus.Running => "运行中",
        WorkflowNodeExecutionStatus.WaitingForSignal => "等待信号",
        WorkflowNodeExecutionStatus.Succeeded => "已完成",
        WorkflowNodeExecutionStatus.Failed => "失败",
        WorkflowNodeExecutionStatus.TimedOut => "超时",
        WorkflowNodeExecutionStatus.Unknown => "结果未知",
        WorkflowNodeExecutionStatus.Blocked => "阻塞",
        WorkflowNodeExecutionStatus.Cancelled => "已取消",
        WorkflowNodeExecutionStatus.Skipped => "已跳过",
        _ => status.ToString()
    };

    private static string GetStatusBrush(WorkflowNodeExecutionStatus status) => status switch
    {
        WorkflowNodeExecutionStatus.Succeeded or WorkflowNodeExecutionStatus.Skipped => "#247A3D",
        WorkflowNodeExecutionStatus.Claimed or WorkflowNodeExecutionStatus.Running => "#0F766E",
        WorkflowNodeExecutionStatus.Unknown => "#A30D5D",
        WorkflowNodeExecutionStatus.Failed or WorkflowNodeExecutionStatus.TimedOut or WorkflowNodeExecutionStatus.Cancelled => "#B42318",
        WorkflowNodeExecutionStatus.Pending or WorkflowNodeExecutionStatus.Ready or
            WorkflowNodeExecutionStatus.WaitingForResource or WorkflowNodeExecutionStatus.WaitingForSignal or
            WorkflowNodeExecutionStatus.Blocked => "#8A5A00",
        _ => "#667085"
    };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) return "-";
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }
}

public sealed class WorkflowRunDeviceOperationItemViewModel
{
    public WorkflowRunDeviceOperationItemViewModel(WorkflowDeviceOperationSnapshot snapshot, string nodeName)
    {
        Snapshot = snapshot;
        NodeName = nodeName;
        RequestFields = WorkflowRunFieldItemViewModel.From(snapshot.RequestSummary);
        ResultFields = WorkflowRunFieldItemViewModel.From(snapshot.ResultSummary);
    }

    public WorkflowDeviceOperationSnapshot Snapshot { get; }
    public string NodeName { get; }
    public IReadOnlyList<WorkflowRunFieldItemViewModel> RequestFields { get; }
    public IReadOnlyList<WorkflowRunFieldItemViewModel> ResultFields { get; }
    public Guid OperationId => Snapshot.OperationId;
    public Guid NodeExecutionId => Snapshot.NodeExecutionId;
    public Guid RequestId => Snapshot.RequestId;
    public string CapabilityId => Snapshot.CapabilityId;
    public string DeviceId => Snapshot.DeviceId ?? "-";
    public string CorrelationId => Snapshot.CorrelationId ?? "-";
    public string IdempotencyKey => Snapshot.IdempotencyKey;
    public int Attempt => Snapshot.Attempt;
    public WorkflowDeviceOperationStatus Status => Snapshot.Status;
    public string StatusDisplay => Snapshot.Status == WorkflowDeviceOperationStatus.Unknown
        ? "结果未知"
        : Snapshot.Status.ToString();
    public string StatusBrush => Snapshot.Status switch
    {
        WorkflowDeviceOperationStatus.Succeeded => "#247A3D",
        WorkflowDeviceOperationStatus.Accepted or WorkflowDeviceOperationStatus.Running => "#0F766E",
        WorkflowDeviceOperationStatus.Prepared => "#8A5A00",
        WorkflowDeviceOperationStatus.Unknown => "#A30D5D",
        WorkflowDeviceOperationStatus.Rejected or WorkflowDeviceOperationStatus.Failed or WorkflowDeviceOperationStatus.Cancelled => "#B42318",
        _ => "#667085"
    };
    public DateTimeOffset RequestedAt => Snapshot.RequestedAt;
    public DateTimeOffset? CompletedAt => Snapshot.CompletedAt;
    public DateTimeOffset? ReconciledAt => Snapshot.ReconciledAt;
    public string LastError => Snapshot.LastError ?? string.Empty;
    public string RequestSummary => WorkflowRunFieldItemViewModel.Summarize(RequestFields);
    public string ResultSummary => WorkflowRunFieldItemViewModel.Summarize(ResultFields);
}

public sealed class WorkflowRunTimelineItemViewModel
{
    public WorkflowRunTimelineItemViewModel(WorkflowRunTimelineEntry entry)
    {
        Entry = entry;
        Details = WorkflowRunFieldItemViewModel.From(entry.Details);
    }

    public WorkflowRunTimelineEntry Entry { get; }
    public IReadOnlyList<WorkflowRunFieldItemViewModel> Details { get; }
    public Guid Id => Entry.Id;
    public Guid? NodeExecutionId => Entry.NodeExecutionId;
    public Guid? DeviceOperationId => Entry.DeviceOperationId;
    public DateTimeOffset OccurredAt => Entry.OccurredAt;
    public string EventType => Entry.EventType;
    public string Outcome => Entry.Outcome;
    public string Actor => Entry.Actor ?? "-";
    public string Code => Entry.Code ?? string.Empty;
    public string Reason => Entry.Reason ?? string.Empty;
    public string CorrelationId => Entry.CorrelationId ?? "-";
    public string DetailSummary => WorkflowRunFieldItemViewModel.Summarize(Details);
}

public sealed record WorkflowRunFieldItemViewModel(string Key, string Value)
{
    public static IReadOnlyList<WorkflowRunFieldItemViewModel> From(
        IReadOnlyDictionary<string, string?> values) =>
        values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => new WorkflowRunFieldItemViewModel(item.Key, item.Value ?? "-"))
            .ToArray();

    public static string Summarize(IReadOnlyList<WorkflowRunFieldItemViewModel> values) =>
        values.Count == 0 ? "-" : string.Join("；", values.Select(item => $"{item.Key}={item.Value}"));
}
