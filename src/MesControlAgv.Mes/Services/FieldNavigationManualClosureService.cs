using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Mes.Services;

public sealed class FieldNavigationManualClosureService(
    FieldNavigationAcceptanceRepository repository, IAgvGateway gateway, IWorkflowRunControlAuthorizer authorizer)
{
    public Task<FieldNavigationManualClosureResult> CloseAsync(
        Guid acceptanceId, FieldNavigationManualCloseRequest request, CancellationToken ct) =>
        FieldNavigationAcceptanceGate.RunAsync(() => CloseCoreAsync(acceptanceId, request, ct), ct);

    private async Task<FieldNavigationManualClosureResult> CloseCoreAsync(
        Guid acceptanceId, FieldNavigationManualCloseRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Actor) || request.Actor.Length > 256 ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000)
            throw new ArgumentException("Request ID, actor and a reason of at most 2000 characters are required.");
        request = request with { Actor = request.Actor.Trim(), Reason = request.Reason.Trim() };
        authorizer.Demand(request.Actor, WorkflowRunControlPermissions.ResolveUnknown);
        var acceptance = await repository.GetAsync(acceptanceId, ct)
            ?? throw new KeyNotFoundException("Field navigation acceptance does not exist.");
        await repository.Database.Entry(acceptance).ReloadAsync(ct);
        if (acceptance.IsWorkflowLinked())
            throw new InvalidOperationException("Workflow-linked nodes must use workflow reconciliation.");
        var path = JsonSerializer.Deserialize<string[]>(acceptance.PlannedPathJson) ?? [];
        var command = new FieldNavigationManualCloseCommand(request.RequestId, request.Actor, request.Reason,
            acceptance.AgvId, acceptance.SourceStationId, acceptance.TargetStationId, path,
            request.ReplacementAcceptanceId is { } replacementId
                ? new(replacementId, acceptance.MapName, acceptance.MapMd5) : null);
        var requestJson = JsonSerializer.Serialize(command);
        var audits = await repository.ListAuditsAsync(acceptanceId, ct);
        var pending = audits.FirstOrDefault(item => item.EventType == "ManualClosureRequested");
        if (pending is not null && pending.DetailsJson != requestJson)
            throw new InvalidOperationException("Manual closure request conflicts with the persisted request.");
        var confirmed = audits.SingleOrDefault(item => item.EventType == "ManualClosureConfirmed");
        if (confirmed is not null)
        {
            if (pending is null || acceptance.Status != FieldNavigationAcceptanceStatuses.ManuallyClosed)
                throw new InvalidOperationException("Manual closure persistence is inconsistent.");
            return JsonSerializer.Deserialize<FieldNavigationManualClosureResult>(confirmed.DetailsJson)!;
        }
        if (acceptance.Status != FieldNavigationAcceptanceStatuses.Unknown || acceptance.PermitConsumedAtUtc is null)
            throw new InvalidOperationException("Only a consumed Unknown standalone acceptance can be manually closed.");
        if (gateway is not IFieldNavigationManualClosureGateway closer)
            throw new InvalidOperationException("Adapter does not support audited manual disposition.");
        if (pending is null)
        {
            if (command.Replacement is { } reference)
                await ValidateReplacementAsync(acceptance, reference, path, ct);
            await repository.SaveWithAuditAsync(acceptance, "ManualClosureRequested", command, ct);
        }

        // Repeating this exact request after a lost HTTP acknowledgement reads the
        // Adapter's durable disposition; it never repeats a physical command.
        var result = await closer.CloseUnconfirmedNavigationAsync(acceptanceId, command, ct);
        if (result.TaskId != acceptanceId || result.RequestId != request.RequestId ||
            result.Actor != request.Actor || result.Reason != request.Reason || result.AgvId != acceptance.AgvId ||
            result.SourceStationId != acceptance.SourceStationId || result.TargetStationId != acceptance.TargetStationId ||
            result.Replacement != command.Replacement ||
            result.Evidence.TaskId != acceptanceId || result.Evidence.CurrentStationId !=
                (command.Replacement is null ? acceptance.SourceStationId : acceptance.TargetStationId) ||
            !result.Evidence.Path.SequenceEqual(path, StringComparer.Ordinal) ||
            result.Evidence.Segments.Count != path.Length - 1 || result.Evidence.Segments.Any(item => item.VendorStatus != 404) ||
            (command.Replacement is not null && !MatchesHistoricalEvidence(result, command.Replacement)))
            throw new InvalidOperationException("Adapter manual disposition does not match the exact acceptance.");
        acceptance.Status = FieldNavigationAcceptanceStatuses.ManuallyClosed;
        // Retain LastError: manual closure is not a controller success/cancel.
        await repository.SaveWithAuditAsync(acceptance, "ManualClosureConfirmed", result, ct);
        return result;
    }

    private async Task ValidateReplacementAsync(Entities.FieldNavigationAcceptance original,
        FieldNavigationReplacementReference reference, string[] path, CancellationToken ct)
    {
        if (reference.TaskId == Guid.Empty || reference.TaskId == original.Id || path.Length < 2 ||
            string.IsNullOrWhiteSpace(original.MapName) || string.IsNullOrWhiteSpace(original.MapMd5) ||
            original.DeviceTaskId != original.Id.ToString("N"))
            throw new InvalidOperationException("A distinct replacement and complete original identity are required.");
        var replacement = await repository.GetAsync(reference.TaskId, ct)
            ?? throw new InvalidOperationException("Replacement acceptance does not exist.");
        await repository.Database.Entry(replacement).ReloadAsync(ct);
        if (replacement.IsWorkflowLinked() || replacement.Status != FieldNavigationAcceptanceStatuses.Arrived ||
            replacement.PermitConsumedAtUtc is not { } dispatched ||
            replacement.DeviceTaskId != replacement.Id.ToString("N") ||
            replacement.CreatedAtUtc <= original.CreatedAtUtc || dispatched <= original.PermitConsumedAtUtc ||
            dispatched < replacement.CreatedAtUtc || replacement.UpdatedAtUtc < dispatched ||
            replacement.AgvId != original.AgvId || replacement.SourceStationId != original.SourceStationId ||
            replacement.TargetStationId != original.TargetStationId || replacement.MapName != original.MapName ||
            !string.Equals(replacement.MapMd5, original.MapMd5, StringComparison.OrdinalIgnoreCase) ||
            !(JsonSerializer.Deserialize<string[]>(replacement.PlannedPathJson) ?? []).SequenceEqual(path, StringComparer.Ordinal))
            throw new InvalidOperationException("A later consumed, arrived, independent replacement of the exact route is required.");
    }

    private static bool MatchesHistoricalEvidence(FieldNavigationManualClosureResult result,
        FieldNavigationReplacementReference reference) =>
        result.Evidence.MapEvidence is { IsControllerAuthoritative: true } map &&
        string.Equals(map.MapName, reference.MapName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(map.Md5, reference.MapMd5, StringComparison.OrdinalIgnoreCase) &&
        result.Evidence.MapIdentityCheckedAtUtc is { } checkedAt &&
        checkedAt <= result.ClosedAtUtc && checkedAt >= result.ClosedAtUtc.AddSeconds(-15) &&
        result.Evidence.ObservedAtUtc <= result.ClosedAtUtc &&
        result.Evidence.ObservedAtUtc >= result.ClosedAtUtc.AddSeconds(-15) &&
        result.Evidence.Segments.All(item => !string.IsNullOrWhiteSpace(item.DeviceTaskId)) &&
        result.Evidence.Segments.Select(item => item.DeviceTaskId).Distinct(StringComparer.Ordinal).Count() == result.Evidence.Segments.Count;
}

internal static class FieldNavigationLinkExtensions
{
    internal static bool IsWorkflowLinked(this Entities.FieldNavigationAcceptance item) =>
        item.WorkflowRunId.HasValue || item.WorkflowNodeExecutionId.HasValue || item.WorkflowDeviceOperationId.HasValue;
}
