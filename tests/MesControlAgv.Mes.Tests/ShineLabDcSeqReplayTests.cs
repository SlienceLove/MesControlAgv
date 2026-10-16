using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Tests;

public sealed class ShineLabDcSeqReplayTests
{
    private static readonly IOptions<ShineLabTcpOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new ShineLabTcpOptions
        { PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel });

    [Fact]
    public void Field_finish_events_do_not_override_running_or_idle_and_remain_separate_evidence()
    {
        var hub = new ShineLabStatusHub(Options);
        hub.Apply("device-1", "Device", "STN61_01", Json("""{"status":1}"""));
        hub.Apply("finish-1", "TaskFinish", "STN61_01",
            Json("""{"channel":"","sampleId":"","finishDate":"2026-09-21 16:50:36"}"""));
        hub.Apply("finish-2", "TaskFinish", "STN61_01",
            Json("""{"channel":"","sampleId":"","finishDate":"2026-09-21 16:50:36"}"""));
        hub.Apply("sample-1", "SampleFinish", "STN61_01",
            Json("""{"channel":"","sampleId":"6","finishDate":"2026-09-21 16:51:42"}"""));
        var running = hub.GetStatus("STN61_01")!;
        Assert.Equal("Running", running.State);
        Assert.True(running.HasActiveTask);
        Assert.Null(running.TaskFinishedAtUtc);
        Assert.Null(running.Progress);
        Assert.Null(running.Stage);
        Assert.Equal("6", running.LastProtocolEvent!.SampleId);
        Assert.NotNull(running.LastProtocolEvent.ReportedFinishAtUtc);

        hub.Apply("device-2", "Device", "STN61_01", Json("""{"status":1}"""));
        Assert.True(hub.GetStatus("STN61_01")!.HasActiveTask);
        hub.Apply("device-3", "Device", "STN61_01", Json("""{"status":0}"""));
        var idle = hub.GetStatus("STN61_01")!;
        Assert.Equal("Idle", idle.State);
        Assert.False(idle.HasActiveTask);
        Assert.Null(idle.SampleId);
        Assert.Null(idle.Progress);
        Assert.Null(idle.TaskFinishedAtUtc);
        Assert.Equal("SampleFinish", idle.LastProtocolEvent!.Method);
    }

    [Fact]
    public void Instrument_offline_is_distinct_from_tcp_offline()
    {
        var hub = new ShineLabStatusHub(Options);
        hub.Apply("1", "Device", "STN61_01", Json("""{"status":99}"""));
        Assert.True(hub.GetStatus("STN61_01")!.Online);
        Assert.Equal("InstrumentOffline", hub.GetStatus("STN61_01")!.State);
        hub.MarkDisconnected("STN61_01");
        Assert.False(hub.GetStatus("STN61_01")!.Online);
        Assert.Equal("Offline", hub.GetStatus("STN61_01")!.State);
    }

    [Fact]
    public async Task Sample_only_and_empty_id_events_are_retained_without_guessing_task_ownership()
    {
        await using var db = Database();
        var repository = new ShineLabTaskRepository(db, Options);
        await repository.CreateOrGetAsync(Request("task-1"), default);
        await repository.CreateOrGetAsync(Request("task-2"), default); // Same sample ID intentionally.
        await repository.ApplyPushAsync("TaskFinish", "STN61_01",
            Json("""{"sampleId":"","finishDate":"2026-09-21 16:50:36"}"""), default, "wire-1");
        await repository.ApplyPushAsync("SampleFinish", "STN61_01",
            Json("""{"sampleId":"6","finishDate":"2026-09-21 16:51:42"}"""), default, "wire-2");
        var evidence = await repository.GetPushEvidenceAsync("STN61_01", 100, default);
        Assert.Equal(2, evidence.Count);
        Assert.All(evidence, item => { Assert.Null(item.TaskUuid); Assert.Equal("MissingTaskId", item.Correlation); });
        Assert.All(await db.ShineLabTasks.ToListAsync(), task =>
        { Assert.Equal("Created", task.Status); Assert.Null(task.CompletedAtUtc); });
    }

    [Theory]
    [InlineData("SampleFinish")]
    [InlineData("TaskFinish")]
    [InlineData("EndMission")]
    public async Task Paired_finish_frames_keep_both_evidence_rows_but_only_one_task_observation(string method)
    {
        await using var db = Database();
        var repository = new ShineLabTaskRepository(db, Options);
        await repository.CreateOrGetAsync(Request("task-1"), default);
        await repository.MarkOperationStartedAsync("task-1", "Running", "Running", default);
        await repository.ApplyPushAsync(method, "STN61_01",
            Json("""{"task_uuid":"task-1","sampleId":"6","finishDate":"2026-09-21 16:51:42"}"""), default, "wire-1");
        // New repository scope and changed property order, like successive TCP frames.
        repository = new ShineLabTaskRepository(db, Options);
        await repository.ApplyPushAsync(method, "STN61_01",
            Json("""{"finishDate":"2026-09-21 16:51:42", "sampleId":"6", "task_uuid":"task-1"}"""), default, "wire-2");
        var evidence = await repository.GetPushEvidenceAsync("STN61_01", 100, default);
        Assert.Equal(2, evidence.Count);
        Assert.Single(evidence.Where(item => item.IsDuplicate));
        Assert.Single((await repository.GetEventsAsync("task-1", default)).Where(item => item.EventType == method + "Observed"));
        var task = (await repository.GetAsync("task-1", default))!;
        Assert.Equal("Running", task.Status);
        Assert.Null(task.CompletedAtUtc);
    }

    [Fact]
    public async Task Foreign_equipment_cannot_advance_task_even_with_valid_task_uuid()
    {
        await using var db = Database();
        var repository = new ShineLabTaskRepository(db, Options);
        await repository.CreateOrGetAsync(Request("task-1"), default);
        await repository.ApplyPushAsync("TaskFinish", "OTHER",
            Json("""{"task_uuid":"task-1","sampleId":"6"}"""), default, "wire-1");
        await repository.ApplyPushAsync("Device", "OTHER",
            Json("""{"task_uuid":"task-1","status":1}"""), default, "wire-2");
        Assert.Equal("Created", (await repository.GetAsync("task-1", default))!.Status);
        Assert.Equal("EquipmentMismatch", Assert.Single(await repository.GetPushEvidenceAsync("OTHER", 10, default)).Correlation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateOrGetAsync(
            Request("task-1") with { EquipmentCode = "OTHER" }, default));
    }

    [Theory]
    [InlineData(0, "Accepted")]
    [InlineData(2, "Stopping")]
    [InlineData(7, "Accepted")]
    public async Task Acknowledgement_does_not_prove_execution_or_row_creation(int action, string status)
    {
        await using var db = Database();
        var repository = new ShineLabTaskRepository(db, Options);
        await repository.CreateOrGetAsync(Request("task-1"), default);
        var response = new ShineLabCommandResponse("wire", "Config", "STN61_01", true, null,
            Json("""{"result":"Success"}"""));
        var task = await repository.MarkConfigResultAsync("task-1", response, default);
        Assert.Equal("Configured", task.Status);
        Assert.Equal("ConfigAcceptedAwaitingVerification", task.CurrentStage);
        task = await repository.MarkCommandResultAsync("task-1", new("task-1", action),
            response with { StrMethod = "Command" }, default);
        Assert.Equal(status, task.Status);
        Assert.Null(task.StartedAtUtc);
        Assert.Null(task.CompletedAtUtc);
    }

    [Fact]
    public async Task New_finish_time_is_not_a_duplicate_and_result_is_not_completion()
    {
        await using var db = Database();
        var repository = new ShineLabTaskRepository(db, Options);
        await repository.CreateOrGetAsync(Request("task-1"), default);
        foreach (var seconds in new[] { "42", "43" })
            await repository.ApplyPushAsync("SampleFinish", "STN61_01",
                JsonSerializer.SerializeToElement(new { task_uuid = "task-1", sampleId = "6",
                    finishDate = "2026-09-21 16:51:" + seconds }), default, "wire-" + seconds);
        await repository.ApplyPushAsync("Result", "STN61_01",
            Json("""{"task_uuid":"task-1","sampleId":"6","compounds":[]}"""), default, "result-1");
        Assert.All(await repository.GetPushEvidenceAsync("STN61_01", 100, default), item => Assert.False(item.IsDuplicate));
        var task = (await repository.GetAsync("task-1", default))!;
        Assert.NotNull(task.ResultJson);
        Assert.Null(task.CompletedAtUtc);
        Assert.NotEqual("Completed", task.Status);
    }

    [Fact]
    public async Task Explicit_fault_is_visible_without_claiming_stopped_or_completed()
    {
        var hub = new ShineLabStatusHub(Options);
        var body = Json("""{"task_uuid":"task-1","sampleId":"6","errorCode":"E1","errorMsg":"fault"}""");
        hub.Apply("device", "Device", "STN61_01", Json("""{"status":1}"""));
        hub.Apply("fault", "TaskError", "STN61_01", body);
        var state = hub.GetStatus("STN61_01")!;
        Assert.Equal("Error", state.State);
        Assert.Equal("E1", state.AlarmCode);
        Assert.Equal("fault", state.AlarmMessage);
        Assert.Null(state.TaskFinishedAtUtc);

        await using var db = Database();
        var repository = new ShineLabTaskRepository(db, Options);
        await repository.CreateOrGetAsync(Request("task-1"), default);
        await repository.ApplyPushAsync("TaskError", "STN61_01", body, default, "fault");
        var task = (await repository.GetAsync("task-1", default))!;
        Assert.Equal("Error", task.Status);
        Assert.Equal("fault", task.LastError);
        Assert.Null(task.CompletedAtUtc);
    }

    [Theory]
    [InlineData("same", "same", true)]
    [InlineData("first", "second", false)]
    [InlineData(null, null, false)]
    public async Task Without_finish_time_only_identical_wire_id_is_deduplicated(string? first, string? second, bool duplicate)
    {
        await using var db = Database();
        var repository = new ShineLabTaskRepository(db, Options);
        var body = Json("""{"sampleId":"6"}""");
        await repository.ApplyPushAsync("SampleFinish", "STN61_01", body, default, first);
        await repository.ApplyPushAsync("SampleFinish", "STN61_01", body, default, second);
        var evidence = await repository.GetPushEvidenceAsync("STN61_01", 100, default);
        Assert.Equal(duplicate ? 1 : 0, evidence.Count(item => item.IsDuplicate));
        Assert.Equal(2, evidence.Count);
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static MesDbContext Database() => new(new DbContextOptionsBuilder<MesDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static ShineLabTaskCreateRequest Request(string taskUuid) => new("STN61_01", taskUuid,
        [new("6", "DCSeq_AfterBaseline_06", "0", 1, "1", "")]);
}
