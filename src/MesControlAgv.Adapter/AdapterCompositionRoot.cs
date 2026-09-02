using MesControlAgv.Adapter.Modules;
using MesControlAgv.Adapter.Modules.AuboArm;
using MesControlAgv.Adapter.Modules.SampleWorkstation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Adapter;

public static class AdapterCompositionRoot
{
    public static IServiceCollection AddServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString,
        string? simulatorBaseUrl = null,
        DeviceAdapterModuleCatalog? moduleCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var catalog = moduleCatalog ?? CreateDefaultModuleCatalog();
        var context = new DeviceAdapterModuleContext(configuration, connectionString, simulatorBaseUrl);
        services.AddSingleton(catalog);
        services.AddSingleton(catalog.CreateDeviceRegistry(context));
        services.AddSingleton<DeviceOperationPolicy>();
        catalog.AddServices(services, context);
        return services;
    }

    public static DeviceAdapterModuleCatalog CreateDefaultModuleCatalog() =>
        new([new AgvAdapterModule(), new SampleWorkstationAdapterModule(), new AuboArmAdapterModule()]);
}
