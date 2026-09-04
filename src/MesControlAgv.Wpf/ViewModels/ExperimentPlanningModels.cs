using System.ComponentModel;
using System.Runtime.CompilerServices;
using MesControlAgv.Contracts.Experiments;

namespace MesControlAgv.Wpf.ViewModels;

public abstract class ExperimentBindableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ExperimentPlanListItemViewModel(ExperimentPlan plan)
{
    public ExperimentPlan Plan { get; } = plan;
    public Guid PlanId => Plan.PlanId;
    public int Version => Plan.Version;
    public string Name => Plan.Name;
    public string Status => ExperimentUiText.PlanStatus(Plan.Status);
    public string Workflow => Plan.WorkflowSteps.Count > 1
        ? $"{Plan.WorkflowSteps.Count} 个固定流程"
        : $"{Plan.WorkflowId:N} / v{Plan.WorkflowVersion}";
    public int WorkflowStepCount => Plan.WorkflowSteps.Count;
    public string Completeness => Plan.Validation is null
        ? "尚未校验"
        : Plan.Validation.IsValid
            ? "完整"
            : $"{Plan.Validation.Issues.Count(issue => issue.Severity == ExperimentPlanValidationSeverity.Error)} 项错误";
    public string UpdatedAt => Plan.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed class ExperimentPlanVersionItemViewModel(ExperimentPlan plan)
{
    public ExperimentPlan Plan { get; } = plan;
    public int Version => Plan.Version;
    public string VersionText => $"v{Plan.Version}";
    public string Status => ExperimentUiText.PlanStatus(Plan.Status);
    public string Validation => Plan.Validation is null
        ? "未校验"
        : Plan.Validation.IsValid
            ? "通过"
            : $"{Plan.Validation.Issues.Count} 项问题";
    public string UpdatedAt => Plan.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed record PublishedWorkflowVersionOption(
    Guid WorkflowId,
    int Version,
    string Name,
    bool IsAvailable = true,
    bool IsPreset = false)
{
    public string TemplateKind => IsPreset ? "已验证模板" : "已发布流程";
    public string Display => IsAvailable
        ? $"{Name} / v{Version} · {TemplateKind}"
        : $"固定版本不可用 / {WorkflowId:N} / v{Version}";
}

/// <summary>
/// Editable plan-level reference to one immutable workflow template. The
/// definition remains owned by the workflow editor; this object only stores
/// ordering, a display label, duration estimate and optional overrides.
/// </summary>
public sealed class ExperimentPlanWorkflowStepEditorViewModel : ExperimentBindableObject
{
    private readonly Guid _stepId;
    private int _order;
    private PublishedWorkflowVersionOption? _selectedWorkflowVersion;
    private string _name = string.Empty;
    private int _estimatedDurationMinutes;
    private IReadOnlyDictionary<string, string?> _parameters =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    private string _parameterOverridesText = string.Empty;
    private string _parameterOverridesError = string.Empty;

    public ExperimentPlanWorkflowStepEditorViewModel()
        : this(Guid.NewGuid())
    {
    }

    private ExperimentPlanWorkflowStepEditorViewModel(Guid stepId)
    {
        _stepId = stepId == Guid.Empty ? Guid.NewGuid() : stepId;
    }

    public Guid StepId => _stepId;

    public int Order
    {
        get => _order;
        private set => SetField(ref _order, value);
    }

    public PublishedWorkflowVersionOption? SelectedWorkflowVersion
    {
        get => _selectedWorkflowVersion;
        set
        {
            if (!SetField(ref _selectedWorkflowVersion, value)) return;
            if (value is not null && string.IsNullOrWhiteSpace(Name))
                Name = value.Name;
            OnPropertyChanged(nameof(WorkflowDisplay));
            OnPropertyChanged(nameof(NameOrWorkflowDisplay));
            OnPropertyChanged(nameof(IsAvailable));
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetField(ref _name, value ?? string.Empty))
                OnPropertyChanged(nameof(NameOrWorkflowDisplay));
        }
    }

    public int EstimatedDurationMinutes
    {
        get => _estimatedDurationMinutes;
        set
        {
            if (!SetField(ref _estimatedDurationMinutes, Math.Max(0, value))) return;
            OnPropertyChanged(nameof(EstimatedDurationDisplay));
        }
    }

    public string WorkflowDisplay => SelectedWorkflowVersion?.Display ?? "请选择已发布流程模板";
    public string NameOrWorkflowDisplay => string.IsNullOrWhiteSpace(Name) ? WorkflowDisplay : Name;
    public bool IsAvailable => SelectedWorkflowVersion?.IsAvailable == true;
    public string EstimatedDurationDisplay => EstimatedDurationMinutes > 0
        ? $"预计 {EstimatedDurationMinutes} 分钟"
        : "预计时长待补充";
    public IReadOnlyDictionary<string, string?> Parameters
    {
        get => _parameters;
        private set
        {
            _parameters = new Dictionary<string, string?>(
                value ?? new Dictionary<string, string?>(),
                StringComparer.OrdinalIgnoreCase);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ParametersSummary));
        }
    }
    public string ParametersSummary => Parameters.Count == 0
        ? "无步骤参数覆盖"
        : $"{Parameters.Count} 个步骤参数覆盖";
    public string ParameterOverridesText
    {
        get => _parameterOverridesText;
        set
        {
            value ??= string.Empty;
            if (!SetField(ref _parameterOverridesText, value)) return;
            ParseParameterOverrides(value);
        }
    }
    public string ParameterOverridesError
    {
        get => _parameterOverridesError;
        private set
        {
            if (!SetField(ref _parameterOverridesError, value)) return;
            OnPropertyChanged(nameof(HasParameterOverridesError));
        }
    }
    public bool HasParameterOverridesError => !string.IsNullOrWhiteSpace(ParameterOverridesError);

    public static ExperimentPlanWorkflowStepEditorViewModel From(
        ExperimentPlanWorkflowStep step,
        PublishedWorkflowVersionOption? option) => new(step.StepId)
    {
        Order = Math.Max(1, step.Order),
        SelectedWorkflowVersion = option,
        Name = step.Name,
        EstimatedDurationMinutes = step.EstimatedDurationMinutes,
        Parameters = step.Parameters,
        ParameterOverridesText = FormatParameterOverrides(step.Parameters)
    };

    internal void SetOrder(int order) => Order = Math.Max(1, order);

    internal ExperimentPlanWorkflowStep ToContract(int order) => new()
    {
        StepId = StepId,
        Order = Math.Max(1, order),
        WorkflowId = SelectedWorkflowVersion?.WorkflowId ?? Guid.Empty,
        WorkflowVersion = SelectedWorkflowVersion?.Version ?? 0,
        Name = Name.Trim(),
        EstimatedDurationMinutes = Math.Max(0, EstimatedDurationMinutes),
        Parameters = HasParameterOverridesError
            ? throw new InvalidOperationException($"步骤参数覆盖格式错误：{ParameterOverridesError}")
            : new Dictionary<string, string?>(Parameters, StringComparer.OrdinalIgnoreCase)
    };

    private void ParseParameterOverrides(string value)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var rawItem in value.Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var item = rawItem.Trim();
            var separator = item.IndexOf('=');
            if (separator < 0)
            {
                errors.Add($"“{item}”缺少 =");
                continue;
            }

            var name = item[..separator].Trim();
            if (name.Length == 0)
            {
                errors.Add("参数名不能为空");
                continue;
            }

            if (!parameters.TryAdd(name, item[(separator + 1)..].Trim()))
                errors.Add($"参数“{name}”重复");
        }

        _parameters = parameters;
        ParameterOverridesError = string.Join("；", errors);
        OnPropertyChanged(nameof(Parameters));
        OnPropertyChanged(nameof(ParametersSummary));
    }

    private static string FormatParameterOverrides(
        IReadOnlyDictionary<string, string?>? parameters) =>
        string.Join("; ", (parameters ?? new Dictionary<string, string?>())
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{item.Key}={item.Value ?? string.Empty}"));
}

