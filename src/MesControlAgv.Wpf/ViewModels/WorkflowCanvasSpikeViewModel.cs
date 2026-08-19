using System.Collections.ObjectModel;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Wpf.Infrastructure;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// Isolated G1 canvas Spike. It uses only in-memory workflow documents and
/// does not receive an MES client, gateway, serial port or device command API.
/// </summary>
public sealed class WorkflowCanvasSpikeViewModel : INotifyPropertyChanged
{
    private readonly WorkflowLayeredLayout _layout = new();
    private WorkflowDocumentEditor _editor;
    private WorkflowGraphFragment? _clipboard;
    private WorkflowCanvasMode _canvasMode = WorkflowCanvasMode.Edit;
    private WorkflowCanvasNodeViewModel? _selectedNode;
    private WorkflowCanvasEdgeViewModel? _selectedConnection;
    private WorkflowCanvasIssueViewModel? _selectedIssue;
    private object? _pendingConnection;
    private IList _selectedNodes = new ObservableCollection<WorkflowCanvasNodeViewModel>();
    private string _status = string.Empty;
    private string _lastConnectionMessage = "尚未创建连接。";

    public WorkflowCanvasSpikeViewModel()
    {
        DatasetOptions =
        [
            new WorkflowCanvasDatasetOption("linear", "线性 20 / 19", "20 个节点、19 条边"),
            new WorkflowCanvasDatasetOption("branching", "分支 60 / 90", "成功、失败、超时和条件边"),
            new WorkflowCanvasDatasetOption("large", "压力 200 / 350", "自定义节点、边标签和状态覆盖"),
            new WorkflowCanvasDatasetOption("copy", "复制 5 / 6", "复制粘贴与 ID 重写探针")
        ];
        CanvasModes = Enum.GetValues<WorkflowCanvasMode>();
        Nodes = [];
        Connections = [];
        Issues = [];
        AttachSelectedNodes(_selectedNodes);
        _editor = new WorkflowDocumentEditor(_layout.Arrange(WorkflowSpikeSamples.CreateLinear()));

        LoadDatasetCommand = new RelayCommand<string>(LoadDataset);
        AddNodeCommand = new RelayCommand<string>(AddNode);
        AutoLayoutCommand = new RelayCommand(AutoLayout);
        RoundTripCommand = new RelayCommand(RoundTrip);
        UndoCommand = new RelayCommand(Undo, () => _editor.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => _editor.CanRedo);
        CopyCommand = new RelayCommand(Copy, () => SelectedNodes.Count > 0);
        PasteCommand = new RelayCommand(Paste, () => _clipboard is not null);
        DeleteCommand = new RelayCommand(DeleteSelection, () => SelectedNodes.Count > 0 || SelectedConnection is not null);
        CommitNodeLocationsCommand = new RelayCommand(CommitNodeLocations, () => IsEditing);
        CompleteConnectionCommand = new RelayCommand<object>(CompleteConnection);
        SelectIssueCommand = new RelayCommand<WorkflowCanvasIssueViewModel>(SelectIssue);

        RefreshPresentation("已加载线性 20 / 19 样例；数据仅保存在内存中，不连接 MES 或设备。");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<Guid>? FocusNodeRequested;

    public ObservableCollection<WorkflowCanvasNodeViewModel> Nodes { get; }

    public ObservableCollection<WorkflowCanvasEdgeViewModel> Connections { get; }

    /// <summary>
    /// Nodify owns the concrete selection list and requires a writable IList
    /// binding. The view-model only consumes its node projection.
    /// </summary>
    public IList SelectedNodes
    {
        get => _selectedNodes;
        set
        {
            if (ReferenceEquals(_selectedNodes, value)) return;
            DetachSelectedNodes(_selectedNodes);
            _selectedNodes = value ?? new ObservableCollection<WorkflowCanvasNodeViewModel>();
            AttachSelectedNodes(_selectedNodes);
            OnPropertyChanged();
            OnSelectedNodesChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    public ObservableCollection<WorkflowCanvasIssueViewModel> Issues { get; }

    public IReadOnlyList<WorkflowCanvasDatasetOption> DatasetOptions { get; }

    public IReadOnlyList<WorkflowCanvasMode> CanvasModes { get; }

    public ICommand LoadDatasetCommand { get; }

    public ICommand AddNodeCommand { get; }

    public ICommand AutoLayoutCommand { get; }

    public ICommand RoundTripCommand { get; }

    public ICommand UndoCommand { get; }

    public ICommand RedoCommand { get; }

    public ICommand CopyCommand { get; }

    public ICommand PasteCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand CommitNodeLocationsCommand { get; }

    /// <summary>
    /// Receives a Tuple supplied by Nodify's ConnectionCompletedCommand. The
    /// view-model intentionally accepts object rather than a Nodify type.
    /// </summary>
    public ICommand CompleteConnectionCommand { get; }

    public ICommand SelectIssueCommand { get; }

    public WorkflowCanvasMode CanvasMode
    {
        get => _canvasMode;
        set
        {
            if (!SetField(ref _canvasMode, value)) return;
            RefreshRuntimeOverlay();
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(IsReadOnly));
            RaiseCommandStates();
        }
    }

    public bool IsEditing => CanvasMode == WorkflowCanvasMode.Edit;

    public bool IsReadOnly => !IsEditing;

    public WorkflowCanvasNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        private set => SetField(ref _selectedNode, value);
    }

    public WorkflowCanvasEdgeViewModel? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (!SetField(ref _selectedConnection, value)) return;
            RaiseCommandStates();
        }
    }

    public WorkflowCanvasIssueViewModel? SelectedIssue
    {
        get => _selectedIssue;
        set
        {
            if (!SetField(ref _selectedIssue, value) || value is null) return;
            FocusNodeRequested?.Invoke(this, value.NodeId);
        }
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string LastConnectionMessage
    {
        get => _lastConnectionMessage;
        private set => SetField(ref _lastConnectionMessage, value);
    }

    public int NodeCount => Nodes.Count;

    public int EdgeCount => Connections.Count;

    public object? PendingConnection
    {
        get => _pendingConnection;
        set => SetField(ref _pendingConnection, value);
    }

    public void SelectNode(Guid nodeId)
    {
        var node = Nodes.FirstOrDefault(candidate => candidate.Id == nodeId);
        if (node is null) return;
        SelectedNodes.Clear();
        SelectedNodes.Add(node);
    }

    public bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (modifiers == ModifierKeys.Control && key == Key.Z && _editor.CanUndo)
        {
            Undo();
            return true;
        }

        if (modifiers == ModifierKeys.Control && key == Key.Y && _editor.CanRedo)
        {
            Redo();
            return true;
        }

        if (modifiers == ModifierKeys.Control && key == Key.C && SelectedNodes.Count > 0)
        {
            Copy();
            return true;
        }

        if (modifiers == ModifierKeys.Control && key == Key.V && _clipboard is not null)
        {
            Paste();
            return true;
        }

        if (modifiers == ModifierKeys.None && key == Key.Delete && (SelectedNodes.Count > 0 || SelectedConnection is not null))
        {
            DeleteSelection();
            return true;
        }

        return false;
    }

    private void LoadDataset(string? key)
    {
        var document = key switch
        {
            "branching" => WorkflowSpikeSamples.CreateBranching(),
            "large" => WorkflowSpikeSamples.CreateLarge(),
            "copy" => WorkflowSpikeSamples.CreateCopyProbe(),
            _ => WorkflowSpikeSamples.CreateLinear()
        };
        _editor = new WorkflowDocumentEditor(_layout.Arrange(document));
        _clipboard = null;
        RefreshPresentation($"已加载 {document.Name}：{document.Nodes.Count} 个节点，{document.Edges.Count} 条边；不连接 MES 或设备。");
    }

    private void AddNode(string? nodeTypeId)
    {
        if (!IsEditing || string.IsNullOrWhiteSpace(nodeTypeId)) return;
        var number = _editor.Current.Nodes.Count + 1;
        var node = CreatePaletteNode(nodeTypeId, number);
        if (_editor.TryAddNode(node, new WorkflowNodeLayout
        {
            NodeId = node.Id,
            X = 100 + (number % 5) * 60,
            Y = 100 + (number % 4) * 50
        }))
        {
            RefreshPresentation($"已添加 {node.Name}。", [node.Id]);
        }
    }

    private void AutoLayout()
    {
        if (!_editor.TryApplyLayout(document => _layout.Arrange(document))) return;
        RefreshPresentation("已应用可替换的分层布局服务。");
    }

    private void RoundTrip()
    {
        var selectedNodeIds = SelectedNodes.OfType<WorkflowCanvasNodeViewModel>().Select(node => node.Id).ToArray();
        var restored = WorkflowDocumentEditor.Deserialize(_editor.Serialize());
        _editor = new WorkflowDocumentEditor(restored);
        RefreshPresentation("已完成内存 JSON 往返，未写入本地或 MES；编辑历史已重新开始。", selectedNodeIds);
    }

    private void Undo()
    {
        if (!_editor.Undo()) return;
        RefreshPresentation("已撤销最后一次编辑。");
    }

    private void Redo()
    {
        if (!_editor.Redo()) return;
        RefreshPresentation("已重做最后一次编辑。");
    }

    private void Copy()
    {
        _clipboard = _editor.Copy(SelectedNodes.OfType<WorkflowCanvasNodeViewModel>().Select(node => node.Id));
        Status = $"已复制 {_clipboard.Nodes.Count} 个节点和 {_clipboard.Edges.Count} 条内部边。";
        RaiseCommandStates();
    }

    private void Paste()
    {
        if (!IsEditing || _clipboard is null) return;
        var pasted = _editor.Paste(_clipboard);
        RefreshPresentation($"已粘贴 {pasted.Count} 个节点；所有节点和边 ID 已重写。", pasted);
    }

    private void DeleteSelection()
    {
        if (!IsEditing) return;
        if (SelectedConnection is not null)
        {
            var edgeId = SelectedConnection.Id;
            if (_editor.TryRemoveEdge(edgeId))
            {
                SelectedConnection = null;
                RefreshPresentation("已删除连接。");
            }
            return;
        }

        var ids = SelectedNodes.OfType<WorkflowCanvasNodeViewModel>().Select(node => node.Id).ToArray();
        if (_editor.TryRemoveNodes(ids)) RefreshPresentation($"已删除 {ids.Length} 个节点及其关联连接。");
    }

    private void CommitNodeLocations()
    {
        if (!IsEditing) return;
        var layouts = Nodes.Select(node => new WorkflowNodeLayout
        {
            NodeId = node.Id,
            X = node.Location.X,
            Y = node.Location.Y,
            Width = node.Width,
            Height = node.Height
        }).ToArray();
        if (_editor.TryUpdateLayouts(layouts))
        {
            Status = "节点位置已作为一次编辑记录保存。";
            RaiseCommandStates();
        }
    }

    private void CompleteConnection(object? parameter)
    {
        if (!IsEditing)
        {
            LastConnectionMessage = "当前画布为只读，不能创建连接。";
            return;
        }

        if (!TryGetConnectionPorts(parameter, out var source, out var target))
        {
            LastConnectionMessage = "未识别到有效的连线端口。";
            return;
        }

        var result = _editor.TryConnect(
            source.NodeId,
            source.Key,
            target.NodeId,
            target.Key,
            source.EdgeKind ?? WorkflowEdgeKind.Success);
        LastConnectionMessage = result.IsAllowed ? "连接已创建。" : result.Message;
        if (result.IsAllowed) RefreshPresentation(LastConnectionMessage);
    }

    private void SelectIssue(WorkflowCanvasIssueViewModel? issue)
    {
        if (issue is null) return;
        SelectedIssue = issue;
    }

    private void RefreshPresentation(string status, IReadOnlyList<Guid>? selectNodeIds = null)
    {
        var previousSelection = selectNodeIds ?? SelectedNodes.OfType<WorkflowCanvasNodeViewModel>().Select(node => node.Id).ToArray();
        Nodes.Clear();
        Connections.Clear();
        SelectedNodes.Clear();

        var layouts = _editor.Current.Layouts.ToDictionary(layout => layout.NodeId);
        var nodeMap = new Dictionary<Guid, WorkflowCanvasNodeViewModel>();
        foreach (var node in _editor.Current.Nodes)
        {
            layouts.TryGetValue(node.Id, out var layout);
            var viewModel = new WorkflowCanvasNodeViewModel(
                node,
                new Point(layout?.X ?? 80, layout?.Y ?? 80),
                layout?.Width ?? 200,
                layout?.Height ?? 120,
                IsEditing);
            nodeMap[node.Id] = viewModel;
            Nodes.Add(viewModel);
        }

        foreach (var edge in _editor.Current.Edges)
        {
            if (!nodeMap.TryGetValue(edge.SourceNodeId, out var sourceNode) ||
                !nodeMap.TryGetValue(edge.TargetNodeId, out var targetNode)) continue;
            var source = sourceNode.Ports.FirstOrDefault(port => string.Equals(port.Key, edge.SourcePort, StringComparison.OrdinalIgnoreCase));
            var target = targetNode.Ports.FirstOrDefault(port => string.Equals(port.Key, edge.TargetPort, StringComparison.OrdinalIgnoreCase));
            if (source is not null && target is not null)
                Connections.Add(new WorkflowCanvasEdgeViewModel(edge, sourceNode, targetNode, source, target));
        }

        foreach (var id in previousSelection)
        {
            if (nodeMap.TryGetValue(id, out var node)) SelectedNodes.Add(node);
        }

        RefreshIssues();
        RefreshRuntimeOverlay();
        Status = status;
        OnPropertyChanged(nameof(NodeCount));
        OnPropertyChanged(nameof(EdgeCount));
        RaiseCommandStates();
    }

    private void RefreshRuntimeOverlay()
    {
        foreach (var node in Nodes)
        {
            node.IsEditing = IsEditing;
            node.RuntimeState = CanvasMode == WorkflowCanvasMode.Runtime
                ? node.Index % 11 == 0 ? "Failed" : node.Index % 5 == 0 ? "Running" : node.Index % 3 == 0 ? "Completed" : "Waiting"
                : string.Empty;
        }
    }

    private void RefreshIssues()
    {
        Issues.Clear();
        var invalidTarget = Nodes.FirstOrDefault();
        if (invalidTarget is null) return;
        Issues.Add(new WorkflowCanvasIssueViewModel(
            invalidTarget.Id,
            "SPIKE-CONNECTION-POLICY",
            "示例：非法端口连线会被领域连接策略拒绝。"));
    }

    private void OnSelectedNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SelectedNode = SelectedNodes.OfType<WorkflowCanvasNodeViewModel>().FirstOrDefault();
        RaiseCommandStates();
    }

    private void AttachSelectedNodes(IList selection)
    {
        if (selection is INotifyCollectionChanged observable)
            observable.CollectionChanged += OnSelectedNodesChanged;
    }

    private void DetachSelectedNodes(IList selection)
    {
        if (selection is INotifyCollectionChanged observable)
            observable.CollectionChanged -= OnSelectedNodesChanged;
    }

    private void RaiseCommandStates()
    {
        foreach (var command in new[] { UndoCommand, RedoCommand, CopyCommand, PasteCommand, DeleteCommand, CommitNodeLocationsCommand })
            (command as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static bool TryGetConnectionPorts(
        object? parameter,
        out WorkflowCanvasPortViewModel source,
        out WorkflowCanvasPortViewModel target)
    {
        source = null!;
        target = null!;
        if (parameter is not Tuple<object, object> pair ||
            pair.Item1 is not WorkflowCanvasPortViewModel sourcePort ||
            pair.Item2 is not WorkflowCanvasPortViewModel targetPort) return false;

        source = sourcePort;
        target = targetPort;
        return true;
    }

    private static WorkflowNodeDefinition CreatePaletteNode(string nodeTypeId, int number)
    {
        var displayName = nodeTypeId switch
        {
            "agv.navigate-to-station" => "AGV 移动到站点",
            "robot.execute-program" => "机械臂执行程序",
            "instrument.read-status" => "仪器读取状态",
            "control.wait-for-signal" => "等待外部信号",
            "control.condition" => "条件判断",
            _ => "实验步骤"
        };
        var outputs = nodeTypeId == "control.condition"
            ? new[]
            {
                new WorkflowPortDefinition { Key = "true", DisplayName = "满足", Direction = WorkflowPortDirection.Output, EdgeKind = WorkflowEdgeKind.ConditionTrue },
                new WorkflowPortDefinition { Key = "false", DisplayName = "不满足", Direction = WorkflowPortDirection.Output, EdgeKind = WorkflowEdgeKind.ConditionFalse }
            }
            : new[]
            {
                new WorkflowPortDefinition { Key = "success", DisplayName = "成功", Direction = WorkflowPortDirection.Output, EdgeKind = WorkflowEdgeKind.Success },
                new WorkflowPortDefinition { Key = "failure", DisplayName = "失败", Direction = WorkflowPortDirection.Output, EdgeKind = WorkflowEdgeKind.Failure },
                new WorkflowPortDefinition { Key = "timeout", DisplayName = "超时", Direction = WorkflowPortDirection.Output, EdgeKind = WorkflowEdgeKind.Timeout }
            };
        return new WorkflowNodeDefinition
        {
            NodeTypeId = nodeTypeId,
            Name = $"{displayName} {number}",
            Description = "Spike 示例节点，不会执行设备操作。",
            Ports = [
                new WorkflowPortDefinition { Key = "in", DisplayName = "输入", Direction = WorkflowPortDirection.Input, Cardinality = WorkflowPortCardinality.Many },
                .. outputs
            ]
        };
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

public sealed record WorkflowCanvasDatasetOption(string Key, string DisplayName, string Description);

public sealed class WorkflowCanvasNodeViewModel : INotifyPropertyChanged
{
    private Point _location;
    private bool _isEditing;
    private string _runtimeState = string.Empty;

    public WorkflowCanvasNodeViewModel(
        WorkflowNodeDefinition definition,
        Point location,
        double width,
        double height,
        bool isEditing)
    {
        Id = definition.Id;
        NodeTypeId = definition.NodeTypeId;
        Name = definition.Name;
        Description = definition.Description;
        Index = definition.Configuration.TryGetValue("sampleIndex", out var indexText) && int.TryParse(indexText, out var index) ? index : 0;
        _location = location;
        Width = width;
        Height = height;
        _isEditing = isEditing;
        Ports = new ObservableCollection<WorkflowCanvasPortViewModel>(definition.Ports.Select(port => new WorkflowCanvasPortViewModel(Id, port, isEditing)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; }

    public string NodeTypeId { get; }

    public string Name { get; }

    public string Description { get; }

    public int Index { get; }

    public double Width { get; }

    public double Height { get; }

    public ObservableCollection<WorkflowCanvasPortViewModel> Ports { get; }

    public IEnumerable<WorkflowCanvasPortViewModel> InputPorts => Ports.Where(port => port.Direction == WorkflowPortDirection.Input);

    public IEnumerable<WorkflowCanvasPortViewModel> OutputPorts => Ports.Where(port => port.Direction == WorkflowPortDirection.Output);

    public Point Location
    {
        get => _location;
        set
        {
            if (EqualityComparer<Point>.Default.Equals(_location, value)) return;
            _location = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Location)));
        }
    }

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            foreach (var port in Ports) port.IsEditing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
        }
    }

    public string RuntimeState
    {
        get => _runtimeState;
        set
        {
            if (_runtimeState == value) return;
            _runtimeState = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RuntimeState)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RuntimeStateBrush)));
        }
    }

    public string RuntimeStateBrush => RuntimeState switch
    {
        "Running" => "#0F766E",
        "Completed" => "#247A3D",
        "Failed" => "#B42318",
        "Waiting" => "#8A5A00",
        _ => "#667085"
    };
}

