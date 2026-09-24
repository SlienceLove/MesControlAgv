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

public sealed class ShineLabCommandJournalTests
{
    private const string Equipment = "STN61_01";
    private const string TaskId = "journal-test";

    [Theory]
    [InlineData("Config", null)]
    [InlineData("Command", 0)]
    [InlineData("Command", 2)]
    public async Task Late_reply_after_restart_recovers_by_id_without_resending(string method, int? action)
    {
        string path;
        string id;
        using (var factory = new MesWebApplicationFactory())
        using (var app = Configured(factory))
        using (var http = app.CreateClient())
        {
            path = factory.DatabasePath;
            await CreateAndMarkAsync(app, method);
            using var stream = new MemoryStream();
            var manager = app.Services.GetRequiredService<ShineLabConnectionManager>();
            manager.Register(Equipment, "before-restart", ShineLabWireFormat.LineJson, stream);
            var timeout = await Assert.ThrowsAsync<ShineLabCommandTimeoutException>(() => manager.SendAsync(
                Equipment, method, JsonSerializer.SerializeToElement(new { action }), TimeSpan.FromMilliseconds(30),
                default, TaskId, action));
            id = timeout.StrId;
            var journal = app.Services.GetRequiredService<ShineLabCommandJournal>();
            await journal.ReconcileAsync(TaskId, method, id, method + "OutcomeUnknown", "timeout", default);
            var audit = await http.GetFromJsonAsync<JsonElement>($"/api/shinelab/tasks/{TaskId}/command-audit");
            var attempt = Assert.Single(audit.GetProperty("attempts").EnumerateArray());
            Assert.Equal(id, attempt.GetProperty("strId").GetString());
            Assert.Equal(Encoding.UTF8.GetString(stream.ToArray()).TrimEnd('\n'), attempt.GetProperty("rawJson").GetString());
            Assert.Equal("TimedOut", attempt.GetProperty("waitOutcome").GetString());
            Assert.Equal("Unknown", (await http.GetFromJsonAsync<ShineLabTaskDetailResponse>($"/api/shinelab/tasks/{TaskId}"))!.Task.Status);
        }
        using var restart = new MesWebApplicationFactory(path);
        using var restored = Configured(restart);
        using var client = restored.CreateClient();
        var journal2 = restored.Services.GetRequiredService<ShineLabCommandJournal>();
        var body = JsonSerializer.SerializeToElement(new { channel = "", result = "Success", msg = "" });
        var raw = JsonSerializer.Serialize(new { strID = id, strMethod = method, equipmentCode = Equipment, body });
        var context = new ShineLabPushContext(raw, "after-restart", "LineJson", DateTimeOffset.UtcNow);
        await journal2.RecordReplyAsync(id, method, Equipment, body, context, default);
        await journal2.RecordReplyAsync(id, method, Equipment, body, context, default);
        // A timeout continuation racing with a saved reply cannot overwrite it.
        await journal2.ReconcileAsync(TaskId, method, id, method + "OutcomeUnknown", "old timeout", default);
        var detail = (await client.GetFromJsonAsync<ShineLabTaskDetailResponse>($"/api/shinelab/tasks/{TaskId}"))!;
        Assert.Equal(method == "Config" ? "Configured" : action == 2 ? "Stopping" : "Accepted", detail.Task.Status);
        Assert.Null(detail.Task.CompletedAtUtc);
        Assert.Single(detail.Events.Where(e => e.EventType == method + "Accepted"));
        var saved = await client.GetFromJsonAsync<JsonElement>($"/api/shinelab/tasks/{TaskId}/command-audit");
        Assert.Single(saved.GetProperty("attempts").EnumerateArray());
        var replies = saved.GetProperty("replies").EnumerateArray().ToArray();
        Assert.Equal(2, replies.Length);
        Assert.Contains(replies, r => r.GetProperty("correlation").GetString() == "Duplicate");
        Assert.All(replies, r => Assert.Equal(raw, r.GetProperty("rawJson").GetString()));
    }

