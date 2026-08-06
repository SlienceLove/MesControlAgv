using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class TaskCreationViewModelTests
{
    [Fact]
    public async Task Refresh_loads_enabled_stations_for_dynamic_task_configuration()
    {
        var client = CreateClient();
        using var viewModel = new MainViewModel(client);

        await viewModel.RefreshAsync();

        Assert.Equal(1, client.GetStationsCallCount);
        Assert.Equal(client.Stations, viewModel.AvailableStations);
        Assert.Null(viewModel.NewTaskSourceStation);
        Assert.Null(viewModel.NewTaskTargetStation);
    }

    [Fact]
    public async Task Plan_route_uses_selected_station_ids_and_exposes_preview()
    {
        var client = CreateClient();
        client.PlannedPath = new DashboardPlannedPath(["SAMPLE_01", "ST_OPEN_01", "ST_PREP_01"], 2);
        using var viewModel = new MainViewModel(client);
        await viewModel.RefreshAsync();

        viewModel.NewTaskSourceStation = client.Stations.Single(station => station.Code == 2);
        viewModel.NewTaskTargetStation = client.Stations.Single(station => station.Code == 4);
        viewModel.PlanRouteCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.RoutePreview.Contains("ST_PREP_01", StringComparison.Ordinal));
        Assert.Equal(("SAMPLE_01", "ST_PREP_01", (IReadOnlyCollection<string>?)null), client.LastPlanRequest);
        Assert.Contains("SAMPLE_01", viewModel.RoutePreview, StringComparison.Ordinal);
        Assert.Contains("ST_OPEN_01", viewModel.RoutePreview, StringComparison.Ordinal);
        Assert.Contains("2", viewModel.RoutePreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_task_sends_configured_source_target_and_metadata()
    {
        var client = CreateClient();
        using var viewModel = new MainViewModel(client);
        await viewModel.RefreshAsync();
        viewModel.NewTaskSourceStation = client.Stations.Single(station => station.Code == 2);
        viewModel.NewTaskTargetStation = client.Stations.Single(station => station.Code == 4);
        viewModel.NewTaskPriority = 8;
        viewModel.NewTaskDescription = "Urgent preparation transfer";
        viewModel.NewTaskExternalId = "WPF-ORDER-42";

        viewModel.CreateTaskCommand.Execute(null);

        await WaitUntilAsync(() => client.LastCreateRequest is not null);
        Assert.Equal((2, 4, 8, "Urgent preparation transfer", "WPF-ORDER-42"), client.LastCreateRequest);
    }

    [Fact]
    public async Task Create_task_is_disabled_when_no_stations_are_available()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "MovingToPickup", 0, null);
        var client = new FakeMesClient([task]);
        using var viewModel = new MainViewModel(client);
        await viewModel.RefreshAsync();

        Assert.Empty(viewModel.AvailableStations);
        Assert.False(viewModel.CreateTaskCommand.CanExecute(null));
        viewModel.CreateTaskCommand.Execute(null);
        Assert.Null(client.LastCreateRequest);
    }

    private static FakeMesClient CreateClient()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "MovingToPickup", 0, null);
        var client = new FakeMesClient([task]);
        client.SetStations(
            new DashboardStation(2, "Sample", "SAMPLE_01", true),
            new DashboardStation(4, "Preparation", "ST_PREP_01", true));
        return client;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("The expected asynchronous command did not complete.");
    }
}
