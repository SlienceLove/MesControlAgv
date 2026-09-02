using System.Text.Json;

namespace MesControlAgv.Adapter.Modules.AuboArm;

/// <summary>
/// In-process stand-in for the AUBO controller's JSON-RPC surface. It answers the same
/// RegisterControl / RobotState / RuntimeMachine methods so the control-centre chain and
/// the Lua handshake can be exercised end to end before any hardware is reachable.
/// </summary>
public sealed class AuboArmLoopbackController : IAuboArmControlledRpcTransport
{
    private readonly object _gate = new();
    private readonly Dictionary<string, object> _variables = new(StringComparer.Ordinal);
    private readonly List<string> _writeLog = [];
    private readonly AuboArmOptions _options;

    public AuboArmLoopbackController(AuboArmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public int RobotMode { get; set; } = 8;
    public int SafetyMode { get; set; } = 1;
    public int RuntimeState { get; set; }
    public int OperationalMode { get; set; } = 1;
    public string LoadedProgram { get; set; } = string.Empty;

    /// <summary>When enabled, a simulated run returns to Stopped after the configured delay.</summary>
    public bool AutoCompletePrograms { get; set; }

    public TimeSpan ProgramRunDuration { get; set; } = TimeSpan.FromSeconds(1);

    private int _runGeneration;

    /// <summary>
    /// Emulates the resident Lua project: takes the pending command, echoes the sequence,
    /// then publishes a result code the way an if-block would on completion.
    /// </summary>
    public void RunLuaHandshakeCycle(Func<int, (int ResultCode, string? Detail)> ifBlock)
    {
        ArgumentNullException.ThrowIfNull(ifBlock);
        int command;
        int sequence;
        lock (_gate)
        {
            command = ReadInt(_options.CommandVariableKey) ?? 0;
            sequence = ReadInt(_options.SequenceVariableKey) ?? 0;
            if (sequence == 0) return;
            _variables[_options.AcknowledgeVariableKey] = sequence;
            _variables[_options.ResultVariableKey] = 0;
        }

        var (resultCode, detail) = ifBlock(command);
        lock (_gate)
        {
            _variables[_options.ResultVariableKey] = resultCode;
            if (detail is not null) _variables[_options.ResultDetailVariableKey] = detail;
        }
    }

    public void SetVariable(string key, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate) _variables[key.Trim()] = value;
    }

    public Task<JsonElement> InvokeAsync(
        string method,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(parameters);

        var bareMethod = StripRobotPrefix(method);
        lock (_gate)
        {
            return Task.FromResult(bareMethod switch
            {
                "RobotState.getRobotModeType" => Serialize(RobotMode),
                "RobotState.getSafetyModeType" => Serialize(SafetyMode),
                "RobotState.getOperationalMode" => Serialize(OperationalMode),
                "RobotManage.getOperationalMode" => Serialize(OperationalMode),
                "RuntimeMachine.getRuntimeState" => Serialize(RuntimeState),
                "RuntimeMachine.getStatus" => Serialize(RuntimeState switch
                {
                    0 => "Running",
                    1 => "Retracting",
                    2 => "Pausing",
                    3 => "Paused",
                    4 => "Stepping",
                    5 => "Stopping",
                    6 => "Stopped",
                    7 => "Aborting",
                    _ => "Unknown"
                }),
                "RuntimeMachine.getPreloadProgram" => Serialize(LoadedProgram),
                "RuntimeMachine.loadProgram" => LoadProgram(parameters),
                "RuntimeMachine.runProgram" => RunProgram(),
                "RuntimeMachine.abort" => AbortProgram(),
                "RegisterControl.hasNamedVariable" => Serialize(_variables.ContainsKey(Key(parameters))),
                "RegisterControl.getNamedVariableType" => Serialize(TypeName(Key(parameters))),
                "RegisterControl.getInt32" => Serialize(ReadValue<int>(Key(parameters)) ?? Fallback<int>(parameters)),
                "RegisterControl.getBool" => Serialize(ReadValue<bool>(Key(parameters)) ?? Fallback<bool>(parameters)),
                "RegisterControl.getDouble" => Serialize(ReadValue<double>(Key(parameters)) ?? Fallback<double>(parameters)),
                "RegisterControl.getString" => Serialize(ReadText(Key(parameters)) ?? string.Empty),
                "RegisterControl.setInt32" => Write<int>(parameters),
                "RegisterControl.setBool" => Write<bool>(parameters),
                "RegisterControl.setDouble" => Write<double>(parameters),
                "RegisterControl.setString" => WriteText(parameters),
                "RegisterControl.setWatchDog" => ArmWatchDog(parameters),
                _ => throw new AuboArmRpcException(method, -1, "method is not implemented by the loopback controller")
            });
        }
    }

