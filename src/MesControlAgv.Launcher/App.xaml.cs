using System.Diagnostics;
using System.IO;
using System.Windows;

namespace MesControlAgv.Launcher;

public partial class App : Application
{
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
            startInfo.Environment["WPF_RUNTIME_MODE"] = "simulator";
            startInfo.Environment["WPF_MANAGE_LOCAL_SERVICES"] = "true";
            startInfo.Environment["SIMULATOR_BASE_URL"] = "http://localhost:5183/";
            startInfo.Environment["ADAPTER_BASE_URL"] = "http://localhost:5041/";
            startInfo.Environment["MES_BASE_URL"] = "http://localhost:5045/";
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
}
