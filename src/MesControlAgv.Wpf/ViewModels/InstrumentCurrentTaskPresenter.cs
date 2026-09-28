using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>Read-only current-task display; never infers identity from list order or selection.</summary>
public static class InstrumentCurrentTaskPresenter
{
    public const string Unconfirmed = "当前任务无法确认，请刷新状态";
    public const string RunningUnidentified = "运行中，任务待确认";
    private static bool Fresh(DateTimeOffset? time, DateTimeOffset now) =>
        time is { } value && now - value >= TimeSpan.Zero && now - value <= TimeSpan.FromSeconds(60);

    public static string Workstation(SampleWorkstationDashboardSnapshot? snapshot, DateTimeOffset? readAt, DateTimeOffset now)
    {
        if (snapshot?.Status is not { } status || !Fresh(readAt, now) ||
            !string.IsNullOrWhiteSpace(snapshot.StatusReadError)) return Unconfirmed;
        if (!status.Online || status.State == SampleWorkstationDeviceState.Offline) return "设备离线，当前任务无法确认";
        if (!Fresh(status.ObservedAtUtc, now)) return Unconfirmed;
        if (status.State == SampleWorkstationDeviceState.Idle) return "当前无运行任务";
        if (status.State != SampleWorkstationDeviceState.Running) return Unconfirmed;
        // Device status has no current task ID; task rows cannot establish live identity.
        return RunningUnidentified;
    }

    public static string Ion(ShineLabDeviceStatusResponse? status, bool readSucceeded, DateTimeOffset? readAt,
        IEnumerable<ShineLabOperatorTaskRow> records, DateTimeOffset now)
    {
        if (status is null || !readSucceeded || !Fresh(readAt, now)) return Unconfirmed;
        if (!status.Online || status.State is "Offline" or "InstrumentOffline" || status.Status == 99)
            return "设备离线，当前任务无法确认";
        if (!Fresh(status.LastSeenAtUtc, now) || !Fresh(status.LastDeviceStatusAtUtc, now)) return Unconfirmed;
        if (status.Status == 0 && status.State == "Idle" && !status.HasActiveTask) return "当前无运行任务";
        if (status.Status != 1 || status.State != "Running" || !status.HasActiveTask || status.TaskFinishedAtUtc is not null)
            return Unconfirmed;

        var hasSample = !string.IsNullOrWhiteSpace(status.SampleId);
        var hasTask = !string.IsNullOrWhiteSpace(status.TaskUuid);
        if (hasSample || hasTask)
        {
            var matches = records.Where(r => r.Task.EquipmentCode.Equals(status.EquipmentCode, StringComparison.OrdinalIgnoreCase)
                && (!hasTask || r.Task.TaskUuid == status.TaskUuid)
                && r.Detail?.Task.TaskUuid == r.Task.TaskUuid
                && r.Detail.Task.EquipmentCode.Equals(status.EquipmentCode, StringComparison.OrdinalIgnoreCase))
                .SelectMany(r => r.Detail!.Samples ?? [])
                .Where(s => !hasSample || s.SampleId == status.SampleId).ToArray();
            if (matches.Length == 1)
                return $"运行中 · {Name(matches[0].SampleName)} · {Name(matches[0].SourceSampleId, "编号待确认")}";
        }
        // A live name may be shown, but protocol UUIDs/wire sample IDs are not business numbers.
        return string.IsNullOrWhiteSpace(status.SampleName) ? RunningUnidentified
            : $"运行中 · {status.SampleName} · 编号待确认";
    }

    private static string Name(string? value, string fallback = "名称未提供") => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
