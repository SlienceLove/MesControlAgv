using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// G5-D manual scheduling board. Commands mutate plan/job scheduling state only;
/// admission creates the existing G5-C run/leases but never advances a node or
/// invokes a device command.
/// </summary>
public sealed class ExperimentSchedulingViewModel : ExperimentBindableObject, IDisposable
{
    private readonly IMesClient _mes;
    private readonly IExperimentSchedulingConfirmation _confirmation;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly AsyncCommand _refreshCommand;
    private readonly AsyncCommand _createJobCommand;
    private readonly AsyncCommand _scheduleCommand;
    private readonly AsyncCommand _unscheduleCommand;
    private readonly AsyncCommand _cancelJobCommand;
    private readonly AsyncCommand _admitCommand;
    private readonly RelayCommand _addJobParameterCommand;
    private readonly RelayCommand<ExperimentParameterEditorViewModel> _removeJobParameterCommand;
    private readonly RelayCommand<ExperimentScheduleBlockViewModel> _selectScheduleBlockCommand;
    private readonly List<ExperimentJobItemViewModel> _allJobs = [];
    private IReadOnlyList<ExperimentSchedulingAuditEntry> _allAudits = [];
    private ExperimentScheduleSnapshot _snapshot = new();
    private ExperimentJobItemViewModel? _selectedJob;
    private ScheduleEntry? _selectedSchedule;
    private ExperimentScheduleActivity? _selectedActivity;
    private ExperimentPublishedPlanOption? _selectedPublishedPlan;
    private bool _isBusy;
    private bool _hasLoaded;
    private bool _suppressResourceSelection;
    private DateTime? _boardDate = DateTime.Today;
    private int _windowStartHour = 6;
    private int _windowHours = 16;
    private DateTime? _placementDate = DateTime.Today;
    private int _placementStartHour = 8;
    private int _placementStartMinute;
    private int _durationMinutes = 60;
    private int _priority = 50;
    private string _operatorName = Environment.GetEnvironmentVariable("EXPERIMENT_OPERATOR") ?? Environment.UserName;
    private string _reason = string.Empty;
    private string _sampleBatchId = string.Empty;
    private string _sampleId = string.Empty;
    private string _statusMessage = "排程数据尚未加载。";
    private string _errorMessage = string.Empty;
    private DateTimeOffset? _lastRefreshedAt;
    private bool _isTimelineFocusMode;

    public ExperimentSchedulingViewModel(
        IMesClient mes,
        IExperimentSchedulingConfirmation? confirmation = null)
    {
        _mes = mes ?? throw new ArgumentNullException(nameof(mes));
        _confirmation = confirmation ?? MessageBoxExperimentSchedulingConfirmation.Instance;
        _refreshCommand = new AsyncCommand(() => RefreshAsync(), () => !IsBusy);
        _createJobCommand = new AsyncCommand(CreateJobAsync, () => CanCreateJob);
        _scheduleCommand = new AsyncCommand(ScheduleAsync, () => CanSchedule);
        _unscheduleCommand = new AsyncCommand(UnscheduleAsync, () => CanUnschedule);
        _cancelJobCommand = new AsyncCommand(CancelJobAsync, () => CanCancelJob);
        _admitCommand = new AsyncCommand(AdmitAsync, () => CanAdmit);
        _addJobParameterCommand = new RelayCommand(AddJobParameter, () => !IsBusy);
        _removeJobParameterCommand = new RelayCommand<ExperimentParameterEditorViewModel>(
            RemoveJobParameter,
            item => !IsBusy && item is not null);
        _selectScheduleBlockCommand = new RelayCommand<ExperimentScheduleBlockViewModel>(SelectScheduleBlock);
    }

