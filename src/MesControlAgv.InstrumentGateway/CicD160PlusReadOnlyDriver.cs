using System.Buffers.Binary;
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
    private readonly CicD160PlusOptions _options = configuredOptions.Value;

    public async Task<CicD160PlusIdentity> IdentifyAsync(CancellationToken cancellationToken)
    {
        var read = await transport.ReadInputRegistersAsync(
            CicD160PlusProtocolMap.IdentityStart,
            CicD160PlusProtocolMap.IdentityCount,
            cancellationToken);
        return new(
            _options.InstrumentId,
            _options.Model,
            CicD160PlusProtocolMap.DecodeObservedIdentifier(read.Data),
            DateTimeOffset.UtcNow,
            Convert.ToHexString(read.Request),
            Convert.ToHexString(read.Response));
    }

    public async Task<CicD160PlusStatusResult> ReadStatusAsync(CancellationToken cancellationToken)
    {
        var identity = await transport.ReadInputRegistersAsync(
            CicD160PlusProtocolMap.IdentityStart,
            CicD160PlusProtocolMap.IdentityCount,
            cancellationToken);
        var detector = await transport.ReadInputRegistersAsync(
            CicD160PlusProtocolMap.DetectorStart,
            CicD160PlusProtocolMap.DetectorCount,
            cancellationToken);
        var process = await transport.ReadInputRegistersAsync(
            CicD160PlusProtocolMap.ProcessStart,
            CicD160PlusProtocolMap.ProcessCount,
            cancellationToken);
        var suppressor = await transport.ReadInputRegistersAsync(
            CicD160PlusProtocolMap.SuppressorStart,
            CicD160PlusProtocolMap.SuppressorCount,
            cancellationToken);

        var conductivity = ReadSingleLittleEndian(detector.Data, 0);
        var totalConductivity = ReadSingleLittleEndian(detector.Data, 12);
        var processState = CicD160PlusProtocolMap.DecodeProcess(process.Data);
        var suppressorState = CicD160PlusProtocolMap.DecodeSuppressor(suppressor.Data);
        var observedAt = DateTimeOffset.UtcNow;

        var status = new IonChromatographyStatusSnapshot(
            _options.InstrumentId,
            _options.Model,
            CicD160PlusProtocolMap.DecodeObservedIdentifier(identity.Data),
            Online: true,
            DeviceState: "ReadOnlyObserved",
            PortOwned: false,
            ObservedAtUtc: observedAt,
            Pressure: (double)CicD160PlusProtocolMap.DecodePressureMpa(processState.PressureRaw),
            ColumnTemperature: processState.ColumnTemperatureActualRaw / 100d,
            DetectorTemperature: null,
            Alarm: null,
            Conductivity: conductivity,
            TotalConductivity: totalConductivity,
            Flow: processState.FlowActualRaw / 1000d,
            MappingConfidence: "VendorDocumentAndFieldValidated",
            FlowSetpoint: processState.FlowSetpointRaw / 1000d,
            ColumnTemperatureSetpoint: processState.ColumnTemperatureSetpointRaw / 100d,
            TemperatureControlStateRaw: processState.TemperatureControlStateRaw,
            PumpStateRaw: processState.PumpStateRaw,
            PressureRaw: processState.PressureRaw,
            SuppressorEluentStateRaw: suppressorState.SuppressorEluentStateRaw,
            FaultCode1Raw: suppressorState.FaultCode1Raw,
            FaultCode2Raw: suppressorState.FaultCode2Raw);

        return new(status,
        [
            ToEvidence("Identity", identity),
            ToEvidence("Detector", detector),
            ToEvidence("Process", process),
            ToEvidence("Suppressor", suppressor)
        ]);
    }

    private static ModbusReadEvidence ToEvidence(string name, ModbusReadResult read) => new(
        name,
        read.StartAddress,
        read.RegisterCount,
        Convert.ToHexString(read.Request),
        Convert.ToHexString(read.Response),
        read.ElapsedMs);

    private static float ReadSingleLittleEndian(byte[] data, int offset)
    {
        if (offset < 0 || offset + sizeof(float) > data.Length)
            throw new FormatException("Captured D160+ float field is outside the response data.");
        return BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, sizeof(float)));
    }
}
