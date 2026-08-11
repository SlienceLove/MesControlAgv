using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class ControlCenterCommandCoordinatorTests
{
    [Fact]
    public async Task Create_task_forwards_the_complete_operator_request()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "Created", 0, null);
        var client = new FakeMesClient([task]);
        var coordinator = new ControlCenterCommandCoordinator(client);

        var result = await coordinator.CreateTaskAsync(
            2,
            4,
            7,
            "sample transfer",
            "external-1",
            CancellationToken.None);

        Assert.Equal(task.Id, result.Id);
        Assert.Equal((2, 4, 7, "sample transfer", "external-1"), client.LastCreateRequest);
    }

    [Fact]
    public async Task Simulator_arrival_updates_the_simulator_and_MES_arrival()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "MovingToPickup", 0, null);
        var client = new FakeMesClient([task]);
        var simulator = new RecordingSimulatorControlClient();
        var coordinator = new ControlCenterCommandCoordinator(client, simulator);

        await coordinator.MarkSimulatorArrivalAsync(task.Id, movingToDropoff: false, CancellationToken.None);

        Assert.Equal(1, simulator.TaskControlCallCount);
        Assert.Equal(1, client.MarkArrivedCallCount);
    }

    [Fact]
    public async Task Agv_command_fails_closed_for_missing_or_failed_results()
    {
        var taskId = Guid.NewGuid();
        var client = new FakeMesClient([new DashboardTask(taskId, 2, 4, "MovingToPickup", 0, null)]);
        var coordinator = new ControlCenterCommandCoordinator(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteAgvCommandAsync("AGV-01", "pause", taskId, CancellationToken.None));

        client.CommandResult = new AgvCommandResult(taskId, taskId.ToString("N"), "SAMPLE_01", "failed", "blocked");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteAgvCommandAsync("AGV-01", "pause", taskId, CancellationToken.None));

        Assert.Contains("blocked", exception.Message, StringComparison.Ordinal);
    }
}
