using System.Buffers.Binary;
using System.Text;

namespace MesControlAgv.InstrumentGateway;

public sealed record CicD160PlusProcessState(
    ushort TemperatureControlStateRaw,
    ushort ColumnTemperatureSetpointRaw,
    ushort ColumnTemperatureActualRaw,
    ushort FlowSetpointRaw,
    ushort FlowActualRaw,
    ushort PressureRaw,
    ushort PumpStateRaw);

public sealed record CicD160PlusSuppressorState(
    ushort SuppressorEluentStateRaw,
    ushort FaultCode1Raw,
    ushort FaultCode2Raw);

public sealed record CicD160PlusSafetyState(
    string ObservedIdentifier,
    CicD160PlusProcessState Process,
    CicD160PlusSuppressorState Suppressor);

internal static class CicD160PlusProtocolMap
{
    internal const ushort IdentityStart = 0x1900;
    internal const ushort IdentityCount = 24;
    internal const ushort DetectorStart = 0x1770;
    internal const ushort DetectorCount = 12;
    internal const ushort ProcessStart = 0x17D4;
    internal const ushort ProcessCount = 18;
    internal const ushort SuppressorStart = 0x1838;
    internal const ushort SuppressorCount = 10;

    internal static decimal DecodePressureMpa(ushort rawValue) =>
        CicD160PlusPressureScale.ToMpa(rawValue);

    internal static string DecodeObservedIdentifier(ReadOnlySpan<byte> data)
    {
        var terminator = data.IndexOf((byte)0);
        var text = Encoding.ASCII.GetString(terminator < 0 ? data : data[..terminator]).Trim();
        return text.Length == 0 ? "Unknown" : text;
    }

    internal static CicD160PlusProcessState DecodeProcess(ReadOnlySpan<byte> data) => new(
        ReadRegister(data, ProcessStart, 0x17D4),
        ReadRegister(data, ProcessStart, 0x17D7),
        ReadRegister(data, ProcessStart, 0x17D8),
        ReadRegister(data, ProcessStart, 0x17DB),
        ReadRegister(data, ProcessStart, 0x17DC),
        ReadRegister(data, ProcessStart, 0x17DD),
        ReadRegister(data, ProcessStart, 0x17DF));

    internal static CicD160PlusSuppressorState DecodeSuppressor(ReadOnlySpan<byte> data) => new(
        ReadRegister(data, SuppressorStart, 0x183A),
        ReadRegister(data, SuppressorStart, 0x183B),
        ReadRegister(data, SuppressorStart, 0x1841));

    private static ushort ReadRegister(ReadOnlySpan<byte> data, ushort startAddress, ushort address)
    {
        var offset = checked((address - startAddress) * 2);
        if (offset + sizeof(ushort) > data.Length)
            throw new FormatException($"D160+ register 0x{address:X4} is outside the captured response data.");
        return BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, sizeof(ushort)));
    }
}
