using System.Text;
using MesControlAgv.Domain.Map;

namespace MesControlAgv.Domain.Tests.Map;

public sealed class StationMappingLoaderTests
{
    [Fact]
    public async Task Returns_empty_when_path_is_null()
    {
        var config = await StationMappingLoader.LoadFileAsync(null);

        Assert.Same(StationMappingConfig.Empty, config);
    }

    [Fact]
    public async Task Returns_empty_when_file_missing()
    {
        var config = await StationMappingLoader.LoadFileAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.json"));

        Assert.Same(StationMappingConfig.Empty, config);
    }

    [Fact]
    public async Task Loads_entries_and_resolves_by_mark()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "entries": [
                    {
                      "smapMark": "LM1",
                      "mesAgvStationId": "WS-01",
                      "displayName": "上料位"
                    }
                  ]
                }
                """,
                Encoding.UTF8);

            var config = await StationMappingLoader.LoadFileAsync(path);

            Assert.True(config.TryResolve("LM1", out var entry));
            Assert.Equal("LM1", entry.SmapMark);
            Assert.Equal("WS-01", entry.MesAgvStationId);
            Assert.Equal("上料位", entry.DisplayName);
            Assert.False(config.TryResolve("LMX", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
