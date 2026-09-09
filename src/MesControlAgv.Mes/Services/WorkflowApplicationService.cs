using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Startup-time snapshot of the independent switches required for one-click
/// physical workflow execution. It prevents MES from accepting a batch that
/// would remain Ready forever because one of its workers is disabled.
/// </summary>
public sealed record WorkflowPhysicalBatchAdmissionGate(bool Enabled, string Reason);

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
public sealed partial class WorkflowApplicationService : IWorkflowApplicationService
{
    private static readonly SemaphoreSlim PhysicalExecutionAdmissionGate = new(1, 1);
    private readonly MesDbContext _database;
    private readonly IWorkflowVersionReader _versionReader;
    private readonly WorkflowRuntimeExecutor _runtimeExecutor;
    private readonly WorkflowValidator _validator;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkflowRunControlAuthorizer _controlAuthorizer;
    private readonly ExperimentRuntimeLeaseLifecycle _experimentRuntimeLeaseLifecycle;
    private readonly WorkflowPhysicalBatchAdmissionGate? _physicalBatchAdmissionGate;
    private readonly IPhysicalReadinessState? _physicalReadiness;
    private readonly PhysicalExecutionAdmissionPolicy? _admissionPolicy;

    public WorkflowApplicationService(
        MesDbContext database,
        IWorkflowVersionReader versionReader,
        WorkflowRuntimeExecutor runtimeExecutor,
        WorkflowValidator validator,
        TimeProvider? timeProvider = null,
        IWorkflowRunControlAuthorizer? controlAuthorizer = null,
        ExperimentRuntimeLeaseLifecycle? experimentRuntimeLeaseLifecycle = null,
        WorkflowPhysicalBatchAdmissionGate? physicalBatchAdmissionGate = null,
        IPhysicalReadinessState? physicalReadiness = null,
        PhysicalExecutionAdmissionPolicy? admissionPolicy = null)
    {
        _database = database;
        _versionReader = versionReader;
        _runtimeExecutor = runtimeExecutor;
        _validator = validator;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _controlAuthorizer = controlAuthorizer ??
            new ConfiguredWorkflowRunControlAuthorizer(
                Microsoft.Extensions.Options.Options.Create(new WorkflowRunControlAuthorizationOptions()));
        _experimentRuntimeLeaseLifecycle = experimentRuntimeLeaseLifecycle ??
            new ExperimentRuntimeLeaseLifecycle(database, _timeProvider);
        _physicalBatchAdmissionGate = physicalBatchAdmissionGate;
        _physicalReadiness = physicalReadiness;
        _admissionPolicy = admissionPolicy ?? new PhysicalExecutionAdmissionPolicy(
            ProfileConfiguration.Default with
            {
                Features = ProfileConfiguration.Default.Features with
                {
                    UseSimulator = physicalReadiness is null
                }
            },
            physicalReadiness ?? new DisabledPhysicalReadinessState(),
            NullLogger<PhysicalExecutionAdmissionPolicy>.Instance);
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

    public async Task<WorkflowExecutionRequest?> GetExecutionRequestAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        if (executionId == Guid.Empty) return null;

        var requestJson = await _database.WorkflowExecutions
            .AsNoTracking()
            .Where(item => item.ExecutionId == executionId)
            .Select(item => item.RequestJson)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestJson)) return null;

        try
        {
            return WorkflowPersistence.DeserializeRequest(requestJson);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // A legacy/corrupt request must not become permission to create a
            // physical permit. The worker will leave the node for manual review.
            return null;
        }
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
            if (snapshot.PendingStepRequest is { } runningStep &&
                record.TransportOperationId is { } runningOperationId)
            {
                await EnsureClaimRuntimeRecordsAsync(
                    record,
                    runningStep,
                    runningOperationId,
                    Math.Max(1, record.Attempt),
                    record.UpdatedAtUtc ?? _timeProvider.GetUtcNow().UtcDateTime,
                    cancellationToken);
                await _database.SaveChangesAsync(cancellationToken);
            }

            return WorkflowPersistence.ToExecutionSnapshot(record);
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
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        record.UpdatedAtUtc = now;
        var runtimeRecords = await EnsureClaimRuntimeRecordsAsync(
            record,
            snapshot.PendingStepRequest,
            record.TransportOperationId.Value,
            attempt,
            now,
            cancellationToken);
        AddRuntimeAudit(record, "WorkflowStepClaimed", "Running", null, new Dictionary<string, string?>
        {
            ["nodeExecutionId"] = runtimeRecords.Node.Id.ToString(),
            ["nodeId"] = snapshot.PendingStepRequest.NodeId.ToString(),
            ["transportOperationId"] = record.TransportOperationId.Value.ToString(),
            ["deviceOperationId"] = runtimeRecords.Device?.OperationId.ToString(),
            ["attempt"] = attempt.ToString()
        });
        await _experimentRuntimeLeaseLifecycle.SynchronizeRunStateAsync(
            record,
            "workflow-runtime",
            "Workflow node execution started.",
            cancellationToken);
        await _database.SaveChangesAsync(cancellationToken);
        return WorkflowPersistence.ToExecutionSnapshot(record);
    }

    public Task<WorkflowExecutionSnapshot> CompleteClaimedStepAsync(
        Guid executionId,
        WorkflowStepCompletionRequest completion,
        CancellationToken cancellationToken) =>
        ExecuteAdvancedRuntimeSerializedAsync(
            () => CompleteClaimedStepCoreAsync(executionId, completion, cancellationToken),
            cancellationToken);

    private async Task<WorkflowExecutionSnapshot> CompleteClaimedStepCoreAsync(
        Guid executionId,
        WorkflowStepCompletionRequest completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var record = await FindExecutionAsync(executionId, cancellationToken);
        var snapshot = WorkflowPersistence.ToExecutionSnapshot(record);
        var preservePause = snapshot.RuntimeStatus == WorkflowRuntimeStatus.Paused;
        if (snapshot.RuntimeStatus is not (WorkflowRuntimeStatus.Running or WorkflowRuntimeStatus.Paused) ||
            record.TransportOperationId is null ||
            completion.TransportOperationId == Guid.Empty ||
            record.TransportOperationId != completion.TransportOperationId)
        {
            throw new InvalidOperationException("The completion does not match a claimed workflow step.");
        }

        var completedStep = snapshot.PendingStepRequest!;
        var completedAttempt = Math.Max(1, record.Attempt);
        var error = string.IsNullOrWhiteSpace(completion.Error) ? null : completion.Error.Trim();
        WorkflowNextStepRequest? nextStep = null;
        if (completion.Outcome == WorkflowStepCompletionOutcome.Succeeded)
        {
            nextStep = WorkflowPersistence.ResolveFollowingStep(record, completedStep);
            record.CurrentNodeId = nextStep?.NodeId ?? completedStep.NodeId;
            record.PendingStepJson = nextStep is null ? null : WorkflowPersistence.Serialize(nextStep);
            record.TransportOperationId = null;
            record.Attempt = 0;
            record.LastError = null;
            record.RuntimeStatus = (nextStep is null
                ? WorkflowRuntimeStatus.Completed
                : preservePause
                    ? WorkflowRuntimeStatus.Paused
                    : WorkflowRuntimeStatus.Prepared).ToString();
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
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        record.UpdatedAtUtc = now;
        var runtimeRecords = await ApplyCompletionRuntimeRecordsAsync(
            record,
            completedStep,
            completedAttempt,
            completion with { Error = error },
            nextStep,
            now,
            cancellationToken);
        var auditDetails = new Dictionary<string, string?>
        {
            ["nodeExecutionId"] = runtimeRecords.NodeExecutionId.ToString(),
            ["nodeId"] = completedStep.NodeId.ToString(),
            ["transportOperationId"] = completion.TransportOperationId.ToString(),
            ["deviceOperationId"] = runtimeRecords.DeviceOperationId?.ToString(),
            ["nextNodeId"] = nextStep?.NodeId.ToString()
        };
        if (completion.Outcome == WorkflowStepCompletionOutcome.Succeeded)
        {
            AddRuntimeAudit(record, "WorkflowStepCompleted", record.RuntimeStatus, null, auditDetails);
        }
        else
        {
            AddRuntimeAudit(record, "WorkflowStepReconciled", record.RuntimeStatus, record.LastError, auditDetails);
        }

        await _experimentRuntimeLeaseLifecycle.SynchronizeRunStateAsync(
            record,
            "workflow-runtime",
            record.LastError ?? $"Workflow step completed with outcome '{completion.Outcome}'.",
            cancellationToken);
        await _database.SaveChangesAsync(cancellationToken);
        if (completion.Outcome == WorkflowStepCompletionOutcome.Succeeded)
        {
            await ProcessAdvancedRunCoreAsync(record.ExecutionId, cancellationToken);
        }
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
        return Task.FromResult(_validator.ValidateForPublication(definition));
    }

    public async Task<WorkflowValidationResult> ValidateVersionAsync(
        Guid workflowId,
        int version,
        CancellationToken cancellationToken)
    {
        var record = await FindVersionAsync(workflowId, version, cancellationToken);
        var definition = WorkflowPersistence.DeserializeDefinition(record.DefinitionJson);
        var result = _validator.ValidateForPublication(definition);
        record.ValidationJson = WorkflowPersistence.Serialize(result);
        record.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        if (WorkflowPersistence.ParseStatus(record) is
            WorkflowVersionStatus.Draft or WorkflowVersionStatus.Validated)
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

        var definition = WorkflowPersistence.DeserializeDefinition(record.DefinitionJson);
        var currentValidation = _validator.ValidateForPublication(definition);
        record.ValidationJson = WorkflowPersistence.Serialize(currentValidation);
        if (!currentValidation.IsValid)
        {
            record.Status = WorkflowVersionStatus.Draft.ToString();
            record.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            AddLifecycleAudit(
                record,
                "WorkflowPublicationBlocked",
                "Invalid",
                actor,
                "WORKFLOW_VALIDATION_FAILED",
                ValidationAuditDetails(currentValidation));
            await _database.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(
                "The workflow no longer passes the current publication validation gate.");
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
        AddLifecycleAudit(
            record,
            "WorkflowVersionPublished",
            "Published",
            actor,
            null,
            ValidationAuditDetails(currentValidation));
        await _database.SaveChangesAsync(cancellationToken);
        return WorkflowPersistence.ToContract(record, version);
    }

    public async Task<WorkflowExecutionResult> ExecuteAsync(
        WorkflowExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (request.DryRun || request.PhysicalAuthorization is null)
                return await ExecuteCoreAsync(request, cancellationToken);

            await PhysicalExecutionAdmissionGate.WaitAsync(cancellationToken);
            try
            {
                return await ExecuteCoreAsync(request, cancellationToken);
            }
            finally
            {
                PhysicalExecutionAdmissionGate.Release();
            }
        }
        catch (PhysicalExecutionAdmissionException exception)
        {
            return await PersistPhysicalAdmissionRejectionAsync(
                request,
                exception,
                cancellationToken);
        }
    }

    private async Task<WorkflowExecutionResult> PersistPhysicalAdmissionRejectionAsync(
        WorkflowExecutionRequest request,
        PhysicalExecutionAdmissionException exception,
        CancellationToken cancellationToken)
    {
        var result = CreateRejection(
            request,
            WorkflowExecutionRejectionCodes.PhysicalExecutionDisabled,
            $"{exception.Code}: {exception.Detail}");

        if (request.RequestId == Guid.Empty)
        {
            AddExecutionAudit(result.Audit);
            await _database.SaveChangesAsync(cancellationToken);
            return result;
        }

        var fingerprint = CreateFingerprint(request);
        var prior = await _database.WorkflowExecutions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.RequestId == request.RequestId, cancellationToken);
        if (prior is not null)
        {
            if (StringComparer.Ordinal.Equals(prior.Fingerprint, fingerprint) ||
                IsLegacyPhysicalFingerprintMatch(prior, request))
            {
                return WorkflowPersistence.DeserializeResult(prior.ResultJson) with
                {
                    IsIdempotentReplay = true
                };
            }

            return await PersistRequestIdReusedAuditAsync(request, cancellationToken);
        }

        var executionRecord = await CreateExecutionRecordAsync(
            request,
            fingerprint,
            result,
            cancellationToken);
        _database.WorkflowExecutions.Add(executionRecord);
        AddExecutionAudit(result.Audit);
        try
        {
            await _database.SaveChangesAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateException dbException) when (IsRequestIdUniqueConstraintViolation(dbException))
        {
            // Another MES request may have won the RequestId race. Re-read its
            // durable result and apply the same idempotency contract as the
            // normal execution path.
            _database.ChangeTracker.Clear();
            var concurrentlyPersisted = await _database.WorkflowExecutions
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.RequestId == request.RequestId, cancellationToken);
            if (concurrentlyPersisted is null)
            {
                throw;
            }

            if (StringComparer.Ordinal.Equals(concurrentlyPersisted.Fingerprint, fingerprint) ||
                IsLegacyPhysicalFingerprintMatch(concurrentlyPersisted, request))
            {
                return WorkflowPersistence.DeserializeResult(concurrentlyPersisted.ResultJson) with
                {
                    IsIdempotentReplay = true
                };
            }

            return await PersistRequestIdReusedAuditAsync(request, cancellationToken);
        }
    }

    private async Task<WorkflowExecutionResult> PersistRequestIdReusedAuditAsync(
        WorkflowExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var reused = CreateRejection(
            request,
            WorkflowExecutionRejectionCodes.RequestIdReused,
            "The request id has already been used for a different workflow execution payload.");
        AddExecutionAudit(reused.Audit);
        await _database.SaveChangesAsync(cancellationToken);
        return reused;
    }

    private static bool IsRequestIdUniqueConstraintViolation(DbUpdateException exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqliteException &&
                sqliteException.SqliteErrorCode == 19 &&
                sqliteException.Message.Contains(
                    "WorkflowExecutions.RequestId",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<WorkflowExecutionResult> ExecuteCoreAsync(
        WorkflowExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var isPhysical = !request.DryRun && request.PhysicalAuthorization is not null;
        if (request.RequestId != Guid.Empty)
        {
            var submittedFingerprint = CreateFingerprint(request);
            var prior = await _database.WorkflowExecutions
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.RequestId == request.RequestId, cancellationToken);
            if (prior is not null &&
                (StringComparer.Ordinal.Equals(prior.Fingerprint, submittedFingerprint) ||
                 IsLegacyPhysicalFingerprintMatch(prior, request)))
            {
                return WorkflowPersistence.DeserializeResult(prior.ResultJson) with { IsIdempotentReplay = true };
            }
        }
        if (isPhysical)
        {
            if (_admissionPolicy is null)
            {
                throw new PhysicalExecutionAdmissionException(
                    PhysicalReadinessReasonCodes.SupervisorDisabled,
                    "Physical execution admission policy is unavailable; physical execution is disabled.");
            }
            _admissionPolicy.RequireSupervisedExecution("workflow.execute");
        }

        var readinessBinding = await BindPhysicalReadinessAsync(request, cancellationToken);
        request = readinessBinding.Request;

        if (request.RequestId != Guid.Empty)
        {
            var fingerprint = CreateFingerprint(request);
            var prior = await _database.WorkflowExecutions
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.RequestId == request.RequestId, cancellationToken);
            if (prior is not null)
            {
                if (StringComparer.Ordinal.Equals(prior.Fingerprint, fingerprint) ||
                    IsLegacyPhysicalFingerprintMatch(prior, request))
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

            WorkflowExecutionResult result;
            if (readinessBinding.RejectionReason is not null)
            {
                result = CreateRejection(
                    request,
                    readinessBinding.RejectionCode ??
                        WorkflowExecutionRejectionCodes.PhysicalDeviceNotReady,
                    readinessBinding.RejectionReason);
            }
            else
            {
                var activePhysicalRun = request.PhysicalAuthorization is null
                    ? null
                    : await FindActivePhysicalRunAsync(
                        request.PhysicalAuthorization.AgvId,
                        cancellationToken);
                if (request.PhysicalAuthorization is not null &&
                    _physicalBatchAdmissionGate is { Enabled: false } batchGate)
                {
                    result = CreateRejection(
                        request,
                        WorkflowExecutionRejectionCodes.PhysicalExecutionDisabled,
                        batchGate.Reason);
                }
                else if (activePhysicalRun is not null)
                {
                    result = CreateRejection(
                        request,
                        WorkflowExecutionRejectionCodes.PhysicalAgvBusy,
                        $"AGV '{request.PhysicalAuthorization!.AgvId}' already has active physical workflow run '{activePhysicalRun.ExecutionId}'. Resolve or complete it before starting another batch.");
                }
                else
                {
                    result = await _runtimeExecutor.ExecuteAsync(request, cancellationToken);
                    if (result.IsAccepted &&
                        request.PhysicalAuthorization is not null &&
                        _physicalBatchAdmissionGate is { Enabled: true } &&
                        !await IsApprovedStandardMaterialWorkflowAsync(request, cancellationToken))
                    {
                        result = CreateRejection(
                            request,
                            WorkflowExecutionRejectionCodes.PhysicalTemplateRequired,
                            "One-click physical execution only accepts the approved LM1→LM7→LM2→LM7→LM1 material workflow with 取料盘/放料盘/回收料盘 programs.");
                    }
                }

                if (result.IsAccepted && readinessBinding.DeviceEpochs is { Count: > 0 } epochs &&
                    !TryAcknowledgePhysicalAuthorization(epochs, out var acknowledgementError))
                {
                    result = CreateRejection(
                        request,
                        WorkflowExecutionRejectionCodes.PhysicalDeviceNotReady,
                        acknowledgementError ??
                        "Physical readiness changed while the workflow was being admitted; submit a new authorization.");
                }
            }
            var executionRecord = await CreateExecutionRecordAsync(
                request,
                fingerprint,
                result,
                cancellationToken);
            _database.WorkflowExecutions.Add(executionRecord);
            if (result is { IsAccepted: true, DryRun: false, NextStepRequest: { } initialStep })
            {
                AddInitialNodeExecutionRecord(
                    executionRecord,
                    initialStep,
                    executionRecord.UpdatedAtUtc ?? executionRecord.CreatedAtUtc);
            }
            AddExecutionAudit(result.Audit);
            try
            {
                await _database.SaveChangesAsync(cancellationToken);
                if (result is { IsAccepted: true, DryRun: false })
                {
                    await ProcessAdvancedRuntimeAsync(result.ExecutionId, cancellationToken);
                }
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

                if (StringComparer.Ordinal.Equals(concurrentlyPersisted.Fingerprint, fingerprint) ||
                    IsLegacyPhysicalFingerprintMatch(concurrentlyPersisted, request))
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

    /// <summary>
    /// Captures the currently observed physical session epoch into a new run
    /// authorization.  This is deliberately done at MES admission, so the WPF
    /// client does not have to race a reconnect between displaying readiness and
    /// submitting the request.  A supplied stale epoch is rejected.
    /// </summary>
    private async Task<PhysicalReadinessBinding> BindPhysicalReadinessAsync(
        WorkflowExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.DryRun || request.PhysicalAuthorization is null ||
            _physicalReadiness is not { Enabled: true } readiness)
        {
            return new PhysicalReadinessBinding(request, null, null, null);
        }

        var authorization = request.PhysicalAuthorization;
        var snapshot = readiness.GetSnapshot();
        var agv = snapshot.Devices.FirstOrDefault(device =>
            string.Equals(device.DeviceFamily, "agv", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                device.DeviceId,
                authorization.AgvId?.Trim(),
                StringComparison.OrdinalIgnoreCase));
        if (agv is null || agv.State != PhysicalDeviceReadinessState.Ready)
        {
            return new PhysicalReadinessBinding(
                request,
                WorkflowExecutionRejectionCodes.PhysicalDeviceNotReady,
                $"Physical AGV '{authorization.AgvId}' is not Ready in the current supervisor snapshot.",
                null);
        }
        if (!string.IsNullOrWhiteSpace(authorization.ReadinessSupervisorInstanceId) &&
            !string.Equals(
                authorization.ReadinessSupervisorInstanceId.Trim(),
                snapshot.SupervisorInstanceId,
                StringComparison.Ordinal))
        {
            return new PhysicalReadinessBinding(
                request,
                WorkflowExecutionRejectionCodes.PhysicalDeviceEpochMismatch,
                "Physical authorization belongs to a previous readiness supervisor instance.",
                null);
        }

        var epochs = new Dictionary<string, long>(
            authorization.DeviceEpochs ?? new Dictionary<string, long>(),
            StringComparer.OrdinalIgnoreCase);
        var agvEpoch = authorization.GetDeviceEpoch(agv.DeviceId);
        if (agvEpoch.HasValue && agvEpoch.Value != agv.DeviceEpoch)
        {
            return new PhysicalReadinessBinding(
                request,
                WorkflowExecutionRejectionCodes.PhysicalDeviceEpochMismatch,
                $"Physical AGV '{agv.DeviceId}' authorization epoch {agvEpoch.Value} does not match current epoch {agv.DeviceEpoch}.",
                null);
        }
        epochs[agv.DeviceId] = agv.DeviceEpoch;

        // Bind any explicitly supplied device epochs, and reject stale or
        // unknown identities rather than silently dropping an operator's data.
        foreach (var supplied in authorization.DeviceEpochs ??
                 new Dictionary<string, long>())
        {
            if (!readiness.TryGetDevice(supplied.Key, out var device))
            {
                return new PhysicalReadinessBinding(
                    request,
                    WorkflowExecutionRejectionCodes.PhysicalDeviceEpochMismatch,
                    $"Physical authorization names unknown device '{supplied.Key}'.",
                    null);
            }
            if (supplied.Value != device.DeviceEpoch)
            {
                return new PhysicalReadinessBinding(
                    request,
                    WorkflowExecutionRejectionCodes.PhysicalDeviceEpochMismatch,
                    $"Physical device '{supplied.Key}' authorization epoch {supplied.Value} does not match current epoch {device.DeviceEpoch}.",
                    null);
            }
            if (device.State != PhysicalDeviceReadinessState.Ready)
            {
                return new PhysicalReadinessBinding(
                    request,
                    WorkflowExecutionRejectionCodes.PhysicalDeviceNotReady,
                    $"Physical device '{supplied.Key}' is not Ready in the current supervisor snapshot.",
                    null);
            }
            epochs[device.DeviceId] = device.DeviceEpoch;
        }

        // Resolve device ids referenced by the pinned workflow.  Robot-arm and
        // other future device nodes are bound when they are already observed;
        // a missing/not-ready referenced device rejects admission so a run
        // cannot become executable merely because it was accepted earlier.
        if (request.WorkflowId != Guid.Empty && request.Version > 0)
        {
            var version = await _versionReader.GetVersionAsync(
                request.WorkflowId,
                request.Version,
                cancellationToken);
            var referencedDeviceIds = (version?.Definition.Nodes ?? [])
                .Select(node => node.Configuration.TryGetValue(
                    WorkflowNodeConfigurationKeys.DeviceId,
                    out var value) ? value : null)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var deviceId in referencedDeviceIds)
            {
                if (string.Equals(deviceId, agv.DeviceId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!readiness.TryGetDevice(deviceId, out var device))
                {
                    return new PhysicalReadinessBinding(
                        request,
                        WorkflowExecutionRejectionCodes.PhysicalDeviceNotReady,
                        $"Workflow references physical device '{deviceId}', which is not registered with the readiness supervisor.",
                        null);
                }
                if (device.State != PhysicalDeviceReadinessState.Ready)
                {
                    return new PhysicalReadinessBinding(
                        request,
                        WorkflowExecutionRejectionCodes.PhysicalDeviceNotReady,
                        $"Workflow device '{deviceId}' is not Ready in the current supervisor snapshot.",
                        null);
                }
                if (epochs.TryGetValue(device.DeviceId, out var suppliedEpoch) &&
                    suppliedEpoch != device.DeviceEpoch)
                {
                    return new PhysicalReadinessBinding(
                        request,
                        WorkflowExecutionRejectionCodes.PhysicalDeviceEpochMismatch,
                        $"Workflow device '{deviceId}' authorization epoch does not match the current epoch.",
                        null);
                }
                epochs[device.DeviceId] = device.DeviceEpoch;
            }
        }

        var bound = request with
        {
            PhysicalAuthorization = authorization with
            {
                DeviceEpochs = epochs,
                ReadinessSupervisorInstanceId = snapshot.SupervisorInstanceId
            }
        };
        return new PhysicalReadinessBinding(bound, null, null, epochs);
    }

    private bool TryAcknowledgePhysicalAuthorization(
        IReadOnlyDictionary<string, long> epochs,
        out string? error)
    {
        if (_physicalReadiness is not { Enabled: true } readiness)
        {
            error = null;
            return true;
        }

        foreach (var pair in epochs)
        {
            if (readiness.AcknowledgeAuthorization(pair.Key, pair.Value)) continue;
            error = $"Physical device '{pair.Key}' changed readiness while the run was being admitted; submit a new authorization.";
            return false;
        }

        error = null;
        return true;
    }

    private sealed record PhysicalReadinessBinding(
        WorkflowExecutionRequest Request,
        string? RejectionCode,
        string? RejectionReason,
        IReadOnlyDictionary<string, long>? DeviceEpochs);

    private async Task<WorkflowExecutionRecord?> FindActivePhysicalRunAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agvId)) return null;

        var candidates = await _database.WorkflowExecutions
            .AsNoTracking()
            .Where(item => item.RuntimeStatus == WorkflowRuntimeStatus.Prepared.ToString() ||
                           item.RuntimeStatus == WorkflowRuntimeStatus.Running.ToString() ||
                           item.RuntimeStatus == WorkflowRuntimeStatus.Paused.ToString() ||
                           item.RuntimeStatus == WorkflowRuntimeStatus.Unknown.ToString())
            .OrderBy(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            try
            {
                var activeRequest = WorkflowPersistence.DeserializeRequest(candidate.RequestJson);
                if (!activeRequest.DryRun &&
                    activeRequest.PhysicalAuthorization is { } authorization &&
                    string.Equals(
                        authorization.AgvId.Trim(),
                        agvId.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException)
            {
                // A malformed legacy request cannot claim a physical AGV. Its
                // own recovery path remains independent from batch admission.
            }
        }

        return null;
    }

    private async Task<bool> IsApprovedStandardMaterialWorkflowAsync(
        WorkflowExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var version = await _versionReader.GetVersionAsync(
            request.WorkflowId,
            request.Version,
            cancellationToken);
        if (version is null) return false;

        var nodes = (version.Definition.Nodes ?? [])
            .OrderBy(node => node.Order)
            .ToArray();
        var expectedTypes = new[]
        {
            WorkflowNodeType.Start,
            WorkflowNodeType.Move,
            WorkflowNodeType.RobotProgram,
            WorkflowNodeType.Move,
            WorkflowNodeType.RobotProgram,
            WorkflowNodeType.Move,
            WorkflowNodeType.RobotProgram,
            WorkflowNodeType.Move,
            WorkflowNodeType.End
        };
        if (nodes.Length != expectedTypes.Length ||
            nodes.Where((node, index) => node.Type != expectedTypes[index]).Any())
            return false;

        for (var index = 0; index < nodes.Length; index++)
        {
            var expectedNext = index + 1 < nodes.Length ? nodes[index + 1].Id : (Guid?)null;
            var actualNext = nodes[index].NextNodeIds?.ToArray() ?? [];
            if (expectedNext is null ? actualNext.Length != 0 :
                actualNext.Length != 1 || actualNext[0] != expectedNext.Value)
                return false;
        }

        var expectedStations = new[] { "LM7", "LM2", "LM7", "LM1" };
        var moveNodes = nodes.Where(node => node.Type == WorkflowNodeType.Move).ToArray();
        if (moveNodes.Where((node, index) => !string.Equals(
                ReadNodeValue(node, WorkflowNodeConfigurationKeys.TargetStation) ?? node.TargetStation,
                expectedStations[index],
                StringComparison.OrdinalIgnoreCase)).Any())
            return false;

        var expectedPrograms = new[] { "取料盘.pro", "放料盘.pro", "回收料盘.pro" };
        var programNodes = nodes.Where(node => node.Type == WorkflowNodeType.RobotProgram).ToArray();
        return programNodes.Select((node, index) => new
        {
            DeviceId = ReadNodeValue(node, WorkflowNodeConfigurationKeys.DeviceId),
            Program = ReadNodeValue(node, WorkflowNodeConfigurationKeys.ProgramName),
            Expected = expectedPrograms[index]
        })
            .All(item =>
                string.Equals(item.DeviceId, "ARM-01", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Program, item.Expected, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadNodeValue(WorkflowNode node, string key) =>
        node.Configuration.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

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

    private static IReadOnlyDictionary<string, string?> ValidationAuditDetails(
        WorkflowValidationResult validation) => new Dictionary<string, string?>
        {
            ["issueCount"] = validation.Issues.Count.ToString(),
            ["warningCount"] = validation.Issues.Count(issue =>
                issue.Severity == WorkflowValidationSeverity.Warning).ToString(),
            ["validatorVersion"] = validation.ValidatorVersion,
            ["catalogVersion"] = validation.CatalogVersion,
            ["profileProductId"] = validation.ProfileProductId,
            ["profileVersion"] = validation.ProfileVersion
        };

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
        var authorization = request.DryRun ? null : request.PhysicalAuthorization;
        var authorizationPart = authorization is null
            ? string.Empty
            : string.Join(
                '\u001e',
                FingerprintValue(authorization.AgvId),
                FingerprintValue(authorization.OperatorName),
                FingerprintValue(authorization.SafetyObserverName),
                FingerprintValue(authorization.PermitPrefix),
                authorization.ExpiresAtUtc.ToUniversalTime().Ticks,
                FingerprintValue(authorization.ReadinessSupervisorInstanceId),
                FingerprintEpochs(authorization.DeviceEpochs));
        var baseFingerprint = string.Join(
            '\u001f',
            request.WorkflowId,
            request.Version,
            request.RequestedBy,
            request.CorrelationId,
            request.DryRun,
            string.Join('\u001e', parameterPart));
        return authorization is null
            ? baseFingerprint
            : $"{baseFingerprint}\u001f{authorizationPart}";
    }

    private static string FingerprintValue(string? value) =>
        $"{value?.Length ?? -1}:{value}";

    private static string FingerprintEpochs(IReadOnlyDictionary<string, long>? epochs)
    {
        if (epochs is null || epochs.Count == 0) return string.Empty;
        return string.Join(
            '\u001e',
            epochs
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{FingerprintValue(pair.Key)}={pair.Value}"));
    }

    private static bool IsLegacyPhysicalFingerprintMatch(
        WorkflowExecutionRecord persisted,
        WorkflowExecutionRequest request)
    {
        if (request.DryRun || request.PhysicalAuthorization is null ||
            !StringComparer.Ordinal.Equals(
                persisted.Fingerprint,
                CreateFingerprint(request with { PhysicalAuthorization = null })))
            return false;

        try
        {
            var persistedRequest = WorkflowPersistence.DeserializeRequest(persisted.RequestJson);
            return ArePhysicalAuthorizationsEqual(
                persistedRequest.PhysicalAuthorization,
                request.PhysicalAuthorization);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            return false;
        }
    }

    private static bool ArePhysicalAuthorizationsEqual(
        WorkflowPhysicalRunAuthorization? left,
        WorkflowPhysicalRunAuthorization? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (!StringComparer.Ordinal.Equals(left.AgvId, right.AgvId) ||
            !StringComparer.Ordinal.Equals(left.OperatorName, right.OperatorName) ||
            !StringComparer.Ordinal.Equals(left.SafetyObserverName, right.SafetyObserverName) ||
            !StringComparer.Ordinal.Equals(left.PermitPrefix, right.PermitPrefix) ||
            !StringComparer.Ordinal.Equals(
                left.ReadinessSupervisorInstanceId,
                right.ReadinessSupervisorInstanceId) ||
            left.ExpiresAtUtc.ToUniversalTime() != right.ExpiresAtUtc.ToUniversalTime())
            return false;

        var leftEpochs = new Dictionary<string, long>(
            left.DeviceEpochs ?? new Dictionary<string, long>(),
            StringComparer.OrdinalIgnoreCase);
        var rightEpochs = new Dictionary<string, long>(
            right.DeviceEpochs ?? new Dictionary<string, long>(),
            StringComparer.OrdinalIgnoreCase);
        return leftEpochs.Count == rightEpochs.Count &&
               leftEpochs.All(pair =>
                   rightEpochs.TryGetValue(pair.Key, out var value) && value == pair.Value);
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

    public static WorkflowNextStepRequest? DeserializePendingStep(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<WorkflowNextStepRequest>(value, SerializerOptions)
              ?? throw new InvalidOperationException("The persisted workflow pending step is invalid.");

    public static Guid CreateStableOperationId(Guid executionId, Guid nodeId, int attempt)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{executionId:N}|{nodeId:N}|{attempt}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    public static Guid CreateStableRecordId(string category, Guid executionId, Guid nodeId, int attempt)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{category}|{executionId:N}|{nodeId:N}|{attempt}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    public static WorkflowNextStepRequest? ResolveFollowingStep(
        WorkflowExecutionRecord record,
        WorkflowNextStepRequest completedStep,
        WorkflowEdgeKind outcomeKind = WorkflowEdgeKind.Success,
        Guid? selectedEdgeId = null)
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

        var edges = (definition.Edges ?? Array.Empty<WorkflowEdgeDefinition>()).ToArray();
        var hasExplicitEdges = edges.Length > 0;
        var visited = new HashSet<Guid> { current.Id };
        while (true)
        {
            WorkflowNode? next = null;
            if (hasExplicitEdges)
            {
                var outgoing = edges.Where(edge => edge.SourceNodeId == current.Id).ToArray();
                var candidates = selectedEdgeId is { } edgeId
                    ? outgoing.Where(edge => edge.Id == edgeId).ToArray()
                    : outgoing.Where(edge => edge.Kind == outcomeKind).ToArray();
                if (candidates.Length > 1)
                {
                    throw new InvalidOperationException(
                        $"Workflow outcome '{outcomeKind}' resolves to more than one edge.");
                }

                if (candidates.Length == 1)
                {
                    nodesById.TryGetValue(candidates[0].TargetNodeId, out next);
                }
                else if (outgoing.Length > 0)
                {
                    throw new InvalidOperationException(
                        $"Workflow outcome '{outcomeKind}' has no explicit path from node '{current.Id}'.");
                }
            }
            else
            {
                var nextNodeIds = (current.NextNodeIds ?? Array.Empty<Guid>()).ToArray();
                if (nextNodeIds.Length > 1)
                {
                    throw new InvalidOperationException("Workflow runtime cannot advance an unselected branch.");
                }

                if (nextNodeIds.Length == 1)
                {
                    nodesById.TryGetValue(nextNodeIds[0], out next);
                }
                else
                {
                    next = nodes.FirstOrDefault(node => node.Order > current.Order);
                }
            }

            if (next is null && (hasExplicitEdges ||
                                 (current.NextNodeIds ?? Array.Empty<Guid>()).Count > 0))
            {
                if (selectedEdgeId is not null)
                {
                    throw new InvalidOperationException(
                        $"Selected workflow edge '{selectedEdgeId}' is unavailable.");
                }

                if (!hasExplicitEdges)
                {
                    throw new InvalidOperationException("The workflow path points to a missing node.");
                }
            }

            if (next is not null && !nodesById.ContainsKey(next.Id))
            {
                throw new InvalidOperationException("The workflow path points to a missing node.");
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
            selectedEdgeId = null;
            outcomeKind = WorkflowEdgeKind.Success;
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
                NodeTypeId = current.NodeTypeId,
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
        var values = WorkflowRuntimeInputProjection.ProjectParameters(request, node);
        foreach (var parameter in node.Parameters ?? Array.Empty<WorkflowParameter>())
        {
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
        var request = DeserializeRequest(record.RequestJson);
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
            PhysicalAuthorization = request.DryRun ? null : request.PhysicalAuthorization,
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

