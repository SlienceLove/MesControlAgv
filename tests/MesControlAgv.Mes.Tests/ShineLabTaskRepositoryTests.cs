using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Tests;

public sealed class ShineLabTaskRepositoryTests
{
    [Fact]
    public async Task Task_uuid_is_idempotent_but_rejects_different_payload()
    {
        await using var database = CreateDatabase();
        var repository = new ShineLabTaskRepository(database);
        var request = CreateRequest("task-001", 11);

        var first = await repository.CreateOrGetAsync(request, CancellationToken.None);
        var replay = await repository.CreateOrGetAsync(request, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(replay.Created);
        Assert.Equal(first.Task.Id, replay.Task.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CreateOrGetAsync(CreateRequest("task-001", 12), CancellationToken.None));
    }

    [Fact]
    public async Task ShineLab_pushes_advance_persisted_task_to_completed()
    {
        await using var database = CreateDatabase();
        var repository = new ShineLabTaskRepository(database);
        await repository.CreateOrGetAsync(CreateRequest("task-002", 11), CancellationToken.None);

        await repository.ApplyPushAsync(
            "UpdateInfo",
            "SHA18I",
            JsonSerializer.SerializeToElement(new { task_uuid = "task-002", status = 1, stage = "Detecting" }),
            CancellationToken.None);
        await repository.ApplyPushAsync(
            "Result",
            "SHA18I",
            JsonSerializer.SerializeToElement(new { task_uuid = "task-002", data = new[] { new { testItem = "Li", value = 3.2 } } }),
            CancellationToken.None);
        await repository.ApplyPushAsync(
            "TaskFinish",
            "SHA18I",
            JsonSerializer.SerializeToElement(new { task_uuid = "task-002", finishDate = "2026-09-02 10:00:00" }),
            CancellationToken.None);

        var task = await repository.GetAsync("task-002", CancellationToken.None);
        var events = await repository.GetEventsAsync("task-002", CancellationToken.None);
        Assert.NotNull(task);
        Assert.Equal("Completed", task.Status);
        Assert.Equal("Completed", task.CurrentStage);
        Assert.NotNull(task.ResultJson);
        Assert.NotNull(task.StartedAtUtc);
        Assert.NotNull(task.CompletedAtUtc);
        Assert.Equal(["TaskCreated", "UpdateInfo", "Result", "TaskFinish"], events.Select(item => item.EventType));
    }

    [Fact]
    public async Task Restart_recovery_marks_in_flight_task_unknown()
    {
        await using var database = CreateDatabase();
        var repository = new ShineLabTaskRepository(database);
        await repository.CreateOrGetAsync(CreateRequest("task-003", 11), CancellationToken.None);
        await repository.MarkOperationStartedAsync(
            "task-003", "Running", "Detecting", CancellationToken.None);

        var count = await repository.MarkInterruptedTasksUnknownAsync(CancellationToken.None);

        var task = await repository.GetAsync("task-003", CancellationToken.None);
        var events = await repository.GetEventsAsync("task-003", CancellationToken.None);
        Assert.Equal(1, count);
        Assert.NotNull(task);
        Assert.Equal("Unknown", task.Status);
        Assert.Equal("RecoveredAfterRestart", task.CurrentStage);
        Assert.Contains(events, item => item.EventType == "RecoveredAsUnknown");
    }

    private static MesDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseInMemoryDatabase($"shinelab-task-{Guid.NewGuid():N}")
            .Options;
        return new MesDbContext(options);
    }

    private static ShineLabTaskCreateRequest CreateRequest(string taskUuid, int position) => new(
        "SHA18I",
        taskUuid,
        [new ShineLabSampleData("S-01", "标准样", "1", position, null, "A")],
        "AS18-M01",
        "IC-P01",
        "Normal");
}
