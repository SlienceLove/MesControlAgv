using MesControlAgv.Domain;

namespace MesControlAgv.Wpf.Services;

public sealed class ControlCenterCommandCoordinator(
    IMesClient mes,
    ISimulatorControlClient? simulator = null)
{
    public Task<DashboardTask> CreateTaskAsync(
        int sourceStationCode,
        int targetStationCode,
        int priority,
        string? description,
        string? externalId,
        CancellationToken cancellationToken) =>
        mes.CreateTaskAsync(
            sourceStationCode,
            targetStationCode,
            priority,
            description,
            externalId,
            cancellationToken);

    public Task<DashboardTask> DispatchTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        mes.DispatchTaskAsync(taskId, cancellationToken);

    public async Task MarkSimulatorArrivalAsync(
        Guid taskId,
        bool movingToDropoff,
        CancellationToken cancellationToken)
    {
        var simulatorClient = simulator
            ?? throw new InvalidOperationException("Simulator control is unavailable in the current runtime mode.");
        var deviceTaskId = movingToDropoff
            ? TransportOperationIds.Dropoff(taskId)
            : TransportOperationIds.Pickup(taskId);
        await simulatorClient.ApplyControlAsync(deviceTaskId, "arrive", cancellationToken);
        await mes.MarkArrivedAsync(taskId, cancellationToken);
    }

    public Task<DashboardTask> ConfirmPickupAsync(
        Guid taskId,
        string operatorName,
        CancellationToken cancellationToken) =>
        mes.ConfirmPickupAsync(taskId, operatorName, cancellationToken);

    public Task<DashboardTask> ConfirmDropoffAsync(
        Guid taskId,
        string operatorName,
        CancellationToken cancellationToken) =>
        mes.ConfirmDropoffAsync(taskId, operatorName, cancellationToken);

    public Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken) =>
        mes.RetryAsync(taskId, cancellationToken);

    public Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken) =>
        mes.RecoverAsync(taskId, cancellationToken);

    public Task<DashboardTask> CancelAsync(
        Guid taskId,
        string operatorName,
        CancellationToken cancellationToken) =>
        mes.CancelAsync(taskId, operatorName, cancellationToken);

    public async Task<AgvCommandResult> ExecuteAgvCommandAsync(
        string agvId,
        string command,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        var result = await mes.ExecuteAgvCommandAsync(agvId, command, taskId, cancellationToken)
            ?? throw new InvalidOperationException($"AGV {agvId} did not return a result for '{command}'.");
        if (!string.IsNullOrWhiteSpace(result.LastError) ||
            string.Equals(result.State, "failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.State, "error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(result.LastError ?? $"AGV {agvId} rejected '{command}'.");
        }

        return result;
    }

    public Task ApplySimulatorControlAsync(string mode, CancellationToken cancellationToken) =>
        (simulator ?? throw new InvalidOperationException("Simulator control is unavailable in the current runtime mode."))
        .ApplyControlAsync(mode, cancellationToken);
}
