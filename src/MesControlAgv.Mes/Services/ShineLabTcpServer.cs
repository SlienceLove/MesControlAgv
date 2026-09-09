using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Receives the long-lived connection initiated by the resident ShineLab
/// process.  This server only parses JSON and updates the status hub; it never
/// opens a laboratory serial port.
/// </summary>
public sealed class ShineLabTcpServer(
    IOptions<ShineLabTcpOptions> configuredOptions,
    ShineLabStatusHub statusHub,
    ShineLabConnectionManager connectionManager,
    ILogger<ShineLabTcpServer> logger,
    IServiceScopeFactory? scopeFactory = null) : BackgroundService
{
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
        logger.LogInformation("ShineLab TCP server listening on {Address}:{Port}.", address, _options.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                client.NoDelay = true;
                ReplaceActiveClient(client);
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
            try { await Task.WhenAll(_clients.Keys); }
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
        try
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 4096, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true
            };

            if (_options.SendCertificationOnConnect)
            {
                var probe = new
                {
                    strID = $"mes-cert-probe-{Guid.NewGuid():N}",
                    strMethod = "Certification",
                    equipmentCode = _options.ServerEquipmentCode,
                    body = new { chan = "A" }
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(probe));
                logger.LogInformation(
                    "Sent diagnostic Certification probe to ShineLab client as {EquipmentCode}.",
                    _options.ServerEquipmentCode);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(clientTimeout.Token);
                if (line is null) break;
                clientTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.StaleAfterSeconds)));
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (!TryParse(line, out var message, out var error))
                {
                    logger.LogWarning("Ignored malformed ShineLab message: {Error}", error);
                    continue;
                }

                equipmentCode = message.EquipmentCode;
                connectionManager.Register(message.EquipmentCode, connectionId, writer);
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
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response));
                }
                else if (isCommandResponse)
                {
                    logger.LogDebug("Received ShineLab response {Method}/{StrId}.", message.StrMethod, message.StrId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation(
                "ShineLab client connection timed out after {TimeoutSeconds}s without a message.",
                _options.StaleAfterSeconds);
        }
        catch (IOException exception)
        {
            logger.LogDebug(exception, "ShineLab TCP connection ended with an IO error.");
        }
        catch (SocketException exception)
        {
            logger.LogDebug(exception, "ShineLab TCP connection ended with a socket error.");
        }
        catch (ObjectDisposedException exception)
        {
            logger.LogDebug(exception, "ShineLab TCP connection was replaced or disposed.");
        }
        finally
        {
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

    private static bool TryParse(string line, out ShineLabMessage message, out string error)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
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
