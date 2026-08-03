using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using MesControlAgv.Adapter.Contracts;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Adapter.Services;

public sealed class TcpAgvOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int StatusPort { get; set; } = 19204;
    public int CommandPort { get; set; } = 19206;
    public int ControlPort { get; set; } = 19207;
    public int PushPort { get; set; } = 19301;
    public string NickName { get; set; } = "MesControlAgv.Adapter";
    public bool AcquireControl { get; set; } = true;
    public bool EnablePush { get; set; } = true;
    public int PushIntervalMs { get; set; } = 500;
    public int RequestTimeoutMs { get; set; } = 3000;
    public int ConnectTimeoutMs { get; set; } = 3000;
    public int PushReconnectDelayMs { get; set; } = 1000;
    public int MaxPayloadBytes { get; set; } = 1024 * 1024;
    public double MinimumConfidence { get; set; }
    public int CancelApiId { get; set; } = 3067;
}

public sealed class AgvApiException(int apiId, int errorCode, string? errorMessage)
    : InvalidOperationException($"AGV API {apiId} failed with ret_code {errorCode}: {errorMessage ?? "unknown error"}")
{
    public int ApiId { get; } = apiId;
    public int ErrorCode { get; } = errorCode;
    public string? ErrorMessage { get; } = errorMessage;
}

public sealed class AgvProtocolException(string message) : IOException(message);

public readonly record struct AgvTcpPacket(ushort ApiId, byte[] Payload);

public static class AgvTcpProtocol
{
    public const int HeaderLength = 16;
    public const byte Magic = 0x5A;
    private const byte Version = 0x01;

    public static byte[] CreatePacket(ushort apiId, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[HeaderLength + payload.Length];
        packet[0] = Magic;
        packet[1] = Version;
        packet[2] = 0;
        packet[3] = 1;
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), payload.Length);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8, 2), apiId);
        payload.CopyTo(packet.AsSpan(HeaderLength));
        return packet;
    }

    public static async Task<AgvTcpPacket> ReadPacketAsync(
        Stream stream,
        int maxPayloadBytes,
        CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        await ReadExactlyAsync(stream, header, cancellationToken);
        if (header[0] != Magic || header[1] != Version || header[2] != 0 || header[3] != 1)
        {
            throw new AgvProtocolException("AGV packet header is invalid.");
        }

        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4));
        if (payloadLength < 0 || payloadLength > maxPayloadBytes)
        {
            throw new AgvProtocolException($"AGV payload length {payloadLength} is outside the configured limit.");
        }

        var payload = new byte[payloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return new AgvTcpPacket(BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8, 2)), payload);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new EndOfStreamException("AGV closed the TCP connection.");
            offset += read;
        }
    }
}

internal sealed class TcpApiChannel : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly string _host;
    private readonly int _port;
    private readonly TcpAgvOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TcpApiChannel(string host, int port, TcpAgvOptions options)
    {
        _host = host;
        _port = port;
        _options = options;
    }

    public async Task<JsonDocument> RequestAsync(ushort apiId, object? payload, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeoutMs);
            var bytes = payload is null ? [] : JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            try
            {
                await EnsureConnectedAsync(timeout.Token);
                var packet = AgvTcpProtocol.CreatePacket(apiId, bytes);
                await _stream!.WriteAsync(packet, timeout.Token);
                await _stream.FlushAsync(timeout.Token);
                var response = await AgvTcpProtocol.ReadPacketAsync(_stream, _options.MaxPayloadBytes, timeout.Token);
                var expectedApiId = apiId + 10000;
                if (response.ApiId != expectedApiId)
                {
                    throw new AgvProtocolException($"Expected AGV response API {expectedApiId}, received {response.ApiId}.");
                }

                return response.Payload.Length == 0
                    ? JsonDocument.Parse("{}")
                    : JsonDocument.Parse(response.Payload);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                ResetConnection();
                throw new TimeoutException($"AGV API {apiId} timed out on port {_port}.");
            }
            catch (Exception exception) when (exception is IOException or SocketException)
            {
                ResetConnection();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null) return;

        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ConnectTimeoutMs);
            await client.ConnectAsync(_host, _port, timeout.Token);
            _client = client;
            _stream = client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void ResetConnection()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        ResetConnection();
        _gate.Dispose();
    }
}

public sealed class TcpAgvClient : IAgvDeviceClient, IHostedService, IDisposable
{
    private const ushort QueryControlApi = 1060;
    private const ushort AcquireControlApi = 4005;
    private const ushort NavigateApi = 3066;
    private const ushort QueryTaskApi = 1110;
    private const ushort PauseApi = 3001;
    private const ushort ResumeApi = 3002;
    private const ushort RealtimeStatusApi = 1101;
    private const ushort ConfigurePushApi = 9300;
    private const ushort PushApi = 19301;

