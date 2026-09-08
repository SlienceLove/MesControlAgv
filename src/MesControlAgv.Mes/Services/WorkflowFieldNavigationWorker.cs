using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Entities;
using System.Collections.Concurrent;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Explicit opt-in bridge from a durable workflow Move node to one separately
/// created and operator-authorized field-navigation acceptance. The normal mode
/// requires a pre-created permit; the separate AutoAuthorizeFromRunRequest mode
/// only accepts an explicit, persisted run-level batch authorization.
/// </summary>
public sealed class WorkflowFieldNavigationWorkerOptions
{
    public bool Enabled { get; init; }
    /// <summary>
    /// Allows a run carrying an explicit WorkflowPhysicalRunAuthorization to
    /// have its linked Move permits created as nodes become ready. This remains
    /// disabled by default; enabling it is a separate physical-site gate.
    /// </summary>
    public bool AutoAuthorizeFromRunRequest { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a field Move waits for a transient physical condition to clear
    /// before yielding back to the worker loop. No device command is sent while
    /// this gate is closed; a later poll may continue the same ready node.
    /// </summary>
    public TimeSpan TransientRetryWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Read-only preflight interval used inside the retry window.</summary>
    public TimeSpan TransientRetryInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Required continuous healthy-read duration before a Move may be claimed.
    /// This prevents a single noisy confidence sample from reopening dispatch.
    /// </summary>
    public TimeSpan ReadyStabilityWindow { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Bounded one-shot cleanup after the final Move completes the workflow.
    /// A timeout is logged and never retried automatically because the release
    /// request may already have reached the Adapter.
    /// </summary>
    public TimeSpan ControlReleaseTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Keeps a timed-out node quiet while the same physical blocker remains. A
/// changed preflight result (including recovery) opens a fresh retry window;
/// this prevents a permanently blocked node from spinning every poll while
/// still allowing an operator's correction to continue the same node.
/// </summary>
public sealed class WorkflowFieldNavigationRetryState
{
    private readonly ConcurrentDictionary<Guid, string> _timedOut = new();

    public bool IsTimedOut(Guid nodeExecutionId, string warningKey) =>
        _timedOut.TryGetValue(nodeExecutionId, out var previous) &&
        string.Equals(previous, warningKey, StringComparison.Ordinal);

    public void MarkTimedOut(Guid nodeExecutionId, string warningKey) =>
        _timedOut[nodeExecutionId] = warningKey;

    public void Clear(Guid nodeExecutionId) => _timedOut.TryRemove(nodeExecutionId, out _);
}

public sealed class WorkflowFieldNavigationDispatcher(
    IWorkflowApplicationService workflows,
    IFieldNavigationAcceptanceApplicationService acceptances,
    FieldNavigationAcceptanceRepository repository,
    ProfileConfiguration profile,
    WorkflowFieldNavigationWorkerOptions options,
    TimeProvider? timeProvider = null,
    IAgvGateway? agv = null,
    ILogger? logger = null,
    WorkflowFieldNavigationRetryState? retryState = null,
    IPhysicalReadinessState? physicalReadiness = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IAgvGateway? _agv = agv;
    private readonly ILogger? _logger = logger;
    private readonly WorkflowFieldNavigationRetryState? _retryState = retryState;
    private readonly IPhysicalReadinessState? _physicalReadiness = physicalReadiness;

    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        if (!CanRun()) return;

        // Reconcile already claimed physical work before waiting on any new
        // Ready node. A five-minute preflight window for a disconnected new
        // batch must never starve status propagation for a navigation command
        // that may already be moving on the controller.
        await RecoverAsync(cancellationToken);

        foreach (var workItem in await workflows.ListFieldNavigationDispatchableNodesAsync(cancellationToken))
        {
            if (!await WaitForTransientPhysicalReadinessAsync(workItem, cancellationToken))
                continue;

            var acceptance = await repository.GetByWorkflowNodeExecutionIdAsync(
                workItem.NodeExecution.Id,
                cancellationToken);
            if (acceptance is null && options.AutoAuthorizeFromRunRequest)
            {
                await TryCreateAndAuthorizeFromRunAsync(workItem, cancellationToken);
                acceptance = await repository.GetByWorkflowNodeExecutionIdAsync(
                    workItem.NodeExecution.Id,
                    cancellationToken);
            }
            if (acceptance?.Status != FieldNavigationAcceptanceStatuses.Authorized) continue;

            var claimed = await workflows.ClaimNodeExecutionAsync(
                workItem.NodeExecution.Id,
                cancellationToken);
            if (claimed.DeviceOperation is not { } operation)
            {
                await CompleteWithEntityAsync(
                    claimed,
                    WorkflowStepCompletionOutcome.Failed,
                    "The workflow Move node was claimed without a durable device operation.",
                    acceptance,
                    cancellationToken);
                continue;
            }

            await repository.LinkDeviceOperationAsync(
                acceptance,
                operation.OperationId,
                cancellationToken);
            try
            {
                var dispatched = await acceptances.DispatchForWorkflowAsync(
                    acceptance.Id,
                    claimed.NodeExecution.Id,
                    operation.OperationId,
                    cancellationToken);
                await ApplyAcceptanceAsync(claimed, dispatched, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // The acceptance service may have crossed the Adapter boundary
                // before an unexpected response failure. Persist Unknown and
                // never claim or dispatch this node a second time automatically.
                await CompleteAsync(
                    claimed,
                    WorkflowStepCompletionOutcome.Unknown,
                    exception.Message,
                    ToResponse(acceptance),
                    cancellationToken);
            }
        }
    }

    private async Task<FieldNavigationAcceptanceResponse?> TryCreateAndAuthorizeFromRunAsync(
        WorkflowNodeExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        if (_agv is null) return null;

        var request = await workflows.GetExecutionRequestAsync(
            workItem.NodeExecution.WorkflowRunId,
            cancellationToken);
        var authorization = request?.PhysicalAuthorization;
        if (request is null || authorization is null ||
            string.IsNullOrWhiteSpace(authorization.AgvId) ||
            string.IsNullOrWhiteSpace(authorization.OperatorName) ||
            string.IsNullOrWhiteSpace(authorization.SafetyObserverName) ||
            string.IsNullOrWhiteSpace(authorization.PermitPrefix))
        {
            return null;
        }

        if (authorization.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            _logger?.LogWarning(
                "Workflow Move {NodeExecutionId} remains Ready because physical batch authorization expired at {ExpiresAtUtc}.",
                workItem.NodeExecution.Id,
                authorization.ExpiresAtUtc);
            return null;
        }

        if (!string.Equals(request.RequestedBy?.Trim(), authorization.OperatorName.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        if (!workItem.NodeExecution.Inputs.TryGetValue(
                WorkflowNodeConfigurationKeys.TargetStation,
                out var targetStation) ||
            string.IsNullOrWhiteSpace(targetStation))
        {
            return null;
        }
        var normalizedTargetStation = targetStation!.Trim();

        var snapshot = await _agv.GetSnapshotAsync(cancellationToken);
        if (!snapshot.Online ||
            snapshot.CurrentTaskId is not null ||
            string.IsNullOrWhiteSpace(snapshot.CurrentStationId) ||
            (!string.IsNullOrWhiteSpace(snapshot.ControlOwner) &&
             !string.Equals(snapshot.ControlOwner, "none", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(snapshot.ControlOwner, "adapter", StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(snapshot.AgvId, authorization.AgvId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var permitId = BuildPermitId(
            authorization.PermitPrefix,
            workItem.NodeExecution.WorkflowRunId,
            workItem.NodeExecution.NodeId,
            workItem.NodeExecution.Attempt);
        try
        {
            var draft = await acceptances.CreateAsync(
                new CreateFieldNavigationAcceptanceRequest(
                    authorization.AgvId.Trim(),
                    snapshot.CurrentStationId.Trim(),
                    normalizedTargetStation,
                    $"批量现场流程 {workItem.NodeExecution.WorkflowRunId:D} / {workItem.NodeExecution.NodeName}")
                {
                    WorkflowRunId = workItem.NodeExecution.WorkflowRunId,
                    WorkflowNodeExecutionId = workItem.NodeExecution.Id
                },
                cancellationToken);
            return await acceptances.AuthorizeAsync(
                draft.Id,
                new AuthorizeFieldNavigationAcceptanceRequest(
                    authorization.OperatorName.Trim(),
                    authorization.SafetyObserverName.Trim(),
                    permitId,
                    authorization.ExpiresAtUtc),
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // A concurrent/manual acceptance or a changed readiness state is
            // left for the next polling cycle or explicit operator review.
            return null;
        }
    }

    private async Task<bool> WaitForTransientPhysicalReadinessAsync(
        WorkflowNodeExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        if (!await HasCurrentSupervisorEpochAsync(workItem, cancellationToken))
            return false;

        if (_agv is not IPhysicalPreflightAgvGateway physical)
        {
            // The run-level batch path must have a physical read-only
            // preflight capability. Without it, keeping the node Ready is
            // safer than creating a permit and allowing a dispatch boundary
            // to proceed on incomplete evidence. Legacy manual-acceptance
            // flows retain their existing boundary checks.
            if (options.AutoAuthorizeFromRunRequest)
            {
                _logger?.LogWarning(
                    "Workflow Move {NodeExecutionId} cannot enter batch dispatch because the active AGV gateway does not expose physical preflight.",
                    workItem.NodeExecution.Id);
                return false;
            }

            return true;
        }

        var retryWindow = options.TransientRetryWindow;
        if (retryWindow <= TimeSpan.Zero)
            return true;

        var retryInterval = options.TransientRetryInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(5)
            : options.TransientRetryInterval;
        var deadline = _timeProvider.GetUtcNow().Add(retryWindow);
        string? lastWarning = null;
        DateTimeOffset? readySince = null;

        while (true)
        {
            if (!await HasCurrentSupervisorEpochAsync(workItem, cancellationToken))
                return false;

            PhysicalAgvPreflightResponse? assessment = null;
            IReadOnlyList<string> blockers;
            try
            {
                assessment = await physical.GetPhysicalPreflightAsync(cancellationToken);
                var minimumConfidence = profile.PhysicalAcceptance?.Safety.MinimumLocalizationConfidence ?? 0.9;
                blockers = GetTransientBlockers(assessment, minimumConfidence);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                blockers = [$"现场预检读取失败：{exception.Message}"];
            }

            if (blockers.Count == 0)
            {
                _retryState?.Clear(workItem.NodeExecution.Id);
                if (assessment?.Readiness is null || options.ReadyStabilityWindow <= TimeSpan.Zero)
                    return true;

                readySince ??= _timeProvider.GetUtcNow();
                if (_timeProvider.GetUtcNow() - readySince >= options.ReadyStabilityWindow)
                    return true;

                var stableRemaining = options.ReadyStabilityWindow -
                                      (_timeProvider.GetUtcNow() - readySince.Value);
                await Task.Delay(
                        stableRemaining < retryInterval ? stableRemaining : retryInterval,
                        _timeProvider,
                        cancellationToken);
                continue;
            }

            readySince = null;

            var warning = string.Join("; ", blockers);
            var warningKey = string.Join("|", blockers);
            if (_retryState?.IsTimedOut(workItem.NodeExecution.Id, warningKey) == true)
            {
                _logger?.LogWarning(
                    "Workflow Move {NodeExecutionId} remains paused after the retry window elapsed: {Reasons}",
                    workItem.NodeExecution.Id,
                    warning);
                return false;
            }
            if (!string.Equals(lastWarning, warning, StringComparison.Ordinal))
            {
                _logger?.LogWarning(
                    "Workflow Move {NodeExecutionId} is waiting for physical readiness: {Reasons}",
                    workItem.NodeExecution.Id,
                    warning);
                lastWarning = warning;
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                _logger?.LogWarning(
                    "Workflow Move {NodeExecutionId} kept waiting after the transient retry window elapsed; leaving it Ready for the next poll.",
                    workItem.NodeExecution.Id);
                _retryState?.MarkTimedOut(workItem.NodeExecution.Id, warningKey);
                return false;
            }

            await Task.Delay(
            remaining < retryInterval ? remaining : retryInterval,
            _timeProvider,
            cancellationToken);
        }
    }

    private async Task<bool> HasCurrentSupervisorEpochAsync(
        WorkflowNodeExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        if (_physicalReadiness is not { Enabled: true } readiness) return true;

        var request = await workflows.GetExecutionRequestAsync(
            workItem.NodeExecution.WorkflowRunId,
            cancellationToken);
        var authorization = request?.PhysicalAuthorization;
        var agvId = authorization?.AgvId?.Trim();
        var epoch = string.IsNullOrWhiteSpace(agvId)
            ? null
            : authorization!.GetDeviceEpoch(agvId);
        string? reason = null;
        if (!string.IsNullOrWhiteSpace(agvId) &&
            readiness.IsCurrentAndReady(
                agvId,
                epoch,
                authorization?.ReadinessSupervisorInstanceId,
                out reason))
        {
            return true;
        }

        _logger?.LogWarning(
            "Workflow Move {NodeExecutionId} remains Ready because physical readiness epoch validation failed for {AgvId}: {Reason}.",
            workItem.NodeExecution.Id,
            agvId ?? "unknown",
            reason ?? PhysicalReadinessReasonCodes.EpochRequired);
        return false;
    }

    private static IReadOnlyList<string> GetTransientBlockers(
        PhysicalAgvPreflightResponse assessment,
        double minimumConfidence)
    {
        var blockers = new List<string>();
        var snapshot = assessment.Snapshot;
        var readiness = assessment.Readiness;

        if (!snapshot.Online)
            blockers.Add("AGV 离线");
        if (snapshot.CurrentTaskId is not null)
            blockers.Add("AGV 仍有活动任务");
        if (!string.IsNullOrWhiteSpace(snapshot.ControlOwner) &&
            !string.Equals(snapshot.ControlOwner, "none", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(snapshot.ControlOwner, "adapter", StringComparison.OrdinalIgnoreCase))
            blockers.Add($"AGV 控制权被 {snapshot.ControlOwner} 占用");

        if (readiness is null)
            return blockers.Count == 0 && assessment.DispatchPermitted
                ? []
                : blockers.Append("AGV 安全状态暂不可读取").ToArray();

        if (readiness.Emergency == true)
            blockers.Add("AGV 急停有效");
        if (readiness.Blocked == true)
            blockers.Add("AGV 被阻挡");
        if (readiness.ManualBlock == true)
            blockers.Add("AGV 手动阻挡有效");
        if (readiness.FatalCount > 0)
            blockers.Add($"AGV 有 {readiness.FatalCount} 个致命故障");
        if (readiness.ErrorCount > 0)
            blockers.Add($"AGV 有 {readiness.ErrorCount} 个错误");
        if (readiness.RelocationStatus is not 1)
            blockers.Add($"AGV 重定位状态未成功（{readiness.RelocationStatus?.ToString() ?? "未知"}）");

        if (readiness.LocalizationConfidence is not { } confidence ||
            !double.IsFinite(confidence) || confidence < minimumConfidence)
            blockers.Add($"AGV 定位置信度不足（{readiness.LocalizationConfidence?.ToString("0.###") ?? "未知"} < {minimumConfidence:0.##}）");

        if (assessment.MapEvidence is null)
            blockers.Add("AGV 地图证据暂不可读取");

        // The generic preflight endpoint always reports these two reasons before
        // a field-navigation session owns control. They are handled above and
        // must not make an otherwise safe, idle AGV wait forever. All other
        // controller/profile mismatches remain blocking.
        var hasExpectedPreControlBlocker = false;
        foreach (var reason in assessment.BlockingReasons)
        {
            if (string.Equals(reason, "automatic_dispatch_disabled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "adapter_does_not_hold_control", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "blocked_status_not_clear", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "controller_faults_active", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "agv_has_active_task", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "localization_confidence_below_threshold", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(reason, "automatic_dispatch_disabled", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(reason, "adapter_does_not_hold_control", StringComparison.OrdinalIgnoreCase))
                    hasExpectedPreControlBlocker = true;
                continue;
            }

            blockers.Add(DescribePreflightReason(reason));
        }

        // A driver must not be able to return DispatchPermitted=false with an
        // empty/unknown reason set and still pass this gate. The known
        // pre-control reasons above are deliberately ignored, but an otherwise
        // unexplained negative assessment remains a hard blocker.
        if (!assessment.DispatchPermitted && blockers.Count == 0 && !hasExpectedPreControlBlocker)
            blockers.Add("AGV 现场预检未通过（阻断原因未知）");

        return blockers;
    }

    private static string DescribePreflightReason(string reason) => reason switch
    {
        "controller_map_evidence_unavailable" => "AGV 地图证据不可用",
        "controller_map_name_mismatch" => "控制器地图名称与批准地图不一致",
        "controller_map_version_mismatch" => "控制器地图版本与批准版本不一致",
        "controller_map_md5_mismatch" => "控制器地图 MD5 与批准值不一致",
        "controller_map_station_mismatch" => "控制器站点目录与批准目录不一致",
        "controller_map_route_mismatch" => "控制器路线与批准路线不一致",
        _ => $"现场预检阻断：{reason}"
    };

    private static string BuildPermitId(string prefix, Guid runId, Guid nodeId, int attempt)
    {
        var normalized = prefix.Trim();
        return $"{normalized}-{runId:N}-{nodeId:N}-a{Math.Max(1, attempt)}";
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (!CanRun()) return;

        foreach (var workItem in await workflows.ListFieldNavigationRecoverableNodesAsync(cancellationToken))
        {
            var acceptance = await repository.GetByWorkflowNodeExecutionIdAsync(
                workItem.NodeExecution.Id,
                cancellationToken);
            if (acceptance is null)
            {
                await CompleteWithEntityAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    "A running physical Move node has no linked field-navigation acceptance.",
                    null,
                    cancellationToken);
                continue;
            }
            if (workItem.DeviceOperation is not { } operation ||
                acceptance.WorkflowDeviceOperationId != operation.OperationId)
            {
                await CompleteWithEntityAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    "The field-navigation acceptance is not linked to the active workflow device operation.",
                    acceptance,
                    cancellationToken);
                continue;
            }

            if (!await HasCurrentSupervisorEpochForRecoveryAsync(
                    workItem,
                    cancellationToken))
            {
                continue;
            }

            await ApplyAcceptanceAsync(
                workItem,
                ToResponse(acceptance),
                cancellationToken);
        }
    }

    private async Task<bool> HasCurrentSupervisorEpochForRecoveryAsync(
        WorkflowNodeExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        if (_physicalReadiness is not { Enabled: true } readiness)
            return true;

        var request = await workflows.GetExecutionRequestAsync(
            workItem.NodeExecution.WorkflowRunId,
            cancellationToken);
        var authorization = request?.PhysicalAuthorization;
        var agvId = authorization?.AgvId?.Trim();
        var epoch = string.IsNullOrWhiteSpace(agvId)
            ? null
            : authorization!.GetDeviceEpoch(agvId);
        var supervisorInstanceId = authorization?.ReadinessSupervisorInstanceId;
        string? reason = null;

        if (!string.IsNullOrWhiteSpace(agvId) &&
            readiness.IsCurrentAndReady(
                agvId,
                epoch,
                supervisorInstanceId,
                out reason))
        {
            return true;
        }

        var reconciliationReason = string.IsNullOrWhiteSpace(reason)
            ? PhysicalReadinessReasonCodes.EpochRequired
            : reason;
        var error =
            $"Manual reconciliation required: persisted physical Move recovery was blocked because the readiness supervisor instance/AGV epoch is not current ({reconciliationReason}).";
        _logger?.LogWarning(
            "Workflow Move {NodeExecutionId} recovery was blocked by physical readiness binding: {Reason}.",
            workItem.NodeExecution.Id,
            error);
        await CompleteWithEntityAsync(
            workItem,
            WorkflowStepCompletionOutcome.Unknown,
            error,
            null,
            cancellationToken);
        return false;
    }

    private bool CanRun() =>
        options.Enabled &&
        !profile.Features.UseSimulator &&
        profile.Features.EnableFieldNavigationAcceptance;

    private Task ApplyAcceptanceAsync(
        WorkflowNodeExecutionWorkItem workItem,
        FieldNavigationAcceptanceResponse acceptance,
        CancellationToken cancellationToken) => acceptance.Status switch
        {
            FieldNavigationAcceptanceStatuses.Arrived => CompleteArrivedAsync(
                workItem,
                acceptance,
                cancellationToken),
            FieldNavigationAcceptanceStatuses.Cancelled => CompleteCancelledAsync(
                workItem,
                acceptance,
                cancellationToken),
            FieldNavigationAcceptanceStatuses.Failed or
            FieldNavigationAcceptanceStatuses.Rejected or
            FieldNavigationAcceptanceStatuses.Expired => CompleteAsync(
                workItem,
                WorkflowStepCompletionOutcome.Failed,
                acceptance.LastError ?? $"field_navigation_{acceptance.Status}",
                acceptance,
                cancellationToken),
            FieldNavigationAcceptanceStatuses.Unknown => CompleteAsync(
                workItem,
                WorkflowStepCompletionOutcome.Unknown,
                acceptance.LastError ?? "field_navigation_outcome_unknown",
                acceptance,
                cancellationToken),
            FieldNavigationAcceptanceStatuses.Dispatching or
            FieldNavigationAcceptanceStatuses.Accepted => RecordProgressAsync(
                workItem,
                WorkflowDeviceOperationStatus.Accepted,
                acceptance.LastError,
                cancellationToken),
            FieldNavigationAcceptanceStatuses.Moving => RecordProgressAsync(
                workItem,
                WorkflowDeviceOperationStatus.Running,
                acceptance.LastError,
                cancellationToken),
            _ => Task.CompletedTask
        };

    private async Task CompleteArrivedAsync(
        WorkflowNodeExecutionWorkItem workItem,
        FieldNavigationAcceptanceResponse acceptance,
        CancellationToken cancellationToken)
    {
        var isFinalPhysicalMove = false;
        try
        {
            isFinalPhysicalMove = await IsFinalPhysicalMoveAsync(workItem, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(
                exception,
                "Unable to determine whether workflow Move {NodeExecutionId} is the final physical node; control will remain unchanged.",
                workItem.NodeExecution.Id);
        }

        if (isFinalPhysicalMove)
        {
            // Release before marking the run Completed. The active-run gate
            // therefore prevents a new physical batch from being admitted
            // while cleanup is in progress. A lost response is never replayed.
            await TryReleaseBatchControlOnceAsync(workItem, "completed its final Move node");
        }

        await CompleteAsync(
            workItem,
            WorkflowStepCompletionOutcome.Succeeded,
            null,
            acceptance,
            cancellationToken);
    }

    private async Task CompleteCancelledAsync(
        WorkflowNodeExecutionWorkItem workItem,
        FieldNavigationAcceptanceResponse acceptance,
        CancellationToken cancellationToken)
    {
        // A cancelled one-click batch is terminal just like a completed final
        // Move. Release the control lease once before persisting the terminal
        // run state so a later batch cannot inherit it. Legacy/manual
        // acceptances do not carry run-level PhysicalAuthorization and retain
        // their existing operator-managed ownership behavior.
        var run = await workflows.GetExecutionAsync(
            workItem.NodeExecution.WorkflowRunId,
            cancellationToken);
        if (run?.PhysicalAuthorization is not null)
            await TryReleaseBatchControlOnceAsync(workItem, "was cancelled");

        await CompleteAsync(
            workItem,
            WorkflowStepCompletionOutcome.Cancelled,
            acceptance.LastError,
            acceptance,
            cancellationToken);
    }

    private async Task TryReleaseBatchControlOnceAsync(
        WorkflowNodeExecutionWorkItem workItem,
        string terminalContext)
    {
        if (_agv is not IPhysicalAgvControlGateway control) return;

        var timeout = options.ControlReleaseTimeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(10)
            : options.ControlReleaseTimeout;
        using var cleanup = new CancellationTokenSource(timeout);
        try
        {
            var released = await control.ReleaseControlAsync(cleanup.Token);
            if (released)
            {
                _logger?.LogInformation(
                    "Released Adapter AGV control before workflow run {WorkflowRunId} {TerminalContext}.",
                    workItem.NodeExecution.WorkflowRunId,
                    terminalContext);
            }
            else
            {
                _logger?.LogWarning(
                    "Workflow run {WorkflowRunId} {TerminalContext}, but Adapter control was not owned at cleanup time; no release command was sent.",
                    workItem.NodeExecution.WorkflowRunId,
                    terminalContext);
            }
        }
        catch (Exception exception)
        {
            // A lost response after POST is ambiguous. Never retry release;
            // the next read-only preflight must expose the owner.
            _logger?.LogWarning(
                exception,
                "Workflow run {WorkflowRunId} {TerminalContext}, but AGV control release could not be confirmed. Recheck ownership without replaying the request.",
                workItem.NodeExecution.WorkflowRunId,
                terminalContext);
        }
    }

    private async Task<bool> IsFinalPhysicalMoveAsync(
        WorkflowNodeExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        var run = await workflows.GetExecutionAsync(
            workItem.NodeExecution.WorkflowRunId,
            cancellationToken);
        if (run?.PhysicalAuthorization is null) return false;

        var version = await workflows.GetVersionAsync(
            run.WorkflowId,
            run.Version,
            cancellationToken);
        var current = version?.Definition.Nodes.SingleOrDefault(
            node => node.Id == workItem.NodeExecution.NodeId);
        if (current?.NextNodeIds is not { Count: 1 } nextIds) return false;
        return version!.Definition.Nodes.Any(node =>
            node.Id == nextIds[0] && node.Type == WorkflowNodeType.End);
    }

    private Task RecordProgressAsync(
        WorkflowNodeExecutionWorkItem workItem,
        WorkflowDeviceOperationStatus status,
        string? error,
        CancellationToken cancellationToken) =>
        workItem.DeviceOperation is not { } operation
            ? Task.CompletedTask
            : workflows.RecordDeviceOperationProgressAsync(
                workItem.NodeExecution.Id,
                operation.OperationId,
                status,
                error,
                cancellationToken);

    private Task CompleteWithEntityAsync(
        WorkflowNodeExecutionWorkItem workItem,
        WorkflowStepCompletionOutcome outcome,
        string? error,
        FieldNavigationAcceptance? acceptance,
        CancellationToken cancellationToken) =>
        CompleteAsync(
            workItem,
            outcome,
            error,
            acceptance is null ? null : ToResponse(acceptance),
            cancellationToken);

    private Task<WorkflowExecutionSnapshot> CompleteAsync(
        WorkflowNodeExecutionWorkItem workItem,
        WorkflowStepCompletionOutcome outcome,
        string? error,
        FieldNavigationAcceptanceResponse? acceptance,
        CancellationToken cancellationToken)
    {
        var outputs = new Dictionary<string, string?>
        {
            ["acceptanceId"] = acceptance?.Id.ToString(),
            ["permitId"] = acceptance?.PermitId,
            ["deviceTaskId"] = acceptance?.DeviceTaskId,
            ["stationId"] = acceptance?.TargetStationId,
            ["arrivedAtUtc"] = outcome == WorkflowStepCompletionOutcome.Succeeded
                ? _timeProvider.GetUtcNow().ToString("O")
                : null
        };
        return workflows.CompleteNodeExecutionAsync(
            workItem.NodeExecution.Id,
            new WorkflowNodeExecutionCompletionRequest
            {
                DeviceOperationId = workItem.DeviceOperation?.OperationId,
                Outcome = outcome,
                Error = error,
                Outputs = outputs
            },
            cancellationToken);
    }

    private static FieldNavigationAcceptanceResponse ToResponse(FieldNavigationAcceptance acceptance) => new(
        acceptance.Id,
        acceptance.Status,
        acceptance.AgvId,
        acceptance.SourceStationId,
        acceptance.TargetStationId,
        acceptance.MapName,
        acceptance.MapMd5,
        System.Text.Json.JsonSerializer.Deserialize<List<string>>(acceptance.PlannedPathJson) ?? [],
        acceptance.Description,
        acceptance.OperatorName,
        acceptance.SafetyObserverName,
        acceptance.PermitId,
        acceptance.AuthorizedAtUtc,
        acceptance.ExpiresAtUtc,
        acceptance.PermitConsumedAtUtc,
        acceptance.DeviceTaskId,
        acceptance.LastError,
        acceptance.CreatedAtUtc,
        acceptance.UpdatedAtUtc)
    {
        WorkflowRunId = acceptance.WorkflowRunId,
        WorkflowNodeExecutionId = acceptance.WorkflowNodeExecutionId,
        WorkflowDeviceOperationId = acceptance.WorkflowDeviceOperationId,
        DeviceEpoch = acceptance.DeviceEpoch,
        ReadinessSupervisorInstanceId = acceptance.ReadinessSupervisorInstanceId
    };
}

public sealed class WorkflowFieldNavigationWorker(
    IServiceScopeFactory scopeFactory,
    ProfileConfiguration profile,
    WorkflowFieldNavigationWorkerOptions options,
    TimeProvider timeProvider,
    WorkflowFieldNavigationRetryState retryState,
    ILogger<WorkflowFieldNavigationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || profile.Features.UseSimulator ||
            !profile.Features.EnableFieldNavigationAcceptance) return;

        var interval = options.PollInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(2)
            : options.PollInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dispatcher = new WorkflowFieldNavigationDispatcher(
                    scope.ServiceProvider.GetRequiredService<IWorkflowApplicationService>(),
                    scope.ServiceProvider.GetRequiredService<IFieldNavigationAcceptanceApplicationService>(),
                    scope.ServiceProvider.GetRequiredService<FieldNavigationAcceptanceRepository>(),
                    profile,
                    options,
                    timeProvider,
                    scope.ServiceProvider.GetRequiredService<IAgvGateway>(),
                    logger,
                    retryState,
                    scope.ServiceProvider.GetService<IPhysicalReadinessState>());
                await dispatcher.ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Field-navigation workflow worker stopped one polling cycle without replaying a write.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
