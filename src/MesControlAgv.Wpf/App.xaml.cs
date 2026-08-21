using System.Net.Http;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.Modules;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.WorkflowCanvas;

namespace MesControlAgv.Wpf;

public partial class App : Application
{
    private readonly CancellationTokenSource _startupCancellation = new();
    private LocalSimulatorRuntime? _localRuntime;
    private MainViewModel? _viewModel;
    private bool _startupCompleted;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (string.Equals(
                Environment.GetEnvironmentVariable("WPF_SOFTWARE_RENDERING"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        if (e.Args.Any(argument => string.Equals(argument, "--workflow-canvas-spike", StringComparison.OrdinalIgnoreCase)))
        {
            StartWorkflowCanvasSpike();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var startupWindow = new StartupWindow();
        startupWindow.Closed += (_, _) =>
        {
            if (!_startupCompleted)
            {
                _startupCancellation.Cancel();
            }
        };
        startupWindow.Show();

        try
        {
            var mesUrl = ReadBaseUrl("MES_BASE_URL", "http://localhost:5045/");
            var simulatorUrl = ReadBaseUrl("SIMULATOR_BASE_URL", "http://localhost:5183/");
            var adapterUrl = ReadBaseUrl("ADAPTER_BASE_URL", "http://localhost:5041/");
            var runtimeMode = Environment.GetEnvironmentVariable("WPF_RUNTIME_MODE") ?? "simulator";
            if (!runtimeMode.Equals("simulator", StringComparison.OrdinalIgnoreCase) &&
                !runtimeMode.Equals("physical", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("WPF_RUNTIME_MODE 只能设置为 simulator 或 physical。");
            }

            var isSimulator = runtimeMode.Equals("simulator", StringComparison.OrdinalIgnoreCase);
            if (LocalSimulatorRuntime.ShouldManageLocalServices(
                    runtimeMode,
                    simulatorUrl,
                    adapterUrl,
                    mesUrl,
                    Environment.GetEnvironmentVariable("WPF_MANAGE_LOCAL_SERVICES")))
            {
                var progress = new Progress<string>(startupWindow.SetStatus);
                _localRuntime = await LocalSimulatorRuntime.StartAsync(
                    simulatorUrl,
                    adapterUrl,
                    mesUrl,
                    progress,
                    _startupCancellation.Token);
            }
            else
            {
                startupWindow.SetStatus(isSimulator ? "正在连接现有本地服务..." : "正在连接 MES...");
            }

            var mesClient = new MesClient(new HttpClient { BaseAddress = mesUrl });
            ISimulatorControlClient? simulatorClient = null;
            if (isSimulator)
            {
                simulatorClient = new SimulatorControlClient(new HttpClient { BaseAddress = simulatorUrl });
            }

            var moduleRegistry = ControlCenterModuleRegistry.CreateStandard();
            var mapLayoutSource = new SmapMapLayoutSource(
                () => Environment.GetEnvironmentVariable("MAP_SMAP_PATH"),
                () => Environment.GetEnvironmentVariable("MAP_STATION_MAPPING_PATH"));
            var workflowStorePath = Environment.GetEnvironmentVariable("WPF_WORKFLOW_STORE_PATH");
            var workflowStore = string.IsNullOrWhiteSpace(workflowStorePath)
                ? null
                : new WorkflowStore(workflowStorePath);
            _viewModel = new MainViewModel(
                mesClient,
                simulatorClient,
                moduleRegistry,
                mapLayoutSource,
                workflowStore);
            var window = new MainWindow { DataContext = _viewModel };
            window.Closed += (_, _) =>
            {
                _viewModel?.Dispose();
                _viewModel = null;
            };

            MainWindow = window;
            _startupCompleted = true;
            startupWindow.Close();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            _ = _viewModel.StartAsync();
        }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested)
        {
            _localRuntime?.Dispose();
            _localRuntime = null;
            _startupCompleted = true;
            startupWindow.Close();
            Shutdown();
        }
        catch (Exception exception)
        {
            _localRuntime?.Dispose();
            _localRuntime = null;
            _startupCompleted = true;
            startupWindow.Close();
            MessageBox.Show(
                $"中控启动失败：{exception.Message}",
                "AGV MES 中控",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _startupCancellation.Cancel();
        _viewModel?.Dispose();
        _viewModel = null;
        _localRuntime?.Dispose();
        _localRuntime = null;
        _startupCancellation.Dispose();
        base.OnExit(e);
    }

    private void StartWorkflowCanvasSpike()
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var window = new WorkflowCanvasSpikeWindow();
        MainWindow = window;
        _startupCompleted = true;
        window.Show();
    }

    private static Uri ReadBaseUrl(string variableName, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variableName) ?? fallback;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"{variableName} 必须是绝对 HTTP(S) 地址。");
        }

        return new Uri($"{uri.AbsoluteUri.TrimEnd('/')}/", UriKind.Absolute);
    }
}
