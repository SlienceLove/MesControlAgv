using System.Text.Json;

namespace MesControlAgv.E2E.Tests;

public sealed class LocalPortContractTests
{
    [Fact]
    public void Development_files_and_defaults_use_the_shared_port_contract()
    {
        var root = FindRepositoryRoot();
        using var rootConfig = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "appsettings.Development.json")));
        using var mesConfig = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Mes", "appsettings.Development.json")));
        using var adapterConfig = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Adapter", "appsettings.Development.json")));
        using var mesLaunch = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Mes", "Properties", "launchSettings.json")));
        using var adapterLaunch = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Adapter", "Properties", "launchSettings.json")));
        using var simulatorLaunch = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Simulator", "Properties", "launchSettings.json")));

        Assert.Equal("http://localhost:5041", rootConfig.RootElement.GetProperty("Mes").GetProperty("AdapterBaseUrl").GetString());
        Assert.Equal("http://localhost:5183", rootConfig.RootElement.GetProperty("Adapter").GetProperty("SimulatorBaseUrl").GetString());
        Assert.Equal("http://localhost:5041/", mesConfig.RootElement.GetProperty("Adapter").GetProperty("BaseUrl").GetString());
        Assert.Equal("http://localhost:5183/", adapterConfig.RootElement.GetProperty("Simulator").GetProperty("BaseUrl").GetString());
        Assert.Equal("http://localhost:5045", mesLaunch.RootElement.GetProperty("profiles").GetProperty("http").GetProperty("applicationUrl").GetString());
        Assert.Equal("http://localhost:5041", adapterLaunch.RootElement.GetProperty("profiles").GetProperty("http").GetProperty("applicationUrl").GetString());
        Assert.Equal("http://localhost:5183", simulatorLaunch.RootElement.GetProperty("profiles").GetProperty("http").GetProperty("applicationUrl").GetString());

        Assert.Contains("http://localhost:5041/", File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Mes", "Program.cs")));
        Assert.Contains("http://localhost:5183/", File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Adapter", "Program.cs")));
        Assert.Contains("http://localhost:5045/", File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Wpf", "App.xaml.cs")));
        var launcher = File.ReadAllText(Path.Combine(root, "scripts", "run-local.ps1"));
        var stopper = File.ReadAllText(Path.Combine(root, "scripts", "stop-local.ps1"));
        var verifier = File.ReadAllText(Path.Combine(root, "scripts", "verify-local.ps1"));

        Assert.Contains("SimulatorUrl", launcher);
        Assert.Contains("AdapterUrl", launcher);
        Assert.Contains("MesUrl", launcher);
        Assert.Contains("MesDatabasePath", launcher);
        Assert.Contains("AdapterDatabasePath", launcher);
        Assert.Contains("StatePath", launcher);
        Assert.Contains("RunId", launcher);
        Assert.Contains("Wait-Health", launcher);
        Assert.Contains("ConnectionStrings__Mes", launcher);
        Assert.Contains("ConnectionStrings__Adapter", launcher);

        Assert.Contains("StatePath", stopper);
        Assert.Contains("RunId", stopper);
        Assert.Contains("Multiple local service state files", stopper);

        Assert.Contains("MesUrl", verifier);
        Assert.Contains("AdapterUrl", verifier);
        Assert.Contains("SimulatorUrl", verifier);
        Assert.Contains("MesDatabasePath", verifier);
        Assert.Contains("AdapterDatabasePath", verifier);
        Assert.Contains("StatePath", verifier);
        Assert.Contains("RunId", verifier);
        Assert.Contains("Scenario", verifier);
        Assert.Contains("failure-retry", verifier);
        Assert.Contains("WaitingDropoffConfirmation", verifier);
        Assert.Contains("DeviceFailed", verifier);
        Assert.Contains("RetryRequested", verifier);
        Assert.Contains("Local Simulator transport verification", verifier);
        Assert.DoesNotContain("Live AGV transport verification", verifier);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MesControlAgv.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("MesControlAgv.sln was not found.");
    }
}
