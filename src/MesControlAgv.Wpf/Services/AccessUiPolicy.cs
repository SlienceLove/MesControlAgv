using MesControlAgv.Contracts;

namespace MesControlAgv.Wpf.Services;

/// <summary>Presentation only. MES remains the authority for every request.</summary>
public static class AccessUiPolicy
{
    private static readonly HashSet<string> ViewerPages = ["KpiDashboardTab", "ExperimentSchedulingTab", "MaterialManagementTab",
        "SampleManagementTab", "ShineLabDeviceStatusTab", "DigitalTwinTab", "WorkflowRunMonitorTab", "MapDashboardTab"];
    private static readonly HashSet<string> AdminPages = ["StartupDiagnosticsTab", "ReadinessTab", "AgvCommunicationTab", "AuboArmTab"];
    private static readonly HashSet<string> ReadActions = ["刷新", "刷新状态", "刷新任务", "刷新列表", "刷新库存", "查询", "搜索", "筛选", "清除筛选",
        "全屏", "退出全屏", "全屏监控", "退出全屏监控", "全屏展示", "完整场景", "设备区", "地面开关", "地图参照", "路线开关", "聚焦所选", "↻"];
    public static bool CanNavigate(string role, string page) => role switch
    {
        AccessRoles.Administrator => true,
        AccessRoles.Operator => !AdminPages.Contains(page),
        AccessRoles.Viewer => ViewerPages.Contains(page),
        _ => false
    };
    public static bool CanUseAction(string role, string? label) => role != AccessRoles.Viewer && AccessRoles.IsValid(role)
        || ReadActions.Contains(label?.Trim() ?? "");
    public static string Summary(string role) => role switch
    {
        AccessRoles.Administrator => "管理员 · 用户、配置及业务操作权限（仍须满足设备准入）",
        AccessRoles.Operator => "操作员 · 可处理业务任务和物料，无用户管理及设备调试权限",
        _ => "只读模式 · 可查看状态与记录，不可创建、修改或下发任务"
    };
}
