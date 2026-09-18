using MesControlAgv.Contracts;

namespace MesControlAgv.Contracts.Experiments;

public enum ExperimentWorkstationPreparationStatus
{
    Prepared,
    Importing,
    Imported,
    Unknown
}

public sealed record WorkstationTemplateSourceKey
{
    public string Module { get; init; } = string.Empty;
    public int X { get; init; }
    public int Y { get; init; }
}

/// <summary>
/// Binds one verified central sample to one physical barcode slot. TemplateSource
/// is required when the bottle is used by the template and omitted only for an
/// intentionally unused second bottle.
/// </summary>
public sealed record PrepareWorkstationBottleBinding
{
    public int BottleNumber { get; init; }
    public Guid SampleId { get; init; }
    public WorkstationTemplateSourceKey? TemplateSource { get; init; }
}

public sealed record PrepareExperimentWorkstationTaskRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public string SourceTaskNo { get; init; } = string.Empty;
    public int VerificationRevision { get; init; }
    public string VerificationSnapshotHash { get; init; } = string.Empty;
    /// <summary>Null clones the captured template rows; a supplied list is the edited task table.</summary>
    public IReadOnlyList<SampleWorkstationTransferRow>? Transfers { get; init; }
    public IReadOnlyList<PrepareWorkstationBottleBinding> BottleBindings { get; init; } =
        Array.Empty<PrepareWorkstationBottleBinding>();
}

public sealed record ImportExperimentWorkstationTaskRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed record PreparedWorkstationBottleBinding
{
    public int BottleNumber { get; init; }
    public bool IsUsedByTemplate { get; init; }
    public WorkstationTemplateSourceKey? TemplateSource { get; init; }
    public Guid VerificationRowId { get; init; }
    public Guid SampleId { get; init; }
    public string BusinessSampleId { get; init; } = string.Empty;
    public string SampleBarcode { get; init; } = string.Empty;
}

/// <summary>Template transfer plus exact source provenance; this is not an execution-success claim.</summary>
public sealed record PreparedWorkstationTransfer
{
    public int Order { get; init; }
    public SampleWorkstationTransferRow Transfer { get; init; } =
        new(string.Empty, string.Empty, 0, 0, string.Empty, 0, 0, string.Empty, 0, 0, 0);
    public int BottleNumber { get; init; }
    public Guid VerificationRowId { get; init; }
    public Guid SourceSampleId { get; init; }
    public string SourceBusinessSampleId { get; init; } = string.Empty;
    public string SourceSampleBarcode { get; init; } = string.Empty;
}

public sealed record ExperimentWorkstationPreparationPayload
{
    public string SourceTemplateFileName { get; init; } = string.Empty;
    public string SourceTemplateFileSha256 { get; init; } = string.Empty;
    public SampleWorkstationTaskTemplate SourceTemplate { get; init; } =
        new(string.Empty, string.Empty, Array.Empty<SampleWorkstationTransferRow>());
    public string GeneratedTemplateFileName { get; init; } = string.Empty;
    public string GeneratedTemplateFileSha256 { get; init; } = string.Empty;
    public SampleWorkstationTaskTemplate GeneratedTemplate { get; init; } =
        new(string.Empty, string.Empty, Array.Empty<SampleWorkstationTransferRow>());
    public IReadOnlyList<PreparedWorkstationBottleBinding> BottleBindings { get; init; } =
        Array.Empty<PreparedWorkstationBottleBinding>();
    public IReadOnlyList<PreparedWorkstationTransfer> Transfers { get; init; } =
        Array.Empty<PreparedWorkstationTransfer>();
}

public sealed record ExperimentWorkstationPreparation
{
    public Guid PreparationId { get; init; }
    public Guid ExperimentJobId { get; init; }
    public long Revision { get; init; }
    public Guid WorkflowId { get; init; }
    public int WorkflowVersion { get; init; }
    public Guid ScheduleEntryId { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string VendorTaskNo { get; init; } = string.Empty;
    public Guid VerificationId { get; init; }
    public int VerificationRevision { get; init; }
    public string VerificationSnapshotHash { get; init; } = string.Empty;
    public ExperimentWorkstationPreparationPayload Payload { get; init; } = new();
    public string PayloadHash { get; init; } = string.Empty;
    public ExperimentWorkstationPreparationStatus Status { get; init; }
    public Guid PreparedRequestId { get; init; }
    public Guid? ImportRequestId { get; init; }
    public DateTimeOffset PreparedAt { get; init; }
    public DateTimeOffset? ImportingAt { get; init; }
    public DateTimeOffset? ImportedAt { get; init; }
    public DateTimeOffset? UnknownAt { get; init; }
    public string? LastError { get; init; }
    public bool IsIdempotentReplay { get; init; }
}

public static class ExperimentWorkstationPreparationIssueCodes
{
    public const string VersionConflict = "EXP-WORKSTATION-PREPARATION-VERSION-CONFLICT";
    public const string InvalidTemplateBinding = "EXP-WORKSTATION-PREPARATION-INVALID-BINDING";
    public const string PreparationNotImported = "EXP-WORKSTATION-PREPARATION-NOT-IMPORTED";
    public const string ImportOutcomeUnknown = "EXP-WORKSTATION-PREPARATION-IMPORT-UNKNOWN";
    public const string DeviceBusy = "EXP-WORKSTATION-PREPARATION-DEVICE-BUSY";
    public const string RuntimeBindingInvalid = "EXP-WORKSTATION-PREPARATION-RUNTIME-BINDING-INVALID";
}
