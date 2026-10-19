using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using MesControlAgv.Reporting;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class LabReportViewModelTests
{
    private static ReportSnapshot Snapshot() => new("snapshot", DateTimeOffset.UtcNow,
        new(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow),
        [new("job", "00001", "batch", "Completed", DateTimeOffset.UtcNow, null, null, null)], [], []);

    [Fact] public async Task InvalidFilterClearsOldJobsAndPreventsAiCall()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Ok(Snapshot())); })) { BaseAddress = new("http://localhost/") };
        using var vm = new LabReportViewModel(new MesClient(http));
        await vm.RefreshAsync(); Assert.Single(vm.Jobs);
        vm.From = null; Assert.Empty(vm.Jobs); Assert.False(vm.GenerateCommand.CanExecute(null));
        await vm.GenerateAsync(); Assert.Equal(1, calls);
    }
    [Fact] public async Task ChangedFilterInvalidatesExistingAiSummary()
    {
        using var http = new HttpClient(new Handler((r, _) => Task.FromResult(r.Method == HttpMethod.Get ? Ok(Snapshot()) :
            Ok(new AiSummary(true, "草稿", "test", "旧建议", ["S1"], DateTimeOffset.UtcNow))))) { BaseAddress = new("http://localhost/") };
        using var vm = new LabReportViewModel(new MesClient(http)); await vm.RefreshAsync(); await vm.GenerateAsync();
        Assert.Contains("旧建议", vm.SummaryText); vm.Batch = "new"; Assert.DoesNotContain("旧建议", vm.SummaryText);
    }
    [Fact] public async Task ModelFailureKeepsStatisticsAndAllowsExcelExport()
    {
        using var http = new HttpClient(new Handler((r, _) => Task.FromResult(r.Method == HttpMethod.Get ? Ok(Snapshot()) :
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))) { BaseAddress = new("http://localhost/") };
        using var vm = new LabReportViewModel(new MesClient(http)); await vm.RefreshAsync(); await vm.GenerateAsync();
        Assert.Single(vm.Jobs); Assert.True(vm.IsIdle);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try { await vm.ExportAsync(path); Assert.True(System.IO.File.Exists(path)); }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }
    [Fact] public async Task CancelledSummaryNeverApplied()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (r, token) =>
        {
            if (r.Method == HttpMethod.Get) return Ok(Snapshot());
            entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Ok(new { });
        })) { BaseAddress = new("http://localhost/") };
        using var vm = new LabReportViewModel(new MesClient(http)); await vm.RefreshAsync();
        var pending = vm.GenerateAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); vm.Cancel(); await pending;
        Assert.True(vm.IsIdle); Assert.Contains("尚未", vm.SummaryText); Assert.Single(vm.Jobs);
    }
    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token); }
}
