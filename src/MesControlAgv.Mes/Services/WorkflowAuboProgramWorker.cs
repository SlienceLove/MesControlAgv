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
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        if (!CanUseProfile()) return;

        foreach (var workItem in await workflows.ListAuboProgramDispatchableNodesAsync(cancellationToken))
        {
            var claimed = await workflows.ClaimNodeExecutionAsync(
                workItem.NodeExecution.Id,
                cancellationToken);
            await ExecuteClaimedAsync(claimed, cancellationToken);
        }

        await RecoverRunningAsync(cancellationToken);
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (!CanUseProfile()) return;
        await RecoverRunningAsync(cancellationToken);
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
                // Once the call is entered, a lost response is ambiguous.  Do
                // not retry or issue a second load automatically.
                mutationMayHaveStarted = true;
                var loaded = await arm.LoadProgramAsync(
                    armId,
                    normalizedProgram,
                    "workflow-runtime",
                    Guid.NewGuid(),
                    cancellationToken);
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

            mutationMayHaveStarted = true;
            var started = await arm.RunProgramAsync(
                armId,
                normalizedProgram,
                "workflow-runtime",
                Guid.NewGuid(),
                cancellationToken);
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
        while (_timeProvider.GetUtcNow() < deadline)
        {
            var status = await arm.GetProgramAsync(armId, cancellationToken);
            if (status.RuntimeState == AuboArmRuntimeState.Stopped)
            {
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

            if (status.RuntimeState == AuboArmRuntimeState.Unknown)
            {
                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    "The AUBO runtime state became unknown; do not retry automatically.",
                    cancellationToken);
                return;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(Math.Max(50, options.PollIntervalMs)),
                _timeProvider,
                cancellationToken);
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
                var status = await arm.GetProgramAsync(armId, cancellationToken);
                if (status.RuntimeState == AuboArmRuntimeState.Stopped)
                {
                    await CompleteAsync(
                        workItem,
                        WorkflowStepCompletionOutcome.Succeeded,
                        null,
                        cancellationToken,
                        new Dictionary<string, string?>
                        {
                            [WorkflowNodeConfigurationKeys.DeviceId] = armId,
                            [WorkflowNodeConfigurationKeys.ProgramName] = ReadInputOrNull(
                                workItem.NodeExecution.Inputs,
                                WorkflowNodeConfigurationKeys.ProgramName),
                            ["runtime"] = status.RuntimeStatus ?? status.RuntimeState.ToString(),
                            ["reconciledAtUtc"] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)
                        });
                }
                else if (status.RuntimeState == AuboArmRuntimeState.Running)
                {
                    if (workItem.DeviceOperation is { } operation)
                    {
                        await workflows.RecordDeviceOperationProgressAsync(
                            workItem.NodeExecution.Id,
                            operation.OperationId,
                            WorkflowDeviceOperationStatus.Running,
                            null,
                            cancellationToken);
                    }
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

    private Task CompleteAsync(
        WorkflowNodeExecutionWorkItem workItem,
        WorkflowStepCompletionOutcome outcome,
        string? error,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? outputs = null) =>
        workflows.CompleteNodeExecutionAsync(
            workItem.NodeExecution.Id,
            new WorkflowNodeExecutionCompletionRequest
            {
                DeviceOperationId = workItem.DeviceOperation?.OperationId,
                Outcome = outcome,
                Error = error,
                Outputs = outputs ?? new Dictionary<string, string?>()
            },
            cancellationToken);

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
                    timeProvider);
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
