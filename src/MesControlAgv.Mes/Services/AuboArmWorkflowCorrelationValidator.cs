using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Validates the durable identity attached to an AUBO program write.  A
/// workflow worker must present the exact device-operation record it claimed;
/// a request for a different run/node/attempt is rejected before the Adapter
/// can reach the controller.  Requests without any correlation remain
/// available for the deliberately separate manual UI, but are logged and the
/// Adapter marks their response as unassociated.
/// </summary>
public static class AuboArmWorkflowCorrelationValidator
{
    public const string MissingCorrelationWarningCode = "AUBO_UNCORRELATED_WRITE";
    public const string InvalidCorrelationCode = "AUBO_WORKFLOW_CORRELATION_INVALID";

    /// <summary>
    /// Validates a load/run correlation.  Unknown device operations remain
    /// rejected here: an Unknown operation is not permission to replay a
    /// mutating program write.
    /// </summary>
    public static Task<AuboArmOperationCorrelation?> ValidateAsync(
        string deviceId,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        IWorkflowApplicationService workflows,
        ILogger logger,
        string operation,
        CancellationToken cancellationToken) =>
        ValidateCorrelatedAsync(
            deviceId,
            operationId,
            correlation,
            workflows,
            logger,
            operation,
            allowUnknownDeviceOperation: false,
            stopValidation: false,
            cancellationToken);

    /// <summary>
    /// Validates the narrower correlation accepted by an explicit AUBO stop.
    /// Stop is a safety cleanup operation, so it may target a durable Running
    /// or Unknown operation, but it must still identify the same run, node,
    /// request, attempt, capability, and physical device.  This method only
    /// reads workflow state and never calls an Adapter.
    /// </summary>
    public static async Task<AuboArmOperationCorrelation> ValidateForStopAsync(
        string deviceId,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        IWorkflowApplicationService workflows,
        ILogger logger,
        string operation,
        CancellationToken cancellationToken)
    {
        var validated = await ValidateCorrelatedAsync(
                deviceId,
                operationId,
                correlation,
                workflows,
                logger,
                operation,
                allowUnknownDeviceOperation: true,
                stopValidation: true,
                cancellationToken)
            .ConfigureAwait(false);

        return validated ?? throw Invalid("AUBO stop requires a complete workflow correlation.");
    }