    [Theory]
    [InlineData("wrong-device", "EquipmentMismatch")]
    [InlineData("wrong-method", "MethodMismatch")]
    [InlineData("unknown-id", "UnknownRequestId")]
    [InlineData("bad-body", "MalformedReply")]
    public async Task Unmatched_reply_is_retained_without_completing_waiter(string mode, string expected)
    {
        using var factory = new MesWebApplicationFactory();
        using var app = Configured(factory);
        using var http = app.CreateClient();
        await CreateAndMarkAsync(app, "Config");
        var manager = app.Services.GetRequiredService<ShineLabConnectionManager>();
        using var stream = new MemoryStream();
        manager.Register(Equipment, "test", ShineLabWireFormat.LineJson, stream);
        var pending = manager.SendAsync(Equipment, "Config", JsonSerializer.SerializeToElement(new { }),
            TimeSpan.FromSeconds(5), default, TaskId);
        var id = await WaitForAttemptAsync(app);
        var wireId = mode == "unknown-id" ? "untracked" : id;
        var method = mode == "wrong-method" ? "Command" : "Config";
        var device = mode == "wrong-device" ? "OTHER" : Equipment;
        var body = mode == "bad-body" ? JsonSerializer.SerializeToElement(new { success = 1 }) :
            JsonSerializer.SerializeToElement(new { result = "Success" });
        var raw = JsonSerializer.Serialize(new { strID = wireId, strMethod = method, equipmentCode = device, body });
        await manager.RecordReplyAsync(wireId, method, device, body, new(raw, "test", "LineJson", DateTimeOffset.UtcNow), default);
        Assert.False(manager.TryCompleteResponse(wireId, method, device, body));
        Assert.False(pending.IsCompleted);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        var reply = Assert.Single(await db.ShineLabCommandReplies.ToListAsync());
        Assert.Equal(expected, reply.Correlation);
        Assert.Null(reply.TaskUuid);
        manager.Unregister("test");
        await Assert.ThrowsAsync<ShineLabCommandTransportException>(() => pending);
    }

    [Fact]
    public async Task Conflict_or_old_reply_does_not_overwrite_current_acknowledgement()
    {
        using var factory = new MesWebApplicationFactory();
        using var app = Configured(factory);
        using var http = app.CreateClient();
        await CreateAndMarkAsync(app, "Config");
        var journal = app.Services.GetRequiredService<ShineLabCommandJournal>();
        await journal.PrepareAsync(Attempt("old"), default);
        await journal.PrepareAsync(Attempt("current"), default);
        await ReplyAsync(journal, "old", "Failed");
        Assert.Equal("Configuring", (await http.GetFromJsonAsync<ShineLabTaskDetailResponse>($"/api/shinelab/tasks/{TaskId}"))!.Task.Status);
        await ReplyAsync(journal, "current", "Success");
        await ReplyAsync(journal, "current", "Failed");
        var detail = (await http.GetFromJsonAsync<ShineLabTaskDetailResponse>($"/api/shinelab/tasks/{TaskId}"))!;
        Assert.Equal("AcknowledgementConflictNeedsReview", detail.Task.CurrentStage);
        var audit = await http.GetFromJsonAsync<JsonElement>($"/api/shinelab/tasks/{TaskId}/command-audit");
        var current = audit.GetProperty("attempts").EnumerateArray().Single(a => a.GetProperty("strId").GetString() == "current");
        Assert.True(current.GetProperty("responseConflict").GetBoolean());
        Assert.Contains("Success", current.GetProperty("firstResponseJson").GetString());
    }

    [Fact]
    public async Task Result_resolves_config_uncertainty_and_late_success_does_not_regress_it()
    {
        using var factory = new MesWebApplicationFactory();
        using var app = Configured(factory);
        using var http = app.CreateClient();
        await CreateAndMarkAsync(app, "Config");
        var journal = app.Services.GetRequiredService<ShineLabCommandJournal>();
        await journal.PrepareAsync(Attempt("request"), default);
        await journal.ReconcileAsync(TaskId, "Config", "request", "ConfigOutcomeUnknown", "timeout", default);
        using (var scope = app.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ShineLabTaskRepository>();
            var sample = Assert.Single(await repo.GetSamplesAsync(TaskId, default));
            await repo.ApplyPushAsync("Result", Equipment, JsonSerializer.SerializeToElement(new
                { sampleId = sample.SampleId, testDate = "t", compounds = new[] { new { concent = 1.0 } } }), default, "result");
        }
        await ReplyAsync(journal, "request", "Success");
        var detail = (await http.GetFromJsonAsync<ShineLabTaskDetailResponse>($"/api/shinelab/tasks/{TaskId}"))!;
        Assert.Equal("AwaitingCompletion", detail.Task.Status);
        Assert.Equal("AllResultsReceived", detail.ResultState);
        Assert.Null(detail.Task.LastError);
        Assert.Null(detail.Task.CompletedAtUtc);
        Assert.Contains(detail.Events, e => e.EventType == "OutcomeUnknown");
        Assert.Contains(detail.Events, e => e.EventType == "ConfigExecutionObservedWithoutAcknowledgement");
        Assert.Contains(detail.Events, e => e.EventType == "ConfigAccepted");
    }

