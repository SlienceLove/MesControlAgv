using System.ComponentModel;
using System.Runtime.CompilerServices;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

public sealed record KpiStatusSlice(string Label, int Value, string Color);

public sealed class KpiDashboardViewModel : INotifyPropertyChanged
{
    private DateOnly _date = DateOnly.FromDateTime(DateTime.UtcNow);
    private KpiTaskSummary _taskSummary = new(0, 0, 0, 0, 0);
    private KpiSampleSummary _sampleSummary = new(0, 0, 0, 0, 0, 0, "等待 MES 数据");
    private IReadOnlyList<KpiTaskTrendPoint> _taskTrend = [];
    private IReadOnlyList<KpiConsumable> _consumables = [];
    private IReadOnlyList<KpiInstrumentStatus> _instruments = [];
    private IReadOnlyList<KpiStatusSlice> _statusSlices = [];
    private string _lastUpdated = "尚未刷新";

    public DateOnly Date { get => _date; private set => SetField(ref _date, value); }
    public KpiTaskSummary TaskSummary { get => _taskSummary; private set => SetField(ref _taskSummary, value); }
    public KpiSampleSummary SampleSummary { get => _sampleSummary; private set => SetField(ref _sampleSummary, value); }
    public IReadOnlyList<KpiTaskTrendPoint> TaskTrend { get => _taskTrend; private set => SetField(ref _taskTrend, value); }
    public IReadOnlyList<KpiConsumable> Consumables { get => _consumables; private set => SetField(ref _consumables, value); }
    public IReadOnlyList<KpiInstrumentStatus> Instruments { get => _instruments; private set => SetField(ref _instruments, value); }
    public IReadOnlyList<KpiStatusSlice> StatusSlices { get => _statusSlices; private set => SetField(ref _statusSlices, value); }
    public string LastUpdated { get => _lastUpdated; private set => SetField(ref _lastUpdated, value); }
    public string CompletionRate => TaskSummary.Total == 0 ? "0%" : $"{TaskSummary.Completed * 100.0 / TaskSummary.Total:0}%";

    public async Task RefreshAsync(IMesClient client, CancellationToken cancellationToken)
    {
        var dashboard = await client.GetKpiDashboardAsync(Date, cancellationToken);
        Date = dashboard.Date;
        TaskSummary = dashboard.TaskSummary;
        SampleSummary = dashboard.SampleSummary;
        TaskTrend = dashboard.TaskTrend;
        Consumables = dashboard.Consumables;
        Instruments = dashboard.Instruments;
        StatusSlices =
        [
            new("运行中", TaskSummary.Running, "#2F80ED"),
            new("已完成", TaskSummary.Completed, "#27AE60"),
            new("失败", TaskSummary.Failed, "#EB5757"),
            new("取消", TaskSummary.Cancelled, "#F2994A")
        ];
        LastUpdated = $"最后更新：{DateTime.Now:HH:mm:ss}";
        OnPropertyChanged(nameof(CompletionRate));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