    private static readonly string[] PushFields =
    [
        "x", "y", "angle", "confidence", "current_station", "reloc_status", "task_status", "target_id",
        "blocked", "block_reason", "emergency", "fatals", "errors", "battery_level",
        "charging", "fork_auto_flag"
    ];

    private readonly TcpAgvOptions _options;
    private readonly ILogger<TcpAgvClient> _logger;
    private readonly TcpApiChannel _statusChannel;
    private readonly TcpApiChannel _commandChannel;
    private readonly TcpApiChannel _controlChannel;
    private readonly object _snapshotLock = new();
    private AgvSnapshotResponse? _pushSnapshot;
    private DeviceReadiness? _pushReadiness;
    private DateTimeOffset _pushReceivedAt;
    private string _lastControlOwner = "unknown";
    private CancellationTokenSource? _lifetime;
    private Task? _pushLoop;

    public TcpAgvClient(IOptions<TcpAgvOptions> options, ILogger<TcpAgvClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _statusChannel = new TcpApiChannel(_options.Host, _options.StatusPort, _options);
        _commandChannel = new TcpApiChannel(_options.Host, _options.CommandPort, _options);
        _controlChannel = new TcpApiChannel(_options.Host, _options.ControlPort, _options);
    }

    public async Task EnsureControlAsync(CancellationToken cancellationToken)
    {
        var current = await QueryControlAsync(cancellationToken);
        if (current.Owner == "adapter") return;
        if (!_options.AcquireControl)
        {
            throw new ControlUnavailableException(current.Owner);
        }

        try
        {
            using var response = await _controlChannel.RequestAsync(AcquireControlApi, new { nick_name = _options.NickName }, cancellationToken);
            EnsureSuccess(response, AcquireControlApi);
        }
        catch (AgvApiException exception) when (exception.ErrorCode is 40012 or 40020)
        {
            throw new ControlUnavailableException(current.Owner);
        }

        var acquired = await QueryControlAsync(cancellationToken);
        if (acquired.Owner != "adapter") throw new ControlUnavailableException(acquired.Owner);
    }

    public async Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            var control = await QueryControlAsync(cancellationToken);
            var cached = GetFreshPushSnapshot();
            if (cached is not null) return cached with { ControlOwner = control.Owner };

