using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Modules.AuboArm;

/// <summary>
/// Controlled project operations over the verified WebSocket transport.  The driver
/// owns the safety checks and the single-operation gate; callers never get a raw RPC
/// method or a way to start an implicitly selected project.
/// </summary>
public sealed class AuboArmProgramDriver : IAuboArmProgramDriver, IDisposable
{
    private readonly IAuboArmReadOnlyRpcTransport _transport;
    private readonly AuboArmReadOnlyDriver _reader;
    private readonly AuboArmOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IAuboArmLoadedProgramReader? _loadedProgramReader;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public AuboArmProgramDriver(
        IAuboArmReadOnlyRpcTransport transport,
        AuboArmReadOnlyDriver reader,
        AuboArmOptions options,
        TimeProvider timeProvider,
        IAuboArmLoadedProgramReader? loadedProgramReader = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _transport = transport;
        _reader = reader;
        _options = options;
        _timeProvider = timeProvider;
        _loadedProgramReader = loadedProgramReader;
    }

    public async Task<AuboArmProgramStatusResponse> GetProgramAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        EnsureDevice(deviceId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadProgramStatusAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<AuboArmProgramCatalogResponse> GetProgramCatalogAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        EnsureDevice(deviceId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _reader.GetProgramCatalogAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task<AuboArmProgramOperationResponse> LoadProgramAsync(
        string deviceId,
        string programName,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        ExecuteSerializedAsync(
            () => LoadCoreAsync(deviceId, programName, operatorName, operationId, cancellationToken),
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> RunProgramAsync(
        string deviceId,
        string? programName,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        ExecuteSerializedAsync(
            () => RunCoreAsync(deviceId, programName, operatorName, operationId, cancellationToken),
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> StopProgramAsync(
        string deviceId,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        ExecuteSerializedAsync(
            () => StopCoreAsync(deviceId, operatorName, operationId, cancellationToken),
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> LoadProgramAsync(
        string deviceId,
        AuboArmProgramRequest request,
        CancellationToken cancellationToken) =>
        LoadProgramAsync(
            deviceId,
            request.EffectiveProgramName,
            request.EffectiveOperatorName,
            request.OperationId.GetValueOrDefault(Guid.NewGuid()),
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> RunProgramAsync(
        string deviceId,
        AuboArmProgramRunRequest request,
        CancellationToken cancellationToken) =>
        RunProgramAsync(
            deviceId,
            request.EffectiveProgramName,
            request.EffectiveOperatorName,
            request.OperationId.GetValueOrDefault(Guid.NewGuid()),
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> StopProgramAsync(
        string deviceId,
        AuboArmProgramStopRequest request,
        CancellationToken cancellationToken) =>
        StopProgramAsync(
            deviceId,
            request.EffectiveOperatorName,
            request.OperationId.GetValueOrDefault(Guid.NewGuid()),
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> LoadProgramAsync(
        string deviceId,
        string programName,
        string operatorName,
        CancellationToken cancellationToken) =>
        LoadProgramAsync(deviceId, programName, operatorName, Guid.NewGuid(), cancellationToken);

    public Task<AuboArmProgramOperationResponse> RunProgramAsync(
        string deviceId,
        string? programName,
        string operatorName,
        CancellationToken cancellationToken) =>
        RunProgramAsync(deviceId, programName, operatorName, Guid.NewGuid(), cancellationToken);

    public Task<AuboArmProgramOperationResponse> StopProgramAsync(
        string deviceId,
        string operatorName,
        CancellationToken cancellationToken) =>
        StopProgramAsync(deviceId, operatorName, Guid.NewGuid(), cancellationToken);

    private async Task<AuboArmProgramOperationResponse> ExecuteSerializedAsync(
        Func<Task<AuboArmProgramOperationResponse>> operation,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<AuboArmProgramOperationResponse> LoadCoreAsync(
        string deviceId,
        string programName,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        EnsureOperation(deviceId, operatorName, operationId);
        var normalized = NormalizeAndAuthorizeProgram(programName);
        var before = await ReadProgramStatusAsync(deviceId, cancellationToken).ConfigureAwait(false);
        EnsureSafeForMutation(before, requireAutomatic: false);

        // A load while the interpreter is running is ambiguous and can alter the
        // active project.  Refuse it before any state-changing RPC.
        if (before.RuntimeState != AuboArmRuntimeState.Stopped)
        {
            throw new InvalidOperationException(
                $"AUBO runtime is {before.RuntimeStatus ?? before.RuntimeState.ToString()}; stop it before loading a project.");
        }

        var mayHaveWritten = false;
        JsonElement result;
        try
        {
            mayHaveWritten = true;
            result = await InvokeAsync("RuntimeMachine.loadProgram", [normalized], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unknown(operationId, deviceId, normalized, "load", "loadProgram timed out", mayHaveWritten);
        }
        catch (AuboArmRpcException exception)
        {
            return await FailedResultAsync(
                operationId, deviceId, normalized, operatorName, "load", exception.Code,
                exception.Detail ?? exception.Message, mayHaveWritten, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or WebSocketException or IOException or AuboArmProtocolException)
        {
            throw Unknown(operationId, deviceId, normalized, "load", exception.Message, mayHaveWritten);
        }

        var resultCode = ReadResultCode(result);
        if (resultCode is < 0)
        {
            return await FailedResultAsync(
                operationId, deviceId, normalized, operatorName, "load", resultCode, "AUBO rejected load", mayHaveWritten,
                cancellationToken).ConfigureAwait(false);
        }

        var after = await ReadStatusAfterMutationAsync(
            operationId, deviceId, normalized, "load", mayHaveWritten, cancellationToken).ConfigureAwait(false);
        var loaded = string.Equals(after.LoadedProgram, normalized, StringComparison.Ordinal);
        return new AuboArmProgramOperationResponse(
            operationId,
            deviceId,
            normalized,
            "load",
            operatorName.Trim(),
            loaded ? AuboArmProgramOperationState.Loaded : AuboArmProgramOperationState.Unknown,
            after.RuntimeState,
            after.RuntimeStatus,
            after.LoadedProgram,
            resultCode,
            loaded ? null : "AUBO acknowledged load but the requested project was not observed as loaded.",
            mayHaveWritten,
            _timeProvider.GetUtcNow());
    }

    private async Task<AuboArmProgramOperationResponse> RunCoreAsync(
        string deviceId,
        string? programName,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        EnsureOperation(deviceId, operatorName, operationId);
        var before = await ReadProgramStatusAsync(deviceId, cancellationToken).ConfigureAwait(false);
        EnsureSafeForMutation(before, _options.RequireAutomaticModeForProgramRun);

        if (before.RuntimeState == AuboArmRuntimeState.Running)
        {
            throw new InvalidOperationException(
                "AUBO is already running; refusing to send a duplicate runProgram request.");
        }

        if (before.RuntimeState != AuboArmRuntimeState.Stopped)
        {
            throw new InvalidOperationException(
                $"AUBO runtime is {before.RuntimeStatus ?? before.RuntimeState.ToString()}; it must be Stopped before starting.");
        }

        var loaded = before.LoadedProgram;
        if (string.IsNullOrWhiteSpace(loaded))
        {
            throw new InvalidOperationException(
                "No AUBO project is loaded; load an explicitly approved project before starting.");
        }

        // A caller may omit ProgramName to request the currently loaded
        // project. That is still a controlled operation: the loaded project
        // itself must be on the explicit allowlist. Otherwise an operator (or
        // stale pendant state) could run an arbitrary project by simply
        // leaving the request field blank.
        _ = NormalizeAndAuthorizeProgram(loaded);

        if (!string.IsNullOrWhiteSpace(programName))
        {
            var requested = NormalizeAndAuthorizeProgram(programName);
            if (!string.Equals(requested, loaded, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Loaded AUBO project is '{loaded}', not the requested '{requested}'. Load it explicitly first.");
            }
        }

        var mayHaveWritten = false;
        JsonElement result;
        try
        {
            mayHaveWritten = true;
            result = await InvokeAsync("RuntimeMachine.runProgram", [], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unknown(operationId, deviceId, loaded, "run", "runProgram timed out", mayHaveWritten);
        }
        catch (AuboArmRpcException exception)
        {
            return await FailedResultAsync(
                operationId, deviceId, loaded, operatorName, "run", exception.Code,
                exception.Detail ?? exception.Message, mayHaveWritten, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or WebSocketException or IOException or AuboArmProtocolException)
        {
            throw Unknown(operationId, deviceId, loaded, "run", exception.Message, mayHaveWritten);
        }

        var resultCode = ReadResultCode(result);
        if (resultCode is < 0)
        {
            return await FailedResultAsync(
                operationId, deviceId, loaded, operatorName, "run", resultCode, "AUBO rejected run", mayHaveWritten,
                cancellationToken).ConfigureAwait(false);
        }

        var after = await ReadStatusAfterMutationAsync(
            operationId, deviceId, loaded, "run", mayHaveWritten, cancellationToken).ConfigureAwait(false);
        var state = after.RuntimeState == AuboArmRuntimeState.Running
            ? AuboArmProgramOperationState.Running
            : after.RuntimeState == AuboArmRuntimeState.Stopped
                ? AuboArmProgramOperationState.Accepted
                : AuboArmProgramOperationState.Unknown;
        return new AuboArmProgramOperationResponse(
            operationId,
            deviceId,
            loaded,
            "run",
            operatorName.Trim(),
            state,
            after.RuntimeState,
            after.RuntimeStatus,
            after.LoadedProgram,
            resultCode,
            state == AuboArmProgramOperationState.Unknown
                ? "AUBO accepted run but the runtime state is not yet known."
                : null,
            mayHaveWritten,
            _timeProvider.GetUtcNow());
    }

    private async Task<AuboArmProgramOperationResponse> StopCoreAsync(
        string deviceId,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        EnsureOperation(deviceId, operatorName, operationId);
        var before = await ReadProgramStatusAsync(deviceId, cancellationToken).ConfigureAwait(false);
        // Abort is the safety escape path: unlike load/run it remains available
        // while the robot is pausing, in a protective stop, or outside Automatic
        // mode.  We still require a live, known controller before sending it.
        if (!before.Online)
            throw new InvalidOperationException("AUBO controller is offline; stop outcome cannot be confirmed.");
        var program = before.LoadedProgram ?? string.Empty;

        // Stopping an already stopped runtime is idempotent and, importantly, does
        // not send a second abort command after a caller has already reconciled it.
        if (before.RuntimeState == AuboArmRuntimeState.Stopped)
        {
            return new AuboArmProgramOperationResponse(
                operationId,
                deviceId,
                program,
                "stop",
                operatorName.Trim(),
                AuboArmProgramOperationState.Stopped,
                before.RuntimeState,
                before.RuntimeStatus,
                before.LoadedProgram,
                0,
                null,
                MayHaveWritten: false,
                _timeProvider.GetUtcNow());
        }

        var mayHaveWritten = false;
        JsonElement result;
        try
        {
            mayHaveWritten = true;
            result = await InvokeAsync("RuntimeMachine.abort", [], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unknown(operationId, deviceId, program, "stop", "abort timed out", mayHaveWritten);
        }
        catch (AuboArmRpcException exception)
        {
            return await FailedResultAsync(
                operationId, deviceId, program, operatorName, "stop", exception.Code,
                exception.Detail ?? exception.Message, mayHaveWritten, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or WebSocketException or IOException or AuboArmProtocolException)
        {
            throw Unknown(operationId, deviceId, program, "stop", exception.Message, mayHaveWritten);
        }

        var resultCode = ReadResultCode(result);
        if (resultCode is < 0)
        {
            return await FailedResultAsync(
                operationId, deviceId, program, operatorName, "stop", resultCode, "AUBO rejected abort", mayHaveWritten,
                cancellationToken).ConfigureAwait(false);
        }

        var deadline = _timeProvider.GetUtcNow().AddMilliseconds(_options.ProgramOperationTimeoutMs);
        AuboArmProgramStatusResponse after;
        do
        {
            try
            {
                after = await ReadProgramStatusAsync(deviceId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw Unknown(operationId, deviceId, program, "stop", "status read after abort timed out", mayHaveWritten);
            }
            catch (Exception exception) when (exception is TimeoutException or WebSocketException or IOException or AuboArmProtocolException or AuboArmRpcException)
            {
                throw Unknown(operationId, deviceId, program, "stop", exception.Message, mayHaveWritten);
            }
            if (after.RuntimeState == AuboArmRuntimeState.Stopped)
            {
                return new AuboArmProgramOperationResponse(
                    operationId,
                    deviceId,
                    program,
                    "stop",
                    operatorName.Trim(),
                    AuboArmProgramOperationState.Stopped,
                    after.RuntimeState,
                    after.RuntimeStatus,
                    after.LoadedProgram,
                    resultCode,
                    null,
                    mayHaveWritten,
                    _timeProvider.GetUtcNow());
            }

            if (_timeProvider.GetUtcNow() >= deadline) break;
            await Task.Delay(TimeSpan.FromMilliseconds(100), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        while (_timeProvider.GetUtcNow() < deadline);

        throw Unknown(
            operationId,
            deviceId,
            program,
            "stop",
            $"Runtime remained {after.RuntimeStatus ?? after.RuntimeState.ToString()} after abort.",
            mayHaveWritten);
    }

    private async Task<AuboArmProgramStatusResponse> ReadProgramStatusAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        EnsureDevice(deviceId);
        var status = await _reader.GetStatusAsync(deviceId, cancellationToken).ConfigureAwait(false);
        var loaded = await ReadLoadedProgramAsync(deviceId, cancellationToken).ConfigureAwait(false);
        var runtimeText = await ReadStringAsync("RuntimeMachine.getStatus", [], cancellationToken)
            .ConfigureAwait(false);
        return new AuboArmProgramStatusResponse(
            deviceId,
            status.Online,
            NormalizeLoadedProgram(loaded),
            status.RuntimeState,
            runtimeText,
            _timeProvider.GetUtcNow())
        {
            RobotMode = status.Mode,
            SafetyMode = status.SafetyMode,
            OperationalMode = status.OperationalMode,
            ControlEnabled = _options.ControlEnabled
        };
    }

    private async Task<AuboArmProgramStatusResponse> ReadStatusAfterMutationAsync(
        Guid operationId,
        string deviceId,
        string program,
        string operation,
        bool mayHaveWritten,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadProgramStatusAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unknown(operationId, deviceId, program, operation, "status read after mutation timed out", mayHaveWritten);
        }
        catch (Exception exception) when (exception is TimeoutException or WebSocketException or IOException or AuboArmProtocolException or AuboArmRpcException)
        {
            throw Unknown(operationId, deviceId, program, operation, exception.Message, mayHaveWritten);
        }
    }

    private async Task<JsonElement> InvokeAsync(
        string method,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ProgramOperationTimeoutMs);
        try
        {
            return await _transport.InvokeAsync(method, parameters, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"AUBO method '{method}' timed out.");
        }
    }

    private async Task<string?> ReadStringAsync(
        string method,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        var result = await _transport.InvokeAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        return result.ValueKind switch
        {
            JsonValueKind.String => result.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Object when result.TryGetProperty("program", out var program)
                && program.ValueKind == JsonValueKind.String => program.GetString(),
            JsonValueKind.Object when result.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String => name.GetString(),
            JsonValueKind.Object when result.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.String => value.GetString(),
            _ => result.ToString()
        };
    }

    private async Task<string?> ReadLoadedProgramAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (_loadedProgramReader is not null)
        {
            var dashboardProgram = await _loadedProgramReader
                .GetLoadedProgramAsync(deviceId, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(dashboardProgram))
                return NormalizeLoadedProgram(dashboardProgram);
        }

        // Keep the WebSocket preload slot as a compatibility fallback when the
        // optional Dashboard Server is unavailable.
        return NormalizeLoadedProgram(
            await ReadStringAsync(
                "RuntimeMachine.getPreloadProgram",
                [0],
                cancellationToken)
            .ConfigureAwait(false));
    }

    private void EnsureSafeForMutation(AuboArmProgramStatusResponse status, bool requireAutomatic)
    {
        if (!status.Online) throw new InvalidOperationException("AUBO controller is offline.");
        if (status.RobotMode != AuboArmMode.Running)
            throw new InvalidOperationException(
                $"AUBO robot mode is {status.RobotMode}; expected Running before a program operation.");
        if (status.SafetyMode != AuboArmSafetyMode.Normal)
            throw new InvalidOperationException(
                $"AUBO safety mode is {status.SafetyMode}; expected Normal before a program operation.");
        if (status.RuntimeState == AuboArmRuntimeState.Unknown)
            throw new InvalidOperationException("AUBO runtime state is unknown; reconcile before mutating.");
        if (requireAutomatic && _options.RequireAutomaticModeForProgramRun
            && status.OperationalMode != AuboArmOperationalMode.Automatic)
        {
            throw new InvalidOperationException(
                $"AUBO operational mode is {status.OperationalMode}; expected Automatic before remote start.");
        }
        if (status.RuntimeState is AuboArmRuntimeState.Paused
            or AuboArmRuntimeState.Pausing
            or AuboArmRuntimeState.Stopping
            or AuboArmRuntimeState.Aborting
            or AuboArmRuntimeState.Retracting
            or AuboArmRuntimeState.Stepping)
        {
            throw new InvalidOperationException(
                $"AUBO runtime is {status.RuntimeStatus ?? status.RuntimeState.ToString()}; wait for a stable state.");
        }
    }

    private async Task<AuboArmProgramOperationResponse> FailedResultAsync(
        Guid operationId,
        string deviceId,
        string program,
        string operatorName,
        string operation,
        int? resultCode,
        string message,
        bool mayHaveWritten,
        CancellationToken cancellationToken)
    {
        var status = await ReadProgramStatusAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return new AuboArmProgramOperationResponse(
            operationId,
            deviceId,
            program,
            operation,
            operatorName.Trim(),
            AuboArmProgramOperationState.Failed,
            status.RuntimeState,
            status.RuntimeStatus,
            status.LoadedProgram,
            resultCode,
            message,
            mayHaveWritten,
            _timeProvider.GetUtcNow());
    }

    private AuboArmOutcomeUnknownException Unknown(
        Guid operationId,
        string deviceId,
        string program,
        string operation,
        string detail,
        bool mayHaveWritten) =>
        new($"AUBO {operation} operation {operationId:N} for '{program}' on '{deviceId}' is unknown: {detail}", mayHaveWritten);

    private string NormalizeAndAuthorizeProgram(string value)
    {
        var normalized = AuboArmOptions.NormalizeProgramName(value);
        if (normalized.Length is 0 or > 128
            || normalized.Any(char.IsControl)
            || normalized.Contains('/')
            || normalized.Contains('\\')
            || normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Program name must be a simple project name without path components.",
                nameof(value));
        }

        if (!_options.AllowedProgramNames.Any(allowed =>
                string.Equals(NormalizeLoadedProgram(allowed), normalized, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Program '{normalized}' is not on the configured AUBO program allowlist.",
                nameof(value));
        }

        return normalized;
    }

    private static string? NormalizeLoadedProgram(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        try { return AuboArmOptions.NormalizeProgramName(normalized); }
        catch (ArgumentException) { return normalized; }
    }

    private static int? ReadResultCode(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Number && result.TryGetInt32(out var number)) return number;
        if (result.ValueKind == JsonValueKind.String
            && int.TryParse(result.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("ret_code", out var ret))
        {
            if (ret.TryGetInt32(out number)) return number;
            if (ret.ValueKind == JsonValueKind.String
                && int.TryParse(ret.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return number;
        }
        return null;
    }

    private void EnsureDevice(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (!string.Equals(deviceId.Trim(), _options.DeviceId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Adapter device '{deviceId}' is not the configured AUBO arm.");
    }

    private void EnsureOperation(string deviceId, string operatorName, Guid operationId)
    {
        EnsureDevice(deviceId);
        if (!_options.ControlEnabled)
            throw new DeviceControlDisabledException(deviceId);
        if (operationId == Guid.Empty) throw new ArgumentException("An operation id is required.", nameof(operationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);
        if (operatorName.Trim().Length > 128)
            throw new ArgumentException("Operator name is too long.", nameof(operatorName));
    }

    public void Dispose() => _operationGate.Dispose();
}
