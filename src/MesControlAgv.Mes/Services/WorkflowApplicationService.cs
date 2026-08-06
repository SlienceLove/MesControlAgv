using System.Text.Json;
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
            _database.WorkflowExecutions.Add(new WorkflowExecutionRecord
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
                CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime
            });
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

