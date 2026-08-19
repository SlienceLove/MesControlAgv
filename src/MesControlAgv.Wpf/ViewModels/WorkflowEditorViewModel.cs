using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.Workflows;

using ContractWorkflowDefinition = MesControlAgv.Contracts.Workflows.WorkflowDefinition;
using ContractWorkflowGraphDocument = MesControlAgv.Contracts.Workflows.WorkflowGraphDocument;
using ContractWorkflowNode = MesControlAgv.Contracts.Workflows.WorkflowNode;
using ContractWorkflowExecutionRequest = MesControlAgv.Contracts.Workflows.WorkflowExecutionRequest;
using ContractWorkflowExecutionResult = MesControlAgv.Contracts.Workflows.WorkflowExecutionResult;
using ContractWorkflowExecutionSnapshot = MesControlAgv.Contracts.Workflows.WorkflowExecutionSnapshot;
using ContractWorkflowExecutionStatus = MesControlAgv.Contracts.Workflows.WorkflowExecutionStatus;
using ContractWorkflowRuntimeStatus = MesControlAgv.Contracts.Workflows.WorkflowRuntimeStatus;
using ContractWorkflowAuditResponse = MesControlAgv.Contracts.Workflows.WorkflowAuditResponse;
using ContractWorkflowVersion = MesControlAgv.Contracts.Workflows.WorkflowVersion;
using ContractWorkflowParameter = MesControlAgv.Contracts.Workflows.WorkflowParameter;
using ContractWorkflowPublishStatus = MesControlAgv.Contracts.Workflows.WorkflowPublishStatus;
using ContractWorkflowValidationResult = MesControlAgv.Contracts.Workflows.WorkflowValidationResult;
using ContractWorkflowVersionStatus = MesControlAgv.Contracts.Workflows.WorkflowVersionStatus;

namespace MesControlAgv.Wpf.ViewModels;

public enum WorkflowRemoteState
{
    LocalFallback,
    Loading,
    DraftSaved,
    Validated,
    ValidationFailed,
    Published,
    DryRunAccepted,
    DryRunRejected,
    Cancelled,
    ServiceUnavailable,
    Error
}

public sealed class WorkflowEditorViewModel : INotifyPropertyChanged
{
    private readonly WorkflowStore _store;
    private readonly IMesClient? _mes;
    private readonly Func<string> _actorProvider;
    private readonly SemaphoreSlim _remoteGate = new(1, 1);
    private readonly Dictionary<Guid, ContractWorkflowVersion> _remoteVersions = [];
    private readonly ObservableCollection<WorkflowNode> _emptyNodes = [];
    private WorkflowDefinition? _selectedWorkflow;
    private WorkflowNode? _selectedNode;
    private WorkflowNodeParameter? _selectedParameter;
    private string _message = string.Empty;
    private WorkflowRemoteState _remoteState = WorkflowRemoteState.LocalFallback;
    private string _remoteStatus = "仅使用本地数据";
    private bool _isRemoteBusy;
    private ContractWorkflowValidationResult? _lastValidation;
    private ContractWorkflowExecutionResult? _lastExecution;
    private ContractWorkflowExecutionSnapshot? _lastExecutionSnapshot;
    private IReadOnlyList<ContractWorkflowAuditResponse> _lastExecutionAudits = [];
    private bool _profileDefaultsApplied;

    public WorkflowEditorViewModel(
        WorkflowStore store,
        IMesClient? mes = null,
        Func<string>? actorProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _mes = mes;
        _actorProvider = actorProvider ?? (() => "wpf-editor");
        Workflows = new ObservableCollection<WorkflowDefinition>(_store.Load());

        NewWorkflowCommand = new EditorCommand(CreateWorkflow);
        CopyWorkflowCommand = new EditorCommand(CopyWorkflow, () => SelectedWorkflow is not null);
        DeleteWorkflowCommand = new EditorCommand(DeleteWorkflow, () => SelectedWorkflow is not null);
        SaveCommand = new EditorCommand(Save);
        AddNodeCommand = new EditorCommand(AddNode, () => SelectedWorkflow is not null);
        DeleteNodeCommand = new EditorCommand(DeleteNode, () => SelectedWorkflow is not null && SelectedNode is not null);
        AddParameterCommand = new EditorCommand(AddParameter, () => SelectedNode is not null);
        DeleteParameterCommand = new EditorCommand(
            DeleteParameter,
            () => SelectedNode is not null && SelectedParameter is not null);
        MoveNodeLeftCommand = new EditorCommand(() => MoveNode(-1), CanMoveNodeLeft);
        MoveNodeRightCommand = new EditorCommand(() => MoveNode(1), CanMoveNodeRight);
        LoadFromMesCommand = new AsyncCommand(
        () => RunRemoteAsync("加载工作流", () => LoadFromMesCoreAsync(CancellationToken.None), CancellationToken.None),
            CanUseRemote);
        SaveDraftCommand = new AsyncCommand(
        () => RunRemoteAsync("保存草稿", () => SaveDraftCoreAsync(CancellationToken.None), CancellationToken.None),
            CanSaveDraft);
        ValidateCommand = new AsyncCommand(
        () => RunRemoteAsync("校验工作流", () => ValidateCoreAsync(CancellationToken.None), CancellationToken.None),
            CanValidate);
        PublishCommand = new AsyncCommand(
        () => RunRemoteAsync("发布工作流", () => PublishCoreAsync(CancellationToken.None), CancellationToken.None),
            CanPublish);
        DryRunCommand = new AsyncCommand(
        () => RunRemoteAsync("模拟运行工作流", () => DryRunCoreAsync(CancellationToken.None), CancellationToken.None),
            CanDryRun);
        RefreshRemoteCommand = LoadFromMesCommand;

        SelectedWorkflow = Workflows.FirstOrDefault();
    }

