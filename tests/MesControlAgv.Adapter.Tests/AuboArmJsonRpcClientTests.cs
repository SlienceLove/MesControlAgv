using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MesControlAgv.Adapter.Modules.AuboArm;

namespace MesControlAgv.Adapter.Tests;

public sealed class AuboArmJsonRpcClientTests
{
    [Fact]
    public async Task Client_ShouldEmitTheVendorNamedParameterEnvelope()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var options = new AuboArmOptions
        {
            Host = IPAddress.Loopback.ToString(),
            Port = port,
            RequestTimeoutMs = 2000,
            ConnectTimeoutMs = 2000
        };

        try
        {
            using var client = new AuboArmJsonRpcClient(options);
            var invoke = client.InvokeAsync(
                "RegisterControl.setString",
                ["mes_result_detail", "ok"],
                CancellationToken.None);

            using var server = await listener.AcceptTcpClientAsync();
            using var stream = server.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true
            };

            var line = await reader.ReadLineAsync();
            Assert.False(string.IsNullOrWhiteSpace(line));
            using var request = JsonDocument.Parse(line!);
            var root = request.RootElement;
            Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
            Assert.Equal("RegisterControl.setString", root.GetProperty("method").GetString());
            Assert.Equal(JsonValueKind.String, root.GetProperty("id").ValueKind);
            var parameters = root.GetProperty("params");
            Assert.Equal("mes_result_detail", parameters.GetProperty("key").GetString());
            Assert.Equal("ok", parameters.GetProperty("value").GetString());

            var id = root.GetProperty("id").GetString();
            await writer.WriteLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":0}}");

            var result = await invoke;
            Assert.Equal(JsonValueKind.Number, result.ValueKind);
            Assert.Equal(0, result.GetInt32());
        }
        finally
        {
            listener.Stop();
        }
    }
}
