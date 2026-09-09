using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using MesControlAgv.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Receives the long-lived connection initiated by the resident ShineLab
/// process. ShineLab's native transport is a 55 AA length-framed JSON stream;
/// this service never opens a laboratory serial port.
/// </summary>
public sealed class ShineLabTcpServer(
    IOptions<ShineLabTcpOptions> configuredOptions,
    ShineLabStatusHub statusHub,
    ShineLabConnectionManager connectionManager,
    ILogger<ShineLabTcpServer> logger,
    IServiceScopeFactory? scopeFactory = null) : BackgroundService
{
    private const int InvalidFrameLimit = 8;
    private readonly ShineLabTcpOptions _options = configuredOptions.Value;
    private readonly ConcurrentDictionary<Task, byte> _clients = new();
    private readonly object _activeClientSync = new();
    private TcpListener? _listener;
    private TcpClient? _activeClient;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("ShineLab TCP server is disabled.");
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            return;
        }

        var address = ResolveAddress(_options.ListenAddress);
        _listener = new TcpListener(address, _options.Port);
        _listener.Start();
        logger.LogInformation("ShineLab TCP server listening on {Address}:{Port} using native 55AA framing.", address, _options.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                client.NoDelay = true;
                ReplaceActiveClient(client);
                logger.LogInformation(
                    "Accepted ShineLab TCP client {RemoteEndPoint}.",
                    client.Client.RemoteEndPoint);
                var task = HandleClientAsync(client, stoppingToken);
                _clients.TryAdd(task, 0);
                _ = task.ContinueWith(
                    completed => _clients.TryRemove(completed, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_activeClientSync)
            {
                _activeClient?.Dispose();
                _activeClient = null;
            }

            _listener.Stop();
            _listener = null;
            try
            {
                await Task.WhenAll(_clients.Keys);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
            {
                logger.LogDebug(exception, "ShineLab client tasks ended while stopping.");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        string? equipmentCode = null;
        var connectionId = Guid.NewGuid().ToString("N");
        using var clientTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        clientTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.StaleAfterSeconds)));
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1024, _options.ReadBufferBytes));

        try
        {
            await using var stream = client.GetStream();
            var frameReader = new ShineLabTcpFrameReader();
            var invalidFrameCount = 0;

            if (_options.SendCertificationOnConnect)
            {
                var probe = new
                {
                    strID = $"mes-cert-probe-{Guid.NewGuid():N}",
                    strMethod = "Certification",
                    equipmentCode = _options.ServerEquipmentCode,
                    body = new { chan = "A" }
                };
                await SendJsonAsync(stream, probe, clientTimeout.Token);
                logger.LogInformation(
                    "Sent diagnostic native Certification probe to ShineLab client as {EquipmentCode}.",
                    _options.ServerEquipmentCode);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), clientTimeout.Token);
                if (read == 0) break;

                frameReader.Append(buffer.AsSpan(0, read));
                while (true)
                {
                    var frameStatus = frameReader.TryRead(out var frame, out var frameError);
                    if (frameStatus == ShineLabFrameReadStatus.NeedMoreData)
                        break;

                    if (frameStatus == ShineLabFrameReadStatus.InvalidFrame)
                    {
                        invalidFrameCount++;
                        logger.LogWarning(
                            "Ignored invalid ShineLab frame on connection {ConnectionId}: {Error}",
                            connectionId,
                            frameError);
                        if (invalidFrameCount >= InvalidFrameLimit)
                            throw new InvalidDataException("Too many invalid ShineLab frames on one connection.");
                        continue;
                    }

                    if (!ShineLabTcpFrameCodec.TryDecodeJson(frame, out var json, out var decodeError))
                    {
                        invalidFrameCount++;
                        logger.LogWarning(
                            "Ignored undecodable ShineLab frame on connection {ConnectionId}: {Error}; {Summary}",
                            connectionId,
                            decodeError,
                            FrameSummary(frame));
                        if (invalidFrameCount >= InvalidFrameLimit)
                            throw new InvalidDataException("Too many undecodable ShineLab frames on one connection.");
                        continue;
                    }

                    if (!TryParse(json, out var message, out var parseError))
                    {
                        invalidFrameCount++;
                        logger.LogWarning(
                            "Ignored malformed ShineLab JSON on connection {ConnectionId}: {Error}; {Summary}",
                            connectionId,
                            parseError,
                            FrameSummary(frame));
                        if (invalidFrameCount >= InvalidFrameLimit)
                            throw new InvalidDataException("Too many malformed ShineLab messages on one connection.");
                        continue;
                    }

                    invalidFrameCount = 0;
                    clientTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.StaleAfterSeconds)));
                    equipmentCode = message.EquipmentCode;
                    logger.LogDebug(
                        "Received ShineLab frame {Method}/{StrId} from {EquipmentCode}; {Summary}",
                        message.StrMethod,
                        message.StrId,
                        message.EquipmentCode,
                        FrameSummary(frame));

                    connectionManager.Register(message.EquipmentCode, connectionId, stream);
                    var isCommandResponse = connectionManager.TryCompleteResponse(
                        message.StrId,
                        message.StrMethod,
                        message.EquipmentCode,
                        message.Body);
                    statusHub.Apply(message.StrId, message.StrMethod, message.EquipmentCode, message.Body, connectionId);
                    if (scopeFactory is not null && IsTaskEventMethod(message.StrMethod))
                    {
                        using var scope = scopeFactory.CreateScope();
                        var tasks = scope.ServiceProvider.GetRequiredService<ShineLabTaskService>();
                        await tasks.ApplyPushAsync(
                            message.StrMethod,
                            message.EquipmentCode,
                            message.Body,
                            stoppingToken);
                    }

                    if (message.StrMethod.Equals("Certification", StringComparison.OrdinalIgnoreCase))
                    {
                        var response = new
                        {
                            strID = message.StrId,
                            strMethod = message.StrMethod,
                            equipmentCode = message.EquipmentCode,
                            body = new { result = "Success", msg = "" }
                        };
                        await SendJsonAsync(stream, response, clientTimeout.Token);
                        logger.LogInformation(
                            "Replied to ShineLab Certification {StrId} for {EquipmentCode} using native framing.",
                            message.StrId,
                            message.EquipmentCode);
                    }
                    else if (isCommandResponse)
                    {
                        logger.LogDebug("Received ShineLab response {Method}/{StrId}.", message.StrMethod, message.StrId);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation(
                "ShineLab client connection {ConnectionId} timed out after {TimeoutSeconds}s without a complete message.",
                connectionId,
                _options.StaleAfterSeconds);
        }
        catch (InvalidDataException exception)
        {
            logger.LogWarning(exception, "ShineLab TCP connection {ConnectionId} closed after protocol violations.", connectionId);
        }
        catch (IOException exception)
        {
            logger.LogDebug(exception, "ShineLab TCP connection {ConnectionId} ended with an IO error.", connectionId);
        }
        catch (SocketException exception)
        {
            logger.LogDebug(exception, "ShineLab TCP connection {ConnectionId} ended with a socket error.", connectionId);
        }
        catch (ObjectDisposedException exception)
        {
            logger.LogDebug(exception, "ShineLab TCP connection {ConnectionId} was replaced or disposed.", connectionId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            connectionManager.Unregister(connectionId);
            statusHub.MarkDisconnected(equipmentCode, connectionId);
            client.Dispose();
        }
    }

    private void ReplaceActiveClient(TcpClient client)
    {
        lock (_activeClientSync)
        {
            _activeClient?.Dispose();
            _activeClient = client;
        }
    }

    private static async Task SendJsonAsync(Stream stream, object message, CancellationToken cancellationToken)
    {
        var frame = ShineLabTcpFrameCodec.Encode(message);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static bool TryParse(string json, out ShineLabMessage message, out string error)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var strId = ReadRequiredString(root, "strID");
            var strMethod = ReadRequiredString(root, "strMethod");
            var equipmentCode = ReadRequiredString(root, "equipmentCode");
            var body = root.TryGetProperty("body", out var bodyElement)
                ? bodyElement.Clone()
                : JsonSerializer.SerializeToElement(new { });
            message = new ShineLabMessage(strId, strMethod, equipmentCode, body);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            message = default;
            error = exception.Message;
            return false;
        }
    }

    private static string ReadRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new FormatException($"Missing string property '{name}'.");
        return value.GetString() ?? throw new FormatException($"Property '{name}' is empty.");
    }

    private static string FrameSummary(ReadOnlySpan<byte> frame)
    {
        var hash = Convert.ToHexString(SHA256.HashData(frame));
        var prefixLength = Math.Min(frame.Length, 12);
        var prefix = Convert.ToHexString(frame[..prefixLength]);
        return $"length={frame.Length}, sha256={hash[..16]}, prefix={prefix}";
    }

    private static IPAddress ResolveAddress(string value) =>
        string.IsNullOrWhiteSpace(value) || value is "*" or "0.0.0.0"
            ? IPAddress.Any
            : IPAddress.TryParse(value, out var address)
                ? address
                : throw new InvalidOperationException($"Invalid ShineLabTcp:ListenAddress '{value}'.");

    private static bool IsTaskEventMethod(string method) => method is
        "Device" or "UpdateInfo" or "AlarmInfo" or "SampleFinish" or "TaskFinish" or "TaskError" or "Result" or "EndMission";

    private readonly record struct ShineLabMessage(
        string StrId,
        string StrMethod,
        string EquipmentCode,
        JsonElement Body);
}
