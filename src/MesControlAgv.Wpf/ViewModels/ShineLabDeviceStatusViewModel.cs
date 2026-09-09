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
public sealed class ShineLabDeviceStatusViewModel : INotifyPropertyChanged
{
    public const string IonChromatographyInstrument = "离子色谱";
    public const string SampleWorkstationInstrument = "开盖分液";
    public const string DefaultSampleWorkstationDeviceId = "SAMPLE-WORKSTATION-01";

    private readonly IMesClient _mes;
    private readonly string _sampleWorkstationDeviceId;
    private ShineLabDeviceStatusResponse? _selectedDevice;
    private ShineLabSubdeviceStatusRow? _selectedSubdevice;
    private SampleWorkstationDashboardSnapshot? _sampleWorkstationSnapshot;
    private string _connectionStatus = "尚未读取";
    private string _message = "等待 ShineLab TCP Client 推送状态...";
    private bool _isRefreshing;
    private string _selectedInstrument = IonChromatographyInstrument;
    private long _refreshVersion;

    public ShineLabDeviceStatusViewModel(
        IMesClient mes,
        string sampleWorkstationDeviceId = DefaultSampleWorkstationDeviceId)
    {
        _mes = mes;
        _sampleWorkstationDeviceId = string.IsNullOrWhiteSpace(sampleWorkstationDeviceId)
            ? DefaultSampleWorkstationDeviceId
            : sampleWorkstationDeviceId.Trim();
        RefreshCommand = new AsyncCommand(() => RefreshAsync(), () => !IsRefreshing);
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

            OnPropertyChanged(nameof(IsIonChromatographySelected));
            OnPropertyChanged(nameof(IsSampleWorkstationSelected));
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

    // Opening/dispensing workstation projection.  These properties are
    // intentionally read-only: no initialize/start/import/delete commands are
    // exposed in this first protocol phase.
    public string SampleWorkstationDeviceId => _sampleWorkstationDeviceId;
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
            ? "仅展示只读状态，不提供初始化、启动、任务导入或删除操作。"
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
        _sampleWorkstationSnapshot = snapshot;
        WorkstationTasks.Clear();
        if (snapshot?.Tasks is { } tasks)
        {
            foreach (var task in tasks.OrderByDescending(item => item.RecordNumber))
            {
                WorkstationTasks.Add(task);
            }
        }

        RaiseWorkstationProperties();
        ConnectionStatus = WorkstationConnectionStatus;
        Message = snapshot is null
            ? "MES 未启用或暂时无法读取开盖分液只读接口。"
            : string.IsNullOrWhiteSpace(WorkstationReadErrors)
                ? $"已读取开盖分液状态与 {WorkstationTasks.Count} 条任务，最后查询：{DateTime.Now:HH:mm:ss}"
                : $"已读取开盖分液可用数据，部分接口异常：{WorkstationReadErrors}";
    }

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
            nameof(WorkstationLatestTaskState), nameof(WorkstationLatestTaskTime), nameof(WorkstationLatestTaskRemark)
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

    public event PropertyChangedEventHandler? PropertyChanged;
}
