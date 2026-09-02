namespace MesControlAgv.Mes.Services;

/// <summary>
/// Central-side listener for the resident ShineLab TCP client.  Serial
/// communication remains entirely inside ShineLab; this service only receives
/// newline-delimited JSON status/events.
/// </summary>
public sealed class ShineLabTcpOptions
{
    public const string SectionName = "ShineLabTcp";

    public bool Enabled { get; set; }
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 5500;
    public int StaleAfterSeconds { get; set; } = 10;
    public int CommandTimeoutMs { get; set; } = 10000;
}
