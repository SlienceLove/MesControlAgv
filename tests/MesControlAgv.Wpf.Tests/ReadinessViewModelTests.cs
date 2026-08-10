using MesControlAgv.Contracts;
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

        Assert.Equal("Profile map: guangzhou606 / 2026.08 / e1b8d6b2b24362c1d44f1884c0abd8fb", readiness.ProfileFingerprint);
        Assert.Equal("Live map: guangzhou606 / 816e68b9a367d9c8d5eaee9331a7ef58", readiness.LiveFingerprint);
        Assert.Equal("unknown", readiness.OperatingMode);
        Assert.False(readiness.DispatchPermitted);
        Assert.Contains("manual_block_enabled", readiness.BlockingReasons, StringComparison.Ordinal);
        Assert.Contains("SAMPLE_CUSTOM -> DROP_CUSTOM", readiness.MapEdgeSummary, StringComparison.Ordinal);
        Assert.Contains("AGV-02: SAMPLE_CUSTOM -> PREP_CUSTOM -> DROP_CUSTOM", readiness.ActualExecutionPath, StringComparison.Ordinal);
        Assert.Contains("Read-only snapshot received", readiness.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_failure_is_visible_and_does_not_permit_dispatch()
    {
        var client = new FakeMesClient([]) { ReadinessException = new InvalidOperationException("MES unavailable") };
        var readiness = new ReadinessViewModel(client);

        await readiness.RefreshAsync();

        Assert.False(readiness.DispatchPermitted);
        Assert.Equal("Profile map: unknown", readiness.ProfileFingerprint);
        Assert.Contains("Readiness refresh failed: MES unavailable", readiness.Status, StringComparison.Ordinal);
        Assert.Contains("not been loaded", readiness.BlockingReasons, StringComparison.Ordinal);
    }
}