public sealed class ExperimentPlanValidationIssueItemViewModel(ExperimentPlanValidationIssue issue)
{
    public ExperimentPlanValidationIssue Issue { get; } = issue;
    public string Severity => Issue.Severity == ExperimentPlanValidationSeverity.Error ? "错误" : "警告";
    public string Code => Issue.Code;
    public string Message => Issue.Message;
    public string Field => Issue.Field ?? "-";
    public string Resource => Issue.Resource is null
        ? "-"
        : $"{Issue.Resource.ResourceType}/{Issue.Resource.ResourceId}";
}

public sealed class ExperimentMaterialEditorViewModel : ExperimentBindableObject
{
    private string _materialId = string.Empty;
    private string _name = string.Empty;
    private decimal? _quantity;
    private string _unit = string.Empty;
    private string _specification = string.Empty;

    public string MaterialId { get => _materialId; set => SetField(ref _materialId, value ?? string.Empty); }
    public string Name { get => _name; set => SetField(ref _name, value ?? string.Empty); }
    public decimal? Quantity { get => _quantity; set => SetField(ref _quantity, value); }
    public string Unit { get => _unit; set => SetField(ref _unit, value ?? string.Empty); }
    public string Specification { get => _specification; set => SetField(ref _specification, value ?? string.Empty); }

    public static ExperimentMaterialEditorViewModel From(ExperimentMaterialRequirement material) => new()
    {
        MaterialId = material.MaterialId,
        Name = material.Name,
        Quantity = material.Quantity,
        Unit = material.Unit ?? string.Empty,
        Specification = material.Specification ?? string.Empty
    };

    public ExperimentMaterialRequirement ToContract() => new()
    {
        MaterialId = MaterialId,
        Name = Name,
        Quantity = Quantity,
        Unit = NormalizeOptional(Unit),
        Specification = NormalizeOptional(Specification)
    };

