using System.Diagnostics;
using System.IO;
using System.Windows;

namespace MesControlAgv.Launcher;

public partial class App : Application
{
    private const string DefaultPhysicalAdapterBaseUrl = "http://127.0.0.1:5141/";
    private const string DefaultPhysicalMesBaseUrl = "http://127.0.0.1:5145/";
    private const string DefaultSimulatorBaseUrl = "http://localhost:5183/";
    private const string DefaultSimulatorAdapterBaseUrl = "http://localhost:5041/";
    private const string DefaultSimulatorMesBaseUrl = "http://localhost:5045/";
    private Process? _wpfProcess;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var wpfPath = Path.Combine(AppContext.BaseDirectory, "MesControlAgv.Wpf.exe");
        if (!File.Exists(wpfPath))
        {
            ShowStartupError($"未找到 WPF 运行文件：{wpfPath}。请重新构建或发布启动器。");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = wpfPath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // The desktop launcher is a physical-session entry point by default.
            // Physical services are owned by the separately supervised session;
            // the launcher must never start the in-process Simulator implicitly.
            // An explicit WPF_RUNTIME_MODE=simulator remains available for
            // offline regression and keeps the old loopback service contract.
            var configuredRuntimeMode = Environment.GetEnvironmentVariable("WPF_RUNTIME_MODE");
            var hasExplicitRuntimeMode = !string.IsNullOrWhiteSpace(configuredRuntimeMode);
            var runtimeMode = string.IsNullOrWhiteSpace(configuredRuntimeMode)
                ? "physical"
                : configuredRuntimeMode.Trim();
            var simulatorMode = runtimeMode.Equals("simulator", StringComparison.OrdinalIgnoreCase);
            SetEnvironment(startInfo, "WPF_RUNTIME_MODE", runtimeMode);
            SetDefaultEnvironment(
                startInfo,
                "WPF_MANAGE_LOCAL_SERVICES",
                simulatorMode ? "true" : "false",
                force: !hasExplicitRuntimeMode);
            SetDefaultEnvironment(
                startInfo,
                "WPF_ENABLE_PHYSICAL_BATCH",
                "false",
                force: !hasExplicitRuntimeMode);
            SetDefaultEnvironment(
                startInfo,
                "SIMULATOR_BASE_URL",
                DefaultSimulatorBaseUrl,
                force: !hasExplicitRuntimeMode);
            SetDefaultEnvironment(
                startInfo,
                "ADAPTER_BASE_URL",
                simulatorMode ? DefaultSimulatorAdapterBaseUrl : DefaultPhysicalAdapterBaseUrl,
                force: !hasExplicitRuntimeMode);
            SetDefaultEnvironment(
                startInfo,
                "MES_BASE_URL",
                simulatorMode ? DefaultSimulatorMesBaseUrl : DefaultPhysicalMesBaseUrl,
                force: !hasExplicitRuntimeMode);
            _wpfProcess = Process.Start(startInfo);
            if (_wpfProcess is null)
            {
                throw new InvalidOperationException("无法启动 WPF 中控进程。");
            }

            _wpfProcess.EnableRaisingEvents = true;
            _wpfProcess.Exited += WpfProcess_Exited;
        }
        catch (Exception exception)
        {
            ShowStartupError(exception.Message);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_wpfProcess is not null)
        {
            try
            {
                if (!_wpfProcess.HasExited)
                {
                    _wpfProcess.CloseMainWindow();
                    if (!_wpfProcess.WaitForExit(5000))
                    {
                        _wpfProcess.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                _wpfProcess.Dispose();
                _wpfProcess = null;
            }
        }

        base.OnExit(e);
    }

    private void WpfProcess_Exited(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var exitCode = _wpfProcess?.ExitCode ?? 1;
            Shutdown(exitCode);
        });
    }

    private static void ShowStartupError(string message)
    {
        MessageBox.Show(
            message,
            "AGV MES 启动器",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Current?.Shutdown(-1);
    }

    private static void SetEnvironment(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.Environment[name] = value;
    }

    private static void SetDefaultEnvironment(
        ProcessStartInfo startInfo,
        string name,
        string value,
        bool force)
    {
        if (force || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
        {
            startInfo.Environment[name] = value;
        }
    }
}
