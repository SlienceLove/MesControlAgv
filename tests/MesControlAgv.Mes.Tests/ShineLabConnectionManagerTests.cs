using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Tests;

public sealed class ShineLabConnectionManagerTests
{
    [Fact]
    public void Distinct_equipment_codes_stay_registered_side_by_side()
    {
        using var manager = new ShineLabConnectionManager();
        using var firstStream = new MemoryStream();
        using var secondStream = new MemoryStream();

        manager.Register("STN61_01", "conn-1", ShineLabWireFormat.LineJson, firstStream);
        manager.Register("STN61_02", "conn-2", ShineLabWireFormat.LineJson, secondStream);

        var first = manager.GetSnapshot("STN61_01");
        var second = manager.GetSnapshot("STN61_02");
        Assert.True(first.Connected);
        Assert.Equal("conn-1", first.ConnectionId);
        Assert.True(second.Connected);
        Assert.Equal("conn-2", second.ConnectionId);
        Assert.Equal(2, manager.GetSnapshots().Count);
    }

    [Fact]
    public void Reconnect_replaces_only_the_reconnecting_equipment_entry()
    {
        using var manager = new ShineLabConnectionManager();
        using var firstStream = new MemoryStream();
        using var secondStream = new MemoryStream();
        using var reconnectStream = new MemoryStream();

        manager.Register("STN61_01", "conn-1", ShineLabWireFormat.LineJson, firstStream);
        manager.Register("STN61_02", "conn-2", ShineLabWireFormat.LineJson, secondStream);
        manager.Register("STN61_01", "conn-3", ShineLabWireFormat.LineJson, reconnectStream);

        Assert.Equal("conn-3", manager.GetSnapshot("STN61_01").ConnectionId);
        Assert.Equal("conn-2", manager.GetSnapshot("STN61_02").ConnectionId);
        Assert.Equal(2, manager.GetSnapshots().Count);
    }

    [Fact]
    public async Task Closing_one_connection_leaves_another_devices_command_pending()
    {
        using var manager = new ShineLabConnectionManager();
        using var firstStream = new MemoryStream();
        using var secondStream = new MemoryStream();
        manager.Register("STN61_01", "conn-1", ShineLabWireFormat.LineJson, firstStream);
        manager.Register("STN61_02", "conn-2", ShineLabWireFormat.LineJson, secondStream);

        using var body = JsonDocument.Parse("{}");
        var pending = manager.SendAsync(
            "STN61_01",
            "Config",
            body.RootElement,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        // The other device dropping must not fail this command: before the
        // per-connection pending map, every in-flight strID was completed.
        manager.Unregister("conn-2");
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);

        manager.Unregister("conn-1");
        await Assert.ThrowsAsync<IOException>(() => pending);
    }

    [Fact]
    public async Task Reconnecting_the_same_equipment_fails_only_its_own_in_flight_command()
    {
        using var manager = new ShineLabConnectionManager();
        using var firstStream = new MemoryStream();
        using var secondStream = new MemoryStream();
        using var reconnectStream = new MemoryStream();
        manager.Register("STN61_01", "conn-1", ShineLabWireFormat.LineJson, firstStream);
        manager.Register("STN61_02", "conn-2", ShineLabWireFormat.LineJson, secondStream);

        using var body = JsonDocument.Parse("{}");
        var survivor = manager.SendAsync(
            "STN61_02",
            "Config",
            body.RootElement,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        var replaced = manager.SendAsync(
            "STN61_01",
            "Config",
            body.RootElement,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        manager.Register("STN61_01", "conn-3", ShineLabWireFormat.LineJson, reconnectStream);

        await Assert.ThrowsAsync<IOException>(() => replaced);
        Assert.False(survivor.IsCompleted);
    }
}
