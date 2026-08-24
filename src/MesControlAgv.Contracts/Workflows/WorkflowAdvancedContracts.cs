namespace MesControlAgv.Contracts.Workflows;

/// <summary>Allowlisted source of a value used by a condition gateway.</summary>
public enum WorkflowConditionValueSource
{
    Unspecified,
    RunInput,
    NodeOutput
}

/// <summary>Allowlisted comparison operations. Arbitrary expressions are not a contract option.</summary>
public enum WorkflowConditionOperator
{
    Unspecified,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual
}

/// <summary>Explicit behavior when a condition input has not been persisted.</summary>
public enum WorkflowConditionMissingValueBehavior
{
    Unspecified,
    Fail,
    Wait
}

/// <summary>
/// One versioned predicate attached to a condition edge. The left operand can
/// only read a run input or a persisted node output; the right operand is a
/// typed invariant literal. This intentionally cannot carry executable code.
/// </summary>
public sealed record WorkflowConditionExpression
{
    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public WorkflowConditionValueSource Source { get; init; }
    public Guid? SourceNodeId { get; init; }
    public string SourceKey { get; init; } = string.Empty;
    public WorkflowSchemaValueType ValueType { get; init; } = WorkflowSchemaValueType.String;
    public WorkflowConditionOperator Operator { get; init; }
    public string CompareValue { get; init; } = string.Empty;
    public WorkflowConditionMissingValueBehavior MissingValueBehavior { get; init; }
}
