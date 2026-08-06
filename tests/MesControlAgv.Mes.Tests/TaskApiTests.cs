using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Domain;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MesControlAgv.Mes.Tests;

public sealed class TaskApiTests : IClassFixture<MesWebApplicationFactory>
{
    private readonly HttpClient _client;

    public TaskApiTests(MesWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Create_task_rejects_station_outside_active_profile()
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", new
        {
            sourceStationCode = 999,
            targetStationCode = 4
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Create_task_persists_a_pending_task_without_dispatching_an_agv()
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", new
        {
            sourceStationCode = 2,
            targetStationCode = 4
        });

        response.EnsureSuccessStatusCode();
        var task = await response.Content.ReadFromJsonAsync<TaskResponse>();

        Assert.NotNull(task);
        Assert.Equal("Created", task.Status);
        Assert.True(task.CreatedAt > DateTime.UtcNow.AddMinutes(-1));
        Assert.Null(task.EndedAt);

        var detail = await _client.GetFromJsonAsync<TaskDetailResponse>($"/api/tasks/{task.Id}");
        Assert.NotNull(detail);
        Assert.Contains(detail.Events, taskEvent => taskEvent.EventType == "TaskCreated");
        Assert.DoesNotContain(detail.Events, taskEvent => taskEvent.EventType == "DispatchRequested");
    }

    [Fact]
    public async Task Dispatch_task_starts_a_previously_created_task()
    {
        var create = await _client.PostAsJsonAsync("/api/tasks", new
        {
            sourceStationCode = 2,
            targetStationCode = 4
        });
        var created = await create.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(created);

        var dispatch = await _client.PostAsync($"/api/tasks/{created.Id}/dispatch", null);
        dispatch.EnsureSuccessStatusCode();
        var dispatched = await dispatch.Content.ReadFromJsonAsync<TaskResponse>();

        Assert.NotNull(dispatched);
        Assert.Equal("MovingToPickup", dispatched.Status);
    }

    [Fact]
    public async Task Dispatch_task_rejects_a_task_that_is_not_pending()
    {
        var create = await _client.PostAsJsonAsync("/api/tasks", new
        {
            sourceStationCode = 2,
            targetStationCode = 4
        });
        var created = await create.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(created);
        var first = await _client.PostAsync($"/api/tasks/{created.Id}/dispatch", null);
        first.EnsureSuccessStatusCode();

        var second = await _client.PostAsync($"/api/tasks/{created.Id}/dispatch", null);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Fleet_execution_status_correlates_the_agv_with_its_active_mes_task()
    {
        var create = await _client.PostAsJsonAsync("/api/tasks", new
        {
            sourceStationCode = 2,
            targetStationCode = 4
        });
        var created = await create.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(created);
        var dispatch = await _client.PostAsync($"/api/tasks/{created.Id}/dispatch", null);
        dispatch.EnsureSuccessStatusCode();

        var fleet = await _client.GetFromJsonAsync<List<AgvFleetStatusResponse>>("/api/agvs/fleet/status");
        var status = Assert.Single(fleet!);

        Assert.True(status.Snapshot.Online);
        Assert.Equal("adapter", status.Snapshot.ControlOwner);
        Assert.NotNull(status.ActiveTask);
        Assert.Equal(created.Id, status.ActiveTask.TransportTaskId);
        Assert.Equal("MovingToPickup", status.ActiveTask.MesStatus);
        Assert.Equal("moving", status.ActiveTask.DeviceState);
        Assert.Equal(TransportOperationIds.Pickup(created.Id), status.ActiveTask.OperationId);
    }

    [Fact]
    public async Task Cancel_pending_task_is_handled_by_mes_without_an_adapter_operation()
    {
        var create = await _client.PostAsJsonAsync("/api/tasks", new
        {
            sourceStationCode = 2,
            targetStationCode = 4
        });
        var created = await create.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(created);

        var cancel = await _client.PostAsJsonAsync($"/api/tasks/{created.Id}/cancel", new { operatorName = "operator-a" });
        cancel.EnsureSuccessStatusCode();
        var cancelled = await cancel.Content.ReadFromJsonAsync<TaskResponse>();

        Assert.NotNull(cancelled);
        Assert.Equal("Cancelled", cancelled.Status);
        var detail = await _client.GetFromJsonAsync<TaskDetailResponse>($"/api/tasks/{created.Id}");
        Assert.Contains(detail!.Events, taskEvent => taskEvent.EventType == "CancelConfirmed");
    }

    [Fact]
    public async Task List_tasks_filters_by_the_requested_utc_date_and_defaults_to_today()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/tasks", new
        {
            sourceStationCode = 2,
            targetStationCode = 4
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(created);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var todayTasks = await _client.GetFromJsonAsync<List<TaskResponse>>("/api/tasks");
        var filteredTasks = await _client.GetFromJsonAsync<List<TaskResponse>>($"/api/tasks?date={today:yyyy-MM-dd}");
        var oldTasks = await _client.GetFromJsonAsync<List<TaskResponse>>("/api/tasks?date=2000-01-01");

        Assert.Contains(todayTasks!, task => task.Id == created.Id);
        Assert.Contains(filteredTasks!, task => task.Id == created.Id);
        Assert.DoesNotContain(oldTasks!, task => task.Id == created.Id);
    }

    [Fact]
    public async Task Stations_endpoint_returns_the_fixed_station_catalog()
    {
        var stations = await _client.GetFromJsonAsync<List<StationResponse>>("/api/stations");

        Assert.NotNull(stations);
        Assert.Equal(7, stations.Count);
        Assert.Equal("SAMPLE_01", stations.Single(station => station.Code == 2).AgvStationId);
    }
}

public sealed record TaskResponse(Guid Id, int SourceStationCode, int TargetStationCode, string Status, int RetryCount, string? LastError, DateTime CreatedAt, DateTime? EndedAt);
public sealed record TaskDetailResponse(TaskResponse Task, List<TaskEventResponse> Events);
public sealed record TaskEventResponse(string EventType);
public sealed record StationResponse(int Code, string Name, string AgvStationId, bool Enabled);
