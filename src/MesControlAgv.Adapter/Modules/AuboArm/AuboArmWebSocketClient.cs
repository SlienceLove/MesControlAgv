using System.Buffers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesControlAgv.Adapter.Modules.AuboArm;

/// <summary>
/// JSON-RPC transport for the field-verified AUBO WebSocket endpoint (9012).
/// One invocation owns the socket until its complete response frame is received;
/// requests are serialized so a response can never be attributed to the wrong
/// caller.  A failed invocation resets the connection but is never replayed.
/// </summary>
public sealed class AuboArmWebSocketClient : IAuboArmControlledRpcTransport, IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly AuboArmOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClientWebSocket? _socket;
    private int _requestId;
    private int _disposed;

    public AuboArmWebSocketClient(AuboArmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Resolved endpoint, useful for diagnostics without exposing credentials.</summary>
    public Uri Endpoint => BuildEndpoint(_options);

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task<JsonElement> InvokeAsync(
        string method,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(parameters);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeoutMs);
            try
            {
                await EnsureConnectedAsync(timeout.Token).ConfigureAwait(false);

                var id = $"mes-{Interlocked.Increment(ref _requestId)}-{Guid.NewGuid():N}";
                // The field-verified script sends an explicit empty positional
                // array for zero-argument calls; retain that shape instead of
                // relying on a vendor-specific interpretation of an omitted
                // `params` member.
                var rpcParams = parameters.Count == 0
                    ? Array.Empty<object?>()
                    : AuboRpcParameterMapper.ToVendorObject(method, parameters);
                var payload = JsonSerializer.Serialize(
                    new AuboRpcRequest("2.0", method, rpcParams, id),
                    JsonOptions);
                var bytes = Encoding.UTF8.GetBytes(payload);
                await _socket!.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    timeout.Token).ConfigureAwait(false);

                var responseText = await ReceiveMessageAsync(timeout.Token).ConfigureAwait(false);
                var parsed = ParseResponse(method, responseText, id);
                if (!_options.ReuseWebSocketConnection) ResetConnection();
                return parsed;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                ResetConnection();
                throw new TimeoutException(
                    $"AUBO WebSocket method '{method}' timed out on {Endpoint}.");
            }
            catch (OperationCanceledException)
            {
                ResetConnection();
                throw;
            }
            catch (Exception exception) when (
                exception is WebSocketException or IOException or SocketException or JsonException
                    or AuboArmProtocolException or AuboArmRpcException)
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
        ThrowIfDisposed();
        if (_socket?.State == WebSocketState.Open) return;
        ResetConnection();

        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            throw new AuboArmProtocolException(
                $"{AuboArmOptions.SectionName} has no site-confirmed WebSocket host.");
        }

        var socket = new ClientWebSocket
        {
            Options = { KeepAliveInterval = TimeSpan.FromSeconds(20) }
        };
        socket.Options.Proxy = null;
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(_options.ConnectTimeoutMs);
            await socket.ConnectAsync(Endpoint, connectTimeout.Token).ConfigureAwait(false);
            _socket = socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task<string> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
            throw new AuboArmProtocolException("The AUBO WebSocket is not open.");

        var rented = ArrayPool<byte>.Shared.Rent(Math.Min(64 * 1024, _options.MaximumMessageBytes));
        await using var memory = new MemoryStream();
        try
        {
            while (true)
            {
                var received = await _socket.ReceiveAsync(
                    new ArraySegment<byte>(rented),
                    cancellationToken).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    throw new AuboArmProtocolException(
                        "The AUBO controller closed the WebSocket before returning a response.");
                }

                if (received.MessageType != WebSocketMessageType.Text)
                {
                    throw new AuboArmProtocolException(
                        $"The AUBO controller returned an unsupported WebSocket message type '{received.MessageType}'.");
                }

                if (received.Count > 0)
                {
                    if (memory.Length + received.Count > _options.MaximumMessageBytes)
                    {
                        throw new AuboArmProtocolException(
                            $"The AUBO response exceeded the configured {_options.MaximumMessageBytes}-byte limit.");
                    }

                    memory.Write(rented, 0, received.Count);
                }

                if (received.EndOfMessage) break;
            }

            return Encoding.UTF8.GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static JsonElement ParseResponse(string method, string responseText, string expectedId)
    {
        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AuboArmProtocolException(
                $"AUBO method '{method}' returned a non-object JSON-RPC response.");
        }

        if (!root.TryGetProperty("jsonrpc", out var version)
            || version.ValueKind != JsonValueKind.String
            || !string.Equals(version.GetString(), "2.0", StringComparison.Ordinal))
        {
            var versionText = version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : version.ToString();
            throw new AuboArmProtocolException(
                $"AUBO method '{method}' returned JSON-RPC version '{versionText ?? "<missing>"}'.");
        }

        if (!root.TryGetProperty("id", out var idElement))
        {
            throw new AuboArmProtocolException(
                $"AUBO method '{method}' returned no JSON-RPC response id.");
        }

        var actualId = idElement.ValueKind switch
        {
            JsonValueKind.String => idElement.GetString(),
            JsonValueKind.Number => idElement.ToString(),
            JsonValueKind.Null => null,
            _ => idElement.ToString()
        };
        if (!string.Equals(actualId, expectedId, StringComparison.Ordinal))
        {
            throw new AuboArmProtocolException(
                $"AUBO method '{method}' returned response id {actualId ?? "<null>"}, expected {expectedId}.");
        }

        if (root.TryGetProperty("error", out var error)
            && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            var code = ReadErrorCode(error);
            var detail = ReadErrorDetail(error);
            throw new AuboArmRpcException(method, code, detail);
        }

        if (!root.TryGetProperty("result", out var result))
        {
            throw new AuboArmProtocolException(
                $"AUBO method '{method}' returned no result member.");
        }

        if (result.ValueKind == JsonValueKind.Number
            && result.TryGetInt32(out var resultCode)
            && resultCode < 0
            && IsCommandStyleMethod(method))
        {
            throw new AuboArmRpcException(method, resultCode, "negative ARCS return code");
        }

        return result.Clone();
    }

    private static int ReadErrorCode(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object) return -1;
        if (!error.TryGetProperty("code", out var code)) return -1;
        if (code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number)) return number;
        return code.ValueKind == JsonValueKind.String
            && int.TryParse(code.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : -1;
    }

    private static string? ReadErrorDetail(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object) return error.ToString();
        if (error.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String)
        {
            var text = message.GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        if (error.TryGetProperty("data", out var data)
            && data.ValueKind != JsonValueKind.Null)
        {
            return data.ToString();
        }

        return error.ToString();
    }

    private static bool IsCommandStyleMethod(string method) =>
        method.Contains(".set", StringComparison.Ordinal)
        || method.Contains(".clear", StringComparison.Ordinal)
        || method.Contains(".load", StringComparison.Ordinal)
        || method.Contains(".run", StringComparison.Ordinal)
        || method.EndsWith(".abort", StringComparison.Ordinal)
        || method.EndsWith(".stop", StringComparison.Ordinal);

    private static Uri BuildEndpoint(AuboArmOptions options)
    {
        var host = options.Host.Trim();
        if (host.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(host, UriKind.Absolute, out var configured))
        {
            var builder = new UriBuilder(configured)
            {
                Scheme = configured.Scheme is "wss" ? "wss" : "ws",
                Path = NormalizePath(options.WebSocketPath, configured.AbsolutePath)
            };
            // When the host is supplied as a complete ws:// URI, preserve its
            // explicit non-default port; a plain host uses the configured port.
            if (options.Port is > 0 and <= 65535
                && (configured.Port <= 0 || configured.Port == 80 || configured.Port == 443
                    || options.Port != 9012))
            {
                builder.Port = options.Port;
            }
            return builder.Uri;
        }

        var path = NormalizePath(options.WebSocketPath, "/");
        var uriBuilder = new UriBuilder(Uri.UriSchemeWs, host, options.Port, path);
        return uriBuilder.Uri;
    }

    private static string NormalizePath(string configuredPath, string fallback)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath) ? fallback : configuredPath.Trim();
        if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;
        return path.Length == 0 ? "/" : path;
    }

    private void ResetConnection()
    {
        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket is null) return;
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                // A failed/expired request must not wait for a close handshake.
                socket.Abort();
            }
        }
        finally
        {
            socket.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(AuboArmWebSocketClient));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        ResetConnection();
        _gate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record AuboRpcRequest(string Jsonrpc, string Method, object? Params, string Id);
}
