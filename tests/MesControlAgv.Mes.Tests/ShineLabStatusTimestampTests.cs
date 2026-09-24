using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Tests;

public sealed class ShineLabStatusTimestampTests
{
    [Theory]
    [InlineData("Device")]
    [InlineData("UpdateInfo")]
    public void Status_timestamp_is_exposed_and_heartbeats_do_not_refresh_it(string method)
    {
        var hub = new ShineLabStatusHub(Options.Create(new ShineLabTcpOptions { PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel }));
        hub.Apply("1", method, "STN61_01", JsonSerializer.SerializeToElement(new { status = 1, sampleId = "123456789" }), "connection-1");
        var running = hub.GetStatus("STN61_01")!;
        Assert.NotNull(running.LastDeviceStatusAtUtc);
        hub.Apply("2", "Heart", "STN61_01", JsonSerializer.SerializeToElement(new { }), "connection-1");
        var heartbeat = hub.GetStatus("STN61_01")!;
        Assert.Equal(running.LastDeviceStatusAtUtc, heartbeat.LastDeviceStatusAtUtc);
        Assert.True(heartbeat.LastSeenAtUtc >= running.LastSeenAtUtc);
        Assert.Equal("123456789", heartbeat.SampleId);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(heartbeat, options);
        Assert.Contains("lastDeviceStatusAtUtc", json);
        Assert.Equal(running.LastDeviceStatusAtUtc, JsonSerializer.Deserialize<ShineLabDeviceStatusResponse>(json, options)!.LastDeviceStatusAtUtc);
        hub.Apply("3", "Device", "STN61_01", JsonSerializer.SerializeToElement(new { unrelated = 1 }), "connection-1");
        Assert.Equal(running.LastDeviceStatusAtUtc, hub.GetStatus("STN61_01")!.LastDeviceStatusAtUtc);
        hub.Apply("4", "Heart", "STN61_01", JsonSerializer.SerializeToElement(new { }), "connection-2");
        Assert.Null(hub.GetStatus("STN61_01")!.LastDeviceStatusAtUtc);
        hub.Apply("5", "Device", "STN61_01", JsonSerializer.SerializeToElement(new { status = 0 }), "connection-2");
        Assert.NotNull(hub.GetStatus("STN61_01")!.LastDeviceStatusAtUtc);
        hub.Apply("6", "Certification", "STN61_01", JsonSerializer.SerializeToElement(new { }), "connection-2");
        Assert.Null(hub.GetStatus("STN61_01")!.LastDeviceStatusAtUtc);
    }
}
