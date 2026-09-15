using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// Small read-only projection used by the ion-chromatography sub-device list.
/// Keeping the display text here avoids exposing task/sample fields in the
/// overview table while preserving the full response for the selected-device
/// detail panel and existing integrations.
/// </summary>
public sealed record ShineLabSubdeviceStatusRow(
    string DeviceName,
    string EquipmentCode,
    string OnlineText,
    string StateText,
    DateTimeOffset LastSeenAtUtc);

/// <summary>
/// Displays device status from MES.  Ion chromatography and the sample
/// opening/dispensing workstation are separate top-level sources: ShineLab
/// status responses are used only for the ion-chromatography sub-device list,
/// while the opening/dispensing page consumes the normalized workstation
/// read-only contract.
/// </summary>
public sealed class ShineLabDeviceStatusViewModel : INotifyPropertyChanged, IDisposable
{
    public const string IonChromatographyInstrument = "离子色谱";
    public const string SampleWorkstationInstrument = "开盖分液";
    public const string DefaultSampleWorkstationDeviceId = "SAMPLE-WORKSTATION-01";

    private readonly IMesClient _mes;
    private readonly string _sampleWorkstationDeviceId;
    private readonly bool _sampleWorkstationTestControlEnabled;
    private readonly ISampleWorkstationTestConfirmation _sampleWorkstationTestConfirmation;
    private readonly TimeSpan _workstationObservationInterval;
    private readonly TimeSpan _workstationObservationTimeout;
    private readonly int _maximumObservationPolls;
    private ShineLabDeviceStatusResponse? _selectedDevice;
    private ShineLabSubdeviceStatusRow? _selectedSubdevice;
    private SampleWorkstationTaskSummaryResponse? _selectedWorkstationTask;
    private SampleWorkstationDashboardSnapshot? _sampleWorkstationSnapshot;
    private CancellationTokenSource? _workstationObservationCancellation;
    private string _connectionStatus = "尚未读取";
    private string _message = "等待 ShineLab TCP Client 推送状态...";
    private string _workstationTestControlMessage = "请选择已有任务进行联调测试。";
    private bool _isRefreshing;
    private bool _isWorkstationTestBusy;
    private bool _disposed;
    private string _selectedInstrument = IonChromatographyInstrument;
    private long _refreshVersion;

