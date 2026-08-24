using System.Text.Json;
using System.Text.Json.Serialization;
using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Domain.Workflows;

/// <summary>
/// Framework-neutral editing service used by the canvas adapter. Every mutating
/// operation records an immutable snapshot, making undo/redo deterministic and
/// independent of a particular diagram control.
/// </summary>
public sealed class WorkflowDocumentEditor
{
    private readonly Stack<WorkflowGraphDocument> _undo = new();
    private readonly Stack<WorkflowGraphDocument> _redo = new();
    private readonly WorkflowConnectionPolicy _connectionPolicy;

    public WorkflowDocumentEditor(WorkflowGraphDocument document, WorkflowConnectionPolicy? connectionPolicy = null)
    {
        Current = document ?? throw new ArgumentNullException(nameof(document));
        _connectionPolicy = connectionPolicy ?? new WorkflowConnectionPolicy();
    }

    public WorkflowGraphDocument Current { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public bool TryAddNode(WorkflowNodeDefinition node, WorkflowNodeLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Id == Guid.Empty || Current.Nodes.Any(existing => existing.Id == node.Id)) return false;

        var nodes = Current.Nodes.Append(node).ToArray();
        var layouts = Current.Layouts.Where(item => item.NodeId != node.Id).Append(
            layout ?? new WorkflowNodeLayout { NodeId = node.Id }).ToArray();
        Commit(Current with { Nodes = nodes, Layouts = layouts });
        return true;
    }

    public bool TryRemoveNodes(IEnumerable<Guid> nodeIds)
    {
        var ids = nodeIds.Where(id => id != Guid.Empty).ToHashSet();
        if (ids.Count == 0 || !Current.Nodes.Any(node => ids.Contains(node.Id))) return false;

        Commit(Current with
        {
            Nodes = Current.Nodes.Where(node => !ids.Contains(node.Id)).ToArray(),
            Edges = Current.Edges.Where(edge => !ids.Contains(edge.SourceNodeId) && !ids.Contains(edge.TargetNodeId)).ToArray(),
            Layouts = Current.Layouts.Where(layout => !ids.Contains(layout.NodeId)).ToArray()
        });
        return true;
    }

    public WorkflowConnectionDecision TryConnect(
        Guid sourceNodeId,
        string sourcePort,
        Guid targetNodeId,
        string targetPort,
        WorkflowEdgeKind kind = WorkflowEdgeKind.Success,
        string? condition = null,
        int priority = 0,
        WorkflowConditionExpression? conditionExpression = null)
    {
        var decision = _connectionPolicy.Evaluate(Current, sourceNodeId, sourcePort, targetNodeId, targetPort, kind);
        if (!decision.IsAllowed) return decision;

        var edge = new WorkflowEdgeDefinition
        {
            SourceNodeId = sourceNodeId,
            SourcePort = sourcePort,
            TargetNodeId = targetNodeId,
            TargetPort = targetPort,
            Kind = kind,
            Condition = condition,
            ConditionExpression = conditionExpression,
            Priority = priority
        };
        Commit(Current with { Edges = Current.Edges.Append(edge).ToArray() });
        return decision;
    }

    public bool TryRemoveEdge(Guid edgeId)
    {
        if (!Current.Edges.Any(edge => edge.Id == edgeId)) return false;
        Commit(Current with { Edges = Current.Edges.Where(edge => edge.Id != edgeId).ToArray() });
        return true;
    }

    public bool TryUpdateLayout(Guid nodeId, double x, double y, double? width = null, double? height = null)
    {
        if (!Current.Nodes.Any(node => node.Id == nodeId)) return false;
        var existing = Current.Layouts.FirstOrDefault(layout => layout.NodeId == nodeId);
        var updated = new WorkflowNodeLayout
        {
            NodeId = nodeId,
            X = x,
            Y = y,
            Width = width ?? existing?.Width ?? 200,
            Height = height ?? existing?.Height ?? 120
        };
        Commit(Current with { Layouts = Current.Layouts.Where(layout => layout.NodeId != nodeId).Append(updated).ToArray() });
        return true;
    }

    /// <summary>
    /// Commits all moved nodes as one history entry. Diagram controls commonly
    /// report many intermediate positions during a drag; recording each pixel
    /// would make undo noisy and inflate the history unnecessarily.
    /// </summary>
    public bool TryUpdateLayouts(IEnumerable<WorkflowNodeLayout> layouts)
    {
        ArgumentNullException.ThrowIfNull(layouts);
        var nodeIds = Current.Nodes.Select(node => node.Id).ToHashSet();
        var updates = layouts
            .Where(layout => layout.NodeId != Guid.Empty && nodeIds.Contains(layout.NodeId))
            .GroupBy(layout => layout.NodeId)
            .ToDictionary(group => group.Key, group => group.Last());
        if (updates.Count == 0) return false;

        var currentLayouts = Current.Layouts.ToDictionary(layout => layout.NodeId);
        foreach (var update in updates) currentLayouts[update.Key] = update.Value;
        var next = Current with { Layouts = currentLayouts.Values.OrderBy(layout => layout.NodeId).ToArray() };
        if (Equals(next, Current)) return false;
        Commit(next);
        return true;
    }

