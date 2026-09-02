using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Services;

/// <summary>
/// Optional AGV digital-I/O capability. It is separate from the navigation
/// contract so simulator and legacy drivers do not have to pretend to expose
/// physical I/O.
/// </summary>
public interface IAgvIoDeviceClient
{
    Task<AgvIoSnapshotResponse> GetIoAsync(CancellationToken cancellationToken);

    Task<AgvDoWriteResponse> SetDoAsync(
        int id,
        bool status,
        CancellationToken cancellationToken);
}
