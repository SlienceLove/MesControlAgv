using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// Read-only projection of one pinned workflow graph plus audited run-state
/// controls. These controls never issue a device command.
/// </summary>
public sealed class WorkflowRunMonitorViewModel : INotifyPropertyChanged
{
    private readonly IMesClient _mes;
    private readonly IWorkflowRunControlConfirmation _confirmation;
    private readonly AsyncCommand _refreshCommand;
    private readonly AsyncCommand _checkPermissionsCommand;
    private readonly AsyncCommand _pauseCommand;
    private readonly AsyncCommand _resumeCommand;
    private readonly AsyncCommand _cancelCommand;
    private readonly AsyncCommand _resolveSucceededCommand;
    private readonly AsyncCommand _resolveFailedCommand;
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
    private string _operatorName = Environment.GetEnvironmentVariable("WORKFLOW_OPERATOR") ?? "local-operator";
    private string _controlReason = string.Empty;
    private string? _permissionActor;
    private IReadOnlyList<string> _grantedPermissions = [];
    private string _permissionStatus = "权限尚未校验。";

    public WorkflowRunMonitorViewModel(
        IMesClient mes,
        IWorkflowRunControlConfirmation? confirmation = null)
    {
        _mes = mes ?? throw new ArgumentNullException(nameof(mes));
        _confirmation = confirmation ?? MessageBoxWorkflowRunControlConfirmation.Instance;
        _refreshCommand = new AsyncCommand(RefreshFromInputAsync, CanRefresh);
        _checkPermissionsCommand = new AsyncCommand(CheckPermissionsAsync, CanCheckPermissions);
        _pauseCommand = new AsyncCommand(PauseAsync, () => CanPause);
        _resumeCommand = new AsyncCommand(ResumeAsync, () => CanResume);
        _cancelCommand = new AsyncCommand(CancelAsync, () => CanCancel);
        _resolveSucceededCommand = new AsyncCommand(
            () => ResolveUnknownAsync(WorkflowUnknownResolutionOutcome.ConfirmedSucceeded),
            () => CanResolveUnknown);
        _resolveFailedCommand = new AsyncCommand(
            () => ResolveUnknownAsync(WorkflowUnknownResolutionOutcome.ConfirmedFailed),
            () => CanResolveUnknown);
        RefreshCommand = _refreshCommand;
        CheckPermissionsCommand = _checkPermissionsCommand;
        PauseCommand = _pauseCommand;
        ResumeCommand = _resumeCommand;
        CancelCommand = _cancelCommand;
        ResolveUnknownSucceededCommand = _resolveSucceededCommand;
        ResolveUnknownFailedCommand = _resolveFailedCommand;
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
    public ICommand CheckPermissionsCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ResolveUnknownSucceededCommand { get; }
    public ICommand ResolveUnknownFailedCommand { get; }

    public string OperatorName
    {
        get => _operatorName;
        set
        {
            if (!SetField(ref _operatorName, value ?? string.Empty)) return;
            _permissionActor = null;
            _grantedPermissions = [];
            PermissionStatus = "操作者已更改，请重新校验权限。";
            OnPropertyChanged(nameof(GrantedPermissionsDisplay));
            RaiseControlStateChanged();
        }
    }

    public string ControlReason
    {
        get => _controlReason;
        set
        {
            if (!SetField(ref _controlReason, value ?? string.Empty)) return;
            RaiseControlStateChanged();
        }
    }

    public string PermissionStatus
    {
        get => _permissionStatus;
        private set => SetField(ref _permissionStatus, value);
    }

    public string GrantedPermissionsDisplay => _grantedPermissions.Count == 0
        ? "无运行控制权限"
        : string.Join("  /  ", _grantedPermissions);

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

            RaiseControlStateChanged();
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
            RaiseControlStateChanged();
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

    public bool CanPause => string.IsNullOrEmpty(PauseUnavailableReason);

    public bool CanResume => string.IsNullOrEmpty(ResumeUnavailableReason);

    public bool CanCancel => string.IsNullOrEmpty(CancelUnavailableReason);

    public bool CanResolveUnknown => string.IsNullOrEmpty(UnknownResolutionUnavailableReason);

    public string PauseUnavailableReason => GetControlUnavailableReason(
        WorkflowRunControlPermissions.Pause,
        Run?.RuntimeStatus is WorkflowRuntimeStatus.Prepared or WorkflowRuntimeStatus.Running,
        "仅等待执行或运行中的流程可以暂停。");

    public string ResumeUnavailableReason => GetControlUnavailableReason(
        WorkflowRunControlPermissions.Pause,
        Run?.RuntimeStatus == WorkflowRuntimeStatus.Paused,
        "仅已暂停的流程可以恢复。");

    public string CancelUnavailableReason
    {
        get
        {
            var baseReason = GetControlUnavailableReason(
                WorkflowRunControlPermissions.Cancel,
                Run?.RuntimeStatus is WorkflowRuntimeStatus.Prepared or WorkflowRuntimeStatus.Paused,
                Run?.RuntimeStatus == WorkflowRuntimeStatus.Unknown
                    ? "Unknown 必须先完成独立处置。"
                    : "仅静止的等待执行或已暂停流程可以取消。");
            if (!string.IsNullOrEmpty(baseReason)) return baseReason;
            return HasUnsafeCancellationEvidence
                ? "仍有运行中或 Unknown 的节点/设备证据，不能直接取消。"
                : string.Empty;
        }
    }

    public string UnknownResolutionUnavailableReason
    {
        get
        {
            var baseReason = GetControlUnavailableReason(
                WorkflowRunControlPermissions.ResolveUnknown,
                Run?.RuntimeStatus == WorkflowRuntimeStatus.Unknown,
                "仅结果未知的流程可以进行人工裁决。");
            if (!string.IsNullOrEmpty(baseReason)) return baseReason;
            return SelectedNode?.Status == WorkflowNodeExecutionStatus.Unknown
                ? string.Empty
                : "请选择状态为 Unknown 的节点执行记录。";
        }
    }

    public string UnknownResolutionContext => SelectedNode?.Status == WorkflowNodeExecutionStatus.Unknown
        ? $"待处置节点：{SelectedNode.NodeName} / 尝试 {SelectedNode.Attempt} / {SelectedNode.Id:D}"
        : "请选择 Unknown 节点并核对设备操作与时间线证据。";

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
            await RefreshPermissionsAsync(cancellationToken, reportFailure: false);
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

    private bool CanCheckPermissions() =>
        !IsBusy && !string.IsNullOrWhiteSpace(OperatorName);

    private async Task CheckPermissionsAsync()
    {
        await RefreshPermissionsAsync(CancellationToken.None, reportFailure: true);
    }

    private async Task RefreshPermissionsAsync(
        CancellationToken cancellationToken,
        bool reportFailure)
    {
        if (string.IsNullOrWhiteSpace(OperatorName))
        {
            _permissionActor = null;
            _grantedPermissions = [];
            PermissionStatus = "请输入操作者身份。";
            OnPropertyChanged(nameof(GrantedPermissionsDisplay));
            RaiseControlStateChanged();
            return;
        }

        var actor = OperatorName.Trim();
        try
        {
            var snapshot = await _mes.GetWorkflowRunControlPermissionsAsync(actor, cancellationToken);
            if (!string.Equals(snapshot.Actor, actor, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("MES returned permissions for a different operator.");
            _permissionActor = snapshot.Actor;
            _grantedPermissions = snapshot.Permissions
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            PermissionStatus = _grantedPermissions.Count == 0
                ? $"操作者 {snapshot.Actor} 没有运行控制权限。"
                : $"已由 MES 校验操作者 {snapshot.Actor} 的权限。";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _permissionActor = null;
            _grantedPermissions = [];
            PermissionStatus = reportFailure
                ? $"权限校验失败：{exception.Message}"
                : "MES 权限接口不可用，运行监控保持只读。";
        }

        OnPropertyChanged(nameof(GrantedPermissionsDisplay));
        RaiseControlStateChanged();
    }

    private async Task PauseAsync()
    {
        if (!_confirmation.Confirm(
                "确认暂停流程",
                "暂停只阻止后续节点调度，不会向设备发送暂停命令。当前设备动作仍会继续对账。是否继续？"))
        {
            return;
        }

        await ExecuteControlAsync(
            "暂停",
            (runId, request, cancellationToken) =>
                _mes.PauseWorkflowRunAsync(runId, request, cancellationToken));
    }

    private async Task ResumeAsync()
    {
        if (!_confirmation.Confirm(
                "确认恢复流程",
                "恢复后 MES 将重新允许后续节点声明。是否继续？"))
        {
            return;
        }

        await ExecuteControlAsync(
            "恢复",
            (runId, request, cancellationToken) =>
                _mes.ResumeWorkflowRunAsync(runId, request, cancellationToken));
    }

    private async Task CancelAsync()
    {
        if (!_confirmation.Confirm(
                "确认取消流程",
                "取消会终止当前流程运行，且不会向实体设备发送取消命令。此操作不可撤销。是否继续？"))
        {
            return;
        }

        await ExecuteControlAsync(
            "取消",
            (runId, request, cancellationToken) =>
                _mes.CancelWorkflowRunAsync(runId, request, cancellationToken));
    }

    private async Task ResolveUnknownAsync(WorkflowUnknownResolutionOutcome outcome)
    {
        if (Run is null || SelectedNode is null) return;
        var confirmedSuccess = outcome == WorkflowUnknownResolutionOutcome.ConfirmedSucceeded;
        var title = confirmedSuccess ? "确认现场结果为成功" : "确认失败并终止流程";
        var message = confirmedSuccess
            ? "此操作会把选中的 Unknown 节点记录为现场确认成功，并按已发布流程推进。不会重发设备命令。是否继续？"
            : "此操作会把选中的 Unknown 节点记录为失败并终止流程。不会重发设备命令。是否继续？";
        if (!_confirmation.Confirm(title, message)) return;

        var runId = Run.ExecutionId;
        var actor = OperatorName.Trim();
        var reason = ControlReason.Trim();
        var nodeExecutionId = SelectedNode.Id;
        IsBusy = true;
        try
        {
            var result = await _mes.ResolveWorkflowRunUnknownAsync(
                runId,
                new WorkflowUnknownResolutionRequest
                {
                    RequestId = Guid.NewGuid(),
                    Actor = actor,
                    Reason = reason,
                    NodeExecutionId = nodeExecutionId,
                    Outcome = outcome
                },
                CancellationToken.None);
            ControlReason = string.Empty;
            await LoadAsync(runId);
            StatusMessage = result.IsIdempotentReplay
                ? "Unknown 处置请求已按原结果重放。"
                : confirmedSuccess
                    ? "已记录现场确认成功；未重发设备命令。"
                    : "已记录现场确认失败并终止流程；未重发设备命令。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Unknown 处置失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteControlAsync(
        string actionName,
        Func<Guid, WorkflowRunControlRequest, CancellationToken, Task<WorkflowRunControlResult>> execute)
    {
        if (Run is null) return;
        var runId = Run.ExecutionId;
        var request = new WorkflowRunControlRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = OperatorName.Trim(),
            Reason = ControlReason.Trim()
        };
        IsBusy = true;
        try
        {
            var result = await execute(runId, request, CancellationToken.None);
            ControlReason = string.Empty;
            await LoadAsync(runId);
            StatusMessage = result.IsIdempotentReplay
                ? $"{actionName}请求已按原结果重放。"
                : $"流程已{actionName}，操作者和理由已写入审计。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"流程{actionName}失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
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
        OnPropertyChanged(nameof(UnknownResolutionContext));
        RaiseControlStateChanged();
    }

    private bool HasUnsafeCancellationEvidence =>
        Nodes.Any(node => node.Status is WorkflowNodeExecutionStatus.Claimed or
            WorkflowNodeExecutionStatus.Running or WorkflowNodeExecutionStatus.Unknown) ||
        DeviceOperations.Any(operation => operation.Status is WorkflowDeviceOperationStatus.Accepted or
            WorkflowDeviceOperationStatus.Running or WorkflowDeviceOperationStatus.Unknown);

    private string GetControlUnavailableReason(
        string permission,
        bool allowedState,
        string stateReason)
    {
        if (IsBusy) return "正在处理其他运行请求。";
        if (Run is null) return "请先加载流程运行。";
        if (string.IsNullOrWhiteSpace(OperatorName)) return "请输入操作者身份。";
        if (!string.Equals(_permissionActor, OperatorName.Trim(), StringComparison.OrdinalIgnoreCase))
            return "请先由 MES 校验当前操作者权限。";
        if (!_grantedPermissions.Contains(permission, StringComparer.OrdinalIgnoreCase))
            return $"需要 {permission} 权限。";
        if (string.IsNullOrWhiteSpace(ControlReason)) return "请输入本次操作原因。";
        return allowedState ? string.Empty : stateReason;
    }

    private void RaiseControlStateChanged()
    {
        _checkPermissionsCommand.RaiseCanExecuteChanged();
        _pauseCommand.RaiseCanExecuteChanged();
        _resumeCommand.RaiseCanExecuteChanged();
        _cancelCommand.RaiseCanExecuteChanged();
        _resolveSucceededCommand.RaiseCanExecuteChanged();
        _resolveFailedCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanResolveUnknown));
        OnPropertyChanged(nameof(PauseUnavailableReason));
        OnPropertyChanged(nameof(ResumeUnavailableReason));
        OnPropertyChanged(nameof(CancelUnavailableReason));
        OnPropertyChanged(nameof(UnknownResolutionUnavailableReason));
        OnPropertyChanged(nameof(UnknownResolutionContext));
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
