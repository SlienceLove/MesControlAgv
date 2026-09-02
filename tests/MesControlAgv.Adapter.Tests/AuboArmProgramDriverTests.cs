using MesControlAgv.Adapter.Modules.AuboArm;
using MesControlAgv.Adapter.Modules;
using MesControlAgv.Contracts;
using Microsoft.Extensions.Configuration;

namespace MesControlAgv.Adapter.Tests;

public sealed class AuboArmProgramDriverTests
{
    [Fact]
    public async Task LoadRunAndStop_use_the_explicit_project_and_safe_states()
    {
        var options = CreateOptions() with { Enabled = true, ControlEnabled = true, Host = "127.0.0.1", AllowedProgramNames = ["测试"] };
        var controller = new AuboArmLoopbackController(options)
        {
            RuntimeState = 6,
            OperationalMode = 1
        };
        var reader = new AuboArmReadOnlyDriver(controller, options, TimeProvider.System);
        var driver = new AuboArmProgramDriver(controller, reader, options, TimeProvider.System);
        var operationId = Guid.NewGuid();

        var loaded = await driver.LoadProgramAsync(
            options.DeviceId, "测试.pro", "operator", operationId, CancellationToken.None);
        Assert.Equal(AuboArmProgramOperationState.Loaded, loaded.State);
        Assert.Equal("测试", loaded.LoadedProgram);

        var running = await driver.RunProgramAsync(
            options.DeviceId, "测试", "operator", Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(AuboArmProgramOperationState.Running, running.State);

        var stopped = await driver.StopProgramAsync(
            options.DeviceId, "operator", Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(AuboArmProgramOperationState.Stopped, stopped.State);
    }

    [Fact]
    public async Task Run_refuses_a_project_that_is_not_loaded_and_does_not_write()
    {
        var options = CreateOptions() with { Enabled = true, ControlEnabled = true, Host = "127.0.0.1", AllowedProgramNames = ["测试"] };
        var controller = new AuboArmLoopbackController(options) { RuntimeState = 6 };
        var reader = new AuboArmReadOnlyDriver(controller, options, TimeProvider.System);
        var driver = new AuboArmProgramDriver(controller, reader, options, TimeProvider.System);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => driver.RunProgramAsync(
            options.DeviceId, "测试", "operator", Guid.NewGuid(), CancellationToken.None));

        Assert.Contains("No AUBO project", exception.Message, StringComparison.Ordinal);
        Assert.Empty(controller.WriteLog);
    }

    [Fact]
    public async Task Run_without_a_name_still_requires_the_loaded_project_to_be_allowlisted()
    {
        var options = CreateOptions() with
        {
            Enabled = true,
            ControlEnabled = true,
            Host = "127.0.0.1",
            AllowedProgramNames = ["测试"]
        };
        var controller = new AuboArmLoopbackController(options)
        {
            LoadedProgram = "未批准",
            RuntimeState = 6,
            OperationalMode = 1
        };
        var reader = new AuboArmReadOnlyDriver(controller, options, TimeProvider.System);
        var driver = new AuboArmProgramDriver(controller, reader, options, TimeProvider.System);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => driver.RunProgramAsync(
            options.DeviceId, null, "operator", Guid.NewGuid(), CancellationToken.None));

        Assert.Contains("allowlist", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(controller.WriteLog);
    }

    [Fact]
    public async Task Program_mutations_are_disabled_without_explicit_control_enablement()
    {
        var options = CreateOptions() with { Enabled = true, Host = "127.0.0.1" };
        var controller = new AuboArmLoopbackController(options) { RuntimeState = 6 };
        var reader = new AuboArmReadOnlyDriver(controller, options, TimeProvider.System);
        var driver = new AuboArmProgramDriver(controller, reader, options, TimeProvider.System);

        await Assert.ThrowsAsync<DeviceControlDisabledException>(() => driver.LoadProgramAsync(
            options.DeviceId, "测试", "operator", Guid.NewGuid(), CancellationToken.None));
        Assert.Empty(controller.WriteLog);
    }

    private static AuboArmOptions CreateOptions() =>
        AuboArmOptions.BindAndValidate(new ConfigurationBuilder().Build());
}
