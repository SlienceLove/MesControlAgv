using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using MesControlAgv.Application;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// EF Core backed version reader used by the runtime executor.  Definitions are
/// stored as immutable JSON snapshots so changes to the WPF editor model do not
/// mutate an already pinned workflow version.
/// </summary>
public sealed class MesWorkflowVersionReader(MesDbContext database) : IWorkflowVersionReader
{
    public async Task<WorkflowVersion?> GetVersionAsync(
        Guid workflowId,
        int version,
        CancellationToken cancellationToken)
    {
        var record = await database.WorkflowVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.WorkflowId == workflowId && item.Version == version,
                cancellationToken);
        if (record is null)
        {
            return null;
        }

        var publishedVersion = await WorkflowPersistence.GetPublishedVersionAsync(
            database,
            workflowId,
            cancellationToken);
        return WorkflowPersistence.ToContract(record, publishedVersion);
    }
}

/// <summary>
/// MES persistence implementation for the intentionally small workflow MVP. It
/// owns draft/version lifecycle changes and persists runtime admission results;
/// the application runtime remains side-effect free and never calls an AGV.
/// </summary>
public sealed class WorkflowApplicationService : IWorkflowApplicationService
{
    private readonly MesDbContext _database;
    private readonly IWorkflowVersionReader _versionReader;
    private readonly WorkflowRuntimeExecutor _runtimeExecutor;
    private readonly WorkflowValidator _validator;
    private readonly TimeProvider _timeProvider;

    public WorkflowApplicationService(
        MesDbContext database,
        IWorkflowVersionReader versionReader,
        WorkflowRuntimeExecutor runtimeExecutor,
        WorkflowValidator validator,
        TimeProvider? timeProvider = null)
    {
        _database = database;
        _versionReader = versionReader;
        _runtimeExecutor = runtimeExecutor;
        _validator = validator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken)
    {
        var records = await _database.WorkflowVersions
            .AsNoTracking()
            .OrderBy(item => item.WorkflowId)
            .ThenByDescending(item => item.Version)
            .ToListAsync(cancellationToken);

        return records
            .GroupBy(item => item.WorkflowId)
            .Select(group =>
            {
                var latest = group.First();
                var publishedVersion = group
                    .Where(WorkflowPersistence.IsPublished)
                    .Select(item => (int?)item.Version)
                    .OrderByDescending(item => item)
                    .FirstOrDefault();
                return WorkflowPersistence.ToDefinition(latest, publishedVersion);
            })
            .ToArray();
    }

    public async Task<WorkflowDefinition?> GetAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        var records = await _database.WorkflowVersions
            .AsNoTracking()
            .Where(item => item.WorkflowId == workflowId)
            .OrderByDescending(item => item.Version)
            .ToListAsync(cancellationToken);
        if (records.Count == 0)
        {
            return null;
        }

