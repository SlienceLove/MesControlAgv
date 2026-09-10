using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Contracts;
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

public sealed class ShineLabDirectSampleTaskRowViewModel
{
    public ShineLabDirectSampleTaskRowViewModel(int displayIndex, ShineLabDirectSampleTask task)
    {
        DisplayIndex = displayIndex;
        Task = task;
    }

    public int DisplayIndex { get; }
    public ShineLabDirectSampleTask Task { get; }
    public string SampleId => Task.SampleId;
    public string SampleName => Task.SampleName;
    public string SampleType => $"{Task.SampleType} ({Task.SampleTypeCode})";
    public string Position => Task.Position.ToString(CultureInfo.InvariantCulture);
    public string Channel => Task.Channel;
    public string Methods => $"{Task.InstrumentMethod} / {Task.ProcessingMethod}";
    public string InjectionVolume => $"{Task.InjectionVolume.ToString(CultureInfo.InvariantCulture)} {Task.InjectionVolumeUnit}";
}

/// <summary>
/// 中控离子色谱直连任务导入：读取本地 CSV/XLSX、校验下游协议字段、
/// 生成只读报文预览，并允许对选中的单条样品执行 Config 预检。
/// 共享目录成员仅为旧调用兼容保留；当前界面不暴露该路径，也不发送 Command。
/// </summary>
public sealed class ShineLabSequenceImportViewModel : INotifyPropertyChanged
{
    private const string DirectEquipmentCode = "STN61_01";

    private readonly ShineLabSequenceParser _parser = new();
    private readonly ShineLabDirectSequenceParser _directParser = new();
    private readonly Func<IShineLabBatchHandoff?> _handoffFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IMesClient? _mes;

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
    private ShineLabDirectSequenceResult? _directResult;
    private string _directProtocolPreview = string.Empty;
    private string _directPreviewStatus = "直连协议预览尚未生成";
    private ShineLabDirectSampleTaskRowViewModel? _selectedDirectTask;
    private bool _allowSingleConfigPreflight;
    private bool _isDirectPreflighting;
    private string _directPreflightStatus = "请选择一条样品，并确认仪器空闲后执行单条 Config 预检。";
    private ShineLabTaskResponse? _directPreflightTask;
    private string? _directPreflightTaskUuid;

    public ShineLabSequenceImportViewModel(
        Func<IShineLabBatchHandoff?>? handoffFactory = null,
        Func<DateTimeOffset>? clock = null,
        IMesClient? mes = null)
    {
        _handoffFactory = handoffFactory ?? CreateHandoffFromEnvironment;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _mes = mes;
        SubmitCommand = new AsyncCommand(() => SubmitAsync(), () => CanSubmit);
        ClearCommand = new AsyncCommand(() => { Clear(); return Task.CompletedTask; }, () => !IsSubmitting);
        SendSingleConfigPreflightCommand = new AsyncCommand(
            SendSingleConfigPreflightAsync,
            () => CanSendSingleConfigPreflight);
    }

    public ObservableCollection<ShineLabSampleTaskRowViewModel> SampleTasks { get; } = [];
    public ObservableCollection<ShineLabDirectSampleTaskRowViewModel> DirectTasks { get; } = [];
    public ObservableCollection<string> Issues { get; } = [];
    public ObservableCollection<string> DirectIssues { get; } = [];
    public OfflineDataStateViewModel OfflineState { get; } = new();

    public ICommand SubmitCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand SendSingleConfigPreflightCommand { get; }

    public string DirectProtocolPreview
    {
        get => _directProtocolPreview;
        private set => SetField(ref _directProtocolPreview, value);
    }

    public string DirectPreviewStatus
    {
        get => _directPreviewStatus;
        private set => SetField(ref _directPreviewStatus, value);
    }

    public bool HasDirectTasks => DirectTasks.Count > 0;

    public ShineLabDirectSampleTaskRowViewModel? SelectedDirectTask
    {
        get => _selectedDirectTask;
        set
        {
            if (!SetField(ref _selectedDirectTask, value)) return;
            _directPreflightTaskUuid = null;
            DirectPreflightTask = null;
            AllowSingleConfigPreflight = false;
            DirectPreflightStatus = value is null
                ? "请选择一条样品执行单条 Config 预检。"
                : $"已选择 {value.SampleId}；预检只发送 Config，不发送 Command。";
            if (_directResult?.CanBuildPreview == true && value is not null)
                DirectProtocolPreview = BuildDirectProtocolPreview();
            RaiseDirectPreflightState();
        }
    }

