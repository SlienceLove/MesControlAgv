using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

public sealed record ShineLabCompoundRow(string SampleId, string Name, string RetentionTime,
    string Area, string Concentration, string Unit);

/// <summary>Operator-facing DCSeq control. Reads/prepare never dispatch a device command.</summary>
public sealed class ShineLabControlViewModel : INotifyPropertyChanged
{
    private readonly IMesClient _mes;
    private readonly IShineLabOperationConfirmation _confirmation;
    private readonly Dictionary<int, string> _preparedRows = [];
    private ShineLabServerStatusResponse? _server;
    private ShineLabDeviceStatusResponse? _device;
    private ShineLabDirectSampleTask? _selectedImport;
    private ShineLabTaskResponse? _selectedTask;
    private ShineLabOperatorTaskRow? _selectedTaskItem;
    private string _operatorTaskSummary = "选择样品记录后查看结果。";
    private string _operatorReply = "尚无操作反馈";
    private string? _preparedTaskId;
    private bool _preparedCanSend;
    private bool _busy;
    private string _message = "请刷新连接状态；此页不自动启动样品。";
    private string _lastReply = "尚未发送操作";
    private string _preview = "准备任务后显示服务端实际 Config（含已分配通讯编号）。";
    private string _preparedSummary = "尚未准备任务";
    private string _detailSummary = "请选择任务并查看详情";
    private string _detailJson = "";
    private string _sourceFile = "";

    public ShineLabControlViewModel(IMesClient mes, IShineLabOperationConfirmation? confirmation = null,
        string equipmentCode = "STN61_01")
    {
        _mes = mes;
        _confirmation = confirmation ?? new ShineLabOperationConfirmation();
        EquipmentCode = equipmentCode;
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        TrayPopupCommand = new AsyncCommand(() => SendTrayAsync("TrayPopup"), () => CanUseTray);
        TrayResetCommand = new AsyncCommand(() => SendTrayAsync("TrayReset"), () => CanUseTray);
        PrepareCommand = new AsyncCommand(PrepareSelectedAsync, () => CanPrepare);
        SendConfigCommand = new AsyncCommand(SendPreparedConfigAsync, () => CanSendConfig);
        LoadDetailCommand = new AsyncCommand(LoadSelectedTaskAsync, () => !IsBusy && SelectedTask is not null);
    }

