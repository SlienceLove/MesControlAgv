using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using MesControlAgv.Contracts;

namespace MesControlAgv.ShineLabDispatcher;

/// <summary>ShineLab TCP message independent of the selected wire framing.</summary>
public sealed record ShineLabTcpMessage(
    string StrID, string StrMethod, string EquipmentCode, JsonElement Body);

public static class ShineLabTcpCodec
{
    public static byte[] Encode(
        ShineLabTcpMessage message,
        ShineLabWireFormat wireFormat = ShineLabWireFormat.Native55Aa)
    {
        ArgumentNullException.ThrowIfNull(message);
        var value = new
        {
            strID = message.StrID,
            strMethod = message.StrMethod,
            equipmentCode = message.EquipmentCode,
            body = message.Body
        };
        return ShineLabWireCodec.EncodeJson(JsonSerializer.Serialize(value), wireFormat);
    }

    public static ShineLabTcpMessage Decode(
        ReadOnlySpan<byte> frame,
        ShineLabWireFormat wireFormat = ShineLabWireFormat.Native55Aa)
    {
        string json;
        if (wireFormat == ShineLabWireFormat.Native55Aa)
        {
            if (!ShineLabTcpFrameCodec.TryDecodeJson(frame, out json, out var nativeError))
                throw new InvalidDataException(nativeError);
        }
        else
        {
            var line = frame.ToArray();
            if (line.Length > 0 && line[^1] == (byte)'\n') line = line[..^1];
            if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
            var wireFrame = new ShineLabWireFrame(ShineLabWireFormat.LineJson, frame.ToArray(), line);
            if (!ShineLabWireCodec.TryDecodeJson(wireFrame, out json, out var lineError))
                throw new InvalidDataException(lineError);
        }

        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement);
    }

    /// <summary>Line-delimited JSON fixture decoder.</summary>
    [Obsolete("Use Decode(ReadOnlySpan<byte>, ShineLabWireFormat) for wire-aware decoding.")]
    public static ShineLabTcpMessage DecodeLegacyLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        return Parse(document.RootElement);
    }

    private static ShineLabTcpMessage Parse(JsonElement root) => new(
        root.GetProperty("strID").GetString()!,
        root.GetProperty("strMethod").GetString()!,
        root.GetProperty("equipmentCode").GetString()!,
        root.TryGetProperty("body", out var body) ? body.Clone() : JsonSerializer.SerializeToElement(new { }));
}

/// <summary>
/// MES-side client retained for explicitly opted-in diagnostic use. Real
/// traffic keeps the historical native 55AA framing by default; callers that
/// have identified a YhLoop line-JSON peer can opt into LineJson explicitly.
/// </summary>
public sealed class ShineLabTcpClient
{
    private readonly Func<ShineLabTcpMessage, CancellationToken, Task<ShineLabTcpMessage>> _transport;

    public ShineLabTcpClient(Func<ShineLabTcpMessage, CancellationToken, Task<ShineLabTcpMessage>> transport) =>
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public static ShineLabTcpClient Real(
        string host,
        int port,
        bool allowReal = false,
        ShineLabWireFormat wireFormat = ShineLabWireFormat.Native55Aa)
    {
        if (!allowReal)
            throw new InvalidOperationException("Real ShineLab TCP calls require allowReal=true.");

        return new ShineLabTcpClient(async (message, cancellationToken) =>
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cancellationToken);
            await using var stream = tcp.GetStream();
            await stream.WriteAsync(ShineLabTcpCodec.Encode(message, wireFormat), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            var frame = await ReadFrameAsync(stream, cancellationToken, wireFormat);
            return ShineLabTcpCodec.Decode(frame, wireFormat);
        });
    }

    public Task<ShineLabTcpMessage> SendAsync(
        ShineLabTcpMessage message,
        CancellationToken cancellationToken = default) =>
        _transport(message, cancellationToken);

    private static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken,
        ShineLabWireFormat wireFormat)
    {
        var reader = new ShineLabWireReader();
        var buffer = new byte[4096];
        while (true)
        {
            var status = reader.TryRead(out var frame, out var error);
            if (status == ShineLabFrameReadStatus.InvalidFrame)
                throw new InvalidDataException(error);
            if (status == ShineLabFrameReadStatus.FrameReady)
            {
                if (wireFormat != ShineLabWireFormat.Unknown && frame.Format != wireFormat)
                    throw new InvalidDataException($"Expected {wireFormat} but received {frame.Format}.");
                return frame.RawBytes;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                throw new IOException("ShineLab TCP peer closed before returning a framed response.");
            reader.Append(buffer.AsSpan(0, read));
        }
    }
}
