using MesControlAgv.Simulator;

namespace MesControlAgv.Simulator.Tests;

public class SimulatorStateTests
{
    [Fact]
    public void Timeout_keeps_accepted_navigation_queryable_for_reconciliation()
    {
        var state = new SimulatorState();
        var taskId = Guid.NewGuid();
        state.ApplyControl("timeout");

        Assert.Throws<TimeoutException>(() => state.Navigate(taskId, "SAMPLE_01"));

        var task = state.GetTask(taskId);
        Assert.NotNull(task);
        Assert.Equal("moving", task.State);
        Assert.Equal(taskId, state.CurrentTaskId);
    }

    [Fact]
    public void Cancelling_a_moving_task_stops_it_from_being_current()
    {
        var state = new SimulatorState();
        var taskId = Guid.NewGuid();
        state.Navigate(taskId, "SAMPLE_01");

        var cancelled = state.Cancel(taskId);

        Assert.NotNull(cancelled);
        Assert.Equal("cancelled", cancelled.State);
        Assert.Equal("cancelled", state.GetTask(taskId)!.State);
        Assert.Null(state.CurrentTaskId);
    }

    [Fact]
    public void Multiple_agvs_keep_independent_tasks_and_accept_planned_paths()
    {
        var state = new SimulatorState(["AGV-01", "AGV-02"]);
        var firstTaskId = Guid.NewGuid();
        var secondTaskId = Guid.NewGuid();
        var path = new[] { "CHARGE_01", "PICK_01", "SAMPLE_01" };

        var first = state.Navigate("AGV-01", firstTaskId, "SAMPLE_01", "SAMPLE_01", path);
        var second = state.Navigate("AGV-02", secondTaskId, "SAMPLE_01", "SAMPLE_01", path);

        Assert.Equal("AGV-01", first.AgvId);
        Assert.Equal("AGV-02", second.AgvId);
        Assert.Equal(firstTaskId, state.GetSnapshot("AGV-01").CurrentTaskId);
        Assert.Equal(secondTaskId, state.GetSnapshot("AGV-02").CurrentTaskId);
        Assert.Equal(path, first.Path);
    }

    [Fact]
    public void Task_specific_arrival_releases_the_assigned_agv()
    {
        var state = new SimulatorState(["AGV-01", "AGV-02"]);
        var taskId = Guid.NewGuid();
        state.Navigate("AGV-02", taskId, "CHARGE_01", "SAMPLE_01", ["CHARGE_01", "PICK_01", "SAMPLE_01"]);

        state.ApplyControl(taskId, "arrive");

        Assert.Null(state.GetSnapshot("AGV-02").CurrentTaskId);
        Assert.Equal("SAMPLE_01", state.GetSnapshot("AGV-02").CurrentStationId);
        Assert.Equal("arrived", state.GetTask("AGV-02", taskId)!.State);
    }
}
