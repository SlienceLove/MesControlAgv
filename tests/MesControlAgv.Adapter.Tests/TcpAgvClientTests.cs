using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MesControlAgv.Adapter.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Adapter.Tests;

public sealed class TcpAgvClientTests
{
    [Fact]
    public async Task Client_queries_control_and_status_before_sending_3066_route()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(4, HandleStatusAsync);
        await using var commandServer = new TcpApiTestServer(1, HandleCommandAsync);
        var options = Options.Create(new TcpAgvOptions
        {
            Host = "127.0.0.1",
            StatusPort = statusServer.Port,
            CommandPort = commandServer.Port,
            ControlPort = statusServer.Port,
            EnablePush = false,
            RequestTimeoutMs = 1000,
            ConnectTimeoutMs = 1000
        });
        using var client = new TcpAgvClient(options, NullLogger<TcpAgvClient>.Instance);

        await client.EnsureControlAsync(cancellation.Token);
        var snapshot = await client.GetSnapshotAsync(cancellation.Token);
        var taskId = Guid.NewGuid();
        var response = await client.NavigateAsync(taskId, "SAMPLE_01", "ST_PREP_01", cancellation.Token);

        Assert.True(snapshot.Online);
        Assert.Equal("adapter", snapshot.ControlOwner);
        Assert.Equal("moving", response.State);
        Assert.Equal(taskId.ToString("N"), commandServer.Requests.Single().TaskId);
        Assert.Equal("SAMPLE_01", commandServer.Requests.Single().SourceStationId);
        Assert.Equal("ST_PREP_01", commandServer.Requests.Single().TargetStationId);
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Fact]
    public async Task Client_blocks_3066_when_realtime_safety_status_is_not_ready()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(4, HandleUnsafeStatusAsync);
        await using var commandServer = new TcpApiTestServer(0, HandleCommandAsync);
        var options = Options.Create(new TcpAgvOptions
        {
            Host = "127.0.0.1",
            StatusPort = statusServer.Port,
            CommandPort = commandServer.Port,
            ControlPort = statusServer.Port,
            EnablePush = false,
            RequestTimeoutMs = 1000,
            ConnectTimeoutMs = 1000
        });
        using var client = new TcpAgvClient(options, NullLogger<TcpAgvClient>.Instance);

        await client.EnsureControlAsync(cancellation.Token);
        _ = await client.GetSnapshotAsync(cancellation.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.NavigateAsync(
            Guid.NewGuid(), "SAMPLE_01", "ST_PREP_01", cancellation.Token));
    }

    private static Task<byte[]> HandleStatusAsync(AgvTcpPacket packet)
    {
        var payload = packet.ApiId switch
        {
            1060 => "{\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}",
            1110 => "{\"task_status_list\":[]}",
            1101 => "{\"reloc_status\":1,\"confidence\":1.0,\"emergency\":false,\"fatals\":[],\"errors\":[],\"fork_auto_flag\":true}",
            _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
        };
        return Task.FromResult(Encoding.UTF8.GetBytes(payload));
    }

    private static Task<byte[]> HandleCommandAsync(AgvTcpPacket packet)
    {
        Assert.Equal((ushort)3066, packet.ApiId);
        return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0}"));
    }

    private static Task<byte[]> HandleUnsafeStatusAsync(AgvTcpPacket packet)
    {
        var payload = packet.ApiId switch
        {
            1060 => "{\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}",
            1110 => "{\"task_status_list\":[]}",
            1101 => "{\"reloc_status\":1,\"emergency\":true,\"fatals\":[],\"errors\":[]}",
            _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
        };
        return Task.FromResult(Encoding.UTF8.GetBytes(payload));
    }
}

internal sealed class TcpApiTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly int _expectedRequests;
    private readonly Func<AgvTcpPacket, Task<byte[]>> _handler;
    private readonly List<RouteRequest> _requests = [];

    public TcpApiTestServer(int expectedRequests, Func<AgvTcpPacket, Task<byte[]>> handler)
    {
        _expectedRequests = expectedRequests;
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Completion = RunAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public IReadOnlyList<RouteRequest> Requests => _requests;
    public Task Completion { get; }

    private async Task RunAsync()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            for (var index = 0; index < _expectedRequests; index++)
            {
                var packet = await AgvTcpProtocol.ReadPacketAsync(stream, 1024 * 1024, CancellationToken.None);
                if (packet.ApiId == 3066)
                {
                    using var document = JsonDocument.Parse(packet.Payload);
                    var item = document.RootElement[0];
                    _requests.Add(new RouteRequest(
                        item.GetProperty("task_id").GetString()!,
                        item.GetProperty("source_id").GetString()!,
                        item.GetProperty("id").GetString()!));
                }

                var response = await _handler(packet);
                var responsePacket = AgvTcpProtocol.CreatePacket((ushort)(packet.ApiId + 10000), response);
                await stream.WriteAsync(responsePacket);
                await stream.FlushAsync();
            }
        }
        finally
        {
            _listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        try { await Completion; }
        catch (SocketException) { }
        catch (IOException) { }
    }
}

internal sealed record RouteRequest(string TaskId, string SourceStationId, string TargetStationId);