    private string StripRobotPrefix(string method)
    {
        var prefix = _options.RobotName + ".";
        return method.StartsWith(prefix, StringComparison.Ordinal) ? method[prefix.Length..] : method;
    }

    private static string Key(IReadOnlyList<object?> parameters) =>
        parameters.Count > 0 && parameters[0] is string key && key.Length > 0
            ? key
            : throw new AuboArmProtocolException("A named-variable method requires a key parameter.");

    private static T Fallback<T>(IReadOnlyList<object?> parameters) where T : struct =>
        parameters.Count > 1 && parameters[1] is T fallback ? fallback : default;

    private string TypeName(string key) => _variables.TryGetValue(key, out var value)
        ? value switch
        {
            int => "int32",
            bool => "bool",
            double => "double",
            _ => "string"
        }
        : string.Empty;

    private int? ReadInt(string key) => ReadValue<int>(key);

    private T? ReadValue<T>(string key) where T : struct =>
        _variables.TryGetValue(key, out var value) && value is T typed ? typed : null;

    private string? ReadText(string key) =>
        _variables.TryGetValue(key, out var value) && value is string text ? text : null;

    /// <summary>Writes recorded in call order so a test can assert the trigger came last.</summary>
    public IReadOnlyList<string> WriteLog => _writeLog;

    public double? WatchDogTimeoutSeconds { get; private set; }

    public int? WatchDogAction { get; private set; }

    /// <summary>Set to have a write throw, emulating a link drop mid-handshake.</summary>
    public string? FailWritesForKey { get; set; }

    private JsonElement Write<T>(IReadOnlyList<object?> parameters) where T : struct
    {
        var key = Key(parameters);
        GuardWrite(key);
        if (parameters.Count > 1 && parameters[1] is T value) _variables[key] = value;
        return Serialize(0);
    }

    private JsonElement WriteText(IReadOnlyList<object?> parameters)
    {
        var key = Key(parameters);
        GuardWrite(key);
        _variables[key] = parameters.Count > 1 && parameters[1] is string text ? text : string.Empty;
        return Serialize(0);
    }

    private JsonElement ArmWatchDog(IReadOnlyList<object?> parameters)
    {
        var key = Key(parameters);
        GuardWrite($"watchdog:{key}");
        WatchDogTimeoutSeconds = parameters.Count > 1 && parameters[1] is double timeout ? timeout : null;
        WatchDogAction = parameters.Count > 2 && parameters[2] is int action ? action : null;
        return Serialize(0);
    }

    private JsonElement LoadProgram(IReadOnlyList<object?> parameters)
    {
        var program = parameters.Count > 0 ? parameters[0]?.ToString() : null;
        if (string.IsNullOrWhiteSpace(program))
            throw new AuboArmRpcException("RuntimeMachine.loadProgram", -1, "program is required");
        LoadedProgram = program.Trim();
        RuntimeState = 6;
        return Serialize(0);
    }

    private JsonElement RunProgram()
    {
        if (string.IsNullOrWhiteSpace(LoadedProgram))
            throw new AuboArmRpcException("RuntimeMachine.runProgram", -1, "no program loaded");
        RuntimeState = 0;
        if (AutoCompletePrograms)
        {
            var generation = ++_runGeneration;
            var delay = ProgramRunDuration < TimeSpan.Zero ? TimeSpan.Zero : ProgramRunDuration;
            _ = CompleteProgramAsync(generation, delay);
        }
        return Serialize(0);
    }

    private async Task CompleteProgramAsync(int generation, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        lock (_gate)
        {
            if (generation == _runGeneration && RuntimeState == 0)
                RuntimeState = 6;
        }
    }

    private JsonElement AbortProgram()
    {
        _runGeneration++;
        RuntimeState = 6;
        return Serialize(0);
    }

    private void GuardWrite(string key)
    {
        if (FailWritesForKey is { } failing && string.Equals(key, failing, StringComparison.Ordinal))
        {
            throw new AuboArmRpcException("RegisterControl.set", -1, $"link dropped while writing '{key}'");
        }

        _writeLog.Add(key);
    }

    private static JsonElement Serialize<T>(T value) =>
        JsonSerializer.SerializeToElement(value);
}
