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

    [Fact]
    public async Task Line_json_client_can_send_heart_and_bind_module_without_server_response()
    {
        var port = ReservePort();
        var options = Options.Create(new ShineLabTcpOptions
        {
            Enabled = true,
            ListenAddress = "127.0.0.1",
            Port = port,
            StaleAfterSeconds = 2,
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
            var heart = "{\"strID\":\"heart-001\",\"strMethod\":\"Heart\",\"equipmentCode\":\"STN61_01\",\"strCode\":\"\",\"body\":{\"type\":\"ping\",\"time\":\"110947000\"}}\n";
            var bind = "{\"strID\":\"bind-001\",\"strMethod\":\"BindModule\",\"equipmentCode\":\"STN61_01\",\"strCode\":\"\",\"body\":{\"chan\":\"\"}}\n";
            var bytes = Encoding.UTF8.GetBytes(heart + bind);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();

            ShineLabDeviceStatusResponse? status = null;
            var deadline = DateTime.UtcNow.AddSeconds(2);
            do
            {
                status = hub.GetStatus("STN61_01");
                if (status?.Online == true && status.Channel == string.Empty) break;
                await Task.Delay(20);
            }
            while (DateTime.UtcNow < deadline);

            Assert.NotNull(status);
            Assert.True(status.Online);
            Assert.Equal("Connected", status.State);
            Assert.Equal(string.Empty, status.Channel);
            Assert.Equal(ShineLabWireFormat.LineJson, connectionManager.GetSnapshot().WireFormat);
            Assert.False(stream.DataAvailable, "Heart/BindModule must not cause an unsolicited response.");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
            connectionManager.Dispose();
        }
    }

    [Fact]
    public async Task Line_json_certification_is_replied_with_line_json_only_after_request()
    {
        var port = ReservePort();
        var options = Options.Create(new ShineLabTcpOptions
        {
            Enabled = true,
            ListenAddress = "127.0.0.1",
            Port = port,
            StaleAfterSeconds = 2,
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
            var reader = new ShineLabWireReader();
            var request = "{\"strID\":\"cert-line-001\",\"strMethod\":\"Certification\",\"equipmentCode\":\"STN61_01\",\"strCode\":\"\",\"body\":{}}\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request));
            await stream.FlushAsync();

            using var response = await ReadWireJsonAsync(stream, reader, TimeSpan.FromSeconds(3));
            var root = response.RootElement;
            Assert.Equal("cert-line-001", root.GetProperty("strID").GetString());
            Assert.Equal("Certification", root.GetProperty("strMethod").GetString());
            Assert.Equal("STN61_01", root.GetProperty("equipmentCode").GetString());
            Assert.Equal("Success", root.GetProperty("body").GetProperty("result").GetString());
            Assert.Equal(ShineLabWireFormat.LineJson, connectionManager.GetSnapshot().WireFormat);
            Assert.False(stream.DataAvailable);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
            connectionManager.Dispose();
        }
    }

    [Fact]
    public async Task Line_json_single_config_preflight_emits_protocol_fields_and_accepts_success_response()
    {
        var port = ReservePort();
        var options = Options.Create(new ShineLabTcpOptions
        {
            Enabled = true,
            ListenAddress = "127.0.0.1",
            Port = port,
            StaleAfterSeconds = 10,
            CommandTimeoutMs = 3000,
            SendCertificationOnConnect = false
        });
        var hub = new ShineLabStatusHub(options);
        var connectionManager = new ShineLabConnectionManager();
        var commands = new ShineLabCommandService(connectionManager, options);
        var server = new ShineLabTcpServer(options, hub, connectionManager, NullLogger<ShineLabTcpServer>.Instance);
        await server.StartAsync(CancellationToken.None);

        try
        {
            using var client = await ConnectWithRetryAsync(port);
            await using var stream = client.GetStream();
            var bind = "{\"strID\":\"bind-config-001\",\"strMethod\":\"BindModule\",\"equipmentCode\":\"STN61_01\",\"strCode\":\"\",\"body\":{\"chan\":\"A\"}}\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(bind));
            await stream.FlushAsync();

            var registrationDeadline = DateTime.UtcNow.AddSeconds(2);
            while (connectionManager.GetSnapshot().WireFormat != ShineLabWireFormat.LineJson &&
                   DateTime.UtcNow < registrationDeadline)
            {
                await Task.Delay(20);
            }
            Assert.Equal(ShineLabWireFormat.LineJson, connectionManager.GetSnapshot().WireFormat);

            var configTask = commands.SendConfigAsync(
                "STN61_01",
                new ShineLabConfigRequest(
                    "task-config-check-001",
                    [
                        new ShineLabSampleData(
                            "LOCAL-STD-001", "标准测试样", "1", 1, "1", "A",
                            "AS18-M01", "IC-P01", "Normal", 25m, "uL")
                    ],
                    "AS18-M01",
                    "IC-P01",
                    "Normal"),
                CancellationToken.None);

            var reader = new ShineLabWireReader();
            using var config = await ReadWireJsonAsync(stream, reader, TimeSpan.FromSeconds(3));
            var root = config.RootElement;
            Assert.Equal("Config", root.GetProperty("strMethod").GetString());
            Assert.Equal("STN61_01", root.GetProperty("equipmentCode").GetString());
            var body = root.GetProperty("body");
            Assert.Equal("task-config-check-001", body.GetProperty("task_uuid").GetString());
            Assert.Equal("A", body.GetProperty("chan").GetString());
            var sample = body.GetProperty("sampleData")[0];
            Assert.Equal("LOCAL-STD-001", sample.GetProperty("sampleID").GetString());
            Assert.Equal(1, sample.GetProperty("type").GetInt32());
            Assert.Equal(1, sample.GetProperty("position").GetInt32());
            Assert.Equal("1", sample.GetProperty("mPos").GetString());
            Assert.Equal("A", sample.GetProperty("Channel").GetString());
            Assert.Equal("AS18-M01", sample.GetProperty("instrumentMethod").GetString());
            Assert.Equal("IC-P01", sample.GetProperty("processingMethod").GetString());
            Assert.Equal(25m, sample.GetProperty("injectionVolume").GetDecimal());
            Assert.Equal("uL", sample.GetProperty("injectionVolumeUnit").GetString());
            Assert.False(root.TryGetProperty("command", out _));

            var strId = root.GetProperty("strID").GetString();
            var response = JsonSerializer.Serialize(new
            {
                strID = strId,
                strMethod = "Config",
                equipmentCode = "STN61_01",
                body = new { result = "Success", msg = "accepted" }
            }) + "\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
            await stream.FlushAsync();

            var result = await configTask;
            Assert.True(result.Success);
            Assert.Equal("accepted", result.Message);
            Assert.False(stream.DataAvailable, "Config preflight must not emit a Command frame.");
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

    private static async Task<JsonDocument> ReadWireJsonAsync(
        Stream stream,
        ShineLabWireReader wireReader,
        TimeSpan timeout)
    {
        var buffer = new byte[1024];
        while (true)
        {
            var status = wireReader.TryRead(out var frame, out var frameError);
            if (status == ShineLabFrameReadStatus.InvalidFrame)
                throw new InvalidDataException(frameError);
            if (status == ShineLabFrameReadStatus.FrameReady)
            {
                Assert.True(ShineLabWireCodec.TryDecodeJson(frame, out var json, out var decodeError), decodeError);
                return JsonDocument.Parse(json);
            }

            var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(timeout);
            if (read == 0) throw new EndOfStreamException("ShineLab test peer closed before a wire frame was received.");
            wireReader.Append(buffer.AsSpan(0, read));
        }
    }
}
