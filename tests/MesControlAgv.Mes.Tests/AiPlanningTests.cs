using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MesControlAgv.Mes.Tests;

public sealed class AiPlanningTests
{
    private sealed class FakeModel : IAiPlanningModel
    {
        public string ModelName => "offline-test";
        public Func<AiPlanningRequest, AiPlanningCatalog, AiPlanProposal> Reply { get; set; } =
            (_, c) => new AiPlanProposal
            {
                Name = "取料后送达", SampleBatchId = "B001", Explanation = "依次使用已发布模板，耗时为估计。",
                Steps = c.Templates.Where(t => t.Issues.Count == 0).Take(2)
                    .Select(t => new AiPlanStep { TemplateKey = t.Key, EstimatedDurationMinutes = 3 }).ToArray()
            };
        public Task<AiPlanProposal> ProposeAsync(AiPlanningRequest request, AiPlanningCatalog catalog, CancellationToken token) =>
            Task.FromResult(Reply(request, catalog));
    }

    [Fact]
    public async Task ProposeCreateValidateUsesExistingPlanChainAndNeverCreatesAJobOrRun()
    {
        using var source = new MesWebApplicationFactory();
        var model = new FakeModel();
        using var factory = source.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        { s.RemoveAll<IAiPlanningModel>(); s.AddSingleton<IAiPlanningModel>(model); }));
        using var http = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            foreach (var name in new[] { "去待检区", "送样品到检测位" })
            {
                var definition = WorkflowTestDefinitions.CreateMoveWorkflow() with { Name = name };
                db.WorkflowVersions.Add(new WorkflowVersionRecord
                {
                    WorkflowId = definition.Id, Version = 1, Status = "Published", PublishStatus = "Published",
                    DefinitionJson = JsonSerializer.Serialize(definition), CreatedBy = "test"
                });
            }
            await db.SaveChangesAsync();
        }
        using var response = await http.PostAsJsonAsync("/api/ai-planning/propose",
            new AiPlanningRequest { Message = "按两段模板运输样品" });
        response.EnsureSuccessStatusCode();
        var proposal = (await response.Content.ReadFromJsonAsync<AiPlanningResult>())!;
        Assert.True(proposal.CanCreateDraft, string.Join(";", proposal.Issues));
        Assert.Equal(2, proposal.Proposal.Steps.Count);
        using (var scoped = await http.PostAsJsonAsync("/api/ai-planning/propose", new AiPlanningRequest
        { Message = "只使用指定模板", TemplateKeys = [proposal.Proposal.Steps[0].TemplateKey] }))
            Assert.Single((await scoped.Content.ReadFromJsonAsync<AiPlanningResult>())!.Proposal.Steps);
        using (var invalidScope = await http.PostAsJsonAsync("/api/ai-planning/propose", new AiPlanningRequest
        { Message = "指定不存在模板", TemplateKeys = ["not-in-catalog"] }))
            Assert.Equal(HttpStatusCode.BadRequest, invalidScope.StatusCode);
        model.Reply = (_, _) => proposal.Proposal with { Steps = [new() { TemplateKey = "invented", EstimatedDurationMinutes = 1 }] };
        using (var invalidModel = await http.PostAsJsonAsync("/api/ai-planning/propose", new AiPlanningRequest { Message = "乱编模板" }))
            Assert.False((await invalidModel.Content.ReadFromJsonAsync<AiPlanningResult>())!.ModelAvailable);
        using (var scope = factory.Services.CreateScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<MesDbContext>().ExperimentPlans.ToArrayAsync());
        var request = new CreateAiPlanDraftRequest
        {
            RequestId = Guid.NewGuid(), Actor = "test-operator", CatalogRevision = proposal.CatalogRevision,
            Proposal = proposal.Proposal
        };
        using var created = await http.PostAsJsonAsync("/api/ai-planning/drafts", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var plan = (await created.Content.ReadFromJsonAsync<ExperimentPlan>())!;
        Assert.Equal(ExperimentPlanStatus.Draft, plan.Status);
        Assert.Equal(2, plan.WorkflowSteps.Count);
        Assert.Equal("B001", plan.DefaultParameters["sampleBatchId"]);
        Assert.Contains(plan.ResourceRequirements, r => r.ResourceType == "agv");
        using var repeated = await http.PostAsJsonAsync("/api/ai-planning/drafts", request);
        Assert.Equal(plan.PlanId, (await repeated.Content.ReadFromJsonAsync<ExperimentPlan>())!.PlanId);
        using (var scope = factory.Services.CreateScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IExperimentSchedulingCommandService>();
            var validated = await commands.ValidatePlanAsync(plan.PlanId, plan.Version,
                new ExperimentSchedulingActionRequest { RequestId = Guid.NewGuid(), Actor = "test", Reason = "Offline test" },
                CancellationToken.None);
            Assert.True(validated.Validation!.IsValid, string.Join(";", validated.Validation.Issues.Select(i => i.Message)));
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            Assert.Single(await db.ExperimentPlans.ToArrayAsync());
            Assert.Empty(await db.ExperimentJobs.ToArrayAsync());
            Assert.Empty(await db.WorkflowExecutions.ToArrayAsync());
        }
        using var stale = await http.PostAsJsonAsync("/api/ai-planning/drafts", request with { CatalogRevision = "stale" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var missing = await http.PostAsJsonAsync("/api/ai-planning/drafts",
            request with { RequestId = Guid.NewGuid(), Proposal = proposal.Proposal with { Questions = ["在哪取料？"] } });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var template = await db.WorkflowVersions.FirstAsync();
            template.Status = "Archived"; template.PublishStatus = "Withdrawn"; await db.SaveChangesAsync();
        }
        using var withdrawn = await http.PostAsJsonAsync("/api/ai-planning/drafts", request with { RequestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, withdrawn.StatusCode);
    }

    [Fact]
    public async Task EmptyCatalogDoesNotCallModelAndInvalidRequestDoesNotWrite()
    {
        using var source = new MesWebApplicationFactory();
        var model = new FakeModel { Reply = (_, _) => throw new Exception("must not call") };
        using var factory = source.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        { s.RemoveAll<IAiPlanningModel>(); s.AddSingleton<IAiPlanningModel>(model); }));
        using var http = factory.CreateClient();
        using var response = await http.PostAsJsonAsync("/api/ai-planning/propose", new AiPlanningRequest { Message = "帮我实验" });
        var result = (await response.Content.ReadFromJsonAsync<AiPlanningResult>())!;
        Assert.False(result.CanCreateDraft); Assert.False(result.ModelAvailable);
        using var invalid = await http.PostAsJsonAsync("/api/ai-planning/propose", new AiPlanningRequest());
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Theory]
    [InlineData("invented")]
    [InlineData("")]
    public void UnknownTemplateCannotCompile(string key)
    {
        var proposal = new AiPlanProposal { Name = "test", Steps = [new() { TemplateKey = key, EstimatedDurationMinutes = 1 }] };
        Assert.Contains(AiPlanDraftBuilder.Validate(proposal, new()), i => i.Contains("目录之外"));
    }

    [Fact]
    public void CatalogIssuesAndInvalidDurationRemainBlocking()
    {
        var catalog = new AiPlanningCatalog { Templates = [new() { Key = "x", Name = "disabled", Issues = ["设备已禁用"] }] };
        var proposal = new AiPlanProposal { Name = "test", Steps = [new() { TemplateKey = "x" }] };
        var issues = AiPlanDraftBuilder.Validate(proposal, catalog);
        Assert.Contains(issues, i => i.Contains("设备已禁用"));
        Assert.Contains(issues, i => i.Contains("预计耗时"));
    }
}
