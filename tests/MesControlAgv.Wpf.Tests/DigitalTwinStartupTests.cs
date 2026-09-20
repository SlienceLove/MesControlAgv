using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class DigitalTwinStartupTests
{
    [Theory]
    [InlineData("physical", null, true, false)]
    [InlineData("physical", "false", true, false)]
    [InlineData("physical", "true", true, true)]
    [InlineData("physical", " TRUE ", true, true)]
    [InlineData("physical", "yes", false, false)]
    [InlineData("simulator", "true", false, false)]
    [InlineData("simulator", "false", true, false)]
    [InlineData("unknown", "true", false, false)]
    public void Schematic_follow_requires_explicit_valid_physical_configuration(
        string mode, string? option, bool canStart, bool follows)
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = Path.GetTempPath(), RuntimeMode = mode,
            ManageLocalServices = "false", ManageLocalMes = "false", TwinSchematicFollow = option
        });
        Assert.Equal(canStart, report.CanStart);
        Assert.Equal(follows, report.TwinSchematicFollow);
        Assert.False(report.ManageLocalServices);
        Assert.False(report.ManageLocalMes);
    }

    [Fact]
    public void Daily_environment_uses_existing_services_and_does_not_grant_actions()
    {
        var env = new Dictionary<string, string>
        {
            ["WPF_RUNTIME_MODE"] = "physical", ["WPF_TWIN_SCHEMATIC_FOLLOW"] = "true",
            ["WPF_MANAGE_LOCAL_SERVICES"] = "false", ["WPF_MANAGE_LOCAL_MES"] = "false",
            ["MES_BASE_URL"] = "http://127.0.0.1:15445", ["ADAPTER_BASE_URL"] = "http://127.0.0.1:15441"
        };
        var report = StartupConfigurationInspector.InspectEnvironment(Path.GetTempPath(), key => env.GetValueOrDefault(key));
        Assert.True(report.CanStart);
        Assert.True(report.TwinSchematicFollow);
        Assert.False(report.ManageLocalServices);
        Assert.False(report.ManageLocalMes);
        Assert.Equal(15445, report.MesBaseUrl.Port);
        Assert.Equal(15441, report.AdapterBaseUrl.Port);
        Assert.Contains(report.Items, item => item.Code == "WPF_TWIN_SCHEMATIC_FOLLOW" && item.Detail.Contains("不代表"));
        Assert.False(StartupConfigurationReport.Unknown.TwinSchematicFollow);
    }

    [Fact]
    public void Invalid_connection_does_not_enable_following()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            RuntimeMode = "physical", TwinSchematicFollow = "true", MesBaseUrl = "not-a-url"
        });
        Assert.False(report.CanStart);
        Assert.False(report.TwinSchematicFollow);
    }
}