    public bool AllowSingleConfigPreflight
    {
        get => _allowSingleConfigPreflight;
        set
        {
            if (SetField(ref _allowSingleConfigPreflight, value)) RaiseDirectPreflightState();
        }
    }

    public bool IsDirectPreflighting
    {
        get => _isDirectPreflighting;
        private set
        {
            if (SetField(ref _isDirectPreflighting, value)) RaiseDirectPreflightState();
        }
    }

    public string DirectPreflightStatus
    {
        get => _directPreflightStatus;
        private set => SetField(ref _directPreflightStatus, value);
    }

    public ShineLabTaskResponse? DirectPreflightTask
    {
        get => _directPreflightTask;
        private set => SetField(ref _directPreflightTask, value);
    }

    public bool CanSendSingleConfigPreflight =>
        _mes is not null &&
        !IsDirectPreflighting &&
        _directResult?.CanBuildPreview == true &&
        SelectedDirectTask is not null &&
        AllowSingleConfigPreflight;

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
        DirectTasks.Clear();
        SelectedDirectTask = null;
        SelectedSampleTask = null;
        Issues.Clear();
        DirectIssues.Clear();
        DirectProtocolPreview = string.Empty;
        DirectPreviewStatus = "正在解析直连协议字段...";
        Receipt = null;
        BatchId = string.Empty;

        try
        {
            _result = _parser.Parse(filePath);
        }
        catch (Exception exception)
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

        try
        {
            _directResult = _directParser.Parse(filePath);
        }
        catch (Exception exception)
        {
            _directResult = null;
            DirectIssues.Add(exception.Message);
        }

        if (_directResult is { HasDirectColumns: true })
        {
            foreach (var issue in _directResult.Issues)
            {
                DirectIssues.Add(issue.SourceRowNumber > 0
                    ? $"第 {issue.SourceRowNumber} 行：{issue.Message}"
                    : issue.Message);
            }

            var directIndex = 1;
            foreach (var task in _directResult.Tasks)
                DirectTasks.Add(new ShineLabDirectSampleTaskRowViewModel(directIndex++, task));
            SelectedDirectTask = DirectTasks.FirstOrDefault();

            if (_directResult.CanBuildPreview)
            {
                DirectProtocolPreview = BuildDirectProtocolPreview();
                DirectPreviewStatus = $"直连字段已解析 {DirectTasks.Count} 条；仅生成预览，尚未发送 TCP";
            }
            else
            {
                DirectPreviewStatus = $"直连字段存在 {DirectIssues.Count} 个问题，禁止生成进样预览";
            }
        }
        else
        {
            DirectPreviewStatus = "当前文件未包含完整直连字段，未生成 Config/Command 预览";
        }

