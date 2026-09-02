using System.Net;
using System.Net.Sockets;

var options = BridgeOptions.Parse(args);
var bridge = new ModbusBridge(options);
await bridge.RunAsync();

internal sealed record BridgeOptions(string BindAddress, int Port, byte UnitId, bool AllowMotion)
{
    public static BridgeOptions Parse(string[] args)
    {
        var bind = "192.168.1.11";
        var port = 502;
        byte unit = 1;
        var allowMotion = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bind": bind = Next(args, ref i); break;
                case "--port":
                    if (!int.TryParse(Next(args, ref i), out port) || port is < 1 or > 65535)
                        throw new ArgumentException("--port must be between 1 and 65535.");
                    break;
                case "--unit":
                    if (!byte.TryParse(Next(args, ref i), out unit))
                        throw new ArgumentException("--unit must be a byte.");
                    break;
                case "--allow-motion": allowMotion = true; break;
                case "--help":
                case "-h":
                    PrintUsage();
                    return new BridgeOptions(bind, port, unit, allowMotion);
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }

        if (!IPAddress.TryParse(bind, out _))
            throw new ArgumentException("--bind must be an IP address.");
        return new BridgeOptions(bind, port, unit, allowMotion);
    }

    private static string Next(string[] args, ref int index) =>
        ++index < args.Length ? args[index] : throw new ArgumentException("Missing option value.");

    private static void PrintUsage() =>
        Console.WriteLine("AuboModbusBridge --bind 192.168.1.11 --port 502 [--allow-motion]\n" +
                          "Commands: get <address> | set <address> <value> | quit\n" +
                          "HR0=command (0 idle, 1..5 action), HR1=robot result.");
}

internal sealed class ModbusBridge
{
    private readonly BridgeOptions _options;
    private readonly ushort[] _holding = new ushort[128];
    private readonly TcpListener _listener;
    private readonly object _gate = new();

    public ModbusBridge(BridgeOptions options)
    {
        _options = options;
        _listener = new TcpListener(IPAddress.Parse(options.BindAddress), options.Port);
    }

