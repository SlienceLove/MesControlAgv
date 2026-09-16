using System.Text.Json;
using MesControlAgv.Adapter.Entities;
using MesControlAgv.Contracts;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Adapter.Services;

public sealed partial class AdapterService
{
    private Task<AdapterTaskManualClosure?> FindManualClosureAsync(Guid taskId, CancellationToken ct) =>
        _database.TaskManualClosures.AsNoTracking().SingleOrDefaultAsync(item => item.TaskId == taskId, ct);

    private async Task RejectManuallyClosedTaskAsync(Guid taskId, CancellationToken ct)
    {
        if (await FindManualClosureAsync(taskId, ct) is not null)
            throw new InvalidOperationException("A manually closed task cannot be dispatched or controlled again.");
    }

    public Task<FieldNavigationManualClosureResult> CloseUnconfirmedNavigationAsync(
        Guid taskId, FieldNavigationManualCloseCommand command, CancellationToken ct) =>
        _physicalSessionGate.RunAsync(() => CloseUnconfirmedNavigationCoreAsync(taskId, command, ct), ct);

    private async Task<FieldNavigationManualClosureResult> CloseUnconfirmedNavigationCoreAsync(
        Guid taskId, FieldNavigationManualCloseCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (taskId == Guid.Empty || command.RequestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.Actor) || command.Actor.Length > 256 ||
            string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 2000 ||
            command.PlannedPath is not { Count: >= 2 })
            throw new ArgumentException("A physical task, request ID, actor, reason and exact route are required.");
        command = command with { Actor = command.Actor.Trim(), Reason = command.Reason.Trim() };
        var json = JsonSerializer.Serialize(command);
        var previous = await FindManualClosureAsync(taskId, ct);
        if (previous is not null)
        {
            if (previous.RequestId != command.RequestId || previous.RequestJson != json)
                throw new InvalidOperationException("Manual closure request conflicts with the persisted disposition.");
            _scheduler.Release(taskId); // Also finish cleanup after a lost acknowledgement.
            return JsonSerializer.Deserialize<FieldNavigationManualClosureResult>(previous.ResultJson)!;
        }
        // Returning an immutable receipt does not authorize a new disposition.
        // HTTP read-only mode still rejects POSTs at its existing middleware.
        EnsureMutationIsAllowed("manual navigation disposition");
        if (_profile.PhysicalAcceptance is null)
            throw new InvalidOperationException("Manual disposition requires a physical profile.");
        if (await _database.TaskManualClosures.AnyAsync(item => item.RequestId == command.RequestId, ct))
            throw new InvalidOperationException("Manual closure request ID already belongs to another task.");
        var task = await _database.Tasks.FindAsync([taskId], ct)
            ?? throw new KeyNotFoundException("The exact Adapter task does not exist.");
        await _database.Entry(task).ReloadAsync(ct);
        var path = DeserializePath(task.PathJson);
        if (task.State != "unknown" || task.AgvId != command.AgvId ||
            command.AgvId != GetDefaultAgvId() || _profile.Agvs.Count(agv => agv.Enabled) != 1 ||
            task.TargetStationId != command.TargetStationId || path is not { Count: >= 2 } ||
            !path.SequenceEqual(command.PlannedPath, StringComparer.Ordinal) ||
            path[0] != command.SourceStationId || path[^1] != command.TargetStationId)
            throw new InvalidOperationException("Only an exactly matched Unknown single-AGV route may be manually closed.");
        if (_device is not IAgvTaskAbsenceEvidenceClient reader)
            throw new InvalidOperationException("The driver cannot prove exact controller task absence.");

