using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;

namespace MesControlAgv.Application;

/// <summary>Compiles references, not model-authored device instructions, into the existing plan contract.</summary>
public static class AiPlanDraftBuilder
{
    public static IReadOnlyList<string> Validate(AiPlanProposal proposal, AiPlanningCatalog catalog)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(proposal.Name) || proposal.Name.Length > 120)
            issues.Add("方案名称不能为空且最多120字符。");
        if (proposal.Explanation is null || proposal.Explanation.Length > 4000 ||
            proposal.SampleBatchId is null || proposal.SampleBatchId.Length > 128)
            issues.Add("方案说明或批次格式不正确。");
        if (proposal.Questions is null || proposal.Questions.Count > 20 ||
            proposal.Questions.Any(q => string.IsNullOrWhiteSpace(q) || q.Length > 500))
            issues.Add("待补信息格式不正确。");
        if (proposal.Steps is null || proposal.Steps.Count > 12)
        {
            issues.Add("方案最多包含12个流程模板。");
            return issues;
        }
        if (proposal.Steps.Count == 0) issues.Add("没有可用的流程步骤，请补充需求或发布匹配模板。");
        var byKey = catalog.Templates.ToDictionary(t => t.Key, StringComparer.Ordinal);
        foreach (var step in proposal.Steps)
        {
            if (step is null || step.TemplateKey is null || !byKey.TryGetValue(step.TemplateKey, out var template))
            {
                issues.Add("模型引用了目录之外的流程模板，不能创建。");
                continue;
            }
            issues.AddRange(template.Issues.Select(i => $"{template.Name}：{i}"));
            if (step.EstimatedDurationMinutes is < 1 or > 1440)
                issues.Add($"{template.Name}：请填写1至1440分钟的预计耗时；这不是设备超时时间。");
        }
        return issues.Distinct().ToArray();
    }

    public static ExperimentPlanDraft Build(AiPlanProposal proposal, AiPlanningCatalog catalog,
        ProfileConfiguration profile, Guid requestId)
    {
        var issues = Validate(proposal, catalog);
        if (issues.Count > 0 || proposal.Questions.Count > 0)
            throw new ArgumentException(string.Join("\n", issues.Concat(proposal.Questions)));
        var templates = proposal.Steps.Select(s => catalog.Templates.Single(t => t.Key == s.TemplateKey)).ToArray();
        var resources = new Dictionary<string, ExperimentResourceRequirement>(StringComparer.OrdinalIgnoreCase);
        void Add(string type, string? id, string? capability = null)
        {
            if (string.IsNullOrWhiteSpace(id) && capability is null) return;
            var resource = new ExperimentResourceRequirement
            { ResourceType = type, ResourceId = id, CapabilityId = capability, Quantity = 1, Exclusive = true };
            resources[$"{type}/{id ?? capability}"] = resource;
        }
        foreach (var node in templates.SelectMany(t => t.Definition.Nodes))
        {
            string? Read(string key) => node.Configuration.TryGetValue(key, out var value)
                ? value : node.Parameters.FirstOrDefault(p => p.Name == key)?.Value;
            Add(ExperimentResourceTypeIds.Station, Read(WorkflowNodeConfigurationKeys.TargetStation) ?? node.TargetStation);
            var id = Read(WorkflowNodeConfigurationKeys.DeviceId);
            if (node.Type == WorkflowNodeType.Move || node.NodeTypeId == WorkflowGraphNodeTypeIds.Move)
            {
                var agvs = profile.Agvs.Where(a => a.Enabled).ToArray();
                Add(ExperimentResourceTypeIds.Agv, agvs.Length == 1 ? agvs[0].AgvId : null,
                    WorkflowCapabilityIds.AgvNavigateToStation);
            }
            if (!string.IsNullOrWhiteSpace(id))
            {
                var device = profile.WorkflowDevices.FirstOrDefault(d => d.DeviceId == id);
                var type = device?.DeviceFamily switch
                {
                    WorkflowDeviceFamilyIds.RobotArm => ExperimentResourceTypeIds.RobotArm,
                    WorkflowDeviceFamilyIds.SampleWorkstation => ExperimentResourceTypeIds.Workstation,
                    WorkflowDeviceFamilyIds.IonChromatography => ExperimentResourceTypeIds.Instrument,
                    _ => null
                };
                if (type is not null) Add(type, id);
            }
            Add(ExperimentResourceTypeIds.Instrument, Read(WorkflowNodeConfigurationKeys.InstrumentId));
        }
        // Stable step ids make retrying the same explicit save idempotent.
        var steps = proposal.Steps.Select((s, index) => new ExperimentPlanWorkflowStep
        {
            StepId = StableId(requestId, index),
            Order = index + 1, WorkflowId = templates[index].WorkflowId, WorkflowVersion = templates[index].Version,
            Name = templates[index].Name, EstimatedDurationMinutes = s.EstimatedDurationMinutes
        }).ToArray();
        return new ExperimentPlanDraft
        {
            Name = proposal.Name.Trim(),
            Description = "AI辅助草稿；需人工复核模板路径、物料交接与实验参数。\n" + proposal.Explanation,
            WorkflowId = steps[0].WorkflowId, WorkflowVersion = steps[0].WorkflowVersion, WorkflowSteps = steps,
            ProfileProductId = catalog.ProductId, ProfileVersion = catalog.ProfileVersion,
            ResourceRequirements = resources.Values.ToArray(),
            DefaultParameters = string.IsNullOrWhiteSpace(proposal.SampleBatchId) ? new Dictionary<string, string?>() :
                new Dictionary<string, string?> { [WorkflowRuntimeParameterNames.SampleBatchId] = proposal.SampleBatchId }
        };
    }

    private static Guid StableId(Guid requestId, int index) =>
        new(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{requestId:N}/{index}"))[..16]);
}
