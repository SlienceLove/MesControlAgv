using Microsoft.Extensions.Configuration;

namespace MesControlAgv.Adapter.Modules.AuboArm;

/// <summary>
/// AUBO arm controller options. The module remains disabled by default, while an
/// explicitly enabled deployment can expose the named-variable handshake writer.
/// Control is startup-configured so the field operator can switch from the current
/// read-only baseline deliberately.
/// </summary>
public sealed record AuboArmOptions
{
    public const string SectionName = "Devices:AuboArm";

    public string DeviceId { get; init; } = "ARM-01";

    /// <summary>
    /// Transport implementation. The production default is the verified AUBO
    /// WebSocket endpoint; <c>simulator</c> is an explicit in-process driver for
    /// FieldSimulation and never opens a controller socket.
    /// </summary>
    public string Driver { get; init; } = "websocket";

    public bool IsSimulator => string.Equals(Driver, "simulator", StringComparison.OrdinalIgnoreCase);

    /// <summary>Controller host. No default IP: an unverified endpoint must not be reachable by accident.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>
    /// Site-confirmed WebSocket JSON-RPC port.  The older 30004 TCP endpoint is
    /// intentionally not the default because the field controller accepted the
    /// verified protocol on 9012 only.
    /// </summary>
    public int Port { get; init; } = 9012;

    /// <summary>WebSocket path exposed by the controller (the field endpoint uses <c>/</c>).</summary>
    public string WebSocketPath { get; init; } = "/";

    /// <summary>
    /// Read-only Dashboard Server port used only for the documented
    /// <c>get loaded program</c> query. It is separate from the WebSocket RPC
    /// port and never receives load/play/stop commands from this client.
    /// </summary>
    public int DashboardPort { get; init; } = 29999;

    /// <summary>Timeout for one Dashboard Server loaded-program query.</summary>
    public int DashboardTimeoutMs { get; init; } = 3000;

    /// <summary>Robot name passed to per-robot interfaces; every vendor example uses "rob1".</summary>
    public string RobotName { get; init; } = "rob1";

    public bool Enabled { get; init; }

    /// <summary>
    /// Enables the controlled named-variable handshake route. This must be paired
    /// with Enabled=true and Adapter:RunMode=standard; the default is false.
    /// </summary>
    public bool ControlEnabled { get; init; }

    /// <summary>
    /// Keeps the pre-existing named-variable handshake quarantined.  It is not
    /// part of the fast WebSocket project path and must be enabled separately
    /// after the Lua contract is confirmed on site.
    /// </summary>
    public bool EnableLegacyHandshake { get; init; }

    /// <summary>Enables only the in-process loopback handshake harness; never enables hardware writes.</summary>
    public bool EnableLoopbackControl { get; init; }

    public int RequestTimeoutMs { get; init; } = 3000;

    public int ConnectTimeoutMs { get; init; } = 3000;

    /// <summary>Maximum one JSON-RPC WebSocket message accepted from the controller.</summary>
    public int MaximumMessageBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Keeps the WebSocket open for several sequential RPCs when true.  The field
    /// default is false: the verified script uses one request/response per socket,
    /// which makes a stale close unambiguous and avoids replaying a mutating call.
    /// </summary>
    public bool ReuseWebSocketConnection { get; init; }

    /// <summary>
    /// Upper bound for one load/run/stop operation.  This is not a retry budget:
    /// a timeout after a mutating call is reported as Unknown.
    /// </summary>
    public int ProgramOperationTimeoutMs { get; init; } = 10000;

    /// <summary>
    /// Maximum number of read-only preloaded project slots scanned by the
    /// explicit program-catalog request. AUBO documents slots 0..99.
    /// </summary>
    public int ProgramCatalogMaxSlots { get; init; } = 100;

    /// <summary>Delay between sequential catalog slot reads to avoid burst traffic.</summary>
    public int ProgramCatalogInterRequestDelayMs { get; init; } = 100;

