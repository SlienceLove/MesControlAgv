using System.Collections.ObjectModel;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;

namespace MesControlAgv.Application;

/// <summary>Outcome of accepting or rejecting a workflow execution request.</summary>
public enum WorkflowExecutionStatus
{
    Rejected,
    Accepted
}

/// <summary>Stable rejection codes returned by the workflow runtime boundary.</summary>
public static class WorkflowExecutionRejectionCodes
{
    public const string InvalidRequest = "WORKFLOW_REQUEST_INVALID";
    public const string RequestIdRequired = "WORKFLOW_REQUEST_ID_REQUIRED";
    public const string RequestIdReused = "WORKFLOW_REQUEST_ID_REUSED";
    public const string VersionNotFound = "WORKFLOW_VERSION_NOT_FOUND";
    public const string VersionNotPublished = "WORKFLOW_VERSION_NOT_PUBLISHED";
    public const string VersionNotValidated = "WORKFLOW_VERSION_NOT_VALIDATED";
    public const string ValidationFailed = "WORKFLOW_VALIDATION_FAILED";
    public const string RequiredParameter = "WORKFLOW_PARAMETER_REQUIRED";
    public const string NextStepUnavailable = "WORKFLOW_NEXT_STEP_UNAVAILABLE";
    public const string BranchUnsupported = "WORKFLOW_BRANCH_UNSUPPORTED";
    public const string CycleDetected = "WORKFLOW_CYCLE_DETECTED";
}

