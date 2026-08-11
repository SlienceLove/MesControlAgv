using MesControlAgv.Application;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Mes.Services;

public sealed class ActiveProfileWorkflowAdmissionPolicy(ProfileConfiguration profile)
    : IWorkflowRuntimeAdmissionPolicy
{
    public const string StationUnavailableCode = "WF018";

    private readonly HashSet<string> _enabledStationIds = (profile.Stations ?? [])
        .Where(station => station.Enabled && !string.IsNullOrWhiteSpace(station.StationId))
        .Select(station => station.StationId)
        .ToHashSet(StringComparer.Ordinal);

    public IReadOnlyList<WorkflowValidationIssue> Validate(WorkflowVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return (version.Definition.Nodes ?? [])
            .Where(node => node.Type is WorkflowNodeType.Move or WorkflowNodeType.Pickup or WorkflowNodeType.Dropoff)
            .Where(node => !string.IsNullOrWhiteSpace(node.TargetStation))
            .Where(node => !_enabledStationIds.Contains(node.TargetStation!))
            .Select(node => new WorkflowValidationIssue
            {
                Code = StationUnavailableCode,
                Message = $"Target station '{node.TargetStation}' is not enabled by the active profile.",
                Severity = WorkflowValidationSeverity.Error,
                NodeId = node.Id
            })
            .ToArray();
    }
}