    /// <summary>
    /// Upper bound for one read-only catalog scan. A controller may expose up to
    /// 100 slots, so the deadline keeps a stale or slow slot from making the WPF
    /// refresh appear hung while still returning the approved-name evidence.
    /// </summary>
    public int ProgramCatalogScanTimeoutMs { get; init; } = 5000;

    /// <summary>
    /// Explicitly approved controller project names. Read-only deployments may
    /// leave this empty and populate the list only after the controller catalog
    /// has been observed and the site operator has approved the exact names.
    /// </summary>
    public IReadOnlyList<string> AllowedProgramNames { get; init; } = [];

    /// <summary>Whether RunProgram requires Automatic operational mode (fail closed by default).</summary>
    public bool RequireAutomaticModeForProgramRun { get; init; } = true;

    /// <summary>Named variable the control centre writes; the Lua project branches on it.</summary>
    public string CommandVariableKey { get; init; } = "mes_cmd";

    /// <summary>Monotonic sequence written alongside the command so a repeat code is still a new request.</summary>
    public string SequenceVariableKey { get; init; } = "mes_seq";

    /// <summary>Sequence the Lua project echoes once it has taken the command.</summary>
    public string AcknowledgeVariableKey { get; init; } = "mes_ack";

    /// <summary>Result code the Lua project publishes on completion.</summary>
    public string ResultVariableKey { get; init; } = "mes_result";

    /// <summary>Optional free-text detail the Lua project may publish next to the result code.</summary>
    public string ResultDetailVariableKey { get; init; } = "mes_result_detail";

    /// <summary>Lua project expected in ${ARCS_WS}/program; empty means "do not assert".</summary>
    public string ExpectedProgramName { get; init; } = string.Empty;

    public int HandshakePollIntervalMs { get; init; } = 200;

    public int HandshakeTimeoutMs { get; init; } = 60000;

    /// <summary>
    /// Controller-side watchdog on the sequence variable. If the control centre stops
    /// refreshing it, the controller performs <see cref="WatchDogAction"/> by itself and
    /// deletes the watchdog, so a dead MES cannot leave the arm holding a stale command.
    /// Vendor minimum is 0.1 s.
    /// </summary>
    public double WatchDogTimeoutSeconds { get; init; } = 10.0;

    /// <summary>ENUM: NONE 0, PAUSE 1, STOP 2, PROTECTIVE_STOP 3. Default STOP.</summary>
    public int WatchDogAction { get; init; } = 2;

    /// <summary>
    /// Lua command branches that the control centre may dispatch. The supplied
    /// field program contains branches 1..5; deployments can narrow this list.
    /// </summary>
    public IReadOnlyList<int> AllowedCommandCodes { get; init; } = [1, 2, 3, 4, 5];

    /// <summary>
    /// Named variables the read-only surface may read. Keys outside this set are rejected
    /// so an operator cannot use the status endpoint to sweep the controller's store.
    /// </summary>
    public IReadOnlyList<string> ReadableVariableKeys { get; init; } = [];

    public IReadOnlyList<string> BuildReadableVariableAllowlist() =>
        new[]
            {
                CommandVariableKey,
                SequenceVariableKey,
                AcknowledgeVariableKey,
                ResultVariableKey,
                ResultDetailVariableKey
            }
            .Concat(ReadableVariableKeys)
            .Select(key => key?.Trim() ?? string.Empty)
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public static AuboArmOptions BindAndValidate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(SectionName).Get<AuboArmOptions>() ?? new AuboArmOptions();
        // Read the indexed list explicitly. ConfigurationBinder can materialize an
        // IReadOnlyList<int> as a zero-filled array when a sibling test host overlays
        // only part of the section; the child values are the authoritative source.
        var commandCodesSection = configuration.GetSection($"{SectionName}:AllowedCommandCodes");
        var commandCodeChildren = commandCodesSection.GetChildren().ToArray();
        if (commandCodeChildren.Length == 0)
        {
            options = options with { AllowedCommandCodes = [1, 2, 3, 4, 5] };
        }
        else
        {
            var parsedCommandCodes = commandCodeChildren
                .OrderBy(child => child.Key, StringComparer.Ordinal)
                .Select(child => int.TryParse(child.Value, out var code)
                    ? code
                    : throw new InvalidOperationException(
                        $"{SectionName}:AllowedCommandCodes contains a non-integer value."))
                .ToArray();
            options = options with { AllowedCommandCodes = parsedCommandCodes };
        }

