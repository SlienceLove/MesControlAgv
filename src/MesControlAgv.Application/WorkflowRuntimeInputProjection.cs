using System.Collections.ObjectModel;
using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Application;

/// <summary>
/// Projects the narrow, published node configuration understood by the current
/// runtime into an immutable step input snapshot while retaining legacy parameters.
/// </summary>
public static class WorkflowRuntimeInputProjection
{
    public static IReadOnlyDictionary<string, string?> ProjectParameters(
        WorkflowExecutionRequest request,
        WorkflowNode node)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(node);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var parameters = node.Parameters ?? Array.Empty<WorkflowParameter>();
        foreach (var parameter in parameters)
        {
            values[parameter.Name] = parameter.Value;
        }

        foreach (var parameter in parameters)
        {
            if (TryGetValue(request.Parameters, parameter.Name, out var supplied))
            {
                values[parameter.Name] = supplied;
            }
        }

        if (string.Equals(
                node.NodeTypeId,
                WorkflowGraphNodeTypeIds.TimedWait,
                StringComparison.OrdinalIgnoreCase) &&
            TryGetValue(
                node.Configuration,
                WorkflowRuntimeParameterNames.WaitDurationSeconds,
                out var durationSeconds))
        {
            values[WorkflowRuntimeParameterNames.WaitDurationSeconds] = durationSeconds;
        }

        return new ReadOnlyDictionary<string, string?>(values);
    }

    private static bool TryGetValue(
        IReadOnlyDictionary<string, string?>? values,
        string key,
        out string? value)
    {
        if (values is not null)
        {
            foreach (var pair in values)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(pair.Key, key))
                {
                    value = pair.Value;
                    return true;
                }
            }
        }

        value = null;
        return false;
    }
}