    private static async Task<AuboArmOperationCorrelation?> ValidateCorrelatedAsync(
        string deviceId,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        IWorkflowApplicationService workflows,
        ILogger logger,
        string operation,
        bool allowUnknownDeviceOperation,
        bool stopValidation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (correlation is null)
        {
            SafeLogWarning(logger,
                "{WarningCode}: AUBO {Operation} for {DeviceId} operation {OperationId} has no workflow correlation; manual reconciliation is required.",
                MissingCorrelationWarningCode,
                operation,
                deviceId,
                operationId);
            return null;
        }

        if (!correlation.IsComplete)
        {
            throw Invalid($"{correlation.ValidationError}");
        }

        if (correlation.DeviceOperationId != operationId)
        {
            throw Invalid("DeviceOperationId must equal the request operationId.");
        }

        var run = await workflows.GetExecutionAsync(correlation.WorkflowRunId, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            throw Invalid($"Workflow run '{correlation.WorkflowRunId:D}' was not found.");
        }

        if (run.RequestId != correlation.RequestId)
        {
            throw Invalid("RequestId does not match the workflow run admission record.");
        }

        if (run.IsTerminal)
        {
            throw Invalid("AUBO writes are not permitted for a terminal workflow run.");
        }

        var nodes = await workflows.ListNodeExecutionsAsync(
                correlation.WorkflowRunId,
                cancellationToken)
            .ConfigureAwait(false);
        var node = nodes.FirstOrDefault(item => item.Id == correlation.WorkflowNodeExecutionId);
        if (node is null)
        {
            throw Invalid($"Workflow node execution '{correlation.WorkflowNodeExecutionId:D}' was not found in the run.");
        }

        if (node.WorkflowRunId != correlation.WorkflowRunId ||
            !string.Equals(node.NodeTypeId, WorkflowGraphNodeTypeIds.RobotExecuteProgram, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("The correlated node is not a robot.execute-program node in the requested run.");
        }

        var nodeIsAllowed = stopValidation
            ? node.Status is WorkflowNodeExecutionStatus.Running or WorkflowNodeExecutionStatus.Unknown
            : node.Status is WorkflowNodeExecutionStatus.Claimed or WorkflowNodeExecutionStatus.Running;
        if (!nodeIsAllowed)
        {
            throw Invalid(stopValidation
                ? $"The correlated node is not in a stoppable running/unknown state ({node.Status})."
                : $"The correlated node is not actively claimed ({node.Status}); a device write cannot bypass the worker claim.");
        }

        var operations = await workflows.ListDeviceOperationsAsync(
                correlation.WorkflowRunId,
                cancellationToken)
            .ConfigureAwait(false);
        var durable = operations.FirstOrDefault(item => item.OperationId == operationId);
        if (durable is null)
        {
            throw Invalid($"Device operation '{operationId:D}' was not found in the workflow run.");
        }

        if (durable.WorkflowRunId != correlation.WorkflowRunId ||
            durable.NodeExecutionId != correlation.WorkflowNodeExecutionId ||
            durable.RequestId != correlation.RequestId ||
            durable.Attempt != correlation.Attempt ||
            !string.Equals(durable.CapabilityId, WorkflowCapabilityIds.RobotExecuteProgram, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("The correlated device-operation identity does not match the durable workflow record.");
        }

        if (stopValidation && string.IsNullOrWhiteSpace(durable.DeviceId))
        {
            throw Invalid("The correlated device-operation record has no physical device id for a stop.");
        }

        if (!string.IsNullOrWhiteSpace(durable.DeviceId) &&
            !string.Equals(durable.DeviceId.Trim(), deviceId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("DeviceId does not match the durable device-operation record.");
        }

        if (stopValidation)
        {
            if (durable.Status is not (WorkflowDeviceOperationStatus.Running or WorkflowDeviceOperationStatus.Unknown))
            {
                throw Invalid($"The correlated device operation is not stoppable ({durable.Status}); stop requires Running or Unknown.");
            }
        }
        else if (durable.Status is WorkflowDeviceOperationStatus.Succeeded or
                  WorkflowDeviceOperationStatus.Rejected or
                  WorkflowDeviceOperationStatus.Failed or
                  WorkflowDeviceOperationStatus.Cancelled ||
                  (!allowUnknownDeviceOperation && durable.Status == WorkflowDeviceOperationStatus.Unknown))
        {
            throw Invalid($"The correlated device operation is already terminal ({durable.Status}) and cannot be replayed.");
        }

        if (string.IsNullOrWhiteSpace(durable.CorrelationId))
        {
            SafeLogWarning(logger,
                "{WarningCode}: durable AUBO operation {OperationId} has no persisted correlation string; accepting identifier-only correlation for compatibility.",
                MissingCorrelationWarningCode,
                operationId);
        }
        else if (!string.IsNullOrWhiteSpace(correlation.CorrelationId) &&
                 !string.Equals(durable.CorrelationId.Trim(), correlation.CorrelationId.Trim(), StringComparison.Ordinal))
        {
            throw Invalid("CorrelationId does not match the durable device-operation record.");
        }

        return string.IsNullOrWhiteSpace(correlation.CorrelationId) &&
               !string.IsNullOrWhiteSpace(durable.CorrelationId)
            ? correlation with { CorrelationId = durable.CorrelationId.Trim() }
            : correlation;
    }

    private static AuboArmCorrelationException Invalid(string detail) =>
        new($"{InvalidCorrelationCode}: {detail}");

    private static void SafeLogWarning(ILogger logger, string message, params object?[] args)
    {
        try
        {
            logger.LogWarning(message, args);
        }
        catch
        {
            // Logging providers (notably Windows EventLog without source
            // registration) must never turn a safety warning into a device
            // operation failure. The warning code is also returned in the HTTP
            // response for an auditable, provider-independent signal.
        }
    }
}

/// <summary>HTTP-boundary error for a mismatched workflow/device correlation.</summary>
public sealed class AuboArmCorrelationException(string message) : InvalidOperationException(message)
{
    public string Code => AuboArmWorkflowCorrelationValidator.InvalidCorrelationCode;
}
