using System.Text.Json;

namespace MesControlAgv.Application;

/// <summary>Preserves structured Adapter errors at the MES boundary.</summary>
public sealed class SampleWorkstationGatewayException(
    int statusCode,
    string errorCode,
    string detail,
    bool outcomeUnknown = false,
    int? vendorCode = null,
    JsonElement? vendorData = null) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;
    public string ErrorCode { get; } = errorCode;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
    public int? VendorCode { get; } = vendorCode;
    public JsonElement? VendorData { get; } = vendorData?.Clone();
}
