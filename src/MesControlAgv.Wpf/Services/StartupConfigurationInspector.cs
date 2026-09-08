using System.IO;
using System.Text.Json;

namespace MesControlAgv.Wpf.Services;

public enum StartupDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record StartupDiagnosticItem(
    string Code,
    string Name,
    string Value,
    StartupDiagnosticSeverity Severity,
    string Detail)
{
    public string SeverityText => Severity switch
    {
        StartupDiagnosticSeverity.Info => "正常",
        StartupDiagnosticSeverity.Warning => "警告",
        StartupDiagnosticSeverity.Error => "错误",
        _ => "未知"
    };
}

public sealed record StartupConfigurationInput
{
    public string BaseDirectory { get; init; } = AppContext.BaseDirectory;
    public string? RuntimeMode { get; init; }
    public string? ManageLocalServices { get; init; }
    public string? ManageLocalMes { get; init; }
    public string? MesBaseUrl { get; init; }
    public string? SimulatorBaseUrl { get; init; }
    public string? AdapterBaseUrl { get; init; }
    public string? AdapterConfigurationPath { get; init; }
    public string? InstrumentGatewayConfigurationPath { get; init; }
    public string? MapSmapPath { get; init; }
    public string? WorkflowStorePath { get; init; }
    public string? ShineLabInboxPath { get; init; }
}

public sealed record StartupConfigurationReport
{
    public required string DiagnosticRuleVersion { get; init; }
    public required string RuntimeMode { get; init; }
    public required bool ManageLocalServices { get; init; }
    public required bool ManageLocalMes { get; init; }
    public required Uri MesBaseUrl { get; init; }
    public required Uri SimulatorBaseUrl { get; init; }
    public required Uri AdapterBaseUrl { get; init; }
    public required string AdapterDriver { get; init; }
    public required string AdapterRunMode { get; init; }
    public required string RealWriteAccess { get; init; }
    public required IReadOnlyList<StartupDiagnosticItem> Items { get; init; }

    public bool HasErrors => Items.Any(item => item.Severity == StartupDiagnosticSeverity.Error);
    public bool HasWarnings => Items.Any(item => item.Severity == StartupDiagnosticSeverity.Warning);
    public bool CanStart => !HasErrors;
    public string OverallStatus => HasErrors
        ? "配置检查失败"
        : HasWarnings
            ? "可启动，但有警告"
            : "配置检查通过";

    public static StartupConfigurationReport Unknown { get; } = new()
    {
        DiagnosticRuleVersion = OfflineDiagnosticVersions.RuleSchema,
        RuntimeMode = "unknown",
        ManageLocalServices = false,
        ManageLocalMes = false,
        MesBaseUrl = new Uri("http://127.0.0.1:5145/"),
        SimulatorBaseUrl = new Uri("http://localhost:5183/"),
        AdapterBaseUrl = new Uri("http://127.0.0.1:5141/"),
        AdapterDriver = "未检查",
        AdapterRunMode = "未检查",
        RealWriteAccess = "未检查",
        Items =
        [
            new StartupDiagnosticItem(
                "STARTUP_NOT_INSPECTED",
                "启动配置",
                "未检查",
                StartupDiagnosticSeverity.Warning,
                "当前视图模型未附带启动诊断报告。")
        ]
    };
}

/// <summary>Inspects local startup configuration without opening a socket or device port.</summary>
public static class StartupConfigurationInspector
{
    private const string DefaultPhysicalMesBaseUrl = "http://127.0.0.1:5145/";
    private const string DefaultPhysicalAdapterBaseUrl = "http://127.0.0.1:5141/";
    private const string DefaultSimulatorMesBaseUrl = "http://localhost:5045/";
    private const string DefaultSimulatorAdapterBaseUrl = "http://localhost:5041/";
    private const string DefaultSimulatorBaseUrl = "http://localhost:5183/";

