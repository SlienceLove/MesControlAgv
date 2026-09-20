using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;
using Microsoft.Win32;

namespace MesControlAgv.Wpf.Views;

public partial class WorkstationTaskWindow : Window
{
    private readonly IMesClient _client;
    private readonly string _deviceId;
    private readonly string? _sourceTaskNo;
    private readonly ObservableCollection<TransferEditorRow> _rows = [];
    private IReadOnlyList<SampleWorkstationTaskTemplate> _preview = [];
    private bool _busy;
    private bool _submitted;
    public WorkstationTaskWindow(IMesClient client, string deviceId, string? sourceTaskNo, bool batch)
    {
        InitializeComponent();
        _client = client; _deviceId = deviceId; _sourceTaskNo = sourceTaskNo;
        TransferGrid.ItemsSource = _rows; _rows.Add(new());
        TaskNo.Text = NewNumber(); TaskName.Text = "新建分液任务"; Actor.Text = Environment.UserName;
        WorkspaceTabs.SelectedIndex = batch ? 1 : 0;
        Closing += (_, e) => { if (_busy) { e.Cancel = true; Status.Text = "正在处理请求，请等待结果后关闭。"; } };
    }
    private static string NewNumber() => DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + RandomNumberGenerator.GetInt32(100000, 1000000);
    private void NewNumber_Click(object s, RoutedEventArgs e) => TaskNo.Text = NewNumber();
    private void AddRow_Click(object s, RoutedEventArgs e) => _rows.Add(new());
    private void RemoveRow_Click(object s, RoutedEventArgs e) { if (TransferGrid.SelectedItem is TransferEditorRow row) _rows.Remove(row); }
    private void Guard(Action action) { try { action(); } catch (Exception ex) { Status.Text = ex.Message; } }
    private SampleWorkstationTaskTemplate Collect()
    {
        if (!TransferGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !TransferGrid.CommitEdit(DataGridEditingUnit.Row, true))
            throw new ArgumentException("请先修正当前编辑单元格。");
        var task = new SampleWorkstationTaskTemplate(TaskNo.Text.Trim(), TaskName.Text.Trim(), _rows.Select(r => r.ToContract()).ToArray());
        WorkstationBatchWorkbook.Validate([task]); return task;
    }
    private void SetRows(SampleWorkstationTaskTemplate template)
    {
        TaskNo.Text = NewNumber(); TaskName.Text = template.TaskName;
        _rows.Clear(); foreach (var r in template.Transfers) _rows.Add(new(r));
        Status.Text = "模板已加载并生成新编号，可编辑明细后保存。";
    }
    private async void ReadSource_Click(object s, RoutedEventArgs e)
    {
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(_sourceTaskNo)) { Status.Text = "请先在设备状态页的最近任务列表选中一个来源任务，再打开此窗口。"; return; }
        _busy = true; WorkspaceTabs.IsEnabled = false;
        try { SetRows((await _client.GetSampleWorkstationTemplateAsync(_deviceId, _sourceTaskNo, CancellationToken.None)).Template); }
        catch (Exception ex) { Status.Text = "读取模板失败：" + ex.Message; }
        finally { _busy = false; WorkspaceTabs.IsEnabled = true; }
    }
    private static byte[] ReadFile(string path)
    {
        if (new FileInfo(path).Length > SampleWorkstationTemplateFile.MaximumFileBytes) throw new ArgumentException("Excel 文件不能超过 4 MiB。");
        return File.ReadAllBytes(path);
    }
    private void OpenTemplate_Click(object s, RoutedEventArgs e) => Guard(() =>
    {
        var dialog = new OpenFileDialog { Filter = "Excel 模板 (*.xlsx)|*.xlsx" };
        if (dialog.ShowDialog(this) != true) return;
        var tasks = WorkstationBatchWorkbook.Read(ReadFile(dialog.FileName));
        if (tasks.Count != 1) throw new ArgumentException("单任务模板只能包含一个任务；多个任务请使用批量导入页。");
        SetRows(tasks[0]);
    });
    private void SaveTemplate_Click(object s, RoutedEventArgs e) => Guard(() =>
    {
        var task = Collect(); var dialog = new SaveFileDialog { Filter = "Excel 模板 (*.xlsx)|*.xlsx", FileName = "开盖分液任务样表.xlsx" };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllBytes(dialog.FileName, WorkstationBatchWorkbook.Write([task]));
        Status.Text = "已保存模板。可复制行填写多个任务：相同编号的行归为同一任务。编号请保持文本格式。";
    });
    private void PreviewSingle_Click(object s, RoutedEventArgs e) => Guard(() => SetPreview([Collect()]));
    private void SetPreview(IReadOnlyList<SampleWorkstationTaskTemplate> tasks)
    {
        if (_submitted) throw new InvalidOperationException("本窗口已提交过导入，请先核对结果；新批次请重新打开窗口。");
        WorkstationBatchWorkbook.Validate(tasks); _preview = tasks;
        PreviewGrid.ItemsSource = tasks; PreviewGrid.SelectedIndex = 0;
        Summary.Text = $"校验通过：{tasks.Count} 个任务，{tasks.Sum(t => t.Transfers.Count)} 条分液明细";
        ImportButton.IsEnabled = true; WorkspaceTabs.SelectedIndex = 1;
        Status.Text = "请复核任务编号、来源、目标及体积，再确认导入。";
    }
    private void ChooseExcel_Click(object s, RoutedEventArgs e) => Guard(() =>
    {
        var dialog = new OpenFileDialog { Filter = "Excel 批量任务 (*.xlsx)|*.xlsx" };
        if (dialog.ShowDialog(this) != true) return;
        // Invalidate the previous preview before attempting a new file.
        _preview = []; PreviewGrid.ItemsSource = null; PreviewDetails.ItemsSource = null; ImportButton.IsEnabled = false;
        Summary.Text = "正在校验所选文件...";
        SetPreview(WorkstationBatchWorkbook.Read(ReadFile(dialog.FileName)));
    });
    private void PreviewSelectionChanged(object s, SelectionChangedEventArgs e) =>
        PreviewDetails.ItemsSource = (PreviewGrid.SelectedItem as SampleWorkstationTaskTemplate)?.Transfers;
    private async void Import_Click(object s, RoutedEventArgs e)
    {
        if (_busy || _submitted || _preview.Count == 0) return;
        if (string.IsNullOrWhiteSpace(Actor.Text)) { Status.Text = "请填写操作人。"; return; }
        if (MessageBox.Show(this, $"将向 {_deviceId} 导入 {_preview.Count} 个任务，共 {_preview.Sum(t => t.Transfers.Count)} 条分液明细。\n不会初始化或启动设备。是否确认？",
                "确认任务导入", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var request = new WorkstationBatchRequest(Guid.NewGuid(), Actor.Text.Trim(), _preview);
        _busy = true; _submitted = true; WorkspaceTabs.IsEnabled = false; ImportButton.IsEnabled = false; ChooseExcel.IsEnabled = false;
        var receipt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MesControlAgv", "workstation-batches", request.RequestId.ToString("N") + ".json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
            File.WriteAllText(receipt, JsonSerializer.Serialize(new { DeviceId = _deviceId, Request = request }));
            ImportResult.Text = $"请求编号：{request.RequestId}\n正在导入，请勿重复提交。";
            var result = await _client.ImportWorkstationBatchAsync(_deviceId, request, CancellationToken.None);
            if (result.RequestId != request.RequestId || result.DeviceId != _deviceId ||
                !result.Items.Select(i => i.TaskNo).SequenceEqual(_preview.Select(t => t.TaskNo)))
                throw new InvalidOperationException("返回批次身份不匹配，请人工核对。");
            File.WriteAllText(receipt, JsonSerializer.Serialize(new { DeviceId = _deviceId, Request = request, Result = result }));
            static string Label(string v) => v switch { "Imported" => "已验证导入", "Unknown" => "结果不确定", "Importing" => "待人工核对", "Pending" => "未执行", "NotImported" => "未导入", _ => v };
            ImportResult.Text = $"请求编号：{request.RequestId}\n" + string.Join("\n", result.Items.Select(i => $"{i.TaskNo}：{Label(i.Status)} — {i.Message}"));
            Status.Text = $"导入结果已保存：{receipt}。本次未启动设备。";
        }
        catch (Exception ex)
        {
            ImportResult.Text = $"请求编号：{request.RequestId}\n未获得完整成功确认，不会自动重试。\n{ex.Message}";
            Status.Text = "请保留请求编号核对结果；不要用新编号重复下发同一批次。";
        }
        finally { _busy = false; WorkspaceTabs.IsEnabled = true; }
    }
    private void ExportResults_Click(object s, RoutedEventArgs e) => Guard(() =>
    {
        if (string.IsNullOrWhiteSpace(ImportResult.Text)) throw new ArgumentException("暂无导入结果。");
        var dialog = new SaveFileDialog { Filter = "文本结果 (*.txt)|*.txt", FileName = "任务导入结果.txt" };
        if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, ImportResult.Text);
    });
}

