using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class IonChromatographyViewModel : INotifyPropertyChanged
{
    public const string DefaultInstrumentId = "CIC-D160-01";

    private readonly IMesClient _mes;
    private IonChromatographyControlCenterStatusResponse? _snapshot;
    private bool _isRefreshing;
    private string _connectionStatus = "尚未读取";
    private string _statusMessage = "-";

    public IonChromatographyViewModel(IMesClient mes)
    {
        _mes = mes;
        RefreshCommand = new AsyncCommand(() => RefreshAsync(), () => !IsRefreshing);
    }

    public ICommand RefreshCommand { get; }
    public OfflineDataStateViewModel OfflineState { get; } = new();
    public string InstrumentId => _snapshot?.Status.InstrumentId ?? DefaultInstrumentId;
    public string Model => _snapshot?.Status.Model ?? "CIC-D160+";
    public string SerialNumber => _snapshot?.Status.SerialNumber ?? "-";
    public string DeviceState => _snapshot?.Status.DeviceState ?? "未知";
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetField(ref _connectionStatus, value);
    }
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }
    public string Conductivity => Format(_snapshot?.Status.Conductivity, "F5", "uS/cm");
    public string TotalConductivity => Format(_snapshot?.Status.TotalConductivity, "F5", "uS/cm");
    public string Pressure => Format(_snapshot?.Status.Pressure, "F2", "MPa");
    public string ColumnTemperature => Format(_snapshot?.Status.ColumnTemperature, "F2", "C");
    public string Flow => Format(_snapshot?.Status.Flow, "F3", "mL/min");
    public string ObservedAt => _snapshot?.Status.ObservedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-";
    public string MappingConfidence => _snapshot?.Status.MappingConfidence ?? "-";
    public string PortStatus => _snapshot?.Status.PortOwned == true ? "读取中" : "已释放";
    public string TaskAdmissionStatus => _snapshot?.TaskAdmissionEnabled == true ? "已开放" : "禁用";
    public string EnabledOperations => _snapshot is null || _snapshot.EnabledOperations.Count == 0
        ? "无"
        : string.Join(" / ", _snapshot.EnabledOperations);
    public string ControlPolicy => _snapshot?.ControlPolicy ?? "ReadOnlyCaptureCorrelated";
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (!SetField(ref _isRefreshing, value)) return;
            (RefreshCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        OfflineState.BeginLoading("正在读取仪器只读状态...");
        StatusMessage = "正在读取只读状态...";
        try
        {
            _snapshot = await _mes.GetIonChromatographyStatusAsync(DefaultInstrumentId, cancellationToken);
            ConnectionStatus = _snapshot?.Status.Online == true ? "在线" : "不可用";
            StatusMessage = _snapshot is null ? "MES 未返回仪器状态。" : "只读状态已更新。";
            OfflineState.MarkReady(
                _snapshot is not null,
                _snapshot is null ? "MES 未返回仪器状态。" : "仪器只读状态已更新。");
            RaiseSnapshotProperties();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConnectionStatus = "已取消";
            StatusMessage = "状态读取已取消。";
            OfflineState.MarkCancelled("仪器状态读取已取消。");
        }
        catch (Exception exception) when (exception is
            HttpRequestException or
            InvalidOperationException or
            NotSupportedException or
            JsonException or
            TaskCanceledException or
            TimeoutException)
        {
            _snapshot = null;
            ConnectionStatus = "不可用";
            StatusMessage = exception.Message;
            OfflineState.MarkError("仪器状态刷新失败，可点击刷新重试。", exception.Message);
            RaiseSnapshotProperties();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void RaiseSnapshotProperties()
    {
        foreach (var property in new[]
        {
            nameof(InstrumentId), nameof(Model), nameof(SerialNumber), nameof(DeviceState),
            nameof(Conductivity), nameof(TotalConductivity), nameof(Pressure), nameof(ColumnTemperature), nameof(Flow),
            nameof(ObservedAt), nameof(MappingConfidence), nameof(PortStatus),
            nameof(TaskAdmissionStatus), nameof(EnabledOperations), nameof(ControlPolicy)
        })
        {
            OnPropertyChanged(property);
        }
    }

    private static string Format(double? value, string format, string unit) => value is null
        ? "-"
        : $"{value.Value.ToString(format, CultureInfo.InvariantCulture)} {unit}";

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
