using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Tests;

public sealed class ShineLabTcpServerTests
{
    [Fact]
    public async Task Client_can_certify_push_status_and_disconnect_without_serial_access()
    {
        var port = ReservePort();
        var options = Options.Create(new ShineLabTcpOptions
        {
            Enabled = true,
            ListenAddress = "127.0.0.1",
            Port = port,
            StaleAfterSeconds = 10
        });
        var hub = new ShineLabStatusHub(options);
        var connectionManager = new ShineLabConnectionManager();
        var server = new ShineLabTcpServer(options, hub, connectionManager, NullLogger<ShineLabTcpServer>.Instance);
        await server.StartAsync(CancellationToken.None);

        try
        {
            using var client = await ConnectWithRetryAsync(port);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true
            };

            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                strID = "cert-001",
                strMethod = "Certification",
                equipmentCode = "SHA18I",
                body = new { protocolVersion = "draft-1" }
            }));
            var certification = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Contains("Success", certification);

            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                strID = "status-001",
                strMethod = "UpdateInfo",
                equipmentCode = "SHA18I",
                body = new
                {
                    status = 1,
                    task_uuid = "task-001",
                    sampleID = "S-01",
                    channel = "A",
                    position = 11,
                    stage = "Injecting"
                }
            }));

            ShineLabDeviceStatusResponse? status = null;
            var deadline = DateTime.UtcNow.AddSeconds(3);
            do
            {
                status = hub.GetStatus("SHA18I");
                if (status?.HasActiveTask == true) break;
                await Task.Delay(20);
            }
            while (DateTime.UtcNow < deadline);

            Assert.NotNull(status);
            Assert.True(status.Online);
            Assert.True(status.HasActiveTask);
            Assert.Equal("task-001", status.TaskUuid);
            Assert.Equal("A", status.Channel);
            Assert.Equal(11, status.Position);

            var commandTask = connectionManager.SendAsync(
                "SHA18I",
                "Command",
                JsonSerializer.SerializeToElement(new { task_uuid = "task-001", action = 0 }),
                TimeSpan.FromSeconds(3),
                CancellationToken.None);
            var commandLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
            using var commandDocument = JsonDocument.Parse(commandLine!);
            var commandRoot = commandDocument.RootElement;
            Assert.Equal("Command", commandRoot.GetProperty("strMethod").GetString());
            var commandId = commandRoot.GetProperty("strID").GetString();
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                strID = commandId,
                strMethod = "Command",
                equipmentCode = "SHA18I",
                body = new { result = "Success", msg = "accepted" }
            }));
            var commandResult = await commandTask;
            Assert.True(commandResult.IsSuccess);
            Assert.Equal("accepted", commandResult.Message);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
            connectionManager.Dispose();
        }
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<TcpClient> ConnectWithRetryAsync(int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return client;
            }
            catch (SocketException exception)
            {
                lastError = exception;
                client.Dispose();
                await Task.Delay(20);
            }
        }

        throw new TimeoutException("ShineLab TCP test server did not start in time.", lastError);
    }
}
