using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Executes explicitly requested physical cleanup actions exactly once. The
/// durable row is the write reservation; a process restart never replays a
/// Prepared row and instead reconciles it to Unknown.
/// </summary>
public sealed class PhysicalSafetyActionService(
    MesDbContext database,
    IAgvGateway? agv,
    IAuboArmProgramGateway? aubo,
    ProfileConfiguration profile,
    IPhysicalReadinessState? physicalReadiness = null,
    TimeProvider? timeProvider = null,
    IWorkflowApplicationService? workflows = null,
    ILogger<PhysicalSafetyActionService>? logger = null)
{
    private static readonly SemaphoreSlim ActionGate = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IPhysicalReadinessState? _physicalReadiness = physicalReadiness;
    private readonly IWorkflowApplicationService? _workflows = workflows;
    private readonly ILogger _logger = logger ?? NullLogger<PhysicalSafetyActionService>.Instance;

    public async Task<PhysicalSafetyActionResponse> ReleaseAgvAsync(
        string agvId,
        PhysicalAgvReleaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deviceId = RequireValue(agvId, nameof(agvId));
        ValidateRequest(request.RequestId, request.OperatorName, request.Reason);
        var fingerprint = Fingerprint(
            PhysicalSafetyActionTypes.AgvRelease,
            deviceId,
            request.OperatorName,
            request.Reason,
            request.WorkflowRunId,
            request.WorkflowNodeExecutionId,
            request.WorkflowDeviceOperationId,
            null,
            null,
            request.DeviceEpoch,
            request.SupervisorInstanceId);

        await ActionGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await FindExistingAsync(request.RequestId, fingerprint, cancellationToken);
            if (existing is not null) return ToResponse(existing);

            var readinessRejection = CheckReadinessBinding(deviceId, request);
            if (readinessRejection is not null)
            {
                return await SaveRejectedAsync(
                    request.RequestId,
                    fingerprint,
                    PhysicalSafetyActionTypes.AgvRelease,
                    deviceId,
                    request.OperatorName,
                    request.Reason,
                    null,
                    readinessRejection,
                    cancellationToken,
                    request.WorkflowRunId,
                    request.WorkflowNodeExecutionId,
                    request.WorkflowDeviceOperationId,
                    request.DeviceEpoch,
                    request.SupervisorInstanceId);
            }

            var rejection = await CheckAgvPreflightAsync(deviceId, cancellationToken);
            if (rejection is not null)
            {
                var rejected = NewRecord(
                    request.RequestId,
                    fingerprint,
                    PhysicalSafetyActionTypes.AgvRelease,
                    deviceId,
                    request.OperatorName,
                    request.Reason,
                    request.WorkflowRunId,
                    request.WorkflowNodeExecutionId,
                    request.WorkflowDeviceOperationId,
                    null,
                    request.DeviceEpoch,
                    request.SupervisorInstanceId,
                    PhysicalSafetyActionStatuses.Rejected,
                    rejection);
                database.PhysicalSafetyActions.Add(rejected);
                await database.SaveChangesAsync(cancellationToken);
                return ToResponse(rejected);
            }

            var prepared = NewRecord(
                request.RequestId,
                fingerprint,
                PhysicalSafetyActionTypes.AgvRelease,
                deviceId,
                request.OperatorName,
                request.Reason,
                request.WorkflowRunId,
                request.WorkflowNodeExecutionId,
                request.WorkflowDeviceOperationId,
                null,
                request.DeviceEpoch,
                request.SupervisorInstanceId,
                PhysicalSafetyActionStatuses.Prepared,
                null);
            database.PhysicalSafetyActions.Add(prepared);
            await database.SaveChangesAsync(cancellationToken);

            if (agv is not IPhysicalAgvControlGateway control)
                return await RejectPreparedAsync(prepared, "The configured AGV gateway cannot release control.", cancellationToken);

            // The readiness check above protects the read-only preflight. The
            // supervisor may refresh while the durable reservation is being
            // written, so perform one final synchronous binding check at the
            // Adapter write boundary. A stale epoch must never reach
            // ReleaseControlAsync.
            var finalReadinessRejection = CheckReadinessBinding(deviceId, request);
            if (finalReadinessRejection is not null)
            {
                return await CompleteAsync(
                    prepared,
                    PhysicalSafetyActionStatuses.Rejected,
                    finalReadinessRejection,
                    CancellationToken.None);
            }

            try
            {
                var released = await control.ReleaseControlAsync(cancellationToken);
                return await CompleteAsync(
                    prepared,
                    released ? PhysicalSafetyActionStatuses.Succeeded : PhysicalSafetyActionStatuses.Rejected,
                    released ? "AGV control release confirmed." : "AGV control was not owned at release time.",
                    CancellationToken.None);
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                return await CompleteAsync(
                    prepared,
                    PhysicalSafetyActionStatuses.Unknown,
                    BuildUnknownSummary("AGV control release", exception),
                    CancellationToken.None);
            }
        }
        finally
        {
            ActionGate.Release();
        }
    }

    public async Task<PhysicalSafetyActionResponse> StopAuboAsync(
        string deviceId,
        PhysicalAuboStopRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var armId = RequireValue(deviceId, nameof(deviceId));
        ValidateRequest(request.RequestId, request.OperatorName, request.Reason ?? "operator_requested_safety_stop");
        if (request.OperationId == Guid.Empty)
            throw new ArgumentException("An explicit AUBO operation id is required.", nameof(request));
        if (request.Correlation is { IsComplete: false })
            throw new AuboArmCorrelationException(
                $"{AuboArmWorkflowCorrelationValidator.InvalidCorrelationCode}: AUBO correlation must be complete when supplied.");

        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? "operator_requested_safety_stop"
            : request.Reason.Trim();
        var correlation = request.Correlation;
        var fingerprint = Fingerprint(
            PhysicalSafetyActionTypes.AuboStop,
            armId,
            request.OperatorName,
            reason,
            correlation?.WorkflowRunId,
            correlation?.WorkflowNodeExecutionId,
            correlation?.DeviceOperationId,
            request.OperationId,
            correlation?.EffectiveCorrelationId,
            null,
            null);

        await ActionGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await FindExistingAsync(request.RequestId, fingerprint, cancellationToken);
            if (existing is not null) return ToResponse(existing);

            if (aubo is null)
                return await SaveRejectedAsync(
                    request.RequestId, fingerprint, PhysicalSafetyActionTypes.AuboStop, armId,
                    request.OperatorName, reason, correlation, "The configured AUBO gateway cannot stop programs.", cancellationToken);

            if (correlation is not null)
            {
                if (_workflows is null)
                {
                    throw new AuboArmCorrelationException(
                        $"{AuboArmWorkflowCorrelationValidator.InvalidCorrelationCode}: workflow correlation validation is unavailable.");
                }

                correlation = await AuboArmWorkflowCorrelationValidator.ValidateForStopAsync(
                    armId,
                    request.OperationId,
                    correlation,
                    _workflows,
                    _logger,
                    "stop",
                    cancellationToken);
            }

            AuboArmProgramStatusResponse status;
            try
            {
                // GetProgramAsync is intentionally called immediately before the
                // durable reservation; a cached/stale runtime state cannot permit stop.
                status = await aubo.GetProgramAsync(armId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await SaveRejectedAsync(
                    request.RequestId, fingerprint, PhysicalSafetyActionTypes.AuboStop, armId,
                    request.OperatorName, reason, correlation,
                    "AUBO stop preflight failed; no stop command was sent.", cancellationToken);
            }

            var stopAllowed = status.Online &&
                !string.IsNullOrWhiteSpace(status.LoadedProgram) &&
                status.RuntimeState is AuboArmRuntimeState.Running or
                    AuboArmRuntimeState.Retracting or
                    AuboArmRuntimeState.Pausing or
                    AuboArmRuntimeState.Paused or
                    AuboArmRuntimeState.Stepping or
                    AuboArmRuntimeState.Aborting;
            if (!stopAllowed)
            {
                var detail = !status.Online
                    ? "AUBO controller is offline."
                    : string.IsNullOrWhiteSpace(status.LoadedProgram)
                        ? "AUBO has no loaded program."
                        : $"AUBO runtime state {status.RuntimeState} is not stoppable.";
                return await SaveRejectedAsync(
                    request.RequestId, fingerprint, PhysicalSafetyActionTypes.AuboStop, armId,
                    request.OperatorName, reason, correlation, detail, cancellationToken);
            }

            var prepared = NewRecord(
                request.RequestId,
                fingerprint,
                PhysicalSafetyActionTypes.AuboStop,
                armId,
                request.OperatorName,
                reason,
                correlation?.WorkflowRunId,
                correlation?.WorkflowNodeExecutionId,
                correlation?.DeviceOperationId,
                correlation?.EffectiveCorrelationId,
                null,
                null,
                PhysicalSafetyActionStatuses.Prepared,
                null);
            database.PhysicalSafetyActions.Add(prepared);
            await database.SaveChangesAsync(cancellationToken);

            try
            {
                var result = await aubo.StopProgramAsync(
                    armId,
                    request.OperatorName.Trim(),
                    request.OperationId,
                    correlation,
                    cancellationToken);
                var statusResult = result.State == AuboArmProgramOperationState.Stopped
                    ? PhysicalSafetyActionStatuses.Succeeded
                    : result.MayHaveWritten
                        ? PhysicalSafetyActionStatuses.Unknown
                        : PhysicalSafetyActionStatuses.Rejected;
                return await CompleteAsync(
                    prepared,
                    statusResult,
                    result.State == AuboArmProgramOperationState.Stopped
                        ? "AUBO stop confirmed."
                        : "AUBO stop was not confirmed; manual reconciliation is required.",
                    CancellationToken.None);
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                return await CompleteAsync(
                    prepared,
                    PhysicalSafetyActionStatuses.Unknown,
                    BuildUnknownSummary("AUBO stop", exception),
                    CancellationToken.None);
            }
        }
        finally
        {
            ActionGate.Release();
        }
    }

    /// <summary>Marks every uncompleted reservation Unknown. It never calls a gateway.</summary>
    public Task ReconcilePreparedAsync(CancellationToken cancellationToken) =>
        ReconcilePreparedRecordsAsync(database, cancellationToken);

    public async Task<PhysicalSafetyActionResponse> ReleaseFinalMoveAsync(
        Guid workflowRunId,
        Guid workflowNodeExecutionId,
        Guid workflowDeviceOperationId,
        string agvId,
        string operatorName,
        long? deviceEpoch,
        string? supervisorInstanceId,
        WorkflowStepCompletionOutcome moveOutcome,
        CancellationToken cancellationToken)
    {
        if (workflowRunId == Guid.Empty || workflowNodeExecutionId == Guid.Empty || workflowDeviceOperationId == Guid.Empty)
            throw new ArgumentException("Final Move release requires complete workflow correlation.");

        var normalizedAgvId = RequireValue(agvId, nameof(agvId));
        var normalizedOperatorName = RequireValue(operatorName, nameof(operatorName));
        const string reason = "workflow_final_move_completed";
        var requestId = DeterministicRequestId(
            workflowRunId,
            workflowNodeExecutionId,
            workflowDeviceOperationId,
            deviceEpoch,
            supervisorInstanceId);
        var fingerprint = Fingerprint(
            PhysicalSafetyActionTypes.AgvRelease,
            normalizedAgvId,
            normalizedOperatorName,
            reason,
            workflowRunId,
            workflowNodeExecutionId,
            workflowDeviceOperationId,
            null,
            null,
            deviceEpoch,
            supervisorInstanceId);

        if (moveOutcome != WorkflowStepCompletionOutcome.Succeeded)
        {
            return await SaveRejectedFinalMoveRejectionAsync(
                requestId,
                fingerprint,
                PhysicalSafetyActionTypes.AgvRelease,
                normalizedAgvId,
                normalizedOperatorName,
                reason,
                $"Final Move release requires a verified normal Move terminal outcome; received {moveOutcome}.",
                cancellationToken,
                workflowRunId,
                workflowNodeExecutionId,
                workflowDeviceOperationId,
                deviceEpoch,
                supervisorInstanceId);
        }

        return await ReleaseAgvAsync(
            normalizedAgvId,
            new PhysicalAgvReleaseRequest(
                requestId,
                normalizedOperatorName,
                reason,
                workflowRunId,
                workflowNodeExecutionId,
                workflowDeviceOperationId,
                deviceEpoch,
                supervisorInstanceId),
            cancellationToken);
    }

    private async Task<PhysicalSafetyActionResponse> SaveRejectedFinalMoveRejectionAsync(
        Guid requestId,
        string fingerprint,
        string actionType,
        string deviceId,
        string operatorName,
        string reason,
        string detail,
        CancellationToken cancellationToken,
        Guid workflowRunId,
        Guid workflowNodeExecutionId,
        Guid workflowDeviceOperationId,
        long? deviceEpoch,
        string? supervisorInstanceId)
    {
        await ActionGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await FindExistingAsync(requestId, fingerprint, cancellationToken);
            return existing is not null
                ? ToResponse(existing)
                : await SaveRejectedAsync(
                    requestId,
                    fingerprint,
                    actionType,
                    deviceId,
                    operatorName,
                    reason,
                    null,
                    detail,
                    cancellationToken,
                    workflowRunId,
                    workflowNodeExecutionId,
                    workflowDeviceOperationId,
                    deviceEpoch,
                    supervisorInstanceId);
        }
        finally
        {
            ActionGate.Release();
        }
    }

    public static async Task ReconcilePreparedRecordsAsync(
        MesDbContext database,
        CancellationToken cancellationToken)
    {
        var records = await database.PhysicalSafetyActions
            .Where(action => action.Status == PhysicalSafetyActionStatuses.Prepared)
            .ToListAsync(cancellationToken);
        if (records.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var record in records)
        {
            record.Status = PhysicalSafetyActionStatuses.Unknown;
            record.ResultSummary = "Prepared safety action found at startup; manual reconciliation is required and no command was replayed.";
            record.CompletedAtUtc = now;
            record.UpdatedAtUtc = now;
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<PhysicalSafetyActionResponse> SaveRejectedAsync(
        Guid requestId,
        string fingerprint,
        string actionType,
        string deviceId,
        string operatorName,
        string reason,
        AuboArmOperationCorrelation? correlation,
        string detail,
        CancellationToken cancellationToken,
        Guid? workflowRunId = null,
        Guid? workflowNodeExecutionId = null,
        Guid? workflowDeviceOperationId = null,
        long? deviceEpoch = null,
        string? supervisorInstanceId = null)
    {
        var record = NewRecord(
            requestId, fingerprint, actionType, deviceId, operatorName, reason,
            workflowRunId ?? correlation?.WorkflowRunId,
            workflowNodeExecutionId ?? correlation?.WorkflowNodeExecutionId,
            workflowDeviceOperationId ?? correlation?.DeviceOperationId,
            correlation?.EffectiveCorrelationId,
            deviceEpoch,
            supervisorInstanceId,
            PhysicalSafetyActionStatuses.Rejected,
            detail);
        database.PhysicalSafetyActions.Add(record);
        await database.SaveChangesAsync(cancellationToken);
        return ToResponse(record);
    }

    private Task<PhysicalSafetyActionResponse> RejectPreparedAsync(
        PhysicalSafetyActionRecord record,
        string detail,
        CancellationToken cancellationToken) => CompleteAsync(
            record,
            PhysicalSafetyActionStatuses.Rejected,
            detail,
            cancellationToken);

    private async Task<PhysicalSafetyActionResponse> CompleteAsync(
        PhysicalSafetyActionRecord record,
        string status,
        string summary,
        CancellationToken cancellationToken)
    {
        if (record.Status != PhysicalSafetyActionStatuses.Prepared)
            return ToResponse(record);
        record.Status = status;
        record.ResultSummary = summary;
        record.CompletedAtUtc = _timeProvider.GetUtcNow();
        record.UpdatedAtUtc = record.CompletedAtUtc.Value;
        await database.SaveChangesAsync(cancellationToken);
        return ToResponse(record);
    }

    private async Task<PhysicalSafetyActionRecord?> FindExistingAsync(
        Guid requestId,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var existing = await database.PhysicalSafetyActions
            .SingleOrDefaultAsync(action => action.RequestId == requestId, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("The physical safety request id is already bound to a different action.");
            return existing;
        }

        var byFingerprint = await database.PhysicalSafetyActions
            .SingleOrDefaultAsync(action => action.Fingerprint == fingerprint, cancellationToken);
        if (byFingerprint is not null)
            throw new InvalidOperationException("The physical safety action fingerprint is already bound to another request id.");
        return null;
    }

    private async Task<string?> CheckAgvPreflightAsync(string agvId, CancellationToken cancellationToken)
    {
        if (agv is null) return "The configured AGV gateway is unavailable.";
        AgvSnapshotResponse snapshot;
        try
        {
            snapshot = await agv.GetSnapshotAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"AGV release preflight failed: {exception.GetType().Name}.";
        }

        if (!string.Equals(snapshot.AgvId, agvId, StringComparison.OrdinalIgnoreCase))
            return "AGV identity returned by the Adapter does not match the requested AGV.";
        if (!snapshot.Online)
            return "AGV is offline.";
        var expectedOwner = profile.PhysicalAcceptance?.ExpectedControlOwner;
        if (string.IsNullOrWhiteSpace(expectedOwner)) expectedOwner = "adapter";
        if (!string.Equals(snapshot.ControlOwner, expectedOwner, StringComparison.OrdinalIgnoreCase))
            return $"AGV control owner is '{snapshot.ControlOwner}', expected '{expectedOwner}'.";
        if (snapshot.CurrentTaskId is not null)
            return "AGV has an active controller task.";

        var activeMesTask = await database.TransportTasks.AnyAsync(task =>
            task.ActiveAgvId == agvId &&
            task.ActiveDeviceTaskId != null &&
            task.Status != Domain.TaskStatus.Completed &&
            task.Status != Domain.TaskStatus.Cancelled &&
            task.Status != Domain.TaskStatus.Failed,
            cancellationToken);
        if (activeMesTask) return "AGV has an active MES task.";

        var activeAcceptance = await database.FieldNavigationAcceptances.AnyAsync(acceptance =>
            acceptance.AgvId == agvId &&
            (acceptance.Status == FieldNavigationAcceptanceStatuses.Authorized ||
             acceptance.Status == FieldNavigationAcceptanceStatuses.Dispatching ||
             acceptance.Status == FieldNavigationAcceptanceStatuses.Accepted ||
             acceptance.Status == FieldNavigationAcceptanceStatuses.Moving ||
             acceptance.Status == FieldNavigationAcceptanceStatuses.Unknown),
            cancellationToken);
        return activeAcceptance ? "AGV has an active field-navigation acceptance." : null;
    }

    /// <summary>
    /// Validates an epoch binding only when the caller supplied one. Explicit
    /// manual safety release remains available without a workflow binding; a
    /// workflow-linked release (and any partially supplied binding) must use
    /// the current enabled supervisor and device epoch.
    /// </summary>
    private string? CheckReadinessBinding(
        string deviceId,
        PhysicalAgvReleaseRequest request)
    {
        var hasWorkflowBinding = request.WorkflowRunId.HasValue ||
                                 request.WorkflowNodeExecutionId.HasValue ||
                                 request.WorkflowDeviceOperationId.HasValue;
        var hasReadinessBinding = request.DeviceEpoch.HasValue ||
                                  !string.IsNullOrWhiteSpace(request.SupervisorInstanceId);
        if (!hasWorkflowBinding && !hasReadinessBinding)
        {
            return null;
        }

        if (_physicalReadiness is not { Enabled: true } readiness)
        {
            return $"{PhysicalReadinessReasonCodes.SupervisorDisabled}: physical safety release binding requires an enabled readiness supervisor.";
        }

        if (!request.DeviceEpoch.HasValue || request.DeviceEpoch.Value <= 0 ||
            string.IsNullOrWhiteSpace(request.SupervisorInstanceId))
        {
            return $"{PhysicalReadinessReasonCodes.EpochAuthorizationRequired}: a bound AGV release requires the current device epoch and supervisor instance id.";
        }

        if (!readiness.IsCurrentAndReady(
                deviceId,
                request.DeviceEpoch,
                request.SupervisorInstanceId,
                out var reason))
        {
            var code = string.IsNullOrWhiteSpace(reason)
                ? PhysicalReadinessReasonCodes.DeviceNotReady
                : reason;
            return $"{code}: physical readiness binding is not current for '{deviceId}'.";
        }

        return null;
    }

    private PhysicalSafetyActionRecord NewRecord(
        Guid requestId,
        string fingerprint,
        string actionType,
        string deviceId,
        string operatorName,
        string reason,
        Guid? workflowRunId,
        Guid? workflowNodeExecutionId,
        Guid? workflowDeviceOperationId,
        string? correlationId,
        long? deviceEpoch,
        string? supervisorInstanceId,
        string status,
        string? resultSummary)
    {
        var now = _timeProvider.GetUtcNow();
        return new PhysicalSafetyActionRecord
        {
            RequestId = requestId,
            Fingerprint = fingerprint,
            ActionType = actionType,
            DeviceId = deviceId,
            OperatorName = operatorName.Trim(),
            Reason = reason.Trim(),
            Status = status,
            ResultSummary = resultSummary,
            WorkflowRunId = workflowRunId,
            WorkflowNodeExecutionId = workflowNodeExecutionId,
            WorkflowDeviceOperationId = workflowDeviceOperationId,
            CorrelationId = correlationId,
            DeviceEpoch = deviceEpoch,
            SupervisorInstanceId = supervisorInstanceId,
            PreparedAtUtc = now,
            CompletedAtUtc = status == PhysicalSafetyActionStatuses.Prepared ? null : now,
            UpdatedAtUtc = now
        };
    }

    private static PhysicalSafetyActionResponse ToResponse(PhysicalSafetyActionRecord record) => new(
        record.Id,
        record.RequestId,
        record.Fingerprint,
        record.ActionType,
        record.DeviceId,
        record.OperatorName,
        record.Reason,
        record.Status,
        record.ResultSummary,
        record.WorkflowRunId,
        record.WorkflowNodeExecutionId,
        record.WorkflowDeviceOperationId,
        record.CorrelationId,
        record.PreparedAtUtc,
        record.CompletedAtUtc,
        record.UpdatedAtUtc);

    private static void ValidateRequest(Guid requestId, string operatorName, string reason)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A request id is required.", nameof(requestId));
        RequireValue(operatorName, nameof(operatorName));
        RequireValue(reason, nameof(reason));
    }

    private static string RequireValue(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();

    private static string Fingerprint(
        string actionType,
        string deviceId,
        string operatorName,
        string reason,
        Guid? workflowRunId,
        Guid? workflowNodeExecutionId,
        Guid? workflowDeviceOperationId,
        Guid? operationId,
        string? correlationId,
        long? deviceEpoch = null,
        string? supervisorInstanceId = null)
    {
        // Keep the original canonical form for unbound/manual actions so old
        // persisted request ids and fingerprints remain idempotent. Once a
        // readiness binding is present, include both values in the canonical
        // input so a new device session cannot replay an old final release.
        var values = new List<string?>
        {
            actionType,
            deviceId.Trim(),
            operatorName.Trim(),
            reason.Trim(),
            workflowRunId?.ToString(),
            workflowNodeExecutionId?.ToString(),
            workflowDeviceOperationId?.ToString(),
            operationId?.ToString(),
            correlationId
        };
        if (deviceEpoch.HasValue || !string.IsNullOrWhiteSpace(supervisorInstanceId))
        {
            values.Add(deviceEpoch?.ToString(CultureInfo.InvariantCulture));
            values.Add(supervisorInstanceId?.Trim());
        }

        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join('\u001f', values))));
    }

    private static Guid DeterministicRequestId(
        Guid runId,
        Guid nodeId,
        Guid operationId,
        long? deviceEpoch = null,
        string? supervisorInstanceId = null)
    {
        var seed = deviceEpoch is null && string.IsNullOrWhiteSpace(supervisorInstanceId)
            ? $"physical-safety-final-move:{runId:N}:{nodeId:N}:{operationId:N}"
            : string.Join(
                '\u001f',
                "physical-safety-final-move:v2",
                runId.ToString("N"),
                nodeId.ToString("N"),
                operationId.ToString("N"),
                deviceEpoch?.ToString(CultureInfo.InvariantCulture),
                supervisorInstanceId?.Trim());
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        bytes[6] = (byte)(bytes[6] & 0x0f | 0x50);
        bytes[8] = (byte)(bytes[8] & 0x3f | 0x80);
        return new Guid(bytes[..16]);
    }

    private static string BuildUnknownSummary(string action, Exception exception) =>
        $"{action} outcome is Unknown after a possible write ({exception.GetType().Name}); manual reconciliation is required and the action was not retried.";
}
