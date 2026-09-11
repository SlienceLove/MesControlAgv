using System.Text;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Tests;

public sealed class ShineLabCommandPayloadBuilderTests
{
    [Fact]
    public void Builds_the_strict_rike_config_body_without_local_metadata()
    {
        var request = new ShineLabConfigRequest(
            "task-config-001",
            [
                new ShineLabSampleData(
                    "S-01", "标准样", "1", 1, "1", "A",
                    "AS18-M01", "IC-P01", "Normal", 25m, "uL")
            ],
            "AS18-M01",
            "IC-P01",
            "Normal");

        var body = ShineLabConfigPayloadBuilder.Build(request);

        Assert.Equal(2, body.EnumerateObject().Count());
        Assert.Equal("A", body.GetProperty("chan").GetString());
        var sample = Assert.Single(body.GetProperty("sampleData").EnumerateArray());
        Assert.Equal(6, sample.EnumerateObject().Count());
        Assert.Equal("S-01", sample.GetProperty("sampleID").GetString());
        Assert.Equal("标准样", sample.GetProperty("sampleName").GetString());
        Assert.Equal(1, sample.GetProperty("type").GetInt32());
        Assert.Equal(1, sample.GetProperty("position").GetInt32());
        Assert.Equal("1", sample.GetProperty("mPos").GetString());
        Assert.Equal("A", sample.GetProperty("Channel").GetString());
        Assert.DoesNotContain(sample.EnumerateObject(), property =>
            property.NameEquals("instrumentMethod") ||
            property.NameEquals("processingMethod") ||
            property.NameEquals("detectionMethod") ||
            property.NameEquals("injectionVolume") ||
            property.NameEquals("injectionVolumeUnit"));
        Assert.DoesNotContain(body.EnumerateObject(), property =>
            property.NameEquals("task_uuid") ||
            property.NameEquals("instrumentMethod") ||
            property.NameEquals("processingMethod") ||
            property.NameEquals("detectionMethod"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(7)]
    public void Builds_the_strict_rike_body_for_supported_actions(int action)
    {
        var request = new ShineLabCommandRequest(
            "task-001",
            action,
            SampleId: "S-01",
            SampleName: "标准样",
            Channel: "a",
            DetectionMethod: "method-01",
            CleanTime: "30");

        var body = ShineLabCommandPayloadBuilder.Build(request);

        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        Assert.Equal(2, body.EnumerateObject().Count());
        Assert.Equal("a", body.GetProperty("chan").GetString());
        Assert.Equal(action, body.GetProperty("action").GetInt32());
        Assert.DoesNotContain(body.EnumerateObject(), property =>
            property.NameEquals("task_uuid") ||
            property.NameEquals("sampleID") ||
            property.NameEquals("sampleName") ||
            property.NameEquals("detectionMethod") ||
            property.NameEquals("cleanTime"));
    }

    [Fact]
    public void Rejects_an_action_that_is_not_in_the_rike_template()
    {
        var request = new ShineLabCommandRequest("task-001", 1, Channel: "A");

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => ShineLabCommandPayloadBuilder.Build(request));

        Assert.Equal(1, exception.ActualValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Rejects_a_missing_channel(string channel)
    {
        var request = new ShineLabCommandRequest("task-001", 0, Channel: channel);

        Assert.Throws<ArgumentException>(() => ShineLabCommandPayloadBuilder.Build(request));
    }

    [Fact]
    public async Task Command_service_writes_the_strict_body_inside_the_line_json_envelope()
    {
        using var manager = new ShineLabConnectionManager();
        using var stream = new MemoryStream();
        manager.Register("STN61_01", "conn-1", ShineLabWireFormat.LineJson, stream);
        var service = new ShineLabCommandService(
            manager,
            Options.Create(new ShineLabTcpOptions { CommandTimeoutMs = 1000 }));
        var request = new ShineLabCommandRequest(
            "task-001",
            0,
            SampleId: "S-01",
            SampleName: "标准样",
            Channel: "A",
            DetectionMethod: "method-01",
            CleanTime: "30");

        var pending = service.SendCommandAsync("STN61_01", request, CancellationToken.None);
        await WaitForWriteAsync(stream);

        var sentJson = Encoding.UTF8.GetString(stream.ToArray(), 0, checked((int)stream.Length - 1));
        using var sent = JsonDocument.Parse(sentJson);
        var root = sent.RootElement;
        Assert.Equal("Command", root.GetProperty("strMethod").GetString());
        Assert.Equal("STN61_01", root.GetProperty("equipmentCode").GetString());
        var body = root.GetProperty("body");
        Assert.Equal(2, body.EnumerateObject().Count());
        Assert.Equal("A", body.GetProperty("chan").GetString());
        Assert.Equal(0, body.GetProperty("action").GetInt32());

        using var response = JsonDocument.Parse("{\"result\":\"Success\",\"msg\":\"\"}");
        Assert.True(manager.TryCompleteResponse(
            root.GetProperty("strID").GetString()!,
            "Command",
            "STN61_01",
            response.RootElement));
        var result = await pending;

        Assert.True(result.Success);
    }

    private static async Task WaitForWriteAsync(MemoryStream stream)
    {
        for (var attempt = 0; attempt < 100 && stream.Length == 0; attempt++)
            await Task.Delay(5);
        Assert.True(stream.Length > 0, "The command was not written to the connection stream.");
    }
}
