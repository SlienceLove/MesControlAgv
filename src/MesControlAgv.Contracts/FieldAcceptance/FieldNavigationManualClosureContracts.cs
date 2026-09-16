using System.Text.Json.Serialization;

namespace MesControlAgv.Contracts;

public sealed record FieldNavigationManualCloseRequest(Guid RequestId, string Actor, string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ReplacementAcceptanceId = null);
public sealed record FieldNavigationReplacementReference(Guid TaskId, string MapName, string MapMd5);
public sealed record FieldNavigationManualCloseCommand(
    Guid RequestId, string Actor, string Reason, string AgvId,
    string SourceStationId, string TargetStationId, IReadOnlyList<string> PlannedPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FieldNavigationReplacementReference? Replacement = null);
public sealed record AgvAbsentSegment(string DeviceTaskId, int VendorStatus);
public sealed record AgvTaskAbsenceEvidence(
    Guid TaskId, string CurrentStationId, IReadOnlyList<string> Path,
    IReadOnlyList<AgvAbsentSegment> Segments, DateTimeOffset ObservedAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ControllerMapEvidenceResponse? MapEvidence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? MapIdentityCheckedAtUtc = null);
public sealed record FieldNavigationManualClosureResult(
    Guid TaskId, Guid RequestId, string Actor, string Reason, string AgvId,
    string SourceStationId, string TargetStationId, string? PreviousError,
    AgvTaskAbsenceEvidence Evidence, DateTimeOffset ClosedAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FieldNavigationReplacementReference? Replacement = null)
{
    public string State => FieldNavigationAcceptanceStatuses.ManuallyClosed;
}
