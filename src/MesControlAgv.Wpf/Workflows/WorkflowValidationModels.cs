using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Wpf.Workflows;

public sealed record WorkflowValidationSeverityFilterOption(
    string Key,
    string DisplayName,
    WorkflowValidationSeverity? Severity);

public sealed record WorkflowValidationLocationFilterOption(
    string Key,
    string DisplayName);

public sealed class WorkflowValidationIssueItemViewModel
{
    internal WorkflowValidationIssueItemViewModel(
        WorkflowValidationIssue source,
        string locationKey,
        string location,
        string suggestedAction)
    {
        Source = source;
        LocationKey = locationKey;
        Location = location;
        SuggestedAction = suggestedAction;
    }

    internal WorkflowValidationIssue Source { get; }

    internal string LocationKey { get; }

    public string Code => Source.Code;

    public string Message => Source.Message;

    public WorkflowValidationSeverity Severity => Source.Severity;

    public bool IsError => Severity == WorkflowValidationSeverity.Error;

    public string SeverityDisplayName => IsError ? "错误" : "警告";

    public Guid? NodeId => Source.NodeId;

    public Guid? EdgeId => Source.EdgeId;

    public string? ConfigurationKey => Source.ConfigurationKey ?? Source.ParameterName;

    public string Location { get; }

    public string SuggestedAction { get; }
}

public sealed class WorkflowValidationViewModel : INotifyPropertyChanged
{
    private const string AllFilterKey = "all";
    private readonly Action<WorkflowValidationIssueItemViewModel> _navigate;
    private readonly ObservableCollection<WorkflowValidationIssueItemViewModel> _allIssues = [];
    private WorkflowValidationResult? _result;
    private WorkflowValidationSeverityFilterOption _selectedSeverityFilter;
    private WorkflowValidationLocationFilterOption _selectedLocationFilter;
    private WorkflowValidationIssueItemViewModel? _selectedIssue;