        ControllerMapEvidenceResponse? map = null;
        DateTimeOffset? mapCheckedAt = null;
        if (command.Replacement is { } reference)
        {
            if (reference.TaskId == Guid.Empty || reference.TaskId == taskId ||
                string.IsNullOrWhiteSpace(reference.MapName) || string.IsNullOrWhiteSpace(reference.MapMd5))
                throw new InvalidOperationException("A distinct completed replacement and map identity are required.");
            var replacement = await _database.Tasks.AsNoTracking().SingleOrDefaultAsync(
                item => item.TaskId == reference.TaskId, ct);
            if (replacement is null || replacement.State != "arrived" || replacement.AgvId != task.AgvId ||
                replacement.DeviceTaskId != reference.TaskId.ToString("N") ||
                task.DeviceTaskId != taskId.ToString("N") || replacement.TargetStationId != task.TargetStationId ||
                DeserializePath(replacement.PathJson) is not { } replacementPath ||
                !replacementPath.SequenceEqual(path, StringComparer.Ordinal))
                throw new InvalidOperationException("The Adapter has no matching arrived replacement route.");
            if (_device is not IControllerMapEvidenceDeviceClient mapReader)
                throw new InvalidOperationException("The driver cannot verify the current map identity.");
            // This read rechecks current map identity even when the full catalog is cached.
            map = await mapReader.GetControllerMapEvidenceAsync(ct);
            mapCheckedAt = DateTimeOffset.UtcNow;
            if (map is not { IsControllerAuthoritative: true } ||
                map.StationIds is null || map.DirectedEdges is null ||
                !string.Equals(map.MapName, reference.MapName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(map.Md5, reference.MapMd5, StringComparison.OrdinalIgnoreCase) ||
                path.Any(station => !map.StationIds.Contains(station, StringComparer.Ordinal)) ||
                path.Zip(path.Skip(1)).Any(edge => !map.DirectedEdges.Any(
                    item => item.From == edge.First && item.To == edge.Second)))
                throw new InvalidOperationException("Current controller map does not match the historical route.");
        }
        var evidence = command.Replacement is null
            ? await reader.ReadTaskAbsenceAsync(taskId, path, ct)
            : (await reader.ReadTaskAbsenceAtDestinationAsync(taskId, path, ct)) with
                { MapEvidence = map, MapIdentityCheckedAtUtc = mapCheckedAt };
        var expectedIds = TcpAgvClient.GetRouteDeviceTaskIds(taskId, path);
        if (evidence.TaskId != taskId || evidence.CurrentStationId != (command.Replacement is null ? path[0] : path[^1]) ||
            !evidence.Path.SequenceEqual(path, StringComparer.Ordinal) || evidence.Segments.Count != path.Count - 1 ||
            evidence.Segments.Any(item => item.VendorStatus != 404 || string.IsNullOrWhiteSpace(item.DeviceTaskId)) ||
            evidence.Segments.Select(item => item.DeviceTaskId).Distinct(StringComparer.Ordinal).Count() != path.Count - 1 ||
            expectedIds.Any(id => evidence.Segments.Count(item => item.DeviceTaskId == id) != 1) ||
            evidence.ObservedAtUtc > DateTimeOffset.UtcNow.AddSeconds(1) ||
            evidence.ObservedAtUtc < DateTimeOffset.UtcNow.AddSeconds(-15) ||
            (mapCheckedAt.HasValue && mapCheckedAt.Value < DateTimeOffset.UtcNow.AddSeconds(-15)))
            throw new InvalidOperationException("The driver did not return fresh, complete matching absence evidence.");
        var result = new FieldNavigationManualClosureResult(taskId, command.RequestId, command.Actor,
            command.Reason, task.AgvId, path[0], path[^1], task.LastError, evidence, DateTimeOffset.UtcNow, command.Replacement);
        _database.TaskManualClosures.Add(new AdapterTaskManualClosure
        {
            TaskId = taskId, RequestId = command.RequestId, RequestJson = json,
            ResultJson = JsonSerializer.Serialize(result)
        });
        task.State = FieldNavigationAcceptanceStatuses.ManuallyClosed;
        // SaveChanges atomically persists the immutable disposition and state.
        // LastError and route remain untouched; this is NOT device cancellation.
        await _database.SaveChangesAsync(ct);
        _scheduler.Release(taskId);
        return result;
    }
}
