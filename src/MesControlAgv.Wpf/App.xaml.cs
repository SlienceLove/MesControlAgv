using System.Net.Http;
using System.IO;
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
    private LocalMesRuntime? _localMesRuntime;
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

        // An external one-shot workflow client can hand an explicit execution
        // or request id to the WPF process. Never infer a "latest" run.
        var startupWorkflowBinding = WorkflowRunStartupBinding.Parse(e.Args);

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
            startupWindow.SetStatus("正在执行离线启动配置检查...");
            if (startupWorkflowBinding.IsSpecified && !startupWorkflowBinding.IsValid)
            {
                throw new InvalidOperationException(startupWorkflowBinding.Error ??
                    "流程启动绑定无效。");
            }
            var startupConfiguration = StartupConfigurationInspector.InspectEnvironment(AppContext.BaseDirectory);
            if (!startupConfiguration.CanStart)
            {
                var errors = startupConfiguration.Items
                    .Where(item => item.Severity == StartupDiagnosticSeverity.Error)
                    .Select(item => item.Detail);
                throw new InvalidOperationException($"启动配置检查失败：{string.Join("；", errors)}");
            }

            var mesUrl = startupConfiguration.MesBaseUrl;
            var simulatorUrl = startupConfiguration.SimulatorBaseUrl;
            var adapterUrl = startupConfiguration.AdapterBaseUrl;
            var runtimeMode = startupConfiguration.RuntimeMode;
            var isSimulator = runtimeMode.Equals("simulator", StringComparison.OrdinalIgnoreCase);
            if (startupConfiguration.ManageLocalServices)
            {
                var progress = new Progress<string>(startupWindow.SetStatus);
                _localRuntime = await LocalSimulatorRuntime.StartAsync(
                    simulatorUrl,
                    adapterUrl,
                    mesUrl,
                    progress,
                    _startupCancellation.Token);
            }
            else if (startupConfiguration.ManageLocalMes)
            {
                var progress = new Progress<string>(startupWindow.SetStatus);
                _localMesRuntime = await LocalMesRuntime.StartAsync(
                    mesUrl,
                    progress,
                    _startupCancellation.Token);
            }
            else
            {
                startupWindow.SetStatus(isSimulator ? "正在连接现有本地服务..." : "正在连接 MES...");
            }

            // Ordinary reads fail fast while the explicit AUBO catalog scan
            // and state-changing requests retain their own larger budgets.
            // This keeps an unplugged UI responsive without turning a slow
            // device mutation into an unnecessary unknown outcome.
            var mesClient = new MesClient(new HttpClient(new MesHttpTimeoutHandler())
            {
                BaseAddress = mesUrl,
                Timeout = Timeout.InfiniteTimeSpan
            });
            ISimulatorControlClient? simulatorClient = null;
            if (isSimulator)
            {
                simulatorClient = new SimulatorControlClient(new HttpClient
                {
                    BaseAddress = simulatorUrl,
                    Timeout = TimeSpan.FromSeconds(3)
                });
            }

            var moduleRegistry = ControlCenterModuleRegistry.CreateStandard();
            var mapLayoutSource = new SmapMapLayoutSource(
                ResolveMapSmapPath,
                ResolveStationMappingPath);
            var workflowStorePath = Environment.GetEnvironmentVariable("WPF_WORKFLOW_STORE_PATH");
            var workflowStore = string.IsNullOrWhiteSpace(workflowStorePath)
                ? null
                : new WorkflowStore(workflowStorePath);
            _viewModel = new MainViewModel(
                mesClient,
                simulatorClient,
                moduleRegistry,
                mapLayoutSource,
                workflowStore,
                startupConfiguration,
                physicalBatchExecutionEnabled: !isSimulator &&
                    string.Equals(
                        Environment.GetEnvironmentVariable("WPF_ENABLE_PHYSICAL_BATCH"),
                        "true",
                        StringComparison.OrdinalIgnoreCase));
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
            _ = StartMainWindowAsync(window, startupWorkflowBinding);
        }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested)
        {
            _localRuntime?.Dispose();
            _localRuntime = null;
            _localMesRuntime?.Dispose();
            _localMesRuntime = null;
            _startupCompleted = true;
            startupWindow.Close();
            Shutdown();
        }
        catch (Exception exception)
        {
            _localRuntime?.Dispose();
            _localRuntime = null;
            _localMesRuntime?.Dispose();
            _localMesRuntime = null;
            _startupCompleted = true;
            startupWindow.Close();
            MessageBox.Show(
                $"中控启动失败：{exception.Message}",
                "中控运营中心",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private async Task StartMainWindowAsync(
        MainWindow window,
        WorkflowRunStartupBinding startupWorkflowBinding)
    {
        if (_viewModel is null) return;
        try
        {
            await _viewModel.StartAsync();
            if (!startupWorkflowBinding.IsSpecified) return;

            if (await _viewModel.BindWorkflowRunAsync(startupWorkflowBinding, _startupCancellation.Token))
            {
                window.ShowWorkflowRunMonitor();
            }
        }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Keep the shell available for a manually verified read-only bind;
            // an invalid external id must not trigger another request.
            _viewModel.WorkflowRunMonitor.SetStatusMessage(
                $"显式流程绑定失败：{exception.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _startupCancellation.Cancel();
        _viewModel?.Dispose();
        _viewModel = null;
        _localRuntime?.Dispose();
        _localRuntime = null;
        _localMesRuntime?.Dispose();
        _localMesRuntime = null;
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

    private static string? ResolveMapSmapPath()
    {
        var configured = Environment.GetEnvironmentVariable("MAP_SMAP_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        // The field-approved map is the safe fallback for this deployment. It
        // is still checked for existence; an absent map never blocks startup.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidate = Path.Combine(
            localAppData,
            "RoboshopPro",
            "appInfo",
            "robots",
            "All",
            "de48aac8dc641f04",
            "maps",
            "guangzhou606.smap");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? ResolveStationMappingPath()
    {
        var configured = Environment.GetEnvironmentVariable("MAP_STATION_MAPPING_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        var candidate = Path.Combine(
            AppContext.BaseDirectory,
            "Configuration",
            "guangzhou606.station-mapping.json");
        return File.Exists(candidate) ? candidate : null;
    }

}
