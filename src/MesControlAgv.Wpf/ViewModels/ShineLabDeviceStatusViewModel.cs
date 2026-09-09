using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// Displays only the status/events received from ShineLab through MES.  It
/// deliberately has no serial client and no device command buttons.
/// </summary>
public sealed class ShineLabDeviceStatusViewModel : INotifyPropertyChanged
{
    private readonly IMesClient _mes;
    private ShineLabDeviceStatusResponse? _selectedDevice;
    private string _connectionStatus = "尚未读取";
    private string _message = "等待 ShineLab TCP Client 推送状态...";
    private bool _isRefreshing;
    private string _selectedInstrument = "离子色谱";

    public ShineLabDeviceStatusViewModel(IMesClient mes)
    {
        _mes = mes;
        RefreshCommand = new AsyncCommand(() => RefreshAsync(), () => !IsRefreshing);
    }

    public ObservableCollection<ShineLabDeviceStatusResponse> Devices { get; } = [];
    public IReadOnlyList<string> InstrumentOptions { get; } = ["离子色谱", "开盖分液"];
    public string SelectedInstrument
    {
        get => _selectedInstrument;
        set { if (!SetField(ref _selectedInstrument, value)) return; OnPropertyChanged(nameof(VisibleDevices)); OnPropertyChanged(nameof(InstrumentTitle)); OnPropertyChanged(nameof(InstrumentDescription)); }
    }
    public string InstrumentTitle => $"{SelectedInstrument}设备状态";
    public string InstrumentDescription => SelectedInstrument == "离子色谱"
        ? "显示离子色谱及其子设备（如 D160、自动进样器 18I）的在线与运行状态"
        : "显示开盖分液及其子设备的在线与运行状态";
    public IEnumerable<ShineLabDeviceStatusResponse> VisibleDevices => Devices.Where(IsCurrentInstrument);
    public ICommand RefreshCommand { get; }

    public ShineLabDeviceStatusResponse? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetField(ref _selectedDevice, value)) return;
            RaiseSelectedProperties();
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

    public string SelectedDeviceName => SelectedDevice?.DeviceName ?? "请选择设备";
    public string SelectedEquipmentCode => SelectedDevice?.EquipmentCode ?? "-";
    public string SelectedOnline => SelectedDevice is null ? "-" : SelectedDevice.Online ? "在线" : "离线";
    public string SelectedState => SelectedDevice?.State ?? "-";
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

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        var selectedCode = SelectedDevice?.EquipmentCode;
        try
        {
            var statuses = await _mes.GetShineLabDeviceStatusesAsync(cancellationToken);
            Devices.Clear();
            foreach (var status in statuses) Devices.Add(status);
            OnPropertyChanged(nameof(VisibleDevices));
            var visibleDevices = VisibleDevices.ToList();
            SelectedDevice = selectedCode is null
                ? visibleDevices.FirstOrDefault()
                : visibleDevices.FirstOrDefault(item => string.Equals(item.EquipmentCode, selectedCode, StringComparison.OrdinalIgnoreCase))
                  ?? visibleDevices.FirstOrDefault();
            ConnectionStatus = Devices.Count == 0 ? "暂无推送设备" : "已连接 MES";
            Message = Devices.Count == 0
                ? "尚未收到 ShineLab 的 Certification/UpdateInfo 推送。"
                : $"已收到 {Devices.Count} 台设备状态，最后查询：{DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConnectionStatus = "已取消";
            Message = "状态查询已取消。";
        }
        catch (Exception exception)
        {
            ConnectionStatus = "MES 不可用";
            Message = exception.Message;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void RaiseSelectedProperties()
    {
        foreach (var name in new[]
        {
            nameof(SelectedDeviceName), nameof(SelectedEquipmentCode), nameof(SelectedOnline), nameof(SelectedState),
            nameof(SelectedTaskState), nameof(SelectedTaskUuid), nameof(SelectedSample), nameof(SelectedChannelPosition),
            nameof(SelectedStage), nameof(SelectedProgress), nameof(SelectedAlarm), nameof(SelectedLastSeen)
        }) OnPropertyChanged(name);
    }

    private bool IsCurrentInstrument(ShineLabDeviceStatusResponse device)
    {
        var text = $"{device.DeviceName} {device.EquipmentCode}";
        return SelectedInstrument == "开盖分液"
            ? text.Contains("开盖", StringComparison.OrdinalIgnoreCase) || text.Contains("分液", StringComparison.OrdinalIgnoreCase)
            : !text.Contains("开盖", StringComparison.OrdinalIgnoreCase) && !text.Contains("分液", StringComparison.OrdinalIgnoreCase);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
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
