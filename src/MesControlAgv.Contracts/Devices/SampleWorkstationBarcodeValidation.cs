namespace MesControlAgv.Contracts;

/// <summary>Both bottle identities must be explicitly supplied before a workstation start.</summary>
public static class SampleWorkstationBarcodeValidation
{
    public static string? MissingMessage(SampleWorkstationTaskBarcodes? barcodes)
    {
        var first = string.IsNullOrWhiteSpace(barcodes?.SampleBarcode1);
        var second = string.IsNullOrWhiteSpace(barcodes?.SampleBarcode2);
        return first || second
            ? $"缺少{(first && second ? "1号、2号" : first ? "1号" : "2号")}样品瓶条码；未发送设备请求。请填写真实条码或明确的测试编号。"
            : null;
    }

    public static SampleWorkstationTaskBarcodes Normalize(SampleWorkstationTaskBarcodes? barcodes)
    {
        if (MissingMessage(barcodes) is { } message) throw new ArgumentException(message, nameof(barcodes));
        return barcodes! with { SampleBarcode1 = barcodes.SampleBarcode1.Trim(), SampleBarcode2 = barcodes.SampleBarcode2.Trim() };
    }
}
