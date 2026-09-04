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
    private readonly AuboArmProgramCatalogCache? _programCatalogCache;
    private readonly ILogger<AuboArmProgramDriver>? _logger;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public AuboArmProgramDriver(
        IAuboArmReadOnlyRpcTransport transport,
        AuboArmReadOnlyDriver reader,
        AuboArmOptions options,
        TimeProvider timeProvider,
        IAuboArmLoadedProgramReader? loadedProgramReader = null,
        AuboArmProgramCatalogCache? programCatalogCache = null,
        ILogger<AuboArmProgramDriver>? logger = null)
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
        _programCatalogCache = programCatalogCache;
        _logger = logger;
    }

    /// <summary>Binary/source-compatible constructor retained for older hosts.</summary>
    public AuboArmProgramDriver(
        IAuboArmReadOnlyRpcTransport transport,
        AuboArmReadOnlyDriver reader,
        AuboArmOptions options,
        TimeProvider timeProvider,
        IAuboArmLoadedProgramReader? loadedProgramReader,
        AuboArmProgramCatalogCache? programCatalogCache)
        : this(
            transport,
            reader,
            options,
            timeProvider,
            loadedProgramReader,
            programCatalogCache,
            logger: null)
    {
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

    public Task<AuboArmProgramCatalogResponse> GetProgramCatalogAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        GetProgramCatalogAsync(deviceId, forceFresh: false, cancellationToken);

    public async Task<AuboArmProgramCatalogResponse> GetProgramCatalogAsync(
        string deviceId,
        bool forceFresh,
        CancellationToken cancellationToken)
    {
        EnsureDevice(deviceId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _reader
                .GetProgramCatalogAsync(deviceId, forceFresh, cancellationToken)
                .ConfigureAwait(false);
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
        LoadProgramAsync(
            deviceId,
            programName,
            operatorName,
            operationId,
            correlation: null,
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> LoadProgramAsync(
        string deviceId,
        string programName,
        string operatorName,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken) =>
        ExecuteSerializedAsync(
            () => ExecuteMutationWithCatalogInvalidationAsync(
                deviceId,
                () => LoadCoreAsync(deviceId, programName, operatorName, operationId, correlation, cancellationToken)),
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> RunProgramAsync(
        string deviceId,
        string? programName,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        RunProgramAsync(
            deviceId,
            programName,
            operatorName,
            operationId,
            correlation: null,
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> RunProgramAsync(
        string deviceId,
        string? programName,
        string operatorName,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken) =>
        ExecuteSerializedAsync(
            () => ExecuteMutationWithCatalogInvalidationAsync(
                deviceId,
                () => RunCoreAsync(deviceId, programName, operatorName, operationId, correlation, cancellationToken)),
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> StopProgramAsync(
        string deviceId,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        StopProgramAsync(
            deviceId,
            operatorName,
            operationId,
            correlation: null,
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> StopProgramAsync(
        string deviceId,
        string operatorName,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken) =>
        ExecuteSerializedAsync(
            () => ExecuteMutationWithCatalogInvalidationAsync(
                deviceId,
                () => StopCoreAsync(deviceId, operatorName, operationId, correlation, cancellationToken)),
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
            request.WorkflowCorrelation,
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
            request.WorkflowCorrelation,
            cancellationToken);

    public Task<AuboArmProgramOperationResponse> StopProgramAsync(
        string deviceId,
        AuboArmProgramStopRequest request,
        CancellationToken cancellationToken) =>
        StopProgramAsync(
            deviceId,
            request.EffectiveOperatorName,
            request.OperationId.GetValueOrDefault(Guid.NewGuid()),
            request.WorkflowCorrelation,
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

    private async Task<AuboArmProgramOperationResponse> ExecuteMutationWithCatalogInvalidationAsync(
        string deviceId,
        Func<Task<AuboArmProgramOperationResponse>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            // A catalog scan can overlap the controller write from another HTTP
            // scope. Invalidate again after the operation so a scan that started
            // between the pre-write invalidation and the RPC cannot leave stale
            // data in the shared cache.
            InvalidateCatalogIfConfiguredDevice(deviceId);
        }
    }

    private void InvalidateCatalogIfConfiguredDevice(string deviceId)
    {
        if (_programCatalogCache is null || string.IsNullOrWhiteSpace(deviceId)) return;
        if (string.Equals(deviceId.Trim(), _options.DeviceId, StringComparison.OrdinalIgnoreCase))
            _programCatalogCache.Invalidate(deviceId);
    }

    private async Task<AuboArmProgramOperationResponse> LoadCoreAsync(
        string deviceId,
        string programName,
        string operatorName,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken)
    {
        EnsureOperation(deviceId, operatorName, operationId, correlation, "load");
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
            _programCatalogCache?.Invalidate(deviceId);
            mayHaveWritten = true;
            result = await InvokeAsync("RuntimeMachine.loadProgram", [normalized], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unknown(operationId, deviceId, normalized, "load", "loadProgram timed out", mayHaveWritten, correlation);
        }
        catch (AuboArmRpcException exception)
        {
            return await FailedResultAsync(
                operationId, deviceId, normalized, operatorName, "load", exception.Code,
                exception.Detail ?? exception.Message, mayHaveWritten, correlation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or WebSocketException or IOException or AuboArmProtocolException)
        {
            throw Unknown(operationId, deviceId, normalized, "load", exception.Message, mayHaveWritten, correlation);
        }

        var resultCode = ReadResultCode(result);
        if (resultCode is < 0)
        {
            return await FailedResultAsync(
                operationId, deviceId, normalized, operatorName, "load", resultCode, "AUBO rejected load", mayHaveWritten,
                correlation, cancellationToken).ConfigureAwait(false);
        }

        // The controller acknowledges RuntimeMachine.loadProgram before its
        // dashboard/preload read becomes consistent.  Poll the read-only state
        // for the bounded operation window instead of turning that normal
        // propagation delay into an Unknown outcome (and forcing a manual
        // operator recovery).
        var after = await ReadStatusAfterLoadAsync(
            operationId, deviceId, normalized, mayHaveWritten, correlation, cancellationToken).ConfigureAwait(false);
        var loaded = string.Equals(after.LoadedProgram, normalized, StringComparison.Ordinal);
        return AttachCorrelation(new AuboArmProgramOperationResponse(
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
            _timeProvider.GetUtcNow()), correlation);
    }

    private async Task<AuboArmProgramOperationResponse> RunCoreAsync(
        string deviceId,
        string? programName,
        string operatorName,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken)
    {
        EnsureOperation(deviceId, operatorName, operationId, correlation, "run");
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
            _programCatalogCache?.Invalidate(deviceId);
            mayHaveWritten = true;
            result = await InvokeAsync("RuntimeMachine.runProgram", [], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unknown(operationId, deviceId, loaded, "run", "runProgram timed out", mayHaveWritten, correlation);
        }
        catch (AuboArmRpcException exception)
        {
            return await FailedResultAsync(
                operationId, deviceId, loaded, operatorName, "run", exception.Code,
                exception.Detail ?? exception.Message, mayHaveWritten, correlation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or WebSocketException or IOException or AuboArmProtocolException)
        {
            throw Unknown(operationId, deviceId, loaded, "run", exception.Message, mayHaveWritten, correlation);
        }

        var resultCode = ReadResultCode(result);
        if (resultCode is < 0)
        {
            return await FailedResultAsync(
                operationId, deviceId, loaded, operatorName, "run", resultCode, "AUBO rejected run", mayHaveWritten,
                correlation, cancellationToken).ConfigureAwait(false);
        }

        // AUBO may acknowledge runProgram while the runtime is still stopped
        // for a short startup interval. Wait for an observable Running state;
        // treating an immediate Stopped read as success would let the workflow
        // advance before the robot had actually begun moving.
        var after = await ReadStatusAfterRunAsync(
            operationId, deviceId, loaded, mayHaveWritten, correlation, cancellationToken).ConfigureAwait(false);
        var state = after.RuntimeState == AuboArmRuntimeState.Running
            ? AuboArmProgramOperationState.Running
            : AuboArmProgramOperationState.Unknown;
        return AttachCorrelation(new AuboArmProgramOperationResponse(
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
                ? "AUBO acknowledged run but the runtime did not enter Running within the confirmation window."
                : null,
            mayHaveWritten,
            _timeProvider.GetUtcNow()), correlation);
    }

    private async Task<AuboArmProgramOperationResponse> StopCoreAsync(
        string deviceId,
        string operatorName,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken)
    {
        EnsureOperation(deviceId, operatorName, operationId, correlation, "stop");
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
            return AttachCorrelation(new AuboArmProgramOperationResponse(
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
                _timeProvider.GetUtcNow()), correlation);
        }

        var mayHaveWritten = false;
        JsonElement result;
        try
        {
            _programCatalogCache?.Invalidate(deviceId);
            mayHaveWritten = true;
            result = await InvokeAsync("RuntimeMachine.abort", [], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unknown(operationId, deviceId, program, "stop", "abort timed out", mayHaveWritten, correlation);
        }
        catch (AuboArmRpcException exception)
        {
            return await FailedResultAsync(
                operationId, deviceId, program, operatorName, "stop", exception.Code,
                exception.Detail ?? exception.Message, mayHaveWritten, correlation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or WebSocketException or IOException or AuboArmProtocolException)
        {
            throw Unknown(operationId, deviceId, program, "stop", exception.Message, mayHaveWritten, correlation);
        }

        var resultCode = ReadResultCode(result);
        if (resultCode is < 0)
        {
            return await FailedResultAsync(
                operationId, deviceId, program, operatorName, "stop", resultCode, "AUBO rejected abort", mayHaveWritten,
                correlation, cancellationToken).ConfigureAwait(false);
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
                throw Unknown(operationId, deviceId, program, "stop", "status read after abort timed out", mayHaveWritten, correlation);
            }
            catch (Exception exception) when (exception is TimeoutException or WebSocketException or IOException or AuboArmProtocolException or AuboArmRpcException)
            {
                throw Unknown(operationId, deviceId, program, "stop", exception.Message, mayHaveWritten, correlation);
            }
            if (after.RuntimeState == AuboArmRuntimeState.Stopped)
            {
                return AttachCorrelation(new AuboArmProgramOperationResponse(
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
                    _timeProvider.GetUtcNow()), correlation);
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
            mayHaveWritten,
            correlation);
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
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadProgramStatusAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unknown(operationId, deviceId, program, operation, "status read after mutation timed out", mayHaveWritten, correlation);
        }
        catch (Exception exception) when (exception is TimeoutException or WebSocketException or IOException or AuboArmProtocolException or AuboArmRpcException)
        {
            throw Unknown(operationId, deviceId, program, operation, exception.Message, mayHaveWritten, correlation);
        }
    }

    private async Task<AuboArmProgramStatusResponse> ReadStatusAfterLoadAsync(
        Guid operationId,
        string deviceId,
        string program,
        bool mayHaveWritten,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken)
    {
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
                throw Unknown(operationId, deviceId, program, "load", "status read after load timed out", mayHaveWritten, correlation);
            }
            catch (Exception exception) when (exception is TimeoutException or WebSocketException or IOException or AuboArmProtocolException or AuboArmRpcException)
            {
                throw Unknown(operationId, deviceId, program, "load", exception.Message, mayHaveWritten, correlation);
            }

            if (string.Equals(after.LoadedProgram, program, StringComparison.Ordinal))
                return after;

            if (_timeProvider.GetUtcNow() >= deadline) break;
            await Task.Delay(TimeSpan.FromMilliseconds(100), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        while (_timeProvider.GetUtcNow() < deadline);

        return after;
    }

    private async Task<AuboArmProgramStatusResponse> ReadStatusAfterRunAsync(
        Guid operationId,
        string deviceId,
        string program,
        bool mayHaveWritten,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken)
    {
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
                throw Unknown(operationId, deviceId, program, "run", "status read after run timed out", mayHaveWritten, correlation);
            }
            catch (Exception exception) when (exception is TimeoutException or WebSocketException or IOException or AuboArmProtocolException or AuboArmRpcException)
            {
                throw Unknown(operationId, deviceId, program, "run", exception.Message, mayHaveWritten, correlation);
            }

            if (after.RuntimeState is AuboArmRuntimeState.Running or AuboArmRuntimeState.Unknown)
                return after;

            if (_timeProvider.GetUtcNow() >= deadline) break;
            await Task.Delay(TimeSpan.FromMilliseconds(100), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        while (_timeProvider.GetUtcNow() < deadline);

        return after;
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
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken)
    {
        var status = await ReadProgramStatusAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return AttachCorrelation(new AuboArmProgramOperationResponse(
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
            _timeProvider.GetUtcNow()), correlation);
    }

    private AuboArmOutcomeUnknownException Unknown(
        Guid operationId,
        string deviceId,
        string program,
        string operation,
        string detail,
        bool mayHaveWritten,
        AuboArmOperationCorrelation? correlation = null) =>
        new($"AUBO {operation} operation {operationId:N} for '{program}' on '{deviceId}' is unknown: {detail}", mayHaveWritten, correlation);

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

    private AuboArmProgramOperationResponse AttachCorrelation(
        AuboArmProgramOperationResponse response,
        AuboArmOperationCorrelation? correlation)
    {
        if (correlation is null)
        {
            return response with
            {
                CorrelationWarningCode = "AUBO_UNCORRELATED_WRITE",
                CorrelationWarning = "The AUBO program write was not associated with a workflow device operation; reconcile it manually."
            };
        }

        return response with
        {
            WorkflowRunId = correlation.WorkflowRunId,
            WorkflowNodeExecutionId = correlation.WorkflowNodeExecutionId,
            DeviceOperationId = correlation.DeviceOperationId,
            RequestId = correlation.RequestId,
            CorrelationId = correlation.EffectiveCorrelationId,
            Attempt = correlation.Attempt
        };
    }

    private void EnsureOperation(
        string deviceId,
        string operatorName,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        string operation)
    {
        EnsureDevice(deviceId);
        if (!_options.ControlEnabled)
            throw new DeviceControlDisabledException(deviceId);
        if (operationId == Guid.Empty) throw new ArgumentException("An operation id is required.", nameof(operationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);
        if (operatorName.Trim().Length > 128)
            throw new ArgumentException("Operator name is too long.", nameof(operatorName));

        if (correlation is null)
        {
            SafeLogWarning(
                "AUBO program {Operation} for device {DeviceId} used operation {OperationId} without workflow correlation. Manual reconciliation is required.",
                operation,
                deviceId,
                operationId);
            return;
        }

        if (!correlation.IsComplete)
        {
            throw new ArgumentException(
                $"AUBO {operation} workflow correlation is incomplete: {correlation.ValidationError}",
                nameof(correlation));
        }

        if (correlation.DeviceOperationId != operationId)
        {
            throw new ArgumentException(
                "AUBO workflow correlation DeviceOperationId must equal operationId.",
                nameof(correlation));
        }

        SafeLogInformation(
            "AUBO program {Operation} for {DeviceId} is correlated to workflow {WorkflowRunId}, node {WorkflowNodeExecutionId}, device operation {DeviceOperationId}, request {RequestId}, attempt {Attempt}, correlation {CorrelationId}.",
            operation,
            deviceId,
            correlation.WorkflowRunId,
            correlation.WorkflowNodeExecutionId,
            correlation.DeviceOperationId,
            correlation.RequestId,
            correlation.Attempt,
            correlation.EffectiveCorrelationId);
    }

    private void SafeLogWarning(string message, params object?[] args)
    {
        try { _logger?.LogWarning(message, args); }
        catch { /* logging must not change the device-operation outcome */ }
    }

    private void SafeLogInformation(string message, params object?[] args)
    {
        try { _logger?.LogInformation(message, args); }
        catch { /* logging must not change the device-operation outcome */ }
    }

    public void Dispose() => _operationGate.Dispose();
}
