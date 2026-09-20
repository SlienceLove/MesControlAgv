using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf;
using MesControlAgv.Wpf.DigitalTwin;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Views;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--fullscreen") return FullScreenSmoke.Run(args[1]);
        if (args.Length == 2 && args[0] == "--fullscreen-keyboard") return FullScreenSmoke.Run(args[1], nativeKeyboard: true);
        if (args.Length == 2 && args[0] == "--inspect-startup")
        {
            using var plan = JsonDocument.Parse(Console.In.ReadToEnd().TrimStart('\uFEFF'));
            var env = plan.RootElement.GetProperty("Environment");
            var executable = plan.RootElement.GetProperty("Executable").GetString()!;
            var diagnostics = StartupConfigurationInspector.InspectEnvironment(Path.GetDirectoryName(executable)!,
                key => env.TryGetProperty(key, out var value) ? value.GetString() : null);
            Check(diagnostics.CanStart && diagnostics.TwinSchematicFollow && !diagnostics.ManageLocalServices &&
                !diagnostics.ManageLocalMes && diagnostics.MesBaseUrl.Port == 15445 && diagnostics.AdapterBaseUrl.Port == 15441,
                "Daily launch profile failed offline inspection: " + JsonSerializer.Serialize(diagnostics.Items));
            Check(plan.RootElement.GetProperty("Arguments").GetArrayLength() == 0, "Unexpected launch arguments");
            Check(!WorkflowRunStartupBinding.Parse([], key => env.TryGetProperty(key, out var value) ? value.GetString() : null).IsSpecified,
                "Daily launcher inherited historical workflow binding");
            File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Daily launcher configuration passed; no process started.");
            return 0;
        }
        var output = Path.GetFullPath(args.Single());
        Directory.CreateDirectory(output);
        var exit = 1;
        // No App.OnStartup and no real socket: exercise the normal window, VM,
        // MES HTTP client, polling sessions and WebView using an HTTP boundary fake.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                using var handler = new OfflineMesHandler();
                using var http = new HttpClient(handler) { BaseAddress = new Uri("http://mes.offline.invalid/") };
                var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
                {
                    BaseDirectory = output, RuntimeMode = "physical", TwinSchematicFollow = "true",
                    ManageLocalServices = "false", ManageLocalMes = "false"
                });
                // Guard even manually constructed reports that bypass inspection.
                foreach (var rejected in new[] { report with { TwinSchematicFollow = false },
                    report with { RuntimeMode = "unknown" }, report with { RuntimeMode = "simulator" } })
                {
                    using var rejectedVm = new MainViewModel(new MesClient(http), startupConfiguration: rejected,
                        workflowStore: new WorkflowStore(Path.Combine(output, "unused-offline-workflows.json")));
                    var rejectedWindow = new MainWindow { DataContext = rejectedVm };
                    rejectedWindow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    Check(!((DigitalTwinView)rejectedWindow.FindName("DigitalTwinView")).IsSchematicFollowing,
                        "Startup accepted disabled/nonphysical report");
                    rejectedWindow.Close();
                }
                using var vm = new MainViewModel(new MesClient(http), startupConfiguration: report,
                    workflowStore: new WorkflowStore(Path.Combine(output, "unused-offline-workflows.json")));
                Check(vm.DigitalTwinStatusSource is MesDigitalTwinStatusSource, "Main VM bypassed MES source");
                var calibration = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MesControlAgv", "DigitalTwin", "606-agv-calibration.json");
                var before = File.Exists(calibration) ? File.ReadAllBytes(calibration) : null;
                window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal,
                    Width = 1480, Height = 1000, Left = -10000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
                window.Show();
                var view = (DigitalTwinView)window.FindName("DigitalTwinView");
                var tabs = (TabControl)window.FindName("MainTabs");
                Check(view.IsSchematicFollowing, "Main window did not apply explicit startup setting");
                Check(handler.PoseReads == 0, "Inactive twin page polled pose");
                tabs.SelectedItem = window.FindName("DigitalTwinTab");
                await Wait(() => view.State.IsReady && handler.PoseReads >= 2, "Main twin page not ready");
                var core = Core(view);
                await Script(core, "window.digitalTwin.inspect().ground.schematic");
                await ReferenceVisibilityCheck.CheckAsync(core, false);
                await Wait(() => view.State.Readings[0].Condition == TwinCondition.Online &&
                    view.State.Readings[1].Condition == TwinCondition.Idle &&
                    view.State.Readings[2].Condition == TwinCondition.Unavailable, "Independent MES statuses not projected");
                var status = (TextBlock)view.FindName("PoseStatusText");
                var initialPosition = await Position(core);
                foreach (var error in new[] { HttpStatusCode.NotFound, HttpStatusCode.NotImplemented, HttpStatusCode.GatewayTimeout })
                {
                    handler.PoseStatus = error;
                    var reads = handler.PoseReads;
                    await Wait(() => handler.PoseReads > reads && status.Text.Contains(error == HttpStatusCode.GatewayTimeout ? "读取失败或超时" : "接口未就绪"), "Pose HTTP failure not displayed: " + error);
                    Check(await Position(core) == initialPosition, "Failure moved model");
                }
                handler.PoseStatus = HttpStatusCode.OK;
                handler.MapHash = "wrong-map";
                await Wait(() => status.Text.Contains("地图"), "Wrong map not rejected");
                handler.MapHash = TwinCalibration.MapMd5;
                handler.AgvId = "AGV-OTHER";
                await Wait(() => status.Text.Contains("编号"), "Wrong identity not rejected");
                handler.AgvId = "AGV-01";
                handler.Stale = true;
                await Wait(() => status.Text.Contains("过期"), "Stale pose not rejected");
                handler.Stale = false;
                handler.X = .3;
                await Wait(() => status.Text.Contains("正在跟随"), "Valid pose did not recover");
                await Script(core, "Math.abs(window.digitalTwin.inspect().nodes.find(n=>n.id==='agv-composite-a').position[0]-21.15)<.002");

                // The application startup choice is not re-applied on Loaded or tab return.
                ((CheckBox)view.FindName("SchematicFollowCheck")).IsChecked = false;
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Check(!view.IsSchematicFollowing, "Loaded overwrote user choice");
                tabs.SelectedIndex = 0;
                await Wait(() => !view.State.IsReady, "Leaving tab did not release WebView");
                await Task.Delay(200);
                var stopped = handler.PoseReads;
                await Task.Delay(900);
                Check(handler.PoseReads == stopped, "Hidden page continued pose reads");
                tabs.SelectedItem = window.FindName("DigitalTwinTab");
                await Wait(() => view.State.IsReady && handler.PoseReads > stopped, "Re-entry did not resume polling");
                Check(!view.IsSchematicFollowing, "Tab return overwrote user choice");
                core = Core(view);
                await ReferenceVisibilityCheck.CheckAsync(core, false);
                view.EnableSchematicFollowing(true);
                await Script(core, "window.digitalTwin.inspect().ground.schematic");

                // A pending HTTP read must be canceled when its tab unloads.
                handler.BlockPose = true;
                await Wait(() => handler.PendingPose == 1, "No pending pose request");
                tabs.SelectedIndex = 0;
                await Wait(() => handler.CanceledPose > 0 && handler.PendingPose == 0, "Pose cancellation not propagated");
                handler.BlockPose = false;
                handler.X = .6;
                tabs.SelectedItem = window.FindName("DigitalTwinTab");
                await Wait(() => view.State.IsReady, "Second re-entry failed");
                core = Core(view);
                await Script(core, "Math.abs(window.digitalTwin.inspect().nodes.find(n=>n.id==='agv-composite-a').position[0]-20.85)<.002");
                using (var capture = File.Create(Path.Combine(output, "main-client-offline.png")))
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);
                Check(handler.MaximumPendingPose == 1, "Overlapping main-client pose requests");
                Check(before is null ? !File.Exists(calibration) : before.SequenceEqual(File.ReadAllBytes(calibration)), "Startup modified saved calibration");
                Check(!File.Exists(Path.Combine(output, "unused-offline-workflows.json")), "Smoke changed workflow store");
                Check(handler.Unexpected.IsEmpty, "Unexpected HTTP calls: " + string.Join(",", handler.Unexpected));
                window.Close(); window = null;
                var closedReads = handler.PoseReads;
                await Task.Delay(1000);
                Check(handler.PoseReads == closedReads, "Closed main window continued polling");
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    passed = true, mainWindow = true, transport = "MES HTTP fake; no real devices",
                    handler.PoseReads, handler.CanceledPose, handler.MaximumPendingPose,
                    checks = new[] { "startup-once", "reference-default-off", "independent-status", "404-501-504",
                        "identity-map-stale-guards", "recovery", "tab-unload-reentry", "cancellation", "calibration-unchanged", "GET-only" },
                    requests = handler.Requests.ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("Central client offline smoke passed."); exit = 0;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); Console.Error.WriteLine(ex); }
            finally { window?.Close(); app.Shutdown(); }
        };
        app.Run(); return exit;
    }

    private static CoreWebView2 Core(DigitalTwinView view) => ((Grid)view.FindName("BrowserHost")).Children.OfType<WebView2>().Single().CoreWebView2;
    private static Task<string> Position(CoreWebView2 core) => core.ExecuteScriptAsync("window.digitalTwin.inspect().nodes.find(n=>n.id==='agv-composite-a').position");
    private static void Check(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
    private static async Task Wait(Func<bool> condition, string error)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException(error); await Task.Delay(50); }
    }
    private static async Task Script(CoreWebView2 core, string expression)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (await core.ExecuteScriptAsync(expression) != "true")
        { if (DateTime.UtcNow > until) throw new TimeoutException(expression); await Task.Delay(50); }
    }
}

