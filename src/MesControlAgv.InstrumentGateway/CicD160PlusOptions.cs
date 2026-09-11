namespace MesControlAgv.InstrumentGateway;

public sealed class CicD160PlusOptions
{
    public const string SectionName = "IonChromatography";
    public const string PhysicalAcceptanceEnvironmentName = "PhysicalAcceptance";

    public bool Enabled { get; set; }
    public string InstrumentId { get; set; } = "CIC-D160-01";
    /// <summary>厂家正式写入模块未交付前保持 protocol_pending；仅允许只读查询。</summary>
    public string ProtocolStatus { get; set; } = "protocol_pending";
    public bool ControlEnabled { get; set; }
    public string Model { get; set; } = "CIC-D160+";
    public string ComPort { get; set; } = "COM4";
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "None";
    public string StopBits { get; set; } = "One";
    public int TimeoutMs { get; set; } = 3000;
    public byte SlaveAddress { get; set; } = 1;
    public Dictionary<string, CicD160PlusDeviceGate> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasValidPendingDeviceGates() =>
        Devices.Count == 2
        && new HashSet<string>(Devices.Keys, StringComparer.OrdinalIgnoreCase)
            .SetEquals(new[] { "CIC-D160-01", "CIC-D160-02" })
        && Devices.All(pair =>
            string.Equals(pair.Key, pair.Value.DeviceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.Value.ProtocolStatus, "protocol_pending", StringComparison.Ordinal)
            && !pair.Value.ControlEnabled);

    public static bool ValidatePhysicalAcceptanceDevices(CicD160PlusOptions options, bool physicalAcceptance) =>
        !physicalAcceptance || options.HasValidPendingDeviceGates();
}

public sealed class CicD160PlusDeviceGate
{
    public string DeviceId { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string ProtocolStatus { get; set; } = "protocol_pending";
    public bool ControlEnabled { get; set; }
}
