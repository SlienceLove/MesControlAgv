using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Modules.AuboArm;

/// <summary>
/// Write-capable JSON-RPC transport toward the AUBO controller. Kept separate from
/// <see cref="IAuboArmReadOnlyRpcTransport"/> so a named-variable write cannot be
/// issued through the read-only path even by accident.
/// </summary>
public interface IAuboArmControlledRpcTransport : IAuboArmReadOnlyRpcTransport;

/// <summary>
/// Executes one MES-to-Lua variable handshake: fail-closed readiness preflight, watchdog
/// arm, command + sequence write, then a bounded poll for the Lua project's result.
/// It intentionally has no DI registration and no HTTP endpoint — construction is the
/// authorization boundary, exactly as CicD160PlusControlledWriteSession is.
/// </summary>
public sealed class AuboArmControlledHandshakeSession : IAuboArmHandshakeWriter
{
    private const string RegisterControl = "RegisterControl";

    private readonly IAuboArmControlledRpcTransport _transport;
    private readonly AuboArmReadOnlyDriver _reader;
    private readonly AuboArmOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyList<int> _allowedCommandCodes;

    public AuboArmControlledHandshakeSession(
        IAuboArmControlledRpcTransport transport,
        AuboArmReadOnlyDriver reader,
        AuboArmOptions options,
        TimeProvider timeProvider,
        IReadOnlyList<int> allowedCommandCodes)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(allowedCommandCodes);
        if (allowedCommandCodes.Count == 0)
        {
            throw new ArgumentException(
                "At least one Lua if-block command code must be authorized before a dispatch is possible.",
                nameof(allowedCommandCodes));
        }

        _transport = transport;
        _reader = reader;
        _options = options;
        _timeProvider = timeProvider;
        _allowedCommandCodes = allowedCommandCodes.Distinct().ToArray();
    }

    public async Task<AuboArmHandshakeResultResponse> DispatchAsync(
        string deviceId,
        Guid operationId,
        int commandCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (operationId == Guid.Empty) throw new ArgumentException("An operation id is required.", nameof(operationId));
        if (!_allowedCommandCodes.Contains(commandCode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandCode),
                commandCode,
                "Command code is not an authorized Lua if-block branch.");
        }

        // Fail closed before any write: an arm that is not Running/Normal/Automatic with a
        // cleanly loaded project must never be handed a command variable.
        var readiness = await _reader.GetReadinessAsync(deviceId, cancellationToken);
        if (!readiness.Ready)
        {
            throw new InvalidOperationException(
                "AUBO arm is not ready for a handshake: " + string.Join("; ", readiness.BlockingReasons));
        }

        var pending = await _reader.GetHandshakeSnapshotAsync(deviceId, cancellationToken);
        if (pending.State is AuboArmHandshakeState.Dispatched or AuboArmHandshakeState.Running)
        {
            throw new InvalidOperationException(
                $"A handshake is still in flight (sequence {pending.Sequence}, state {pending.State}). " +
                "Reconcile it before dispatching another command.");
        }

        var sequence = NextSequence(pending.Sequence);
        return await WriteAndAwaitAsync(deviceId, operationId, commandCode, sequence, cancellationToken);
    }

    /// <summary>
    /// A monotonic sequence makes a repeated command code a distinguishable new request,
    /// which is what lets the Lua side detect an edge rather than a level.
    /// </summary>
    private static int NextSequence(int? observed) =>
        observed is null or < 1 or int.MaxValue ? 1 : observed.Value + 1;

    private async Task<AuboArmHandshakeResultResponse> WriteAndAwaitAsync(
        string deviceId,
        Guid operationId,
        int commandCode,
        int sequence,
        CancellationToken cancellationToken)
    {
        // The watchdog is armed first: if the control centre dies between the two writes,
        // the controller stops the arm on its own instead of holding a stale command.
        await InvokeAsync(
            $"{RegisterControl}.setWatchDog",
            [_options.SequenceVariableKey, _options.WatchDogTimeoutSeconds, _options.WatchDogAction],
            cancellationToken);

        // Result and detail are cleared before the command so a stale value from the
        // previous cycle can never be read back as this cycle's outcome.
        await InvokeAsync($"{RegisterControl}.setInt32", [_options.ResultVariableKey, 0], cancellationToken);
        await InvokeAsync(
            $"{RegisterControl}.setString",
            [_options.ResultDetailVariableKey, string.Empty],
            cancellationToken);
        await InvokeAsync($"{RegisterControl}.setInt32", [_options.CommandVariableKey, commandCode], cancellationToken);

        // The sequence write is the trigger, so it goes last and is the only write whose
        // failure leaves an ambiguous outcome.
        try
        {
            await InvokeAsync($"{RegisterControl}.setInt32", [_options.SequenceVariableKey, sequence], cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new AuboArmOutcomeUnknownException(
                $"The trigger write for sequence {sequence} did not confirm: {exception.Message}");
        }

        return await AwaitLuaResultAsync(deviceId, operationId, commandCode, sequence, cancellationToken);
    }

    private async Task<AuboArmHandshakeResultResponse> AwaitLuaResultAsync(
        string deviceId,
        Guid operationId,
        int commandCode,
        int sequence,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow().AddMilliseconds(_options.HandshakeTimeoutMs);
        var poll = TimeSpan.FromMilliseconds(_options.HandshakePollIntervalMs);
        var lastState = AuboArmHandshakeState.Unknown;

        while (_timeProvider.GetUtcNow() < deadline)
        {
            var snapshot = await _reader.GetHandshakeSnapshotAsync(deviceId, cancellationToken);
            lastState = snapshot.State;

            // Another writer taking the sequence means this operation no longer owns the
            // handshake; its outcome is unknowable rather than failed.
            if (snapshot.Sequence is { } observed && observed != sequence)
            {
                throw new AuboArmOutcomeUnknownException(
                    $"Sequence changed from {sequence} to {observed} while awaiting the Lua result.");
            }

            if (snapshot.State is AuboArmHandshakeState.Completed or AuboArmHandshakeState.Failed)
            {
                return new AuboArmHandshakeResultResponse(
                    operationId,
                    _options.DeviceId,
                    commandCode,
                    sequence,
                    snapshot.State,
                    snapshot.ResultCode,
                    snapshot.State == AuboArmHandshakeState.Failed
                        ? snapshot.ResultDetail ?? $"Lua published result code {snapshot.ResultCode}."
                        : null,
                    MayHaveWritten: true,
                    _timeProvider.GetUtcNow());
            }

            await Task.Delay(poll, _timeProvider, cancellationToken);
        }

        // A timeout is never reported as a failure: the arm may still be mid-motion, so
        // the caller must reconcile rather than resend.
        throw new AuboArmOutcomeUnknownException(
            $"Sequence {sequence} did not reach a terminal Lua result within " +
            $"{_options.HandshakeTimeoutMs} ms; last observed state was {lastState}.");
    }

    private async Task InvokeAsync(
        string method,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        var result = await _transport.InvokeAsync(method, parameters, cancellationToken);

        // ARCS setters return 0 on success and a negative ARCS error code otherwise.
        if (result.ValueKind == JsonValueKind.Number && result.TryGetInt32(out var code) && code < 0)
        {
            throw new AuboArmRpcException(method, code, "negative ARCS return code");
        }
    }
}
