namespace MesControlAgv.InstrumentGateway;

/// <summary>
/// A deliberately separate transport boundary for controlled field validation.
/// No production serial implementation is registered by the gateway host.
/// </summary>
public interface ICicD160PlusControlledWriteTransport : IReadOnlyModbusTransport
{
    Task<byte[]> ExchangeWriteFrameOnceAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken);
}

public sealed class CicD160PlusWriteOutcomeUnknownException : Exception
{
    public CicD160PlusWriteOutcomeUnknownException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed record CicD160PlusControlledWriteResult(
    CicD160PlusWriteOperation Operation,
    ushort Register,
    ushort RawValue,
    CicD160PlusSafetyState Preflight,
    CicD160PlusSafetyState Readback,
    string WriteRequestHex,
    string WriteResponseHex,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<ModbusReadEvidence> ReadEvidence);

/// <summary>
/// Executes one exact current-value rewrite after a fail-closed raw-state
/// preflight. It intentionally has no DI registration or HTTP endpoint.
/// </summary>
public sealed class CicD160PlusControlledWriteSession
{
    private readonly ICicD160PlusControlledWriteTransport _transport;
    private readonly CicD160PlusOfflineWritePolicy _policy;
    private readonly string _expectedObservedIdentifier;
    private readonly byte _slaveAddress;

    public CicD160PlusControlledWriteSession(
        ICicD160PlusControlledWriteTransport transport,
        string expectedObservedIdentifier,
        CicD160PlusOfflineWritePolicy? policy = null,
        byte slaveAddress = 1)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (string.IsNullOrWhiteSpace(expectedObservedIdentifier))
            throw new ArgumentException("An exact observed identifier is required.", nameof(expectedObservedIdentifier));
        if (slaveAddress == 0) throw new ArgumentOutOfRangeException(nameof(slaveAddress));

        _transport = transport;
        _expectedObservedIdentifier = expectedObservedIdentifier;
        _policy = policy ?? new CicD160PlusOfflineWritePolicy();
        _slaveAddress = slaveAddress;
    }

    public async Task<CicD160PlusControlledWriteResult> RewriteCurrentValueOnceAsync(
        CicD160PlusWriteCommand command,
        CancellationToken cancellationToken)
    {
        var frame = CicD160PlusOfflineWriteCodec.BuildFrame(command, _policy, _slaveAddress);
        EnsureCurrentValueRewriteOperation(frame.Operation);

        var evidence = new List<ModbusReadEvidence>();
        var preflight = await ReadSafetyStateAsync("Preflight", evidence, cancellationToken);
        EnsureSafeCurrentValue(preflight, frame, "preflight");

        byte[] response;
        try
        {
            response = await _transport.ExchangeWriteFrameOnceAsync(frame.Bytes, cancellationToken);
        }
        catch (CicD160PlusWriteOutcomeUnknownException)
        {
            throw;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or OperationCanceledException)
        {
            throw new CicD160PlusWriteOutcomeUnknownException(
                "The D160+ write outcome is unknown. Do not retry automatically; read the device state before any further action.",
                exception);
        }

        CicD160PlusOfflineWriteCodec.ValidateEcho(response, frame);

        CicD160PlusSafetyState readback;
        try
        {
            readback = await ReadSafetyStateAsync("Readback", evidence, cancellationToken);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or FormatException or OperationCanceledException)
        {
            throw new CicD160PlusWriteOutcomeUnknownException(
                "The D160+ write echo was accepted, but the safety readback is unavailable. Do not retry automatically; reconcile the device manually.",
                exception);
        }
        EnsureSafeCurrentValue(readback, frame, "readback");

        return new CicD160PlusControlledWriteResult(
            frame.Operation,
            frame.Register,
            frame.RawValue,
            preflight,
            readback,
            Convert.ToHexString(frame.Bytes.Span),
            Convert.ToHexString(response),
            DateTimeOffset.UtcNow,
            evidence);
    }