    public static StartupConfigurationReport InspectEnvironment(
        string baseDirectory,
        Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        return Inspect(new StartupConfigurationInput
        {
            BaseDirectory = baseDirectory,
            RuntimeMode = readEnvironment("WPF_RUNTIME_MODE"),
            ManageLocalServices = readEnvironment("WPF_MANAGE_LOCAL_SERVICES"),
            ManageLocalMes = readEnvironment("WPF_MANAGE_LOCAL_MES"),
            MesBaseUrl = readEnvironment("MES_BASE_URL"),
            SimulatorBaseUrl = readEnvironment("SIMULATOR_BASE_URL"),
            AdapterBaseUrl = readEnvironment("ADAPTER_BASE_URL"),
            AdapterConfigurationPath = readEnvironment("WPF_ADAPTER_CONFIG_PATH"),
            InstrumentGatewayConfigurationPath = readEnvironment("WPF_INSTRUMENT_GATEWAY_CONFIG_PATH"),
            MapSmapPath = readEnvironment("MAP_SMAP_PATH"),
            WorkflowStorePath = readEnvironment("WPF_WORKFLOW_STORE_PATH"),
            ShineLabInboxPath = readEnvironment("SHINELAB_INBOX_PATH")
        });
    }

    public static StartupConfigurationReport Inspect(StartupConfigurationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var items = new List<StartupDiagnosticItem>();
        var runtimeMode = NormalizeRuntimeMode(input.RuntimeMode, items);
        var mesFallback = runtimeMode.Equals("simulator", StringComparison.OrdinalIgnoreCase)
            ? DefaultSimulatorMesBaseUrl
            : DefaultPhysicalMesBaseUrl;
        var adapterFallback = runtimeMode.Equals("simulator", StringComparison.OrdinalIgnoreCase)
            ? DefaultSimulatorAdapterBaseUrl
            : DefaultPhysicalAdapterBaseUrl;
        var mesUrl = ParseUrl("MES_BASE_URL", input.MesBaseUrl, mesFallback, items);
        var simulatorUrl = ParseUrl("SIMULATOR_BASE_URL", input.SimulatorBaseUrl, DefaultSimulatorBaseUrl, items);
        var adapterUrl = ParseUrl("ADAPTER_BASE_URL", input.AdapterBaseUrl, adapterFallback, items);
        var manageLocalServices = ResolveLocalServiceManagement(
            runtimeMode,
            input.ManageLocalServices,
            simulatorUrl,
            adapterUrl,
            mesUrl,
            items);
        var manageLocalMes = ResolveLocalMesManagement(
            runtimeMode,
            input.ManageLocalMes,
            mesUrl,
            items);

        string? adapterConfigurationPath = null;
        try
        {
            adapterConfigurationPath = ResolveAdapterConfigurationPath(input, runtimeMode, manageLocalServices);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            items.Add(Error(
                "ADAPTER_CONFIG_PATH_INVALID",
                "Adapter 配置",
                "无效路径",
                $"WPF_ADAPTER_CONFIG_PATH 无法解析：{exception.Message}"));
        }
        var adapter = InspectAdapterConfiguration(adapterConfigurationPath, runtimeMode, items);
        string? instrumentConfigurationPath = null;
        try
        {
            instrumentConfigurationPath = ResolveOptionalConfigurationPath(
                input.InstrumentGatewayConfigurationPath,
                input.BaseDirectory,
                Path.Combine("services", "InstrumentGateway", "appsettings.json"));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            items.Add(Error(
                "INSTRUMENT_GATEWAY_CONFIG_PATH_INVALID",
                "仪器网关配置",
                "无效路径",
                $"WPF_INSTRUMENT_GATEWAY_CONFIG_PATH 无法解析：{exception.Message}"));
        }
        InspectInstrumentGatewayConfiguration(instrumentConfigurationPath, items);
        AddOptionalPathDiagnostic("MAP_SMAP_PATH", "SMAP 地图", input.MapSmapPath, items);
        AddOptionalPathDiagnostic("WPF_WORKFLOW_STORE_PATH", "流程存储", input.WorkflowStorePath, items);
        AddOptionalPathDiagnostic("SHINELAB_INBOX_PATH", "ShineLab 收件目录", input.ShineLabInboxPath, items);

        return new StartupConfigurationReport
        {
            DiagnosticRuleVersion = OfflineDiagnosticVersions.RuleSchema,
            RuntimeMode = runtimeMode,
            ManageLocalServices = manageLocalServices,
            ManageLocalMes = manageLocalMes,
            MesBaseUrl = mesUrl,
            SimulatorBaseUrl = simulatorUrl,
            AdapterBaseUrl = adapterUrl,
            AdapterDriver = adapter.Driver,
            AdapterRunMode = adapter.RunMode,
            RealWriteAccess = adapter.RealWriteAccess,
            Items = items
        };
    }

