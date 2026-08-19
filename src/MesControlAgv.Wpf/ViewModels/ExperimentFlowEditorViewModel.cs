using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Workflows;
using MesControlAgv.Wpf.Views;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// 基于 Nodify 的实验流程可视化编辑器 ViewModel
/// </summary>
public sealed class ExperimentFlowEditorViewModel : INotifyPropertyChanged
{
    private ExperimentFlowNode? _selectedNode;
    private ExperimentFlowConnection? _selectedConnection;
    private Point _pendingConnectionSource;
    private ExperimentFlowConnector? _pendingConnectionSourceConnector;
    private bool _isPendingConnectionActive;
    private object? _pendingConnection;

    // 预定义的6种颜色用于分支自动分配
    private static readonly string[] BranchColors =
    {
        "#4285F4", // 蓝色
        "#34A853", // 绿色
        "#FBBC04", // 橙色
        "#EA4335", // 红色
        "#9334E6", // 紫色
        "#FF6D01"  // 深橙色
    };

    public ExperimentFlowEditorViewModel()
    {
        Nodes = [];
        Connections = [];

        CreateNodeCommand = new RelayCommand<string>(nodeType => RequestCreateNode(nodeType));
        DeleteSelectedCommand = new RelayCommand(DeleteSelected, () => SelectedNode != null || SelectedConnection != null);

        // Nodify 连接完成命令
        ConnectionCompletedCommand = new RelayCommand<(object source, object? target)>(param =>
        {
            if (param.source is ExperimentFlowConnector sourceConnector &&
                param.target is ExperimentFlowConnector targetConnector)
            {
                CreateConnection(sourceConnector, targetConnector);
            }
            PendingConnection = null;
        });

        // Nodify 开始连接命令
        ConnectionStartCommand = new RelayCommand<ExperimentFlowConnector>(connector =>
        {
            if (connector != null)
            {
                PendingConnection = new PendingConnectionViewModel { Source = connector };
            }
        });

        DisconnectConnectorCommand = new RelayCommand<ExperimentFlowConnector>(connector =>
        {
            var toRemove = Connections.Where(c => c.Source == connector || c.Target == connector).ToList();
            foreach (var conn in toRemove)
            {
                Connections.Remove(conn);
            }
        });

        AutoLayoutCommand = new RelayCommand(AutoLayout);
        ExportConfigCommand = new RelayCommand(ExportConfig);
        ImportConfigCommand = new RelayCommand(ImportConfig);
        AddConnectionCommand = new RelayCommand(AddConnection);
        RemoveConnectionCommand = new RelayCommand<ExperimentFlowConnection>(RemoveConnection);
        EditConnectionSourceCommand = new RelayCommand<ExperimentFlowConnection>(EditConnectionSource);
        EditConnectionTargetCommand = new RelayCommand<ExperimentFlowConnection>(EditConnectionTarget);
        ChangeConnectionColorCommand = new RelayCommand<object>(ChangeConnectionColor);

        InitializeNodeTypeOptions();
    }

