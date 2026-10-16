using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Views;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Single());
        if (Directory.Exists(output)) throw new InvalidOperationException("Use a new output directory.");
        Directory.CreateDirectory(output);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exit = 1;
        app.Startup += async (_, _) =>
        {
            Window? window = null;
            try
            {
                using var http = new HttpClient(new Offline()) { BaseAddress = new("http://offline.invalid/") };
                using var vm = new AiPlanningViewModel(new MesClient(http));
                var view = new AiPlanningView { DataContext = vm };
                window = new Window { Content = view, Width = 1200, Height = 850, Left = -10000, Top = 0,
                    ShowInTaskbar = false, ShowActivated = false, Title = "合成数据离线验收" };
                window.Show();
                vm.Input = "AGV从待检区取料，送到开盖分液工作站，完成后送到检测位。";
                await vm.GenerateAsync();
                if (vm.Steps.Count != 3 || vm.CanSave) throw new Exception("Review gate failed.");
                foreach (var width in new[] { 1200, 1000 })
                {
                    window.Width = width;
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, $"planner-{width}.png")); encoder.Save(file);
                }
                vm.Reviewed = true; await vm.SaveAsync();
                if (vm.SavedPlan?.PlanId == Guid.Empty || vm.SavedPlan is null || vm.CanSave)
                    throw new Exception("Draft creation failed.");
                File.WriteAllText(Path.Combine(output, "result.json"),
                    "{\"passed\":true,\"fakeHttp\":true,\"modelQualityVerified\":false,\"deviceRequests\":0}");
                Console.WriteLine("PASS: offline AI planning view, review gate, draft save and validation; no device/model IO.");
                exit = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { window?.Close(); app.Shutdown(); }
        };
        app.Run(); return exit;
    }

    private sealed class Offline : HttpMessageHandler
    {
        private readonly Guid _planId = Guid.NewGuid();
        private static readonly string[] Names = ["待检区取料并送到分液交接位", "开盖分液模板", "取回样品并送到检测交接位"];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            object data = path switch
            {
                "/api/ai-planning/catalog" => new AiPlanningCatalog
                {
                    Revision = "offline",
                    Templates = Names.Select((name, i) => new AiWorkflowTemplate
                    {
                        Key = $"t{i}", Name = name, Version = 1, Definition = new WorkflowDefinition
                        { Nodes = [new() { Name = name, Order = 1, NodeTypeId = "合成示例，非现场模板" }] }
                    }).ToArray()
                },
                "/api/ai-planning/propose" => new AiPlanningResult
                {
                    ModelAvailable = true, CatalogRevision = "offline", Message = "离线演示：请复核模板内容后创建草稿。",
                    Proposal = new AiPlanProposal
                    {
                        Name = "样品转运与开盖分液方案", SampleBatchId = "DEMO-001",
                        Explanation = "以下三个步骤均为合成模板，用于验证界面交互，不代表实际设备能力。耗时是估计。",
                        Steps = Names.Select((_, i) => new AiPlanStep { TemplateKey = $"t{i}", EstimatedDurationMinutes = i == 1 ? 15 : 3 }).ToArray()
                    }
                },
                "/api/ai-planning/drafts" => new ExperimentPlan { PlanId = _planId, Version = 1 },
                _ when path.EndsWith("/validate") => new ExperimentPlan { PlanId = _planId, Version = 1, Validation = new() },
                _ => throw new InvalidOperationException("Unexpected request: " + path)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(data) });
        }
    }
}