        var programNamesSection = configuration.GetSection($"{SectionName}:AllowedProgramNames");
        var programNameChildren = programNamesSection.GetChildren().ToArray();
        if (programNameChildren.Length > 0)
        {
            options = options with
            {
                AllowedProgramNames = programNameChildren
                    .OrderBy(child => child.Key, StringComparer.Ordinal)
                    .Select(child => child.Value?.Trim() ?? string.Empty)
                    .Where(value => value.Length > 0)
                    .ToArray()
            };
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.DeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RobotName);

        var normalizedDriver = options.Driver?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedDriver is not ("websocket" or "simulator"))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Driver must be 'websocket' or 'simulator'.");
        }

        if (options.RequestTimeoutMs is < 100 or > 60000)
        {
            throw new InvalidOperationException(
                $"{SectionName}:RequestTimeoutMs must be between 100 and 60000.");
        }

        if (options.ConnectTimeoutMs is < 100 or > 60000)
        {
            throw new InvalidOperationException(
                $"{SectionName}:ConnectTimeoutMs must be between 100 and 60000.");
        }

        if (options.DashboardPort is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"{SectionName}:DashboardPort must be between 1 and 65535.");
        }

        if (options.DashboardTimeoutMs is < 100 or > 60000)
        {
            throw new InvalidOperationException(
                $"{SectionName}:DashboardTimeoutMs must be between 100 and 60000.");
        }

        if (options.ProgramOperationTimeoutMs is < 100 or > 600000)
        {
            throw new InvalidOperationException(
                $"{SectionName}:ProgramOperationTimeoutMs must be between 100 and 600000.");
        }

        if (options.ProgramCatalogMaxSlots is < 1 or > 100)
        {
            throw new InvalidOperationException(
                $"{SectionName}:ProgramCatalogMaxSlots must be between 1 and 100.");
        }

        if (options.ProgramCatalogInterRequestDelayMs is < 0 or > 5000)
        {
            throw new InvalidOperationException(
                $"{SectionName}:ProgramCatalogInterRequestDelayMs must be between 0 and 5000.");
        }

        if (options.ProgramCatalogScanTimeoutMs is < 1000 or > 120000)
        {
            throw new InvalidOperationException(
                $"{SectionName}:ProgramCatalogScanTimeoutMs must be between 1000 and 120000.");
        }

        if (options.MaximumMessageBytes is < 1024 or > 16 * 1024 * 1024)
        {
            throw new InvalidOperationException(
                $"{SectionName}:MaximumMessageBytes must be between 1024 and 16777216.");
        }

        if (string.IsNullOrWhiteSpace(options.WebSocketPath)
            || !options.WebSocketPath.TrimStart().StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{SectionName}:WebSocketPath must be an absolute WebSocket path.");
        }

        if (options.HandshakePollIntervalMs is < 50 or > 5000)
        {
            throw new InvalidOperationException(
                $"{SectionName}:HandshakePollIntervalMs must be between 50 and 5000.");
        }

        if (options.HandshakeTimeoutMs < options.HandshakePollIntervalMs * 2
            || options.HandshakeTimeoutMs > 600000)
        {
            throw new InvalidOperationException(
                $"{SectionName}:HandshakeTimeoutMs must be at least twice the poll interval and at most 600000.");
        }

        if (options.WatchDogTimeoutSeconds is < 0.1 or > 60)
        {
            throw new InvalidOperationException(
                $"{SectionName}:WatchDogTimeoutSeconds must be between the vendor minimum 0.1 and 60.");
        }

        if (options.WatchDogAction is < 0 or > 3)
        {
            throw new InvalidOperationException(
                $"{SectionName}:WatchDogAction must be NONE 0, PAUSE 1, STOP 2, or PROTECTIVE_STOP 3.");
        }

        if (options.AllowedCommandCodes is null || options.AllowedCommandCodes.Count == 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:AllowedCommandCodes must contain at least one Lua command code.");
        }

        if (options.AllowedCommandCodes.Any(code => code < 1 || code > 100000)
            || options.AllowedCommandCodes.Distinct().Count() != options.AllowedCommandCodes.Count)
        {
            throw new InvalidOperationException(
                $"{SectionName}:AllowedCommandCodes must contain distinct values between 1 and 100000.");
        }

        if (options.ControlEnabled)
        {
            if (!options.Enabled)
            {
                throw new InvalidOperationException(
                    $"{SectionName}:ControlEnabled requires Enabled=true.");
            }

            if (AdapterRunMode.Parse(configuration["Adapter:RunMode"]).IsReadOnlyPreflight)
            {
                throw new InvalidOperationException(
                    $"{SectionName}:ControlEnabled cannot be used with Adapter:RunMode=read-only-preflight.");
            }
        }

        if (options.EnableLegacyHandshake && !options.ControlEnabled)
        {
            throw new InvalidOperationException(
                $"{SectionName}:EnableLegacyHandshake requires ControlEnabled=true.");
        }

        if (options.Enabled)
        {
            // The in-process simulator deliberately has no controller endpoint.
            if (!normalizedDriver.Equals("simulator", StringComparison.Ordinal))
            {
                // Reads open a socket to the controller, so an enabled device needs a real endpoint.
                ArgumentException.ThrowIfNullOrWhiteSpace(options.Host);
            }
            if (options.Port is < 1 or > 65535)
            {
                throw new InvalidOperationException(
                    $"{SectionName}:Port must be the site-confirmed JSON-RPC port between 1 and 65535.");
            }
        }

        var duplicateKey = new[]
            {
                options.CommandVariableKey,
                options.SequenceVariableKey,
                options.AcknowledgeVariableKey,
                options.ResultVariableKey,
                options.ResultDetailVariableKey
            }
            .Select(key => key?.Trim() ?? string.Empty)
            .GroupBy(key => key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Key.Length == 0 || group.Count() > 1);
        if (duplicateKey is not null)
        {
            throw new InvalidOperationException(
                $"{SectionName} handshake variable keys must be non-empty and distinct.");
        }

        var normalizedProgramNames = (options.AllowedProgramNames ?? [])
            .Select(NormalizeProgramName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (options.ControlEnabled && normalizedProgramNames.Length == 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:AllowedProgramNames must contain at least one non-empty project name.");
        }

        return options with
        {
            DeviceId = options.DeviceId.Trim(),
            Driver = normalizedDriver,
            Host = options.Host.Trim(),
            RobotName = options.RobotName.Trim(),
            WebSocketPath = NormalizeWebSocketPath(options.WebSocketPath),
            CommandVariableKey = options.CommandVariableKey.Trim(),
            SequenceVariableKey = options.SequenceVariableKey.Trim(),
            AcknowledgeVariableKey = options.AcknowledgeVariableKey.Trim(),
            ResultVariableKey = options.ResultVariableKey.Trim(),
            ResultDetailVariableKey = options.ResultDetailVariableKey.Trim(),
            ExpectedProgramName = string.IsNullOrWhiteSpace(options.ExpectedProgramName)
                ? string.Empty
                : NormalizeProgramName(options.ExpectedProgramName),
            AllowedCommandCodes = options.AllowedCommandCodes.Distinct().ToArray(),
            AllowedProgramNames = normalizedProgramNames
        };
    }

    /// <summary>
    /// Removes only the display-file suffix used by the teach pendant.  Path
    /// components and other punctuation are rejected by the program controller.
    /// </summary>
    public static string NormalizeProgramName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.EndsWith(".pro", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];
        else if (normalized.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];
        normalized = normalized.Trim();
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

        return normalized;
    }

    private static string NormalizeWebSocketPath(string value)
    {
        var normalized = value.Trim();
        if (!normalized.StartsWith("/", StringComparison.Ordinal)) normalized = "/" + normalized;
        return normalized.Length == 0 ? "/" : normalized;
    }
}