        Status = _directResult is { HasDirectColumns: true }
            ? _directResult.CanBuildPreview
                ? $"已解析 {DirectTasks.Count} 条直连样品任务；请选择一条进行 Config 预检"
                : $"直连任务存在 {DirectIssues.Count} 个问题；修正后才能进行 Config 预检"
            : _result.CanGenerateSequence
                ? $"已解析 {SampleTasks.Count} 条兼容任务"
                : $"文件未形成可用直连任务，发现 {Issues.Count} 条兼容解析问题";
        var hasImportedTasks = DirectTasks.Count > 0 || SampleTasks.Count > 0;
        OfflineState.MarkReady(
            hasImportedTasks,
            DirectTasks.Count > 0
                ? "直连样品任务文件已解析。"
                : SampleTasks.Count > 0
                    ? "兼容样品任务文件已解析。"
                    : "文件有效，但暂无样品任务。");
        RaiseLoadState();
    }

    public void Clear()
    {
        _result = null;
        _directResult = null;
        SampleTasks.Clear();
        DirectTasks.Clear();
        SelectedDirectTask = null;
        SelectedSampleTask = null;
        Issues.Clear();
        DirectIssues.Clear();
        DirectProtocolPreview = string.Empty;
        DirectPreviewStatus = "直连协议预览尚未生成";
        DirectPreflightStatus = "请选择一条样品，并确认仪器空闲后执行单条 Config 预检。";
        DirectPreflightTask = null;
        _directPreflightTaskUuid = null;
        Receipt = null;
        BatchId = string.Empty;
        SourceFilePath = string.Empty;
        AllowAppend = false;
        Status = "已清空直连样品任务列表";
        OfflineState.MarkReady(hasData: false, "已清空直连样品任务列表。");
        RaiseLoadState();
    }

    public string BuildCsvPreview() => ShineLabCsvWriter.Build(RequireTasks());

    public string BuildDirectProtocolPreview()
    {
        if (_directResult?.CanBuildPreview != true)
            throw new InvalidOperationException("当前没有可生成直连协议预览的有效样品行。");

        var previewTasks = SelectedDirectTask is null
            ? _directResult.Tasks
            : [SelectedDirectTask.Task];
        return ShineLabDirectProtocolPreview.Build(
            previewTasks,
            equipmentCode: "STN61_01",
            taskUuid: "preview-task-001",
            configStrId: "preview-config-001",
            commandStrId: "preview-command-001");
    }

    public async Task SendSingleConfigPreflightAsync()
    {
        if (!CanSendSingleConfigPreflight || _mes is null || SelectedDirectTask is null) return;

        IsDirectPreflighting = true;
        var selected = SelectedDirectTask.Task;
        try
        {
            DirectPreflightStatus = $"正在检查 {DirectEquipmentCode} 在线及空闲状态...";
            var statuses = await _mes.GetShineLabDeviceStatusesAsync(CancellationToken.None);
            var device = statuses.FirstOrDefault(item =>
                item.EquipmentCode.Equals(DirectEquipmentCode, StringComparison.OrdinalIgnoreCase));
            if (device is null || !device.Online)
                throw new InvalidOperationException($"{DirectEquipmentCode} 当前未在线，未发送 Config。");
            if (device.HasActiveTask || device.Status != 0)
                throw new InvalidOperationException(
                    $"{DirectEquipmentCode} 当前不是空闲状态（state={device.State}, status={device.Status}），未发送 Config。");

            var taskUuid = _directPreflightTaskUuid ??=
                $"ic-config-check-{_clock().ToLocalTime():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            var sample = new ShineLabSampleData(
                selected.SampleId,
                selected.SampleName,
                selected.SampleTypeCode.ToString(CultureInfo.InvariantCulture),
                selected.Position,
                selected.MPos,
                selected.Channel,
                selected.InstrumentMethod,
                selected.ProcessingMethod,
                selected.DetectionMethod,
                selected.InjectionVolume,
                selected.InjectionVolumeUnit);
            var request = new ShineLabTaskCreateRequest(
                DirectEquipmentCode,
                taskUuid,
                [sample],
                selected.InstrumentMethod,
                selected.ProcessingMethod,
                selected.DetectionMethod);

            DirectPreflightStatus = $"正在发送 {selected.SampleId} 的单条 Config...";
            DirectPreflightTask = await _mes.CreateShineLabTaskAsync(request, CancellationToken.None);
            DirectPreflightTask = await _mes.ConfigureShineLabTaskAsync(
                taskUuid,
                CancellationToken.None);
            DirectPreflightStatus = DirectPreflightTask.Status == "Configured"
                ? $"Config Success：{selected.SampleId} 已通过单条预检；未发送 Command。"
                : $"Config 返回状态 {DirectPreflightTask.Status}：{DirectPreflightTask.LastError ?? "无错误说明"}；未发送 Command。";
        }
        catch (Exception exception)
        {
            DirectPreflightStatus = $"单条 Config 预检失败：{exception.Message}";
        }
        finally
        {
            AllowSingleConfigPreflight = false;
            IsDirectPreflighting = false;
        }
    }

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
        OnPropertyChanged(nameof(HasDirectTasks));
        RaiseDirectPreflightState();
        RaiseSubmitState();
    }

    private void RaiseDirectPreflightState()
    {
        OnPropertyChanged(nameof(CanSendSingleConfigPreflight));
        (SendSingleConfigPreflightCommand as AsyncCommand)?.RaiseCanExecuteChanged();
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
