using System.Net;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Entities;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Persists the narrowly scoped, supervised field-navigation acceptance flow.
/// It is deliberately separate from normal transport tasks and delegates the
/// final read-only safety assessment to the Adapter immediately before motion.
/// </summary>
public sealed class FieldNavigationAcceptanceService : IFieldNavigationAcceptanceApplicationService
{
    private readonly FieldNavigationAcceptanceRepository _repository;
    private readonly IAgvGateway _gateway;
    private readonly ProfileConfiguration _profile;
    private readonly PathPlanner _planner;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkflowApplicationService? _workflows;
    private readonly IPhysicalReadinessState? _physicalReadiness;
    private readonly PhysicalExecutionAdmissionPolicy _admissionPolicy;

    public FieldNavigationAcceptanceService(
        FieldNavigationAcceptanceRepository repository,
        IAgvGateway gateway,
        ProfileConfiguration profile,
        PathPlanner planner,
        TimeProvider? timeProvider = null,
        IWorkflowApplicationService? workflows = null,
        IPhysicalReadinessState? physicalReadiness = null,
        PhysicalExecutionAdmissionPolicy? admissionPolicy = null)
    {
        _repository = repository;
        _gateway = gateway;
        _profile = profile;
        _planner = planner;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _workflows = workflows;
        _physicalReadiness = physicalReadiness;
        _admissionPolicy = admissionPolicy ?? new PhysicalExecutionAdmissionPolicy(
            profile with
            {
                Features = profile.Features with
                {
                    UseSimulator = physicalReadiness is null
                }
            },
            physicalReadiness ?? new DisabledPhysicalReadinessState(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PhysicalExecutionAdmissionPolicy>.Instance);
    }

    public async Task<FieldNavigationAcceptanceResponse> CreateAsync(
        CreateFieldNavigationAcceptanceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agvId = RequireValue(request.AgvId, nameof(request.AgvId));
        var sourceStationId = RequireValue(request.SourceStationId, nameof(request.SourceStationId));
        var targetStationId = RequireValue(request.TargetStationId, nameof(request.TargetStationId));
        if (StringComparer.Ordinal.Equals(sourceStationId, targetStationId))
        {
            throw new ArgumentException("The source and target stations must differ.", nameof(request));
        }

        await ValidateWorkflowLinkAsync(request, targetStationId, cancellationToken);

        if (!_profile.Agvs.Any(agv => agv.Enabled && StringComparer.Ordinal.Equals(agv.AgvId, agvId)))
        {
            throw new KeyNotFoundException($"AGV '{agvId}' is not enabled by the active profile.");
        }

        var mapSnapshot = _profile.PhysicalAcceptance?.MapSnapshot
            ?? throw new InvalidOperationException("A physical acceptance profile is required for field navigation acceptance.");
        if (!mapSnapshot.StationIds.Contains(sourceStationId, StringComparer.Ordinal) ||
            !mapSnapshot.StationIds.Contains(targetStationId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The acceptance route must use stations from the approved controller map snapshot.");
        }

        var path = _planner.Plan(sourceStationId, targetStationId).Stations;
        if (path.Count < 2)
        {
            throw new InvalidOperationException("The acceptance route must include at least one directed map edge.");
        }

        var approvedEdges = mapSnapshot.DirectedEdges
            .Select(edge => $"{edge.From}\u001f{edge.To}")
            .ToHashSet(StringComparer.Ordinal);
        if (path.Zip(path.Skip(1), (from, to) => $"{from}\u001f{to}")
            .Any(edge => !approvedEdges.Contains(edge)))
        {
            throw new InvalidOperationException(
                "The planned route contains a directed edge that is not present in the approved controller map snapshot.");
        }

        var now = _timeProvider.GetUtcNow();
        var acceptance = new FieldNavigationAcceptance
        {
            Status = FieldNavigationAcceptanceStatuses.Draft,
            AgvId = agvId,
            SourceStationId = sourceStationId,
            TargetStationId = targetStationId,
            MapName = mapSnapshot.MapName,
            MapMd5 = mapSnapshot.Md5,
            PlannedPathJson = JsonSerializer.Serialize(path),
            Description = NormalizeOptional(request.Description),
            WorkflowRunId = request.WorkflowRunId,
            WorkflowNodeExecutionId = request.WorkflowNodeExecutionId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await _repository.CreateAsync(acceptance, new
        {
            agvId,
            sourceStationId,
            targetStationId,
            mapSnapshot.MapName,
            mapSnapshot.Md5,
            plannedPath = path,
            request.WorkflowRunId,
            request.WorkflowNodeExecutionId
        }, cancellationToken);
        return ToResponse(acceptance);
    }

    public async Task<FieldNavigationAcceptanceResponse> AuthorizeAsync(
        Guid acceptanceId,
        AuthorizeFieldNavigationAcceptanceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _admissionPolicy.RequireSupervisedExecution("field-navigation.authorize");
        var acceptance = await RequireAcceptanceAsync(acceptanceId, cancellationToken);
        if (acceptance.Status != FieldNavigationAcceptanceStatuses.Draft)
        {
            throw new InvalidOperationException("Only a draft field-navigation acceptance can be authorized.");
        }

        var operatorName = RequireValue(request.OperatorName, nameof(request.OperatorName));
        var safetyObserverName = RequireValue(request.SafetyObserverName, nameof(request.SafetyObserverName));
        var permitId = RequireValue(request.PermitId, nameof(request.PermitId));
        var now = _timeProvider.GetUtcNow();
        if (request.ExpiresAtUtc <= now)
        {
            throw new ArgumentException("The field-navigation permit must expire in the future.", nameof(request));
        }

        var existingPermit = await _repository.GetByPermitIdAsync(permitId, cancellationToken);
        if (existingPermit is not null && existingPermit.Id != acceptance.Id)
        {
            throw new InvalidOperationException("The field-navigation permit id is already in use.");
        }

        long? deviceEpoch = request.DeviceEpoch;
        string? supervisorInstanceId = request.ReadinessSupervisorInstanceId;
        if (_physicalReadiness is { Enabled: true } readiness)
        {
            var supervisorSnapshot = readiness.GetSnapshot();
            if (!readiness.TryGetDevice(acceptance.AgvId, out var device) ||
                device.State != PhysicalDeviceReadinessState.Ready)
            {
                throw new InvalidOperationException(
                    $"Physical AGV '{acceptance.AgvId}' is not Ready in the current supervisor snapshot.");
            }

            var workflowAuthorization = acceptance.WorkflowRunId is { } workflowRunId &&
                                        _workflows is not null
                ? (await _workflows.GetExecutionRequestAsync(
                    workflowRunId,
                    cancellationToken))?.PhysicalAuthorization
                : null;
            var workflowEpoch = workflowAuthorization?.GetDeviceEpoch(acceptance.AgvId);
            var expectedSupervisorInstanceId = supervisorInstanceId ??
                                               workflowAuthorization?.ReadinessSupervisorInstanceId;
            if (!string.IsNullOrWhiteSpace(expectedSupervisorInstanceId) &&
                !string.Equals(
                    expectedSupervisorInstanceId.Trim(),
                    supervisorSnapshot.SupervisorInstanceId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The field-navigation authorization belongs to a previous physical-readiness supervisor instance.");
            }

            var expectedEpoch = deviceEpoch ?? workflowEpoch;
            if (expectedEpoch.HasValue && expectedEpoch.Value != device.DeviceEpoch)
            {
                throw new InvalidOperationException(
                    $"Physical AGV '{acceptance.AgvId}' authorization epoch {expectedEpoch.Value} does not match current epoch {device.DeviceEpoch}.");
            }

            deviceEpoch = device.DeviceEpoch;
            supervisorInstanceId = supervisorSnapshot.SupervisorInstanceId;
            if (!readiness.AcknowledgeAuthorization(acceptance.AgvId, deviceEpoch.Value))
            {
                throw new InvalidOperationException(
                    "Physical readiness changed while the field-navigation permit was being authorized; submit a new permit.");
            }
        }

        acceptance.Status = FieldNavigationAcceptanceStatuses.Authorized;
        acceptance.OperatorName = operatorName;
        acceptance.SafetyObserverName = safetyObserverName;
        acceptance.PermitId = permitId;
        acceptance.AuthorizedAtUtc = now;
        acceptance.ExpiresAtUtc = request.ExpiresAtUtc;
        acceptance.DeviceEpoch = deviceEpoch;
        acceptance.ReadinessSupervisorInstanceId = supervisorInstanceId;
        acceptance.LastError = null;
        await _repository.SaveWithAuditAsync(acceptance, "Authorized", new
        {
            operatorName,
            safetyObserverName,
            permitId,
            expiresAtUtc = request.ExpiresAtUtc,
            deviceEpoch,
            supervisorInstanceId
        }, cancellationToken);
        return ToResponse(acceptance);
    }

    public async Task<FieldNavigationAcceptanceResponse> DispatchAsync(
        Guid acceptanceId,
        CancellationToken cancellationToken)
    {
        _admissionPolicy.RequireSupervisedExecution("field-navigation.dispatch");
        var acceptance = await RequireAcceptanceAsync(acceptanceId, cancellationToken);
        if (acceptance.WorkflowRunId.HasValue)
        {
            throw new InvalidOperationException(
                "A workflow-linked field-navigation acceptance can only be dispatched by the workflow worker after node claim.");
        }

        return await DispatchCoreAsync(acceptance, cancellationToken);
    }

    public async Task<FieldNavigationAcceptanceResponse> DispatchForWorkflowAsync(
        Guid acceptanceId,
        Guid workflowNodeExecutionId,
        Guid workflowDeviceOperationId,
        CancellationToken cancellationToken)
    {
        _admissionPolicy.RequireSupervisedExecution("field-navigation.workflow-dispatch");
        var acceptance = await RequireAcceptanceAsync(acceptanceId, cancellationToken);
        if (acceptance.WorkflowRunId is null ||
            acceptance.WorkflowNodeExecutionId != workflowNodeExecutionId ||
            acceptance.WorkflowDeviceOperationId != workflowDeviceOperationId)
        {
            throw new InvalidOperationException(
                "The field-navigation acceptance does not match the claimed workflow node/device operation.");
        }

        return await DispatchCoreAsync(acceptance, cancellationToken);
    }

    private async Task<FieldNavigationAcceptanceResponse> DispatchCoreAsync(
        FieldNavigationAcceptance acceptance,
        CancellationToken cancellationToken)
    {
        if (!_profile.Features.EnableFieldNavigationAcceptance)
        {
            throw new InvalidOperationException("Field navigation acceptance is disabled by the active profile.");
        }
        if (acceptance.Status != FieldNavigationAcceptanceStatuses.Authorized)
        {
            throw new InvalidOperationException("Only an authorized field-navigation acceptance can be dispatched.");
        }

        var now = _timeProvider.GetUtcNow();
        if (acceptance.ExpiresAtUtc is null || acceptance.ExpiresAtUtc <= now)
        {
            acceptance.Status = FieldNavigationAcceptanceStatuses.Expired;
            acceptance.LastError = "field_navigation_permit_expired";
            await _repository.SaveWithAuditAsync(acceptance, "PermitExpired", new { acceptance.PermitId }, cancellationToken);
            return ToResponse(acceptance);
        }

        if (_physicalReadiness is { Enabled: true } readiness &&
            !readiness.IsCurrentAndReady(
                acceptance.AgvId,
                acceptance.DeviceEpoch,
                acceptance.ReadinessSupervisorInstanceId,
                out var readinessReason))
        {
            var rejectionCode = NormalizeDispatchReadinessCode(readinessReason);
            acceptance.Status = FieldNavigationAcceptanceStatuses.Rejected;
            acceptance.LastError =
                $"physical_readiness_epoch_invalid:{rejectionCode}";
            await _repository.SaveWithAuditAsync(
                acceptance,
                "DispatchBlockedByPhysicalReadiness",
                new
                {
                    acceptance.DeviceEpoch,
                    acceptance.ReadinessSupervisorInstanceId,
                    reason = rejectionCode
                },
                cancellationToken);
            throw new PhysicalExecutionAdmissionException(
                rejectionCode,
                $"Field-navigation physical readiness is not current for '{acceptance.AgvId}'.");
        }

        var fieldGateway = _gateway as IFieldNavigationAcceptanceGateway
            ?? throw new InvalidOperationException("The configured AGV gateway does not support field-navigation acceptance.");
        var path = DeserializePath(acceptance.PlannedPathJson);
        acceptance.Status = FieldNavigationAcceptanceStatuses.Dispatching;
        acceptance.PermitConsumedAtUtc = now;
        acceptance.LastError = null;
        await _repository.SaveWithAuditAsync(acceptance, "DispatchRequested", new
        {
            acceptance.PermitId,
            acceptance.AgvId,
            acceptance.SourceStationId,
            acceptance.TargetStationId,
            plannedPath = path
        }, cancellationToken);

        try
        {
            var deviceTask = await fieldGateway.DispatchFieldNavigationAcceptanceAsync(
                acceptance.Id,
                new FieldNavigationDispatchCommand(
                    acceptance.AgvId,
                    acceptance.SourceStationId,
                    acceptance.TargetStationId,
                    path),
                cancellationToken);
            acceptance.DeviceTaskId = deviceTask.DeviceTaskId;
            acceptance.Status = ToAcceptanceStatus(deviceTask.State);
            acceptance.LastError = deviceTask.LastError;
            await _repository.SaveWithAuditAsync(acceptance, "DispatchConfirmed", new
            {
                deviceTask.DeviceTaskId,
                deviceTask.State,
                deviceTask.LastError
            }, cancellationToken);
        }
        catch (AdapterHttpException exception) when (exception.ResponseStatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            acceptance.Status = FieldNavigationAcceptanceStatuses.Rejected;
            acceptance.LastError = exception.Detail ?? exception.Message;
            await _repository.SaveWithAuditAsync(acceptance, "DispatchRejected", new
            {
                statusCode = (int)exception.ResponseStatusCode,
                acceptance.LastError
            }, cancellationToken);
        }
        catch (AdapterHttpException exception) when (exception.ResponseStatusCode is
                   HttpStatusCode.BadGateway or
                   HttpStatusCode.ServiceUnavailable or
                   HttpStatusCode.GatewayTimeout or
                   HttpStatusCode.InternalServerError)
        {
            // The Adapter may have crossed the navigation write boundary
            // before its controller/status channel failed. Treat every
            // gateway/transport response as Unknown, never as a confirmed
            // failure that an automatic caller might safely replay.
            acceptance.Status = FieldNavigationAcceptanceStatuses.Unknown;
            acceptance.LastError = exception.Detail ?? exception.Message;
            await _repository.SaveWithAuditAsync(acceptance, "DispatchUnknown", new
            {
                statusCode = (int)exception.ResponseStatusCode,
                acceptance.LastError
            }, cancellationToken);
        }
        catch (TimeoutException exception)
        {
            acceptance.Status = FieldNavigationAcceptanceStatuses.Unknown;
            acceptance.LastError = exception.Message;
            await _repository.SaveWithAuditAsync(acceptance, "DispatchUnknown", new { acceptance.LastError }, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            acceptance.Status = FieldNavigationAcceptanceStatuses.Unknown;
            acceptance.LastError = exception.Message;
            await _repository.SaveWithAuditAsync(acceptance, "DispatchUnknown", new { acceptance.LastError }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            acceptance.Status = FieldNavigationAcceptanceStatuses.Unknown;
            acceptance.LastError = "field_navigation_dispatch_cancellation_unconfirmed";
            await _repository.SaveWithAuditAsync(acceptance, "DispatchUnknown", new { acceptance.LastError }, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            acceptance.Status = FieldNavigationAcceptanceStatuses.Failed;
            acceptance.LastError = exception.Message;
            await _repository.SaveWithAuditAsync(acceptance, "DispatchFailed", new { acceptance.LastError }, cancellationToken);
        }

        return ToResponse(acceptance);
    }

    public Task<FieldNavigationAcceptanceResponse> CancelAsync(
        Guid acceptanceId,
        CancellationToken cancellationToken) =>
        CancelLegacyAsync(acceptanceId, cancellationToken);

    private async Task<FieldNavigationAcceptanceResponse> CancelLegacyAsync(
        Guid acceptanceId,
        CancellationToken cancellationToken)
    {
        var acceptance = await RequireAcceptanceAsync(acceptanceId, cancellationToken);
        return await CancelAsync(acceptanceId, acceptance.OperatorName, cancellationToken);
    }

    public async Task<FieldNavigationAcceptanceResponse> CancelAsync(
        Guid acceptanceId,
        string? operatorName,
        CancellationToken cancellationToken)
    {
        var actor = RequireValue(operatorName, nameof(operatorName));
        var acceptance = await RequireAcceptanceAsync(acceptanceId, cancellationToken);
        if (acceptance.Status is FieldNavigationAcceptanceStatuses.Cancelled or
            FieldNavigationAcceptanceStatuses.Failed or
            FieldNavigationAcceptanceStatuses.Rejected or
            FieldNavigationAcceptanceStatuses.Expired)
        {
            return ToResponse(acceptance);
        }
        if (acceptance.Status is not (FieldNavigationAcceptanceStatuses.Dispatching or
            FieldNavigationAcceptanceStatuses.Accepted or
            FieldNavigationAcceptanceStatuses.Moving or
            FieldNavigationAcceptanceStatuses.Unknown))
        {
            throw new InvalidOperationException("Only a dispatched field-navigation acceptance can be cancelled.");
        }

        await _repository.SaveWithAuditAsync(acceptance, "CancelRequested", new { acceptance.DeviceTaskId, operatorName = actor }, cancellationToken);
        try
        {
            var deviceTask = await _gateway.CancelAsync(acceptance.Id, cancellationToken);
            if (deviceTask is { State: "cancelled" })
            {
                acceptance.Status = FieldNavigationAcceptanceStatuses.Cancelled;
                acceptance.DeviceTaskId = deviceTask.DeviceTaskId;
                acceptance.LastError = deviceTask.LastError;
                await _repository.SaveWithAuditAsync(acceptance, "CancelConfirmed", new { deviceTask.DeviceTaskId, operatorName = actor }, cancellationToken);
            }
            else
            {
                acceptance.Status = FieldNavigationAcceptanceStatuses.Unknown;
                acceptance.LastError = deviceTask?.LastError ?? "cancel_not_confirmed_by_device";
                await _repository.SaveWithAuditAsync(acceptance, "CancelUnknown", new { acceptance.LastError }, cancellationToken);
            }
        }
        catch (AdapterHttpException exception) when (exception.ResponseStatusCode is
                   HttpStatusCode.BadGateway or
                   HttpStatusCode.ServiceUnavailable or
                   HttpStatusCode.GatewayTimeout or
                   HttpStatusCode.InternalServerError)
        {
            acceptance.Status = FieldNavigationAcceptanceStatuses.Unknown;
            acceptance.LastError = exception.Detail ?? exception.Message;
            await _repository.SaveWithAuditAsync(acceptance, "CancelUnknown", new
            {
                statusCode = (int)exception.ResponseStatusCode,
                acceptance.LastError
            }, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            acceptance.Status = FieldNavigationAcceptanceStatuses.Unknown;
            acceptance.LastError = exception.Message;
            await _repository.SaveWithAuditAsync(acceptance, "CancelUnknown", new { acceptance.LastError }, cancellationToken);
        }
        catch (TimeoutException exception)
        {
            acceptance.Status = FieldNavigationAcceptanceStatuses.Unknown;
            acceptance.LastError = exception.Message;
            await _repository.SaveWithAuditAsync(acceptance, "CancelUnknown", new { acceptance.LastError }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            acceptance.Status = FieldNavigationAcceptanceStatuses.Unknown;
            acceptance.LastError = "field_navigation_cancel_cancellation_unconfirmed";
            await _repository.SaveWithAuditAsync(
                acceptance,
                "CancelUnknown",
                new { acceptance.LastError },
                CancellationToken.None);
            throw;
        }

        return ToResponse(acceptance);
    }

    public async Task<FieldNavigationAcceptanceDetailResponse?> GetAsync(
        Guid acceptanceId,
        CancellationToken cancellationToken)
    {
        var acceptance = await _repository.GetAsync(acceptanceId, cancellationToken);
        if (acceptance is null)
        {
            return null;
        }

        var audits = await _repository.ListAuditsAsync(acceptanceId, cancellationToken);
        return new FieldNavigationAcceptanceDetailResponse(
            ToResponse(acceptance),
            audits.Select(audit => new FieldNavigationAcceptanceAuditResponse(
                audit.Id,
                audit.EventType,
                audit.DetailsJson,
                audit.OccurredAtUtc)).ToArray());
    }

    public async Task<IReadOnlyList<FieldNavigationAcceptanceResponse>> ListForWorkflowRunAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        if (workflowRunId == Guid.Empty) return [];
        return (await _repository.ListForWorkflowRunAsync(workflowRunId, cancellationToken))
            .Select(ToResponse)
            .ToArray();
    }

    private async Task<FieldNavigationAcceptance> RequireAcceptanceAsync(Guid acceptanceId, CancellationToken cancellationToken)
    {
        if (acceptanceId == Guid.Empty)
        {
            throw new ArgumentException("A field-navigation acceptance id is required.", nameof(acceptanceId));
        }

        return await _repository.GetAsync(acceptanceId, cancellationToken)
            ?? throw new KeyNotFoundException($"Field-navigation acceptance '{acceptanceId}' was not found.");
    }

    private async Task ValidateWorkflowLinkAsync(
        CreateFieldNavigationAcceptanceRequest request,
        string targetStationId,
        CancellationToken cancellationToken)
    {
        if (request.WorkflowRunId is null && request.WorkflowNodeExecutionId is null) return;
        if (request.WorkflowRunId is not { } workflowRunId || workflowRunId == Guid.Empty ||
            request.WorkflowNodeExecutionId is not { } nodeExecutionId || nodeExecutionId == Guid.Empty)
        {
            throw new ArgumentException(
                "WorkflowRunId and WorkflowNodeExecutionId must be supplied together and must be non-empty.",
                nameof(request));
        }

        var workflows = _workflows ?? throw new InvalidOperationException(
            "Workflow-linked field navigation is not available in this MES deployment.");
        var run = await workflows.GetExecutionAsync(workflowRunId, cancellationToken)
            ?? throw new KeyNotFoundException($"Workflow run '{workflowRunId}' was not found.");
        if (run.RuntimeStatus is not (WorkflowRuntimeStatus.Prepared or WorkflowRuntimeStatus.Running))
        {
            throw new InvalidOperationException(
                $"Workflow run '{workflowRunId}' is {run.RuntimeStatus}; only an active run can receive a field permit.");
        }

        var node = (await workflows.ListNodeExecutionsAsync(workflowRunId, cancellationToken))
            .SingleOrDefault(item => item.Id == nodeExecutionId)
            ?? throw new KeyNotFoundException(
                $"Workflow node execution '{nodeExecutionId}' was not found in run '{workflowRunId}'.");
        if (node.Status != WorkflowNodeExecutionStatus.Ready ||
            !string.Equals(node.NodeTypeId, WorkflowGraphNodeTypeIds.Move, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A field-navigation permit can be linked only to the Ready Move node of an active workflow run.");
        }
        if (!node.Inputs.TryGetValue(WorkflowNodeConfigurationKeys.TargetStation, out var workflowTarget) ||
            !string.Equals(workflowTarget?.Trim(), targetStationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The field-navigation target does not match the linked workflow Move node.");
        }

        var existing = await _repository.GetByWorkflowNodeExecutionIdAsync(nodeExecutionId, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                "The workflow Move node already has a field-navigation acceptance record.");
        }
    }

    private static FieldNavigationAcceptanceResponse ToResponse(FieldNavigationAcceptance acceptance) => new(
        acceptance.Id,
        acceptance.Status,
        acceptance.AgvId,
        acceptance.SourceStationId,
        acceptance.TargetStationId,
        acceptance.MapName,
        acceptance.MapMd5,
        DeserializePath(acceptance.PlannedPathJson),
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

    private static IReadOnlyList<string> DeserializePath(string value) =>
        JsonSerializer.Deserialize<List<string>>(value) ?? [];

    private static string ToAcceptanceStatus(string? deviceState) => deviceState?.Trim().ToLowerInvariant() switch
    {
        "accepted" => FieldNavigationAcceptanceStatuses.Accepted,
        "moving" => FieldNavigationAcceptanceStatuses.Moving,
        // A vendor pause commonly represents a temporary obstacle stop. Keep
        // the already-dispatched operation in flight; the recovery worker will
        // continue read-only reconciliation and no new navigation is sent.
        "paused" => FieldNavigationAcceptanceStatuses.Moving,
        "arrived" or "completed" => FieldNavigationAcceptanceStatuses.Arrived,
        "cancelled" => FieldNavigationAcceptanceStatuses.Cancelled,
        "failed" => FieldNavigationAcceptanceStatuses.Failed,
        _ => FieldNavigationAcceptanceStatuses.Unknown
    };

    private static string NormalizeDispatchReadinessCode(string? reason) => reason switch
    {
        PhysicalReadinessReasonCodes.SupervisorInstanceMismatch =>
            PhysicalReadinessReasonCodes.SupervisorInstanceMismatch,
        PhysicalReadinessReasonCodes.EpochMismatch =>
            PhysicalReadinessReasonCodes.EpochMismatch,
        _ => PhysicalReadinessReasonCodes.DeviceNotReady
    };

    private static string RequireValue(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