public sealed class WorkflowCanvasPortViewModel : INotifyPropertyChanged
{
    private Point _anchor;
    private bool _isEditing;

    public WorkflowCanvasPortViewModel(Guid nodeId, WorkflowPortDefinition definition, bool isEditing)
    {
        NodeId = nodeId;
        Key = definition.Key;
        DisplayName = definition.DisplayName;
        Direction = definition.Direction;
        DataType = definition.DataType;
        EdgeKind = definition.EdgeKind;
        _isEditing = isEditing;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid NodeId { get; }

    public string Key { get; }

    public string DisplayName { get; }

    public WorkflowPortDirection Direction { get; }

    public string DataType { get; }

    public WorkflowEdgeKind? EdgeKind { get; }

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
        }
    }

    public Point Anchor
    {
        get => _anchor;
        set
        {
            if (EqualityComparer<Point>.Default.Equals(_anchor, value)) return;
            _anchor = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Anchor)));
        }
    }
}

public sealed class WorkflowCanvasEdgeViewModel : INotifyPropertyChanged
{
    public WorkflowCanvasEdgeViewModel(
        WorkflowEdgeDefinition definition,
        WorkflowCanvasNodeViewModel sourceNode,
        WorkflowCanvasNodeViewModel targetNode,
        WorkflowCanvasPortViewModel source,
        WorkflowCanvasPortViewModel target)
    {
        Id = definition.Id;
        SourceNode = sourceNode;
        TargetNode = targetNode;
        Source = source;
        Target = target;
        Kind = definition.Kind;
        Condition = definition.Condition;
        SourceNode.PropertyChanged += OnNodeChanged;
        TargetNode.PropertyChanged += OnNodeChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; }

