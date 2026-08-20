using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// One-way compatibility bridge for the historical Nodify editor DTO. The
/// legacy DTO is accepted only as an import shape and is never written again.
/// </summary>
public static class ExperimentFlowGraphAdapter
{
    /// <summary>
    /// Converts the historical standalone-editor DTO into the shared graph
    /// document. Validation and user-visible conversion reporting are owned by
    /// WorkflowDocumentImporter; this method only performs a lossless mapping
    /// of fields represented by the old DTO.
    /// </summary>
    public static WorkflowGraphDocument FromLegacyConfig(
        ExperimentFlowConfigDto config,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var nodes = config.Nodes.Select(node => new WorkflowNodeDefinition
        {
            Id = node.Id,
            NodeTypeId = ToNodeTypeId(node.Type),
            Name = node.Title,
            Description = node.Description,
            Ports = CreatePorts(node.Type),
            Configuration = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["legacyType"] = node.Type
            }
        }).ToArray();
        var layouts = config.Nodes.Select(node => new WorkflowNodeLayout
        {
            NodeId = node.Id,
            X = node.X,
            Y = node.Y
        }).ToArray();
        var edges = config.Connections.Select(connection => new WorkflowEdgeDefinition
        {
            Id = connection.Id,
            SourceNodeId = connection.SourceNodeId,
            SourcePort = "out",
            TargetNodeId = connection.TargetNodeId,
            TargetPort = "in",
            Kind = WorkflowEdgeKind.Success,
            Condition = string.IsNullOrWhiteSpace(connection.Condition) ? null : connection.Condition,
            Metadata = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["legacyColor"] = connection.Color
            }
        }).ToArray();

        return new WorkflowGraphDocument
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? "导入的旧实验流程" : name.Trim(),
            Description = "由旧实验流程设计器兼容格式转换",
            Nodes = nodes,
            Edges = edges,
            Layouts = layouts
        };
    }

    private static string ToNodeTypeId(string? type)
    {
        var normalized = string.IsNullOrWhiteSpace(type) ? "Custom" : type.Trim();
        return normalized switch
        {
            "Start" => WorkflowGraphNodeTypeIds.Start,
            "Move" => WorkflowGraphNodeTypeIds.Move,
            "Wait" => WorkflowGraphNodeTypeIds.Wait,
            "Pickup" => WorkflowGraphNodeTypeIds.Pickup,
            "Dropoff" => WorkflowGraphNodeTypeIds.Dropoff,
            "End" => WorkflowGraphNodeTypeIds.End,
            "InstrumentOperation" => WorkflowGraphNodeTypeIds.InstrumentOperation,
            _ => $"legacy.experiment.{normalized}"
        };
    }

    private static IReadOnlyList<WorkflowPortDefinition> CreatePorts(string type) =>
        type switch
        {
            "Start" => [new WorkflowPortDefinition
            {
                Key = "out",
                DisplayName = "输出",
                Direction = WorkflowPortDirection.Output,
                DataType = "control",
                EdgeKind = WorkflowEdgeKind.Success
            }],
            "End" => [new WorkflowPortDefinition
            {
                Key = "in",
                DisplayName = "输入",
                Direction = WorkflowPortDirection.Input,
                DataType = "control"
            }],
            _ =>
            [
                new WorkflowPortDefinition
                {
                    Key = "in",
                    DisplayName = "输入",
                    Direction = WorkflowPortDirection.Input,
                    DataType = "control",
                    Cardinality = WorkflowPortCardinality.Single
                },
                new WorkflowPortDefinition
                {
                    Key = "out",
                    DisplayName = "输出",
                    Direction = WorkflowPortDirection.Output,
                    DataType = "control",
                    EdgeKind = WorkflowEdgeKind.Success
                }
            ]
        };
}
