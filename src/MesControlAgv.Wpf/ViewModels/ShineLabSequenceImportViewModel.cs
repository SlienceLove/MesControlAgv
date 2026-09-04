using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class ShineLabSampleTaskRowViewModel
{
    public ShineLabSampleTaskRowViewModel(int displayIndex, ShineLabSampleTask task)
    {
        DisplayIndex = displayIndex;
        Task = task;
    }

    public int DisplayIndex { get; }
    public ShineLabSampleTask Task { get; }
    public int SourceRowNumber => Task.SourceRowNumber;
    public string SampleName => Task.SampleName;
    public string SampleType => Task.SampleType;
    public string SampleLevel => Task.SampleLevel;
    public string CycleCount => Task.CycleCount;
    public string InjectionVolume => $"{Task.InjectionVolume} {Task.InjectionUnit}";
    public string ChromatographyMethod => Task.ChromatographyMethod;
}

/// <summary>
/// 中控离子色谱样品任务导入：读取本地 CSV/XLSX，生成 ShineLab CSV，
/// 通过共享目录下发到控制电脑，并按回执状态显示结果。
/// 只有回执为 Verified 才显示导入成功；Unknown 一律要求人工确认，绝不自动重试。
/// 本视图模型不触发“运行”。
/// </summary>
public sealed class ShineLabSequenceImportViewModel : INotifyPropertyChanged
{
    private readonly ShineLabSequenceParser _parser = new();
    private readonly Func<IShineLabBatchHandoff?> _handoffFactory;
    private readonly Func<DateTimeOffset> _clock;

    private ShineLabSequenceImportResult? _result;
    private string _sourceFilePath = string.Empty;
    private string _targetSequence = string.Empty;
    private string _operatorName = Environment.UserName;
    private bool _allowAppend;
    private bool _verifyByExport = true;
    private bool _isSubmitting;
    private string _status = "请选择离子色谱样品任务 CSV 或 XLSX 文件";
    private string _batchId = string.Empty;
    private ShineLabBatchReceipt? _receipt;
    private ShineLabSampleTaskRowViewModel? _selectedSampleTask;

    public ShineLabSequenceImportViewModel(
        Func<IShineLabBatchHandoff?>? handoffFactory = null,
        Func<DateTimeOffset>? clock = null)
    {
        _handoffFactory = handoffFactory ?? CreateHandoffFromEnvironment;
        _clock = clock ?? (() => DateTimeOffset.Now);
        SubmitCommand = new AsyncCommand(() => SubmitAsync(), () => CanSubmit);
        ClearCommand = new AsyncCommand(() => { Clear(); return Task.CompletedTask; }, () => !IsSubmitting);
    }

    public ObservableCollection<ShineLabSampleTaskRowViewModel> SampleTasks { get; } = [];
    public ObservableCollection<string> Issues { get; } = [];
    public OfflineDataStateViewModel OfflineState { get; } = new();

    public ICommand SubmitCommand { get; }
    public ICommand ClearCommand { get; }

    public ShineLabSampleTaskRowViewModel? SelectedSampleTask
    {
        get => _selectedSampleTask;
        set => SetField(ref _selectedSampleTask, value);
    }

    public string SourceFilePath
    {
        get => _sourceFilePath;
        private set => SetField(ref _sourceFilePath, value);
    }

    public string TargetSequence
    {
        get => _targetSequence;
        set
        {
            if (SetField(ref _targetSequence, value)) RaiseSubmitState();
        }
    }

    public string OperatorName
    {
        get => _operatorName;
        set => SetField(ref _operatorName, value);
    }

    /// <summary>
    /// ShineLab 导入是追加语义，必须由操作人员显式确认后才允许下发。
    /// </summary>
    public bool AllowAppend
    {
        get => _allowAppend;
        set
        {
            if (SetField(ref _allowAppend, value)) RaiseSubmitState();
        }
    }

    public bool VerifyByExport
    {
        get => _verifyByExport;
        set => SetField(ref _verifyByExport, value);
    }

