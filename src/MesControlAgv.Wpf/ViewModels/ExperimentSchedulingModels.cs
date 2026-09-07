using MesControlAgv.Contracts.Experiments;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class ExperimentPublishedPlanOption(ExperimentPlan plan)
{
    public ExperimentPlan Plan { get; } = plan;
    public Guid PlanId => Plan.PlanId;
    public int Version => Plan.Version;
    public string Name => Plan.Name;
    public string Display => $"{Plan.Name} / v{Plan.Version}";
    public string WorkflowSummary => Plan.WorkflowSteps.Count == 0
        ? "未配置流程模板"
        : string.Join(" → ", Plan.WorkflowSteps
            .OrderBy(step => step.Order)
            .Select(step => string.IsNullOrWhiteSpace(step.Name)
                ? $"流程 v{step.WorkflowVersion}"
                : step.Name));
}

public sealed class ExperimentJobItemViewModel(ExperimentJob job, string planName, ScheduleEntry? schedule = null)
{
    public ExperimentJob Job { get; } = job;
    public ScheduleEntry? Schedule { get; } = schedule;
    public Guid JobId => Job.JobId;
    public string ShortId => Job.JobId.ToString("N")[..8];
    public string PlanName { get; } = planName;
    public string PlanVersion => $"v{Job.PlanVersion}";
    public string SampleBatchId => Job.SampleBatchId;
    public string SampleId => Job.SampleId ?? "-";
    public string Status => ExperimentUiText.JobStatus(Job.Status);
    public int? Priority => Schedule?.Priority;
    public string PriorityText => Priority?.ToString() ?? "-";
    public string CreatedAt => Job.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public string LastError => Job.LastError ?? string.Empty;
    public string WorkflowSummary => Job.WorkflowSteps.Count == 0
        ? $"{Job.WorkflowId:N} / v{Job.WorkflowVersion}"
        : string.Join(" → ", Job.WorkflowSteps
            .OrderBy(step => step.Order)
            .Select(step => string.IsNullOrWhiteSpace(step.Name)
                ? $"流程 v{step.WorkflowVersion}"
                : step.Name));
    public bool IsInTaskPool => Job.Status is ExperimentJobStatus.Ready or ExperimentJobStatus.Blocked;
}

public sealed class ExperimentResourceSelectionItemViewModel(
    ExperimentResourceAvailability availability) : ExperimentBindableObject
{
    private bool _isSelected;

    public ExperimentResourceAvailability Availability { get; } = availability;
    public ExperimentResourceReference Resource => Availability.Resource;
    public string ResourceKey => ExperimentResourceKeys.Create(Resource.ResourceType, Resource.ResourceId);
    public string DisplayName => string.IsNullOrWhiteSpace(Availability.DisplayName)
        ? Resource.ResourceId
        : Availability.DisplayName;
    public string Type => ExperimentUiText.ResourceType(Resource.ResourceType);
    public bool Enabled => Availability.Enabled;
    public int Capacity => Availability.Capacity;
    public int PlannedCount => Availability.PlannedReservationCount;
    public int AvailableCapacity => Availability.AvailableCapacity;
    public bool HasActiveLease => Availability.HasActiveLease;
    public string Load => $"计划 {PlannedCount}/{Capacity} / 可用 {AvailableCapacity}";
    public string State => !Enabled
        ? "已禁用"
        : HasActiveLease
            ? "运行占用"
            : AvailableCapacity <= 0
                ? "计划已满"
                : "可选择";
    public string BlockingReasons => string.Join("；", Availability.BlockingReasons.Select(reason => $"[{reason.Code}] {reason.Message}"));
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
}

public sealed class ExperimentScheduleBlockReasonItemViewModel(ScheduleBlockReason reason)
{
    public ScheduleBlockReason Reason { get; } = reason;
    public string Code => Reason.Code;
    public string Message => Reason.Message;
    public string Resource => Reason.Resource is null
        ? "-"
        : $"{Reason.Resource.ResourceType}/{Reason.Resource.ResourceId}";
    public string Conflicts => Reason.ConflictingScheduleEntryIds.Count == 0
        ? "-"
        : string.Join(", ", Reason.ConflictingScheduleEntryIds.Select(id => id.ToString("N")[..8]));
}

