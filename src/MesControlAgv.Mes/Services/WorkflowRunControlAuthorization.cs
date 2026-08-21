using MesControlAgv.Contracts.Workflows;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Services;

public sealed class WorkflowRunControlAuthorizationOptions
{
    public List<WorkflowRunControlOperatorOptions> Operators { get; init; } = [];
}

public sealed class WorkflowRunControlOperatorOptions
{
    public string Name { get; init; } = string.Empty;
    public List<string> Permissions { get; init; } = [];
}

public interface IWorkflowRunControlAuthorizer
{
    WorkflowRunControlPermissionsSnapshot GetPermissions(string actor);
    void Demand(string actor, string permission);
}

public sealed class ConfiguredWorkflowRunControlAuthorizer : IWorkflowRunControlAuthorizer
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _permissionsByActor;

    public ConfiguredWorkflowRunControlAuthorizer(IOptions<WorkflowRunControlAuthorizationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _permissionsByActor = options.Value.Operators
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .SelectMany(item => item.Permissions)
                    .Where(permission => !string.IsNullOrWhiteSpace(permission))
                    .Select(permission => permission.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    public WorkflowRunControlPermissionsSnapshot GetPermissions(string actor)
    {
        var normalizedActor = NormalizeActor(actor);
        return new WorkflowRunControlPermissionsSnapshot
        {
            Actor = normalizedActor,
            Permissions = _permissionsByActor.GetValueOrDefault(normalizedActor) ?? []
        };
    }

    public void Demand(string actor, string permission)
    {
        var snapshot = GetPermissions(actor);
        if (!snapshot.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase))
        {
            throw new WorkflowRunControlForbiddenException(
                $"Operator '{snapshot.Actor}' does not have '{permission}' permission.");
        }
    }

    private static string NormalizeActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An operator identity is required.", nameof(actor));
        var normalized = actor.Trim();
        if (normalized.Length > 256)
            throw new ArgumentException("The operator identity cannot exceed 256 characters.", nameof(actor));
        return normalized;
    }
}

public sealed class WorkflowRunControlForbiddenException(string message) : Exception(message);

public sealed class WorkflowRunControlConflictException(string message) : Exception(message);
