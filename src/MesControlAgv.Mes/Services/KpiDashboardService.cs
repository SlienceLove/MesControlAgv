using DomainTaskStatus = MesControlAgv.Domain.TaskStatus;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public sealed class KpiDashboardService(MesDbContext database, IAgvGateway adapter)
    : IKpiDashboardApplicationService
{
    public async Task<KpiDashboardResponse> GetAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var start = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var end = start.AddDays(1);
        var tasks = await database.TransportTasks
            .AsNoTracking()
            .Where(task => task.CreatedAt >= start && task.CreatedAt < end)
            .ToListAsync(cancellationToken);

        var status = tasks.GroupBy(task => task.Status).ToDictionary(group => group.Key, group => group.Count());
        var running = tasks.Count(task => task.Status is not DomainTaskStatus.Completed and not DomainTaskStatus.Failed and not DomainTaskStatus.Cancelled);
        var summary = new KpiTaskSummaryResponse(
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
                return new KpiTaskTrendPointResponse(
                    $"{hour:00}:00",
                    tasks.Count(task => task.CreatedAt >= hourStart && task.CreatedAt < hourEnd),
                    tasks.Count(task => task.Status == DomainTaskStatus.Completed && task.UpdatedAt >= hourStart && task.UpdatedAt < hourEnd));
            })
            .ToList();

        var sample = new KpiSampleSummaryResponse(
            tasks.Count,
            tasks.Count(task => task.Status is DomainTaskStatus.Created or DomainTaskStatus.Dispatching),
            tasks.Count(task => task.Status is DomainTaskStatus.MovingToPickup or DomainTaskStatus.WaitingPickupConfirmation or DomainTaskStatus.MovingToDropoff or DomainTaskStatus.WaitingDropoffConfirmation or DomainTaskStatus.Paused or DomainTaskStatus.Unknown),
            Count(status, DomainTaskStatus.Completed),
            Count(status, DomainTaskStatus.Failed),
            Count(status, DomainTaskStatus.Cancelled),
            "基于运输任务状态聚合；真实样品系统尚未接入");

        var snapshots = adapter is IFleetAwareAgvGateway fleet
            ? await fleet.GetFleetSnapshotAsync(cancellationToken)
            : [await adapter.GetSnapshotAsync(cancellationToken)];
        var instruments = snapshots.Select(snapshot => new KpiInstrumentStatusResponse(
            snapshot.AgvId,
            snapshot.Online ? "在线" : "离线",
            snapshot.Online,
            $"控制权：{snapshot.ControlOwner}；当前位置：{snapshot.CurrentStationId ?? "-"}",
            "Adapter/AGV 状态；真实实验仪器尚未接入")).ToList();

        return new KpiDashboardResponse(
            date,
            summary,
            trend,
            sample,
            [new KpiConsumableResponse("实验耗材库存", 0, 0, "未接入", "现场耗材接口尚未接入")],
            instruments);
    }

    private static int Count(IReadOnlyDictionary<DomainTaskStatus, int> counts, DomainTaskStatus status) =>
        counts.TryGetValue(status, out var count) ? count : 0;
}