    /// <summary>
    /// Updates persisted viewport state without adding navigation gestures to
    /// the graph edit history. Undo and redo preserve the current viewport.
    /// </summary>
    public bool TryUpdateViewport(WorkflowCanvasViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        if (!double.IsFinite(viewport.X) ||
            !double.IsFinite(viewport.Y) ||
            !double.IsFinite(viewport.Zoom) ||
            viewport.Zoom <= 0 ||
            Equals(Current.Viewport, viewport))
        {
            return false;
        }

        Current = Current with { Viewport = viewport };
        return true;
    }

    /// <summary>
    /// Synchronizes edits made by a property panel with the same graph editor.
    /// Normal edits enter history; external lifecycle metadata can update the
    /// current snapshot without becoming an undo step.
    /// </summary>
    public bool TryReplaceDocument(WorkflowGraphDocument document, bool recordHistory = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Id != Current.Id || ReferenceEquals(document, Current)) return false;

        if (recordHistory)
        {
            Commit(document);
        }
        else
        {
            Current = document;
        }

        return true;
    }

    public WorkflowGraphFragment Copy(IEnumerable<Guid> nodeIds)
    {
        var ids = nodeIds.Where(id => id != Guid.Empty).ToHashSet();
        var nodes = Current.Nodes.Where(node => ids.Contains(node.Id)).ToArray();
        var edges = Current.Edges.Where(edge => ids.Contains(edge.SourceNodeId) && ids.Contains(edge.TargetNodeId)).ToArray();
        var layouts = Current.Layouts.Where(layout => ids.Contains(layout.NodeId)).ToArray();
        return new WorkflowGraphFragment { Nodes = nodes, Edges = edges, Layouts = layouts };
    }

    public IReadOnlyList<Guid> Paste(WorkflowGraphFragment fragment, double offsetX = 40, double offsetY = 40)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (fragment.Nodes.Count == 0) return Array.Empty<Guid>();

        var idMap = fragment.Nodes.ToDictionary(node => node.Id, _ => Guid.NewGuid());
        var pastedNodes = fragment.Nodes.Select(node => node with { Id = idMap[node.Id] }).ToArray();
        var pastedEdges = fragment.Edges
            .Where(edge => idMap.ContainsKey(edge.SourceNodeId) && idMap.ContainsKey(edge.TargetNodeId))
            .Select(edge => edge with
            {
                Id = Guid.NewGuid(),
                SourceNodeId = idMap[edge.SourceNodeId],
                TargetNodeId = idMap[edge.TargetNodeId],
                ConditionExpression = RemapConditionExpression(edge.ConditionExpression, idMap)
            })
            .ToArray();
        var pastedLayouts = fragment.Layouts
            .Where(layout => idMap.ContainsKey(layout.NodeId))
            .Select(layout => layout with
            {
                NodeId = idMap[layout.NodeId],
                X = layout.X + offsetX,
                Y = layout.Y + offsetY
            })
            .ToArray();

        Commit(Current with
        {
            Nodes = Current.Nodes.Concat(pastedNodes).ToArray(),
            Edges = Current.Edges.Concat(pastedEdges).ToArray(),
            Layouts = Current.Layouts.Concat(pastedLayouts).ToArray()
        });
        return pastedNodes.Select(node => node.Id).ToArray();
    }

    private static WorkflowConditionExpression? RemapConditionExpression(
        WorkflowConditionExpression? expression,
        IReadOnlyDictionary<Guid, Guid> idMap)
    {
        if (expression is not { Source: WorkflowConditionValueSource.NodeOutput, SourceNodeId: { } sourceNodeId } ||
            !idMap.TryGetValue(sourceNodeId, out var remapped))
        {
            return expression;
        }

        return expression with { SourceNodeId = remapped };
    }

    public bool TryApplyLayout(Func<WorkflowGraphDocument, WorkflowGraphDocument> layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var updated = layout(Current) ?? throw new InvalidOperationException("Layout service returned null.");
        if (ReferenceEquals(updated, Current) || Equals(updated, Current)) return false;
        Commit(updated);
        return true;
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var viewport = Current.Viewport;
        var publishedVersion = Current.PublishedVersion;
        _redo.Push(Current);
        Current = _undo.Pop() with
        {
            Viewport = viewport,
            PublishedVersion = publishedVersion
        };
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var viewport = Current.Viewport;
        var publishedVersion = Current.PublishedVersion;
        _undo.Push(Current);
        Current = _redo.Pop() with
        {
            Viewport = viewport,
            PublishedVersion = publishedVersion
        };
        return true;
    }

    public string Serialize()
    {
        return JsonSerializer.Serialize(Current, JsonOptions);
    }

    public static WorkflowGraphDocument Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Workflow JSON cannot be empty.", nameof(json));
        return JsonSerializer.Deserialize<WorkflowGraphDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException("Workflow JSON did not contain a document.");
    }

    private void Commit(WorkflowGraphDocument next)
    {
        _undo.Push(Current);
        Current = next;
        _redo.Clear();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
