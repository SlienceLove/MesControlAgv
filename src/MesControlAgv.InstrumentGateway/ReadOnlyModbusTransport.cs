namespace MesControlAgv.InstrumentGateway;

public sealed record ModbusReadResult(
    ushort StartAddress,
    ushort RegisterCount,
    byte[] Request,
    byte[] Response,
    byte[] Data,
    long ElapsedMs);

public interface IReadOnlyModbusTransport
{
    Task<ModbusReadResult> ReadInputRegistersAsync(
        ushort startAddress,
        ushort registerCount,
        CancellationToken cancellationToken);
}
