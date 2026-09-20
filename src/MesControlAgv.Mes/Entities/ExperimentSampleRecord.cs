namespace MesControlAgv.Mes.Entities;

public sealed class ExperimentSampleRecord
{
    private string _barcode = string.Empty;

    public Guid SampleId { get; set; }
    public string BusinessSampleId { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public string Barcode
    {
        get => _barcode;
        set
        {
            _barcode = value ?? string.Empty;
            NormalizedBarcode = _barcode.Trim();
        }
    }

    /// <summary>Trim-only barcode key used by the unique database constraint.</summary>
    public string NormalizedBarcode { get; private set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