    /// <summary>自动布局：横向主线，纵向分支</summary>
    private void AutoLayout()
    {
        if (Nodes.Count == 0) return;

        const double horizontalSpacing = 300; // 横向间距
        const double verticalSpacing = 200;   // 纵向间距
        const double startX = 100;
        const double startY = 100;

        // 找到开始节点（没有输入连接的节点）
        var startNodes = Nodes.Where(n => !Connections.Any(c => c.Target?.Node == n)).ToList();
        if (startNodes.Count == 0)
        {
            // 如果没有明确的开始节点，使用第一个节点
            startNodes.Add(Nodes[0]);
        }

        var positioned = new HashSet<Guid>();
        var currentLevel = new List<(ExperimentFlowNode node, int row)>();

        // 从开始节点开始布局
        foreach (var startNode in startNodes)
        {
            currentLevel.Add((startNode, 0));
        }

        int column = 0;

        while (currentLevel.Count > 0)
        {
            var nextLevel = new List<(ExperimentFlowNode node, int row)>();
            int currentRow = 0;

            // 按行号排序当前层级的节点
            foreach (var (node, row) in currentLevel.OrderBy(x => x.row))
            {
                if (positioned.Contains(node.Id)) continue;

                // 设置节点位置
                node.Location = new Point(startX + column * horizontalSpacing, startY + currentRow * verticalSpacing);
                positioned.Add(node.Id);

                // 查找该节点的所有输出连接
                var outputConnections = Connections
                    .Where(c => c.Source?.Node == node)
                    .ToList();

                if (outputConnections.Count == 0)
                {
                    // 没有输出，这是一个终点
                    currentRow++;
                }
                else if (outputConnections.Count == 1)
                {
                    // 单一输出，继续主线
                    var targetNode = outputConnections[0].Target?.Node;
                    if (targetNode != null && !positioned.Contains(targetNode.Id))
                    {
                        nextLevel.Add((targetNode, currentRow));
                    }
                    currentRow++;
                }
                else
                {
                    // 多个输出，创建分支
                    int branchRow = currentRow;
                    foreach (var conn in outputConnections)
                    {
                        var targetNode = conn.Target?.Node;
                        if (targetNode != null && !positioned.Contains(targetNode.Id))
                        {
                            nextLevel.Add((targetNode, branchRow));
                            branchRow++;
                        }
                    }
                    currentRow = branchRow;
                }
            }

            currentLevel = nextLevel;
            column++;
        }
    }

    /// <summary>导出配置到JSON文件</summary>
    private void ExportConfig()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON 文件|*.json",
            DefaultExt = ".json",
            FileName = $"ExperimentFlow_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };

