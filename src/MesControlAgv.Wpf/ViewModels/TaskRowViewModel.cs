using MesControlAgv.Domain;

namespace MesControlAgv.Wpf.ViewModels;

public sealed record TaskRowViewModel(
    Guid Id,
    int SourceStationCode,
    int TargetStationCode,
    string Status,
    int RetryCount,
    string? LastError,
    DateTime CreatedAt = default,
    DateTime? EndedAt = null,
    string? ActiveAgvId = null,
    string? ActiveDeviceTaskId = null,
    IReadOnlyList<string>? ActivePath = null)
{
    public string SourceStationName => GetStationName(SourceStationCode);
    public string TargetStationName => GetStationName(TargetStationCode);
    public string RouteDescription => $"{SourceStationName} -> {TargetStationName}";
    public string CurrentPathDescription => ActivePath is { Count: > 0 }
        ? string.Join(" -> ", ActivePath)
        : "尚未分配执行路径";
    public string AssignedAgvDescription => string.IsNullOrWhiteSpace(ActiveAgvId) ? "尚未分配" : ActiveAgvId;
    public string DeviceTaskDescription => string.IsNullOrWhiteSpace(ActiveDeviceTaskId) ? "尚未创建" : ActiveDeviceTaskId;
    public string TaskDescription => $"从{SourceStationName}取货，运送至{TargetStationName}";
    public string StatusDescription => Status switch
    {
        "Created" => "待派发",
        "Dispatching" => "正在派发",
        "MovingToPickup" => "前往取货站",
        "WaitingPickupConfirmation" => "等待确认取货",
        "MovingToDropoff" => "前往放货站",
        "WaitingDropoffConfirmation" => "等待确认放货",
        "Completed" => "已完成",
        "Paused" => "已暂停",
        "Failed" => "执行失败",
        "Unknown" => "系统异常",
        "Cancelled" => "已取消",
        _ => Status
    };

    public string ErrorDescription => Status == "Unknown" && string.IsNullOrWhiteSpace(LastError)
        ? "原因：无法确认 AGV 当前状态"
        : string.IsNullOrWhiteSpace(LastError)
            ? string.Empty
            : $"原因：{LastError}";

    public static TaskRowViewModel From(Services.DashboardTask task) => new(
        task.Id,
        task.SourceStationCode,
        task.TargetStationCode,
        task.Status,
        task.RetryCount,
        task.LastError,
        task.CreatedAt,
        task.EndedAt,
        task.ActiveAgvId,
        task.ActiveDeviceTaskId,
        task.ActivePath);

    private static string GetStationName(int code)
    {
        try
        {
            return Stations.Get(code).Name;
        }
        catch (KeyNotFoundException)
        {
            return $"未知站点({code})";
        }
    }
}

public sealed record TaskEventRowViewModel(
    Guid Id,
    string EventType,
    string Payload,
    DateTime CreatedAt)
{
    public static TaskEventRowViewModel From(Services.DashboardTaskEvent taskEvent) => new(
        taskEvent.Id,
        taskEvent.EventType,
        taskEvent.Payload,
        taskEvent.CreatedAt);
}
