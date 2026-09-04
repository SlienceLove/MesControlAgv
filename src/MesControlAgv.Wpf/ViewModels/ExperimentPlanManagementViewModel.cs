using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// G5-D experiment-plan projection. It edits planning records only and never
/// executes a workflow or contacts a device path.
/// </summary>
public sealed class ExperimentPlanManagementViewModel : ExperimentBindableObject, IDisposable
{
    private readonly IMesClient _mes;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly AsyncCommand _refreshCommand;
    private readonly RelayCommand _newPlanCommand;
    private readonly AsyncCommand _saveDraftCommand;
    private readonly AsyncCommand _validateCommand;
    private readonly AsyncCommand _publishCommand;
    private readonly AsyncCommand _nextDraftCommand;
    private readonly RelayCommand _addMaterialCommand;
    private readonly RelayCommand<ExperimentMaterialEditorViewModel> _removeMaterialCommand;
    private readonly RelayCommand _addParameterCommand;
    private readonly RelayCommand<ExperimentParameterEditorViewModel> _removeParameterCommand;
    private readonly RelayCommand _addResourceCommand;
    private readonly RelayCommand<ExperimentResourceRequirementEditorViewModel> _removeResourceCommand;
    private readonly RelayCommand _addWorkflowStepCommand;
    private readonly RelayCommand<ExperimentPlanWorkflowStepEditorViewModel> _removeWorkflowStepCommand;
    private readonly RelayCommand _moveWorkflowStepUpCommand;
    private readonly RelayCommand _moveWorkflowStepDownCommand;
    private readonly List<PublishedWorkflowVersionOption> _publishedWorkflowVersionCatalog = [];
    private readonly Dictionary<(Guid WorkflowId, int Version), PublishedWorkflowVersionOption> _referencedWorkflowVersions = [];
    private ExperimentPlanListItemViewModel? _selectedPlan;
    private ExperimentPlanVersionItemViewModel? _selectedVersion;
    private PublishedWorkflowVersionOption? _selectedWorkflowVersion;
    private ExperimentPlanWorkflowStepEditorViewModel? _selectedWorkflowStep;
    private ExperimentPlanValidationIssueItemViewModel? _selectedValidationIssue;
    private bool _isNewPlan;
    private bool _isDirty;
    private bool _isBusy;
    private bool _isLoadingEditor;
    private bool _suppressPlanSelectionLoad;
    private bool _hasLoaded;
    private bool _showVerifiedWorkflowTemplatesOnly;
    private string _name = string.Empty;
    private string _description = string.Empty;
    private string _profileProductId = string.Empty;
    private string _profileVersion = string.Empty;
    private string _layoutId = string.Empty;
    private string _operatorName = Environment.GetEnvironmentVariable("EXPERIMENT_OPERATOR") ?? Environment.UserName;
    private string _reason = string.Empty;
    private string _statusMessage = "方案数据尚未加载。";
    private string _errorMessage = string.Empty;
    private int _detailTabIndex;
    private long _selectionRevision;

    public ExperimentPlanManagementViewModel(IMesClient mes)
    {
        _mes = mes ?? throw new ArgumentNullException(nameof(mes));
        _refreshCommand = new AsyncCommand(() => RefreshAsync(), () => !IsBusy);
        _newPlanCommand = new RelayCommand(BeginNewPlan, () => !IsBusy);
        _saveDraftCommand = new AsyncCommand(SaveDraftAsync, () => CanSaveDraft);
        _validateCommand = new AsyncCommand(ValidateAsync, () => CanValidate);
        _publishCommand = new AsyncCommand(PublishAsync, () => CanPublish);
        _nextDraftCommand = new AsyncCommand(CreateNextDraftAsync, () => CanCreateNextDraft);
        _addMaterialCommand = new RelayCommand(AddMaterial, () => CanEdit);
        _removeMaterialCommand = new RelayCommand<ExperimentMaterialEditorViewModel>(RemoveMaterial, item => CanEdit && item is not null);
        _addParameterCommand = new RelayCommand(AddParameter, () => CanEdit);
        _removeParameterCommand = new RelayCommand<ExperimentParameterEditorViewModel>(RemoveParameter, item => CanEdit && item is not null);
        _addResourceCommand = new RelayCommand(AddResourceRequirement, () => CanEdit);
        _removeResourceCommand = new RelayCommand<ExperimentResourceRequirementEditorViewModel>(RemoveResourceRequirement, item => CanEdit && item is not null);
        _addWorkflowStepCommand = new RelayCommand(AddWorkflowStep, () => CanEdit);
        _removeWorkflowStepCommand = new RelayCommand<ExperimentPlanWorkflowStepEditorViewModel>(
            RemoveWorkflowStep,
            item => CanEdit && item is not null && WorkflowSteps.Count > 1);
        _moveWorkflowStepUpCommand = new RelayCommand(MoveWorkflowStepUp, () => CanEdit && CanMoveWorkflowStepUp);
        _moveWorkflowStepDownCommand = new RelayCommand(MoveWorkflowStepDown, () => CanEdit && CanMoveWorkflowStepDown);
    }

