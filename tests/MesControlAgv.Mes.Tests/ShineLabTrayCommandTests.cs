using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Tests;

public sealed class ShineLabTrayCommandTests
{
    private const string Equipment = "STN61_01";

    [Theory]
    [InlineData("TrayPopup")]
    [InlineData("TrayReset")]
    public void String_action_takes_precedence_and_omits_numeric_start_and_single_channel(string action)
    {
        var body = ShineLabCommandPayloadBuilder.Build(new("local-operation", -999, StrAction: action),
            ShineLabPayloadProfile.DcSeqSingleChannel);
        Assert.Equal(2, body.EnumerateObject().Count());
        Assert.Equal(action, body.GetProperty("strAction").GetString());
        Assert.Equal("", body.GetProperty("param").GetString());
        Assert.False(body.TryGetProperty("action", out _));
        Assert.False(body.TryGetProperty("channel", out _));
        Assert.False(body.TryGetProperty("task_uuid", out _));
    }

    [Theory]
    [InlineData("TrayPopuo")]
    [InlineData("Traypopup")]
    [InlineData("TrayReset ")]
    [InlineData(" ")]
    [InlineData("Init")]
    [InlineData("Start")]
    [InlineData("Stop")]
    [InlineData("Clean")]
    [InlineData("TurnTo")]
    public void Unknown_or_out_of_scope_strings_never_fall_back_to_numeric_start(string action) =>
        Assert.Throws<ArgumentException>(() => ShineLabCommandPayloadBuilder.Build(
            new("operation", 0, StrAction: action), ShineLabPayloadProfile.DcSeqSingleChannel));

