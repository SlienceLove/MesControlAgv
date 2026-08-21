using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;

namespace MesControlAgv.Mes.Tests;

internal static class WorkflowTestDefinitions
{
    private static readonly WorkflowCatalogSet Catalog = BuiltInWorkflowCatalog.Create();

    public static WorkflowDefinition CreateMoveWorkflow(Guid? workflowId = null, params string[] stations)
    {
        if (stations.Length == 0)
            stations = ["SAMPLE_01"];

        var nodeIds = Enumerable.Range(0, stations.Length + 2)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        var nodes = new List<WorkflowNode>
        {
            CreateNode(
                nodeIds[0],
                WorkflowGraphNodeTypeIds.Start,
                "Start",
                1,
                [nodeIds[1]])
        };

        for (var index = 0; index < stations.Length; index++)
        {
            var nextId = nodeIds[index + 2];
            nodes.Add(CreateNode(
                nodeIds[index + 1],
                WorkflowGraphNodeTypeIds.Move,
                $"Move {index + 1}",
                index + 2,
                [nextId],
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    [WorkflowNodeConfigurationKeys.TargetStation] = stations[index],
                    [WorkflowNodeConfigurationKeys.TimeoutSeconds] = "300",
                    [WorkflowNodeConfigurationKeys.RetryCount] = "0"
                }));
        }

        nodes.Add(CreateNode(
            nodeIds[^1],
            WorkflowGraphNodeTypeIds.End,
            "End",
            stations.Length + 2,
            []));

        var edges = Enumerable.Range(0, nodeIds.Length - 1)
            .Select(index => new WorkflowEdgeDefinition
            {
                Id = Guid.NewGuid(),
                SourceNodeId = nodeIds[index],
                SourcePort = "success",
                TargetNodeId = nodeIds[index + 1],
                TargetPort = "in",
                Kind = WorkflowEdgeKind.Success
            })
            .ToArray();

        return new WorkflowDefinition
        {
            Id = workflowId ?? Guid.NewGuid(),
            SchemaVersion = WorkflowGraphDocument.CurrentSchemaVersion,
            Name = "Typed transport workflow",
            Nodes = nodes,
            Edges = edges
        };
    }

    private static WorkflowNode CreateNode(
        Guid id,
        string nodeTypeId,
        string name,
        int order,
        IReadOnlyList<Guid> nextNodeIds,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        if (!Catalog.NodeTypes.TryGet(
                nodeTypeId,
                BuiltInWorkflowCatalog.CurrentSchemaVersion,
                out var definition) || definition is null)
        {
            throw new InvalidOperationException($"Test node type '{nodeTypeId}' is missing from the built-in catalog.");
        }

        configuration ??= new Dictionary<string, string?>();
        configuration.TryGetValue(WorkflowNodeConfigurationKeys.TargetStation, out var targetStation);
        return new WorkflowNode
        {
            Id = id,
            Type = WorkflowGraphNodeTypeIds.ToContractType(nodeTypeId),
            NodeTypeId = nodeTypeId,
            SchemaVersion = definition.SchemaVersion,
            Name = name,
            Order = order,
            TargetStation = targetStation,
            NextNodeIds = nextNodeIds,
            Ports = definition.Ports,
            Configuration = configuration
        };
    }
}
