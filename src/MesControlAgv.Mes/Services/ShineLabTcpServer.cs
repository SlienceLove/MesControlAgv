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
/// process. The deployed YhLoop client uses LF-delimited JSON; a bounded
/// 55AA reader remains available for compatible clients. This service never
/// opens a laboratory serial port.
/// </summary>
public sealed class ShineLabTcpServer(
    IOptions<ShineLabTcpOptions> configuredOptions,
    ShineLabStatusHub statusHub,
    ShineLabConnectionManager connectionManager,
    ILogger<ShineLabTcpServer> logger,
    IServiceScopeFactory? scopeFactory = null) : BackgroundService
{
    private const int InvalidFrameLimit = 8;
    private const int UnidentifiedSampleLimit = 5;
    private const int UnidentifiedLogInterval = 200;
    private readonly ShineLabTcpOptions _options = configuredOptions.Value;
    private readonly ConcurrentDictionary<Task, byte> _clients = new();
    private TcpListener? _listener;

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
        logger.LogInformation(
            "ShineLab TCP server listening on {Address}:{Port} with automatic LF-JSON/55AA framing detection.",
            address,
            _options.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                client.NoDelay = true;
                var connectionId = Guid.NewGuid().ToString("N");
                logger.LogInformation(
                    "Accepted ShineLab TCP client {RemoteEndPoint} as connection {ConnectionId}.",
                    client.Client.RemoteEndPoint,
                    connectionId);
                var task = HandleClientAsync(client, connectionId, stoppingToken);
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

    private async Task HandleClientAsync(
        TcpClient client,
        string connectionId,
        CancellationToken stoppingToken)
    {
        string? equipmentCode = null;
        using var clientTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        clientTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.StaleAfterSeconds)));
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1024, _options.ReadBufferBytes));
        var wireReader = new ShineLabWireReader();
        var readCount = 0;
        long receivedBytes = 0;

        try
        {
            await using var stream = client.GetStream();
            var invalidFrameCount = 0;
            var unidentifiedFrameCount = 0;

            if (_options.SendCertificationOnConnect)
            {
                logger.LogWarning(
                    "Suppressed diagnostic Certification probe on {ConnectionId}; wire format is unknown until the client sends data.",
                    connectionId);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), clientTimeout.Token);
                if (read == 0)
                {
                    logger.LogInformation(
                        "ShineLab peer closed connection {ConnectionId} after {ReadCount} reads/{ReceivedBytes} bytes; detected format {WireFormat}.",
                        connectionId,
                        readCount,
                        receivedBytes,
                        wireReader.Format);
                    break;
                }

                readCount++;
                receivedBytes += read;
                if (readCount == 1)
                {
                    logger.LogInformation(
                        "Received first ShineLab TCP payload on connection {ConnectionId}; {Summary}",
                        connectionId,
                        FrameSummary(buffer.AsSpan(0, read)));
                }
                else
                {
                    logger.LogDebug(
                        "Received ShineLab TCP payload #{ReadCount} on connection {ConnectionId}; {Summary}",
                        readCount,
                        connectionId,
                        FrameSummary(buffer.AsSpan(0, read)));
                }

                wireReader.Append(buffer.AsSpan(0, read));
                while (true)
                {
                    var frameStatus = wireReader.TryRead(out var wireFrame, out var frameError);
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

                    if (!ShineLabWireCodec.TryDecodeJson(wireFrame, out var json, out var decodeError))
                    {
                        invalidFrameCount++;
                        logger.LogWarning(
                            "Ignored undecodable ShineLab {WireFormat} frame on connection {ConnectionId}: {Error}; {Summary}",
                            wireFrame.Format,
                            connectionId,
                            decodeError,
                            FrameSummary(wireFrame.RawBytes));
                        if (invalidFrameCount >= InvalidFrameLimit)
                            throw new InvalidDataException("Too many undecodable ShineLab frames on one connection.");
                        continue;
                    }

                    if (!TryParse(json, out var message, out var parseError))
                    {
                        invalidFrameCount++;
                        logger.LogWarning(
                            "Ignored malformed ShineLab {WireFormat} JSON on connection {ConnectionId}: {Error}; {Summary}",
                            wireFrame.Format,
                            connectionId,
                            parseError,
                            FrameSummary(wireFrame.RawBytes));
                        if (invalidFrameCount >= InvalidFrameLimit)
                            throw new InvalidDataException("Too many malformed ShineLab messages on one connection.");
                        continue;
                    }

                    invalidFrameCount = 0;
                    clientTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.StaleAfterSeconds)));
                    logger.LogDebug(
                        "Received ShineLab {WireFormat} frame {Method}/{StrId} from {EquipmentCode}; {Summary}",
                        wireFrame.Format,
                        message.StrMethod,
                        message.StrId,
                        message.EquipmentCode,
                        FrameSummary(wireFrame.RawBytes));

                    var isCertification = message.StrMethod.Equals(
                        "Certification",
                        StringComparison.OrdinalIgnoreCase);
                    if (string.IsNullOrWhiteSpace(message.EquipmentCode))
                    {
                        if (isCertification)
                        {
                            await ReplyToCertificationAsync(
                                stream,
                                message,
                                wireFrame.Format,
                                clientTimeout.Token);
                            logger.LogInformation(
                                "Replied to provisional ShineLab Certification {StrId} with an empty equipment code on connection {ConnectionId} using {WireFormat} framing.",
                                message.StrId,
                                connectionId,
                                wireFrame.Format);
                            continue;
                        }

                        // The deployed YhLoop client sends every frame — including
                        // UpdateInfo — with an empty equipmentCode, so this is the
                        // normal field path, not a protocol violation. Dropping the
                        // connection here only produced a reconnect loop. Keep the
                        // peer attached and capture the payload verbatim: the first
                        // few frames are the evidence needed to decide how the
                        // device should be identified.
                        unidentifiedFrameCount++;
                        if (unidentifiedFrameCount <= UnidentifiedSampleLimit)
                        {
                            logger.LogInformation(
                                "ShineLab {Method}/{StrId} arrived without an equipment code on connection {ConnectionId}; sample {Sample}/{SampleLimit} payload: {Json}",
                                message.StrMethod,
                                message.StrId,
                                connectionId,
                                unidentifiedFrameCount,
                                UnidentifiedSampleLimit,
                                json);
                        }
                        else if (unidentifiedFrameCount % UnidentifiedLogInterval == 0)
                        {
                            logger.LogWarning(
                                "Ignored {UnidentifiedFrameCount} ShineLab frames without an equipment code on connection {ConnectionId}; latest {Method}/{StrId}.",
                                unidentifiedFrameCount,
                                connectionId,
                                message.StrMethod,
                                message.StrId);
                        }

                        continue;
                    }

                    unidentifiedFrameCount = 0;
                    equipmentCode = message.EquipmentCode.Trim();
                    connectionManager.Register(equipmentCode, connectionId, wireFrame.Format, stream);
                    var isCommandResponse = connectionManager.TryCompleteResponse(
                        message.StrId,
                        message.StrMethod,
                        equipmentCode,
                        message.Body);
                    statusHub.Apply(message.StrId, message.StrMethod, equipmentCode, message.Body, connectionId);
                    if (scopeFactory is not null && IsTaskEventMethod(message.StrMethod))
                    {
                        using var scope = scopeFactory.CreateScope();
                        var tasks = scope.ServiceProvider.GetRequiredService<ShineLabTaskService>();
                        await tasks.ApplyPushAsync(
                            message.StrMethod,
                            equipmentCode,
                            message.Body,
                            stoppingToken);
                    }

                    if (isCertification)
                    {
                        await ReplyToCertificationAsync(
                            stream,
                            message,
                            wireFrame.Format,
                            clientTimeout.Token);
                        logger.LogInformation(
                            "Replied to ShineLab Certification {StrId} for {EquipmentCode} using {WireFormat} framing.",
                            message.StrId,
                            equipmentCode,
                            wireFrame.Format);
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
                "ShineLab client connection {ConnectionId} timed out after {TimeoutSeconds}s without a complete message; observed {ReadCount} reads/{ReceivedBytes} bytes and format {WireFormat}.",
                connectionId,
                _options.StaleAfterSeconds,
                readCount,
                receivedBytes,
                wireReader.Format);
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
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Unexpected ShineLab TCP failure on connection {ConnectionId} after {ReadCount} reads/{ReceivedBytes} bytes; detected format {WireFormat}.",
                connectionId,
                readCount,
                receivedBytes,
                wireReader.Format);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            connectionManager.Unregister(connectionId);
            statusHub.MarkDisconnected(equipmentCode, connectionId);
            client.Dispose();
        }
    }

    private static async Task SendJsonAsync(
        Stream stream,
        object message,
        ShineLabWireFormat wireFormat,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        var frame = ShineLabWireCodec.EncodeJson(json, wireFormat);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static Task ReplyToCertificationAsync(
        Stream stream,
        ShineLabMessage message,
        ShineLabWireFormat wireFormat,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            stream,
            new
            {
                strID = message.StrId,
                strMethod = message.StrMethod,
                equipmentCode = message.EquipmentCode,
                body = new { result = "Success", msg = "" }
            },
            wireFormat,
            cancellationToken);

    private static bool TryParse(string json, out ShineLabMessage message, out string error)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var strId = ReadRequiredString(root, "strID");
            var strMethod = ReadRequiredString(root, "strMethod");
            var equipmentCode = ReadRequiredString(root, "equipmentCode");
            var strCode = root.TryGetProperty("strCode", out var strCodeElement) &&
                          strCodeElement.ValueKind == JsonValueKind.String
                ? strCodeElement.GetString()
                : null;
            var body = root.TryGetProperty("body", out var bodyElement)
                ? bodyElement.Clone()
                : JsonSerializer.SerializeToElement(new { });
            message = new ShineLabMessage(strId, strMethod, equipmentCode, strCode, body);
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
        string? StrCode,
        JsonElement Body);
}
