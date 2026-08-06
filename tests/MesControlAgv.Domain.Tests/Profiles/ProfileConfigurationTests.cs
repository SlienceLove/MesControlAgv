using System.Text;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Domain.Tests.Profiles;

public sealed class ProfileConfigurationTests
{
    [Fact]
    public void Validator_accepts_a_complete_profile_configuration()
    {
        var result = new ProfileConfigurationValidator().Validate(CreateValidConfiguration());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validator_reports_invalid_map_device_timeout_and_feature_configuration()
    {
        var configuration = CreateValidConfiguration() with
        {
            Agvs = [new AgvProfile
            {
                AgvId = "AGV-01",
                Model = "AMR",
                Driver = "",
                Endpoint = "not-a-uri",
                DeviceParameters = new Dictionary<string, string> { ["token"] = "" },
                MaxLoadKg = 0,
                MaxSpeedMetersPerSecond = -1,
                HomeStationId = "UNKNOWN"
            }],
            Map = new MapProfile
            {
                StationIds = ["AGV_ST_01", "AGV_ST_01", "UNKNOWN"],
                Edges = [new() { From = "AGV_ST_01", To = "UNKNOWN", Cost = 0 }]
            },
            Features = null!,
            Timeouts = new TimeoutOptions { DispatchTimeout = TimeSpan.Zero }
        };

        var result = new ProfileConfigurationValidator().Validate(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Path == "agvs[0].driver");
        Assert.Contains(result.Errors, error => error.Path == "agvs[0].endpoint");
        Assert.Contains(result.Errors, error => error.Path == "agvs[0].deviceParameters.token");
        Assert.Contains(result.Errors, error => error.Path == "agvs[0].homeStationId");
        Assert.Contains(result.Errors, error => error.Path == "map.stationIds[1]");
        Assert.Contains(result.Errors, error => error.Path == "map.edges[0].cost");
        Assert.Contains(result.Errors, error => error.Path == "features");
        Assert.Contains(result.Errors, error => error.Path == "timeouts.dispatchTimeout");
    }

    [Fact]
    public async Task Json_loader_supports_an_appsettings_profile_wrapper()
    {
        const string json = """
        {
          "Profile": {
            "product": { "productId": "MES-AGV", "displayName": "AGV MES", "version": "2026.1" },
            "agvs": [{ "agvId": "AGV-01", "model": "Simulator", "driver": "simulator", "endpoint": "http://localhost:5183/", "maxLoadKg": 200, "maxSpeedMetersPerSecond": 1.5, "homeStationId": "SAMPLE_01" }],
            "stations": [
              { "code": 2, "stationId": "SAMPLE_01", "agvStationId": "SAMPLE_01", "name": "Sample", "type": "Sample" },
              { "code": 4, "stationId": "ST_PREP_01", "agvStationId": "ST_PREP_01", "name": "Preparation", "type": "Preparation" }
            ],
            "map": { "stationIds": ["SAMPLE_01", "ST_PREP_01"], "edges": [{ "from": "SAMPLE_01", "to": "ST_PREP_01", "cost": 2 }] },
            "features": { "useSimulator": true },
            "timeouts": { "connectionTimeout": "00:00:10", "dispatchTimeout": "00:00:30", "commandTimeout": "00:00:30", "taskCompletionTimeout": "00:05:00", "taskPollingInterval": "00:00:02" }
          }
        }
        """;

        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = await new JsonProfileConfigurationLoader().LoadAsync(source);

        Assert.Equal("MES-AGV", configuration.Product.ProductId);
        Assert.Equal("SAMPLE_01", configuration.Stations.Single(station => station.Code == 2).AgvStationId);
        Assert.Contains(configuration.Map.Edges, edge => edge.From == "SAMPLE_01" && edge.To == "ST_PREP_01");
        Assert.True(configuration.Features.UseSimulator);
    }

    [Fact]
    public async Task Json_loader_rejects_invalid_profile_documents_with_validation_details()
    {
        const string json = """
        { "product": { "productId": "", "displayName": "", "version": "" }, "agvs": [], "stations": [], "timeouts": { "dispatchTimeout": "00:00:00" } }
        """;

        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var exception = await Assert.ThrowsAsync<ProfileConfigurationValidationException>(
            () => new JsonProfileConfigurationLoader().LoadAsync(source));

        Assert.Contains("product.productId", exception.Message);
        Assert.Contains("agvs", exception.Message);
        Assert.Contains("stations", exception.Message);
        Assert.Contains("map", exception.Message);
        Assert.Contains("features", exception.Message);
        Assert.Contains("timeouts.dispatchTimeout", exception.Message);
    }

    [Fact]
    public void Validator_accepts_a_directed_physical_acceptance_profile()
    {
        var result = new ProfileConfigurationValidator().Validate(CreatePhysicalAcceptanceConfiguration());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validator_rejects_physical_profile_when_snapshot_or_safety_gates_differ()
    {
        var valid = CreatePhysicalAcceptanceConfiguration();
        var physical = valid.PhysicalAcceptance!;
        var configuration = valid with
        {
            Features = valid.Features with { UseSimulator = true },
            Map = valid.Map with
            {
                Edges = valid.Map.Edges.Select((edge, index) => index == 0
                    ? edge with { Bidirectional = true }
                    : edge).ToArray()
            },
            PhysicalAcceptance = physical with
            {
                MapSnapshot = physical.MapSnapshot with
                {
                    Md5 = "bad",
                    DirectedEdges = physical.MapSnapshot.DirectedEdges
                        .Where(edge => !(edge.From == "LM1" && edge.To == "LM5"))
                        .ToArray()
                },
                Safety = physical.Safety with
                {
                    RequireNoEmergency = false,
                    MaximumDispatchSpeedMetersPerSecond = 2
                }
            }
        };

        var result = new ProfileConfigurationValidator().Validate(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Path == "features.useSimulator");
        Assert.Contains(result.Errors, error => error.Path == "map.edges[0].bidirectional");
        Assert.Contains(result.Errors, error => error.Path == "physicalAcceptance.mapSnapshot.md5");
        Assert.Contains(result.Errors, error => error.Path == "physicalAcceptance.mapSnapshot.directedEdges");
        Assert.Contains(result.Errors, error => error.Path == "physicalAcceptance.safety.requireNoEmergency");
        Assert.Contains(result.Errors, error => error.Path == "physicalAcceptance.safety.maximumDispatchSpeedMetersPerSecond");
    }

    [Fact]
    public void Default_profile_remains_simulator_compatible_without_physical_acceptance()
    {
        var configuration = ProfileConfiguration.Default;
        var result = new ProfileConfigurationValidator().Validate(configuration);

        Assert.Null(configuration.PhysicalAcceptance);
        Assert.True(configuration.Features.UseSimulator);
        Assert.Equal("simulator", configuration.Agvs.Single().Driver);
        Assert.True(result.IsValid);
    }

    private static ProfileConfiguration CreatePhysicalAcceptanceConfiguration() => new()
    {
        Product = new ProductProfile { ProductId = "MES-AGV", DisplayName = "AGV MES", Version = "1.0" },
        Agvs =
        [
            new AgvProfile
            {
                AgvId = "AGV-01",
                Model = "Vendor-AMR",
                Driver = "vendor-tcp",
                Endpoint = "tcp://agv-controller.example.invalid:19206",
                MaxLoadKg = 200,
                MaxSpeedMetersPerSecond = 1.5,
                HomeStationId = "LM1"
            }
        ],
        Stations =
        [
            new StationProfile { Code = 1, StationId = "LM1", AgvStationId = "LM1", Name = "LM1", Type = "Station" },
            new StationProfile { Code = 2, StationId = "LM2", AgvStationId = "LM2", Name = "LM2", Type = "Station" },
            new StationProfile { Code = 3, StationId = "LM3", AgvStationId = "LM3", Name = "LM3", Type = "Station" },
            new StationProfile { Code = 4, StationId = "LM4", AgvStationId = "LM4", Name = "LM4", Type = "Station" },
            new StationProfile { Code = 5, StationId = "LM5", AgvStationId = "LM5", Name = "LM5", Type = "Station" }
        ],
        Map = new MapProfile
        {
            StationIds = ["LM1", "LM2", "LM3", "LM4", "LM5"],
            Edges =
            [
                new() { From = "LM1", To = "LM2", Cost = 1, Bidirectional = false },
                new() { From = "LM2", To = "LM3", Cost = 1, Bidirectional = false },
                new() { From = "LM1", To = "LM4", Cost = 1, Bidirectional = false },
                new() { From = "LM4", To = "LM1", Cost = 1, Bidirectional = false },
                new() { From = "LM4", To = "LM5", Cost = 1, Bidirectional = false },
                new() { From = "LM5", To = "LM4", Cost = 1, Bidirectional = false },
                new() { From = "LM1", To = "LM5", Cost = 1, Bidirectional = false }
            ]
        },
        PhysicalAcceptance = new PhysicalAcceptanceProfile
        {
            ExpectedControlOwner = "MesControlAgv.Adapter",
            MapSnapshot = new ControllerMapSnapshot
            {
                MapName = "guangzhou606",
                Version = "1.0.6",
                Md5 = "e1b8d6b2b24362c1d44f1884c0abd8fb",
                CapturedAtUtc = new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero),
                StationIds = ["LM1", "LM2", "LM3", "LM4", "LM5"],
                DirectedEdges =
                [
                    new() { From = "LM1", To = "LM2" },
                    new() { From = "LM2", To = "LM3" },
                    new() { From = "LM1", To = "LM4" },
                    new() { From = "LM4", To = "LM1" },
                    new() { From = "LM4", To = "LM5" },
                    new() { From = "LM5", To = "LM4" },
                    new() { From = "LM1", To = "LM5" }
                ]
            },
            Safety = new PhysicalAgvSafetyProfile
            {
                MinimumLocalizationConfidence = 0.98,
                MaximumDispatchSpeedMetersPerSecond = 0.3,
                RequireControlOwnership = true,
                RequireNoEmergency = true,
                RequireNoBlocked = true,
                RequireNoFaults = true,
                RequireAutomaticMode = true
            }
        },
        Features = new FeatureFlags { UseSimulator = false, EnableAutomaticDispatch = false },
        Timeouts = new TimeoutOptions()
    };

    private static ProfileConfiguration CreateValidConfiguration() => new()
    {
        Product = new ProductProfile { ProductId = "MES-AGV", DisplayName = "AGV MES", Version = "1.0" },
        Agvs = [new AgvProfile { AgvId = "AGV-01", Model = "AMR-100", Driver = "simulator", Endpoint = "http://localhost:5183/", MaxLoadKg = 200, MaxSpeedMetersPerSecond = 1.5, HomeStationId = "ST-01" }],
        Stations =
        [
            new StationProfile { Code = 1, StationId = "ST-01", AgvStationId = "AGV_ST_01", Name = "Input", Type = "Input", Capacity = 2 },
            new StationProfile { Code = 2, StationId = "ST-02", AgvStationId = "AGV_ST_02", Name = "Output", Type = "Output" }
        ],
        Map = new MapProfile { StationIds = ["AGV_ST_01", "AGV_ST_02"], Edges = [new() { From = "AGV_ST_01", To = "AGV_ST_02", Cost = 1 }] },
        Features = new FeatureFlags { UseSimulator = true },
        Timeouts = new TimeoutOptions()
    };
}
