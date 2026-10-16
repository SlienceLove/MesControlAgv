using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Mes.Data;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public sealed class AiPlanningService(MesDbContext db, IWorkflowApplicationService workflows,
    IExperimentSchedulingCommandService plans, IAiPlanningModel model, ProfileConfiguration profile)
{
    public async Task<AiPlanningCatalog> CatalogAsync(CancellationToken token)
    {
        var rows = await db.WorkflowVersions.AsNoTracking()
            .Where(w => w.Status == "Published" && w.PublishStatus == "Published")
            .OrderBy(w => w.WorkflowId).ThenByDescending(w => w.Version).ToArrayAsync(token);
        var latest = rows.DistinctBy(w => w.WorkflowId).ToArray();
        if (latest.Length > 100) throw new InvalidOperationException("已发布模板超过100个，请先整理目录；本轮不静默截断。");
        var templates = new List<AiWorkflowTemplate>();
        foreach (var row in latest)
        {
            var definition = WorkflowPersistence.DeserializeDefinition(row.DefinitionJson);
            // ValidateAsync is read-only; ValidateVersionAsync would mutate the existing version.
            var validation = await workflows.ValidateAsync(definition, token);
            templates.Add(new AiWorkflowTemplate
            {
                Key = $"{row.WorkflowId:N}/v{row.Version}", WorkflowId = row.WorkflowId, Version = row.Version,
                Name = definition.Name, Description = definition.Description, Definition = definition,
                Issues = validation.Issues
                    .Where(i => i.Severity == WorkflowValidationSeverity.Error)
                    .Select(i => i.Message).ToArray()
            });
        }
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            catalogVersion = BuiltInWorkflowCatalog.CurrentCatalogVersion,
            profile.Product, profile.Agvs, profile.Stations, profile.WorkflowDevices, templates
        }))));
        return new AiPlanningCatalog
        {
            Revision = revision, ProductId = profile.Product.ProductId,
            ProfileVersion = profile.Product.Version, Templates = templates
        };
    }

    public async Task<AiPlanningResult> GenerateAsync(AiPlanningRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 2000 ||
            request.PreviousMessages is null || request.PreviousMessages.Count > 8 ||
            request.PreviousMessages.Any(m => string.IsNullOrWhiteSpace(m) || m.Length > 2000))
            throw new ArgumentException("请输入1至2000字符需求，最多携带8轮历史描述。");
        var catalog = await CatalogAsync(token);
        if (request.TemplateKeys is null || request.TemplateKeys.Count > 100 ||
            request.TemplateKeys.Any(key => !catalog.Templates.Any(t => t.Key == key)))
            throw new ArgumentException("所选模板已变化或不在目录中，请刷新模板范围。");
        var scope = request.TemplateKeys.Count == 0 ? catalog : catalog with
        { Templates = catalog.Templates.Where(t => request.TemplateKeys.Contains(t.Key)).ToArray() };
        if (!scope.Templates.Any(t => t.Issues.Count == 0))
            return new AiPlanningResult
            {
                CatalogRevision = catalog.Revision, Model = model.ModelName,
                Message = "没有通过当前框架校验的已发布模板，请先在流程编辑器准备并发布模板。",
                Issues = ["尚无可选择的流程模板；没有调用模型。"]
            };
        try
        {
            var proposal = await model.ProposeAsync(request, scope, token);
            var issues = AiPlanDraftBuilder.Validate(proposal, scope);
            if (proposal.Steps is not null && proposal.Steps.Any(s => s is not null && !scope.Templates.Any(t => t.Key == s.TemplateKey)))
                throw new InvalidOperationException("模型引用了本轮范围之外的模板。");
            if (proposal.Steps is null || proposal.Questions is null || proposal.Steps.Any(s => s is null))
                throw new InvalidOperationException("模型方案结构不完整。");
            return new AiPlanningResult
            {
                ModelAvailable = true, Model = model.ModelName, CatalogRevision = catalog.Revision,
                Proposal = proposal, Issues = issues,
                Message = issues.Count == 0 && proposal.Questions.Count == 0
                    ? "方案已生成。请复核固定站点、取放动作、估计耗时和参数后创建草稿；未执行任何任务。"
                    : "方案存在待补信息，请补充描述后重新生成；尚未创建计划。"
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or
                                   KeyNotFoundException or OperationCanceledException or ArgumentException)
        {
            token.ThrowIfCancellationRequested();
            return new AiPlanningResult
            {
                CatalogRevision = catalog.Revision, Model = model.ModelName,
                Message = "模型未能生成可确认的方案：" + ex.Message,
                Issues = ["可继续使用原实验方案编辑器；未创建计划或执行设备。"]
            };
        }
    }

    public async Task<ExperimentPlan> CreateDraftAsync(CreateAiPlanDraftRequest request, CancellationToken token)
    {
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Actor) || request.Proposal is null ||
            request.UserIntent is null || request.UserIntent.Length > 18000)
            throw new ArgumentException("创建草稿必须提供请求编号、操作者和方案。");
        var catalog = await CatalogAsync(token);
        if (!string.Equals(request.CatalogRevision, catalog.Revision, StringComparison.Ordinal))
            throw new InvalidOperationException("模板或配置已改变，请重新生成并复核方案。");
        var draft = AiPlanDraftBuilder.Build(request.Proposal, catalog, profile, request.RequestId);
        draft = draft with { Description = draft.Description + "\n用户需求记录：\n" + request.UserIntent };
        // Existing idempotent application boundary; no workflow/job publication or admission dependency.
        return await plans.CreatePlanDraftAsync(new SaveExperimentPlanDraftRequest
        {
            RequestId = request.RequestId, Actor = request.Actor,
            Reason = "用户确认AI模板组合草稿；目录版本：" + catalog.Revision, Draft = draft
        }, token);
    }
}
