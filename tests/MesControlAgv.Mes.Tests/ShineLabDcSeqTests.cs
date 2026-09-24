using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Tests;

public sealed class ShineLabDcSeqTests
{
    private static ShineLabConfigRequest Config() => new("offline-dcseq",
        [new("S1", "sample", "0", 1, "1", "A", "local-method", InjectionVolume: 25)]);

    [Fact]
    public void Single_channel_config_omits_all_channel_fields()
    {
        var request = Config();
        request = request with { SampleData = [request.SampleData[0] with { Channel = "" }] };
        var body = ShineLabConfigPayloadBuilder.Build(request, ShineLabPayloadProfile.DcSeqSingleChannel);
        Assert.Equal("sampleData", Assert.Single(body.EnumerateObject()).Name);
        var sample = Assert.Single(body.GetProperty("sampleData").EnumerateArray());
        Assert.Equal(["sampleId", "sampleName", "type", "position", "mPos"], sample.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(7)]
    public void Single_channel_command_omits_channel(int action)
    {
        var body = ShineLabCommandPayloadBuilder.Build(new("single", action), ShineLabPayloadProfile.DcSeqSingleChannel);
        var property = Assert.Single(body.EnumerateObject());
        Assert.Equal("action", property.Name);
        Assert.Equal(action, property.Value.GetInt32());
    }

    [Fact]
    public void Single_channel_profile_does_not_silently_discard_a_channel()
    {
        Assert.Throws<ArgumentException>(() => ShineLabConfigPayloadBuilder.Build(Config(), ShineLabPayloadProfile.DcSeqSingleChannel));
        Assert.Throws<ArgumentException>(() => ShineLabCommandPayloadBuilder.Build(new("single", 0, Channel: "A"), ShineLabPayloadProfile.DcSeqSingleChannel));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShineLabCommandPayloadBuilder.Build(new("single", 1), ShineLabPayloadProfile.DcSeqSingleChannel));
    }

