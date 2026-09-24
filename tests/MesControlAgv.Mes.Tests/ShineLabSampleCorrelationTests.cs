using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Tests;

public sealed class ShineLabSampleCorrelationTests
{
    private const string Equipment = "STN61_01";
    private static readonly IOptions<ShineLabTcpOptions> Options = Microsoft.Extensions.Options.Options.Create(
        new ShineLabTcpOptions { PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel });

    [Fact]
    public async Task Allocation_preserves_source_ids_and_idempotent_requests_but_new_run_has_new_ids()
    {
        await using var db = Database();
        var repo = new ShineLabTaskRepository(db, Options);
        var request = Request("task-1", 20);
        var first = await repo.CreateOrGetAsync(request, default);
        var replay = await repo.CreateOrGetAsync(request with { TaskUuid = " task-1 " }, default);
        Assert.False(replay.Created);
        Assert.Equal(first.Task.ConfigJson, replay.Task.ConfigJson);
        await repo.CreateOrGetAsync(Request("task-2", 20), default);
        var mappings = await db.ShineLabTaskSamples.ToListAsync();
        Assert.Equal(40, mappings.Count);
        Assert.Equal(40, mappings.Select(row => row.SampleId).Distinct().Count());
        Assert.All(mappings, row =>
        {
            Assert.Matches("^[1-9][0-9]{8}$", row.SampleId);
            Assert.Equal("source-6", row.SourceSampleId);
            Assert.False(row.ConfigAttempted);
        });
        var config = JsonSerializer.Deserialize<ShineLabConfigRequest>(first.Task.ConfigJson)!;
        Assert.Equal((await repo.GetSamplesAsync("task-1", default)).Select(row => row.SampleId),
            config.SampleData.Select(row => row.SampleId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.CreateOrGetAsync(
            request with { SampleData = [new("other", "changed", "0", 1, "1", "")] }, default));
    }

    [Fact]
    public async Task Multiple_sample_results_are_independent_and_duplicates_do_not_repeat_observations()
    {
        await using var db = Database();
        var repo = new ShineLabTaskRepository(db, Options);
        await repo.CreateOrGetAsync(Request("task", 2), default);
        await repo.MarkOperationStartedAsync("task", "Configuring", "Configuring", default);
        var samples = await repo.GetSamplesAsync("task", default);
        await repo.ApplyPushAsync("Result", Equipment, Result(samples[1].SampleId, 2), default, "r2");
        Assert.Equal("PartialResultsReceived", ShineLabTaskRepository.GetResultState(samples));
        await repo.ApplyPushAsync("Result", Equipment, Result(samples[0].SampleId, 1), default, "r1");
        await repo.ApplyPushAsync("Result", Equipment, Result(samples[0].SampleId, 1), default, "r1-repeat");
        await repo.ApplyPushAsync("TaskFinish", Equipment,
            JsonSerializer.SerializeToElement(new { sampleId = samples[0].SampleId, finishDate = "finish" }), default, "finish");
        Assert.Equal("AllResultsReceived", ShineLabTaskRepository.GetResultState(samples));
        Assert.Equal(1, JsonSerializer.Deserialize<JsonElement>(samples[0].ResultJson!).GetProperty("compounds")[0].GetProperty("concent").GetInt32());
        Assert.Equal(2, JsonSerializer.Deserialize<JsonElement>(samples[1].ResultJson!).GetProperty("compounds")[0].GetProperty("concent").GetInt32());
        Assert.True(samples[0].TaskFinishObserved);
        Assert.False(samples[1].TaskFinishObserved);
        var task = (await repo.GetAsync("task", default))!;
        Assert.Equal("AllResultsReceivedAwaitingCompletion", task.CurrentStage);
        Assert.Null(task.CompletedAtUtc);
        Assert.Equal(2, JsonSerializer.Deserialize<JsonElement>(task.ResultJson!).GetProperty("samples").GetArrayLength());
        Assert.Equal(2, (await repo.GetEventsAsync("task", default)).Count(e => e.EventType == "ResultObserved"));
        var evidence = await repo.GetPushEvidenceAsync(Equipment, 100, default);
        Assert.Equal(4, evidence.Count);
        Assert.Single(evidence.Where(e => e.IsDuplicate));
        Assert.All(evidence, e => { Assert.Equal("task", e.TaskUuid); Assert.Equal("MesSampleId", e.Correlation); });
    }

    [Theory]
    [InlineData("wrong-device", "EquipmentMismatch")]
    [InlineData("wrong-task", "TaskSampleMismatch")]
    [InlineData("wrong-channel", "ChannelMismatch")]
    [InlineData("not-sent", "SampleNotDispatched")]
    [InlineData("unknown-id", "MissingTaskId")]
    public async Task Invalid_correlation_never_changes_task_or_sample(string mode, string expected)
    {
        await using var db = Database();
        var repo = new ShineLabTaskRepository(db, Options);
        await repo.CreateOrGetAsync(Request("task", 1), default);
        await repo.CreateOrGetAsync(Request("other", 1), default);
        if (mode != "not-sent") await repo.MarkOperationStartedAsync("task", "Configuring", "Configuring", default);
        var sample = Assert.Single(await repo.GetSamplesAsync("task", default));
        var body = JsonSerializer.SerializeToElement(new
        {
            sampleId = mode == "unknown-id" ? "8" : sample.SampleId,
            task_uuid = mode == "wrong-task" ? "other" : null,
            channel = mode == "wrong-channel" ? "B" : "",
            testDate = "test-date", compounds = Array.Empty<object>()
        });
        var equipment = mode == "wrong-device" ? "OTHER_DEVICE" : Equipment;
        await repo.ApplyPushAsync("Result", equipment, body, default, "wire");
        var evidence = Assert.Single(await repo.GetPushEvidenceAsync(equipment, 100, default));
        Assert.Equal(expected, evidence.Correlation);
        Assert.Null(evidence.TaskUuid);
        Assert.Null(sample.ResultJson);
        Assert.All(await db.ShineLabTasks.ToListAsync(), task => Assert.Null(task.ResultJson));
    }

    [Fact]
    public async Task Different_result_for_same_injection_is_review_conflict_not_overwrite()
    {
        await using var db = Database();
        var repo = new ShineLabTaskRepository(db, Options);
        await repo.CreateOrGetAsync(Request("task", 1), default);
        await repo.MarkOperationStartedAsync("task", "Configuring", "Configuring", default);
        var sample = Assert.Single(await repo.GetSamplesAsync("task", default));
        var original = Result(sample.SampleId, 1);
        await repo.ApplyPushAsync("Result", Equipment, original, default, "first");
        await repo.ApplyPushAsync("Result", Equipment, Result(sample.SampleId, 2), default, "changed");
        Assert.Equal(original.GetRawText(), sample.ResultJson);
        Assert.True(sample.ResultConflict);
        Assert.Equal("NeedsReview", ShineLabTaskRepository.GetResultState([sample]));
        Assert.Equal("ResultConflictNeedsReview", (await repo.GetAsync("task", default))!.CurrentStage);
        Assert.Contains(await repo.GetPushEvidenceAsync(Equipment, 100, default), e => e.Correlation == "SampleResultConflict");
    }

    [Fact]
    public async Task Unassociated_frame_does_not_suppress_later_valid_observation_after_config()
    {
        await using var db = Database();
        var repo = new ShineLabTaskRepository(db, Options);
        await repo.CreateOrGetAsync(Request("task", 1), default);
        var sample = Assert.Single(await repo.GetSamplesAsync("task", default));
        var body = Result(sample.SampleId, 1);
        await repo.ApplyPushAsync("Result", Equipment, body, default, "too-early");
        Assert.Null(sample.ResultJson);
        await repo.MarkOperationStartedAsync("task", "Configuring", "Configuring", default);
        await repo.ApplyPushAsync("Result", Equipment, body, default, "valid");
        Assert.NotNull(sample.ResultJson);
        Assert.Single((await repo.GetEventsAsync("task", default)).Where(e => e.EventType == "ResultObserved"));
        var evidence = await repo.GetPushEvidenceAsync(Equipment, 100, default);
        Assert.All(evidence, e => Assert.False(e.IsDuplicate));
    }

    [Fact]
    public async Task Fault_and_stopping_states_are_not_cleared_by_result_or_finish()
    {
        await using var db = Database();
        var repo = new ShineLabTaskRepository(db, Options);
        foreach (var status in new[] { "Error", "Stopping", "Unknown" })
        {
            await repo.CreateOrGetAsync(Request(status, 1), default);
            await repo.MarkOperationStartedAsync(status, "Configuring", "Configuring", default);
            await repo.MarkOperationStartedAsync(status, status, status, default);
            var sample = Assert.Single(await repo.GetSamplesAsync(status, default));
            await repo.ApplyPushAsync("Result", Equipment, Result(sample.SampleId, 1), default, status);
            var task = (await repo.GetAsync(status, default))!;
            Assert.Equal(status, task.Status);
            Assert.Null(task.CompletedAtUtc);
            Assert.NotNull(sample.ResultJson);
        }
    }

    [Fact]
    public async Task No_connection_disarms_mapping_and_allows_later_config_retry()
    {
        await using var db = Database();
        var repo = new ShineLabTaskRepository(db, Options);
        using var manager = new ShineLabConnectionManager();
        var service = new ShineLabTaskService(repo, new ShineLabCommandService(manager, Options));
        await service.CreateAsync(Request("task", 1), default);
        await Assert.ThrowsAsync<ShineLabNotConnectedException>(() => service.ConfigureAsync("task", default));
        Assert.False(Assert.Single(await repo.GetSamplesAsync("task", default)).ConfigAttempted);
        await Assert.ThrowsAsync<ShineLabNotConnectedException>(() => service.ConfigureAsync("task", default));
        Assert.Equal("Created", (await repo.GetAsync("task", default))!.Status);
    }

    [Fact]
    public async Task Attempted_config_is_not_resent_after_rejection()
    {
        await using var db = Database();
        var repo = new ShineLabTaskRepository(db, Options);
        using var manager = new ShineLabConnectionManager();
        var service = new ShineLabTaskService(repo, new ShineLabCommandService(manager, Options));
        await service.CreateAsync(Request("task", 1), default);
        await repo.MarkOperationStartedAsync("task", "Configuring", "Configuring", default);
        await repo.MarkConfigResultAsync("task", new("r", "Config", Equipment, false, "rejected",
            JsonSerializer.SerializeToElement(new { result = "Failed" })), default);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfigureAsync("task", default));
        Assert.Contains("already been used", error.Message);
    }

