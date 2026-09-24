namespace MesControlAgv.Contracts;

/// <summary>Sample row sent to ShineLab's Config command.</summary>
public sealed record ShineLabSampleData(
    string SampleId,
    string SampleName,
    string Type,
    int Position,
    string? MPos,
    string Channel,
    string? InstrumentMethod = null,
    string? ProcessingMethod = null,
    string? DetectionMethod = null,
    decimal? InjectionVolume = null,
    string? InjectionVolumeUnit = null);

public sealed record ShineLabConfigRequest(
    string TaskUuid,
    IReadOnlyList<ShineLabSampleData> SampleData,
    string? InstrumentMethod = null,
    string? ProcessingMethod = null,
    string? DetectionMethod = null);

public sealed record ShineLabCommandRequest(
    string TaskUuid,
    int Action,
    string? SampleId = null,
    string? SampleName = null,
    string? Channel = null,
    string? DetectionMethod = null,
    string? CleanTime = null,
    string? StrAction = null,
    string? Param = null);

public sealed record ShineLabCommandResponse(
    string StrId,
    string StrMethod,
    string EquipmentCode,
    bool Success,
    string? Message,
    System.Text.Json.JsonElement Body);

public sealed record ShineLabTaskCreateRequest(
    string EquipmentCode,
    string TaskUuid,
    IReadOnlyList<ShineLabSampleData> SampleData,
    string? InstrumentMethod = null,
    string? ProcessingMethod = null,
    string? DetectionMethod = null);

public sealed record ShineLabTaskResponse(
    Guid Id,
    string TaskUuid,
    string EquipmentCode,
    string Status,
    string CurrentStage,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? LastError);

public sealed record ShineLabTaskEventResponse(
    Guid Id,
    string TaskUuid,
    string EventType,
    string PayloadJson,
    DateTimeOffset OccurredAtUtc);

public sealed record ShineLabTaskDetailResponse(
    ShineLabTaskResponse Task,
    IReadOnlyList<ShineLabTaskEventResponse> Events,
    IReadOnlyList<ShineLabTaskSampleResponse>? Samples = null,
    string ResultState = "NotTracked");

public sealed record ShineLabTaskSampleResponse(
    int RowNumber,
    string SourceSampleId,
    string SampleId,
    string SampleName,
    bool ConfigAttempted,
    bool SampleFinishObserved,
    bool TaskFinishObserved,
    bool EndMissionObserved,
    string? ResultJson,
    bool ResultConflict,
    long? LastReceivedAtUtcTicks);
