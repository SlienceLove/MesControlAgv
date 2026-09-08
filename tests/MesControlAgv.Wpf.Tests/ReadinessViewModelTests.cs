using MesControlAgv.Contracts;
using MesControlAgv.Domain.Map;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class ReadinessViewModelTests
{
    [Fact]
    public async Task Refresh_aggregates_profile_live_gate_and_actual_execution_path()
    {
        var client = new FakeMesClient([])
        {
            MapSnapshot = new DashboardMapSnapshot(
                [new DashboardStation(2, "Sample", "SAMPLE_CUSTOM", true)],
                [new MapEdgeResponse("SAMPLE_CUSTOM", "DROP_CUSTOM", 12.5, false)],
                "product-1",
                "profile-7",
                "guangzhou606",
                "2026.08",
                "e1b8d6b2b24362c1d44f1884c0abd8fb"),
            PhysicalPreflight = new PhysicalAgvPreflightResponse(
                new AgvSnapshotResponse(true, "adapter", "SAMPLE_CUSTOM", null, "AGV-01"),
                new AgvSafetyReadinessResponse(
                    "unknown", null, "guangzhou606", "816e68b9a367d9c8d5eaee9331a7ef58", null, 0,
                    true, false, false, false, 0, 0, 1, 0.98, DateTimeOffset.UtcNow),
                false,
                ["manual_block_enabled", "automatic_mode_unknown"])
        };
        var readiness = new ReadinessViewModel(client);

        await readiness.RefreshAsync();
        readiness.UpdateFleet([
            new AgvFleetDashboardStatus(
                new AgvDashboardSnapshot(true, "adapter", "SAMPLE_CUSTOM", Guid.NewGuid(), "AGV-02"),
                new AgvActiveTaskStatus(
                    Guid.NewGuid(), Guid.NewGuid(), "MovingToPickup", "device-2", "moving", "DROP_CUSTOM", null,
                    ["SAMPLE_CUSTOM", "PREP_CUSTOM", "DROP_CUSTOM"]))
        ]);

        Assert.Equal("配置地图：guangzhou606 / 2026.08 / e1b8d6b2b24362c1d44f1884c0abd8fb", readiness.ProfileFingerprint);
        Assert.Equal("实时地图：guangzhou606 / 816e68b9a367d9c8d5eaee9331a7ef58", readiness.LiveFingerprint);
        Assert.Equal("未知", readiness.OperatingMode);
        Assert.False(readiness.DispatchPermitted);
        Assert.Contains("manual_block_enabled", readiness.BlockingReasons, StringComparison.Ordinal);
        Assert.Contains("SAMPLE_CUSTOM -> DROP_CUSTOM", readiness.MapEdgeSummary, StringComparison.Ordinal);
        Assert.Contains("AGV-02: SAMPLE_CUSTOM -> PREP_CUSTOM -> DROP_CUSTOM", readiness.ActualExecutionPath, StringComparison.Ordinal);
        Assert.Contains("只读快照已接收", readiness.Status, StringComparison.Ordinal);
        Assert.Equal(OfflineDataStateKind.Ready, readiness.OfflineState.Kind);
    }

    [Fact]
    public async Task Refresh_failure_is_visible_and_does_not_permit_dispatch()
    {
        var client = new FakeMesClient([]) { ReadinessException = new InvalidOperationException("MES unavailable") };
        var readiness = new ReadinessViewModel(client);

        await readiness.RefreshAsync();

        Assert.False(readiness.DispatchPermitted);
        Assert.Equal("配置地图：未知", readiness.ProfileFingerprint);
        Assert.Contains("就绪状态刷新失败：MES unavailable", readiness.Status, StringComparison.Ordinal);
        Assert.Contains("物理预检尚未加载", readiness.BlockingReasons, StringComparison.Ordinal);
        Assert.Equal(OfflineDataStateKind.Error, readiness.OfflineState.Kind);
    }

    [Fact]
    public async Task Supervisor_epoch_and_reauthorization_are_visible_and_block_dispatch()
    {
        var client = new FakeMesClient([])
        {
            MapSnapshot = new DashboardMapSnapshot([], [], null, null, null, null, null),
            PhysicalPreflight = new PhysicalAgvPreflightResponse(
                new AgvSnapshotResponse(true, "adapter", "LM1", null, "AGV-01"),
                null,
                true,
                []),
            PhysicalReadiness = new PhysicalReadinessResponse
            {
                Enabled = true,
                SupervisorInstanceId = "supervisor-1",
                SchedulingPermitted = false,
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Devices =
                [
                    new PhysicalDeviceReadinessSnapshot
                    {
                        DeviceId = "AGV-01",
                        DeviceFamily = "agv",
                        State = PhysicalDeviceReadinessState.Ready,
                        DeviceEpoch = 7,
                        Online = true,
                        ProbeSucceeded = true,
                        RequiresReauthorization = true
                    }
                ],
                BlockingReasons = ["AGV-01:device_reauthorization_required"]
            }
        };
        var readiness = new ReadinessViewModel(client);

        await readiness.RefreshAsync();

        Assert.False(readiness.DispatchPermitted);
        Assert.False(readiness.PhysicalSchedulingPermitted);
        Assert.Equal(7, Assert.Single(readiness.DeviceReadiness).DeviceEpoch);
        Assert.Contains("device_reauthorization_required", readiness.SupervisorBlockingReasons);
        Assert.Contains("不可调度", readiness.SupervisorStatus);
    }

    [Fact]
    public async Task Mes_failure_keeps_local_map_available_for_offline_workflow_editing()
    {
        var client = new FakeMesClient([]) { ReadinessException = new HttpRequestException("MES offline") };
        var mapping = new StationMappingConfig([
            new StationMappingEntry("LM1", "LM1", "充电原点"),
            new StationMappingEntry("LM2", "LM4", null)
        ]);
        var readiness = new ReadinessViewModel(
            client,
            new StubMapLayoutSource(new MapLayoutResult(
                Layout(),
                mapping,
                Loaded: true,
                Error: null,
                MatchingIdentity())));

        await readiness.RefreshAsync();

        Assert.Equal(OfflineDataStateKind.Ready, readiness.OfflineState.Kind);
        Assert.Contains("本地地图", readiness.Status, StringComparison.Ordinal);
        Assert.Equal(["LM1", "LM4"], readiness.LocalStationCatalog.Select(station => station.AgvStationId));
        var edge = Assert.Single(readiness.LocalMapEdges);
        Assert.Equal("LM1", edge.From);
        Assert.Equal("LM4", edge.To);
    }

    [Fact]
    public async Task Mismatched_smap_keeps_static_geometry_but_blocks_runtime_overlays()
    {
        var client = new FakeMesClient([]) { MapSnapshot = MatchingSnapshot() };
        var identity = MatchingIdentity() with { Md5 = "different-md5" };
        var readiness = new ReadinessViewModel(
            client,
            new StubMapLayoutSource(new MapLayoutResult(
                Layout(),
                StationMappingConfig.Empty,
                Loaded: true,
                Error: null,
                identity)));

        await readiness.RefreshAsync();
        readiness.UpdateFleet([MovingFleetStatus()]);

        Assert.Equal("不一致，已禁用运行叠加", readiness.MapLayoutVerificationStatus);
        Assert.Contains("地图 MD5不一致", readiness.MapLayoutVerificationDetails, StringComparison.Ordinal);
        Assert.False(readiness.IsMapLayoutVerified);
        Assert.Equal(2, readiness.Map.Nodes.Count);
        Assert.Single(readiness.Map.Agvs);
        Assert.Empty(readiness.Map.VisualAgvs);
        Assert.Empty(readiness.Map.AgvPathSegments);
        Assert.False(readiness.Map.Layers.RuntimeOverlayAllowed);
        Assert.False(readiness.Map.Layers.ShowRuntimeOverlays);
    }

    [Fact]
    public async Task Matching_smap_allows_independent_runtime_geometry_overlays()
    {
        var client = new FakeMesClient([]) { MapSnapshot = MatchingSnapshot() };
        var readiness = new ReadinessViewModel(
            client,
            new StubMapLayoutSource(new MapLayoutResult(
                Layout(),
                StationMappingConfig.Empty,
                Loaded: true,
                Error: null,
                MatchingIdentity())));

        await readiness.RefreshAsync();
        readiness.UpdateFleet([MovingFleetStatus()]);

        Assert.Equal("已验证一致", readiness.MapLayoutVerificationStatus);
        Assert.True(readiness.IsMapLayoutVerified);
        Assert.Single(readiness.Map.Agvs);
        Assert.Single(readiness.Map.VisualAgvs);
        Assert.Single(readiness.Map.AgvPathSegments);
        Assert.True(readiness.Map.Layers.RuntimeOverlayAllowed);
    }

    private static MapLayout Layout() => new(
        new SmapHeader("2D-Map", "test-map", new MapPoint(0, 0), new MapPoint(10, 10), 0.02, "1.0.6"),
        [new MapStationLayout("LM1", new MapPoint(0, 0)), new MapStationLayout("LM2", new MapPoint(10, 10))],
        [new MapRouteLayout(
            "LM1-LM2",
            "LM1",
            "LM2",
            new MapPoint(0, 0),
            new MapPoint(10, 10),
            new MapPoint(2, 2),
            new MapPoint(8, 8),
            0)],
        new MapWallLayout([]),
        []);

    private static SmapMapIdentity MatchingIdentity() => new(
        "test-map",
        "1.0.6",
        "abc123",
        ["LM1", "LM2"],
        [new SmapDirectedEdge("LM1", "LM2")]);

    private static DashboardMapSnapshot MatchingSnapshot() => new(
        [new DashboardStation(1, "LM1", "LM1", true), new DashboardStation(2, "LM2", "LM2", true)],
        [new MapEdgeResponse("LM1", "LM2", 1, false)],
        "product",
        "profile",
        "test-map",
        "1.0.6",
        "abc123");

    private static AgvFleetDashboardStatus MovingFleetStatus() => new(
        new AgvDashboardSnapshot(true, "adapter", "LM1", Guid.NewGuid(), "AGV-01"),
        new AgvActiveTaskStatus(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "MovingToPickup",
            "device-1",
            "moving",
            "LM2",
            null,
            ["LM1", "LM2"]));

    private sealed class StubMapLayoutSource(MapLayoutResult result) : IMapLayoutSource
    {
        public Task<MapLayoutResult> LoadAsync(CancellationToken ct = default) => Task.FromResult(result);
    }
}