    private static string? NormalizeOptional(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ExperimentParameterEditorViewModel : ExperimentBindableObject
{
    private string _name = string.Empty;
    private string _value = string.Empty;

    public string Name { get => _name; set => SetField(ref _name, value ?? string.Empty); }
    public string Value { get => _value; set => SetField(ref _value, value ?? string.Empty); }

    public static ExperimentParameterEditorViewModel From(KeyValuePair<string, string?> parameter) => new()
    {
        Name = parameter.Key,
        Value = parameter.Value ?? string.Empty
    };
}

public sealed class ExperimentResourceRequirementEditorViewModel : ExperimentBindableObject
{
    private string _resourceType = ExperimentResourceTypeIds.Instrument;
    private string _resourceId = string.Empty;
    private string _capabilityId = string.Empty;
    private int _quantity = 1;
    private bool _exclusive = true;

    public string ResourceType { get => _resourceType; set => SetField(ref _resourceType, value ?? string.Empty); }
    public string ResourceId { get => _resourceId; set => SetField(ref _resourceId, value ?? string.Empty); }
    public string CapabilityId { get => _capabilityId; set => SetField(ref _capabilityId, value ?? string.Empty); }
    public int Quantity { get => _quantity; set => SetField(ref _quantity, value); }
    public bool Exclusive { get => _exclusive; set => SetField(ref _exclusive, value); }

    public static ExperimentResourceRequirementEditorViewModel From(ExperimentResourceRequirement requirement) => new()
    {
        ResourceType = requirement.ResourceType,
        ResourceId = requirement.ResourceId ?? string.Empty,
        CapabilityId = requirement.CapabilityId ?? string.Empty,
        Quantity = requirement.Quantity,
        Exclusive = requirement.Exclusive
    };

    public ExperimentResourceRequirement ToContract() => new()
    {
        ResourceType = ResourceType,
        ResourceId = NormalizeOptional(ResourceId),
        CapabilityId = NormalizeOptional(CapabilityId),
        Quantity = Quantity,
        Exclusive = Exclusive
    };

    private static string? NormalizeOptional(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class ExperimentUiText
{
    public static string PlanStatus(ExperimentPlanStatus status) => status switch
    {
        ExperimentPlanStatus.Draft => "草稿",
        ExperimentPlanStatus.Validated => "已校验",
        ExperimentPlanStatus.Published => "已发布",
        ExperimentPlanStatus.Archived => "已归档",
        _ => "未知"
    };

    public static string JobStatus(ExperimentJobStatus status) => status switch
    {
        ExperimentJobStatus.Draft => "草稿",
        ExperimentJobStatus.Ready => "待排程",
        ExperimentJobStatus.Scheduled => "已排程",
        ExperimentJobStatus.Admitted => "已准入",
        ExperimentJobStatus.Running => "运行中",
        ExperimentJobStatus.Blocked => "阻塞",
        ExperimentJobStatus.Cancelled => "已取消",
        ExperimentJobStatus.Completed => "已完成",
        ExperimentJobStatus.Failed => "失败",
        _ => "未知"
    };

    public static string ScheduleStatus(ScheduleEntryStatus status) => status switch
    {
        ScheduleEntryStatus.Draft => "未排程",
        ScheduleEntryStatus.Scheduled => "已排程",
        ScheduleEntryStatus.Blocked => "阻塞",
        ScheduleEntryStatus.Admitted => "已准入",
        ScheduleEntryStatus.Cancelled => "已取消",
        ScheduleEntryStatus.Completed => "已完成",
        _ => "未知"
    };

    public static string ActivityStatus(string status)
    {
        var normalized = status?.Trim() ?? string.Empty;
        var display = normalized.ToLowerInvariant() switch
        {
            "prepared" => "已准备",
            "accepted" => "已接收",
            "running" => "运行中",
            "succeeded" or "completed" => "已完成",
            "failed" => "失败",
            "timedout" or "timeout" => "超时",
            "cancelled" or "canceled" => "已取消",
            "unknown" => "未知",
            _ => string.IsNullOrWhiteSpace(normalized) ? "未知" : normalized
        };
        if (string.IsNullOrWhiteSpace(normalized)) return display;
        return string.Equals(display, normalized, StringComparison.OrdinalIgnoreCase)
            ? display
            : $"{display} / {normalized}";
    }

    public static string ResourceType(string resourceType) => resourceType switch
    {
        ExperimentResourceTypeIds.Agv => "AGV",
        ExperimentResourceTypeIds.RouteSegment => "路段",
        ExperimentResourceTypeIds.Station => "站点",
        ExperimentResourceTypeIds.RobotArm => "机械臂",
        ExperimentResourceTypeIds.Instrument => "仪器",
        ExperimentResourceTypeIds.Workstation => "工作站",
        ExperimentResourceTypeIds.Carrier => "载具",
        ExperimentResourceTypeIds.OperatorStation => "操作席位",
        _ => resourceType
    };
}