    public ObservableCollection<ExperimentPlanListItemViewModel> Plans { get; } = [];
    public ObservableCollection<ExperimentPlanVersionItemViewModel> Versions { get; } = [];
    public ObservableCollection<PublishedWorkflowVersionOption> PublishedWorkflowVersions { get; } = [];
    public ObservableCollection<ExperimentPlanValidationIssueItemViewModel> ValidationIssues { get; } = [];
    public ObservableCollection<ExperimentMaterialEditorViewModel> Materials { get; } = [];
    public ObservableCollection<ExperimentParameterEditorViewModel> Parameters { get; } = [];
    public ObservableCollection<ExperimentResourceRequirementEditorViewModel> ResourceRequirements { get; } = [];
    public ObservableCollection<ExperimentPlanWorkflowStepEditorViewModel> WorkflowSteps { get; } = [];

    /// <summary>
    /// When enabled, the workflow picker shows system/verified templates first,
    /// while keeping every workflow version already referenced by the current
    /// plan in the projection so changing the filter cannot clear a selection.
    /// </summary>
    public bool ShowVerifiedWorkflowTemplatesOnly
    {
        get => _showVerifiedWorkflowTemplatesOnly;
        set
        {
            if (!SetField(ref _showVerifiedWorkflowTemplatesOnly, value)) return;
            RebindWorkflowStepOptions(_publishedWorkflowVersionCatalog);
            RebuildPublishedWorkflowVersions();
            OnPropertyChanged(nameof(WorkflowTemplateFilterSummary));
        }
    }

    public IReadOnlyList<string> ResourceTypes { get; } =
    [
        ExperimentResourceTypeIds.Agv,
        ExperimentResourceTypeIds.RouteSegment,
        ExperimentResourceTypeIds.Station,
        ExperimentResourceTypeIds.RobotArm,
        ExperimentResourceTypeIds.Instrument,
        ExperimentResourceTypeIds.Workstation,
        ExperimentResourceTypeIds.Carrier,
        ExperimentResourceTypeIds.OperatorStation
    ];

    public ICommand RefreshCommand => _refreshCommand;
    public ICommand NewPlanCommand => _newPlanCommand;
    public ICommand SaveDraftCommand => _saveDraftCommand;
    public ICommand ValidateCommand => _validateCommand;
    public ICommand PublishCommand => _publishCommand;
    public ICommand CreateNextDraftCommand => _nextDraftCommand;
    public ICommand AddMaterialCommand => _addMaterialCommand;
    public ICommand RemoveMaterialCommand => _removeMaterialCommand;
    public ICommand AddParameterCommand => _addParameterCommand;
    public ICommand RemoveParameterCommand => _removeParameterCommand;
    public ICommand AddResourceCommand => _addResourceCommand;
    public ICommand RemoveResourceCommand => _removeResourceCommand;
    public ICommand AddWorkflowStepCommand => _addWorkflowStepCommand;
    public ICommand RemoveWorkflowStepCommand => _removeWorkflowStepCommand;
    public ICommand MoveWorkflowStepUpCommand => _moveWorkflowStepUpCommand;
    public ICommand MoveWorkflowStepDownCommand => _moveWorkflowStepDownCommand;

    public ExperimentPlanListItemViewModel? SelectedPlan
    {
        get => _selectedPlan;
        set
        {
            if (!SetField(ref _selectedPlan, value)) return;
            if (!_suppressPlanSelectionLoad)
                _ = LoadSelectedPlanAsync(value, ++_selectionRevision);
        }
    }

