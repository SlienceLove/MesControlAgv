using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Tests;

public sealed class ShineLabTcpServerTests
{
    [Fact]
    public async Task Server_stays_passive_until_certification_and_does_not_emit_control_frames()
    {
        var port = ReservePort();
        var options = Options.Create(new ShineLabTcpOptions
        {
            Enabled = true,
            ListenAddress = "127.0.0.1",
            Port = port,
            StaleAfterSeconds = 10,
            SendCertificationOnConnect = false
        });
        var hub = new ShineLabStatusHub(options);
        var connectionManager = new ShineLabConnectionManager();
        var server = new ShineLabTcpServer(options, hub, connectionManager, NullLogger<ShineLabTcpServer>.Instance);
        await server.StartAsync(CancellationToken.None);

        try
        {
            using var client = await ConnectWithRetryAsync(port);
            await using var stream = client.GetStream();
            await AssertNoDataAsync(stream, TimeSpan.FromMilliseconds(250));

            var frameReader = new ShineLabTcpFrameReader();
            await SendMessageAsync(stream, new
            {
                strID = "cert-passive-001",
                strMethod = "Certification",
                equipmentCode = "SHA18I",
                body = new { protocolVersion = "draft-1" }
            });
            using var certification = await ReadJsonAsync(stream, frameReader, TimeSpan.FromSeconds(3));
            Assert.Equal("Certification", certification.RootElement.GetProperty("strMethod").GetString());
            Assert.Equal("Success", certification.RootElement.GetProperty("body").GetProperty("result").GetString());

            await SendMessageAsync(stream, new
            {
                strID = "status-passive-001",
                strMethod = "Device",
                equipmentCode = "SHA18I",
                body = new { status = 0, stage = "Idle" }
            });
            await AssertNoDataAsync(stream, TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
            connectionManager.Dispose();
        }
    }

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
            var frameReader = new ShineLabTcpFrameReader();

            await SendMessageAsync(stream, new
            {
                strID = "cert-001",
                strMethod = "Certification",
                equipmentCode = "SHA18I",
                body = new { protocolVersion = "draft-1" }
            });
            using var certification = await ReadJsonAsync(stream, frameReader, TimeSpan.FromSeconds(3));
            Assert.Equal("Success", certification.RootElement.GetProperty("body").GetProperty("result").GetString());

            await SendMessageAsync(stream, new
            {
                strID = "status-001",
                strMethod = "Device",
                equipmentCode = "SHA18I",
                body = new
                {
                    status = 1,
                    task_uuid = "task-001",
                    sampleID = "S-01",
                    chan = "A",
                    pos = 11,
                    stage = "Injecting"
                }
            });

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
            using var commandDocument = await ReadJsonAsync(stream, frameReader, TimeSpan.FromSeconds(3));
            var commandRoot = commandDocument.RootElement;
            Assert.Equal("Command", commandRoot.GetProperty("strMethod").GetString());
            var commandId = commandRoot.GetProperty("strID").GetString();
            await SendMessageAsync(stream, new
            {
                strID = commandId,
                strMethod = "Command",
                equipmentCode = "SHA18I",
                body = new { result = "Success", msg = "accepted" }
            });
            var commandResult = await commandTask;
            Assert.True(commandResult.IsSuccess);
            Assert.Equal("accepted", commandResult.Message);

            await SendMessageAsync(stream, new
            {
                strID = "end-001",
                strMethod = "EndMission",
                equipmentCode = "SHA18I",
                body = new { chan = "A", sampleID = "S-01" }
            });
            await Task.Delay(50);
            var completed = hub.GetStatus("SHA18I");
            Assert.NotNull(completed);
            Assert.Equal("Completed", completed.State);
            Assert.False(completed.HasActiveTask);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
            connectionManager.Dispose();
        }
    }

    [Fact]
    public async Task Idle_client_is_disconnected_after_stale_timeout()
    {
        var port = ReservePort();
        var options = Options.Create(new ShineLabTcpOptions
        {
            Enabled = true,
            ListenAddress = "127.0.0.1",
            Port = port,
            StaleAfterSeconds = 1
        });
        var hub = new ShineLabStatusHub(options);
        var connectionManager = new ShineLabConnectionManager();
        var server = new ShineLabTcpServer(options, hub, connectionManager, NullLogger<ShineLabTcpServer>.Instance);
        await server.StartAsync(CancellationToken.None);

        try
        {
            using var client = await ConnectWithRetryAsync(port);
            await using var stream = client.GetStream();
            var frameReader = new ShineLabTcpFrameReader();
            await SendMessageAsync(stream, new
            {
                strID = "cert-timeout",
                strMethod = "Certification",
                equipmentCode = "SHA18I",
                body = new { }
            });
            using var certification = await ReadJsonAsync(stream, frameReader, TimeSpan.FromSeconds(2));
            Assert.Equal("Success", certification.RootElement.GetProperty("body").GetProperty("result").GetString());

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (connectionManager.GetSnapshot().Connected && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            Assert.False(connectionManager.GetSnapshot().Connected);
            Assert.False(hub.GetStatus("SHA18I")?.Online ?? true);
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

    private static async Task SendMessageAsync(Stream stream, object message)
    {
        var frame = ShineLabTcpFrameCodec.Encode(message);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    private static async Task AssertNoDataAsync(NetworkStream stream, TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            Assert.False(stream.DataAvailable, "The passive ShineLab server emitted an unsolicited frame.");
            await Task.Delay(20);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        Stream stream,
        ShineLabTcpFrameReader frameReader,
        TimeSpan timeout)
    {
        var buffer = new byte[1024];
        while (true)
        {
            var status = frameReader.TryRead(out var frame, out var frameError);
            if (status == ShineLabFrameReadStatus.InvalidFrame)
                throw new InvalidDataException(frameError);
            if (status == ShineLabFrameReadStatus.FrameReady)
            {
                Assert.True(ShineLabTcpFrameCodec.TryDecodeJson(frame, out var json, out var decodeError), decodeError);
                return JsonDocument.Parse(json);
            }

            var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(timeout);
            if (read == 0) throw new EndOfStreamException("ShineLab test peer closed before a frame was received.");
            frameReader.Append(buffer.AsSpan(0, read));
        }
    }
}
