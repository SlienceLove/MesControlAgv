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
        await using var statusServer = new TcpApiTestServer(6, HandleStatusAsync);
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
        Assert.Equal(commandServer.Requests.Single().TaskId, response.DeviceTaskId);
        Assert.Equal("SAMPLE_01", commandServer.Requests.Single().SourceStationId);
        Assert.Equal("ST_PREP_01", commandServer.Requests.Single().TargetStationId);
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Fact]
    public async Task Client_reads_station_from_wrapped_vendor_task_status_package()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(2, packet =>
        {
            var payload = packet.ApiId switch
            {
                1060 => "{\"locked\":false}",
                1110 => "{\"ret_code\":0,\"task_status_package\":{\"closest_target\":\"LM1\",\"task_status_list\":[{\"status\":4,\"task_id\":\"\"}]}}",
                _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
            };
            return Task.FromResult(Encoding.UTF8.GetBytes(payload));
        });
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, statusServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        var snapshot = await client.GetSnapshotAsync(cancellation.Token);

        Assert.True(snapshot.Online);
        Assert.Equal("LM1", snapshot.CurrentStationId);
        Assert.Null(snapshot.CurrentTaskId);
        await statusServer.Completion;
    }

    [Fact]
    public async Task Client_sends_the_complete_path_as_one_deterministic_3066_batch()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(6, HandleStatusAsync);
        await using var commandServer = new TcpApiTestServer(2, HandleCommandAsync);
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, commandServer.Port),
            NullLogger<TcpAgvClient>.Instance);
        var taskId = Guid.NewGuid();
        string[] path = ["LM5", "LM4", "LM1"];

        var first = await client.NavigateAsync(taskId, "LM5", "LM1", path, cancellation.Token);
        var second = await client.NavigateAsync(taskId, "LM5", "LM1", path, cancellation.Token);

        Assert.Equal(taskId, first.TaskId);
        Assert.Equal(path, first.Path);
        Assert.Equal(2, commandServer.Batches.Count);
        Assert.Equal(commandServer.Batches[0], commandServer.Batches[1]);
        Assert.Collection(commandServer.Batches[0],
            segment =>
            {
                Assert.Equal(taskId.ToString("N"), segment.TaskId);
                Assert.Equal(("LM5", "LM4"), (segment.SourceStationId, segment.TargetStationId));
            },
            segment =>
            {
                Assert.NotEqual(taskId.ToString("N"), segment.TaskId);
                Assert.Equal(("LM4", "LM1"), (segment.SourceStationId, segment.TargetStationId));
            });
        Assert.Equal(taskId.ToString("N"), first.DeviceTaskId);
        Assert.Equal(first.DeviceTaskId, second.DeviceTaskId);
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Fact]
    public async Task Client_aggregates_all_segment_statuses_under_the_parent_task()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskStatusQueries = 0;
        await using var statusServer = new TcpApiTestServer(4, packet =>
        {
            if (packet.ApiId == 1060)
            {
                return Task.FromResult(Encoding.UTF8.GetBytes("{\"locked\":true,\"nick_name\":\"MesControlAgv.Adapter\"}"));
            }
            if (packet.ApiId == 1101) return HandleStatusAsync(packet);
            Assert.Equal((ushort)1110, packet.ApiId);
            if (Interlocked.Increment(ref taskStatusQueries) == 1)
            {
                return Task.FromResult(EmptyTaskStatusResponse());
            }

            var taskIds = ReadRequestedTaskIds(packet);
            return Task.FromResult(TaskStatusResponse(
                (taskIds[0], 4, "LM4"),
                (taskIds[1], 2, "LM1")));
        });
        await using var commandServer = new TcpApiTestServer(1, HandleCommandAsync);
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, commandServer.Port),
            NullLogger<TcpAgvClient>.Instance);
        var taskId = Guid.NewGuid();
        string[] path = ["LM5", "LM4", "LM1"];

        await client.NavigateAsync(taskId, "LM5", "LM1", path, cancellation.Token);
        var result = await client.GetTaskAsync(taskId, path, cancellation.Token);

        Assert.NotNull(result);
        Assert.Equal(taskId, result.TaskId);
        Assert.Equal("moving", result.State);
        Assert.Equal("LM1", result.TargetStationId);
        Assert.Equal(path, result.Path);
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Theory]
    [InlineData(6, "cancelled", null)]
    [InlineData(2, "unknown", "cancel_not_confirmed_by_1110")]
    public async Task Client_confirms_batch_cancellation_only_when_every_segment_is_terminal(
        int finalSegmentStatus,
        string expectedState,
        string? expectedError)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(finalSegmentStatus == 6 ? 1 : 2, packet =>
        {
            Assert.Equal((ushort)1110, packet.ApiId);
            var taskIds = ReadRequestedTaskIds(packet);
            return Task.FromResult(TaskStatusResponse(
                (taskIds[0], 4, "LM4"),
                (taskIds[1], finalSegmentStatus, "LM1")));
        });
        await using var commandServer = new TcpApiTestServer(finalSegmentStatus == 6 ? 0 : 1, packet =>
        {
            Assert.Equal((ushort)3067, packet.ApiId);
            return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0}"));
        });
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, commandServer.Port),
            NullLogger<TcpAgvClient>.Instance);
        var taskId = Guid.NewGuid();
        string[] path = ["LM5", "LM4", "LM1"];

        var result = await client.CancelAsync(taskId, path, cancellation.Token);

        Assert.NotNull(result);
        Assert.Equal(taskId, result.TaskId);
        Assert.Equal(expectedState, result.State);
        Assert.Equal(expectedError, result.LastError);
        Assert.Equal(path, result.Path);
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Fact]
    public async Task Client_blocks_3066_when_realtime_safety_status_is_not_ready()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(6, HandleUnsafeStatusAsync);
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

    [Fact]
    public async Task Physical_mode_blocks_3066_when_realtime_safety_status_is_incomplete()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var statusServer = new TcpApiTestServer(2, packet => packet.ApiId switch
        {
            1110 => Task.FromResult(EmptyTaskStatusResponse()),
            1101 => Task.FromResult(Encoding.UTF8.GetBytes("{\"emergency\":false}")),
            _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
        });
        await using var commandServer = new TcpApiTestServer(0, HandleCommandAsync);
        using var client = new TcpAgvClient(
            Options.Create(new TcpAgvOptions
            {
                Host = "127.0.0.1",
                StatusPort = statusServer.Port,
                CommandPort = commandServer.Port,
                ControlPort = statusServer.Port,
                EnablePush = false,
                RequireCompleteSafetyStatus = true,
                RequestTimeoutMs = 1000,
                ConnectTimeoutMs = 1000
            }),
            NullLogger<TcpAgvClient>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.NavigateAsync(
            Guid.NewGuid(), "SAMPLE_01", "ST_PREP_01", cancellation.Token));

        await statusServer.Completion;
    }

    [Theory]
    [InlineData(0, "unknown")]
    [InlineData(1, "accepted")]
    [InlineData(2, "moving")]
    [InlineData(6, "cancelled")]
    public async Task Client_maps_device_task_status_conservatively(int deviceStatus, string expectedState)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskId = Guid.NewGuid();
        await using var statusServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)1110, packet.ApiId);
            return Task.FromResult(TaskStatusResponse(taskId, deviceStatus));
        });
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, statusServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        var result = await client.GetTaskAsync(taskId, cancellation.Token);

        Assert.NotNull(result);
        Assert.Equal(expectedState, result.State);
        await statusServer.Completion;
    }

    [Fact]
    public async Task Client_reports_cancelled_only_after_1110_confirms_status_6()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskId = Guid.NewGuid();
        await using var statusServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)1110, packet.ApiId);
            return Task.FromResult(TaskStatusResponse(taskId, 6));
        });
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, statusServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        var result = await client.CancelAsync(taskId, cancellation.Token);

        Assert.NotNull(result);
        Assert.Equal("cancelled", result.State);
        Assert.Equal("ST_PREP_01", result.TargetStationId);
        Assert.Null(result.LastError);
        await statusServer.Completion;
    }

    [Fact]
    public async Task Client_returns_unknown_when_1110_does_not_confirm_cancellation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskId = Guid.NewGuid();
        await using var statusServer = new TcpApiTestServer(2, packet =>
        {
            Assert.Equal((ushort)1110, packet.ApiId);
            return Task.FromResult(TaskStatusResponse(taskId, 2));
        });
        await using var commandServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)3067, packet.ApiId);
            return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0}"));
        });
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, commandServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        var result = await client.CancelAsync(taskId, cancellation.Token);

        Assert.NotNull(result);
        Assert.Equal("unknown", result.State);
        Assert.Equal("cancel_not_confirmed_by_1110", result.LastError);
        await Task.WhenAll(statusServer.Completion, commandServer.Completion);
    }

    [Fact]
    public async Task Client_returns_unknown_when_1110_does_not_find_cancelled_task()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskId = Guid.NewGuid();
        await using var statusServer = new TcpApiTestServer(1, packet =>
        {
            Assert.Equal((ushort)1110, packet.ApiId);
            return Task.FromResult(Encoding.UTF8.GetBytes("{\"ret_code\":0,\"task_status_list\":[]}"));
        });
        using var client = new TcpAgvClient(
            CreateOptions(statusServer.Port, statusServer.Port),
            NullLogger<TcpAgvClient>.Instance);

        var result = await client.CancelAsync(taskId, cancellation.Token);

        Assert.NotNull(result);
        Assert.Equal("unknown", result.State);
        Assert.Equal("cancel_not_confirmed_by_1110", result.LastError);
        await statusServer.Completion;
    }

    private static IOptions<TcpAgvOptions> CreateOptions(int statusPort, int commandPort) => Options.Create(new TcpAgvOptions
    {
        Host = "127.0.0.1",
        StatusPort = statusPort,
        CommandPort = commandPort,
        ControlPort = statusPort,
        EnablePush = false,
        RequestTimeoutMs = 1000,
        ConnectTimeoutMs = 1000
    });

    private static byte[] TaskStatusResponse(Guid taskId, int status) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            ret_code = 0,
            task_status_list = new[] { new { task_id = taskId.ToString("N"), status, target_name = "ST_PREP_01" } }
        });

    private static byte[] EmptyTaskStatusResponse() =>
        Encoding.UTF8.GetBytes("{\"ret_code\":0,\"task_status_list\":[]}");

    private static byte[] TaskStatusResponse(params (string TaskId, int Status, string Target)[] statuses) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            ret_code = 0,
            task_status_list = statuses.Select(status => new
            {
                task_id = status.TaskId,
                status = status.Status,
                target_name = status.Target
            })
        });

    private static string[] ReadRequestedTaskIds(AgvTcpPacket packet)
    {
        using var document = JsonDocument.Parse(packet.Payload);
        return document.RootElement.GetProperty("task_ids")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
    }

    private static Task<byte[]> HandleStatusAsync(AgvTcpPacket packet)
    {
        if (packet.ApiId == 1101)
        {
            using var document = JsonDocument.Parse(packet.Payload);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.True(document.RootElement.TryGetProperty("return_laser", out var returnLaser));
            Assert.False(returnLaser.GetBoolean());
        }

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
    private readonly List<IReadOnlyList<RouteRequest>> _batches = [];

    public TcpApiTestServer(int expectedRequests, Func<AgvTcpPacket, Task<byte[]>> handler)
    {
        _expectedRequests = expectedRequests;
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Completion = expectedRequests == 0 ? Task.CompletedTask : RunAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public IReadOnlyList<RouteRequest> Requests => _requests;
    public IReadOnlyList<IReadOnlyList<RouteRequest>> Batches => _batches;
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
                    Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
                    var rootProperty = Assert.Single(document.RootElement.EnumerateObject());
                    Assert.Equal("move_task_list", rootProperty.Name);
                    Assert.Equal(JsonValueKind.Array, rootProperty.Value.ValueKind);
                    var batch = rootProperty.Value.EnumerateArray()
                        .Select(item =>
                        {
                            Assert.Equal(3, item.EnumerateObject().Count());
                            return new RouteRequest(
                                item.GetProperty("task_id").GetString()!,
                                item.GetProperty("source_id").GetString()!,
                                item.GetProperty("id").GetString()!);
                        })
                        .ToArray();
                    Assert.NotEmpty(batch);
                    _batches.Add(batch);
                    _requests.AddRange(batch);
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
