using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.DigitalTwin;

public enum TwinChannel { Agv, Arm, Workstation }
public enum TwinCondition { Loading, Unknown, Online, Idle, Running, Paused, Fault, Offline, Unavailable, Stale }

public sealed record TwinBindings(string AgvId = "AGV-01", string ArmId = "ARM-01", string WorkstationId = "SAMPLE-WORKSTATION-01")
{
    public string Id(TwinChannel channel) => channel switch
    {
        TwinChannel.Agv => AgvId, TwinChannel.Arm => ArmId, _ => WorkstationId
    };
}

public sealed record TwinReading(TwinChannel Channel, string EquipmentId, TwinCondition Condition,
    string Text, string Detail, DateTimeOffset? ReceivedAt = null, DateTimeOffset? ObservedAt = null, bool Partial = false)
{
    public string Name => Channel switch { TwinChannel.Agv => "AGV 移动底盘", TwinChannel.Arm => "机械臂", _ => "开盖／分液工作站" };
    public string Tone => Condition switch
    {
        TwinCondition.Fault => "fault",
        TwinCondition.Offline or TwinCondition.Unavailable or TwinCondition.Stale => "warning",
        _ when Partial => "warning",
        TwinCondition.Running => "running",
        TwinCondition.Online or TwinCondition.Idle => "normal",
        TwinCondition.Paused => "warning", _ => "unknown"
    };
    public string Color => Tone switch
    {
        "fault" => "#C43E45", "warning" => "#A36209", "running" => "#1669B2", "normal" => "#17815B", _ => "#697887"
    };
    public string DisplayText => Text + (Partial ? " · 部分数据不可用" : "");
    public string TimeText => ReceivedAt is null ? "尚无有效读取" :
        $"读取 {ReceivedAt.Value.ToLocalTime():HH:mm:ss} · " + (ObservedAt is { } observed
            ? $"观测 {observed.ToLocalTime():HH:mm:ss}" : "设备观测时间未提供");
}