internal sealed class OfflineMesHandler : HttpMessageHandler
{
    public readonly ConcurrentQueue<string> Requests = new(), Unexpected = new();
    public volatile HttpStatusCode PoseStatus = HttpStatusCode.OK;
    public volatile bool Stale, BlockPose;
    public string MapHash = TwinCalibration.MapMd5, AgvId = "AGV-01";
    public double X;
    public int PoseReads, PendingPose, MaximumPendingPose, CanceledPose;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Enqueue(request.Method + " " + path);
        if (request.Method != HttpMethod.Get || request.RequestUri.Host != "mes.offline.invalid")
        { Unexpected.Enqueue(request.ToString()); throw new InvalidOperationException("Unexpected request"); }
        if (path == "/api/agvs/AGV-01/pose")
        {
            Interlocked.Increment(ref PoseReads);
            var pending = Interlocked.Increment(ref PendingPose);
            MaximumPendingPose = Math.Max(MaximumPendingPose, pending);
            try
            {
                if (BlockPose) await Task.Delay(Timeout.Infinite, token);
                var now = DateTimeOffset.UtcNow;
                return new HttpResponseMessage(PoseStatus) { Content = JsonContent.Create(new AgvPoseResponse(
                    AgvId, X, .8, -3.1237, .95, "LM1", "offline", Stale ? now.AddSeconds(-5) : now, "guangzhou606", MapHash, now)) };
            }
            catch (OperationCanceledException) { Interlocked.Increment(ref CanceledPose); throw; }
            finally { Interlocked.Decrement(ref PendingPose); }
        }
        if (path == "/api/agvs/fleet/status") return Ok(new[] { new { snapshot = new { online = true, mode = "Idle", currentStationId = "LM1", agvId = "AGV-01" } } });
        if (path == "/api/robot-arms/ARM-01/status") return Ok(new AuboArmStatusResponse("ARM-01", "robot", true,
            AuboArmMode.Idle, null, AuboArmSafetyMode.Normal, null, AuboArmRuntimeState.Stopped, null, AuboArmOperationalMode.Automatic, null, DateTimeOffset.UtcNow));
        if (path.StartsWith("/api/workstations/SAMPLE-WORKSTATION-01/", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        // Other normal main-window pages perform readonly catalog refresh on
        // their own Loaded events. They stay offline in this focused smoke.
        if (path is "/api/experiment-plans" or "/api/workflows" or "/api/experiment-jobs" or
            "/api/schedule" or "/api/resources/availability" or "/api/experiment-scheduling/audits")
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        Unexpected.Enqueue(path);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}
