using System.Text.Json.Serialization;

namespace MesControlAgv.Contracts.Materials;

public enum DetectionVerdict
{
    Indeterminate,
    Pass,
    Fail
}

public sealed record DetectionIndicatorRule
{
    public Guid IndicatorId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public decimal? DetectionLimit { get; init; }
    public decimal? LowerLimit { get; init; }
    public decimal? UpperLimit { get; init; }
}

public sealed record DetectionStandardRevision
{
    public Guid RevisionId { get; init; }
    public string MethodCode { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Category { get; init; }
    public int Revision { get; init; }
    public bool IsActive { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyList<DetectionIndicatorRule> Indicators { get; init; } = [];
}

public sealed record SaveDetectionStandardRequest
{
    public Guid? BaseRevisionId { get; init; }
    public string MethodCode { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Category { get; init; }
    public string Actor { get; init; } = string.Empty;
    public IReadOnlyList<DetectionIndicatorRule> Indicators { get; init; } = [];
}

public sealed record DeactivateDetectionStandardRequest(string MethodCode, Guid RevisionId);

public sealed record DetectionMeasurement
{
    public Guid IndicatorId { get; init; }
    public decimal Value { get; init; }
    public string Unit { get; init; } = string.Empty;
}

public sealed record DetectionAssessmentRequest
{
    public Guid RequestId { get; init; }
    public Guid SampleId { get; init; }
    public Guid RevisionId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public IReadOnlyList<DetectionMeasurement> Measurements { get; init; } = [];
}

public sealed record DetectionAssessmentItem
{
    public Guid IndicatorId { get; init; }
    public string Name { get; init; } = string.Empty;
    public decimal Value { get; init; }
    public string Unit { get; init; } = string.Empty;
    public decimal? DetectionLimit { get; init; }
    public decimal? LowerLimit { get; init; }
    public decimal? UpperLimit { get; init; }
    public DetectionVerdict Verdict { get; init; }
    public string Reason { get; init; } = string.Empty;
    [JsonIgnore]
    public string VerdictText => Verdict switch
    {
        DetectionVerdict.Pass => "合格",
        DetectionVerdict.Fail => "不合格",
        _ => "无法判定"
    };
}

public sealed record DetectionAssessment
{
    public Guid RequestId { get; init; }
    public Guid SampleId { get; init; }
    public string SampleBarcode { get; init; } = string.Empty;
    public Guid? ExperimentJobId { get; init; }
    public Guid RevisionId { get; init; }
    public string MethodCode { get; init; } = string.Empty;
    public int Revision { get; init; }
    public string Actor { get; init; } = string.Empty;
    public DateTimeOffset AssessedAt { get; init; }
    public DetectionVerdict Verdict { get; init; }
    public IReadOnlyList<DetectionAssessmentItem> Items { get; init; } = [];
    [JsonIgnore]
    public string VerdictText => Verdict switch
    {
        DetectionVerdict.Pass => "合格",
        DetectionVerdict.Fail => "不合格",
        _ => "无法判定"
    };
}