    private static string NormalizeRuntimeMode(string? configured, ICollection<StartupDiagnosticItem> items)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? "physical" : configured.Trim().ToLowerInvariant();
        if (value is not ("simulator" or "physical"))
        {
            items.Add(Error(
                "WPF_RUNTIME_MODE_INVALID",
                "运行模式",
                configured ?? string.Empty,
                "WPF_RUNTIME_MODE 只能设置为 simulator 或 physical。"));
            return "physical";
        }

        items.Add(Info("WPF_RUNTIME_MODE", "运行模式", value, value == "simulator"
            ? "本次启动只使用模拟器能力。"
            : "物理模式不会自动获得现场动作授权。"));
        return value;
    }

    private static Uri ParseUrl(
        string variableName,
        string? configured,
        string fallback,
        ICollection<StartupDiagnosticItem> items)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            items.Add(Error(
                $"{variableName}_INVALID",
                variableName,
                "无效",
                $"{variableName} 必须是不含凭据的绝对 HTTP(S) 地址。"));
            return new Uri(fallback);
        }

        var normalized = new Uri($"{uri.AbsoluteUri.TrimEnd('/')}/", UriKind.Absolute);
        items.Add(Info(variableName, variableName, normalized.AbsoluteUri, "仅完成格式检查，未尝试连接。"));
        return normalized;
    }

    private static bool ResolveLocalServiceManagement(
        string runtimeMode,
        string? configured,
        Uri simulatorUrl,
        Uri adapterUrl,
        Uri mesUrl,
        ICollection<StartupDiagnosticItem> items)
    {
        var hasConfiguredValue = !string.IsNullOrWhiteSpace(configured);
        var explicitlyEnabled = bool.TryParse(configured, out var parsed) && parsed;
        bool manage;
        if (hasConfiguredValue && !bool.TryParse(configured, out _))
        {
            items.Add(Error(
                "WPF_MANAGE_LOCAL_SERVICES_INVALID",
                "本地服务托管",
                configured!,
                "WPF_MANAGE_LOCAL_SERVICES 必须是 true 或 false。"));
            manage = false;
        }
        else
        {
            try
            {
                manage = LocalSimulatorRuntime.ShouldManageLocalServices(
                    runtimeMode,
                    simulatorUrl,
                    adapterUrl,
                    mesUrl,
                    configured);
            }
            catch (InvalidOperationException exception)
            {
                items.Add(Error(
                    "LOCAL_SERVICES_REQUIRE_LOOPBACK",
                    "本地服务托管",
                    configured ?? "自动",
                    exception.Message));
                manage = false;
            }
        }

        if (runtimeMode != "simulator" && explicitlyEnabled)
        {
            items.Add(Warning(
                "LOCAL_SERVICES_IGNORED_IN_PHYSICAL",
                "本地服务托管",
                "已禁用",
                "physical 模式忽略本地模拟服务托管设置。"));
        }

        items.Add(Info(
            "WPF_MANAGE_LOCAL_SERVICES",
            "本地服务托管",
            manage ? "启用" : "禁用",
            manage ? "启动后将管理本机模拟服务进程。" : "启动后只连接已存在的服务。"));
        return manage;
    }

    private static bool ResolveLocalMesManagement(
        string runtimeMode,
        string? configured,
        Uri mesUrl,
        ICollection<StartupDiagnosticItem> items)
    {
        if (!string.IsNullOrWhiteSpace(configured) && !bool.TryParse(configured, out _))
        {
            items.Add(Error(
                "WPF_MANAGE_LOCAL_MES_INVALID",
                "本机 MES 托管",
                configured!,
                "WPF_MANAGE_LOCAL_MES 必须是 true 或 false。"));
            return false;
        }

        var manage = string.IsNullOrWhiteSpace(configured)
            ? runtimeMode.Equals("physical", StringComparison.OrdinalIgnoreCase) && mesUrl.IsLoopback
            : bool.Parse(configured!);

        if (manage && !mesUrl.IsLoopback)
        {
            items.Add(Error(
                "LOCAL_MES_REQUIRE_LOOPBACK",
                "本机 MES 托管",
                mesUrl.AbsoluteUri,
                "WPF 只能托管本机回环地址上的 MES；远程 MES 地址必须关闭 WPF_MANAGE_LOCAL_MES。"));
            manage = false;
        }

        items.Add(Info(
            "WPF_MANAGE_LOCAL_MES",
            "本机 MES 托管",
            manage ? "启用" : "禁用",
            manage ? "启动后检查并按需启动本机 MES。" : "启动后只连接配置的 MES 服务。"));
        return manage;
    }

    private static string? ResolveAdapterConfigurationPath(
        StartupConfigurationInput input,
        string runtimeMode,
        bool manageLocalServices)
    {
        if (!string.IsNullOrWhiteSpace(input.AdapterConfigurationPath))
        {
            return Path.GetFullPath(Path.IsPathRooted(input.AdapterConfigurationPath)
                ? input.AdapterConfigurationPath
                : Path.Combine(input.BaseDirectory, input.AdapterConfigurationPath));
        }

        if (runtimeMode == "simulator" && manageLocalServices)
        {
            var basePath = Path.Combine(
                Path.GetFullPath(input.BaseDirectory),
                "services",
                "Adapter");
            var configuredEnvironment = Environment.GetEnvironmentVariable("WPF_LOCAL_SERVICE_ENVIRONMENT");
            var preferredFileName = string.Equals(
                configuredEnvironment?.Trim(),
                "Development",
                StringComparison.OrdinalIgnoreCase)
                ? "appsettings.Development.json"
                : "appsettings.FieldSimulation.json";
            var candidate = Path.Combine(basePath, preferredFileName);
            if (File.Exists(candidate)) return candidate;

            // Preserve compatibility with older published bundles that do not
            // yet contain the field-simulation file.
            var fallback = Path.Combine(
                basePath,
                preferredFileName.Equals("appsettings.Development.json", StringComparison.Ordinal)
                    ? "appsettings.FieldSimulation.json"
                    : "appsettings.Development.json");
            return File.Exists(fallback) ? fallback : null;
        }

        return null;
    }

    private static string? ResolveOptionalConfigurationPath(
        string? configuredPath,
        string baseDirectory,
        string relativeCandidate)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(baseDirectory, configuredPath));
        }

        var candidate = Path.Combine(Path.GetFullPath(baseDirectory), relativeCandidate);
        return File.Exists(candidate) ? candidate : null;
    }

    private static AdapterInspection InspectAdapterConfiguration(
        string? configurationPath,
        string runtimeMode,
        ICollection<StartupDiagnosticItem> items)
    {
        var driver = runtimeMode == "simulator" ? "simulator（运行模式推断）" : "未声明";
        var runMode = runtimeMode == "simulator" ? "standard（模拟器）" : "未声明";
        var configurationLoaded = false;
        var deviceModulesInspected = false;
        var visionModuleInspected = false;
        if (!string.IsNullOrWhiteSpace(configurationPath))
        {
            if (!File.Exists(configurationPath))
            {
                items.Add(Warning(
                    "ADAPTER_CONFIG_MISSING",
                    "Adapter 配置",
                    configurationPath,
                    "未找到配置文件，无法离线确认真实驱动和写入策略。"));
            }
            else
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(configurationPath));
                    driver = ReadString(document.RootElement, "Agv", "Driver") ?? driver;
                    runMode = ReadString(document.RootElement, "Adapter", "RunMode") ?? "standard";
                    InspectAdapterDeviceModules(document.RootElement, runtimeMode, runMode, items);
                    InspectAgvPreflightSettings(document.RootElement, runMode, items);
                    InspectVisionModule(document.RootElement, items);
                    deviceModulesInspected = true;
                    visionModuleInspected = true;
                    configurationLoaded = true;
                    items.Add(Info(
                        "ADAPTER_CONFIG",
                        "Adapter 配置",
                        configurationPath,
                        "已完成本地 JSON 解析，未启动 Adapter。"));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    items.Add(Error(
                        "ADAPTER_CONFIG_INVALID",
                        "Adapter 配置",
                        configurationPath,
                        $"Adapter 配置无法解析：{exception.Message}"));
                }
            }
        }
        else if (runtimeMode == "physical")
        {
            items.Add(Warning(
                "ADAPTER_CONFIG_NOT_DECLARED",
                "Adapter 配置",
                "未声明",
                "physical 模式建议设置 WPF_ADAPTER_CONFIG_PATH，以便启动前确认驱动和写入策略。"));
        }

        if (!deviceModulesInspected)
        {
            items.Add(Warning("AUBO_ARM_MODULE", "机械臂模块", "未检查", "未读取 Adapter 配置，无法确认机械臂模块状态。"));
            items.Add(Warning("SAMPLE_WORKSTATION_MODULE", "样品工作站模块", "未检查", "未读取 Adapter 配置，无法确认工作站模块状态。"));
        }
        if (!visionModuleInspected) InspectVisionModule(null, items);

        var realWriteAccess = runtimeMode == "simulator"
            ? "禁用（仅模拟器）"
            : runMode.Equals("read-only-preflight", StringComparison.OrdinalIgnoreCase)
                ? "禁用（read-only-preflight）"
                : configurationLoaded
                    ? "可能开放（仍需现场授权）"
                    : "未知（未读取 Adapter 配置）";

        if (runtimeMode == "physical" && driver.Equals("simulator", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(Error(
                "PHYSICAL_MODE_SIMULATOR_DRIVER",
                "AGV 驱动",
                driver,
                "physical 模式不能使用 simulator 驱动。"));
        }

        var severity = realWriteAccess.StartsWith("禁用", StringComparison.Ordinal)
            ? StartupDiagnosticSeverity.Info
            : StartupDiagnosticSeverity.Warning;
        items.Add(new StartupDiagnosticItem(
            "REAL_WRITE_ACCESS",
            "真实写入入口",
            realWriteAccess,
            severity,
            severity == StartupDiagnosticSeverity.Info
                ? "配置层未开放真实设备写入。"
                : "此结果不构成现场动作授权；执行前仍须只读预检和书面许可。"));
        items.Add(Info("ADAPTER_DRIVER", "AGV 驱动", driver, "驱动名称来自本地配置或运行模式推断。"));
        items.Add(Info("ADAPTER_RUN_MODE", "Adapter 运行模式", runMode, "运行模式仅做静态检查。"));

        return new AdapterInspection(driver, runMode, realWriteAccess);
    }

    private static void InspectAgvPreflightSettings(
        JsonElement root,
        string adapterRunMode,
        ICollection<StartupDiagnosticItem> items)
    {
        var acquireControl = ReadBool(root, "Agv", "Tcp", "AcquireControl");
        var enablePush = ReadBool(root, "Agv", "Tcp", "EnablePush");
        var minimumConfidence = ReadDouble(root, "Agv", "Tcp", "MinimumConfidence");
        var readOnly = adapterRunMode.Equals("read-only-preflight", StringComparison.OrdinalIgnoreCase);

        AddAgvGate("AGV_ACQUIRE_CONTROL", "AGV 控制权申请", acquireControl, readOnly, items);
        AddAgvGate("AGV_PUSH", "AGV Push", enablePush, readOnly, items);
        if (minimumConfidence is { } confidence)
        {
            items.Add(Info(
                "AGV_MINIMUM_CONFIDENCE",
                "最低定位置信度",
                confidence.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "数值仅作离线配置记录；现场必须按批准参数和实时预检复核。"));
        }
        else
        {
            items.Add(Warning(
                "AGV_MINIMUM_CONFIDENCE",
                "最低定位置信度",
                "未配置",
                "现场只读预检必须取得新鲜定位置信度。"));
        }
    }

    private static void AddAgvGate(
        string code,
        string name,
        bool? enabled,
        bool readOnlyRunMode,
        ICollection<StartupDiagnosticItem> items)
    {
        if (enabled is not { } value)
        {
            items.Add(Warning(code, name, "未确认", "配置未提供该字段，现场只读预检必须明确记录。"));
            return;
        }

        var severity = readOnlyRunMode && value
            ? StartupDiagnosticSeverity.Error
            : StartupDiagnosticSeverity.Info;
        var display = value ? "启用" : "关闭";
        var detail = readOnlyRunMode
            ? value
                ? "read-only-preflight 要求该开关关闭。"
                : "符合只读预检静态基线；仍需现场记录实际控制权。"
            : "该值不能替代现场授权或只读预检证据。";
        items.Add(new StartupDiagnosticItem(code, name, display, severity, detail));
    }

    private static void InspectAdapterDeviceModules(
        JsonElement root,
        string runtimeMode,
        string adapterRunMode,
        ICollection<StartupDiagnosticItem> items)
    {
        if (!TryGetPath(root, out _, "Devices", "AuboArm"))
        {
            items.Add(Warning("AUBO_ARM_MODULE", "机械臂模块", "未配置", "Adapter 配置中缺少 Devices:AuboArm。"));
        }
        else
        {
            var enabled = ReadBool(root, "Devices", "AuboArm", "Enabled") ?? false;
            var controlEnabled = ReadBool(root, "Devices", "AuboArm", "ControlEnabled") ?? false;
            var driver = ReadString(root, "Devices", "AuboArm", "Driver") ?? "websocket";
            var host = ReadString(root, "Devices", "AuboArm", "Host") ?? string.Empty;
            var value = enabled
                ? controlEnabled
                    ? driver.Equals("simulator", StringComparison.OrdinalIgnoreCase)
                        ? "启用（本地模拟控制）"
                        : "启用（控制开放）"
                    : "启用（只读）"
                : "禁用";
            var severity = controlEnabled && !driver.Equals("simulator", StringComparison.OrdinalIgnoreCase)
                ? StartupDiagnosticSeverity.Warning
                : StartupDiagnosticSeverity.Info;
            items.Add(new StartupDiagnosticItem(
                "AUBO_ARM_MODULE",
                "机械臂模块",
                value,
                severity,
                controlEnabled && driver.Equals("simulator", StringComparison.OrdinalIgnoreCase)
                    ? "仅使用进程内 AUBO 模拟器；不会连接现场控制器。"
                    : controlEnabled
                        ? "机械臂命令握手已配置；仍需现场授权和 Lua 看门狗验证。"
                    : enabled ? "仅开放只读机械臂状态。" : "机械臂模块不会建立控制器连接。"));

            if (controlEnabled && !enabled)
            {
                items.Add(Error("AUBO_CONTROL_REQUIRES_ENABLED", "机械臂控制", "配置冲突", "ControlEnabled=true 要求 Enabled=true。"));
            }
            if (controlEnabled && adapterRunMode.Equals("read-only-preflight", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(Error("AUBO_CONTROL_BLOCKED_BY_RUN_MODE", "机械臂控制", "配置冲突", "read-only-preflight 禁止机械臂写入。"));
            }
            if (enabled && !driver.Equals("simulator", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(host))
            {
                items.Add(Error("AUBO_HOST_REQUIRED", "机械臂端点", "缺失", "机械臂启用时必须提供现场确认的 Host。"));
            }
            if (!driver.Equals("websocket", StringComparison.OrdinalIgnoreCase) &&
                !driver.Equals("simulator", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(Error("AUBO_DRIVER_INVALID", "机械臂驱动", driver, "Driver 必须是 websocket 或 simulator。"));
            }
            else if (driver.Equals("simulator", StringComparison.OrdinalIgnoreCase) &&
                     !runtimeMode.Equals("simulator", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(Error(
                    "AUBO_SIMULATOR_FORBIDDEN_IN_PHYSICAL",
                    "机械臂驱动",
                    driver,
                    "physical 模式禁止使用本地 AUBO 模拟器；请恢复现场 WebSocket 配置并重新完成只读预检。"));
            }
        }

        if (!TryGetPath(root, out _, "Devices", "SampleWorkstation"))
        {
            items.Add(Warning("SAMPLE_WORKSTATION_MODULE", "样品工作站模块", "未配置", "Adapter 配置中缺少 Devices:SampleWorkstation。"));
            return;
        }

        var workstationEnabled = ReadBool(root, "Devices", "SampleWorkstation", "Enabled") ?? false;
        var workstationControlEnabled = ReadBool(root, "Devices", "SampleWorkstation", "ControlEnabled") ?? false;
        var equipmentNo = ReadString(root, "Devices", "SampleWorkstation", "EquipmentNo") ?? string.Empty;
        var workstationBaseUrl = ReadString(root, "Devices", "SampleWorkstation", "BaseUrl") ?? string.Empty;
        items.Add(new StartupDiagnosticItem(
            "SAMPLE_WORKSTATION_MODULE",
            "样品工作站模块",
            workstationEnabled ? "启用（只读）" : "禁用",
            workstationControlEnabled ? StartupDiagnosticSeverity.Error : StartupDiagnosticSeverity.Info,
            workstationEnabled ? "工作站模块仅允许读取厂商接口。" : "工作站模块不会连接厂商服务。"));
        if (workstationControlEnabled)
        {
            items.Add(Error("WORKSTATION_CONTROL_FORBIDDEN", "样品工作站控制", "配置冲突", "当前工作站模块必须保持 ControlEnabled=false。"));
        }
        if (workstationEnabled && string.IsNullOrWhiteSpace(equipmentNo))
        {
            items.Add(Error("WORKSTATION_EQUIPMENT_REQUIRED", "工作站设备号", "缺失", "工作站启用时必须配置 EquipmentNo。"));
        }
        if (!string.IsNullOrWhiteSpace(workstationBaseUrl) &&
            (!Uri.TryCreate(workstationBaseUrl, UriKind.Absolute, out var workstationUri) ||
             workstationUri.Scheme is not ("http" or "https")))
        {
            items.Add(Error("WORKSTATION_URL_INVALID", "工作站 BaseUrl", "无效", "BaseUrl 必须是绝对 HTTP(S) 地址。"));
        }
    }

    private static void InspectVisionModule(JsonElement? root, ICollection<StartupDiagnosticItem> items)
    {
        if (root is not { } configuredRoot || !TryGetPath(configuredRoot, out var vision, "Devices", "Vision"))
        {
            items.Add(Warning(
                "VISION_MODULE_REGISTRATION",
                "视觉模块",
                "未注册",
                "当前 AdapterCompositionRoot 未注册视觉模块；MockVisionDriver 仅用于进程内测试。"));
            return;
        }

        var driver = ReadString(vision, "Driver") ?? "未声明";
        var enabled = ReadBool(vision, "Enabled") ?? false;
        var controlEnabled = ReadBool(vision, "ControlEnabled") ?? false;
        items.Add(new StartupDiagnosticItem(
            "VISION_MODULE_REGISTRATION",
            "视觉模块",
            enabled ? $"配置存在但未注册（{driver}）" : "配置存在但未启用",
            controlEnabled ? StartupDiagnosticSeverity.Error : StartupDiagnosticSeverity.Warning,
            controlEnabled
                ? "视觉控制配置存在，但当前 Adapter 没有已注册的视觉模块，必须保持关闭。"
                : "配置仅作为未来模块接入样本；当前不能进行现场视觉动作。"));
        if (controlEnabled)
        {
            items.Add(Error("VISION_CONTROL_FORBIDDEN", "视觉控制", "配置冲突", "视觉 ControlEnabled 必须保持 false，直到模块和厂商协议完成确认。"));
        }
    }

    private static void InspectInstrumentGatewayConfiguration(
        string? configurationPath,
        ICollection<StartupDiagnosticItem> items)
    {
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            items.Add(Warning(
                "INSTRUMENT_GATEWAY_CONFIG_NOT_DECLARED",
                "离子色谱网关",
                "未配置",
                "设置 WPF_INSTRUMENT_GATEWAY_CONFIG_PATH 后可在启动前校验只读网关。"));
            items.Add(Info(
                "INSTRUMENT_WRITE_ACCESS",
                "仪器真实写入入口",
                "禁用（网关未配置）",
                "当前 WPF 启动不会启用仪器写入。"));
            return;
        }

        if (!File.Exists(configurationPath))
        {
            items.Add(Warning("INSTRUMENT_GATEWAY_CONFIG_MISSING", "离子色谱网关", configurationPath, "配置文件不存在。"));
            items.Add(Info("INSTRUMENT_WRITE_ACCESS", "仪器真实写入入口", "禁用（配置缺失）", "未启用仪器网关。"));
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configurationPath));
            var root = document.RootElement;
            if (!TryGetPath(root, out _, "IonChromatography"))
            {
                items.Add(Error("ION_CONFIG_MISSING", "离子色谱网关", "配置缺失", "配置中缺少 IonChromatography 节。"));
                return;
            }

            var enabled = ReadBool(root, "IonChromatography", "Enabled") ?? false;
            var instrumentId = ReadString(root, "IonChromatography", "InstrumentId") ?? string.Empty;
            var comPort = ReadString(root, "IonChromatography", "ComPort") ?? string.Empty;
            var baudRate = ReadInt(root, "IonChromatography", "BaudRate") ?? 0;
            var dataBits = ReadInt(root, "IonChromatography", "DataBits") ?? 0;
            var timeoutMs = ReadInt(root, "IonChromatography", "TimeoutMs") ?? 0;
            var slaveAddress = ReadInt(root, "IonChromatography", "SlaveAddress") ?? 0;
            items.Add(Info(
                "INSTRUMENT_GATEWAY_CONFIG",
                "仪器网关配置",
                configurationPath,
                "已完成本地 JSON 解析，未打开串口。"));
            items.Add(Info(
                "ION_CHROMATOGRAPHY_MODULE",
                "离子色谱模块",
                enabled ? "启用（只读）" : "禁用",
                enabled ? $"{instrumentId} / {comPort} / {baudRate} baud" : "网关不会打开串口。"));
            items.Add(Info(
                "INSTRUMENT_WRITE_ACCESS",
                "仪器真实写入入口",
                "禁用（HTTP 与驱动只读）",
                "网关只接受 GET/HEAD，运行时只注入 IReadOnlyModbusTransport。"));

            if (!enabled) return;
            if (string.IsNullOrWhiteSpace(instrumentId))
                items.Add(Error("ION_INSTRUMENT_ID_REQUIRED", "仪器 ID", "缺失", "启用网关时 InstrumentId 必填。"));
            if (string.IsNullOrWhiteSpace(comPort))
                items.Add(Error("ION_COM_PORT_REQUIRED", "仪器串口", "缺失", "启用网关时 ComPort 必填。"));
            if (baudRate <= 0)
                items.Add(Error("ION_BAUD_RATE_INVALID", "仪器波特率", baudRate.ToString(), "BaudRate 必须大于 0。"));
            if (dataBits is < 5 or > 8)
                items.Add(Error("ION_DATA_BITS_INVALID", "仪器数据位", dataBits.ToString(), "DataBits 必须在 5 到 8 之间。"));
            if (timeoutMs <= 0)
                items.Add(Error("ION_TIMEOUT_INVALID", "仪器超时", timeoutMs.ToString(), "TimeoutMs 必须大于 0。"));
            if (slaveAddress is < 1 or > 255)
                items.Add(Error("ION_SLAVE_ADDRESS_INVALID", "仪器从站地址", slaveAddress.ToString(), "SlaveAddress 必须在 1 到 255 之间。"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            items.Add(Error(
                "INSTRUMENT_GATEWAY_CONFIG_INVALID",
                "离子色谱网关",
                configurationPath,
                $"仪器网关配置无法解析：{exception.Message}"));
        }
    }

    private static string? ReadString(JsonElement root, params string[] path)
    {
        return TryGetPath(root, out var value, path) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? ReadBool(JsonElement root, params string[] path) =>
        TryGetPath(root, out var value, path) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int? ReadInt(JsonElement root, params string[] path) =>
        TryGetPath(root, out var value, path) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static double? ReadDouble(JsonElement root, params string[] path) =>
        TryGetPath(root, out var value, path) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private static bool TryGetPath(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) return false;
        }

        return true;
    }

    private static void AddOptionalPathDiagnostic(
        string code,
        string name,
        string? path,
        ICollection<StartupDiagnosticItem> items)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            items.Add(Warning(code, name, "未配置", "该功能保持禁用或使用内置默认值。"));
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            items.Add(Warning(code, name, "无效路径", $"配置路径无法解析：{exception.Message}"));
            return;
        }
        var exists = File.Exists(fullPath) || Directory.Exists(fullPath);
        items.Add(exists
            ? Info(code, name, fullPath, "本地路径存在。")
            : Warning(code, name, fullPath, "配置路径不存在，相关功能不可用。"));
    }

    private static StartupDiagnosticItem Info(string code, string name, string value, string detail) =>
        new(code, name, value, StartupDiagnosticSeverity.Info, detail);

    private static StartupDiagnosticItem Warning(string code, string name, string value, string detail) =>
        new(code, name, value, StartupDiagnosticSeverity.Warning, detail);

    private static StartupDiagnosticItem Error(string code, string name, string value, string detail) =>
        new(code, name, value, StartupDiagnosticSeverity.Error, detail);

    private sealed record AdapterInspection(string Driver, string RunMode, string RealWriteAccess);
}
