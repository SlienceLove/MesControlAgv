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
    string? ConnectionId);

/// <summary>
/// Owns the current ShineLab TCP connection and correlates command responses
/// by strID.  The server remains the only writer; WPF/MES callers never touch
/// the underlying socket or serial transport.
/// </summary>
public sealed class ShineLabConnectionManager : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, TaskCompletionSource<ShineLabCommandResult>> _pending = new(StringComparer.Ordinal);
    private Connection? _connection;
    private bool _disposed;

    public void Register(
        string equipmentCode,
        string connectionId,
        Stream stream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(equipmentCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(stream);

        Connection? previous;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _connection;
            _connection = previous is not null && previous.ConnectionId == connectionId
                ? previous with { EquipmentCode = equipmentCode, Stream = stream }
                : new Connection(equipmentCode, connectionId, DateTimeOffset.UtcNow, stream, new SemaphoreSlim(1, 1));
        }

        if (previous is not null && previous.ConnectionId != connectionId)
        {
            CompletePendingForConnection(previous.ConnectionId, new IOException("ShineLab connection was replaced."));
            previous.WriteGate.Dispose();
        }
    }

    public void Unregister(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return;
        Connection? removed = null;
        lock (_sync)
        {
            if (_connection?.ConnectionId == connectionId)
            {
                removed = _connection;
                _connection = null;
            }
        }

        if (removed is not null)
        {
            CompletePendingForConnection(connectionId, new IOException("ShineLab connection closed."));
            removed.WriteGate.Dispose();
        }
    }

    public ShineLabConnectionSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _connection is null
                ? new(false, null, null, null)
                : new(true, _connection.EquipmentCode, _connection.ConnectedAtUtc, _connection.ConnectionId);
        }
    }

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
            if (!_pending.Remove(strId, out completion)) return false;
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
            connection = _connection is not null &&
                        string.Equals(_connection.EquipmentCode, equipmentCode, StringComparison.OrdinalIgnoreCase)
                ? _connection
                : throw new ShineLabNotConnectedException(equipmentCode);
            _pending[strId] = completion;
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
                var frame = ShineLabTcpFrameCodec.Encode(message);
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
        List<TaskCompletionSource<ShineLabCommandResult>> completions;
        lock (_sync)
        {
            completions = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var completion in completions) completion.TrySetException(exception);
    }

    public void Dispose()
    {
        Connection? connection;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            connection = _connection;
            _connection = null;
        }

        if (connection is not null)
        {
            CompletePendingForConnection(connection.ConnectionId, new ObjectDisposedException(nameof(ShineLabConnectionManager)));
            connection.WriteGate.Dispose();
        }
    }

    private sealed record Connection(
        string EquipmentCode,
        string ConnectionId,
        DateTimeOffset ConnectedAtUtc,
        Stream Stream,
        SemaphoreSlim WriteGate);
}
