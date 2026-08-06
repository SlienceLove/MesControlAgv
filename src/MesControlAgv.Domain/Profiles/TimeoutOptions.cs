namespace MesControlAgv.Domain.Profiles;

/// <summary>
/// Time limits and polling cadence shared by AGV/MES operations.
/// </summary>
public sealed record TimeoutOptions
{
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan DispatchTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan TaskCompletionTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan TaskPollingInterval { get; init; } = TimeSpan.FromSeconds(2);
}