/// <summary>
/// The first executable step prepared by the runtime. It is a request for a
/// later application/device adapter, not a device command and has no side effect.
/// </summary>
public sealed record WorkflowNextStepRequest
{
    public Guid StepRequestId { get; init; }
    public Guid ExecutionId { get; init; }
    public Guid WorkflowId { get; init; }
    public int Version { get; init; }
    public Guid NodeId { get; init; }
    public WorkflowNodeType NodeType { get; init; }
    public string NodeName { get; init; } = string.Empty;
    public string? TargetStation { get; init; }
    public bool DryRun { get; init; }
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One immutable audit record returned with every runtime decision. Persistence
/// belongs to the consuming application service; the executor never writes it.
/// </summary>
public sealed record WorkflowExecutionAuditEntry
{
    public Guid EventId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string? Code { get; init; }
    public string? Reason { get; init; }
    public Guid RequestId { get; init; }
    public Guid ExecutionId { get; init; }
    public Guid WorkflowId { get; init; }
    public int Version { get; init; }
    public string? RequestedBy { get; init; }
    public string? CorrelationId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string? WorkflowName { get; init; }
    public Guid? NextNodeId { get; init; }
    public IReadOnlyDictionary<string, string?> Details { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Auditable result of the runtime admission decision. A rejected result never
/// contains a next step. An accepted result may contain the first executable
/// step, while the actual step execution remains outside this boundary.
/// </summary>
public sealed record WorkflowExecutionResult
{
    public WorkflowExecutionStatus Status { get; init; }
    public bool IsAccepted => Status == WorkflowExecutionStatus.Accepted;
    public bool IsRejected => Status == WorkflowExecutionStatus.Rejected;
    public bool IsIdempotentReplay { get; init; }
    public Guid RequestId { get; init; }
    public Guid ExecutionId { get; init; }
    public Guid WorkflowId { get; init; }
    public int Version { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public bool DryRun { get; init; }
    public string? RejectionCode { get; init; }
    public string? RejectionReason { get; init; }
    public IReadOnlyList<WorkflowValidationIssue> ValidationIssues { get; init; } =
        Array.Empty<WorkflowValidationIssue>();
    public WorkflowNextStepRequest? NextStepRequest { get; init; }
    public WorkflowNextStepRequest? NextStep => NextStepRequest;
    public bool HasNextStep => NextStepRequest is not null;
    public WorkflowExecutionAuditEntry Audit { get; init; } = new();
}

/// <summary>
/// Minimal runtime for a versioned workflow. It validates admission and creates
/// an auditable next-step request only; it deliberately does not call an AGV,
/// mutate MES task state, or persist execution state.
/// </summary>
public sealed class WorkflowRuntimeExecutor : IWorkflowRuntimeExecutor
{
    private readonly IWorkflowVersionReader _versionReader;
    private readonly WorkflowValidator _validator;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, CachedExecution> _executions = new();

    public WorkflowRuntimeExecutor(
        IWorkflowVersionReader versionReader,
        WorkflowValidator? validator = null,
        TimeProvider? timeProvider = null)
    {
        _versionReader = versionReader ?? throw new ArgumentNullException(nameof(versionReader));
        _validator = validator ?? new WorkflowValidator();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkflowExecutionResult> ExecuteAsync(
        WorkflowExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.RequestId == Guid.Empty)
        {
            return Reject(
                request,
                WorkflowExecutionRejectionCodes.RequestIdRequired,
                "A workflow execution request must contain a non-empty request id.");
        }

        var fingerprint = CreateFingerprint(request);
        var cached = FindCached(request, fingerprint);
        if (cached is not null)
        {
            return cached;
        }

        WorkflowExecutionResult result;
        if (request.WorkflowId == Guid.Empty || request.Version <= 0)
        {
            result = Reject(
                request,
                WorkflowExecutionRejectionCodes.InvalidRequest,
                "Workflow id and version must identify a positive, pinned version.");
        }
        else
        {
            var version = await _versionReader.GetVersionAsync(
                request.WorkflowId,
                request.Version,
                cancellationToken);
            result = Evaluate(request, version);
        }

        return StoreOrReplay(request, fingerprint, result);
    }

    private WorkflowExecutionResult Evaluate(
        WorkflowExecutionRequest request,
        WorkflowVersion? version)
    {
        if (version is null)
        {
            return Reject(
                request,
                WorkflowExecutionRejectionCodes.VersionNotFound,
                $"Workflow version '{request.WorkflowId}/v{request.Version}' was not found.");
        }

        if (version.WorkflowId != request.WorkflowId || version.Version != request.Version)
        {
            return Reject(
                request,
                WorkflowExecutionRejectionCodes.InvalidRequest,
                "The version returned by the application boundary does not match the pinned request.");
        }

        if (version.Status != WorkflowVersionStatus.Published ||
            version.PublishStatus != WorkflowPublishStatus.Published)
        {
            return Reject(
                request,
                WorkflowExecutionRejectionCodes.VersionNotPublished,
                "Only a version with both Published lifecycle and publication status can execute.");
        }

        if (version.Validation is null || !HasNoValidationErrors(version.Validation))
        {
            return Reject(
                request,
                WorkflowExecutionRejectionCodes.VersionNotValidated,
                "The published version does not contain a successful validation result.",
                version.Validation?.Issues);
        }

        WorkflowValidationResult currentValidation;
        try
        {
            currentValidation = _validator.Validate(version.Definition);
        }
        catch (ArgumentException)
        {
            return Reject(
                request,
                WorkflowExecutionRejectionCodes.ValidationFailed,
                "The published workflow definition could not be validated.");
        }

        if (!currentValidation.IsValid)
        {
            return Reject(
                request,
                WorkflowExecutionRejectionCodes.ValidationFailed,
                "The published workflow definition no longer passes validation.",
                currentValidation.Issues);
        }

        var executionId = Guid.NewGuid();
        var resolution = ResolveNextStep(request, version, executionId);
        if (resolution.ErrorCode is not null)
        {
            return Reject(
                request,
                resolution.ErrorCode,
                resolution.ErrorReason!,
                resolution.ValidationIssues);
        }

        var occurredAt = _timeProvider.GetUtcNow();
        var audit = new WorkflowExecutionAuditEntry
        {
            EventId = Guid.NewGuid(),
            EventType = "WorkflowExecutionAccepted",
            Outcome = WorkflowExecutionStatus.Accepted.ToString(),
            RequestId = request.RequestId,
            ExecutionId = executionId,
            WorkflowId = request.WorkflowId,
            Version = request.Version,
            RequestedBy = request.RequestedBy,
            CorrelationId = request.CorrelationId,
            OccurredAt = occurredAt,
            WorkflowName = version.Definition.Name,
            NextNodeId = resolution.NextStep?.NodeId,
            Details = ReadOnlyDetails(
                ("dryRun", request.DryRun.ToString()),
                ("validationVersion", currentValidation.ValidatorVersion))
        };

        return new WorkflowExecutionResult
        {
            Status = WorkflowExecutionStatus.Accepted,
            RequestId = request.RequestId,
            ExecutionId = executionId,
            WorkflowId = request.WorkflowId,
            Version = request.Version,
            RequestedAt = request.RequestedAt,
            DryRun = request.DryRun,
            ValidationIssues = currentValidation.Issues,
            NextStepRequest = resolution.NextStep,
            Audit = audit
        };
    }

    private NextStepResolution ResolveNextStep(
        WorkflowExecutionRequest request,
        WorkflowVersion version,
        Guid executionId)
    {
        var nodes = (version.Definition.Nodes ?? Array.Empty<WorkflowNode>())
            .OrderBy(node => node.Order)
            .ToArray();
        var nodesById = nodes.ToDictionary(node => node.Id);
        var start = nodes.Single(node => node.Type == WorkflowNodeType.Start);
        var hasExplicitEdges = nodes.Any(node => node.NextNodeIds is { Count: > 0 });
        var visited = new HashSet<Guid>();
        var current = start;

        while (true)
        {
            if (!visited.Add(current.Id))
            {
                return NextStepResolution.Error(
                    WorkflowExecutionRejectionCodes.CycleDetected,
                    "The workflow path contains a cycle and cannot be advanced safely.");
            }

            if (current.Type == WorkflowNodeType.End)
            {
                return NextStepResolution.Terminal();
            }

            var nextNodeIds = (current.NextNodeIds ?? Array.Empty<Guid>()).ToArray();
            if (nextNodeIds.Length > 1)
            {
                return NextStepResolution.Error(
                    WorkflowExecutionRejectionCodes.BranchUnsupported,
                    "The minimal runtime does not choose between multiple outgoing workflow branches.");
            }

            WorkflowNode? next = null;
            if (nextNodeIds.Length == 1)
            {
                nodesById.TryGetValue(nextNodeIds[0], out next);
                if (next is null)
                {
                    return NextStepResolution.Error(
                        WorkflowExecutionRejectionCodes.NextStepUnavailable,
                        "The workflow path points to a node that is not present in the validated definition.");
                }
            }
            else if (!hasExplicitEdges)
            {
                next = nodes.FirstOrDefault(node => node.Order > current.Order);
            }
            else
            {
                return NextStepResolution.Error(
                    WorkflowExecutionRejectionCodes.NextStepUnavailable,
                    "The workflow path has no next node for the current control node.");
            }

            if (next is null)
            {
                return NextStepResolution.Terminal();
            }

            current = next;
            if (current.Type is WorkflowNodeType.Start or WorkflowNodeType.End)
            {
                continue;
            }

            var parameters = ResolveParameters(request, current);
            if (parameters.ErrorCode is not null)
            {
                return NextStepResolution.Error(
                    parameters.ErrorCode,
                    parameters.ErrorReason!,
                    parameters.ValidationIssues);
            }

            return NextStepResolution.Step(new WorkflowNextStepRequest
            {
                StepRequestId = Guid.NewGuid(),
                ExecutionId = executionId,
                WorkflowId = request.WorkflowId,
                Version = request.Version,
                NodeId = current.Id,
                NodeType = current.Type,
                NodeName = current.Name,
                TargetStation = current.TargetStation,
                DryRun = request.DryRun,
                Parameters = parameters.Values!
            });
        }
    }

    private static ParameterResolution ResolveParameters(
        WorkflowExecutionRequest request,
        WorkflowNode node)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var parameters = node.Parameters ?? Array.Empty<WorkflowParameter>();
        foreach (var parameter in parameters)
        {
            values[parameter.Name] = parameter.Value;
        }

        foreach (var parameter in parameters)
        {
            if (TryGetParameter(request.Parameters, parameter.Name, out var value))
            {
                values[parameter.Name] = value;
            }
        }

        var missing = parameters
            .Where(parameter => parameter.IsRequired &&
                                string.IsNullOrWhiteSpace(values[parameter.Name]))
            .Select(parameter => parameter.Name)
            .ToArray();
        if (missing.Length > 0)
        {
            return ParameterResolution.Error(
                WorkflowExecutionRejectionCodes.RequiredParameter,
                $"Required workflow parameter(s) are missing: {string.Join(", ", missing)}.",
                missing.Select(name => new WorkflowValidationIssue
                {
                    Code = WorkflowExecutionRejectionCodes.RequiredParameter,
                    Message = $"Required parameter '{name}' has no execution value.",
                    Severity = WorkflowValidationSeverity.Error,
                    NodeId = node.Id,
                    ParameterName = name
                }).ToArray());
        }

        return ParameterResolution.Success(
            new ReadOnlyDictionary<string, string?>(values));
    }

    private static bool TryGetParameter(
        IReadOnlyDictionary<string, string?>? parameters,
        string name,
        out string? value)
    {
        if (parameters is not null)
        {
            foreach (var parameter in parameters)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(parameter.Key, name))
                {
                    value = parameter.Value;
                    return true;
                }
            }
        }

        value = null;
        return false;
    }

