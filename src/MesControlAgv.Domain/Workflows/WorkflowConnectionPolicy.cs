using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Domain.Workflows;

/// <summary>
/// Domain-level connection rules. Nodify may offer a visual preview, but this
/// policy remains the authoritative decision and is independently testable.
/// </summary>
public sealed class WorkflowConnectionPolicy
{
    public WorkflowConnectionDecision Evaluate(
        WorkflowGraphDocument document,
        Guid sourceNodeId,
        string sourcePort,
        Guid targetNodeId,
        string targetPort,
        WorkflowEdgeKind kind)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (sourceNodeId == Guid.Empty || targetNodeId == Guid.Empty)
            return WorkflowConnectionDecision.Deny("WF-CONNECTION-ID", "源节点和目标节点必须有有效 ID。");
        if (sourceNodeId == targetNodeId)
            return WorkflowConnectionDecision.Deny("WF-CONNECTION-SELF", "不允许连接节点自身。");

        var source = document.Nodes.FirstOrDefault(node => node.Id == sourceNodeId);
        var target = document.Nodes.FirstOrDefault(node => node.Id == targetNodeId);
        if (source is null || target is null)
            return WorkflowConnectionDecision.Deny("WF-CONNECTION-NODE", "源节点或目标节点不存在。");

        var sourceDefinition = source.Ports.FirstOrDefault(port =>
            string.Equals(port.Key, sourcePort, StringComparison.OrdinalIgnoreCase));
        var targetDefinition = target.Ports.FirstOrDefault(port =>
            string.Equals(port.Key, targetPort, StringComparison.OrdinalIgnoreCase));
        if (sourceDefinition is null || targetDefinition is null)
            return WorkflowConnectionDecision.Deny("WF-CONNECTION-PORT", "源端口或目标端口不存在。");
        if (sourceDefinition.Direction != WorkflowPortDirection.Output)
            return WorkflowConnectionDecision.Deny("WF-CONNECTION-SOURCE-DIRECTION", "源端口必须是输出端口。");
        if (targetDefinition.Direction != WorkflowPortDirection.Input)
            return WorkflowConnectionDecision.Deny("WF-CONNECTION-TARGET-DIRECTION", "目标端口必须是输入端口。");
        if (!AreCompatible(sourceDefinition.DataType, targetDefinition.DataType))
            return WorkflowConnectionDecision.Deny("WF-CONNECTION-TYPE", "源端口和目标端口的数据类型不兼容。");
        if (sourceDefinition.EdgeKind is { } emittedKind && emittedKind != kind)
            return WorkflowConnectionDecision.Deny("WF-CONNECTION-SEMANTIC", "连线语义必须与源端口的结果语义一致。");

        if (document.Edges.Any(edge =>
                edge.SourceNodeId == sourceNodeId &&
                string.Equals(edge.SourcePort, sourcePort, StringComparison.OrdinalIgnoreCase) &&
                edge.TargetNodeId == targetNodeId &&
                string.Equals(edge.TargetPort, targetPort, StringComparison.OrdinalIgnoreCase)))
        {
            return WorkflowConnectionDecision.Deny("WF-CONNECTION-DUPLICATE", "相同的连接已经存在。");
        }

        if (targetDefinition.Cardinality == WorkflowPortCardinality.Single && document.Edges.Any(edge =>
                edge.TargetNodeId == targetNodeId &&
                string.Equals(edge.TargetPort, targetPort, StringComparison.OrdinalIgnoreCase)))
        {
            return WorkflowConnectionDecision.Deny("WF-CONNECTION-CARDINALITY", "目标输入端口只允许一条连接。");
        }

        return WorkflowConnectionDecision.Allow();
    }

    private static bool AreCompatible(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
