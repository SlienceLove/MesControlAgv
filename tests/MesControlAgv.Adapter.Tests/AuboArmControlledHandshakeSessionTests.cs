using MesControlAgv.Adapter.Modules.AuboArm;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using Microsoft.Extensions.Configuration;

namespace MesControlAgv.Adapter.Tests;

/// <summary>
/// Covers the quarantined write path. Every test drives the loopback controller, so no
/// frame is ever sent to a real AUBO controller.
/// </summary>
public class AuboArmControlledHandshakeSessionTests
{
    private const int PickCommand = 3;

    [Fact]
    public async Task Dispatch_ShouldWriteCommandThenSequenceAndReturnLuaResult()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options);
        var session = CreateSession(controller, options);

        var dispatch = session.DispatchAsync(
            options.DeviceId,
            Guid.NewGuid(),
            PickCommand,
            CancellationToken.None);

        await RunLuaWhenTriggeredAsync(controller, options, command => (1, $"picked-{command}"));
        var result = await dispatch;

        Assert.Equal(AuboArmHandshakeState.Completed, result.State);
        Assert.Equal(PickCommand, result.CommandCode);
        Assert.Equal(1, result.Sequence);
        Assert.Equal(1, result.ResultCode);
        Assert.Null(result.ErrorMessage);

        // The trigger must be the final write: a sequence bump before the command code
        // would let Lua branch on a stale command.
        Assert.Equal(options.SequenceVariableKey, controller.WriteLog[^1]);
        Assert.Contains(options.CommandVariableKey, controller.WriteLog);
    }

    [Fact]
    public async Task Dispatch_ShouldArmWatchDogBeforeAnyVariableWrite()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options);
        var session = CreateSession(controller, options);

        var dispatch = session.DispatchAsync(options.DeviceId, Guid.NewGuid(), PickCommand, CancellationToken.None);
        await RunLuaWhenTriggeredAsync(controller, options, _ => (1, null));
        await dispatch;

        Assert.Equal($"watchdog:{options.SequenceVariableKey}", controller.WriteLog[0]);
        Assert.Equal(options.WatchDogTimeoutSeconds, controller.WatchDogTimeoutSeconds);
        Assert.Equal(options.WatchDogAction, controller.WatchDogAction);
    }

    [Fact]
    public async Task Dispatch_ShouldClearPreviousResultBeforeWritingCommand()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options);
        controller.SetVariable(options.ResultVariableKey, -7);
        controller.SetVariable(options.ResultDetailVariableKey, "stale-failure");
        var session = CreateSession(controller, options);

        var dispatch = session.DispatchAsync(options.DeviceId, Guid.NewGuid(), PickCommand, CancellationToken.None);
        await RunLuaWhenTriggeredAsync(controller, options, _ => (1, "fresh"));
        var result = await dispatch;

        Assert.Equal(AuboArmHandshakeState.Completed, result.State);
        Assert.Equal(1, result.ResultCode);

        var resultIndex = IndexOfWrite(controller, options.ResultVariableKey);
        var commandIndex = IndexOfWrite(controller, options.CommandVariableKey);
        Assert.True(resultIndex >= 0 && resultIndex < commandIndex);
    }

    [Fact]
    public async Task Dispatch_ShouldReportFailedWhenLuaPublishesNegativeResult()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options);
        var session = CreateSession(controller, options);

        var dispatch = session.DispatchAsync(options.DeviceId, Guid.NewGuid(), PickCommand, CancellationToken.None);
        await RunLuaWhenTriggeredAsync(controller, options, _ => (-12, "gripper-blocked"));
        var result = await dispatch;

        Assert.Equal(AuboArmHandshakeState.Failed, result.State);
        Assert.Equal(-12, result.ResultCode);
        Assert.Equal("gripper-blocked", result.ErrorMessage);
        Assert.True(result.MayHaveWritten);
    }

    [Fact]
    public async Task Dispatch_ShouldRefuseWhenArmIsNotReady()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options) { SafetyMode = 5 };
        var session = CreateSession(controller, options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DispatchAsync(options.DeviceId, Guid.NewGuid(), PickCommand, CancellationToken.None));

        Assert.Contains("ProtectiveStop", exception.Message, StringComparison.Ordinal);
        Assert.Empty(controller.WriteLog);
    }

    [Fact]
    public async Task Dispatch_ShouldRefuseWhenAHandshakeIsStillInFlight()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options);
        controller.SetVariable(options.CommandVariableKey, PickCommand);
        controller.SetVariable(options.SequenceVariableKey, 9);
        var session = CreateSession(controller, options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DispatchAsync(options.DeviceId, Guid.NewGuid(), PickCommand, CancellationToken.None));

        Assert.Contains("still in flight", exception.Message, StringComparison.Ordinal);
        Assert.Empty(controller.WriteLog);
    }

    [Fact]
    public async Task Dispatch_ShouldRejectCommandCodeOutsideAuthorizedBranches()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options);
        var session = CreateSession(controller, options);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => session.DispatchAsync(options.DeviceId, Guid.NewGuid(), 77, CancellationToken.None));

        Assert.Empty(controller.WriteLog);
    }

    [Fact]
    public async Task Dispatch_ShouldSurfaceUnknownWhenTriggerWriteFails()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options)
        {
            FailWritesForKey = null
        };
        var session = CreateSession(controller, options);
        controller.FailWritesForKey = options.SequenceVariableKey;

        var exception = await Assert.ThrowsAsync<AuboArmOutcomeUnknownException>(
            () => session.DispatchAsync(options.DeviceId, Guid.NewGuid(), PickCommand, CancellationToken.None));

        Assert.Contains("Do not retry automatically", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatch_ShouldSurfaceUnknownWhenLuaNeverPublishesAResult()
    {
        var options = CreateOptions() with { HandshakePollIntervalMs = 50, HandshakeTimeoutMs = 150 };
        var controller = new AuboArmLoopbackController(options);
        var session = CreateSession(controller, options);

        var exception = await Assert.ThrowsAsync<AuboArmOutcomeUnknownException>(
            () => session.DispatchAsync(options.DeviceId, Guid.NewGuid(), PickCommand, CancellationToken.None));

        Assert.Contains("did not reach a terminal Lua result", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Do not retry automatically", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_ShouldRequireAtLeastOneAuthorizedCommandCode()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options);

        Assert.Throws<ArgumentException>(() => new AuboArmControlledHandshakeSession(
            controller,
            new AuboArmReadOnlyDriver(controller, options, TimeProvider.System),
            options,
            TimeProvider.System,
            []));
    }

    [Fact]
    public void ReadOnlyDriver_ShouldNotBeAWriter()
    {
        Assert.False(typeof(IAuboArmHandshakeWriter).IsAssignableFrom(typeof(AuboArmReadOnlyDriver)));
    }

    [Fact]
    public async Task LoopbackHarness_ExecutesOnePickStep_WhenExplicitlyEnabled()
    {
        var options = CreateOptions() with { EnableLoopbackControl = true };
        var controller = new AuboArmLoopbackController(options);
        var harness = new AuboArmLoopbackHarness(options, controller, TimeProvider.System, [3]);
        var step = harness.ExecuteSingleStepAsync(3);
        await RunLuaWhenTriggeredAsync(controller, options, _ => (1, "pick-done"));
        var result = await step;
        Assert.Equal(AuboArmHandshakeState.Completed, result.State);
        Assert.Equal(3, result.CommandCode);
    }

    [Fact]
    public void LoopbackHarness_RequiresExplicitEnablement()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options);
        Assert.Throws<InvalidOperationException>(() => new AuboArmLoopbackHarness(options, controller));
    }

    private static int IndexOfWrite(AuboArmLoopbackController controller, string key)
    {
        for (var index = 0; index < controller.WriteLog.Count; index++)
        {
            if (string.Equals(controller.WriteLog[index], key, StringComparison.Ordinal)) return index;
        }

        return -1;
    }

    private static async Task RunLuaWhenTriggeredAsync(
        AuboArmLoopbackController controller,
        AuboArmOptions options,
        Func<int, (int ResultCode, string? Detail)> ifBlock)
    {
        // Waits for the control centre's trigger write, then behaves like the resident
        // Lua project: echo the sequence, run the if-block, publish the result.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (controller.WriteLog.Contains(options.SequenceVariableKey))
            {
                controller.RunLuaHandshakeCycle(ifBlock);
                return;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException("The session never issued its trigger write.");
    }

    private static AuboArmOptions CreateOptions() =>
        AuboArmOptions.BindAndValidate(new ConfigurationBuilder().Build());

    private static AuboArmControlledHandshakeSession CreateSession(
        AuboArmLoopbackController controller,
        AuboArmOptions options) =>
        new(
            controller,
            new AuboArmReadOnlyDriver(controller, options, TimeProvider.System),
            options,
            TimeProvider.System,
            [PickCommand, 4]);
}
