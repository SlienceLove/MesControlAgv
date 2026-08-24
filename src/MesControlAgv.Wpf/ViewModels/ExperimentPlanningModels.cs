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
    public string Workflow => $"{Plan.WorkflowId:N} / v{Plan.WorkflowVersion}";
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
    bool IsAvailable = true)
{
    public string Display => IsAvailable
        ? $"{Name} / v{Version}"
        : $"固定版本不可用 / {WorkflowId:N} / v{Version}";
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
