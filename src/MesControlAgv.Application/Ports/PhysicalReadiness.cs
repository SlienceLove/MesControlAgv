using MesControlAgv.Contracts;

namespace MesControlAgv.Application;

/// <summary>
/// Extensible read-only probe boundary.  A probe may query its controller but
/// must not acquire control, dispatch, write I/O, or issue a program command.
/// </summary>
public interface IPhysicalDeviceReadinessProbe
{
    bool CanProbe(PhysicalDeviceDescriptor device);

    Task<PhysicalDeviceReadinessObservation> ProbeAsync(
        PhysicalDeviceDescriptor device,
        bool fullPreflight,
        CancellationToken cancellationToken);
}

/// <summary>
/// Read-only state/gate shared by UI and physical workers.  Implementations
/// fail closed when the supervisor is enabled and no current epoch is supplied.
/// </summary>
public interface IPhysicalReadinessState
{
    bool Enabled { get; }

    PhysicalReadinessResponse GetSnapshot();

    bool TryGetDevice(
        string deviceId,
        out PhysicalDeviceReadinessSnapshot snapshot);

    bool IsCurrentAndReady(
        string deviceId,
        long? expectedEpoch,
        out string? reason);

    bool IsCurrentAndReady(
        string deviceId,
        long? expectedEpoch,
        string? expectedSupervisorInstanceId,
        out string? reason);

    bool AcknowledgeAuthorization(
        string deviceId,
        long expectedEpoch,
        string? expectedSupervisorInstanceId);
}

/// <summary>Hosted refresh capability layered on top of the read-only state.</summary>
public interface IPhysicalReadinessSupervisor : IPhysicalReadinessState
{
    Task<PhysicalReadinessResponse> RefreshAsync(
        bool forceFull,
        CancellationToken cancellationToken);
}
