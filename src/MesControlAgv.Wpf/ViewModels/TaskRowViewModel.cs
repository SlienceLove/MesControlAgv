namespace MesControlAgv.Wpf.ViewModels;

public sealed record TaskRowViewModel(
    Guid Id,
    int SourceStationCode,
    int TargetStationCode,
    string Status,
    int RetryCount,
    string? LastError)
{
    public static TaskRowViewModel From(Services.DashboardTask task) => new(
        task.Id,
        task.SourceStationCode,
        task.TargetStationCode,
        task.Status,
        task.RetryCount,
        task.LastError);
}
