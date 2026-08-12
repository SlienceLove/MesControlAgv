using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MesControlAgv.Adapter;
using MesControlAgv.Contracts;
using MesControlAgv.Domain.Map;
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
    public bool RequireCompleteSafetyStatus { get; set; }
    public bool RequireAutomaticMode { get; set; } = true;
    public double? MaximumNavigationSpeedMetersPerSecond { get; set; }
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

    public async Task<JsonDocument> RequestAsync(
        ushort apiId,
        object? payload,
        CancellationToken cancellationToken,
        Action? onBeforeWrite = null)
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
                onBeforeWrite?.Invoke();
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
            catch (OperationCanceledException)
            {
                ResetConnection();
                throw;
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

public sealed class TcpAgvClient :
    IAgvDeviceClient,
    IPhysicalAgvDeviceClient,
    IControllerMapEvidenceDeviceClient,
    IControlAcquisitionEvidence,
    INavigationAttemptState,
    ICancellationAttemptState,
    IHostedService,
    IDisposable
{
    private const ushort QueryDeviceInfoApi = 1000;
    private const ushort QueryMapCatalogApi = 1300;
    private const ushort QueryStationCatalogApi = 1301;
    private const ushort QueryMapMd5Api = 1302;
    private const ushort QueryLocalizationApi = 1021;
    private const ushort QueryControlApi = 1060;
    private const ushort AcquireControlApi = 4005;
    private const ushort ReleaseControlApi = 4006;
    private const ushort NavigateApi = 3066;
    private const ushort QueryTaskApi = 1110;
    private const ushort PauseApi = 3001;
    private const ushort ResumeApi = 3002;
    private const ushort CancelApi = 3067;
    private const ushort RealtimeStatusApi = 1101;
    private const ushort ConfigurePushApi = 9300;
    private const ushort DownloadMapApi = 4011;
    private const ushort PushApi = 19301;

    private static readonly string[] PushFields =
    [
        "x", "y", "angle", "confidence", "current_station", "reloc_status", "task_status", "target_id",
        "blocked", "block_reason", "emergency", "fatals", "errors", "battery_level",
        "charging", "fork_auto_flag", "dispatch_mode", "manualBlock", "src_release",
        "current_map", "current_map_md5"
    ];

    private readonly TcpAgvOptions _options;
    private readonly AdapterRunMode _runMode;
    private readonly ILogger<TcpAgvClient> _logger;
    private readonly TcpApiChannel _statusChannel;
    private readonly TcpApiChannel _commandChannel;
    private readonly TcpApiChannel _controlChannel;
    // Serialize ownership read/acquire/reconcile and release transactions.
    private readonly SemaphoreSlim _controlTransactionGate = new(1, 1);
    private readonly object _snapshotLock = new();
    private readonly ConcurrentDictionary<Guid, RoutePlan> _routes = new();
    private readonly ConcurrentDictionary<Guid, Guid> _parentTaskIds = new();
    private readonly ConcurrentDictionary<Guid, byte> _navigationAttempts = new();
    private readonly ConcurrentDictionary<Guid, byte> _navigationWrites = new();
    private readonly ConcurrentDictionary<Guid, byte> _cancellationWrites = new();
    private AgvSnapshotResponse? _pushSnapshot;
    private DeviceReadiness? _pushReadiness;
    private DateTimeOffset _pushReceivedAt;
    private string _lastControlOwner = "unknown";
    private CancellationTokenSource? _lifetime;
    private Task? _pushLoop;

    public TcpAgvClient(
        IOptions<TcpAgvOptions> options,
        ILogger<TcpAgvClient> logger,
        AdapterRunMode? runMode = null)
    {
        _options = options.Value;
        _runMode = runMode ?? AdapterRunMode.Standard;
        _logger = logger;
        _statusChannel = new TcpApiChannel(_options.Host, _options.StatusPort, _options);
        _commandChannel = new TcpApiChannel(_options.Host, _options.CommandPort, _options);
        _controlChannel = new TcpApiChannel(_options.Host, _options.ControlPort, _options);
    }

    public async Task EnsureControlAsync(CancellationToken cancellationToken) =>
        _ = await EnsureControlWithResultAsync(cancellationToken);

    public async Task<ControlAcquisitionResult> EnsureControlWithResultAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfMutationIsBlocked("control acquisition");
        await _controlTransactionGate.WaitAsync(cancellationToken);
        try
        {
            var current = await QueryControlAsync(cancellationToken);
            if (current.Owner == "adapter") return new ControlAcquisitionResult(false);
            if (!_options.AcquireControl)
            {
                throw new ControlUnavailableException(current.Owner);
            }

            var mayHaveWrittenRequest = false;
            try
            {
                var request = new { nick_name = _options.NickName };
                LogMutationRequest(AcquireControlApi, request);
                using var response = await _controlChannel.RequestAsync(
                    AcquireControlApi,
                    request,
                    cancellationToken,
                    () => mayHaveWrittenRequest = true);
                LogMutationResponse(AcquireControlApi, response.RootElement);
                EnsureSuccess(response, AcquireControlApi);

                var acquired = await QueryControlAsync(cancellationToken);
                if (acquired.Owner != "adapter") throw new ControlUnavailableException(acquired.Owner);
                return new ControlAcquisitionResult(true);
            }
            catch (AgvApiException exception) when (exception.ErrorCode is 40012 or 40020)
            {
                throw new ControlUnavailableException(current.Owner);
            }
            catch (Exception exception) when (
                mayHaveWrittenRequest && IsControlAcquisitionOutcomeUnknown(exception))
            {
                await ReconcileAndRollbackUnknownControlAcquisitionAsync(exception);
                throw;
            }
        }
        finally
        {
            _controlTransactionGate.Release();
        }
    }

    public async Task<bool> ReleaseControlAsync(CancellationToken cancellationToken)
    {
        ThrowIfMutationIsBlocked("control release");
        await _controlTransactionGate.WaitAsync(cancellationToken);
        try
        {
            return await ReleaseControlCoreAsync(cancellationToken);
        }
        finally
        {
            _controlTransactionGate.Release();
        }
    }

    private async Task<bool> ReleaseControlCoreAsync(CancellationToken cancellationToken)
    {
        // Keep ownership read, mutation audit, 4006 write, and confirmation as one transaction.
        var current = await QueryControlAsync(cancellationToken);
        if (current.Owner != "adapter") return false;

        LogMutationRequest(ReleaseControlApi, new { });
        using var response = await _controlChannel.RequestAsync(ReleaseControlApi, null, cancellationToken);
        LogMutationResponse(ReleaseControlApi, response.RootElement);
        EnsureSuccess(response, ReleaseControlApi);

        var released = await QueryControlAsync(cancellationToken);
        if (released.Owner == "adapter") throw new ControlReleaseUnconfirmedException();
        return true;
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
            var currentTaskId = TryParseGuid(currentTask?.TaskId);
            return new AgvSnapshotResponse(
                true,
                control.Owner,
                ReadStation(taskStatus.RootElement),
                MapToParentTaskId(currentTaskId));
        }
        catch (Exception exception) when (exception is SocketException or IOException or TimeoutException)
        {
            _logger.LogWarning(exception, "Unable to query AGV snapshot at {Host}.", _options.Host);
            return new AgvSnapshotResponse(false, "unknown", null, null);
        }
    }

    public async Task<AgvSafetyReadinessResponse> GetSafetyReadinessAsync(CancellationToken cancellationToken)
    {
        using var response = await _statusChannel.RequestAsync(
            RealtimeStatusApi,
            new { return_laser = false },
            cancellationToken);
        EnsureSuccess(response, RealtimeStatusApi);
        var readiness = ReadReadiness(response.RootElement);

        using var localizationResponse = await _statusChannel.RequestAsync(
            QueryLocalizationApi,
            null,
            cancellationToken);
        EnsureSuccess(localizationResponse, QueryLocalizationApi);
        readiness = readiness with
        {
            RelocStatus = ReadInt(localizationResponse.RootElement, "reloc_status")
        };

        using var deviceInfoResponse = await _statusChannel.RequestAsync(
            QueryDeviceInfoApi,
            null,
            cancellationToken);
        EnsureSuccess(deviceInfoResponse, QueryDeviceInfoApi);

        return readiness.ToResponse(
            DateTimeOffset.UtcNow,
            ReadString(deviceInfoResponse.RootElement, "model")?.Trim(),
            ReadString(deviceInfoResponse.RootElement, "version")?.Trim());
    }

    public async Task<ControllerMapEvidenceResponse?> GetControllerMapEvidenceAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadControllerMapEvidenceAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is
            AgvApiException or IOException or SocketException or TimeoutException or SmapParseException)
        {
            _logger.LogWarning(exception, "Unable to obtain controller map evidence at {Host}.", _options.Host);
            return null;
        }
    }

    private async Task<ControllerMapEvidenceResponse?> ReadControllerMapEvidenceAsync(
        CancellationToken cancellationToken)
    {
        using var mapCatalogResponse = await _statusChannel.RequestAsync(
            QueryMapCatalogApi,
            null,
            cancellationToken);
        EnsureSuccess(mapCatalogResponse, QueryMapCatalogApi);
        var currentMap = ReadString(mapCatalogResponse.RootElement, "current_map")?.Trim();
        if (string.IsNullOrWhiteSpace(currentMap)) return null;
        var storedMapNames = ReadStringArray(mapCatalogResponse.RootElement, "maps");

        using var stationCatalogResponse = await _statusChannel.RequestAsync(
            QueryStationCatalogApi,
            null,
            cancellationToken);
        EnsureSuccess(stationCatalogResponse, QueryStationCatalogApi);
        var stationIds = ReadStationCatalog(stationCatalogResponse.RootElement);

        var mapMd5 = await QueryMapMd5Async(currentMap, storedMapNames, cancellationToken);

        using var mapResponse = await _controlChannel.RequestAsync(
            DownloadMapApi,
            new { map_name = currentMap },
            cancellationToken);
        EnsureSuccess(mapResponse, DownloadMapApi);
        var mapBytes = JsonSerializer.SerializeToUtf8Bytes(mapResponse.RootElement);
        using var mapStream = new MemoryStream(mapBytes, writable: false);
        var mapDocument = SmapParser.Parse(mapStream);
        var mapIdentity = SmapMapIdentity.FromDocument(mapDocument, mapMd5 ?? "unavailable");

        var catalogMatchesMap = !string.IsNullOrWhiteSpace(mapMd5)
            && MapNamesEqual(currentMap, mapIdentity.MapName)
            && stationIds.ToHashSet(StringComparer.Ordinal)
                .SetEquals(mapIdentity.StationMarks);

        return new ControllerMapEvidenceResponse(
            catalogMatchesMap,
            "vendor-tcp:1300,1301,1302,4011",
            NormalizeMapName(currentMap),
            mapIdentity.MapVersion,
            mapMd5,
            stationIds,
            mapIdentity.DirectedEdges
                .Select(edge => new ControllerDirectedEdgeResponse(edge.FromMark, edge.ToMark))
                .ToArray(),
            DateTimeOffset.UtcNow);
    }

    private async Task<string?> QueryMapMd5Async(
        string currentMap,
        IReadOnlyList<string> storedMapNames,
        CancellationToken cancellationToken)
    {
        var candidates = storedMapNames
            .Where(mapName => MapNamesEqual(mapName, currentMap))
            .Append(currentMap)
            .Append(EnsureSmapExtension(currentMap))
            .Where(mapName => !string.IsNullOrWhiteSpace(mapName))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        AgvApiException? notFound = null;

        foreach (var candidate in candidates)
        {
            try
            {
                using var response = await _statusChannel.RequestAsync(
                    QueryMapMd5Api,
                    new { map_names = new[] { candidate } },
                    cancellationToken);
                EnsureSuccess(response, QueryMapMd5Api);
                var md5 = ReadMapMd5(response.RootElement, candidate)
                    ?? ReadMapMd5(response.RootElement, currentMap);
                if (!string.IsNullOrWhiteSpace(md5)) return md5;
            }
            catch (AgvApiException exception) when (exception.ErrorCode == 40051)
            {
                notFound = exception;
            }
        }

        if (notFound is not null) throw notFound;
        return null;
    }

    public async Task<AgvTaskResponse> NavigateAsync(
        Guid taskId,
        string? sourceStationId,
        string stationId,
        CancellationToken cancellationToken) =>
        await NavigateAsync(taskId, sourceStationId, stationId, null, cancellationToken);

    public async Task<AgvTaskResponse> NavigateAsync(
        Guid taskId,
        string? sourceStationId,
        string stationId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken)
    {
        ThrowIfMutationIsBlocked("navigation");
        if (string.IsNullOrWhiteSpace(stationId)) throw new ArgumentException("Target station is required.", nameof(stationId));
        var normalizedTargetStationId = stationId.Trim();
        var route = BuildNavigationRoute(taskId, sourceStationId, normalizedTargetStationId, path);
        _routes[taskId] = route;
        foreach (var segment in route.Segments) _parentTaskIds[segment.TaskId] = taskId;

        var existingStatuses = await QueryRouteStatusesAsync(route, cancellationToken);
        if (existingStatuses.Any(status => status.Status != 404))
        {
            return CreateRouteResponse(taskId, route, existingStatuses);
        }
        if (_navigationAttempts.ContainsKey(taskId))
        {
            return CreateDispatchUnconfirmedResponse(taskId, route);
        }

        await EnsureReadyAsync(cancellationToken);
        // This is deliberately read-only: losing ownership in this window must
        // block dispatch instead of silently issuing another 4005.
        await EnsureControlIsHeldAsync(cancellationToken);

        var request = new
        {
            move_task_list = route.Segments
                .Select(segment => new MoveTaskRequest(
                    segment.DeviceTaskId,
                    segment.SourceStationId,
                    segment.TargetStationId,
                    _options.MaximumNavigationSpeedMetersPerSecond))
                .ToArray()
        };
        _navigationAttempts.TryAdd(taskId, 0);
        LogMutationRequest(NavigateApi, request);
        try
        {
            using var response = await _commandChannel.RequestAsync(
                NavigateApi,
                request,
                cancellationToken,
                () => _navigationWrites.TryAdd(taskId, 0));
            LogMutationResponse(NavigateApi, response.RootElement);
            EnsureSuccess(response, NavigateApi);

            var confirmedStatuses = await QueryRouteStatusesAsync(route, cancellationToken);
            return confirmedStatuses.Count == 0
                ? CreateDispatchUnconfirmedResponse(taskId, route)
                : CreateRouteResponse(taskId, route, confirmedStatuses);
        }
        catch (TimeoutException) when (MayHaveWrittenNavigation(taskId))
        {
            _logger.LogWarning(
                "AGV mutation response was not confirmed: api_id={ApiId}, operation_id={OperationId}.",
                NavigateApi,
                taskId.ToString("N"));
            try
            {
                var reconciledStatuses = await QueryRouteStatusesAsync(route, cancellationToken);
                return reconciledStatuses.Count == 0
                    ? CreateDispatchUnconfirmedResponse(taskId, route)
                    : CreateRouteResponse(taskId, route, reconciledStatuses);
            }
            catch (Exception exception) when (exception is IOException or SocketException or TimeoutException)
            {
                _logger.LogWarning(
                    exception,
                    "AGV dispatch reconciliation was unavailable: api_id={ApiId}, operation_id={OperationId}.",
                    QueryTaskApi,
                    taskId.ToString("N"));
                return CreateDispatchUnconfirmedResponse(taskId, route);
            }
        }
    }

    public Task<AgvTaskResponse?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        GetTaskAsync(taskId, null, cancellationToken);

    public async Task<AgvTaskResponse?> GetTaskAsync(
        Guid taskId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken)
    {
        var route = ResolveRoute(taskId, path);
        var statuses = await QueryRouteStatusesAsync(route, cancellationToken);
        return statuses.Count == 0 ? null : CreateRouteResponse(taskId, route, statuses);
    }

    public async Task<AgvTaskResponse?> PauseAsync(Guid taskId, CancellationToken cancellationToken)
    {
        ThrowIfMutationIsBlocked("pause");
        LogMutationRequest(PauseApi, new { });
        using var response = await _commandChannel.RequestAsync(PauseApi, null, cancellationToken);
        LogMutationResponse(PauseApi, response.RootElement);
        EnsureSuccess(response, PauseApi);
        return new AgvTaskResponse(taskId, taskId.ToString("N"), string.Empty, "paused", null);
    }

    public async Task<AgvTaskResponse?> ResumeAsync(Guid taskId, CancellationToken cancellationToken)
    {
        ThrowIfMutationIsBlocked("resume");
        LogMutationRequest(ResumeApi, new { });
        using var response = await _commandChannel.RequestAsync(ResumeApi, null, cancellationToken);
        LogMutationResponse(ResumeApi, response.RootElement);
        EnsureSuccess(response, ResumeApi);
        return new AgvTaskResponse(taskId, taskId.ToString("N"), string.Empty, "moving", null);
    }

    public Task<AgvTaskResponse?> CancelAsync(Guid taskId, CancellationToken cancellationToken) =>
        CancelAsync(taskId, null, cancellationToken);

    public async Task<AgvTaskResponse?> CancelAsync(
        Guid taskId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken)
    {
        ThrowIfMutationIsBlocked("cancellation");
        var route = ResolveRoute(taskId, path);
        var activeStatuses = await QueryRouteStatusesAsync(route, cancellationToken);
        if (!activeStatuses.Any(status => status.Status is 1 or 2 or 3))
        {
            return CreateCancellationResponse(taskId, route, activeStatuses);
        }

        LogMutationRequest(CancelApi, new { });
        using var response = await _commandChannel.RequestAsync(
            CancelApi,
            null,
            cancellationToken,
            () => _cancellationWrites.TryAdd(taskId, 0));
        LogMutationResponse(CancelApi, response.RootElement);
        EnsureSuccess(response, CancelApi);

        // Command acknowledgement alone does not prove that the AGV cancelled the task.
        using var statusResponse = await QueryTaskStatusAsync(route.Segments.Select(segment => segment.DeviceTaskId).ToArray(), cancellationToken);
        EnsureSuccess(statusResponse, QueryTaskApi);
        var statuses = ParseTaskStatuses(statusResponse.RootElement)
            .Where(status => route.DeviceTaskIds.Contains(status.TaskId, StringComparer.Ordinal))
            .ToArray();
        var cancellationConfirmed = IsCancellationConfirmed(route, statuses);

        if (cancellationConfirmed)
        {
            return new AgvTaskResponse(
                taskId,
                route.DeviceTaskId,
                ResolveTargetStationId(route, statuses),
                "cancelled",
                null,
                Path: route.Path);
        }

        return CreateCancellationResponse(taskId, route, statuses);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_runMode.IsReadOnlyPreflight) return Task.CompletedTask;
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
        _controlTransactionGate.Dispose();
        _lifetime?.Dispose();
    }

    public bool MayHaveWrittenNavigation(Guid taskId) => _navigationWrites.ContainsKey(taskId);

    public bool MayHaveWrittenCancellation(Guid taskId) => _cancellationWrites.ContainsKey(taskId);

    private async Task<JsonDocument> QueryTaskStatusAsync(string[]? taskIds, CancellationToken cancellationToken)
    {
        object? request = taskIds is null ? null : new { task_ids = taskIds };
        return await _statusChannel.RequestAsync(QueryTaskApi, request, cancellationToken);
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        DeviceReadiness? readiness = _options.RequireCompleteSafetyStatus
            ? null
            : GetFreshReadiness();
        if (readiness is null)
        {
            using var response = await _statusChannel.RequestAsync(
                RealtimeStatusApi,
                new { return_laser = false },
                cancellationToken);
            EnsureSuccess(response, RealtimeStatusApi);
            readiness = ReadReadiness(response.RootElement);

            if (_options.RequireCompleteSafetyStatus)
            {
                using var localizationResponse = await _statusChannel.RequestAsync(
                    QueryLocalizationApi,
                    null,
                    cancellationToken);
                EnsureSuccess(localizationResponse, QueryLocalizationApi);
                readiness = readiness with
                {
                    RelocStatus = ReadInt(localizationResponse.RootElement, "reloc_status")
                };
            }
        }

        if (_options.RequireCompleteSafetyStatus && !readiness.HasCompleteBaseSafetyStatus)
        {
            throw new InvalidOperationException("AGV safety status is incomplete; dispatch is blocked.");
        }
        if (readiness.Emergency == true) throw new InvalidOperationException("AGV emergency stop is active.");
        if (readiness.Blocked == true) throw new InvalidOperationException("AGV is blocked.");
        if (readiness.ManualBlock == true) throw new InvalidOperationException("AGV manual block is active.");
        if (readiness.FatalCount > 0) throw new InvalidOperationException("AGV has active fatal alarms.");
        if (readiness.ErrorCount > 0) throw new InvalidOperationException("AGV has active errors.");
        if (readiness.ForkAutomatic == false) throw new InvalidOperationException("AGV fork is not in automatic mode.");
        if (readiness.VehicleOperatingMode == "manual")
        {
            throw new InvalidOperationException("AGV vehicle is explicitly in manual mode; dispatch is blocked.");
        }
        if (_options.RequireCompleteSafetyStatus
            && _options.RequireAutomaticMode
            && readiness.VehicleOperatingMode != "automatic")
        {
            throw new InvalidOperationException(
                "AGV vehicle automatic-mode signal is unavailable or unconfirmed; dispatch is blocked.");
        }
        if (readiness.RelocStatus is { } relocStatus && relocStatus != 1)
        {
            throw new InvalidOperationException($"AGV relocation status is {relocStatus}, expected SUCCESS (1).");
        }
        if (readiness.Confidence is { } confidence
            && (!double.IsFinite(confidence) || confidence < _options.MinimumConfidence))
        {
            throw new InvalidOperationException($"AGV localization confidence {confidence} is below {_options.MinimumConfidence}.");
        }
    }

    private async Task EnsureControlIsHeldAsync(CancellationToken cancellationToken)
    {
        var current = await QueryControlAsync(cancellationToken);
        if (current.Owner != "adapter") throw new ControlUnavailableException(current.Owner);
    }

    private static bool IsControlAcquisitionOutcomeUnknown(Exception exception) =>
        exception is IOException
            or SocketException
            or TimeoutException
            or OperationCanceledException
            or JsonException
        || exception is AgvApiException { ApiId: QueryControlApi };

    private async Task ReconcileAndRollbackUnknownControlAcquisitionAsync(Exception acquisitionException)
    {
        _logger.LogWarning(
            acquisitionException,
            "AGV control acquisition result is unknown after a possible 4005 write; rechecking ownership with 1060.");

        using var cleanupTimeout = new CancellationTokenSource(GetCleanupTimeout());
        ControlInfo reconciled;
        try
        {
            reconciled = await QueryControlAsync(cleanupTimeout.Token);
        }
        catch (Exception reconciliationException)
        {
            _logger.LogError(
                reconciliationException,
                "AGV control acquisition could not be reconciled after a possible 4005 write; no 4006 was sent.");
            return;
        }

        // An uncertain 4005 result cannot prove that this invocation acquired
        // the ownership now observed. A concurrent process may own the same
        // nickname, so fail closed and never guess by issuing 4006 here.
        if (reconciled.Owner == "adapter")
        {
            _logger.LogError(
                "AGV control remains owned by the Adapter after an uncertain 4005 attempt; no 4006 was sent because acquisition ownership could not be attributed to this call.");
        }
    }

    private TimeSpan GetCleanupTimeout()
    {
        var requestTimeoutMs = Math.Max(1, _options.RequestTimeoutMs);
        var connectTimeoutMs = Math.Max(1, _options.ConnectTimeoutMs);
        return TimeSpan.FromMilliseconds((long)requestTimeoutMs * 4 + connectTimeoutMs * 2L);
    }

    private async Task<ControlInfo> QueryControlAsync(CancellationToken cancellationToken)
    {
        using var response = await _statusChannel.RequestAsync(QueryControlApi, null, cancellationToken);
        EnsureSuccess(response, QueryControlApi);
        var root = response.RootElement;
        var locked = ReadNullableBool(root, "locked")
            ?? throw new AgvProtocolException("AGV API 1060 did not provide a valid locked state.");
        var nickname = ReadString(root, "nick_name");
        var owner = !locked
            ? "none"
            : string.Equals(nickname, _options.NickName, StringComparison.Ordinal)
                ? "adapter"
                : nickname ?? ReadString(root, "ip") ?? "unknown";
        _lastControlOwner = owner;
        return new ControlInfo(owner);
    }

    private async Task ConfigurePushAsync(CancellationToken cancellationToken)
    {
        ThrowIfMutationIsBlocked("push configuration");
        var request = new
        {
            interval = _options.PushIntervalMs,
            included_fields = PushFields
        };
        LogMutationRequest(ConfigurePushApi, new
        {
            request.interval,
            included_field_count = PushFields.Length
        });
        using var response = await _controlChannel.RequestAsync(ConfigurePushApi, request, cancellationToken);
        LogMutationResponse(ConfigurePushApi, response.RootElement);
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
        var readiness = ReadReadiness(root);
        var snapshot = new AgvSnapshotResponse(
            true,
            _lastControlOwner,
            ReadStation(root),
            MapToParentTaskId(TryParseGuid(ReadString(root, "current_task_id") ?? ReadString(root, "task_id"))),
            SafetyReadiness: readiness.ToResponse(DateTimeOffset.UtcNow));
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

    private void ThrowIfMutationIsBlocked(string operation) => _runMode.ThrowIfMutationIsBlocked(operation);

    private void LogMutationRequest(ushort apiId, object request) =>
        _logger.LogInformation(
            "AGV mutation request audit: {MutationAudit}",
            JsonSerializer.Serialize(new { api_id = apiId, request }));

    private void LogMutationResponse(ushort apiId, JsonElement response) =>
        _logger.LogInformation(
            "AGV mutation response audit: {MutationAudit}",
            JsonSerializer.Serialize(new
            {
                api_id = apiId,
                ret_code = ReadInt(response, "ret_code"),
                err_msg = ReadString(response, "err_msg"),
                create_on = ReadString(response, "create_on")
            }));

    private static IReadOnlyList<DeviceTaskStatus> ParseTaskStatuses(JsonElement root)
    {
        var statusRoot = GetTaskStatusRoot(root);
        if (!statusRoot.TryGetProperty("task_status_list", out var list) || list.ValueKind != JsonValueKind.Array) return [];
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

    private static JsonElement GetTaskStatusRoot(JsonElement root) =>
        root.TryGetProperty("task_status_package", out var package)
            && package.ValueKind == JsonValueKind.Object
            ? package
            : root;

    private static string MapTaskState(int status) => status switch
    {
        0 => "unknown",
        1 => "accepted",
        2 => "moving",
        3 => "paused",
        4 => "arrived",
        5 => "failed",
        6 => "cancelled",
        7 or 404 => "unknown",
        _ => "unknown"
    };

    private RoutePlan ResolveRoute(Guid taskId, IReadOnlyList<string>? path)
    {
        if (path is { Count: >= 2 })
        {
            var route = BuildRoute(taskId, path);
            _routes[taskId] = route;
            foreach (var segment in route.Segments) _parentTaskIds[segment.TaskId] = taskId;
            return route;
        }

        return _routes.TryGetValue(taskId, out var registered)
            ? registered
            : RoutePlan.Single(taskId);
    }

    private static RoutePlan BuildNavigationRoute(
        Guid taskId,
        string? sourceStationId,
        string targetStationId,
        IReadOnlyList<string>? requestedPath)
    {
        var normalizedSourceStationId = sourceStationId?.Trim();
        var normalizedTargetStationId = targetStationId.Trim();
        IReadOnlyList<string> path;
        if (requestedPath is { Count: > 0 })
        {
            path = NormalizePath(requestedPath);
            if (string.IsNullOrWhiteSpace(normalizedSourceStationId)
                || !StringComparer.Ordinal.Equals(path[0], normalizedSourceStationId))
            {
                throw new InvalidOperationException("The first path station must match the navigation source.");
            }
            if (!StringComparer.Ordinal.Equals(path[^1], normalizedTargetStationId))
            {
                throw new InvalidOperationException("The final path station must match the navigation target.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(normalizedSourceStationId))
                throw new InvalidOperationException("A source station is required for real AGV 3066 navigation.");
            path = [normalizedSourceStationId, normalizedTargetStationId];
        }

        return BuildRoute(taskId, path);
    }

    private static RoutePlan BuildRoute(Guid taskId, IReadOnlyList<string> requestedPath)
    {
        var path = NormalizePath(requestedPath);
        if (path.Count < 2) throw new InvalidOperationException("A route must contain at least two distinct stations.");
        foreach (var station in path) EnsureAscii(station, nameof(requestedPath));

        var segments = path.Zip(path.Skip(1), (source, target) => (source, target))
            .Select((edge, index) => new RouteSegment(
                index == 0 ? taskId : CreateSegmentTaskId(taskId, index, edge.source, edge.target),
                edge.source,
                edge.target))
            .ToArray();
        return new RoutePlan(path, segments);
    }

    private static IReadOnlyList<string> NormalizePath(IReadOnlyList<string> requestedPath)
    {
        var path = new List<string>(requestedPath.Count);
        var stations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var station in requestedPath)
        {
            if (string.IsNullOrWhiteSpace(station)) throw new InvalidOperationException("Route stations cannot be empty.");
            var normalized = station.Trim();
            if (!stations.Add(normalized)) throw new InvalidOperationException($"Route station '{normalized}' is repeated.");
            path.Add(normalized);
        }
        return path;
    }

    private async Task<IReadOnlyList<DeviceTaskStatus>> QueryRouteStatusesAsync(
        RoutePlan route,
        CancellationToken cancellationToken)
    {
        using var response = await QueryTaskStatusAsync(route.Segments.Select(segment => segment.DeviceTaskId).ToArray(), cancellationToken);
        EnsureSuccess(response, QueryTaskApi);
        return ParseTaskStatuses(response.RootElement)
            .Where(status => route.DeviceTaskIds.Contains(status.TaskId, StringComparer.Ordinal))
            .ToArray();
    }

    private static AgvTaskResponse CreateRouteResponse(
        Guid taskId,
        RoutePlan route,
        IReadOnlyList<DeviceTaskStatus> statuses)
    {
        var state = AggregateTaskState(route, statuses);
        var error = state switch
        {
            "failed" => "device_task_failed",
            "unknown" => "dispatch_not_confirmed_by_1110",
            _ => null
        };
        return new AgvTaskResponse(
            taskId,
            route.DeviceTaskId,
            ResolveTargetStationId(route, statuses),
            state,
            error,
            Path: route.Path);
    }

    private static AgvTaskResponse CreateDispatchUnconfirmedResponse(Guid taskId, RoutePlan route) =>
        new(
            taskId,
            route.DeviceTaskId,
            route.TargetStationId,
            "unknown",
            "dispatch_not_confirmed_by_1110",
            Path: route.Path);

    private static AgvTaskResponse CreateCancellationResponse(
        Guid taskId,
        RoutePlan route,
        IReadOnlyList<DeviceTaskStatus> statuses) =>
        new(
            taskId,
            route.DeviceTaskId,
            ResolveTargetStationId(route, statuses),
            IsCancellationConfirmed(route, statuses) ? "cancelled" : "unknown",
            IsCancellationConfirmed(route, statuses) ? null : "cancel_not_confirmed_by_1110",
            Path: route.Path);

    private static bool IsCancellationConfirmed(
        RoutePlan route,
        IReadOnlyList<DeviceTaskStatus> statuses)
    {
        var statusByTaskId = statuses.ToDictionary(status => status.TaskId, StringComparer.Ordinal);
        return route.Segments.All(segment =>
                statusByTaskId.TryGetValue(segment.DeviceTaskId, out var status) && status.Status is 4 or 6)
            && statuses.Any(status => status.Status == 6);
    }

    private static Guid CreateSegmentTaskId(Guid parentTaskId, int index, string sourceStationId, string targetStationId)
    {
        var seed = Encoding.UTF8.GetBytes($"{parentTaskId:N}|{index}|{sourceStationId}|{targetStationId}");
        var bytes = SHA256.HashData(seed)[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static string AggregateTaskState(RoutePlan route, IReadOnlyList<DeviceTaskStatus> statuses)
    {
        var statusByTaskId = statuses.ToDictionary(status => status.TaskId, StringComparer.Ordinal);
        var ordered = route.Segments
            .Select(segment => statusByTaskId.TryGetValue(segment.DeviceTaskId, out var status) ? status.Status : (int?)null)
            .ToArray();
        if (ordered.Any(status => status == 5)) return "failed";
        if (ordered.All(status => status == 4)) return "arrived";
        if (ordered.All(status => status is 4 or 6) && ordered.Any(status => status == 6)) return "cancelled";
        if (ordered.Any(status => status == 2)) return "moving";
        if (ordered.Any(status => status == 3)) return "paused";
        if (ordered.Any(status => status == 1)) return "accepted";
        if (ordered.Any(status => status == 4)) return "moving";
        return "unknown";
    }

    private static string ResolveTargetStationId(RoutePlan route, IReadOnlyList<DeviceTaskStatus> statuses) =>
        !string.IsNullOrWhiteSpace(route.TargetStationId)
            ? route.TargetStationId
            : statuses.LastOrDefault(status => !string.IsNullOrWhiteSpace(status.TargetStationId))?.TargetStationId ?? string.Empty;

    private Guid? MapToParentTaskId(Guid? deviceTaskId) =>
        deviceTaskId is { } id && _parentTaskIds.TryGetValue(id, out var parentTaskId)
            ? parentTaskId
            : deviceTaskId;

    private static string? ReadStation(JsonElement root)
    {
        foreach (var statusRoot in EnumerateStatusRoots(root))
        {
            foreach (var name in new[] { "current_station", "current_station_id", "closest_target" })
            {
                var value = ReadString(statusRoot, name);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }

            if (statusRoot.TryGetProperty("running_status", out var runningStatus)
                && runningStatus.ValueKind == JsonValueKind.Object)
            {
                var target = ReadString(runningStatus, "target_id");
                if (!string.IsNullOrWhiteSpace(target)) return target;
            }
        }
        return null;
    }

    private static IEnumerable<JsonElement> EnumerateStatusRoots(JsonElement root)
    {
        yield return root;
        if (root.TryGetProperty("task_status_package", out var package)
            && package.ValueKind == JsonValueKind.Object)
        {
            yield return package;
        }
    }

    private static DeviceReadiness ReadReadiness(JsonElement root) => new(
        ReadInt(root, "mode"),
        ReadNullableBool(root, "emergency"),
        ReadNullableBool(root, "blocked"),
        ReadArrayCount(root, "fatals"),
        ReadArrayCount(root, "errors"),
        ReadNullableBool(root, "fork_auto_flag"),
        ReadInt(root, "dispatch_mode"),
        ReadNullableBool(root, "manualBlock"),
        ReadNullableBool(root, "src_release"),
        ReadString(root, "current_map"),
        ReadString(root, "current_map_md5"),
        ReadInt(root, "reloc_status"),
        ReadDouble(root, "confidence"),
        HasBooleanValue(root, "emergency"),
        HasBooleanValue(root, "blocked"),
        HasArrayValue(root, "fatals"),
        HasArrayValue(root, "errors"));

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

    private static bool HasBooleanValue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && (value.ValueKind is JsonValueKind.True or JsonValueKind.False
            || value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out _));

    private static bool HasArrayValue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array;

    private static int ReadArrayCount(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static double? ReadDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number)
            && double.IsFinite(number))
            return number;
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number))
            return number;
        return null;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static IReadOnlyList<string> ReadStationCatalog(JsonElement root)
    {
        if (!root.TryGetProperty("stations", out var stations)
            || stations.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return stations.EnumerateArray()
            .Select(station => ReadString(station, "id")?.Trim())
            .Where(stationId => !string.IsNullOrWhiteSpace(stationId))
            .Cast<string>()
            .ToArray();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
    }

    private static string? ReadMapMd5(JsonElement root, string mapName)
    {
        if (!root.TryGetProperty("map_info", out var mapInfo)
            || mapInfo.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in mapInfo.EnumerateArray())
        {
            var candidateName = ReadString(item, "name");
            if (!MapNamesEqual(candidateName, mapName)) continue;
            return ReadString(item, "md5")?.Trim().ToLowerInvariant();
        }

        return null;
    }

    private static bool MapNamesEqual(string? left, string? right) =>
        string.Equals(NormalizeMapName(left), NormalizeMapName(right), StringComparison.Ordinal);

    private static string? NormalizeMapName(string? mapName)
    {
        var normalized = mapName?.Trim();
        return normalized?.EndsWith(".smap", StringComparison.OrdinalIgnoreCase) == true
            ? normalized[..^5]
            : normalized;
    }

    private static string EnsureSmapExtension(string mapName) =>
        mapName.EndsWith(".smap", StringComparison.OrdinalIgnoreCase)
            ? mapName
            : $"{mapName}.smap";

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
    private sealed record MoveTaskRequest(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("source_id")] string SourceStationId,
        [property: JsonPropertyName("id")] string TargetStationId,
        [property: JsonPropertyName("max_speed")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? MaximumSpeedMetersPerSecond);

    private sealed record RouteSegment(Guid TaskId, string SourceStationId, string TargetStationId)
    {
        public string DeviceTaskId => TaskId.ToString("N");
    }

    private sealed record RoutePlan(IReadOnlyList<string> Path, IReadOnlyList<RouteSegment> Segments)
    {
        public IReadOnlyList<string> DeviceTaskIds => Segments.Select(segment => segment.DeviceTaskId).ToArray();
        public string DeviceTaskId => Segments[0].DeviceTaskId;
        public string TargetStationId => Path.Count == 0 ? string.Empty : Path[^1];

        public static RoutePlan Single(Guid taskId) =>
            new([], [new RouteSegment(taskId, string.Empty, string.Empty)]);
    }

    private sealed record DeviceReadiness(
        int? OperatingMode,
        bool? Emergency,
        bool? Blocked,
        int FatalCount,
        int ErrorCount,
        bool? ForkAutomatic,
        int? DispatchMode,
        bool? ManualBlock,
        bool? SrcRelease,
        string? MapName,
        string? MapMd5,
        int? RelocStatus,
        double? Confidence,
        bool HasEmergency,
        bool HasBlocked,
        bool HasFatalList,
        bool HasErrorList)
    {
        public string VehicleOperatingMode => OperatingMode switch
        {
            0 => "manual",
            1 => "automatic",
            _ => "unknown"
        };

        public string? VehicleOperatingModeSource => OperatingMode is 0 or 1
            ? "vendor-1101-mode"
            : null;

        public bool HasCompleteBaseSafetyStatus => HasEmergency
            && HasBlocked
            && HasFatalList
            && HasErrorList
            && RelocStatus is not null
            && Confidence is not null;

        public AgvSafetyReadinessResponse ToResponse(
            DateTimeOffset observedAtUtc,
            string? vehicleModel = null,
            string? controllerVersion = null) => new(
            VehicleOperatingMode,
            VehicleOperatingModeSource,
            MapName,
            MapMd5,
            ForkAutomatic,
            DispatchMode,
            ManualBlock,
            SrcRelease,
            Emergency,
            Blocked,
            FatalCount,
            ErrorCount,
            RelocStatus,
            Confidence,
            observedAtUtc,
            vehicleModel,
            controllerVersion);
    }
}
