using System.Globalization;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Devices;
using MesControlAgv.Contracts.Samples;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Mes.Services;

/// <summary>Configuration for the one-shot approved workstation workflow node.</summary>
public sealed class WorkflowLegacySampleWorkstationWorkerOptions
{
    public const string SectionName = "WorkflowLegacySampleWorkstationWorker";

    public bool Enabled { get; init; }
    public int PollIntervalMs { get; init; } = 1000;
    public int CompletionTimeoutMs { get; init; } = 300000;
    public int ReadinessRetryWindowMs { get; init; } = 300000;
    public int ReadinessRetryIntervalMs { get; init; } = 2000;
    public string OperatorName { get; init; } = "workflow-runtime";
    public Dictionary<string, SampleWorkstationTemplateOptions> Templates { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGetTemplate(string version, out SampleWorkstationTemplateOptions template)
    {
        var match = Templates.FirstOrDefault(item =>
            string.Equals(item.Key.Trim(), version.Trim(), StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(match.Key))
        {
            template = null!;
            return false;
        }

        template = match.Value with { TemplateVersion = match.Key.Trim() };
        return true;
    }
}

/// <summary>
/// Immutable, server-side-approved trajectory data.  WPF submits only the
/// template version; raw coordinates never cross the public MES command API.
/// </summary>
public sealed record SampleWorkstationTemplateOptions
{
    public string TemplateVersion { get; init; } = string.Empty;
    public int RecordNumber { get; init; } = 1;
    public string LiquidCode { get; init; } = string.Empty;
    public string LiquidName { get; init; } = string.Empty;
    public string WarehouseLocation { get; init; } = string.Empty;
    public int WarehouseX { get; init; }
    public int WarehouseY { get; init; }
    public string SourceBarCode { get; init; } = "{barcode}";
    public int SourceX { get; init; }
    public int SourceY { get; init; }
    public string TargetBarCode { get; init; } = "{sampleId}";
    public int TargetX { get; init; }
    public int TargetY { get; init; }
    public int TransferVolume { get; init; }

    public bool IsValid(out string error)
    {
        var required = new (string Name, string? Value)[]
        {
            (nameof(TemplateVersion), TemplateVersion),
            (nameof(LiquidCode), LiquidCode),
            (nameof(LiquidName), LiquidName),
            (nameof(WarehouseLocation), WarehouseLocation),
            (nameof(SourceBarCode), SourceBarCode),
            (nameof(TargetBarCode), TargetBarCode)
        };
        var missing = required.FirstOrDefault(item => string.IsNullOrWhiteSpace(item.Value));
        if (!string.IsNullOrWhiteSpace(missing.Name))
        {
            error = $"Approved workstation template field '{missing.Name}' is required.";
            return false;
        }
        if (RecordNumber < 1 || TransferVolume <= 0)
        {
            error = "Approved workstation template record number and transfer volume must be positive.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

/// <summary>
/// Executes a workstation node as one durable, single-flight operation.  The
/// four vendor writes are each derived from the parent operation id, so a
/// restart can reconcile a task but never generate a second physical write.
/// </summary>
public sealed class WorkflowLegacySampleWorkstationDispatcher(
    IWorkflowApplicationService workflows,
    ISampleWorkstationController controller,
    ISampleWorkstationReader reader,
    ProfileConfiguration profile,
    WorkflowLegacySampleWorkstationWorkerOptions options,
    IPhysicalReadinessState? physicalReadiness = null,
    SampleManagementService? sampleManagement = null,
    TimeProvider? timeProvider = null,
    ILogger? logger = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger? _logger = logger;
    private readonly IPhysicalReadinessState? _physicalReadiness = physicalReadiness;
    private readonly SampleManagementService? _sampleManagement = sampleManagement;

    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        if (!CanUseProfile()) return;
        foreach (var workItem in (await workflows.ListSampleWorkstationDispatchableNodesAsync(cancellationToken)).Where(item =>
                     string.Equals(item.NodeExecution.NodeTypeId, WorkflowGraphNodeTypeIds.SampleWorkstationExecuteTemplate, StringComparison.OrdinalIgnoreCase)))
        {
            if (!await HasActiveAuthorizationAsync(workItem, cancellationToken)) continue;
            var ready = await WaitForReadinessAsync(workItem, cancellationToken);
            if (ready is null) continue;

            var claimed = await workflows.TryClaimSampleWorkstationNodeExecutionAsync(workItem.NodeExecution.Id, cancellationToken);
            if (claimed is null) continue;
            await ExecuteClaimedAsync(claimed, ready.EquipmentNo, cancellationToken);
        }
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (!CanUseProfile()) return;
        foreach (var workItem in (await workflows.ListSampleWorkstationRecoverableNodesAsync(cancellationToken)).Where(item =>
                     string.Equals(item.NodeExecution.NodeTypeId, WorkflowGraphNodeTypeIds.SampleWorkstationExecuteTemplate, StringComparison.OrdinalIgnoreCase)))
            await ReconcileAfterRestartAsync(workItem, cancellationToken);
    }

    private bool CanUseProfile() => options.Enabled &&
        profile.WorkflowDevices.Any(device =>
            device.Enabled && device.ControlEnabled &&
            string.Equals(device.DeviceFamily, WorkflowDeviceFamilyIds.SampleWorkstation, StringComparison.OrdinalIgnoreCase) &&
            device.CapabilityIds.Contains(WorkflowCapabilityIds.SampleWorkstationExecute, StringComparer.OrdinalIgnoreCase));

    private async Task<bool> HasActiveAuthorizationAsync(
        WorkflowNodeExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        if (profile.Features.UseSimulator) return true;
        if (_physicalReadiness is not { Enabled: true }) return false;
        var request = await workflows.GetExecutionRequestAsync(workItem.NodeExecution.WorkflowRunId, cancellationToken);
        var authorization = request?.PhysicalAuthorization;
        if (authorization is null || authorization.ExpiresAtUtc <= _timeProvider.GetUtcNow()) return false;
        var deviceId = ResolveDeviceId(workItem.NodeExecution.Inputs);
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        return _physicalReadiness.IsCurrentAndReady(
            deviceId,
            authorization.GetDeviceEpoch(deviceId),
            authorization.ReadinessSupervisorInstanceId,
            out _);
    }

    private async Task<SampleWorkstationStatusResponse?> WaitForReadinessAsync(
        WorkflowNodeExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        var deviceId = ResolveDeviceId(workItem.NodeExecution.Inputs);
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        var deadline = _timeProvider.GetUtcNow().AddMilliseconds(Math.Max(0, options.ReadinessRetryWindowMs));
        var interval = TimeSpan.FromMilliseconds(Math.Max(100, options.ReadinessRetryIntervalMs));
        while (true)
        {
            if (!await HasActiveAuthorizationAsync(workItem, cancellationToken))
                return null;
            try
            {
                var status = await reader.GetStatusAsync(deviceId, cancellationToken);
                if (status.Online && status.State == SampleWorkstationDeviceState.Idle)
                    return status;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                _logger?.LogWarning(exception, "Workstation readiness read failed for {DeviceId}; node remains Ready.", deviceId);
            }

            if (_timeProvider.GetUtcNow() >= deadline) return null;
            await Task.Delay(interval, _timeProvider, cancellationToken);
        }
    }

    private async Task ExecuteClaimedAsync(
        WorkflowNodeExecutionWorkItem workItem,
        string equipmentNo,
        CancellationToken cancellationToken)
    {
        var node = workItem.NodeExecution;
        var operation = workItem.DeviceOperation;
        if (operation is null)
        {
            await CompleteAsync(workItem, WorkflowStepCompletionOutcome.Failed,
                "The workstation node was claimed without a durable device operation.", cancellationToken);
            return;
        }

        var deviceId = ResolveDeviceId(node.Inputs);
        var templateVersion = ReadRequired(node.Inputs, WorkflowNodeConfigurationKeys.TemplateVersion);
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(templateVersion))
        {
            await CompleteAsync(workItem, WorkflowStepCompletionOutcome.Failed,
                "The workstation node requires a configured deviceId and templateVersion.", cancellationToken);
            return;
        }
        if (!options.TryGetTemplate(templateVersion, out var template))
        {
            await CompleteAsync(workItem, WorkflowStepCompletionOutcome.Failed,
                $"Approved workstation template '{templateVersion}' was not found.", cancellationToken);
            return;
        }
        if (!template.IsValid(out var templateError))
        {
            await CompleteAsync(workItem, WorkflowStepCompletionOutcome.Failed, templateError, cancellationToken);
            return;
        }

        var sampleId = ReadRequired(node.Inputs, WorkflowRuntimeParameterNames.SampleId);
        SampleRecordResponse? sample = null;
        if (_sampleManagement is not null)
        {
            sample = await _sampleManagement.GetAsync(sampleId, cancellationToken);
            if (sample is null || sample.RunId != node.WorkflowRunId)
            {
                await CompleteAsync(workItem, WorkflowStepCompletionOutcome.Failed,
                    "The workstation node sample is not registered or is not bound to this workflow run.", cancellationToken);
                return;
            }
        }

        var taskNo = BuildTaskNo(node.WorkflowRunId, node.Id, node.Attempt);
        var taskName = string.IsNullOrWhiteSpace(sampleId) ? node.NodeName : sampleId;
        var mutationStarted = false;
        try
        {
            var common = new SampleWorkstationOperationRequest
            {
                RunId = node.WorkflowRunId,
                NodeExecutionId = node.Id,
                OperatorName = options.OperatorName,
                SampleId = sampleId
            };

            mutationStarted = true;
            await EnsureStageAsync(
                workItem,
                common with { OperationId = WorkflowPersistence.CreateStableSubOperationId(operation.OperationId, "init") },
                "init",
                () => controller.InitializeAsync(deviceId, common with { OperationId = WorkflowPersistence.CreateStableSubOperationId(operation.OperationId, "init") }, cancellationToken),
                cancellationToken);

            var createRequest = new SampleWorkstationTaskCreateRequest
            {
                OperationId = WorkflowPersistence.CreateStableSubOperationId(operation.OperationId, "create-task"),
                RunId = node.WorkflowRunId,
                NodeExecutionId = node.Id,
                OperatorName = options.OperatorName,
                SampleId = sampleId,
                TaskNo = taskNo,
                TaskName = taskName,
                TemplateVersion = template.TemplateVersion
            };
            await EnsureStageAsync(workItem, createRequest, "create-task",
                () => controller.CreateTaskAsync(deviceId, createRequest, cancellationToken), cancellationToken);

            var trajectoryRequest = new SampleWorkstationTrajectoryRequest
            {
                OperationId = WorkflowPersistence.CreateStableSubOperationId(operation.OperationId, "trajectory"),
                RunId = node.WorkflowRunId,
                NodeExecutionId = node.Id,
                OperatorName = options.OperatorName,
                SampleId = sampleId,
                TaskNo = taskNo,
                RecordNumber = template.RecordNumber,
                LiquidCode = template.LiquidCode,
                LiquidName = template.LiquidName,
                WarehouseLocation = template.WarehouseLocation,
                WarehouseX = template.WarehouseX,
                WarehouseY = template.WarehouseY,
                SourceBarCode = Expand(template.SourceBarCode, sample),
                SourceX = template.SourceX,
                SourceY = template.SourceY,
                TargetBarCode = Expand(template.TargetBarCode, sample),
                TargetX = template.TargetX,
                TargetY = template.TargetY,
                TransferVolume = template.TransferVolume
            };
            await EnsureStageAsync(workItem, trajectoryRequest, "trajectory",
                () => controller.AddTrajectoryAsync(deviceId, trajectoryRequest, cancellationToken), cancellationToken);

            await workflows.RecordDeviceOperationProgressAsync(
                node.Id, operation.OperationId, WorkflowDeviceOperationStatus.Accepted, null, cancellationToken);

            var startRequest = new SampleWorkstationStartRequest
            {
                OperationId = WorkflowPersistence.CreateStableSubOperationId(operation.OperationId, "start"),
                RunId = node.WorkflowRunId,
                NodeExecutionId = node.Id,
                OperatorName = options.OperatorName,
                SampleId = sampleId,
                TaskNo = taskNo
            };
            await EnsureStageAsync(workItem, startRequest, "start",
                () => controller.StartExperimentAsync(deviceId, startRequest, cancellationToken), cancellationToken);

            await workflows.RecordDeviceOperationProgressAsync(
                node.Id, operation.OperationId, WorkflowDeviceOperationStatus.Running, null, cancellationToken);
            var details = await PollToCompletionAsync(deviceId, taskNo, cancellationToken);
            await CompleteAsync(
                workItem,
                WorkflowStepCompletionOutcome.Succeeded,
                null,
                cancellationToken,
                new Dictionary<string, string?>
                {
                    ["deviceId"] = deviceId,
                    // Already read during startup. Display metadata must not
                    // introduce a new failure after the task proved complete.
                    ["equipmentNo"] = equipmentNo,
                    ["taskNo"] = details.TaskNo,
                    ["templateVersion"] = template.TemplateVersion,
                    ["taskState"] = details.RawState,
                    ["completedAtUtc"] = details.CompletionTime ?? _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
                    ["resultFileReference"] = $"sample-workstation://{Uri.EscapeDataString(deviceId)}/tasks/{Uri.EscapeDataString(details.TaskNo)}",
                    ["stageInitOperationId"] = WorkflowPersistence.CreateStableSubOperationId(operation.OperationId, "init").ToString("D"),
                    ["stageCreateOperationId"] = WorkflowPersistence.CreateStableSubOperationId(operation.OperationId, "create-task").ToString("D"),
                    ["stageTrajectoryOperationId"] = WorkflowPersistence.CreateStableSubOperationId(operation.OperationId, "trajectory").ToString("D"),
                    ["stageStartOperationId"] = WorkflowPersistence.CreateStableSubOperationId(operation.OperationId, "start").ToString("D")
                },
                details.TaskNo);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (mutationStarted)
            {
                await CompleteAsync(workItem, WorkflowStepCompletionOutcome.Unknown,
                    "The workstation workflow was cancelled after a possible physical write; manual reconciliation is required.",
                    CancellationToken.None);
                return;
            }
            throw;
        }
        catch (SampleWorkstationAdapterUnknownException exception)
        {
            await CompleteAsync(
                workItem,
                WorkflowStepCompletionOutcome.Unknown,
                exception.Detail ?? exception.Message,
                cancellationToken,
                vendorTaskId: exception.VendorTaskId,
                unknownReason: exception.UnknownReason ?? UnknownReason.ManualReconciliationRequired);
        }
        catch (Exception exception)
        {
            await CompleteAsync(
                workItem,
                mutationStarted ? WorkflowStepCompletionOutcome.Unknown : WorkflowStepCompletionOutcome.Failed,
                exception.Message,
                cancellationToken);
        }
    }

    private async Task EnsureStageAsync(
        WorkflowNodeExecutionWorkItem workItem,
        SampleWorkstationOperationRequest request,
        string stage,
        Func<Task<SampleWorkstationOperationResponse>> invoke,
        CancellationToken cancellationToken)
    {
        var response = await invoke();
        if (!string.Equals(response.DeviceId, ResolveDeviceId(workItem.NodeExecution.Inputs), StringComparison.OrdinalIgnoreCase) ||
            response.OperationId != request.OperationId || response.RunId != request.RunId || response.NodeExecutionId != request.NodeExecutionId)
            throw new InvalidOperationException($"Workstation stage '{stage}' returned a mismatched operation identity; no command will be replayed.");
        if (response.Status == DeviceOperationLifecycle.Unknown || response.Status == DeviceOperationLifecycle.ManualInterventionRequired)
            throw new InvalidOperationException($"Workstation stage '{stage}' is Unknown: {response.UnknownReason?.ToString() ?? "manual-reconciliation-required"}.");
        if (response.Status is DeviceOperationLifecycle.Failed or DeviceOperationLifecycle.Cancelled)
            throw new InvalidOperationException($"Workstation stage '{stage}' failed: {response.VendorMessage ?? response.Status.ToString()}.");
    }

    private async Task<SampleWorkstationTaskDetailsResponse> PollToCompletionAsync(
        string deviceId,
        string taskNo,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow().AddMilliseconds(Math.Max(1000, options.CompletionTimeoutMs));
        var interval = TimeSpan.FromMilliseconds(Math.Max(100, options.PollIntervalMs));
        while (_timeProvider.GetUtcNow() < deadline)
        {
            try
            {
                var state = await reader.GetTaskStateAsync(deviceId, taskNo, cancellationToken);
                if (!string.Equals(state.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(state.TaskNo, taskNo, StringComparison.Ordinal))
                    throw new InvalidOperationException("The workstation task response identity does not match this operation.");
                if (state.State == SampleWorkstationTaskState.Unknown)
                    throw new InvalidOperationException("The workstation task state is Unknown and requires manual reconciliation.");
                if (state.State == SampleWorkstationTaskState.Completed)
                {
                    var details = await reader.GetTaskDetailsAsync(deviceId, taskNo, cancellationToken);
                    if (!string.Equals(details.TaskNo, taskNo, StringComparison.Ordinal))
                        throw new InvalidOperationException("The workstation task details identity does not match this operation.");
                    if (details.State == SampleWorkstationTaskState.Unknown)
                        throw new InvalidOperationException("The workstation task details report Unknown.");
                    if (details.State == SampleWorkstationTaskState.Completed) return details;
                    // Detail persistence may lag behind the state response.
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TimeoutException or System.Text.Json.JsonException ||
                (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // Only retry queries within the existing completion deadline.
                // Initialize/create/trajectory/start are never repeated here.
                _logger?.LogWarning(exception, "Waiting for workstation task {TaskNo} readback; no command was replayed.", taskNo);
            }
            await Task.Delay(interval, _timeProvider, cancellationToken);
        }

        throw new TimeoutException("The workstation task did not reach Completed before the configured timeout.");
    }

    private async Task ReconcileAfterRestartAsync(
        WorkflowNodeExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        var operation = workItem.DeviceOperation;
        var deviceId = ResolveDeviceId(workItem.NodeExecution.Inputs);
        if (operation is null || string.IsNullOrWhiteSpace(deviceId))
        {
            await CompleteAsync(workItem, WorkflowStepCompletionOutcome.Unknown,
                "The workstation operation cannot be reconciled after restart because its durable identity is incomplete.", cancellationToken);
            return;
        }

        var taskNo = BuildTaskNo(workItem.NodeExecution.WorkflowRunId, workItem.NodeExecution.Id, workItem.NodeExecution.Attempt);
        try
        {
            var state = await reader.GetTaskStateAsync(deviceId, taskNo, cancellationToken);
            if (state.State == SampleWorkstationTaskState.Completed)
            {
                var details = await reader.GetTaskDetailsAsync(deviceId, taskNo, cancellationToken);
                await CompleteAsync(workItem, WorkflowStepCompletionOutcome.Succeeded, null, cancellationToken,
                    new Dictionary<string, string?>
                    {
                        ["deviceId"] = deviceId,
                        ["taskNo"] = details.TaskNo,
                        ["taskState"] = details.RawState,
                        ["completedAtUtc"] = details.CompletionTime
                    }, details.TaskNo);
                return;
            }

            await CompleteAsync(workItem, WorkflowStepCompletionOutcome.Unknown,
                $"The workstation task '{taskNo}' was not proven complete after restart; no command was replayed.", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await CompleteAsync(workItem, WorkflowStepCompletionOutcome.Unknown,
                $"Workstation restart reconciliation failed: {exception.Message}", cancellationToken);
        }
    }

    private async Task CompleteAsync(
        WorkflowNodeExecutionWorkItem workItem,
        WorkflowStepCompletionOutcome outcome,
        string? error,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? outputs = null,
        string? vendorTaskId = null,
        UnknownReason? unknownReason = null)
    {
        var evidence = new Dictionary<string, string?>(outputs ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase)
        {
            ["deviceOperationId"] = workItem.DeviceOperation?.OperationId.ToString("D"),
            ["workflowRunId"] = workItem.NodeExecution.WorkflowRunId.ToString("D"),
            ["workflowNodeExecutionId"] = workItem.NodeExecution.Id.ToString("D"),
            ["templateVersion"] = ReadRequired(workItem.NodeExecution.Inputs, WorkflowNodeConfigurationKeys.TemplateVersion)
        };
        await workflows.CompleteNodeExecutionAsync(workItem.NodeExecution.Id, new WorkflowNodeExecutionCompletionRequest
        {
            DeviceOperationId = workItem.DeviceOperation?.OperationId,
            Outcome = outcome,
            Error = error,
            VendorTaskId = vendorTaskId,
            UnknownReason = outcome == WorkflowStepCompletionOutcome.Unknown
                ? unknownReason ?? UnknownReason.ManualReconciliationRequired
                : null,
            Outputs = evidence
        }, cancellationToken);

        if (outcome == WorkflowStepCompletionOutcome.Succeeded && _sampleManagement is not null &&
            workItem.DeviceOperation is { } deviceOperation)
        {
            var sampleId = ReadRequired(workItem.NodeExecution.Inputs, WorkflowRuntimeParameterNames.SampleId);
            if (!string.IsNullOrWhiteSpace(sampleId))
            {
                try
                {
                    await _sampleManagement.MoveAsync(sampleId, new MoveSampleRequest
                    {
                        OperationId = deviceOperation.OperationId,
                        DeviceId = ResolveDeviceId(workItem.NodeExecution.Inputs) ?? "SAMPLE-WORKSTATION",
                        ToLocation = ResolveDeviceId(workItem.NodeExecution.Inputs) ?? "SAMPLE-WORKSTATION",
                        OperatorName = options.OperatorName,
                        Status = SampleLifecycleStatus.Processing
                    }, cancellationToken);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    _logger?.LogWarning(exception, "Sample custody update failed after workstation completion {OperationId}.", deviceOperation.OperationId);
                }
            }
        }
    }

    private string? ResolveDeviceId(IReadOnlyDictionary<string, string?> inputs)
    {
        if (inputs.TryGetValue(WorkflowNodeConfigurationKeys.DeviceId, out var configured) && !string.IsNullOrWhiteSpace(configured))
        {
            var match = profile.WorkflowDevices.FirstOrDefault(device =>
                string.Equals(device.DeviceId, configured.Trim(), StringComparison.OrdinalIgnoreCase) &&
                device.Enabled && device.ControlEnabled &&
                string.Equals(device.DeviceFamily, WorkflowDeviceFamilyIds.SampleWorkstation, StringComparison.OrdinalIgnoreCase) &&
                device.CapabilityIds.Contains(WorkflowCapabilityIds.SampleWorkstationExecute, StringComparer.OrdinalIgnoreCase));
            return match?.DeviceId;
        }

        return profile.WorkflowDevices.FirstOrDefault(device =>
            device.Enabled && device.ControlEnabled &&
            string.Equals(device.DeviceFamily, WorkflowDeviceFamilyIds.SampleWorkstation, StringComparison.OrdinalIgnoreCase) &&
            device.CapabilityIds.Contains(WorkflowCapabilityIds.SampleWorkstationExecute, StringComparer.OrdinalIgnoreCase))?.DeviceId;
    }

    private static string ReadRequired(IReadOnlyDictionary<string, string?> inputs, string key) =>
        inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : string.Empty;

    private static string Expand(string value, SampleRecordResponse? sample)
    {
        if (sample is null) return value.Trim();
        return value
            .Replace("{sampleId}", sample.SampleId, StringComparison.OrdinalIgnoreCase)
            .Replace("{barcode}", sample.Barcode, StringComparison.OrdinalIgnoreCase)
            .Replace("{containerPosition}", sample.ContainerPosition ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string BuildTaskNo(Guid runId, Guid nodeId, int attempt) =>
        $"MES-{runId:N}"[..Math.Min(36, $"MES-{runId:N}".Length)] + $"-{Math.Max(1, attempt)}";
}

/// <summary>Hosted polling loop; disabled unless explicitly enabled in a field profile.</summary>
public sealed class WorkflowLegacySampleWorkstationWorker(
    IServiceScopeFactory scopeFactory,
    ProfileConfiguration profile,
    WorkflowLegacySampleWorkstationWorkerOptions options,
    TimeProvider timeProvider,
    ILogger<WorkflowLegacySampleWorkstationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        try
        {
            using var recoveryScope = scopeFactory.CreateScope();
            var recovery = CreateDispatcher(recoveryScope);
            await recovery.RecoverAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Workstation startup reconciliation failed; no command was replayed.");
        }

        var interval = TimeSpan.FromMilliseconds(Math.Max(250, options.PollIntervalMs));
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
                logger.LogError(exception, "Workstation workflow polling cycle failed; no mutation was replayed.");
            }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private WorkflowLegacySampleWorkstationDispatcher CreateDispatcher(IServiceScope scope) =>
        new(
            scope.ServiceProvider.GetRequiredService<IWorkflowApplicationService>(),
            scope.ServiceProvider.GetRequiredService<ISampleWorkstationController>(),
            scope.ServiceProvider.GetRequiredService<LegacySampleWorkstationAdapterClient>(),
            profile,
            options,
            scope.ServiceProvider.GetService<IPhysicalReadinessState>(),
            scope.ServiceProvider.GetService<SampleManagementService>(),
            timeProvider,
            logger);
}