        var publishedVersion = records
            .Where(WorkflowPersistence.IsPublished)
            .Select(item => (int?)item.Version)
            .FirstOrDefault();
        return WorkflowPersistence.ToDefinition(records[0], publishedVersion);
    }

    public async Task<IReadOnlyList<WorkflowVersion>> ListVersionsAsync(
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        var records = await _database.WorkflowVersions
            .AsNoTracking()
            .Where(item => item.WorkflowId == workflowId)
            .OrderByDescending(item => item.Version)
            .ToListAsync(cancellationToken);
        var publishedVersion = records
            .Where(WorkflowPersistence.IsPublished)
            .Select(item => (int?)item.Version)
            .FirstOrDefault();
        return records
            .Select(item => WorkflowPersistence.ToContract(item, publishedVersion))
            .ToArray();
    }

    public async Task<WorkflowExecutionSnapshot?> GetExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        if (executionId == Guid.Empty)
        {
            return null;
        }

        var record = await _database.WorkflowExecutions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ExecutionId == executionId, cancellationToken);
        return record is null ? null : WorkflowPersistence.ToExecutionSnapshot(record);
    }

    public async Task<WorkflowExecutionSnapshot?> GetExecutionByRequestAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
        {
            return null;
        }

        var record = await _database.WorkflowExecutions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken);
        return record is null ? null : WorkflowPersistence.ToExecutionSnapshot(record);
    }

    public async Task<IReadOnlyList<WorkflowExecutionSnapshot>> ListRecoverableExecutionsAsync(
        CancellationToken cancellationToken)
    {
        var records = await _database.WorkflowExecutions
            .AsNoTracking()
            .Where(item => item.RuntimeStatus == WorkflowRuntimeStatus.Running.ToString() ||
                           item.RuntimeStatus == WorkflowRuntimeStatus.Unknown.ToString())
            .OrderBy(item => item.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
        return records.Select(WorkflowPersistence.ToExecutionSnapshot).ToArray();
    }

    public async Task<IReadOnlyList<WorkflowExecutionSnapshot>> ListSimulatorDispatchableExecutionsAsync(
        CancellationToken cancellationToken)
    {
        var records = await _database.WorkflowExecutions
            .AsNoTracking()
            .Where(item => item.RuntimeStatus == WorkflowRuntimeStatus.Prepared.ToString())
            .OrderBy(item => item.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
        return records
            .Select(WorkflowPersistence.ToExecutionSnapshot)
            .Where(snapshot => !snapshot.DryRun &&
                               snapshot.PendingStepRequest is { } step &&
                               ((step.NodeType == WorkflowNodeType.Move &&
                                 !string.IsNullOrWhiteSpace(step.TargetStation)) ||
                                (step.NodeType == WorkflowNodeType.Wait &&
                                 step.Parameters.Keys.Any(key => StringComparer.OrdinalIgnoreCase.Equals(
                                     key,
                                     WorkflowRuntimeParameterNames.WaitDurationSeconds)))))
            .ToArray();
    }

    public async Task<WorkflowExecutionSnapshot> ClaimNextStepAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        var record = await FindExecutionAsync(executionId, cancellationToken);
        var snapshot = WorkflowPersistence.ToExecutionSnapshot(record);
        if (snapshot.RuntimeStatus == WorkflowRuntimeStatus.Running)
        {
            return snapshot;
        }

        if (snapshot.RuntimeStatus != WorkflowRuntimeStatus.Prepared || snapshot.PendingStepRequest is null)
        {
            throw new InvalidOperationException("Only a prepared workflow execution with a pending step can be claimed.");
        }

        var attempt = record.Attempt + 1;
        record.TransportOperationId = WorkflowPersistence.CreateStableOperationId(
            record.ExecutionId,
            snapshot.PendingStepRequest.NodeId,
            attempt);
        record.Attempt = attempt;
        record.RuntimeStatus = WorkflowRuntimeStatus.Running.ToString();
        record.LastError = null;
        record.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        AddRuntimeAudit(record, "WorkflowStepClaimed", "Running", null, new Dictionary<string, string?>
        {
            ["nodeId"] = snapshot.PendingStepRequest.NodeId.ToString(),
            ["transportOperationId"] = record.TransportOperationId.Value.ToString(),
            ["attempt"] = attempt.ToString()
        });
        await _database.SaveChangesAsync(cancellationToken);
        return WorkflowPersistence.ToExecutionSnapshot(record);
    }

    public async Task<WorkflowExecutionSnapshot> CompleteClaimedStepAsync(
        Guid executionId,
        WorkflowStepCompletionRequest completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var record = await FindExecutionAsync(executionId, cancellationToken);
        var snapshot = WorkflowPersistence.ToExecutionSnapshot(record);
        if (snapshot.RuntimeStatus != WorkflowRuntimeStatus.Running ||
            record.TransportOperationId is null ||
            completion.TransportOperationId == Guid.Empty ||
            record.TransportOperationId != completion.TransportOperationId)
        {
            throw new InvalidOperationException("The completion does not match a claimed workflow step.");
        }

        var error = string.IsNullOrWhiteSpace(completion.Error) ? null : completion.Error.Trim();
        if (completion.Outcome == WorkflowStepCompletionOutcome.Succeeded)
        {
            var nextStep = WorkflowPersistence.ResolveFollowingStep(record, snapshot.PendingStepRequest!);
            record.CurrentNodeId = nextStep?.NodeId ?? snapshot.PendingStepRequest!.NodeId;
            record.PendingStepJson = nextStep is null ? null : WorkflowPersistence.Serialize(nextStep);
            record.TransportOperationId = null;
            record.Attempt = 0;
            record.LastError = null;
            record.RuntimeStatus = (nextStep is null
                ? WorkflowRuntimeStatus.Completed
                : WorkflowRuntimeStatus.Prepared).ToString();
            AddRuntimeAudit(record, "WorkflowStepCompleted", record.RuntimeStatus, null, new Dictionary<string, string?>
            {
                ["transportOperationId"] = completion.TransportOperationId.ToString(),
                ["nextNodeId"] = nextStep?.NodeId.ToString()
            });
        }
        else
        {
            record.RuntimeStatus = completion.Outcome switch
            {
                WorkflowStepCompletionOutcome.Failed => WorkflowRuntimeStatus.Failed.ToString(),
                WorkflowStepCompletionOutcome.Unknown => WorkflowRuntimeStatus.Unknown.ToString(),
                WorkflowStepCompletionOutcome.Cancelled => WorkflowRuntimeStatus.Cancelled.ToString(),
                _ => throw new ArgumentOutOfRangeException(nameof(completion))
            };
            record.LastError = error ?? completion.Outcome.ToString();
            AddRuntimeAudit(record, "WorkflowStepReconciled", record.RuntimeStatus, record.LastError, new Dictionary<string, string?>
            {
                ["transportOperationId"] = completion.TransportOperationId.ToString()
            });
        }

        record.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        await _database.SaveChangesAsync(cancellationToken);
        return WorkflowPersistence.ToExecutionSnapshot(record);
    }

    public async Task<IReadOnlyList<WorkflowAuditResponse>> ListAuditsAsync(
        Guid workflowId,
        int? version,
        int limit,
        CancellationToken cancellationToken)
    {
        if (workflowId == Guid.Empty)
        {
            throw new ArgumentException("A workflow id is required.", nameof(workflowId));
        }

        var boundedLimit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);
        var query = _database.WorkflowAudits
            .AsNoTracking()
            .Where(audit => audit.WorkflowId == workflowId);
        if (version is not null)
        {
            query = query.Where(audit => audit.Version == version.Value);
        }

        var records = await query
            .OrderBy(audit => audit.OccurredAtUtc)
            .ThenBy(audit => audit.Id)
            .Take(boundedLimit)
            .ToListAsync(cancellationToken);

        return records.Select(WorkflowPersistence.ToAuditContract).ToArray();
    }

    public Task<WorkflowVersion?> GetVersionAsync(
        Guid workflowId,
        int version,
        CancellationToken cancellationToken) =>
        _versionReader.GetVersionAsync(workflowId, version, cancellationToken);

    public async Task<WorkflowVersion> CreateDraftAsync(
        WorkflowDefinition definition,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var workflowId = definition.Id == Guid.Empty ? Guid.NewGuid() : definition.Id;
        var normalizedDefinition = NormalizeDefinition(definition, workflowId);
        var existingVersions = await _database.WorkflowVersions
            .Where(item => item.WorkflowId == workflowId)
            .Select(item => item.Version)
            .ToListAsync(cancellationToken);
        var version = existingVersions.DefaultIfEmpty(0).Max() + 1;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var record = new WorkflowVersionRecord
        {
            WorkflowId = workflowId,
            Version = version,
            DefinitionJson = WorkflowPersistence.Serialize(normalizedDefinition),
            Status = WorkflowVersionStatus.Draft.ToString(),
            PublishStatus = WorkflowPublishStatus.NotPublished.ToString(),
            CreatedBy = RequireActor(actor),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _database.WorkflowVersions.Add(record);
        AddLifecycleAudit(record, "WorkflowDraftCreated", "Draft", actor, null, null);
        await _database.SaveChangesAsync(cancellationToken);
        return WorkflowPersistence.ToContract(record, null);
    }

    public async Task<WorkflowVersion> UpdateDraftAsync(
        Guid workflowId,
        int version,
        WorkflowDefinition definition,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var record = await FindVersionAsync(workflowId, version, cancellationToken);
        if (WorkflowPersistence.ParseStatus(record) != WorkflowVersionStatus.Draft ||
            WorkflowPersistence.ParsePublishStatus(record) != WorkflowPublishStatus.NotPublished)
        {
            throw new InvalidOperationException("Only an unpublished draft version can be edited.");
        }

        if (definition.Id != Guid.Empty && definition.Id != workflowId)
        {
            throw new InvalidOperationException("The draft payload workflow id does not match the addressed workflow.");
        }

        record.DefinitionJson = WorkflowPersistence.Serialize(NormalizeDefinition(definition, workflowId));
        record.ValidationJson = null;
        record.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        AddLifecycleAudit(record, "WorkflowDraftUpdated", "Draft", actor, null, null);
        await _database.SaveChangesAsync(cancellationToken);
        return WorkflowPersistence.ToContract(record, await GetPublishedVersionAsync(workflowId, cancellationToken));
    }

    public Task<WorkflowValidationResult> ValidateAsync(
        WorkflowDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_validator.Validate(definition));
    }

    public async Task<WorkflowValidationResult> ValidateVersionAsync(
        Guid workflowId,
        int version,
        CancellationToken cancellationToken)
    {
        var record = await FindVersionAsync(workflowId, version, cancellationToken);
        var definition = WorkflowPersistence.DeserializeDefinition(record.DefinitionJson);
        var result = _validator.Validate(definition);
        record.ValidationJson = WorkflowPersistence.Serialize(result);
        record.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        if (WorkflowPersistence.ParseStatus(record) == WorkflowVersionStatus.Draft)
        {
            record.Status = result.IsValid
                ? WorkflowVersionStatus.Validated.ToString()
                : WorkflowVersionStatus.Draft.ToString();
        }

        AddLifecycleAudit(
            record,
            "WorkflowVersionValidated",
            result.IsValid ? "Valid" : "Invalid",
            actor: null,
            code: result.IsValid ? null : "WORKFLOW_VALIDATION_FAILED",
            details: new Dictionary<string, string?>
            {
                ["issueCount"] = result.Issues.Count.ToString(),
                ["validatorVersion"] = result.ValidatorVersion
            });
        await _database.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<WorkflowVersion> PublishAsync(
        Guid workflowId,
        int version,
        string actor,
        CancellationToken cancellationToken)
    {
        var record = await FindVersionAsync(workflowId, version, cancellationToken);
        if (WorkflowPersistence.IsPublished(record))
        {
            return WorkflowPersistence.ToContract(record, version);
        }

        var validation = WorkflowPersistence.DeserializeValidation(record.ValidationJson);
        if (WorkflowPersistence.ParseStatus(record) != WorkflowVersionStatus.Validated ||
            validation is null ||
            !validation.IsValid)
        {
            throw new InvalidOperationException("Only a successfully validated workflow version can be published.");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var priorPublished = await _database.WorkflowVersions
            .Where(item => item.WorkflowId == workflowId &&
                           item.Version != version &&
                           item.PublishStatus == WorkflowPublishStatus.Published.ToString())
            .ToListAsync(cancellationToken);
        foreach (var prior in priorPublished)
        {
            prior.Status = WorkflowVersionStatus.Archived.ToString();
            prior.PublishStatus = WorkflowPublishStatus.Superseded.ToString();
            prior.UpdatedAtUtc = now;
            AddLifecycleAudit(prior, "WorkflowVersionSuperseded", "Superseded", actor, null, null);
        }

        record.Status = WorkflowVersionStatus.Published.ToString();
        record.PublishStatus = WorkflowPublishStatus.Published.ToString();
        record.PublishedBy = RequireActor(actor);
        record.PublishedAtUtc = now;
        record.UpdatedAtUtc = now;
        AddLifecycleAudit(record, "WorkflowVersionPublished", "Published", actor, null, null);
        await _database.SaveChangesAsync(cancellationToken);
        return WorkflowPersistence.ToContract(record, version);
    }

    public async Task<WorkflowExecutionResult> ExecuteAsync(
        WorkflowExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.RequestId != Guid.Empty)
        {
            var fingerprint = CreateFingerprint(request);
            var prior = await _database.WorkflowExecutions
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.RequestId == request.RequestId, cancellationToken);
            if (prior is not null)
            {
                if (StringComparer.Ordinal.Equals(prior.Fingerprint, fingerprint))
                {
                    return WorkflowPersistence.DeserializeResult(prior.ResultJson) with { IsIdempotentReplay = true };
                }

                var reused = CreateRejection(
                    request,
                    WorkflowExecutionRejectionCodes.RequestIdReused,
                    "The request id has already been used for a different workflow execution payload.");
                AddExecutionAudit(reused.Audit);
                await _database.SaveChangesAsync(cancellationToken);
                return reused;
            }

            var result = await _runtimeExecutor.ExecuteAsync(request, cancellationToken);
            _database.WorkflowExecutions.Add(await CreateExecutionRecordAsync(
                request,
                fingerprint,
                result,
                cancellationToken));
            AddExecutionAudit(result.Audit);
            try
            {
                await _database.SaveChangesAsync(cancellationToken);
                return result;
            }
            catch (DbUpdateException)
            {
                // A concurrent MES process may have persisted the same RequestId after
                // this request was read. Re-read the durable result instead of turning
                // an idempotent retry into a server error.
                _database.ChangeTracker.Clear();
                var concurrentlyPersisted = await _database.WorkflowExecutions
                    .AsNoTracking()
                    .SingleOrDefaultAsync(item => item.RequestId == request.RequestId, cancellationToken);
                if (concurrentlyPersisted is null)
                {
                    throw;
                }

                if (StringComparer.Ordinal.Equals(concurrentlyPersisted.Fingerprint, fingerprint))
                {
                    return WorkflowPersistence.DeserializeResult(concurrentlyPersisted.ResultJson) with
                    {
                        IsIdempotentReplay = true
                    };
                }

                var reused = CreateRejection(
                    request,
                    WorkflowExecutionRejectionCodes.RequestIdReused,
                    "The request id has already been used for a different workflow execution payload.");
                AddExecutionAudit(reused.Audit);
                await _database.SaveChangesAsync(cancellationToken);
                return reused;
            }
        }

        var invalidRequest = await _runtimeExecutor.ExecuteAsync(request, cancellationToken);
        AddExecutionAudit(invalidRequest.Audit);
        await _database.SaveChangesAsync(cancellationToken);
        return invalidRequest;
    }

    private async Task<WorkflowExecutionRecord> CreateExecutionRecordAsync(
        WorkflowExecutionRequest request,
        string fingerprint,
        WorkflowExecutionResult result,
        CancellationToken cancellationToken)
    {
        string? definitionSnapshotJson = null;
        if (result.IsAccepted)
        {
            definitionSnapshotJson = await _database.WorkflowVersions
                .AsNoTracking()
                .Where(item => item.WorkflowId == request.WorkflowId && item.Version == request.Version)
                .Select(item => item.DefinitionJson)
                .SingleOrDefaultAsync(cancellationToken);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return new WorkflowExecutionRecord
        {
            RequestId = request.RequestId,
            Fingerprint = fingerprint,
            WorkflowId = request.WorkflowId,
            Version = request.Version,
            ExecutionId = result.ExecutionId,
            Outcome = result.Status.ToString(),
            RejectionCode = result.RejectionCode,
            RequestJson = WorkflowPersistence.Serialize(request),
            ResultJson = WorkflowPersistence.Serialize(result),
            DefinitionSnapshotJson = definitionSnapshotJson,
            RuntimeStatus = WorkflowPersistence.GetAdmissionRuntimeStatus(result).ToString(),
            CurrentNodeId = result.NextStepRequest?.NodeId,
            PendingStepJson = result.NextStepRequest is null
                ? null
                : WorkflowPersistence.Serialize(result.NextStepRequest),
            Attempt = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    private async Task<WorkflowExecutionRecord> FindExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        if (executionId == Guid.Empty)
        {
            throw new ArgumentException("A workflow execution id is required.", nameof(executionId));
        }

        var record = await _database.WorkflowExecutions
            .SingleOrDefaultAsync(item => item.ExecutionId == executionId, cancellationToken);
        return record ?? throw new KeyNotFoundException($"Workflow execution '{executionId}' was not found.");
    }

    private void AddRuntimeAudit(
        WorkflowExecutionRecord record,
        string eventType,
        string outcome,
        string? reason,
        IReadOnlyDictionary<string, string?> details)
    {
        var request = WorkflowPersistence.DeserializeRequest(record.RequestJson);
        _database.WorkflowAudits.Add(new WorkflowAuditRecord
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            Outcome = outcome,
            Reason = reason,
            WorkflowId = record.WorkflowId,
            Version = record.Version,
            RequestId = record.RequestId,
            ExecutionId = record.ExecutionId == Guid.Empty ? null : record.ExecutionId,
            Actor = request.RequestedBy,
            CorrelationId = request.CorrelationId,
            DetailsJson = WorkflowPersistence.Serialize(details),
            OccurredAtUtc = _timeProvider.GetUtcNow().UtcDateTime
        });
    }

    private async Task<WorkflowVersionRecord> FindVersionAsync(
        Guid workflowId,
        int version,
        CancellationToken cancellationToken)
    {
        var record = await _database.WorkflowVersions
            .SingleOrDefaultAsync(
                item => item.WorkflowId == workflowId && item.Version == version,
                cancellationToken);
        return record ?? throw new KeyNotFoundException($"Workflow version '{workflowId}/v{version}' was not found.");
    }

    private async Task<int?> GetPublishedVersionAsync(Guid workflowId, CancellationToken cancellationToken) =>
        await WorkflowPersistence.GetPublishedVersionAsync(_database, workflowId, cancellationToken);

    private void AddLifecycleAudit(
        WorkflowVersionRecord record,
        string eventType,
        string outcome,
        string? actor,
        string? code,
        IReadOnlyDictionary<string, string?>? details)
    {
        _database.WorkflowAudits.Add(new WorkflowAuditRecord
        {
            EventType = eventType,
            Outcome = outcome,
            Code = code,
            WorkflowId = record.WorkflowId,
            Version = record.Version,
            Actor = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim(),
            DetailsJson = WorkflowPersistence.Serialize(details ?? new Dictionary<string, string?>()),
            OccurredAtUtc = _timeProvider.GetUtcNow().UtcDateTime
        });
    }

    private void AddExecutionAudit(WorkflowExecutionAuditEntry audit)
    {
        _database.WorkflowAudits.Add(new WorkflowAuditRecord
        {
            Id = audit.EventId == Guid.Empty ? Guid.NewGuid() : audit.EventId,
            EventType = audit.EventType,
            Outcome = audit.Outcome,
            Code = audit.Code,
            Reason = audit.Reason,
            WorkflowId = audit.WorkflowId,
            Version = audit.Version,
            RequestId = audit.RequestId == Guid.Empty ? null : audit.RequestId,
            ExecutionId = audit.ExecutionId == Guid.Empty ? null : audit.ExecutionId,
            Actor = audit.RequestedBy,
            CorrelationId = audit.CorrelationId,
            DetailsJson = WorkflowPersistence.Serialize(audit.Details),
            OccurredAtUtc = audit.OccurredAt.UtcDateTime
        });
    }

    private WorkflowExecutionResult CreateRejection(
        WorkflowExecutionRequest request,
        string code,
        string reason)
    {
        var occurredAt = _timeProvider.GetUtcNow();
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
            Audit = new WorkflowExecutionAuditEntry
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
                Details = new Dictionary<string, string?>
                {
                    ["dryRun"] = request.DryRun.ToString()
                }
            }
        };
    }

    private static WorkflowDefinition NormalizeDefinition(WorkflowDefinition definition, Guid workflowId) =>
        definition with { Id = workflowId, PublishedVersion = null };

    private static string RequireActor(string actor) =>
        string.IsNullOrWhiteSpace(actor)
            ? throw new ArgumentException("A non-empty workflow actor is required.", nameof(actor))
            : actor.Trim();

    private static string CreateFingerprint(WorkflowExecutionRequest request)
    {
        var parameters = request.Parameters ?? new Dictionary<string, string?>();
        var parameterPart = parameters
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
}

