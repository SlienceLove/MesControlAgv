using System.Collections.ObjectModel;
using System.Text.Json;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;

namespace MesControlAgv.Wpf.Workflows;

/// <summary>
/// WPF compatibility projection for the unified graph document. The projection
/// is deliberately kept at the WPF boundary: MES and Domain never depend on
/// observable collections or canvas controls.
/// </summary>
public static class WorkflowDocumentMapper
{
    public static WorkflowGraphDocument ToGraph(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var localNodes = workflow.Nodes ?? [];
        var edges = workflow.Edges is { Count: > 0 }
            ? workflow.Edges.ToArray()
            : BuildEdgesFromLegacyNodes(localNodes);
        var localNodeIds = localNodes.Select(node => node.Id).ToHashSet();
        var savedLayouts = (workflow.Layouts ?? [])
            .Where(layout => localNodeIds.Contains(layout.NodeId))
            .GroupBy(layout => layout.NodeId)
            .ToDictionary(group => group.Key, group => group.Last());
        var layouts = localNodes.Select(node =>
            savedLayouts.TryGetValue(node.Id, out var layout)
                ? layout
                : new WorkflowNodeLayout
                {
                    NodeId = node.Id,
                    X = node.X,
                    Y = node.Y
                }).ToArray();

        return new WorkflowGraphDocument
        {
            Id = workflow.Id,
            Name = workflow.Name,
            Description = workflow.Description,
            IsPreset = workflow.IsPreset,
            PublishedVersion = workflow.PublishedVersion,
            Nodes = localNodes
                .OrderBy(node => node.Order)
                .Select(node => new WorkflowNodeDefinition
                {
                    Id = node.Id,
                    NodeTypeId = string.IsNullOrWhiteSpace(node.GraphNodeTypeId)
                        ? WorkflowGraphNodeTypeIds.For((MesControlAgv.Contracts.Workflows.WorkflowNodeType)node.Type)
                        : node.GraphNodeTypeId,
                    SchemaVersion = string.IsNullOrWhiteSpace(node.SchemaVersion) ? "1.0" : node.SchemaVersion,
                    Name = node.Name,
                    Description = node.Description,
                    Ports = node.Ports is { Count: > 0 }
                        ? node.Ports.ToArray()
                        : WorkflowGraphContractAdapter.CreatePorts(
                            (MesControlAgv.Contracts.Workflows.WorkflowNodeType)node.Type),
                    Configuration = ToConfiguration(node)
                })
                .ToArray(),
            Edges = edges,
            Layouts = layouts,
            Viewport = workflow.Viewport ?? new WorkflowCanvasViewport()
        };
    }

    public static WorkflowDefinition FromGraph(WorkflowGraphDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var contract = WorkflowGraphContractAdapter.ToContract(document);
        return FromContract(contract);
    }

    public static WorkflowDefinition FromContract(
        MesControlAgv.Contracts.Workflows.WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var contractNodes = workflow.Nodes ?? Array.Empty<MesControlAgv.Contracts.Workflows.WorkflowNode>();
        var edges = workflow.Edges is { Count: > 0 }
            ? workflow.Edges.ToArray()
            : BuildEdgesFromContractNodes(contractNodes);
        var layouts = (workflow.Layouts ?? Array.Empty<WorkflowNodeLayout>())
            .ToDictionary(layout => layout.NodeId);
        var nodes = contractNodes
            .OrderBy(node => node.Order)
            .Select((node, index) =>
            {
                layouts.TryGetValue(node.Id, out var layout);
                return new WorkflowNode
                {
                    Id = node.Id,
                    Type = (WorkflowNodeType)node.Type,
                    GraphNodeTypeId = string.IsNullOrWhiteSpace(node.NodeTypeId)
                        ? WorkflowGraphNodeTypeIds.For(node.Type)
                        : node.NodeTypeId,
                    SchemaVersion = string.IsNullOrWhiteSpace(node.SchemaVersion) ? "1.0" : node.SchemaVersion,
                    Name = node.Name,
                    Description = node.Description,
                    TargetStation = node.TargetStation,
                    X = layout?.X ?? node.X,
                    Y = layout?.Y ?? node.Y,
                    Order = node.Order > 0 ? node.Order : index + 1,
                    Parameters = new ObservableCollection<WorkflowNodeParameter>(
                        (node.Parameters ?? Array.Empty<MesControlAgv.Contracts.Workflows.WorkflowParameter>())
                            .Select(parameter => new WorkflowNodeParameter
                            {
                                Name = parameter.Name,
                                Value = parameter.Value,
                                DataType = parameter.DataType,
                                IsRequired = parameter.IsRequired
                            })),
                    Ports = new ObservableCollection<WorkflowPortDefinition>(
                        node.Ports is { Count: > 0 }
                            ? node.Ports
                            : WorkflowGraphContractAdapter.CreatePorts(node.Type)),
                    NextNodeIds = new ObservableCollection<Guid>(node.NextNodeIds ?? []),
                    Configuration = new Dictionary<string, string?>(
                        node.Configuration ?? new Dictionary<string, string?>(),
                        StringComparer.OrdinalIgnoreCase)
                };
            })
            .ToArray();

        return new WorkflowDefinition
        {
            Id = workflow.Id,
            Name = workflow.Name,
            Description = workflow.Description,
            IsPreset = workflow.IsPreset,
            PublishedVersion = workflow.PublishedVersion,
            Nodes = new ObservableCollection<WorkflowNode>(nodes),
            Edges = new ObservableCollection<WorkflowEdgeDefinition>(edges),
            Layouts = new ObservableCollection<WorkflowNodeLayout>(workflow.Layouts ?? []),
            Viewport = workflow.Viewport ?? new WorkflowCanvasViewport()
        };
    }

    private static IReadOnlyDictionary<string, string?> ToConfiguration(WorkflowNode node)
    {
        var configuration = new Dictionary<string, string?>(
            node.Configuration ?? new Dictionary<string, string?>(),
            StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(node.TargetStation))
        {
            configuration[WorkflowGraphContractAdapter.TargetStationConfigurationKey] = node.TargetStation;
        }

        if (node.Parameters is { Count: > 0 })
        {
            configuration[WorkflowGraphContractAdapter.ParametersConfigurationKey] =
                JsonSerializer.Serialize(node.Parameters);
        }

        return configuration;
    }

    private static IReadOnlyList<WorkflowEdgeDefinition> BuildEdgesFromLegacyNodes(
        IReadOnlyList<WorkflowNode> nodes)
    {
        var knownIds = nodes.Select(node => node.Id).ToHashSet();
        return nodes
            .SelectMany(node => (node.NextNodeIds ?? [])
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

    private static IReadOnlyList<WorkflowEdgeDefinition> BuildEdgesFromContractNodes(
        IReadOnlyList<MesControlAgv.Contracts.Workflows.WorkflowNode> nodes)
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
}