    [Fact]
    public async Task Real_tcp_disconnect_then_late_reply_recovers_without_new_downlink()
    {
        using var factory = new MesWebApplicationFactory();
        using var app = Configured(factory);
        using var http = app.CreateClient();
        await http.PostAsJsonAsync("/api/shinelab/tasks", Request());
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var options = Options.Create(new ShineLabTcpOptions { Enabled = true, ListenAddress = "127.0.0.1", Port = port, StaleAfterSeconds = 60 });
        using var server = new ShineLabTcpServer(options, app.Services.GetRequiredService<ShineLabStatusHub>(),
            app.Services.GetRequiredService<ShineLabConnectionManager>(), NullLogger<ShineLabTcpServer>.Instance,
            app.Services.GetRequiredService<IServiceScopeFactory>());
        await server.StartAsync(default);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string id;
            using (var tcp = new TcpClient())
            {
                await tcp.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
                using var reader = new StreamReader(tcp.GetStream(), Encoding.UTF8, leaveOpen: true);
                await SendAsync(tcp.GetStream(), new { strID = "cert", strMethod = "Certification", equipmentCode = Equipment, body = new { } });
                await reader.ReadLineAsync(deadline.Token);
                var send = http.PostAsync($"/api/shinelab/tasks/{TaskId}/config", null, deadline.Token);
                var raw = (await reader.ReadLineAsync(deadline.Token))!;
                using var json = JsonDocument.Parse(raw);
                id = json.RootElement.GetProperty("strID").GetString()!;
                // Do not reply; HTTP wait expires while the instrument could still apply Config.
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await send).StatusCode);
            }
            using var reconnected = new TcpClient();
            await reconnected.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            // Field reproduction: buffered Config Success arrives BEFORE new Certification.
            await SendAsync(reconnected.GetStream(), new { strID = id, strMethod = "Config", equipmentCode = Equipment, body = new { result = "Success", msg = "" } });
            ShineLabTaskDetailResponse detail;
            do
            {
                detail = (await http.GetFromJsonAsync<ShineLabTaskDetailResponse>($"/api/shinelab/tasks/{TaskId}", deadline.Token))!;
                if (detail.Task.Status != "Configured") await Task.Delay(20, deadline.Token);
            } while (detail.Task.Status != "Configured");
            using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await reconnected.GetStream().ReadAsync(new byte[1], quiet.Token));
            var audit = await http.GetFromJsonAsync<JsonElement>($"/api/shinelab/tasks/{TaskId}/command-audit");
            Assert.Single(audit.GetProperty("attempts").EnumerateArray());
            Assert.Single(audit.GetProperty("replies").EnumerateArray());
        }
        finally { await server.StopAsync(default); }
    }

    [Fact]
    public async Task Existing_database_gains_audit_schema_without_losing_sample_identity_or_result()
    {
        string path;
        string sampleId;
        using (var factory = new MesWebApplicationFactory())
        using (var app = Configured(factory))
        using (var http = app.CreateClient())
        {
            path = factory.DatabasePath;
            await CreateAndMarkAsync(app, "Config");
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var repo = scope.ServiceProvider.GetRequiredService<ShineLabTaskRepository>();
            sampleId = Assert.Single(await repo.GetSamplesAsync(TaskId, default)).SampleId;
            await repo.ApplyPushAsync("Result", Equipment, JsonSerializer.SerializeToElement(new
                { sampleId, testDate = "t", compounds = Array.Empty<object>() }), default, "saved-result");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE ShineLabCommandReplies;");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE ShineLabCommandAttempts;");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE ShineLabTasks DROP COLUMN ConfigRequestId;");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE ShineLabTasks DROP COLUMN CommandRequestId;");
        }
        using var restarted = new MesWebApplicationFactory(path);
        using var restored = Configured(restarted);
        using var client = restored.CreateClient();
        var detail = (await client.GetFromJsonAsync<ShineLabTaskDetailResponse>($"/api/shinelab/tasks/{TaskId}"))!;
        Assert.Equal(sampleId, Assert.Single(detail.Samples!).SampleId);
        Assert.Equal("AllResultsReceived", detail.ResultState);
        var audit = await client.GetFromJsonAsync<JsonElement>($"/api/shinelab/tasks/{TaskId}/command-audit");
        Assert.Empty(audit.GetProperty("attempts").EnumerateArray()); // Never fabricate historical strIDs.
        Assert.Empty(audit.GetProperty("replies").EnumerateArray());
    }

    [Fact]
    public async Task Result_filter_is_applied_before_limit_so_alarm_flood_cannot_hide_task_results()
    {
        using var factory = new MesWebApplicationFactory();
        using var app = Configured(factory);
        using var client = app.CreateClient();
        await CreateAndMarkAsync(app, "Config");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            db.ShineLabPushEvidence.Add(new() { EquipmentCode = Equipment, TaskUuid = TaskId,
                Method = "Result", StrId = "result", RawJson = "{}", ReceivedAtUtcTicks = 1 });
            db.ShineLabPushEvidence.AddRange(Enumerable.Range(2, 510).Select(i => new ShineLabPushEvidence
                { EquipmentCode = Equipment, Method = "AlarmInfo", ReceivedAtUtcTicks = i }));
            await db.SaveChangesAsync();
        }
        var rows = await client.GetFromJsonAsync<List<ShineLabPushEvidence>>(
            $"/api/shinelab/devices/{Equipment}/push-evidence?taskUuid={TaskId}&method=Result&limit=1");
        Assert.Equal("result", Assert.Single(rows!).StrId);
    }

    [Fact]
    public async Task Restart_replays_persisted_but_unapplied_reply_without_connecting_or_sending()
    {
        string path;
        using (var factory = new MesWebApplicationFactory())
        using (var app = Configured(factory))
        using (var client = app.CreateClient())
        {
            path = factory.DatabasePath;
            await CreateAndMarkAsync(app, "Config");
            var journal = app.Services.GetRequiredService<ShineLabCommandJournal>();
            await journal.PrepareAsync(Attempt("request"), default);
            await ReplyAsync(journal, "request", "Success");
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            // Test DB only: reproduce an interruption after durable RX, before task projection.
            await db.ShineLabTasks.Where(t => t.TaskUuid == TaskId).ExecuteUpdateAsync(update => update
                .SetProperty(t => t.ConfigResponseJson, (string?)null).SetProperty(t => t.Status, "Configuring"));
            await db.ShineLabTaskEvents.Where(e => e.TaskUuid == TaskId && e.EventType == "ConfigAccepted").ExecuteDeleteAsync();
        }
        using var restarted = new MesWebApplicationFactory(path);
        using var restored = Configured(restarted);
        using var http = restored.CreateClient();
        var detail = (await http.GetFromJsonAsync<ShineLabTaskDetailResponse>($"/api/shinelab/tasks/{TaskId}"))!;
        Assert.Equal("Configured", detail.Task.Status);
        Assert.Single(detail.Events.Where(e => e.EventType == "ConfigAccepted"));
        Assert.False(restored.Services.GetRequiredService<ShineLabConnectionManager>().GetSnapshot().Connected);
        var audit = await http.GetFromJsonAsync<JsonElement>($"/api/shinelab/tasks/{TaskId}/command-audit");
        Assert.Single(audit.GetProperty("attempts").EnumerateArray());
    }

    [Fact]
    public async Task Canceled_wait_keeps_identity_and_late_reply_can_reconcile()
    {
        using var factory = new MesWebApplicationFactory();
        using var app = Configured(factory);
        using var client = app.CreateClient();
        await CreateAndMarkAsync(app, "Config");
        using var cancel = new CancellationTokenSource();
        using var stream = new MemoryStream();
        var manager = app.Services.GetRequiredService<ShineLabConnectionManager>();
        manager.Register(Equipment, "test", ShineLabWireFormat.LineJson, stream);
        var pending = manager.SendAsync(Equipment, "Config", JsonSerializer.SerializeToElement(new { }),
            TimeSpan.FromSeconds(5), cancel.Token, TaskId);
        var id = await WaitForAttemptAsync(app);
        cancel.Cancel();
        var failure = await Assert.ThrowsAsync<ShineLabCommandCanceledException>(() => pending);
        Assert.Equal(id, failure.StrId);
        var journal = app.Services.GetRequiredService<ShineLabCommandJournal>();
        await journal.ReconcileAsync(TaskId, "Config", id, "ConfigOutcomeUnknown", "canceled", default);
        await ReplyAsync(journal, id, "Success");
        Assert.Equal("Configured", (await client.GetFromJsonAsync<ShineLabTaskDetailResponse>($"/api/shinelab/tasks/{TaskId}"))!.Task.Status);
    }

    [Fact]
    public async Task Failed_audit_preparation_sends_no_bytes()
    {
        using var factory = new MesWebApplicationFactory();
        using var app = Configured(factory);
        using var client = app.CreateClient();
        var manager = app.Services.GetRequiredService<ShineLabConnectionManager>();
        using var stream = new MemoryStream();
        manager.Register(Equipment, "test", ShineLabWireFormat.LineJson, stream);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SendAsync(Equipment, "Config",
            JsonSerializer.SerializeToElement(new { }), TimeSpan.FromSeconds(1), default, "task-does-not-exist"));
        Assert.Equal(0, stream.Length);
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Error")]
    [InlineData("Stopping")]
    public async Task Late_config_ack_does_not_reset_execution_or_fault_state(string status)
    {
        using var factory = new MesWebApplicationFactory();
        using var app = Configured(factory);
        using var client = app.CreateClient();
        await CreateAndMarkAsync(app, "Config");
        var journal = app.Services.GetRequiredService<ShineLabCommandJournal>();
        await journal.PrepareAsync(Attempt("request"), default);
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ShineLabTaskRepository>()
                .MarkOperationStartedAsync(TaskId, status, status, default);
        await journal.ReconcileAsync(TaskId, "Config", "request", "ConfigOutcomeUnknown", "timeout", default);
        await ReplyAsync(journal, "request", "Success");
        var detail = (await client.GetFromJsonAsync<ShineLabTaskDetailResponse>($"/api/shinelab/tasks/{TaskId}"))!;
        Assert.Equal(status, detail.Task.Status);
        Assert.Equal(status, detail.Task.CurrentStage);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [InlineData(2, true)]
    [InlineData(7, true)]
    public async Task Explicit_retry_only_clears_old_end_time_for_accepted_start(int retryAction, bool accepted)
    {
        using var factory = new MesWebApplicationFactory();
        using var app = Configured(factory);
        using var http = app.CreateClient();
        await CreateAndMarkAsync(app, "Command");
        var journal = app.Services.GetRequiredService<ShineLabCommandJournal>();
        async Task Acknowledge(string id, int action, bool success)
        {
            var attempt = Attempt(id);
            attempt.Method = "Command";
            attempt.Action = action;
            await journal.PrepareAsync(attempt, default);
            var body = JsonSerializer.SerializeToElement(new { result = success ? "Success" : "Failed", msg = "" });
            var raw = JsonSerializer.Serialize(new { strID = id, strMethod = "Command", equipmentCode = Equipment, body });
            await journal.RecordReplyAsync(id, "Command", Equipment, body, new(raw, "offline-test", "LineJson", DateTimeOffset.UtcNow), default);
        }
        await Acknowledge("rejected-start", 0, false);
        var rejected = (await http.GetFromJsonAsync<ShineLabTaskDetailResponse>($"/api/shinelab/tasks/{TaskId}"))!;
        Assert.Equal("Failed", rejected.Task.Status);
        Assert.NotNull(rejected.Task.CompletedAtUtc);
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ShineLabTaskRepository>()
                .MarkOperationStartedAsync(TaskId, "Commanding", "Commanding", default);
        await Acknowledge("explicit-retry", retryAction, accepted);
        var detail = (await http.GetFromJsonAsync<ShineLabTaskDetailResponse>($"/api/shinelab/tasks/{TaskId}"))!;
        Assert.Equal(accepted ? retryAction == 2 ? "Stopping" : "Accepted" : "Failed", detail.Task.Status);
        if (accepted && retryAction == 0) Assert.Null(detail.Task.CompletedAtUtc);
        else Assert.NotNull(detail.Task.CompletedAtUtc);
        Assert.Null(detail.Task.StartedAtUtc); // An ACK is not a sample-specific start observation.
        Assert.Contains(detail.Events, e => e.EventType == "CommandRejected");
        var audit = await http.GetFromJsonAsync<JsonElement>($"/api/shinelab/tasks/{TaskId}/command-audit");
        Assert.Equal(2, audit.GetProperty("attempts").GetArrayLength());
        Assert.False(app.Services.GetRequiredService<ShineLabConnectionManager>().GetSnapshot().Connected);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Error")]
    public async Task Late_start_reply_preserves_newer_terminal_evidence(string status)
    {
        using var factory = new MesWebApplicationFactory();
        using var app = Configured(factory);
        using var http = app.CreateClient();
        await CreateAndMarkAsync(app, "Command");
        var journal = app.Services.GetRequiredService<ShineLabCommandJournal>();
        var attempt = Attempt("late-start");
        attempt.Method = "Command"; attempt.Action = 0;
        await journal.PrepareAsync(attempt, default);
        var completedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var task = await db.ShineLabTasks.SingleAsync(t => t.TaskUuid == TaskId);
            task.Status = status; task.CurrentStage = status; task.CompletedAtUtc = completedAt;
            await db.SaveChangesAsync();
        }
        var body = JsonSerializer.SerializeToElement(new { result = "Success" });
        await journal.RecordReplyAsync("late-start", "Command", Equipment, body,
            new("{}", "test", "LineJson", DateTimeOffset.UtcNow), default);
        var detail = (await http.GetFromJsonAsync<ShineLabTaskDetailResponse>($"/api/shinelab/tasks/{TaskId}"))!;
        Assert.Equal(status, detail.Task.Status);
        Assert.Equal(completedAt, detail.Task.CompletedAtUtc);
    }

    private static async Task CreateAndMarkAsync(WebApplicationFactory<Program> app, string method)
    {
        using var scope = app.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ShineLabTaskRepository>();
        await repo.CreateOrGetAsync(Request(), default);
        await repo.MarkOperationStartedAsync(TaskId, method == "Config" ? "Configuring" : "Commanding", method + "uring", default);
    }
    private static ShineLabTaskCreateRequest Request() => new(Equipment, TaskId, [new("source", "synthetic", "0", 1, "1", "")]);
    private static ShineLabCommandAttempt Attempt(string id) => new()
    { StrId = id, Method = "Config", EquipmentCode = Equipment, TaskUuid = TaskId, RawJson = "{}", ConnectionId = "test", WireFormat = "LineJson", PreparedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks };
    private static async Task ReplyAsync(ShineLabCommandJournal journal, string id, string result)
    {
        var body = JsonSerializer.SerializeToElement(new { result });
        var raw = JsonSerializer.Serialize(new { strID = id, strMethod = "Config", equipmentCode = Equipment, body });
        await journal.RecordReplyAsync(id, "Config", Equipment, body, new(raw, "reconnected", "LineJson", DateTimeOffset.UtcNow), default);
    }
    private static async Task<string> WaitForAttemptAsync(WebApplicationFactory<Program> app)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            using var scope = app.Services.CreateScope();
            var attempt = await scope.ServiceProvider.GetRequiredService<MesDbContext>().ShineLabCommandAttempts.SingleOrDefaultAsync(deadline.Token);
            if (attempt is not null) return attempt.StrId;
            await Task.Delay(10, deadline.Token);
        }
    }
    private static async Task SendAsync(Stream stream, object value) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n"));
    private static WebApplicationFactory<Program> Configured(MesWebApplicationFactory factory) => factory.WithWebHostBuilder(builder =>
        builder.ConfigureServices(services => services.PostConfigure<ShineLabTcpOptions>(o =>
        { o.Enabled = false; o.PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel; o.CommandTimeoutMs = 100; })));
}
