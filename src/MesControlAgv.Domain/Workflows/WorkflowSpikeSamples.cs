using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Domain.Workflows;

/// <summary>
/// Deterministic in-memory graphs for the Nodify Spike. They describe device
/// capabilities only; they never dispatch a device operation.
/// </summary>
public static class WorkflowSpikeSamples
{
    public static WorkflowGraphDocument CreateLinear() => CreateGraph("线性实验样例", 20, 19);

    public static WorkflowGraphDocument CreateBranching() => CreateGraph("分支实验样例", 60, 90);

    public static WorkflowGraphDocument CreateLarge() => CreateGraph("大图压力样例", 200, 350);

    public static WorkflowGraphDocument CreateCopyProbe() => CreateGraph("复制粘贴验收样例", 5, 6);

    private static WorkflowGraphDocument CreateGraph(string name, int nodeCount, int edgeCount)
    {
        if (nodeCount < 2 || edgeCount < nodeCount - 1)
            throw new ArgumentOutOfRangeException(nameof(edgeCount));

        var nodes = Enumerable.Range(0, nodeCount)
            .Select(index => CreateNode(index, nodeCount))
            .ToArray();
        var edges = new List<WorkflowEdgeDefinition>(edgeCount);

        for (var index = 0; index < nodeCount - 1; index++)
            edges.Add(CreateEdge(nodes[index], nodes[index + 1], edges.Count));

        for (var distance = 2; edges.Count < edgeCount; distance++)
        {
            for (var sourceIndex = 0; sourceIndex + distance < nodeCount && edges.Count < edgeCount; sourceIndex++)
            {
                var targetIndex = sourceIndex + distance;
                edges.Add(CreateEdge(nodes[sourceIndex], nodes[targetIndex], edges.Count));
            }
        }

        return new WorkflowGraphDocument
        {
            Name = name,
            Description = "Nodify Spike 内存数据，不连接 MES 或实体设备。",
            Nodes = nodes,
            Edges = edges
        };
    }

    private static WorkflowNodeDefinition CreateNode(int index, int count)
    {
        var isStart = index == 0;
        var isEnd = index == count - 1;
        var nodeType = (index % 5) switch
        {
            0 => "control.sequence",
            1 => "agv.navigate-to-station",
            2 => "robot.execute-program",
            3 => "instrument.read-status",
            _ => "control.wait-for-signal"
        };

        var ports = new List<WorkflowPortDefinition>();
        if (!isStart)
        {
            ports.Add(new WorkflowPortDefinition
            {
                Key = "in",
                DisplayName = "输入",
                Direction = WorkflowPortDirection.Input,
                DataType = "control",
                Cardinality = WorkflowPortCardinality.Many
            });
        }

        if (!isEnd)
        {
            ports.AddRange(
            [
                new WorkflowPortDefinition { Key = "success", DisplayName = "成功", Direction = WorkflowPortDirection.Output, EdgeKind = WorkflowEdgeKind.Success },
                new WorkflowPortDefinition { Key = "failure", DisplayName = "失败", Direction = WorkflowPortDirection.Output, EdgeKind = WorkflowEdgeKind.Failure },
                new WorkflowPortDefinition { Key = "timeout", DisplayName = "超时", Direction = WorkflowPortDirection.Output, EdgeKind = WorkflowEdgeKind.Timeout },
                new WorkflowPortDefinition { Key = "condition-true", DisplayName = "条件为真", Direction = WorkflowPortDirection.Output, EdgeKind = WorkflowEdgeKind.ConditionTrue },
                new WorkflowPortDefinition { Key = "condition-false", DisplayName = "条件为假", Direction = WorkflowPortDirection.Output, EdgeKind = WorkflowEdgeKind.ConditionFalse }
            ]);
        }

        return new WorkflowNodeDefinition
        {
            Id = Guid.NewGuid(),
            NodeTypeId = isStart ? "control.start" : isEnd ? "control.end" : nodeType,
            Name = isStart ? "开始" : isEnd ? "结束" : $"步骤 {index + 1}",
            Description = isStart || isEnd ? "流程边界" : "可配置实验步骤",
            Ports = ports,
            Configuration = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["sampleIndex"] = index.ToString(),
                ["station"] = index % 3 == 0 ? $"IC_{index % 4 + 1:00}" : null
            }
        };
    }

    private static WorkflowEdgeDefinition CreateEdge(
        WorkflowNodeDefinition source,
        WorkflowNodeDefinition target,
        int index)
    {
        var kind = (index % 7) switch
        {
            1 => WorkflowEdgeKind.Failure,
            2 => WorkflowEdgeKind.Timeout,
            3 => WorkflowEdgeKind.ConditionTrue,
            4 => WorkflowEdgeKind.ConditionFalse,
            _ => WorkflowEdgeKind.Success
        };

        return new WorkflowEdgeDefinition
        {
            SourceNodeId = source.Id,
            SourcePort = kind switch
            {
                WorkflowEdgeKind.Failure => "failure",
                WorkflowEdgeKind.Timeout => "timeout",
                WorkflowEdgeKind.ConditionTrue => "condition-true",
                WorkflowEdgeKind.ConditionFalse => "condition-false",
                _ => "success"
            },
            TargetNodeId = target.Id,
            TargetPort = "in",
            Kind = kind,
            Condition = kind is WorkflowEdgeKind.ConditionTrue or WorkflowEdgeKind.ConditionFalse
                ? "result == true"
                : null,
            Priority = index
        };
    }
}