    public ObservableCollection<ExperimentPublishedPlanOption> PublishedPlans { get; } = [];
    public ObservableCollection<ExperimentJobItemViewModel> TaskPool { get; } = [];
    public ObservableCollection<ExperimentResourceSelectionItemViewModel> Resources { get; } = [];
    public ObservableCollection<ExperimentResourceLaneViewModel> ResourceLanes { get; } = [];
    public ObservableCollection<ExperimentTimelineTickViewModel> TimelineTicks { get; } = [];
    public ObservableCollection<ExperimentScheduleBlockReasonItemViewModel> BlockingReasons { get; } = [];
    public ObservableCollection<ExperimentSchedulingAuditItemViewModel> SelectedJobAudits { get; } = [];
    public ObservableCollection<ExperimentParameterEditorViewModel> JobParameters { get; } = [];

    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToArray();
    public IReadOnlyList<int> MinuteOptions { get; } = [0, 15, 30, 45];
    public IReadOnlyList<int> WindowHourOptions { get; } = [4, 8, 12, 16, 24];
    public double TimelineWidth => ExperimentScheduleTimelineProjector.TimelineWidth;

    public ICommand RefreshCommand => _refreshCommand;
    public ICommand CreateJobCommand => _createJobCommand;
    public ICommand ScheduleCommand => _scheduleCommand;
    public ICommand UnscheduleCommand => _unscheduleCommand;
    public ICommand CancelJobCommand => _cancelJobCommand;
    public ICommand AdmitCommand => _admitCommand;
    public ICommand AddJobParameterCommand => _addJobParameterCommand;
    public ICommand RemoveJobParameterCommand => _removeJobParameterCommand;
    public ICommand SelectScheduleBlockCommand => _selectScheduleBlockCommand;

    public ExperimentPublishedPlanOption? SelectedPublishedPlan
    {
        get => _selectedPublishedPlan;
        set
        {
            if (!SetField(ref _selectedPublishedPlan, value)) return;
            RaiseCommandStates();
        }
    }