    [Fact]
    public void Existing_numeric_command_body_remains_unchanged()
    {
        var body = ShineLabCommandPayloadBuilder.Build(new("operation", 2, StrAction: ""),
            ShineLabPayloadProfile.DcSeqSingleChannel);
        Assert.Equal("{\"action\":2}", body.GetRawText());
        Assert.Throws<ArgumentException>(() => ShineLabCommandPayloadBuilder.Build(
            new("operation", 0, Param: "1"), ShineLabPayloadProfile.DcSeqSingleChannel));
        Assert.Throws<ArgumentException>(() => ShineLabCommandPayloadBuilder.Build(
            new("operation", 0, Channel: "A", StrAction: "TrayPopup"), ShineLabPayloadProfile.Rike));
        Assert.Throws<ArgumentException>(() => ShineLabCommandPayloadBuilder.Build(
            new("operation", 0, StrAction: "TrayPopup", Param: "1"), ShineLabPayloadProfile.DcSeqSingleChannel));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 99)]
    [InlineData(true, -1)]
    public async Task Disabled_running_fault_offline_or_certification_only_send_nothing(bool enabled, int status)
    {
        var options = Options.Create(new ShineLabTcpOptions
        { PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel, EnableTrayCommands = enabled });
        var hub = new ShineLabStatusHub(options);
        if (status == -1) hub.Apply("cert", "Certification", Equipment, JsonSerializer.SerializeToElement(new { }));
        else hub.Apply("device", "Device", Equipment, JsonSerializer.SerializeToElement(new { status }));
        using var manager = new ShineLabConnectionManager();
        using var stream = new MemoryStream();
        manager.Register(Equipment, "test", ShineLabWireFormat.LineJson, stream);
        var service = new ShineLabCommandService(manager, options, hub);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendCommandAsync(Equipment,
            new("operation", 0, StrAction: "TrayPopup"), default));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void Heartbeat_and_reconnect_cannot_turn_old_idle_into_fresh_instrument_status()
    {
        var hub = new ShineLabStatusHub(Options.Create(new ShineLabTcpOptions { PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel }));
        hub.Apply("idle", "Device", Equipment, JsonSerializer.SerializeToElement(new { status = 0 }), "old");
        Assert.True(hub.HasFreshIdleDeviceStatus(Equipment, TimeSpan.FromSeconds(1)));
        hub.Apply("heartbeat", "Heart", Equipment, JsonSerializer.SerializeToElement(new { }), "old");
        Assert.False(hub.HasFreshIdleDeviceStatus(Equipment, TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow.AddSeconds(2)));
        hub.Apply("reconnect", "Heart", Equipment, JsonSerializer.SerializeToElement(new { }), "new");
        Assert.False(hub.HasFreshIdleDeviceStatus(Equipment, TimeSpan.FromSeconds(60)));
        hub.Apply("idle-new", "Device", Equipment, JsonSerializer.SerializeToElement(new { status = 0 }), "new");
        Assert.True(hub.HasFreshIdleDeviceStatus(Equipment, TimeSpan.FromSeconds(60)));
        hub.MarkDisconnected(Equipment, "new");
        Assert.False(hub.HasFreshIdleDeviceStatus(Equipment, TimeSpan.FromSeconds(60)));
    }

    [Theory]
    [InlineData("TrayPopup")]
    [InlineData("TrayReset")]
    public async Task Device_tray_command_is_audited_but_never_advances_sample_task(string action)
    {
        using var factory = new MesWebApplicationFactory();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<ShineLabTcpOptions>(options =>
            {
                options.Enabled = false; options.EnableTrayCommands = true;
                options.PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel;
            })));
        using var http = app.CreateClient();
        var created = await http.PostAsJsonAsync("/api/shinelab/tasks", new ShineLabTaskCreateRequest(Equipment,
            "existing-sample-task", [new("source", "synthetic", "0", 1, "1", "")]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var request = new ShineLabCommandRequest("existing-sample-task", 0, StrAction: action);
        var taskRoute = await http.PostAsJsonAsync("/api/shinelab/tasks/existing-sample-task/command", request);
        Assert.Equal(HttpStatusCode.BadRequest, taskRoute.StatusCode);
        var preview = await http.PostAsJsonAsync("/api/shinelab/preview/command", request);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var hub = app.Services.GetRequiredService<ShineLabStatusHub>();
        var manager = app.Services.GetRequiredService<ShineLabConnectionManager>();
        using var stream = new MemoryStream();
        manager.Register(Equipment, "test", ShineLabWireFormat.LineJson, stream);
        hub.Apply("device", "Device", Equipment, JsonSerializer.SerializeToElement(new { status = 0 }), "test");
        var send = http.PostAsJsonAsync($"/api/shinelab/devices/{Equipment}/command", request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (stream.Length == 0) await Task.Delay(10, timeout.Token);
        var bytes = stream.ToArray();
        using var sent = JsonDocument.Parse(bytes);
        Assert.Equal(action, sent.RootElement.GetProperty("body").GetProperty("strAction").GetString());
        Assert.False(sent.RootElement.GetProperty("body").TryGetProperty("action", out _));
        var second = await http.PostAsJsonAsync($"/api/shinelab/devices/{Equipment}/command", request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(bytes.Length, stream.Length);
        var id = sent.RootElement.GetProperty("strID").GetString()!;
        var body = JsonSerializer.SerializeToElement(new { result = "Success", msg = "" });
        var rawReply = JsonSerializer.Serialize(new { strID = id, strMethod = "Command", equipmentCode = Equipment, body });
        await manager.RecordReplyAsync(id, "Command", Equipment, body,
            new(rawReply, "test", "LineJson", DateTimeOffset.UtcNow), default);
        Assert.True(manager.TryCompleteResponse(id, "Command", Equipment, body));
        Assert.Equal(HttpStatusCode.OK, (await send).StatusCode);
        var attempts = await http.GetFromJsonAsync<JsonElement>($"/api/shinelab/devices/{Equipment}/command-attempts?limit=10");
        var attempt = Assert.Single(attempts.EnumerateArray());
        Assert.Equal(id, attempt.GetProperty("strId").GetString());
        Assert.Equal(JsonValueKind.Null, attempt.GetProperty("action").ValueKind);
        Assert.Equal(JsonValueKind.Null, attempt.GetProperty("taskUuid").ValueKind);
        Assert.Equal(Encoding.UTF8.GetString(bytes).TrimEnd('\n'), attempt.GetProperty("rawJson").GetString());
        var detail = (await http.GetFromJsonAsync<ShineLabTaskDetailResponse>("/api/shinelab/tasks/existing-sample-task"))!;
        Assert.Equal("Created", detail.Task.Status);
        Assert.Single(detail.Events);
        Assert.Null(detail.Task.StartedAtUtc);
        Assert.Null(detail.Task.CompletedAtUtc);
    }
}
