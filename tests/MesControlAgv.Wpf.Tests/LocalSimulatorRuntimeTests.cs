using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class LocalSimulatorRuntimeTests
{
    private static readonly Uri SimulatorUrl = new("http://localhost:5183/");
    private static readonly Uri AdapterUrl = new("http://localhost:5041/");
    private static readonly Uri MesUrl = new("http://localhost:5045/");

    [Fact]
    public void Simulator_mode_manages_default_loopback_services()
    {
        var result = LocalSimulatorRuntime.ShouldManageLocalServices(
            "simulator",
            SimulatorUrl,
            AdapterUrl,
            MesUrl,
            configuredValue: null);

        Assert.True(result);
    }

    [Fact]
    public void Physical_mode_never_manages_local_services()
    {
        var result = LocalSimulatorRuntime.ShouldManageLocalServices(
            "physical",
            SimulatorUrl,
            AdapterUrl,
            MesUrl,
            configuredValue: "true");

        Assert.False(result);
    }

    [Fact]
    public void Simulator_mode_uses_external_services_when_an_endpoint_is_not_loopback()
    {
        var result = LocalSimulatorRuntime.ShouldManageLocalServices(
            "simulator",
            new Uri("http://simulator.example.test:5183/"),
            AdapterUrl,
            MesUrl,
            configuredValue: null);

        Assert.False(result);
    }

    [Fact]
    public void Explicit_local_management_rejects_non_loopback_endpoints()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => LocalSimulatorRuntime.ShouldManageLocalServices(
            "simulator",
            new Uri("http://simulator.example.test:5183/"),
            AdapterUrl,
            MesUrl,
            configuredValue: "true"));

        Assert.Contains("loopback", exception.Message);
    }
}