    public WorkflowCanvasNodeViewModel SourceNode { get; }

    public WorkflowCanvasNodeViewModel TargetNode { get; }

    public WorkflowCanvasPortViewModel Source { get; }

    public WorkflowCanvasPortViewModel Target { get; }

    public WorkflowEdgeKind Kind { get; }

    public string? Condition { get; }

    public string Label => Condition ?? Kind switch
    {
        WorkflowEdgeKind.Success => "成功",
        WorkflowEdgeKind.Failure => "失败",
        WorkflowEdgeKind.Timeout => "超时",
        WorkflowEdgeKind.Cancelled => "取消",
        WorkflowEdgeKind.ConditionTrue => "条件为真",
        WorkflowEdgeKind.ConditionFalse => "条件为假",
        WorkflowEdgeKind.Compensation => "补偿",
        _ => "连接"
    };

    public string Stroke => Kind switch
    {
        WorkflowEdgeKind.Success => "#247A3D",
        WorkflowEdgeKind.Failure => "#B42318",
        WorkflowEdgeKind.Timeout => "#8A5A00",
        WorkflowEdgeKind.Cancelled => "#667085",
        WorkflowEdgeKind.ConditionTrue => "#155EEF",
        WorkflowEdgeKind.ConditionFalse => "#7A5AF8",
        WorkflowEdgeKind.Compensation => "#C4320A",
        _ => "#344054"
    };

    public Point SourcePoint => new(
        SourceNode.Location.X + SourceNode.Width,
        GetPortY(SourceNode, Source));

    public Point TargetPoint => new(
        TargetNode.Location.X,
        GetPortY(TargetNode, Target));

    public Point MidPoint => new((SourcePoint.X + TargetPoint.X) / 2, (SourcePoint.Y + TargetPoint.Y) / 2 - 12);

    private static double GetPortY(WorkflowCanvasNodeViewModel node, WorkflowCanvasPortViewModel port)
    {
        var peers = (port.Direction == WorkflowPortDirection.Input ? node.InputPorts : node.OutputPorts).ToArray();
        var index = Array.IndexOf(peers, port);
        return node.Location.Y + node.Height / 2 + (index - (peers.Length - 1) / 2d) * 20;
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkflowCanvasNodeViewModel.Location)) return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourcePoint)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetPoint)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MidPoint)));
    }
}

public sealed record WorkflowCanvasIssueViewModel(Guid NodeId, string Code, string Message);
