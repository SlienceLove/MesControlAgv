using System.Net.Http;
using System.Windows;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.Modules;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var mesUrl = Environment.GetEnvironmentVariable("MES_BASE_URL") ?? "http://localhost:5045/";
        var runtimeMode = Environment.GetEnvironmentVariable("WPF_RUNTIME_MODE") ?? "simulator";
        if (!runtimeMode.Equals("simulator", StringComparison.OrdinalIgnoreCase) &&
            !runtimeMode.Equals("physical", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("WPF_RUNTIME_MODE must be either 'simulator' or 'physical'.");
        }

        var mesClient = new MesClient(new HttpClient { BaseAddress = new Uri(mesUrl) });
        ISimulatorControlClient? simulatorClient = null;
        if (runtimeMode.Equals("simulator", StringComparison.OrdinalIgnoreCase))
        {
            var simulatorUrl = Environment.GetEnvironmentVariable("SIMULATOR_BASE_URL") ?? "http://localhost:5183/";
            simulatorClient = new SimulatorControlClient(new HttpClient { BaseAddress = new Uri(simulatorUrl) });
        }

        var moduleRegistry = ControlCenterModuleRegistry.CreateStandard();
        var viewModel = new MainViewModel(mesClient, simulatorClient, moduleRegistry);
        var window = new MainWindow { DataContext = viewModel };
        window.Closed += (_, _) => viewModel.Dispose();
        window.Show();
        _ = viewModel.StartAsync();
    }
}
