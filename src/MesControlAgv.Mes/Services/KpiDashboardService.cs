using DomainTaskStatus = MesControlAgv.Domain.TaskStatus;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Data;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public sealed record KpiDashboard(
    DateOnly Date,
    KpiTaskSummary TaskSummary,
    IReadOnlyList<KpiTaskTrendPoint> TaskTrend,
    KpiSampleSummary SampleSummary,
    IReadOnlyList<KpiConsumable> Consumables,
    IReadOnlyList<KpiInstrumentStatus> Instruments);

public sealed record KpiTaskSummary(int Total, int Running, int Completed, int Failed, int Cancelled);
public sealed record KpiTaskTrendPoint(string Hour, int Created, int Completed);
public sealed record KpiSampleSummary(int Total, int Waiting, int Processing, int Completed, int Failed, int Cancelled, string DataSource);
public sealed record KpiConsumable(string Name, int Remaining, int Capacity, string Status, string DataSource);
public sealed record KpiInstrumentStatus(string Name, string Status, bool Online, string Detail, string DataSource);

public sealed class KpiDashboardService(MesDbContext database, IAdapterClient adapter)
{
    public async Task<KpiDashboard> GetAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var start = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var end = start.AddDays(1);
        var tasks = await database.TransportTasks
            .AsNoTracking()
            .Where(task => task.CreatedAt >= start && task.CreatedAt < end)
            .ToListAsync(cancellationToken);

        var status = tasks.GroupBy(task => task.Status).ToDictionary(group => group.Key, group => group.Count());
        var running = tasks.Count(task => task.Status is not DomainTaskStatus.Completed and not DomainTaskStatus.Failed and not DomainTaskStatus.Cancelled);
        var summary = new KpiTaskSummary(
            tasks.Count,
            running,
            Count(status, DomainTaskStatus.Completed),
            Count(status, DomainTaskStatus.Failed),
            Count(status, DomainTaskStatus.Cancelled));

        var trend = Enumerable.Range(0, 24)
            .Select(hour =>
            {
                var hourStart = start.AddHours(hour);
                var hourEnd = hourStart.AddHours(1);
                return new KpiTaskTrendPoint(
                    $"{hour:00}:00",
                    tasks.Count(task => task.CreatedAt >= hourStart && task.CreatedAt < hourEnd),
                    tasks.Count(task => task.Status == DomainTaskStatus.Completed && task.UpdatedAt >= hourStart && task.UpdatedAt < hourEnd));
            })
            .ToList();

        var sample = new KpiSampleSummary(
            tasks.Count,
            tasks.Count(task => task.Status is DomainTaskStatus.Created or DomainTaskStatus.Dispatching),
            tasks.Count(task => task.Status is DomainTaskStatus.MovingToPickup or DomainTaskStatus.WaitingPickupConfirmation or DomainTaskStatus.MovingToDropoff or DomainTaskStatus.WaitingDropoffConfirmation or DomainTaskStatus.Paused or DomainTaskStatus.Unknown),
            Count(status, DomainTaskStatus.Completed),
            Count(status, DomainTaskStatus.Failed),
            Count(status, DomainTaskStatus.Cancelled),
            "基于运输任务状态聚合；真实样品系统尚未接入");

        var snapshots = adapter is IFleetAwareAdapterClient fleet
            ? await fleet.GetFleetSnapshotAsync(cancellationToken)
            : [await adapter.GetSnapshotAsync(cancellationToken)];
        var instruments = snapshots.Select(snapshot => new KpiInstrumentStatus(
            snapshot.AgvId,
            snapshot.Online ? "在线" : "离线",
            snapshot.Online,
            $"控制权：{snapshot.ControlOwner}；当前位置：{snapshot.CurrentStationId ?? "-"}",
            "Adapter/AGV 状态；真实实验仪器尚未接入")).ToList();

        return new KpiDashboard(
            date,
            summary,
            trend,
            sample,
            [new KpiConsumable("实验耗材库存", 0, 0, "未接入", "现场耗材接口尚未接入")],
            instruments);
    }

    private static int Count(IReadOnlyDictionary<DomainTaskStatus, int> counts, DomainTaskStatus status) =>
        counts.TryGetValue(status, out var count) ? count : 0;
}
