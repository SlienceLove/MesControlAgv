using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MesControlAgv.Wpf;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Views;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static class FullScreenSmoke
{
    public static int Run(string directory, bool nativeKeyboard = false)
    {
        var output = Path.GetFullPath(directory);
        Directory.CreateDirectory(output);
        var exit = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                using var source = new OfflineMesHandler();
                using var http = new HttpClient(source) { BaseAddress = new Uri("http://mes.offline.invalid/") };
                var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
                { RuntimeMode = "physical", TwinSchematicFollow = "true", ManageLocalMes = "false", ManageLocalServices = "false", BaseDirectory = output });
                using var vm = new MainViewModel(new MesClient(http), startupConfiguration: report,
                    workflowStore: new WorkflowStore(Path.Combine(output, "unused-workflow.json")));
                window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Left = -10000, Top = 0,
                    Width = 1480, Height = 1000, ShowActivated = false, ShowInTaskbar = false };
                window.Show();
                var tabs = (TabControl)window.FindName("MainTabs");
                window.SetTwinFullScreen(true);
                Check(!window.IsTwinFullScreen, "Fullscreen enabled outside twin page");
                tabs.SelectedItem = window.FindName("DigitalTwinTab");
                var view = (DigitalTwinView)window.FindName("DigitalTwinView");
                await Wait(() => view.State.IsReady && source.PoseReads >= 2, "Viewer not ready");
                var host = (Grid)view.FindName("BrowserHost");
                var browser = host.Children.OfType<WebView2>().Single();
                var core = browser.CoreWebView2;
                await Script(core, "window.digitalTwin.inspect().ground.schematic");
                var poseSession = Field(view, "_poseSession");
                var telemetry = Field(view, "_telemetry");
                var generation = Field(view, "_generation");
                var normalBounds = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
                var normalBrowserHeight = host.ActualHeight;
                var original = await Scene(core);
                Click(view, "map-reference-toggle");
                await ReferenceVisibilityCheck.CheckAsync(core, true);
                ((Button)view.FindName("FullScreenButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Wait(() => window.IsTwinFullScreen && host.ActualHeight > normalBrowserHeight + 200, "Fullscreen did not expand viewport");
                await Script(core, "document.querySelector('[data-tone]').textContent.includes('臂 空闲')");
                Check(view.IsPresentationMode && view.IsSchematicFollowing, "Presentation changed following");
                foreach (var name in new[] { "DetailedHeader", "DetailedReadings", "DetailedFooter" })
                    Check(((FrameworkElement)view.FindName(name)).Visibility == Visibility.Collapsed, "Detailed UI leaked: " + name);
                Check(((FrameworkElement)window.FindName("NavigationSidebar")).Visibility == Visibility.Collapsed, "Sidebar visible");
                Check(((FrameworkElement)window.FindName("ShellHeader")).Visibility == Visibility.Collapsed, "Header visible");
                Check(window.WindowStyle == WindowStyle.None && window.WindowState == WindowState.Maximized, "Window not fullscreen");
                await ReferenceVisibilityCheck.CheckAsync(core, true);
                CheckSamePose(original, await Scene(core));
                Check(ReferenceEquals(poseSession, Field(view, "_poseSession")) && ReferenceEquals(telemetry, Field(view, "_telemetry")), "Fullscreen restarted polling sessions");
                Check(Equals(generation, Field(view, "_generation")) && ReferenceEquals(browser, host.Children[0]), "Fullscreen rebuilt browser");
                var poseReads = source.PoseReads;
                source.X = .25;
                await Script(core, "Math.abs(window.digitalTwin.inspect().nodes[0].position[0]-21.2)<.002");
                Check(source.PoseReads > poseReads, "Pose reads stopped in fullscreen");
                var warning = (TextBlock)view.FindName("PresentationWarning");
                source.PoseStatus = HttpStatusCode.GatewayTimeout;
                await Wait(() => warning.IsVisible && warning.Text.Contains("读取失败"), "Fullscreen concealed failed positioning");
                Check(!warning.Text.Contains("x="), "Raw pose leaked into presentation");
                source.PoseStatus = HttpStatusCode.OK;
                await Wait(() => warning.Visibility == Visibility.Collapsed, "Warning did not clear on recovery");
                Click(view, "map-reference-toggle");
                await ReferenceVisibilityCheck.CheckAsync(core, false);
                await Capture(window, view, Path.Combine(output, "fullscreen-offline.png"));

                // CDP injection validates the DOM->host bridge, separately from
                // the optional Windows keyboard/focus acceptance below.
                await BrowserKey(core, "Escape", 27);
                await Wait(() => !window.IsTwinFullScreen, "WebView Esc failed to exit fullscreen");
                await Wait(() => Math.Abs(window.ActualWidth-normalBounds.Width)<2 && Math.Abs(window.ActualHeight-normalBounds.Height)<2, "Restore size failed");
                Check(Math.Abs(window.Left-normalBounds.Left)<2 && Math.Abs(window.Top-normalBounds.Top)<2, "Restore position failed");
                Check(window.WindowStyle == WindowStyle.SingleBorderWindow && !view.IsPresentationMode, "Restore chrome failed");
                await ReferenceVisibilityCheck.CheckAsync(core, false);
                foreach (var name in new[] { "DetailedHeader", "DetailedReadings", "DetailedFooter" })
                    Check(((FrameworkElement)view.FindName(name)).Visibility == Visibility.Visible, "Details did not restore");
                await BrowserKey(core, "F11", 122);
                await Wait(() => window.IsTwinFullScreen, "WebView F11 failed");
                ((Button)Find<Button>(view).Single(b => b.IsVisible && (string?)b.Content == "退出全屏 · Esc")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(!window.IsTwinFullScreen, "Exit button failed");

                await core.ExecuteScriptAsync("window.dispatchEvent(new KeyboardEvent('keydown',{key:'F11',repeat:true}))");
                await Task.Delay(150);
                Check(!window.IsTwinFullScreen, "Repeated F11 toggled fullscreen");
                if (nativeKeyboard)
                {
                    window.SetTwinFullScreen(true);
                    await NativePresentationKeyboard.FocusAsync(window, browser);
                    NativePresentationKeyboard.Key(window, 27);
                    await Wait(() => !window.IsTwinFullScreen, "Windows Esc with WebView focus failed");
                    await NativePresentationKeyboard.FocusAsync(window, browser);
                    NativePresentationKeyboard.Key(window, 122);
                    await Wait(() => window.IsTwinFullScreen, "Windows F11 with WebView focus failed");
                    NativePresentationKeyboard.Key(window, 27);
                    await Wait(() => !window.IsTwinFullScreen, "Windows Esc after F11 failed");
                }
                // Independently verify WPF's routed keyboard handler.
                WpfKey(window, Key.F11);
                Check(window.IsTwinFullScreen, "WPF F11 failed");
                WpfKey(window, Key.Escape);
                Check(!window.IsTwinFullScreen, "WPF Esc failed");
                ((CheckBox)view.FindName("SchematicFollowCheck")).IsChecked = false;
                window.SetTwinFullScreen(true);
                Check(!view.IsSchematicFollowing && warning.IsVisible, "Fullscreen silently re-enabled following or hid unsynced warning");
                window.SetTwinFullScreen(false);
                Check(!view.IsSchematicFollowing, "Exit changed following selection");
                ((CheckBox)view.FindName("SchematicFollowCheck")).IsChecked = true;
                for (var i=0;i<3;i++) { window.SetTwinFullScreen(true); window.SetTwinFullScreen(false); }
                Check(ReferenceEquals(poseSession, Field(view, "_poseSession")) && ReferenceEquals(browser, host.Children[0]), "Repeated switch replaced session/browser");
                window.WindowState = WindowState.Maximized;
                window.SetTwinFullScreen(true); window.SetTwinFullScreen(false);
                Check(window.WindowState == WindowState.Maximized, "Maximized state not restored");
                window.SetTwinFullScreen(true);
                tabs.SelectedIndex = 1;
                Check(!window.IsTwinFullScreen, "Other tab trapped in fullscreen");
                Check(source.MaximumPendingPose == 1, "Duplicate pose polling");
                Check(source.Unexpected.IsEmpty, "Unexpected HTTP calls");
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new {
                    passed=true, transport="offline MES HTTP fake only", sameBrowser=true, sameSessions=true, nativeKeyboard,
                    source.PoseReads, source.MaximumPendingPose, normalBrowserHeight,
                    checks=new[]{"button-entry-exit","webview-esc-f11","wpf-esc-f11","restore-normal-maximized",
                        "same-scene-pose","map-choice-preserved","compact-labels","details-hidden","error-recovery","follow-choice-preserved"}
                },new JsonSerializerOptions{WriteIndented=true}));
                Console.WriteLine("Fullscreen native smoke passed."); exit=0;
            }
            catch(Exception ex){Console.Error.WriteLine(ex);File.WriteAllText(Path.Combine(output,"error.txt"),ex.ToString());}
            finally{window?.Close();app.Shutdown();}
        };
        app.Run();return exit;
    }
    private static object? Field(object value,string name)=>value.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(value);
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static async Task Wait(Func<bool> condition,string error){var until=DateTime.UtcNow.AddSeconds(30);while(!condition()){if(DateTime.UtcNow>until)throw new TimeoutException(error);await Task.Delay(50);}}
    private static async Task Script(CoreWebView2 core,string expression){var until=DateTime.UtcNow.AddSeconds(15);while(await core.ExecuteScriptAsync(expression)!="true"){if(DateTime.UtcNow>until)throw new TimeoutException(expression);await Task.Delay(50);}}
    private static async Task<JsonElement> Scene(CoreWebView2 core){using var json=JsonDocument.Parse(await core.ExecuteScriptAsync("window.digitalTwin.inspect()"));return json.RootElement.Clone();}
    private static void CheckSamePose(JsonElement a,JsonElement b)
    {
        Check(a.GetProperty("nodes").ToString()==b.GetProperty("nodes").ToString() ||
            a.GetProperty("nodes").EnumerateArray().Zip(b.GetProperty("nodes").EnumerateArray()).All(pair=>
            pair.First.GetProperty("position").ToString()==pair.Second.GetProperty("position").ToString() &&
            pair.First.GetProperty("yaw").ToString()==pair.Second.GetProperty("yaw").ToString()),"Fullscreen changed model pose");
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
    {for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var c=VisualTreeHelper.GetChild(root,i);if(c is T t)yield return t;foreach(var nested in Find<T>(c))yield return nested;}}
    private static void Click(DigitalTwinView view,string tag)=>Find<Button>(view).Single(b=>b.IsVisible && b.Tag as string==tag).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static async Task BrowserKey(CoreWebView2 core,string key,int code)
    {foreach(var type in new[]{"keyDown","keyUp"})await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",JsonSerializer.Serialize(new{type,key,code=key,windowsVirtualKeyCode=code}));}
    private static void WpfKey(MainWindow window,Key key)=>window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent});
    private static async Task Capture(MainWindow window,DigitalTwinView view,string path)
    {
        window.UpdateLayout();
        var host=(Grid)view.FindName("BrowserHost");var core=host.Children.OfType<WebView2>().Single().CoreWebView2;
        using var stream=new MemoryStream();await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,stream);stream.Position=0;
        var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();
        var visual=new DrawingVisual();using(var drawing=visual.RenderOpen()){
            drawing.DrawRectangle(new VisualBrush(window),null,new Rect(window.RenderSize));
            drawing.DrawImage(bitmap,new Rect(host.TranslatePoint(new Point(),window),host.RenderSize));}
        var dpi=VisualTreeHelper.GetDpi(window);var target=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(window.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
        target.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(target));using var file=File.Create(path);encoder.Save(file);
    }
}
