using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Domain.Workflows;

/// <summary>
/// Stateless domain validation for workflow definitions. It accepts the contract
/// model directly so API/application adapters and the WPF editor can share exactly
/// the same publish gate without coupling Domain to WPF.
/// </summary>
public sealed class WorkflowValidator
{
    public const string ValidatorVersion = "workflow-contract-v1";

    public WorkflowValidationResult Validate(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var issues = new List<WorkflowValidationIssue>();
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            issues.Add(Error("WF001", "Workflow name is required."));
        }

        var nodes = definition.Nodes ?? Array.Empty<WorkflowNode>();
        if (nodes.Count == 0)
        {
            issues.Add(Error("WF002", "Workflow must contain at least one node."));
        }

        ValidateNodeIdentityAndOrder(nodes, issues);
        ValidateBoundaryNodes(nodes, issues);
        ValidateNodeProperties(nodes, issues);
        ValidateEdges(nodes, issues);

        return new WorkflowValidationResult
        {
            Issues = issues,
            ValidatorVersion = ValidatorVersion,
            ValidatedAt = DateTimeOffset.UtcNow
        };
    }

    private static void ValidateNodeIdentityAndOrder(
        IReadOnlyList<WorkflowNode> nodes,
        ICollection<WorkflowValidationIssue> issues)
    {
        var duplicateIds = nodes
            .Where(node => node.Id != Guid.Empty)
            .GroupBy(node => node.Id)
            .Where(group => group.Count() > 1);
        foreach (var duplicate in duplicateIds)
        {
            issues.Add(Error("WF003", $"Node id '{duplicate.Key}' must be unique."));
        }

        var emptyIdCount = nodes.Count(node => node.Id == Guid.Empty);
        if (emptyIdCount > 0)
        {
            issues.Add(Error("WF004", "Every workflow node must have a non-empty id."));
        }

        var duplicateOrders = nodes
            .Where(node => node.Order > 0)
            .GroupBy(node => node.Order)
            .Where(group => group.Count() > 1);
        foreach (var duplicate in duplicateOrders)
        {
            issues.Add(Error("WF005", $"Node order '{duplicate.Key}' must be unique."));
        }

        if (nodes.Any(node => node.Order <= 0))
        {
            issues.Add(Error("WF006", "Every workflow node must have a positive order."));
        }
    }

    private static void ValidateBoundaryNodes(
        IReadOnlyList<WorkflowNode> nodes,
        ICollection<WorkflowValidationIssue> issues)
    {
        var starts = nodes.Where(node => node.Type == WorkflowNodeType.Start).ToList();
        var ends = nodes.Where(node => node.Type == WorkflowNodeType.End).ToList();

        if (starts.Count != 1)
        {
            issues.Add(Error("WF007", "Workflow must contain exactly one Start node."));
        }

        if (ends.Count != 1)
        {
            issues.Add(Error("WF008", "Workflow must contain exactly one End node."));
        }

        if (starts.Count == 1 && nodes.Count > 0 && starts[0].Order != nodes.Min(node => node.Order))
        {
            issues.Add(Warning("WF009", "The Start node is not the first ordered node.", starts[0].Id));
        }

        if (ends.Count == 1 && nodes.Count > 0 && ends[0].Order != nodes.Max(node => node.Order))
        {
            issues.Add(Warning("WF010", "The End node is not the last ordered node.", ends[0].Id));
        }
    }

    private static void ValidateNodeProperties(
        IReadOnlyList<WorkflowNode> nodes,
        ICollection<WorkflowValidationIssue> issues)
    {
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Name))
            {
                issues.Add(Error("WF011", "Node name is required.", node.Id));
            }

            if (node.Type is WorkflowNodeType.Move or WorkflowNodeType.Pickup or WorkflowNodeType.Dropoff &&
                string.IsNullOrWhiteSpace(node.TargetStation))
            {
                issues.Add(Error("WF012", $"Node type '{node.Type}' requires a target station.", node.Id));
            }

            var parameters = node.Parameters ?? Array.Empty<WorkflowParameter>();
            var duplicateParameters = parameters
                .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name))
                .GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1);
            foreach (var duplicate in duplicateParameters)
            {
                issues.Add(Error(
                    "WF013",
                    $"Parameter '{duplicate.Key}' must be unique within a node.",
                    node.Id,
                    duplicate.Key));
            }

            foreach (var parameter in parameters)
            {
                if (string.IsNullOrWhiteSpace(parameter.Name))
                {
                    issues.Add(Error("WF014", "Parameter name is required.", node.Id));
                }

                if (parameter.IsRequired && string.IsNullOrWhiteSpace(parameter.Value))
                {
                    issues.Add(Warning(
                        "WF015",
                        $"Required parameter '{parameter.Name}' has no default value.",
                        node.Id,
                        parameter.Name));
                }
            }
        }
    }

    private static void ValidateEdges(
        IReadOnlyList<WorkflowNode> nodes,
        ICollection<WorkflowValidationIssue> issues)
    {
        var nodeIds = nodes.Select(node => node.Id).ToHashSet();
        foreach (var node in nodes)
        {
            var nextNodeIds = node.NextNodeIds ?? Array.Empty<Guid>();
            var duplicateTargets = nextNodeIds
                .GroupBy(id => id)
                .Where(group => group.Count() > 1);
            foreach (var duplicate in duplicateTargets)
            {
                issues.Add(Error("WF016", $"Node '{node.Name}' contains a duplicate next-node reference.", node.Id));
            }

            foreach (var targetId in nextNodeIds)
            {
                if (targetId == Guid.Empty || !nodeIds.Contains(targetId))
                {
                    issues.Add(Error(
                        "WF017",
                        $"Node '{node.Name}' references a node that does not exist.",
                        node.Id));
                }
            }
        }
    }

    private static WorkflowValidationIssue Error(string code, string message, Guid? nodeId = null, string? parameterName = null) => new()
    {
        Code = code,
        Message = message,
        Severity = WorkflowValidationSeverity.Error,
        NodeId = nodeId,
        ParameterName = parameterName
    };

    private static WorkflowValidationIssue Warning(string code, string message, Guid? nodeId = null, string? parameterName = null) => new()
    {
        Code = code,
        Message = message,
        Severity = WorkflowValidationSeverity.Warning,
        NodeId = nodeId,
        ParameterName = parameterName
    };
}
