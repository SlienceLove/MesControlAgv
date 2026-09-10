using System.Text;
using System.Text.Json;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class ShineLabDirectSequenceParserTests
{
    private const string StandardType = "\u6807\u51c6\u6837";
    private const string BlankType = "\u7a7a\u767d\u6837";

    [Fact]
    public void Standard_fixture_is_valid_for_direct_import()
    {
        var fixture = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "shinelab-direct-injection-standard.csv");

        var direct = new ShineLabDirectSequenceParser().Parse(fixture);
        Assert.True(direct.CanBuildPreview);
        Assert.Empty(direct.Issues);
        Assert.Equal(2, direct.Tasks.Count);
    }

    [Fact]
    public void Parser_maps_sample_types_and_preserves_direct_fields()
    {
        var csv = string.Join('\n',
            "sampleID,sampleName,sampleType,sampleLevel,position,mPos,channel,instrumentMethod,processingMethod,detectionMethod,injectionVolume,injectionVolumeUnit,cycleCount",
            $"LOCAL-STD-001,Standard,{StandardType},1,1,1,A,AS18-M01,IC-P01,Normal,25,uL,1",
            $"LOCAL-BLANK-001,Blank,{BlankType},1,2,2,A,AS18-M01,IC-P01,Normal,25,uL,1");

        var result = Parse(csv);

        Assert.True(result.HasDirectColumns);
        Assert.Empty(result.Issues);
        Assert.Equal(2, result.Tasks.Count);
        Assert.Equal(1, result.Tasks[0].SampleTypeCode);
        Assert.Equal(3, result.Tasks[1].SampleTypeCode);
        Assert.Equal(2, result.Tasks[1].Position);
        Assert.Equal("A", result.Tasks[0].Channel);
        Assert.Equal("AS18-M01", result.Tasks[0].InstrumentMethod);
        Assert.Equal("IC-P01", result.Tasks[0].ProcessingMethod);
        Assert.Equal(25m, result.Tasks[0].InjectionVolume);
        Assert.Equal("uL", result.Tasks[0].InjectionVolumeUnit);
    }

    [Fact]
    public void Protocol_preview_contains_config_and_action_zero_command()
    {
        var csv = string.Join('\n',
            "sampleID,sampleName,sampleType,position,mPos,channel,instrumentMethod,processingMethod,injectionVolume,injectionVolumeUnit",
            $"LOCAL-STD-001,Standard,{StandardType},1,1,A,AS18-M01,IC-P01,25,uL");
        var result = Parse(csv);

        var preview = ShineLabDirectProtocolPreview.Build(
            result.Tasks,
            equipmentCode: "STN61_01",
            taskUuid: "task-test-001",
            configStrId: "config-test-001",
            commandStrId: "command-test-001");
        using var document = JsonDocument.Parse(preview);
        var root = document.RootElement;

        Assert.Equal("Config", root.GetProperty("config").GetProperty("strMethod").GetString());
        Assert.Equal("STN61_01", root.GetProperty("config").GetProperty("equipmentCode").GetString());
        Assert.Equal(1, root.GetProperty("config").GetProperty("body").GetProperty("sampleData").GetArrayLength());
        Assert.Equal(1, root.GetProperty("config").GetProperty("body").GetProperty("sampleData")[0].GetProperty("type").GetInt32());
        Assert.Equal("Command", root.GetProperty("command").GetProperty("strMethod").GetString());
        Assert.Equal(0, root.GetProperty("command").GetProperty("body").GetProperty("action").GetInt32());
    }

    [Fact]
    public void Parser_rejects_invalid_position_channel_method_and_volume()
    {
        var csv = string.Join('\n',
            "sampleID,sampleName,sampleType,position,mPos,channel,instrumentMethod,processingMethod,injectionVolume,injectionVolumeUnit",
            $"BAD-001,Bad,{StandardType},0,,C,,,0,cc");

        var result = Parse(csv);

        Assert.True(result.HasDirectColumns);
        Assert.Empty(result.Tasks);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("position", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => issue.Message.Contains("mPos", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => issue.Message.Contains("channel", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => issue.Message.Contains("instrumentMethod", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => issue.Message.Contains("processingMethod", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => issue.Message.Contains("injectionVolume", StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_rejects_mixed_channels_in_one_config()
    {
        var csv = string.Join('\n',
            "sampleID,sampleName,sampleType,position,mPos,channel,instrumentMethod,processingMethod,injectionVolume,injectionVolumeUnit",
            $"A-001,Channel A,{StandardType},1,1,A,AS18-M01,IC-P01,25,uL",
            $"B-001,Channel B,{StandardType},2,2,B,AS18-M01,IC-P01,25,uL");

        var result = Parse(csv);

        Assert.False(result.CanBuildPreview);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("同一 channel", StringComparison.Ordinal));
    }

    private static ShineLabDirectSequenceResult Parse(string csv)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        return new ShineLabDirectSequenceParser().Parse(stream, "direct.csv");
    }
}
