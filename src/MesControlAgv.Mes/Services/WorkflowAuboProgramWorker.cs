using System.Globalization;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Explicit opt-in for workflow robot-program execution.  It is disabled by
/// default and additionally requires a profile device with
/// <c>robot.execute-program</c> and <c>ControlEnabled=true</c>.
/// </summary>
public sealed class WorkflowAuboProgramWorkerOptions
{
    public bool Enabled { get; init; }
    public int PollIntervalMs { get; init; } = 1000;
    public int CompletionTimeoutMs { get; init; } = 60000;

    /// <summary>
    /// Read-only wait budget before a physical robot-program node is claimed.
    /// Zero preserves the legacy immediate-claim behavior; physical production
    /// profiles should opt in so transient offline/manual/safety states leave
    /// the node Ready and can recover without restarting the workflow.
    /// </summary>
    public int ReadinessRetryWindowMs { get; init; }

    /// <summary>Interval for read-only AUBO readiness observations.</summary>
    public int ReadinessRetryIntervalMs { get; init; } = 2000;

    /// <summary>Continuous healthy interval required before load/run is allowed.</summary>
    public int ReadyStabilityWindowMs { get; init; }

    /// <summary>
    /// Maximum continuous read-only status outage tolerated after runProgram
    /// has been confirmed Running. No mutating operation is replayed.
    /// </summary>
    public int StatusReadRetryWindowMs { get; init; } = 60000;

    /// <summary>Stable Stopped interval required before recording completion.</summary>
    public int TerminalStabilityWindowMs { get; init; }
}

