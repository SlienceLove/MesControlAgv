using System.Collections.ObjectModel;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Domain.Workflows;

public sealed record WorkflowStationAvailability(string StationId, bool Enabled);

public sealed record WorkflowDeviceAvailability
{
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceFamily { get; init; } = string.Empty;
    public IReadOnlyList<string> CapabilityIds { get; init; } = Array.Empty<string>();
    public bool Enabled { get; init; }
    public bool ControlEnabled { get; init; }

    public bool Provides(string capabilityId) => CapabilityIds.Contains(
        capabilityId,
        StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Immutable, static deployment facts used by the publication gate. Online state,
/// occupancy, controller readiness, and port ownership remain runtime admission facts.
/// </summary>
public sealed class WorkflowPublicationContext
{
    private readonly IReadOnlyDictionary<string, WorkflowStationAvailability> _stations;
    private readonly IReadOnlyDictionary<string, WorkflowDeviceAvailability> _devices;

    private WorkflowPublicationContext(
        string productId,
        string profileVersion,
        IEnumerable<WorkflowStationAvailability> stations,
        IEnumerable<WorkflowDeviceAvailability> devices)
    {
        ProductId = RequireText(productId, nameof(productId));
        ProfileVersion = RequireText(profileVersion, nameof(profileVersion));
        _stations = FreezeUnique(stations, station => station.StationId, "station");
        _devices = FreezeUnique(devices, device => device.DeviceId, "device");
        Stations = Array.AsReadOnly(_stations.Values.OrderBy(value => value.StationId, StringComparer.Ordinal).ToArray());
        Devices = Array.AsReadOnly(_devices.Values.OrderBy(value => value.DeviceId, StringComparer.Ordinal).ToArray());
    }

    public string ProductId { get; }
    public string ProfileVersion { get; }
    public IReadOnlyList<WorkflowStationAvailability> Stations { get; }
    public IReadOnlyList<WorkflowDeviceAvailability> Devices { get; }

    public bool TryGetStation(string stationId, out WorkflowStationAvailability? station)
    {
        station = null;
        return !string.IsNullOrWhiteSpace(stationId) && _stations.TryGetValue(stationId, out station);
    }

    public bool TryGetDevice(string deviceId, out WorkflowDeviceAvailability? device)
    {
        device = null;
        return !string.IsNullOrWhiteSpace(deviceId) && _devices.TryGetValue(deviceId, out device);
    }

    public IReadOnlyList<WorkflowDeviceAvailability> GetDevices(string deviceFamily) => Devices
        .Where(device => string.Equals(device.DeviceFamily, deviceFamily, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    public static WorkflowPublicationContext FromProfile(ProfileConfiguration profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(profile.Product);

        var agvs = (profile.Agvs ?? []).Select(agv => new WorkflowDeviceAvailability
        {
            DeviceId = agv.AgvId,
            DeviceFamily = WorkflowDeviceFamilyIds.Agv,
            CapabilityIds = [WorkflowCapabilityIds.AgvNavigateToStation],
            Enabled = agv.Enabled,
            ControlEnabled = true
        });
        var configuredDevices = (profile.WorkflowDevices ?? []).Select(device => new WorkflowDeviceAvailability
        {
            DeviceId = device.DeviceId,
            DeviceFamily = device.DeviceFamily,
            CapabilityIds = Array.AsReadOnly((device.CapabilityIds ?? []).ToArray()),
            Enabled = device.Enabled,
            ControlEnabled = device.ControlEnabled
        });

        return new WorkflowPublicationContext(
            profile.Product.ProductId,
            profile.Product.Version,
            (profile.Stations ?? []).Select(station => new WorkflowStationAvailability(
                station.StationId,
                station.Enabled)),
            agvs.Concat(configuredDevices));
    }

    private static IReadOnlyDictionary<string, T> FreezeUnique<T>(
        IEnumerable<T> values,
        Func<T, string> keySelector,
        string label)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var key = RequireText(keySelector(value), label);
            if (!result.TryAdd(key, value))
                throw new ArgumentException($"Workflow publication {label} '{key}' is duplicated.", nameof(values));
        }

        return new ReadOnlyDictionary<string, T>(result);
    }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();
}
