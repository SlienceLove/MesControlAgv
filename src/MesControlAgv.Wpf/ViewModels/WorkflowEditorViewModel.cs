using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.Workflows;

using ContractWorkflowDefinition = MesControlAgv.Contracts.Workflows.WorkflowDefinition;
using ContractWorkflowNode = MesControlAgv.Contracts.Workflows.WorkflowNode;
using ContractWorkflowVersion = MesControlAgv.Contracts.Workflows.WorkflowVersion;
using ContractWorkflowExecutionRequest = MesControlAgv.Contracts.Workflows.WorkflowExecutionRequest;
using ContractWorkflowParameter = MesControlAgv.Contracts.Workflows.WorkflowParameter;
using ContractWorkflowPublishStatus = MesControlAgv.Contracts.Workflows.WorkflowPublishStatus;
using ContractWorkflowValidationResult = MesControlAgv.Contracts.Workflows.WorkflowValidationResult;
using ContractWorkflowVersionStatus = MesControlAgv.Contracts.Workflows.WorkflowVersionStatus;

namespace MesControlAgv.Wpf.ViewModels;

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
    private string _message = string.Empty;
    private string _remoteStatus = "Local only";
    private bool _isRemoteBusy;
    private ContractWorkflowValidationResult? _lastValidation;
    private DashboardWorkflowExecution? _lastExecution;

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
        MoveNodeLeftCommand = new EditorCommand(() => MoveNode(-1), CanMoveNodeLeft);
        MoveNodeRightCommand = new EditorCommand(() => MoveNode(1), CanMoveNodeRight);
        LoadFromMesCommand = new AsyncCommand(
            () => RunRemoteAsync("Load workflows", LoadFromMesAsync),
            CanUseRemote);
        SaveDraftCommand = new AsyncCommand(
            () => RunRemoteAsync("Save draft", SaveDraftAsync),
            CanSaveDraft);
        ValidateCommand = new AsyncCommand(
            () => RunRemoteAsync("Validate workflow", ValidateAsync),
            CanValidate);
        PublishCommand = new AsyncCommand(
            () => RunRemoteAsync("Publish workflow", PublishAsync),
            CanPublish);
        DryRunCommand = new AsyncCommand(
            () => RunRemoteAsync("Dry-run workflow", DryRunAsync),
            CanDryRun);

        SelectedWorkflow = Workflows.FirstOrDefault();
    }

    public ObservableCollection<WorkflowDefinition> Workflows { get; }

    /// <summary>
    /// Enabled station profiles returned by MES. Workflow transport nodes must
    /// resolve their target against this catalog before a version can publish.
    /// </summary>
    public ObservableCollection<DashboardStation> AvailableStations { get; } = [];

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
            _lastValidation = value is not null && _remoteVersions.TryGetValue(value.Id, out var remote)
                ? remote.Validation
                : null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Nodes));
            OnPropertyChanged(nameof(SelectedRemoteVersion));
            OnPropertyChanged(nameof(RemoteStatus));
            OnPropertyChanged(nameof(ValidationSummary));
            OnPropertyChanged(nameof(HasValidStationTargets));
            OnPropertyChanged(nameof(StationValidationSummary));
            RefreshCommandStates();
        }
    }

    public WorkflowNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (ReferenceEquals(_selectedNode, value)) return;
            if (_selectedNode is not null) _selectedNode.PropertyChanged -= SelectedNode_PropertyChanged;
            _selectedNode = value;
            if (_selectedNode is not null) _selectedNode.PropertyChanged += SelectedNode_PropertyChanged;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StationValidationSummary));
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

    public DashboardWorkflowExecution? LastExecution => _lastExecution;

    public bool HasValidStationTargets =>
        SelectedWorkflow is not { } workflow ||
        workflow.Nodes
            .Where(node => node.Type is WorkflowNodeType.Move or WorkflowNodeType.Pickup or WorkflowNodeType.Dropoff)
            .All(node => TryResolveStation(node.TargetStation, out _));

    public string StationValidationSummary
    {
        get
        {
            if (SelectedWorkflow is not { } workflow) return "No workflow selected.";
            var transportNodes = workflow.Nodes
                .Where(node => node.Type is WorkflowNodeType.Move or WorkflowNodeType.Pickup or WorkflowNodeType.Dropoff)
                .ToList();
            if (transportNodes.Count == 0) return "No transport station targets.";
            if (AvailableStations.Count == 0) return "MES enabled station catalog is not loaded.";

            var invalid = transportNodes
                .Where(node => !TryResolveStation(node.TargetStation, out _))
                .Select(node => string.IsNullOrWhiteSpace(node.Name) ? node.Id.ToString("N") : node.Name)
                .ToList();
            return invalid.Count == 0
                ? "All transport station targets are valid."
                : $"Invalid station target(s): {string.Join(", ", invalid)}";
        }
    }

    /// <summary>Resolve a station code, name, or AGV station id to an enabled MES profile.</summary>
    public bool TryResolveStation(string? value, out DashboardStation station)
    {
        station = default!;
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        var match = AvailableStations.FirstOrDefault(item =>
            string.Equals(item.AgvStationId, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Name, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Code.ToString(System.Globalization.CultureInfo.InvariantCulture), normalized, StringComparison.Ordinal));
        if (match is null) return false;
        station = match;
        return true;
    }

    public string ValidationSummary => _lastValidation is null
        ? "Not validated"
        : _lastValidation.IsValid
            ? _lastValidation.HasWarnings ? "Valid with warnings" : "Valid"
            : $"Invalid ({_lastValidation.Issues.Count} issue(s))";

    public ICommand NewWorkflowCommand { get; }
    public ICommand CopyWorkflowCommand { get; }
    public ICommand DeleteWorkflowCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand AddNodeCommand { get; }
    public ICommand DeleteNodeCommand { get; }
    public ICommand MoveNodeLeftCommand { get; }
    public ICommand MoveNodeRightCommand { get; }
    public ICommand LoadFromMesCommand { get; }
    public ICommand SaveDraftCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand PublishCommand { get; }
    public ICommand DryRunCommand { get; }

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
        HasValidStationTargets &&
        SelectedRemoteVersion is { Status: ContractWorkflowVersionStatus.Draft or ContractWorkflowVersionStatus.Validated } version &&
        version.Validation?.IsValid == true;

    private bool CanDryRun() =>
        CanUseRemote() &&
        SelectedRemoteVersion is { Status: ContractWorkflowVersionStatus.Published, PublishStatus: ContractWorkflowPublishStatus.Published };

    private async Task RunRemoteAsync(string action, Func<Task> operation)
    {
        if (_mes is null) return;

        if (!await _remoteGate.WaitAsync(0))
        {
            Message = "A workflow action is already running.";
            return;
        }

        IsRemoteBusy = true;
        RemoteStatus = action + "...";
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            Message = exception.Message;
            RemoteStatus = "Remote action failed";
        }
        finally
        {
            IsRemoteBusy = false;
            _remoteGate.Release();
            RefreshCommandStates();
        }
    }

    private async Task LoadFromMesAsync()
    {
        if (_mes is null) return;

        var stations = await _mes.GetStationsAsync(CancellationToken.None);
        AvailableStations.Clear();
        foreach (var station in stations.Where(item => item.Enabled).OrderBy(item => item.Code))
        {
            AvailableStations.Add(station);
        }
        OnPropertyChanged(nameof(StationValidationSummary));

        var definitions = await _mes.GetWorkflowsAsync(CancellationToken.None);
        var selectedId = SelectedWorkflow?.Id;
        var loadedIds = new HashSet<Guid>();
        foreach (var definition in definitions)
        {
            var local = FromContract(definition);
            var versions = await _mes.GetWorkflowVersionsAsync(definition.Id, CancellationToken.None);
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

        UpdateRemotePresentation("Loaded " + definitions.Count + " workflow(s) from MES");
        Message = RemoteStatus;
        RefreshCommandStates();
    }

    private async Task SaveDraftAsync()
    {
        if (_mes is null || SelectedWorkflow is not { } workflow) return;

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
                CancellationToken.None);
        }
        else
        {
            saved = await _mes.CreateWorkflowDraftAsync(definition, Actor, CancellationToken.None);
        }

        SetRemoteVersion(saved);
        _store.Save(Workflows);
        Message = $"Draft saved as v{saved.Version}.";
    }

    private async Task ValidateAsync()
    {
        if (_mes is null || SelectedWorkflow is not { } workflow) return;

        var current = SelectedRemoteVersion;
        var result = current is null
            ? await _mes.ValidateWorkflowAsync(ToContract(workflow), CancellationToken.None)
            : await _mes.ValidateWorkflowVersionAsync(workflow.Id, current.Version, CancellationToken.None);
        _lastValidation = result;
        if (current is not null)
        {
            _remoteVersions[workflow.Id] = current with
            {
                Validation = result,
                Status = result.IsValid ? ContractWorkflowVersionStatus.Validated : ContractWorkflowVersionStatus.Draft
            };
        }

        UpdateRemotePresentation(result.IsValid ? "Validation passed" : "Validation failed");
        OnPropertyChanged(nameof(LastValidation));
        OnPropertyChanged(nameof(ValidationSummary));
        Message = ValidationSummary;
    }

    private async Task PublishAsync()
    {
        if (_mes is null || SelectedWorkflow is not { } workflow || SelectedRemoteVersion is not { } current) return;

        var published = await _mes.PublishWorkflowAsync(
            workflow.Id,
            current.Version,
            Actor,
            CancellationToken.None);
        SetRemoteVersion(published);
        Message = $"Workflow published as v{published.Version}.";
    }

    private async Task DryRunAsync()
    {
        if (_mes is null || SelectedWorkflow is not { } workflow || SelectedRemoteVersion is not { } version) return;

        var result = await _mes.ExecuteWorkflowAsync(
            new ContractWorkflowExecutionRequest
            {
                WorkflowId = workflow.Id,
                Version = version.Version,
                RequestedBy = Actor,
                CorrelationId = $"wpf-dry-run-{Guid.NewGuid():N}",
                DryRun = true
            },
            CancellationToken.None);
        _lastExecution = result;
        OnPropertyChanged(nameof(LastExecution));
        Message = result.IsAccepted
            ? result.NextStep is null
                ? "Dry-run accepted; workflow is terminal."
                : $"Dry-run accepted; next step: {result.NextStep.NodeName}."
            : $"Dry-run rejected: {result.RejectionCode ?? result.RejectionReason ?? "unknown"}.";
        RemoteStatus = Message;
    }

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
            RemoteStatus = $"MES v{version.Version}: {version.Status}/{version.PublishStatus}";
        }
        else
        {
            RemoteStatus = IsRemoteAvailable ? "No MES version" : "Local only";
        }

        OnPropertyChanged(nameof(SelectedRemoteVersion));
        OnPropertyChanged(nameof(ValidationSummary));
    }

    private ContractWorkflowDefinition ToContract(WorkflowDefinition workflow) => new()
    {
        Id = workflow.Id,
        Name = workflow.Name,
        Description = workflow.Description,
        IsPreset = workflow.IsPreset,
        PublishedVersion = workflow.PublishedVersion,
        Nodes = workflow.Nodes
            .OrderBy(node => node.Order)
            .Select(node => new ContractWorkflowNode
            {
                Id = node.Id,
                Type = (MesControlAgv.Contracts.Workflows.WorkflowNodeType)node.Type,
                Name = node.Name,
                Description = node.Description,
                TargetStation = ResolveTargetStation(node.TargetStation),
                X = node.X,
                Y = node.Y,
                Order = node.Order,
                Parameters = node.Parameters.Select(parameter => new ContractWorkflowParameter
                {
                    Name = parameter.Name,
                    Value = parameter.Value,
                    DataType = parameter.DataType,
                    IsRequired = parameter.IsRequired
                }).ToArray(),
                NextNodeIds = node.NextNodeIds.ToArray()
            })
            .ToArray()
    };

    private string? ResolveTargetStation(string? value) =>
        TryResolveStation(value, out var station) ? station.AgvStationId : value?.Trim();

    private static WorkflowDefinition FromContract(ContractWorkflowDefinition workflow)
    {
        var local = new WorkflowDefinition
        {
            Id = workflow.Id,
            Name = workflow.Name,
            Description = workflow.Description,
            IsPreset = workflow.IsPreset,
            PublishedVersion = workflow.PublishedVersion,
            Nodes = new ObservableCollection<WorkflowNode>(workflow.Nodes
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
                    Order = node.Order,
                    Parameters = new ObservableCollection<WorkflowNodeParameter>(node.Parameters.Select(parameter => new WorkflowNodeParameter
                    {
                        Name = parameter.Name,
                        Value = parameter.Value,
                        DataType = parameter.DataType,
                        IsRequired = parameter.IsRequired
                    })),
                    NextNodeIds = new ObservableCollection<Guid>(node.NextNodeIds)
                }))
        };
        return local;
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

    private void RefreshCommandStates()
    {
        foreach (var command in new[]
        {
            CopyWorkflowCommand,
            DeleteWorkflowCommand,
            AddNodeCommand,
            DeleteNodeCommand,
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

    private void SelectedNode_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkflowNode.TargetStation))
        {
            OnPropertyChanged(nameof(HasValidStationTargets));
            OnPropertyChanged(nameof(StationValidationSummary));
            RefreshCommandStates();
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
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
}
