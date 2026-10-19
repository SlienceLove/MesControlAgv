using System.Net;
using MesControlAgv.Contracts;

namespace MesControlAgv.Wpf.Services;

/// <summary>One sequential loop. Network failures never terminate future session checks.</summary>
public sealed class AccessSessionMonitor(
    Func<CancellationToken, Task<AccessUser>> read,
    Func<CancellationToken, Task>? delay = null)
{
    public async Task RunAsync(Action<AccessUser> updated, Action invalidated, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await (delay?.Invoke(cancellationToken) ?? Task.Delay(TimeSpan.FromSeconds(30), cancellationToken));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var user = await read(timeout.Token);
                if (cancellationToken.IsCancellationRequested) return;
                updated(user);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (AccessRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (!cancellationToken.IsCancellationRequested) invalidated();
                return;
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or TimeoutException or AccessRequestException or System.Text.Json.JsonException)
            { /* Keep the next scheduled check; an unavailable network is not a revoked identity. */ }
        }
    }
}
