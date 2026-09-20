using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Services;

public sealed partial class TcpAgvClient
{
    private readonly SemaphoreSlim _poseGate = new(1, 1);
    private (string? Name, string? Md5, DateTimeOffset? ObservedAt, long Connection) _poseMap;

    public async Task<AgvPoseResponse> GetPoseAsync(CancellationToken cancellationToken)
    {
        await _poseGate.WaitAsync(cancellationToken);
        try
        {
            if (_poseMap.ObservedAt is null || DateTimeOffset.UtcNow - _poseMap.ObservedAt > TimeSpan.FromSeconds(10) ||
                _poseMap.Connection != _statusChannel.ConnectionVersion)
            {
                _poseMap = default; // A failed refresh cannot leave old identity marked as fresh.
                using var catalog = await _statusChannel.RequestReadOnlyAsync(QueryMapCatalogApi, null, cancellationToken);
                EnsureSuccess(catalog, QueryMapCatalogApi);
                var version = _statusChannel.ConnectionVersion;
                var name = ReadString(catalog.RootElement, "current_map")?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var md5 = await QueryMapMd5Async(name, ReadStringArray(catalog.RootElement, "maps"), cancellationToken);
                    if (version == _statusChannel.ConnectionVersion && md5 is { Length: 32 } && md5.All(Uri.IsHexDigit))
                        _poseMap = (name, md5, DateTimeOffset.UtcNow, version);
                }
            }

            using var response = await _statusChannel.RequestReadOnlyAsync(
                RealtimeStatusApi, new { return_laser = false }, cancellationToken);
            EnsureSuccess(response, RealtimeStatusApi);
            var received = DateTimeOffset.UtcNow;
            if (_poseMap.Connection != _statusChannel.ConnectionVersion) _poseMap = default;
            var root = response.RootElement;
            // Identity is supplied only by the endpoint's configured single physical device.
            return new AgvPoseResponse("", ReadDouble(root, "x"), ReadDouble(root, "y"),
                ReadDouble(root, "angle"), ReadDouble(root, "confidence"), ReadStation(root),
                ReadString(root, "create_on"), received, _poseMap.Name, _poseMap.Md5, _poseMap.ObservedAt);
        }
        catch
        {
            _poseMap = default;
            throw;
        }
        finally { _poseGate.Release(); }
    }
}
