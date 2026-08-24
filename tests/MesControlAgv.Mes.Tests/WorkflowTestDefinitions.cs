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

    public static WorkflowDefinition CreateTimedWaitWorkflow(
        string durationSeconds,
        Guid? workflowId = null)
    {
        var nodeIds = Enumerable.Range(0, 3)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        return new WorkflowDefinition
        {
            Id = workflowId ?? Guid.NewGuid(),
            SchemaVersion = WorkflowGraphDocument.CurrentSchemaVersion,
            Name = "Typed timed wait workflow",
            Nodes =
            [
                CreateNode(nodeIds[0], WorkflowGraphNodeTypeIds.Start, "Start", 1, [nodeIds[1]]),
                CreateNode(
                    nodeIds[1],
                    WorkflowGraphNodeTypeIds.TimedWait,
                    "Timed wait",
                    2,
                    [nodeIds[2]],
                    new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        [WorkflowRuntimeParameterNames.WaitDurationSeconds] = durationSeconds
                    }),
                CreateNode(nodeIds[2], WorkflowGraphNodeTypeIds.End, "End", 3, [])
            ],
            Edges = CreateLinearEdges(nodeIds)
        };
    }

    public static WorkflowDefinition CreateConditionWorkflow(Guid? workflowId = null)
    {
        var id = workflowId ?? Guid.NewGuid();
        var startId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var endId = Guid.NewGuid();
        var condition = new WorkflowConditionExpression
        {
            Source = WorkflowConditionValueSource.RunInput,
            SourceKey = "sample.pressure",
            ValueType = WorkflowSchemaValueType.Decimal,
            Operator = WorkflowConditionOperator.LessThanOrEqual,
            CompareValue = "12",
            MissingValueBehavior = WorkflowConditionMissingValueBehavior.Wait
        };
        return new WorkflowDefinition
        {
            Id = id,
            SchemaVersion = WorkflowGraphDocument.CurrentSchemaVersion,
            Name = "G6 condition contract",
            Nodes =
            [
                CreateNode(startId, WorkflowGraphNodeTypeIds.Start, "Start", 1, [conditionId]),
                CreateNode(conditionId, WorkflowGraphNodeTypeIds.Condition, "Condition", 2, [endId]),
                CreateNode(endId, WorkflowGraphNodeTypeIds.End, "End", 3, [])
            ],
            Edges =
            [
                new WorkflowEdgeDefinition
                {
                    SourceNodeId = startId,
                    SourcePort = "success",
                    TargetNodeId = conditionId,
                    TargetPort = "in",
                    Kind = WorkflowEdgeKind.Success
                },
                new WorkflowEdgeDefinition
                {
                    SourceNodeId = conditionId,
                    SourcePort = "condition",
                    TargetNodeId = endId,
                    TargetPort = "in",
                    Kind = WorkflowEdgeKind.ConditionTrue,
                    ConditionExpression = condition,
                    Priority = 10
                },
                new WorkflowEdgeDefinition
                {
                    SourceNodeId = conditionId,
                    SourcePort = "default",
                    TargetNodeId = endId,
                    TargetPort = "in",
                    Kind = WorkflowEdgeKind.ConditionFalse
                }
            ]
        };
    }

    public static WorkflowDefinition CreateMoveTimedWaitMoveWorkflow(
        string durationSeconds,
        Guid? workflowId = null)
    {
        var nodeIds = Enumerable.Range(0, 5)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        return new WorkflowDefinition
        {
            Id = workflowId ?? Guid.NewGuid(),
            SchemaVersion = WorkflowGraphDocument.CurrentSchemaVersion,
            Name = "Typed move wait move workflow",
            Nodes =
            [
                CreateNode(nodeIds[0], WorkflowGraphNodeTypeIds.Start, "Start", 1, [nodeIds[1]]),
                CreateNode(
                    nodeIds[1],
                    WorkflowGraphNodeTypeIds.Move,
                    "Move one",
                    2,
                    [nodeIds[2]],
                    MoveConfiguration("SAMPLE_01")),
                CreateNode(
                    nodeIds[2],
                    WorkflowGraphNodeTypeIds.TimedWait,
                    "Timed wait",
                    3,
                    [nodeIds[3]],
                    new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        [WorkflowRuntimeParameterNames.WaitDurationSeconds] = durationSeconds
                    }),
                CreateNode(
                    nodeIds[3],
                    WorkflowGraphNodeTypeIds.Move,
                    "Move two",
                    4,
                    [nodeIds[4]],
                    MoveConfiguration("ST_OPEN_01")),
                CreateNode(nodeIds[4], WorkflowGraphNodeTypeIds.End, "End", 5, [])
            ],
            Edges = CreateLinearEdges(nodeIds)
        };
    }

    private static IReadOnlyDictionary<string, string?> MoveConfiguration(string station) =>
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [WorkflowNodeConfigurationKeys.TargetStation] = station,
            [WorkflowNodeConfigurationKeys.TimeoutSeconds] = "300",
            [WorkflowNodeConfigurationKeys.RetryCount] = "0"
        };

    private static IReadOnlyList<WorkflowEdgeDefinition> CreateLinearEdges(IReadOnlyList<Guid> nodeIds) =>
        Enumerable.Range(0, nodeIds.Count - 1)
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