public static class TwinProjection
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(5);

    public static TwinReading Pending(TwinChannel channel, string id) => new(channel, id, TwinCondition.Loading, "读取中", "仅查询 MES，不发送设备指令。");
    public static TwinReading Failed(TwinChannel channel, string id, string detail) => new(channel, id, TwinCondition.Unavailable, "读取失败", detail);

    public static TwinReading Freshness(TwinReading reading, DateTimeOffset now)
    {
        if (reading.Condition is TwinCondition.Loading or TwinCondition.Unavailable or TwinCondition.Stale) return reading;
        if (reading.ReceivedAt is { } received && now - received > MaxAge ||
            reading.ObservedAt is { } observed && now - observed > MaxAge)
            return reading with { Condition = TwinCondition.Stale, Text = "数据过期", Detail = $"上次：{reading.DisplayText}。{reading.Detail}" };
        if (reading.ObservedAt > now + ClockTolerance)
            return reading with { Condition = TwinCondition.Unknown, Text = "观测时间异常" };
        return reading;
    }

    public static TwinReading Agv(IReadOnlyList<AgvFleetDashboardStatus> fleet, string id, DateTimeOffset now)
    {
        var matches = fleet.Where(s => s.Snapshot.AgvId == id).ToArray();
        if (matches.Length != 1) return Failed(TwinChannel.Agv, id, matches.Length == 0 ? "MES 未返回此 AGV。" : "MES 返回重复 AGV 编号，未采用。");
        var item = matches[0];
        var state = item.ActiveTask?.DeviceState?.Trim().ToLowerInvariant();
        var condition = !item.Snapshot.Online ? TwinCondition.Offline :
            !string.IsNullOrWhiteSpace(item.ActiveTask?.LastError) || state is "failed" or "faulted" or "error" ? TwinCondition.Fault :
            state is "paused" or "pausing" ? TwinCondition.Paused :
            state is "running" or "executing" or "moving" ? TwinCondition.Running : TwinCondition.Online;
        var detail = $"站点：{item.Snapshot.CurrentStationId ?? "未提供"}；设备任务：{item.ActiveTask?.DeviceState ?? "未提供"}。";
        if (!string.IsNullOrWhiteSpace(item.ActiveTask?.LastError)) detail += $" {item.ActiveTask.LastError}";
        return new(TwinChannel.Agv, id, condition, Text(condition) + "（MES 返回）", detail, now);
    }

    public static TwinReading Arm(AuboArmStatusResponse? status, string id, DateTimeOffset now)
    {
        if (status is null || status.DeviceId != id) return Failed(TwinChannel.Arm, id, "机械臂状态为空或设备编号不符。");
        var condition = !status.Online ? TwinCondition.Offline :
            status.Mode == AuboArmMode.Error || status.SafetyMode is AuboArmSafetyMode.Violation or
                AuboArmSafetyMode.ProtectiveStop or AuboArmSafetyMode.SafeguardStop or AuboArmSafetyMode.SystemEmergencyStop or
                AuboArmSafetyMode.RobotEmergencyStop or AuboArmSafetyMode.Fault ? TwinCondition.Fault :
            !Enum.IsDefined(status.Mode) || !Enum.IsDefined(status.SafetyMode) || !Enum.IsDefined(status.RuntimeState) ||
                status.Mode is AuboArmMode.Unknown or AuboArmMode.NoController or AuboArmMode.Disconnected ||
                status.SafetyMode is AuboArmSafetyMode.Unknown or AuboArmSafetyMode.Undefined ||
                status.RuntimeState == AuboArmRuntimeState.Unknown ? TwinCondition.Unknown :
            status.SafetyMode == AuboArmSafetyMode.Recovery || status.RuntimeState is AuboArmRuntimeState.Paused or AuboArmRuntimeState.Pausing ||
                status.Mode is not (AuboArmMode.Running or AuboArmMode.Idle) ? TwinCondition.Paused :
            status.RuntimeState is AuboArmRuntimeState.Running or AuboArmRuntimeState.Stepping ? TwinCondition.Running :
            status.RuntimeState == AuboArmRuntimeState.Stopped ? TwinCondition.Idle : TwinCondition.Paused;
        // AUBO's powered/operational robot mode can be Running while its program is Stopped.
        // Only the interpreter runtime state is evidence of program execution.
        var result = new TwinReading(TwinChannel.Arm, id, condition, condition == TwinCondition.Idle ? "程序已停止" : Text(condition),
            $"模式 {status.Mode} · 安全 {status.SafetyMode} · 程序 {status.RuntimeState}", now,
            status.ObservedAtUtc == default ? null : status.ObservedAtUtc);
        return CheckObservation(result, now);
    }

    public static TwinReading Workstation(SampleWorkstationDashboardSnapshot? snapshot, string id, DateTimeOffset now)
    {
        if (snapshot?.Status is not { } status || status.DeviceId != id || snapshot.StatusReadError is not null)
            return Failed(TwinChannel.Workstation, id, "工作站状态不可用；" + (snapshot?.StatusReadError ?? "返回为空或编号不符。"));
        var error = snapshot.Error;
        var validError = error is not null && error.Recognized && error.ErrorCode.HasValue && error.DeviceId == id && error.ObservedAtUtc != default &&
            now - error.ObservedAtUtc <= MaxAge && error.ObservedAtUtc <= now + ClockTolerance && snapshot.ErrorReadError is null;
        // This endpoint reports experiment result codes, not the instrument-state enum:
        // 0=completed, 1=manually ended, 2=abnormally ended, 3=started, -1..-4=errors.
        var knownResult = validError && error!.ErrorCode is >= -4 and <= 3;
        var partial = !knownResult || snapshot.TasksReadError is not null;
        var condition = !status.Online || status.State == SampleWorkstationDeviceState.Offline ? TwinCondition.Offline :
            status.State == SampleWorkstationDeviceState.Faulted || knownResult && error!.ErrorCode is 2 or -1 or -2 or -3 or -4 ? TwinCondition.Fault :
            knownResult && error!.ErrorCode == 1 ? TwinCondition.Paused :
            status.State switch
            {
                SampleWorkstationDeviceState.Idle => TwinCondition.Idle,
                SampleWorkstationDeviceState.Running => TwinCondition.Running,
                SampleWorkstationDeviceState.Paused or SampleWorkstationDeviceState.Initializing => TwinCondition.Paused,
                _ => TwinCondition.Unknown
            };
        var detail = $"状态 {status.State}（原始值 {status.RawState}）";
        if (validError) detail += $"；结果码 {error!.ErrorCode}：{error.Description}";
        if (!knownResult) detail += "；告警未识别、读取不完整或已过期";
        if (snapshot.TasksReadError is not null) detail += "；任务列表读取失败";
        return CheckObservation(new(TwinChannel.Workstation, id, condition, Text(condition), detail, now,
            status.ObservedAtUtc == default ? null : status.ObservedAtUtc, partial), now);
    }

    private static TwinReading CheckObservation(TwinReading reading, DateTimeOffset now) => reading.ObservedAt is null
        ? reading with { Condition = TwinCondition.Unknown, Text = "观测时间未提供", Detail = $"MES 返回：{reading.Text}。{reading.Detail}" }
        : Freshness(reading, now);

    public static TwinReading Composite(TwinReading agv, TwinReading arm) =>
        new[] { agv, arm }.OrderByDescending(r => Priority(r)).First();
    private static int Priority(TwinReading r) => r.Condition switch
    {
        TwinCondition.Fault => 100, TwinCondition.Offline => 90, TwinCondition.Unavailable => 85,
        TwinCondition.Stale => 80, TwinCondition.Unknown => 75, TwinCondition.Loading => 70,
        _ when r.Partial => 65, TwinCondition.Paused => 60, TwinCondition.Running => 50, _ => 0
    };
    private static string Text(TwinCondition condition) => condition switch
    {
        TwinCondition.Offline => "离线", TwinCondition.Fault => "故障／安全停止", TwinCondition.Running => "运行中",
        TwinCondition.Paused => "暂停／待确认", TwinCondition.Idle => "空闲", TwinCondition.Online => "在线", _ => "状态未知"
    };
}
