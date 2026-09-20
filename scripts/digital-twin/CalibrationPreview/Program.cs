using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Wpf.DigitalTwin;
using MesControlAgv.Wpf.Views;

internal static class CalibrationPreviewProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Explicit launch switch; never silently connect when a developer runs the project.
        if (args.Length is not (2 or 3) || args[0] != "--live-readonly" ||
            (args.Length == 3 && args[2] != "--schematic-follow")) return 2;
        var schematicFollow = args.Length == 3;
        var evidenceDirectory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(evidenceDirectory);
        using var singleton = new Mutex(true, "Local\\MesControlAgv-DigitalTwin-CalibrationPreview", out var acquired);
        if (!acquired) { MessageBox.Show("标定预览窗口已打开，请使用已有窗口。"); return 3; }
        var source = new PreviewSource();
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var exit = 0;
        try
        {
            var view = new DigitalTwinView { StatusSource = source };
            view.EnableSchematicFollowing(schematicFollow);
            var window = new Window {
                Title = schematicFollow ? "606 数字孪生 · 示意定位／实机坐标跟随（只读）" : "606 数字孪生 · 真实坐标标定预览（只读，不控制设备）",
                Content = view, WindowState = WindowState.Maximized, MinWidth = 1000, MinHeight = 700
            };
            var started = DateTimeOffset.UtcNow;
            var captureTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            Task captureTask = Task.CompletedTask;
            bool closePending = false;
            captureTimer.Tick += (_, _) =>
            {
                if (!view.State.IsReady && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(45)) return;
                if (source.PoseReads < 3 && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(45)) return;
                captureTimer.Stop();
                captureTask = CaptureStartupAsync();
            };
            async Task CaptureStartupAsync()
            {
                try
                {
                    var currentPose = source.LastPose;
                    await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "startup.json"), JsonSerializer.Serialize(new {
                        processId = Environment.ProcessId, observedAt = DateTimeOffset.UtcNow,
                        source = source.SourceDisplay, sceneReady = view.State.IsReady, poseReads = source.PoseReads,
                        schematicFollowing = view.IsSchematicFollowing,
                        pose = currentPose, poseValidation = currentPose is { } pose ? TwinPoseValidation.Rejection(pose, source.Bindings.AgvId, DateTimeOffset.UtcNow) : "No live pose",
                        poseDisplay = ((TextBlock)view.FindName("PoseStatusText")).Text,
                        devices = view.State.Readings, physicalActionsSent = false, schedulerStarted = false
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { TryLog(evidenceDirectory, "evidence-error.txt", ex); }
                try
                {
                    if (view.State.IsReady) await Capture.Save(view, Path.Combine(evidenceDirectory, "live-preview.png"));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { TryLog(evidenceDirectory, "capture-error.txt", ex); }
            }
            window.Closing += async (_, e) =>
            {
                captureTimer.Stop();
                if (captureTask.IsCompleted) return;
                e.Cancel = true;
                if (closePending) return;
                closePending = true;
                // Keep the dispatcher and WebView alive until the in-flight capture has completed.
                await captureTask;
                window.Close();
            };
            window.Loaded += (_, _) => captureTimer.Start();
            window.Closed += (_, _) => captureTimer.Stop();
            app.Run(window);
        }
        catch (Exception ex)
        {
            TryLog(evidenceDirectory, "error.txt", ex);
            MessageBox.Show("标定预览启动失败，错误已记录；现有中控未受影响。"); exit = 1;
        }
        finally
        {
            try { source.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { TryLog(evidenceDirectory, "shutdown-error.txt", ex); exit = 1; }
            singleton.ReleaseMutex();
        }
        return exit;
    }
    private static void TryLog(string directory, string file, Exception error)
    {
        try { File.WriteAllText(Path.Combine(directory, file), error.ToString()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
