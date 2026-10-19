using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class AiPlanningViewModelTests
{
    private static AiPlanningCatalog Catalog => new()
    { Revision = "r1", Templates = [new() { Key = "t1", Name = "取料转运", WorkflowId = Guid.NewGuid(), Version = 1 }] };
    private static AiPlanningResult Result => new()
    {
        ModelAvailable = true, CatalogRevision = "r1", Message = "生成成功",
        Proposal = new() { Name = "方案", Steps = [new() { TemplateKey = "t1", EstimatedDurationMinutes = 3 }] }
    };
    private static HttpResponseMessage Ok<T>(T data) => new(HttpStatusCode.OK) { Content = JsonContent.Create(data) };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(new Handler(handler)) { BaseAddress = new("http://offline.invalid/") };
    private static Task<HttpResponseMessage> Standard(HttpRequestMessage r, CancellationToken t) =>
        Task.FromResult(r.Method == HttpMethod.Get ? Ok(Catalog) : Ok(Result));

    [Fact]
    public async Task MustReviewAndNewInputInvalidatesReview()
    {
        using var http = Client(Standard); using var vm = new AiPlanningViewModel(new MesClient(http));
        vm.Input = "取料送分液"; await vm.GenerateAsync();
        Assert.Single(vm.Steps); Assert.False(vm.CanSave);
        vm.Reviewed = true; Assert.True(vm.CanSave);
        vm.Input = "改成2号工作站"; Assert.False(vm.CanSave);
        vm.Reviewed = true; Assert.False(vm.CanSave); // Pending unsent requirement cannot use old proposal.
    }

    [Fact]
    public async Task DurationEditRequiresReviewAndInvalidValuesBlock()
    {
        using var http = Client(Standard); using var vm = new AiPlanningViewModel(new MesClient(http));
        vm.Input = "转运"; await vm.GenerateAsync(); vm.Reviewed = true;
        vm.Steps[0].EstimatedDurationMinutes = 0;
        Assert.False(vm.Reviewed); Assert.False(vm.CanSave);
        vm.Steps[0].EstimatedDurationMinutes = 5; vm.Reviewed = true;
        Assert.True(vm.CanSave);
        vm.Steps[0].DurationText = "abc"; vm.Reviewed = true;
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task QuestionsAndChangedCatalogBlockCreation()
    {
        using var http = Client((r, _) => Task.FromResult(r.Method == HttpMethod.Get ? Ok(Catalog) :
            Ok(Result with { Proposal = Result.Proposal with { Questions = ["哪个取料位？"] } })));
        using var vm = new AiPlanningViewModel(new MesClient(http));
        vm.Input = "转运"; await vm.GenerateAsync(); vm.Reviewed = true; Assert.False(vm.CanSave);
        using var staleHttp = Client((r, _) => Task.FromResult(r.Method == HttpMethod.Get ? Ok(Catalog) :
            Ok(Result with { CatalogRevision = "r2" })));
        using var stale = new AiPlanningViewModel(new MesClient(staleHttp));
        stale.Input = "转运"; await stale.GenerateAsync();
        Assert.Empty(stale.Steps); Assert.False(stale.CanSave); Assert.Contains("目录已改变", stale.Status);
    }

    [Fact]
    public async Task UncertainSaveRetainsRequestIdAndDoesNotGenerateDuplicate()
    {
        var ids = new List<Guid>(); var calls = new List<string>(); var plan = new ExperimentPlan { PlanId = Guid.NewGuid(), Version = 1 };
        using var http = Client(async (r, t) =>
        {
            var path = r.RequestUri!.AbsolutePath; calls.Add(path);
            if (path == "/api/ai-planning/catalog") return Ok(Catalog);
            if (path == "/api/ai-planning/propose") return Ok(Result);
            if (path.EndsWith("/validate")) return Ok(plan with { Validation = new() });
            var body = (await r.Content!.ReadFromJsonAsync<CreateAiPlanDraftRequest>(cancellationToken: t))!;
            ids.Add(body.RequestId);
            if (ids.Count == 1) throw new HttpRequestException("response lost");
            return Ok(plan);
        });
        using var vm = new AiPlanningViewModel(new MesClient(http));
        vm.Input = "转运"; await vm.GenerateAsync(); vm.Reviewed = true; await vm.SaveAsync();
        Assert.Null(vm.SavedPlan); Assert.True(vm.CanSave); Assert.False(vm.CanEdit);
        await vm.SaveAsync();
        Assert.Equal(2, ids.Count); Assert.Equal(ids[0], ids[1]); Assert.Equal(plan.PlanId, vm.SavedPlan!.PlanId);
        Assert.False(vm.CanSave); Assert.DoesNotContain(calls, p => p.Contains("execute") || p.Contains("publish") || p.Contains("admit"));
    }

    [Fact]
    public async Task FailedRegenerationClearsPreviousProposalAndCancellationIsUsable()
    {
        var count = 0;
        using var http = Client((r, _) =>
        {
            if (r.Method == HttpMethod.Get) return Task.FromResult(Ok(Catalog));
            if (++count == 2) throw new HttpRequestException("offline");
            return Task.FromResult(Ok(Result));
        });
        using var vm = new AiPlanningViewModel(new MesClient(http));
        vm.Input = "转运"; await vm.GenerateAsync(); Assert.Single(vm.Steps);
        vm.Input = "换个终点"; await vm.GenerateAsync();
        Assert.Empty(vm.Steps); Assert.False(vm.CanSave); Assert.True(vm.IsIdle);
    }

    [Fact]
    public async Task FollowUpCarriesUserMessagesButNotModelClaims()
    {
        AiPlanningRequest? last = null;
        using var http = Client(async (r, token) =>
        {
            if (r.Method == HttpMethod.Get) return Ok(Catalog);
            last = await r.Content!.ReadFromJsonAsync<AiPlanningRequest>(cancellationToken: token);
            return Ok(Result);
        });
        using var vm = new AiPlanningViewModel(new MesClient(http));
        await vm.RefreshCatalogAsync();
        vm.Templates[0].Selected = true;
        vm.Input = "批次B001"; await vm.GenerateAsync();
        vm.Input = "再送分液站"; await vm.GenerateAsync();
        Assert.Equal("批次B001", Assert.Single(last!.PreviousMessages));
        Assert.Equal("再送分液站", last.Message);
        Assert.Equal("t1", Assert.Single(last.TemplateKeys));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token); }
}
