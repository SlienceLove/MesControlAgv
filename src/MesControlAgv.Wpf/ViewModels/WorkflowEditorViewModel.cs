using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.Workflows;

using ContractWorkflowDefinition = MesControlAgv.Contracts.Workflows.WorkflowDefinition;
using ContractWorkflowNode = MesControlAgv.Contracts.Workflows.WorkflowNode;
using ContractWorkflowNodeType = MesControlAgv.Contracts.Workflows.WorkflowNodeType;
using ContractWorkflowExecutionRequest = MesControlAgv.Contracts.Workflows.WorkflowExecutionRequest;
using ContractWorkflowExecutionResult = MesControlAgv.Contracts.Workflows.WorkflowExecutionResult;
using ContractWorkflowExecutionStatus = MesControlAgv.Contracts.Workflows.WorkflowExecutionStatus;
using ContractWorkflowValidationResult = MesControlAgv.Contracts.Workflows.WorkflowValidationResult;
using ContractWorkflowVersion = MesControlAgv.Contracts.Workflows.WorkflowVersion;
using ContractWorkflowVersionStatus = MesControlAgv.Contracts.Workflows.WorkflowVersionStatus;
using ContractWorkflowPublishStatus = MesControlAgv.Contracts.Workflows.WorkflowPublishStatus;
using ContractWorkflowValidationSeverity = MesControlAgv.Contracts.Workflows.WorkflowValidationSeverity;

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
    private readonly string _actor;
    private readonly ObservableCollection<WorkflowNode> _emptyNodes = [];
    private WorkflowDefinition? _selectedWorkflow;
    private WorkflowNode? _selectedNode;
    private string _message = string.Empty;
    private WorkflowRemoteState _remoteState = WorkflowRemoteState.LocalFallback;
    private ContractWorkflowVersion? _remoteVersion;
    private ContractWorkflowValidationResult? _validationResult;
    private ContractWorkflowExecutionResult? _dryRunResult;
    private bool _isLoading;

    public WorkflowEditorViewModel(WorkflowStore store, IMesClient? mes = null, string? actor = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _mes = mes;
        _actor = string.IsNullOrWhiteSpace(actor) ? Environment.UserName : actor.Trim();
        Workflows = new ObservableCollection<WorkflowDefinition>(_store.Load());

        NewWorkflowCommand = new EditorCommand(CreateWorkflow);
        CopyWorkflowCommand = new EditorCommand(CopyWorkflow, () => SelectedWorkflow is not null);
        DeleteWorkflowCommand = new EditorCommand(DeleteWorkflow, () => SelectedWorkflow is not null);
        SaveCommand = new EditorCommand(Save);
        RefreshRemoteCommand = new AsyncEditorCommand(() => LoadRemoteAsync(), () => _mes is not null && !IsLoading);
        SaveDraftCommand = new AsyncEditorCommand(() => SaveDraftAsync(), () => SelectedWorkflow is not null && !IsLoading);
        ValidateCommand = new AsyncEditorCommand(() => ValidateRemoteAsync(), () => SelectedWorkflow is not null && !IsLoading);
        PublishCommand = new AsyncEditorCommand(() => PublishRemoteAsync(), () => SelectedWorkflow is not null && !IsLoading);
        DryRunCommand = new AsyncEditorCommand(() => ExecuteDryRunAsync(), () => SelectedWorkflow is not null && !IsLoading);
        AddNodeCommand = new EditorCommand(AddNode, () => SelectedWorkflow is not null);
        DeleteNodeCommand = new EditorCommand(DeleteNode, () => SelectedWorkflow is not null && SelectedNode is not null);
        MoveNodeLeftCommand = new EditorCommand(() => MoveNode(-1), CanMoveNodeLeft);
        MoveNodeRightCommand = new EditorCommand(() => MoveNode(1), CanMoveNodeRight);

        SelectedWorkflow = Workflows.FirstOrDefault();
    }

    public WorkflowEditorViewModel(IMesClient mes, WorkflowStore store, string? actor = null)
        : this(store, mes, actor)
    {
    }

    public ObservableCollection<WorkflowDefinition> Workflows { get; }

    public IReadOnlyList<WorkflowNodeTypeOption> NodeTypeOptions { get; } =
    [
        new(WorkflowNodeType.Start, "开始"),
        new(WorkflowNodeType.Move, "AGV 移动"),
        new(WorkflowNodeType.Wait, "等待"),
        new(WorkflowNodeType.Pickup, "取货"),
        new(WorkflowNodeType.Dropoff, "放货"),
        new(WorkflowNodeType.Custom, "自定义"),
        new(WorkflowNodeType.End, "结束")
    ];

    public WorkflowDefinition? SelectedWorkflow
    {
        get => _selectedWorkflow;
        set
        {
            if (ReferenceEquals(_selectedWorkflow, value)) return;
            _selectedWorkflow = value;
            SelectedNode = value?.Nodes.OrderBy(node => node.Order).FirstOrDefault();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Nodes));
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
        WorkflowRemoteState.Loading => "MES workflow request in progress",
        WorkflowRemoteState.DraftSaved => "MES draft saved",
        WorkflowRemoteState.Validated => "MES validation passed",
        WorkflowRemoteState.ValidationFailed => "MES validation failed",
        WorkflowRemoteState.Published => "MES version published",
        WorkflowRemoteState.DryRunAccepted => "Dry-run admitted; no AGV command sent",
        WorkflowRemoteState.DryRunRejected => "Dry-run rejected",
        WorkflowRemoteState.Cancelled => "MES workflow request cancelled; local JSON remains active",
        WorkflowRemoteState.ServiceUnavailable => "MES unavailable; local JSON remains active",
        WorkflowRemoteState.Error => "MES workflow operation failed",
        _ => "Local JSON fallback"
    };

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value) return;
            _isLoading = value;
            OnPropertyChanged();
            RefreshRemoteCommandStates();
        }
    }

    public ContractWorkflowVersion? RemoteVersion
    {
        get => _remoteVersion;
        private set { if (ReferenceEquals(_remoteVersion, value)) return; _remoteVersion = value; OnPropertyChanged(); OnPropertyChanged(nameof(RemoteVersionDescription)); }
    }

    public string RemoteVersionDescription => RemoteVersion is { } version
        ? $"v{version.Version} / {version.Status} / {version.PublishStatus}"
        : "No MES version confirmed";

    public ContractWorkflowValidationResult? ValidationResult
    {
        get => _validationResult;
        private set { _validationResult = value; OnPropertyChanged(); OnPropertyChanged(nameof(ValidationSummary)); }
    }

    public string ValidationSummary => ValidationResult is null
        ? "Not validated"
        : ValidationResult.IsValid
            ? (ValidationResult.HasWarnings ? $"Valid with {ValidationResult.Issues.Count} warning(s)" : "Valid")
            : $"{ValidationResult.Issues.Count(issue => issue.Severity == ContractWorkflowValidationSeverity.Error)} error(s)";

    public ContractWorkflowExecutionResult? DryRunResult
    {
        get => _dryRunResult;
        private set { _dryRunResult = value; OnPropertyChanged(); OnPropertyChanged(nameof(DryRunSummary)); }
    }

    public string DryRunSummary => DryRunResult is null
        ? "Dry-run not executed"
        : DryRunResult.IsAccepted
            ? $"Admitted: {DryRunResult.NextStep?.NodeName ?? "no next step"}"
            : $"Rejected: {DryRunResult.RejectionCode ?? "unknown"}";

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
    public ICommand MoveNodeLeftCommand { get; }
    public ICommand MoveNodeRightCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadRemoteAsync(CancellationToken cancellationToken = default)
    {
        if (_mes is null) return;
        await RunRemoteAsync(async () =>
        {
            var remote = await _mes.GetWorkflowsAsync(cancellationToken);
            foreach (var definition in remote)
            {
                var local = ToWpfDefinition(definition);
                var index = Workflows.ToList().FindIndex(item => item.Id == local.Id);
                if (index >= 0)
                {
                    Workflows[index] = local;
                    if (SelectedWorkflow?.Id == local.Id) SelectedWorkflow = local;
                }
                else Workflows.Add(local);
            }

            if (remote.Count > 0 && (SelectedWorkflow is null || !remote.Any(item => item.Id == SelectedWorkflow.Id)))
            {
                SelectedWorkflow = Workflows.FirstOrDefault(item => item.Id == remote[0].Id)
                    ?? SelectedWorkflow
                    ?? Workflows.FirstOrDefault();
            }

            if (SelectedWorkflow is not null)
            {
                var versions = await _mes.GetWorkflowVersionsAsync(SelectedWorkflow.Id, cancellationToken);
                RemoteVersion = versions.OrderByDescending(item => item.Version).FirstOrDefault();
                ValidationResult = RemoteVersion?.Validation;
            }

            RemoteState = RemoteVersion?.PublishStatus == ContractWorkflowPublishStatus.Published
                ? WorkflowRemoteState.Published
                : RemoteVersion?.Status == ContractWorkflowVersionStatus.Validated
                    ? WorkflowRemoteState.Validated
                    : WorkflowRemoteState.DraftSaved;
            Message = "MES workflows loaded; local JSON remains available as fallback.";
        }, cancellationToken);
    }

    public async Task SaveDraftAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedWorkflow is not { } workflow) return;
        try
        {
            Save();
        }
        catch (Exception exception)
        {
            RemoteState = WorkflowRemoteState.Error;
            Message = $"Local workflow save failed; MES draft was not sent: {exception.Message}";
            return;
        }
        if (_mes is null)
        {
            RemoteState = WorkflowRemoteState.LocalFallback;
            return;
        }

        var definition = ToContractDefinition(workflow);
        await RunRemoteAsync(async () =>
        {
            RemoteVersion = RemoteVersion is { WorkflowId: var id, Version: > 0 } version && id == workflow.Id &&
                version.Status == ContractWorkflowVersionStatus.Draft && version.PublishStatus == ContractWorkflowPublishStatus.NotPublished
                ? await _mes.UpdateWorkflowDraftAsync(workflow.Id, version.Version, definition, _actor, cancellationToken)
                : await _mes.CreateWorkflowDraftAsync(definition, _actor, cancellationToken);
            ValidationResult = RemoteVersion.Validation;
            RemoteState = WorkflowRemoteState.DraftSaved;
            Message = $"Local JSON saved and MES draft v{RemoteVersion.Version} confirmed.";
        }, cancellationToken);
    }

    public async Task ValidateRemoteAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedWorkflow is not { } workflow) return;
        if (_mes is null)
        {
            ValidationResult = new MesControlAgv.Domain.Workflows.WorkflowValidator().Validate(ToContractDefinition(workflow));
            RemoteState = ValidationResult.IsValid ? WorkflowRemoteState.Validated : WorkflowRemoteState.ValidationFailed;
            Message = ValidationResult.IsValid ? "Local validation passed." : "Local validation failed.";
            return;
        }

        await RunRemoteAsync(async () =>
        {
            ValidationResult = RemoteVersion is { WorkflowId: var id, Version: > 0 } version && id == workflow.Id
                ? await _mes.ValidateWorkflowVersionAsync(workflow.Id, version.Version, cancellationToken)
                : await _mes.ValidateWorkflowAsync(ToContractDefinition(workflow), cancellationToken);
            RemoteState = ValidationResult.IsValid ? WorkflowRemoteState.Validated : WorkflowRemoteState.ValidationFailed;
            Message = ValidationResult.IsValid ? "MES validation passed." : "MES validation failed; publish is blocked.";
        }, cancellationToken);
    }

    public async Task PublishRemoteAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedWorkflow is not { } workflow) return;
        if (_mes is null)
        {
            RemoteState = WorkflowRemoteState.ServiceUnavailable;
            Message = "MES is not configured; local JSON remains active and publishing is unavailable.";
            return;
        }
        var version = RemoteVersion;
        // Confirm the editor's current local definition in MES before the
        // validation/publish transition, even when a draft was loaded.
        await SaveDraftAsync(cancellationToken);
        version = RemoteVersion;

        if (RemoteState is WorkflowRemoteState.ServiceUnavailable or
            WorkflowRemoteState.Cancelled or
            WorkflowRemoteState.Error ||
            version is null ||
            version.WorkflowId != workflow.Id)
        {
            // Never publish a stale version when saving the current local
            // definition did not receive a durable MES confirmation.
            return;
        }
        await RunRemoteAsync(async () =>
        {
            ValidationResult = await _mes.ValidateWorkflowVersionAsync(workflow.Id, version.Version, cancellationToken);
            if (!ValidationResult.IsValid)
            {
                RemoteState = WorkflowRemoteState.ValidationFailed;
                Message = "MES validation failed; publish is blocked.";
                return;
            }

            RemoteVersion = await _mes.PublishWorkflowAsync(workflow.Id, version.Version, _actor, cancellationToken);
            ValidationResult = RemoteVersion.Validation ?? ValidationResult;
            RemoteState = WorkflowRemoteState.Published;
            Message = $"MES workflow v{RemoteVersion.Version} published.";
        }, cancellationToken);
    }

    public async Task ExecuteDryRunAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedWorkflow is not { } workflow) return;
        if (_mes is null)
        {
            RemoteState = WorkflowRemoteState.ServiceUnavailable;
            Message = "MES is not configured; dry-run is unavailable and no AGV command was sent.";
            return;
        }
        if (RemoteVersion is not { WorkflowId: var id, Version: > 0 } version || id != workflow.Id ||
            version.Status != ContractWorkflowVersionStatus.Published || version.PublishStatus != ContractWorkflowPublishStatus.Published)
        {
            RemoteState = WorkflowRemoteState.DryRunRejected;
            Message = "Publish a confirmed MES version before dry-run.";
            DryRunResult = new ContractWorkflowExecutionResult
            {
                Status = ContractWorkflowExecutionStatus.Rejected,
                WorkflowId = workflow.Id,
                RejectionCode = "WORKFLOW_VERSION_NOT_PUBLISHED",
                RejectionReason = "No published MES version is confirmed.",
                DryRun = true
            };
            return;
        }

        await RunRemoteAsync(async () =>
        {
            DryRunResult = await _mes.ExecuteWorkflowAsync(new ContractWorkflowExecutionRequest
            {
                WorkflowId = workflow.Id,
                Version = version.Version,
                RequestedBy = _actor,
                DryRun = true
            }, cancellationToken);
            RemoteState = DryRunResult.IsAccepted ? WorkflowRemoteState.DryRunAccepted : WorkflowRemoteState.DryRunRejected;
            Message = DryRunResult.IsAccepted ? "Workflow dry-run admitted; no AGV command was sent." :
                $"Workflow dry-run rejected: {DryRunResult.RejectionCode ?? "unknown"}.";
        }, cancellationToken);
    }

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

    private void AddNode() => AddNodeAt(WorkflowNodeType.Custom, null, null);

    public void AddNodeAt(WorkflowNodeType type, double? x, double? y)
    {
        if (SelectedWorkflow is not { } workflow) return;
        var nextOrder = workflow.Nodes.Count + 1;
        var node = new WorkflowNode
        {
            Type = type,
            Name = DefaultNodeName(type, nextOrder),
            Description = DefaultNodeDescription(type),
            X = x ?? Math.Max(0, workflow.Nodes.Count * 180),
            Y = y ?? 100,
            Order = nextOrder
        };
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
        WorkflowNodeType.End => "完成实验流程",
        _ => "自定义实验步骤"
    };

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

    private async Task RunRemoteAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        if (_mes is null) return;
        IsLoading = true;
        RemoteState = WorkflowRemoteState.Loading;
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RemoteState = WorkflowRemoteState.Cancelled;
            Message = "MES workflow request was cancelled; local JSON remains active.";
        }
        catch (OperationCanceledException exception)
        {
            RemoteState = WorkflowRemoteState.ServiceUnavailable;
            Message = $"MES workflow request timed out: {exception.Message}; local JSON remains active.";
        }
        catch (HttpRequestException exception)
        {
            RemoteState = WorkflowRemoteState.ServiceUnavailable;
            Message = $"MES unavailable: {exception.Message}; local JSON remains active.";
        }
        catch (Exception exception)
        {
            RemoteState = WorkflowRemoteState.Error;
            Message = $"MES workflow operation failed: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static ContractWorkflowDefinition ToContractDefinition(WorkflowDefinition workflow)
    {
        var nodes = workflow.Nodes.OrderBy(node => node.Order).ToList();
        return new ContractWorkflowDefinition
        {
            Id = workflow.Id,
            Name = workflow.Name,
            Description = workflow.Description,
            IsPreset = workflow.IsPreset,
            Nodes = nodes.Select((node, index) => new ContractWorkflowNode
            {
                Id = node.Id,
                Type = (ContractWorkflowNodeType)node.Type,
                Name = node.Name,
                Description = node.Description,
                TargetStation = node.TargetStation,
                X = node.X,
                Y = node.Y,
                Order = node.Order,
                NextNodeIds = index + 1 < nodes.Count ? [nodes[index + 1].Id] : []
            }).ToList()
        };
    }

    private static WorkflowDefinition ToWpfDefinition(ContractWorkflowDefinition definition) => new()
    {
        Id = definition.Id,
        Name = definition.Name,
        Description = definition.Description,
        IsPreset = definition.IsPreset,
        Nodes = new ObservableCollection<WorkflowNode>((definition.Nodes ?? Array.Empty<ContractWorkflowNode>())
            .OrderBy(node => node.Order)
            .Select(node => new WorkflowNode
            {
                Id = node.Id,
                Type = (WorkflowNodeType)node.Type,
                Name = node.Name,
                Description = node.Description,
                TargetStation = node.TargetStation,
                X = node.X,
                Y = node.Y,
                Order = node.Order
            }))
    };

    private void RefreshCommandStates()
    {
        foreach (var command in new[] { CopyWorkflowCommand, DeleteWorkflowCommand, AddNodeCommand, DeleteNodeCommand, MoveNodeLeftCommand, MoveNodeRightCommand }.OfType<EditorCommand>()) command.RaiseCanExecuteChanged();
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
