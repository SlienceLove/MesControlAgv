namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// Identifies where an operator-facing online/offline projection came from.
/// An online simulator is deliberately not equivalent to a verified field device.
/// </summary>
public enum RuntimeConnectionSource
{
    Unverified,
    LocalSimulator,
    PhysicalDevice
}

public static class RuntimeConnectionSourcePresentation
{
    public static RuntimeConnectionSource Resolve(string? runtimeMode) =>
        runtimeMode?.Trim().ToLowerInvariant() switch
        {
            "simulator" => RuntimeConnectionSource.LocalSimulator,
            "physical" => RuntimeConnectionSource.PhysicalDevice,
            _ => RuntimeConnectionSource.Unverified
        };

    public static string SourceDisplay(RuntimeConnectionSource source) => source switch
    {
        RuntimeConnectionSource.LocalSimulator => "数据来源：本地模拟器（非现场设备）",
        RuntimeConnectionSource.PhysicalDevice => "数据来源：物理设备",
        _ => "数据来源：未验证"
    };

    public static string SourceDetail(RuntimeConnectionSource source) => source switch
    {
        RuntimeConnectionSource.LocalSimulator =>
            "当前状态来自 localhost Simulator/Adapter/MES；显示在线不代表实体 AGV 或 AUBO 可达。",
        RuntimeConnectionSource.PhysicalDevice =>
            "当前状态来自 physical 运行链；是否允许操作仍以本次现场只读预检和授权为准。",
        _ => "当前运行来源尚未确认，不能把在线状态作为现场操作依据。"
    };

    public static string SourceBrush(RuntimeConnectionSource source) => source switch
    {
        RuntimeConnectionSource.LocalSimulator => "#E8F3FF",
        RuntimeConnectionSource.PhysicalDevice => "#ECFDF3",
        _ => "#FFF4E5"
    };

    public static string SourceForeground(RuntimeConnectionSource source) => source switch
    {
        RuntimeConnectionSource.LocalSimulator => "#175CD3",
        RuntimeConnectionSource.PhysicalDevice => "#027A48",
        _ => "#B54708"
    };

    public static string DescribeConnection(
        RuntimeConnectionSource source,
        bool online,
        string? rawOfflineStatus = null)
    {
        var offlineStatus = string.IsNullOrWhiteSpace(rawOfflineStatus)
            ? "离线"
            : rawOfflineStatus.Trim();
        return source switch
        {
            RuntimeConnectionSource.LocalSimulator => online
                ? "本地模拟器在线"
                : $"本地模拟器：{offlineStatus}",
            RuntimeConnectionSource.PhysicalDevice => online
                ? "物理设备在线"
                : offlineStatus == "未刷新"
                    ? "物理设备未验证"
                    : $"物理设备未验证（{offlineStatus}）",
            _ => online
                ? "来源未验证：在线"
                : $"来源未验证（{offlineStatus}）"
        };
    }

    public static string DescribeMesConnection(RuntimeConnectionSource source, bool connected) => source switch
    {
        RuntimeConnectionSource.LocalSimulator => connected ? "本地模拟 MES 已连接" : "本地模拟 MES 不可用",
        RuntimeConnectionSource.PhysicalDevice => connected ? "物理 MES 已连接" : "物理 MES 不可用",
        _ => connected ? "MES 已连接（来源未验证）" : "MES 不可用（来源未验证）"
    };
}
