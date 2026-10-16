namespace MesControlAgv.Mes.Entities;

public sealed class DetectionStandardRevisionRecord
{
    public Guid RevisionId { get; set; }
    public string MethodCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public int Revision { get; set; }
    public bool IsActive { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public List<DetectionIndicatorRuleRecord> Indicators { get; set; } = [];
}

public sealed class DetectionIndicatorRuleRecord
{
    public Guid IndicatorId { get; set; }
    public Guid RevisionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal? DetectionLimit { get; set; }
    public decimal? LowerLimit { get; set; }
    public decimal? UpperLimit { get; set; }
}

public sealed class DetectionAssessmentRecord
{
    public Guid RequestId { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public Guid SampleId { get; set; }
    public string SampleBarcode { get; set; } = string.Empty;
    public Guid? ExperimentJobId { get; set; }
    public Guid RevisionId { get; set; }
    public string MethodCode { get; set; } = string.Empty;
    public int Revision { get; set; }
    public string Actor { get; set; } = string.Empty;
    public DateTime AssessedAtUtc { get; set; }
    public string Verdict { get; set; } = string.Empty;
    public List<DetectionAssessmentItemRecord> Items { get; set; } = [];
}

public sealed class DetectionAssessmentItemRecord
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid IndicatorId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Value { get; set; }
    public string Unit { get; set; } = string.Empty;
    public decimal? DetectionLimit { get; set; }
    public decimal? LowerLimit { get; set; }
    public decimal? UpperLimit { get; set; }
    public string Verdict { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
