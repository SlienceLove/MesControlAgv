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
        Assert.Contains("Adapter:   http://localhost:5041", launcher);
        Assert.Contains("MES:       http://localhost:5045", launcher);
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