    public string EquipmentCode { get; }
    public ObservableCollection<ShineLabDirectSampleTask> ImportedSamples { get; } = [];
    public ObservableCollection<string> ImportIssues { get; } = [];
    public ObservableCollection<ShineLabTaskResponse> Tasks { get; } = [];
    public ObservableCollection<ShineLabOperatorTaskRow> TaskItems { get; } = [];
    public ShineLabOperatorTaskRow? SelectedTaskItem
    {
        get => _selectedTaskItem;
        set { if (Set(ref _selectedTaskItem, value)) SelectedTask = value?.Task; }
    }
    public string OperatorTaskSummary { get => _operatorTaskSummary; private set => Set(ref _operatorTaskSummary, value); }
    public string OperatorReply { get => _operatorReply; private set => Set(ref _operatorReply, value); }
    public string OperatorPreparedSummary => _preparedTaskId is null ? "请先核对所选样品。" :
        $"{SelectedImport?.SampleName}：{(_preparedCanSend ? "核对完成，可以下发" : "已处理，请勿重复下发")}";
    public string OperatorConnectionSummary => _server is null ? "通讯状态待刷新" :
        $"{(_device?.Online == true ? "仪器通讯已连接" : "仪器通讯已断开")} · {(_device?.Online == true ? _device.State switch { "Idle" => "空闲", "Running" => "运行中", "Error" => "异常", _ => "状态待确认" } : "等待连接")}";
    public string OperatorOnlineText => _server is null ? "在线状态未确认" : _device?.Online == true ? "在线" : "离线";
    public ObservableCollection<ShineLabTaskSampleResponse> TaskSamples { get; } = [];
    public ObservableCollection<ShineLabCompoundRow> Compounds { get; } = [];
    public ObservableCollection<ShineLabResultReviewItem> ResultReviews { get; } = [];
    public IEnumerable<ShineLabResultReviewItem> OperatorResultReviews => ResultReviews.Select(r => r with
    {
        Observation = r.Observation.Replace("Result", "检测结果").Replace("JSON", "数据格式"),
        Suggestions = r.Suggestions.Replace("Result", "检测结果").Replace("JSON", "数据格式")
    });
    public string OperatorMessage => IsBusy ? "正在处理，请稍候…" :
        Message.Contains("未确认") ? "操作结果尚未确认，请核对仪器后再处理，不要重复操作。" :
        Message.Contains("未发送") && Message.Contains("预检") ? "预检未通过，未发送。请核对设备连接及运行状态。" :
        Message.Contains("失败") ? "操作未完成，请检查文件或仪器状态；通讯详情由系统保留。" :
        Message.Replace("Config", "样品配置").Replace("DcSeqSingleChannel", "单通道模式");
    public string ResultReviewSummary => ResultReviews.Count > 0
        ? $"结果待复核：{ResultReviews.Count} 项提示（可能原因不是诊断结论）"
        : TaskSamples.Any(s => s.ResultJson is not null) ? "未发现当前规则命中；不等于实验结果有效性已确认" : "等待结果，尚未进行数值复核";
    public ICommand RefreshCommand { get; }
    public ICommand TrayPopupCommand { get; }
    public ICommand TrayResetCommand { get; }
    public ICommand PrepareCommand { get; }
    public ICommand SendConfigCommand { get; }
    public ICommand LoadDetailCommand { get; }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) RaiseState(); } }
    public bool IsInteractive => !IsBusy;
    public string Message { get => _message; private set { if (Set(ref _message, value)) OnChanged(nameof(OperatorMessage)); } }
    public string LastReply { get => _lastReply; private set => Set(ref _lastReply, value); }
    public string ConfigPreview { get => _preview; private set => Set(ref _preview, value); }
    public string PreparedSummary { get => _preparedSummary; private set => Set(ref _preparedSummary, value); }
    public string TaskSummary { get => _detailSummary; private set => Set(ref _detailSummary, value); }
    public string TaskDetailJson { get => _detailJson; private set => Set(ref _detailJson, value); }
    public string SourceFile { get => _sourceFile; private set => Set(ref _sourceFile, value); }
    public string ConnectionSummary => _server is null ? "MES 通讯接口不可用 / 未刷新" :
        $"监听 {_server.ListenAddress}:{_server.Port}｜{_server.PayloadProfile}｜{(_device?.Online == true ? "TCP 在线" : "TCP 离线")}｜设备 {_device?.State ?? "未收到"}";
    public string TrayAvailability => CanUseTray ? "点击后将刷新并核验设备状态，通过后需单独确认" :
        _server?.EnableTrayCommands != true ? "托盘功能未启用或能力尚未读取" : "托盘控制要求设备在线、空闲且状态新鲜";
    private bool SupportedProfile => _server?.Enabled == true && _server.PayloadProfile == "DcSeqSingleChannel";
    private bool CachedIdle => _device is { Online: true, Status: 0, HasActiveTask: false, State: "Idle" };
    private bool FreshIdle => CachedIdle && _device is { } device &&
        DateTimeOffset.UtcNow - device.LastSeenAtUtc <= TimeSpan.FromSeconds(_server?.StaleAfterSeconds ?? 0);
    // Enable preflight, not dispatch. Time-based cached eligibility silently rejected clicks
    // before ReadConnectionAsync could obtain the latest status. Dispatch still checks FreshIdle twice.
    public bool CanUseTray => !IsBusy && SupportedProfile && _server?.EnableTrayCommands == true && CachedIdle;
    public bool CanPrepare => !IsBusy && SupportedProfile && SelectedImport is not null && ImportIssues.Count == 0;
    public bool CanSendConfig => !IsBusy && SupportedProfile && _preparedCanSend && _preparedTaskId is not null;
    public ShineLabDirectSampleTask? SelectedImport
    {
        get => _selectedImport;
        set
        {
            if (!Set(ref _selectedImport, value)) return;
            _preparedTaskId = null; _preparedCanSend = false;
            ConfigPreview = "请准备选中行以取得实际通讯编号；尚未发送。";
            PreparedSummary = "尚未准备当前行";
            RaiseState();
        }
    }
    public ShineLabTaskResponse? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (!Set(ref _selectedTask, value)) return;
            Set(ref _selectedTaskItem, TaskItems.FirstOrDefault(t => t.Task.TaskUuid == value?.TaskUuid), nameof(SelectedTaskItem));
            TaskSamples.Clear(); Compounds.Clear(); ResultReviews.Clear(); TaskDetailJson = "";
            OperatorTaskSummary = "选择样品记录后点击查看结果。";
            OnChanged(nameof(ResultReviewSummary));
            TaskSummary = "点击查看选中任务，读取编号映射及结果。";
            RaiseState();
        }
    }

    private async Task ReadConnectionAsync()
    {
        try
        {
            _server = await _mes.GetShineLabServerStatusAsync(default);
            var devices = await _mes.GetShineLabDeviceStatusesAsync(default);
            _device = devices.SingleOrDefault(d => d.EquipmentCode.Equals(EquipmentCode, StringComparison.OrdinalIgnoreCase));
        }
        catch { _server = null; _device = null; throw; }
        finally { RaiseState(); }
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await ReadConnectionAsync();
            var selected = SelectedTask?.TaskUuid;
            var tasks = await _mes.GetShineLabTasksAsync(100, default);
            Tasks.Clear(); TaskItems.Clear();
            foreach (var task in tasks.Where(t => t.EquipmentCode.Equals(EquipmentCode, StringComparison.OrdinalIgnoreCase)))
            {
                Tasks.Add(task);
                var detail = await _mes.GetShineLabTaskDetailAsync(task.TaskUuid, default);
                TaskItems.Add(new(task, detail));
            }
            SelectedTaskItem = TaskItems.FirstOrDefault(t => t.Task.TaskUuid == selected) ?? TaskItems.FirstOrDefault();
            if (SelectedTask is not null) await ReadDetailsAsync(SelectedTask.TaskUuid);
            Message = SupportedProfile ? "连接与任务已刷新；未发送任何设备命令。" : "此控制页要求 MES 使用 DcSeqSingleChannel；当前仅查看状态。";
        }
        catch (Exception e) { Message = $"刷新失败：{e.Message}"; }
        finally { IsBusy = false; }
    }

    public async Task ImportAsync(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        ImportedSamples.Clear(); ImportIssues.Clear(); _preparedRows.Clear(); SelectedImport = null;
        try
        {
            SourceFile = path;
            await ReadConnectionAsync();
            if (!SupportedProfile) throw new InvalidOperationException("请先连接 DCSeq 单通道 MES，不能按旧通道规则导入。");
            var parsed = new ShineLabDirectSequenceParser().Parse(path, ShineLabPayloadProfile.DcSeqSingleChannel);
            foreach (var issue in parsed.Issues) ImportIssues.Add($"第 {issue.SourceRowNumber} 行：{issue.Message}");
            foreach (var row in parsed.Tasks) ImportedSamples.Add(row);
            if (!parsed.CanBuildPreview && ImportIssues.Count == 0) ImportIssues.Add("文件没有可用样品行。");
            SelectedImport = ImportedSamples.FirstOrDefault();
            Message = $"解析 {ImportedSamples.Count} 行，问题 {ImportIssues.Count} 项。导入未创建 AQ 样品，也未发送命令。";
        }
        catch (Exception e) { ImportIssues.Add(e.Message); Message = $"导入失败：{e.Message}"; }
        finally { IsBusy = false; }
    }

    public async Task PrepareSelectedAsync()
    {
        if (!CanPrepare || SelectedImport is not { } row) return;
        IsBusy = true; _preparedCanSend = false;
        try
        {
            await ReadConnectionAsync();
            if (!SupportedProfile) throw new InvalidOperationException("MES 协议模式发生变化，未准备任务。");
            if (!_preparedRows.TryGetValue(row.SourceRowNumber, out var id))
                _preparedRows[row.SourceRowNumber] = id = $"wpf-dcseq-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            var sample = new ShineLabSampleData(row.SampleId, row.SampleName, row.SampleTypeCode.ToString(CultureInfo.InvariantCulture),
                row.Position, row.MPos, "", row.InstrumentMethod, row.ProcessingMethod, row.DetectionMethod,
                row.InjectionVolume, row.InjectionVolumeUnit);
            await _mes.CreateShineLabTaskAsync(new(EquipmentCode, id, [sample], row.InstrumentMethod, row.ProcessingMethod, row.DetectionMethod), default);
            var detail = await _mes.GetShineLabTaskDetailAsync(id, default) ?? throw new InvalidOperationException("无法读取已准备任务。");
            var mapping = detail.Samples?.SingleOrDefault() ?? throw new InvalidOperationException("MES 未返回唯一样品映射，禁止下发。");
            var preview = await _mes.GetShineLabTaskConfigPreviewAsync(id, default);
            if (detail.Task.TaskUuid != id || detail.Task.EquipmentCode != EquipmentCode || mapping.SourceSampleId != row.SampleId ||
                preview.GetProperty("taskUuid").GetString() != id || preview.GetProperty("equipmentCode").GetString() != EquipmentCode ||
                preview.GetProperty("payloadProfile").GetString() != "DcSeqSingleChannel" ||
                preview.GetProperty("body").TryGetProperty("channel", out _) ||
                preview.GetProperty("body").GetProperty("sampleData").GetArrayLength() != 1 ||
                preview.GetProperty("body").GetProperty("sampleData")[0].GetProperty("sampleId").GetString() != mapping.SampleId)
                throw new InvalidOperationException("服务端任务、编号映射或预览不一致，禁止下发。");
            if (!ReferenceEquals(SelectedImport, row)) return;
            _preparedTaskId = id;
            _preparedCanSend = detail.Task.Status == "Created" && !mapping.ConfigAttempted;
            ConfigPreview = JsonSerializer.Serialize(preview, new JsonSerializerOptions { WriteIndented = true });
            PreparedSummary = $"任务：{id}\n源编号：{mapping.SourceSampleId} → 通讯 sampleId：{mapping.SampleId}\n状态：{detail.Task.Status}";
            Message = "任务与编号已在中控保存，尚未发 Config。方法和体积仅作核对，不由当前协议设置。";
        }
        catch (Exception e) { Message = $"准备失败：{e.Message}"; }
        finally { IsBusy = false; }
    }

    public async Task SendPreparedConfigAsync()
    {
        if (!CanSendConfig || _preparedTaskId is not { } id || SelectedImport is not { } row) return;
        IsBusy = true;
        var dispatchAttempted = false;
        Message = "正在刷新设备状态，尚未发送 Config。";
        try
        {
            await ReadConnectionAsync();
            if (!SupportedProfile || !FreshIdle) throw new InvalidOperationException("设备不处于新鲜在线空闲状态，未发送。");
            if (!_confirmation.Confirm("下发选中样品（不启动）",
                $"样品 {row.SampleName}\n{OperatorPreparedSummary}\n位置 {row.Position} / {row.MPos}，方法 {row.ProcessingMethod}，体积 {row.InjectionVolume} {row.InjectionVolumeUnit}。\n方法/体积需在 AQ 核对；目标为 AQ 上次运行序列，样品可能自动勾选，运行中可能自动接续。确认当前无运行样品、目标序列正确？"))
            { Message = "已取消，未发送 Config。"; return; }
            await ReadConnectionAsync();
            if (!SupportedProfile || !FreshIdle || id != _preparedTaskId || !ReferenceEquals(row, SelectedImport))
                throw new InvalidOperationException("状态或选中样品发生变化，未发送。");
            _preparedCanSend = false; // No retry from a second click after an uncertain outcome.
            dispatchAttempted = true;
            var response = await _mes.ConfigureShineLabTaskAsync(id, default);
            LastReply = $"Config：{response.Status} / {response.CurrentStage}。接受不等于样品已创建，请在 AQ 核对。未发送启动。";
            OperatorReply = $"样品下发：{ShineLabOperatorTaskRow.DescribeState(response.Status)}。请在 AQ 核对新增行并保存，尚未启动。";
            Message = OperatorReply;
        }
        catch (Exception e)
        {
            Message = dispatchAttempted
                ? $"Config 未确认：{e.Message}。请查询任务并核对 AQ，不要重复追加。"
                : $"Config 预检未通过，未发送：{e.Message} 请核对连接与设备状态。";
        }
        finally { IsBusy = false; }
    }

    public async Task SendTrayAsync(string action)
    {
        if (action is not ("TrayPopup" or "TrayReset") || !CanUseTray) return;
        IsBusy = true;
        var label = action == "TrayPopup" ? "托盘弹出" : "托盘复位";
        var dispatchAttempted = false;
        Message = $"正在刷新设备状态，尚未发送{label}。";
        try
        {
            await ReadConnectionAsync();
            EnsureTrayReady();
            if (!_confirmation.Confirm(label, $"执行一次{label}。\n请确认无运行样品，待运行行已取消勾选，运动区域无遮挡且手已离开。\n不会自动重试，实际到位需现场确认。"))
            { Message = "已取消，未发送托盘命令。"; return; }
            await ReadConnectionAsync();
            EnsureTrayReady();
            dispatchAttempted = true;
            var response = await _mes.SendShineLabCommandAsync(EquipmentCode,
                new($"wpf-tray-{Guid.NewGuid():N}", 0, StrAction: action, Param: ""), default);
            LastReply = $"{label}：{(response.Success ? "Success（请现场确认到位）" : "Failed")}\n请求编号 {response.StrId}\n{response.Body.GetRawText()}";
            OperatorReply = $"{label}：{(response.Success ? "仪器已受理，请现场确认到位" : "仪器未接受操作，请检查设备状态")}。";
            Message = "命令已发送一次，未自动执行后续动作。";
        }
        catch (Exception e)
        {
            LastReply = dispatchAttempted
                ? $"{label}结果未确认：{e.Message}。请核对现场及报文，不自动重试。"
                : $"{label}预检未通过，未发送：{e.Message}";
            Message = LastReply;
            OperatorReply = dispatchAttempted ? $"{label}结果未确认，请核对现场，不要重复操作。" : $"{label}预检未通过，未发送。请刷新并核对设备状态。";
        }
        finally { IsBusy = false; }
    }

    private void EnsureTrayReady()
    {
        if (!SupportedProfile || _server?.EnableTrayCommands != true || !FreshIdle)
            throw new InvalidOperationException("托盘功能未启用，或设备不处于在线空闲状态。");
    }

    public async Task LoadSelectedTaskAsync()
    {
        if (IsBusy || SelectedTask is not { } selected) return;
        IsBusy = true;
        try { await ReadDetailsAsync(selected.TaskUuid); }
        catch (Exception e) { TaskSamples.Clear(); Compounds.Clear(); ResultReviews.Clear(); OnChanged(nameof(ResultReviewSummary)); TaskDetailJson = ""; TaskSummary = $"查询失败：{e.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task ReadDetailsAsync(string id)
    {
        var detail = await _mes.GetShineLabTaskDetailAsync(id, default) ?? throw new InvalidOperationException("任务不存在。");
        if (SelectedTask?.TaskUuid != id) return;
        TaskSamples.Clear(); Compounds.Clear(); ResultReviews.Clear();
        var emptyResults = new List<string>();
        foreach (var sample in detail.Samples ?? [])
        {
            TaskSamples.Add(sample);
            foreach (var review in ShineLabResultReview.Assess(sample)) ResultReviews.Add(review);
            if (sample.ResultJson is null) continue;
            JsonDocument json;
            try { json = JsonDocument.Parse(sample.ResultJson); }
            catch (JsonException) { continue; } // Show the advisory and retain raw detail instead of hiding all samples.
            using var resultDocument = json;
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("compounds", out var compounds) || compounds.ValueKind != JsonValueKind.Array) continue;
            if (compounds.GetArrayLength() == 0) emptyResults.Add(sample.SampleId);
            foreach (var compound in compounds.EnumerateArray())
                Compounds.Add(new(sample.SampleId, Value(compound, "name"), Value(compound, "retaintime"),
                    Value(compound, "area"), Value(compound, "concent"), Value(compound, "concunit")));
        }
        var resultText = detail.ResultState switch
        {
            "AllResultsReceived" => "结果已收齐（不等于正常结束已确认）",
            "PartialResultsReceived" => "已收到部分结果", "NeedsReview" => "结果冲突，需核对",
            "NotTracked" => "历史未托管任务", _ => "等待结果"
        };
        TaskSummary = $"{id}\n任务：{detail.Task.Status} / {detail.Task.CurrentStage}\n{resultText}\n{detail.Task.LastError}";
        var finishedWithoutResult = TaskSamples.Where(s => (s.TaskFinishObserved || s.EndMissionObserved) && s.ResultJson is null)
            .Select(s => s.SampleId).ToArray();
        if (finishedWithoutResult.Length > 0)
            TaskSummary += $"\n已收到结束通知，但尚未收到 Result：{string.Join("、", finishedWithoutResult)}。不代表无峰或零浓度，请核对 AQ/DA 数据及客户端日志；不要自动重跑。";
        if (emptyResults.Count > 0)
            TaskSummary += $"\n已收到 Result 的组分列表为空：{string.Join("、", emptyResults)}。不代表无峰，请核对处理方法、曲线和客户端结果。";
        TaskDetailJson = JsonSerializer.Serialize(detail, new JsonSerializerOptions { WriteIndented = true });
        OperatorTaskSummary = $"{string.Join("、", TaskSamples.Select(s => s.SampleName))}\n{ShineLabOperatorTaskRow.DescribeState(detail.Task.Status)} · " +
            (detail.ResultState == "AllResultsReceived" ? "检测结果已收到，仍需核对有效性" : detail.ResultState == "PartialResultsReceived" ? "已收到部分检测结果" : "等待检测结果");
        OnChanged(nameof(ResultReviewSummary));
    }

    private static string Value(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ToString() : "-";
    private void RaiseState()
    {
        foreach (var name in new[] { nameof(IsInteractive), nameof(CanUseTray), nameof(CanPrepare), nameof(CanSendConfig), nameof(ConnectionSummary), nameof(TrayAvailability) }) OnChanged(name);
        OnChanged(nameof(OperatorPreparedSummary)); OnChanged(nameof(OperatorConnectionSummary));
        OnChanged(nameof(OperatorOnlineText));
        OnChanged(nameof(OperatorMessage)); OnChanged(nameof(OperatorResultReviews));
        foreach (var command in new[] { RefreshCommand, TrayPopupCommand, TrayResetCommand, PrepareCommand, SendConfigCommand, LoadDetailCommand })
            (command as AsyncCommand)?.RaiseCanExecuteChanged();
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnChanged(name); return true; }
    private void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
