using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class MapAnimationCoordinatorTests
{
    [Fact]
    public void Sync_selects_segment_after_current_station()
    {
        var coordinator = new MapAnimationCoordinator();
        var overlay = MovingOverlay("AGV-01", "B", ["A", "B", "C"], 10, 0);

        coordinator.Sync(
            [overlay],
            [Line("AGV-01", 0, 0, 10, 0), Line("AGV-01", 10, 0, 30, 0)],
            Operations(("AGV-01", "operation-1")));
        var poses = coordinator.Tick(TimeSpan.FromSeconds(0.8));

        Assert.Equal(20, poses["agv-01"].X, 6);
        Assert.Equal(0, poses["AGV-01"].Y, 6);
        Assert.Equal(0, poses["AGV-01"].HeadingRadians, 6);
        Assert.True(poses["AGV-01"].IsAnimating);
    }

    [Fact]
    public void Tick_completes_one_segment_and_never_loops()
    {
        var coordinator = new MapAnimationCoordinator(TimeSpan.FromSeconds(1.6));
        SyncMoving(coordinator, "operation-1");

        var completed = coordinator.Tick(TimeSpan.FromSeconds(1.6))["AGV-01"];
        var muchLater = coordinator.Tick(TimeSpan.FromMinutes(2))["AGV-01"];

        Assert.Equal(10, completed.X, 6);
        Assert.False(completed.IsAnimating);
        Assert.Equal(completed, muchLater);
    }

    [Fact]
    public void Sync_with_same_signature_preserves_progress_but_operation_change_restarts()
    {
        var coordinator = new MapAnimationCoordinator();
        SyncMoving(coordinator, "operation-1");
        Assert.Equal(5, coordinator.Tick(TimeSpan.FromSeconds(0.8))["AGV-01"].X, 6);

        var unchanged = SyncMoving(coordinator, "operation-1")["AGV-01"];
        var restarted = SyncMoving(coordinator, "operation-2")["AGV-01"];

        Assert.Equal(5, unchanged.X, 6);
        Assert.Equal(0, restarted.X, 6);
    }

    [Fact]
    public void Path_or_current_station_change_restarts_on_the_new_relevant_segment()
    {
        var coordinator = new MapAnimationCoordinator();
        var segments = new[]
        {
            Line("AGV-01", 0, 0, 10, 0),
            Line("AGV-01", 10, 0, 20, 0)
        };
        coordinator.Sync(
            [MovingOverlay("AGV-01", "A", ["A", "B", "C"], 0, 0)],
            segments,
            Operations(("AGV-01", "operation-1")));
        coordinator.Tick(TimeSpan.FromSeconds(0.8));

        var nextStation = coordinator.Sync(
            [MovingOverlay("AGV-01", "B", ["A", "B", "C"], 10, 0)],
            segments,
            Operations(("AGV-01", "operation-1")))["AGV-01"];
        var changedPath = coordinator.Sync(
            [MovingOverlay("AGV-01", "B", ["A", "B", "D"], 10, 0)],
            segments,
            Operations(("AGV-01", "operation-1")))["AGV-01"];

        Assert.Equal(10, nextStation.X, 6);
        Assert.Equal(10, changedPath.X, 6);
        Assert.True(changedPath.IsAnimating);
    }

    [Fact]
    public void Multiple_agvs_keep_independent_progress_and_restarts()
    {
        var coordinator = new MapAnimationCoordinator();
        var overlays = new[]
        {
            MovingOverlay("AGV-01", "A", ["A", "B"], 0, 0),
            MovingOverlay("AGV-02", "X", ["X", "Y"], 100, 0)
        };
        var segments = new[]
        {
            Line("AGV-01", 0, 0, 10, 0),
            Line("AGV-02", 100, 0, 120, 0)
        };
        coordinator.Sync(
            overlays,
            segments,
            Operations(("AGV-01", "operation-1"), ("AGV-02", "operation-2")));
        coordinator.Tick(TimeSpan.FromSeconds(0.8));

        var poses = coordinator.Sync(
            overlays,
            segments,
            Operations(("AGV-01", "replacement"), ("AGV-02", "operation-2")));

        Assert.Equal(0, poses["AGV-01"].X, 6);
        Assert.Equal(110, poses["AGV-02"].X, 6);
    }

    [Theory]
    [InlineData(false, "MovingToTarget", "moving", "operation-1")]
    [InlineData(true, "Paused", "paused", "operation-1")]
    [InlineData(true, "MovingToTarget", "accepted", "operation-1")]
    [InlineData(true, "unexpected", "moving", "operation-1")]
    [InlineData(true, "MovingToTarget", "moving", null)]
    public void Offline_nonmoving_unknown_or_operationless_agv_holds_current_node(
        bool online,
        string mesStatus,
        string deviceState,
        string? operation)
    {
        var coordinator = new MapAnimationCoordinator();
        var overlay = new MapAgvOverlayViewModel(
            "AGV-01",
            "A",
            mesStatus,
            deviceState,
            ["A", "B"],
            3,
            4,
            online);
        coordinator.Sync(
            [overlay],
            [Line("AGV-01", 0, 0, 10, 0)],
            Operations(("AGV-01", operation)));

        var pose = coordinator.Tick(TimeSpan.FromSeconds(5))["AGV-01"];

        Assert.Equal((3d, 4d, false), (pose.X, pose.Y, pose.IsAnimating));
        Assert.Equal(0, pose.HeadingRadians);
    }

    [Theory]
    [InlineData("Z", new[] { "A", "B" })]
    [InlineData("B", new[] { "A", "B" })]
    [InlineData("A", new[] { "A", "B", "A", "C" })]
    public void Missing_final_or_ambiguous_current_segment_holds_current_node(
        string currentStation,
        string[] path)
    {
        var coordinator = new MapAnimationCoordinator();
        var overlay = MovingOverlay("AGV-01", currentStation, path, 7, 8);
        coordinator.Sync(
            [overlay],
            [Line("AGV-01", 0, 0, 10, 0), Line("AGV-01", 10, 0, 20, 0), Line("AGV-01", 20, 0, 30, 0)],
            Operations(("AGV-01", "operation-1")));

        var pose = coordinator.Tick(TimeSpan.FromSeconds(1))["AGV-01"];

        Assert.Equal((7d, 8d, false), (pose.X, pose.Y, pose.IsAnimating));
    }

    [Fact]
    public void Nonmoving_refresh_pauses_without_resetting_the_signature_progress()
    {
        var coordinator = new MapAnimationCoordinator();
        SyncMoving(coordinator, "operation-1");
        coordinator.Tick(TimeSpan.FromSeconds(0.8));
        var paused = new MapAgvOverlayViewModel(
            "AGV-01", "A", "Paused", "paused", ["A", "B"], 2, 3, true);

        var pausedPose = coordinator.Sync(
            [paused],
            [Line("AGV-01", 0, 0, 10, 0)],
            Operations(("AGV-01", "operation-1")))["AGV-01"];
        coordinator.Tick(TimeSpan.FromSeconds(1));
        var resumedPose = SyncMoving(coordinator, "operation-1")["AGV-01"];

        Assert.Equal((2d, 3d, false), (pausedPose.X, pausedPose.Y, pausedPose.IsAnimating));
        Assert.Equal(5, resumedPose.X, 6);
    }

    [Fact]
    public void Tick_returns_bezier_position_and_heading()
    {
        var coordinator = new MapAnimationCoordinator();
        var curve = new MapPathSegmentViewModel("AGV-01", 0, 0, 10, 0, 0, 10, 10, 10);
        coordinator.Sync(
            [MovingOverlay("AGV-01", "A", ["A", "B"], 0, 0)],
            [curve],
            Operations(("AGV-01", "operation-1")));

        var midpoint = coordinator.Tick(TimeSpan.FromSeconds(0.8))["AGV-01"];
        var endpoint = coordinator.Tick(TimeSpan.FromSeconds(0.8))["AGV-01"];

        Assert.Equal(5, midpoint.X, 6);
        Assert.Equal(7.5, midpoint.Y, 6);
        Assert.Equal(0, midpoint.HeadingRadians, 6);
        Assert.Equal(-Math.PI / 2, endpoint.HeadingRadians, 6);
    }

    [Fact]
    public void Sync_prunes_removed_agvs()
    {
        var coordinator = new MapAnimationCoordinator();
        coordinator.Sync(
            [
                MovingOverlay("AGV-01", "A", ["A", "B"], 0, 0),
                MovingOverlay("AGV-02", "A", ["A", "B"], 0, 0)
            ],
            [Line("AGV-01", 0, 0, 10, 0), Line("AGV-02", 0, 0, 10, 0)],
            Operations(("AGV-01", "operation-1"), ("AGV-02", "operation-2")));

        var poses = coordinator.Sync(
            [MovingOverlay("AGV-02", "A", ["A", "B"], 0, 0)],
            [Line("AGV-02", 0, 0, 10, 0)],
            Operations(("AGV-02", "operation-2")));

        Assert.False(poses.ContainsKey("AGV-01"));
        Assert.True(poses.ContainsKey("AGV-02"));
    }

    private static IReadOnlyDictionary<string, MapAgvAnimationPose> SyncMoving(
        MapAnimationCoordinator coordinator,
        string operation) =>
        coordinator.Sync(
            [MovingOverlay("AGV-01", "A", ["A", "B"], 0, 0)],
            [Line("AGV-01", 0, 0, 10, 0)],
            Operations(("AGV-01", operation)));

    private static MapAgvOverlayViewModel MovingOverlay(
        string agvId,
        string currentStation,
        IReadOnlyList<string> path,
        double x,
        double y) =>
        new(agvId, currentStation, "MovingToTarget", "moving", path, x, y, true);

    private static MapPathSegmentViewModel Line(
        string agvId,
        double x1,
        double y1,
        double x2,
        double y2) =>
        new(agvId, x1, y1, x2, y2);

    private static IReadOnlyDictionary<string, string?> Operations(
        params (string AgvId, string? Operation)[] values) =>
        values.ToDictionary(value => value.AgvId, value => value.Operation, StringComparer.OrdinalIgnoreCase);
}
