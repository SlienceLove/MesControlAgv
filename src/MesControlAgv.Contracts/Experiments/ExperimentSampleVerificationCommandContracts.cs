namespace MesControlAgv.Contracts.Experiments;

/// <summary>Stable validation and concurrency issue codes for sample verification.</summary>
public static class ExperimentSampleVerificationIssueCodes
{
    public const string SampleNotFound = "EXP-SAMPLE-NOT-FOUND";
    public const string BarcodeDuplicate = "EXP-SAMPLE-BARCODE-DUPLICATE";
    public const string BatchMismatch = "EXP-SAMPLE-BATCH-MISMATCH";
    public const string PositionDuplicate = "EXP-SAMPLE-POSITION-DUPLICATE";
    public const string VersionConflict = "EXP-SAMPLE-VERIFICATION-VERSION-CONFLICT";
    public const string VerificationRequired = "EXP-SAMPLE-VERIFICATION-REQUIRED";
    public const string VerificationInvalidated = "EXP-SAMPLE-VERIFICATION-INVALIDATED";
}

/// <summary>Optional query filters for centrally registered samples.</summary>
public sealed record QueryExperimentSamplesRequest
{
    public string? BatchId { get; init; }
}

/// <summary>Registers or updates a centrally managed sample before it is snapshotted.</summary>
public sealed record SaveExperimentSampleRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public ExperimentSample Sample { get; init; } = new();
}

/// <summary>Saves the current immutable row set for a task as a new revision.</summary>
public sealed record SaveExperimentSampleVerificationRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public IReadOnlyList<ExperimentSampleTaskRow> Rows { get; init; } = Array.Empty<ExperimentSampleTaskRow>();
}

/// <summary>Records a completed visual verification for the current snapshot revision.</summary>
public sealed record CompleteExperimentSampleVerificationRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public int Revision { get; init; }
    public string SnapshotHash { get; init; } = string.Empty;
    public string? VerificationNote { get; init; }
}