    public ShineLabDeviceStatusViewModel(
        IMesClient mes,
        string sampleWorkstationDeviceId = DefaultSampleWorkstationDeviceId,
        bool sampleWorkstationTestControlEnabled = false,
        ISampleWorkstationTestConfirmation? sampleWorkstationTestConfirmation = null,
        TimeSpan? workstationObservationInterval = null,
        TimeSpan? workstationObservationTimeout = null,
        int maximumObservationPolls = 300)
    {
        if (maximumObservationPolls <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumObservationPolls));
        _mes = mes;
        _sampleWorkstationDeviceId = string.IsNullOrWhiteSpace(sampleWorkstationDeviceId)
            ? DefaultSampleWorkstationDeviceId
            : sampleWorkstationDeviceId.Trim();
        _sampleWorkstationTestControlEnabled = sampleWorkstationTestControlEnabled;
        _sampleWorkstationTestConfirmation = sampleWorkstationTestConfirmation
            ?? MessageBoxSampleWorkstationTestConfirmation.Instance;
        _workstationObservationInterval = workstationObservationInterval ?? TimeSpan.FromSeconds(2);
        if (_workstationObservationInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(workstationObservationInterval));
        _workstationObservationTimeout = workstationObservationTimeout ?? TimeSpan.FromMinutes(10);
        if (_workstationObservationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(workstationObservationTimeout));
        _maximumObservationPolls = maximumObservationPolls;
        RefreshCommand = new AsyncCommand(() => RefreshAsync(), () => !IsRefreshing);
        StartWorkstationTestTaskCommand = new AsyncCommand(
            () => StartSelectedWorkstationTestTaskAsync(),
            CanStartSelectedWorkstationTestTask);
    }

    /// <summary>All ShineLab responses received from MES.</summary>
    public ObservableCollection<ShineLabDeviceStatusResponse> Devices { get; } = [];

    /// <summary>Display-only rows for the ion chromatography sub-devices.</summary>
    public ObservableCollection<ShineLabSubdeviceStatusRow> IonSubdevices { get; } = [];

    /// <summary>Latest read-only task summaries returned by the workstation.</summary>
    public ObservableCollection<SampleWorkstationTaskSummaryResponse> WorkstationTasks { get; } = [];

    public IReadOnlyList<string> InstrumentOptions { get; } =
        [IonChromatographyInstrument, SampleWorkstationInstrument];

    public string SelectedInstrument
    {
        get => _selectedInstrument;
        set
        {
            if (!SetField(ref _selectedInstrument, value)) return;
            Interlocked.Increment(ref _refreshVersion);

            // A ShineLab sub-device must never remain selected while the
            // opening/dispensing view is active.  Conversely, returning to
            // ion chromatography immediately selects the first known device.
            if (IsSampleWorkstationSelected)
            {
                SelectedDevice = null;
            }
            else if (SelectedDevice is null || !Devices.Contains(SelectedDevice))
            {
                SelectedDevice = Devices.FirstOrDefault();
            }

            if (!IsSampleWorkstationSelected)
            {
                _workstationObservationCancellation?.Cancel();
            }

            OnPropertyChanged(nameof(IsIonChromatographySelected));
            OnPropertyChanged(nameof(IsSampleWorkstationSelected));
            OnPropertyChanged(nameof(IsWorkstationTestControlVisible));
            OnPropertyChanged(nameof(VisibleDevices));
            OnPropertyChanged(nameof(VisibleIonSubdevices));
            OnPropertyChanged(nameof(InstrumentTitle));
            OnPropertyChanged(nameof(InstrumentDescription));
            ConnectionStatus = IsIonChromatographySelected
                ? "待刷新离子色谱状态"
                : "待刷新开盖分液状态";
            Message = IsIonChromatographySelected
                ? "已切换到离子色谱，请刷新子设备状态。"
                : "已切换到开盖分液，请刷新工作站只读状态。";
            RaiseWorkstationProperties();
            RaiseWorkstationTestCommandCanExecuteChanged();
        }
    }

    public bool IsIonChromatographySelected =>
        string.Equals(SelectedInstrument, IonChromatographyInstrument, StringComparison.Ordinal);

    public bool IsSampleWorkstationSelected =>
        string.Equals(SelectedInstrument, SampleWorkstationInstrument, StringComparison.Ordinal);

    public string InstrumentTitle => $"{SelectedInstrument}设备状态";

    public string InstrumentDescription => IsIonChromatographySelected
        ? "显示离子色谱及其子设备（如 D160+、自动进样器 18i）的在线与运行状态"
        : "显示开盖分液工作站的只读状态、错误信息与最近任务";

    /// <summary>
    /// Only ion-chromatography responses are exposed through this collection.
    /// Opening/dispensing data comes from <see cref="SampleWorkstationSnapshot"/>
    /// and is intentionally never inferred from device names.
    /// </summary>
    public IEnumerable<ShineLabDeviceStatusResponse> VisibleDevices =>
        IsIonChromatographySelected ? Devices : Array.Empty<ShineLabDeviceStatusResponse>();

    public IEnumerable<ShineLabSubdeviceStatusRow> VisibleIonSubdevices =>
        IsIonChromatographySelected ? IonSubdevices : Array.Empty<ShineLabSubdeviceStatusRow>();

    public ICommand RefreshCommand { get; }

    public ICommand StartWorkstationTestTaskCommand { get; }

    public ShineLabDeviceStatusResponse? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            var changed = SetField(ref _selectedDevice, value);
            var selectedSubdevice = value is null
                ? null
                : IonSubdevices.FirstOrDefault(item => string.Equals(
                    item.EquipmentCode,
                    value.EquipmentCode,
                    StringComparison.OrdinalIgnoreCase));
            if (!ReferenceEquals(_selectedSubdevice, selectedSubdevice))
            {
                _selectedSubdevice = selectedSubdevice;
                OnPropertyChanged(nameof(SelectedSubdevice));
            }
            if (changed) RaiseSelectedProperties();
        }
    }

    public ShineLabSubdeviceStatusRow? SelectedSubdevice
    {
        get => _selectedSubdevice;
        set
        {
            if (!SetField(ref _selectedSubdevice, value)) return;
            if (value is null)
            {
                if (_selectedDevice is not null) SelectedDevice = null;
            }
            else
            {
                var source = Devices.FirstOrDefault(item => string.Equals(
                    item.EquipmentCode,
                    value.EquipmentCode,
                    StringComparison.OrdinalIgnoreCase));
                if (!ReferenceEquals(_selectedDevice, source)) SelectedDevice = source;
            }
        }
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetField(ref _connectionStatus, value);
    }

    public string Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (!SetField(ref _isRefreshing, value)) return;
            (RefreshCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            RaiseWorkstationTestCommandCanExecuteChanged();
        }
    }

    // Existing selected-device fields are kept for compatibility with the
    // detail panel and callers that consume the view model directly.
    public string SelectedDeviceName => SelectedDevice?.DeviceName ?? "请选择设备";
    public string SelectedEquipmentCode => SelectedDevice?.EquipmentCode ?? "-";
    public string SelectedOnline => SelectedDevice is null ? "-" : SelectedDevice.Online ? "在线" : "离线";
    public string SelectedState => SelectedDevice?.State ?? "-";
    public string SelectedStateDisplay => SelectedDevice is null ? "-" : FormatShineLabState(SelectedDevice.State, SelectedDevice.Online);
    public string SelectedTaskState => SelectedDevice is null
        ? "-"
        : SelectedDevice.HasActiveTask ? "有任务进行中" : "无进行中任务";
    public string SelectedTaskUuid => SelectedDevice?.TaskUuid ?? "-";
    public string SelectedSample => SelectedDevice is null
        ? "-"
        : string.IsNullOrWhiteSpace(SelectedDevice.SampleName)
            ? SelectedDevice.SampleId ?? "-"
            : $"{SelectedDevice.SampleName} ({SelectedDevice.SampleId ?? "-"})";
    public string SelectedChannelPosition => SelectedDevice is null
        ? "-"
        : $"通道 {SelectedDevice.Channel ?? "-"} / 位置 {SelectedDevice.Position?.ToString() ?? "-"}";
    public string SelectedStage => SelectedDevice?.Stage ?? "-";
    public string SelectedProgress => SelectedDevice?.Progress is { } progress ? $"{progress}%" : "-";
    public string SelectedAlarm => SelectedDevice is null
        ? "-"
        : string.IsNullOrWhiteSpace(SelectedDevice.AlarmMessage)
            ? "无"
            : $"{SelectedDevice.AlarmCode ?? "ALARM"}: {SelectedDevice.AlarmMessage}";
    public string SelectedLastSeen => SelectedDevice?.LastSeenAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

    // Opening/dispensing workstation projection. Normal deployments remain
    // read-only; a configuration-gated test entry can start an existing task.
    public string SampleWorkstationDeviceId => _sampleWorkstationDeviceId;
    public bool IsWorkstationTestControlVisible =>
        _sampleWorkstationTestControlEnabled && IsSampleWorkstationSelected;
    public bool IsWorkstationTestBusy
    {
        get => _isWorkstationTestBusy;
        private set
        {
            if (!SetField(ref _isWorkstationTestBusy, value)) return;
            RaiseWorkstationTestCommandCanExecuteChanged();
        }
    }
    public SampleWorkstationTaskSummaryResponse? SelectedWorkstationTask
    {
        get => _selectedWorkstationTask;
        set
        {
            if (!SetField(ref _selectedWorkstationTask, value)) return;
            OnPropertyChanged(nameof(SelectedWorkstationTaskNo));
            OnPropertyChanged(nameof(SelectedWorkstationTaskState));
            RaiseWorkstationTestCommandCanExecuteChanged();
        }
    }
    public string SelectedWorkstationTaskNo => SelectedWorkstationTask?.TaskNo ?? "未选择";
    public string SelectedWorkstationTaskState => SelectedWorkstationTask is { } task
        ? FormatWorkstationTaskState(task.State, task.RawState)
        : "-";
    public string WorkstationTestControlMessage
    {
        get => _workstationTestControlMessage;
        private set => SetField(ref _workstationTestControlMessage, value);
    }
    public SampleWorkstationDashboardSnapshot? SampleWorkstationSnapshot => _sampleWorkstationSnapshot;
    public SampleWorkstationStatusResponse? WorkstationStatus => _sampleWorkstationSnapshot?.Status;
    public SampleWorkstationErrorResponse? WorkstationErrorResponse => _sampleWorkstationSnapshot?.Error;
    public bool HasWorkstationData => _sampleWorkstationSnapshot is not null;
    public string WorkstationConnectionStatus => _sampleWorkstationSnapshot?.Status is null
        ? _sampleWorkstationSnapshot is null
            ? "接口未启用或不可用"
            : "状态接口不可用"
        : IsKnownWorkstationState(_sampleWorkstationSnapshot.Status)
            ? "已连接 MES"
            : "状态未知，请确认协议数据";
    public string WorkstationOnline => _sampleWorkstationSnapshot?.Status is not { } status
        ? "不可用"
        : status.State == SampleWorkstationDeviceState.Unknown
            ? "未知"
            : status.Online && status.State != SampleWorkstationDeviceState.Offline ? "在线" : "离线";
    public string WorkstationState => FormatWorkstationState(_sampleWorkstationSnapshot?.Status);
    public string WorkstationRawState => _sampleWorkstationSnapshot?.Status is { } status
        ? status.RawState.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "-";
    public string WorkstationError => FormatWorkstationError(
        _sampleWorkstationSnapshot?.Error,
        _sampleWorkstationSnapshot?.ErrorReadError,
        _sampleWorkstationSnapshot is not null);
    public string WorkstationObservedAt => FormatObservedAt(
        _sampleWorkstationSnapshot?.Status?.ObservedAtUtc ?? _sampleWorkstationSnapshot?.Error?.ObservedAtUtc);
    public string WorkstationAvailabilityMessage => _sampleWorkstationSnapshot is null
        ? "MES 未启用或暂时无法读取开盖分液只读接口。"
        : string.IsNullOrWhiteSpace(WorkstationReadErrors)
            ? IsWorkstationTestControlVisible
                ? "联调测试入口已启用；仅可二次确认后启动已有任务。"
                : "仅展示只读状态，不提供初始化、启动、任务导入或删除操作。"
            : $"只读接口部分不可用：{WorkstationReadErrors}";
    public string WorkstationReadErrors => _sampleWorkstationSnapshot is null
        ? string.Empty
        : string.Join(
            "；",
            new[]
            {
                FormatSectionError("状态", _sampleWorkstationSnapshot.StatusReadError),
                FormatSectionError("错误", _sampleWorkstationSnapshot.ErrorReadError),
                FormatSectionError("任务", _sampleWorkstationSnapshot.TasksReadError)
            }.OfType<string>());
    public string WorkstationTaskCount => WorkstationTasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public SampleWorkstationTaskSummaryResponse? LatestWorkstationTask =>
        WorkstationTasks.OrderByDescending(task => task.RecordNumber).FirstOrDefault();
    public string WorkstationLatestTaskNo => LatestWorkstationTask?.TaskNo ?? "暂无任务";
    public string WorkstationLatestTaskName => LatestWorkstationTask?.TaskName ?? "-";
    public string WorkstationLatestTaskState => LatestWorkstationTask is { } task
        ? FormatWorkstationTaskState(task.State, task.RawState)
        : "暂无任务";
    public string WorkstationLatestTaskTime => LatestWorkstationTask?.MakeTime ?? "-";
    public string WorkstationLatestTaskRemark => LatestWorkstationTask?.Remark ?? "-";

    public async Task StartSelectedWorkstationTestTaskAsync(
        CancellationToken cancellationToken = default)
    {
        var selected = SelectedWorkstationTask;
        if (!CanStartSelectedWorkstationTestTask() || selected is null)
        {
            WorkstationTestControlMessage = "请先刷新状态并选择可启动的已有任务。";
            return;
        }

        if (!_sampleWorkstationTestConfirmation.Confirm(
                "确认启动开盖分液测试任务",
                $"设备：{_sampleWorkstationDeviceId}\n任务：{selected.TaskNo}\n当前状态：{SelectedWorkstationTaskState}\n\n确认后只发送一次启动请求，是否继续？"))
        {
            WorkstationTestControlMessage = "已取消，未发送请求。";
            return;
        }

        IsWorkstationTestBusy = true;
        SampleWorkstationCommandResponse response;
        try
        {
            response = await _mes.StartSampleWorkstationTestTaskAsync(
                _sampleWorkstationDeviceId,
                selected.TaskNo,
                cancellationToken);
        }
        catch (SampleWorkstationTestStartException exception) when (!exception.OutcomeUnknown)
        {
            WorkstationTestControlMessage = $"启动请求失败：{exception.Message}；未自动重试。";
            IsWorkstationTestBusy = false;
            return;
        }
        catch (Exception exception)
        {
            WorkstationTestControlMessage =
                $"启动结果不明确，请刷新状态并现场核对：{exception.Message}";
            IsWorkstationTestBusy = false;
            return;
        }

        if (!response.Acknowledged)
        {
            WorkstationTestControlMessage = "设备未确认启动请求；未自动重试。";
            IsWorkstationTestBusy = false;
            return;
        }

        if (_disposed)
        {
            _isWorkstationTestBusy = false;
            return;
        }

        WorkstationTestControlMessage = "设备已接收启动请求，正在等待运行状态。";
        _workstationObservationCancellation?.Cancel();
        var manualCancellation = new CancellationTokenSource();
        using var timeoutCancellation = new CancellationTokenSource(
            _workstationObservationTimeout,
            TimeProvider.System);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            manualCancellation.Token,
            timeoutCancellation.Token);
        _workstationObservationCancellation = manualCancellation;
        try
        {
            await ObserveWorkstationTestTaskAsync(
                selected.TaskNo,
                linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (
            timeoutCancellation.IsCancellationRequested
            && !manualCancellation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            if (!_disposed)
            {
                WorkstationTestControlMessage =
                    "终态尚未确认，设备可能仍在运行；请刷新状态并现场核对。";
            }
        }
        catch (OperationCanceledException) when (
            manualCancellation.IsCancellationRequested
            || cancellationToken.IsCancellationRequested)
        {
            if (!_disposed)
            {
                WorkstationTestControlMessage = "已停止本地状态观察；设备任务未被停止。";
            }
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                WorkstationTestControlMessage =
                    $"状态读取失败；启动请求已确认，请刷新并现场核对：{exception.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_workstationObservationCancellation, manualCancellation))
            {
                _workstationObservationCancellation = null;
            }
            manualCancellation.Dispose();
            if (_disposed)
            {
                _isWorkstationTestBusy = false;
            }
            else
            {
                IsWorkstationTestBusy = false;
            }
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        var selectedCode = SelectedDevice?.EquipmentCode;
        var instrument = SelectedInstrument;
        var refreshVersion = Volatile.Read(ref _refreshVersion);
        try
        {
            if (string.Equals(instrument, IonChromatographyInstrument, StringComparison.Ordinal))
            {
                await RefreshIonChromatographyAsync(selectedCode, refreshVersion, cancellationToken);
            }
            else
            {
                await RefreshSampleWorkstationAsync(refreshVersion, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!IsRefreshCurrent(instrument, refreshVersion)) return;
            ConnectionStatus = "已取消";
            Message = "状态查询已取消。";
        }
        catch (Exception exception)
        {
            if (!IsRefreshCurrent(instrument, refreshVersion)) return;
            if (string.Equals(instrument, SampleWorkstationInstrument, StringComparison.Ordinal))
            {
                ClearWorkstationData();
                ConnectionStatus = "开盖分液接口不可用";
                Message = $"开盖分液只读接口不可用：{exception.Message}";
            }
            else
            {
                ConnectionStatus = "MES 不可用";
                Message = exception.Message;
            }
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task RefreshIonChromatographyAsync(
        string? selectedCode,
        long refreshVersion,
        CancellationToken cancellationToken)
    {
        var statuses = await _mes.GetShineLabDeviceStatusesAsync(cancellationToken);
        if (!IsRefreshCurrent(IonChromatographyInstrument, refreshVersion)) return;
        Devices.Clear();
        IonSubdevices.Clear();
        foreach (var status in statuses)
        {
            Devices.Add(status);
            IonSubdevices.Add(new ShineLabSubdeviceStatusRow(
                status.DeviceName,
                status.EquipmentCode,
                status.Online ? "在线" : "离线",
                FormatShineLabState(status.State, status.Online),
                status.LastSeenAtUtc));
        }

        OnPropertyChanged(nameof(VisibleDevices));
        OnPropertyChanged(nameof(VisibleIonSubdevices));
        SelectedDevice = selectedCode is null
            ? Devices.FirstOrDefault()
            : Devices.FirstOrDefault(item => string.Equals(
                item.EquipmentCode,
                selectedCode,
                StringComparison.OrdinalIgnoreCase)) ?? Devices.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedSubdevice));

        ConnectionStatus = Devices.Count == 0 ? "暂无推送设备" : "已连接 MES";
        Message = Devices.Count == 0
            ? "尚未收到 ShineLab 的 Certification/UpdateInfo 推送。"
            : $"已收到 {Devices.Count} 台离子色谱子设备状态，最后查询：{DateTime.Now:HH:mm:ss}";
    }

    private async Task RefreshSampleWorkstationAsync(
        long refreshVersion,
        CancellationToken cancellationToken)
    {
        var snapshot = await _mes.GetSampleWorkstationSnapshotAsync(
            _sampleWorkstationDeviceId,
            cancellationToken);
        if (!IsRefreshCurrent(SampleWorkstationInstrument, refreshVersion)) return;
        ApplyWorkstationSnapshot(snapshot);

        ConnectionStatus = WorkstationConnectionStatus;
        Message = snapshot is null
            ? "MES 未启用或暂时无法读取开盖分液只读接口。"
            : string.IsNullOrWhiteSpace(WorkstationReadErrors)
                ? $"已读取开盖分液状态与 {WorkstationTasks.Count} 条任务，最后查询：{DateTime.Now:HH:mm:ss}"
                : $"已读取开盖分液可用数据，部分接口异常：{WorkstationReadErrors}";
    }

    private SampleWorkstationDashboardSnapshot? ApplyWorkstationSnapshot(
        SampleWorkstationDashboardSnapshot? snapshot,
        string? preferredTaskNo = null)
    {
        var selectedTaskNo = preferredTaskNo ?? SelectedWorkstationTask?.TaskNo;
        var previous = _sampleWorkstationSnapshot;
        var effective = snapshot;
        if (snapshot is null && previous is not null)
        {
            effective = previous with
            {
                StatusReadError = "工作站快照响应为空",
                ErrorReadError = "工作站快照响应为空",
                TasksReadError = "工作站快照响应为空"
            };
        }
        else if (snapshot is not null && previous is not null)
        {
            effective = snapshot with
            {
                Status = string.IsNullOrWhiteSpace(snapshot.StatusReadError)
                    ? snapshot.Status
                    : previous.Status,
                Error = string.IsNullOrWhiteSpace(snapshot.ErrorReadError)
                    ? snapshot.Error
                    : previous.Error,
                Tasks = string.IsNullOrWhiteSpace(snapshot.TasksReadError)
                    ? snapshot.Tasks
                    : previous.Tasks
            };
        }

        _sampleWorkstationSnapshot = effective;
        WorkstationTasks.Clear();
        if (effective?.Tasks is { } tasks)
        {
            foreach (var task in tasks.OrderByDescending(item => item.RecordNumber))
            {
                WorkstationTasks.Add(task);
            }
        }

        SelectedWorkstationTask = selectedTaskNo is null
            ? null
            : WorkstationTasks.FirstOrDefault(task => string.Equals(
                task.TaskNo,
                selectedTaskNo,
                StringComparison.Ordinal));
        RaiseWorkstationProperties();
        RaiseWorkstationTestCommandCanExecuteChanged();
        return effective;
    }

    private async Task ObserveWorkstationTestTaskAsync(
        string taskNo,
        CancellationToken cancellationToken)
    {
        var observedRunning = false;
        for (var poll = 0; poll < _maximumObservationPolls; poll++)
        {
            if (poll > 0 && _workstationObservationInterval > TimeSpan.Zero)
            {
                await Task.Delay(_workstationObservationInterval, cancellationToken);
            }

            var receivedSnapshot = await _mes.GetSampleWorkstationSnapshotAsync(
                _sampleWorkstationDeviceId,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = ApplyWorkstationSnapshot(receivedSnapshot, taskNo);
            var task = snapshot?.Tasks.FirstOrDefault(item => string.Equals(
                item.TaskNo,
                taskNo,
                StringComparison.Ordinal));

            observedRunning |= snapshot?.Status?.State == SampleWorkstationDeviceState.Running
                || snapshot?.Error?.ErrorCode == 3
                || task?.State == SampleWorkstationTaskState.Running;

            if (observedRunning &&
                string.IsNullOrWhiteSpace(snapshot?.StatusReadError) &&
                string.IsNullOrWhiteSpace(snapshot?.ErrorReadError) &&
                string.IsNullOrWhiteSpace(snapshot?.TasksReadError) &&
                snapshot?.Status is { State: SampleWorkstationDeviceState.Idle, RawState: 0 } &&
                snapshot.Error?.ErrorCode == 0 &&
                task?.State == SampleWorkstationTaskState.Completed)
            {
                WorkstationTestControlMessage = "任务完成";
                return;
            }

            WorkstationTestControlMessage = observedRunning
                ? "任务正在运行，正在等待完成。"
                : "设备已接收启动请求，正在等待运行状态。";
        }

        WorkstationTestControlMessage =
            "终态尚未确认，设备可能仍在运行；请刷新状态并现场核对。";
    }

    private bool CanStartSelectedWorkstationTestTask() =>
        !_disposed
        && IsWorkstationTestControlVisible
        && !IsRefreshing
        && !IsWorkstationTestBusy
        && string.IsNullOrWhiteSpace(_sampleWorkstationSnapshot?.StatusReadError)
        && string.IsNullOrWhiteSpace(_sampleWorkstationSnapshot?.ErrorReadError)
        && string.IsNullOrWhiteSpace(_sampleWorkstationSnapshot?.TasksReadError)
        && SelectedWorkstationTask is { State: not SampleWorkstationTaskState.Running }
        && WorkstationStatus is
        {
            Online: true,
            State: SampleWorkstationDeviceState.Idle,
            RawState: 0
        };

    private void RaiseWorkstationTestCommandCanExecuteChanged() =>
        (StartWorkstationTestTaskCommand as AsyncCommand)?.RaiseCanExecuteChanged();

    private bool IsRefreshCurrent(string instrument, long refreshVersion) =>
        refreshVersion == Volatile.Read(ref _refreshVersion)
        && string.Equals(SelectedInstrument, instrument, StringComparison.Ordinal);

    private void ClearWorkstationData()
    {
        _sampleWorkstationSnapshot = null;
        WorkstationTasks.Clear();
        RaiseWorkstationProperties();
    }

    private void RaiseSelectedProperties()
    {
        foreach (var name in new[]
        {
            nameof(SelectedDeviceName), nameof(SelectedEquipmentCode), nameof(SelectedOnline), nameof(SelectedState),
            nameof(SelectedStateDisplay), nameof(SelectedTaskState), nameof(SelectedTaskUuid), nameof(SelectedSample),
            nameof(SelectedChannelPosition), nameof(SelectedStage), nameof(SelectedProgress), nameof(SelectedAlarm),
            nameof(SelectedLastSeen)
        }) OnPropertyChanged(name);
    }

    private void RaiseWorkstationProperties()
    {
        foreach (var name in new[]
        {
            nameof(SampleWorkstationSnapshot), nameof(WorkstationStatus), nameof(WorkstationErrorResponse),
            nameof(HasWorkstationData), nameof(WorkstationConnectionStatus), nameof(WorkstationOnline),
            nameof(WorkstationState), nameof(WorkstationRawState), nameof(WorkstationError),
            nameof(WorkstationObservedAt), nameof(WorkstationAvailabilityMessage), nameof(WorkstationReadErrors),
            nameof(WorkstationTaskCount),
            nameof(LatestWorkstationTask), nameof(WorkstationLatestTaskNo), nameof(WorkstationLatestTaskName),
            nameof(WorkstationLatestTaskState), nameof(WorkstationLatestTaskTime), nameof(WorkstationLatestTaskRemark),
            nameof(SelectedWorkstationTaskNo), nameof(SelectedWorkstationTaskState)
        }) OnPropertyChanged(name);
    }

    private static bool IsKnownWorkstationState(SampleWorkstationStatusResponse status) =>
        status.State != SampleWorkstationDeviceState.Unknown;

    private static string FormatWorkstationState(SampleWorkstationStatusResponse? status)
    {
        if (status is null) return "未启用";
        if (!status.Online || status.State == SampleWorkstationDeviceState.Offline) return "离线";
        return status.State switch
        {
            SampleWorkstationDeviceState.Idle => "空闲",
            SampleWorkstationDeviceState.Running => "运行",
            SampleWorkstationDeviceState.Paused => "暂停",
            SampleWorkstationDeviceState.Faulted => "故障",
            SampleWorkstationDeviceState.Initializing => "初始化",
            _ => "未知"
        };
    }

    private static string FormatWorkstationTaskState(
        SampleWorkstationTaskState state,
        string rawState) => state switch
        {
            SampleWorkstationTaskState.Waiting => "等待",
            SampleWorkstationTaskState.Running => "运行",
            SampleWorkstationTaskState.Completed => "完成",
            _ => string.IsNullOrWhiteSpace(rawState) ? "未知" : $"未知（{rawState}）"
        };

    private static string FormatWorkstationError(
        SampleWorkstationErrorResponse? error,
        string? errorReadError,
        bool hasSnapshot)
    {
        if (!hasSnapshot) return "接口未启用或暂不可用";
        if (!string.IsNullOrWhiteSpace(errorReadError)) return $"错误接口不可用：{errorReadError}";
        if (error is null || string.IsNullOrWhiteSpace(error.Description)) return "暂无报错信息";
        var prefix = error.ErrorCode is { } code ? $"[{code}] " : string.Empty;
        var recognition = error.Recognized ? string.Empty : "未识别：";
        return $"{prefix}{recognition}{error.Description}";
    }

    private static string? FormatSectionError(string section, string? error) =>
        string.IsNullOrWhiteSpace(error) ? null : $"{section}接口：{error}";

    private static string FormatObservedAt(DateTimeOffset? observedAt) =>
        observedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

    private static string FormatShineLabState(string? state, bool online)
    {
        if (!online) return "离线";
        return state?.Trim().ToLowerInvariant() switch
        {
            "running" or "run" or "injecting" or "运行" or "执行中" => "运行",
            "idle" or "ready" or "standby" or "空闲" or "就绪" => "空闲",
            "paused" or "pause" or "暂停" => "暂停",
            "fault" or "faulted" or "error" or "故障" => "故障",
            null or "" => "未知",
            _ => state ?? "未知"
        };
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        _disposed = true;
        _workstationObservationCancellation?.Cancel();
        _isWorkstationTestBusy = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
