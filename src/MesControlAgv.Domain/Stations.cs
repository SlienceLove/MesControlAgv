namespace MesControlAgv.Domain;

public static class Stations
{
    private static readonly IReadOnlyDictionary<int, Station> Catalog = new Dictionary<int, Station>
    {
        [0] = new(0, "充电桩", "CHARGE_01", true),
        [1] = new(1, "耗材位", "PICK_01", true),
        [2] = new(2, "样品位", "SAMPLE_01", true),
        [3] = new(3, "开盖分液工作站", "ST_OPEN_01", true),
        [4] = new(4, "液体前处理工作站", "ST_PREP_01", true),
        [5] = new(5, "自动进样器", "ST_INJECT_01", true),
        [6] = new(6, "样品回收位", "DROP_01", true)
    };

    private static readonly IReadOnlyCollection<Station> AllStations = Catalog.Values.ToArray();

    public static IReadOnlyCollection<Station> All => AllStations;

    public static Station Get(int code) => Catalog.TryGetValue(code, out var station)
        ? station
        : throw new KeyNotFoundException($"Unknown station code: {code}.");
}