/// <summary>
/// Single-flight dispatcher for <c>robot.execute-program</c> workflow nodes.
/// It loads a requested project only when necessary, starts it exactly once,
/// and records Unknown when a mutating outcome cannot be proven.  No mutation
/// is automatically replayed after a timeout or restart.
/// </summary>
public sealed class WorkflowAuboProgramDispatcher(
    IWorkflowApplicationService workflows,
    IAuboArmGateway arm,
    ProfileConfiguration profile,
    WorkflowAuboProgramWorkerOptions options,
    TimeProvider? timeProvider = null,
    ILogger? logger = null,
    IPhysicalReadinessState? physicalReadiness = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger? _logger = logger;
    private readonly IPhysicalReadinessState? _physicalReadiness = physicalReadiness;

    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        if (!CanUseProfile()) return;

        foreach (var workItem in await workflows.ListAuboProgramDispatchableNodesAsync(cancellationToken))
        {
            if (!await HasActiveBatchAuthorizationAsync(workItem, cancellationToken))
                continue;

            if (!await WaitForPhysicalReadinessAsync(workItem, cancellationToken))
                continue;

            var claimed = await workflows.ClaimNodeExecutionAsync(
                workItem.NodeExecution.Id,
                cancellationToken);
            await ExecuteClaimedAsync(claimed, cancellationToken);
        }
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (!CanUseProfile()) return;
        await RecoverRunningAsync(cancellationToken);
    }

    private async Task<bool> HasActiveBatchAuthorizationAsync(
        WorkflowNodeExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        if (profile.Features.UseSimulator) return true;

        // Physical execution never falls back to the legacy/manual path. A
        // missing or disabled supervisor is a closed gate.
        if (_physicalReadiness is not { Enabled: true })
            return false;

        var request = await workflows.GetExecutionRequestAsync(
            workItem.NodeExecution.WorkflowRunId,
            cancellationToken);
        // Legacy/manual physical runs retain their existing per-node controls
        // only while the new supervisor is disabled. Once it is enabled, every
        // physical write must carry a current device-session epoch.
        if (request?.PhysicalAuthorization is not { } authorization)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        if (authorization.ExpiresAtUtc <= now)
        {
            _logger?.LogWarning(
                "Workflow AUBO node {NodeExecutionId} remains Ready because physical batch authorization expired at {ExpiresAtUtc}.",
                workItem.NodeExecution.Id,
                authorization.ExpiresAtUtc);
            return false;
        }

        var armId = ResolveArmId(workItem.NodeExecution.Inputs);
        return string.IsNullOrWhiteSpace(armId) ||
               await HasCurrentSupervisorEpochAsync(
                   workItem,
                   armId,
                   cancellationToken);
    }

    private bool CanUseProfile() =>
        options.Enabled &&
        profile.WorkflowDevices.Any(device =>
            device.Enabled &&
            device.ControlEnabled &&
            string.Equals(device.DeviceFamily, WorkflowDeviceFamilyIds.RobotArm, StringComparison.OrdinalIgnoreCase) &&
            device.CapabilityIds.Contains(
                WorkflowCapabilityIds.RobotExecuteProgram,
                StringComparer.OrdinalIgnoreCase));

    private async Task<bool> WaitForPhysicalReadinessAsync(
        WorkflowNodeExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        if (profile.Features.UseSimulator || options.ReadinessRetryWindowMs <= 0)
            return true;

        var armId = ResolveArmId(workItem.NodeExecution.Inputs);
        if (string.IsNullOrWhiteSpace(armId))
            return true; // deterministic configuration failure is handled after claim

        var deadline = _timeProvider.GetUtcNow().AddMilliseconds(options.ReadinessRetryWindowMs);
        var interval = TimeSpan.FromMilliseconds(Math.Max(100, options.ReadinessRetryIntervalMs));
        var stableWindow = TimeSpan.FromMilliseconds(Math.Max(0, options.ReadyStabilityWindowMs));
        DateTimeOffset? readySince = null;
        string? lastWarning = null;

        while (true)
        {
            if (!await HasCurrentSupervisorEpochAsync(workItem, armId, cancellationToken))
                return false;

            IReadOnlyList<string> blockers;
            try
            {
                var status = await arm.GetProgramAsync(armId, cancellationToken);
                blockers = GetPhysicalReadinessBlockers(status);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                blockers = [$"机械臂状态读取失败：{exception.Message}"];
            }

            var now = _timeProvider.GetUtcNow();
            if (blockers.Count == 0)
            {
                readySince ??= now;
                if (stableWindow <= TimeSpan.Zero || now - readySince >= stableWindow)
                    return true;
            }
            else
            {
                readySince = null;
                var warning = string.Join("; ", blockers);
                if (!string.Equals(lastWarning, warning, StringComparison.Ordinal))
                {
                    _logger?.LogWarning(
                        "Workflow AUBO node {NodeExecutionId} remains Ready while waiting for physical readiness: {Reasons}",
                        workItem.NodeExecution.Id,
                        warning);
                    lastWarning = warning;
                }
            }

            var remaining = deadline - now;
            if (remaining <= TimeSpan.Zero)
            {
                _logger?.LogWarning(
                    "Workflow AUBO node {NodeExecutionId} exhausted its read-only readiness window and remains Ready for a later worker cycle.",
                    workItem.NodeExecution.Id);
                return false;
            }

            await Task.Delay(remaining < interval ? remaining : interval, _timeProvider, cancellationToken);
        }
    }

    private static IReadOnlyList<string> GetPhysicalReadinessBlockers(AuboArmProgramStatusResponse status)
    {
        var blockers = new List<string>();
        if (!status.Online)
            blockers.Add("机械臂离线");
        if (!status.ControlEnabled)
            blockers.Add("机械臂程序控制未启用");
        if (status.RobotMode != AuboArmMode.Running)
            blockers.Add($"机械臂模式为 {status.RobotMode}，需要 Running");
        if (status.SafetyMode != AuboArmSafetyMode.Normal)
            blockers.Add($"机械臂安全模式为 {status.SafetyMode}，需要 Normal");
        if (status.OperationalMode != AuboArmOperationalMode.Automatic)
            blockers.Add($"机械臂运行模式为 {status.OperationalMode}，需要 Automatic");
        if (status.RuntimeState != AuboArmRuntimeState.Stopped)
            blockers.Add($"机械臂解释器为 {status.RuntimeStatus ?? status.RuntimeState.ToString()}，需要 Stopped");
        return blockers;
    }

    private async Task ExecuteClaimedAsync(
        WorkflowNodeExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        var node = workItem.NodeExecution;
        var operation = workItem.DeviceOperation;
        if (operation is null)
        {
            await CompleteAsync(
                workItem,
                WorkflowStepCompletionOutcome.Failed,
                "A robot-program node was claimed without a durable device operation.",
                cancellationToken);
            return;
        }

        if (!TryReadInput(node.Inputs, WorkflowNodeConfigurationKeys.ProgramName, out var programName))
        {
            await CompleteAsync(
                workItem,
                WorkflowStepCompletionOutcome.Failed,
                "The robot-program node has no programName input.",
                cancellationToken);
            return;
        }

        var armId = ResolveArmId(node.Inputs);
        if (string.IsNullOrWhiteSpace(armId))
        {
            await CompleteAsync(
                workItem,
                WorkflowStepCompletionOutcome.Failed,
                "The robot-program node has no enabled robot-arm deviceId.",
                cancellationToken);
            return;
        }

        var normalizedProgram = NormalizeProgramName(programName);
        var mutationMayHaveStarted = false;
        try
        {
            var operationId = operation.OperationId;
            var correlation = CreateArmCorrelation(node, operation);
            if (correlation is null)
            {
                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    "The robot-program device operation has incomplete workflow correlation; no AUBO write was attempted.",
                    cancellationToken);
                return;
            }

            var before = await arm.GetProgramAsync(armId, cancellationToken);
            if (!before.Online)
            {
                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Failed,
                    "The AUBO controller is offline before the program operation.",
                    cancellationToken);
                return;
            }

            if (!string.Equals(NormalizeLoadedProgram(before.LoadedProgram), normalizedProgram, StringComparison.Ordinal))
            {
                if (!await HasActiveBatchAuthorizationAsync(workItem, cancellationToken))
                {
                    await CompleteAsync(
                        workItem,
                        WorkflowStepCompletionOutcome.Unknown,
                        "Physical readiness or authorization changed immediately before AUBO load; no AUBO write was attempted.",
                        cancellationToken);
                    return;
                }

                // Once the call is entered, a lost response is ambiguous.  Do
                // not retry or issue a second load automatically.
                mutationMayHaveStarted = true;
                var loaded = await arm.LoadProgramAsync(
                    armId,
                    normalizedProgram,
                    "workflow-runtime",
                    operationId,
                    correlation,
                    cancellationToken);
                if (!IsCorrelationCompatible(loaded, correlation))
                {
                    await CompleteAsync(
                        workItem,
                        WorkflowStepCompletionOutcome.Unknown,
                        "The AUBO load response did not preserve the durable workflow correlation; reconcile manually.",
                        cancellationToken);
                    return;
                }
                if (loaded.State == AuboArmProgramOperationState.Unknown || !loaded.Succeeded)
                {
                    await CompleteAsync(
                        workItem,
                        loaded.State == AuboArmProgramOperationState.Unknown
                            ? WorkflowStepCompletionOutcome.Unknown
                            : WorkflowStepCompletionOutcome.Failed,
                        loaded.ErrorMessage ?? "The AUBO program load did not succeed.",
                        cancellationToken);
                    return;
                }
            }

            if (!await HasActiveBatchAuthorizationAsync(workItem, cancellationToken))
            {
                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    mutationMayHaveStarted
                        ? "Physical readiness or authorization changed after AUBO load; run was not attempted and manual reconciliation is required."
                        : "Physical readiness or authorization changed immediately before AUBO run; no AUBO write was attempted.",
                    cancellationToken);
                return;
            }

            mutationMayHaveStarted = true;
            var started = await arm.RunProgramAsync(
                armId,
                normalizedProgram,
                "workflow-runtime",
                operationId,
                correlation,
                cancellationToken);
            if (!IsCorrelationCompatible(started, correlation))
            {
                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    "The AUBO run response did not preserve the durable workflow correlation; reconcile manually.",
                    cancellationToken);
                return;
            }
            if (started.State == AuboArmProgramOperationState.Unknown || !started.Succeeded)
            {
                await CompleteAsync(
                    workItem,
                    started.State == AuboArmProgramOperationState.Unknown
                        ? WorkflowStepCompletionOutcome.Unknown
                        : WorkflowStepCompletionOutcome.Failed,
                    started.ErrorMessage ?? "The AUBO program start did not succeed.",
                    cancellationToken);
                return;
            }

            await workflows.RecordDeviceOperationProgressAsync(
                node.Id,
                operation.OperationId,
                WorkflowDeviceOperationStatus.Running,
                null,
                cancellationToken);
            await ObserveUntilTerminalAsync(
                workItem,
                armId,
                normalizedProgram,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (mutationMayHaveStarted)
            {
                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    "The workflow was cancelled after a possible AUBO write; reconcile manually.",
                    CancellationToken.None);
                return;
            }

            throw;
        }
        catch (Exception exception)
        {
            await CompleteAsync(
                workItem,
                mutationMayHaveStarted
                    ? WorkflowStepCompletionOutcome.Unknown
                    : WorkflowStepCompletionOutcome.Failed,
                exception.Message,
                cancellationToken);
        }
    }

    private async Task ObserveUntilTerminalAsync(
        WorkflowNodeExecutionWorkItem workItem,
        string armId,
        string programName,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow().AddMilliseconds(options.CompletionTimeoutMs);
        var pollInterval = TimeSpan.FromMilliseconds(Math.Max(50, options.PollIntervalMs));
        var readRetryWindow = TimeSpan.FromMilliseconds(Math.Max(0, options.StatusReadRetryWindowMs));
        var terminalStabilityWindow = TimeSpan.FromMilliseconds(Math.Max(0, options.TerminalStabilityWindowMs));
        DateTimeOffset? unavailableSince = null;
        DateTimeOffset? stoppedSince = null;
        var completionBecameAmbiguous = false;
        string? lastObservationWarning = null;

        while (_timeProvider.GetUtcNow() < deadline)
        {
            if (!await HasCurrentSupervisorEpochAsync(workItem, armId, cancellationToken))
            {
                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    "The AUBO physical-session epoch changed after run was confirmed; no command was replayed and manual reconciliation is required.",
                    cancellationToken);
                return;
            }

            AuboArmProgramStatusResponse status;
            try
            {
                status = await arm.GetProgramAsync(armId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var now = _timeProvider.GetUtcNow();
                unavailableSince ??= now;
                lastObservationWarning = exception.Message;
                _logger?.LogWarning(
                    exception,
                    "AUBO status observation for workflow node {NodeExecutionId} failed; retrying read-only status without replaying load/run.",
                    workItem.NodeExecution.Id);
                if (readRetryWindow <= TimeSpan.Zero || now - unavailableSince >= readRetryWindow)
                {
                    await CompleteAsync(
                        workItem,
                        WorkflowStepCompletionOutcome.Unknown,
                        $"AUBO status remained unavailable after run was confirmed; no command was replayed: {lastObservationWarning}",
                        cancellationToken);
                    return;
                }

                await Task.Delay(pollInterval, _timeProvider, cancellationToken);
                continue;
            }

            var observedAt = _timeProvider.GetUtcNow();
            if (!status.Online || status.RuntimeState == AuboArmRuntimeState.Unknown)
            {
                unavailableSince ??= observedAt;
                lastObservationWarning = !status.Online
                    ? "机械臂离线"
                    : "机械臂运行状态未知";
                if (readRetryWindow <= TimeSpan.Zero || observedAt - unavailableSince >= readRetryWindow)
                {
                    await CompleteAsync(
                        workItem,
                        WorkflowStepCompletionOutcome.Unknown,
                        $"AUBO status remained unavailable after run was confirmed; no command was replayed: {lastObservationWarning}",
                        cancellationToken);
                    return;
                }

                await Task.Delay(pollInterval, _timeProvider, cancellationToken);
                continue;
            }

            unavailableSince = null;
            lastObservationWarning = null;

            var controllerSafe = profile.Features.UseSimulator ||
                                 (status.RobotMode == AuboArmMode.Running &&
                                  status.SafetyMode == AuboArmSafetyMode.Normal &&
                                  status.OperationalMode == AuboArmOperationalMode.Automatic &&
                                  status.ControlEnabled);

            if (status.RuntimeState == AuboArmRuntimeState.Running && controllerSafe)
            {
                // A paused/protective state that genuinely resumed to Running
                // is no longer ambiguous; continue observing the same program.
                completionBecameAmbiguous = false;
                stoppedSince = null;
            }
            else if (status.RuntimeState != AuboArmRuntimeState.Stopped || !controllerSafe)
            {
                completionBecameAmbiguous = true;
                stoppedSince = null;
            }

            if (status.RuntimeState == AuboArmRuntimeState.Stopped)
            {
                if (!string.Equals(
                        NormalizeLoadedProgram(status.LoadedProgram),
                        programName,
                        StringComparison.Ordinal))
                {
                    await CompleteAsync(
                        workItem,
                        WorkflowStepCompletionOutcome.Unknown,
                        $"AUBO stopped with loaded project '{status.LoadedProgram ?? "none"}', expected '{programName}'.",
                        cancellationToken);
                    return;
                }

                if (!controllerSafe || completionBecameAmbiguous)
                {
                    await CompleteAsync(
                        workItem,
                        WorkflowStepCompletionOutcome.Unknown,
                        "AUBO stopped after a paused, safety, mode, or control transition; program completion requires manual reconciliation.",
                        cancellationToken);
                    return;
                }

                stoppedSince ??= observedAt;
                if (terminalStabilityWindow > TimeSpan.Zero &&
                    observedAt - stoppedSince < terminalStabilityWindow)
                {
                    await Task.Delay(pollInterval, _timeProvider, cancellationToken);
                    continue;
                }

                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Succeeded,
                    null,
                    cancellationToken,
                    new Dictionary<string, string?>
                    {
                        [WorkflowNodeConfigurationKeys.DeviceId] = armId,
                        [WorkflowNodeConfigurationKeys.ProgramName] = programName,
                        ["runtime"] = status.RuntimeStatus ?? status.RuntimeState.ToString(),
                        ["completedAtUtc"] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)
                    });
                return;
            }

            await Task.Delay(pollInterval, _timeProvider, cancellationToken);
        }

        await CompleteAsync(
            workItem,
            WorkflowStepCompletionOutcome.Unknown,
            "The AUBO program did not reach a terminal state before the observation timeout.",
            cancellationToken);
    }

    private async Task RecoverRunningAsync(CancellationToken cancellationToken)
    {
        foreach (var workItem in await workflows.ListAuboProgramRecoverableNodesAsync(cancellationToken))
        {
            var armId = ResolveArmId(workItem.NodeExecution.Inputs);
            if (string.IsNullOrWhiteSpace(armId))
            {
                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    "A running robot-program node has no recoverable device id.",
                    cancellationToken);
                continue;
            }

            try
            {
                if (!await HasCurrentSupervisorEpochAsync(workItem, armId, cancellationToken))
                {
                    await CompleteAsync(
                        workItem,
                        WorkflowStepCompletionOutcome.Unknown,
                        "The AUBO physical-session epoch changed before restart reconciliation; no command was replayed.",
                        cancellationToken);
                    continue;
                }

                var status = await arm.GetProgramAsync(armId, cancellationToken);
                if (!profile.Features.UseSimulator)
                {
                    // Restart destroys proof of which side of a prior write
                    // boundary the process crossed. Status is evidence for
                    // manual reconciliation only; never replay a command.
                    await CompleteAsync(
                        workItem,
                        WorkflowStepCompletionOutcome.Unknown,
                        $"The AUBO controller reported {status.RuntimeStatus ?? status.RuntimeState.ToString()} after restart; physical completion requires manual reconciliation and no command was replayed.",
                        cancellationToken,
                        new Dictionary<string, string?>
                        {
                            [WorkflowNodeConfigurationKeys.DeviceId] = armId,
                            [WorkflowNodeConfigurationKeys.ProgramName] = ReadInputOrNull(
                                workItem.NodeExecution.Inputs,
                                WorkflowNodeConfigurationKeys.ProgramName),
                            ["runtime"] = status.RuntimeStatus ?? status.RuntimeState.ToString(),
                            ["loadedProgram"] = status.LoadedProgram,
                            ["reconciledAtUtc"] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)
                        });
                    continue;
                }
                if (status.RuntimeState == AuboArmRuntimeState.Stopped)
                {
                    // A stopped controller after a process restart is not
                    // proof that this workflow program completed. It may have
                    // been stopped manually, interrupted by a safety event,
                    // or never started before the response was lost. Keep the
                    // run Unknown so an operator can reconcile it explicitly;
                    // never turn an ambiguous physical write into success.
                    await CompleteAsync(
                        workItem,
                        WorkflowStepCompletionOutcome.Unknown,
                        "The AUBO controller is stopped after restart; program completion cannot be proven and requires manual reconciliation.",
                        cancellationToken,
                        new Dictionary<string, string?>
                        {
                            [WorkflowNodeConfigurationKeys.DeviceId] = armId,
                            [WorkflowNodeConfigurationKeys.ProgramName] = ReadInputOrNull(
                                workItem.NodeExecution.Inputs,
                                WorkflowNodeConfigurationKeys.ProgramName),
                            ["runtime"] = status.RuntimeStatus ?? status.RuntimeState.ToString(),
                            ["loadedProgram"] = status.LoadedProgram,
                            ["reconciledAtUtc"] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)
                        });
                }
                else if (status.RuntimeState == AuboArmRuntimeState.Running)
                {
                    if (!TryReadInput(
                            workItem.NodeExecution.Inputs,
                            WorkflowNodeConfigurationKeys.ProgramName,
                            out var requestedProgram))
                    {
                        await CompleteAsync(
                            workItem,
                            WorkflowStepCompletionOutcome.Unknown,
                            "A running AUBO program has no recoverable programName input.",
                            cancellationToken);
                        continue;
                    }

                    var normalizedProgram = NormalizeProgramName(requestedProgram);
                    if (!string.Equals(
                            NormalizeLoadedProgram(status.LoadedProgram),
                            normalizedProgram,
                            StringComparison.Ordinal))
                    {
                        await CompleteAsync(
                            workItem,
                            WorkflowStepCompletionOutcome.Unknown,
                            $"AUBO is running project '{status.LoadedProgram ?? "none"}' after restart, expected '{normalizedProgram}'.",
                            cancellationToken);
                        continue;
                    }

                    if (workItem.DeviceOperation is { } operation)
                        await workflows.RecordDeviceOperationProgressAsync(
                            workItem.NodeExecution.Id,
                            operation.OperationId,
                            WorkflowDeviceOperationStatus.Running,
                            null,
                            cancellationToken);

                    // Seeing the requested project Running after restart is
                    // fresh controller evidence. Continue the same read-only
                    // terminal observation in this worker cycle; do not wait
                    // for a later cycle that might see only Stopped and lose
                    // the proof that the program resumed.
                    await ObserveUntilTerminalAsync(
                        workItem,
                        armId,
                        normalizedProgram,
                        cancellationToken);
                }
                else
                {
                    await CompleteAsync(
                        workItem,
                        WorkflowStepCompletionOutcome.Unknown,
                        "The AUBO runtime could not be reconciled after restart.",
                        cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    $"AUBO reconciliation failed: {exception.Message}",
                    cancellationToken);
            }
        }
    }

    private async Task<bool> HasCurrentSupervisorEpochAsync(
        WorkflowNodeExecutionWorkItem workItem,
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (_physicalReadiness is not { Enabled: true } readiness)
            return profile.Features.UseSimulator;

        var request = await workflows.GetExecutionRequestAsync(
            workItem.NodeExecution.WorkflowRunId,
            cancellationToken);
        var expectedEpoch = request?.PhysicalAuthorization?.GetDeviceEpoch(deviceId);
        if (readiness.IsCurrentAndReady(
                deviceId,
                expectedEpoch,
                request?.PhysicalAuthorization?.ReadinessSupervisorInstanceId,
                out var reason))
            return true;

        _logger?.LogWarning(
            "Workflow AUBO node {NodeExecutionId} is blocked by physical readiness for {DeviceId}: {Reason}.",
            workItem.NodeExecution.Id,
            deviceId,
            reason ?? PhysicalReadinessReasonCodes.EpochRequired);
        return false;
    }

    private Task CompleteAsync(
        WorkflowNodeExecutionWorkItem workItem,
        WorkflowStepCompletionOutcome outcome,
        string? error,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? outputs = null) =>
        CompleteWithCorrelationAsync(workItem, outcome, error, cancellationToken, outputs);

    private Task CompleteWithCorrelationAsync(
        WorkflowNodeExecutionWorkItem workItem,
        WorkflowStepCompletionOutcome outcome,
        string? error,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? outputs)
    {
        var evidence = new Dictionary<string, string?>(
            outputs ?? new Dictionary<string, string?>(),
            StringComparer.OrdinalIgnoreCase);
        if (workItem.DeviceOperation is { } operation &&
            string.Equals(operation.CapabilityId, WorkflowCapabilityIds.RobotExecuteProgram, StringComparison.OrdinalIgnoreCase))
        {
            evidence["deviceOperationId"] = operation.OperationId.ToString("D");
            evidence["workflowRunId"] = operation.WorkflowRunId.ToString("D");
            evidence["workflowNodeExecutionId"] = operation.NodeExecutionId.ToString("D");
            evidence["requestId"] = operation.RequestId.ToString("D");
            evidence["correlationId"] = operation.CorrelationId;
            evidence["attempt"] = operation.Attempt.ToString(CultureInfo.InvariantCulture);
        }

        return workflows.CompleteNodeExecutionAsync(
            workItem.NodeExecution.Id,
            new WorkflowNodeExecutionCompletionRequest
            {
                DeviceOperationId = workItem.DeviceOperation?.OperationId,
                Outcome = outcome,
                Error = error,
                Outputs = evidence
            },
            cancellationToken);
    }

    private string? ResolveArmId(IReadOnlyDictionary<string, string?> inputs)
    {
        if (TryReadInput(inputs, WorkflowNodeConfigurationKeys.DeviceId, out var configured))
        {
            // A node may name a device explicitly, but that does not grant it
            // control permission. Resolve it against the deployment profile so
            // a stale/foreign device id cannot bypass the profile gate.
            return profile.WorkflowDevices.Any(device =>
                       string.Equals(device.DeviceId, configured, StringComparison.OrdinalIgnoreCase) &&
                       device.Enabled &&
                       device.ControlEnabled &&
                       string.Equals(device.DeviceFamily, WorkflowDeviceFamilyIds.RobotArm, StringComparison.OrdinalIgnoreCase) &&
                       device.CapabilityIds.Contains(
                           WorkflowCapabilityIds.RobotExecuteProgram,
                           StringComparer.OrdinalIgnoreCase))
                ? configured
                : null;
        }

        return profile.WorkflowDevices
            .Where(device => device.Enabled && device.ControlEnabled)
            .Where(device => string.Equals(device.DeviceFamily, WorkflowDeviceFamilyIds.RobotArm, StringComparison.OrdinalIgnoreCase))
            .Where(device => device.CapabilityIds.Contains(
                WorkflowCapabilityIds.RobotExecuteProgram,
                StringComparer.OrdinalIgnoreCase))
            .Select(device => device.DeviceId)
            .FirstOrDefault();
    }

    private static AuboArmOperationCorrelation? CreateArmCorrelation(
        WorkflowNodeExecutionSnapshot node,
        WorkflowDeviceOperationSnapshot operation)
    {
        if (node.WorkflowRunId == Guid.Empty ||
            node.Id == Guid.Empty ||
            operation.OperationId == Guid.Empty ||
            operation.WorkflowRunId != node.WorkflowRunId ||
            operation.NodeExecutionId != node.Id ||
            operation.RequestId == Guid.Empty ||
            operation.Attempt <= 0)
        {
            return null;
        }

        // Legacy runs may not have persisted a correlation string. Derive a
        // deterministic value from the durable identifiers rather than issuing
        // an uncorrelated physical write or inventing a new operation id.
        var correlationId = string.IsNullOrWhiteSpace(operation.CorrelationId)
            ? $"workflow:{node.WorkflowRunId:N}:node:{node.Id:N}:operation:{operation.OperationId:N}"
            : operation.CorrelationId.Trim();
        return AuboArmOperationCorrelation.Create(
            node.WorkflowRunId,
            node.Id,
            operation.OperationId,
            operation.RequestId,
            correlationId,
            operation.Attempt);
    }

    private bool IsCorrelationCompatible(
        AuboArmProgramOperationResponse response,
        AuboArmOperationCorrelation expected)
    {
        if (response.OperationId != expected.DeviceOperationId)
        {
            _logger?.LogWarning(
                "AUBO response operation id {ResponseOperationId} did not match durable device operation {ExpectedOperationId}.",
                response.OperationId,
                expected.DeviceOperationId);
            return false;
        }

        if (!response.IsWorkflowCorrelated)
        {
            // Older Adapter binaries did not echo additive correlation fields.
            // The request was still sent with the durable id; retain compatibility
            // while making the downgrade visible in the worker log.
            _logger?.LogWarning(
                "AUBO response for durable operation {OperationId} omitted workflow correlation fields; verify the Adapter deployment before field use.",
                expected.DeviceOperationId);
            return true;
        }

        return response.WorkflowRunId == expected.WorkflowRunId &&
               response.WorkflowNodeExecutionId == expected.WorkflowNodeExecutionId &&
               response.DeviceOperationId == expected.DeviceOperationId &&
               response.RequestId == expected.RequestId &&
               string.Equals(
                   response.CorrelationId ?? expected.EffectiveCorrelationId,
                   expected.EffectiveCorrelationId,
                   StringComparison.Ordinal) &&
               response.Attempt == expected.Attempt;
    }

    private static bool TryReadInput(
        IReadOnlyDictionary<string, string?> inputs,
        string key,
        out string value)
    {
        value = string.Empty;
        if (!inputs.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return false;
        value = raw.Trim();
        return true;
    }

    private static string? ReadInputOrNull(IReadOnlyDictionary<string, string?> inputs, string key) =>
        TryReadInput(inputs, key, out var value) ? value : null;

    private static string NormalizeProgramName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.EndsWith(".pro", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];
        if (normalized.Length is 0 or > 128 || normalized.Any(char.IsControl) ||
            normalized.Contains('/') || normalized.Contains('\\') || normalized.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Program name must be a simple project name.", nameof(value));
        return normalized;
    }

    private static string? NormalizeLoadedProgram(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return NormalizeProgramName(value); }
        catch (ArgumentException) { return value.Trim(); }
    }
}

