using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MesControlAgv.Application;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class AiPlanningViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IMesClient _mes;
    private CancellationTokenSource? _operation;
    private readonly List<string> _messages = [];
    private AiPlanningCatalog _catalog = new();
    private AiPlanningResult? _result;
    private string _input = "", _name = "", _batch = "", _status = "描述实验目标，AI将从已发布模板生成方案草稿。";
    private bool _busy, _reviewed, _disposed;
    private Guid _saveId = Guid.NewGuid();
    private CreateAiPlanDraftRequest? _saveRequest;
    private ExperimentPlan? _saved;
    public event PropertyChangedEventHandler? PropertyChanged;

    public AiPlanningViewModel(IMesClient mes)
    {
        _mes = mes;
        GenerateCommand = new AsyncCommand(GenerateAsync, () => IsIdle && !string.IsNullOrWhiteSpace(Input) && _saveRequest is null);
        SaveCommand = new AsyncCommand(SaveAsync, () => CanSave);
        ResetCommand = new RelayCommand(Reset, () => IsIdle);
        CancelCommand = new RelayCommand(() => _operation?.Cancel(), () => !IsIdle);
        RefreshCatalogCommand = new AsyncCommand(RefreshCatalogAsync, () => IsIdle && _saveRequest is null);
    }

    public AsyncCommand GenerateCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncCommand RefreshCatalogCommand { get; }
    public ObservableCollection<AiPlanningTemplateChoice> Templates { get; } = [];
    public ObservableCollection<AiPlanningStepViewModel> Steps { get; } = [];
    public bool IsIdle => !_busy && !_disposed;
    public bool CanEdit => IsIdle && _saveRequest is null && _result is not null;
    public bool CanSave => IsIdle && _saved is null && (_saveRequest is not null ||
        _result?.ModelAvailable == true && _reviewed && string.IsNullOrWhiteSpace(_input) &&
        _result.Proposal.Questions.Count == 0 && Issues.Count == 0);
    public ExperimentPlan? SavedPlan => _saved;
    public string Input { get => _input; set { _input = value; _reviewed = false; Changed(); } }
    public string PlanName { get => _name; set { _name = value; Edited(); } }
    public string Batch { get => _batch; set { _batch = value; Edited(); } }
    public bool Reviewed { get => _reviewed; set { _reviewed = value; Changed(); } }
    public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }
    public string Explanation => _result?.Proposal.Explanation ?? "";
    public string Conversation => string.Join("\n", _messages.Select((m, i) => $"{i + 1}. {m}"));
    public string Questions => string.Join("\n", _result?.Proposal.Questions ?? []);
    public string IssuesText => string.Join("\n", Issues);
    public string CatalogSummary => $"当前已发布模板：{_catalog.Templates.Count}；固定站点、动作和参数不会被AI修改。";
    public IReadOnlyList<string> Issues => _result is null ? [] :
        AiPlanDraftBuilder.Validate(CurrentProposal(), _catalog);

    private AiPlanProposal CurrentProposal() => (_result?.Proposal ?? new()) with
    {
        Name = _name, SampleBatchId = _batch,
        Steps = Steps.Select(s => new AiPlanStep
        { TemplateKey = s.TemplateKey, EstimatedDurationMinutes = s.EstimatedDurationMinutes }).ToArray()
    };

    public Task RefreshCatalogAsync() => RunAsync(async token =>
    {
        if (_saveRequest is not null) return;
        _result = null; Steps.Clear(); _reviewed = false;
        _catalog = await _mes.GetAiPlanningCatalogAsync(token);
        Templates.Clear();
        foreach (var template in _catalog.Templates)
            Templates.Add(new AiPlanningTemplateChoice(template, ScopeChanged));
        Status = "可勾选本次相关模板；不勾选表示使用全部可用模板。";
    });

    private void ScopeChanged() { if (_saveRequest is not null) return; _result = null; Steps.Clear(); _reviewed = false; Changed(); }

    public Task GenerateAsync() => RunAsync(async token =>
    {
        if (string.IsNullOrWhiteSpace(_input) || _saveRequest is not null) return;
        var input = _input.Trim();
        var selected = Templates.Where(t => t.Selected).Select(t => t.Key).ToArray();
        // Invalidate the previous proposal before any network await.
        _result = null; _reviewed = false; Steps.Clear(); Changed();
        _catalog = await _mes.GetAiPlanningCatalogAsync(token);
        var result = await _mes.ProposeAiPlanAsync(new AiPlanningRequest
        { Message = input, PreviousMessages = _messages.ToArray(), TemplateKeys = selected }, token);
        token.ThrowIfCancellationRequested();
        if (result.CatalogRevision != _catalog.Revision)
        {
            Status = "生成期间目录已改变，请重新生成；旧方案未保留。";
            return;
        }
        _result = result;
        if (result.ModelAvailable) { _messages.Add(input); _input = ""; }
        _name = result.Proposal.Name; _batch = result.Proposal.SampleBatchId;
        foreach (var step in result.Proposal.Steps ?? [])
        {
            var template = _catalog.Templates.FirstOrDefault(t => t.Key == step.TemplateKey);
            Steps.Add(new AiPlanningStepViewModel(step, template, Edited));
        }
        _saveId = Guid.NewGuid();
        Status = result.Message;
    });

    public Task SaveAsync() => RunAsync(async token =>
    {
        if (_saved is not null) return;
        if (_saveRequest is null)
        {
            if (!_reviewed || _result?.ModelAvailable != true || !string.IsNullOrWhiteSpace(_input) ||
                _result.Proposal.Questions.Count > 0 || Issues.Count > 0) return;
            _saveRequest = new CreateAiPlanDraftRequest
            {
                RequestId = _saveId, Actor = Environment.GetEnvironmentVariable("EXPERIMENT_OPERATOR") ?? Environment.UserName,
                CatalogRevision = _catalog.Revision, Proposal = CurrentProposal(), UserIntent = Conversation
            };
        }
        // Retain the exact request on an uncertain network result; explicit retry uses the same id.
        _saved = await _mes.CreateAiPlanDraftAsync(_saveRequest, token);
        Status = $"已创建实验方案草稿 {_saved.PlanId} v{_saved.Version}，未发布、未排程、未执行。";
        try
        {
            _saved = await _mes.ValidateExperimentPlanAsync(_saved.PlanId, _saved.Version,
                new ExperimentSchedulingActionRequest { RequestId = Guid.NewGuid(), Actor = _saveRequest.Actor,
                    Reason = "AI草稿创建后的现有方案校验；不自动发布" }, token);
            Status += _saved.Validation?.IsValid == true
                ? "\n现有方案校验通过，仍需在实验方案中复核物料、交接和实验参数。"
                : "\n方案校验待处理：" + string.Join("；", _saved.Validation?.Issues.Select(i => i.Message) ?? []);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Status += "\n草稿已保存，校验未完成：" + ex.Message + "；请打开实验方案继续。"; }
    });

    private void Edited() { _reviewed = false; Changed(); }
    private void Reset()
    {
        _result = null; _messages.Clear(); Steps.Clear(); _input = ""; _name = ""; _batch = "";
        _reviewed = false; _saveRequest = null; _saved = null; _saveId = Guid.NewGuid();
        Status = "已开始新对话。已有草稿不会删除；若上一请求结果不明，请先在实验方案核对请求编号。";
        Changed();
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (!IsIdle) return;
        _busy = true; _operation = new CancellationTokenSource(); Changed();
        try { await action(_operation.Token); }
        catch (OperationCanceledException) { Status = "已取消等待；如取消了保存，请先核对草稿，勿新建重复方案。"; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Status = ex.Message + (_saveRequest is null ? "" : $"\n保存请求编号：{_saveId}。再次保存会核对同一请求，不自动重试。"); }
        finally { _operation.Dispose(); _operation = null; _busy = false; Changed(); }
    }

    private void Changed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        GenerateCommand.RaiseCanExecuteChanged(); SaveCommand.RaiseCanExecuteChanged();
        RefreshCatalogCommand.RaiseCanExecuteChanged();
        ResetCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged();
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void Dispose() { _disposed = true; _operation?.Cancel(); }
}

