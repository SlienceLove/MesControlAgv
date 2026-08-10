using MesControlAgv.Contracts;
using MesControlAgv.Domain.Map;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class MapLayoutIdentityVerifierTests
{
    [Fact]
    public void Matches_after_mapping_station_marks_and_expanding_bidirectional_edges()
    {
        var identity = Identity(
            ["LM1", "LM2"],
            [new SmapDirectedEdge("LM1", "LM2"), new SmapDirectedEdge("LM2", "LM1")]);
        var mapping = new StationMappingConfig(
        [
            new StationMappingEntry("LM1", "MES-A", null),
            new StationMappingEntry("LM2", "MES-B", null)
        ]);
        var snapshot = Snapshot(
            [new DashboardStation(1, "A", "MES-A", true), new DashboardStation(2, "B", "MES-B", true)],
            [new MapEdgeResponse("MES-A", "MES-B", 1, true)]);

        var result = MapLayoutIdentityVerifier.Verify(identity, mapping, snapshot);

        Assert.Equal(MapIdentityVerificationStatus.Match, result.Status);
        Assert.Single(result.Reasons);
    }

    [Fact]
    public void Reports_all_known_metadata_station_and_directed_edge_mismatches()
    {
        var identity = new SmapMapIdentity(
            "wrong-map",
            "wrong-version",
            "wrong-md5",
            ["A", "EXTRA"],
            [new SmapDirectedEdge("A", "EXTRA")]);
        var snapshot = Snapshot(
            [new DashboardStation(1, "A", "A", true), new DashboardStation(2, "B", "B", true)],
            [new MapEdgeResponse("A", "B", 1, false)]);

        var result = MapLayoutIdentityVerifier.Verify(identity, StationMappingConfig.Empty, snapshot);

        Assert.Equal(MapIdentityVerificationStatus.Mismatch, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("地图名称不一致", StringComparison.Ordinal));
        Assert.Contains(result.Reasons, reason => reason.Contains("地图版本不一致", StringComparison.Ordinal));
        Assert.Contains(result.Reasons, reason => reason.Contains("地图 MD5不一致", StringComparison.Ordinal));
        Assert.Contains(result.Reasons, reason => reason.Contains("站点集合不一致", StringComparison.Ordinal));
        Assert.Contains(result.Reasons, reason => reason.Contains("有向边集合不一致", StringComparison.Ordinal));
    }

    [Fact]
    public void Is_unverifiable_when_required_profile_metadata_is_missing()
    {
        var snapshot = Snapshot(
            [new DashboardStation(1, "A", "A", true), new DashboardStation(2, "B", "B", true)],
            [new MapEdgeResponse("A", "B", 1, false)]) with
        {
            ProfileMapMd5 = null
        };

        var result = MapLayoutIdentityVerifier.Verify(
            Identity(["A", "B"], [new SmapDirectedEdge("A", "B")]),
            StationMappingConfig.Empty,
            snapshot);

        Assert.Equal(MapIdentityVerificationStatus.Unverifiable, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("无法验证地图 MD5", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false, "已加载的 .smap 身份")]
    [InlineData(false, true, "MES 地图快照")]
    public void Is_unverifiable_when_either_input_is_unavailable(
        bool missingIdentity,
        bool missingSnapshot,
        string expectedReason)
    {
        var result = MapLayoutIdentityVerifier.Verify(
            missingIdentity ? null : Identity(["A", "B"], [new SmapDirectedEdge("A", "B")]),
            StationMappingConfig.Empty,
            missingSnapshot
                ? null
                : Snapshot(
                    [new DashboardStation(1, "A", "A", true), new DashboardStation(2, "B", "B", true)],
                    [new MapEdgeResponse("A", "B", 1, false)]));

        Assert.Equal(MapIdentityVerificationStatus.Unverifiable, result.Status);
        Assert.Contains(expectedReason, result.Reasons.Single(), StringComparison.Ordinal);
    }

    private static SmapMapIdentity Identity(
        IReadOnlyList<string> stations,
        IReadOnlyList<SmapDirectedEdge> edges) =>
        new("test-map", "1.0.6", "abc123", stations, edges);

    private static DashboardMapSnapshot Snapshot(
        IReadOnlyList<DashboardStation> stations,
        IReadOnlyList<MapEdgeResponse> edges) =>
        new(stations, edges, "product", "profile", "test-map", "1.0.6", "abc123");
}