            using var taskStatus = await QueryTaskStatusAsync(null, cancellationToken);
            var currentTask = ParseTaskStatuses(taskStatus.RootElement)
                .FirstOrDefault(task => task.Status is 1 or 2 or 3);
            return new AgvSnapshotResponse(
                true,
                control.Owner,
                ReadStation(taskStatus.RootElement),
                TryParseGuid(currentTask?.TaskId));
        }
        catch (Exception exception) when (exception is SocketException or IOException or TimeoutException)
        {
            _logger.LogWarning(exception, "Unable to query AGV snapshot at {Host}.", _options.Host);
            return new AgvSnapshotResponse(false, "unknown", null, null);
        }
    }

    public async Task<AdapterTaskResponse> NavigateAsync(
        Guid taskId,
        string? sourceStationId,
        string stationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stationId)) throw new ArgumentException("Target station is required.", nameof(stationId));
        if (string.IsNullOrWhiteSpace(sourceStationId))
        {
            throw new InvalidOperationException("A source station is required for real AGV 3066 navigation.");
        }
        EnsureAscii(sourceStationId, nameof(sourceStationId));
        EnsureAscii(stationId, nameof(stationId));

        await EnsureReadyAsync(cancellationToken);

        var request = new[]
        {
            new
            {
                task_id = taskId.ToString("N"),
                source_id = sourceStationId,
                id = stationId
            }
        };
        using var response = await _commandChannel.RequestAsync(NavigateApi, request, cancellationToken);
        EnsureSuccess(response, NavigateApi);
        return new AdapterTaskResponse(taskId, taskId.ToString("N"), stationId, "moving", null);
    }

    public async Task<AdapterTaskResponse?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        using var response = await QueryTaskStatusAsync([taskId.ToString("N")], cancellationToken);
        EnsureSuccess(response, QueryTaskApi);
        var status = ParseTaskStatuses(response.RootElement).FirstOrDefault(item => item.TaskId == taskId.ToString("N"));
        if (status is null) return null;
        return new AdapterTaskResponse(
            taskId,
            taskId.ToString("N"),
            status.TargetStationId ?? string.Empty,
            MapTaskState(status.Status),
            status.Status == 5 ? ReadString(response.RootElement, "err_msg") : null);
    }

    public async Task<AdapterTaskResponse?> PauseAsync(Guid taskId, CancellationToken cancellationToken)
    {
        using var response = await _commandChannel.RequestAsync(PauseApi, null, cancellationToken);
        EnsureSuccess(response, PauseApi);
        return new AdapterTaskResponse(taskId, taskId.ToString("N"), string.Empty, "paused", null);
    }

    public async Task<AdapterTaskResponse?> ResumeAsync(Guid taskId, CancellationToken cancellationToken)
    {
        using var response = await _commandChannel.RequestAsync(ResumeApi, null, cancellationToken);
        EnsureSuccess(response, ResumeApi);
        return new AdapterTaskResponse(taskId, taskId.ToString("N"), string.Empty, "moving", null);
    }

    public async Task<AdapterTaskResponse?> CancelAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var payload = _options.CancelApiId == 3068 ? new { task_id = taskId.ToString("N") } : null;
        using var response = await _commandChannel.RequestAsync((ushort)_options.CancelApiId, payload, cancellationToken);
        EnsureSuccess(response, _options.CancelApiId);
        return new AdapterTaskResponse(taskId, taskId.ToString("N"), string.Empty, "cancelled", null);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.EnablePush)
        {
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pushLoop = PushLoopAsync(_lifetime.Token);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _lifetime?.Cancel();
        if (_pushLoop is not null)
        {
            try { await _pushLoop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    public void Dispose()
    {
        _lifetime?.Cancel();
        _statusChannel.Dispose();
        _commandChannel.Dispose();
        _controlChannel.Dispose();
        _lifetime?.Dispose();
    }

    private async Task<JsonDocument> QueryTaskStatusAsync(string[]? taskIds, CancellationToken cancellationToken)
    {
        object? request = taskIds is null ? null : new { task_ids = taskIds };
        return await _statusChannel.RequestAsync(QueryTaskApi, request, cancellationToken);
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        var readiness = GetFreshReadiness();
        if (readiness is null)
        {
            using var response = await _statusChannel.RequestAsync(RealtimeStatusApi, null, cancellationToken);
            EnsureSuccess(response, RealtimeStatusApi);
            readiness = ReadReadiness(response.RootElement);
        }

        if (readiness.Emergency == true) throw new InvalidOperationException("AGV emergency stop is active.");
        if (readiness.Blocked == true) throw new InvalidOperationException("AGV is blocked.");
        if (readiness.FatalCount > 0) throw new InvalidOperationException("AGV has active fatal alarms.");
        if (readiness.ErrorCount > 0) throw new InvalidOperationException("AGV has active errors.");
        if (readiness.ForkAutoFlag == false) throw new InvalidOperationException("AGV fork is not in automatic mode.");
        if (readiness.RelocStatus is { } relocStatus && relocStatus != 1)
        {
            throw new InvalidOperationException($"AGV relocation status is {relocStatus}, expected SUCCESS (1).");
        }
        if (readiness.Confidence is { } confidence && confidence < _options.MinimumConfidence)
        {
            throw new InvalidOperationException($"AGV localization confidence {confidence} is below {_options.MinimumConfidence}.");
        }
    }

    private async Task<ControlInfo> QueryControlAsync(CancellationToken cancellationToken)
    {
        using var response = await _statusChannel.RequestAsync(QueryControlApi, null, cancellationToken);
        EnsureSuccess(response, QueryControlApi);
        var root = response.RootElement;
        var locked = ReadBool(root, "locked");
        var nickname = ReadString(root, "nick_name");
        var owner = !locked
            ? "none"
            : string.Equals(nickname, _options.NickName, StringComparison.OrdinalIgnoreCase)
                ? "adapter"
                : nickname ?? ReadString(root, "ip") ?? "unknown";
        _lastControlOwner = owner;
        return new ControlInfo(owner);
    }

    private async Task ConfigurePushAsync(CancellationToken cancellationToken)
    {
        var request = new
        {
            interval = _options.PushIntervalMs,
            included_fields = PushFields
        };
        using var response = await _controlChannel.RequestAsync(ConfigurePushApi, request, cancellationToken);
        EnsureSuccess(response, ConfigurePushApi);
    }

    private async Task PushLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConfigurePushAsync(cancellationToken);
                using var client = new TcpClient { NoDelay = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.ConnectTimeoutMs);
                await client.ConnectAsync(_options.Host, _options.PushPort, timeout.Token);
                await using var stream = client.GetStream();

                while (!cancellationToken.IsCancellationRequested)
                {
                    var packet = await AgvTcpProtocol.ReadPacketAsync(stream, _options.MaxPayloadBytes, cancellationToken);
                    if (packet.ApiId != PushApi) continue;
                    using var document = packet.Payload.Length == 0 ? null : JsonDocument.Parse(packet.Payload);
                    if (document is not null) UpdatePushSnapshot(document.RootElement);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "AGV push channel disconnected; retrying.");
                try { await Task.Delay(_options.PushReconnectDelayMs, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }
    }

    private void UpdatePushSnapshot(JsonElement root)
    {
        var snapshot = new AgvSnapshotResponse(
            true,
            _lastControlOwner,
            ReadStation(root),
            TryParseGuid(ReadString(root, "current_task_id") ?? ReadString(root, "task_id")));
        var readiness = ReadReadiness(root);
        lock (_snapshotLock)
        {
            _pushSnapshot = snapshot;
            _pushReadiness = readiness;
            _pushReceivedAt = DateTimeOffset.UtcNow;
        }
    }

    private AgvSnapshotResponse? GetFreshPushSnapshot()
    {
        lock (_snapshotLock)
        {
            return _pushSnapshot is not null && DateTimeOffset.UtcNow - _pushReceivedAt <= TimeSpan.FromSeconds(3)
                ? _pushSnapshot
                : null;
        }
    }

    private DeviceReadiness? GetFreshReadiness()
    {
        lock (_snapshotLock)
        {
            return _pushReadiness is not null && DateTimeOffset.UtcNow - _pushReceivedAt <= TimeSpan.FromSeconds(3)
                ? _pushReadiness
                : null;
        }
    }

    private static void EnsureSuccess(JsonDocument response, int apiId)
    {
        var root = response.RootElement;
        var code = ReadInt(root, "ret_code") ?? 0;
        if (code != 0) throw new AgvApiException(apiId, code, ReadString(root, "err_msg"));
    }

    private static IReadOnlyList<DeviceTaskStatus> ParseTaskStatuses(JsonElement root)
    {
        if (!root.TryGetProperty("task_status_list", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var statuses = new List<DeviceTaskStatus>();
        foreach (var item in list.EnumerateArray())
        {
            var taskId = ReadString(item, "task_id");
            var status = ReadInt(item, "status");
            if (taskId is not null && status is not null)
            {
                statuses.Add(new DeviceTaskStatus(taskId, status.Value, ReadString(item, "target_name")));
            }
        }
        return statuses;
    }

    private static string MapTaskState(int status) => status switch
    {
        0 or 1 => "accepted",
        2 => "moving",
        3 => "paused",
        4 => "arrived",
        5 => "failed",
        6 => "cancelled",
        7 or 404 => "unknown",
        _ => "unknown"
    };

    private static string? ReadStation(JsonElement root)
    {
        foreach (var name in new[] { "current_station", "current_station_id", "closest_target" })
        {
            var value = ReadString(root, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        if (root.TryGetProperty("running_status", out var runningStatus) && runningStatus.ValueKind == JsonValueKind.Object)
        {
            return ReadString(runningStatus, "target_id");
        }
        return null;
    }

    private static DeviceReadiness ReadReadiness(JsonElement root) => new(
        ReadNullableBool(root, "emergency"),
        ReadNullableBool(root, "blocked"),
        ReadArrayCount(root, "fatals"),
        ReadArrayCount(root, "errors"),
        ReadNullableBool(root, "fork_auto_flag"),
        ReadInt(root, "reloc_status"),
        ReadDouble(root, "confidence"));

    private static int? ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
        return null;
    }

    private static bool ReadBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind == JsonValueKind.True) return true;
        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var result) && result;
    }

    private static bool? ReadNullableBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var result)
            ? result
            : null;
    }

    private static int ReadArrayCount(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static double? ReadDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out number)) return number;
        return null;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static Guid? TryParseGuid(string? value) => Guid.TryParse(value, out var id) ? id : null;

    private static void EnsureAscii(string value, string parameterName)
    {
        if (value.Any(character => character > 0x7F))
        {
            throw new ArgumentException("AGV station IDs must contain ASCII characters only.", parameterName);
        }
    }

    private sealed record ControlInfo(string Owner);
    private sealed record DeviceTaskStatus(string TaskId, int Status, string? TargetStationId);
    private sealed record DeviceReadiness(
        bool? Emergency,
        bool? Blocked,
        int FatalCount,
        int ErrorCount,
        bool? ForkAutoFlag,
        int? RelocStatus,
        double? Confidence);
}
