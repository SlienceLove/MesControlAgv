using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using MesControlAgv.Contracts.Materials;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class DetectionAssessmentViewModel : INotifyPropertyChanged
{
    private readonly IMesClient _mes;
    private readonly string _actor;
    private DetectionStandardRevision? _selectedStandard;
    private DetectionAssessment? _preview;
    private DetectionAssessment? _selectedHistory;
    private string _status = "选择检测方法和本次检测的指标。";
    private Guid _requestId = Guid.NewGuid();
    private int _inputVersion;

    public DetectionAssessmentViewModel(IMesClient mes, SampleMaterial sample, string actor)
    {
        _mes = mes;
        Sample = sample;
        _actor = actor;
        PreviewCommand = new AsyncCommand(PreviewAsync);
        SaveCommand = new AsyncCommand(SaveAsync, () => !IsSaved && Preview is not null);
    }

    public SampleMaterial Sample { get; }
    public ObservableCollection<DetectionStandardRevision> Standards { get; } = [];
    public ObservableCollection<DetectionMeasurementEdit> Measurements { get; } = [];
    public ObservableCollection<DetectionAssessment> History { get; } = [];
    public AsyncCommand PreviewCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public DetectionAssessment? Preview { get => _preview; private set => SetField(ref _preview, value); }
    public DetectionAssessment? SelectedHistory { get => _selectedHistory; set => SetField(ref _selectedHistory, value); }
    public string VerdictText => Preview is null ? string.Empty : $"预览结论：{Label(Preview.Verdict)}";
    public bool IsSaved { get; private set; }
    public bool IsMethodSelectionEnabled => string.IsNullOrWhiteSpace(Sample.DetectionMethodCode);

    public DetectionStandardRevision? SelectedStandard
    {
        get => _selectedStandard;
        set
        {
            if (!SetField(ref _selectedStandard, value)) return;
            foreach (var item in Measurements) item.PropertyChanged -= MeasurementChanged;
            Measurements.Clear();
            if (value is not null)
            {
                foreach (var rule in value.Indicators)
                {
                    var item = new DetectionMeasurementEdit(rule);
                    item.PropertyChanged += MeasurementChanged;
                    Measurements.Add(item);
                }
            }
            ResetPreview();
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            var standardsTask = _mes.GetDetectionStandardsAsync(null, false, CancellationToken.None);
            var historyTask = _mes.GetDetectionAssessmentHistoryAsync(Sample.SampleId, CancellationToken.None);
            await Task.WhenAll(standardsTask, historyTask);
            Standards.Clear();
            foreach (var item in await standardsTask) Standards.Add(item);
            if (!IsMethodSelectionEnabled)
                SelectedStandard = Standards.FirstOrDefault(item =>
                    string.Equals(item.MethodCode, Sample.DetectionMethodCode, StringComparison.OrdinalIgnoreCase));
            History.Clear();
            foreach (var item in await historyTask) History.Add(item);
            SelectedHistory = History.FirstOrDefault();
            Status = !IsMethodSelectionEnabled && SelectedStandard is null
                ? $"批次绑定的方法 {Sample.DetectionMethodCode} 已停用或不存在，暂不能创建新判定。"
                : Standards.Count == 0
                    ? "暂无启用的检测标准，请先到“检测标准”页签创建。"
                    : IsMethodSelectionEnabled
                        ? "选择检测方法和本次检测的指标。"
                        : $"已使用批次绑定的方法 {Sample.DetectionMethodCode}。";
        }
        catch (Exception exception) { Status = $"加载检测数据失败：{exception.Message}"; }
    }

    public async Task PreviewAsync()
    {
        try
        {
            var request = BuildRequest();
            var inputVersion = _inputVersion;
            var result = await _mes.PreviewDetectionAssessmentAsync(request, CancellationToken.None);
            if (inputVersion != _inputVersion)
            {
                Status = "输入已变化，请重新预览。";
                return;
            }
            Preview = result;
            var evaluated = result.Items.ToDictionary(item => item.IndicatorId);
            foreach (var item in Measurements)
                item.SetPreview(evaluated.GetValueOrDefault(item.IndicatorId));
            SaveCommand.RaiseCanExecuteChanged();
            Changed(nameof(VerdictText));
            Status = $"已预览 {result.Items.Count} 个指标。";
        }
        catch (Exception exception) { ResetPreview(); Status = $"预览失败：{exception.Message}"; }
    }

    public async Task SaveAsync()
    {
        try
        {
            if (Preview is null) throw new InvalidOperationException("请先预览本次判定。");
            var request = BuildRequest();
            var saved = await _mes.SaveDetectionAssessmentAsync(request, CancellationToken.None);
            IsSaved = true;
            SaveCommand.RaiseCanExecuteChanged();
            Preview = saved;
            Changed(nameof(VerdictText));
            History.Insert(0, saved);
            SelectedHistory = saved;
            Status = $"已保存：{Label(saved.Verdict)}。";
            _requestId = Guid.NewGuid();
        }
        catch (Exception exception) { Status = $"保存失败：{exception.Message}"; }
    }

    private DetectionAssessmentRequest BuildRequest()
    {
        if (SelectedStandard is null) throw new ArgumentException("请选择检测方法。");
        var selected = Measurements.Where(item => item.IsSelected).ToList();
        if (selected.Count == 0) throw new ArgumentException("至少选择一个检测指标。");
        return new DetectionAssessmentRequest
        {
            RequestId = _requestId,
            SampleId = Sample.SampleId,
            RevisionId = SelectedStandard.RevisionId,
            Actor = _actor,
            Measurements = selected.Select(item => item.ToMeasurement()).ToList()
        };
    }

    private void ResetPreview()
    {
        _inputVersion++;
        Preview = null;
        foreach (var item in Measurements) item.SetPreview(null);
        Changed(nameof(VerdictText));
        _requestId = Guid.NewGuid();
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void MeasurementChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DetectionMeasurementEdit.IsSelected) or nameof(DetectionMeasurementEdit.ValueText))
            ResetPreview();
    }

    public static string Label(DetectionVerdict verdict) => verdict switch
    {
        DetectionVerdict.Pass => "合格",
        DetectionVerdict.Fail => "不合格",
        _ => "无法判定"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DetectionMeasurementEdit(DetectionIndicatorRule rule) : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _valueText = string.Empty;
    private string _previewVerdict = string.Empty;
    private string _previewReason = string.Empty;
    public Guid IndicatorId => rule.IndicatorId;
    public string Name => rule.Name;
    public string Unit => rule.Unit;
    public string Range => rule.LowerLimit.HasValue || rule.UpperLimit.HasValue
        ? $"{rule.LowerLimit?.ToString(CultureInfo.InvariantCulture) ?? "-"} ~ {rule.UpperLimit?.ToString(CultureInfo.InvariantCulture) ?? "-"} {rule.Unit}"
        : "未设置合格限值";
    public string DetectionLimit => rule.DetectionLimit?.ToString(CultureInfo.InvariantCulture) ?? "-";
    public string PreviewVerdict => _previewVerdict;
    public string PreviewReason => _previewReason;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public string ValueText
    {
        get => _valueText;
        set
        {
            if (_valueText == value) return;
            _valueText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValueText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetPreview(DetectionAssessmentItem? item)
    {
        _previewVerdict = item?.VerdictText ?? string.Empty;
        _previewReason = item?.Reason ?? string.Empty;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewVerdict)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewReason)));
    }

    public DetectionMeasurement ToMeasurement()
    {
        if (!decimal.TryParse(ValueText.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
            !decimal.TryParse(ValueText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            throw new ArgumentException($"指标“{Name}”的检测值必须是数字。");
        return new DetectionMeasurement { IndicatorId = IndicatorId, Value = value, Unit = Unit };
    }
}
