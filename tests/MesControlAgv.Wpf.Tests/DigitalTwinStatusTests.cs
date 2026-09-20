using System.Net;
using System.Net.Http;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.DigitalTwin;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class DigitalTwinStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);
    private static AuboArmStatusResponse Arm() => new("ARM-01", "robot", true, AuboArmMode.Idle, null,
        AuboArmSafetyMode.Normal, null, AuboArmRuntimeState.Stopped, null, AuboArmOperationalMode.Automatic, null, Now);
    private static SampleWorkstationDashboardSnapshot Workstation() => new(
        new("SAMPLE-WORKSTATION-01", "1", true, SampleWorkstationDeviceState.Running, 1, Now),
        new("SAMPLE-WORKSTATION-01", 0, "无告警", true, Now), []);

    [Fact]
    public void Agv_identity_must_match_exactly_and_observation_time_is_not_invented()
    {
        AgvFleetDashboardStatus item = new(new(true, "MES", "LM1", null, "AGV-01"), null);
        var reading = TwinProjection.Agv([item], "AGV-01", Now);
        Assert.Equal(TwinCondition.Online, reading.Condition);
        Assert.Null(reading.ObservedAt);
        Assert.Contains("未提供", reading.TimeText);
        Assert.Equal(TwinCondition.Unavailable, TwinProjection.Agv([item], "AGV-02", Now).Condition);
        Assert.Equal(TwinCondition.Unavailable, TwinProjection.Agv([item, item], "AGV-01", Now).Condition);
    }

    [Theory]
    [InlineData(AuboArmSafetyMode.Normal, TwinCondition.Idle)]
    [InlineData(AuboArmSafetyMode.ProtectiveStop, TwinCondition.Fault)]
    [InlineData(AuboArmSafetyMode.SystemEmergencyStop, TwinCondition.Fault)]
    [InlineData(AuboArmSafetyMode.Unknown, TwinCondition.Unknown)]
    [InlineData((AuboArmSafetyMode)999, TwinCondition.Unknown)]
    public void Arm_safety_is_not_hidden_by_online(AuboArmSafetyMode safety, TwinCondition expected) =>
        Assert.Equal(expected, TwinProjection.Arm(Arm() with { SafetyMode = safety }, "ARM-01", Now).Condition);

    [Theory]
    [InlineData(AuboArmRuntimeState.Stopped, TwinCondition.Idle, "程序已停止")]
    [InlineData(AuboArmRuntimeState.Running, TwinCondition.Running, "运行中")]
    [InlineData(AuboArmRuntimeState.Stepping, TwinCondition.Running, "运行中")]
    [InlineData(AuboArmRuntimeState.Stopping, TwinCondition.Paused, "暂停／待确认")]
    [InlineData(AuboArmRuntimeState.Unknown, TwinCondition.Unknown, "状态未知")]
    public void Powered_arm_mode_does_not_override_program_runtime_state(
        AuboArmRuntimeState runtime, TwinCondition expected, string text)
    {
        var reading = TwinProjection.Arm(Arm() with { Mode = AuboArmMode.Running, RuntimeState = runtime }, "ARM-01", Now);
        Assert.Equal(expected, reading.Condition);
        Assert.Equal(text, reading.Text);
    }

    [Fact]
    public void Stopped_program_does_not_hide_a_protective_stop()
    {
        var reading = TwinProjection.Arm(Arm() with
        {
            Mode = AuboArmMode.Running, RuntimeState = AuboArmRuntimeState.Stopped,
            SafetyMode = AuboArmSafetyMode.ProtectiveStop
        }, "ARM-01", Now);
        Assert.Equal(TwinCondition.Fault, reading.Condition);
    }

    [Theory]
    [InlineData(-11, TwinCondition.Stale)]
    [InlineData(-10, TwinCondition.Idle)]
    [InlineData(6, TwinCondition.Unknown)]
    public void Observation_time_is_checked(int offsetSeconds, TwinCondition expected) =>
        Assert.Equal(expected, TwinProjection.Arm(Arm() with { ObservedAtUtc = Now.AddSeconds(offsetSeconds) }, "ARM-01", Now).Condition);

    [Fact]
    public void Null_wrong_identity_and_missing_time_are_not_healthy()
    {
        Assert.Equal(TwinCondition.Unavailable, TwinProjection.Arm(null, "ARM-01", Now).Condition);
        Assert.Equal(TwinCondition.Unavailable, TwinProjection.Arm(Arm(), "ARM-02", Now).Condition);
        Assert.Equal(TwinCondition.Unknown, TwinProjection.Arm(Arm() with { ObservedAtUtc = default }, "ARM-01", Now).Condition);
        Assert.Equal(TwinCondition.Offline, TwinProjection.Arm(Arm() with { Online = false }, "ARM-01", Now).Condition);
    }

    [Fact]
    public void Workstation_partial_failure_does_not_drop_valid_status_or_look_healthy()
    {
        var reading = TwinProjection.Workstation(Workstation() with { Error = null, ErrorReadError = "HTTP 500" }, "SAMPLE-WORKSTATION-01", Now);
        Assert.Equal(TwinCondition.Running, reading.Condition);
        Assert.True(reading.Partial);
        Assert.Equal("warning", reading.Tone);
        Assert.Contains("部分", reading.DisplayText);
        Assert.Equal(TwinCondition.Unavailable, TwinProjection.Workstation(Workstation(), "OTHER", Now).Condition);
    }

    [Fact]
    public void Stale_or_wrong_device_alarm_is_not_applied_as_current_alarm()
    {
        var snapshot = Workstation();
        var reading = TwinProjection.Workstation(snapshot with { Error = snapshot.Error! with { DeviceId = "OTHER", ErrorCode = 17 } },
            "SAMPLE-WORKSTATION-01", Now);
        Assert.Equal(TwinCondition.Running, reading.Condition);
        Assert.True(reading.Partial);
        var fault = TwinProjection.Workstation(snapshot with { Error = snapshot.Error! with { ErrorCode = -2 } }, "SAMPLE-WORKSTATION-01", Now);
        Assert.Equal(TwinCondition.Fault, fault.Condition);
        var stale = TwinProjection.Workstation(snapshot with { Error = snapshot.Error! with { ErrorCode = 17, ObservedAtUtc = Now.AddMinutes(-1) } }, "SAMPLE-WORKSTATION-01", Now);
        Assert.Equal(TwinCondition.Running, stale.Condition);
        Assert.True(stale.Partial);
    }

    [Theory]
    [InlineData(0, TwinCondition.Running)]
    [InlineData(1, TwinCondition.Paused)]
    [InlineData(2, TwinCondition.Fault)]
    [InlineData(3, TwinCondition.Running)]
    [InlineData(-1, TwinCondition.Fault)]
    [InlineData(-2, TwinCondition.Fault)]
    [InlineData(-3, TwinCondition.Fault)]
    [InlineData(-4, TwinCondition.Fault)]
    public void Workstation_result_codes_are_not_instrument_state_codes(int code, TwinCondition expected)
    {
        var snapshot = Workstation();
        var reading = TwinProjection.Workstation(snapshot with { Error = snapshot.Error! with { ErrorCode = code } },
            "SAMPLE-WORKSTATION-01", Now);
        Assert.Equal(expected, reading.Condition);
        Assert.False(reading.Partial);
        Assert.Contains("结果码", reading.Detail);
    }

    [Fact]
    public void Unknown_result_is_incomplete_and_state_fault_still_wins_over_started_code()
    {
        var snapshot = Workstation();
        Assert.True(TwinProjection.Workstation(snapshot with { Error = snapshot.Error! with { ErrorCode = 999 } },
            "SAMPLE-WORKSTATION-01", Now).Partial);
        var fault = TwinProjection.Workstation(snapshot with
        {
            Status = snapshot.Status! with { State = SampleWorkstationDeviceState.Faulted },
            Error = snapshot.Error! with { ErrorCode = 3 }
        }, "SAMPLE-WORKSTATION-01", Now);
        Assert.Equal(TwinCondition.Fault, fault.Condition);
    }

    [Fact]
    public void Unrecognized_workstation_error_is_not_treated_as_no_alarm()
    {
        var snapshot = Workstation();
        var reading = TwinProjection.Workstation(snapshot with
        {
            Error = snapshot.Error! with { Recognized = false, ErrorCode = null, Description = "UndocumentedVendorErrorPayload" }
        }, "SAMPLE-WORKSTATION-01", Now);
        Assert.True(reading.Partial);
        Assert.Equal("warning", reading.Tone);
        Assert.Contains("告警未识别", reading.Detail);
    }

    [Theory]
    [InlineData(TwinCondition.Unavailable)]
    [InlineData(TwinCondition.Offline)]
    [InlineData(TwinCondition.Stale)]
    [InlineData(TwinCondition.Unknown)]
    [InlineData(TwinCondition.Fault)]
    public void Composite_does_not_hide_a_bad_channel(TwinCondition condition)
    {
        var agv = new TwinReading(TwinChannel.Agv, "AGV-01", TwinCondition.Running, "运行中", "", Now);
        var arm = new TwinReading(TwinChannel.Arm, "ARM-01", condition, "异常", "", Now);
        Assert.Equal(condition, TwinProjection.Composite(agv, arm).Condition);
    }

    [Fact]
    public void Freshness_revokes_success_and_viewmodel_rejects_mismatched_identity()
    {
        var reading = new TwinReading(TwinChannel.Agv, "AGV-01", TwinCondition.Online, "在线", "", Now);
        Assert.Equal(TwinCondition.Stale, TwinProjection.Freshness(reading, Now.AddSeconds(11)).Condition);
        var vm = new DigitalTwinViewModel();
        vm.ApplyReading(reading with { EquipmentId = "OTHER" });
        Assert.Equal(TwinCondition.Unavailable, vm.Readings[0].Condition);
        Assert.Equal("AGV-01", vm.Readings[0].EquipmentId);
        Assert.Equal(3, vm.Readings.Count);
        Assert.Contains("未绑定", DigitalTwinScene.Find("d160-a")!.Name);
        Assert.Contains("未绑定", DigitalTwinScene.Find("autosampler-a")!.Name);
    }

    [Fact]
    public async Task Provider_uses_only_expected_get_routes()
    {
        var handler = new ReadOnlyHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://example.invalid/") };
        var source = new MesDigitalTwinStatusSource(new MesClient(http), RuntimeConnectionSource.LocalSimulator);
        foreach (var channel in Enum.GetValues<TwinChannel>()) await source.ReadAsync(channel, CancellationToken.None);
        Assert.Contains("非现场", source.SourceDisplay);
        Assert.Equal(new[] { "/api/agvs/fleet/status", "/api/robot-arms/ARM-01/status",
            "/api/workstations/SAMPLE-WORKSTATION-01/status", "/api/workstations/SAMPLE-WORKSTATION-01/errors",
            "/api/workstations/SAMPLE-WORKSTATION-01/tasks" }, handler.Paths);
    }

    [Fact]
    public async Task A_slow_channel_does_not_block_other_channels_or_overlap_itself()
    {
        var gate = new TaskCompletionSource<TwinReading>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TestSource((channel, token) => channel == TwinChannel.Arm ? gate.Task : Task.FromResult(Online(channel)));
        using var session = new DigitalTwinStatusSession(source, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(40));
        var failedArm = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gotStation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Reading += r =>
        {
            if (r.Channel == TwinChannel.Arm && r.Condition == TwinCondition.Unavailable) failedArm.TrySetResult();
            if (r.Channel == TwinChannel.Workstation) gotStation.TrySetResult();
        };
        session.Start();
        await Task.WhenAll(failedArm.Task, gotStation.Task).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, source.ArmReads);
        session.Dispose();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        gate.SetResult(Online(TwinChannel.Arm));
    }

    [Fact]
    public async Task Cancellation_ignores_late_result_and_stops_polling()
    {
        var gate = new TaskCompletionSource<TwinReading>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TestSource((_, _) => gate.Task);
        var reads = 0;
        using var session = new DigitalTwinStatusSession(source);
        session.Reading += _ => Interlocked.Increment(ref reads);
        session.Start(); session.Dispose();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        gate.SetResult(Online(TwinChannel.Agv));
        Assert.Equal(0, reads);
        Assert.Throws<ObjectDisposedException>(() => session.Start());
    }

    private static TwinReading Online(TwinChannel channel) => new(channel, new TwinBindings().Id(channel), TwinCondition.Online, "在线", "", DateTimeOffset.UtcNow);
    private sealed class TestSource(Func<TwinChannel, CancellationToken, Task<TwinReading>> read) : IDigitalTwinStatusSource
    {
        private int _armReads;
        public int ArmReads => _armReads;
        public string SourceDisplay => "测试数据";
        public TwinBindings Bindings { get; } = new();
        public Task<TwinReading> ReadAsync(TwinChannel channel, CancellationToken cancellationToken)
        {
            if (channel == TwinChannel.Arm) Interlocked.Increment(ref _armReads);
            return read(channel, cancellationToken);
        }
    }
    private sealed class ReadOnlyHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                path.Contains("fleet") || path.EndsWith("/tasks") ? "[]" : "null", System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
