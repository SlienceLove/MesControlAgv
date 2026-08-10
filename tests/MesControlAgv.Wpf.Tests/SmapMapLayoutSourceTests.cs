using System.Text;
using System.Security.Cryptography;
using MesControlAgv.Domain.Map;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class SmapMapLayoutSourceTests
{
    [Fact]
    public async Task Not_loaded_when_env_path_empty()
    {
        var source = new SmapMapLayoutSource(() => "", () => null);

        var result = await source.LoadAsync();

        Assert.False(result.Loaded);
        Assert.Null(result.Layout);
        Assert.Same(StationMappingConfig.Empty, result.Mapping);
    }

    [Fact]
    public async Task Loads_layout_from_valid_smap_file()
    {
        var smapPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                smapPath,
                """
                {
                  "header": {
                    "mapType": "2D-Map",
                    "mapName": "guangzhou606",
                    "minPos": { "x": 0, "y": 0 },
                    "maxPos": { "x": 10, "y": 10 },
                    "resolution": 0.02,
                    "version": "1.0.6"
                  },
                  "advancedPointList": [
                    { "instanceName": "LM1", "pos": { "x": 1, "y": 2 } }
                  ]
                }
                """,
                Encoding.UTF8);

            var source = new SmapMapLayoutSource(() => smapPath, () => null);

            var result = await source.LoadAsync();

            Assert.True(result.Loaded);
            Assert.NotNull(result.Layout);
            Assert.NotEmpty(result.Layout!.Stations);
            Assert.NotNull(result.Identity);
            Assert.Equal("guangzhou606", result.Identity!.MapName);
            Assert.Equal("1.0.6", result.Identity.MapVersion);
            Assert.Equal(
                Convert.ToHexString(MD5.HashData(await File.ReadAllBytesAsync(smapPath))).ToLowerInvariant(),
                result.Identity.Md5);
            Assert.Equal(["LM1"], result.Identity.StationMarks);
        }
        finally
        {
            File.Delete(smapPath);
        }
    }

    [Fact]
    public async Task Reports_error_and_not_loaded_on_bad_file()
    {
        var smapPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(smapPath, "{", Encoding.UTF8);

            var source = new SmapMapLayoutSource(() => smapPath, () => null);

            var result = await source.LoadAsync();

            Assert.False(result.Loaded);
            Assert.Null(result.Layout);
            Assert.NotNull(result.Error);
        }
        finally
        {
            File.Delete(smapPath);
        }
    }
}
