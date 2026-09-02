using System.Globalization;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Modules.AuboArm;

/// <summary>
/// Read-only AUBO arm driver. Every call is a getter: this type holds no method that
/// writes a variable, loads a program, or moves the arm.
/// </summary>
public sealed class AuboArmReadOnlyDriver(
    IAuboArmReadOnlyRpcTransport transport,
    AuboArmOptions options,
    TimeProvider timeProvider,
    IAuboArmLoadedProgramReader? loadedProgramReader = null) : IAuboArmDriver
{
    private const string RegisterControl = "RegisterControl";

    public async Task<AuboArmStatusResponse> GetStatusAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        EnsureDevice(deviceId);
        var modeValue = await InvokeOrNullAsync($"{Robot}.RobotState.getRobotModeType", [], cancellationToken);
        var safetyValue = await InvokeOrNullAsync($"{Robot}.RobotState.getSafetyModeType", [], cancellationToken);
        // These are the method names emitted by the onsite AUBO controller logs.
        // Keep a compatibility fallback for older firmware/loopback controllers.
        var runtimeValue = await InvokeOrNullAsync("RuntimeMachine.getStatus", [], cancellationToken)
            ?? await InvokeOrNullAsync("RuntimeMachine.getRuntimeState", [], cancellationToken);
        var operationalValue = await InvokeOrNullAsync(
                $"{Robot}.RobotManage.getOperationalMode", [], cancellationToken)
            ?? await InvokeOrNullAsync(
                $"{Robot}.RobotState.getOperationalMode", [], cancellationToken);

        var mode = ParseInt(modeValue);
        var safety = ParseInt(safetyValue);
        var runtime = ParseRuntimeInt(runtimeValue);
        var operational = ParseOperationalInt(operationalValue);

        return new AuboArmStatusResponse(
            options.DeviceId,
            options.RobotName,
            Online: HasValue(modeValue),
            MapMode(mode, ReadText(modeValue)),
            mode,
            MapSafetyMode(safety, ReadText(safetyValue)),
            safety,
            MapRuntimeState(runtime, ReadText(runtimeValue)),
            runtime,
            MapOperationalMode(operational, ReadText(operationalValue)),
            operational,
            timeProvider.GetUtcNow())
        {
            RawModeText = ReadText(modeValue),
            RawSafetyModeText = ReadText(safetyValue),
            RuntimeStatus = ReadRuntimeText(runtimeValue),
            OperationalModeText = ReadText(operationalValue)
        };
    }

    public async Task<AuboArmReadinessResponse> GetReadinessAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(deviceId, cancellationToken);
        var loadedProgram = await ReadLoadedProgramAsync(deviceId, cancellationToken);
        var normalizedLoadedProgram = NormalizeProgramName(loadedProgram);
        var reasons = new List<string>();

        if (!status.Online)
        {
            reasons.Add("The controller did not report a robot mode; treat the arm as offline.");
        }

        // Fail closed on Unknown: an unmapped vendor integer is not evidence of readiness.
        if (status.Mode != AuboArmMode.Running)
        {
            reasons.Add($"Robot mode is {status.Mode} ({FormatRaw(status.RawMode)}), expected Running (8).");
        }

        if (status.SafetyMode != AuboArmSafetyMode.Normal)
        {
            reasons.Add($"Safety mode is {status.SafetyMode} ({FormatRaw(status.RawSafetyMode)}), expected Normal (1).");
        }

        if (status.OperationalMode is not (AuboArmOperationalMode.Automatic or AuboArmOperationalMode.Disabled))
        {
            reasons.Add(
                $"Operational mode is {status.OperationalMode} ({FormatRaw(status.RawOperationalMode)}); " +
                "a manual/teach pendant mode blocks control-centre dispatch.");
        }

        if (status.RuntimeState is not (AuboArmRuntimeState.Running or AuboArmRuntimeState.Stopped))
        {
            reasons.Add(
                $"Interpreter state is {status.RuntimeState} ({FormatRaw(status.RawRuntimeState)}); " +
                "the Lua project must be running or cleanly stopped.");
        }

        if (options.ExpectedProgramName.Length > 0
            && !string.Equals(normalizedLoadedProgram, options.ExpectedProgramName, StringComparison.Ordinal))
        {
            reasons.Add(
                $"Loaded project is '{normalizedLoadedProgram ?? "none"}', expected '{options.ExpectedProgramName}'.");
        }

        return new AuboArmReadinessResponse(
            options.DeviceId,
            reasons.Count == 0,
            reasons,
            status,
            normalizedLoadedProgram,
            timeProvider.GetUtcNow());
    }

    public async Task<AuboArmProgramCatalogResponse> GetProgramCatalogAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        EnsureDevice(deviceId);
        var status = await GetStatusAsync(deviceId, cancellationToken);
        var preloaded = new List<string>();
        var slots = new List<AuboArmProgramSlot>();
        var errors = new List<string>();
        var current = await ReadLoadedProgramAsync(deviceId, cancellationToken);
        for (var index = 0; index < options.ProgramCatalogMaxSlots; index++)
        {
            if (index > 0 && options.ProgramCatalogInterRequestDelayMs > 0)
            {
                await Task.Delay(
                    options.ProgramCatalogInterRequestDelayMs,
                    cancellationToken);
            }
            try
            {
                var value = await TryReadStringAsync(
                    "RuntimeMachine.getPreloadProgram",
                    [index],
                    cancellationToken);
                var normalized = NormalizeProgramName(value);
                if (index == 0 && string.IsNullOrWhiteSpace(current)) current = normalized;
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    preloaded.Add(normalized!);
                    slots.Add(new AuboArmProgramSlot(index, normalized!));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Keep partial read-only evidence visible. A catalog read must
                // never turn into a write or an automatic reconnect loop.
                errors.Add($"slot {index}: {exception.Message}");
            }
        }

        return new AuboArmProgramCatalogResponse(
            options.DeviceId,
            status.Online,
            current,
            preloaded.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            options.AllowedProgramNames.ToArray(),
            errors.Count == 0,
            errors,
            timeProvider.GetUtcNow())
        {
            Slots = slots.ToArray()
        };
    }

    public async Task<AuboArmVariableResponse> GetVariableAsync(
        string deviceId,
        string key,
        CancellationToken cancellationToken)
    {
        EnsureDevice(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalizedKey = key.Trim();
        if (!options.BuildReadableVariableAllowlist().Contains(normalizedKey, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Named variable '{normalizedKey}' is not on the configured readable allowlist.",
                nameof(key));
        }

        return await ReadVariableAsync(normalizedKey, cancellationToken);
    }

    public async Task<AuboArmHandshakeSnapshotResponse> GetHandshakeSnapshotAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        EnsureDevice(deviceId);
        var command = await ReadVariableAsync(options.CommandVariableKey, cancellationToken);
        var sequence = await ReadVariableAsync(options.SequenceVariableKey, cancellationToken);
        var acknowledged = await ReadVariableAsync(options.AcknowledgeVariableKey, cancellationToken);
        var result = await ReadVariableAsync(options.ResultVariableKey, cancellationToken);
        var detail = await ReadVariableAsync(options.ResultDetailVariableKey, cancellationToken);

        return new AuboArmHandshakeSnapshotResponse(
            options.DeviceId,
            ClassifyHandshake(sequence.Int32Value, acknowledged.Int32Value, result.Int32Value),
            command.Int32Value,
            sequence.Int32Value,
            acknowledged.Int32Value,
            result.Int32Value,
            detail.StringValue,
            timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Derives handshake state from the sequence/ack/result triple alone. No history is
    /// consulted, so the same conclusion is reached after an Adapter restart.
    /// </summary>
    public static AuboArmHandshakeState ClassifyHandshake(int? sequence, int? acknowledged, int? result)
    {
        if (sequence is null or 0) return AuboArmHandshakeState.Idle;
        if (acknowledged != sequence) return AuboArmHandshakeState.Dispatched;
        // Lua publishes the result only after its if-block finishes, so an echoed
        // sequence with no result means the branch is still executing.
        return result switch
        {
            null or 0 => AuboArmHandshakeState.Running,
            > 0 => AuboArmHandshakeState.Completed,
            _ => AuboArmHandshakeState.Failed
        };
    }

    private string Robot => options.RobotName;

    private async Task<AuboArmVariableResponse> ReadVariableAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var observedAt = timeProvider.GetUtcNow();
        var exists = await TryReadBoolAsync($"{RegisterControl}.hasNamedVariable", [key], cancellationToken);
        if (exists != true)
        {
            return new AuboArmVariableResponse(
                options.DeviceId,
                key,
                Exists: false,
                RawType: null,
                Int32Value: null,
                BoolValue: null,
                DoubleValue: null,
                StringValue: null,
                observedAt);
        }

        var rawType = await TryReadStringAsync($"{RegisterControl}.getNamedVariableType", [key], cancellationToken);
        var normalizedType = rawType?.Trim() ?? string.Empty;
        int? int32Value = null;
        bool? boolValue = null;
        double? doubleValue = null;
        string? stringValue = null;

        // Read strictly by the reported type. Coercing an unexpected type would invent a
        // value the controller never published.
        if (normalizedType.Contains("int", StringComparison.OrdinalIgnoreCase))
        {
            int32Value = await TryReadIntAsync($"{RegisterControl}.getInt32", [key, 0], cancellationToken);
        }
        else if (normalizedType.Contains("bool", StringComparison.OrdinalIgnoreCase))
        {
            boolValue = await TryReadBoolAsync($"{RegisterControl}.getBool", [key, false], cancellationToken);
        }
        else if (normalizedType.Contains("double", StringComparison.OrdinalIgnoreCase)
            || normalizedType.Contains("float", StringComparison.OrdinalIgnoreCase))
        {
            doubleValue = await TryReadDoubleAsync($"{RegisterControl}.getDouble", [key, 0.0], cancellationToken);
        }
        else if (normalizedType.Contains("string", StringComparison.OrdinalIgnoreCase))
        {
            stringValue = await TryReadStringAsync($"{RegisterControl}.getString", [key, string.Empty], cancellationToken);
        }

        return new AuboArmVariableResponse(
            options.DeviceId,
            key,
            Exists: true,
            string.IsNullOrWhiteSpace(rawType) ? null : normalizedType,
            int32Value,
            boolValue,
            doubleValue,
            stringValue,
            observedAt);
    }

    private Task<int?> TryReadIntAsync(string method, CancellationToken cancellationToken) =>
        TryReadIntAsync(method, [], cancellationToken);

    private async Task<int?> TryReadRuntimeStateAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeOrNullAsync("RuntimeMachine.getStatus", [], cancellationToken)
            ?? await InvokeOrNullAsync("RuntimeMachine.getRuntimeState", [], cancellationToken);
        if (result is not { } value) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind != JsonValueKind.String) return null;

        return value.GetString()?.Trim().ToLowerInvariant() switch
        {
            "running" => 0,
            "retracting" => 1,
            "pausing" => 2,
            "paused" => 3,
            "stepping" => 4,
            "stopping" => 5,
            "stopped" => 6,
            "aborting" => 7,
            _ => null
        };
    }

    private async Task<int?> TryReadOperationalModeAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeOrNullAsync(
                $"{Robot}.RobotManage.getOperationalMode",
                [],
                cancellationToken)
            ?? await InvokeOrNullAsync(
                $"{Robot}.RobotState.getOperationalMode",
                [],
                cancellationToken);
        if (result is not { } value) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind != JsonValueKind.String) return null;

        return value.GetString()?.Trim().ToLowerInvariant() switch
        {
            "disabled" => 0,
            "automatic" => 1,
            "manual" => 2,
            _ => null
        };
    }

    private async Task<int?> TryReadIntAsync(
        string method,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        var result = await InvokeOrNullAsync(method, parameters, cancellationToken);
        if (result is not { } value) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private async Task<bool?> TryReadBoolAsync(
        string method,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        var result = await InvokeOrNullAsync(method, parameters, cancellationToken);
        if (result is not { } value) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private async Task<double?> TryReadDoubleAsync(
        string method,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        var result = await InvokeOrNullAsync(method, parameters, cancellationToken);
        if (result is not { } value) return null;
        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number)
            && double.IsFinite(number))
        {
            return number;
        }
        return value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number)
                ? number
                : null;
    }

    private async Task<string?> TryReadStringAsync(
        string method,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        var result = await InvokeOrNullAsync(method, parameters, cancellationToken);
        if (result is not { } value) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "value", "program", "name", "status" })
            {
                if (value.TryGetProperty(propertyName, out var property)
                    && property.ValueKind == JsonValueKind.String)
                    return property.GetString();
            }
        }

        return null;
    }

    private async Task<string?> ReadLoadedProgramAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (loadedProgramReader is not null)
        {
            var dashboardProgram = await loadedProgramReader
                .GetLoadedProgramAsync(deviceId, cancellationToken)
                .ConfigureAwait(false);
            var normalizedDashboardProgram = NormalizeProgramName(dashboardProgram);
            if (!string.IsNullOrWhiteSpace(normalizedDashboardProgram))
                return normalizedDashboardProgram;
        }

        // RuntimeMachine.getPreloadProgram(0) is a useful fallback on firmware
        // without the Dashboard Server, but it represents a preload slot rather
        // than the full currently loaded project.
        return NormalizeProgramName(
            await TryReadStringAsync(
                "RuntimeMachine.getPreloadProgram",
                [0],
                cancellationToken)
            .ConfigureAwait(false));
    }

    private async Task<JsonElement?> InvokeOrNullAsync(
        string method,
        IReadOnlyList<object?> parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            return await transport.InvokeAsync(method, parameters, cancellationToken);
        }
        catch (AuboArmRpcException)
        {
            // An unsupported or rejected getter yields "unknown", never a fabricated value.
            return null;
        }
    }

    private void EnsureDevice(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (!string.Equals(deviceId.Trim(), options.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            throw new KeyNotFoundException($"Adapter device '{deviceId}' is not the configured AUBO arm.");
        }
    }

    private static string FormatRaw(int? raw) =>
        raw?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";

    private static string? NormalizeProgramName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return AuboArmOptions.NormalizeProgramName(value); }
        catch (ArgumentException) { return value.Trim(); }
    }

    private static int? ParseInt(JsonElement? value)
    {
        if (value is not { } item) return null;
        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number)) return number;
        return item.ValueKind == JsonValueKind.String
            && int.TryParse(item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private static string? ReadText(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } item ? NormalizeEnumText(item.GetString()) : null;

    private static string? ReadRuntimeText(JsonElement? value)
    {
        if (value is not { } item) return null;
        if (item.ValueKind == JsonValueKind.String) return item.GetString()?.Trim();
        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number))
            return MapRuntimeState(number).ToString();
        if (item.ValueKind == JsonValueKind.Object
            && (item.TryGetProperty("status", out var status)
                || item.TryGetProperty("runtimeStatus", out status))
            && status.ValueKind == JsonValueKind.String)
            return NormalizeEnumText(status.GetString());
        return item.ToString();
    }

    private static int? ParseRuntimeInt(JsonElement? value)
    {
        var parsed = ParseInt(value);
        if (parsed is not null) return parsed;
        var text = ReadRuntimeText(value)?.ToLowerInvariant();
        return text switch
        {
            "running" => 0,
            "retracting" => 1,
            "pausing" => 2,
            "paused" => 3,
            "stepping" => 4,
            "stopping" => 5,
            "stopped" => 6,
            "aborting" => 7,
            _ => null
        };
    }

    private static int? ParseOperationalInt(JsonElement? value)
    {
        var parsed = ParseInt(value);
        if (parsed is not null) return parsed;
        return ReadText(value)?.ToLowerInvariant() switch
        {
            "disabled" => 0,
            "automatic" => 1,
            "manual" => 2,
            _ => null
        };
    }

    private static bool HasValue(JsonElement? value) =>
        value is { } item && item.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static string? NormalizeEnumText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        var separator = normalized.LastIndexOf('_');
        if (separator >= 0 && separator < normalized.Length - 1
            && normalized[..separator].Contains("ENUM", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[(separator + 1)..];
        return normalized;
    }

    private static AuboArmMode MapMode(int? raw, string? text) =>
        raw is not null ? MapMode(raw) : text?.ToLowerInvariant() switch
        {
            "no_controller" or "nocontroller" => AuboArmMode.NoController,
            "disconnected" => AuboArmMode.Disconnected,
            "confirm_safety" or "confirmsafety" => AuboArmMode.ConfirmSafety,
            "booting" => AuboArmMode.Booting,
            "power_off" or "poweroff" => AuboArmMode.PowerOff,
            "power_on" or "poweron" => AuboArmMode.PowerOn,
            "idle" => AuboArmMode.Idle,
            "brake_releasing" or "brakereleasing" => AuboArmMode.BrakeReleasing,
            "back_drive" or "backdrive" => AuboArmMode.BackDrive,
            "running" => AuboArmMode.Running,
            "maintenance" => AuboArmMode.Maintenance,
            "error" => AuboArmMode.Error,
            "power_offing" or "poweroffing" => AuboArmMode.PowerOffing,
            _ => AuboArmMode.Unknown
        };

    private static AuboArmMode MapMode(int? raw) => raw switch
    {
        -1 => AuboArmMode.NoController,
        0 => AuboArmMode.Disconnected,
        1 => AuboArmMode.ConfirmSafety,
        2 => AuboArmMode.Booting,
        3 => AuboArmMode.PowerOff,
        4 => AuboArmMode.PowerOn,
        5 => AuboArmMode.Idle,
        6 => AuboArmMode.BrakeReleasing,
        7 => AuboArmMode.BackDrive,
        8 => AuboArmMode.Running,
        9 => AuboArmMode.Maintenance,
        10 => AuboArmMode.Error,
        11 => AuboArmMode.PowerOffing,
        _ => AuboArmMode.Unknown
    };

    private static AuboArmSafetyMode MapSafetyMode(int? raw, string? text) =>
        raw is not null ? MapSafetyMode(raw) : text?.ToLowerInvariant() switch
        {
            "undefined" => AuboArmSafetyMode.Undefined,
            "normal" => AuboArmSafetyMode.Normal,
            "reduced_mode" or "reducedmode" => AuboArmSafetyMode.ReducedMode,
            "recovery" => AuboArmSafetyMode.Recovery,
            "violation" => AuboArmSafetyMode.Violation,
            "protective_stop" or "protectivestop" => AuboArmSafetyMode.ProtectiveStop,
            "safeguard_stop" or "safeguardstop" => AuboArmSafetyMode.SafeguardStop,
            "system_emergency_stop" or "systememergencystop" => AuboArmSafetyMode.SystemEmergencyStop,
            "robot_emergency_stop" or "robotemergencystop" => AuboArmSafetyMode.RobotEmergencyStop,
            "fault" => AuboArmSafetyMode.Fault,
            _ => AuboArmSafetyMode.Unknown
        };

    private static AuboArmSafetyMode MapSafetyMode(int? raw) => raw switch
    {
        0 => AuboArmSafetyMode.Undefined,
        1 => AuboArmSafetyMode.Normal,
        2 => AuboArmSafetyMode.ReducedMode,
        3 => AuboArmSafetyMode.Recovery,
        4 => AuboArmSafetyMode.Violation,
        5 => AuboArmSafetyMode.ProtectiveStop,
        6 => AuboArmSafetyMode.SafeguardStop,
        7 => AuboArmSafetyMode.SystemEmergencyStop,
        8 => AuboArmSafetyMode.RobotEmergencyStop,
        9 => AuboArmSafetyMode.Fault,
        _ => AuboArmSafetyMode.Unknown
    };

    private static AuboArmRuntimeState MapRuntimeState(int? raw, string? text) =>
        raw is not null ? MapRuntimeState(raw) : text?.ToLowerInvariant() switch
        {
            "running" => AuboArmRuntimeState.Running,
            "retracting" => AuboArmRuntimeState.Retracting,
            "pausing" => AuboArmRuntimeState.Pausing,
            "paused" => AuboArmRuntimeState.Paused,
            "stepping" => AuboArmRuntimeState.Stepping,
            "stopping" => AuboArmRuntimeState.Stopping,
            "stopped" => AuboArmRuntimeState.Stopped,
            "aborting" => AuboArmRuntimeState.Aborting,
            _ => AuboArmRuntimeState.Unknown
        };

    private static AuboArmRuntimeState MapRuntimeState(int? raw) => raw switch
    {
        0 => AuboArmRuntimeState.Running,
        1 => AuboArmRuntimeState.Retracting,
        2 => AuboArmRuntimeState.Pausing,
        3 => AuboArmRuntimeState.Paused,
        4 => AuboArmRuntimeState.Stepping,
        5 => AuboArmRuntimeState.Stopping,
        6 => AuboArmRuntimeState.Stopped,
        7 => AuboArmRuntimeState.Aborting,
        _ => AuboArmRuntimeState.Unknown
    };

    private static AuboArmOperationalMode MapOperationalMode(int? raw, string? text) =>
        raw is not null ? MapOperationalMode(raw) : text?.ToLowerInvariant() switch
        {
            "disabled" => AuboArmOperationalMode.Disabled,
            "automatic" => AuboArmOperationalMode.Automatic,
            "manual" => AuboArmOperationalMode.Manual,
            _ => AuboArmOperationalMode.Unknown
        };

    private static AuboArmOperationalMode MapOperationalMode(int? raw) => raw switch
    {
        0 => AuboArmOperationalMode.Disabled,
        1 => AuboArmOperationalMode.Automatic,
        2 => AuboArmOperationalMode.Manual,
        _ => AuboArmOperationalMode.Unknown
    };
}