    public ExperimentJobItemViewModel? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (!SetField(ref _selectedJob, value)) return;
            ApplySelectedJob();
        }
    }

    public ScheduleEntry? SelectedSchedule
    {
        get => _selectedSchedule;
        private set
        {
            if (!SetField(ref _selectedSchedule, value)) return;
            OnPropertyChanged(nameof(SelectedScheduleWindow));
            OnPropertyChanged(nameof(SelectedResources));
            OnPropertyChanged(nameof(ScheduleButtonText));
        }
    }

    public ExperimentScheduleActivity? SelectedActivity
    {
        get => _selectedActivity;
        private set
        {
            if (!SetField(ref _selectedActivity, value)) return;
            OnPropertyChanged(nameof(SelectedActivityStatus));
            OnPropertyChanged(nameof(SelectedActivityResource));
            OnPropertyChanged(nameof(SelectedActivityWindow));
            OnPropertyChanged(nameof(SelectedActivityError));
            OnPropertyChanged(nameof(HasSelectedActivity));
        }
    }

    public DateTime? BoardDate
    {
        get => _boardDate;
        set
        {
            if (!SetField(ref _boardDate, value?.Date)) return;
            OnPropertyChanged(nameof(BoardWindow));
        }
    }

    public int WindowStartHour
    {
        get => _windowStartHour;
        set
        {
            if (!SetField(ref _windowStartHour, Math.Clamp(value, 0, 23))) return;
            OnPropertyChanged(nameof(BoardWindow));
        }
    }

    public int WindowHours
    {
        get => _windowHours;
        set
        {
            if (!SetField(ref _windowHours, Math.Clamp(value, 1, 24))) return;
            OnPropertyChanged(nameof(BoardWindow));
        }
    }

    public DateTime? PlacementDate
    {
        get => _placementDate;
        set
        {
            if (!SetField(ref _placementDate, value?.Date)) return;
            RaiseCommandStates();
        }
    }

    public int PlacementStartHour
    {
        get => _placementStartHour;
        set
        {
            if (!SetField(ref _placementStartHour, Math.Clamp(value, 0, 23))) return;
            RaiseCommandStates();
        }
    }

    public int PlacementStartMinute
    {
        get => _placementStartMinute;
        set
        {
            if (!SetField(ref _placementStartMinute, Math.Clamp(value, 0, 59))) return;
            RaiseCommandStates();
        }
    }

    public int DurationMinutes
    {
        get => _durationMinutes;
        set
        {
            if (!SetField(ref _durationMinutes, value)) return;
            RaiseCommandStates();
        }
    }

    public int Priority
    {
        get => _priority;
        set
        {
            if (!SetField(ref _priority, value)) return;
            RaiseCommandStates();
        }
    }

    public string OperatorName
    {
        get => _operatorName;
        set
        {
            if (!SetField(ref _operatorName, value ?? string.Empty)) return;
            RaiseCommandStates();
        }
    }

    public string Reason
    {
        get => _reason;
        set
        {
            if (!SetField(ref _reason, value ?? string.Empty)) return;
            RaiseCommandStates();
        }
    }

    public string SampleBatchId
    {
        get => _sampleBatchId;
        set
        {
            if (!SetField(ref _sampleBatchId, value ?? string.Empty)) return;
            RaiseCommandStates();
        }
    }

    public string SampleId { get => _sampleId; set => SetField(ref _sampleId, value ?? string.Empty); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            RaiseCommandStates();
        }
    }

    public string StatusMessage { get => _statusMessage; private set => SetField(ref _statusMessage, value); }
    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (!SetField(ref _errorMessage, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public DateTimeOffset? LastRefreshedAt
    {
        get => _lastRefreshedAt;
        private set
        {
            if (!SetField(ref _lastRefreshedAt, value)) return;
            OnPropertyChanged(nameof(RefreshStatus));
        }
    }

    public string RefreshStatus => LastRefreshedAt is null
        ? "尚未刷新"
        : $"更新于 {LastRefreshedAt.Value.ToLocalTime():HH:mm:ss}";
    public bool IsTimelineFocusMode
    {
        get => _isTimelineFocusMode;
        set
        {
            if (!SetField(ref _isTimelineFocusMode, value)) return;
            OnPropertyChanged(nameof(TimelineFocusButtonText));
        }
    }
    public string TimelineFocusButtonText => IsTimelineFocusMode
        ? "退出时间轴全屏"
        : "全屏查看时间轴";

    public string BoardWindow => TryGetBoardWindow(out var start, out var end)
        ? $"{start.ToLocalTime():yyyy-MM-dd HH:mm} - {end.ToLocalTime():MM-dd HH:mm}"
        : "窗口无效";
    public string ScheduleButtonText => SelectedSchedule?.Status is ScheduleEntryStatus.Scheduled or ScheduleEntryStatus.Blocked
        ? "人工改期"
        : "人工排程";
    public string SelectedScheduleWindow => SelectedSchedule is null
        ? "未排程"
        : $"{SelectedSchedule.PlannedStart.ToLocalTime():yyyy-MM-dd HH:mm} - {SelectedSchedule.PlannedEnd.ToLocalTime():HH:mm}";
    public string SelectedResources => SelectedSchedule is null || SelectedSchedule.RequestedResources.Count == 0
        ? "未选择资源"
        : string.Join("、", SelectedSchedule.RequestedResources.Select(resource => $"{resource.ResourceType}/{resource.ResourceId}"));
    public string SelectedPlan => SelectedJob is null
        ? "-"
        : $"{SelectedJob.PlanName} / v{SelectedJob.Job.PlanVersion}";
    public string SelectedWorkflow => SelectedJob is null
        ? "-"
        : SelectedJob.WorkflowSummary;
    public bool HasSelectedActivity => SelectedActivity is not null;
    public string SelectedActivityStatus => SelectedActivity is null
        ? "尚未采集"
        : ExperimentUiText.ActivityStatus(SelectedActivity.Status);
    public string SelectedActivityResource => SelectedActivity is null
        ? "-"
        : $"{SelectedActivity.Resource.ResourceType}/{SelectedActivity.Resource.ResourceId}";
    public string SelectedActivityWindow => SelectedActivity is null
        ? "-"
        : $"{FormatActivityTime(SelectedActivity.ActualStart ?? SelectedActivity.PlannedStart)} - " +
          $"{FormatActivityTime(SelectedActivity.ActualEnd ?? SelectedActivity.PlannedEnd)}";
    public string SelectedActivityError => string.IsNullOrWhiteSpace(SelectedActivity?.LastError)
        ? "-"
        : SelectedActivity!.LastError!;
    public string SelectedParameters => SelectedJob is null || SelectedJob.Job.Parameters.Count == 0
        ? "无"
        : string.Join("；", SelectedJob.Job.Parameters.OrderBy(item => item.Key).Select(item => $"{item.Key}={item.Value ?? "<null>"}"));
    public bool HasSelectedJob => SelectedJob is not null;
    public bool HasBlockingReasons => BlockingReasons.Count > 0;
    public bool CanCreateJob => !IsBusy && HasActionMetadata &&
                                SelectedPublishedPlan is not null &&
                                !string.IsNullOrWhiteSpace(SampleBatchId);
    public bool CanSchedule => !IsBusy && HasActionMetadata &&
                               SelectedJob?.Job.Status is ExperimentJobStatus.Ready or ExperimentJobStatus.Scheduled or ExperimentJobStatus.Blocked &&
                               PlacementDate is not null && DurationMinutes is > 0 and <= 1440 && Priority is >= 0 and <= 100;
    public bool CanUnschedule => !IsBusy && HasActionMetadata &&
                                 SelectedJob?.Job.Status is ExperimentJobStatus.Scheduled or ExperimentJobStatus.Blocked;
    public bool CanCancelJob => !IsBusy && HasActionMetadata &&
                                SelectedJob?.Job.Status is ExperimentJobStatus.Draft or ExperimentJobStatus.Ready or ExperimentJobStatus.Scheduled or ExperimentJobStatus.Blocked;
    public bool CanAdmit => !IsBusy && HasActionMetadata &&
                            SelectedJob?.Job.Status == ExperimentJobStatus.Scheduled &&
                            SelectedSchedule?.Status == ScheduleEntryStatus.Scheduled;
    private bool HasActionMetadata =>
        !string.IsNullOrWhiteSpace(OperatorName) && !string.IsNullOrWhiteSpace(Reason);

    public Task EnsureLoadedAsync() => _hasLoaded ? Task.CompletedTask : RefreshAsync();

    public Task RefreshAsync(Guid? preferredJobId = null) => RunOperationAsync(
        "正在刷新任务与资源窗口...",
        async () =>
        {
            await RefreshCoreAsync(preferredJobId);
            StatusMessage = $"已加载 {TaskPool.Count} 项待处理任务和 {ResourceLanes.Count} 条资源泳道。";
        });

    public Task CreateJobAsync() => RunOperationAsync(
        "正在创建实验任务...",
        async () =>
        {
            var plan = SelectedPublishedPlan?.Plan ?? throw new InvalidOperationException("请选择已发布方案版本。");
            var parameters = BuildJobParameters();
            var job = await _mes.CreateExperimentJobAsync(
                new CreateExperimentJobRequest
                {
                    RequestId = Guid.NewGuid(),
                    Actor = OperatorName.Trim(),
                    Reason = Reason.Trim(),
                    PlanId = plan.PlanId,
                    PlanVersion = plan.Version,
                    SampleBatchId = SampleBatchId.Trim(),
                    SampleId = NormalizeOptional(SampleId),
                    Parameters = parameters
                },
                _shutdown.Token);
            SampleBatchId = string.Empty;
            SampleId = string.Empty;
            ClearJobParameters();
            await RefreshCoreAsync(job.JobId);
            StatusMessage = $"实验任务 {job.JobId:N} 已进入待排任务池。";
        });

    public Task ScheduleAsync() => RunOperationAsync(
        "正在提交人工排程...",
        async () =>
        {
            var job = SelectedJob?.Job ?? throw new InvalidOperationException("请选择待排任务。");
            var (start, end) = GetPlacementWindow();
            var schedule = await _mes.ScheduleExperimentJobAsync(
                job.JobId,
                new ScheduleExperimentJobRequest
                {
                    RequestId = Guid.NewGuid(),
                    Actor = OperatorName.Trim(),
                    Reason = Reason.Trim(),
                    PlannedStart = start,
                    PlannedEnd = end,
                    Priority = Priority,
                    Resources = Resources
                        .Where(resource => resource.IsSelected)
                        .Select(resource => resource.Resource)
                        .ToArray()
                },
                _shutdown.Token);
            await RefreshCoreAsync(job.JobId);
            StatusMessage = schedule.Status == ScheduleEntryStatus.Blocked
                ? $"排程已保存但被 {schedule.BlockingReasons.Count} 项原因阻塞。"
                : $"任务 {job.JobId.ToString("N")[..8]} 已人工排程。";
        });

    public async Task UnscheduleAsync()
    {
        var jobId = SelectedJob?.JobId;
        if (jobId is null) return;
        await RunOperationAsync(
            "正在撤销排程...",
            async () =>
            {
                await _mes.UnscheduleExperimentJobAsync(jobId.Value, CreateActionRequest(), _shutdown.Token);
                await RefreshCoreAsync(jobId);
                StatusMessage = $"任务 {jobId.Value.ToString("N")[..8]} 已返回待排任务池。";
            });
    }

    public async Task CancelJobAsync()
    {
        var selected = SelectedJob;
        if (selected is null || !_confirmation.Confirm(
                "确认取消实验任务",
                $"将取消样品批次“{selected.SampleBatchId}”的任务及其计划预留。此操作不会向设备发送命令，且不可撤销。是否继续？"))
        {
            return;
        }

        await RunOperationAsync(
            "正在取消实验任务...",
            async () =>
            {
                await _mes.CancelExperimentJobAsync(selected.JobId, CreateActionRequest(), _shutdown.Token);
                await RefreshCoreAsync(selected.JobId);
                StatusMessage = $"任务 {selected.ShortId} 已取消。";
            });
    }

    public async Task AdmitAsync()
    {
        var selected = SelectedJob;
        if (selected is null || !_confirmation.Confirm(
                "确认运行准入",
                $"将为样品批次“{selected.SampleBatchId}”创建固定工作流运行并把计划预留转换为运行租约。准入本身不会推进节点或发送设备命令。是否继续？"))
        {
            return;
        }

        await RunOperationAsync(
            "正在执行运行准入...",
            async () =>
            {
                var result = await _mes.AdmitExperimentJobAsync(
                    selected.JobId,
                    new AdmitExperimentJobRequest
                    {
                        RequestId = Guid.NewGuid(),
                        Actor = OperatorName.Trim(),
                        Reason = Reason.Trim()
                    },
                    _shutdown.Token);
                await RefreshCoreAsync(selected.JobId);
                if (result.IsRejected)
                {
                    ErrorMessage = $"[{result.RejectionCode ?? "EXP-ADMISSION-REJECTED"}] {result.RejectionReason ?? "运行准入被拒绝。"}";
                    StatusMessage = "运行准入被拒绝，未创建运行租约。";
                    return;
                }

                StatusMessage = $"任务已准入，运行 ID：{result.WorkflowRunId:N}。";
            });
    }

    private async Task RefreshCoreAsync(Guid? preferredJobId)
    {
        if (!TryGetBoardWindow(out var windowStart, out var windowEnd))
            throw new InvalidOperationException("请选择有效的排程日期与窗口。");
        var selectedJobId = preferredJobId ?? SelectedJob?.JobId;
        (Guid PlanId, int Version)? selectedPlanKey = SelectedPublishedPlan is null
            ? null
            : (SelectedPublishedPlan.PlanId, SelectedPublishedPlan.Version);

        var plansTask = LoadPublishedPlansAsync();
        var jobsTask = _mes.GetExperimentJobsAsync(null, _shutdown.Token);
        // The task pool spans dates, so retain every schedule entry for status
        // explanation and filter only the visual timeline to the selected window.
        var scheduleTask = _mes.GetExperimentScheduleAsync(null, null, _shutdown.Token);
        var availabilityTask = _mes.GetExperimentResourceAvailabilityAsync(windowStart, windowEnd, _shutdown.Token);
        var auditsTask = _mes.GetExperimentSchedulingAuditsAsync(null, null, null, 200, _shutdown.Token);
        await Task.WhenAll(plansTask, jobsTask, scheduleTask, availabilityTask, auditsTask);

        PublishedPlans.Clear();
        foreach (var plan in plansTask.Result)
            PublishedPlans.Add(new ExperimentPublishedPlanOption(plan));
        SelectedPublishedPlan = selectedPlanKey is { } key
            ? PublishedPlans.FirstOrDefault(item => item.PlanId == key.Item1 && item.Version == key.Item2) ?? PublishedPlans.FirstOrDefault()
            : PublishedPlans.FirstOrDefault();

        _snapshot = scheduleTask.Result;
        _allAudits = auditsTask.Result;
        var planNames = plansTask.Result.ToDictionary(
            plan => (plan.PlanId, plan.Version),
            plan => plan.Name);
        _allJobs.Clear();
        _allJobs.AddRange(jobsTask.Result.Select(job => new ExperimentJobItemViewModel(
            job,
            planNames.GetValueOrDefault((job.PlanId, job.PlanVersion)) ?? job.PlanId.ToString("N")[..8],
            _snapshot.Entries
                .Where(entry => entry.ExperimentJobId == job.JobId &&
                                entry.Status is not ScheduleEntryStatus.Draft and not ScheduleEntryStatus.Cancelled)
                .OrderByDescending(entry => entry.UpdatedAt)
                .FirstOrDefault())));
        TaskPool.Clear();
        foreach (var job in _allJobs.Where(item => item.IsInTaskPool)
                     .OrderBy(item => item.Job.Status == ExperimentJobStatus.Blocked ? 0 : 1)
                     .ThenByDescending(item => FindSchedule(item.JobId)?.Priority ?? 0)
                     .ThenBy(item => item.Job.CreatedAt))
        {
            TaskPool.Add(job);
        }

        ReplaceResources(availabilityTask.Result);
        RebuildTimeline(windowStart, windowEnd, availabilityTask.Result);
        SelectedJob = selectedJobId is { } id
            ? _allJobs.FirstOrDefault(item => item.JobId == id)
            : TaskPool.FirstOrDefault() ??
              _snapshot.Entries.Select(entry => _allJobs.FirstOrDefault(item => item.JobId == entry.ExperimentJobId)).FirstOrDefault(item => item is not null) ??
              _allJobs.FirstOrDefault();
        LastRefreshedAt = DateTimeOffset.Now;
        _hasLoaded = true;
        OnPropertyChanged(nameof(RefreshStatus));
    }

    private async Task<IReadOnlyList<ExperimentPlan>> LoadPublishedPlansAsync()
    {
        var latest = await _mes.GetExperimentPlansAsync(_shutdown.Token);
        var tasks = latest.Select(plan => _mes.GetExperimentPlanVersionsAsync(plan.PlanId, _shutdown.Token));
        var versions = await Task.WhenAll(tasks);
        return versions
            .SelectMany(items => items)
            .Where(plan => plan.Status == ExperimentPlanStatus.Published)
            .GroupBy(plan => (plan.PlanId, plan.Version))
            .Select(group => group.First())
            .OrderBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(plan => plan.Version)
            .ToArray();
    }

    private void ReplaceResources(IReadOnlyList<ExperimentResourceAvailability> availability)
    {
        foreach (var resource in Resources) resource.PropertyChanged -= ResourceSelectionChanged;
        Resources.Clear();
        foreach (var item in availability
                     .OrderBy(item => item.Resource.ResourceType, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var resource = new ExperimentResourceSelectionItemViewModel(item);
            resource.PropertyChanged += ResourceSelectionChanged;
            Resources.Add(resource);
        }
    }

    private void RebuildTimeline(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        IReadOnlyList<ExperimentResourceAvailability> availability)
    {
        TimelineTicks.Clear();
        foreach (var tick in ExperimentScheduleTimelineProjector.CreateTicks(windowStart, windowEnd))
            TimelineTicks.Add(tick);
        var jobs = _allJobs.ToDictionary(item => item.JobId, item => item.Job);
        ResourceLanes.Clear();
        foreach (var lane in ExperimentScheduleTimelineProjector.CreateLanes(
                     windowStart,
                     windowEnd,
                     availability,
                     _snapshot,
                     jobs))
        {
            ResourceLanes.Add(lane);
        }
    }

    private void ApplySelectedJob()
    {
        SelectedSchedule = SelectedJob is null ? null : FindSchedule(SelectedJob.JobId);
        if (SelectedActivity is null || SelectedJob is null ||
            SelectedActivity.ExperimentJobId != SelectedJob.JobId)
        {
            SelectedActivity = SelectedJob is null
                ? null
                : _snapshot.Activities
                    .Where(activity => activity.ExperimentJobId == SelectedJob.JobId)
                    .OrderByDescending(activity => activity.ActualStart ?? activity.PlannedStart)
                    .ThenByDescending(activity => activity.ActivityId)
                    .FirstOrDefault();
        }
        BlockingReasons.Clear();
        foreach (var reason in SelectedSchedule?.BlockingReasons ?? [])
            BlockingReasons.Add(new ExperimentScheduleBlockReasonItemViewModel(reason));
        SelectedJobAudits.Clear();
        if (SelectedJob is not null)
        {
            foreach (var audit in _allAudits
                         .Where(audit => audit.ExperimentJobId == SelectedJob.JobId)
                         .OrderByDescending(audit => audit.OccurredAt))
            {
                SelectedJobAudits.Add(new ExperimentSchedulingAuditItemViewModel(audit));
            }
        }

        _suppressResourceSelection = true;
        try
        {
            var selectedKeys = (SelectedSchedule?.RequestedResources ?? [])
                .Select(resource => ExperimentResourceKeys.Create(resource.ResourceType, resource.ResourceId))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var resource in Resources) resource.IsSelected = selectedKeys.Contains(resource.ResourceKey);
        }
        finally
        {
            _suppressResourceSelection = false;
        }

        if (SelectedSchedule is not null)
        {
            var localStart = SelectedSchedule.PlannedStart.ToLocalTime();
            PlacementDate = localStart.Date;
            PlacementStartHour = localStart.Hour;
            PlacementStartMinute = localStart.Minute;
            DurationMinutes = Math.Max(1, (int)Math.Round((SelectedSchedule.PlannedEnd - SelectedSchedule.PlannedStart).TotalMinutes));
            Priority = SelectedSchedule.Priority;
        }
        else
        {
            PlacementDate = BoardDate ?? DateTime.Today;
        }

        OnPropertyChanged(nameof(SelectedPlan));
        OnPropertyChanged(nameof(SelectedWorkflow));
        OnPropertyChanged(nameof(SelectedParameters));
        OnPropertyChanged(nameof(HasSelectedJob));
        OnPropertyChanged(nameof(HasBlockingReasons));
        RaiseCommandStates();
    }

    private ScheduleEntry? FindSchedule(Guid jobId) => _snapshot.Entries
        .Where(entry => entry.ExperimentJobId == jobId &&
                        entry.Status is not ScheduleEntryStatus.Draft and not ScheduleEntryStatus.Cancelled)
        .OrderByDescending(entry => entry.UpdatedAt)
        .FirstOrDefault();

    private void SelectScheduleBlock(ExperimentScheduleBlockViewModel? block)
    {
        SelectedActivity = block?.ActivityId is { } activityId
            ? _snapshot.Activities.FirstOrDefault(activity => activity.ActivityId == activityId)
            : null;
        if (block?.JobId is { } jobId)
        {
            SelectedJob = _allJobs.FirstOrDefault(item => item.JobId == jobId);
            return;
        }
        if (block?.WorkflowRunId is { } runId)
            StatusMessage = $"活动运行租约属于流程运行 {runId:N}。";
    }

    private void AddJobParameter()
    {
        var parameter = new ExperimentParameterEditorViewModel();
        JobParameters.Add(parameter);
    }

    private void RemoveJobParameter(ExperimentParameterEditorViewModel? parameter)
    {
        if (parameter is not null) JobParameters.Remove(parameter);
    }

    private void ClearJobParameters() => JobParameters.Clear();

    private IReadOnlyDictionary<string, string?> BuildJobParameters()
    {
        var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in JobParameters)
        {
            var key = parameter.Name.Trim();
            if (key.Length == 0) throw new InvalidOperationException("任务参数名称不能为空。");
            if (!parameters.TryAdd(key, NormalizeOptional(parameter.Value)))
                throw new InvalidOperationException($"任务参数“{key}”重复。");
        }
        return parameters;
    }

    private void ResourceSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressResourceSelection || e.PropertyName != nameof(ExperimentResourceSelectionItemViewModel.IsSelected)) return;
        RaiseCommandStates();
    }

    private bool TryGetBoardWindow(out DateTimeOffset start, out DateTimeOffset end)
    {
        if (BoardDate is not { } date || WindowHours <= 0)
        {
            start = default;
            end = default;
            return false;
        }
        start = CreateLocalTime(date, WindowStartHour, 0);
        end = start.AddHours(WindowHours);
        return true;
    }

    private (DateTimeOffset Start, DateTimeOffset End) GetPlacementWindow()
    {
        if (PlacementDate is not { } date || DurationMinutes is <= 0 or > 1440)
            throw new InvalidOperationException("请选择有效的计划日期与预计时长。");
        var start = CreateLocalTime(date, PlacementStartHour, PlacementStartMinute);
        return (start, start.AddMinutes(DurationMinutes));
    }

    private static DateTimeOffset CreateLocalTime(DateTime date, int hour, int minute)
    {
        var local = DateTime.SpecifyKind(date.Date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private ExperimentSchedulingActionRequest CreateActionRequest() => new()
    {
        RequestId = Guid.NewGuid(),
        Actor = OperatorName.Trim(),
        Reason = Reason.Trim()
    };

    private async Task RunOperationAsync(string runningMessage, Func<Task> operation)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = runningMessage;
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusMessage = "排程操作失败。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanCreateJob));
        OnPropertyChanged(nameof(CanSchedule));
        OnPropertyChanged(nameof(CanUnschedule));
        OnPropertyChanged(nameof(CanCancelJob));
        OnPropertyChanged(nameof(CanAdmit));
        _refreshCommand.RaiseCanExecuteChanged();
        _createJobCommand.RaiseCanExecuteChanged();
        _scheduleCommand.RaiseCanExecuteChanged();
        _unscheduleCommand.RaiseCanExecuteChanged();
        _cancelJobCommand.RaiseCanExecuteChanged();
        _admitCommand.RaiseCanExecuteChanged();
        _addJobParameterCommand.RaiseCanExecuteChanged();
        _removeJobParameterCommand.RaiseCanExecuteChanged();
    }

    private static string? NormalizeOptional(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatActivityTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("MM-dd HH:mm:ss");

    public void Dispose()
    {
        _shutdown.Cancel();
        foreach (var resource in Resources) resource.PropertyChanged -= ResourceSelectionChanged;
        _shutdown.Dispose();
    }
}
