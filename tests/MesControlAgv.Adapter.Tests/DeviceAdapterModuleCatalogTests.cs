using MesControlAgv.Adapter.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Adapter.Tests;

public sealed class DeviceAdapterModuleCatalogTests
{
    [Fact]
    public void Catalog_lookup_is_case_insensitive_and_descriptors_are_stable()
    {
        var catalog = new DeviceAdapterModuleCatalog(
        [
            new FakeModule("vision", "vision", DeviceTransportKind.Http),
            new FakeModule("agv", "agv", DeviceTransportKind.Tcp)
        ]);

        Assert.True(catalog.Contains("AGV"));
        Assert.Equal(["agv", "vision"], catalog.Descriptors.Select(item => item.ModuleId));
    }

    [Fact]
    public void Catalog_rejects_duplicate_module_ids()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new DeviceAdapterModuleCatalog(
            [
                new FakeModule("sample-workstation", "sample-workstation", DeviceTransportKind.Http),
                new FakeModule("SAMPLE-WORKSTATION", "sample-workstation", DeviceTransportKind.Http)
            ]));

        Assert.Contains("already registered", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Catalog_forwards_service_initialization_and_endpoint_lifecycle()
    {
        var first = new FakeModule("agv", "agv", DeviceTransportKind.Tcp);
        var second = new FakeModule("sample-workstation", "sample-workstation", DeviceTransportKind.Http);
        var catalog = new DeviceAdapterModuleCatalog([first, second]);
        var services = new ServiceCollection();
        var context = new DeviceAdapterModuleContext(
            new ConfigurationBuilder().Build(),
            "Data Source=adapter-module-tests.db");

        catalog.AddServices(services, context);
        using var provider = services.BuildServiceProvider();
        await catalog.InitializeAsync(provider, CancellationToken.None);
        catalog.MapEndpoints(new FakeEndpointRouteBuilder(provider));

        Assert.Equal(["agv", "sample-workstation"],
            provider.GetServices<ModuleRegistrationMarker>().Select(item => item.ModuleId));
        Assert.Equal(1, first.InitializeCalls);
        Assert.Equal(1, second.InitializeCalls);
        Assert.Equal(1, first.MapEndpointCalls);
        Assert.Equal(1, second.MapEndpointCalls);
    }

    [Fact]
    public void Device_registry_and_policy_are_case_insensitive_and_fail_closed()
    {
        var registry = new DeviceAdapterRegistry(
        [
            new DeviceAdapterRegistration("AGV-01", "agv", "agv", "simulator", DeviceTransportKind.Simulator, true, true),
            new DeviceAdapterRegistration("WS-01", "sample-workstation", "sample-workstation", "vendor-http-read-only", DeviceTransportKind.Http, true, false),
            new DeviceAdapterRegistration("WS-02", "sample-workstation", "sample-workstation", "vendor-http-read-only", DeviceTransportKind.Http, false, false)
        ]);
        var policy = new DeviceOperationPolicy(registry);

        Assert.Equal("AGV-01", policy.EnsureControlEnabled("agv-01").DeviceId);
        Assert.Equal("WS-01", policy.EnsureReadEnabled("ws-01").DeviceId);
        Assert.Throws<DeviceControlDisabledException>(() => policy.EnsureControlEnabled("WS-01"));
        Assert.Throws<DeviceDisabledException>(() => policy.EnsureReadEnabled("WS-02"));
        Assert.Throws<KeyNotFoundException>(() => policy.EnsureReadEnabled("missing"));
    }

    private sealed class FakeModule(
        string moduleId,
        string deviceType,
        params DeviceTransportKind[] transports) : IDeviceAdapterModule
    {
        public DeviceAdapterModuleDescriptor Descriptor { get; } =
            new(moduleId, deviceType, transports);

        public int InitializeCalls { get; private set; }
        public int MapEndpointCalls { get; private set; }

        public void AddServices(IServiceCollection services, DeviceAdapterModuleContext context) =>
            services.AddSingleton(new ModuleRegistrationMarker(Descriptor.ModuleId));

        public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            InitializeCalls++;
            return Task.CompletedTask;
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints) => MapEndpointCalls++;
    }

    private sealed record ModuleRegistrationMarker(string ModuleId);

    private sealed class FakeEndpointRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }
}
