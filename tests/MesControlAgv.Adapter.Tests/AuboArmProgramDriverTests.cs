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
    public async Task Correlated_program_operations_echo_the_durable_identity_and_share_operation_id()
    {
        var options = CreateOptions() with
        {
            Enabled = true,
            ControlEnabled = true,
            Host = "127.0.0.1",
            AllowedProgramNames = ["娴嬭瘯"]
        };
        var controller = new AuboArmLoopbackController(options)
        {
            RuntimeState = 6,
            OperationalMode = 1
        };
        var reader = new AuboArmReadOnlyDriver(controller, options, TimeProvider.System);
        var driver = new AuboArmProgramDriver(controller, reader, options, TimeProvider.System);
        var operationId = Guid.NewGuid();
        var correlation = AuboArmOperationCorrelation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            operationId,
            Guid.NewGuid(),
            "phase3c-correlation");

        var loaded = await driver.LoadProgramAsync(
            options.DeviceId, "娴嬭瘯", "operator", operationId, correlation, CancellationToken.None);
        var running = await driver.RunProgramAsync(
            options.DeviceId, "娴嬭瘯", "operator", operationId, correlation, CancellationToken.None);

        Assert.True(loaded.IsWorkflowCorrelated);
        Assert.Equal(correlation.WorkflowRunId, loaded.WorkflowRunId);
        Assert.Equal(correlation.WorkflowNodeExecutionId, running.WorkflowNodeExecutionId);
        Assert.Equal(operationId, loaded.DeviceOperationId);
        Assert.Equal(operationId, running.OperationId);
        Assert.Equal("phase3c-correlation", running.CorrelationId);
        Assert.Null(loaded.CorrelationWarningCode);
    }

    [Fact]
    public async Task Correlated_write_rejects_an_operation_id_mismatch_before_rpc()
    {
        var options = CreateOptions() with
        {
            Enabled = true,
            ControlEnabled = true,
            Host = "127.0.0.1",
            AllowedProgramNames = ["娴嬭瘯"]
        };
        var controller = new AuboArmLoopbackController(options) { RuntimeState = 6 };
        var reader = new AuboArmReadOnlyDriver(controller, options, TimeProvider.System);
        var driver = new AuboArmProgramDriver(controller, reader, options, TimeProvider.System);
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => driver.LoadProgramAsync(
            options.DeviceId,
            "娴嬭瘯",
            "operator",
            Guid.NewGuid(),
            AuboArmOperationCorrelation.Create(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "mismatch"),
            CancellationToken.None));

        Assert.Contains("DeviceOperationId", exception.Message, StringComparison.Ordinal);
        Assert.Empty(controller.WriteLog);
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

    [Fact]
    public async Task Successful_load_invalidates_the_catalog_cache()
    {
        var options = CreateOptions() with
        {
            Enabled = true,
            ControlEnabled = true,
            Host = "127.0.0.1",
            AllowedProgramNames = ["旧程序", "新程序"],
            ProgramCatalogMaxSlots = 1,
            ProgramCatalogInterRequestDelayMs = 0
        };
        var controller = new AuboArmLoopbackController(options)
        {
            LoadedProgram = "旧程序",
            RuntimeState = 6,
            OperationalMode = 1
        };
        var cache = new AuboArmProgramCatalogCache(TimeProvider.System);
        var reader = new AuboArmReadOnlyDriver(
            controller, options, TimeProvider.System, null, cache);
        var driver = new AuboArmProgramDriver(
            controller, reader, options, TimeProvider.System, null, cache);

        var before = await driver.GetProgramCatalogAsync(options.DeviceId, CancellationToken.None);
        Assert.False(before.IsCached);
        var cached = await driver.GetProgramCatalogAsync(options.DeviceId, CancellationToken.None);
        Assert.True(cached.IsCached);

        var load = await driver.LoadProgramAsync(
            options.DeviceId, "新程序", "operator", Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(AuboArmProgramOperationState.Loaded, load.State);

        var after = await driver.GetProgramCatalogAsync(options.DeviceId, CancellationToken.None);
        Assert.False(after.IsCached);
        Assert.Equal("新程序", after.CurrentProgram);
    }

    [Fact]
    public async Task Run_and_stop_also_invalidate_the_catalog_cache()
    {
        var options = CreateOptions() with
        {
            Enabled = true,
            ControlEnabled = true,
            Host = "127.0.0.1",
            AllowedProgramNames = ["程序"],
            ProgramCatalogMaxSlots = 1,
            ProgramCatalogInterRequestDelayMs = 0
        };
        var controller = new AuboArmLoopbackController(options)
        {
            LoadedProgram = "程序",
            RuntimeState = 6,
            OperationalMode = 1
        };
        var cache = new AuboArmProgramCatalogCache(TimeProvider.System);
        var reader = new AuboArmReadOnlyDriver(controller, options, TimeProvider.System, null, cache);
        var driver = new AuboArmProgramDriver(controller, reader, options, TimeProvider.System, null, cache);

        _ = await driver.GetProgramCatalogAsync(options.DeviceId, CancellationToken.None);
        Assert.True((await driver.GetProgramCatalogAsync(options.DeviceId, CancellationToken.None)).IsCached);
        var run = await driver.RunProgramAsync(
            options.DeviceId, "程序", "operator", Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(AuboArmProgramOperationState.Running, run.State);
        Assert.False((await driver.GetProgramCatalogAsync(options.DeviceId, CancellationToken.None)).IsCached);

        var stop = await driver.StopProgramAsync(
            options.DeviceId, "operator", Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(AuboArmProgramOperationState.Stopped, stop.State);
        Assert.False((await driver.GetProgramCatalogAsync(options.DeviceId, CancellationToken.None)).IsCached);
    }

    [Fact]
    public async Task Manual_program_write_returns_an_explicit_uncorrelated_warning()
    {
        var options = CreateOptions() with
        {
            Enabled = true,
            ControlEnabled = true,
            Host = "127.0.0.1",
            AllowedProgramNames = ["绋嬪簭"]
        };
        var controller = new AuboArmLoopbackController(options)
        {
            LoadedProgram = "绋嬪簭",
            RuntimeState = 6,
            OperationalMode = 1
        };
        var reader = new AuboArmReadOnlyDriver(controller, options, TimeProvider.System);
        var driver = new AuboArmProgramDriver(controller, reader, options, TimeProvider.System);

        var result = await driver.RunProgramAsync(
            options.DeviceId, "绋嬪簭", "operator", Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("AUBO_UNCORRELATED_WRITE", result.CorrelationWarningCode);
        Assert.False(result.IsWorkflowCorrelated);
    }

    private static AuboArmOptions CreateOptions() =>
        AuboArmOptions.BindAndValidate(new ConfigurationBuilder().Build());
}
