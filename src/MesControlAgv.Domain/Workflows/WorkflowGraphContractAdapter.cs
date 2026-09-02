using System.Text.Json;
using System.Text.Json.Serialization;
using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Domain.Workflows;

/// <summary>
/// Converts the canvas graph document to the existing MES workflow contract.
/// The MES contract remains backward compatible: older clients can continue to
/// use NextNodeIds while newer clients retain explicit edges, ports and layout.
/// </summary>
public static class WorkflowGraphContractAdapter
{
    public const string TargetStationConfigurationKey = WorkflowNodeConfigurationKeys.TargetStation;
    public const string ParametersConfigurationKey = "$parameters";

    public static WorkflowDefinition ToContract(WorkflowGraphDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var edges = document.Edges ?? Array.Empty<WorkflowEdgeDefinition>();
        var layouts = document.Layouts ?? Array.Empty<WorkflowNodeLayout>();
        var layoutById = layouts.ToDictionary(layout => layout.NodeId);
        var nodes = (document.Nodes ?? Array.Empty<WorkflowNodeDefinition>())
            .Select((node, index) => ToContractNode(
                node,
                edges,
                layoutById.TryGetValue(node.Id, out var layout) ? layout : null,
                index + 1))
            .ToArray();

        return new WorkflowDefinition
        {
            Id = document.Id,
            SchemaVersion = document.SchemaVersion,
            Name = document.Name,
            Description = document.Description,
            IsPreset = document.IsPreset,
            PublishedVersion = document.PublishedVersion,
            Nodes = nodes,
            Edges = edges.ToArray(),
            Layouts = (document.Layouts ?? Array.Empty<WorkflowNodeLayout>()).ToArray(),
            Viewport = document.Viewport ?? new WorkflowCanvasViewport()
        };
    }

    public static WorkflowGraphDocument FromContract(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var nodes = definition.Nodes ?? Array.Empty<WorkflowNode>();
        var explicitEdges = definition.Edges ?? Array.Empty<WorkflowEdgeDefinition>();
        var edges = explicitEdges.Count > 0
            ? explicitEdges.ToArray()
            : BuildEdgesFromNextNodeIds(nodes);

        var layouts = definition.Layouts is { Count: > 0 }
            ? definition.Layouts.ToArray()
            : nodes.Select(node => new WorkflowNodeLayout
            {
                NodeId = node.Id,
                X = node.X,
                Y = node.Y
            }).ToArray();

        return new WorkflowGraphDocument
        {
            Id = definition.Id,
            SchemaVersion = definition.SchemaVersion <= 0
                ? WorkflowGraphDocument.CurrentSchemaVersion
                : definition.SchemaVersion,
            Name = definition.Name,
            Description = definition.Description,
            IsPreset = definition.IsPreset,
            PublishedVersion = definition.PublishedVersion,
            Nodes = nodes.Select(node => FromContractNode(node, edges)).ToArray(),
            Edges = edges,
            Layouts = layouts,
            Viewport = definition.Viewport ?? new WorkflowCanvasViewport()
        };
    }

    public static WorkflowNodeDefinition CreateNode(
        WorkflowNodeType type,
        string name,
        string description = "",
        Guid? id = null,
        IReadOnlyDictionary<string, string?>? configuration = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        NodeTypeId = WorkflowGraphNodeTypeIds.For(type),
        Name = name,
        Description = description,
        Ports = CreatePorts(type),
        Configuration = configuration ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    };

    public static IReadOnlyList<WorkflowPortDefinition> CreatePorts(WorkflowNodeType type) =>
        type switch
        {
            WorkflowNodeType.Start =>
            [
                OutputPort("success", WorkflowEdgeKind.Success)
            ],
            WorkflowNodeType.End =>
            [
                InputPort("in", WorkflowPortCardinality.Many)
            ],
            WorkflowNodeType.Move or WorkflowNodeType.RobotProgram or WorkflowNodeType.InstrumentOperation =>
            [
                InputPort("in", WorkflowPortCardinality.Single),
                OutputPort("success", WorkflowEdgeKind.Success),
                OutputPort("failure", WorkflowEdgeKind.Failure),
                OutputPort("timeout", WorkflowEdgeKind.Timeout)
            ],
            _ =>
            [
                InputPort("in", WorkflowPortCardinality.Single),
                OutputPort("success", WorkflowEdgeKind.Success)
            ]
        };