    public WorkflowValidationViewModel(Action<WorkflowValidationIssueItemViewModel> navigate)
    {
        _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
        AllIssues = new ReadOnlyObservableCollection<WorkflowValidationIssueItemViewModel>(_allIssues);
        VisibleIssues = [];
        SeverityFilters =
        [
            new(AllFilterKey, "全部级别", null),
            new("error", "仅错误", WorkflowValidationSeverity.Error),
            new("warning", "仅警告", WorkflowValidationSeverity.Warning)
        ];
        LocationFilters = [new WorkflowValidationLocationFilterOption(AllFilterKey, "全部位置")];
        _selectedSeverityFilter = SeverityFilters[0];
        _selectedLocationFilter = LocationFilters[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ReadOnlyObservableCollection<WorkflowValidationIssueItemViewModel> AllIssues { get; }

    public ObservableCollection<WorkflowValidationIssueItemViewModel> VisibleIssues { get; }

    public IReadOnlyList<WorkflowValidationSeverityFilterOption> SeverityFilters { get; }

    public ObservableCollection<WorkflowValidationLocationFilterOption> LocationFilters { get; }

    public WorkflowValidationSeverityFilterOption SelectedSeverityFilter
    {
        get => _selectedSeverityFilter;
        set
        {
            var next = value ?? SeverityFilters[0];
            if (ReferenceEquals(_selectedSeverityFilter, next)) return;
            _selectedSeverityFilter = next;
            OnPropertyChanged();
            RefreshVisibleIssues();
        }
    }

    public WorkflowValidationLocationFilterOption SelectedLocationFilter
    {
        get => _selectedLocationFilter;
        set
        {
            var next = value ?? LocationFilters[0];
            if (ReferenceEquals(_selectedLocationFilter, next)) return;
            _selectedLocationFilter = next;
            OnPropertyChanged();
            RefreshVisibleIssues();
        }
    }

    public WorkflowValidationIssueItemViewModel? SelectedIssue
    {
        get => _selectedIssue;
        set
        {
            if (ReferenceEquals(_selectedIssue, value)) return;
            _selectedIssue = value;
            OnPropertyChanged();
            if (value is not null) _navigate(value);
        }
    }

    public bool HasResult => _result is not null;

    public bool HasIssues => _allIssues.Count > 0;

    public bool HasVisibleIssues => VisibleIssues.Count > 0;

    public int ErrorCount => _allIssues.Count(issue => issue.IsError);

    public int WarningCount => _allIssues.Count - ErrorCount;

    public string Summary => _result is null
        ? "尚未校验"
        : _allIssues.Count == 0
            ? "校验通过，无问题"
            : $"{ErrorCount} 个错误 / {WarningCount} 个警告";

    public string IssueTabHeader => $"校验问题 ({_allIssues.Count})";

    public string EmptyMessage => _result is null
        ? "尚未生成校验结果"
        : _allIssues.Count == 0
            ? "当前版本没有校验问题"
            : "当前筛选条件下没有问题";

    public string ValidatorMetadata => _result is null
        ? string.Empty
        : $"验证器 {_result.ValidatorVersion ?? "未知"} / 目录 {_result.CatalogVersion ?? "未知"} / " +
          _result.ValidatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public string ProfileMetadata => _result is null
        ? string.Empty
        : $"Profile {_result.ProfileProductId ?? "未知"} / {_result.ProfileVersion ?? "未知"}";

    public void Load(WorkflowValidationResult? result, WorkflowGraphDocument? document)
    {
        var severityKey = _selectedSeverityFilter.Key;
        var locationKey = _selectedLocationFilter.Key;
        _result = result;
        SetSelectedIssue(null, navigate: false);
        _allIssues.Clear();

        if (result is not null)
        {
            foreach (var issue in (result.Issues ?? [])
                         .Select(item => CreateIssue(item, document))
                         .OrderByDescending(item => item.IsError)
                         .ThenBy(item => item.Code, StringComparer.Ordinal)
                         .ThenBy(item => item.Location, StringComparer.Ordinal))
            {
                _allIssues.Add(issue);
            }
        }

        LocationFilters.Clear();
        LocationFilters.Add(new WorkflowValidationLocationFilterOption(AllFilterKey, "全部位置"));
        foreach (var option in _allIssues
                     .GroupBy(issue => issue.LocationKey, StringComparer.Ordinal)
                     .Select(group => new WorkflowValidationLocationFilterOption(group.Key, group.First().Location))
                     .OrderBy(option => option.DisplayName, StringComparer.Ordinal))
        {
            LocationFilters.Add(option);
        }

        _selectedSeverityFilter = SeverityFilters.FirstOrDefault(option => option.Key == severityKey)
            ?? SeverityFilters[0];
        _selectedLocationFilter = LocationFilters.FirstOrDefault(option => option.Key == locationKey)
            ?? LocationFilters[0];
        OnPropertyChanged(nameof(SelectedSeverityFilter));
        OnPropertyChanged(nameof(SelectedLocationFilter));
        RefreshVisibleIssues();
        RaiseResultProperties();
    }

    private void RefreshVisibleIssues()
    {
        SetSelectedIssue(null, navigate: false);
        VisibleIssues.Clear();
        foreach (var issue in _allIssues.Where(IsVisible)) VisibleIssues.Add(issue);
        OnPropertyChanged(nameof(HasVisibleIssues));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private bool IsVisible(WorkflowValidationIssueItemViewModel issue) =>
        (_selectedSeverityFilter.Severity is null || issue.Severity == _selectedSeverityFilter.Severity) &&
        (_selectedLocationFilter.Key == AllFilterKey || issue.LocationKey == _selectedLocationFilter.Key);

    private void SetSelectedIssue(WorkflowValidationIssueItemViewModel? value, bool navigate)
    {
        if (ReferenceEquals(_selectedIssue, value)) return;
        _selectedIssue = value;
        OnPropertyChanged(nameof(SelectedIssue));
        if (navigate && value is not null) _navigate(value);
    }

    private void RaiseResultProperties()
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(HasVisibleIssues));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IssueTabHeader));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(ValidatorMetadata));
        OnPropertyChanged(nameof(ProfileMetadata));
    }

    private static WorkflowValidationIssueItemViewModel CreateIssue(
        WorkflowValidationIssue issue,
        WorkflowGraphDocument? document)
    {
        if (issue.EdgeId is { } edgeId)
        {
            var edge = document?.Edges.FirstOrDefault(candidate => candidate.Id == edgeId);
            var source = FindNodeName(document, edge?.SourceNodeId);
            var target = FindNodeName(document, edge?.TargetNodeId);
            var location = edge is null
                ? $"边 {ShortId(edgeId)}"
                : $"边：{source} -> {target}";
            return new WorkflowValidationIssueItemViewModel(
                issue,
                $"edge:{edgeId:N}",
                location,
                "检查边端口、基数和路径语义");
        }

        if (issue.NodeId is { } nodeId)
        {
            var nodeName = FindNodeName(document, nodeId);
            var field = issue.ConfigurationKey ?? issue.ParameterName;
            var action = string.IsNullOrWhiteSpace(field)
                ? "检查节点配置和连接"
                : $"检查字段“{field}”";
            return new WorkflowValidationIssueItemViewModel(
                issue,
                $"node:{nodeId:N}",
                $"节点：{nodeName}",
                action);
        }

        return new WorkflowValidationIssueItemViewModel(
            issue,
            "workflow",
            "流程",
            "检查流程结构、版本和发布条件");
    }

    private static string FindNodeName(WorkflowGraphDocument? document, Guid? nodeId)
    {
        if (nodeId is null) return "未知节点";
        var node = document?.Nodes.FirstOrDefault(candidate => candidate.Id == nodeId.Value);
        return string.IsNullOrWhiteSpace(node?.Name) ? ShortId(nodeId.Value) : node.Name;
    }

    private static string ShortId(Guid id) => id.ToString("N")[..8];

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
