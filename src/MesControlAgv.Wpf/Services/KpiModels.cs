namespace MesControlAgv.Wpf.Services;

public sealed record KpiTaskSummary(int Total, int Running, int Completed, int Failed, int Cancelled);
public sealed record KpiTaskTrendPoint(string Hour, int Created, int Completed);
public sealed record KpiSampleSummary(int Total, int Waiting, int Processing, int Completed, int Failed, int Cancelled, string DataSource);
public sealed record KpiConsumable(string Name, int Remaining, int Capacity, string Status, string DataSource);
public sealed record KpiInstrumentStatus(string Name, string Status, bool Online, string Detail, string DataSource);
public sealed record KpiDashboard(
    DateOnly Date,
    KpiTaskSummary TaskSummary,
    IReadOnlyList<KpiTaskTrendPoint> TaskTrend,
    KpiSampleSummary SampleSummary,
    IReadOnlyList<KpiConsumable> Consumables,
    IReadOnlyList<KpiInstrumentStatus> Instruments);
