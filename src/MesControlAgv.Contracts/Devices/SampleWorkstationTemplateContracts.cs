namespace MesControlAgv.Contracts;

/// <summary>Only the vendor XLSX boundary; central DTOs and HTTP volumes remain µL.</summary>
public enum SampleWorkstationTemplateVolumeUnit
{
    Microliters = 0,
    Milliliters = 1
}

/// <summary>One source-to-target transfer. Module codes are NOT sample identities.</summary>
public sealed record SampleWorkstationTransferRow(
    string LiquidCode, string TipModule, int TipX, int TipY,
    string SourceModule, int SourceX, int SourceY,
    string TargetModule, int TargetX, int TargetY, int VolumeMicroliters);

public sealed record SampleWorkstationTaskTemplate(
    string TaskNo, string TaskName, IReadOnlyList<SampleWorkstationTransferRow> Transfers);

public sealed record SampleWorkstationTemplateResponse(
    string DeviceId, string FileName, byte[] FileContent, string FileSha256,
    SampleWorkstationTaskTemplate Template, DateTimeOffset ObservedAtUtc)
{
    public SampleWorkstationTemplateVolumeUnit FileVolumeUnit { get; init; }
}
