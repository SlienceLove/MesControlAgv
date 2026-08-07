namespace MesControlAgv.Contracts;

public sealed record KpiDashboardResponse(
    DateOnly Date,
    KpiTaskSummaryResponse TaskSummary,
    IReadOnlyList<KpiTaskTrendPointResponse> TaskTrend,
    KpiSampleSummaryResponse SampleSummary,
    IReadOnlyList<KpiConsumableResponse> Consumables,
    IReadOnlyList<KpiInstrumentStatusResponse> Instruments);

public sealed record KpiTaskSummaryResponse(int Total, int Running, int Completed, int Failed, int Cancelled);
public sealed record KpiTaskTrendPointResponse(string Hour, int Created, int Completed);
public sealed record KpiSampleSummaryResponse(int Total, int Waiting, int Processing, int Completed, int Failed, int Cancelled, string DataSource);
public sealed record KpiConsumableResponse(string Name, int Remaining, int Capacity, string Status, string DataSource);
public sealed record KpiInstrumentStatusResponse(string Name, string Status, bool Online, string Detail, string DataSource);
