using System.Windows;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>实验流程配置文件数据传输对象</summary>
public sealed class ExperimentFlowConfigDto
{
    public List<NodeDto> Nodes { get; set; } = [];
    public List<ConnectionDto> Connections { get; set; } = [];
}

public sealed class NodeDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class ConnectionDto
{
    public Guid Id { get; set; }
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }
    public string Condition { get; set; } = string.Empty;
    public string Color { get; set; } = "#4285F4";
}