public sealed class TransferEditorRow
{
    public string LiquidCode { get; set; } = "";
    public string TipModule { get; set; } = "";
    public string TipX { get; set; } = "0";
    public string TipY { get; set; } = "0";
    public string SourceModule { get; set; } = "";
    public string SourceX { get; set; } = "1";
    public string SourceY { get; set; } = "1";
    public string TargetModule { get; set; } = "";
    public string TargetX { get; set; } = "1";
    public string TargetY { get; set; } = "1";
    public string Volume { get; set; } = "";
    public TransferEditorRow() { }
    public TransferEditorRow(SampleWorkstationTransferRow r)
    {
        LiquidCode = r.LiquidCode; TipModule = r.TipModule; TipX = r.TipX.ToString(); TipY = r.TipY.ToString();
        SourceModule = r.SourceModule; SourceX = r.SourceX.ToString(); SourceY = r.SourceY.ToString();
        TargetModule = r.TargetModule; TargetX = r.TargetX.ToString(); TargetY = r.TargetY.ToString(); Volume = r.VolumeMicroliters.ToString();
    }
    public SampleWorkstationTransferRow ToContract()
    {
        int Int(string value, string name) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException($"{name}须为整数。");
        return new(LiquidCode.Trim(), TipModule.Trim(), Int(TipX, "枪头X"), Int(TipY, "枪头Y"), SourceModule.Trim(), Int(SourceX, "来源X"), Int(SourceY, "来源Y"), TargetModule.Trim(), Int(TargetX, "目标X"), Int(TargetY, "目标Y"), Int(Volume, "体积"));
    }
}
