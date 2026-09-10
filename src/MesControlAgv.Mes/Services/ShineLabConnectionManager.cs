using System.Collections.Generic;
using System.Net.Sockets;
using System.Text.Json;
using MesControlAgv.Contracts;

namespace MesControlAgv.Mes.Services;

public sealed class ShineLabNotConnectedException(string equipmentCode)
    : InvalidOperationException($"ShineLab equipment '{equipmentCode}' is not connected.");

public sealed record ShineLabCommandResult(
    string StrId,
    string StrMethod,
    string EquipmentCode,
    string Result,
    string? Message,
    JsonElement Body)
{
    public bool IsSuccess => Result.Equals("Success", StringComparison.OrdinalIgnoreCase);
}

public sealed record ShineLabConnectionSnapshot(
    bool Connected,
    string? EquipmentCode,
    DateTimeOffset? ConnectedAtUtc,
    string? ConnectionId,
    ShineLabWireFormat WireFormat = ShineLabWireFormat.Unknown);

/// <summary>
/// Owns the live ShineLab TCP connections and correlates command responses by
/// strID.  Connections are keyed by equipmentCode so that a second device — or
/// an unrelated auxiliary connection from the same control PC — never evicts an
/// already identified one.  The server remains the only writer; WPF/MES callers
/// never touch the underlying socket or serial transport.
/// </summary>
public sealed class ShineLabConnectionManager : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PendingCommand> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public void Register(
        string equipmentCode,
        string connectionId,
        Stream stream)
        // Preserve the pre-wire-detection API contract: callers that manually
        // register a stream historically used the native 55AA framing. The
        // TCP server uses the overload below and supplies the detected format.
        => Register(equipmentCode, connectionId, ShineLabWireFormat.Native55Aa, stream);

    public void Register(
        string equipmentCode,
        string connectionId,
        ShineLabWireFormat wireFormat,
        Stream stream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(equipmentCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(stream);

        Connection? replaced = null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _connections.TryGetValue(equipmentCode, out var previous);
            if (previous is not null && previous.ConnectionId == connectionId)
            {
                _connections[equipmentCode] = previous with
                {
                    EquipmentCode = equipmentCode,
                    WireFormat = wireFormat == ShineLabWireFormat.Unknown ? previous.WireFormat : wireFormat,
                    Stream = stream
                };
            }
            else
            {
                // Only the entry for this equipmentCode is replaced. A reconnect
                // from one device must not disturb any other device's socket or
                // its in-flight commands.
                replaced = previous;
                _connections[equipmentCode] = new Connection(
                    equipmentCode,
                    connectionId,
                    DateTimeOffset.UtcNow,
                    wireFormat,
                    stream,
                    new SemaphoreSlim(1, 1));
            }
        }

        if (replaced is not null)
        {
            CompletePendingForConnection(replaced.ConnectionId, new IOException("ShineLab connection was replaced."));
            replaced.WriteGate.Dispose();
        }
    }

    public void Unregister(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return;
        Connection? removed = null;
        lock (_sync)
        {
            foreach (var entry in _connections)
            {
                if (entry.Value.ConnectionId != connectionId) continue;
                removed = entry.Value;
                break;
            }

            if (removed is not null) _connections.Remove(removed.EquipmentCode);
        }

        if (removed is not null)
        {
            CompletePendingForConnection(connectionId, new IOException("ShineLab connection closed."));
            removed.WriteGate.Dispose();
        }
    }

    /// <summary>
    /// Returns the most recently established connection, or a disconnected
    /// snapshot when none is registered.  Prefer <see cref="GetSnapshot(string)"/>
    /// when a specific device is meant; this overload exists for callers that
    /// predate multi-device support.
    /// </summary>
    public ShineLabConnectionSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            Connection? newest = null;
            foreach (var connection in _connections.Values)
            {
                if (newest is null || connection.ConnectedAtUtc >= newest.ConnectedAtUtc)
                    newest = connection;
            }

            return Describe(newest);
        }
    }

    public ShineLabConnectionSnapshot GetSnapshot(string equipmentCode)
    {
        if (string.IsNullOrWhiteSpace(equipmentCode)) return Describe(null);
        lock (_sync)
        {
            return Describe(_connections.GetValueOrDefault(equipmentCode));
        }
    }

    public IReadOnlyList<ShineLabConnectionSnapshot> GetSnapshots()
    {
        lock (_sync)
        {
            return _connections.Values.Select(Describe).ToList();
        }
    }

    private static ShineLabConnectionSnapshot Describe(Connection? connection) =>
        connection is null
            ? new(false, null, null, null, ShineLabWireFormat.Unknown)
            : new(
                true,
                connection.EquipmentCode,
                connection.ConnectedAtUtc,
                connection.ConnectionId,
                connection.WireFormat);

    public bool TryCompleteResponse(
        string strId,
        string strMethod,
        string equipmentCode,
        JsonElement body)
    {
        if (string.IsNullOrWhiteSpace(strId) || body.ValueKind != JsonValueKind.Object)
            return false;
        if (!body.TryGetProperty("result", out var resultElement) ||
            resultElement.ValueKind != JsonValueKind.String)
            return false;

        var result = resultElement.GetString() ?? string.Empty;
        var message = body.TryGetProperty("msg", out var messageElement) &&
                      messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString()
            : null;
        TaskCompletionSource<ShineLabCommandResult>? completion;
        lock (_sync)
        {
            if (!_pending.Remove(strId, out var pending)) return false;
            completion = pending.Completion;
        }

        completion.TrySetResult(new(strId, strMethod, equipmentCode, result, message, body));
        return true;
    }

    public async Task<ShineLabCommandResult> SendAsync(
        string equipmentCode,
        string method,
        JsonElement body,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(equipmentCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        Connection connection;
        var strId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<ShineLabCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            connection = _connections.GetValueOrDefault(equipmentCode)
                ?? throw new ShineLabNotConnectedException(equipmentCode);
            if (connection.WireFormat == ShineLabWireFormat.Unknown)
                throw new InvalidOperationException("ShineLab wire format has not been identified yet.");
            _pending[strId] = new PendingCommand(completion, connection.ConnectionId);
        }

        try
        {
            await connection.WriteGate.WaitAsync(cancellationToken);
            try
            {
                var message = new
                {
                    strID = strId,
                    strMethod = method,
                    equipmentCode = connection.EquipmentCode,
                    body
                };
                var frame = ShineLabWireCodec.EncodeJson(
                    JsonSerializer.Serialize(message),
                    connection.WireFormat);
                await connection.Stream.WriteAsync(frame, cancellationToken);
                await connection.Stream.FlushAsync(cancellationToken);
            }
            finally
            {
                connection.WriteGate.Release();
            }

            return await completion.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            lock (_sync) _pending.Remove(strId);
            throw new IOException("ShineLab command outcome is unknown because the connection failed.", exception);
        }
        catch
        {
            lock (_sync) _pending.Remove(strId);
            throw;
        }
    }

    private void CompletePendingForConnection(string connectionId, Exception exception)
    {
        List<TaskCompletionSource<ShineLabCommandResult>> completions = [];
        lock (_sync)
        {
            // Only this connection's commands are failed. Commands in flight on
            // another device's connection stay pending.
            foreach (var strId in _pending
                         .Where(entry => entry.Value.ConnectionId == connectionId)
                         .Select(entry => entry.Key)
                         .ToList())
            {
                completions.Add(_pending[strId].Completion);
                _pending.Remove(strId);
            }
        }
        foreach (var completion in completions) completion.TrySetException(exception);
    }

    public void Dispose()
    {
        List<Connection> connections;
        List<TaskCompletionSource<ShineLabCommandResult>> completions;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            connections = _connections.Values.ToList();
            _connections.Clear();
            completions = _pending.Values.Select(pending => pending.Completion).ToList();
            _pending.Clear();
        }

        foreach (var completion in completions)
            completion.TrySetException(new ObjectDisposedException(nameof(ShineLabConnectionManager)));
        foreach (var connection in connections) connection.WriteGate.Dispose();
    }

    private sealed record PendingCommand(
        TaskCompletionSource<ShineLabCommandResult> Completion,
        string ConnectionId);

    private sealed record Connection(
        string EquipmentCode,
        string ConnectionId,
        DateTimeOffset ConnectedAtUtc,
        ShineLabWireFormat WireFormat,
        Stream Stream,
        SemaphoreSlim WriteGate);
}
