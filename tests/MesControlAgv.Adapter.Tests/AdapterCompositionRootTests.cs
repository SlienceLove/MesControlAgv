using System.Text;
using MesControlAgv.Adapter.Drivers;
using MesControlAgv.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Adapter.Tests;

public sealed class AdapterCompositionRootTests
{
    [Fact]
    public void Valid_physical_profile_registers_the_vendor_tcp_driver_without_connecting()
    {
        using var provider = AddServices(CreatePhysicalConfiguration()).BuildServiceProvider();

        Assert.IsType<VendorTcpDriver>(provider.GetRequiredService<IAgvDriver>());
    }

    [Theory]
    [InlineData("Agv:Driver", "simulator", "Agv:Driver=vendor-tcp")]
    [InlineData("Agv:Tcp:NickName", "another-owner", "Agv:Tcp:NickName")]
    [InlineData("Agv:Tcp:AcquireControl", "false", "Agv:Tcp:AcquireControl=true")]
    public void Physical_profile_rejects_invalid_control_configuration(
        string key,
        string value,
        string expectedMessage)
    {
        var configuration = CreatePhysicalConfiguration();
        configuration[key] = value;

        var exception = Assert.Throws<InvalidOperationException>(() => AddServices(configuration));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Physical_profile_rejects_a_confidence_threshold_below_the_approved_value()
    {
        var configuration = CreatePhysicalConfiguration();
        configuration["Agv:Tcp:MinimumConfidence"] = "0.97";

        var exception = Assert.Throws<InvalidOperationException>(() => AddServices(configuration));

        Assert.Contains("MinimumConfidence", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Physical_profile_rejects_automatic_dispatch_without_live_map_verification()
    {
        var configuration = CreatePhysicalConfiguration();
        configuration["Profile:Features:EnableAutomaticDispatch"] = "true";

        var exception = Assert.Throws<InvalidOperationException>(() => AddServices(configuration));

        Assert.Contains("live controller map verification", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_profile_remains_compatible_with_the_simulator_driver()
    {
        var configuration = new ConfigurationBuilder().Build();
        using var provider = AddServices(configuration).BuildServiceProvider();

        Assert.IsType<SimulatorDriver>(provider.GetRequiredService<IAgvDriver>());
    }

    private static IServiceCollection AddServices(IConfiguration configuration) =>
        new ServiceCollection().AddServices(configuration, "Data Source=adapter-composition-root-tests.db");

    private static IConfigurationRoot CreatePhysicalConfiguration() =>
        new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""
            {
              "Profile": {
                "product": { "productId": "MES-AGV", "displayName": "AGV MES", "version": "1.0" },
                "agvs": [{ "agvId": "AGV-01", "model": "Vendor-AMR", "driver": "vendor-tcp", "endpoint": "tcp://agv-controller.example.invalid:19206", "maxLoadKg": 200, "maxSpeedMetersPerSecond": 1.5, "homeStationId": "LM1" }],
                "stations": [
                  { "code": 1, "stationId": "LM1", "agvStationId": "LM1", "name": "LM1", "type": "Station" },
                  { "code": 2, "stationId": "LM2", "agvStationId": "LM2", "name": "LM2", "type": "Station" }
                ],
                "map": { "stationIds": ["LM1", "LM2"], "edges": [{ "from": "LM1", "to": "LM2", "cost": 1, "bidirectional": false }] },
                "physicalAcceptance": {
                  "expectedControlOwner": "MesControlAgv.Adapter",
                  "mapSnapshot": {
                    "mapName": "acceptance-map", "version": "1.0", "md5": "e1b8d6b2b24362c1d44f1884c0abd8fb", "capturedAtUtc": "2026-08-05T00:00:00+00:00",
                    "stationIds": ["LM1", "LM2"], "directedEdges": [{ "from": "LM1", "to": "LM2" }]
                  },
                  "safety": {
                    "minimumLocalizationConfidence": 0.98, "maximumDispatchSpeedMetersPerSecond": 0.3,
                    "requireControlOwnership": true, "requireNoEmergency": true, "requireNoBlocked": true, "requireNoFaults": true, "requireAutomaticMode": true
                  }
                },
                "features": { "useSimulator": false, "enableAutomaticDispatch": false },
                "timeouts": { "connectionTimeout": "00:00:10", "dispatchTimeout": "00:00:30", "commandTimeout": "00:00:30", "taskCompletionTimeout": "00:05:00", "taskPollingInterval": "00:00:02" }
              },
              "Agv": {
                "Driver": "vendor-tcp",
                "Tcp": { "NickName": "MesControlAgv.Adapter", "AcquireControl": true, "MinimumConfidence": 0.98 }
              }
            }
            """)))
            .Build();
}
