using System.Text.Json;

namespace MesControlAgv.Mes.Tests;

public sealed class DefaultConfigurationContractTests
{
    [Fact]
    public void Mes_root_configuration_is_physical_safe_by_default()
    {
        using var document = LoadConfiguration("appsettings.json");
        var profile = document.RootElement.GetProperty("Profile");
        var agv = profile.GetProperty("agvs")[0];

        Assert.False(profile.GetProperty("features").GetProperty("useSimulator").GetBoolean());
        Assert.Equal("vendor-tcp", agv.GetProperty("driver").GetString());
        Assert.Equal(
            "tcp://physical-controller-host.invalid:19206",
            agv.GetProperty("endpoint").GetString());
        Assert.Equal(
            "physical-controller-host.invalid",
            agv.GetProperty("deviceParameters").GetProperty("controllerHost").GetString());
        Assert.False(document.RootElement
            .GetProperty("PhysicalReadinessSupervisor")
            .GetProperty("Enabled")
            .GetBoolean());
        Assert.False(document.RootElement
            .GetProperty("WorkflowAuboWorker")
            .GetProperty("enabled")
            .GetBoolean());
        Assert.False(document.RootElement
            .GetProperty("WorkflowSimulatorWorker")
            .GetProperty("enabled")
            .GetBoolean());
    }

    [Fact]
    public void Simulator_configuration_is_explicitly_scoped_to_field_simulation()
    {
        using var defaultDocument = LoadConfiguration("appsettings.json");
        using var simulationDocument = LoadConfiguration("appsettings.FieldSimulation.json");

        Assert.False(defaultDocument.RootElement
            .GetProperty("Profile")
            .GetProperty("features")
            .GetProperty("useSimulator")
            .GetBoolean());
        Assert.True(simulationDocument.RootElement
            .GetProperty("Profile")
            .GetProperty("features")
            .GetProperty("useSimulator")
            .GetBoolean());
        Assert.Equal(
            "simulator",
            simulationDocument.RootElement
                .GetProperty("Profile")
                .GetProperty("agvs")[0]
                .GetProperty("driver")
                .GetString());
    }

    private static JsonDocument LoadConfiguration(string fileName)
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        return JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "src", "MesControlAgv.Mes", fileName)));
    }
}