    public ExperimentPlanVersionItemViewModel? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!SetField(ref _selectedVersion, value)) return;
            if (value is not null) LoadEditor(value.Plan);
            RaiseStateChanged();
        }
    }

    public PublishedWorkflowVersionOption? SelectedWorkflowVersion
    {
        get => _selectedWorkflowVersion;
        set
        {
            if (!SetField(ref _selectedWorkflowVersion, value)) return;
            if (!_isLoadingEditor && WorkflowSteps.Count > 0 &&
                !ReferenceEquals(WorkflowSteps[0].SelectedWorkflowVersion, value))
            {
                WorkflowSteps[0].SelectedWorkflowVersion = value;
            }
            MarkDirty();
        }
    }

    public ExperimentPlanWorkflowStepEditorViewModel? SelectedWorkflowStep
    {
        get => _selectedWorkflowStep;
        set
        {
            if (!SetField(ref _selectedWorkflowStep, value)) return;
            if (!_isLoadingEditor && value is not null &&
                WorkflowSteps.Count > 0 && ReferenceEquals(WorkflowSteps[0], value) &&
                !ReferenceEquals(SelectedWorkflowVersion, value.SelectedWorkflowVersion))
            {
                SetField(ref _selectedWorkflowVersion, value.SelectedWorkflowVersion, nameof(SelectedWorkflowVersion));
            }
            RaiseStateChanged();
        }
    }

    public ExperimentPlanValidationIssueItemViewModel? SelectedValidationIssue
    {
        get => _selectedValidationIssue;
        set
        {
            if (!SetField(ref _selectedValidationIssue, value) || value is null) return;
            DetailTabIndex = value.Field switch
            {
                "materialRequirements" => 1,
                "defaultParameters" => 2,
                "resourceRequirements" => 3,
                "workflowSteps" => 4,
                _ => 0
            };
        }
    }

    public string Name { get => _name; set => SetEditorField(ref _name, value ?? string.Empty); }
    public string Description { get => _description; set => SetEditorField(ref _description, value ?? string.Empty); }
    public string ProfileProductId { get => _profileProductId; set => SetEditorField(ref _profileProductId, value ?? string.Empty); }
    public string ProfileVersion { get => _profileVersion; set => SetEditorField(ref _profileVersion, value ?? string.Empty); }
    public string LayoutId { get => _layoutId; set => SetEditorField(ref _layoutId, value ?? string.Empty); }

    public string OperatorName
    {
        get => _operatorName;
        set
        {
            if (!SetField(ref _operatorName, value ?? string.Empty)) return;
            RaiseStateChanged();
        }
    }

    public string Reason
    {
        get => _reason;
        set
        {
            if (!SetField(ref _reason, value ?? string.Empty)) return;
            RaiseStateChanged();
        }
    }

    public bool IsNewPlan
    {
        get => _isNewPlan;
        private set
        {
            if (!SetField(ref _isNewPlan, value)) return;
            RaiseStateChanged();
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (!SetField(ref _isDirty, value)) return;
            RaiseStateChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            RaiseStateChanged();
        }
    }

    public string StatusMessage { get => _statusMessage; private set => SetField(ref _statusMessage, value); }
    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (!SetField(ref _errorMessage, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public int DetailTabIndex { get => _detailTabIndex; set => SetField(ref _detailTabIndex, value); }
    public bool CanEdit => !IsBusy && (IsNewPlan || CurrentPlan?.Status is ExperimentPlanStatus.Draft or ExperimentPlanStatus.Validated);
    public bool CanSaveDraft => CanEdit && IsDirty && HasActionMetadata;
    public bool CanValidate => !IsBusy && !IsDirty && HasActionMetadata &&
                               CurrentPlan?.Status is ExperimentPlanStatus.Draft or ExperimentPlanStatus.Validated;
    public bool CanPublish => !IsBusy && !IsDirty && HasActionMetadata &&
                              CurrentPlan?.Status == ExperimentPlanStatus.Validated &&
                              CurrentPlan.Validation?.IsValid == true;
    public bool CanCreateNextDraft => !IsBusy && HasActionMetadata &&
                                      CurrentPlan?.Status == ExperimentPlanStatus.Published;
    public string CurrentVersion => IsNewPlan
        ? "新方案 / v1 草稿"
        : CurrentPlan is null
            ? "未选择版本"
            : $"v{CurrentPlan.Version} / {ExperimentUiText.PlanStatus(CurrentPlan.Status)}";
    public string ValidationSummary => CurrentPlan?.Validation is null
        ? "尚未校验"
        : CurrentPlan.Validation.IsValid
            ? $"校验通过 / {CurrentPlan.Validation.ValidatorVersion}"
            : $"校验未通过 / {CurrentPlan.Validation.Issues.Count} 项问题";
    public string WorkflowStepsSummary => WorkflowSteps.Count == 0
        ? "尚未配置固定流程"
        : $"{WorkflowSteps.Count} 个固定流程 · {string.Join(" → ", WorkflowSteps.Select(step => step.NameOrWorkflowDisplay))}";
    public bool CanMoveWorkflowStepUp => SelectedWorkflowStep is not null &&
                                         WorkflowSteps.IndexOf(SelectedWorkflowStep) > 0;
    public bool CanMoveWorkflowStepDown => SelectedWorkflowStep is not null &&
                                           WorkflowSteps.IndexOf(SelectedWorkflowStep) >= 0 &&
                                           WorkflowSteps.IndexOf(SelectedWorkflowStep) < WorkflowSteps.Count - 1;
    public bool HasValidationIssues => ValidationIssues.Count > 0;
    public string WorkflowTemplateFilterSummary
    {
        get
        {
            if (!ShowVerifiedWorkflowTemplatesOnly)
                return $"全部已发布模板 · {PublishedWorkflowVersions.Count} 项";

            var verifiedCount = PublishedWorkflowVersions.Count(option => option.IsPreset);
            var preservedCount = PublishedWorkflowVersions.Count(option => !option.IsPreset);
            return preservedCount == 0
                ? $"仅系统/已验证模板 · {verifiedCount} 项"
                : $"仅系统/已验证模板 · {verifiedCount} 项 · 保留当前引用 {preservedCount} 项";
        }
    }
    private ExperimentPlan? CurrentPlan => SelectedVersion?.Plan;
    private bool HasActionMetadata =>
        !string.IsNullOrWhiteSpace(OperatorName) && !string.IsNullOrWhiteSpace(Reason);

    public Task EnsureLoadedAsync() => _hasLoaded ? Task.CompletedTask : RefreshAsync();

    public Task RefreshAsync(Guid? preferredPlanId = null, int? preferredVersion = null) =>
        RunOperationAsync(
            "正在刷新实验方案...",
            async () =>
            {
                await RefreshCoreAsync(preferredPlanId, preferredVersion);
                StatusMessage = $"已加载 {Plans.Count} 个实验方案。";
            });

    public Task SaveDraftAsync() => RunOperationAsync(
        "正在保存方案草稿...",
        async () =>
        {
            var request = new SaveExperimentPlanDraftRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = OperatorName.Trim(),
                Reason = Reason.Trim(),
                Draft = BuildDraft()
            };
            var saved = IsNewPlan
                ? await _mes.CreateExperimentPlanDraftAsync(request, _shutdown.Token)
                : await _mes.UpdateExperimentPlanDraftAsync(
                    CurrentPlan!.PlanId,
                    CurrentPlan.Version,
                    request,
                    _shutdown.Token);
            await RefreshCoreAsync(saved.PlanId, saved.Version);
            StatusMessage = $"方案“{saved.Name}”v{saved.Version} 草稿已保存。";
        });

    public Task ValidateAsync() => RunPlanActionAsync(
        "正在校验方案...",
        (plan, request) => _mes.ValidateExperimentPlanAsync(plan.PlanId, plan.Version, request, _shutdown.Token),
        plan => plan.Validation?.IsValid == true
            ? $"方案“{plan.Name}”v{plan.Version} 校验通过。"
            : $"方案“{plan.Name}”v{plan.Version} 有 {plan.Validation?.Issues.Count ?? 0} 项问题。",
        selectIssuesTab: true);

    public Task PublishAsync() => RunPlanActionAsync(
        "正在发布方案...",
        (plan, request) => _mes.PublishExperimentPlanAsync(plan.PlanId, plan.Version, request, _shutdown.Token),
        plan => $"方案“{plan.Name}”v{plan.Version} 已发布。",
        selectIssuesTab: false);

    public Task CreateNextDraftAsync() => RunPlanActionAsync(
        "正在复制下一草稿版本...",
        (plan, request) => _mes.CreateNextExperimentPlanDraftAsync(plan.PlanId, plan.Version, request, _shutdown.Token),
        plan => $"已创建方案“{plan.Name}”v{plan.Version} 草稿。",
        selectIssuesTab: false);

    private async Task RunPlanActionAsync(
        string runningMessage,
        Func<ExperimentPlan, ExperimentSchedulingActionRequest, Task<ExperimentPlan>> action,
        Func<ExperimentPlan, string> successMessage,
        bool selectIssuesTab)
    {
        await RunOperationAsync(
            runningMessage,
            async () =>
            {
                var current = CurrentPlan ?? throw new InvalidOperationException("请先选择方案版本。");
                var result = await action(current, CreateActionRequest());
                await RefreshCoreAsync(result.PlanId, result.Version);
                if (selectIssuesTab && result.Validation?.IsValid == false)
                    SelectedValidationIssue = ValidationIssues.FirstOrDefault();
                StatusMessage = successMessage(result);
            });
    }

    private async Task RefreshCoreAsync(Guid? preferredPlanId, int? preferredVersion)
    {
        var oldPlanId = preferredPlanId ?? SelectedPlan?.PlanId ?? CurrentPlan?.PlanId;
        var oldVersion = preferredVersion ?? CurrentPlan?.Version;
        var planTask = _mes.GetExperimentPlansAsync(_shutdown.Token);
        var workflowTask = LoadPublishedWorkflowVersionsAsync();
        await Task.WhenAll(planTask, workflowTask);

        Plans.Clear();
        foreach (var plan in planTask.Result)
            Plans.Add(new ExperimentPlanListItemViewModel(plan));

        _suppressPlanSelectionLoad = true;
        try
        {
            SelectedPlan = oldPlanId is { } planId
                ? Plans.FirstOrDefault(item => item.PlanId == planId) ?? Plans.FirstOrDefault()
                : Plans.FirstOrDefault();
        }
        finally
        {
            _suppressPlanSelectionLoad = false;
        }

        if (SelectedPlan is null)
        {
            Versions.Clear();
            SelectedVersion = null;
            BeginNewPlan();
        }
        else
        {
            await LoadVersionsCoreAsync(SelectedPlan.PlanId, oldVersion);
        }

        _hasLoaded = true;
    }

    private async Task LoadPublishedWorkflowVersionsAsync()
    {
        var definitions = await _mes.GetWorkflowsAsync(_shutdown.Token);
        var versionTasks = definitions.Select(async definition => new
        {
            Definition = definition,
            Versions = await _mes.GetWorkflowVersionsAsync(definition.Id, _shutdown.Token)
        });
        var results = await Task.WhenAll(versionTasks);
        var options = results
            .SelectMany(item => item.Versions
                .Where(version => version.Status == WorkflowVersionStatus.Published &&
                                  version.PublishStatus == WorkflowPublishStatus.Published)
                .Select(version => new PublishedWorkflowVersionOption(
                    version.WorkflowId,
                    version.Version,
                    string.IsNullOrWhiteSpace(version.Definition.Name)
                        ? item.Definition.Name
                        : version.Definition.Name,
                    IsPreset: version.Definition.IsPreset || item.Definition.IsPreset)))
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(option => option.Version)
            .ToArray();

        _publishedWorkflowVersionCatalog.Clear();
        _publishedWorkflowVersionCatalog.AddRange(options);
        RebindWorkflowStepOptions(_publishedWorkflowVersionCatalog);
        RebuildPublishedWorkflowVersions();
    }

    private async Task LoadSelectedPlanAsync(ExperimentPlanListItemViewModel? selected, long revision)
    {
        if (selected is null) return;
        await RunOperationAsync(
            "正在加载方案版本...",
            async () =>
            {
                var versions = await _mes.GetExperimentPlanVersionsAsync(selected.PlanId, _shutdown.Token);
                if (revision != _selectionRevision || SelectedPlan?.PlanId != selected.PlanId) return;
                ApplyVersions(versions, selected.Version);
                StatusMessage = $"已加载方案“{selected.Name}”的 {Versions.Count} 个版本。";
            });
    }

    private async Task LoadVersionsCoreAsync(Guid planId, int? preferredVersion)
    {
        var versions = await _mes.GetExperimentPlanVersionsAsync(planId, _shutdown.Token);
        ApplyVersions(versions, preferredVersion);
    }

    private void ApplyVersions(IReadOnlyList<ExperimentPlan> versions, int? preferredVersion)
    {
        Versions.Clear();
        foreach (var version in versions.OrderByDescending(item => item.Version))
            Versions.Add(new ExperimentPlanVersionItemViewModel(version));
        SelectedVersion = preferredVersion is { } number
            ? Versions.FirstOrDefault(item => item.Version == number) ?? Versions.FirstOrDefault()
            : Versions.FirstOrDefault();
    }

    private void LoadEditor(ExperimentPlan plan)
    {
        _isLoadingEditor = true;
        try
        {
            _referencedWorkflowVersions.Clear();
            IsNewPlan = false;
            Name = plan.Name;
            Description = plan.Description;
            ProfileProductId = plan.ProfileProductId ?? string.Empty;
            ProfileVersion = plan.ProfileVersion ?? string.Empty;
            LayoutId = plan.LayoutId ?? string.Empty;
            var planSteps = plan.WorkflowSteps.Count > 0
                ? plan.WorkflowSteps
                : CreateLegacyWorkflowStep(plan.WorkflowId, plan.WorkflowVersion, plan.PlanId);
            ReplaceWorkflowStepEditors(planSteps.Select(step =>
                ExperimentPlanWorkflowStepEditorViewModel.From(
                    step,
                    FindOrAddWorkflowOption(step.WorkflowId, step.WorkflowVersion, step.Name))));
            SelectedWorkflowVersion = WorkflowSteps.FirstOrDefault()?.SelectedWorkflowVersion ??
                                      FindOrAddWorkflowOption(plan.WorkflowId, plan.WorkflowVersion, plan.Name);
            ReplaceEditors(Materials, plan.MaterialRequirements.Select(ExperimentMaterialEditorViewModel.From));
            ReplaceEditors(Parameters, plan.DefaultParameters.Select(ExperimentParameterEditorViewModel.From));
            ReplaceEditors(ResourceRequirements, plan.ResourceRequirements.Select(ExperimentResourceRequirementEditorViewModel.From));
            ValidationIssues.Clear();
            foreach (var issue in plan.Validation?.Issues ?? [])
                ValidationIssues.Add(new ExperimentPlanValidationIssueItemViewModel(issue));
            SelectedValidationIssue = null;
            IsDirty = false;
            DetailTabIndex = 0;
        }
        finally
        {
            _isLoadingEditor = false;
        }
        RebuildPublishedWorkflowVersions();
        RaiseStateChanged();
    }

    private PublishedWorkflowVersionOption? FindOrAddWorkflowOption(
        Guid workflowId,
        int version,
        string? fallbackName = null)
    {
        if (workflowId == Guid.Empty || version <= 0) return null;
        var option = _publishedWorkflowVersionCatalog.FirstOrDefault(item =>
            item.WorkflowId == workflowId && item.Version == version);
        if (option is not null) return option;

        var key = (workflowId, version);
        if (_referencedWorkflowVersions.TryGetValue(key, out option)) return option;

        option = new PublishedWorkflowVersionOption(
            workflowId,
            version,
            string.IsNullOrWhiteSpace(fallbackName) ? "已引用流程" : fallbackName.Trim(),
            IsAvailable: false);
        _referencedWorkflowVersions[key] = option;
        return option;
    }

    private static IReadOnlyList<ExperimentPlanWorkflowStep> CreateLegacyWorkflowStep(
        Guid workflowId,
        int version,
        Guid stepId) => workflowId == Guid.Empty || version <= 0
        ? []
        :
        [
            new ExperimentPlanWorkflowStep
            {
                StepId = stepId == Guid.Empty ? Guid.NewGuid() : stepId,
                Order = 1,
                WorkflowId = workflowId,
                WorkflowVersion = version
            }
        ];

    private void BeginNewPlan()
    {
        _selectionRevision++;
        _suppressPlanSelectionLoad = true;
        _isLoadingEditor = true;
        try
        {
            _referencedWorkflowVersions.Clear();
            SelectedPlan = null;
            Versions.Clear();
            SelectedVersion = null;
            IsNewPlan = true;
            Name = string.Empty;
            Description = string.Empty;
            ProfileProductId = string.Empty;
            ProfileVersion = string.Empty;
            LayoutId = string.Empty;
            SelectedWorkflowVersion = PublishedWorkflowVersions.FirstOrDefault();
            ClearEditors();
            if (SelectedWorkflowVersion is not null)
                AddWorkflowStepCore(SelectedWorkflowVersion);
            ValidationIssues.Clear();
            SelectedValidationIssue = null;
            DetailTabIndex = 0;
        }
        finally
        {
            _isLoadingEditor = false;
            _suppressPlanSelectionLoad = false;
        }
        IsDirty = true;
        ErrorMessage = string.Empty;
        StatusMessage = "正在编辑新方案草稿。";
        RebuildPublishedWorkflowVersions();
        RaiseStateChanged();
    }

    private ExperimentPlanDraft BuildDraft()
    {
        var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in Parameters)
        {
            if (!parameters.TryAdd(parameter.Name.Trim(), NormalizeOptional(parameter.Value)))
                throw new InvalidOperationException($"默认参数“{parameter.Name}”重复。");
        }

        return new ExperimentPlanDraft
        {
            Name = Name,
            Description = Description,
            WorkflowId = SelectedWorkflowVersion?.WorkflowId ?? Guid.Empty,
            WorkflowVersion = SelectedWorkflowVersion?.Version ?? 0,
            WorkflowSteps = WorkflowSteps.Select((step, index) => step.ToContract(index + 1)).ToArray(),
            MaterialRequirements = Materials.Select(item => item.ToContract()).ToArray(),
            DefaultParameters = parameters,
            ResourceRequirements = ResourceRequirements.Select(item => item.ToContract()).ToArray(),
            ProfileProductId = NormalizeOptional(ProfileProductId),
            ProfileVersion = NormalizeOptional(ProfileVersion),
            LayoutId = NormalizeOptional(LayoutId)
        };
    }

    private ExperimentSchedulingActionRequest CreateActionRequest() => new()
    {
        RequestId = Guid.NewGuid(),
        Actor = OperatorName.Trim(),
        Reason = Reason.Trim()
    };

    private void AddMaterial()
    {
        var item = new ExperimentMaterialEditorViewModel();
        AttachEditor(item);
        Materials.Add(item);
        MarkDirty();
    }

    private void RemoveMaterial(ExperimentMaterialEditorViewModel? item)
    {
        if (item is null || !Materials.Remove(item)) return;
        item.PropertyChanged -= EditorPropertyChanged;
        MarkDirty();
    }

    private void AddParameter()
    {
        var item = new ExperimentParameterEditorViewModel();
        AttachEditor(item);
        Parameters.Add(item);
        MarkDirty();
    }

    private void RemoveParameter(ExperimentParameterEditorViewModel? item)
    {
        if (item is null || !Parameters.Remove(item)) return;
        item.PropertyChanged -= EditorPropertyChanged;
        MarkDirty();
    }

    private void AddResourceRequirement()
    {
        var item = new ExperimentResourceRequirementEditorViewModel();
        AttachEditor(item);
        ResourceRequirements.Add(item);
        MarkDirty();
    }

    private void RemoveResourceRequirement(ExperimentResourceRequirementEditorViewModel? item)
    {
        if (item is null || !ResourceRequirements.Remove(item)) return;
        item.PropertyChanged -= EditorPropertyChanged;
        MarkDirty();
    }

    private void ReplaceEditors<T>(ObservableCollection<T> target, IEnumerable<T> source)
        where T : ExperimentBindableObject
    {
        foreach (var item in target) item.PropertyChanged -= EditorPropertyChanged;
        target.Clear();
        foreach (var item in source)
        {
            AttachEditor(item);
            target.Add(item);
        }
    }

    private void ClearEditors()
    {
        ReplaceEditors(Materials, []);
        ReplaceEditors(Parameters, []);
        ReplaceEditors(ResourceRequirements, []);
        ReplaceWorkflowStepEditors([]);
    }

    private void AddWorkflowStep()
    {
        AddWorkflowStepCore(PublishedWorkflowVersions.FirstOrDefault());
    }

    private void AddWorkflowStepCore(PublishedWorkflowVersionOption? option)
    {
        var item = new ExperimentPlanWorkflowStepEditorViewModel
        {
            SelectedWorkflowVersion = option,
            Name = option?.Name ?? $"步骤 {WorkflowSteps.Count + 1}"
        };
        item.SetOrder(WorkflowSteps.Count + 1);
        AttachWorkflowStep(item);
        WorkflowSteps.Add(item);
        SelectedWorkflowStep = item;
        OnPropertyChanged(nameof(WorkflowStepsSummary));
        RaiseStateChanged();
        MarkDirty();
    }

    private void RemoveWorkflowStep(ExperimentPlanWorkflowStepEditorViewModel? item)
    {
        if (item is null || WorkflowSteps.Count <= 1 || !WorkflowSteps.Remove(item)) return;
        item.PropertyChanged -= WorkflowStepPropertyChanged;
        ReindexWorkflowSteps();
        SelectedWorkflowStep = WorkflowSteps.ElementAtOrDefault(Math.Max(0, WorkflowSteps.Count - 1));
        OnPropertyChanged(nameof(WorkflowStepsSummary));
        RaiseStateChanged();
        MarkDirty();
    }

    private void MoveWorkflowStepUp()
    {
        if (SelectedWorkflowStep is null) return;
        var index = WorkflowSteps.IndexOf(SelectedWorkflowStep);
        if (index <= 0) return;
        WorkflowSteps.Move(index, index - 1);
        ReindexWorkflowSteps();
        OnPropertyChanged(nameof(WorkflowStepsSummary));
        RaiseStateChanged();
        MarkDirty();
    }

    private void MoveWorkflowStepDown()
    {
        if (SelectedWorkflowStep is null) return;
        var index = WorkflowSteps.IndexOf(SelectedWorkflowStep);
        if (index < 0 || index >= WorkflowSteps.Count - 1) return;
        WorkflowSteps.Move(index, index + 1);
        ReindexWorkflowSteps();
        OnPropertyChanged(nameof(WorkflowStepsSummary));
        RaiseStateChanged();
        MarkDirty();
    }

    private void ReplaceWorkflowStepEditors(IEnumerable<ExperimentPlanWorkflowStepEditorViewModel> source)
    {
        foreach (var item in WorkflowSteps) item.PropertyChanged -= WorkflowStepPropertyChanged;
        WorkflowSteps.Clear();
        foreach (var item in source)
        {
            AttachWorkflowStep(item);
            WorkflowSteps.Add(item);
        }
        ReindexWorkflowSteps();
        SelectedWorkflowStep = WorkflowSteps.FirstOrDefault();
        OnPropertyChanged(nameof(WorkflowStepsSummary));
    }

    private void ReplaceWorkflowSteps(IEnumerable<ExperimentPlanWorkflowStep> source)
    {
        ReplaceWorkflowStepEditors(source.Select(step =>
            ExperimentPlanWorkflowStepEditorViewModel.From(
                step,
                FindOrAddWorkflowOption(step.WorkflowId, step.WorkflowVersion, step.Name))));
    }

    private void RebindWorkflowStepOptions(IReadOnlyList<PublishedWorkflowVersionOption> options)
    {
        if (WorkflowSteps.Count == 0) return;
        var wasLoading = _isLoadingEditor;
        _isLoadingEditor = true;
        try
        {
            foreach (var step in WorkflowSteps)
            {
                var current = step.SelectedWorkflowVersion;
                if (current is null) continue;
                step.SelectedWorkflowVersion = options.FirstOrDefault(option =>
                    option.WorkflowId == current.WorkflowId && option.Version == current.Version) ??
                    FindOrAddWorkflowOption(current.WorkflowId, current.Version);
            }
        }
        finally
        {
            _isLoadingEditor = wasLoading;
        }
    }

    private void RebuildPublishedWorkflowVersions()
    {
        var workflowSelections = WorkflowSteps
            .Select(step => (Step: step, Option: step.SelectedWorkflowVersion))
            .ToArray();
        var selectedWorkflowVersion = SelectedWorkflowVersion;
        var wasLoading = _isLoadingEditor;
        _isLoadingEditor = true;
        try
        {
            var visible = _publishedWorkflowVersionCatalog
                .Where(option => !ShowVerifiedWorkflowTemplatesOnly || option.IsPreset)
                .ToList();

            // A filtered catalog must still contain the versions selected by
            // the current plan. This keeps WPF ComboBox SelectedItem references
            // stable and makes legacy/unavailable references visible instead of
            // silently replacing them with the first matching template.
            var referenced = WorkflowSteps
                .Select(step => step.SelectedWorkflowVersion)
                .Append(SelectedWorkflowVersion)
                .Concat(_referencedWorkflowVersions.Values)
                .Where(option => option is not null)
                .Cast<PublishedWorkflowVersionOption>()
                .GroupBy(option => (option.WorkflowId, option.Version))
                .Select(group => group.First());

            foreach (var option in referenced)
            {
                if (visible.Any(item => item.WorkflowId == option.WorkflowId && item.Version == option.Version))
                    continue;
                visible.Add(option);
            }

            PublishedWorkflowVersions.Clear();
            foreach (var option in visible
                         .OrderBy(option => option.IsAvailable ? 0 : 1)
                         .ThenBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenByDescending(option => option.Version))
            {
                PublishedWorkflowVersions.Add(option);
            }

            // WPF may transiently write null to SelectedItem while an
            // ObservableCollection is reset. Restore the captured selections
            // before releasing the loading guard so filtering is non-mutating.
            foreach (var selection in workflowSelections)
            {
                if (!ReferenceEquals(selection.Step.SelectedWorkflowVersion, selection.Option))
                    selection.Step.SelectedWorkflowVersion = selection.Option;
            }

            if (!ReferenceEquals(_selectedWorkflowVersion, selectedWorkflowVersion))
                SetField(ref _selectedWorkflowVersion, selectedWorkflowVersion, nameof(SelectedWorkflowVersion));
        }
        finally
        {
            _isLoadingEditor = wasLoading;
        }

        OnPropertyChanged(nameof(WorkflowTemplateFilterSummary));
    }

    private void ReindexWorkflowSteps()
    {
        for (var index = 0; index < WorkflowSteps.Count; index++)
            WorkflowSteps[index].SetOrder(index + 1);
    }

    private void AttachWorkflowStep(ExperimentPlanWorkflowStepEditorViewModel item) =>
        item.PropertyChanged += WorkflowStepPropertyChanged;

    private void WorkflowStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ExperimentPlanWorkflowStepEditorViewModel step &&
            !_isLoadingEditor && ReferenceEquals(step, WorkflowSteps.FirstOrDefault()) &&
            !ReferenceEquals(SelectedWorkflowVersion, step.SelectedWorkflowVersion))
        {
            SetField(ref _selectedWorkflowVersion, step.SelectedWorkflowVersion, nameof(SelectedWorkflowVersion));
        }
        OnPropertyChanged(nameof(WorkflowStepsSummary));
        MarkDirty();
    }

    private void AttachEditor(ExperimentBindableObject item) => item.PropertyChanged += EditorPropertyChanged;
    private void EditorPropertyChanged(object? sender, PropertyChangedEventArgs e) => MarkDirty();

    private void MarkDirty()
    {
        if (_isLoadingEditor) return;
        IsDirty = true;
    }

    private void SetEditorField(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetField(ref field, value, name)) return;
        MarkDirty();
    }

    private async Task RunOperationAsync(string runningMessage, Func<Task> operation)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = runningMessage;
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusMessage = "方案操作失败。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanSaveDraft));
        OnPropertyChanged(nameof(CanValidate));
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(CanCreateNextDraft));
        OnPropertyChanged(nameof(CurrentVersion));
        OnPropertyChanged(nameof(ValidationSummary));
        OnPropertyChanged(nameof(WorkflowStepsSummary));
        OnPropertyChanged(nameof(WorkflowTemplateFilterSummary));
        OnPropertyChanged(nameof(CanMoveWorkflowStepUp));
        OnPropertyChanged(nameof(CanMoveWorkflowStepDown));
        OnPropertyChanged(nameof(HasValidationIssues));
        foreach (var command in new[]
                 {
                     _refreshCommand,
                     _saveDraftCommand,
                     _validateCommand,
                     _publishCommand,
                     _nextDraftCommand
                 })
            command.RaiseCanExecuteChanged();
        _newPlanCommand.RaiseCanExecuteChanged();
        _addMaterialCommand.RaiseCanExecuteChanged();
        _removeMaterialCommand.RaiseCanExecuteChanged();
        _addParameterCommand.RaiseCanExecuteChanged();
        _removeParameterCommand.RaiseCanExecuteChanged();
        _addResourceCommand.RaiseCanExecuteChanged();
        _removeResourceCommand.RaiseCanExecuteChanged();
        _addWorkflowStepCommand.RaiseCanExecuteChanged();
        _removeWorkflowStepCommand.RaiseCanExecuteChanged();
        _moveWorkflowStepUpCommand.RaiseCanExecuteChanged();
        _moveWorkflowStepDownCommand.RaiseCanExecuteChanged();
    }

    private static string? NormalizeOptional(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
