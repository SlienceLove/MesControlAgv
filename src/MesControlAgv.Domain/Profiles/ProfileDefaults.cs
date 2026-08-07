namespace MesControlAgv.Domain.Profiles;

internal static class ProfileDefaults
{
    public static ProductProfile Product { get; } = new()
    {
        ProductId = "MES-AGV",
        DisplayName = "AGV MES",
        Version = "1.0"
    };

    public static IReadOnlyList<StationProfile> StationProfiles { get; } =
    [
        new() { Code = 0, StationId = "CHARGE_01", AgvStationId = "CHARGE_01", Name = "充电桩", Type = "Charge" },
        new() { Code = 1, StationId = "PICK_01", AgvStationId = "PICK_01", Name = "耗材位", Type = "Pickup" },
        new() { Code = 2, StationId = "SAMPLE_01", AgvStationId = "SAMPLE_01", Name = "样品位", Type = "Sample" },
        new() { Code = 3, StationId = "ST_OPEN_01", AgvStationId = "ST_OPEN_01", Name = "开盖分液工作站", Type = "Open" },
        new() { Code = 4, StationId = "ST_PREP_01", AgvStationId = "ST_PREP_01", Name = "液体前处理工作站", Type = "Preparation" },
        new() { Code = 5, StationId = "ST_INJECT_01", AgvStationId = "ST_INJECT_01", Name = "自动进样器", Type = "Injection" },
        new() { Code = 6, StationId = "DROP_01", AgvStationId = "DROP_01", Name = "样品回收位", Type = "Dropoff" }
    ];

    public static IReadOnlyList<AgvProfile> AgvProfiles { get; } =
    [
        new()
        {
            AgvId = "AGV-01",
            Model = "Simulator",
            Driver = "simulator",
            Endpoint = "http://localhost:5183/",
            MaxLoadKg = 200,
            MaxSpeedMetersPerSecond = 1.5,
            HomeStationId = "CHARGE_01"
        }
    ];

    public static MapProfile Map { get; } = new()
    {
        StationIds = ["CHARGE_01", "PICK_01", "SAMPLE_01", "ST_OPEN_01", "ST_PREP_01", "ST_INJECT_01", "DROP_01"],
        Edges =
        [
            new() { From = "CHARGE_01", To = "PICK_01", Cost = 1 },
            new() { From = "PICK_01", To = "SAMPLE_01", Cost = 1 },
            new() { From = "SAMPLE_01", To = "ST_OPEN_01", Cost = 1 },
            new() { From = "ST_OPEN_01", To = "ST_PREP_01", Cost = 1 },
            new() { From = "ST_PREP_01", To = "ST_INJECT_01", Cost = 1 },
            new() { From = "ST_INJECT_01", To = "DROP_01", Cost = 1 },
            new() { From = "SAMPLE_01", To = "ST_PREP_01", Cost = 2 }
        ]
    };

    public static FeatureFlags Features { get; } = new() { UseSimulator = true };
    public static TimeoutOptions Timeouts { get; } = new();

    public static ProfileConfiguration CreateConfiguration() => new()
    {
        Product = Product,
        Agvs = AgvProfiles.ToArray(),
        Stations = StationProfiles.ToArray(),
        Map = Map,
        Features = Features,
        Timeouts = Timeouts
    };
}