    [Fact]
    public void Config_matches_new_vendor_field_names_and_excludes_local_metadata()
    {
        var body = ShineLabConfigPayloadBuilder.Build(Config(), ShineLabPayloadProfile.DcSeq);
        Assert.Equal(["channel", "sampleData"], body.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("A", body.GetProperty("channel").GetString());
        var sample = Assert.Single(body.GetProperty("sampleData").EnumerateArray());
        Assert.Equal(["sampleId", "sampleName", "type", "position", "mPos"],
            sample.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("S1", sample.GetProperty("sampleId").GetString());
        Assert.Equal(0, sample.GetProperty("type").GetInt32());
    }

    [Theory]
    [InlineData("B", "S2", "0")]
    [InlineData("A", "S1", "0")]
    [InlineData("A", "", "0")]
    [InlineData("A", "S2", "invalid")]
    [InlineData("A", "S2", "6")]
    [InlineData("A", "S2", "-1")]
    public void Config_rejects_ambiguous_or_invalid_samples(string channel, string sampleId, string type)
    {
        var request = Config();
        request = request with { SampleData = [request.SampleData[0], new(sampleId, "second", type, 2, "2", channel)] };
        Assert.Throws<ArgumentException>(() => ShineLabConfigPayloadBuilder.Build(request, ShineLabPayloadProfile.DcSeq));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(7)]
    public void Command_uses_channel_and_only_confirmed_actions(int action)
    {
        var body = ShineLabCommandPayloadBuilder.Build(new("local-task", action, Channel: "A"), ShineLabPayloadProfile.DcSeq);
        Assert.Equal(["channel", "action"], body.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(action, body.GetProperty("action").GetInt32());
    }

    [Fact]
    public void Unconfirmed_action_and_unknown_profile_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShineLabCommandPayloadBuilder.Build(
            new("local-task", 1, Channel: "A"), ShineLabPayloadProfile.DcSeq));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShineLabConfigPayloadBuilder.Build(Config(), (ShineLabPayloadProfile)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShineLabCommandPayloadBuilder.Build(
            new("local-task", 0, Channel: "A"), (ShineLabPayloadProfile)999));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Service_sends_selected_profile_with_LF_and_correlates_single_response(bool config)
    {
        using var manager = new ShineLabConnectionManager();
        using var stream = new MemoryStream();
        manager.Register("STN61_01", "offline", ShineLabWireFormat.LineJson, stream);
        var service = new ShineLabCommandService(manager, Options.Create(new ShineLabTcpOptions
        {
            PayloadProfile = ShineLabPayloadProfile.DcSeq,
            CommandTimeoutMs = 3000
        }));
        var pending = config
            ? service.SendConfigAsync("STN61_01", Config(), CancellationToken.None)
            : service.SendCommandAsync("STN61_01", new("local-task", 2, Channel: "A"), CancellationToken.None);
        for (var attempt = 0; attempt < 100 && stream.Length == 0; attempt++)
            await Task.Delay(5);
        var bytes = stream.ToArray();
        Assert.NotEmpty(bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        using var message = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        var root = message.RootElement;
        Assert.Equal("STN61_01", root.GetProperty("equipmentCode").GetString());
        Assert.Equal("A", root.GetProperty("body").GetProperty("channel").GetString());
        Assert.False(root.GetProperty("body").TryGetProperty("chan", out _));
        using var reply = JsonDocument.Parse("{\"result\":\"Success\",\"msg\":\"\"}");
        Assert.True(manager.TryCompleteResponse(root.GetProperty("strID").GetString()!,
            config ? "Config" : "Command", "STN61_01", reply.RootElement));
        Assert.True((await pending).Success);
    }
}

public sealed class ShineLabDcSeqPreviewTests(MesWebApplicationFactory factory)
    : IClassFixture<MesWebApplicationFactory>
{
    [Theory]
    [InlineData(ShineLabPayloadProfile.DcSeq)]
    [InlineData(ShineLabPayloadProfile.DcSeqSingleChannel)]
    public async Task Preview_uses_effective_profile_without_creating_tasks_or_writing_to_device(ShineLabPayloadProfile profile)
    {
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<ShineLabTcpOptions>(options => options.PayloadProfile = profile)));
        using var client = app.CreateClient();
        var manager = app.Services.GetRequiredService<ShineLabConnectionManager>();
        using var stream = new MemoryStream();
        manager.Register("STN61_01", "preview-only", ShineLabWireFormat.LineJson, stream);
        var taskId = $"preview-{Guid.NewGuid():N}";
        var channel = profile == ShineLabPayloadProfile.DcSeq ? "A" : "";
        var request = new ShineLabConfigRequest(taskId, [new("S1", "sample", "0", 1, "1", channel)]);
        using var response = await client.PostAsJsonAsync("/api/shinelab/preview/config", request);
        response.EnsureSuccessStatusCode();
        var preview = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(profile.ToString(), preview.GetProperty("payloadProfile").GetString());
        Assert.Equal(profile == ShineLabPayloadProfile.DcSeq, preview.GetProperty("body").TryGetProperty("channel", out _));
        using var command = await client.PostAsJsonAsync("/api/shinelab/preview/command",
            new ShineLabCommandRequest(taskId, 0, Channel: channel));
        command.EnsureSuccessStatusCode();
        var commandBody = await command.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(profile == ShineLabPayloadProfile.DcSeq, commandBody.GetProperty("body").TryGetProperty("channel", out _));
        using var rejected = await client.PostAsJsonAsync("/api/shinelab/preview/command",
            new ShineLabCommandRequest(taskId, 1, Channel: channel));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(0, stream.Length);
        using var task = await client.GetAsync($"/api/shinelab/tasks/{taskId}");
        Assert.Equal(HttpStatusCode.NotFound, task.StatusCode);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/shinelab/server/status");
        Assert.Equal(profile.ToString(), status.GetProperty("payloadProfile").GetString());
    }
}
