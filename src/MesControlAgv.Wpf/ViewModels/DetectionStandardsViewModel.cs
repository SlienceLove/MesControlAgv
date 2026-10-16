using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using MesControlAgv.Contracts.Materials;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class DetectionStandardsViewModel : INotifyPropertyChanged
{
    private readonly IMesClient _mes;
    private readonly string _actor = Environment.GetEnvironmentVariable("WORKFLOW_OPERATOR") ?? Environment.UserName;
    private string _search = string.Empty;
    private string _methodCode = string.Empty;
    private string _name = string.Empty;
    private string _category = string.Empty;
    private string _status = string.Empty;
    private bool _includeHistory;
    private DetectionStandardRevision? _selectedStandard;

    public DetectionStandardsViewModel(IMesClient mes)
    {
        _mes = mes;
        RefreshCommand = new AsyncCommand(RefreshAsync);
        SaveCommand = new AsyncCommand(SaveAsync);
        DeactivateCommand = new AsyncCommand(DeactivateAsync);
        NewCommand = new RelayCommand(New);
        AddIndicatorCommand = new RelayCommand(() => Indicators.Add(new DetectionIndicatorEdit()));
        RemoveIndicatorCommand = new RelayCommand<DetectionIndicatorEdit>(item =>
        {
            if (item is not null && Indicators.Count > 1) Indicators.Remove(item);
        });
        New();
    }

    public ObservableCollection<DetectionStandardRevision> Standards { get; } = [];
    public ObservableCollection<DetectionIndicatorEdit> Indicators { get; } = [];
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DeactivateCommand { get; }
    public RelayCommand NewCommand { get; }
    public RelayCommand AddIndicatorCommand { get; }
    public RelayCommand<DetectionIndicatorEdit> RemoveIndicatorCommand { get; }

    public string Search { get => _search; set => SetField(ref _search, value); }
    public string MethodCode { get => _methodCode; set => SetField(ref _methodCode, value); }
    public string Name { get => _name; set => SetField(ref _name, value); }
    public string Category { get => _category; set => SetField(ref _category, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public bool IncludeHistory { get => _includeHistory; set => SetField(ref _includeHistory, value); }
    public string RevisionText => SelectedStandard is null ? "新方法" : $"修订 {SelectedStandard.Revision} · {(SelectedStandard.IsActive ? "当前启用" : "历史 / 停用")}";
    public bool IsMethodCodeLocked => SelectedStandard is not null;

    public DetectionStandardRevision? SelectedStandard
    {
        get => _selectedStandard;
        set
        {
            if (!SetField(ref _selectedStandard, value)) return;
            if (value is not null)
            {
                MethodCode = value.MethodCode;
                Name = value.Name;
                Category = value.Category ?? string.Empty;
                Indicators.Clear();
                foreach (var rule in value.Indicators) Indicators.Add(DetectionIndicatorEdit.From(rule));
            }
            Changed(nameof(RevisionText));
            Changed(nameof(IsMethodCodeLocked));
        }
    }

    public async Task RefreshAsync()
    {
        try
        {
            var selectedId = SelectedStandard?.RevisionId;
            var standards = await _mes.GetDetectionStandardsAsync(Search, IncludeHistory, CancellationToken.None);
            Standards.Clear();
            foreach (var item in standards) Standards.Add(item);
            var selected = Standards.FirstOrDefault(item => item.RevisionId == selectedId);
            if (selectedId.HasValue && selected is null) New();
            else SelectedStandard = selected;
            Status = $"找到 {Standards.Count} 个标准修订。";
        }
        catch (Exception exception) { Status = $"标准查询失败：{exception.Message}"; }
    }

    public void New()
    {
        SelectedStandard = null;
        MethodCode = string.Empty;
        Name = string.Empty;
        Category = string.Empty;
        Indicators.Clear();
        Indicators.Add(new DetectionIndicatorEdit());
        Status = "填写方法编号和检测指标后保存。";
    }

    public async Task SaveAsync()
    {
        try
        {
            if (SelectedStandard is { IsActive: false })
                throw new InvalidOperationException("历史修订只读；请先选择当前版本或新建方法。");
            var request = new SaveDetectionStandardRequest
            {
                BaseRevisionId = SelectedStandard?.RevisionId,
                MethodCode = MethodCode,
                Name = Name,
                Category = Category,
                Actor = _actor,
                Indicators = Indicators.Select(item => item.ToRule()).ToList()
            };
            var saved = await _mes.SaveDetectionStandardAsync(request, CancellationToken.None);
            await RefreshAsync();
            SelectedStandard = Standards.FirstOrDefault(item => item.RevisionId == saved.RevisionId);
            Status = $"{saved.MethodCode} 修订 {saved.Revision} 已保存并启用。";
        }
        catch (Exception exception) { Status = $"保存失败：{exception.Message}"; }
    }

    public async Task DeactivateAsync()
    {
        if (SelectedStandard is not { IsActive: true } selected) return;
        try
        {
            await _mes.DeactivateDetectionStandardAsync(selected.MethodCode, selected.RevisionId, CancellationToken.None);
            await RefreshAsync();
            Status = $"{selected.MethodCode} 已停用，历史记录仍可查询。";
        }
        catch (Exception exception) { Status = $"停用失败：{exception.Message}"; }
    }

    public DetectionAssessmentViewModel CreateAssessment(SampleMaterial sample) =>
        new(_mes, sample, _actor);

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }
    private void Changed(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DetectionIndicatorEdit
{
    public string Name { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public string DetectionLimit { get; set; } = string.Empty;
    public string LowerLimit { get; set; } = string.Empty;
    public string UpperLimit { get; set; } = string.Empty;

    public static DetectionIndicatorEdit From(DetectionIndicatorRule rule) => new()
    {
        Name = rule.Name,
        Unit = rule.Unit,
        DetectionLimit = rule.DetectionLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        LowerLimit = rule.LowerLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        UpperLimit = rule.UpperLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
    };

    public DetectionIndicatorRule ToRule() => new()
    {
        Name = Name,
        Unit = Unit,
        DetectionLimit = ParseOptional(DetectionLimit, "检出限"),
        LowerLimit = ParseOptional(LowerLimit, "合格下限"),
        UpperLimit = ParseOptional(UpperLimit, "合格上限")
    };

    private static decimal? ParseOptional(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (decimal.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) ||
            decimal.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
        throw new ArgumentException($"{label}必须是数字。");
    }
}
