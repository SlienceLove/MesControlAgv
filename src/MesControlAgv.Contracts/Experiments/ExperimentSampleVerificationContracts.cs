namespace MesControlAgv.Contracts.Experiments;

/// <summary>Availability of a centrally registered experiment sample.</summary>
public enum ExperimentSampleStatus
{
    Active,
    Disabled
}

/// <summary>Minimal centrally registered sample identity used by task snapshots.</summary>
public sealed record ExperimentSample
{
    public Guid SampleId { get; init; }
    public string BusinessSampleId { get; init; } = string.Empty;
    public string BatchId { get; init; } = string.Empty;
    public string Barcode { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public ExperimentSampleStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public enum ExperimentSampleVerificationStatus
{
    Draft,
    ReadyForVerification,
    Verified,
    Invalidated
}

/// <summary>One stable, ordered sample row captured for an experiment task.</summary>
public sealed record ExperimentSampleTaskRow
{
    public Guid RowId { get; init; }
    public Guid SampleId { get; init; }
    /// <summary>Business sample identifier captured as part of verification identity.</summary>
    public string BusinessSampleId { get; init; } = string.Empty;
    public string SampleBarcode { get; init; } = string.Empty;
    public string Position { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int Order { get; init; }
}

/// <summary>One row-level reason a task snapshot remains a draft.</summary>
public sealed record ExperimentSampleVerificationValidationIssue
{
    public Guid? RowId { get; init; }
    public int? Order { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Immutable task-sample snapshot. Later changes create a new revision rather
/// than overwriting the rows that an operator verified.
/// </summary>
public sealed record ExperimentSampleVerification
{
    public Guid VerificationId { get; init; }
    public Guid ExperimentJobId { get; init; }
    public int Revision { get; init; }
    public ExperimentSampleVerificationStatus Status { get; init; }
    public IReadOnlyList<ExperimentSampleTaskRow> Rows { get; init; } = Array.Empty<ExperimentSampleTaskRow>();
    public IReadOnlyList<ExperimentSampleVerificationValidationIssue> ValidationIssues { get; init; } = Array.Empty<ExperimentSampleVerificationValidationIssue>();
    public string SnapshotHash { get; init; } = string.Empty;
    public string? VerifiedBy { get; init; }
    public DateTimeOffset? VerifiedAt { get; init; }
    public string? VerificationNote { get; init; }
    public DateTimeOffset? InvalidatedAt { get; init; }
    public string? InvalidationReason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
