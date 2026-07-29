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
        var mesUrl = Environment.GetEnvironmentVariable("MES_BASE_URL") ?? "http://localhost:5000/";
        var viewModel = new MainViewModel(new MesClient(new HttpClient { BaseAddress = new Uri(mesUrl) }));
        var window = new MainWindow { DataContext = viewModel };
        window.Closed += (_, _) => viewModel.Dispose();
        window.Show();
        _ = viewModel.StartAsync();
    }
}