    [Fact]
    public async Task Sqlite_unique_sample_id_constraint_and_old_database_upgrade_preserve_existing_tasks()
    {
        string path;
        using (var factory = new MesWebApplicationFactory())
        {
            using var http = factory.CreateClient();
            path = factory.DatabasePath;
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var legacy = new ShineLabTaskRepository(db);
            await legacy.CreateOrGetAsync(Request("legacy", 1), default);
            await db.Database.ExecuteSqlRawAsync("DROP TABLE ShineLabTaskSamples;");
        }
        using var restarted = new MesWebApplicationFactory(path);
        using var app = Configured(restarted);
        using var client = app.CreateClient();
        using var newScope = app.Services.CreateScope();
        var upgradedDb = newScope.ServiceProvider.GetRequiredService<MesDbContext>();
        var repo = newScope.ServiceProvider.GetRequiredService<ShineLabTaskRepository>();
        Assert.NotNull(await repo.GetAsync("legacy", default));
        Assert.Empty(await repo.GetSamplesAsync("legacy", default));
        await repo.CreateOrGetAsync(Request("new", 1), default);
        var sample = Assert.Single(await repo.GetSamplesAsync("new", default));
        upgradedDb.ShineLabTaskSamples.Add(new ShineLabTaskSample
        { TaskUuid = "other", RowNumber = 1, SampleId = sample.SampleId });
        await Assert.ThrowsAsync<DbUpdateException>(() => upgradedDb.SaveChangesAsync());
    }

