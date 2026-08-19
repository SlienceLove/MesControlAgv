using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Domain.Workflows;

/// <summary>
/// Small deterministic layout service for the Spike. It is intentionally kept
/// independent from Nodify so a future diagram control can reuse or replace it.
/// </summary>
public sealed class WorkflowLayeredLayout
{
    public WorkflowGraphDocument Arrange(
        WorkflowGraphDocument document,
        double originX = 80,
        double originY = 80,
        double horizontalSpacing = 280,
        double verticalSpacing = 180)
    {
        ArgumentNullException.ThrowIfNull(document);
        var nodeIds = document.Nodes.Select(node => node.Id).ToHashSet();
        var incoming = document.Nodes.ToDictionary(node => node.Id, _ => 0);
        var outgoing = document.Nodes.ToDictionary(node => node.Id, _ => new List<Guid>());
        foreach (var edge in document.Edges.Where(edge => nodeIds.Contains(edge.SourceNodeId) && nodeIds.Contains(edge.TargetNodeId)))
        {
            outgoing[edge.SourceNodeId].Add(edge.TargetNodeId);
            incoming[edge.TargetNodeId]++;
        }

        var depth = document.Nodes.ToDictionary(node => node.Id, _ => 0);
        var queue = new Queue<Guid>(incoming.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = new HashSet<Guid>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current)) continue;
            foreach (var target in outgoing[current])
            {
                depth[target] = Math.Max(depth[target], depth[current] + 1);
                incoming[target]--;
                if (incoming[target] == 0) queue.Enqueue(target);
            }
        }

        // Cycles are not rejected by this visual helper; place any remaining
        // nodes after the deepest acyclic layer so the layout stays deterministic.
        var fallbackDepth = depth.Values.DefaultIfEmpty(0).Max() + 1;
        foreach (var node in document.Nodes.Where(node => !visited.Contains(node.Id)))
            depth[node.Id] = fallbackDepth;

        var layouts = document.Nodes
            .GroupBy(node => depth[node.Id])
            .OrderBy(group => group.Key)
            .SelectMany(group => group
                .OrderBy(node => node.Name, StringComparer.Ordinal)
                .ThenBy(node => node.Id)
                .Select((node, index) => new WorkflowNodeLayout
                {
                    NodeId = node.Id,
                    X = originX + group.Key * horizontalSpacing,
                    Y = originY + index * verticalSpacing
                }))
            .ToArray();

        return document with { Layouts = layouts };
    }
}
