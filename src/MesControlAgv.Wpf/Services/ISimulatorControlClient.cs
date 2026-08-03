using System.Net.Http;

namespace MesControlAgv.Wpf.Services;

public interface ISimulatorControlClient
{
    Task ApplyControlAsync(string mode, CancellationToken cancellationToken);
}

public sealed class SimulatorControlClient(HttpClient client) : ISimulatorControlClient
{
    public async Task ApplyControlAsync(string mode, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync($"controls/{mode}", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