        if (dialog.ShowDialog() == true)
        {
            var graph = ExperimentFlowGraphAdapter.ToGraph(this);
            var json = System.Text.Json.JsonSerializer.Serialize(graph, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

            System.IO.File.WriteAllText(dialog.FileName, json);
            System.Windows.MessageBox.Show($"配置已导出到：\n{dialog.FileName}", "导出成功", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }

    /// <summary>从JSON文件导入配置</summary>
    private void ImportConfig()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON 文件|*.json",
            DefaultExt = ".json"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var json = System.IO.File.ReadAllText(dialog.FileName);
                var options = new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                var config = ExperimentFlowGraphAdapter.LooksLikeGraphDocument(json)
                    ? ExperimentFlowGraphAdapter.ToLegacyConfig(
                        System.Text.Json.JsonSerializer.Deserialize<WorkflowGraphDocument>(json, options)
                        ?? throw new InvalidOperationException("Graph document is empty."))
                    : System.Text.Json.JsonSerializer.Deserialize<ExperimentFlowConfigDto>(json, options);

                if (config == null)
                {
                    System.Windows.MessageBox.Show("配置文件格式错误", "导入失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                // 清空现有节点和连接
                Nodes.Clear();
                Connections.Clear();

                // 导入节点
                var nodeMap = new Dictionary<Guid, ExperimentFlowNode>();
                foreach (var nodeDto in config.Nodes)
                {
                    var node = new ExperimentFlowNode
                    {
                        Id = nodeDto.Id,
                        Title = nodeDto.Title,
                        Description = nodeDto.Description,
                        Type = nodeDto.Type,
                        Location = new Point(nodeDto.X, nodeDto.Y)
                    };

                    // 添加连接器
                    node.Input.Add(new ExperimentFlowConnector
                    {
                        Id = Guid.NewGuid(),
                        Title = "输入",
                        IsInput = true,
                        Node = node
                    });

                    node.Output.Add(new ExperimentFlowConnector
                    {
                        Id = Guid.NewGuid(),
                        Title = "输出",
                        IsInput = false,
                        Node = node
                    });

                    Nodes.Add(node);
                    nodeMap[node.Id] = node;
                }

                // 导入连接
                foreach (var connDto in config.Connections)
                {
                    if (nodeMap.TryGetValue(connDto.SourceNodeId, out var sourceNode) &&
                        nodeMap.TryGetValue(connDto.TargetNodeId, out var targetNode))
                    {
                        if (sourceNode.Output.Any() && targetNode.Input.Any())
                        {
                            var connection = new ExperimentFlowConnection
                            {
                                Id = connDto.Id,
                                Source = sourceNode.Output[0],
                                Target = targetNode.Input[0],
                                Condition = connDto.Condition,
                                Color = connDto.Color  // 先使用配置文件中的颜色
                            };

                            Connections.Add(connection);
                        }
                    }
                }

                // 检查是否所有连接都是默认蓝色（说明是旧配置文件），如果是则重新分配颜色
                if (Connections.All(c => c.Color == "#4285F4"))
                {
                    // 按源节点分组，重新分配颜色
                    var connectionsBySource = Connections.GroupBy(c => c.Source?.Node).Where(g => g.Key != null);
                    foreach (var group in connectionsBySource)
                    {
                        int index = 0;
                        foreach (var conn in group)
                        {
                            conn.Color = BranchColors[index % BranchColors.Length];
                            index++;
                        }
                    }
                }

                System.Windows.MessageBox.Show($"成功导入 {Nodes.Count} 个节点和 {Connections.Count} 条连接", "导入成功", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"导入失败：{ex.Message}", "错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }

    /// <summary>添加连接</summary>
    private void AddConnection()
    {
        if (SelectedNode == null) return;

        // 创建一个对话框让用户选择目标节点
        var dialog = new AddConnectionDialog
        {
            Owner = System.Windows.Application.Current.MainWindow,
            AvailableNodes = Nodes.Where(n => n.Id != SelectedNode.Id).ToList(),
            SourceNode = SelectedNode
        };

        if (dialog.ShowDialog() == true && dialog.TargetNode != null)
        {
            if (SelectedNode.Output.Any() && dialog.TargetNode.Input.Any())
            {
                var connection = new ExperimentFlowConnection
                {
                    Source = SelectedNode.Output[0],
                    Target = dialog.TargetNode.Input[0],
                    Condition = dialog.Condition,
                    Color = GetAutoConnectionColor(SelectedNode)
                };

                Connections.Add(connection);
                OnPropertyChanged(nameof(IncomingConnections));
                OnPropertyChanged(nameof(OutgoingConnections));
            }
        }
    }

    /// <summary>删除连接</summary>
    private void RemoveConnection(ExperimentFlowConnection? connection)
    {
        if (connection == null) return;

        Connections.Remove(connection);
        OnPropertyChanged(nameof(IncomingConnections));
        OnPropertyChanged(nameof(OutgoingConnections));
    }

    /// <summary>修改连接的源节点（前置节点）</summary>
    private void EditConnectionSource(ExperimentFlowConnection? connection)
    {
        if (connection == null || connection.Target?.Node == null) return;

        var dialog = new AddConnectionDialog
        {
            Owner = System.Windows.Application.Current.MainWindow,
            AvailableNodes = Nodes.Where(n => n.Id != connection.Target.Node.Id).ToList(),
            SourceNode = null
        };

        dialog.Title = "修改前置节点";

        if (dialog.ShowDialog() == true && dialog.TargetNode != null)
        {
            if (dialog.TargetNode.Output.Any() && connection.Target != null)
            {
                // 更新连接的源节点
                connection.Source = dialog.TargetNode.Output[0];
                OnPropertyChanged(nameof(IncomingConnections));
                OnPropertyChanged(nameof(OutgoingConnections));
            }
        }
    }

    /// <summary>修改连接的目标节点（后续节点）</summary>
    private void EditConnectionTarget(ExperimentFlowConnection? connection)
    {
        if (connection == null || connection.Source?.Node == null) return;

        var dialog = new AddConnectionDialog
        {
            Owner = System.Windows.Application.Current.MainWindow,
            AvailableNodes = Nodes.Where(n => n.Id != connection.Source.Node.Id).ToList(),
            SourceNode = connection.Source.Node
        };

        dialog.Title = "修改后续节点";

        if (dialog.ShowDialog() == true && dialog.TargetNode != null)
        {
            if (connection.Source != null && dialog.TargetNode.Input.Any())
            {
                // 更新连接的目标节点
                connection.Target = dialog.TargetNode.Input[0];
                OnPropertyChanged(nameof(IncomingConnections));
                OnPropertyChanged(nameof(OutgoingConnections));
            }
        }
    }

    /// <summary>修改连接线颜色</summary>
    private void ChangeConnectionColor(object? parameter)
    {
        if (parameter is not System.Windows.Controls.Button button) return;
        if (button.CommandParameter is not ExperimentFlowConnection connection) return;
        if (button.Tag is not string color) return;

        connection.Color = color;
    }

    /// <summary>自动分配连接线颜色：根据源节点的输出连接索引自动分配颜色</summary>
    private string GetAutoConnectionColor(ExperimentFlowNode sourceNode)
    {
        // 计算该源节点已有多少条输出连接
        var existingOutputCount = Connections.Count(c => c.Source?.Node == sourceNode);

        // 根据索引选择颜色（循环使用6种颜色）
        return BranchColors[existingOutputCount % BranchColors.Length];
    }

    public ICommand ConnectionCompletedCommand { get; }
    public ICommand ConnectionStartCommand { get; }
    public ICommand DisconnectConnectorCommand { get; }

    public object? PendingConnection
    {
        get => _pendingConnection;
        set => SetField(ref _pendingConnection, value);
    }

    public event EventHandler<CreateNodeRequestEventArgs>? CreateNodeRequested;

    private void RequestCreateNode(string? nodeType)
    {
        if (string.IsNullOrEmpty(nodeType)) return;

        var args = new CreateNodeRequestEventArgs(nodeType, GetNodeDefaultTitle(nodeType), Nodes);
        CreateNodeRequested?.Invoke(this, args);

        if (args.Confirmed)
        {
            var newNode = CreateNode(nodeType, args.NodeTitle, args.NodeDescription);

            // 直接创建连接，因为现在基于节点位置而不是 Connector.Anchor
            if (args.PreviousNodeId.HasValue)
            {
                var previousNode = Nodes.FirstOrDefault(n => n.Id == args.PreviousNodeId.Value);
                if (previousNode != null && previousNode.Output.Any() && newNode.Input.Any())
                {
                    CreateConnection(previousNode.Output[0], newNode.Input[0]);
                }
            }

            if (args.NextNodeId.HasValue)
            {
                var nextNode = Nodes.FirstOrDefault(n => n.Id == args.NextNodeId.Value);
                if (nextNode != null && newNode.Output.Any() && nextNode.Input.Any())
                {
                    CreateConnection(newNode.Output[0], nextNode.Input[0]);
                }
            }
        }
    }

    private ExperimentFlowNode CreateNode(string nodeType, string title, string description)
    {
        var node = new ExperimentFlowNode
        {
            Id = Guid.NewGuid(),
            Type = nodeType,
            Title = title,
            Description = description,
            Location = new Point(100 + (Nodes.Count * 50), 100 + (Nodes.Count * 30))
        };

        // 根据节点类型添加输入/输出连接器
        if (nodeType != "Start")
        {
            var inputConnector = new ExperimentFlowConnector
            {
                Id = Guid.NewGuid(),
                Title = "输入",
                IsInput = true,
                Node = node
            };
            node.Input.Add(inputConnector);
        }

        if (nodeType != "End")
        {
            var outputConnector = new ExperimentFlowConnector
            {
                Id = Guid.NewGuid(),
                Title = "输出",
                IsInput = false,
                Node = node
            };
            node.Output.Add(outputConnector);
        }

        // 并行网关支持多个输出
        if (nodeType == "ParallelGateway")
        {
            node.Output.Add(new ExperimentFlowConnector
            {
                Id = Guid.NewGuid(),
                Title = "分支2",
                IsInput = false,
                Node = node
            });
        }

        Nodes.Add(node);
        SelectedNode = node;

        return node;
    }

    private void CreateConnection(ExperimentFlowConnector source, ExperimentFlowConnector target)
    {
        // 验证连接有效性
        if (source == target) return;
        if (source.Node == target.Node) return;
        if (source.IsInput == target.IsInput) return; // 必须一个输入一个输出

        // 确保 source 是输出，target 是输入
        if (source.IsInput)
        {
            (source, target) = (target, source);
        }

        // 检查是否已存在连接
        if (Connections.Any(c => c.Source == source && c.Target == target))
            return;

        var connection = new ExperimentFlowConnection
        {
            Id = Guid.NewGuid(),
            Source = source,
            Target = target,
            Color = GetAutoConnectionColor(source.Node!)
        };

        Connections.Add(connection);

        // 调试输出
        System.Diagnostics.Debug.WriteLine($"创建连接: {source.Node?.Title}({source.Title}) -> {target.Node?.Title}({target.Title})");
        System.Diagnostics.Debug.WriteLine($"  Source Anchor: {source.Anchor}, Target Anchor: {target.Anchor}");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ExperimentFlowNode> Nodes { get; }
    public ObservableCollection<ExperimentFlowConnection> Connections { get; }
    public ObservableCollection<ExperimentNodeTypeOption> NodeTypeOptions { get; } = [];

    public ICommand CreateNodeCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand AutoLayoutCommand { get; }
    public ICommand ExportConfigCommand { get; }
    public ICommand ImportConfigCommand { get; }
    public ICommand AddConnectionCommand { get; }
    public ICommand RemoveConnectionCommand { get; }
    public ICommand EditConnectionSourceCommand { get; }
    public ICommand EditConnectionTargetCommand { get; }
    public ICommand ChangeConnectionColorCommand { get; }

    public ExperimentFlowNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetField(ref _selectedNode, value))
            {
                OnPropertyChanged(nameof(IncomingConnections));
                OnPropertyChanged(nameof(OutgoingConnections));
            }
        }
    }

    public ExperimentFlowConnection? SelectedConnection
    {
        get => _selectedConnection;
        set => SetField(ref _selectedConnection, value);
    }

    /// <summary>选中节点的输入连接（前置节点）</summary>
    public IEnumerable<ExperimentFlowConnection> IncomingConnections
    {
        get
        {
            if (SelectedNode == null) return [];
            return Connections.Where(c => c.Target?.Node == SelectedNode);
        }
    }

    /// <summary>选中节点的输出连接（后续节点）</summary>
    public IEnumerable<ExperimentFlowConnection> OutgoingConnections
    {
        get
        {
            if (SelectedNode == null) return [];
            return Connections.Where(c => c.Source?.Node == SelectedNode);
        }
    }

    public Point PendingConnectionSource
    {
        get => _pendingConnectionSource;
        set => SetField(ref _pendingConnectionSource, value);
    }

    public ExperimentFlowConnector? PendingConnectionSourceConnector
    {
        get => _pendingConnectionSourceConnector;
        set => SetField(ref _pendingConnectionSourceConnector, value);
    }

    public bool IsPendingConnectionActive
    {
        get => _isPendingConnectionActive;
        set => SetField(ref _isPendingConnectionActive, value);
    }

    private void InitializeNodeTypeOptions()
    {
        NodeTypeOptions.Add(new ExperimentNodeTypeOption("Start", "开始", "开始节点"));
        NodeTypeOptions.Add(new ExperimentNodeTypeOption("Move", "AGV 移动", "AGV 运输"));
        NodeTypeOptions.Add(new ExperimentNodeTypeOption("Pickup", "取货", "AGV 取货"));
        NodeTypeOptions.Add(new ExperimentNodeTypeOption("Dropoff", "放货", "AGV 放货"));
        NodeTypeOptions.Add(new ExperimentNodeTypeOption("InstrumentOperation", "仪器操作", "启动仪器分析"));
        NodeTypeOptions.Add(new ExperimentNodeTypeOption("DataImport", "数据导入", "导入 Excel 配置"));
        NodeTypeOptions.Add(new ExperimentNodeTypeOption("Wait", "等待", "等待一段时间"));
        NodeTypeOptions.Add(new ExperimentNodeTypeOption("ParallelGateway", "并行网关", "分叉执行多个任务"));
        NodeTypeOptions.Add(new ExperimentNodeTypeOption("End", "结束", "结束节点"));
    }

    private void CreateNode(string? nodeType)
    {
        if (string.IsNullOrEmpty(nodeType)) return;

        var node = new ExperimentFlowNode
        {
            Id = Guid.NewGuid(),
            Type = nodeType,
            Title = GetNodeDefaultTitle(nodeType),
            Location = new Point(100 + (Nodes.Count * 50), 100 + (Nodes.Count * 30))
        };

        // 根据节点类型添加输入/输出连接器
        if (nodeType != "Start")
        {
            node.Input.Add(new ExperimentFlowConnector { Id = Guid.NewGuid(), Title = "输入", IsInput = true });
        }

        if (nodeType != "End")
        {
            node.Output.Add(new ExperimentFlowConnector { Id = Guid.NewGuid(), Title = "输出", IsInput = false });
        }

        // 并行网关支持多个输出
        if (nodeType == "ParallelGateway")
        {
            node.Output.Add(new ExperimentFlowConnector { Id = Guid.NewGuid(), Title = "分支2", IsInput = false });
        }

        Nodes.Add(node);
        SelectedNode = node;
    }

    private void DeleteSelected()
    {
        if (SelectedConnection != null)
        {
            Connections.Remove(SelectedConnection);
            SelectedConnection = null;
        }
        else if (SelectedNode != null)
        {
            // 删除相关连接
            var relatedConnections = Connections
                .Where(c => c.Source?.Node == SelectedNode || c.Target?.Node == SelectedNode)
                .ToList();
            foreach (var conn in relatedConnections)
            {
                Connections.Remove(conn);
            }

            Nodes.Remove(SelectedNode);
            SelectedNode = null;
        }
    }

    private static string GetNodeDefaultTitle(string nodeType) => nodeType switch
    {
        "Start" => "开始",
        "Move" => "移动到站点",
        "Pickup" => "取货",
        "Dropoff" => "放货",
        "InstrumentOperation" => "仪器操作",
        "DataImport" => "数据导入",
        "Wait" => "等待",
        "ParallelGateway" => "并行分叉",
        "End" => "结束",
        _ => "未命名节点"
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(SelectedNode) or nameof(SelectedConnection))
        {
            (DeleteSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>节点</summary>
public sealed class ExperimentFlowNode : INotifyPropertyChanged
{
    private Point _location;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private bool _isSelected;

    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Description
    {
        get => _description;
        set => SetField(ref _description, value);
    }

    public Point Location
    {
        get => _location;
        set => SetField(ref _location, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public ObservableCollection<ExperimentFlowConnector> Input { get; } = [];
    public ObservableCollection<ExperimentFlowConnector> Output { get; } = [];

    // 节点参数（根据类型动态使用）
    public Dictionary<string, object?> Parameters { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

/// <summary>连接器（输入/输出端口）</summary>
public sealed class ExperimentFlowConnector : INotifyPropertyChanged
{
    private Point _anchor;
    private string _title = string.Empty;

    public Guid Id { get; set; }
    public bool IsInput { get; set; }
    public ExperimentFlowNode? Node { get; set; }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public Point Anchor
    {
        get => _anchor;
        set
        {
            if (SetField(ref _anchor, value))
            {
                System.Diagnostics.Debug.WriteLine($"Connector Anchor 更新: {Title} -> {value}");
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

/// <summary>连接线</summary>
public sealed class ExperimentFlowConnection : INotifyPropertyChanged
{
    private ExperimentFlowConnector? _source;
    private ExperimentFlowConnector? _target;
    private bool _isSelected;
    private Point _sourcePoint;
    private Point _targetPoint;
    private string _condition = string.Empty;
    private string _color = "#4285F4"; // 默认蓝色

    public Guid Id { get; set; } = Guid.NewGuid();

    public ExperimentFlowConnector? Source
    {
        get => _source;
        set
        {
            if (_source != null && _source.Node != null)
            {
                _source.Node.PropertyChanged -= OnNodeLocationChanged;
            }

            if (SetField(ref _source, value))
            {
                if (_source?.Node != null)
                {
                    _source.Node.PropertyChanged += OnNodeLocationChanged;
                }
                UpdatePoints();
            }
        }
    }

    public ExperimentFlowConnector? Target
    {
        get => _target;
        set
        {
            if (_target != null && _target.Node != null)
            {
                _target.Node.PropertyChanged -= OnNodeLocationChanged;
            }

            if (SetField(ref _target, value))
            {
                if (_target?.Node != null)
                {
                    _target.Node.PropertyChanged += OnNodeLocationChanged;
                }
                UpdatePoints();
            }
        }
    }

    public Point SourcePoint
    {
        get => _sourcePoint;
        set => SetField(ref _sourcePoint, value);
    }

    public Point TargetPoint
    {
        get => _targetPoint;
        set => SetField(ref _targetPoint, value);
    }

    /// <summary>连接线中点（用于显示条件标签）</summary>
    public Point MidPoint
    {
        get
        {
            return new Point(
                (SourcePoint.X + TargetPoint.X) / 2,
                (SourcePoint.Y + TargetPoint.Y) / 2 - 10 // 稍微向上偏移10像素
            );
        }
    }

    /// <summary>连接条件标签（如"条件A"、"成功"、"失败"等）</summary>
    public string Condition
    {
        get => _condition;
        set => SetField(ref _condition, value);
    }

    /// <summary>连接线颜色（十六进制颜色值，如 #4285F4）</summary>
    public string Color
    {
        get => _color;
        set => SetField(ref _color, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    private void OnNodeLocationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExperimentFlowNode.Location))
        {
            // 立即更新，不延迟
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(UpdatePoints, System.Windows.Threading.DispatcherPriority.Render);
        }
    }

    private void UpdatePoints()
    {
        if (_source?.Node != null)
        {
            var nodePos = _source.Node.Location;
            SourcePoint = new Point(nodePos.X + 200, nodePos.Y + 60); // 右侧中心
        }

        if (_target?.Node != null)
        {
            var nodePos = _target.Node.Location;
            TargetPoint = new Point(nodePos.X, nodePos.Y + 60); // 左侧中心
        }

        OnPropertyChanged(nameof(MidPoint));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>节点类型选项</summary>
public sealed record ExperimentNodeTypeOption(string Type, string DisplayName, string Description);

/// <summary>创建节点请求事件参数</summary>
public sealed class CreateNodeRequestEventArgs : EventArgs
{
    public CreateNodeRequestEventArgs(string nodeType, string defaultTitle, IEnumerable<ExperimentFlowNode> existingNodes)
    {
        NodeType = nodeType;
        NodeTitle = defaultTitle;
        ExistingNodes = existingNodes;
    }

    public string NodeType { get; }
    public string NodeTitle { get; set; }
    public string NodeDescription { get; set; } = string.Empty;
    public Guid? PreviousNodeId { get; set; }
    public Guid? NextNodeId { get; set; }
    public bool Confirmed { get; set; }
    public IEnumerable<ExperimentFlowNode> ExistingNodes { get; }
}

/// <summary>待连接状态</summary>
public sealed class PendingConnectionViewModel
{
    public ExperimentFlowConnector? Source { get; set; }
}
