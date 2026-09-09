using System.Security.Cryptography;
using System.Text;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

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
    TimeProvider? timeProvider = null)
{
    private static readonly SemaphoreSlim ActionGate = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<PhysicalSafetyActionResponse> ReleaseAgvAsync(
        string agvId,
        PhysicalAgvReleaseRequest request,
        CancellationToken cancellationToken)
    {
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
            null);

        await ActionGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await FindExistingAsync(request.RequestId, fingerprint, cancellationToken);
            if (existing is not null) return ToResponse(existing);

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
        var armId = RequireValue(deviceId, nameof(deviceId));
        ValidateRequest(request.RequestId, request.OperatorName, request.Reason ?? "operator_requested_safety_stop");
        if (request.OperationId == Guid.Empty)
            throw new ArgumentException("An explicit AUBO operation id is required.", nameof(request));
        if (request.Correlation is { IsComplete: false })
            throw new ArgumentException("AUBO correlation must be complete when supplied.", nameof(request));

        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? "operator_requested_safety_stop"
            : request.Reason.Trim();
        var fingerprint = Fingerprint(
            PhysicalSafetyActionTypes.AuboStop,
            armId,
            request.OperatorName,
            reason,
            request.Correlation?.WorkflowRunId,
            request.Correlation?.WorkflowNodeExecutionId,
            request.Correlation?.DeviceOperationId,
            request.OperationId,
            request.Correlation?.EffectiveCorrelationId);

        await ActionGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await FindExistingAsync(request.RequestId, fingerprint, cancellationToken);
            if (existing is not null) return ToResponse(existing);

            if (aubo is null)
                return await SaveRejectedAsync(
                    request.RequestId, fingerprint, PhysicalSafetyActionTypes.AuboStop, armId,
                    request.OperatorName, reason, request.Correlation, "The configured AUBO gateway cannot stop programs.", cancellationToken);

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
                    request.OperatorName, reason, request.Correlation,
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
                    request.OperatorName, reason, request.Correlation, detail, cancellationToken);
            }

            var prepared = NewRecord(
                request.RequestId,
                fingerprint,
                PhysicalSafetyActionTypes.AuboStop,
                armId,
                request.OperatorName,
                reason,
                request.Correlation?.WorkflowRunId,
                request.Correlation?.WorkflowNodeExecutionId,
                request.Correlation?.DeviceOperationId,
                request.Correlation?.EffectiveCorrelationId,
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
                    request.Correlation,
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
        CancellationToken cancellationToken)
    {
        if (workflowRunId == Guid.Empty || workflowNodeExecutionId == Guid.Empty || workflowDeviceOperationId == Guid.Empty)
            throw new ArgumentException("Final Move release requires complete workflow correlation.");
        if (physicalReadiness is not { Enabled: true } readiness ||
            !readiness.IsCurrentAndReady(agvId, deviceEpoch, supervisorInstanceId, out _))
        {
            return await SaveRejectedFinalMoveRejectionAsync(
                DeterministicRequestId(workflowRunId, workflowNodeExecutionId, workflowDeviceOperationId),
                Fingerprint(PhysicalSafetyActionTypes.AgvRelease, agvId, operatorName,
                    "workflow_final_move_completed", workflowRunId, workflowNodeExecutionId,
                    workflowDeviceOperationId, null, null),
                PhysicalSafetyActionTypes.AgvRelease,
                agvId,
                operatorName,
                "workflow_final_move_completed",
                "The physical-readiness supervisor instance or AGV epoch is not current.",
                cancellationToken,
                workflowRunId,
                workflowNodeExecutionId,
                workflowDeviceOperationId,
                deviceEpoch,
                supervisorInstanceId);
        }

        return await ReleaseAgvAsync(
            agvId,
            new PhysicalAgvReleaseRequest(
                DeterministicRequestId(workflowRunId, workflowNodeExecutionId, workflowDeviceOperationId),
                operatorName,
                "workflow_final_move_completed",
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
        string? correlationId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '\u001f', actionType, deviceId.Trim(), operatorName.Trim(), reason.Trim(),
            workflowRunId, workflowNodeExecutionId, workflowDeviceOperationId, operationId, correlationId))));

    private static Guid DeterministicRequestId(Guid runId, Guid nodeId, Guid operationId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"physical-safety-final-move:{runId:N}:{nodeId:N}:{operationId:N}"));
        bytes[6] = (byte)(bytes[6] & 0x0f | 0x50);
        bytes[8] = (byte)(bytes[8] & 0x3f | 0x80);
        return new Guid(bytes[..16]);
    }

    private static string BuildUnknownSummary(string action, Exception exception) =>
        $"{action} outcome is Unknown after a possible write ({exception.GetType().Name}); manual reconciliation is required and the action was not retried.";
}
