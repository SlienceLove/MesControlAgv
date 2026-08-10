using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.Modules;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// 汇总批量导入状态并复用现有解析、排序行为。
/// 提交操作暂由 <see cref="MainViewModel"/> 统一编排。
/// </summary>
public sealed class BatchImportViewModel : INotifyPropertyChanged
{
    private readonly BatchTaskImportParser _parser = new();
    private string _batchStatus = "请选择 CSV 或 XLSX 文件导入任务";

    public ObservableCollection<BatchTaskRowViewModel> BatchTasks { get; } = [];
    public ObservableCollection<string> BatchImportIssues { get; } = [];

    public string BatchStatus
    {
        get => _batchStatus;
        set => SetField(ref _batchStatus, value);
    }

    public void Import(string filePath)
    {
        var result = _parser.Parse(filePath);
        BatchTasks.Clear();
        BatchImportIssues.Clear();
        foreach (var issue in result.Issues) BatchImportIssues.Add($"第 {issue.SourceRowNumber} 行：{issue.Message}");
        foreach (var task in result.Tasks) BatchTasks.Add(new BatchTaskRowViewModel(task));
        BatchStatus = $"已导入 {BatchTasks.Count} 条任务，发现 {BatchImportIssues.Count} 条问题；可编辑优先级后提交";
    }

    public void Clear()
    {
        BatchTasks.Clear();
        BatchImportIssues.Clear();
        BatchStatus = "已清空导入列表";
    }

    public void Sort()
    {
        var sorted = BatchTasks
            .OrderByDescending(task => task.Priority)
            .ThenBy(task => task.PlannedTime ?? DateTime.MaxValue)
            .ThenBy(task => task.SourceRowNumber)
            .ToList();
        BatchTasks.Clear();
        foreach (var task in sorted) BatchTasks.Add(task);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

/// <summary>
/// 为中控各模块视图模型提供稳定的组合边界。
/// </summary>
public sealed class ControlCenterViewModel
{
    public ControlCenterViewModel(
        WorkflowEditorViewModel workflowEditor,
        ControlCenterModuleRegistry? moduleRegistry = null)
    {
        Workflow = workflowEditor ?? throw new ArgumentNullException(nameof(workflowEditor));
        ModuleRegistry = moduleRegistry ?? ControlCenterModuleRegistry.CreateStandard();
    }

    public ControlCenterModuleRegistry ModuleRegistry { get; }
    public IReadOnlyList<ControlCenterModuleDescriptor> EnabledModules => ModuleRegistry.EnabledModules;
    public TaskMonitorViewModel TaskMonitor { get; } = new();
    public AgvCommunicationViewModel AgvCommunication { get; } = new();
    public BatchImportViewModel BatchImport { get; } = new();
    public KpiDashboardViewModel KpiDashboard { get; } = new();
    public WorkflowEditorViewModel Workflow { get; }
}