    private WorkflowExecutionResult? FindCached(
        WorkflowExecutionRequest request,
        string fingerprint)
    {
        lock (_sync)
        {
            if (!_executions.TryGetValue(request.RequestId, out var cached))
            {
                return null;
            }

            if (cached.Fingerprint == fingerprint)
            {
                return cached.Result with { IsIdempotentReplay = true };
            }

            return Reject(
                request,
                WorkflowExecutionRejectionCodes.RequestIdReused,
                "The request id has already been used for a different workflow execution payload.");
        }
    }

    private WorkflowExecutionResult StoreOrReplay(
        WorkflowExecutionRequest request,
        string fingerprint,
        WorkflowExecutionResult result)
    {
        lock (_sync)
        {
            if (_executions.TryGetValue(request.RequestId, out var cached))
            {
                if (cached.Fingerprint == fingerprint)
                {
                    return cached.Result with { IsIdempotentReplay = true };
                }

                return Reject(
                    request,
                    WorkflowExecutionRejectionCodes.RequestIdReused,
                    "The request id has already been used for a different workflow execution payload.");
            }

            _executions.Add(request.RequestId, new CachedExecution(fingerprint, result));
            return result;
        }
    }

    private WorkflowExecutionResult Reject(
        WorkflowExecutionRequest request,
        string code,
        string reason,
        IReadOnlyList<WorkflowValidationIssue>? validationIssues = null)
    {
        var occurredAt = _timeProvider.GetUtcNow();
        var audit = new WorkflowExecutionAuditEntry
        {
            EventId = Guid.NewGuid(),
            EventType = "WorkflowExecutionRejected",
            Outcome = WorkflowExecutionStatus.Rejected.ToString(),
            Code = code,
            Reason = reason,
            RequestId = request.RequestId,
            WorkflowId = request.WorkflowId,
            Version = request.Version,
            RequestedBy = request.RequestedBy,
            CorrelationId = request.CorrelationId,
            OccurredAt = occurredAt,
            Details = ReadOnlyDetails(("dryRun", request.DryRun.ToString()))
        };

        return new WorkflowExecutionResult
        {
            Status = WorkflowExecutionStatus.Rejected,
            RequestId = request.RequestId,
            WorkflowId = request.WorkflowId,
            Version = request.Version,
            RequestedAt = request.RequestedAt,
            DryRun = request.DryRun,
            RejectionCode = code,
            RejectionReason = reason,
            ValidationIssues = validationIssues ?? Array.Empty<WorkflowValidationIssue>(),
            Audit = audit
        };
    }