internal static class WorkflowPersistence
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, SerializerOptions);

    public static WorkflowDefinition DeserializeDefinition(string value) =>
        JsonSerializer.Deserialize<WorkflowDefinition>(value, SerializerOptions)
        ?? throw new InvalidOperationException("The persisted workflow definition is empty or invalid.");

    public static WorkflowValidationResult? DeserializeValidation(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<WorkflowValidationResult>(value, SerializerOptions)
              ?? throw new InvalidOperationException("The persisted workflow validation result is invalid.");

    public static WorkflowExecutionResult DeserializeResult(string value) =>
        JsonSerializer.Deserialize<WorkflowExecutionResult>(value, SerializerOptions)
        ?? throw new InvalidOperationException("The persisted workflow execution result is invalid.");

    public static WorkflowExecutionRequest DeserializeRequest(string value) =>
        JsonSerializer.Deserialize<WorkflowExecutionRequest>(value, SerializerOptions)
        ?? throw new InvalidOperationException("The persisted workflow execution request is invalid.");

    public static WorkflowRuntimeStatus GetAdmissionRuntimeStatus(WorkflowExecutionResult result) =>
        result.IsRejected
            ? WorkflowRuntimeStatus.Rejected
            : result.DryRun
                ? WorkflowRuntimeStatus.DryRunCompleted
                : result.NextStepRequest is null
                    ? WorkflowRuntimeStatus.Completed
                    : WorkflowRuntimeStatus.Prepared;

    private static WorkflowRuntimeStatus GetRuntimeStatus(
        WorkflowExecutionRecord record,
        WorkflowExecutionResult result) =>
        Enum.TryParse<WorkflowRuntimeStatus>(record.RuntimeStatus, ignoreCase: true, out var runtimeStatus)
            ? runtimeStatus
            : GetAdmissionRuntimeStatus(result);

    private static WorkflowNextStepRequest? DeserializePendingStep(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<WorkflowNextStepRequest>(value, SerializerOptions)
              ?? throw new InvalidOperationException("The persisted workflow pending step is invalid.");

    public static Guid CreateStableOperationId(Guid executionId, Guid nodeId, int attempt)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{executionId:N}|{nodeId:N}|{attempt}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    public static WorkflowNextStepRequest? ResolveFollowingStep(
        WorkflowExecutionRecord record,
        WorkflowNextStepRequest completedStep)
    {
        if (string.IsNullOrWhiteSpace(record.DefinitionSnapshotJson))
        {
            throw new InvalidOperationException("The workflow execution has no immutable definition snapshot to advance.");
        }

        var definition = DeserializeDefinition(record.DefinitionSnapshotJson);
        var request = DeserializeRequest(record.RequestJson);
        var nodes = (definition.Nodes ?? Array.Empty<WorkflowNode>())
            .OrderBy(node => node.Order)
            .ToArray();
        var nodesById = nodes.ToDictionary(node => node.Id);
        if (!nodesById.TryGetValue(completedStep.NodeId, out var current))
        {
            throw new InvalidOperationException("The completed workflow node is absent from the immutable definition snapshot.");
        }

        var hasExplicitEdges = nodes.Any(node => node.NextNodeIds is { Count: > 0 });
        var visited = new HashSet<Guid> { current.Id };
        while (true)
        {
            var nextNodeIds = (current.NextNodeIds ?? Array.Empty<Guid>()).ToArray();
            if (nextNodeIds.Length > 1)
            {
                throw new InvalidOperationException("Workflow runtime cannot advance an unselected branch.");
            }

            WorkflowNode? next = null;
            if (nextNodeIds.Length == 1)
            {
                nodesById.TryGetValue(nextNodeIds[0], out next);
                if (next is null)
                {
                    throw new InvalidOperationException("The workflow path points to a missing node.");
                }
            }
            else if (!hasExplicitEdges)
            {
                next = nodes.FirstOrDefault(node => node.Order > current.Order);
            }

            if (next is null || next.Type == WorkflowNodeType.End)
            {
                return null;
            }

            if (!visited.Add(next.Id))
            {
                throw new InvalidOperationException("The workflow path contains a cycle and cannot be advanced safely.");
            }

            current = next;
            if (current.Type == WorkflowNodeType.Start)
            {
                continue;
            }

            return new WorkflowNextStepRequest
            {
                StepRequestId = Guid.NewGuid(),
                ExecutionId = record.ExecutionId,
                WorkflowId = record.WorkflowId,
                Version = record.Version,
                NodeId = current.Id,
                NodeType = current.Type,
                NodeName = current.Name,
                TargetStation = current.TargetStation,
                DryRun = request.DryRun,
                Parameters = ResolveParameters(request, current)
            };
        }
    }

    private static IReadOnlyDictionary<string, string?> ResolveParameters(
        WorkflowExecutionRequest request,
        WorkflowNode node)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in node.Parameters ?? Array.Empty<WorkflowParameter>())
        {
            values[parameter.Name] = parameter.Value;
            if (request.Parameters is not null)
            {
                var supplied = request.Parameters.FirstOrDefault(pair =>
                    StringComparer.OrdinalIgnoreCase.Equals(pair.Key, parameter.Name));
                if (!string.IsNullOrEmpty(supplied.Key)) values[parameter.Name] = supplied.Value;
            }

            if (parameter.IsRequired && string.IsNullOrWhiteSpace(values[parameter.Name]))
            {
                throw new InvalidOperationException($"Required workflow parameter '{parameter.Name}' is missing.");
            }
        }

        return values;
    }

    public static WorkflowExecutionSnapshot ToExecutionSnapshot(WorkflowExecutionRecord record)
    {
        var result = DeserializeResult(record.ResultJson);
        var runtimeStatus = GetRuntimeStatus(record, result);
        var pendingStep = DeserializePendingStep(record.PendingStepJson) ?? result.NextStepRequest;
        var createdAt = new DateTimeOffset(DateTime.SpecifyKind(record.CreatedAtUtc, DateTimeKind.Utc));
        var updatedAtUtc = record.UpdatedAtUtc ?? record.CreatedAtUtc;
        var updatedAt = new DateTimeOffset(DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc));
        return new WorkflowExecutionSnapshot
        {
            RequestId = record.RequestId,
            ExecutionId = record.ExecutionId,
            WorkflowId = record.WorkflowId,
            Version = record.Version,
            RuntimeStatus = runtimeStatus,
            DryRun = result.DryRun,
            CurrentNodeId = record.CurrentNodeId ?? pendingStep?.NodeId,
            PendingStepRequest = runtimeStatus is WorkflowRuntimeStatus.Rejected or
                WorkflowRuntimeStatus.DryRunCompleted or
                WorkflowRuntimeStatus.Completed or
                WorkflowRuntimeStatus.Failed or
                WorkflowRuntimeStatus.Cancelled
                ? null
                : pendingStep,
            TransportOperationId = record.TransportOperationId,
            Attempt = record.Attempt,
            LastError = record.LastError,
            RejectionCode = result.RejectionCode,
            RejectionReason = result.RejectionReason,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt
        };
    }

    public static WorkflowVersion ToContract(WorkflowVersionRecord record, int? publishedVersion) => new()
    {
        WorkflowId = record.WorkflowId,
        Version = record.Version,
        Definition = ToDefinition(record, publishedVersion),
        Status = ParseStatus(record),
        PublishStatus = ParsePublishStatus(record),
        Validation = DeserializeValidation(record.ValidationJson),
        CreatedBy = record.CreatedBy,
        CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(record.CreatedAtUtc, DateTimeKind.Utc)),
        ChangeSummary = record.ChangeSummary,
        PublishedBy = record.PublishedBy,
        PublishedAt = record.PublishedAtUtc is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(record.PublishedAtUtc.Value, DateTimeKind.Utc))
    };

    public static WorkflowDefinition ToDefinition(WorkflowVersionRecord record, int? publishedVersion) =>
        DeserializeDefinition(record.DefinitionJson) with
        {
            Id = record.WorkflowId,
            PublishedVersion = publishedVersion
        };

    public static WorkflowAuditResponse ToAuditContract(WorkflowAuditRecord record) => new()
    {
        Id = record.Id,
        EventType = record.EventType,
        Outcome = record.Outcome,
        Code = record.Code,
        Reason = record.Reason,
        WorkflowId = record.WorkflowId,
        Version = record.Version,
        RequestId = record.RequestId,
        ExecutionId = record.ExecutionId,
        Actor = record.Actor,
        CorrelationId = record.CorrelationId,
        Details = DeserializeDetails(record.DetailsJson),
        OccurredAt = new DateTimeOffset(DateTime.SpecifyKind(record.OccurredAtUtc, DateTimeKind.Utc))
    };

    public static IReadOnlyDictionary<string, string?> DeserializeDetails(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(value, SerializerOptions)
                ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // Preserve the audit row even when an old or vendor-specific payload
            // is not a string dictionary; the raw value remains inspectable.
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["raw"] = value
            };
        }
    }

    public static WorkflowVersionStatus ParseStatus(WorkflowVersionRecord record) =>
        Enum.TryParse<WorkflowVersionStatus>(record.Status, ignoreCase: true, out var status)
            ? status
            : throw new InvalidOperationException($"Persisted workflow version status '{record.Status}' is invalid.");

    public static WorkflowPublishStatus ParsePublishStatus(WorkflowVersionRecord record) =>
        Enum.TryParse<WorkflowPublishStatus>(record.PublishStatus, ignoreCase: true, out var status)
            ? status
            : throw new InvalidOperationException($"Persisted workflow publish status '{record.PublishStatus}' is invalid.");

    public static bool IsPublished(WorkflowVersionRecord record) =>
        ParseStatus(record) == WorkflowVersionStatus.Published &&
        ParsePublishStatus(record) == WorkflowPublishStatus.Published;

    public static async Task<int?> GetPublishedVersionAsync(
        MesDbContext database,
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        return await database.WorkflowVersions
            .AsNoTracking()
            .Where(item => item.WorkflowId == workflowId &&
                           item.Status == WorkflowVersionStatus.Published.ToString() &&
                           item.PublishStatus == WorkflowPublishStatus.Published.ToString())
            .Select(item => (int?)item.Version)
            .OrderByDescending(item => item)
            .FirstOrDefaultAsync(cancellationToken);
    }
}

