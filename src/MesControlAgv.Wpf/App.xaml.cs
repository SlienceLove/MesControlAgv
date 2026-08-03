using System.Net.Http;
using System.Windows;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var mesUrl = Environment.GetEnvironmentVariable("MES_BASE_URL") ?? "http://localhost:5045/";
        var simulatorUrl = Environment.GetEnvironmentVariable("SIMULATOR_BASE_URL") ?? "http://localhost:5183/";
        var mesClient = new MesClient(new HttpClient { BaseAddress = new Uri(mesUrl) });
        var simulatorClient = new SimulatorControlClient(new HttpClient { BaseAddress = new Uri(simulatorUrl) });
        var viewModel = new MainViewModel(mesClient, simulatorClient);
        var window = new MainWindow { DataContext = viewModel };
        window.Closed += (_, _) => viewModel.Dispose();
        window.Show();
        _ = viewModel.StartAsync();
    }
}
