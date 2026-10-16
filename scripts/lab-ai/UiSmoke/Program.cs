using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MesControlAgv.Reporting;
using MesControlAgv.Wpf;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Single());
        if (Directory.Exists(output)) throw new InvalidOperationException("Use a fresh evidence directory.");
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var exit = 1;
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                using var http = new HttpClient(new OfflineHandler()) { BaseAddress = new("http://reports.offline.invalid/") };
                using var vm = new MainViewModel(new MesClient(http), workflowStore: new WorkflowStore(Path.Combine(output, "unused-workflows.json")));
                window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 1440, Height = 960, Left = -10000, Top = 0,
                    ShowInTaskbar = false, ShowActivated = false };
                window.Show();
                ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("LabReportTab");
                await vm.LabReports.RefreshAsync();
                var snapshot = vm.LabReports.Snapshot with { Measurements = [new("000001", "00001", "DEMO", "氯离子", 1.23m,
                    "mg/L", DateTimeOffset.Now, "合成示例，不是现场结果", "demo", 2)] };
                var input = Path.Combine(output, "sample.xlsx"); ReportFiles.WriteExcel(input, snapshot, null);
                await vm.LabReports.PreviewFileAsync(input);
                if (vm.LabReports.Preview?.CanAccept != true) throw new Exception("Preview failed.");
                vm.LabReports.AcceptImport();
                await vm.LabReports.GenerateAsync();
                if (vm.LabReports.Measurements.Single().SampleId != "00001" || !vm.LabReports.SummaryText.Contains("离线"))
                    throw new Exception("Offline fallback or ID preservation failed.");
                await vm.LabReports.ExportAsync(Path.Combine(output, "report.xlsx"));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                foreach (var size in new[] { (1440d, 960d), (1000d, 700d) })
                {
                    window.Width = size.Item1; window.Height = size.Item2; window.UpdateLayout();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var file = File.Create(Path.Combine(output, $"reports-{size.Item1}.png")); encoder.Save(file);
                }
                File.WriteAllText(Path.Combine(output, "result.json"), "{\"passed\":true,\"transport\":\"fake MES HTTP, no device IO\",\"excelRoundtrip\":true,\"aiOfflineExport\":true}");
                Console.WriteLine("PASS: offline report tab, Excel import preview, confirmation, AI-offline fallback and export."); exit = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { window?.Close(); app.Shutdown(); }
        };
        app.Run(); return exit;
    }
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var now = DateTimeOffset.Now;
            object data = request.RequestUri!.AbsolutePath switch
            {
                "/api/lab-reports/snapshot" => new ReportSnapshot("demo", now, new(now.AddDays(-1), now.AddDays(1)),
                    [new("demo-job", "00001", "DEMO", "Completed", now, now.AddMinutes(-1), now, null)], [], ["演示数据"]),
                "/api/lab-reports/summary" => new AiSummary(false, "模型离线，统计可导出。", "qwen3:1.7b", "", [], now),
                _ => new { }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(data) });
        }
    }
}