    public async Task RunAsync()
    {
        Console.WriteLine($"Listening on {_options.BindAddress}:{_options.Port}, unit {_options.UnitId}.");
        Console.WriteLine($"Motion commands: {(_options.AllowMotion ? "ENABLED" : "DISABLED; use --allow-motion")}");
        Console.WriteLine("Initial HR0=0, HR1=0. Commands: get <address> | set <address> <value> | quit.");

        _listener.Start();
        using var stop = new CancellationTokenSource();
        var acceptTask = AcceptLoopAsync(stop.Token);
        var consoleTask = ConsoleLoopAsync(stop);
        await Task.WhenAny(acceptTask, consoleTask);
        stop.Cancel();
        _listener.Stop();
        try { await Task.WhenAll(acceptTask, consoleTask); }
        catch (OperationCanceledException) { }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] connected {remote}");
        try
        {
            await using var stream = client.GetStream();
            var header = new byte[7];
            while (!cancellationToken.IsCancellationRequested)
            {
                await ReadExactlyAsync(stream, header, cancellationToken);
                var transaction = ReadU16(header, 0);
                if (ReadU16(header, 2) != 0) throw new InvalidDataException("Invalid Modbus protocol id.");
                var length = ReadU16(header, 4);
                var unit = header[6];
                if (length is < 2 or > 253) throw new InvalidDataException("Invalid Modbus MBAP length.");

                var pdu = new byte[length - 1];
                await ReadExactlyAsync(stream, pdu, cancellationToken);
                var responsePdu = HandlePdu(unit, pdu, remote);
                var response = new byte[7 + responsePdu.Length];
                WriteU16(response, 0, transaction);
                WriteU16(response, 2, 0);
                WriteU16(response, 4, (ushort)(responsePdu.Length + 1));
                response[6] = unit;
                responsePdu.CopyTo(response, 7);
                await stream.WriteAsync(response, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }
        catch (EndOfStreamException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {remote}: {ex.Message}"); }
        finally
        {
            client.Dispose();
            Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] disconnected {remote}");
        }
    }

    private byte[] HandlePdu(byte unit, byte[] pdu, string remote)
    {
        if (pdu.Length == 0) throw new InvalidDataException("Empty Modbus PDU.");
        if (unit != _options.UnitId) return ExceptionPdu(pdu[0], 0x0B);
        return pdu[0] switch
        {
            3 => ReadHolding(pdu),
            6 => WriteSingle(pdu, remote),
            16 => WriteMultiple(pdu, remote),
            _ => ExceptionPdu(pdu[0], 0x01)
        };
    }

    private byte[] ReadHolding(byte[] pdu)
    {
        if (pdu.Length != 5) return ExceptionPdu(3, 0x03);
        var start = ReadU16(pdu, 1);
        var count = ReadU16(pdu, 3);
        if (count is < 1 or > 125 || start + count > _holding.Length) return ExceptionPdu(3, 0x02);
        var response = new byte[2 + count * 2];
        response[0] = 3;
        response[1] = (byte)(count * 2);
        lock (_gate)
        {
            for (var i = 0; i < count; i++) WriteU16(response, 2 + i * 2, _holding[start + i]);
        }
        return response;
    }

    private byte[] WriteSingle(byte[] pdu, string remote)
    {
        if (pdu.Length != 5) return ExceptionPdu(6, 0x03);
        var address = ReadU16(pdu, 1);
        var value = ReadU16(pdu, 3);
        if (address >= _holding.Length || !CanWrite(address, value)) return ExceptionPdu(6, 0x03);
        lock (_gate) _holding[address] = value;
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {remote} wrote HR{address}={value}");
        return pdu.ToArray();
    }

    private byte[] WriteMultiple(byte[] pdu, string remote)
    {
        if (pdu.Length < 6) return ExceptionPdu(16, 0x03);
        var start = ReadU16(pdu, 1);
        var count = ReadU16(pdu, 3);
        var byteCount = pdu[5];
        if (count is < 1 or > 123 || byteCount != count * 2 || pdu.Length != 6 + byteCount || start + count > _holding.Length)
            return ExceptionPdu(16, 0x03);
        for (var i = 0; i < count; i++)
        {
            var value = ReadU16(pdu, 6 + i * 2);
            if (!CanWrite((ushort)(start + i), value)) return ExceptionPdu(16, 0x03);
        }
        lock (_gate)
        {
            for (var i = 0; i < count; i++) _holding[start + i] = ReadU16(pdu, 6 + i * 2);
        }
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {remote} wrote HR{start}..HR{start + count - 1}");
        return pdu[..6];
    }

    private bool CanWrite(ushort address, ushort value)
    {
        if (address is not (0 or 1)) return false;
        if (address == 0 && value > 5) return false;
        return address != 0 || value == 0 || _options.AllowMotion;
    }

    private async Task ConsoleLoopAsync(CancellationTokenSource stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var line = await Task.Run(Console.ReadLine);
            if (line is null) { stop.Cancel(); return; }
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;
            if (parts[0].Equals("quit", StringComparison.OrdinalIgnoreCase)) { stop.Cancel(); return; }
            if (parts[0].Equals("get", StringComparison.OrdinalIgnoreCase) && parts.Length == 2 && ushort.TryParse(parts[1], out var getAddress))
            {
                lock (_gate) Console.WriteLine($"HR{getAddress}={(getAddress < _holding.Length ? _holding[getAddress] : 0)}");
                continue;
            }
            if (parts[0].Equals("set", StringComparison.OrdinalIgnoreCase) && parts.Length == 3
                && ushort.TryParse(parts[1], out var address) && ushort.TryParse(parts[2], out var value))
            {
                if (address >= _holding.Length || !CanWrite(address, value))
                    Console.WriteLine("Rejected: writable registers are HR0/HR1; HR0 accepts 0..5 and needs --allow-motion for nonzero values.");
                else
                {
                    lock (_gate) _holding[address] = value;
                    Console.WriteLine($"HR{address}={value}");
                }
                continue;
            }
            Console.WriteLine("Commands: get <address> | set <address> <value> | quit");
        }
    }

    private static byte[] ExceptionPdu(byte function, byte code) => [(byte)(function | 0x80), code];

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static ushort ReadU16(byte[] bytes, int offset) => (ushort)((bytes[offset] << 8) | bytes[offset + 1]);

    private static void WriteU16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)(value >> 8);
        bytes[offset + 1] = (byte)value;
    }
}
