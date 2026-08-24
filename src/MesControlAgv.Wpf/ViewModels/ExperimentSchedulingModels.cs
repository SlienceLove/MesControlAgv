using MesControlAgv.Contracts.Experiments;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class ExperimentPublishedPlanOption(ExperimentPlan plan)
{
    public ExperimentPlan Plan { get; } = plan;
    public Guid PlanId => Plan.PlanId;
    public int Version => Plan.Version;
    public string Name => Plan.Name;
    public string Display => $"{Plan.Name} / v{Plan.Version}";
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
}

public sealed class ExperimentResourceLaneViewModel
{
    public const double LaneHeight = 60;

    public string ResourceKey { get; init; } = string.Empty;
    public string ResourceId { get; init; } = string.Empty;
    public string ResourceType { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Load { get; init; } = string.Empty;
    public bool HasActiveLease { get; init; }
    public IReadOnlyList<ExperimentScheduleBlockViewModel> Blocks { get; init; } = [];
    public double Height => LaneHeight;
}

public static class ExperimentScheduleTimelineProjector
{
    public const double TimelineWidth = 960;
    private const string UnassignedKey = "UNASSIGNED";

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
        foreach (var entry in snapshot.Entries.Where(entry =>
                     Overlaps(entry.PlannedStart, entry.PlannedEnd, windowStart, windowEnd)))
        {
            foreach (var resource in entry.RequestedResources)
            {
                var key = ExperimentResourceKeys.Create(resource.ResourceType, resource.ResourceId);
                laneSources.TryAdd(key, new LaneSource(resource, resource.ResourceId, null));
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
                .Select(resource => ExperimentResourceKeys.Create(resource.ResourceType, resource.ResourceId))
                .ToArray();
            if (entryKeys.Length == 0 ? resourceKey != UnassignedKey : !entryKeys.Contains(resourceKey, StringComparer.Ordinal))
                continue;
            jobs.TryGetValue(entry.ExperimentJobId, out var job);
            var style = ScheduleStyle(entry.Status);
            blocks.Add(CreateBlock(
                entry.PlannedStart,
                entry.PlannedEnd,
                windowStart,
                windowEnd,
                job?.SampleBatchId ?? entry.ExperimentJobId.ToString("N")[..8],
                $"{ExperimentUiText.ScheduleStatus(entry.Status)} / 优先级 {entry.Priority}",
                style.Fill,
                style.Border,
                jobId: entry.ExperimentJobId,
                scheduleEntryId: entry.ScheduleEntryId));
        }

        foreach (var lease in snapshot.ActiveLeases.Where(lease =>
                     lease.Status == ResourceLeaseStatus.Active &&
                     ExperimentResourceKeys.Create(lease.Resource.ResourceType, lease.Resource.ResourceId) == resourceKey &&
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
                isRuntimeLease: true));
        }

        AssignTracks(blocks);
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
        bool isRuntimeLease = false)
    {
        var clippedStart = startsAt < windowStart ? windowStart : startsAt;
        var clippedEnd = endsAt > windowEnd ? windowEnd : endsAt;
        var range = (windowEnd - windowStart).TotalMinutes;
        var left = (clippedStart - windowStart).TotalMinutes / range * TimelineWidth;
        var width = Math.Max(12, (clippedEnd - clippedStart).TotalMinutes / range * TimelineWidth);
        return new ExperimentScheduleBlockViewModel
        {
            JobId = jobId,
            ScheduleEntryId = scheduleEntryId,
            WorkflowRunId = workflowRunId,
            Label = label,
            Detail = detail,
            Fill = fill,
            Border = border,
            Left = Math.Clamp(left, 0, TimelineWidth),
            Width = Math.Min(width, TimelineWidth - Math.Clamp(left, 0, TimelineWidth)),
            IsRuntimeLease = isRuntimeLease
        };
    }

    private static void AssignTracks(List<ExperimentScheduleBlockViewModel> blocks)
    {
        var trackEnds = new[] { -1d, -1d };
        foreach (var block in blocks.OrderBy(item => item.Left).ThenBy(item => item.IsRuntimeLease))
        {
            var track = block.Left >= trackEnds[0] + 2
                ? 0
                : block.Left >= trackEnds[1] + 2
                    ? 1
                    : trackEnds[0] <= trackEnds[1] ? 0 : 1;
            block.Top = 5 + track * 26;
            trackEnds[track] = Math.Max(trackEnds[track], block.Left + block.Width);
        }
    }

    private static bool Overlaps(
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd) =>
        endsAt > windowStart && startsAt < windowEnd;

    private static (string Fill, string Border) ScheduleStyle(ScheduleEntryStatus status) => status switch
    {
        ScheduleEntryStatus.Blocked => ("#FEE4E2", "#B42318"),
        ScheduleEntryStatus.Admitted => ("#D1FADF", "#027A48"),
        ScheduleEntryStatus.Completed => ("#E8F5E9", "#2E7D32"),
        _ => ("#DCEBFF", "#1769AA")
    };

    private sealed record LaneSource(
        ExperimentResourceReference Resource,
        string DisplayName,
        ExperimentResourceAvailability? Availability)
    {
        public static LaneSource Unassigned { get; } = new(
            new ExperimentResourceReference { ResourceType = "unassigned", ResourceId = "UNASSIGNED" },
            "未分配资源",
            null);
    }
}
