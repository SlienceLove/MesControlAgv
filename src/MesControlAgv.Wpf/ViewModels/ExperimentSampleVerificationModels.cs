using MesControlAgv.Contracts.Experiments;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>Presentation-only projection of a row in the current MES snapshot.</summary>
public sealed class ExperimentSampleVerificationRowViewModel(
    ExperimentSampleTaskRow row,
    ExperimentSample? sample,
    IReadOnlyList<ExperimentSampleVerificationValidationIssue> issues)
{
    public ExperimentSampleTaskRow Row { get; } = row;
    public Guid RowId => Row.RowId;
    public Guid SampleId => Row.SampleId;
    public string SampleNumber => !string.IsNullOrWhiteSpace(Row.BusinessSampleId)
        ? Row.BusinessSampleId
        : sample?.BusinessSampleId ?? string.Empty;
    public string Barcode => Row.SampleBarcode;
    public string Position => Row.Position;
    public string DisplayName => Row.DisplayName;
    public int Order => Row.Order;
    public string Validation => issues.Count == 0
        ? "通过"
        : string.Join("；", issues.Select(issue => $"[{issue.Code}] {issue.Message}"));
    public bool HasValidationIssue => issues.Count != 0;
}