public sealed class AiPlanningTemplateChoice(AiWorkflowTemplate template, Action changed) : INotifyPropertyChanged
{
    private bool _selected;
    public string Key => template.Key;
    public string Name => $"{template.Name} · v{template.Version}" +
        (template.Issues.Count == 0 ? "" : "（当前不可用）");
    public bool Selected
    {
        get => _selected;
        set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); changed(); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AiPlanningStepViewModel : INotifyPropertyChanged
{
    private string _durationText;
    private readonly Action _changed;
    public AiPlanningStepViewModel(AiPlanStep step, AiWorkflowTemplate? template, Action changed)
    {
        TemplateKey = step.TemplateKey; _durationText = step.EstimatedDurationMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture); _changed = changed;
        Name = template is null ? "未知模板" : $"{template.Name} · v{template.Version}";
        Details = template is null ? "目录中不存在此模板" : string.Join("\n", template.Definition.Nodes.OrderBy(n => n.Order)
            .Select(n => $"{n.Order}. {n.Name} [{n.NodeTypeId}] 站点:{n.TargetStation}\n    " +
                string.Join("；", n.Configuration.Select(p => $"{p.Key}={p.Value}")
                    .Concat(n.Parameters.Select(p => $"{p.Name}={p.Value}")))));
    }
    public string TemplateKey { get; }
    public string Name { get; }
    public string Details { get; }
    public int EstimatedDurationMinutes
    {
        get => int.TryParse(_durationText, out var value) ? value : 0;
        set => DurationText = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    public string DurationText
    {
        get => _durationText;
        set { _durationText = value; PropertyChanged?.Invoke(this, new(null)); _changed(); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
