using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.DigitalTwin;

/// <summary>No control methods are exposed to the scene or its polling session.</summary>
public interface IDigitalTwinStatusSource
{
    string SourceDisplay { get; }
    TwinBindings Bindings { get; }
    Task<TwinReading> ReadAsync(TwinChannel channel, CancellationToken cancellationToken);
}

public sealed class MesDigitalTwinStatusSource(IMesClient mes, RuntimeConnectionSource connectionSource,
    TwinBindings? bindings = null) : IDigitalTwinStatusSource, IDigitalTwinPoseSource
{
    private readonly SemaphoreSlim _poseGate = new(1, 1);
    public async Task<MesControlAgv.Contracts.AgvPoseResponse> ReadPoseAsync(CancellationToken cancellationToken)
    {
        if (connectionSource != RuntimeConnectionSource.PhysicalDevice)
            throw new NotSupportedException("真实位置仅适用于物理运行链。");
        await _poseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await mes.GetAgvPoseAsync(Bindings.AgvId, cancellationToken).ConfigureAwait(false); }
        finally { _poseGate.Release(); }
    }
    // Shared across page sessions, so a late-cancelling request cannot overlap its replacement.
    private readonly SemaphoreSlim[] _gates = [new(1, 1), new(1, 1), new(1, 1)];
    public TwinBindings Bindings { get; } = bindings ?? new();
    public string SourceDisplay => connectionSource == RuntimeConnectionSource.PhysicalDevice
        ? "数据来源：物理运行链（MES 只读）"
        : RuntimeConnectionSourcePresentation.SourceDisplay(connectionSource);

    public async Task<TwinReading> ReadAsync(TwinChannel channel, CancellationToken cancellationToken)
    {
        var gate = _gates[(int)channel];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return channel switch
            {
                TwinChannel.Agv => TwinProjection.Agv(await mes.GetAgvFleetStatusAsync(cancellationToken).ConfigureAwait(false), Bindings.AgvId, DateTimeOffset.UtcNow),
                TwinChannel.Arm => TwinProjection.Arm(await mes.GetAuboArmStatusAsync(Bindings.ArmId, cancellationToken).ConfigureAwait(false), Bindings.ArmId, DateTimeOffset.UtcNow),
                _ => TwinProjection.Workstation(await mes.GetSampleWorkstationSnapshotAsync(Bindings.WorkstationId, cancellationToken).ConfigureAwait(false), Bindings.WorkstationId, DateTimeOffset.UtcNow)
            };
        }
        finally { gate.Release(); }
    }
}
