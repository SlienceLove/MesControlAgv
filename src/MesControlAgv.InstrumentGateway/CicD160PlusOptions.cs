namespace MesControlAgv.InstrumentGateway;

public sealed class CicD160PlusOptions
{
    public const string SectionName = "IonChromatography";

    public bool Enabled { get; set; }
    public string InstrumentId { get; set; } = "CIC-D160-01";
    public string Model { get; set; } = "CIC-D160+";
    public string ComPort { get; set; } = "COM4";
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "None";
    public string StopBits { get; set; } = "One";
    public int TimeoutMs { get; set; } = 3000;
    public byte SlaveAddress { get; set; } = 1;
}
