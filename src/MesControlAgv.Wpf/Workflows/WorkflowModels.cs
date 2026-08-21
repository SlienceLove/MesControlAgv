using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Wpf.Workflows;

public enum WorkflowNodeType
{
    Start,
    Move,
    Wait,
    Pickup,
    Dropoff,
    End,
    Custom,
    InstrumentOperation
}

public sealed class WorkflowNodeParameter : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string? _value;
    private string _dataType = "string";
    private bool _isRequired;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value ?? string.Empty);
    }

    public string? Value
    {
        get => _value;
        set => SetField(ref _value, value);
    }

    public string DataType
    {
        get => _dataType;
        set => SetField(ref _dataType, value ?? "string");
    }

    public bool IsRequired
    {
        get => _isRequired;
        set => SetField(ref _isRequired, value);
    }

    public WorkflowNodeParameter Clone() => new()
    {
        Name = Name,
        Value = Value,
        DataType = DataType,
        IsRequired = IsRequired
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class WorkflowNode : INotifyPropertyChanged
{
    private WorkflowNodeType _type;
    private string _graphNodeTypeId = string.Empty;
    private string _schemaVersion = "1.0";
    private string _name = string.Empty;
    private string _description = string.Empty;
    private string? _targetStation;
    private double _x;
    private double _y;
    private int _order;
    private ObservableCollection<WorkflowPortDefinition> _ports = [];
    private Dictionary<string, string?> _configuration = new(StringComparer.OrdinalIgnoreCase);

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Stable graph-catalog identifier. The enum remains for existing WPF
    /// bindings and is treated as a compatibility projection.
    /// </summary>
    public string GraphNodeTypeId
    {
        get => _graphNodeTypeId;
        set => SetField(ref _graphNodeTypeId, value ?? string.Empty);
    }

    public string SchemaVersion
    {
        get => _schemaVersion;
        set => SetField(ref _schemaVersion, string.IsNullOrWhiteSpace(value) ? "1.0" : value);
    }

    public WorkflowNodeType Type
    {
        get => _type;
        set
        {
            if (_type == value) return;
            _type = value;
            GraphNodeTypeId = WorkflowGraphNodeTypeIds.For(
                (MesControlAgv.Contracts.Workflows.WorkflowNodeType)value);
            Ports = new ObservableCollection<WorkflowPortDefinition>(
                MesControlAgv.Domain.Workflows.WorkflowGraphContractAdapter.CreatePorts(
                    (MesControlAgv.Contracts.Workflows.WorkflowNodeType)value));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Type)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TypeDescription)));
        }
    }

    public string TypeDescription => Type switch
    {
        WorkflowNodeType.Start => "开始",
        WorkflowNodeType.Move => "AGV 移动",
        WorkflowNodeType.Wait => "等待",
        WorkflowNodeType.Pickup => "取货",
        WorkflowNodeType.Dropoff => "放货",
        WorkflowNodeType.End => "结束",
        WorkflowNodeType.InstrumentOperation => "仪器操作",
        _ => "自定义"
    };

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value ?? string.Empty);
    }

    public string Description
    {
        get => _description;
        set => SetField(ref _description, value ?? string.Empty);
    }

    public string? TargetStation
    {
        get => _targetStation;
        set => SetField(ref _targetStation, value);
    }

    public double X
    {
        get => _x;
        set => SetField(ref _x, value);
    }

    public double Y
    {
        get => _y;
        set => SetField(ref _y, value);
    }

    public int Order
    {
        get => _order;
        set => SetField(ref _order, value);
    }

    public ObservableCollection<WorkflowNodeParameter> Parameters { get; set; } = [];

    public ObservableCollection<Guid> NextNodeIds { get; set; } = [];

    public ObservableCollection<WorkflowPortDefinition> Ports
    {
        get => _ports;
        set => SetField(ref _ports, value ?? []);
    }

    public Dictionary<string, string?> Configuration
    {
        get => _configuration;
        set => SetField(
            ref _configuration,
            value ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
    }

    public WorkflowNode Clone() => new()
    {
        Id = Guid.NewGuid(),
        Type = Type,
        GraphNodeTypeId = GraphNodeTypeId,
        SchemaVersion = SchemaVersion,
        Name = Name,
        Description = Description,
        TargetStation = TargetStation,
        X = X,
        Y = Y,
        Order = Order,
        Parameters = new ObservableCollection<WorkflowNodeParameter>(Parameters.Select(parameter => parameter.Clone())),
        Ports = new ObservableCollection<WorkflowPortDefinition>(Ports),
        Configuration = new Dictionary<string, string?>(Configuration, StringComparer.OrdinalIgnoreCase)
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class WorkflowDefinition : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _description = string.Empty;
    private bool _isPreset;
    private int? _publishedVersion;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value ?? string.Empty);
    }

    public string Description
    {
        get => _description;
        set => SetField(ref _description, value ?? string.Empty);
    }

    public bool IsPreset
    {
        get => _isPreset;
        set => SetField(ref _isPreset, value);
    }

    /// <summary>本地定义最近一次观察到的 MES 发布版本。</summary>
    public int? PublishedVersion
    {
        get => _publishedVersion;
        set => SetField(ref _publishedVersion, value);
    }

    /// <summary>
    /// Explicit graph edges are retained by the unified document adapter;
    /// NextNodeIds remains populated for the legacy MES runtime projection.
    /// </summary>
    public ObservableCollection<WorkflowEdgeDefinition> Edges { get; set; } = [];

    public ObservableCollection<WorkflowNodeLayout> Layouts { get; set; } = [];

    public WorkflowCanvasViewport Viewport { get; set; } = new();

    public ObservableCollection<WorkflowNode> Nodes { get; set; } = [];

    public WorkflowDefinition Clone(string? name = null)
    {
        var sourceNodes = Nodes.OrderBy(node => node.Order).ToArray();
        var idMap = sourceNodes.ToDictionary(node => node.Id, _ => Guid.NewGuid());
        var clonedNodes = sourceNodes.Select(node =>
        {
            var clone = node.Clone();
            clone.Id = idMap[node.Id];
            clone.NextNodeIds = new ObservableCollection<Guid>(
                node.NextNodeIds
                    .Where(idMap.ContainsKey)
                    .Select(id => idMap[id]));
            return clone;
        });

        var clonedIds = sourceNodes.Select(node => node.Id).ToHashSet();
        var cloneMap = sourceNodes.ToDictionary(node => node.Id, node => idMap[node.Id]);
        return new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = name ?? $"{Name} - 副本",
            Description = Description,
            IsPreset = false,
            PublishedVersion = null,
            Nodes = new ObservableCollection<WorkflowNode>(clonedNodes),
            Edges = new ObservableCollection<WorkflowEdgeDefinition>(Edges
                .Where(edge => clonedIds.Contains(edge.SourceNodeId) && clonedIds.Contains(edge.TargetNodeId))
                .Select(edge => edge with
                {
                    Id = Guid.NewGuid(),
                    SourceNodeId = cloneMap[edge.SourceNodeId],
                    TargetNodeId = cloneMap[edge.TargetNodeId]
                })),
            Layouts = new ObservableCollection<WorkflowNodeLayout>(Layouts
                .Where(layout => clonedIds.Contains(layout.NodeId))
                .Select(layout => layout with { NodeId = cloneMap[layout.NodeId] })),
            Viewport = Viewport
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record WorkflowNodeTypeOption(
    string NodeTypeId,
    string SchemaVersion,
    string DisplayName,
    string Category,
    WorkflowNodeType CompatibilityType,
    bool IsAvailable = true,
    string? UnavailableReason = null);