    public WorkflowEditorViewModel(WorkflowStore store, IMesClient? mes, string? actor)
        : this(store, mes, () => string.IsNullOrWhiteSpace(actor) ? "wpf-editor" : actor.Trim())
    {
    }

    public WorkflowEditorViewModel(IMesClient mes, WorkflowStore store, string? actor = null)
        : this(store, mes, () => string.IsNullOrWhiteSpace(actor) ? "wpf-editor" : actor.Trim())
    {
    }

    public ObservableCollection<WorkflowDefinition> Workflows { get; }

    public bool ApplyProfileStations(IReadOnlyList<DashboardStation> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);
        if (_profileDefaultsApplied || !_store.LastLoadUsedDefaults)
        {
            return false;
        }

        var enabled = stations
            .Where(station => station.Enabled && !string.IsNullOrWhiteSpace(station.AgvStationId))
            .OrderBy(station => station.Code)
            .ToList();
        if (enabled.Count < 2)
        {
            return false;
        }

        var source = FindPreferredStation(enabled, ["Sample", "Pickup"]) ?? enabled[0];
        var remaining = enabled
            .Where(station => !string.Equals(station.AgvStationId, source.AgvStationId, StringComparison.Ordinal))
            .ToList();
        if (remaining.Count == 0)
        {
            return false;
        }
        var target = FindPreferredStation(remaining, ["Preparation", "Dropoff"]) ?? remaining[^1];
        var defaults = WorkflowStore.CreateDefaultWorkflows(source.AgvStationId, target.AgvStationId);

        Workflows.Clear();
        foreach (var workflow in defaults)
        {
            Workflows.Add(workflow);
        }

