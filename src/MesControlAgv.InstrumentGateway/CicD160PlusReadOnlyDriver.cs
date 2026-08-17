using System.Buffers.Binary;
using System.Text;
using MesControlAgv.Application;
using Microsoft.Extensions.Options;

namespace MesControlAgv.InstrumentGateway;

public sealed record CicD160PlusIdentity(
    string InstrumentId,
    string Model,
    string ObservedIdentifier,
    DateTimeOffset ObservedAtUtc,
    string RequestHex,
    string ResponseHex);

public sealed record CicD160PlusStatusResult(
    IonChromatographyStatusSnapshot Status,
    IReadOnlyList<ModbusReadEvidence> Evidence);

public sealed record ModbusReadEvidence(
    string Name,
    ushort StartAddress,
    ushort RegisterCount,
    string RequestHex,
    string ResponseHex,
    long ElapsedMs);

public sealed class CicD160PlusReadOnlyDriver(
    IReadOnlyModbusTransport transport,
    IOptions<CicD160PlusOptions> configuredOptions)
{
    private const ushort IdentityStart = 0x1900;
    private const ushort IdentityCount = 20;
    private const ushort DetectorStart = 0x1770;
    private const ushort DetectorCount = 12;
    private const ushort ProcessStart = 0x17D4;
    private const ushort ProcessCount = 18;
    private readonly CicD160PlusOptions _options = configuredOptions.Value;

    public async Task<CicD160PlusIdentity> IdentifyAsync(CancellationToken cancellationToken)
    {
        var read = await transport.ReadInputRegistersAsync(IdentityStart, IdentityCount, cancellationToken);
        return new(
            _options.InstrumentId,
            _options.Model,
            DecodeObservedIdentifier(read.Data),
            DateTimeOffset.UtcNow,
            Convert.ToHexString(read.Request),
            Convert.ToHexString(read.Response));
    }

    public async Task<CicD160PlusStatusResult> ReadStatusAsync(CancellationToken cancellationToken)
    {
        var identity = await transport.ReadInputRegistersAsync(IdentityStart, IdentityCount, cancellationToken);
        var detector = await transport.ReadInputRegistersAsync(DetectorStart, DetectorCount, cancellationToken);
        var process = await transport.ReadInputRegistersAsync(ProcessStart, ProcessCount, cancellationToken);

        var conductivity = ReadSingleLittleEndian(detector.Data, 0);
        var totalConductivity = ReadSingleLittleEndian(detector.Data, 12);
        var columnTemperature = BinaryPrimitives.ReadUInt16BigEndian(process.Data.AsSpan(8, 2)) / 100d;
        var flow = BinaryPrimitives.ReadUInt16BigEndian(process.Data.AsSpan(14, 2)) / 1000d;
        var observedAt = DateTimeOffset.UtcNow;

        var status = new IonChromatographyStatusSnapshot(
            _options.InstrumentId,
            _options.Model,
            DecodeObservedIdentifier(identity.Data),
            Online: true,
            DeviceState: "ReadOnlyObserved",
            PortOwned: false,
            ObservedAtUtc: observedAt,
            Pressure: null,
            ColumnTemperature: columnTemperature,
            DetectorTemperature: null,
            Alarm: null,
            Conductivity: conductivity,
            TotalConductivity: totalConductivity,
            Flow: flow,
            MappingConfidence: "CaptureCorrelatedCandidate");

        return new(status,
        [
            ToEvidence("Identity", identity),
            ToEvidence("Detector", detector),
            ToEvidence("Process", process)
        ]);
    }

    private static ModbusReadEvidence ToEvidence(string name, ModbusReadResult read) => new(
        name,
        read.StartAddress,
        read.RegisterCount,
        Convert.ToHexString(read.Request),
        Convert.ToHexString(read.Response),
        read.ElapsedMs);

    private static string DecodeObservedIdentifier(byte[] data)
    {
        var length = Array.FindIndex(data, value => value == 0);
        if (length < 0) length = data.Length;
        var text = Encoding.ASCII.GetString(data, 0, length).Trim();
        return text.Length == 0 ? "Unknown" : text;
    }

    private static float ReadSingleLittleEndian(byte[] data, int offset)
    {
        if (offset < 0 || offset + sizeof(float) > data.Length)
            throw new FormatException("Captured D160+ float field is outside the response data.");
        return BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, sizeof(float)));
    }
}
