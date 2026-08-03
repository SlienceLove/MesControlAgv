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
}