public sealed class ExperimentSchedulingAuditItemViewModel(ExperimentSchedulingAuditEntry audit)
{
    public ExperimentSchedulingAuditEntry Audit { get; } = audit;
    public string OccurredAt => Audit.OccurredAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string EventType => Audit.EventType;
    public string Outcome => Audit.Outcome;
    public string Code => Audit.Code ?? "-";
    public string Actor => Audit.Actor;
    public string Reason => Audit.Reason;
}

public sealed record ExperimentTimelineTickViewModel(string Label, double Left);

public sealed class ExperimentScheduleBlockViewModel
{
    public Guid? JobId { get; init; }
    public Guid? ScheduleEntryId { get; init; }
    public Guid? WorkflowRunId { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Fill { get; init; } = "#DCEBFF";
    public string Border { get; init; } = "#1769AA";
    public double Left { get; init; }
    public double Width { get; init; }
    public double Top { get; set; }
    public double Height { get; init; } = 22;
    public bool IsRuntimeLease { get; init; }
    public Guid? WorkflowStepId { get; init; }
    public int? WorkflowStepOrder { get; init; }
    public int WorkflowStepCount { get; init; }
    public string WorkflowStepName { get; init; } = string.Empty;
    public Guid? ActivityId { get; init; }
    public bool IsRuntimeActivity { get; init; }
    public bool IsCurrent { get; init; }
    public double BorderThickness => IsCurrent ? 2 : 1;
    public string LayerDisplay => IsRuntimeActivity
        ? IsCurrent ? "当前实际" : "实际设备活动"
        : IsRuntimeLease
            ? IsCurrent ? "当前租约" : "运行租约"
            : IsCurrent ? "当前计划" : "计划预留";
}

public sealed class ExperimentResourceLaneViewModel
{
    public const double LaneHeight = 60;
    private const double TrackHeight = 26;
    private const double LanePadding = 8;

    public string ResourceKey { get; init; } = string.Empty;
    public string ResourceId { get; init; } = string.Empty;
    public string ResourceType { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Load { get; init; } = string.Empty;
    public bool HasActiveLease { get; init; }
    public int TrackCount { get; init; }
    public IReadOnlyList<ExperimentScheduleBlockViewModel> Blocks { get; init; } = [];
    public double Height => Math.Max(LaneHeight, LanePadding + Math.Max(1, TrackCount) * TrackHeight);
}

public static class ExperimentScheduleTimelineProjector
{
    public const double TimelineWidth = 960;
    private const string UnassignedResourceType = "unassigned";
    private const string UnassignedResourceId = "UNASSIGNED";
    private static readonly string UnassignedKey =
        ExperimentResourceKeys.Create(UnassignedResourceType, UnassignedResourceId);

    public static IReadOnlyList<ExperimentTimelineTickViewModel> CreateTicks(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var hours = (windowEnd - windowStart).TotalHours;
        if (hours <= 0) return [];
        var interval = hours <= 12 ? 1 : hours <= 24 ? 2 : 4;
        var ticks = new List<ExperimentTimelineTickViewModel>();
        for (var elapsed = 0d; elapsed <= hours + 0.001; elapsed += interval)
        {
            var time = windowStart.AddHours(Math.Min(elapsed, hours));
            ticks.Add(new ExperimentTimelineTickViewModel(
                time.ToLocalTime().ToString("HH:mm"),
                Math.Min(TimelineWidth, elapsed / hours * TimelineWidth)));
        }
        if (ticks.Count == 0 || ticks[^1].Left < TimelineWidth - 0.1)
            ticks.Add(new ExperimentTimelineTickViewModel(windowEnd.ToLocalTime().ToString("HH:mm"), TimelineWidth));
        return ticks;
    }

