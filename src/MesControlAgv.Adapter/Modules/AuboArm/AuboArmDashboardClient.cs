using System.Net.Sockets;
using System.Text;
using MesControlAgv.Application;

namespace MesControlAgv.Adapter.Modules.AuboArm;

/// <summary>
/// Read-only client for the AUBO Dashboard Server (TCP 29999). The only command
/// emitted by this type is the documented <c>get loaded program</c> query; it does
/// not expose or send load/play/stop commands.
/// </summary>
public sealed class AuboArmDashboardClient : IAuboArmLoadedProgramReader, IDisposable
{
    private const int MaximumResponseBytes = 16 * 1024;
    private readonly AuboArmOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public AuboArmDashboardClient(AuboArmOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<string?> GetLoadedProgramAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (!string.Equals(deviceId.Trim(), _options.DeviceId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Adapter device '{deviceId}' is not the configured AUBO arm.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.DashboardTimeoutMs);
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_options.Host, _options.DashboardPort, timeout.Token)
                    .ConfigureAwait(false);
                await using var stream = client.GetStream();
                var command = Encoding.ASCII.GetBytes("get loaded program\n");
                await stream.WriteAsync(command, timeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeout.Token).ConfigureAwait(false);

                var response = await ReadResponseAsync(stream, timeout.Token).ConfigureAwait(false);
                return ParseLoadedProgram(response);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A Dashboard Server timeout only makes the optional filename
                // observation unavailable; the WebSocket state path remains the
                // authoritative source for readiness and safety decisions.
                return null;
            }
            catch (SocketException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        using var response = new MemoryStream();
        while (response.Length < MaximumResponseBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            response.Write(buffer, 0, read);
            if (buffer.AsSpan(0, read).IndexOf((byte)'\n') >= 0) break;
        }

        if (response.Length >= MaximumResponseBytes)
            throw new IOException("AUBO Dashboard Server response exceeded the configured limit.");

        return Encoding.UTF8.GetString(response.ToArray());
    }

    private static string? ParseLoadedProgram(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;
        foreach (var rawLine in response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim().Trim('\0');
            if (line.Equals("No program loaded", StringComparison.OrdinalIgnoreCase)) return null;
            const string prefix = "Loaded program:";
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var value = line[prefix.Length..].Trim();
            if (value.StartsWith('<') && value.EndsWith('>') && value.Length >= 2)
                value = value[1..^1].Trim();
            var separator = value.LastIndexOfAny(['/', '\\']);
            var fileName = separator >= 0 ? value[(separator + 1)..] : value;
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            try
            {
                return AuboArmOptions.NormalizeProgramName(fileName);
            }
            catch (ArgumentException)
            {
                return fileName.Trim();
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
