namespace MesControlAgv.Mes.Services;

/// <summary>
/// Central-side listener for the resident ShineLab TCP client.  Serial
/// communication remains entirely inside ShineLab; this service receives the
/// deployed LF-delimited YhLoop JSON and retains bounded 55AA compatibility.
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
    /// <summary>
    /// Stand-in identity for a deployed YhLoop client that sends every frame
    /// with an empty equipmentCode.  Disabled by default: enabling it asserts
    /// that exactly one instrument reaches this listener, because every
    /// unidentified frame is attributed to this code.  Remove it once the
    /// vendor client populates equipmentCode itself.
    /// </summary>
    public string FallbackEquipmentCode { get; set; } = string.Empty;
}
