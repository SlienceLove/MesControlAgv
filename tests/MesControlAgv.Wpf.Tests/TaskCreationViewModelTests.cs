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
    public async Task Plan_route_rejects_a_response_with_mismatched_station_metadata()
    {
        var client = CreateClient();
        client.PlannedPath = new DashboardPlannedPath(
            ["SAMPLE_01", "ST_PREP_01"],
            1,
            "OTHER_START",
            "ST_PREP_01");
        using var viewModel = new MainViewModel(client);
        await viewModel.RefreshAsync();
        viewModel.NewTaskSourceStation = client.Stations.Single(station => station.Code == 2);
        viewModel.NewTaskTargetStation = client.Stations.Single(station => station.Code == 4);

        viewModel.PlanRouteCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.RoutePreview.Contains("不一致", StringComparison.Ordinal));
        Assert.Null(viewModel.PlannedRoute);
        Assert.False(viewModel.CreateTaskCommand.CanExecute(null));
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

        Assert.False(viewModel.CreateTaskCommand.CanExecute(null));
        viewModel.PlanRouteCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.PlannedRoute is not null);
        Assert.True(viewModel.CreateTaskCommand.CanExecute(null));
        viewModel.CreateTaskCommand.Execute(null);

        await WaitUntilAsync(() => client.LastCreateRequest is not null);
        Assert.Equal((2, 4, 8, "Urgent preparation transfer", "WPF-ORDER-42"), client.LastCreateRequest);
    }

    [Fact]
    public async Task Profile_station_catalog_change_invalidates_an_existing_route_preview()
    {
        var client = CreateClient();
        using var viewModel = new MainViewModel(client);
        await viewModel.RefreshAsync();
        viewModel.NewTaskSourceStation = client.Stations.Single(station => station.Code == 2);
        viewModel.NewTaskTargetStation = client.Stations.Single(station => station.Code == 4);
        viewModel.PlanRouteCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.PlannedRoute is not null);

        client.SetStations(
            new DashboardStation(2, "Sample", "SAMPLE_RELOADED", true),
            new DashboardStation(4, "Preparation", "ST_PREP_RELOADED", true));
        await viewModel.RefreshAsync();

        Assert.Null(viewModel.PlannedRoute);
        Assert.Contains("预览", viewModel.RoutePreview, StringComparison.Ordinal);
        Assert.False(viewModel.CreateTaskCommand.CanExecute(null));
    }

    [Fact]
    public async Task Periodic_refresh_preserves_an_existing_route_when_station_catalog_is_unchanged()
    {
        var client = CreateClient();
        using var viewModel = new MainViewModel(client);
        await viewModel.RefreshAsync();
        viewModel.NewTaskSourceStation = client.Stations.Single(station => station.Code == 2);
        viewModel.NewTaskTargetStation = client.Stations.Single(station => station.Code == 4);
        viewModel.PlanRouteCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.PlannedRoute is not null);
        var plannedRoute = viewModel.PlannedRoute;
        var preview = viewModel.RoutePreview;
        var source = viewModel.NewTaskSourceStation;
        var target = viewModel.NewTaskTargetStation;
        var stationRows = viewModel.AvailableStations.ToArray();
        var collectionChanges = 0;
        viewModel.AvailableStations.CollectionChanged += (_, _) => collectionChanges++;
        client.SetStations(
            new DashboardStation(2, "Sample", "SAMPLE_01", true),
            new DashboardStation(4, "Preparation", "ST_PREP_01", true));

        await viewModel.RefreshAsync();

        Assert.Equal(0, collectionChanges);
        Assert.Same(plannedRoute, viewModel.PlannedRoute);
        Assert.Same(source, viewModel.NewTaskSourceStation);
        Assert.Same(target, viewModel.NewTaskTargetStation);
        Assert.Same(stationRows[0], viewModel.AvailableStations[0]);
        Assert.Same(stationRows[1], viewModel.AvailableStations[1]);
        Assert.Equal(preview, viewModel.RoutePreview);
        Assert.Equal(2, viewModel.NewTaskSourceStation?.Code);
        Assert.Equal(4, viewModel.NewTaskTargetStation?.Code);
        Assert.True(viewModel.CreateTaskCommand.CanExecute(null));
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

    [Fact]
    public async Task Task_list_uses_custom_profile_station_names_from_mes_catalog()
    {
        var task = new DashboardTask(Guid.NewGuid(), 101, 202, "Created", 0, null);
        var client = new FakeMesClient([task]);
        client.SetStations(
            new DashboardStation(101, "Custom pickup", "CUSTOM_PICKUP", true),
            new DashboardStation(202, "Custom dropoff", "CUSTOM_DROPOFF", true));
        using var viewModel = new MainViewModel(client);

        await viewModel.RefreshAsync();

        Assert.Equal("Custom pickup", viewModel.SelectedTask?.SourceStationName);
        Assert.Equal("Custom dropoff", viewModel.SelectedTask?.TargetStationName);
        Assert.Equal("Custom pickup -> Custom dropoff", viewModel.SelectedTask?.RouteDescription);
    }

    [Fact]
    public async Task Batch_submission_resolves_custom_station_name_and_agv_station_id_from_mes_catalog()
    {
        var task = new DashboardTask(Guid.NewGuid(), 101, 202, "Created", 0, null);
        var client = new FakeMesClient([task]);
        client.SetStations(
            new DashboardStation(101, "Custom pickup", "CUSTOM_PICKUP", true),
            new DashboardStation(202, "Custom dropoff", "CUSTOM_DROPOFF", true));
        using var viewModel = new MainViewModel(client);
        await viewModel.RefreshAsync();

        var filePath = Path.Combine(Path.GetTempPath(), $"wpf-custom-stations-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(
                filePath,
                "TaskId,SourceStation,TargetStation,Priority\nCUSTOM-01,Custom pickup,CUSTOM_DROPOFF,9\n");

            await viewModel.ImportBatchFileAsync(filePath);
            viewModel.SubmitBatchCommand.Execute(null);

            await WaitUntilAsync(() => client.LastCreateRequest is not null);
            Assert.Equal((101, 202, 9, (string?)null, "CUSTOM-01"), client.LastCreateRequest);
            Assert.Contains("Custom pickup", viewModel.SelectedTask?.RouteDescription, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Created_task_can_be_explicitly_dispatched_from_the_control_center()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "Created", 0, null);
        var client = new FakeMesClient([task]);
        client.SetStations(
            new DashboardStation(2, "Sample", "SAMPLE_01", true),
            new DashboardStation(4, "Preparation", "ST_PREP_01", true));
        using var viewModel = new MainViewModel(client);

        await viewModel.RefreshAsync();

        Assert.True(viewModel.DispatchTaskCommand.CanExecute(null));
        viewModel.DispatchTaskCommand.Execute(null);

        await WaitUntilAsync(() => client.DispatchCallCount == 1);
        Assert.Equal(task.Id, client.LastDispatchTaskId);
    }

    [Fact]
    public async Task Dispatch_refreshes_selected_task_and_reports_execution_assignment()
    {
        var created = new DashboardTask(Guid.NewGuid(), 2, 4, "Created", 0, null);
        var dispatched = created with
        {
            Status = "MovingToPickup",
            ActiveAgvId = "AGV-02",
            ActiveDeviceTaskId = "pickup-device-42",
            ActivePath = ["SAMPLE_01", "ST_OPEN_01", "ST_PREP_01"]
        };
        var client = new FakeMesClient([created]) { DispatchResult = dispatched };
        client.SetStations(
            new DashboardStation(2, "Sample", "SAMPLE_01", true),
            new DashboardStation(4, "Preparation", "ST_PREP_01", true));
        using var viewModel = new MainViewModel(client);

        await viewModel.RefreshAsync();
        viewModel.DispatchTaskCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.SelectedTask?.Status == "MovingToPickup");
        Assert.Equal("AGV-02", viewModel.SelectedTask?.AssignedAgvDescription);
        Assert.Equal("pickup-device-42", viewModel.SelectedTask?.DeviceTaskDescription);
        Assert.Contains("ST_OPEN_01", viewModel.SelectedTask?.CurrentPathDescription);
        Assert.Contains("已派发", viewModel.ActionStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operator_validation_disables_manual_confirmation_and_cancel()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "WaitingPickupConfirmation", 0, null);
        using var viewModel = new MainViewModel(new FakeMesClient([task]));

        await viewModel.RefreshAsync();
        viewModel.OperatorName = "  ";

        Assert.False(viewModel.ConfirmPickupCommand.CanExecute(null));
        Assert.False(viewModel.CancelCommand.CanExecute(null));
        Assert.Contains("操作员", viewModel.OperatorValidationMessage, StringComparison.Ordinal);

        viewModel.OperatorName = "operator-1";
        Assert.True(viewModel.ConfirmPickupCommand.CanExecute(null));
        Assert.True(viewModel.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task Dispatch_failure_remains_visible_after_background_refresh()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "Created", 0, null);
        var client = new FakeMesClient([task])
        {
            DispatchException = new InvalidOperationException("No online idle AGV is available.")
        };
        using var viewModel = new MainViewModel(client);

        await viewModel.RefreshAsync();
        viewModel.DispatchTaskCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Message.Contains("No online", StringComparison.Ordinal));

        Assert.Equal("操作失败", viewModel.ActionStatus);
        await viewModel.RefreshAsync();
        Assert.Contains("No online idle AGV", viewModel.Message, StringComparison.Ordinal);
        Assert.True(viewModel.DispatchTaskCommand.CanExecute(null));
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
