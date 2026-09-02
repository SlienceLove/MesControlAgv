using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesControlAgv.Adapter.Modules.AuboArm;

public sealed class AuboArmProtocolException(string message) : InvalidOperationException(message);

public sealed class AuboArmRpcException(string method, int code, string? detail)
    : InvalidOperationException($"AUBO method '{method}' failed with code {code}: {detail ?? "unknown error"}")
{
    public string Method { get; } = method;
    public int Code { get; } = code;
    public string? Detail { get; } = detail;
}

/// <summary>
/// Read-only JSON-RPC transport toward the AUBO controller. Only methods the caller
/// passes are sent, and this type has no method that mutates controller state, so a
/// write cannot be issued through it even by mistake.
/// </summary>
public interface IAuboArmReadOnlyRpcTransport
{
    Task<JsonElement> InvokeAsync(
        string method,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken);
}

/// <summary>
/// Legacy newline-delimited TCP client retained only for offline compatibility tests.
/// The live AUBO module registers <see cref="AuboArmWebSocketClient"/> instead;
/// the field controller's verified endpoint is WebSocket 9012, not raw TCP 30004.
/// </summary>
public sealed class AuboArmJsonRpcClient : IAuboArmControlledRpcTransport, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Produces the documented envelope member names: jsonrpc / method / params / id.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly AuboArmOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private int _requestId;

    public AuboArmJsonRpcClient(AuboArmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async Task<JsonElement> InvokeAsync(
        string method,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(parameters);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeoutMs);
            try
            {
                await EnsureConnectedAsync(timeout.Token);
                // The controller logs use string request ids (for example
                // "ysv9yDry-d2OR-DKWo"). JSON-RPC permits either number or string,
                // but matching the observed shape also lets us compare responses from
                // the real AUBO endpoint without coercion.
                var id = $"mes-{Interlocked.Increment(ref _requestId)}-{Guid.NewGuid():N}";
                var rpcParams = AuboRpcParameterMapper.ToVendorObject(method, parameters);
                var payload = JsonSerializer.Serialize(
                    new AuboRpcRequest("2.0", method, rpcParams, id),
                    JsonOptions);
                var bytes = Encoding.UTF8.GetBytes(payload + "\n");
                await _stream!.WriteAsync(bytes, timeout.Token);
                await _stream.FlushAsync(timeout.Token);

                var line = await _reader!.ReadLineAsync(timeout.Token)
                    ?? throw new AuboArmProtocolException("The AUBO controller closed the connection.");
                return ReadResult(method, line, id);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                ResetConnection();
                throw new TimeoutException($"AUBO method '{method}' timed out on {_options.Host}:{_options.Port}.");
            }
            catch (OperationCanceledException)
            {
                ResetConnection();
                throw;
            }
            catch (Exception exception) when (exception is IOException or SocketException or JsonException)
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

    private static JsonElement ReadResult(string method, string line, string expectedId)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AuboArmProtocolException($"AUBO method '{method}' returned a non-object response.");
        }

        if (root.TryGetProperty("id", out var idElement))
        {
            var actualId = idElement.ValueKind switch
            {
                JsonValueKind.String => idElement.GetString(),
                JsonValueKind.Number => idElement.ToString(),
                _ => idElement.ToString()
            };
            if (!string.Equals(actualId, expectedId, StringComparison.Ordinal))
            {
                throw new AuboArmProtocolException(
                    $"AUBO method '{method}' returned response id {actualId}, expected {expectedId}.");
            }
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            var code = error.TryGetProperty("code", out var codeElement)
                && codeElement.ValueKind == JsonValueKind.Number
                && codeElement.TryGetInt32(out var parsedCode)
                    ? parsedCode
                    : -1;
            var detail = error.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()
                    : error.ToString();
            throw new AuboArmRpcException(method, code, detail);
        }

        if (!root.TryGetProperty("result", out var result))
        {
            throw new AuboArmProtocolException($"AUBO method '{method}' returned no result member.");
        }

        // Most ARCS methods return an int where 0 is success and a negative value is an
        // error code. A negative int here is a vendor failure, not a value to normalize.
        if (result.ValueKind == JsonValueKind.Number
            && result.TryGetInt32(out var resultCode)
            && resultCode < 0
            && IsCommandStyleMethod(method))
        {
            throw new AuboArmRpcException(method, resultCode, "negative ARCS return code");
        }

        return result.Clone();
    }

    private static bool IsCommandStyleMethod(string method) =>
        method.Contains(".set", StringComparison.Ordinal)
        || method.Contains(".clear", StringComparison.Ordinal)
        || method.Contains(".load", StringComparison.Ordinal)
        || method.Contains(".run", StringComparison.Ordinal);

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null) return;
        if (string.IsNullOrWhiteSpace(_options.Host) || _options.Port is < 1 or > 65535)
        {
            throw new AuboArmProtocolException(
                $"{AuboArmOptions.SectionName} has no site-confirmed Host/Port; refusing to open a connection.");
        }

        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ConnectTimeoutMs);
            await client.ConnectAsync(_options.Host, _options.Port, timeout.Token);
            _client = client;
            _stream = client.GetStream();
            _reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void ResetConnection()
    {
        _reader?.Dispose();
        _stream?.Dispose();
        _client?.Dispose();
        _reader = null;
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        ResetConnection();
        _gate.Dispose();
    }

    private sealed record AuboRpcRequest(string Jsonrpc, string Method, object? Params, string Id);
}

/// <summary>
/// Converts the positional values used by the application boundary to the named
/// parameter objects emitted by the AUBO ARCS JSON-RPC client. The field report's
/// controller log is authoritative here: RegisterControl calls use key/value (and
/// default_value), while motion/runtime calls use their documented field names.
/// Unknown methods retain an array so adding a vendor method does not silently
/// reorder or drop arguments.
/// </summary>
internal static class AuboRpcParameterMapper
{
    public static object? ToVendorObject(string method, IReadOnlyList<object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Count == 0) return null;

        var bare = method;
        var dot = bare.LastIndexOf('.');
        if (dot >= 0) bare = bare[(dot + 1)..];

        string[]? names = bare switch
        {
            "hasNamedVariable" or "getNamedVariableType" => ["key"],
            "getInt32" or "getBool" or "getDouble" or "getString" => ["key", "default_value"],
            "setInt32" or "setBool" or "setDouble" or "setString" => ["key", "value"],
            "setWatchDog" => ["key", "timeout", "action"],
            "getPreloadProgram" => ["index"],
            "preloadProgram" => ["index", "program"],
            "loadProgram" => ["program"],
            "setOperationalMode" => ["mode"],
            "setLinkModeEnable" => ["enable"],
            "freedrive" => ["enable"],
            "moveJoint" => ["q", "a", "v", "blend_radius", "duration"],
            "moveLine" => ["pose", "a", "v", "blend_radius", "duration"],
            "speedLine" => ["xd", "a", "t"],
            "stopJoint" => ["acc"],
            "stopLine" => ["acc", "acc_rot"],
            _ => null
        };

        if (names is null || names.Length != parameters.Count) return parameters;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < names.Length; index++) values[names[index]] = parameters[index];
        return values;
    }
}