/// <summary>Low-rate hosted loop for the explicitly enabled AUBO workflow worker.</summary>
public sealed class WorkflowAuboProgramWorker(
    IServiceScopeFactory scopeFactory,
    ProfileConfiguration profile,
    WorkflowAuboProgramWorkerOptions options,
    TimeProvider timeProvider,
    ILogger<WorkflowAuboProgramWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;

        // Recovery is startup-only. Subsequent polls are the current process's
        // forward path and must not reinterpret an in-flight node as a restart.
        try
        {
            using var recoveryScope = scopeFactory.CreateScope();
            var recoveryDispatcher = new WorkflowAuboProgramDispatcher(
                recoveryScope.ServiceProvider.GetRequiredService<IWorkflowApplicationService>(),
                recoveryScope.ServiceProvider.GetRequiredService<IAuboArmGateway>(),
                profile,
                options,
                timeProvider,
                logger,
                recoveryScope.ServiceProvider.GetService<IPhysicalReadinessState>());
            await recoveryDispatcher.RecoverAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "AUBO workflow startup recovery failed; no recovery mutation was replayed.");
        }

        var interval = TimeSpan.FromMilliseconds(Math.Max(250, options.PollIntervalMs));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dispatcher = new WorkflowAuboProgramDispatcher(
                    scope.ServiceProvider.GetRequiredService<IWorkflowApplicationService>(),
                    scope.ServiceProvider.GetRequiredService<IAuboArmGateway>(),
                    profile,
                    options,
                    timeProvider,
                    logger,
                    scope.ServiceProvider.GetService<IPhysicalReadinessState>());
                await dispatcher.ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "AUBO workflow worker stopped one polling cycle without replaying a mutation.");
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
