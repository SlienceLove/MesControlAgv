namespace MesControlAgv.Wpf.Services;

public enum FieldPreflightInputStatus
{
    KnownOffline,
    NeedsFieldConfirmation,
    NotApplicable,
    BlockedByOfflineConfiguration
}

public sealed record FieldPreflightInputItem(
    string Code,
    string Name,
    string Value,
    FieldPreflightInputStatus Status,
    string Detail)
{
    public string StatusText => Status switch
    {
        FieldPreflightInputStatus.KnownOffline => "离线已知",
        FieldPreflightInputStatus.NeedsFieldConfirmation => "需现场确认",
        FieldPreflightInputStatus.NotApplicable => "不适用",
        FieldPreflightInputStatus.BlockedByOfflineConfiguration => "离线阻断",
        _ => "未知"
    };
}

public sealed record FieldPreflightChecklist
{
    public required IReadOnlyList<FieldPreflightInputItem> Items { get; init; }
    public bool HasOfflineBlocks => Items.Any(item => item.Status == FieldPreflightInputStatus.BlockedByOfflineConfiguration);
    public bool RequiresFieldConfirmation => Items.Any(item => item.Status == FieldPreflightInputStatus.NeedsFieldConfirmation);
    public bool OfflineConfigurationAcceptable => !HasOfflineBlocks;
    public bool CanDetermineGo => false;
    public string DecisionText => "不判定 GO（仅准备现场只读预检输入）";
    public string SummaryText => HasOfflineBlocks
        ? "存在离线配置阻断，不能进入现场预检"
        : "离线配置可供现场预检使用，仍需新鲜现场证据";
}

/// <summary>
/// Maps static offline configuration into the fields a field operator must
/// collect during a fresh read-only preflight. It deliberately never returns a GO decision.
/// </summary>
public static class FieldPreflightChecklistBuilder
{
    public static FieldPreflightChecklist Build(StartupConfigurationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var items = new List<FieldPreflightInputItem>
        {
            new(
                "RUNTIME_MODE",
                "运行模式",
                report.RuntimeMode,
                report.RuntimeMode.Equals("physical", StringComparison.OrdinalIgnoreCase)
                    ? FieldPreflightInputStatus.NeedsFieldConfirmation
                    : FieldPreflightInputStatus.NotApplicable,
                "现场必须在独立会话确认实际运行模式；离线值不能授权动作。"),
            new(
                "ADAPTER_RUN_MODE",
                "Adapter 运行模式",
                report.AdapterRunMode,
                report.AdapterRunMode.Contains("read-only-preflight", StringComparison.OrdinalIgnoreCase)
                    ? FieldPreflightInputStatus.KnownOffline
                    : FieldPreflightInputStatus.BlockedByOfflineConfiguration,
                "现场只读预检必须使用 read-only-preflight。"),
            new(
                "AGV_DRIVER",
                "AGV 驱动",
                report.AdapterDriver,
                report.RuntimeMode.Equals("physical", StringComparison.OrdinalIgnoreCase)
                    ? FieldPreflightInputStatus.NeedsFieldConfirmation
                    : FieldPreflightInputStatus.NotApplicable,
                "现场需核对控制器型号、固件和驱动身份。"),
            MapStaticGate(report, "AGV_ACQUIRE_CONTROL", "控制权申请", "现场只读预检必须记录 control owner=none，不能申请控制权。"),
            MapStaticGate(report, "AGV_PUSH", "Push", "现场验收默认关闭 Push；实际状态需在新鲜会话记录。"),
            MapStaticGate(report, "AGV_MINIMUM_CONFIDENCE", "最低定位置信度", "离线数值仅作参考，现场必须按批准阈值复核。"),
            NeedsField("CONTROLLER_IDENTITY", "控制器身份", "设备型号、序列号和固件必须现场读取并记录。"),
            NeedsField("CONTROL_OWNER", "控制权持有者", "必须现场确认当前控制权为 none；历史记录不可替代。"),
            NeedsField("CURRENT_STATION", "当前站点与空闲状态", "现场读取当前位置、活动任务和车辆静止状态。"),
            NeedsField("MAP_IDENTITY", "地图身份", "现场核对地图名称、版本、MD5、站点和有向边。"),
            NeedsField("LOCALIZATION", "定位与置信度", "现场取得定位查询和新鲜置信度证据。"),
            NeedsField("SAFETY_GATES", "急停/阻塞/告警门禁", "现场确认 emergency、blocked、fatal/error 和人工接管路径。"),
            NeedsField("AUTHORIZATION", "许可与安全监护", "现场填写唯一 permit/task ID、操作员、安全观察员和过期时间。"),
            NeedsField("SINGLE_SEGMENT", "单段路线计划", "现场按权威有向图逐段执行，不自动重试或推断反向边。")
        };

        if (report.HasErrors)
        {
            items.Insert(
                0,
                new FieldPreflightInputItem(
                    "STARTUP_CONFIGURATION",
                    "启动配置错误",
                    "存在错误",
                    FieldPreflightInputStatus.BlockedByOfflineConfiguration,
                    "启动诊断存在错误项；先修正本地配置并重新生成清单，再安排现场只读预检。"));
        }

        return new FieldPreflightChecklist { Items = items };
    }

    private static FieldPreflightInputItem MapStaticGate(
        StartupConfigurationReport report,
        string code,
        string name,
        string detail)
    {
        var item = report.Items.FirstOrDefault(candidate => candidate.Code == code);
        if (item is null)
        {
            return NeedsField(code, name, detail);
        }

        // A read-only preflight must keep mutation gates closed regardless of
        // whether the source profile itself is labelled standard. Treat an
        // explicitly open gate as an offline block instead of deferring it to
        // the field operator.
        if (code is ("AGV_ACQUIRE_CONTROL" or "AGV_PUSH") && IsOpen(item.Value))
        {
            return new FieldPreflightInputItem(
                code,
                name,
                item.Value,
                FieldPreflightInputStatus.BlockedByOfflineConfiguration,
                "只读预检要求该写入开关在离线配置中关闭；请先修正配置再安排现场确认。");
        }

        return item.Severity == StartupDiagnosticSeverity.Error
            ? new FieldPreflightInputItem(code, name, item.Value, FieldPreflightInputStatus.BlockedByOfflineConfiguration, item.Detail)
            : new FieldPreflightInputItem(
                code,
                name,
                item.Value,
                IsClosed(item.Value)
                    ? FieldPreflightInputStatus.KnownOffline
                    : FieldPreflightInputStatus.NeedsFieldConfirmation,
                detail);
    }

    private static bool IsOpen(string value) =>
        value.Equals("启用", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("on", StringComparison.OrdinalIgnoreCase);

    private static bool IsClosed(string value) =>
        value.Equals("关闭", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("off", StringComparison.OrdinalIgnoreCase);

    private static FieldPreflightInputItem NeedsField(string code, string name, string detail) =>
        new(code, name, "未取得", FieldPreflightInputStatus.NeedsFieldConfirmation, detail);
}