    public static IReadOnlyList<ExperimentResourceLaneViewModel> CreateLanes(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        IReadOnlyList<ExperimentResourceAvailability> availability,
        ExperimentScheduleSnapshot snapshot,
        IReadOnlyDictionary<Guid, ExperimentJob> jobs)
    {
        var laneSources = new Dictionary<string, LaneSource>(StringComparer.Ordinal);
        foreach (var item in availability)
        {
            var key = ExperimentResourceKeys.Create(item.Resource.ResourceType, item.Resource.ResourceId);
            laneSources[key] = new LaneSource(item.Resource, item.DisplayName, item);
        }
        foreach (var activity in snapshot.Activities)
        {
            var resource = NormalizeResource(activity.Resource);
            var key = ExperimentResourceKeys.Create(resource.ResourceType, resource.ResourceId);
            laneSources.TryAdd(key, new LaneSource(resource, resource.ResourceId, null));
        }
        foreach (var entry in snapshot.Entries.Where(entry =>
                     Overlaps(entry.PlannedStart, entry.PlannedEnd, windowStart, windowEnd)))
        {
            foreach (var resource in entry.RequestedResources)
            {
                var normalizedResource = NormalizeResource(resource);
                var key = ExperimentResourceKeys.Create(
                    normalizedResource.ResourceType,
                    normalizedResource.ResourceId);
                laneSources.TryAdd(key, new LaneSource(normalizedResource, normalizedResource.ResourceId, null));
            }
            if (entry.RequestedResources.Count == 0 && Overlaps(entry.PlannedStart, entry.PlannedEnd, windowStart, windowEnd))
                laneSources.TryAdd(UnassignedKey, LaneSource.Unassigned);
        }
        foreach (var lease in snapshot.ActiveLeases)
        {
            var key = ExperimentResourceKeys.Create(lease.Resource.ResourceType, lease.Resource.ResourceId);
            laneSources.TryAdd(key, new LaneSource(lease.Resource, lease.Resource.ResourceId, null));
        }

        return laneSources
            .OrderBy(item => item.Key == UnassignedKey ? 1 : 0)
            .ThenBy(item => item.Value.Resource.ResourceType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(item => BuildLane(item.Key, item.Value, windowStart, windowEnd, snapshot, jobs))
            .ToArray();
    }

    private static ExperimentResourceLaneViewModel BuildLane(
        string resourceKey,
        LaneSource source,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        ExperimentScheduleSnapshot snapshot,
        IReadOnlyDictionary<Guid, ExperimentJob> jobs)
    {
        var blocks = new List<ExperimentScheduleBlockViewModel>();
        foreach (var entry in snapshot.Entries.Where(entry =>
                     entry.Status is not ScheduleEntryStatus.Draft and not ScheduleEntryStatus.Cancelled &&
                     Overlaps(entry.PlannedStart, entry.PlannedEnd, windowStart, windowEnd)))
        {
            var entryKeys = entry.RequestedResources
                .Select(GetResourceKey)
                .ToArray();
            if (entryKeys.Length == 0 ? resourceKey != UnassignedKey : !entryKeys.Contains(resourceKey, StringComparer.Ordinal))
                continue;
            jobs.TryGetValue(entry.ExperimentJobId, out var job);
            var style = ScheduleStyle(entry.Status);
            foreach (var segment in CreateWorkflowSegments(entry, job))
            {
                blocks.Add(CreateBlock(
                    segment.StartsAt,
                    segment.EndsAt,
                    windowStart,
                    windowEnd,
                    segment.Label,
                    $"{ExperimentUiText.ScheduleStatus(entry.Status)} / 优先级 {entry.Priority} / {segment.Detail}",
                     style.Fill,
                     style.Border,
                     jobId: entry.ExperimentJobId,
                     scheduleEntryId: entry.ScheduleEntryId,
                     workflowStepId: segment.StepId,
                     workflowStepOrder: segment.StepOrder,
                     workflowStepCount: segment.StepCount,
                     workflowStepName: segment.StepName,
                     isCurrent: IsCurrentPlanBlock(entry, job, segment, snapshot.GeneratedAt)));
            }
        }

        foreach (var lease in snapshot.ActiveLeases.Where(lease =>
                     lease.Status == ResourceLeaseStatus.Active &&
                     GetResourceKey(lease.Resource) == resourceKey &&
                     lease.AcquiredAt < windowEnd))
        {
            var linkedEntry = lease.ScheduleEntryId is { } scheduleEntryId
                ? snapshot.Entries.FirstOrDefault(entry => entry.ScheduleEntryId == scheduleEntryId)
                : null;
            blocks.Add(CreateBlock(
                lease.AcquiredAt,
                windowEnd,
                windowStart,
                windowEnd,
                "活动运行租约",
                $"运行 {lease.WorkflowRunId:N} / 获取 {lease.AcquiredAt.ToLocalTime():HH:mm}",
                "#FEF0C7",
                "#B54708",
                 jobId: linkedEntry?.ExperimentJobId,
                 workflowRunId: lease.WorkflowRunId,
                 isRuntimeLease: true,
                 isCurrent: IsCurrentLease(lease, snapshot.GeneratedAt)));
        }

        foreach (var activity in snapshot.Activities.Where(activity =>
                     GetResourceKey(activity.Resource) == resourceKey))
        {
            var startsAt = activity.ActualStart ?? activity.PlannedStart;
            var endsAt = ResolveActivityEnd(activity, startsAt, snapshot.GeneratedAt);
            if (!Overlaps(startsAt, endsAt, windowStart, windowEnd)) continue;
            var style = ActivityStyle(activity.Status);
            blocks.Add(CreateBlock(
                startsAt,
                endsAt,
                windowStart,
                windowEnd,
                activity.ActivityName,
                $"实际 {activity.Status} / 任务 {activity.ExperimentJobId:N}" +
                (string.IsNullOrWhiteSpace(activity.LastError) ? string.Empty : $" / {activity.LastError}"),
                style.Fill,
                style.Border,
                 jobId: activity.ExperimentJobId,
                 scheduleEntryId: activity.ScheduleEntryId,
                  workflowRunId: activity.WorkflowRunId,
                  workflowStepId: activity.WorkflowStepId,
                  activityId: activity.ActivityId,
                 isRuntimeActivity: true,
                 isCurrent: IsCurrentActivity(activity, snapshot.GeneratedAt)));
        }

        var trackCount = AssignTracks(blocks);
        var availability = source.Availability;
        return new ExperimentResourceLaneViewModel
        {
            ResourceKey = resourceKey,
            ResourceId = source.Resource.ResourceId,
            ResourceType = source.Resource.ResourceType,
            DisplayName = source.DisplayName,
            Load = availability is null
                ? resourceKey == UnassignedKey ? "未选择资源" : "未在当前 Profile 目录"
                : $"计划 {availability.PlannedReservationCount}/{availability.Capacity} / 可用 {availability.AvailableCapacity}",
            HasActiveLease = availability?.HasActiveLease == true || blocks.Any(block => block.IsRuntimeLease),
            TrackCount = trackCount,
            Blocks = blocks.OrderBy(block => block.Left).ThenBy(block => block.IsRuntimeLease).ToArray()
        };
    }

    private static ExperimentScheduleBlockViewModel CreateBlock(
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        string label,
        string detail,
        string fill,
        string border,
        Guid? jobId = null,
        Guid? scheduleEntryId = null,
        Guid? workflowRunId = null,
        bool isRuntimeLease = false,
        Guid? workflowStepId = null,
        int? workflowStepOrder = null,
        int workflowStepCount = 0,
        string? workflowStepName = null,
        Guid? activityId = null,
        bool isRuntimeActivity = false,
        bool isCurrent = false)
    {
        var clippedStart = startsAt < windowStart ? windowStart : startsAt;
        var clippedEnd = endsAt > windowEnd ? windowEnd : endsAt;
        var range = (windowEnd - windowStart).TotalMinutes;
        var left = (clippedStart - windowStart).TotalMinutes / range * TimelineWidth;
        var width = Math.Max(12, (clippedEnd - clippedStart).TotalMinutes / range * TimelineWidth);
        var layerDisplay = isRuntimeActivity
            ? isCurrent ? "当前实际" : "实际设备活动"
            : isRuntimeLease
                ? isCurrent ? "当前租约" : "运行租约"
                : isCurrent ? "当前计划" : "计划预留";
        return new ExperimentScheduleBlockViewModel
        {
            JobId = jobId,
            ScheduleEntryId = scheduleEntryId,
            WorkflowRunId = workflowRunId,
            WorkflowStepId = workflowStepId,
            Label = label,
            Detail = $"{layerDisplay} / {detail}",
            Fill = fill,
            Border = border,
            Left = Math.Clamp(left, 0, TimelineWidth),
            Width = Math.Min(width, TimelineWidth - Math.Clamp(left, 0, TimelineWidth)),
            WorkflowStepOrder = workflowStepOrder,
            WorkflowStepCount = workflowStepCount,
            WorkflowStepName = workflowStepName ?? string.Empty,
            ActivityId = activityId,
            IsRuntimeActivity = isRuntimeActivity,
            IsRuntimeLease = isRuntimeLease,
            IsCurrent = isCurrent
        };
    }

    private static bool IsCurrentPlanBlock(
        ScheduleEntry entry,
        ExperimentJob? job,
        WorkflowTimelineSegment segment,
        DateTimeOffset generatedAt)
    {
        if (entry.Status != ScheduleEntryStatus.Admitted &&
            job?.Status is not (ExperimentJobStatus.Admitted or ExperimentJobStatus.Running))
            return false;
        var observedAt = generatedAt == default ? DateTimeOffset.UtcNow : generatedAt;
        return segment.StartsAt <= observedAt && segment.EndsAt > observedAt;
    }

    private static bool IsCurrentLease(ResourceLease lease, DateTimeOffset generatedAt)
    {
        if (lease.Status != ResourceLeaseStatus.Active) return false;
        var observedAt = generatedAt == default ? DateTimeOffset.UtcNow : generatedAt;
        return lease.AcquiredAt <= observedAt && lease.ExpiresAt > observedAt;
    }

    private static bool IsCurrentActivity(ExperimentScheduleActivity activity, DateTimeOffset generatedAt)
    {
        var status = activity.Status?.Trim().ToLowerInvariant();
        if (status is not ("prepared" or "accepted" or "running")) return false;
        var observedAt = generatedAt == default ? DateTimeOffset.UtcNow : generatedAt;
        var startsAt = activity.ActualStart ?? activity.PlannedStart;
        var endsAt = activity.ActualEnd ?? observedAt;
        return startsAt <= observedAt && endsAt >= observedAt;
    }

    private static IReadOnlyList<WorkflowTimelineSegment> CreateWorkflowSegments(
        ScheduleEntry entry,
        ExperimentJob? job)
    {
        var steps = job?.WorkflowSteps
            .OrderBy(step => step.Order)
            .ToArray() ?? [];
        if (steps.Length <= 1 || steps.Any(step => step.EstimatedDurationMinutes <= 0))
        {
            var summary = steps.Length == 0
                ? "工作流模板"
                : steps.Length == 1
                    ? FormatStep(steps[0], 1, 1)
                    : $"{steps.Length} 个流程步骤（预计时长待补充）";
            var name = steps.Length == 1 ? GetStepName(steps[0]) : summary;
            return
            [
                new WorkflowTimelineSegment(
                    entry.PlannedStart,
                    entry.PlannedEnd,
                    job?.SampleBatchId ?? entry.ExperimentJobId.ToString("N")[..8],
                    summary,
                    steps.Length == 1 ? steps[0].Order : null,
                    steps.Length == 1 ? 1 : steps.Length,
                    name,
                    steps.Length == 1 ? steps[0].StepId : null)
            ];
        }

        var totalEstimatedMinutes = steps.Sum(step => step.EstimatedDurationMinutes);
        var plannedMinutes = Math.Max(1, (entry.PlannedEnd - entry.PlannedStart).TotalMinutes);
        var current = entry.PlannedStart;
        var segments = new List<WorkflowTimelineSegment>(steps.Length);
        for (var index = 0; index < steps.Length; index++)
        {
            var step = steps[index];
            var isLast = index == steps.Length - 1;
            var duration = isLast
                ? entry.PlannedEnd - current
                : TimeSpan.FromMinutes(plannedMinutes * step.EstimatedDurationMinutes / totalEstimatedMinutes);
            var end = isLast ? entry.PlannedEnd : current + duration;
            segments.Add(new WorkflowTimelineSegment(
                current,
                end,
                job?.SampleBatchId ?? entry.ExperimentJobId.ToString("N")[..8],
                FormatStep(step, index + 1, steps.Length),
                step.Order,
                steps.Length,
                GetStepName(step),
                step.StepId));
            current = end;
        }
        return segments;
    }

    private static string FormatStep(ExperimentPlanWorkflowStep step, int position, int count) =>
        $"步骤 {position}/{count} · {GetStepName(step)}";

    private static string GetStepName(ExperimentPlanWorkflowStep step) =>
        string.IsNullOrWhiteSpace(step.Name)
            ? $"流程 v{step.WorkflowVersion}"
            : step.Name;

    private static int AssignTracks(List<ExperimentScheduleBlockViewModel> blocks)
    {
        var trackEnds = new List<double>();
        foreach (var block in blocks.OrderBy(item => item.Left).ThenBy(item => item.IsRuntimeLease))
        {
            var track = -1;
            for (var index = 0; index < trackEnds.Count; index++)
            {
                if (block.Left >= trackEnds[index] + 2)
                {
                    track = index;
                    break;
                }
            }
            if (track < 0)
            {
                track = trackEnds.Count;
                trackEnds.Add(-1);
            }
            block.Top = 5 + track * 26;
            trackEnds[track] = Math.Max(trackEnds[track], block.Left + block.Width);
        }
        return trackEnds.Count;
    }

    private static bool Overlaps(
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd) =>
        endsAt > windowStart && startsAt < windowEnd;

    private static ExperimentResourceReference NormalizeResource(ExperimentResourceReference? resource) =>
        resource is not null &&
        !string.IsNullOrWhiteSpace(resource.ResourceType) &&
        !string.IsNullOrWhiteSpace(resource.ResourceId)
            ? resource
            : LaneSource.Unassigned.Resource;

    private static string GetResourceKey(ExperimentResourceReference? resource)
    {
        var normalized = NormalizeResource(resource);
        return ExperimentResourceKeys.Create(normalized.ResourceType, normalized.ResourceId);
    }

    private static DateTimeOffset ResolveActivityEnd(
        ExperimentScheduleActivity activity,
        DateTimeOffset startsAt,
        DateTimeOffset generatedAt)
    {
        if (activity.ActualEnd is { } actualEnd && actualEnd > startsAt)
            return actualEnd;

        var observedAt = generatedAt == default ? DateTimeOffset.UtcNow : generatedAt;
        return observedAt > startsAt ? observedAt : startsAt.AddMinutes(1);
    }

    private static (string Fill, string Border) ScheduleStyle(ScheduleEntryStatus status) => status switch
    {
        ScheduleEntryStatus.Blocked => ("#FEE4E2", "#B42318"),
        ScheduleEntryStatus.Admitted => ("#D1FADF", "#027A48"),
        ScheduleEntryStatus.Completed => ("#E8F5E9", "#2E7D32"),
        _ => ("#DCEBFF", "#1769AA")
    };

    private static (string Fill, string Border) ActivityStyle(string status) =>
        status.Trim().ToLowerInvariant() switch
        {
            "running" => ("#E0F2FE", "#0369A1"),
            "succeeded" => ("#D1FADF", "#027A48"),
            "failed" or "timedout" => ("#FEE4E2", "#B42318"),
            "unknown" => ("#FCE7F3", "#A30D5D"),
            _ => ("#E0F2FE", "#0369A1")
        };

    private sealed record LaneSource(
        ExperimentResourceReference Resource,
        string DisplayName,
        ExperimentResourceAvailability? Availability)
    {
        public static LaneSource Unassigned { get; } = new(
            new ExperimentResourceReference { ResourceType = UnassignedResourceType, ResourceId = UnassignedResourceId },
            "未分配资源",
            null);
    }

    private sealed record WorkflowTimelineSegment(
        DateTimeOffset StartsAt,
        DateTimeOffset EndsAt,
        string LabelPrefix,
        string Detail,
        int? StepOrder,
        int StepCount,
        string StepName,
        Guid? StepId)
    {
        public string Label => $"{LabelPrefix} · {StepName}";
    }
}
