using System.Text.Json;
using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// Compatibility bridge for the historical Nodify editor. New exports use the
/// graph document; the legacy DTO is accepted only as an import shape.
/// </summary>
public static class ExperimentFlowGraphAdapter
{
    public static WorkflowGraphDocument ToGraph(ExperimentFlowEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        var nodes = editor.Nodes.Select(node => new WorkflowNodeDefinition
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
        var layouts = editor.Nodes.Select(node => new WorkflowNodeLayout
        {
            NodeId = node.Id,
            X = node.Location.X,
            Y = node.Location.Y
        }).ToArray();
        var edges = editor.Connections
            .Where(connection => connection.Source?.Node is not null && connection.Target?.Node is not null)
            .Select(connection => new WorkflowEdgeDefinition
            {
                Id = connection.Id,
                SourceNodeId = connection.Source!.Node!.Id,
                SourcePort = "out",
                TargetNodeId = connection.Target!.Node!.Id,
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
            Name = "Legacy experiment flow",
            Description = "Exported from the compatibility editor",
            Nodes = nodes,
            Edges = edges,
            Layouts = layouts
        };
    }

    public static ExperimentFlowConfigDto ToLegacyConfig(WorkflowGraphDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var layouts = (document.Layouts ?? Array.Empty<WorkflowNodeLayout>())
            .ToDictionary(layout => layout.NodeId);

        return new ExperimentFlowConfigDto
        {
            Nodes = (document.Nodes ?? Array.Empty<WorkflowNodeDefinition>())
                .Select(node =>
                {
                    layouts.TryGetValue(node.Id, out var layout);
                    var type = node.Configuration is not null &&
                               node.Configuration.TryGetValue("legacyType", out var legacyType) &&
                               !string.IsNullOrWhiteSpace(legacyType)
                        ? legacyType!
                        : FromNodeTypeId(node.NodeTypeId);
                    return new NodeDto
                    {
                        Id = node.Id,
                        Title = node.Name,
                        Description = node.Description,
                        Type = type,
                        X = layout?.X ?? 0,
                        Y = layout?.Y ?? 0
                    };
                }).ToList(),
            Connections = (document.Edges ?? Array.Empty<WorkflowEdgeDefinition>())
                .Select(edge => new ConnectionDto
                {
                    Id = edge.Id,
                    SourceNodeId = edge.SourceNodeId,
                    TargetNodeId = edge.TargetNodeId,
                    Condition = edge.Condition ?? string.Empty,
                    Color = edge.Metadata is not null &&
                            edge.Metadata.TryGetValue("legacyColor", out var color) &&
                            !string.IsNullOrWhiteSpace(color)
                        ? color!
                        : "#4285F4"
                }).ToList()
        };
    }

    public static bool LooksLikeGraphDocument(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
        return HasProperty(document.RootElement, "schemaVersion") ||
               HasProperty(document.RootElement, "edges") ||
               HasProperty(document.RootElement, "layouts");
    }

    private static bool HasProperty(JsonElement element, string name) =>
        element.EnumerateObject().Any(property =>
            string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string ToNodeTypeId(string type) => type switch
    {
        "Start" => WorkflowGraphNodeTypeIds.Start,
        "Move" => WorkflowGraphNodeTypeIds.Move,
        "Wait" => WorkflowGraphNodeTypeIds.Wait,
        "Pickup" => WorkflowGraphNodeTypeIds.Pickup,
        "Dropoff" => WorkflowGraphNodeTypeIds.Dropoff,
        "End" => WorkflowGraphNodeTypeIds.End,
        "InstrumentOperation" => WorkflowGraphNodeTypeIds.InstrumentOperation,
        _ => $"legacy.experiment.{type.Trim()}"
    };

    private static string FromNodeTypeId(string? typeId) => typeId switch
    {
        WorkflowGraphNodeTypeIds.Start => "Start",
        WorkflowGraphNodeTypeIds.Move => "Move",
        WorkflowGraphNodeTypeIds.Wait => "Wait",
        WorkflowGraphNodeTypeIds.Pickup => "Pickup",
        WorkflowGraphNodeTypeIds.Dropoff => "Dropoff",
        WorkflowGraphNodeTypeIds.End => "End",
        WorkflowGraphNodeTypeIds.InstrumentOperation => "InstrumentOperation",
        _ when typeId?.StartsWith("legacy.experiment.", StringComparison.OrdinalIgnoreCase) == true =>
            typeId["legacy.experiment.".Length..],
        _ => "Custom"
    };

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
