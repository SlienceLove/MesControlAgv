using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MesControlAgv.ShineLabDispatcher;

/// <summary>ShineLab 下游表 TCP 线协议：每个 JSON 对象以 LF 结束（CRLF 也可接收）。</summary>
public sealed record ShineLabTcpMessage(
    string StrID, string StrMethod, string EquipmentCode, JsonElement Body);

public static class ShineLabTcpCodec
{
    public static byte[] Encode(ShineLabTcpMessage message)
    {
        var value = new { strID = message.StrID, strMethod = message.StrMethod,
            equipmentCode = message.EquipmentCode, body = message.Body };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n");
    }

    public static ShineLabTcpMessage Decode(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        return new ShineLabTcpMessage(root.GetProperty("strID").GetString()!,
            root.GetProperty("strMethod").GetString()!,
            root.GetProperty("equipmentCode").GetString()!, root.GetProperty("body").Clone());
    }
}

/// <summary>
/// MES 作为 TCP client，ShineLab/平台作为 server。默认仅允许模拟 transport；真实连接必须显式 allowReal=true。
/// </summary>
public sealed class ShineLabTcpClient
{
    private readonly Func<ShineLabTcpMessage, CancellationToken, Task<ShineLabTcpMessage>> _transport;
    public ShineLabTcpClient(Func<ShineLabTcpMessage, CancellationToken, Task<ShineLabTcpMessage>> transport) => _transport = transport;

    public static ShineLabTcpClient Real(string host, int port, bool allowReal = false)
    {
        if (!allowReal) throw new InvalidOperationException("真实 ShineLab TCP 调用需要显式 allowReal=true。");
        return new ShineLabTcpClient(async (message, cancellationToken) =>
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cancellationToken);
            await using var stream = tcp.GetStream();
            await stream.WriteAsync(ShineLabTcpCodec.Encode(message), cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            var line = await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("ShineLab TCP 对端未返回响应。");
            return ShineLabTcpCodec.Decode(line);
        });
    }

    public Task<ShineLabTcpMessage> SendAsync(ShineLabTcpMessage message, CancellationToken cancellationToken = default) => _transport(message, cancellationToken);
}
