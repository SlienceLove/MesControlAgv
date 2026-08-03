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
}
