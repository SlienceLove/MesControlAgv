using System.Net.Http;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.Modules;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.WorkflowCanvas;
using MesControlAgv.Wpf.Views;
using MesControlAgv.Contracts;

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
            var mesHttpClient = new HttpClient(new MesHttpTimeoutHandler())
            {
                BaseAddress = mesUrl,
                Timeout = Timeout.InfiniteTimeSpan
            };
            var accessClient = new AccessClient(mesHttpClient);
            var accessMode = await accessClient.GetModeAsync(_startupCancellation.Token);
            AccessLoginResponse? loginResult = null;
            if (accessMode.Enabled)
            {
                startupWindow.SetStatus("等待用户登录...");
                var login = new LoginWindow(accessClient, accessMode.Demo);
                if (login.ShowDialog() != true || login.Result is null)
                {
                    _startupCompleted = true;
                    startupWindow.Close();
                    Shutdown();
                    return;
                }
                loginResult = login.Result;
                accessClient.SetSession(loginResult);
            }
            // Rebuild every user session, including the HTTP client, page models and selections.
            // Owned service processes remain alive while only the shell is replaced.
            void OpenShell(HttpClient http, AccessClient access, AccessLoginResponse? session, WorkflowRunStartupBinding binding)
            {
                var shellLifetime = CancellationTokenSource.CreateLinkedTokenSource(_startupCancellation.Token);
                var simulatorHttp = isSimulator ? new HttpClient { BaseAddress = simulatorUrl, Timeout = TimeSpan.FromSeconds(3) } : null;
                var workflowPath = Environment.GetEnvironmentVariable("WPF_WORKFLOW_STORE_PATH");
                var model = new MainViewModel(
                    new MesClient(http),
                    simulatorHttp is not null ? new SimulatorControlClient(simulatorHttp) : null,
                    ControlCenterModuleRegistry.CreateStandard(),
                    new SmapMapLayoutSource(ResolveMapSmapPath, ResolveStationMappingPath),
                    string.IsNullOrWhiteSpace(workflowPath) ? null : new WorkflowStore(workflowPath),
                    startupConfiguration,
                    physicalBatchExecutionEnabled: !isSimulator && string.Equals(Environment.GetEnvironmentVariable("WPF_ENABLE_PHYSICAL_BATCH"), "true", StringComparison.OrdinalIgnoreCase),
                    sampleWorkstationTestControlEnabled: string.Equals(Environment.GetEnvironmentVariable("WPF_ENABLE_SAMPLE_WORKSTATION_TEST_CONTROL"), "true", StringComparison.OrdinalIgnoreCase));
                _viewModel = model;
                var window = new MainWindow { DataContext = model };
                if (session is not null)
                {
                    model.OperatorName = session.User.Username;
                    window.SetAccessSession(access, session.User, accessMode.Demo);
                }
                window.Closed += (_, _) =>
                {
                    shellLifetime.Cancel();
                    model.Dispose();
                    http.Dispose();
                    simulatorHttp?.Dispose();
                    shellLifetime.Dispose();
                    if (ReferenceEquals(_viewModel, model)) _viewModel = null;
                };
                window.SessionEnded += (_, _) =>
                {
                    // Queue after the originating click/dialog/monitor continuation has unwound.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ShutdownMode = ShutdownMode.OnExplicitShutdown;
                        window.Close();
                        var nextHttp = new HttpClient(new MesHttpTimeoutHandler()) { BaseAddress = mesUrl, Timeout = Timeout.InfiniteTimeSpan };
                        try
                        {
                            var nextAccess = new AccessClient(nextHttp);
                            var login = new LoginWindow(nextAccess, accessMode.Demo);
                            MainWindow = login;
                            if (login.ShowDialog() == true && login.Result is not null)
                            {
                                nextAccess.SetSession(login.Result);
                                OpenShell(nextHttp, nextAccess, login.Result, new WorkflowRunStartupBinding(null, null));
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"重新登录失败：{ex.Message}", "中控运营中心", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                        nextHttp.Dispose();
                        Shutdown();
                    }));
                };
                MainWindow = window;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                window.Show();
                _ = StartMainWindowAsync(window, binding, shellLifetime.Token);
            }
            _startupCompleted = true;
            startupWindow.Close();
            OpenShell(mesHttpClient, accessClient, loginResult, startupWorkflowBinding);
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
        WorkflowRunStartupBinding startupWorkflowBinding,
        CancellationToken shellCancellation)
    {
        var model = window.DataContext as MainViewModel;
        if (model is null) return;
        try
        {
            await model.StartAsync();
            if (shellCancellation.IsCancellationRequested) return;
            if (!startupWorkflowBinding.IsSpecified) return;

            if (await model.BindWorkflowRunAsync(startupWorkflowBinding, shellCancellation) && !shellCancellation.IsCancellationRequested)
            {
                window.ShowWorkflowRunMonitor();
            }
        }
        catch (OperationCanceledException) when (shellCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (shellCancellation.IsCancellationRequested) return;
            // Keep the shell available for a manually verified read-only bind;
            // an invalid external id must not trigger another request.
            model.WorkflowRunMonitor.SetStatusMessage(
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
