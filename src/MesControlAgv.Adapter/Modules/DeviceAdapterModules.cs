using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Adapter.Modules;

public enum DeviceTransportKind
{
    Simulator,
    Tcp,
    Http,
    Serial
}

public sealed record DeviceAdapterModuleDescriptor(
    string ModuleId,
    string DeviceType,
    IReadOnlyList<DeviceTransportKind> SupportedTransports);

public sealed record DeviceAdapterModuleContext(
    IConfiguration Configuration,
    string ConnectionString,
    string? SimulatorBaseUrl = null);

public sealed record DeviceAdapterRegistration(
    string DeviceId,
    string DeviceType,
    string ModuleId,
    string DriverId,
    DeviceTransportKind Transport,
    bool Enabled,
    bool ControlEnabled);

/// <summary>
/// A device-family module owns its typed drivers, persistence initialization,
/// and normalized Adapter routes. Transport details remain inside the module.
/// </summary>
public interface IDeviceAdapterModule
{
    DeviceAdapterModuleDescriptor Descriptor { get; }

    IReadOnlyList<DeviceAdapterRegistration> GetDevices(DeviceAdapterModuleContext context) => [];

    void AddServices(IServiceCollection services, DeviceAdapterModuleContext context);

    Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}

public sealed class DeviceAdapterModuleCatalog
{
    private readonly IReadOnlyList<IDeviceAdapterModule> _modules;

    public DeviceAdapterModuleCatalog(IEnumerable<IDeviceAdapterModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var registered = new Dictionary<string, IDeviceAdapterModule>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            ArgumentNullException.ThrowIfNull(module);
            ValidateDescriptor(module.Descriptor);
            if (!registered.TryAdd(module.Descriptor.ModuleId, module))
            {
                throw new InvalidOperationException(
                    $"Device Adapter module '{module.Descriptor.ModuleId}' is already registered.");
            }
        }

        _modules = registered.Values
            .OrderBy(module => module.Descriptor.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<DeviceAdapterModuleDescriptor> Descriptors =>
        _modules.Select(module => module.Descriptor).ToArray();

    public bool Contains(string moduleId) =>
        !string.IsNullOrWhiteSpace(moduleId) &&
        _modules.Any(module => string.Equals(
            module.Descriptor.ModuleId,
            moduleId,
            StringComparison.OrdinalIgnoreCase));

    public DeviceAdapterRegistry CreateDeviceRegistry(DeviceAdapterModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new DeviceAdapterRegistry(_modules.SelectMany(module => module.GetDevices(context)));
    }

    public void AddServices(IServiceCollection services, DeviceAdapterModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        foreach (var module in _modules)
        {
            module.AddServices(services, context);
        }
    }

    public async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var module in _modules)
        {
            await module.InitializeAsync(services, cancellationToken);
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        foreach (var module in _modules)
        {
            module.MapEndpoints(endpoints);
        }
    }

    private static void ValidateDescriptor(DeviceAdapterModuleDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.ModuleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.DeviceType);
        if (descriptor.SupportedTransports is null || descriptor.SupportedTransports.Count == 0)
        {
            throw new ArgumentException(
                $"Device Adapter module '{descriptor.ModuleId}' must declare at least one transport.",
                nameof(descriptor));
        }

        if (descriptor.SupportedTransports.Distinct().Count() != descriptor.SupportedTransports.Count)
        {
            throw new ArgumentException(
                $"Device Adapter module '{descriptor.ModuleId}' declares duplicate transports.",
                nameof(descriptor));
        }
    }
}

public sealed class DeviceAdapterRegistry
{
    private readonly IReadOnlyDictionary<string, DeviceAdapterRegistration> _devices;

    public DeviceAdapterRegistry(IEnumerable<DeviceAdapterRegistration> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var registered = new Dictionary<string, DeviceAdapterRegistration>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            Validate(device);
            if (!registered.TryAdd(device.DeviceId, device))
            {
                throw new InvalidOperationException(
                    $"Adapter device '{device.DeviceId}' is already registered.");
            }
        }

        _devices = registered;
    }

    public IReadOnlyList<DeviceAdapterRegistration> Devices => _devices.Values
        .OrderBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public DeviceAdapterRegistration? Find(string deviceId) =>
        string.IsNullOrWhiteSpace(deviceId) ? null : _devices.GetValueOrDefault(deviceId);

    public DeviceAdapterRegistration GetRequired(string deviceId) =>
        Find(deviceId) ?? throw new KeyNotFoundException($"Adapter device '{deviceId}' is not registered.");

    private static void Validate(DeviceAdapterRegistration device)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(device.DeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(device.DeviceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(device.ModuleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(device.DriverId);
        if (device.ControlEnabled && !device.Enabled)
        {
            throw new ArgumentException(
                $"Adapter device '{device.DeviceId}' cannot enable control while the device is disabled.",
                nameof(device));
        }
    }
}

public sealed class DeviceOperationPolicy(DeviceAdapterRegistry devices)
{
    public DeviceAdapterRegistration EnsureReadEnabled(string deviceId)
    {
        var device = devices.GetRequired(deviceId);
        if (!device.Enabled)
        {
            throw new DeviceDisabledException(device.DeviceId);
        }

        return device;
    }

    public DeviceAdapterRegistration EnsureControlEnabled(string deviceId)
    {
        var device = EnsureReadEnabled(deviceId);
        if (!device.ControlEnabled)
        {
            throw new DeviceControlDisabledException(device.DeviceId);
        }

        return device;
    }
}

public sealed class DeviceDisabledException(string deviceId)
    : InvalidOperationException($"Adapter device '{deviceId}' is disabled.");

public sealed class DeviceControlDisabledException(string deviceId)
    : InvalidOperationException($"Control is disabled for Adapter device '{deviceId}'.");
