namespace MesControlAgv.Domain;

public sealed record AgvCandidate(
    string AgvId,
    bool Online,
    string ControlOwner,
    string CurrentStationId,
    bool Busy);

public sealed record ScheduledRoute(
    Guid TaskId,
    string AgvId,
    PlannedPath Path,
    DateTime ScheduledAt);

public sealed record SchedulingDecision(
    bool Assigned,
    string? AgvId,
    PlannedPath? Path,
    string? Reason)
{
    public static SchedulingDecision Rejected(string reason) => new(false, null, null, reason);
}

public sealed class MultiAgvScheduler(PathPlanner planner)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ScheduledRoute> _routes = [];

    public IReadOnlyCollection<ScheduledRoute> ActiveRoutes
    {
        get
        {
            lock (_gate) return _routes.Values.ToArray();
        }
    }

    public SchedulingDecision Schedule(
        Guid taskId,
        string sourceStationId,
        string targetStationId,
        IReadOnlyCollection<AgvCandidate> candidates)
    {
        lock (_gate)
        {
            if (_routes.TryGetValue(taskId, out var existing))
            {
                return new SchedulingDecision(true, existing.AgvId, existing.Path, null);
            }

            var available = candidates
                .Where(candidate => candidate.Online
                    && candidate.Busy is false
                    && StringComparer.Ordinal.Equals(candidate.ControlOwner, "adapter"))
                .OrderBy(candidate => candidate.AgvId, StringComparer.Ordinal)
                .ToArray();
            if (available.Length == 0)
            {
                return SchedulingDecision.Rejected("No online, idle AGV controlled by adapter is available.");
            }

            var reservedReverseTransitions = _routes.Values
                .SelectMany(route => route.Path.Stations.Zip(route.Path.Stations.Skip(1), BuildTransitionKey))
                .ToHashSet(StringComparer.Ordinal);

            var options = new List<(AgvCandidate Candidate, PlannedPath Path)>();
            foreach (var candidate in available)
            {
                try
                {
                    var path = planner.PlanVia(candidate.CurrentStationId, sourceStationId, targetStationId);
                    if (path.Stations
                        .Zip(path.Stations.Skip(1), (from, to) => BuildTransitionKey(to, from))
                        .Any(reservedReverseTransitions.Contains))
                    {
                        continue;
                    }
                    options.Add((candidate, path));
                }
                catch (InvalidOperationException)
                {
                    // Another active route may reserve this candidate's only path.
                }
            }

            var selected = options
                .OrderBy(option => option.Path.Cost)
                .ThenBy(option => option.Candidate.AgvId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (selected.Candidate is null)
            {
                return SchedulingDecision.Rejected("All available AGVs are blocked by active route reservations.");
            }

            var route = new ScheduledRoute(taskId, selected.Candidate.AgvId, selected.Path, DateTime.UtcNow);
            _routes.Add(taskId, route);
            return new SchedulingDecision(true, route.AgvId, route.Path, null);
        }
    }

    public bool Release(Guid taskId)
    {
        lock (_gate) return _routes.Remove(taskId);
    }

    public void ReleaseForIdleAgvs(IReadOnlySet<string> agvIds)
    {
        lock (_gate)
        {
            foreach (var taskId in _routes.Values
                         .Where(route => agvIds.Contains(route.AgvId))
                         .Select(route => route.TaskId)
                         .ToArray())
            {
                _routes.Remove(taskId);
            }
        }
    }

    private static string BuildTransitionKey(string from, string to) => $"{from}\u001f{to}";
}
