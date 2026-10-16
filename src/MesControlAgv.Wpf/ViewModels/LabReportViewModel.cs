using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using MesControlAgv.Reporting;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class LabReportViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IMesClient _mes;
    private CancellationTokenSource? _operation;
    private bool _busy, _disposed;
    private string _status = "先读取 MES 或导入 Excel；导入预览确认后才进入报表。", _batch = "";
    private DateTime? _from = DateTime.Today, _to = DateTime.Today;
    private ReportSnapshot _snapshot;
    private ImportPreview? _preview;
    private AiSummary? _summary;
    public event PropertyChangedEventHandler? PropertyChanged;

    public LabReportViewModel(IMesClient mes)
    {
        _mes = mes; _snapshot = ReportRules.Empty(CurrentQuery());
        RefreshCommand = new AsyncCommand(RefreshAsync, () => IsIdle);
        AcceptImportCommand = new AsyncCommand(() => { AcceptImport(); return Task.CompletedTask; }, () => IsIdle && Preview?.CanAccept == true);
        GenerateCommand = new AsyncCommand(GenerateAsync, () => IsIdle && HasData && FilterValid);
        ClearImportCommand = new AsyncCommand(() => { ReplaceSnapshot(_snapshot with { Measurements = [] }); _preview = null; Changed(); return Task.CompletedTask; }, () => IsIdle);
    }

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand AcceptImportCommand { get; }
    public AsyncCommand GenerateCommand { get; }
    public AsyncCommand ClearImportCommand { get; }
    public bool IsIdle => !_busy && !_disposed;
    public bool HasData => _snapshot.Jobs.Length + _snapshot.Measurements.Length > 0;
    public bool FilterValid { get { try { _ = CurrentQuery(); return true; } catch (ArgumentException) { return false; } } }
    public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }
    public DateTime? From { get => _from; set { if (_from != value) { _from = value; InvalidateFilter(); } } }
    public DateTime? To { get => _to; set { if (_to != value) { _to = value; InvalidateFilter(); } } }
    public string Batch { get => _batch; set { if (_batch != value) { _batch = value; InvalidateFilter(); } } }
    public ImportPreview? Preview => _preview;
    public ReportJob[] Jobs => _snapshot.Jobs;
    public Measurement[] Measurements => _snapshot.Measurements;
    public KeyValuePair<string, string>[] Statistics => ReportFiles.Describe(_snapshot).ToArray();
    public string SummaryText => _summary is null ? "尚未生成 AI 摘要。" : _summary.Message + "\n\n" + _summary.Text +
        "\n\n模型：" + _summary.Model + "\n依据：" + string.Join(", ", _summary.References);
    public ReportSnapshot Snapshot => _snapshot;

    private ReportQuery CurrentQuery()
    {
        if (_from is null || _to is null) throw new ArgumentException("请选择起止日期。");
        var query = new ReportQuery(new DateTimeOffset(_from.Value.Date), new DateTimeOffset(_to.Value.Date.AddDays(1)),
            string.IsNullOrWhiteSpace(_batch) ? null : _batch.Trim());
        ReportRules.ValidateQuery(query); return query;
    }

    private void InvalidateFilter()
    {
        Cancel();
        try { ReplaceSnapshot(ReportRules.Empty(CurrentQuery()) with { Measurements = _snapshot.Measurements }); }
        catch (ArgumentException)
        {
            ReplaceSnapshot(_snapshot with { Jobs = [], Warnings = ["当前日期筛选无效，已清除旧 MES 任务；请修正筛选。"] });
        }
        Status = "筛选已改变，请重新读取 MES；已确认导入记录不按任务日期自动裁剪。";
        Changed();
    }

    private void ReplaceSnapshot(ReportSnapshot snapshot)
    { _snapshot = snapshot with { Id = Guid.NewGuid().ToString("N") }; _summary = null; Changed(); }

    public Task RefreshAsync() => RunAsync(async token =>
    {
        var query = CurrentQuery();
        var result = await _mes.GetLabReportAsync(query, token);
        token.ThrowIfCancellationRequested();
        ReplaceSnapshot(result with { Measurements = _snapshot.Measurements });
        Status = $"已读取 {Jobs.Length} 条 MES 实验任务；快照 {result.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}。";
    });

    public Task PreviewFileAsync(string path) => RunAsync(async token =>
    {
        _preview = null; Changed();
        var preview = await Task.Run(() => MeasurementFiles.Read(path), token);
        token.ThrowIfCancellationRequested(); _preview = preview; Changed();
        Status = preview.CanAccept ? $"预览 {preview.Records.Length} 条，通过校验；点击“确认导入”替换本报表测量记录。" : "预览存在问题，请修改原文件后重试；原报表不变。";
    });

    public void AcceptImport()
    {
        if (!IsIdle || _preview?.CanAccept != true) return;
        ReplaceSnapshot(_snapshot with { Measurements = _preview.Records.ToArray() });
        _preview = null; Status = "已导入报表快照，未写入 MES 或修改原文件。"; Changed();
    }

    public Task GenerateAsync() => RunAsync(async token =>
    {
        _ = CurrentQuery();
        if (!HasData) throw new InvalidOperationException("没有数据可供生成摘要。");
        _summary = null; Changed();
        var snapshot = _snapshot;
        var result = await _mes.SummarizeLabReportAsync(snapshot, token);
        token.ThrowIfCancellationRequested();
        if (_snapshot.Id != snapshot.Id) return;
        _summary = result; Status = result.Message; Changed();
    });

    public Task ExportAsync(string path) => RunAsync(async token =>
    {
        _ = CurrentQuery();
        if (!HasData) throw new InvalidOperationException("没有数据，请先读取 MES 或确认导入。");
        var snapshot = _snapshot; var summary = _summary;
        await Task.Run(() =>
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".xlsx": ReportFiles.WriteExcel(path, snapshot, summary); break;
                case ".csv": ReportFiles.WriteCsv(path, snapshot); break;
                case ".html": ReportFiles.WriteHtml(path, snapshot, summary); break;
                default: throw new InvalidDataException("导出格式不支持。");
            }
        }, token);
        Status = "已导出：" + path + (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? "（CSV 仅测量明细；完整报告请用 Excel）" : "");
    });

    public Task ExportTemplateAsync(string path) => RunAsync(async token =>
    { await Task.Run(() => MeasurementFiles.WriteTemplate(path), token); Status = "Excel 模板已保存：" + path; });

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (!IsIdle) return;
        _busy = true; _operation = new(); Changed(); Status = "正在处理，可取消；不会操作设备。";
        try { await action(_operation.Token); }
        catch (OperationCanceledException) { Status = "已取消，原有统计可继续查看和导出。"; }
        catch (Exception ex) { Status = "处理失败：" + ex.Message + "；不会修改 MES 原始数据。"; }
        finally { _operation.Dispose(); _operation = null; _busy = false; Changed(); }
    }

    public void Cancel() => _operation?.Cancel();
    public void Dispose() { _disposed = true; Cancel(); }
    private void Changed()
    {
        OnPropertyChanged(string.Empty);
        RefreshCommand?.RaiseCanExecuteChanged(); AcceptImportCommand?.RaiseCanExecuteChanged();
        GenerateCommand?.RaiseCanExecuteChanged(); ClearImportCommand?.RaiseCanExecuteChanged();
    }
    private void OnPropertyChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