    private async Task<CicD160PlusSafetyState> ReadSafetyStateAsync(
        string phase,
        ICollection<ModbusReadEvidence> evidence,
        CancellationToken cancellationToken)
    {
        var identity = await _transport.ReadInputRegistersAsync(
            CicD160PlusProtocolMap.IdentityStart,
            CicD160PlusProtocolMap.IdentityCount,
            cancellationToken);
        var process = await _transport.ReadInputRegistersAsync(
            CicD160PlusProtocolMap.ProcessStart,
            CicD160PlusProtocolMap.ProcessCount,
            cancellationToken);
        var suppressor = await _transport.ReadInputRegistersAsync(
            CicD160PlusProtocolMap.SuppressorStart,
            CicD160PlusProtocolMap.SuppressorCount,
            cancellationToken);

        evidence.Add(ToEvidence($"{phase}Identity", identity));
        evidence.Add(ToEvidence($"{phase}Process", process));
        evidence.Add(ToEvidence($"{phase}Suppressor", suppressor));

        return new CicD160PlusSafetyState(
            CicD160PlusProtocolMap.DecodeObservedIdentifier(identity.Data),
            CicD160PlusProtocolMap.DecodeProcess(process.Data),
            CicD160PlusProtocolMap.DecodeSuppressor(suppressor.Data));
    }

    private void EnsureSafeCurrentValue(
        CicD160PlusSafetyState state,
        CicD160PlusWriteFrame frame,
        string phase)
    {
        if (!string.Equals(state.ObservedIdentifier, _expectedObservedIdentifier, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected D160+ identifier '{_expectedObservedIdentifier}', observed '{state.ObservedIdentifier}'.");
        }

        var blockers = new List<string>();
        AddNonzeroBlocker(blockers, "temperature-control state", state.Process.TemperatureControlStateRaw);
        AddNonzeroBlocker(blockers, "pump state", state.Process.PumpStateRaw);
        AddNonzeroBlocker(blockers, "pressure", state.Process.PressureRaw);
        AddNonzeroBlocker(blockers, "suppressor/eluent state", state.Suppressor.SuppressorEluentStateRaw);
        AddNonzeroBlocker(blockers, "fault code 1", state.Suppressor.FaultCode1Raw);
        AddNonzeroBlocker(blockers, "fault code 2", state.Suppressor.FaultCode2Raw);
        if (blockers.Count > 0)
            throw new InvalidOperationException($"D160+ current-value rewrite {phase} failed: {string.Join(", ", blockers)}.");

        var currentRawValue = frame.Operation switch
        {
            CicD160PlusWriteOperation.SetPumpFlow => state.Process.FlowSetpointRaw,
            CicD160PlusWriteOperation.SetColumnTemperature => state.Process.ColumnTemperatureSetpointRaw,
            _ => throw new InvalidOperationException($"D160+ operation {frame.Operation} is not a current-value rewrite.")
        };
        if (currentRawValue != frame.RawValue)
        {
            throw new InvalidOperationException(
                $"D160+ {frame.Operation} is not a no-op: current raw value {currentRawValue}, requested {frame.RawValue}.");
        }
    }

    private static void EnsureCurrentValueRewriteOperation(CicD160PlusWriteOperation operation)
    {
        if (operation is not (CicD160PlusWriteOperation.SetPumpFlow or CicD160PlusWriteOperation.SetColumnTemperature))
        {
            throw new InvalidOperationException(
                $"D160+ operation {operation} is blocked; this session permits current-value setpoint rewrites only.");
        }
    }

    private static void AddNonzeroBlocker(ICollection<string> blockers, string name, ushort value)
    {
        if (value != 0) blockers.Add($"{name} raw={value}");
    }

    private static ModbusReadEvidence ToEvidence(string name, ModbusReadResult read) => new(
        name,
        read.StartAddress,
        read.RegisterCount,
        Convert.ToHexString(read.Request),
        Convert.ToHexString(read.Response),
        read.ElapsedMs);
}
