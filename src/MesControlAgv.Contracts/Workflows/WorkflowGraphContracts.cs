namespace MesControlAgv.Contracts.Workflows;

/// <summary>
/// The design-time graph model used by the workflow canvas spike. It is kept
/// separate from the legacy linear WorkflowDefinition until the editor and
/// MES persistence paths have completed their migration.
/// </summary>
public enum WorkflowPortDirection
{
    Input,
    Output
}

public enum WorkflowPortCardinality
{
    Many,
    Single
}

/// <summary>Semantic outcome carried by a workflow edge.</summary>
public enum WorkflowEdgeKind
{
    Success,
    Failure,
    Timeout,
    Cancelled,
    ConditionTrue,
    ConditionFalse,
    Compensation
}

public enum WorkflowCanvasMode
{
    Edit,
    ReadOnly,
    Runtime
}

public sealed record WorkflowPortDefinition
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public WorkflowPortDirection Direction { get; init; }
    public string DataType { get; init; } = "control";
    public WorkflowPortCardinality Cardinality { get; init; } = WorkflowPortCardinality.Many;
    /// <summary>
    /// The edge outcome emitted by an output port. A null value is reserved for
    /// generic ports whose edge kind is selected by the caller.
    /// </summary>
    public WorkflowEdgeKind? EdgeKind { get; init; }
}

public sealed record WorkflowNodeDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string NodeTypeId { get; init; } = string.Empty;
    public string SchemaVersion { get; init; } = "1.0";
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<WorkflowPortDefinition> Ports { get; init; } = Array.Empty<WorkflowPortDefinition>();
    public IReadOnlyDictionary<string, string?> Configuration { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

public sealed record WorkflowEdgeDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SourceNodeId { get; init; }
    public string SourcePort { get; init; } = string.Empty;
    public Guid TargetNodeId { get; init; }
    public string TargetPort { get; init; } = string.Empty;
    public WorkflowEdgeKind Kind { get; init; } = WorkflowEdgeKind.Success;
    public string? Condition { get; init; }
    public int Priority { get; init; }
}

public sealed record WorkflowNodeLayout
{
    public Guid NodeId { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; } = 200;
    public double Height { get; init; } = 120;
}

public sealed record WorkflowCanvasViewport
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Zoom { get; init; } = 1;
}

/// <summary>
/// Versioned graph document. Layout and viewport are editor state and must not
/// affect runtime semantics, but are persisted so an editor round trip is lossless.
/// </summary>
public sealed record WorkflowGraphDocument
{
    public const int CurrentSchemaVersion = 1;

    public Guid Id { get; init; } = Guid.NewGuid();
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<WorkflowNodeDefinition> Nodes { get; init; } = Array.Empty<WorkflowNodeDefinition>();
    public IReadOnlyList<WorkflowEdgeDefinition> Edges { get; init; } = Array.Empty<WorkflowEdgeDefinition>();
    public IReadOnlyList<WorkflowNodeLayout> Layouts { get; init; } = Array.Empty<WorkflowNodeLayout>();
    public WorkflowCanvasViewport Viewport { get; init; } = new();
}

public sealed record WorkflowConnectionDecision
{
    public bool IsAllowed { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    public static WorkflowConnectionDecision Allow() => new() { IsAllowed = true, Code = "OK" };

    public static WorkflowConnectionDecision Deny(string code, string message) => new()
    {
        Code = code,
        Message = message
    };
}

public sealed record WorkflowGraphFragment
{
    public IReadOnlyList<WorkflowNodeDefinition> Nodes { get; init; } = Array.Empty<WorkflowNodeDefinition>();
    public IReadOnlyList<WorkflowEdgeDefinition> Edges { get; init; } = Array.Empty<WorkflowEdgeDefinition>();
    public IReadOnlyList<WorkflowNodeLayout> Layouts { get; init; } = Array.Empty<WorkflowNodeLayout>();
}
