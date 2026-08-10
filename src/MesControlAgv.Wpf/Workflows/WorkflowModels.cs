using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MesControlAgv.Wpf.Workflows;

public enum WorkflowNodeType
{
    Start,
    Move,
    Wait,
    Pickup,
    Dropoff,
    End,
    Custom
}

public sealed class WorkflowNodeParameter
{
    public string Name { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string DataType { get; set; } = "string";
    public bool IsRequired { get; set; }

    public WorkflowNodeParameter Clone() => new()
    {
        Name = Name,
        Value = Value,
        DataType = DataType,
        IsRequired = IsRequired
    };
}

public sealed class WorkflowNode : INotifyPropertyChanged
{
    private WorkflowNodeType _type;
    private string _name = string.Empty;
    private string _description = string.Empty;
    private string? _targetStation;
    private double _x;
    private double _y;
    private int _order;

    public Guid Id { get; set; } = Guid.NewGuid();

    public WorkflowNodeType Type
    {
        get => _type;
        set
        {
            if (_type == value) return;
            _type = value;
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

    public WorkflowNode Clone() => new()
    {
        Id = Guid.NewGuid(),
        Type = Type,
        Name = Name,
        Description = Description,
        TargetStation = TargetStation,
        X = X,
        Y = Y,
        Order = Order,
        Parameters = new ObservableCollection<WorkflowNodeParameter>(Parameters.Select(parameter => parameter.Clone()))
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

    /// <summary>Latest MES publication observed for this local definition.</summary>
    public int? PublishedVersion { get; set; }

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

        return new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = name ?? $"{Name} - 副本",
            Description = Description,
            IsPreset = false,
            PublishedVersion = null,
            Nodes = new ObservableCollection<WorkflowNode>(clonedNodes)
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

public sealed record WorkflowNodeTypeOption(WorkflowNodeType Value, string DisplayName);
