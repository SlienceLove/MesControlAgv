using System.Globalization;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Devices;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Mes.Services;

/// <summary>Options for starting one task that already exists in the vendor workstation.</summary>
public sealed class WorkflowSampleWorkstationWorkerOptions
{
    public const string SectionName = "WorkflowSampleWorkstationWorker";

    public bool Enabled { get; init; }
    public int PollIntervalMs { get; init; } = 1000;
    public int ReadinessRetryIntervalMs { get; init; } = 2000;
    public int StartObservationTimeoutMs { get; init; } = 30000;
    public int CompletionTimeoutMs { get; init; } = 600000;
}

/// <summary>
/// Claims a workstation node only after read-only readiness checks, sends one
/// StartTask request, and then observes state without replaying the command.
/// </summary>
public sealed class WorkflowSampleWorkstationDispatcher(
    IWorkflowApplicationService workflows,
    ISampleWorkstationReader reader,
    ISampleWorkstationCommands commands,
    ProfileConfiguration profile,
    WorkflowSampleWorkstationWorkerOptions options,
    TimeProvider? timeProvider = null,
    ILogger? logger = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger? _logger = logger;

    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        if (!CanUseProfile()) return;

        foreach (var workItem in await workflows.ListSampleWorkstationDispatchableNodesAsync(cancellationToken))
        {
            if (!TryResolveInputs(workItem, out var deviceId, out var taskNo, out var inputError))
            {
                var claimedInvalid = await workflows.ClaimNodeExecutionAsync(
                    workItem.NodeExecution.Id,
                    cancellationToken);
                await CompleteAsync(
                    claimedInvalid,
                    WorkflowStepCompletionOutcome.Failed,
                    inputError,
                    cancellationToken);
                continue;
            }

            var readiness = await TryReadObservationAsync(deviceId, taskNo, cancellationToken);
            if (readiness is null || !IsReady(readiness))
                continue;

            var claimed = await workflows.ClaimNodeExecutionAsync(
                workItem.NodeExecution.Id,
                cancellationToken);
            await ExecuteClaimedAsync(claimed, deviceId, taskNo, cancellationToken);
        }
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (!CanUseProfile()) return;

        foreach (var workItem in await workflows.ListSampleWorkstationRecoverableNodesAsync(cancellationToken))
        {
            if (!TryResolveInputs(workItem, out var deviceId, out var taskNo, out var inputError) ||
                workItem.DeviceOperation is not { } operation)
            {
                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    inputError ?? "The workstation operation has incomplete durable identity after restart.",
                    cancellationToken,
                    unknownReason: UnknownReason.ManualReconciliationRequired);
                continue;
            }

            if (operation.Status != WorkflowDeviceOperationStatus.Running)
            {
                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    "The workstation start was accepted, but this MES instance did not persist Running evidence before restart; no command was replayed.",
                    cancellationToken,
                    unknownReason: UnknownReason.MissingResult);
                continue;
            }

            await ObserveRunningUntilTerminalAsync(
                workItem,
                deviceId,
                taskNo,
                operation.UpdatedAt,
                cancellationToken);
        }
    }

    private bool CanUseProfile() =>
        options.Enabled &&
        profile.WorkflowDevices.Any(device =>
            device.Enabled &&
            device.ControlEnabled &&
            string.Equals(
                device.DeviceFamily,
                WorkflowDeviceFamilyIds.SampleWorkstation,
                StringComparison.OrdinalIgnoreCase) &&
            device.CapabilityIds.Contains(
                WorkflowCapabilityIds.SampleWorkstationStartExistingTask,
                StringComparer.OrdinalIgnoreCase));

    private async Task ExecuteClaimedAsync(
        WorkflowNodeExecutionWorkItem workItem,
        string deviceId,
        string taskNo,
        CancellationToken cancellationToken)
    {
        if (workItem.DeviceOperation is not { } operation)
        {
            await CompleteAsync(
                workItem,
                WorkflowStepCompletionOutcome.Failed,
                "The workstation node was claimed without a durable device operation.",
                cancellationToken);
            return;
        }

        var startAttempted = false;
        try
        {
            startAttempted = true;
            var response = await commands.StartTaskAsync(deviceId, taskNo, cancellationToken);
            if (!IsMatchingStartResponse(response, deviceId, taskNo))
            {
                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    "The workstation start response did not match the requested device and task; no command was replayed.",
                    cancellationToken,
                    taskNo,
                    UnknownReason.IdentityMismatch);
                return;
            }
            if (!response.Acknowledged)
            {
                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Failed,
                    $"The workstation explicitly rejected task '{taskNo}' with code {response.Code}.",
                    cancellationToken,
                    taskNo);
                return;
            }

            await workflows.RecordDeviceOperationProgressAsync(
                workItem.NodeExecution.Id,
                operation.OperationId,
                WorkflowDeviceOperationStatus.Accepted,
                null,
                cancellationToken);
            await ObserveStartAndCompletionAsync(workItem, deviceId, taskNo, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!startAttempted) throw;
            await CompleteAsync(
                workItem,
                WorkflowStepCompletionOutcome.Unknown,
                "The workflow was cancelled after the workstation start call began; no stop or retry command was sent.",
                CancellationToken.None,
                taskNo,
                UnknownReason.ManualReconciliationRequired);
        }
        catch (SampleWorkstationGatewayException exception)
        {
            await CompleteAsync(
                workItem,
                exception.OutcomeUnknown
                    ? WorkflowStepCompletionOutcome.Unknown
                    : WorkflowStepCompletionOutcome.Failed,
                exception.Message,
                cancellationToken,
                taskNo,
                exception.OutcomeUnknown ? UnknownReason.IncompleteResponse : null);
        }
        catch (Exception exception)
        {
            await CompleteAsync(
                workItem,
                startAttempted
                    ? WorkflowStepCompletionOutcome.Unknown
                    : WorkflowStepCompletionOutcome.Failed,
                exception.Message,
                cancellationToken,
                taskNo,
                startAttempted ? UnknownReason.ManualReconciliationRequired : null);
        }
    }

    private async Task ObserveStartAndCompletionAsync(
        WorkflowNodeExecutionWorkItem workItem,
        string deviceId,
        string taskNo,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow().AddMilliseconds(
            Math.Max(1, options.StartObservationTimeoutMs));
        var interval = TimeSpan.FromMilliseconds(Math.Max(1, options.PollIntervalMs));
        string? lastReadError = null;

        while (_timeProvider.GetUtcNow() < deadline)
        {
            WorkstationObservation? observation;
            try
            {
                observation = await ReadObservationAsync(deviceId, taskNo, cancellationToken);
                lastReadError = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastReadError = exception.Message;
                _logger?.LogWarning(
                    exception,
                    "Workstation start observation failed for task {TaskNo}; retrying reads only.",
                    taskNo);
                await Task.Delay(interval, _timeProvider, cancellationToken);
                continue;
            }

            if (!HasMatchingIdentity(observation, deviceId, taskNo))
            {
                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    "The workstation observation identity did not match this node.",
                    cancellationToken,
                    taskNo,
                    UnknownReason.IdentityMismatch);
                return;
            }
            if (HasUnknownOrAbnormalTerminal(observation, out var abnormalReason))
            {
                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    abnormalReason,
                    cancellationToken,
                    taskNo,
                    UnknownReason.ManualReconciliationRequired);
                return;
            }
            if (HasRunningEvidence(observation))
            {
                var runningObservedAt = _timeProvider.GetUtcNow();
                await workflows.RecordDeviceOperationProgressAsync(
                    workItem.NodeExecution.Id,
                    workItem.DeviceOperation!.OperationId,
                    WorkflowDeviceOperationStatus.Running,
                    null,
                    cancellationToken);
                await ObserveRunningUntilTerminalAsync(
                    workItem,
                    deviceId,
                    taskNo,
                    runningObservedAt,
                    cancellationToken);
                return;
            }

            await Task.Delay(interval, _timeProvider, cancellationToken);
        }

        await CompleteAsync(
            workItem,
            WorkflowStepCompletionOutcome.Unknown,
            lastReadError is null
                ? "No Running evidence was observed within the workstation start window; an old Completed state was not accepted."
                : $"No Running evidence was observed because workstation reads remained unavailable: {lastReadError}",
            cancellationToken,
            taskNo,
            UnknownReason.MissingResult);
    }

    private async Task ObserveRunningUntilTerminalAsync(
        WorkflowNodeExecutionWorkItem workItem,
        string deviceId,
        string taskNo,
        DateTimeOffset runningObservedAt,
        CancellationToken cancellationToken)
    {
        var deadline = runningObservedAt.AddMilliseconds(Math.Max(1, options.CompletionTimeoutMs));
        var interval = TimeSpan.FromMilliseconds(Math.Max(1, options.PollIntervalMs));
        string? lastReadError = null;

        while (_timeProvider.GetUtcNow() < deadline)
        {
            try
            {
                var observation = await ReadObservationAsync(deviceId, taskNo, cancellationToken);
                lastReadError = null;
                if (!HasMatchingIdentity(observation, deviceId, taskNo))
                {
                    await CompleteAsync(
                        workItem,
                        WorkflowStepCompletionOutcome.Unknown,
                        "The workstation completion observation identity did not match this node.",
                        cancellationToken,
                        taskNo,
                        UnknownReason.IdentityMismatch);
                    return;
                }
                if (HasUnknownOrAbnormalTerminal(observation, out var abnormalReason))
                {
                    await CompleteAsync(
                        workItem,
                        WorkflowStepCompletionOutcome.Unknown,
                        abnormalReason,
                        cancellationToken,
                        taskNo,
                        UnknownReason.ManualReconciliationRequired);
                    return;
                }
                if (IsCompleted(observation))
                {
                    var completedAt = _timeProvider.GetUtcNow();
                    await CompleteAsync(
                        workItem,
                        WorkflowStepCompletionOutcome.Succeeded,
                        null,
                        cancellationToken,
                        taskNo,
                        outputs: new Dictionary<string, string?>
                        {
                            ["deviceId"] = deviceId,
                            ["taskNo"] = taskNo,
                            ["taskState"] = observation.Task.RawState,
                            ["runningObservedAtUtc"] = runningObservedAt.ToString("O", CultureInfo.InvariantCulture),
                            ["completedAtUtc"] = completedAt.ToString("O", CultureInfo.InvariantCulture)
                        });
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastReadError = exception.Message;
                _logger?.LogWarning(
                    exception,
                    "Workstation completion observation failed for task {TaskNo}; retrying reads only.",
                    taskNo);
            }

            await Task.Delay(interval, _timeProvider, cancellationToken);
        }

        await CompleteAsync(
            workItem,
            WorkflowStepCompletionOutcome.Unknown,
            lastReadError is null
                ? "The workstation task did not reach a consistent Completed + Idle/0 + code 0 terminal state before timeout."
                : $"The workstation completion state remained unreadable before timeout: {lastReadError}",
            cancellationToken,
            taskNo,
            UnknownReason.Timeout);
    }

    private async Task<WorkstationObservation?> TryReadObservationAsync(
        string deviceId,
        string taskNo,
        CancellationToken cancellationToken)
    {
        try
        {
            var observation = await ReadObservationAsync(deviceId, taskNo, cancellationToken);
            return HasMatchingIdentity(observation, deviceId, taskNo) ? observation : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "Workstation readiness read failed for device {DeviceId}; node remains Ready.",
                deviceId);
            return null;
        }
    }

    private async Task<WorkstationObservation> ReadObservationAsync(
        string deviceId,
        string taskNo,
        CancellationToken cancellationToken)
    {
        var status = await reader.GetStatusAsync(deviceId, cancellationToken);
        var error = await reader.GetErrorsAsync(deviceId, cancellationToken);
        var task = await reader.GetTaskStateAsync(deviceId, taskNo, cancellationToken);
        return new WorkstationObservation(status, error, task);
    }

    private bool TryResolveInputs(
        WorkflowNodeExecutionWorkItem workItem,
        out string deviceId,
        out string taskNo,
        out string? error)
    {
        deviceId = ReadRequired(workItem.NodeExecution.Inputs, WorkflowNodeConfigurationKeys.DeviceId);
        taskNo = ReadRequired(workItem.NodeExecution.Inputs, WorkflowNodeConfigurationKeys.TaskNo);
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(taskNo))
        {
            error = "The workstation node requires configured deviceId and taskNo values.";
            return false;
        }

        var resolvedDeviceId = deviceId;
        var configured = profile.WorkflowDevices.Any(device =>
            string.Equals(device.DeviceId, resolvedDeviceId, StringComparison.OrdinalIgnoreCase) &&
            device.Enabled &&
            device.ControlEnabled &&
            string.Equals(
                device.DeviceFamily,
                WorkflowDeviceFamilyIds.SampleWorkstation,
                StringComparison.OrdinalIgnoreCase) &&
            device.CapabilityIds.Contains(
                WorkflowCapabilityIds.SampleWorkstationStartExistingTask,
                StringComparer.OrdinalIgnoreCase));
        error = configured
            ? null
            : $"Sample workstation '{deviceId}' is not enabled for existing-task workflow control.";
        return configured;
    }

    private async Task CompleteAsync(
        WorkflowNodeExecutionWorkItem workItem,
        WorkflowStepCompletionOutcome outcome,
        string? error,
        CancellationToken cancellationToken,
        string? taskNo = null,
        UnknownReason? unknownReason = null,
        IReadOnlyDictionary<string, string?>? outputs = null)
    {
        await workflows.CompleteNodeExecutionAsync(
            workItem.NodeExecution.Id,
            new WorkflowNodeExecutionCompletionRequest
            {
                DeviceOperationId = workItem.DeviceOperation?.OperationId,
                Outcome = outcome,
                Error = error,
                UnknownReason = outcome == WorkflowStepCompletionOutcome.Unknown
                    ? unknownReason ?? UnknownReason.ManualReconciliationRequired
                    : null,
                VendorTaskId = taskNo,
                Outputs = outputs ?? new Dictionary<string, string?>()
            },
            cancellationToken);
    }

    private static bool IsMatchingStartResponse(
        SampleWorkstationCommandResponse response,
        string deviceId,
        string taskNo) =>
        string.Equals(response.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
        response.Operation == SampleWorkstationCommandOperation.StartTask &&
        (string.IsNullOrWhiteSpace(response.TaskNo) ||
         string.Equals(response.TaskNo, taskNo, StringComparison.Ordinal));

    private static bool HasMatchingIdentity(
        WorkstationObservation observation,
        string deviceId,
        string taskNo) =>
        string.Equals(observation.Status.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(observation.Error.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(observation.Task.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(observation.Task.TaskNo, taskNo, StringComparison.Ordinal);

    private static bool IsReady(WorkstationObservation observation) =>
        observation.Status.Online &&
        observation.Status.State == SampleWorkstationDeviceState.Idle &&
        observation.Status.RawState == 0 &&
        observation.Error.ErrorCode == 0 &&
        observation.Error.Recognized &&
        observation.Task.State is not (SampleWorkstationTaskState.Running or SampleWorkstationTaskState.Unknown);

    private static bool HasRunningEvidence(WorkstationObservation observation) =>
        observation.Status.State == SampleWorkstationDeviceState.Running ||
        observation.Status.RawState == 1 ||
        observation.Error.ErrorCode == 3 ||
        observation.Task.State == SampleWorkstationTaskState.Running;

    private static bool IsCompleted(WorkstationObservation observation) =>
        observation.Task.State == SampleWorkstationTaskState.Completed &&
        observation.Status.Online &&
        observation.Status.State == SampleWorkstationDeviceState.Idle &&
        observation.Status.RawState == 0 &&
        observation.Error.ErrorCode == 0;

    private static bool HasUnknownOrAbnormalTerminal(
        WorkstationObservation observation,
        out string reason)
    {
        if (observation.Task.State == SampleWorkstationTaskState.Unknown)
        {
            reason = "The workstation reported an unknown task state.";
            return true;
        }
        if (observation.Error.ErrorCode is not (0 or 3))
        {
            reason = $"The workstation reported error/result code {observation.Error.ErrorCode?.ToString() ?? "null"}.";
            return true;
        }
        if (observation.Status.State is SampleWorkstationDeviceState.Faulted or
            SampleWorkstationDeviceState.Offline or SampleWorkstationDeviceState.Paused)
        {
            reason = $"The workstation entered '{observation.Status.State}' while the task outcome was not proven.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static string ReadRequired(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : string.Empty;

    private sealed record WorkstationObservation(
        SampleWorkstationStatusResponse Status,
        SampleWorkstationErrorResponse Error,
        SampleWorkstationTaskStateResponse Task);
}

/// <summary>Hosted polling loop; disabled until explicitly enabled by deployment configuration.</summary>
public sealed class WorkflowSampleWorkstationWorker(
    IServiceScopeFactory scopeFactory,
    ProfileConfiguration profile,
    WorkflowSampleWorkstationWorkerOptions options,
    TimeProvider timeProvider,
    ILogger<WorkflowSampleWorkstationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;

        try
        {
            using var recoveryScope = scopeFactory.CreateScope();
            await CreateDispatcher(recoveryScope).RecoverAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Sample workstation startup reconciliation failed; no command was replayed.");
        }

        var interval = TimeSpan.FromMilliseconds(Math.Max(100, options.ReadinessRetryIntervalMs));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await CreateDispatcher(scope).ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Sample workstation workflow cycle failed; no command was replayed automatically.");
            }

            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private WorkflowSampleWorkstationDispatcher CreateDispatcher(IServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<IWorkflowApplicationService>(),
        scope.ServiceProvider.GetRequiredService<ISampleWorkstationReader>(),
        scope.ServiceProvider.GetRequiredService<ISampleWorkstationCommands>(),
        profile,
        options,
        timeProvider,
        logger);
}
