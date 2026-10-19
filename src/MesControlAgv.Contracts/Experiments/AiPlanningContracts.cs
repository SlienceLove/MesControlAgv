using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Contracts.Experiments;

public sealed record AiWorkflowTemplate
{
    public string Key { get; init; } = "";
    public Guid WorkflowId { get; init; }
    public int Version { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public WorkflowDefinition Definition { get; init; } = new();
    public IReadOnlyList<string> Issues { get; init; } = [];
}

public sealed record AiPlanningCatalog
{
    public string Revision { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string ProfileVersion { get; init; } = "";
    public IReadOnlyList<AiWorkflowTemplate> Templates { get; init; } = [];
}

public sealed record AiPlanningRequest
{
    public string Message { get; init; } = "";
    public IReadOnlyList<string> TemplateKeys { get; init; } = [];
    // User-written conversation only; never model-generated authority.
    public IReadOnlyList<string> PreviousMessages { get; init; } = [];
}

public sealed record AiPlanStep
{
    public string TemplateKey { get; init; } = "";
    public int EstimatedDurationMinutes { get; init; }
}

public sealed record AiPlanProposal
{
    public string Name { get; init; } = "";
    public string Explanation { get; init; } = "";
    public string SampleBatchId { get; init; } = "";
    public IReadOnlyList<AiPlanStep> Steps { get; init; } = [];
    public IReadOnlyList<string> Questions { get; init; } = [];
}

public sealed record AiPlanningResult
{
    public bool ModelAvailable { get; init; }
    public string Message { get; init; } = "";
    public string Model { get; init; } = "";
    public string CatalogRevision { get; init; } = "";
    public AiPlanProposal Proposal { get; init; } = new();
    public IReadOnlyList<string> Issues { get; init; } = [];
    public bool CanCreateDraft => ModelAvailable && Issues.Count == 0 &&
        Proposal.Questions.Count == 0 && Proposal.Steps.Count > 0;
}

public sealed record CreateAiPlanDraftRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = "";
    public string UserIntent { get; init; } = "";
    public string CatalogRevision { get; init; } = "";
    public AiPlanProposal Proposal { get; init; } = new();
}
