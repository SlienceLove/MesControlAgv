using System.Text.Json;
using MesControlAgv.Adapter.Modules.AuboArm;
using MesControlAgv.Contracts;
using Microsoft.Extensions.Configuration;

namespace MesControlAgv.Adapter.Tests;

public class AuboArmReadOnlyDriverTests
{
    [Fact]
    public async Task Status_ShouldNormalizeVendorEnums()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options)
        {
            RobotMode = 8,
            SafetyMode = 1,
            RuntimeState = 0,
            OperationalMode = 1
        };
        var driver = CreateDriver(controller, options);

        var status = await driver.GetStatusAsync(options.DeviceId, CancellationToken.None);

        Assert.True(status.Online);
        Assert.Equal(AuboArmMode.Running, status.Mode);
        Assert.Equal(AuboArmSafetyMode.Normal, status.SafetyMode);
        Assert.Equal(AuboArmRuntimeState.Running, status.RuntimeState);
        Assert.Equal(AuboArmOperationalMode.Automatic, status.OperationalMode);
        Assert.Equal(8, status.RawMode);
    }

    [Fact]
    public async Task Status_ShouldReportUnknownForUndocumentedEnumValue()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options) { SafetyMode = 99 };
        var driver = CreateDriver(controller, options);

        var status = await driver.GetStatusAsync(options.DeviceId, CancellationToken.None);

        Assert.Equal(AuboArmSafetyMode.Unknown, status.SafetyMode);
        Assert.Equal(99, status.RawSafetyMode);
    }

    [Fact]
    public async Task Readiness_ShouldBlockWhenSafetyModeIsProtectiveStop()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options) { SafetyMode = 5 };
        var driver = CreateDriver(controller, options);

        var readiness = await driver.GetReadinessAsync(options.DeviceId, CancellationToken.None);

        Assert.False(readiness.Ready);
        Assert.Contains(readiness.BlockingReasons, reason => reason.Contains("ProtectiveStop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Readiness_ShouldBlockWhenControllerIsInManualMode()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options) { OperationalMode = 2 };
        var driver = CreateDriver(controller, options);

        var readiness = await driver.GetReadinessAsync(options.DeviceId, CancellationToken.None);

        Assert.False(readiness.Ready);
        Assert.Contains(readiness.BlockingReasons, reason => reason.Contains("Manual", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Readiness_ShouldBlockWhenStatusIsUnavailable()
    {
        var options = CreateOptions();
        var driver = CreateDriver(new SilentTransport(), options);

        var readiness = await driver.GetReadinessAsync(options.DeviceId, CancellationToken.None);

        Assert.False(readiness.Ready);
        Assert.False(readiness.Status.Online);
        Assert.Contains(readiness.BlockingReasons, reason => reason.Contains("offline", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgramCatalog_ShouldReadPreloadedSlotsAndConfiguredAllowlistWithoutWrites()
    {
        var options = CreateOptions() with
        {
            ProgramCatalogMaxSlots = 3,
            AllowedProgramNames = ["测试1", "测试2"]
        };
        var controller = new AuboArmLoopbackController(options) { LoadedProgram = "测试1" };
        var driver = CreateDriver(controller, options);

        var catalog = await driver.GetProgramCatalogAsync(options.DeviceId, CancellationToken.None);

        Assert.True(catalog.Online);
        Assert.Equal("测试1", catalog.CurrentProgram);
        Assert.Contains("测试1", catalog.AvailablePrograms);
        Assert.Contains("测试2", catalog.AvailablePrograms);
        Assert.True(catalog.IsComplete);
        Assert.Empty(controller.WriteLog);
    }

    [Fact]
    public async Task Variable_ShouldRejectKeyOutsideAllowlist()
    {
        var options = CreateOptions();
        var driver = CreateDriver(new AuboArmLoopbackController(options), options);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => driver.GetVariableAsync(options.DeviceId, "some_other_key", CancellationToken.None));

        Assert.Contains("allowlist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Variable_ShouldReportMissingVariableAsNotExisting()
    {
        var options = CreateOptions();
        var driver = CreateDriver(new AuboArmLoopbackController(options), options);

        var variable = await driver.GetVariableAsync(
            options.DeviceId,
            options.ResultVariableKey,
            CancellationToken.None);

        Assert.False(variable.Exists);
        Assert.Null(variable.Int32Value);
        Assert.Null(variable.RawType);
    }

    [Fact]
    public async Task Variable_ShouldNotCoerceMismatchedVendorType()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options);
        controller.SetVariable(options.ResultVariableKey, "not-a-number");
        var driver = CreateDriver(controller, options);

        var variable = await driver.GetVariableAsync(
            options.DeviceId,
            options.ResultVariableKey,
            CancellationToken.None);

        Assert.True(variable.Exists);
        Assert.Equal("string", variable.RawType);
        Assert.Null(variable.Int32Value);
        Assert.Equal("not-a-number", variable.StringValue);
    }

    [Fact]
    public async Task Handshake_ShouldReportIdleBeforeAnyDispatch()
    {
        var options = CreateOptions();
        var driver = CreateDriver(new AuboArmLoopbackController(options), options);

        var snapshot = await driver.GetHandshakeSnapshotAsync(options.DeviceId, CancellationToken.None);

        Assert.Equal(AuboArmHandshakeState.Idle, snapshot.State);
    }

    [Fact]
    public async Task Handshake_ShouldTrackDispatchedThenCompleted()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options);
        controller.SetVariable(options.CommandVariableKey, 3);
        controller.SetVariable(options.SequenceVariableKey, 7);
        var driver = CreateDriver(controller, options);

        var dispatched = await driver.GetHandshakeSnapshotAsync(options.DeviceId, CancellationToken.None);
        Assert.Equal(AuboArmHandshakeState.Dispatched, dispatched.State);
        Assert.Equal(3, dispatched.CommandCode);

        controller.RunLuaHandshakeCycle(command => command == 3 ? (1, "pick-done") : (-1, "unknown-command"));

        var completed = await driver.GetHandshakeSnapshotAsync(options.DeviceId, CancellationToken.None);
        Assert.Equal(AuboArmHandshakeState.Completed, completed.State);
        Assert.Equal(7, completed.AcknowledgedSequence);
        Assert.Equal(1, completed.ResultCode);
        Assert.Equal("pick-done", completed.ResultDetail);
    }

    [Fact]
    public async Task Handshake_ShouldReportFailedWhenLuaPublishesNegativeResult()
    {
        var options = CreateOptions();
        var controller = new AuboArmLoopbackController(options);
        controller.SetVariable(options.CommandVariableKey, 99);
        controller.SetVariable(options.SequenceVariableKey, 4);
        var driver = CreateDriver(controller, options);

        controller.RunLuaHandshakeCycle(command => command == 3 ? (1, null) : (-1, "unknown-command"));

        var snapshot = await driver.GetHandshakeSnapshotAsync(options.DeviceId, CancellationToken.None);

        Assert.Equal(AuboArmHandshakeState.Failed, snapshot.State);
        Assert.Equal(-1, snapshot.ResultCode);
    }

    [Fact]
    public void Handshake_AcknowledgedWithoutResult_ShouldRemainRunning()
    {
        Assert.Equal(
            AuboArmHandshakeState.Running,
            AuboArmReadOnlyDriver.ClassifyHandshake(sequence: 5, acknowledged: 5, result: 0));
    }

    [Fact]
    public async Task Driver_ShouldRejectUnknownDeviceId()
    {
        var options = CreateOptions();
        var driver = CreateDriver(new AuboArmLoopbackController(options), options);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => driver.GetStatusAsync("OTHER-ARM", CancellationToken.None));
    }

    [Fact]
    public void Options_ShouldRequireDeviceEnablementForControl()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Devices:AuboArm:ControlEnabled"] = "true"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => AuboArmOptions.BindAndValidate(configuration));

        Assert.Contains("requires Enabled=true", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_ShouldAllowExplicitControlInStandardMode()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Devices:AuboArm:Enabled"] = "true",
                ["Devices:AuboArm:ControlEnabled"] = "true",
                ["Devices:AuboArm:Host"] = "192.168.1.102",
                ["Devices:AuboArm:AllowedProgramNames:0"] = "现场批准程序"
            })
            .Build();

        var options = AuboArmOptions.BindAndValidate(configuration);

        Assert.True(options.Enabled);
        Assert.True(options.ControlEnabled);
        Assert.Contains(3, options.AllowedCommandCodes);
    }

    [Fact]
    public void Options_ShouldRejectControlInReadOnlyPreflightMode()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Adapter:RunMode"] = "read-only-preflight",
                ["Devices:AuboArm:Enabled"] = "true",
                ["Devices:AuboArm:ControlEnabled"] = "true",
                ["Devices:AuboArm:Host"] = "192.168.1.102"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => AuboArmOptions.BindAndValidate(configuration));

        Assert.Contains("read-only-preflight", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_ShouldRequireEndpointWhenEnabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Devices:AuboArm:Enabled"] = "true"
            })
            .Build();

        Assert.ThrowsAny<Exception>(() => AuboArmOptions.BindAndValidate(configuration));
    }

    [Fact]
    public void Options_ShouldDefaultToDisabledAndReadOnly()
    {
        var options = AuboArmOptions.BindAndValidate(new ConfigurationBuilder().Build());

        Assert.False(options.Enabled);
        Assert.False(options.ControlEnabled);
        Assert.Equal("rob1", options.RobotName);
        Assert.Equal(15000, options.ProgramCatalogScanTimeoutMs);
        Assert.Equal(30000, options.ProgramCatalogCacheTtlMs);
    }

    [Fact]
    public void Options_ShouldRejectNegativeCatalogCacheTtl()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Devices:AuboArm:ProgramCatalogCacheTtlMs"] = "-1"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => AuboArmOptions.BindAndValidate(configuration));

        Assert.Contains("ProgramCatalogCacheTtlMs", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_ShouldRejectCatalogTimeoutShorterThanConfiguredPacingBudget()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Devices:AuboArm:ProgramCatalogMaxSlots"] = "100",
                ["Devices:AuboArm:ProgramCatalogInterRequestDelayMs"] = "100",
                ["Devices:AuboArm:ProgramCatalogScanTimeoutMs"] = "5000"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => AuboArmOptions.BindAndValidate(configuration));

        Assert.Contains("slot pacing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Options_ShouldRejectDuplicateHandshakeKeys()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Devices:AuboArm:CommandVariableKey"] = "same",
                ["Devices:AuboArm:ResultVariableKey"] = "same"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => AuboArmOptions.BindAndValidate(configuration));

        Assert.Contains("distinct", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_AllowlistShouldAlwaysIncludeHandshakeKeys()
    {
        var options = CreateOptions();

        var allowlist = options.BuildReadableVariableAllowlist();

        Assert.Contains(options.CommandVariableKey, allowlist);
        Assert.Contains(options.SequenceVariableKey, allowlist);
        Assert.Contains(options.AcknowledgeVariableKey, allowlist);
        Assert.Contains(options.ResultVariableKey, allowlist);
        Assert.Contains(options.ResultDetailVariableKey, allowlist);
    }

    private static AuboArmOptions CreateOptions() =>
        AuboArmOptions.BindAndValidate(new ConfigurationBuilder().Build());

    private static AuboArmReadOnlyDriver CreateDriver(
        IAuboArmReadOnlyRpcTransport transport,
        AuboArmOptions options) =>
        new(transport, options, TimeProvider.System);

    private sealed class SilentTransport : IAuboArmReadOnlyRpcTransport
    {
        public Task<JsonElement> InvokeAsync(
            string method,
            IReadOnlyList<object?> parameters,
            CancellationToken cancellationToken) =>
            throw new AuboArmRpcException(method, -1, "controller unavailable");
    }
}
