using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Contracts;

namespace MesControlAgv.Wpf.Views;

public partial class WorkstationTaskWindow
{
    private readonly ObservableCollection<WorkstationSourceTask> _sourceTasks = [];
    private int _nextSourceRow = 1;
    private string _editorBaseline = "";

    private async void LoadTasks_Click(object sender, RoutedEventArgs e) => await LoadSourceTasksAsync(reset: true);
    private async void MoreTasks_Click(object sender, RoutedEventArgs e) => await LoadSourceTasksAsync(reset: false);

    private async Task LoadSourceTasksAsync(bool reset)
    {
        if (_busy) return;
        _busy = true; WorkspaceTabs.IsEnabled = false;
        var selectedNumber = (SourceTasks.SelectedItem as WorkstationSourceTask)?.TaskNo ?? SourceTasks.Text;
        try
        {
            var start = reset ? 1 : _nextSourceRow;
            var page = await _client.GetSampleWorkstationTasksAsync(_deviceId, start, 50, CancellationToken.None);
            var known = reset ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : _sourceTasks.Select(t => t.TaskNo).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var additions = page.Where(t => !string.IsNullOrWhiteSpace(t.TaskNo) && known.Add(t.TaskNo))
                .Select(t => new WorkstationSourceTask(t.TaskNo, t.TaskName)).ToArray();
            if (!reset && page.Count > 0 && additions.Length == 0)
            {
                MoreTasks.Visibility = Visibility.Collapsed;
                TaskListStatus.Text = "设备未返回更多不同任务，已停止分页；可直接输入已知编号。";
                return;
            }
            if (reset) _sourceTasks.Clear();
            foreach (var task in additions) _sourceTasks.Add(task);
            _nextSourceRow = checked(start + page.Count); // A short page is not proof of end-of-list.
            SourceTasks.SelectedItem = _sourceTasks.FirstOrDefault(t => t.TaskNo == selectedNumber);
            if (SourceTasks.SelectedItem is null) SourceTasks.Text = selectedNumber;
            MoreTasks.Visibility = page.Count > 0 && _sourceTasks.Count < 5000 ? Visibility.Visible : Visibility.Collapsed;
            TaskListStatus.Text = _sourceTasks.Count == 0 ? "设备没有返回任务，可填入参考示例或打开本地模板。"
                : $"已加载 {_sourceTasks.Count} 个设备任务，选择后点击“复制为新模板”。" +
                  (page.Count == 0 ? " 已到列表末尾。" : _sourceTasks.Count >= 5000 ? " 已达显示上限，可直接输入编号。" : "");
        }
        catch (Exception ex)
        {
            TaskListStatus.Text = "加载失败，保留现有列表及编辑内容；可使用本地模板或参考示例。" + ex.Message;
        }
        finally { _busy = false; WorkspaceTabs.IsEnabled = true; }
    }

    private string EditorSnapshot() => JsonSerializer.Serialize(new { Number = TaskNo.Text, Name = TaskName.Text, Rows = _rows });

    private bool ConfirmReplaceEditor()
    {
        if (!TransferGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !TransferGrid.CommitEdit(DataGridEditingUnit.Row, true))
            throw new ArgumentException("请先修正当前编辑单元格，再替换模板。");
        return EditorSnapshot() == _editorBaseline || MessageBox.Show(this,
            "将替换当前编辑内容。尚未保存的修改会丢失，是否继续？", "替换模板",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private void FillReference_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!ConfirmReplaceEditor()) return;
        SetRows(new SampleWorkstationTaskTemplate("REFERENCE", "参考分液任务（请修改）", [
            new("示例_溶剂", "示例_枪头模块", 0, 0, "示例_来源模块", 1, 1, "示例_目标模块", 1, 1, 100),
            new("示例_溶剂", "示例_枪头模块", 0, 0, "示例_来源模块", 1, 2, "示例_目标模块", 1, 2, 100)]));
        Status.Text = "已填入参考示例，可导出 Excel。100 µL 和坐标仅供格式参考；导入前须替换所有“示例_”编码并核实实际参数。";
    });

    private static void EnsureNoReferenceCodes(IReadOnlyList<SampleWorkstationTaskTemplate> tasks)
    {
        if (tasks.SelectMany(t => t.Transfers).Any(r => new[] { r.LiquidCode, r.TipModule, r.SourceModule, r.TargetModule }
            .Any(c => c.Trim().StartsWith("示例_", StringComparison.Ordinal))))
            throw new ArgumentException("参考示例不能直接导入，请先将所有“示例_”编码替换为设备实际编码，并核实坐标和体积。");
    }

    private void ClearPreview()
    {
        _preview = []; PreviewGrid.ItemsSource = null; PreviewDetails.ItemsSource = null;
        ImportButton.IsEnabled = false; Summary.Text = "模板已更换，请重新校验后预览。";
    }
}

public sealed record WorkstationSourceTask(string TaskNo, string TaskName)
{
    public string Label => $"{TaskNo} · {TaskName}";
}
