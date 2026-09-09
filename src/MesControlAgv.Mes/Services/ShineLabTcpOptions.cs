namespace MesControlAgv.Mes.Services;

/// <summary>
/// Central-side listener for the resident ShineLab TCP client.  Serial
/// communication remains entirely inside ShineLab; this service only receives
/// native 55AA-framed JSON status/events.
/// </summary>
public sealed class ShineLabTcpOptions
{
    public const string SectionName = "ShineLabTcp";

    public bool Enabled { get; set; }
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 5500;
    public int StaleAfterSeconds { get; set; } = 10;
    public int CommandTimeoutMs { get; set; } = 10000;
    public int ReadBufferBytes { get; set; } = 8192;
    /// <summary>
    /// Diagnostic-only compatibility probe. Disabled by default because the
    /// production protocol expects the downstream client to initiate
    /// Certification.
    /// </summary>
    public bool SendCertificationOnConnect { get; set; }
    public string ServerEquipmentCode { get; set; } = "SHDC-IRAY-C";
}
