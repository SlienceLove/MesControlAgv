using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// Read-only projection of one pinned workflow graph plus audited run-state
/// controls. These controls never issue a device command.
/// </summary>
public sealed class WorkflowRunMonitorViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan DefaultAutoRefreshInterval = TimeSpan.FromSeconds(3);
    private readonly IMesClient _mes;
    private readonly IWorkflowRunControlConfirmation _confirmation;
    private readonly AsyncCommand _refreshCommand;
    private readonly AsyncCommand _checkPermissionsCommand;
    private readonly AsyncCommand _pauseCommand;
    private readonly AsyncCommand _resumeCommand;
    private readonly AsyncCommand _cancelCommand;
    private readonly AsyncCommand _resolveSucceededCommand;
    private readonly AsyncCommand _resolveFailedCommand;
    private readonly AsyncCommand _createAndAuthorizeFieldMoveCommand;
    private readonly IWorkflowRuntimeAlertPresenter _alertPresenter;
    private readonly bool _physicalRuntime;
    private CancellationTokenSource? _autoRefreshCancellation;
    private Task? _autoRefreshLoop;
    private bool _autoRefreshViewAttached;
    private bool _isAutoRefreshEnabled = true;
    private TimeSpan _autoRefreshInterval = DefaultAutoRefreshInterval;
    private bool _isAutoRefreshRunning;
    private bool _disposed;
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
    private IReadOnlyList<FieldNavigationAcceptanceResponse> _fieldAcceptances = [];
    private FieldNavigationAcceptanceResponse? _selectedFieldAcceptance;
    private string _fieldAgvId = string.Empty;
    private string _fieldSourceStationId = string.Empty;
    private string _fieldSafetyObserverName = string.Empty;
    private string _fieldPermitId = string.Empty;
    private string _fieldPermitMinutes = "30";
    private string _fieldDescription = string.Empty;
    private string _physicalGateStatus = "现场条件尚未读取";
    private string _physicalGateWarning = string.Empty;
    private string? _lastPhysicalGateWarningKey;

    public WorkflowRunMonitorViewModel(
        IMesClient mes,
        IWorkflowRunControlConfirmation? confirmation = null,
        IWorkflowRuntimeAlertPresenter? alertPresenter = null,
        bool physicalRuntime = false)
    {
        _mes = mes ?? throw new ArgumentNullException(nameof(mes));
        _confirmation = confirmation ?? MessageBoxWorkflowRunControlConfirmation.Instance;
        _alertPresenter = alertPresenter ?? MessageBoxWorkflowRuntimeAlertPresenter.Instance;
        _physicalRuntime = physicalRuntime;
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
        _createAndAuthorizeFieldMoveCommand = new AsyncCommand(
            CreateAndAuthorizeFieldMoveAsync,
            () => CanCreateAndAuthorizeFieldMove);
        RefreshCommand = _refreshCommand;
        CheckPermissionsCommand = _checkPermissionsCommand;
        PauseCommand = _pauseCommand;
        ResumeCommand = _resumeCommand;
        CancelCommand = _cancelCommand;
        ResolveUnknownSucceededCommand = _resolveSucceededCommand;
        ResolveUnknownFailedCommand = _resolveFailedCommand;
        CreateAndAuthorizeFieldMoveCommand = _createAndAuthorizeFieldMoveCommand;
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
    public ICommand CreateAndAuthorizeFieldMoveCommand { get; }

    /// <summary>
    /// The view calls this when it is visible. Refreshing is read-only and is
    /// stopped again when the view is unloaded, so a hidden tab cannot retain
    /// a network polling loop.
    /// </summary>
    public void StartAutoRefresh()
    {
        if (_disposed) return;
        _autoRefreshViewAttached = true;
        EnsureAutoRefreshLoop();
        OnPropertyChanged(nameof(AutoRefreshStatusDisplay));
    }

    public void StopAutoRefresh()
    {
        _autoRefreshViewAttached = false;
        CancelAutoRefreshLoop();
        OnPropertyChanged(nameof(AutoRefreshStatusDisplay));
    }

    public bool IsAutoRefreshEnabled
    {
        get => _isAutoRefreshEnabled;
        set
        {
            if (!SetField(ref _isAutoRefreshEnabled, value)) return;
            if (value) EnsureAutoRefreshLoop();
            else CancelAutoRefreshLoop();
            OnPropertyChanged(nameof(AutoRefreshStatusDisplay));
        }
    }

    public TimeSpan AutoRefreshInterval
    {
        get => _autoRefreshInterval;
        set
        {
            var interval = value <= TimeSpan.Zero ? DefaultAutoRefreshInterval : value;
            if (!SetField(ref _autoRefreshInterval, interval)) return;
            if (_autoRefreshLoop is not null)
            {
                CancelAutoRefreshLoop();
                EnsureAutoRefreshLoop();
            }
            OnPropertyChanged(nameof(AutoRefreshStatusDisplay));
        }
    }

    public bool IsAutoRefreshRunning => _isAutoRefreshRunning;

    public string AutoRefreshStatusDisplay
    {
        get
        {
            if (!IsAutoRefreshEnabled) return "自动刷新已关闭";
            if (Run is null) return "加载运行后自动刷新";
            if (Run.IsTerminal) return "流程已终态，自动刷新已暂停";
            return IsAutoRefreshRunning
                ? $"每 {AutoRefreshInterval.TotalSeconds:0.#} 秒自动刷新"
                : "自动刷新待启动";
        }
    }

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

    /// <summary>Latest read-only physical gate summary for the pending Move.</summary>
    public string PhysicalGateStatus
    {
        get => _physicalGateStatus;
        private set => SetField(ref _physicalGateStatus, value);
    }

    public string PhysicalGateWarning
    {
        get => _physicalGateWarning;
        private set
        {
            if (!SetField(ref _physicalGateWarning, value)) return;
            OnPropertyChanged(nameof(HasPhysicalGateWarning));
        }
    }

    public bool HasPhysicalGateWarning => !string.IsNullOrWhiteSpace(PhysicalGateWarning);

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

            SelectedFieldAcceptance = value is null
                ? null
                : FieldAcceptances.LastOrDefault(item => item.WorkflowNodeExecutionId == value.Id);

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

    public IReadOnlyList<FieldNavigationAcceptanceResponse> FieldAcceptances
    {
        get => _fieldAcceptances;
        private set => SetField(ref _fieldAcceptances, value);
    }

    public FieldNavigationAcceptanceResponse? SelectedFieldAcceptance
    {
        get => _selectedFieldAcceptance;
        private set
        {
            if (!SetField(ref _selectedFieldAcceptance, value)) return;
            OnPropertyChanged(nameof(FieldAcceptanceStatus));
            OnPropertyChanged(nameof(HasFieldAcceptance));
            RaiseControlStateChanged();
        }
    }

    public bool HasFieldAcceptance => SelectedFieldAcceptance is not null;

    public string FieldAcceptanceStatus => SelectedFieldAcceptance is null
        ? "尚未创建现场导航验收单"
        : $"{SelectedFieldAcceptance.Status} / {SelectedFieldAcceptance.Id:D}";

    public string FieldSourceStationId
    {
        get => _fieldSourceStationId;
        set
        {
            if (!SetField(ref _fieldSourceStationId, value ?? string.Empty)) return;
            RaiseControlStateChanged();
        }
    }

    public string FieldAgvId
    {
        get => _fieldAgvId;
        set
        {
            if (!SetField(ref _fieldAgvId, value ?? string.Empty)) return;
            RaiseControlStateChanged();
        }
    }

    public string FieldSafetyObserverName
    {
        get => _fieldSafetyObserverName;
        set
        {
            if (!SetField(ref _fieldSafetyObserverName, value ?? string.Empty)) return;
            RaiseControlStateChanged();
        }
    }

    public string FieldPermitId
    {
        get => _fieldPermitId;
        set
        {
            if (!SetField(ref _fieldPermitId, value ?? string.Empty)) return;
            RaiseControlStateChanged();
        }
    }

    public string FieldPermitMinutes
    {
        get => _fieldPermitMinutes;
        set
        {
            if (!SetField(ref _fieldPermitMinutes, value ?? string.Empty)) return;
            RaiseControlStateChanged();
        }
    }

    public string FieldDescription
    {
        get => _fieldDescription;
        set => SetField(ref _fieldDescription, value ?? string.Empty);
    }

    public bool CanCreateAndAuthorizeFieldMove =>
        string.IsNullOrEmpty(FieldMoveUnavailableReason);

    public string FieldMoveUnavailableReason
    {
        get
        {
            if (IsBusy) return "正在处理其他运行请求。";
            if (Run is null || SelectedNode is null) return "请先加载流程并选择 Move 节点。";
            if (SelectedNode.Status != WorkflowNodeExecutionStatus.Ready ||
                !string.Equals(SelectedNode.NodeTypeId, WorkflowGraphNodeTypeIds.Move, StringComparison.OrdinalIgnoreCase))
                return "只能为当前 Ready Move 节点创建验收单。";
            if (!TryGetFieldTargetStation(SelectedNode, out _)) return "Move 节点缺少目标站点输入。";
            if (SelectedFieldAcceptance is not null) return "该 Move 节点已有关联验收单。";
            if (string.IsNullOrWhiteSpace(FieldAgvId)) return "请输入现场确认的 AGV ID。";
            if (string.IsNullOrWhiteSpace(FieldSourceStationId)) return "请输入现场确认的起点站。";
            if (string.IsNullOrWhiteSpace(OperatorName)) return "请输入操作者。";
            if (string.IsNullOrWhiteSpace(FieldSafetyObserverName)) return "请输入安全监护人。";
            if (string.IsNullOrWhiteSpace(FieldPermitId)) return "请输入唯一许可编号。";
            if (!int.TryParse(FieldPermitMinutes, out var minutes) || minutes is < 1 or > 1440)
                return "许可有效期必须为 1–1440 分钟。";
            return string.Empty;
        }
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

    private IReadOnlyList<WorkflowRunNodeItemViewModel> ProgressNodes =>
        Nodes
            .GroupBy(node => node.NodeId)
            .Select(group => group
                .OrderByDescending(node => node.Attempt)
                .ThenByDescending(node => node.UpdatedAt)
                .First())
            .Where(node => Version?.Definition.Nodes.Any(definitionNode =>
                definitionNode.Id == node.NodeId &&
                definitionNode.Type is not (WorkflowNodeType.Start or WorkflowNodeType.End)) ?? true)
            .ToArray();

    private int DefinedExecutableNodeCount => Version?.Definition.Nodes.Count(node =>
        node.Type is not (WorkflowNodeType.Start or WorkflowNodeType.End)) ?? 0;

    public int TotalNodeCount => DefinedExecutableNodeCount > 0
        ? DefinedExecutableNodeCount
        : ProgressNodes.Count;

    public int CompletedNodeCount => ProgressNodes.Count(node => node.Status is
        WorkflowNodeExecutionStatus.Succeeded or WorkflowNodeExecutionStatus.Skipped);

    public int FailedNodeCount => ProgressNodes.Count(node => node.Status is
        WorkflowNodeExecutionStatus.Failed or WorkflowNodeExecutionStatus.TimedOut);

    public int CancelledNodeCount => ProgressNodes.Count(node => node.Status == WorkflowNodeExecutionStatus.Cancelled);

    public int UnknownNodeCount => ProgressNodes.Count(node => node.Status == WorkflowNodeExecutionStatus.Unknown);

    public int TerminalNodeCount => ProgressNodes.Count(node => node.Status is
        WorkflowNodeExecutionStatus.Succeeded or WorkflowNodeExecutionStatus.Skipped or
        WorkflowNodeExecutionStatus.Failed or WorkflowNodeExecutionStatus.TimedOut or
        WorkflowNodeExecutionStatus.Unknown or WorkflowNodeExecutionStatus.Cancelled);

    public double ProgressPercent => TotalNodeCount == 0
        ? 0
        : Math.Round(TerminalNodeCount * 100d / TotalNodeCount, 1);

    public string ProgressDisplay => TotalNodeCount == 0
        ? "暂无节点执行记录"
        : $"已处理 {TerminalNodeCount}/{TotalNodeCount} 个节点（完成 {CompletedNodeCount}，失败 {FailedNodeCount}，取消 {CancelledNodeCount}，未知 {UnknownNodeCount}）";

    public string CurrentNodeDisplay
    {
        get
        {
            if (Run?.CurrentNodeId is not { } currentNodeId) return "无";
            return Nodes
                       .Where(node => node.NodeId == currentNodeId)
                       .OrderByDescending(node => node.Attempt)
                       .ThenByDescending(node => node.UpdatedAt)
                       .Select(node => $"{node.NodeName} / {node.StatusDisplay}")
                       .FirstOrDefault() ?? currentNodeId.ToString("D");
        }
    }

    public bool HasFailureEvidence => !string.IsNullOrWhiteSpace(FailureReasonDisplay);

    public string FailureReasonDisplay
    {
        get
        {
            if (Run is null) return string.Empty;

            var reasons = new List<string>();
            AddReason(reasons, Run.LastError);
            AddReason(reasons, Run.RejectionReason);
            foreach (var node in Nodes.Where(node => node.Status is
                         WorkflowNodeExecutionStatus.Failed or WorkflowNodeExecutionStatus.TimedOut))
                AddReason(reasons, node.LastError);
            foreach (var operation in DeviceOperations.Where(operation => operation.Status is
                         WorkflowDeviceOperationStatus.Rejected or WorkflowDeviceOperationStatus.Failed))
                AddReason(reasons, operation.LastError);
            foreach (var entry in Timeline.Where(entry =>
                         entry.Outcome.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                         entry.Outcome.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                         entry.Outcome.Contains("reject", StringComparison.OrdinalIgnoreCase) ||
                         entry.EventType.Contains("Failed", StringComparison.OrdinalIgnoreCase)))
                AddReason(reasons, entry.Reason);

            if (reasons.Count == 0 &&
                (Run.RuntimeStatus is WorkflowRuntimeStatus.Failed or WorkflowRuntimeStatus.Rejected ||
                 Nodes.Any(node => node.Status is WorkflowNodeExecutionStatus.Failed or WorkflowNodeExecutionStatus.TimedOut) ||
                 DeviceOperations.Any(operation => operation.Status is
                     WorkflowDeviceOperationStatus.Rejected or WorkflowDeviceOperationStatus.Failed)))
            {
                return "MES 未提供失败原因，请核对设备证据和时间线。";
            }

            return string.Join("；", reasons);
        }
    }

    public bool IsCancelled => Run?.RuntimeStatus == WorkflowRuntimeStatus.Cancelled;

    public string CancellationStatusDisplay => IsCancelled
        ? "流程已取消：MES 已停止后续节点调度；已发出的设备操作不会自动撤销，请核对设备证据和时间线。"
        : string.Empty;

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
            var acceptancesTask = _mes.GetWorkflowFieldNavigationAcceptancesAsync(workflowRunId, cancellationToken);
            await Task.WhenAll(versionTask, nodesTask, operationsTask, timelineTask, acceptancesTask);

            var version = await versionTask ??
                          throw new InvalidOperationException("The workflow run's pinned version was not found.");
            var nodes = await nodesTask;
            var operations = await operationsTask;
            var timeline = await timelineTask;
            var acceptances = await acceptancesTask;
            ValidateReadModel(run, version, nodes, operations, timeline);
            ValidateFieldAcceptances(run, nodes, acceptances);
            ApplyReadModel(run, version, nodes, operations, timeline, acceptances);
            await RefreshPhysicalGateAsync(cancellationToken);
            await RefreshPermissionsAsync(cancellationToken, reportFailure: false);
            RefreshedAt = DateTimeOffset.Now;
            StatusMessage = $"已读取 {Nodes.Count} 次节点执行、{DeviceOperations.Count} 次设备操作、{FieldAcceptances.Count} 张现场验收单和 {Timeline.Count} 条时间线记录。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefresh() =>
        !IsBusy && Guid.TryParse(RunIdText, out var runId) && runId != Guid.Empty;

    public async Task RefreshLoadedRunAsync(CancellationToken cancellationToken = default)
    {
        if (Run?.ExecutionId is not { } workflowRunId || workflowRunId == Guid.Empty) return;
        await LoadAsync(workflowRunId, cancellationToken);
    }

    private async Task RefreshPhysicalGateAsync(CancellationToken cancellationToken)
    {
        if (!_physicalRuntime || Run is null || Run.DryRun)
        {
            ClearPhysicalGate();
            return;
        }

        if (Run.IsTerminal)
        {
            if (Run.RuntimeStatus == WorkflowRuntimeStatus.Completed &&
                Run.PhysicalAuthorization is not null)
                await RefreshCompletedPhysicalCleanupAsync(cancellationToken);
            else
                ClearPhysicalGate();
            return;
        }

        if (Run.PhysicalAuthorization is { } authorization &&
            authorization.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            PhysicalGateStatus = "本次现场批量许可已过期，流程不会继续派发";
            UpdatePhysicalGateWarning(
                "physical-authorization-expired",
                $"本次现场批量许可已于 {authorization.ExpiresAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} 过期。系统不会复用或自动延长许可；请由操作员核对当前现场状态后重新处置。",
                "现场批量许可告警");
            return;
        }

        var nodeTypeId = Run.PendingStepRequest?.NodeTypeId;
        if (string.Equals(
                nodeTypeId,
                WorkflowGraphNodeTypeIds.RobotExecuteProgram,
                StringComparison.OrdinalIgnoreCase))
        {
            await RefreshAuboPhysicalGateAsync(cancellationToken);
            return;
        }

        if (!string.Equals(nodeTypeId, WorkflowGraphNodeTypeIds.Move, StringComparison.OrdinalIgnoreCase))
        {
            ClearPhysicalGate();
            return;
        }

        PhysicalAgvPreflightResponse? assessment;
        try
        {
            assessment = await _mes.GetPhysicalPreflightAsync(cancellationToken);
        }
        catch (NotSupportedException)
        {
            PhysicalGateStatus = "当前 MES 未提供现场预检接口";
            PhysicalGateWarning = string.Empty;
            _lastPhysicalGateWarningKey = null;
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException exception)
        {
            UpdatePhysicalGateWarning(
                "preflight-timeout",
                $"AGV 现场预检超时，流程保持暂停并等待只读重试：{exception.Message}",
                "AGV 网络预检告警");
            PhysicalGateStatus = "AGV 现场预检超时";
            return;
        }
        catch (HttpRequestException exception)
        {
            UpdatePhysicalGateWarning(
                "preflight-unreachable",
                $"AGV/Adapter 只读预检不可达，流程保持暂停：{exception.Message}。请检查现场以太网和 Adapter 服务。",
                "AGV 网络预检告警");
            PhysicalGateStatus = "AGV 网络或 Adapter 不可达";
            return;
        }
        catch (Exception exception)
        {
            UpdatePhysicalGateWarning(
                "preflight-read-failed",
                $"现场状态读取失败，流程保持暂停并等待重试：{exception.Message}");
            PhysicalGateStatus = "现场预检读取失败";
            return;
        }

        if (assessment is null)
        {
            UpdatePhysicalGateWarning(
                "preflight-empty",
                "现场预检没有返回有效结果，流程保持暂停并等待重试。");
            PhysicalGateStatus = "现场预检结果为空";
            return;
        }

        var keys = new List<string>();
        var messages = new List<string>();
        var snapshot = assessment.Snapshot;
        var readiness = assessment.Readiness;
        var pendingNodeId = Run.PendingStepRequest?.NodeId;
        var currentMove = pendingNodeId is null
            ? null
            : Nodes.Where(node => node.NodeId == pendingNodeId.Value)
                .OrderByDescending(node => node.Attempt)
                .ThenByDescending(node => node.UpdatedAt)
                .FirstOrDefault();
        var activeMove = currentMove?.Status is WorkflowNodeExecutionStatus.Claimed or
            WorkflowNodeExecutionStatus.Running;

        void Add(string key, string message)
        {
            if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase)) keys.Add(key);
            if (!messages.Contains(message, StringComparer.OrdinalIgnoreCase)) messages.Add(message);
        }

        if (!snapshot.Online) Add("offline", "AGV 离线");
        if (!activeMove && snapshot.CurrentTaskId is not null)
            Add("active-task", "AGV 仍有未结束任务");
        if (!string.IsNullOrWhiteSpace(snapshot.ControlOwner) &&
            !string.Equals(snapshot.ControlOwner, "none", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(snapshot.ControlOwner, "adapter", StringComparison.OrdinalIgnoreCase))
            Add("external-control", $"AGV 控制权被 {snapshot.ControlOwner} 占用");

        if (readiness is null)
        {
            Add("safety-unavailable", "AGV 安全状态暂不可读取");
        }
        else
        {
            if (readiness.Emergency == true) Add("emergency", "AGV 急停有效");
            if (readiness.Blocked == true) Add("blocked", "AGV 被阻挡");
            if (readiness.ManualBlock == true) Add("manual-block", "AGV 手动阻挡有效");
            if (readiness.FatalCount > 0) Add("fatal", $"AGV 有 {readiness.FatalCount} 个致命故障");
            if (readiness.ErrorCount > 0) Add("error", $"AGV 有 {readiness.ErrorCount} 个错误");
            if (readiness.RelocationStatus is not 1)
                Add("relocation", $"AGV 重定位状态未成功（{readiness.RelocationStatus?.ToString() ?? "未知"}）");
            if (readiness.LocalizationConfidence is not { } confidence ||
                !double.IsFinite(confidence) || confidence < 0.9)
                Add("confidence", $"AGV 定位置信度不足（{readiness.LocalizationConfidence?.ToString("0.###") ?? "未知"} < 0.9）");
        }

        if (assessment.MapEvidence is null)
            Add("map-evidence", "AGV 地图证据暂不可读取");

        foreach (var reason in assessment.BlockingReasons)
        {
            if (reason.Equals("automatic_dispatch_disabled", StringComparison.OrdinalIgnoreCase) ||
                reason.Equals("adapter_does_not_hold_control", StringComparison.OrdinalIgnoreCase) ||
                reason.Equals("blocked_status_not_clear", StringComparison.OrdinalIgnoreCase) ||
                reason.Equals("controller_faults_active", StringComparison.OrdinalIgnoreCase) ||
                reason.Equals("agv_has_active_task", StringComparison.OrdinalIgnoreCase) ||
                reason.Equals("localization_confidence_below_threshold", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Add($"preflight:{reason}", DescribePhysicalPreflightReason(reason));
        }

        if (messages.Count == 0)
        {
            PhysicalGateStatus = readiness?.LocalizationConfidence is { } confidence
                ? $"现场条件正常：{snapshot.CurrentStationId ?? "未知站点"}，定位置信度 {confidence:0.###}"
                : "现场条件正常，可继续流程";
            PhysicalGateWarning = string.Empty;
            _lastPhysicalGateWarningKey = null;
            return;
        }

        PhysicalGateStatus = "现场条件未满足，流程暂停等待自动复核";
        UpdatePhysicalGateWarning(
            string.Join("|", keys),
            $"现场条件未满足，流程保持暂停并在限定窗口内自动复核：{string.Join("；", messages)}。条件恢复后将继续，未恢复则保持暂停，不会绕过安全门槛。");
    }

    private async Task RefreshCompletedPhysicalCleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            var assessment = await _mes.GetPhysicalPreflightAsync(cancellationToken);
            var owner = assessment?.Snapshot.ControlOwner;
            if (string.Equals(owner, "adapter", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(owner, "MesControlAgv.Adapter", StringComparison.OrdinalIgnoreCase))
            {
                PhysicalGateStatus = "流程已完成，但 AGV 控制权释放尚未确认";
                UpdatePhysicalGateWarning(
                    "completed:control-release-unconfirmed",
                    "流程节点已全部完成，但 AGV 控制权仍显示由 Adapter 持有。系统不会自动重复发送释放命令，请先读取现场控制权后再开始下一批。",
                    "AGV 控制权清理告警");
                return;
            }

            PhysicalGateStatus = string.Equals(owner, "none", StringComparison.OrdinalIgnoreCase)
                ? "流程已完成，AGV 控制权已释放"
                : $"流程已完成，AGV 当前控制权：{owner ?? "未知"}";
            PhysicalGateWarning = string.Empty;
            _lastPhysicalGateWarningKey = null;
        }
        catch (NotSupportedException)
        {
            PhysicalGateStatus = "流程已完成，当前 MES 不支持控制权复核";
            PhysicalGateWarning = string.Empty;
            _lastPhysicalGateWarningKey = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            PhysicalGateStatus = "流程已完成，AGV 控制权状态未确认";
            UpdatePhysicalGateWarning(
                "completed:control-read-failed",
                $"流程已完成，但无法读取 AGV 控制权状态：{exception.Message}。请先人工复核，系统不会自动重发释放命令。",
                "AGV 控制权清理告警");
        }
    }

    private async Task RefreshAuboPhysicalGateAsync(CancellationToken cancellationToken)
    {
        var pending = Run?.PendingStepRequest;
        var currentNode = pending is null
            ? null
            : Nodes.Where(node => node.NodeId == pending.NodeId)
                .OrderByDescending(node => node.Attempt)
                .ThenByDescending(node => node.UpdatedAt)
                .FirstOrDefault();
        var inputs = currentNode?.Snapshot.Inputs;
        var deviceId = ReadNodeInput(
            pending?.Parameters,
            inputs,
            WorkflowNodeConfigurationKeys.DeviceId) ?? "ARM-01";
        var programName = ReadNodeInput(
            pending?.Parameters,
            inputs,
            WorkflowNodeConfigurationKeys.ProgramName);

        AuboArmProgramStatusResponse? status;
        try
        {
            status = await _mes.GetAuboArmProgramAsync(deviceId, cancellationToken);
        }
        catch (NotSupportedException)
        {
            UpdatePhysicalGateWarning(
                "aubo:status-not-supported",
                "当前 MES 未提供机械臂程序状态接口，流程不会在缺少现场状态时自动启动机械臂。",
                "AUBO 现场条件告警");
            PhysicalGateStatus = "机械臂程序状态接口不可用";
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            UpdatePhysicalGateWarning(
                "aubo:status-read-failed",
                $"机械臂状态读取失败，流程保持等待并仅重试只读状态：{exception.Message}",
                "AUBO 现场条件告警");
            PhysicalGateStatus = "机械臂状态读取失败";
            return;
        }

        if (status is null)
        {
            UpdatePhysicalGateWarning(
                "aubo:status-empty",
                "机械臂没有返回程序状态，流程保持等待，不会发送加载或启动命令。",
                "AUBO 现场条件告警");
            PhysicalGateStatus = "机械臂程序状态为空";
            return;
        }

        var keys = new List<string>();
        var messages = new List<string>();
        var activeProgramNode = currentNode?.Status is WorkflowNodeExecutionStatus.Claimed or
            WorkflowNodeExecutionStatus.Running;

        void Add(string key, string message)
        {
            if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase)) keys.Add(key);
            if (!messages.Contains(message, StringComparer.OrdinalIgnoreCase)) messages.Add(message);
        }

        if (!status.Online) Add("offline", "机械臂离线");
        if (!status.ControlEnabled) Add("control-disabled", "机械臂程序控制未启用");
        if (status.RobotMode != AuboArmMode.Running)
            Add("robot-mode", $"机械臂模式为 {status.RobotMode}，需要 Running");
        if (status.SafetyMode != AuboArmSafetyMode.Normal)
            Add("safety-mode", $"机械臂安全模式为 {status.SafetyMode}，需要 Normal");
        if (status.OperationalMode != AuboArmOperationalMode.Automatic)
            Add("operational-mode", $"机械臂运行模式为 {status.OperationalMode}，需要 Automatic");

        if (!activeProgramNode && status.RuntimeState != AuboArmRuntimeState.Stopped)
        {
            Add(
                "runtime-not-stopped",
                $"机械臂解释器为 {status.RuntimeStatus ?? status.RuntimeState.ToString()}，启动前需要 Stopped");
        }
        else if (activeProgramNode && status.RuntimeState is
                 AuboArmRuntimeState.Unknown or
                 AuboArmRuntimeState.Pausing or
                 AuboArmRuntimeState.Paused or
                 AuboArmRuntimeState.Stopping or
                 AuboArmRuntimeState.Aborting or
                 AuboArmRuntimeState.Retracting)
        {
            Add(
                "runtime-transition",
                $"机械臂程序当前为 {status.RuntimeStatus ?? status.RuntimeState.ToString()}，等待恢复或人工核销");
        }

        if (activeProgramNode && status.RuntimeState == AuboArmRuntimeState.Running &&
            !string.IsNullOrWhiteSpace(programName) &&
            !string.Equals(
                NormalizeProgramName(status.LoadedProgram),
                NormalizeProgramName(programName),
                StringComparison.Ordinal))
        {
            Add(
                "loaded-program-mismatch",
                $"机械臂运行工程为 {status.LoadedProgram ?? "未知"}，当前节点要求 {programName}");
        }

        if (messages.Count == 0)
        {
            PhysicalGateStatus = status.RuntimeState switch
            {
                AuboArmRuntimeState.Running =>
                    $"机械臂正在执行 {status.LoadedProgram ?? programName ?? "当前工程"}",
                AuboArmRuntimeState.Stopped when activeProgramNode =>
                    "机械臂已停止，等待 MES 完成状态确认",
                _ => $"机械臂现场条件正常：{deviceId} / {status.RuntimeState}"
            };
            PhysicalGateWarning = string.Empty;
            _lastPhysicalGateWarningKey = null;
            return;
        }

        PhysicalGateStatus = "机械臂现场条件未满足，流程等待自动复核";
        UpdatePhysicalGateWarning(
            $"aubo:{string.Join("|", keys)}",
            $"机械臂现场条件未满足：{string.Join("；", messages)}。系统只重试状态读取；条件恢复后继续，已发送但结果不明确的加载/启动命令不会自动重发。",
            "AUBO 现场条件告警");
    }

    private static string? ReadNodeInput(
        IReadOnlyDictionary<string, string?>? pendingInputs,
        IReadOnlyDictionary<string, string?>? executionInputs,
        string key)
    {
        if (pendingInputs is not null && pendingInputs.TryGetValue(key, out var pending) &&
            !string.IsNullOrWhiteSpace(pending))
            return pending.Trim();
        if (executionInputs is not null && executionInputs.TryGetValue(key, out var execution) &&
            !string.IsNullOrWhiteSpace(execution))
            return execution.Trim();
        return null;
    }

    private static string? NormalizeProgramName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.EndsWith(".pro", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private void ClearPhysicalGate()
    {
        PhysicalGateStatus = string.Empty;
        PhysicalGateWarning = string.Empty;
        _lastPhysicalGateWarningKey = null;
    }

    private static string DescribePhysicalPreflightReason(string reason) => reason switch
    {
        "controller_map_evidence_unavailable" => "AGV 地图证据不可用",
        "controller_map_name_mismatch" => "控制器地图名称与批准地图不一致",
        "controller_map_version_mismatch" => "控制器地图版本与批准版本不一致",
        "controller_map_md5_mismatch" => "控制器地图 MD5 与批准值不一致",
        "controller_map_station_mismatch" => "控制器站点目录与批准目录不一致",
        "controller_map_route_mismatch" => "控制器路线与批准路线不一致",
        _ => $"现场预检阻断：{reason}"
    };

    private void UpdatePhysicalGateWarning(
        string key,
        string message,
        string title = "AGV 现场条件告警")
    {
        PhysicalGateWarning = message;
        if (string.Equals(_lastPhysicalGateWarningKey, key, StringComparison.Ordinal)) return;

        _lastPhysicalGateWarningKey = key;
        _alertPresenter.ShowWarning(title, message);
    }

    private void EnsureAutoRefreshLoop()
    {
        if (_disposed || !_autoRefreshViewAttached || !IsAutoRefreshEnabled ||
            Run is null || Run.IsTerminal || _autoRefreshLoop is not null)
            return;

        var cancellation = new CancellationTokenSource();
        _autoRefreshCancellation = cancellation;
        _isAutoRefreshRunning = true;
        OnPropertyChanged(nameof(IsAutoRefreshRunning));
        OnPropertyChanged(nameof(AutoRefreshStatusDisplay));
        _autoRefreshLoop = AutoRefreshLoopAsync(cancellation);
    }

    private void CancelAutoRefreshLoop()
    {
        try { _autoRefreshCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        _isAutoRefreshRunning = false;
        OnPropertyChanged(nameof(IsAutoRefreshRunning));
        OnPropertyChanged(nameof(AutoRefreshStatusDisplay));
    }

    private async Task AutoRefreshLoopAsync(CancellationTokenSource cancellation)
    {
        try
        {
            using var timer = new PeriodicTimer(AutoRefreshInterval);
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                if (!IsAutoRefreshEnabled || Run is null || Run.IsTerminal) break;
                if (IsBusy) continue;

                try
                {
                    await RefreshLoadedRunAsync(cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    if (!cancellation.IsCancellationRequested)
                        StatusMessage = $"自动刷新失败：{exception.Message}";
                }

                if (Run?.IsTerminal == true) break;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_autoRefreshCancellation, cancellation))
            {
                _autoRefreshCancellation = null;
                _autoRefreshLoop = null;
                _isAutoRefreshRunning = false;
                OnPropertyChanged(nameof(IsAutoRefreshRunning));
                OnPropertyChanged(nameof(AutoRefreshStatusDisplay));
                if (_autoRefreshViewAttached && IsAutoRefreshEnabled &&
                    Run is not null && !Run.IsTerminal)
                    EnsureAutoRefreshLoop();
            }
        }
    }

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

    private async Task CreateAndAuthorizeFieldMoveAsync()
    {
        if (Run is null || SelectedNode is null ||
            !TryGetFieldTargetStation(SelectedNode, out var targetStation) ||
            !int.TryParse(FieldPermitMinutes, out var permitMinutes))
        {
            return;
        }

        if (!_confirmation.Confirm(
                "创建并授权现场导航验收单",
                $"将为流程节点“{SelectedNode.NodeName}”创建 {FieldSourceStationId.Trim()} → {targetStation.Trim()} 验收单，" +
                "并写入操作者、安全监护人和一次性许可。此操作不会由 WPF 直接派发 AGV；只有现场 worker 明确启用后才会认领节点。是否继续？"))
        {
            return;
        }

        var runId = Run.ExecutionId;
        IsBusy = true;
        FieldNavigationAcceptanceResponse? draft = null;
        try
        {
            draft = await _mes.CreateFieldNavigationAcceptanceAsync(
                new CreateFieldNavigationAcceptanceRequest(
                    FieldAgvId.Trim(),
                    FieldSourceStationId.Trim(),
                    targetStation.Trim(),
                    string.IsNullOrWhiteSpace(FieldDescription) ? null : FieldDescription.Trim())
                {
                    WorkflowRunId = runId,
                    WorkflowNodeExecutionId = SelectedNode.Id
                },
                CancellationToken.None);
            await _mes.AuthorizeFieldNavigationAcceptanceAsync(
                draft.Id,
                new AuthorizeFieldNavigationAcceptanceRequest(
                    OperatorName.Trim(),
                    FieldSafetyObserverName.Trim(),
                    FieldPermitId.Trim(),
                    DateTimeOffset.UtcNow.AddMinutes(permitMinutes)),
                CancellationToken.None);
            await LoadAsync(runId);
            StatusMessage = "现场导航验收单已创建并授权；WPF 未直接发送 AGV 命令。";
        }
        catch (Exception exception)
        {
            StatusMessage = draft is null
                ? $"现场导航验收单创建失败：{exception.Message}"
                : $"验收单 {draft.Id:D} 已创建但授权失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool TryGetFieldTargetStation(
        WorkflowRunNodeItemViewModel node,
        out string targetStation)
    {
        targetStation = string.Empty;
        if ((!node.Snapshot.Inputs.TryGetValue(WorkflowNodeConfigurationKeys.TargetStation, out var value) &&
             !node.Snapshot.Inputs.TryGetValue("targetStation", out value)) ||
            string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        targetStation = value.Trim();
        return true;
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
        IReadOnlyList<WorkflowRunTimelineEntry> timelineEntries,
        IReadOnlyList<FieldNavigationAcceptanceResponse> fieldAcceptances)
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
        FieldAcceptances = fieldAcceptances
            .OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id)
            .ToArray();
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
        SelectedFieldAcceptance = SelectedNode is null
            ? null
            : FieldAcceptances.LastOrDefault(item => item.WorkflowNodeExecutionId == SelectedNode.Id);
        SelectedDeviceOperation = operationRows.FirstOrDefault(operation => operation.OperationId == selectedOperationId) ??
                                  SelectedNode?.DeviceOperations.LastOrDefault();
        SetField(
            ref _selectedTimelineEntry,
            timelineRows.FirstOrDefault(entry => entry.Id == selectedTimelineEntryId) ?? timelineRows.LastOrDefault(),
            nameof(SelectedTimelineEntry));
        EnsureAutoRefreshLoop();
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
        CancelAutoRefreshLoop();
        _run = null;
        _version = null;
        Nodes = [];
        DeviceOperations = [];
        Timeline = [];
        FieldAcceptances = [];
        SelectedNode = null;
        SelectedDeviceOperation = null;
        SelectedTimelineEntry = null;
        SelectedFieldAcceptance = null;
        CanvasViewModel = null;
        RefreshedAt = null;
        PhysicalGateStatus = "现场条件尚未读取";
        PhysicalGateWarning = string.Empty;
        _lastPhysicalGateWarningKey = null;
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
        OnPropertyChanged(nameof(TotalNodeCount));
        OnPropertyChanged(nameof(CompletedNodeCount));
        OnPropertyChanged(nameof(FailedNodeCount));
        OnPropertyChanged(nameof(CancelledNodeCount));
        OnPropertyChanged(nameof(UnknownNodeCount));
        OnPropertyChanged(nameof(TerminalNodeCount));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressDisplay));
        OnPropertyChanged(nameof(CurrentNodeDisplay));
        OnPropertyChanged(nameof(HasFailureEvidence));
        OnPropertyChanged(nameof(FailureReasonDisplay));
        OnPropertyChanged(nameof(IsCancelled));
        OnPropertyChanged(nameof(CancellationStatusDisplay));
        OnPropertyChanged(nameof(AutoRefreshStatusDisplay));
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
        _createAndAuthorizeFieldMoveCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanResolveUnknown));
        OnPropertyChanged(nameof(PauseUnavailableReason));
        OnPropertyChanged(nameof(ResumeUnavailableReason));
        OnPropertyChanged(nameof(CancelUnavailableReason));
        OnPropertyChanged(nameof(UnknownResolutionUnavailableReason));
        OnPropertyChanged(nameof(UnknownResolutionContext));
        OnPropertyChanged(nameof(CanCreateAndAuthorizeFieldMove));
        OnPropertyChanged(nameof(FieldMoveUnavailableReason));
    }

    private static void AddReason(List<string> reasons, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return;
        var normalized = reason.Trim();
        if (!reasons.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            reasons.Add(normalized);
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

    private static void ValidateFieldAcceptances(
        WorkflowExecutionSnapshot run,
        IReadOnlyList<WorkflowNodeExecutionSnapshot> nodes,
        IReadOnlyList<FieldNavigationAcceptanceResponse> acceptances)
    {
        var nodeIds = nodes.Select(node => node.Id).ToHashSet();
        if (acceptances.Any(acceptance =>
                acceptance.WorkflowRunId != run.ExecutionId ||
                acceptance.WorkflowNodeExecutionId is not { } nodeId ||
                !nodeIds.Contains(nodeId)))
        {
            throw new InvalidOperationException(
                "MES returned a field-navigation acceptance that is not linked to this workflow run.");
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _autoRefreshViewAttached = false;
        CancelAutoRefreshLoop();
    }

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
    public string ResultOrErrorSummary => string.IsNullOrWhiteSpace(LastError)
        ? ResultSummary
        : ResultSummary == "-"
            ? $"错误={LastError}"
            : $"{ResultSummary}；错误={LastError}";
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