    [Fact]
    public async Task Http_task_config_over_tcp_and_sample_only_results_survive_restart()
    {
        string path;
        string[] ids;
        using (var factory = new MesWebApplicationFactory())
        using (var app = Configured(factory))
        using (var http = app.CreateClient())
        {
            path = factory.DatabasePath;
            var created = await http.PostAsJsonAsync("/api/shinelab/tasks", Request("tcp-task", 2));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var before = (await http.GetFromJsonAsync<ShineLabTaskDetailResponse>("/api/shinelab/tasks/tcp-task"))!;
            ids = before.Samples!.Select(sample => sample.SampleId).ToArray();
            Assert.All(before.Samples!, sample => Assert.False(sample.ConfigAttempted));
            var preview = await http.GetFromJsonAsync<JsonElement>("/api/shinelab/tasks/tcp-task/config-preview");
            var previewBody = preview.GetProperty("body");
            Assert.False(previewBody.TryGetProperty("channel", out _));
            Assert.Equal(ids, previewBody.GetProperty("sampleData").EnumerateArray()
                .Select(row => row.GetProperty("sampleId").GetString()));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var serverOptions = Microsoft.Extensions.Options.Options.Create(new ShineLabTcpOptions
            { Enabled = true, ListenAddress = "127.0.0.1", Port = port, StaleAfterSeconds = 60 });
            using var server = new ShineLabTcpServer(serverOptions,
                app.Services.GetRequiredService<ShineLabStatusHub>(),
                app.Services.GetRequiredService<ShineLabConnectionManager>(),
                NullLogger<ShineLabTcpServer>.Instance, app.Services.GetRequiredService<IServiceScopeFactory>());
            await server.StartAsync(default);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var tcp = new TcpClient();
                // BackgroundService starts listening synchronously before first accept await.
                await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                await using var stream = tcp.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                await SendAsync(stream, new { strID = "cert", strMethod = "Certification", equipmentCode = Equipment, body = new { } });
                using var cert = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
                Assert.Equal("Certification", cert.RootElement.GetProperty("strMethod").GetString());
                var configCall = http.PostAsync("/api/shinelab/tasks/tcp-task/config", null, timeout.Token);
                using var config = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
                Assert.Equal("Config", config.RootElement.GetProperty("strMethod").GetString());
                Assert.Equal(previewBody.GetRawText(), config.RootElement.GetProperty("body").GetRawText());
                await SendAsync(stream, new { strID = config.RootElement.GetProperty("strID").GetString(),
                    strMethod = "Config", equipmentCode = Equipment, body = new { result = "Success" } });
                Assert.Equal(HttpStatusCode.OK, (await configCall).StatusCode);
                // Same task Config retry returns its existing accepted state, no second wire send.
                Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/shinelab/tasks/tcp-task/config", null, timeout.Token)).StatusCode);
                foreach (var id in ids)
                    for (var repeat = 0; repeat < 4; repeat++)
                        await SendAsync(stream, new { strID = id + "-" + repeat, strMethod = "Result", equipmentCode = Equipment, body = Result(id, 1) });
                ShineLabTaskDetailResponse detail;
                do
                {
                    detail = (await http.GetFromJsonAsync<ShineLabTaskDetailResponse>("/api/shinelab/tasks/tcp-task", timeout.Token))!;
                    if (detail.ResultState != "AllResultsReceived") await Task.Delay(20, timeout.Token);
                } while (detail.ResultState != "AllResultsReceived");
                Assert.Equal(2, detail.Events.Count(e => e.EventType == "ResultObserved"));
                Assert.All(detail.Samples!, sample => Assert.NotNull(sample.ResultJson));
                Assert.Null(detail.Task.CompletedAtUtc);
                List<ShineLabPushEvidence> evidence;
                do
                {
                    evidence = (await http.GetFromJsonAsync<List<ShineLabPushEvidence>>(
                        "/api/shinelab/devices/STN61_01/push-evidence?limit=100", timeout.Token))!;
                    if (evidence.Count < 8) await Task.Delay(20, timeout.Token);
                } while (evidence.Count < 8);
                Assert.Equal(6, evidence.Count(e => e.IsDuplicate));
                Assert.All(evidence, e => { Assert.Equal("tcp-task", e.TaskUuid); Assert.NotNull(e.RawJson); });
                using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await stream.ReadAsync(new byte[1], quiet.Token));
            }
            finally { await server.StopAsync(default); }
        }
        using var restarted = new MesWebApplicationFactory(path);
        using var restoredApp = Configured(restarted);
        using var restoredHttp = restoredApp.CreateClient();
        var replay = await restoredHttp.PostAsJsonAsync("/api/shinelab/tasks", Request("tcp-task", 2));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var persisted = (await restoredHttp.GetFromJsonAsync<ShineLabTaskDetailResponse>("/api/shinelab/tasks/tcp-task"))!;
        Assert.Equal(ids, persisted.Samples!.Select(sample => sample.SampleId));
        Assert.Equal("AllResultsReceived", persisted.ResultState);
        Assert.All(persisted.Samples!, sample => { Assert.True(sample.ConfigAttempted); Assert.NotNull(sample.ResultJson); });
    }

    private static WebApplicationFactory<Program> Configured(MesWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<ShineLabTcpOptions>(options =>
            { options.Enabled = false; options.PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel; })));

    private static async Task SendAsync(Stream stream, object value) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n"));

    private static JsonElement Result(string sampleId, int concentration) => JsonSerializer.SerializeToElement(new
    { sampleId, testDate = "2026-09-23 12:00:00", compounds = new[] { new { name = "SYNTHETIC", concent = concentration } } });

    private static ShineLabTaskCreateRequest Request(string taskUuid, int count) => new(Equipment, taskUuid,
        Enumerable.Range(1, count).Select(index => new ShineLabSampleData("source-6", "synthetic-" + index, "0", 1, "1", "")).ToArray());

    private static MesDbContext Database() => new(new DbContextOptionsBuilder<MesDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
