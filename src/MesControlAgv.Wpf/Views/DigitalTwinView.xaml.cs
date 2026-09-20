using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Wpf.DigitalTwin;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MesControlAgv.Wpf.Views;

public partial class DigitalTwinView : UserControl
{
    private WebView2? _browser;
    private int _generation;
    private bool _initializing;
    private DigitalTwinStatusSession? _telemetry;
    private readonly DispatcherTimer _freshnessTimer;
    public static readonly DependencyProperty StatusSourceProperty = DependencyProperty.Register(
        nameof(StatusSource), typeof(IDigitalTwinStatusSource), typeof(DigitalTwinView),
        new PropertyMetadata(null, (owner, _) => ((DigitalTwinView)owner).RestartTelemetry()));
    public IDigitalTwinStatusSource? StatusSource
    {
        get => (IDigitalTwinStatusSource?)GetValue(StatusSourceProperty);
        set => SetValue(StatusSourceProperty, value);
    }
    private static readonly Lazy<Task<CoreWebView2Environment>> EnvironmentTask = new(() =>
        CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MesControlAgv", "DigitalTwinWebView2")));

    public DigitalTwinViewModel State { get; } = new();
    public DigitalTwinView()
    {
        InitializeComponent();
        LayoutRoot.DataContext = State;
        _freshnessTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => { State.UpdateFreshness(DateTimeOffset.UtcNow); UpdatePose(); }, Dispatcher);
        _freshnessTimer.Stop();
        State.PropertyChanged += (_, args) => {
            if (args.PropertyName == nameof(State.Readings)) SendTelemetry();
            UpdatePresentationWarning();
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => { if (IsVisible && State.IsReady) RestartTelemetry(); else if (!IsVisible) StopTelemetry(); };
        InitializeCalibration();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await InitializeViewerAsync();
    private void OnUnloaded(object sender, RoutedEventArgs e) => ReleaseViewer();

    private async Task InitializeViewerAsync()
    {
        if (_initializing || _browser is not null || !IsLoaded) return;
        var generation = ++_generation;
        _initializing = true;
        State.IsReady = false;
        State.ResetTelemetry(StatusSource);
        State.Status = "正在加载本地三维场景……";
        try
        {
            var assets = Path.Combine(AppContext.BaseDirectory, "DigitalTwin", "Web");
            if (DigitalTwinScene.FindMissingAsset(assets) is { } missing)
            {
                State.Status = $"三维资源缺失：{missing}。请重新准备数字孪生资源后重试。";
                return;
            }
            var environment = await EnvironmentTask.Value;
            if (generation != _generation || !IsLoaded) return;
            var browser = new WebView2();
            _browser = browser;
            BrowserHost.Children.Add(browser);
            await browser.EnsureCoreWebView2Async(environment);
            if (generation != _generation || !IsLoaded) return;
            var core = browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.SetVirtualHostNameToFolderMapping(DigitalTwinScene.Host, assets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, args) => args.Cancel = !DigitalTwinScene.IsViewerPage(args.Uri);
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (!DigitalTwinScene.IsLocalResource(args.Request.Uri) &&
                    !args.Request.Uri.StartsWith($"blob:https://{DigitalTwinScene.Host}/", StringComparison.Ordinal))
                    args.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Local assets only", "");
            };
            core.WebMessageReceived += (_, args) =>
            {
                if (generation == _generation && HandleCalibrationPoint(args.Source, args.WebMessageAsJson)) return;
                if (generation != _generation ||
                    !DigitalTwinScene.TryReadMessage(args.Source, args.WebMessageAsJson, out var kind, out var id)) return;
                switch (kind)
                {
                    case "fullscreen-toggle": if (IsVisible) _fullScreenHost?.Invoke(null); break;
                    case "fullscreen-exit": if (IsPresentationMode) _fullScreenHost?.Invoke(false); break;
                    case "ready": State.IsReady = true; State.Status = "场景已就绪 · 状态约 2 秒／位置约 500 ms 只读刷新，显示不代表操作授权。"; RestartTelemetry(); break;
                    case "selected": State.SelectedDevice = DigitalTwinScene.Find(id); break;
                    case "load-error": State.IsReady = false; StopTelemetry(); State.Status = "三维场景加载失败，请点击重新加载。"; break;
                }
            };
            core.NavigationCompleted += (_, args) =>
            {
                if (generation == _generation && !args.IsSuccess)
                { State.IsReady = false; StopTelemetry(); State.Status = "三维页面加载失败，请点击重新加载。"; }
            };
            core.ProcessFailed += (_, _) =>
            { if (generation == _generation) { State.IsReady = false; StopTelemetry(); State.Status = "三维视窗已停止，请点击重新加载恢复。"; } };
            core.Navigate(DigitalTwinScene.Page);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (generation == _generation)
            {
                ReleaseViewer();
                State.Status = exception is WebView2RuntimeNotFoundException
                    ? "未安装 Microsoft Edge WebView2 Runtime，请安装后重启中控。"
                    : "三维视窗初始化失败，请点击重新加载。";
            }
        }
        finally { if (generation == _generation) _initializing = false; }
    }

    private void ReleaseViewer()
    {
        StopTelemetry();
        _generation++;
        _initializing = false;
        State.IsReady = false;
        State.SelectedDevice = null;
        var browser = _browser;
        _browser = null;
        BrowserHost.Children.Clear();
        browser?.Dispose();
        State.Status = "三维资源已释放，打开页面后重新加载。";
    }

    private void RestartTelemetry()
    {
        StopTelemetry();
        State.ResetTelemetry(StatusSource);
        if (!State.IsReady || !IsLoaded || !IsVisible) return;
        SendTelemetry();
        if (StatusSource is not { } source) return;
        var session = new DigitalTwinStatusSession(source);
        _telemetry = session;
        session.Reading += reading =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (_telemetry == session && IsLoaded && State.IsReady) State.ApplyReading(reading);
            });
        };
        _freshnessTimer.Start();
        session.Start();
        StartPose();
    }

    private void StopTelemetry()
    {
        StopPose();
        _freshnessTimer?.Stop();
        var session = _telemetry;
        _telemetry = null;
        session?.Dispose();
        if (session is not null) State.ResetTelemetry(null);
    }

    private void SendTelemetry()
    {
        if (!State.IsReady || _browser?.CoreWebView2 is not { } core) return;
        var readings = State.Readings;
        var composite = TwinProjection.Composite(readings[0], readings[1]);
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new {
                type = "telemetry", source = State.SourceDisplay,
                devices = new[] {
                    new { id = "agv-composite-a", title = "复合机器人", tone = composite.Tone,
                        text = IsPresentationMode ? $"AGV {CompactStatus(readings[0])} · 臂 {CompactStatus(readings[1])}" : $"AGV {readings[0].Text} · 臂 {readings[1].Text}" },
                    new { id = "decapper-a", title = "开盖／分液", tone = readings[2].Tone, text = IsPresentationMode ? CompactStatus(readings[2]) : readings[2].DisplayText }
                }
            }));
        }
        catch (InvalidOperationException) { State.IsReady = false; StopTelemetry(); State.Status = "三维视窗不可用，请重新加载。"; }
    }

    private void SendCommand(string type)
    {
        if (!State.IsReady || _browser?.CoreWebView2 is not { } core) return;
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type, id = State.SelectedDevice?.Id })); }
        catch (InvalidOperationException) { State.IsReady = false; State.Status = "三维视窗不可用，请重新加载。"; }
    }
    private void DevicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => SendCommand("select");
    private void ViewerCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string command }) SendCommand(command);
    }
    private async void Reload_Click(object sender, RoutedEventArgs e)
    { ReleaseViewer(); await InitializeViewerAsync(); }
}
