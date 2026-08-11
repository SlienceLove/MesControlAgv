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
    public void Read_only_preflight_mode_requires_explicitly_disabled_mutations()
    {
        var configuration = CreatePhysicalConfiguration();
        configuration["Adapter:RunMode"] = AdapterRunMode.ReadOnlyPreflightValue;
        configuration["Agv:Tcp:AcquireControl"] = "false";
        configuration["Agv:Tcp:EnablePush"] = "false";
        configuration["Profile:Features:EnableTaskCancellation"] = "false";

        using var provider = AddServices(configuration).BuildServiceProvider();

        Assert.Same(AdapterRunMode.ReadOnlyPreflight, provider.GetRequiredService<AdapterRunMode>());
    }

    [Theory]
    [InlineData("Agv:Tcp:AcquireControl", "true", "AcquireControl=false")]
    [InlineData("Agv:Tcp:EnablePush", "true", "EnablePush=false")]
    [InlineData("Profile:Features:EnableTaskCancellation", "true", "task cancellation")]
    public void Read_only_preflight_mode_rejects_mutating_options(
        string key,
        string value,
        string expectedMessage)
    {
        var configuration = CreatePhysicalConfiguration();
        configuration["Adapter:RunMode"] = AdapterRunMode.ReadOnlyPreflightValue;
        configuration["Agv:Tcp:AcquireControl"] = "false";
        configuration["Agv:Tcp:EnablePush"] = "false";
        configuration["Profile:Features:EnableTaskCancellation"] = "false";
        configuration[key] = value;

        var exception = Assert.Throws<InvalidOperationException>(() => AddServices(configuration));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_profile_remains_compatible_with_the_simulator_driver()
    {
        var configuration = new ConfigurationBuilder().Build();
        using var provider = AddServices(configuration).BuildServiceProvider();

        Assert.IsType<SimulatorDriver>(provider.GetRequiredService<IAgvDriver>());
    }

    [Fact]
    public void Physical_environment_replaces_default_json_arrays_instead_of_merging_them()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"adapter-physical-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, PhysicalAcceptanceConfiguration.FileName),
                """
                {
                  "Profile": {
                    "stations": [
                      { "stationId": "LM1" },
                      { "stationId": "LM2" }
                    ]
                  }
                }
                """);
            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Profile:stations:0:stationId"] = "SIM1",
                ["Profile:stations:1:stationId"] = "SIM2",
                ["Profile:stations:2:stationId"] = "SIM3"
            });

            PhysicalAcceptanceConfiguration.ReplaceDefaultSources(
                configuration,
                directory,
                ["--Adapter:RunMode=read-only-preflight"]);

            var stations = configuration.GetSection("Profile:stations")
                .GetChildren()
                .Select(section => section["stationId"])
                .ToArray();
            Assert.Equal(["LM1", "LM2"], stations);
            Assert.Null(configuration["Profile:stations:2:stationId"]);
            Assert.Equal("read-only-preflight", configuration["Adapter:RunMode"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