    private static WorkflowNodeDefinition FromContractNode(
        WorkflowNode node,
        IReadOnlyList<WorkflowEdgeDefinition> edges)
    {
        var configuration = new Dictionary<string, string?>(
            node.Configuration ?? new Dictionary<string, string?>(),
            StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(node.TargetStation))
        {
            configuration[TargetStationConfigurationKey] = node.TargetStation;
        }

        if ((node.Parameters ?? Array.Empty<WorkflowParameter>()).Count > 0)
        {
            configuration[ParametersConfigurationKey] = JsonSerializer.Serialize(
                node.Parameters,
                SerializerOptions);
        }

        return new WorkflowNodeDefinition
        {
            Id = node.Id,
            NodeTypeId = string.IsNullOrWhiteSpace(node.NodeTypeId)
                ? WorkflowGraphNodeTypeIds.For(node.Type)
                : node.NodeTypeId,
            SchemaVersion = string.IsNullOrWhiteSpace(node.SchemaVersion) ? "1.0" : node.SchemaVersion,
            Name = node.Name,
            Description = node.Description,
            Ports = node.Ports is { Count: > 0 }
                ? node.Ports.ToArray()
                : CreatePorts(node.Type),
            Configuration = configuration
        };
    }

    private static WorkflowNode ToContractNode(
        WorkflowNodeDefinition node,
        IReadOnlyList<WorkflowEdgeDefinition> edges,
        WorkflowNodeLayout? layout,
        int order)
    {
        var type = WorkflowGraphNodeTypeIds.ToContractType(node.NodeTypeId);
        var configuration = node.Configuration ?? new Dictionary<string, string?>();
        var targetStation = configuration.TryGetValue(TargetStationConfigurationKey, out var station)
            ? station
            : null;
        var parameters = DecodeParameters(configuration);
        var outgoing = edges
            .Where(edge => edge.SourceNodeId == node.Id)
            .OrderBy(edge => edge.Priority)
            .ThenBy(edge => edge.Id)
            .Select(edge => edge.TargetNodeId)
            .Distinct()
            .ToArray();

        return new WorkflowNode
        {
            Id = node.Id,
            Type = type,
            NodeTypeId = string.IsNullOrWhiteSpace(node.NodeTypeId)
                ? WorkflowGraphNodeTypeIds.For(type)
                : node.NodeTypeId,
            SchemaVersion = node.SchemaVersion,
            Name = node.Name,
            Description = node.Description,
            TargetStation = targetStation,
            X = layout?.X ?? 0,
            Y = layout?.Y ?? 0,
            Order = order,
            Parameters = parameters,
            NextNodeIds = outgoing,
            Ports = node.Ports is { Count: > 0 }
                ? node.Ports.ToArray()
                : CreatePorts(type),
            Configuration = new Dictionary<string, string?>(configuration, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static IReadOnlyList<WorkflowEdgeDefinition> BuildEdgesFromNextNodeIds(
        IReadOnlyList<WorkflowNode> nodes)
    {
        var knownIds = nodes.Select(node => node.Id).ToHashSet();
        return nodes
            .SelectMany(node => (node.NextNodeIds ?? Array.Empty<Guid>())
                .Where(knownIds.Contains)
                .Select((targetId, index) => new WorkflowEdgeDefinition
                {
                    SourceNodeId = node.Id,
                    SourcePort = "success",
                    TargetNodeId = targetId,
                    TargetPort = "in",
                    Kind = WorkflowEdgeKind.Success,
                    Priority = index
                }))
            .ToArray();
    }

    private static IReadOnlyList<WorkflowParameter> DecodeParameters(
        IReadOnlyDictionary<string, string?> configuration)
    {
        if (!configuration.TryGetValue(ParametersConfigurationKey, out var json) ||
            string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<WorkflowParameter>>(json, SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static WorkflowPortDefinition InputPort(string key, WorkflowPortCardinality cardinality) => new()
    {
        Key = key,
        DisplayName = "Input",
        Direction = WorkflowPortDirection.Input,
        DataType = "control",
        Cardinality = cardinality
    };

    private static WorkflowPortDefinition OutputPort(string key, WorkflowEdgeKind kind) => new()
    {
        Key = key,
        DisplayName = "Success",
        Direction = WorkflowPortDirection.Output,
        DataType = "control",
        Cardinality = WorkflowPortCardinality.Many,
        EdgeKind = kind
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
