using MesControlAgv.Contracts;

namespace MesControlAgv.Application;

/// <summary>
/// Application boundary for read-only operational KPI projections.
/// </summary>
public interface IKpiDashboardApplicationService
{
    Task<KpiDashboardResponse> GetAsync(DateOnly date, CancellationToken cancellationToken);
}
