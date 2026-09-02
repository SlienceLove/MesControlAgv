using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Modules.AuboArm;

/// <summary>Explicitly gated, single-step pick/place test harness. It can only use a loopback controller.</summary>
public sealed class AuboArmLoopbackHarness
{
    private readonly AuboArmOptions _options;
    private readonly AuboArmControlledHandshakeSession _session;

    public AuboArmLoopbackHarness(AuboArmOptions options, AuboArmLoopbackController controller,
        TimeProvider? timeProvider = null, IReadOnlyList<int>? allowedCommands = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(controller);
        if (!options.EnableLoopbackControl)
            throw new InvalidOperationException("AUBO loopback control is disabled; set Devices:AuboArm:EnableLoopbackControl=true for a test run.");
        _options = options;
        _session = new AuboArmControlledHandshakeSession(controller,
            new AuboArmReadOnlyDriver(controller, options, timeProvider ?? TimeProvider.System), options,
            timeProvider ?? TimeProvider.System, allowedCommands ?? [1, 2, 3]);
    }

    public Task<AuboArmHandshakeResultResponse> ExecuteSingleStepAsync(int commandCode, CancellationToken cancellationToken = default) =>
        _session.DispatchAsync(_options.DeviceId, Guid.NewGuid(), commandCode, cancellationToken);
}
