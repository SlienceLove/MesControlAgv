using System.Globalization;
using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Domain.Workflows;

/// <summary>
/// G6-A static semantics. These checks define a deterministic, non-executable
/// contract while the corresponding catalog entries remain disabled.
/// </summary>
internal static class WorkflowAdvancedPublicationRules
{
    private static readonly HashSet<string> AdvancedNodeTypeIds = new(StringComparer.OrdinalIgnoreCase)
    {
        WorkflowGraphNodeTypeIds.Condition,
        WorkflowGraphNodeTypeIds.SignalWait,
        WorkflowGraphNodeTypeIds.ParallelFork,
        WorkflowGraphNodeTypeIds.ParallelJoin,
        WorkflowGraphNodeTypeIds.Subflow,
        WorkflowGraphNodeTypeIds.CompensationStart
    };

    public static void Validate(
        WorkflowDefinition workflow,
        WorkflowCatalogSet catalogs,
        ICollection<WorkflowValidationIssue> issues)
    {
        var nodes = workflow.Nodes ?? [];
        var edges = workflow.Edges ?? [];
        var nodesById = nodes
            .Where(node => node.Id != Guid.Empty)
            .GroupBy(node => node.Id)
            .ToDictionary(group => group.Key, group => group.First());

        ValidateSchemaBoundary(workflow.SchemaVersion, nodes, edges, issues);
        ValidateConditionGateways(nodes, edges, nodesById, catalogs, issues);
        ValidateSignalWaits(nodes, issues);
        ValidateParallelGateways(nodes, edges, issues);
        ValidateSubflows(workflow.Id, nodes, issues);
        ValidateCompensationPaths(nodes, edges, nodesById, issues);
    }

    private static void ValidateSchemaBoundary(
        int schemaVersion,
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdgeDefinition> edges,
        ICollection<WorkflowValidationIssue> issues)
    {
        if (schemaVersion >= WorkflowGraphDocument.AdvancedSemanticsSchemaVersion) return;

        foreach (var node in nodes.Where(node => AdvancedNodeTypeIds.Contains(node.NodeTypeId)))
        {
            issues.Add(NodeError(
                WorkflowPublicationIssueCodes.AdvancedSchemaRequired,
                $"Node type '{node.NodeTypeId}' requires graph schema v{WorkflowGraphDocument.AdvancedSemanticsSchemaVersion}.",
                node.Id));
        }

        foreach (var edge in edges.Where(edge =>
                     edge.ConditionExpression is not null ||
                     edge.Kind is WorkflowEdgeKind.Parallel or WorkflowEdgeKind.Compensation))
        {
            issues.Add(EdgeError(
                WorkflowPublicationIssueCodes.AdvancedSchemaRequired,
                $"Advanced edge semantics require graph schema v{WorkflowGraphDocument.AdvancedSemanticsSchemaVersion}.",
                edge));
        }
    }

