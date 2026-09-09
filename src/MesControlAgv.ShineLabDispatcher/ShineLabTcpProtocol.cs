using System.Net.Sockets;
using System.Text.Json;
using MesControlAgv.Contracts;

namespace MesControlAgv.ShineLabDispatcher;

/// <summary>ShineLab native 55AA-framed TCP message.</summary>
public sealed record ShineLabTcpMessage(
    string StrID, string StrMethod, string EquipmentCode, JsonElement Body);

public static class ShineLabTcpCodec
{
    public static byte[] Encode(ShineLabTcpMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var value = new
        {
            strID = message.StrID,
            strMethod = message.StrMethod,
            equipmentCode = message.EquipmentCode,
            body = message.Body
        };
        return ShineLabTcpFrameCodec.Encode(value);
    }

    public static ShineLabTcpMessage Decode(ReadOnlySpan<byte> frame)
    {
        if (!ShineLabTcpFrameCodec.TryDecodeJson(frame, out var json, out var error))
            throw new InvalidDataException(error);
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement);
    }

    /// <summary>Legacy fixture decoder; never used for a real TCP stream.</summary>
    [Obsolete("Use Decode(ReadOnlySpan<byte>) for native ShineLab frames.")]
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
/// traffic uses the same native frame codec as the central server.
/// </summary>
public sealed class ShineLabTcpClient
{
    private readonly Func<ShineLabTcpMessage, CancellationToken, Task<ShineLabTcpMessage>> _transport;

    public ShineLabTcpClient(Func<ShineLabTcpMessage, CancellationToken, Task<ShineLabTcpMessage>> transport) =>
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public static ShineLabTcpClient Real(string host, int port, bool allowReal = false)
    {
        if (!allowReal)
            throw new InvalidOperationException("Real ShineLab TCP calls require allowReal=true.");

        return new ShineLabTcpClient(async (message, cancellationToken) =>
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cancellationToken);
            await using var stream = tcp.GetStream();
            await stream.WriteAsync(ShineLabTcpCodec.Encode(message), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            var frame = await ReadFrameAsync(stream, cancellationToken);
            return ShineLabTcpCodec.Decode(frame);
        });
    }

    public Task<ShineLabTcpMessage> SendAsync(
        ShineLabTcpMessage message,
        CancellationToken cancellationToken = default) =>
        _transport(message, cancellationToken);

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var reader = new ShineLabTcpFrameReader();
        var buffer = new byte[4096];
        while (true)
        {
            var status = reader.TryRead(out var frame, out var error);
            if (status == ShineLabFrameReadStatus.InvalidFrame)
                throw new InvalidDataException(error);
            if (status == ShineLabFrameReadStatus.FrameReady)
                return frame;

            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                throw new IOException("ShineLab TCP peer closed before returning a framed response.");
            reader.Append(buffer.AsSpan(0, read));
        }
    }
}
