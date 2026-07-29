using System.Net;
using System.Net.Http.Json;
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
    public async Task Create_task_only_accepts_sample_to_prep_route()
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", new
        {
            sourceStationCode = 1,
            targetStationCode = 4
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Create_task_persists_created_task_and_audit_event()
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", new
        {
            sourceStationCode = 2,
            targetStationCode = 4
        });

        response.EnsureSuccessStatusCode();
        var task = await response.Content.ReadFromJsonAsync<TaskResponse>();

        Assert.NotNull(task);
        Assert.Equal("MovingToPickup", task.Status);

        var detail = await _client.GetFromJsonAsync<TaskDetailResponse>($"/api/tasks/{task.Id}");
        Assert.NotNull(detail);
        Assert.Contains(detail.Events, taskEvent => taskEvent.EventType == "TaskCreated");
        Assert.Contains(detail.Events, taskEvent => taskEvent.EventType == "PickupMoveStarted");
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

public sealed record TaskResponse(Guid Id, int SourceStationCode, int TargetStationCode, string Status, int RetryCount, string? LastError);
public sealed record TaskDetailResponse(TaskResponse Task, List<TaskEventResponse> Events);
public sealed record TaskEventResponse(string EventType);
public sealed record StationResponse(int Code, string Name, string AgvStationId, bool Enabled);
