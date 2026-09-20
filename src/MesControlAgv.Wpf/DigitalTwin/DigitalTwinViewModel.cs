using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MesControlAgv.Wpf.DigitalTwin;

public sealed class DigitalTwinViewModel : INotifyPropertyChanged
{
    private bool _isReady;
    private string _status = "打开页面后加载三维场景。";
    private DigitalTwinDevice? _selectedDevice;
    private TwinBindings _bindings = new();
    private TwinReading[] _received = Enum.GetValues<TwinChannel>().Select(c => TwinProjection.Failed(c, new TwinBindings().Id(c), "尚未连接状态数据源。")).ToArray();
    private IReadOnlyList<TwinReading> _readings = [];
    private string _sourceDisplay = "数据来源：未连接";
    public DigitalTwinViewModel() => UpdateFreshness(DateTimeOffset.UtcNow);
    public IReadOnlyList<TwinReading> Readings => _readings;
    public string SourceDisplay { get => _sourceDisplay; private set => Set(ref _sourceDisplay, value); }
    public void ResetTelemetry(IDigitalTwinStatusSource? source)
    {
        _bindings = source?.Bindings ?? new();
        SourceDisplay = source?.SourceDisplay ?? "数据来源：未连接（仅场景预览）";
        _received = Enum.GetValues<TwinChannel>().Select(c => source is null
            ? TwinProjection.Failed(c, _bindings.Id(c), "状态数据源未连接；场景仍可浏览。")
            : TwinProjection.Pending(c, _bindings.Id(c))).ToArray();
        UpdateFreshness(DateTimeOffset.UtcNow);
    }
    public void ApplyReading(TwinReading reading)
    {
        if (!Enum.IsDefined(reading.Channel)) return;
        _received[(int)reading.Channel] = reading.EquipmentId == _bindings.Id(reading.Channel) ? reading
            : TwinProjection.Failed(reading.Channel, _bindings.Id(reading.Channel), "返回编号与显示映射不符，未采用。");
        UpdateFreshness(DateTimeOffset.UtcNow);
    }
    public void UpdateFreshness(DateTimeOffset now)
    {
        var readings = _received.Select(r => TwinProjection.Freshness(r, now)).ToArray();
        if (_readings.SequenceEqual(readings)) return;
        _readings = readings;
        PropertyChanged?.Invoke(this, new(nameof(Readings)));
    }
    public IReadOnlyList<DigitalTwinDevice> Devices => DigitalTwinScene.Devices;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsReady { get => _isReady; set => Set(ref _isReady, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public DigitalTwinDevice? SelectedDevice
    {
        get => _selectedDevice;
        set { if (Set(ref _selectedDevice, value)) PropertyChanged?.Invoke(this, new(nameof(SelectionDescription))); }
    }
    public string SelectionDescription => SelectedDevice?.Description ?? "点击模型或从列表选择设备。";
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
        return true;
    }
}