    private static bool HasNoValidationErrors(WorkflowValidationResult validation)
    {
        var issues = validation.Issues ?? Array.Empty<WorkflowValidationIssue>();
        return !issues.Any(issue => issue.Severity == WorkflowValidationSeverity.Error);
    }

    private static string CreateFingerprint(WorkflowExecutionRequest request)
    {
        var parameterPart = (request.Parameters ?? new Dictionary<string, string?>())
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key.Length}:{pair.Key}={pair.Value?.Length ?? -1}:{pair.Value}");
        return string.Join(
            '\u001f',
            request.WorkflowId,
            request.Version,
            request.RequestedBy,
            request.CorrelationId,
            request.DryRun,
            string.Join('\u001e', parameterPart));
    }

    private static IReadOnlyDictionary<string, string?> ReadOnlyDetails(
        params (string Key, string? Value)[] values)
    {
        return new ReadOnlyDictionary<string, string?>(
            values.ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase));
    }

    private sealed record CachedExecution(string Fingerprint, WorkflowExecutionResult Result);

    private sealed record NextStepResolution(
        WorkflowNextStepRequest? NextStep,
        string? ErrorCode,
        string? ErrorReason,
        IReadOnlyList<WorkflowValidationIssue> ValidationIssues)
    {
        public static NextStepResolution Step(WorkflowNextStepRequest nextStep) =>
            new(nextStep, null, null, Array.Empty<WorkflowValidationIssue>());

        public static NextStepResolution Terminal() =>
            new(null, null, null, Array.Empty<WorkflowValidationIssue>());

        public static NextStepResolution Error(
            string code,
            string reason,
            IReadOnlyList<WorkflowValidationIssue>? validationIssues = null) =>
            new(null, code, reason, validationIssues ?? Array.Empty<WorkflowValidationIssue>());
    }

    private sealed record ParameterResolution(
        IReadOnlyDictionary<string, string?>? Values,
        string? ErrorCode,
        string? ErrorReason,
        IReadOnlyList<WorkflowValidationIssue> ValidationIssues)
    {
        public static ParameterResolution Success(IReadOnlyDictionary<string, string?> values) =>
            new(values, null, null, Array.Empty<WorkflowValidationIssue>());

        public static ParameterResolution Error(
            string code,
            string reason,
            IReadOnlyList<WorkflowValidationIssue> validationIssues) =>
            new(null, code, reason, validationIssues);
    }
}
