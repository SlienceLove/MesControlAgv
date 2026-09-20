using System.IO;
using System.Text.Json;
using MesControlAgv.Wpf.DigitalTwin;

namespace MesControlAgv.Wpf.Tests;

public sealed class DigitalTwinSceneTests
{
    [Theory]
    [InlineData("https://digital-twin.local/index.html", true)]
    [InlineData("https://digital-twin.local/vendor/three/build/three.module.min.js", true)]
    [InlineData("http://digital-twin.local/index.html", false)]
    [InlineData("https://digital-twin.local.evil.test/index.html", false)]
    [InlineData("https://digital-twin.local:8443/index.html", false)]
    [InlineData("https://user@digital-twin.local/index.html", false)]
    [InlineData("http://127.0.0.1:5141/api/status", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    public void Viewer_only_accepts_its_local_https_origin(string uri, bool accepted) =>
        Assert.Equal(accepted, DigitalTwinScene.IsLocalResource(uri));

    [Theory]
    [InlineData("{\"type\":\"selected\",\"id\":\"agv-composite-a\"}", true)]
    [InlineData("{\"type\":\"selected\",\"id\":\"autosampler-a\"}", true)]
    [InlineData("{\"type\":\"selected\",\"id\":\"d160-b\"}", false)]
    [InlineData("{\"type\":\"selected\",\"id\":\"STN61_01\"}", false)]
    [InlineData("{\"type\":\"selected\",\"id\":null}", true)]
    [InlineData("{\"type\":\"ready\"}", true)]
    [InlineData("{\"type\":\"load-error\"}", true)]
    [InlineData("{\"type\":\"fullscreen-toggle\"}", true)]
    [InlineData("{\"type\":\"fullscreen-exit\"}", true)]
    [InlineData("{\"type\":\"fullscreen-enter-device-control\"}", false)]
    [InlineData("{\"type\":\"selected\",\"id\":\"AGV-01\"}", false)]
    [InlineData("{\"type\":\"execute\",\"id\":\"agv-composite-a\"}", false)]
    [InlineData("{\"type\":1}", false)]
    [InlineData("{\"type\":\"selected\",\"id\":{}}", false)]
    [InlineData("[]", false)]
    [InlineData("broken", false)]
    public void Bridge_accepts_only_presentation_events_and_model_ids(string json, bool accepted) =>
        Assert.Equal(accepted, DigitalTwinScene.TryReadMessage(DigitalTwinScene.Page, json, out _, out _));

    [Fact]
    public void Messages_from_other_documents_or_oversized_messages_are_rejected()
    {
        Assert.False(DigitalTwinScene.TryReadMessage("https://digital-twin.local/other.html", "{\"type\":\"ready\"}", out _, out _));
        Assert.False(DigitalTwinScene.TryReadMessage("https://example.com/", "{\"type\":\"ready\"}", out _, out _));
        Assert.False(DigitalTwinScene.TryReadMessage(DigitalTwinScene.Page, new string(' ', 4097), out _, out _));
    }

    [Fact]
    public void Packaged_viewer_contains_model_and_offline_decoders()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "DigitalTwin", "Web");
        Assert.Null(DigitalTwinScene.FindMissingAsset(folder));
        Assert.NotNull(DigitalTwinScene.FindMissingAsset(Path.Combine(folder, "absent-folder")));
        Assert.Equal(4, DigitalTwinScene.Devices.Count);
        Assert.Single(DigitalTwinScene.Devices.Where(d => d.Id.StartsWith("agv-", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("d160-a", "D160")]
    [InlineData("autosampler-a", "SHA18i")]
    public void Instruments_are_explicitly_unbound_pending_communications(string id, string model)
    {
        var device = Assert.IsType<DigitalTwinDevice>(DigitalTwinScene.Find(id));
        Assert.Contains(model, device.Name);
        Assert.Contains("未绑定", device.Name);
        var state = new DigitalTwinViewModel { SelectedDevice = device };
        Assert.Contains("通信待完成", state.SelectionDescription);
        Assert.Contains("STN61_01", state.SelectionDescription);
        Assert.Null(DigitalTwinScene.Find("d160-b"));
        Assert.Null(DigitalTwinScene.Find("STN61_01"));
    }

    [Fact]
    public void Packaged_model_matches_corrected_catalog_without_instrument_bindings()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "DigitalTwin", "Web", "lab606.glb"));
        using var reader = new BinaryReader(stream);
        Assert.Equal(0x46546C67u, reader.ReadUInt32());
        Assert.Equal(2u, reader.ReadUInt32());
        Assert.Equal(stream.Length, reader.ReadUInt32());
        var jsonLength = reader.ReadInt32();
        Assert.Equal(0x4E4F534Au, reader.ReadUInt32());
        using var document = JsonDocument.Parse(reader.ReadBytes(jsonLength));
        var nodes = document.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(DigitalTwinScene.Devices.Select(d => d.Id).Append("environment").Order(),
            nodes.Select(n => n.GetProperty("name").GetString()).Order());
        foreach (var id in new[] { "d160-a", "autosampler-a" })
        {
            var node = nodes.Single(n => n.GetProperty("name").GetString() == id);
            Assert.Empty(node.GetProperty("extras").GetProperty("equipmentCodeCandidates").EnumerateArray());
        }
        var triangles = document.RootElement.GetProperty("meshes").EnumerateArray()
            .SelectMany(m => m.GetProperty("primitives").EnumerateArray())
            .Sum(p => document.RootElement.GetProperty("accessors")[p.GetProperty("indices").GetInt32()]
                .GetProperty("count").GetInt32() / 3);
        Assert.Equal(1287310, triangles);
    }
}
