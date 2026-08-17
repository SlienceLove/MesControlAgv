using System.Diagnostics;
using System.IO.Ports;
using Microsoft.Extensions.Options;

namespace MesControlAgv.InstrumentGateway;

public sealed class SerialReadOnlyModbusTransport(IOptions<CicD160PlusOptions> configuredOptions)
    : IReadOnlyModbusTransport, IDisposable
{
    private readonly CicD160PlusOptions _options = configuredOptions.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ModbusReadResult> ReadInputRegistersAsync(
        ushort startAddress,
        ushort registerCount,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled) throw new InvalidOperationException("The instrument gateway is disabled.");
        CicD160PlusReadOnlyRegisterPolicy.EnsureAllowed(startAddress, registerCount);
        var request = ModbusRtuCodec.BuildReadInputRegisters(_options.SlaveAddress, startAddress, registerCount);
        if (!ModbusRtuCodec.IsValidReadInputRegistersRequest(request))
            throw new InvalidOperationException("The generated request did not pass the read-only Modbus policy.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() => Exchange(startAddress, registerCount, request, cancellationToken), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ModbusReadResult Exchange(
        ushort startAddress,
        ushort registerCount,
        byte[] request,
        CancellationToken cancellationToken)
    {
        using var port = new SerialPort(
            _options.ComPort,
            _options.BaudRate,
            ParseParity(_options.Parity),
            _options.DataBits,
            ParseStopBits(_options.StopBits))
        {
            ReadTimeout = _options.TimeoutMs,
            WriteTimeout = _options.TimeoutMs,
            DtrEnable = false,
            RtsEnable = false
        };

        var stopwatch = Stopwatch.StartNew();
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        port.Write(request, 0, request.Length);

        var header = ReadExactly(port, 3, cancellationToken);
        if (header[0] != _options.SlaveAddress)
            throw new FormatException($"Expected slave 0x{_options.SlaveAddress:X2}, received 0x{header[0]:X2}.");
        if (header[1] == (ModbusRtuCodec.ReadInputRegistersFunction | 0x80))
        {
            var exceptionTail = ReadExactly(port, 2, cancellationToken);
            var exceptionFrame = header.Concat(exceptionTail).ToArray();
            throw new InvalidOperationException($"D160+ returned Modbus exception 0x{exceptionFrame[2]:X2}.");
        }
        if (header[1] != ModbusRtuCodec.ReadInputRegistersFunction)
            throw new FormatException($"Read-only gateway rejected response function 0x{header[1]:X2}.");

        var tail = ReadExactly(port, checked(header[2] + 2), cancellationToken);
        var response = header.Concat(tail).ToArray();
        var data = ModbusRtuCodec.ParseReadInputRegistersResponse(
            response,
            _options.SlaveAddress,
            registerCount);
        stopwatch.Stop();
        return new(startAddress, registerCount, request, response, data, stopwatch.ElapsedMilliseconds);
    }

    private static byte[] ReadExactly(SerialPort port, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = port.Read(buffer, offset, count - offset);
            if (read <= 0) throw new IOException($"Serial port closed after {offset} of {count} bytes.");
            offset += read;
        }
        return buffer;
    }

    private static Parity ParseParity(string value) => Enum.TryParse<Parity>(value, true, out var parity)
        ? parity
        : throw new InvalidOperationException($"Unsupported serial parity: {value}.");

    private static StopBits ParseStopBits(string value) => Enum.TryParse<StopBits>(value, true, out var stopBits)
        ? stopBits
        : throw new InvalidOperationException($"Unsupported serial stop bits: {value}.");

    public void Dispose() => _gate.Dispose();
}
