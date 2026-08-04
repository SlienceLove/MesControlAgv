using System.Net.Http;

namespace MesControlAgv.Wpf.Services;

public interface ISimulatorControlClient
{
    Task ApplyControlAsync(string mode, CancellationToken cancellationToken);
    Task ApplyControlAsync(Guid deviceTaskId, string mode, CancellationToken cancellationToken);
}

public sealed class SimulatorControlClient(HttpClient client) : ISimulatorControlClient
{
    public async Task ApplyControlAsync(string mode, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync($"controls/{mode}", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task ApplyControlAsync(Guid deviceTaskId, string mode, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync($"controls/{mode}/{deviceTaskId}", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