    public string BatchId
    {
        get => _batchId;
        private set => SetField(ref _batchId, value);
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public bool IsSubmitting
    {
        get => _isSubmitting;
        private set
        {
            if (!SetField(ref _isSubmitting, value)) return;
            RaiseSubmitState();
            (ClearCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        }
    }

    public ShineLabBatchReceipt? Receipt
    {
        get => _receipt;
        private set
        {
            if (!SetField(ref _receipt, value)) return;
            OnPropertyChanged(nameof(ReceiptStatusText));
            OnPropertyChanged(nameof(IsVerified));
            OnPropertyChanged(nameof(RequiresManualCheck));
        }
    }

    public string ReceiptStatusText => Receipt is null
        ? "-"
        : Receipt.Status switch
        {
            ShineLabBatchStatus.Verified => $"已验证：导出比对一致，{Receipt.ObservedRows ?? Receipt.ExpectedRows} 条任务",
            ShineLabBatchStatus.Submitted => "已提交：CSV 已送入 ShineLab，但未完成导出比对，尚不能判定成功",
            ShineLabBatchStatus.Failed => $"失败：{Receipt.Error ?? "未提供原因"}",
            ShineLabBatchStatus.Unknown => $"未知：{Receipt.Error ?? "无法判断结果"}；需人工确认，禁止自动重试",
            ShineLabBatchStatus.Importing => "导入中",
            _ => Receipt.Status.ToString()
        };

    public bool IsVerified => Receipt?.Status == ShineLabBatchStatus.Verified;

    public bool RequiresManualCheck =>
        Receipt?.Status is ShineLabBatchStatus.Unknown or ShineLabBatchStatus.Submitted;

    public bool HasTasks => SampleTasks.Count > 0;

    public bool CanSubmit =>
        !IsSubmitting &&
        _result?.CanGenerateSequence == true &&
        AllowAppend &&
        !string.IsNullOrWhiteSpace(TargetSequence);

    public void Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        OfflineState.BeginLoading("正在解析样品任务文件...");
        SampleTasks.Clear();
        SelectedSampleTask = null;
        Issues.Clear();
        Receipt = null;
        BatchId = string.Empty;

        try
        {
            _result = _parser.Parse(filePath);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            _result = null;
            SourceFilePath = filePath;
            Issues.Add(exception.Message);
            Status = $"文件解析失败：{exception.Message}";
            OfflineState.MarkError("样品任务文件解析失败。", exception.Message);
            RaiseLoadState();
            return;
        }

        SourceFilePath = filePath;
        foreach (var issue in _result.Issues)
        {
            Issues.Add(issue.SourceRowNumber > 0
                ? $"第 {issue.SourceRowNumber} 行：{issue.Message}"
                : issue.Message);
        }

        var displayIndex = 1;
        foreach (var task in _result.Tasks)
        {
            SampleTasks.Add(new ShineLabSampleTaskRowViewModel(displayIndex++, task));
        }
        SelectedSampleTask = SampleTasks.FirstOrDefault();

        Status = _result.CanGenerateSequence
            ? $"已解析 {SampleTasks.Count} 条样品任务；确认追加导入并填写目标序列后可下发到控制电脑"
            : $"已解析 {SampleTasks.Count} 条样品任务，发现 {Issues.Count} 条问题；修正后才能下发";
        OfflineState.MarkReady(
            SampleTasks.Count > 0,
            SampleTasks.Count > 0 ? "样品任务文件已解析。" : "文件有效，但暂无样品任务。");
        RaiseLoadState();
    }

    public void Clear()
    {
        _result = null;
        SampleTasks.Clear();
        SelectedSampleTask = null;
        Issues.Clear();
        Receipt = null;
        BatchId = string.Empty;
        SourceFilePath = string.Empty;
        AllowAppend = false;
        Status = "已清空样品任务列表";
        OfflineState.MarkReady(hasData: false, "已清空样品任务列表。");
        RaiseLoadState();
    }

    public string BuildCsvPreview() => ShineLabCsvWriter.Build(RequireTasks());

    public async Task SubmitAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSubmit) return;

        var handoff = _handoffFactory();
        if (handoff is null)
        {
            Status = "未配置控制电脑收件目录；请设置 SHINELAB_INBOX_PATH 环境变量后重试。";
            return;
        }

        IsSubmitting = true;
        try
        {
            var tasks = RequireTasks();
            var csvContent = ShineLabCsvWriter.Build(tasks);
            var batchId = BuildBatchId();
            var manifest = new ShineLabBatchManifest
            {
                BatchId = batchId,
                CsvSha256 = ShineLabCsvWriter.ComputeSha256(csvContent),
                CsvFileName = $"{batchId}.csv",
                TargetSequence = TargetSequence.Trim(),
                ExpectedRows = tasks.Count,
                CreatedAtUtc = _clock().ToUniversalTime(),
                CreatedBy = string.IsNullOrWhiteSpace(OperatorName) ? Environment.UserName : OperatorName.Trim(),
                AllowAppend = true,
                VerifyByExport = VerifyByExport,
                AllowRun = false
            };

            BatchId = batchId;
            Status = $"批次 {batchId} 已下发，正在等待控制电脑回执...";
            Receipt = await handoff.SubmitAsync(manifest, csvContent, cancellationToken);
            Status = Receipt.Status == ShineLabBatchStatus.Verified
                ? $"批次 {batchId} 导入成功并已通过导出比对。"
                : $"批次 {batchId} 未判定成功：{ReceiptStatusText}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "下发已取消；请人工确认控制电脑上的实际状态。";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Status = $"下发失败：{exception.Message}";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private IReadOnlyList<ShineLabSampleTask> RequireTasks()
    {
        if (_result?.CanGenerateSequence != true)
        {
            throw new InvalidOperationException("当前没有可下发的样品任务，或存在未修正的问题。");
        }

        return _result.Tasks;
    }

    private string BuildBatchId() =>
        $"batch-{_clock().ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";

    private static IShineLabBatchHandoff? CreateHandoffFromEnvironment()
    {
        var inbox = Environment.GetEnvironmentVariable("SHINELAB_INBOX_PATH");
        if (string.IsNullOrWhiteSpace(inbox)) return null;

        var timeout = TimeSpan.FromMinutes(5);
        var configured = Environment.GetEnvironmentVariable("SHINELAB_RECEIPT_TIMEOUT_SECONDS");
        if (int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            timeout = TimeSpan.FromSeconds(seconds);
        }

        return new ShineLabBatchHandoff(new ShineLabHandoffOptions { InboxPath = inbox, ReceiptTimeout = timeout });
    }

    private void RaiseLoadState()
    {
        OnPropertyChanged(nameof(HasTasks));
        RaiseSubmitState();
    }

    private void RaiseSubmitState()
    {
        OnPropertyChanged(nameof(CanSubmit));
        (SubmitCommand as AsyncCommand)?.RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
