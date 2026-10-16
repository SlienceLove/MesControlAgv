using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Tests;

public sealed class ShineLabAutoCurveValidationTests
{
    private const string Equipment = "STN61_01";
    private const string Standard = "厂家测试标准-01";
    private static ShineLabConfigRequest Config(string? standard = null) =>
        new("test-standard", [new("source-1", "offline-only", "0", 1, "1", "")], Standard: standard);

    [Fact]
    public void Omitted_standard_preserves_legacy_serialization_and_wire_body()
    {
        var request = Config();
        var legacy = JsonSerializer.Serialize(new { request.TaskUuid, request.SampleData,
            request.InstrumentMethod, request.ProcessingMethod, request.DetectionMethod });
        Assert.Equal(legacy, JsonSerializer.Serialize(request));
        Assert.False(ShineLabConfigPayloadBuilder.Build(request, ShineLabPayloadProfile.DcSeqSingleChannel)
            .TryGetProperty("standard", out _));
    }

    [Theory]
    [InlineData(ShineLabPayloadProfile.DcSeqSingleChannel, "")]
    [InlineData(ShineLabPayloadProfile.DcSeq, "A")]
    public void Standard_is_an_explicit_config_field_and_not_a_method_alias(ShineLabPayloadProfile profile, string channel)
    {
        var request = Config(Standard) with
        {
            SampleData = [Config().SampleData[0] with { Channel = channel }],
            InstrumentMethod = "local-instrument", ProcessingMethod = "local-processing", DetectionMethod = "local-standard"
        };
        var body = ShineLabConfigPayloadBuilder.Build(request, profile);
        Assert.Equal(Standard, body.GetProperty("standard").GetString());
        Assert.Equal(profile == ShineLabPayloadProfile.DcSeq, body.TryGetProperty("channel", out _));
        Assert.DoesNotContain("local-", body.GetRawText());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Empty_standard_is_rejected_until_vendor_defines_its_meaning(string standard) =>
        Assert.Throws<ArgumentException>(() => ShineLabConfigPayloadBuilder.Build(Config(standard), ShineLabPayloadProfile.DcSeqSingleChannel));

    [Fact]
    public void Unsupported_profiles_and_ambiguous_channels_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => ShineLabConfigPayloadBuilder.Build(Config(Standard), ShineLabPayloadProfile.Rike));
        Assert.Throws<ArgumentException>(() => ShineLabAutoCurvePayloadBuilder.Build(new(), ShineLabPayloadProfile.Rike));
        Assert.Throws<ArgumentException>(() => ShineLabAutoCurvePayloadBuilder.Build(new("A"), ShineLabPayloadProfile.DcSeqSingleChannel));
        Assert.Throws<ArgumentException>(() => ShineLabAutoCurvePayloadBuilder.Build(new(), ShineLabPayloadProfile.DcSeq));
        Assert.Equal("{}", ShineLabAutoCurvePayloadBuilder.Build(new(), ShineLabPayloadProfile.DcSeqSingleChannel).GetRawText());
        Assert.Equal("{\"channel\":\"A\"}", ShineLabAutoCurvePayloadBuilder.Build(new("A"), ShineLabPayloadProfile.DcSeq).GetRawText());
    }

    [Fact]
    public async Task Stored_standard_survives_reload_changes_fingerprint_and_disabled_send_does_not_arm_sample()
    {
        var dbOptions = new DbContextOptionsBuilder<MesDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new MesDbContext(dbOptions);
        var options = Options.Create(new ShineLabTcpOptions { PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel });
        var repo = new ShineLabTaskRepository(db, options);
        var request = new ShineLabTaskCreateRequest(Equipment, "standard-task", Config().SampleData, Standard: Standard);
        await repo.CreateOrGetAsync(request, default);
        db.ChangeTracker.Clear();
        var replay = await repo.CreateOrGetAsync(request, default);
        Assert.False(replay.Created);
        var config = JsonSerializer.Deserialize<ShineLabConfigRequest>(replay.Task.ConfigJson)!;
        Assert.Equal(Standard, config.Standard);
        Assert.Equal(Standard, ShineLabConfigPayloadBuilder.Build(config, options.Value.PayloadProfile).GetProperty("standard").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.CreateOrGetAsync(request with { Standard = "other" }, default));
        using var manager = new ShineLabConnectionManager();
        using var stream = new MemoryStream();
        manager.Register(Equipment, "test", ShineLabWireFormat.LineJson, stream);
        var tasks = new ShineLabTaskService(repo, new ShineLabCommandService(manager, options));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tasks.ConfigureAsync(request.TaskUuid, default));
        Assert.Equal("Created", (await repo.GetAsync(request.TaskUuid, default))!.Status);
        Assert.False(Assert.Single(await repo.GetSamplesAsync(request.TaskUuid, default)).ConfigAttempted);
        Assert.Equal(0, stream.Length);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 99)]
    [InlineData(true, -1)]
    public async Task AutoCurve_disabled_or_without_idle_evidence_sends_no_bytes(bool enabled, int status)
    {
        var options = Options.Create(new ShineLabTcpOptions
        { EnableAutoCurve = enabled, PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel });
        var hub = new ShineLabStatusHub(options);
        hub.Apply("state", status == -1 ? "Certification" : "Device", Equipment, JsonSerializer.SerializeToElement(new { status }));
        using var manager = new ShineLabConnectionManager();
        using var stream = new MemoryStream();
        manager.Register(Equipment, "test", ShineLabWireFormat.LineJson, stream);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ShineLabCommandService(manager, options, hub)
            .SendAutoCurveAsync(Equipment, new(), default));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task Http_previews_work_while_live_features_are_disabled_and_send_nothing()
    {
        using var factory = new MesWebApplicationFactory();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<ShineLabTcpOptions>(o => { o.Enabled = false; o.PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel; })));
        using var http = app.CreateClient();
        using var stream = new MemoryStream();
        app.Services.GetRequiredService<ShineLabConnectionManager>().Register(Equipment, "test", ShineLabWireFormat.LineJson, stream);
        var config = await http.PostAsJsonAsync("/api/shinelab/preview/config", Config(Standard));
        config.EnsureSuccessStatusCode();
        Assert.Equal(Standard, (await config.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("body").GetProperty("standard").GetString());
        var curve = await http.PostAsJsonAsync("/api/shinelab/preview/auto-curve", new ShineLabAutoCurveRequest());
        curve.EnsureSuccessStatusCode();
        var json = await curve.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AutoCurve", json.GetProperty("strMethod").GetString());
        Assert.Equal("{}", json.GetProperty("body").GetRawText());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.PostAsJsonAsync($"/api/shinelab/devices/{Equipment}/auto-curve", new ShineLabAutoCurveRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.PostAsJsonAsync($"/api/shinelab/devices/{Equipment}/config", Config(Standard))).StatusCode);
        Assert.Empty((await http.GetFromJsonAsync<JsonElement>($"/api/shinelab/devices/{Equipment}/command-attempts")).EnumerateArray());
        Assert.Equal(0, stream.Length);
    }

    [Theory]
    [InlineData("Success")]
    [InlineData("Failed")]
    [InlineData("LateSuccess")]
    public async Task Loopback_client_receives_standard_then_AutoCurve_and_replies_are_durable_without_task_completion(string outcome)
    {
        using var factory = new MesWebApplicationFactory();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<ShineLabTcpOptions>(o =>
            {
                o.Enabled = false; o.PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel;
                o.EnableStandardSelection = true; o.EnableAutoCurve = true; o.EnableTrayCommands = true;
                o.CommandTimeoutMs = outcome == "LateSuccess" ? 500 : 5000;
            })));
        using var http = app.CreateClient();
        var created = await http.PostAsJsonAsync("/api/shinelab/tasks", new ShineLabTaskCreateRequest(Equipment,
            "tcp-standard", Config().SampleData, Standard: Standard));
        created.EnsureSuccessStatusCode();
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var hub = app.Services.GetRequiredService<ShineLabStatusHub>();
        using var server = new ShineLabTcpServer(Options.Create(new ShineLabTcpOptions
            { Enabled = true, ListenAddress = "127.0.0.1", Port = port, StaleAfterSeconds = 60 }),
            hub, app.Services.GetRequiredService<ShineLabConnectionManager>(), NullLogger<ShineLabTcpServer>.Instance,
            app.Services.GetRequiredService<IServiceScopeFactory>());
        await server.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            await using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            async Task Send(string id, string method, object body) => await stream.WriteAsync(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { strID = id, strMethod = method, equipmentCode = Equipment, body }) + "\n"), timeout.Token);
            await Send("cert", "Certification", new { });
            using var cert = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            await Send("idle", "Device", new { status = 0 });
            while (!hub.HasFreshIdleDeviceStatus(Equipment, TimeSpan.FromSeconds(10))) await Task.Delay(10, timeout.Token);
            var configuring = http.PostAsync("/api/shinelab/tasks/tcp-standard/config", null, timeout.Token);
            using var config = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            Assert.Equal("Config", config.RootElement.GetProperty("strMethod").GetString());
            Assert.Equal(Standard, config.RootElement.GetProperty("body").GetProperty("standard").GetString());
            await Send(config.RootElement.GetProperty("strID").GetString()!, "Config", new { result = "Success", msg = "" });
            (await configuring).EnsureSuccessStatusCode();

            var sending = http.PostAsJsonAsync($"/api/shinelab/devices/{Equipment}/auto-curve", new ShineLabAutoCurveRequest(), timeout.Token);
            using var command = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            var id = command.RootElement.GetProperty("strID").GetString()!;
            Assert.Equal("AutoCurve", command.RootElement.GetProperty("strMethod").GetString());
            Assert.Equal("{}", command.RootElement.GetProperty("body").GetRawText());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.PostAsJsonAsync($"/api/shinelab/devices/{Equipment}/auto-curve", new ShineLabAutoCurveRequest(), timeout.Token)).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.PostAsJsonAsync($"/api/shinelab/devices/{Equipment}/command",
                new ShineLabCommandRequest("tray", 0, StrAction: "TrayPopup"), timeout.Token)).StatusCode);
            if (outcome == "LateSuccess") Assert.Equal(HttpStatusCode.ServiceUnavailable, (await sending).StatusCode);
            var result = outcome == "Failed" ? "Failed" : "Success";
            await Send(id, "AutoCurve", new { result, msg = "synthetic response" });
            if (outcome != "LateSuccess")
            {
                var response = await sending;
                response.EnsureSuccessStatusCode();
                Assert.Equal(result == "Success", (await response.Content.ReadFromJsonAsync<ShineLabCommandResponse>())!.Success);
            }
            JsonElement[] replies;
            do
            {
                replies = (await http.GetFromJsonAsync<JsonElement>($"/api/shinelab/devices/{Equipment}/command-replies", timeout.Token))
                    .EnumerateArray().Where(r => r.GetProperty("method").GetString() == "AutoCurve").ToArray();
                if (replies.Length == 0) await Task.Delay(10, timeout.Token);
            } while (replies.Length == 0);
            Assert.Equal("Matched", Assert.Single(replies).GetProperty("correlation").GetString());
            Assert.Contains("synthetic response", replies[0].GetProperty("rawJson").GetString());
            var attempts = await http.GetFromJsonAsync<JsonElement>($"/api/shinelab/devices/{Equipment}/command-attempts", timeout.Token);
            var attempt = Assert.Single(attempts.EnumerateArray().Where(a => a.GetProperty("method").GetString() == "AutoCurve"));
            Assert.Equal(JsonValueKind.Null, attempt.GetProperty("taskUuid").ValueKind);
            Assert.Equal(outcome == "LateSuccess" ? "TimedOut" : "ReplyWithinWait", attempt.GetProperty("waitOutcome").GetString());
            var task = (await http.GetFromJsonAsync<ShineLabTaskDetailResponse>("/api/shinelab/tasks/tcp-standard", timeout.Token))!;
            Assert.Equal("Configured", task.Task.Status);
            Assert.Null(task.Task.CompletedAtUtc);
            using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadAsync(new byte[1], quiet.Token));
        }
        finally { await server.StopAsync(default); }
    }
}
