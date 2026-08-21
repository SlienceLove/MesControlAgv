using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Mes.Services;

public sealed record ConfiguredExperimentResource
{
    public ExperimentResourceReference Resource { get; init; } = new();
    public string ResourceKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public int Capacity { get; init; } = 1;
    public IReadOnlyList<string> CapabilityIds { get; init; } = Array.Empty<string>();

    public bool Provides(string capabilityId) => CapabilityIds.Contains(
        capabilityId,
        StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Profile-backed planning identities. It contains no endpoints, protocol
/// fields, commands, online probes, or device side effects.
/// </summary>
public sealed class ExperimentResourceCatalog
{
    private readonly IReadOnlyDictionary<string, ConfiguredExperimentResource> _resources;

    public ExperimentResourceCatalog(ProfileConfiguration profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var resources = new List<ConfiguredExperimentResource>();

        resources.AddRange((profile.Agvs ?? []).Select(agv => Create(
            ExperimentResourceTypeIds.Agv,
            agv.AgvId,
            agv.AgvId,
            agv.Enabled,
            1,
            [WorkflowCapabilityIds.AgvNavigateToStation])));
        resources.AddRange((profile.Stations ?? []).Select(station => Create(
            ExperimentResourceTypeIds.Station,
            station.StationId,
            string.IsNullOrWhiteSpace(station.Name) ? station.StationId : station.Name,
            station.Enabled,
            Math.Max(1, station.Capacity),
            [])));
        resources.AddRange((profile.WorkflowDevices ?? []).Select(device => Create(
            MapDeviceResourceType(device.DeviceFamily),
            device.DeviceId,
            device.DeviceId,
            device.Enabled,
            1,
            device.CapabilityIds ?? [])));

        var result = new Dictionary<string, ConfiguredExperimentResource>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            if (!result.TryAdd(resource.ResourceKey, resource))
            {
                throw new ArgumentException(
                    $"Experiment resource '{resource.Resource.ResourceType}/{resource.Resource.ResourceId}' is duplicated by the active Profile.",
                    nameof(profile));
            }
        }

        _resources = result;
        Resources = result.Values
            .OrderBy(resource => resource.Resource.ResourceType, StringComparer.Ordinal)
            .ThenBy(resource => resource.Resource.ResourceId, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<ConfiguredExperimentResource> Resources { get; }

    public bool TryGet(
        ExperimentResourceReference reference,
        out ConfiguredExperimentResource? resource)
    {
        resource = null;
        if (string.IsNullOrWhiteSpace(reference.ResourceType) ||
            string.IsNullOrWhiteSpace(reference.ResourceId))
        {
            return false;
        }

        return _resources.TryGetValue(
            ExperimentResourceKeys.Create(reference.ResourceType, reference.ResourceId),
            out resource);
    }

    private static ConfiguredExperimentResource Create(
        string resourceType,
        string resourceId,
        string displayName,
        bool enabled,
        int capacity,
        IEnumerable<string> capabilityIds)
    {
        var reference = new ExperimentResourceReference
        {
            ResourceType = resourceType,
            ResourceId = resourceId
        };
        return new ConfiguredExperimentResource
        {
            Resource = reference,
            ResourceKey = ExperimentResourceKeys.Create(resourceType, resourceId),
            DisplayName = displayName,
            Enabled = enabled,
            Capacity = capacity,
            CapabilityIds = capabilityIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static string MapDeviceResourceType(string deviceFamily)
    {
        if (string.Equals(deviceFamily, WorkflowDeviceFamilyIds.Agv, StringComparison.OrdinalIgnoreCase))
        {
            return ExperimentResourceTypeIds.Agv;
        }
        if (string.Equals(
                deviceFamily,
                WorkflowDeviceFamilyIds.IonChromatography,
                StringComparison.OrdinalIgnoreCase))
        {
            return ExperimentResourceTypeIds.Instrument;
        }
        if (deviceFamily.Contains("robot", StringComparison.OrdinalIgnoreCase) ||
            deviceFamily.Contains("arm", StringComparison.OrdinalIgnoreCase))
        {
            return ExperimentResourceTypeIds.RobotArm;
        }

        return ExperimentResourceTypeIds.Workstation;
    }
}