    private static void ValidateConditionGateways(
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdgeDefinition> edges,
        IReadOnlyDictionary<Guid, WorkflowNode> nodesById,
        WorkflowCatalogSet catalogs,
        ICollection<WorkflowValidationIssue> issues)
    {
        var conditionIds = nodes
            .Where(node => IsNodeType(node, WorkflowGraphNodeTypeIds.Condition))
            .Select(node => node.Id)
            .ToHashSet();

        foreach (var edge in edges.Where(edge => edge.ConditionExpression is not null))
        {
            if (!conditionIds.Contains(edge.SourceNodeId) ||
                !string.Equals(edge.SourcePort, "condition", StringComparison.Ordinal) ||
                edge.Kind != WorkflowEdgeKind.ConditionTrue)
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.ConditionExpressionLocation,
                    "A structured condition is allowed only on a Condition gateway's condition output.",
                    edge));
            }
        }

        foreach (var gateway in nodes.Where(node => IsNodeType(node, WorkflowGraphNodeTypeIds.Condition)))
        {
            var outgoing = edges.Where(edge => edge.SourceNodeId == gateway.Id).ToArray();
            var branches = outgoing.Where(edge =>
                string.Equals(edge.SourcePort, "condition", StringComparison.Ordinal)).ToArray();
            var defaults = outgoing.Where(edge =>
                string.Equals(edge.SourcePort, "default", StringComparison.Ordinal)).ToArray();

            if (branches.Length == 0)
            {
                issues.Add(NodeError(
                    WorkflowPublicationIssueCodes.ConditionExpressionRequired,
                    "A Condition gateway requires at least one ordered condition branch.",
                    gateway.Id));
            }

            foreach (var branch in branches)
            {
                if (branch.ConditionExpression is null)
                {
                    issues.Add(EdgeError(
                        WorkflowPublicationIssueCodes.ConditionExpressionRequired,
                        "Every condition branch requires a structured condition expression.",
                        branch));
                    continue;
                }

                ValidateConditionExpression(gateway, branch, branch.ConditionExpression, edges, nodesById, catalogs, issues);
            }

            if (defaults.Length != 1)
            {
                issues.Add(NodeError(
                    WorkflowPublicationIssueCodes.ConditionDefaultInvalid,
                    "A Condition gateway requires exactly one unconditional default branch.",
                    gateway.Id));
            }

            foreach (var defaultEdge in defaults.Where(edge => edge.ConditionExpression is not null))
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.ConditionDefaultInvalid,
                    "The default branch cannot carry a condition expression.",
                    defaultEdge));
            }

            foreach (var branch in branches.Where(edge => edge.Priority < 0))
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.ConditionPriorityInvalid,
                    "Condition branch priority must be zero or greater.",
                    branch));
            }

            foreach (var duplicate in branches
                         .Where(edge => edge.Priority >= 0)
                         .GroupBy(edge => edge.Priority)
                         .Where(group => group.Count() > 1))
            {
                foreach (var branch in duplicate)
                {
                    issues.Add(EdgeError(
                        WorkflowPublicationIssueCodes.ConditionPriorityInvalid,
                        $"Condition branch priority '{duplicate.Key}' is duplicated.",
                        branch));
                }
            }
        }
    }

    private static void ValidateConditionExpression(
        WorkflowNode gateway,
        WorkflowEdgeDefinition edge,
        WorkflowConditionExpression expression,
        IReadOnlyList<WorkflowEdgeDefinition> edges,
        IReadOnlyDictionary<Guid, WorkflowNode> nodesById,
        WorkflowCatalogSet catalogs,
        ICollection<WorkflowValidationIssue> issues)
    {
        if (!string.Equals(
                expression.SchemaVersion,
                WorkflowConditionExpression.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            issues.Add(EdgeError(
                WorkflowPublicationIssueCodes.ConditionExpressionSchema,
                $"Condition schema '{expression.SchemaVersion}' is not supported.",
                edge));
        }

        if (!IsReferenceKey(expression.SourceKey))
        {
            issues.Add(EdgeError(
                WorkflowPublicationIssueCodes.ConditionSourceInvalid,
                "Condition source key must be a non-empty structured field key.",
                edge));
        }

        if (expression.MissingValueBehavior == WorkflowConditionMissingValueBehavior.Unspecified)
        {
            issues.Add(EdgeError(
                WorkflowPublicationIssueCodes.ConditionMissingBehavior,
                "Condition input-missing behavior must explicitly be Fail or Wait.",
                edge));
        }

        if (expression.Operator == WorkflowConditionOperator.Unspecified ||
            IsRelational(expression.Operator) && expression.ValueType is not (
                WorkflowSchemaValueType.Integer or
                WorkflowSchemaValueType.Decimal or
                WorkflowSchemaValueType.DateTimeOffset))
        {
            issues.Add(EdgeError(
                WorkflowPublicationIssueCodes.ConditionOperatorInvalid,
                $"Operator '{expression.Operator}' is not valid for '{expression.ValueType}'.",
                edge));
        }

        if (!TryParseLiteral(expression.ValueType, expression.CompareValue))
        {
            issues.Add(EdgeError(
                WorkflowPublicationIssueCodes.ConditionValueInvalid,
                $"Comparison value is not a valid invariant '{expression.ValueType}' literal.",
                edge));
        }

        switch (expression.Source)
        {
            case WorkflowConditionValueSource.RunInput:
                if (expression.SourceNodeId is not null)
                {
                    issues.Add(EdgeError(
                        WorkflowPublicationIssueCodes.ConditionSourceInvalid,
                        "A run-input condition cannot declare a source node id.",
                        edge));
                }
                break;

            case WorkflowConditionValueSource.NodeOutput:
                ValidateNodeOutputSource(gateway, edge, expression, edges, nodesById, catalogs, issues);
                break;

            default:
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.ConditionSourceInvalid,
                    "Condition value source must explicitly be RunInput or NodeOutput.",
                    edge));
                break;
        }
    }

    private static void ValidateNodeOutputSource(
        WorkflowNode gateway,
        WorkflowEdgeDefinition edge,
        WorkflowConditionExpression expression,
        IReadOnlyList<WorkflowEdgeDefinition> edges,
        IReadOnlyDictionary<Guid, WorkflowNode> nodesById,
        WorkflowCatalogSet catalogs,
        ICollection<WorkflowValidationIssue> issues)
    {
        if (expression.SourceNodeId is not { } sourceNodeId ||
            sourceNodeId == Guid.Empty ||
            !nodesById.TryGetValue(sourceNodeId, out var sourceNode))
        {
            issues.Add(EdgeError(
                WorkflowPublicationIssueCodes.ConditionSourceUnavailable,
                "The condition source node does not exist in this graph.",
                edge));
            return;
        }

        if (!IsReachable(sourceNodeId, gateway.Id, edges))
        {
            issues.Add(EdgeError(
                WorkflowPublicationIssueCodes.ConditionSourceUnavailable,
                "A condition can only read output persisted by an upstream node.",
                edge));
        }

        var resolution = catalogs.NodeTypes.Resolve(sourceNode.NodeTypeId, sourceNode.SchemaVersion);
        var field = resolution.Definition?.ResultSchema.Fields.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, expression.SourceKey, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            issues.Add(EdgeError(
                WorkflowPublicationIssueCodes.ConditionSourceUnavailable,
                $"Node '{sourceNode.Name}' does not declare result field '{expression.SourceKey}'.",
                edge));
            return;
        }

        if (field.ValueType != expression.ValueType)
        {
            issues.Add(EdgeError(
                WorkflowPublicationIssueCodes.ConditionTypeMismatch,
                $"Condition type '{expression.ValueType}' does not match source field type '{field.ValueType}'.",
                edge));
        }
    }

    private static void ValidateSignalWaits(
        IReadOnlyList<WorkflowNode> nodes,
        ICollection<WorkflowValidationIssue> issues)
    {
        foreach (var node in nodes.Where(node => IsNodeType(node, WorkflowGraphNodeTypeIds.SignalWait)))
        {
            ValidateReferenceConfiguration(
                node,
                WorkflowNodeConfigurationKeys.SignalName,
                "Signal name",
                WorkflowPublicationIssueCodes.SignalReferenceInvalid,
                issues);
            ValidateReferenceConfiguration(
                node,
                WorkflowNodeConfigurationKeys.CorrelationKey,
                "Signal correlation input key",
                WorkflowPublicationIssueCodes.SignalReferenceInvalid,
                issues);
        }
    }

    private static void ValidateParallelGateways(
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdgeDefinition> edges,
        ICollection<WorkflowValidationIssue> issues)
    {
        var forks = nodes.Where(node => IsNodeType(node, WorkflowGraphNodeTypeIds.ParallelFork)).ToArray();
        var joins = nodes.Where(node => IsNodeType(node, WorkflowGraphNodeTypeIds.ParallelJoin)).ToArray();
        var allGateways = forks.Concat(joins).ToArray();
        var forkIds = forks.Select(node => node.Id).ToHashSet();

        foreach (var edge in edges.Where(edge => edge.Kind == WorkflowEdgeKind.Parallel))
        {
            if (forkIds.Contains(edge.SourceNodeId) &&
                string.Equals(edge.SourcePort, "branch", StringComparison.Ordinal))
            {
                continue;
            }

            issues.Add(EdgeError(
                WorkflowPublicationIssueCodes.ParallelBranchInvalid,
                "A parallel edge must originate from a Parallel Fork branch output.",
                edge));
        }

        foreach (var gateway in allGateways)
        {
            if (TryGetConfiguration(gateway, WorkflowNodeConfigurationKeys.ParallelGatewayKey, out var key) &&
                !string.IsNullOrWhiteSpace(key) &&
                !IsReferenceKey(key))
            {
                issues.Add(NodeError(
                    WorkflowPublicationIssueCodes.ParallelPairInvalid,
                    "Parallel gateway pair key must be a structured field key.",
                    gateway.Id,
                    WorkflowNodeConfigurationKeys.ParallelGatewayKey));
            }
        }

        var keys = allGateways
            .Select(node => TryGetConfiguration(node, WorkflowNodeConfigurationKeys.ParallelGatewayKey, out var key)
                ? key
                : null)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var key in keys)
        {
            var matchingForks = forks.Where(node => ConfigurationEquals(
                node,
                WorkflowNodeConfigurationKeys.ParallelGatewayKey,
                key!)).ToArray();
            var matchingJoins = joins.Where(node => ConfigurationEquals(
                node,
                WorkflowNodeConfigurationKeys.ParallelGatewayKey,
                key!)).ToArray();
            if (matchingForks.Length != 1 || matchingJoins.Length != 1)
            {
                foreach (var gateway in matchingForks.Concat(matchingJoins))
                {
                    issues.Add(NodeError(
                        WorkflowPublicationIssueCodes.ParallelPairInvalid,
                        $"Parallel gateway key '{key}' must identify exactly one fork and one join.",
                        gateway.Id,
                        WorkflowNodeConfigurationKeys.ParallelGatewayKey));
                }
                continue;
            }

            ValidateParallelPair(matchingForks[0], matchingJoins[0], edges, issues);
        }
    }

    private static void ValidateParallelPair(
        WorkflowNode fork,
        WorkflowNode join,
        IReadOnlyList<WorkflowEdgeDefinition> edges,
        ICollection<WorkflowValidationIssue> issues)
    {
        var branches = edges.Where(edge =>
            edge.SourceNodeId == fork.Id &&
            string.Equals(edge.SourcePort, "branch", StringComparison.Ordinal) &&
            edge.Kind == WorkflowEdgeKind.Parallel).ToArray();
        if (branches.Length < 2)
        {
            issues.Add(NodeError(
                WorkflowPublicationIssueCodes.ParallelBranchInvalid,
                "A Parallel Fork requires at least two branch edges.",
                fork.Id));
        }

        foreach (var branch in branches.Where(edge => edge.Priority < 0))
        {
            issues.Add(EdgeError(
                WorkflowPublicationIssueCodes.ParallelBranchInvalid,
                "Parallel branch priority must be zero or greater.",
                branch));
        }

        foreach (var duplicate in branches
                     .Where(edge => edge.Priority >= 0)
                     .GroupBy(edge => edge.Priority)
                     .Where(group => group.Count() > 1))
        {
            foreach (var branch in duplicate)
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.ParallelBranchInvalid,
                    $"Parallel branch priority '{duplicate.Key}' is duplicated.",
                    branch));
            }
        }

        var incoming = edges.Where(edge => edge.TargetNodeId == join.Id).ToArray();
        if (incoming.Length < 2)
        {
            issues.Add(NodeError(
                WorkflowPublicationIssueCodes.ParallelJoinInvalid,
                "A Parallel Join requires at least two incoming branches.",
                join.Id));
        }

        foreach (var branch in branches.Where(branch => !IsReachable(branch.TargetNodeId, join.Id, edges)))
        {
            issues.Add(EdgeError(
                WorkflowPublicationIssueCodes.ParallelBranchInvalid,
                "Every parallel branch must reach its paired join.",
                branch));
        }

        foreach (var incomingEdge in incoming.Where(candidate =>
                     !IsReachable(fork.Id, candidate.SourceNodeId, edges)))
        {
            issues.Add(EdgeError(
                WorkflowPublicationIssueCodes.ParallelJoinInvalid,
                "A paired join cannot accept an unrelated incoming branch.",
                incomingEdge));
        }
    }

    private static void ValidateSubflows(
        Guid workflowId,
        IReadOnlyList<WorkflowNode> nodes,
        ICollection<WorkflowValidationIssue> issues)
    {
        foreach (var node in nodes.Where(node => IsNodeType(node, WorkflowGraphNodeTypeIds.Subflow)))
        {
            if (!TryGetConfiguration(node, WorkflowNodeConfigurationKeys.SubflowWorkflowId, out var target) ||
                !Guid.TryParse(target, out var targetWorkflowId) ||
                targetWorkflowId == Guid.Empty)
            {
                issues.Add(NodeError(
                    WorkflowPublicationIssueCodes.SubflowReferenceInvalid,
                    "A subflow must reference a non-empty workflow id.",
                    node.Id,
                    WorkflowNodeConfigurationKeys.SubflowWorkflowId));
            }
            else if (targetWorkflowId == workflowId)
            {
                issues.Add(NodeError(
                    WorkflowPublicationIssueCodes.SubflowReferenceInvalid,
                    "A workflow cannot directly reference itself as a subflow.",
                    node.Id,
                    WorkflowNodeConfigurationKeys.SubflowWorkflowId));
            }
        }
    }

    private static void ValidateCompensationPaths(
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdgeDefinition> edges,
        IReadOnlyDictionary<Guid, WorkflowNode> nodesById,
        ICollection<WorkflowValidationIssue> issues)
    {
        var starts = nodes.Where(node => IsNodeType(node, WorkflowGraphNodeTypeIds.CompensationStart)).ToArray();
        var startIds = starts.Select(node => node.Id).ToHashSet();

        foreach (var edge in edges.Where(edge => edge.Kind == WorkflowEdgeKind.Compensation))
        {
            if (!startIds.Contains(edge.SourceNodeId) ||
                !string.Equals(edge.SourcePort, "compensation", StringComparison.Ordinal))
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.CompensationPathInvalid,
                    "A compensation edge must originate from a Compensation Start node.",
                    edge));
            }
        }

        foreach (var start in starts)
        {
            var incoming = edges.Where(edge => edge.TargetNodeId == start.Id).ToArray();
            if (incoming.Length == 0)
            {
                issues.Add(NodeError(
                    WorkflowPublicationIssueCodes.CompensationPathInvalid,
                    "A Compensation Start node must be entered from an explicit exception path.",
                    start.Id));
            }

            foreach (var edge in incoming.Where(edge => edge.Kind is not (
                         WorkflowEdgeKind.Failure or
                         WorkflowEdgeKind.Timeout or
                         WorkflowEdgeKind.Cancelled)))
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.CompensationPathInvalid,
                    "Compensation can only be entered from failure, timeout, or cancellation.",
                    edge));
            }

            var outgoing = edges.Where(edge =>
                edge.SourceNodeId == start.Id &&
                string.Equals(edge.SourcePort, "compensation", StringComparison.Ordinal) &&
                edge.Kind == WorkflowEdgeKind.Compensation).ToArray();
            if (outgoing.Length != 1)
            {
                issues.Add(NodeError(
                    WorkflowPublicationIssueCodes.CompensationPathInvalid,
                    "A Compensation Start node requires exactly one compensation edge.",
                    start.Id));
            }

            foreach (var edge in outgoing)
            {
                if (nodesById.TryGetValue(edge.TargetNodeId, out var target) &&
                    IsNodeType(target, WorkflowGraphNodeTypeIds.CompensationStart))
                {
                    issues.Add(EdgeError(
                        WorkflowPublicationIssueCodes.CompensationPathInvalid,
                        "Compensation Start nodes cannot be chained directly.",
                        edge));
                }
            }
        }
    }

    private static void ValidateReferenceConfiguration(
        WorkflowNode node,
        string configurationKey,
        string displayName,
        string issueCode,
        ICollection<WorkflowValidationIssue> issues)
    {
        if (TryGetConfiguration(node, configurationKey, out var value) && IsReferenceKey(value)) return;
        issues.Add(NodeError(
            issueCode,
            $"{displayName} must be a non-empty structured field key.",
            node.Id,
            configurationKey));
    }

    private static bool IsReachable(
        Guid sourceNodeId,
        Guid targetNodeId,
        IReadOnlyList<WorkflowEdgeDefinition> edges)
    {
        var pending = new Queue<Guid>();
        var visited = new HashSet<Guid>();
        pending.Enqueue(sourceNodeId);
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current)) continue;
            if (current == targetNodeId) return true;
            foreach (var target in edges.Where(edge => edge.SourceNodeId == current).Select(edge => edge.TargetNodeId))
                pending.Enqueue(target);
        }

        return false;
    }

    private static bool TryParseLiteral(WorkflowSchemaValueType type, string? value) => type switch
    {
        WorkflowSchemaValueType.String => value is not null,
        WorkflowSchemaValueType.Integer => decimal.TryParse(
            value,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var integer) && integer == decimal.Truncate(integer),
        WorkflowSchemaValueType.Decimal => decimal.TryParse(
            value,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out _),
        WorkflowSchemaValueType.Boolean => bool.TryParse(value, out _),
        WorkflowSchemaValueType.DateTimeOffset => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out _),
        _ => false
    };

    private static bool IsRelational(WorkflowConditionOperator value) => value is
        WorkflowConditionOperator.LessThan or
        WorkflowConditionOperator.LessThanOrEqual or
        WorkflowConditionOperator.GreaterThan or
        WorkflowConditionOperator.GreaterThanOrEqual;

    private static bool IsReferenceKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool IsNodeType(WorkflowNode node, string nodeTypeId) =>
        string.Equals(node.NodeTypeId, nodeTypeId, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetConfiguration(WorkflowNode node, string key, out string? value)
    {
        foreach (var pair in node.Configuration ?? new Dictionary<string, string?>())
        {
            if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            value = pair.Value;
            return true;
        }

        value = null;
        return false;
    }

    private static bool ConfigurationEquals(WorkflowNode node, string key, string expected) =>
        TryGetConfiguration(node, key, out var value) &&
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static WorkflowValidationIssue NodeError(
        string code,
        string message,
        Guid nodeId,
        string? configurationKey = null) => new()
    {
        Code = code,
        Message = message,
        Severity = WorkflowValidationSeverity.Error,
        NodeId = nodeId,
        ConfigurationKey = configurationKey
    };

    private static WorkflowValidationIssue EdgeError(
        string code,
        string message,
        WorkflowEdgeDefinition edge) => new()
    {
        Code = code,
        Message = message,
        Severity = WorkflowValidationSeverity.Error,
        NodeId = edge.SourceNodeId == Guid.Empty ? null : edge.SourceNodeId,
        EdgeId = edge.Id == Guid.Empty ? null : edge.Id
    };
}
