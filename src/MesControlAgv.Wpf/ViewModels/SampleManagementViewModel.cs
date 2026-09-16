using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using MesControlAgv.Contracts.Samples;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// P0 sample custody surface: scan/manual registration, template import and
/// read-only traceability.  Device movement is written by MES workers; this
/// view model only displays the normalized records and events.
/// </summary>
public sealed class SampleManagementViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IMesClient _mes;
    private readonly SampleImportParser _parser = new();
    private readonly CancellationTokenSource _shutdown = new();
    private string _sampleId = string.Empty;
    private string _barcode = string.Empty;
    private string _sampleBatchId = string.Empty;
    private string _sourceLocation = string.Empty;
    private string _containerPosition = string.Empty;
    private string _runIdText = string.Empty;
    private string _queryText = string.Empty;
    private string _operatorName = Environment.GetEnvironmentVariable("WORKFLOW_OPERATOR") ?? Environment.UserName;
    private string _sourceFileName = string.Empty;
    private string _statusMessage = "可扫码登记样品，或导入 CSV/XLSX 模板。";
    private bool _isBusy;
    private SampleRecordResponse? _selectedSample;

    public SampleManagementViewModel(IMesClient mes)
    {
        _mes = mes ?? throw new ArgumentNullException(nameof(mes));
        RegisterCommand = new AsyncCommand(RegisterAsync, () => !IsBusy);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        QueryCommand = new AsyncCommand(QueryAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(QueryText));
        BindRunCommand = new AsyncCommand(BindRunAsync, () => !IsBusy && SelectedSample is not null && TryParseRunId(RunIdText, out _));
    }

    public ObservableCollection<SampleRecordResponse> Samples { get; } = [];
    public ObservableCollection<SampleEventResponse> Events { get; } = [];
    public ObservableCollection<string> ImportIssues { get; } = [];
    public OfflineDataStateViewModel OfflineState { get; } = new();

    public string SampleId { get => _sampleId; set => SetField(ref _sampleId, value); }
    public string Barcode { get => _barcode; set => SetField(ref _barcode, value); }
    public string SampleBatchId { get => _sampleBatchId; set => SetField(ref _sampleBatchId, value); }
    public string SourceLocation { get => _sourceLocation; set => SetField(ref _sourceLocation, value); }
    public string ContainerPosition { get => _containerPosition; set => SetField(ref _containerPosition, value); }
    public string RunIdText
    {
        get => _runIdText;
        set
        {
            if (!SetField(ref _runIdText, value)) return;
            BindRunCommand.RaiseCanExecuteChanged();
        }
    }
    public string QueryText
    {
        get => _queryText;
        set
        {
            if (!SetField(ref _queryText, value)) return;
            QueryCommand.RaiseCanExecuteChanged();
        }
    }
    public string OperatorName { get => _operatorName; set => SetField(ref _operatorName, value); }
    public string SourceFileName { get => _sourceFileName; private set => SetField(ref _sourceFileName, value); }
    public string StatusMessage { get => _statusMessage; private set => SetField(ref _statusMessage, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            RegisterCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
            QueryCommand.RaiseCanExecuteChanged();
            BindRunCommand.RaiseCanExecuteChanged();
        }
    }
    public SampleRecordResponse? SelectedSample
    {
        get => _selectedSample;
        set
        {
            if (!SetField(ref _selectedSample, value)) return;
            BindRunCommand.RaiseCanExecuteChanged();
            _ = LoadSelectedEventsAsync(value, _shutdown.Token);
        }
    }

    public AsyncCommand RegisterCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand QueryCommand { get; }
    public AsyncCommand BindRunCommand { get; }

    public async Task ImportFileAsync(string filePath)
    {
        if (IsBusy) return;
        IsBusy = true;
        OfflineState.BeginLoading("正在解析并导入样品文件…");
        try
        {
            var parsed = _parser.Parse(filePath);
            ImportIssues.Clear();
            foreach (var issue in parsed.Issues)
                ImportIssues.Add($"第 {issue.SourceRowNumber} 行：{issue.Message}");
            if (parsed.Rows.Count == 0)
            {
                StatusMessage = $"文件没有可导入行，发现 {ImportIssues.Count} 个问题。";
                OfflineState.MarkReady(false, StatusMessage);
                return;
            }

            SourceFileName = Path.GetFileName(filePath);
            var result = await _mes.ImportSamplesAsync(
                new ImportSamplesRequest
                {
                    SourceFileName = SourceFileName,
                    OperatorName = OperatorName,
                    Rows = parsed.Rows
                },
                _shutdown.Token);

            foreach (var row in result.Rows.Where(row => row.Outcome == "failed" && !string.IsNullOrWhiteSpace(row.Error)))
                ImportIssues.Add($"第 {row.RowNumber} 行：{row.Error}");
            StatusMessage = $"已处理 {result.TotalRows} 行：新增 {result.SucceededRows - result.ExistingRows}，已存在 {result.ExistingRows}，失败 {result.FailedRows}。";
            OfflineState.MarkReady(result.SucceededRows > 0, StatusMessage);
            await RefreshCoreAsync(_shutdown.Token);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or NotSupportedException or IOException or InvalidOperationException or HttpRequestException)
        {
            StatusMessage = $"样品导入失败：{exception.Message}";
            OfflineState.MarkError(StatusMessage, exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RegisterAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        OfflineState.BeginLoading("正在登记样品…");
        try
        {
            var sample = await _mes.RegisterSampleAsync(
                new RegisterSampleRequest
                {
                    SampleId = SampleId,
                    Barcode = Barcode,
                    SampleBatchId = SampleBatchId,
                    SourceLocation = SourceLocation,
                    ContainerPosition = string.IsNullOrWhiteSpace(ContainerPosition) ? null : ContainerPosition,
                    OperatorName = OperatorName
                },
                _shutdown.Token);
            if (!string.IsNullOrWhiteSpace(RunIdText))
            {
                if (!TryParseRunId(RunIdText, out var runId))
                    throw new ArgumentException("RunId 必须是有效 GUID。", nameof(RunIdText));
                sample = await _mes.BindSampleRunAsync(
                    sample.SampleId,
                    new BindSampleRunRequest { RunId = runId, OperatorName = OperatorName },
                    _shutdown.Token);
            }

            StatusMessage = $"样品 {sample.SampleId} 已登记，当前状态 {sample.Status}。";
            OfflineState.MarkReady(true, StatusMessage);
            await RefreshCoreAsync(_shutdown.Token);
            SelectedSample = Samples.FirstOrDefault(item => item.SampleId == sample.SampleId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or HttpRequestException)
        {
            StatusMessage = $"样品登记失败：{exception.Message}";
            OfflineState.MarkError(StatusMessage, exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        OfflineState.BeginLoading("正在刷新样品记录…");
        try
        {
            await RefreshCoreAsync(_shutdown.Token);
            OfflineState.MarkReady(true, $"已加载 {Samples.Count} 条样品记录。");
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            StatusMessage = $"刷新样品失败：{exception.Message}";
            OfflineState.MarkError(StatusMessage, exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var samples = await _mes.GetSamplesAsync(200, cancellationToken);
        Samples.Clear();
        foreach (var sample in samples) Samples.Add(sample);
        if (SelectedSample is { } selected)
            SelectedSample = Samples.FirstOrDefault(item => item.Id == selected.Id);
    }

    private async Task QueryAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(QueryText)) return;
        IsBusy = true;
        try
        {
            var sample = await _mes.GetSampleAsync(QueryText.Trim(), _shutdown.Token);
            if (sample is null)
            {
                StatusMessage = $"未找到样品：{QueryText.Trim()}。";
                Events.Clear();
                return;
            }
            SelectedSample = sample;
            StatusMessage = $"已定位样品 {sample.SampleId}，状态 {sample.Status}。";
            await LoadSelectedEventsAsync(sample, _shutdown.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            StatusMessage = $"查询样品失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BindRunAsync()
    {
        if (IsBusy || SelectedSample is null || !TryParseRunId(RunIdText, out var runId)) return;
        IsBusy = true;
        try
        {
            var sample = await _mes.BindSampleRunAsync(
                SelectedSample.SampleId,
                new BindSampleRunRequest { RunId = runId, OperatorName = OperatorName },
                _shutdown.Token);
            StatusMessage = $"样品 {sample.SampleId} 已绑定 RunId {runId:D}。";
            await RefreshCoreAsync(_shutdown.Token);
            SelectedSample = Samples.FirstOrDefault(item => item.Id == sample.Id);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or ArgumentException)
        {
            StatusMessage = $"绑定 Run 失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadSelectedEventsAsync(SampleRecordResponse? sample, CancellationToken cancellationToken)
    {
        Events.Clear();
        if (sample is null) return;
        try
        {
            var events = await _mes.GetSampleEventsAsync(sample.SampleId, cancellationToken);
            foreach (var item in events) Events.Add(item);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            StatusMessage = $"读取样品履历失败：{exception.Message}";
        }
    }

    private static bool TryParseRunId(string value, out Guid runId) => Guid.TryParse(value?.Trim(), out runId);

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
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