        _profileDefaultsApplied = true;
        SelectedWorkflow = Workflows.FirstOrDefault();
        return true;
    }

    public IReadOnlyList<WorkflowNodeTypeOption> NodeTypeOptions { get; } =
    [
        new(WorkflowNodeType.Start, "开始"),
        new(WorkflowNodeType.Move, "AGV 移动"),
        new(WorkflowNodeType.Wait, "等待"),
        new(WorkflowNodeType.Pickup, "取货"),
        new(WorkflowNodeType.Dropoff, "放货"),
        new(WorkflowNodeType.InstrumentOperation, "仪器操作"),
        new(WorkflowNodeType.Custom, "自定义"),
        new(WorkflowNodeType.End, "结束")
    ];

    private static DashboardStation? FindPreferredStation(
        IEnumerable<DashboardStation> stations,
        IReadOnlyList<string> preferredTypes)
    {
        foreach (var type in preferredTypes)
        {
            var station = stations.FirstOrDefault(candidate =>
                string.Equals(candidate.Type, type, StringComparison.OrdinalIgnoreCase));
            if (station is not null)
            {
                return station;
            }
        }

        return null;
    }

    public WorkflowDefinition? SelectedWorkflow
    {
        get => _selectedWorkflow;
        set
        {
            if (ReferenceEquals(_selectedWorkflow, value)) return;
            _selectedWorkflow = value;
            SelectedNode = value?.Nodes.OrderBy(node => node.Order).FirstOrDefault();
            _lastValidation = value is not null && _remoteVersions.TryGetValue(value.Id, out var remote)
                ? remote.Validation
                : null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Nodes));
            OnPropertyChanged(nameof(SelectedGraphDocument));
            OnPropertyChanged(nameof(SelectedRemoteVersion));
            OnPropertyChanged(nameof(RemoteStatus));
            OnPropertyChanged(nameof(ValidationSummary));
            RefreshCommandStates();
        }
    }

    public WorkflowNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (ReferenceEquals(_selectedNode, value)) return;
            _selectedNode = value;
            SelectedParameter = value?.Parameters.FirstOrDefault();
            OnPropertyChanged();
            RefreshCommandStates();
        }
    }

    /// <summary>
    /// Unified graph snapshot consumed by future canvas adapters. WPF bindings
    /// still expose the legacy observable projection during G2 migration, but
    /// persistence and MES boundaries now use this document shape.
    /// </summary>
    public ContractWorkflowGraphDocument? SelectedGraphDocument =>
        SelectedWorkflow is { } workflow
            ? WorkflowDocumentMapper.ToGraph(workflow)
            : null;

    public WorkflowNodeParameter? SelectedParameter
    {
        get => _selectedParameter;
        set
        {
            if (ReferenceEquals(_selectedParameter, value)) return;
            _selectedParameter = value;
            OnPropertyChanged();
            RefreshCommandStates();
        }
    }

    public ObservableCollection<WorkflowNode> Nodes => SelectedWorkflow?.Nodes ?? _emptyNodes;

    public string Message
    {
        get => _message;
        private set
        {
            if (_message == value) return;
            _message = value;
            OnPropertyChanged();
        }
    }

    public WorkflowRemoteState RemoteState
    {
        get => _remoteState;
        private set
        {
            if (_remoteState == value) return;
            _remoteState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RemoteStateDescription));
        }
    }

    public string RemoteStateDescription => RemoteState switch
    {
        WorkflowRemoteState.Loading => "MES 工作流请求进行中",
        WorkflowRemoteState.DraftSaved => "MES 草稿已保存",
        WorkflowRemoteState.Validated => "MES 校验通过",
        WorkflowRemoteState.ValidationFailed => "MES 校验未通过",
        WorkflowRemoteState.Published => "MES 版本已发布",
        WorkflowRemoteState.DryRunAccepted => "模拟运行已受理，未发送 AGV 指令",
        WorkflowRemoteState.DryRunRejected => "模拟运行被拒绝",
        WorkflowRemoteState.Cancelled => "MES 工作流请求已取消，本地 JSON 仍可用",
        WorkflowRemoteState.ServiceUnavailable => "MES 不可用，本地 JSON 仍可用",
        WorkflowRemoteState.Error => "MES 工作流操作失败",
        _ => "仅使用本地 JSON"
    };

    public bool IsLoading => IsRemoteBusy;

    public ContractWorkflowVersion? RemoteVersion => SelectedRemoteVersion;

    public string RemoteVersionDescription => RemoteVersion is { } version
        ? $"v{version.Version} / {version.Status} / {version.PublishStatus}"
        : "尚未确认 MES 版本";

    public ContractWorkflowValidationResult? ValidationResult => LastValidation;

    public ContractWorkflowExecutionResult? DryRunResult => LastExecution;

    /// <summary>Last read-only runtime snapshot returned by MES, if available.</summary>
    public ContractWorkflowExecutionSnapshot? ExecutionSnapshot => _lastExecutionSnapshot;

    public IReadOnlyList<ContractWorkflowAuditResponse> ExecutionAudits => _lastExecutionAudits;

    public string DryRunSummary => DryRunResult is null
        ? "尚未执行模拟运行"
        : DryRunResult.IsAccepted
            ? $"已受理：{DryRunResult.NextStep?.NodeName ?? "无下一步"}"
            : $"已拒绝：{DryRunResult.RejectionCode ?? "未知原因"}";

    public string ExecutionRuntimeSummary => ExecutionSnapshot is null
        ? "运行快照：尚未读取"
        : $"运行快照：{DescribeRuntimeStatus(ExecutionSnapshot.RuntimeStatus)}" +
          $"；当前节点：{ExecutionSnapshot.PendingStepRequest?.NodeName ?? "无"}" +
          $"；尝试：{ExecutionSnapshot.Attempt}";

    public string ExecutionAuditSummary => ExecutionAudits.Count == 0
        ? "运行审计：尚无记录"
        : $"运行审计：{ExecutionAudits.Count} 条；最新：{ExecutionAudits[^1].EventType}";
    public bool IsRemoteAvailable => _mes is not null;

    public bool IsRemoteBusy
    {
        get => _isRemoteBusy;
        private set
        {
            if (_isRemoteBusy == value) return;
            _isRemoteBusy = value;
            OnPropertyChanged();
            RefreshCommandStates();
        }
    }

    public string RemoteStatus
    {
        get => _remoteStatus;
        private set => SetField(ref _remoteStatus, value);
    }

    public ContractWorkflowVersion? SelectedRemoteVersion =>
        SelectedWorkflow is { } workflow && _remoteVersions.TryGetValue(workflow.Id, out var version)
            ? version
            : null;

    public ContractWorkflowValidationResult? LastValidation => _lastValidation;

    public ContractWorkflowExecutionResult? LastExecution => _lastExecution;

    public string ValidationSummary => _lastValidation is null
        ? "尚未校验"
        : _lastValidation.IsValid
            ? (_lastValidation.HasWarnings ? "有效，但有警告" : "有效")
            : $"无效（{_lastValidation.Issues.Count} 个问题）";

    public ICommand NewWorkflowCommand { get; }
    public ICommand CopyWorkflowCommand { get; }
    public ICommand DeleteWorkflowCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RefreshRemoteCommand { get; }
    public ICommand SaveDraftCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand PublishCommand { get; }
    public ICommand DryRunCommand { get; }
    public ICommand AddNodeCommand { get; }
    public ICommand DeleteNodeCommand { get; }
    public ICommand AddParameterCommand { get; }
    public ICommand DeleteParameterCommand { get; }
    public ICommand MoveNodeLeftCommand { get; }
    public ICommand MoveNodeRightCommand { get; }
    public ICommand LoadFromMesCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void CreateWorkflow()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "新实验流程",
            Description = "可编辑的实验流程",
            IsPreset = false,
            Nodes =
            [
                new WorkflowNode { Type = WorkflowNodeType.Start, Name = "开始", Description = "启动实验流程", X = 0, Y = 100, Order = 1 },
                new WorkflowNode { Type = WorkflowNodeType.End, Name = "结束", Description = "实验流程完成", X = 220, Y = 100, Order = 2 }
            ]
        };
        Workflows.Add(workflow);
        SelectedWorkflow = workflow;
        Message = "已新建实验流程。";
    }

    private void CopyWorkflow()
    {
        if (SelectedWorkflow is not { } source) return;
        var copy = source.Clone();
        Workflows.Add(copy);
        SelectedWorkflow = copy;
        Message = "已复制实验流程。";
    }

    private void DeleteWorkflow()
    {
        if (SelectedWorkflow is not { } workflow) return;
        var index = Workflows.IndexOf(workflow);
        Workflows.Remove(workflow);
        SelectedWorkflow = Workflows.ElementAtOrDefault(Math.Clamp(index, 0, Math.Max(Workflows.Count - 1, 0)));
        Message = "已删除实验流程。";
    }

    private void Save()
    {
        _store.Save(Workflows);
        Message = $"已保存到 {_store.FilePath}";
    }

    private bool CanUseRemote() => _mes is not null && !IsRemoteBusy;

    private bool CanSaveDraft() => CanUseRemote() && SelectedWorkflow is not null;

    private bool CanValidate() => CanUseRemote() && SelectedWorkflow is not null;

    private bool CanPublish() =>
        CanUseRemote() &&
        SelectedRemoteVersion is { Status: ContractWorkflowVersionStatus.Draft or ContractWorkflowVersionStatus.Validated } version &&
        version.Validation?.IsValid == true;

    private bool CanDryRun() =>
        CanUseRemote() &&
        SelectedRemoteVersion is { Status: ContractWorkflowVersionStatus.Published, PublishStatus: ContractWorkflowPublishStatus.Published };

    private async Task RunRemoteAsync(string action, Func<Task> operation, CancellationToken cancellationToken)
    {
        if (_mes is null) return;

        if (!await _remoteGate.WaitAsync(0))
        {
            Message = "已有工作流操作正在执行，请稍候。";
            return;
        }

        IsRemoteBusy = true;
        RemoteState = WorkflowRemoteState.Loading;
        RemoteStatus = action + "...";
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RemoteState = WorkflowRemoteState.Cancelled;
            Message = "MES 工作流请求已取消，本地 JSON 仍可用。";
            RemoteStatus = RemoteStateDescription;
        }
        catch (OperationCanceledException exception)
        {
            RemoteState = WorkflowRemoteState.ServiceUnavailable;
            Message = $"MES 工作流请求超时：{exception.Message}；本地 JSON 仍可用。";
            RemoteStatus = RemoteStateDescription;
        }
        catch (HttpRequestException exception)
        {
            RemoteState = WorkflowRemoteState.ServiceUnavailable;
            Message = $"MES 不可用：{exception.Message}；本地 JSON 仍可用。";
            RemoteStatus = RemoteStateDescription;
        }
        catch (Exception exception)
        {
            RemoteState = WorkflowRemoteState.Error;
            Message = $"MES 工作流操作失败：{exception.Message}";
            RemoteStatus = RemoteStateDescription;
        }
        finally
        {
            IsRemoteBusy = false;
            _remoteGate.Release();
            RefreshCommandStates();
        }
    }

    public Task LoadRemoteAsync(CancellationToken cancellationToken = default) =>
        RunRemoteAsync("加载工作流", () => LoadFromMesCoreAsync(cancellationToken), cancellationToken);

    public Task SaveDraftAsync(CancellationToken cancellationToken = default) =>
        RunRemoteAsync("保存草稿", () => SaveDraftCoreAsync(cancellationToken), cancellationToken);

    public Task ValidateRemoteAsync(CancellationToken cancellationToken = default) =>
        RunRemoteAsync("校验工作流", () => ValidateCoreAsync(cancellationToken), cancellationToken);

    public Task PublishRemoteAsync(CancellationToken cancellationToken = default) =>
        RunRemoteAsync("发布工作流", () => PublishWithLifecycleCoreAsync(cancellationToken), cancellationToken);

    public Task ExecuteDryRunAsync(CancellationToken cancellationToken = default) =>
        RunRemoteAsync("模拟运行工作流", () => DryRunCoreAsync(cancellationToken), cancellationToken);

    private async Task LoadFromMesCoreAsync(CancellationToken cancellationToken)
    {
        if (_mes is null) return;

        var definitions = await _mes.GetWorkflowsAsync(cancellationToken);
        var selectedId = SelectedWorkflow?.Id;
        var loadedIds = new HashSet<Guid>();
        foreach (var definition in definitions)
        {
            var local = FromContract(definition);
            var versions = await _mes.GetWorkflowVersionsAsync(definition.Id, cancellationToken);
            var latest = versions.OrderByDescending(version => version.Version).FirstOrDefault();
            if (latest is not null) _remoteVersions[definition.Id] = latest;

            var existing = Workflows.FirstOrDefault(workflow => workflow.Id == local.Id);
            if (existing is null)
            {
                Workflows.Add(local);
            }
            else
            {
                var index = Workflows.IndexOf(existing);
                Workflows[index] = local;
            }

            loadedIds.Add(local.Id);
        }

        if (selectedId is { } id && loadedIds.Contains(id))
        {
            SelectedWorkflow = Workflows.First(workflow => workflow.Id == id);
        }
        else if (loadedIds.Count > 0)
        {
            SelectedWorkflow = Workflows.First(workflow => loadedIds.Contains(workflow.Id));
        }

        UpdateRemotePresentation($"已从 MES 加载 {definitions.Count} 个工作流");
        RemoteState = SelectedRemoteVersion?.PublishStatus == ContractWorkflowPublishStatus.Published
            ? WorkflowRemoteState.Published
            : SelectedRemoteVersion?.Status == ContractWorkflowVersionStatus.Validated
                ? WorkflowRemoteState.Validated
                : WorkflowRemoteState.DraftSaved;
        Message = RemoteStatus;
    }

    private async Task SaveDraftCoreAsync(CancellationToken cancellationToken)
    {
        if (_mes is null || SelectedWorkflow is not { } workflow) return;

        try
        {
            _store.Save(Workflows);
        }
        catch (Exception exception)
        {
            RemoteState = WorkflowRemoteState.Error;
            Message = $"本地工作流保存失败，未向 MES 发送草稿：{exception.Message}";
            return;
        }

        var definition = ToContract(workflow);
        var current = SelectedRemoteVersion;
        ContractWorkflowVersion saved;
        if (current is { Status: ContractWorkflowVersionStatus.Draft, PublishStatus: ContractWorkflowPublishStatus.NotPublished })
        {
            saved = await _mes.UpdateWorkflowDraftAsync(
                workflow.Id,
                current.Version,
                definition,
                Actor,
                cancellationToken);
        }
        else
        {
            saved = await _mes.CreateWorkflowDraftAsync(definition, Actor, cancellationToken);
        }

        SetRemoteVersion(saved);
        RemoteState = WorkflowRemoteState.DraftSaved;
        Message = $"草稿已保存为 v{saved.Version}。";
    }

    private async Task ValidateCoreAsync(CancellationToken cancellationToken)
    {
        if (_mes is null || SelectedWorkflow is not { } workflow) return;

        var current = SelectedRemoteVersion;
        var result = current is null
            ? await _mes.ValidateWorkflowAsync(ToContract(workflow), cancellationToken)
            : await _mes.ValidateWorkflowVersionAsync(workflow.Id, current.Version, cancellationToken);
        _lastValidation = result;
        if (current is not null)
        {
            _remoteVersions[workflow.Id] = current with
            {
                Validation = result,
                Status = current.Status
            };
        }

        UpdateRemotePresentation(result.IsValid ? "校验通过" : "校验未通过");
        RemoteState = result.IsValid ? WorkflowRemoteState.Validated : WorkflowRemoteState.ValidationFailed;
        OnPropertyChanged(nameof(LastValidation));
        OnPropertyChanged(nameof(ValidationSummary));
        Message = ValidationSummary;
    }

    private async Task PublishCoreAsync(CancellationToken cancellationToken)
    {
        if (_mes is null || SelectedWorkflow is not { } workflow || SelectedRemoteVersion is not { } current) return;

        var published = await _mes.PublishWorkflowAsync(
            workflow.Id,
            current.Version,
            Actor,
            cancellationToken);
        SetRemoteVersion(published);
        RemoteState = WorkflowRemoteState.Published;
        Message = $"工作流已发布为 v{published.Version}。";
    }

    private async Task PublishWithLifecycleCoreAsync(CancellationToken cancellationToken)
    {
        await SaveDraftCoreAsync(cancellationToken);
        await ValidateCoreAsync(cancellationToken);
        if (SelectedRemoteVersion?.Validation?.IsValid != true)
        {
            RemoteState = WorkflowRemoteState.ValidationFailed;
            Message = "工作流校验未通过，未向 MES 发送发布请求。";
            return;
        }

        await PublishCoreAsync(cancellationToken);
    }

    private async Task DryRunCoreAsync(CancellationToken cancellationToken)
    {
        if (_mes is null || SelectedWorkflow is not { } workflow) return;

        if (SelectedRemoteVersion is not { PublishStatus: ContractWorkflowPublishStatus.Published } version)
        {
            _lastExecution = new ContractWorkflowExecutionResult
            {
                Status = ContractWorkflowExecutionStatus.Rejected,
                RequestId = Guid.NewGuid(),
                WorkflowId = workflow.Id,
                RejectionCode = "WORKFLOW_VERSION_NOT_PUBLISHED",
                RejectionReason = "模拟运行需要已确认发布的 MES 工作流版本。",
                DryRun = true
            };
            _lastExecutionSnapshot = null;
            _lastExecutionAudits = [];
            RemoteState = WorkflowRemoteState.DryRunRejected;
            OnPropertyChanged(nameof(LastExecution));
            OnPropertyChanged(nameof(DryRunResult));
            OnPropertyChanged(nameof(ExecutionSnapshot));
            OnPropertyChanged(nameof(ExecutionRuntimeSummary));
            OnPropertyChanged(nameof(ExecutionAudits));
            OnPropertyChanged(nameof(ExecutionAuditSummary));
            Message = "模拟运行被拒绝：WORKFLOW_VERSION_NOT_PUBLISHED。";
            RemoteStatus = Message;
            return;
        }

        var result = await _mes.ExecuteWorkflowAsync(
            new ContractWorkflowExecutionRequest
            {
                WorkflowId = workflow.Id,
                Version = version.Version,
                RequestedBy = Actor,
                CorrelationId = $"wpf-dry-run-{Guid.NewGuid():N}",
                DryRun = true
            },
            cancellationToken);
        _lastExecution = result;
        _lastExecutionSnapshot = await ReadExecutionSnapshotAsync(result, cancellationToken);
        _lastExecutionAudits = await ReadExecutionAuditsAsync(result, cancellationToken);
        RemoteState = result.IsAccepted ? WorkflowRemoteState.DryRunAccepted : WorkflowRemoteState.DryRunRejected;
        OnPropertyChanged(nameof(LastExecution));
        OnPropertyChanged(nameof(ExecutionSnapshot));
        OnPropertyChanged(nameof(ExecutionRuntimeSummary));
        OnPropertyChanged(nameof(ExecutionAudits));
        OnPropertyChanged(nameof(ExecutionAuditSummary));
        Message = result.IsAccepted
            ? result.NextStep is null
                ? "模拟运行已受理，工作流已到达终点。"
                : $"模拟运行已受理，下一步：{result.NextStep.NodeName}。"
            : $"模拟运行被拒绝：{result.RejectionCode ?? result.RejectionReason ?? "未知原因"}。";
        RemoteStatus = Message;
    }

    private async Task<ContractWorkflowExecutionSnapshot?> ReadExecutionSnapshotAsync(
        ContractWorkflowExecutionResult result,
        CancellationToken cancellationToken)
    {
        if (_mes is null || !result.IsAccepted || result.ExecutionId == Guid.Empty)
        {
            return null;
        }

        try
        {
            return await _mes.GetWorkflowExecutionAsync(result.ExecutionId, cancellationToken);
        }
        catch (HttpRequestException)
        {
            // A successful admission must remain visible when connected to an
            // older MES that does not yet expose the read-only snapshot route.
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<ContractWorkflowAuditResponse>> ReadExecutionAuditsAsync(
        ContractWorkflowExecutionResult result,
        CancellationToken cancellationToken)
    {
        if (_mes is null || result.WorkflowId == Guid.Empty)
        {
            return [];
        }

        try
        {
            return await _mes.GetWorkflowAuditsAsync(result.WorkflowId, result.Version, 20, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (NotSupportedException)
        {
            return [];
        }
    }

    private static string DescribeRuntimeStatus(ContractWorkflowRuntimeStatus status) => status switch
    {
        ContractWorkflowRuntimeStatus.Rejected => "已拒绝",
        ContractWorkflowRuntimeStatus.DryRunCompleted => "模拟运行完成",
        ContractWorkflowRuntimeStatus.Prepared => "等待编排",
        ContractWorkflowRuntimeStatus.Running => "运行中",
        ContractWorkflowRuntimeStatus.Paused => "已暂停",
        ContractWorkflowRuntimeStatus.Completed => "已完成",
        ContractWorkflowRuntimeStatus.Failed => "失败",
        ContractWorkflowRuntimeStatus.Unknown => "待对账",
        ContractWorkflowRuntimeStatus.Cancelled => "已取消",
        _ => "未知"
    };

    private string Actor
    {
        get
        {
            var actor = _actorProvider();
            return string.IsNullOrWhiteSpace(actor) ? "wpf-editor" : actor.Trim();
        }
    }

    private void SetRemoteVersion(ContractWorkflowVersion version)
    {
        _remoteVersions[version.WorkflowId] = version;
        _lastValidation = version.Validation;
        if (SelectedWorkflow?.Id == version.WorkflowId)
        {
            SelectedWorkflow.PublishedVersion = version.Definition.PublishedVersion;
        }

        OnPropertyChanged(nameof(SelectedRemoteVersion));
        OnPropertyChanged(nameof(LastValidation));
        OnPropertyChanged(nameof(ValidationSummary));
        UpdateRemotePresentation();
        RefreshCommandStates();
    }

    private void UpdateRemotePresentation(string? status = null)
    {
        if (status is not null)
        {
            RemoteStatus = status;
        }
        else if (SelectedRemoteVersion is { } version)
        {
            RemoteStatus = $"MES v{version.Version}：{version.Status}/{version.PublishStatus}";
        }
        else
        {
            RemoteStatus = IsRemoteAvailable ? "暂无 MES 版本" : "仅使用本地数据";
        }

        OnPropertyChanged(nameof(SelectedRemoteVersion));
        OnPropertyChanged(nameof(ValidationSummary));
    }

    private static ContractWorkflowDefinition ToContract(WorkflowDefinition workflow) =>
        WorkflowGraphContractAdapter.ToContract(WorkflowDocumentMapper.ToGraph(workflow));

    private static WorkflowDefinition FromContract(ContractWorkflowDefinition workflow) =>
        WorkflowDocumentMapper.FromContract(workflow);

    private void AddNode() => AddNodeAt(WorkflowNodeType.Custom, null, null);

    public void AddNodeAt(WorkflowNodeType type, double? x, double? y)
    {
        if (SelectedWorkflow is not { } workflow) return;
        var nextOrder = workflow.Nodes.Count + 1;
        var node = new WorkflowNode
        {
            Type = type,
            GraphNodeTypeId = MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.For(
                (MesControlAgv.Contracts.Workflows.WorkflowNodeType)type),
            Name = DefaultNodeName(type, nextOrder),
            Description = DefaultNodeDescription(type),
            X = x ?? Math.Max(0, workflow.Nodes.Count * 180),
            Y = y ?? 100,
            Order = nextOrder
        };
        foreach (var port in WorkflowGraphContractAdapter.CreatePorts(
                     (MesControlAgv.Contracts.Workflows.WorkflowNodeType)type))
        {
            node.Ports.Add(port);
        }
        AddDefaultParameters(node);
        workflow.Nodes.Add(node);
        NormalizeOrders(workflow);
        SelectedNode = node;
        Message = "已添加流程节点。";
        RefreshCommandStates();
    }

    private static string DefaultNodeName(WorkflowNodeType type, int order) => type switch
    {
        WorkflowNodeType.Start => "开始",
        WorkflowNodeType.Move => "AGV 移动",
        WorkflowNodeType.Wait => "等待",
        WorkflowNodeType.Pickup => "确认取货",
        WorkflowNodeType.Dropoff => "确认放货",
        WorkflowNodeType.InstrumentOperation => "仪器操作",
        WorkflowNodeType.End => "结束",
        _ => $"自定义 {order}"
    };

    private static string DefaultNodeDescription(WorkflowNodeType type) => type switch
    {
        WorkflowNodeType.Start => "启动实验流程",
        WorkflowNodeType.Move => "控制 AGV 移动",
        WorkflowNodeType.Wait => "等待指定条件",
        WorkflowNodeType.Pickup => "等待人工确认取货",
        WorkflowNodeType.Dropoff => "等待人工确认放货",
        WorkflowNodeType.InstrumentOperation => "等待仪器协议适配器执行",
        WorkflowNodeType.End => "完成实验流程",
        _ => "自定义实验步骤"
    };

    private static void AddDefaultParameters(WorkflowNode node)
    {
        if (node.Type == WorkflowNodeType.Wait)
        {
            node.Parameters.Add(new WorkflowNodeParameter
            {
                Name = MesControlAgv.Contracts.Workflows.WorkflowRuntimeParameterNames.WaitDurationSeconds,
                Value = "1",
                DataType = "decimal"
            });
        }
        else if (node.Type == WorkflowNodeType.InstrumentOperation)
        {
            node.Parameters.Add(new WorkflowNodeParameter
            {
                Name = MesControlAgv.Contracts.Workflows.WorkflowRuntimeParameterNames.InstrumentId,
                DataType = "string",
                IsRequired = true
            });
            node.Parameters.Add(new WorkflowNodeParameter
            {
                Name = MesControlAgv.Contracts.Workflows.WorkflowRuntimeParameterNames.InstrumentOperation,
                DataType = "string",
                IsRequired = true
            });
        }
    }

    private void AddParameter()
    {
        if (SelectedNode is not { } node) return;
        var parameter = new WorkflowNodeParameter
        {
            Name = $"parameter{node.Parameters.Count + 1}",
            DataType = "string"
        };
        node.Parameters.Add(parameter);
        SelectedParameter = parameter;
        Message = "已添加节点参数。";
    }

    private void DeleteParameter()
    {
        if (SelectedNode is not { } node || SelectedParameter is not { } parameter) return;
        var index = node.Parameters.IndexOf(parameter);
        node.Parameters.Remove(parameter);
        SelectedParameter = node.Parameters.ElementAtOrDefault(
            Math.Clamp(index, 0, Math.Max(node.Parameters.Count - 1, 0)));
        Message = "已删除节点参数。";
    }

    private void DeleteNode()
    {
        if (SelectedWorkflow is not { } workflow || SelectedNode is not { } node) return;
        workflow.Nodes.Remove(node);
        NormalizeOrders(workflow);
        SelectedNode = workflow.Nodes.OrderBy(item => item.Order).ElementAtOrDefault(Math.Max(0, workflow.Nodes.Count - 1));
        Message = "已删除流程节点。";
        RefreshCommandStates();
    }

    private void MoveNode(int direction)
    {
        if (SelectedWorkflow is not { } workflow || SelectedNode is not { } node) return;
        var ordered = workflow.Nodes.OrderBy(item => item.Order).ToList();
        var index = ordered.IndexOf(node);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= ordered.Count) return;

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        workflow.Nodes.Clear();
        foreach (var item in ordered) workflow.Nodes.Add(item);
        NormalizeOrders(workflow);
        Message = direction < 0 ? "节点已左移。" : "节点已右移。";
        RefreshCommandStates();
    }

    private bool CanMoveNodeLeft() => SelectedWorkflow is not null && SelectedNode is not null && SelectedNode.Order > 1;

    private bool CanMoveNodeRight() => SelectedWorkflow is not null && SelectedNode is not null && SelectedNode.Order < (SelectedWorkflow?.Nodes.Count ?? 0);

    private static void NormalizeOrders(WorkflowDefinition workflow)
    {
        var order = 1;
        foreach (var node in workflow.Nodes) node.Order = order++;
    }

    private void RefreshCommandStates()
    {
        foreach (var command in new[]
        {
            CopyWorkflowCommand,
            DeleteWorkflowCommand,
            AddNodeCommand,
            DeleteNodeCommand,
            AddParameterCommand,
            DeleteParameterCommand,
            MoveNodeLeftCommand,
            MoveNodeRightCommand,
            LoadFromMesCommand,
            SaveDraftCommand,
            ValidateCommand,
            PublishCommand,
            DryRunCommand
        }.OfType<EditorCommand>()) command.RaiseCanExecuteChanged();

        foreach (var command in new[]
        {
            LoadFromMesCommand,
            SaveDraftCommand,
            ValidateCommand,
            PublishCommand,
            DryRunCommand
        }.OfType<AsyncCommand>()) command.RaiseCanExecuteChanged();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void RefreshRemoteCommandStates()
    {
        foreach (var command in new[] { RefreshRemoteCommand, SaveDraftCommand, ValidateCommand, PublishCommand, DryRunCommand }.OfType<AsyncEditorCommand>())
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class EditorCommand(Action execute, Func<bool>? canExecute = null) : ICommand
    {
        private readonly Action _execute = execute;
        private readonly Func<bool> _canExecute = canExecute ?? (() => true);

        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => _canExecute();
        public void Execute(object? parameter) => _execute();
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AsyncEditorCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
    {
        private readonly Func<Task> _execute = execute;
        private readonly Func<bool> _canExecute = canExecute ?? (() => true);
        private bool _running;

        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => !_running && _canExecute();
        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            _running = true;
            RaiseCanExecuteChanged();
            try { await _execute(); }
            finally { _running = false; RaiseCanExecuteChanged(); }
        }
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
