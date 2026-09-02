using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Contracts;

namespace MesControlAgv.Mes.Tests;

public sealed class ShineLabTaskApiTests(MesWebApplicationFactory factory)
    : IClassFixture<MesWebApplicationFactory>
{
    [Fact]
    public async Task Create_is_idempotent_and_task_can_be_queried()
    {
        using var client = factory.CreateClient();
        var taskUuid = $"task-{Guid.NewGuid():N}";
        var request = new ShineLabTaskCreateRequest(
            "SHA18I",
            taskUuid,
            [new ShineLabSampleData("S-01", "标准样", "1", 11, null, "A")],
            "AS18-M01",
            "IC-P01",
            "Normal");

        var firstResponse = await client.PostAsJsonAsync("/api/shinelab/tasks", request);
        var replayResponse = await client.PostAsJsonAsync("/api/shinelab/tasks", request);
        var first = await firstResponse.Content.ReadFromJsonAsync<ShineLabTaskResponse>();
        var replay = await replayResponse.Content.ReadFromJsonAsync<ShineLabTaskResponse>();
        var detail = await client.GetFromJsonAsync<ShineLabTaskDetailResponse>(
            $"/api/shinelab/tasks/{taskUuid}");

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.NotNull(first);
        Assert.NotNull(replay);
        Assert.Equal(first.Id, replay.Id);
        Assert.NotNull(detail);
        Assert.Equal(taskUuid, detail.Task.TaskUuid);
        Assert.Contains(detail.Events, item => item.EventType == "TaskCreated");
    }

    [Fact]
    public async Task Reusing_task_uuid_with_different_sample_is_conflict()
    {
        using var client = factory.CreateClient();
        var taskUuid = $"task-{Guid.NewGuid():N}";
        var first = new ShineLabTaskCreateRequest(
            "SHA18I",
            taskUuid,
            [new ShineLabSampleData("S-01", "标准样", "1", 11, null, "A")]);
        var changed = first with
        {
            SampleData = [new ShineLabSampleData("S-01", "标准样", "1", 12, null, "A")]
        };

        await client.PostAsJsonAsync("/api/shinelab/tasks", first);
        var response = await client.PostAsJsonAsync("/api/shinelab/tasks", changed);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
