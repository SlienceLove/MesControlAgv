using System.Collections.ObjectModel;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class InstrumentCurrentTaskTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T03:00:00Z");
    private static ShineLabDeviceStatusResponse Device() => new("STN61_01", "离子色谱", true, 1, "Running", true,
        null, "123456789", null, null, null, null, null, null, null, Now) { LastDeviceStatusAtUtc = Now };
    private static ShineLabOperatorTaskRow Record(string taskId = "task-exact", string equipment = "STN61_01",
        string sampleId = "123456789", string name = "检测样品", string source = "BIZ-01")
    {
        var task = new ShineLabTaskResponse(Guid.NewGuid(), taskId, equipment, "Running", "Running", Now, Now, Now, null, null);
        return new(task, new(task, [], [new(1, source, sampleId, name, true, false, false, false, null, false, null)]));
    }
    private static string Ion(ShineLabDeviceStatusResponse? device, params ShineLabOperatorTaskRow[] rows) =>
        InstrumentCurrentTaskPresenter.Ion(device, true, Now, rows, Now);

    [Fact]
    public void Ion_matches_exact_wire_identity_not_latest_or_selected_task()
    {
        var exact = Record(); var newer = Record("new-task", sampleId: "987654321", name: "不是当前任务");
        Assert.Equal("运行中 · 检测样品 · BIZ-01", Ion(Device(), newer, exact));
        Assert.DoesNotContain("123456789", Ion(Device(), exact));
        Assert.Equal("运行中 · 检测样品 · BIZ-01", Ion(Device() with { SampleId = null, TaskUuid = "task-exact" }, exact));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("duplicate")]
    [InlineData("other-device")]
    [InlineData("conflicting-task")]
    [InlineData("multi-sample")]
    [InlineData("wrong-detail")]
    public void Ion_does_not_guess_when_identity_is_missing_ambiguous_or_inconsistent(string mode)
    {
        var record = Record(); var device = Device();
        ShineLabOperatorTaskRow[] records = [record];
        switch (mode)
        {
            case "absent": device = device with { SampleId = null }; break;
            case "duplicate": records = [record, record]; break;
            case "other-device": records = [Record(equipment: "OTHER")]; break;
            case "conflicting-task": device = device with { TaskUuid = "OTHER" }; break;
            case "multi-sample":
                device = device with { SampleId = null, TaskUuid = record.Task.TaskUuid };
                records = [record with { Detail = record.Detail! with { Samples = [record.Detail.Samples![0], Record(sampleId: "OTHER").Detail!.Samples![0]] } }];
                break;
            case "wrong-detail": records = [record with { Detail = Record("OTHER").Detail }]; break;
        }
        Assert.Equal(InstrumentCurrentTaskPresenter.RunningUnidentified, Ion(device, records));
    }

    [Fact]
    public void Ion_live_name_is_allowed_but_wire_id_is_never_shown_as_business_number()
    {
        Assert.Equal("运行中 · AQ手动样品 · 编号待确认", Ion(Device() with { SampleName = "AQ手动样品" }));
    }

    [Theory]
    [InlineData("idle", "当前无运行任务")]
    [InlineData("offline", "设备离线，当前任务无法确认")]
    [InlineData("instrument-offline", "设备离线，当前任务无法确认")]
    [InlineData("ended", InstrumentCurrentTaskPresenter.Unconfirmed)]
    [InlineData("conflict", InstrumentCurrentTaskPresenter.Unconfirmed)]
    [InlineData("stale", InstrumentCurrentTaskPresenter.Unconfirmed)]
    [InlineData("future", InstrumentCurrentTaskPresenter.Unconfirmed)]
    public void Ion_state_overrides_historical_running_rows(string mode, string expected)
    {
        var device = mode switch
        {
            "idle" => Device() with { Status = 0, State = "Idle", HasActiveTask = false },
            "offline" => Device() with { Online = false },
            "instrument-offline" => Device() with { Status = 99, State = "InstrumentOffline" },
            "ended" => Device() with { TaskFinishedAtUtc = Now },
            "conflict" => Device() with { HasActiveTask = false },
            "stale" => Device() with { LastSeenAtUtc = Now.AddSeconds(-61) },
            _ => Device() with { LastSeenAtUtc = Now.AddSeconds(1) }
        };
        Assert.Equal(expected, Ion(device, Record()));
    }

    [Fact]
    public void Ion_failed_or_expired_reads_hide_old_identity_even_if_device_object_says_online()
    {
        Assert.Equal(InstrumentCurrentTaskPresenter.Unconfirmed, InstrumentCurrentTaskPresenter.Ion(Device(), false, Now, [Record()], Now));
        Assert.Equal(InstrumentCurrentTaskPresenter.Unconfirmed, InstrumentCurrentTaskPresenter.Ion(Device(), true, Now.AddSeconds(-61), [Record()], Now));
        Assert.Equal(InstrumentCurrentTaskPresenter.Unconfirmed, Ion(Device() with { LastDeviceStatusAtUtc = Now.AddSeconds(-61) }, Record()));
        Assert.Equal(InstrumentCurrentTaskPresenter.Unconfirmed, Ion(Device() with { LastDeviceStatusAtUtc = null }, Record()));
    }

    private static SampleWorkstationDashboardSnapshot Snapshot() => new(
        new("SAMPLE-WORKSTATION-01", "WS", true, SampleWorkstationDeviceState.Running, 1, Now), null,
        [new(10, "WAIT", "最新但未运行", SampleWorkstationTaskState.Waiting, "0", "", null),
         new(1, "RUN-01", "分液当前任务", SampleWorkstationTaskState.Running, "1", "", null)]);

    [Theory]
    [InlineData("running", "运行中 · 分液当前任务 · RUN-01")]
    [InlineData("idle", "当前无运行任务")]
    [InlineData("offline", "设备离线，当前任务无法确认")]
    [InlineData("paused", InstrumentCurrentTaskPresenter.Unconfirmed)]
    [InlineData("status-error", InstrumentCurrentTaskPresenter.Unconfirmed)]
    [InlineData("tasks-error", InstrumentCurrentTaskPresenter.RunningUnidentified)]
    [InlineData("multiple", InstrumentCurrentTaskPresenter.RunningUnidentified)]
    [InlineData("missing", InstrumentCurrentTaskPresenter.RunningUnidentified)]
    [InlineData("stale", InstrumentCurrentTaskPresenter.Unconfirmed)]
    public void Workstation_requires_live_state_and_one_running_task(string mode, string expected)
    {
        var snapshot = Snapshot();
        snapshot = mode switch
        {
            "idle" => snapshot with { Status = snapshot.Status! with { State = SampleWorkstationDeviceState.Idle } },
            "offline" => snapshot with { Status = snapshot.Status! with { Online = false } },
            "paused" => snapshot with { Status = snapshot.Status! with { State = SampleWorkstationDeviceState.Paused } },
            "status-error" => snapshot with { StatusReadError = "failed" },
            "tasks-error" => snapshot with { TasksReadError = "failed" },
            "multiple" => snapshot with { Tasks = [snapshot.Tasks[1], snapshot.Tasks[1]] },
            "missing" => snapshot with { Tasks = [] },
            "stale" => snapshot with { Status = snapshot.Status! with { ObservedAtUtc = Now.AddSeconds(-61) } },
            _ => snapshot
        };
        Assert.Equal(expected, InstrumentCurrentTaskPresenter.Workstation(snapshot, Now, Now));
    }

    [Fact]
    public async Task View_model_updates_on_observations_and_record_mapping_and_hides_identity_on_failure()
    {
        var records = new ObservableCollection<ShineLabOperatorTaskRow>();
        var mes = new FakeMesClient([]) { ShineLabDeviceStatuses = [Device() with { LastSeenAtUtc = DateTimeOffset.UtcNow, LastDeviceStatusAtUtc = DateTimeOffset.UtcNow }] };
        using var model = new ShineLabDeviceStatusViewModel(mes, ionTaskRecords: records);
        await model.RefreshAsync();
        Assert.Equal(InstrumentCurrentTaskPresenter.RunningUnidentified, model.IonCurrentTaskSummary);
        var changed = new List<string?>(); model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        records.Add(Record());
        Assert.Equal("运行中 · 检测样品 · BIZ-01", model.IonCurrentTaskSummary);
        Assert.Contains(nameof(model.IonCurrentTaskSummary), changed);
        mes.ShineLabDeviceStatusException = new HttpRequestException("offline");
        await model.RefreshAsync();
        Assert.Equal(InstrumentCurrentTaskPresenter.Unconfirmed, model.IonCurrentTaskSummary);
        Assert.Single(model.Devices); // Historical rows need not be destructively cleared.
        mes.ShineLabDeviceStatusException = null;
        mes.ShineLabDeviceStatuses = [Device() with { LastSeenAtUtc = DateTimeOffset.UtcNow, LastDeviceStatusAtUtc = DateTimeOffset.UtcNow, Status = 0, State = "Idle", HasActiveTask = false }];
        await model.RefreshAsync();
        Assert.Equal("当前无运行任务", model.IonCurrentTaskSummary);
        Assert.Null(mes.LastShineLabCommand);
        Assert.Equal(0, mes.SampleWorkstationStartCallCount);
    }

    [Fact]
    public async Task Workstation_view_model_keeps_old_list_but_does_not_reuse_it_as_current_after_read_failure()
    {
        var mes = new FakeMesClient([]) { SampleWorkstationSnapshot = Snapshot() with
        { Status = Snapshot().Status! with { ObservedAtUtc = DateTimeOffset.UtcNow } } };
        using var model = new ShineLabDeviceStatusViewModel(mes) { SelectedInstrument = ShineLabDeviceStatusViewModel.SampleWorkstationInstrument };
        await model.RefreshAsync();
        Assert.Contains("RUN-01", model.WorkstationCurrentTaskSummary);
        model.SelectedWorkstationTask = model.WorkstationTasks.First(t => t.TaskNo == "WAIT");
        Assert.Contains("RUN-01", model.WorkstationCurrentTaskSummary);
        mes.SampleWorkstationSnapshot = new(null, null, [], "failed", "failed", "failed");
        await model.RefreshAsync();
        Assert.Equal(2, model.WorkstationTasks.Count);
        Assert.Equal(InstrumentCurrentTaskPresenter.Unconfirmed, model.WorkstationCurrentTaskSummary);
    }
}
