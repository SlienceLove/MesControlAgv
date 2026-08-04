using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.Workflows;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class WorkflowEditorViewModel : INotifyPropertyChanged
{
    private readonly WorkflowStore _store;
    private readonly ObservableCollection<WorkflowNode> _emptyNodes = [];
    private WorkflowDefinition? _selectedWorkflow;
    private WorkflowNode? _selectedNode;
    private string _message = string.Empty;

    public WorkflowEditorViewModel(WorkflowStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Workflows = new ObservableCollection<WorkflowDefinition>(_store.Load());

        NewWorkflowCommand = new EditorCommand(CreateWorkflow);
        CopyWorkflowCommand = new EditorCommand(CopyWorkflow, () => SelectedWorkflow is not null);
        DeleteWorkflowCommand = new EditorCommand(DeleteWorkflow, () => SelectedWorkflow is not null);
        SaveCommand = new EditorCommand(Save);
        AddNodeCommand = new EditorCommand(AddNode, () => SelectedWorkflow is not null);
        DeleteNodeCommand = new EditorCommand(DeleteNode, () => SelectedWorkflow is not null && SelectedNode is not null);
        MoveNodeLeftCommand = new EditorCommand(() => MoveNode(-1), CanMoveNodeLeft);
        MoveNodeRightCommand = new EditorCommand(() => MoveNode(1), CanMoveNodeRight);

        SelectedWorkflow = Workflows.FirstOrDefault();
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

    public ICommand NewWorkflowCommand { get; }
    public ICommand CopyWorkflowCommand { get; }
    public ICommand DeleteWorkflowCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand AddNodeCommand { get; }
    public ICommand DeleteNodeCommand { get; }
    public ICommand MoveNodeLeftCommand { get; }
    public ICommand MoveNodeRightCommand { get; }

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
        foreach (var command in new[] { CopyWorkflowCommand, DeleteWorkflowCommand, AddNodeCommand, DeleteNodeCommand, MoveNodeLeftCommand, MoveNodeRightCommand }.OfType<EditorCommand>()) command.RaiseCanExecuteChanged();
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
