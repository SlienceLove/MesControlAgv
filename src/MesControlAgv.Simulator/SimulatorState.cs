namespace MesControlAgv.Simulator;

public sealed class SimulatorState
{
    private readonly Dictionary<string, SimulatedAgv> _agvs;

    public SimulatorState(IEnumerable<string>? agvIds = null)
    {
        var ids = (agvIds ?? ["AGV-01", "AGV-02", "AGV-03"])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0) ids = ["AGV-01"];

        _agvs = ids.ToDictionary(id => id, id => new SimulatedAgv(id), StringComparer.Ordinal);
    }

    public bool Online => DefaultAgv.Online;
    public string ControlOwner => DefaultAgv.ControlOwner;
    public string? CurrentStationId => DefaultAgv.CurrentStationId;
    public Guid? CurrentTaskId => DefaultAgv.CurrentTaskId;

    public IReadOnlyList<SimulatorSnapshot> GetSnapshots() =>
        _agvs.Values
            .Select(agv => new SimulatorSnapshot(agv.Id, agv.Online, agv.ControlOwner, agv.CurrentStationId, agv.CurrentTaskId))
            .ToArray();

    public SimulatorSnapshot GetSnapshot(string agvId)
    {
        var agv = GetAgv(agvId);
        return new SimulatorSnapshot(agv.Id, agv.Online, agv.ControlOwner, agv.CurrentStationId, agv.CurrentTaskId);
    }

    public SimulatedTask Navigate(Guid taskId, string stationId) =>
        Navigate(DefaultAgv.Id, taskId, null, stationId, null);

    public SimulatedTask Navigate(
        string agvId,
        Guid taskId,
        string? sourceStationId,
        string stationId,
        IReadOnlyList<string>? path)
    {
        var agv = GetAgv(agvId);
        EnsureAvailable(agv);
        if (agv.ControlOwner != "adapter") throw new InvalidOperationException($"AGV control owner is {agv.ControlOwner}.");

        if (agv.Tasks.TryGetValue(taskId, out var existing) && existing.State is not "failed") return existing;
        if (path is { Count: > 0 } && (!StringComparer.Ordinal.Equals(path[0], agv.CurrentStationId)
            || !StringComparer.Ordinal.Equals(path[^1], stationId)))
        {
            throw new InvalidOperationException("The planned path does not match the AGV current station and target.");
        }

        var timeout = agv.NextFault == "timeout";
        if (timeout) agv.NextFault = null;
        if (agv.NextFault == "fail")
        {
            agv.NextFault = null;
            var failed = new SimulatedTask(taskId, stationId, "failed", "navigation failed", agv.Id, path);
            agv.Tasks[taskId] = failed;
            return failed;
        }

        var task = new SimulatedTask(taskId, stationId, "moving", null, agv.Id, path);
        agv.Tasks[taskId] = task;
        agv.CurrentTaskId = taskId;
        if (timeout) throw new TimeoutException();
        return task;
    }

    public SimulatedTask? GetTask(Guid taskId) => GetTask(DefaultAgv.Id, taskId);

    public SimulatedTask? GetTask(string agvId, Guid taskId) =>
        GetAgv(agvId).Tasks.GetValueOrDefault(taskId);

    public SimulatedTask? Cancel(Guid taskId) => Cancel(DefaultAgv.Id, taskId);

    public SimulatedTask? Cancel(string agvId, Guid taskId)
    {
        var agv = GetAgv(agvId);
        EnsureAvailable(agv);
        if (agv.ControlOwner != "adapter") throw new InvalidOperationException($"AGV control owner is {agv.ControlOwner}.");
        if (!agv.Tasks.TryGetValue(taskId, out var task)) return null;

        var cancelled = task with { State = "cancelled", LastError = null };
        agv.Tasks[taskId] = cancelled;
        if (agv.CurrentTaskId == taskId) agv.CurrentTaskId = null;
        return cancelled;
    }

    public SimulatedTask? Pause(Guid taskId) => Pause(DefaultAgv.Id, taskId);

    public SimulatedTask? Pause(string agvId, Guid taskId)
    {
        var agv = GetAgv(agvId);
        if (!agv.Tasks.TryGetValue(taskId, out var task)) return null;
        var paused = task with { State = "paused" };
        agv.Tasks[taskId] = paused;
        return paused;
    }

    public SimulatedTask? Resume(Guid taskId) => Resume(DefaultAgv.Id, taskId);

    public SimulatedTask? Resume(string agvId, Guid taskId)
    {
        var agv = GetAgv(agvId);
        if (!agv.Tasks.TryGetValue(taskId, out var task)) return null;
        var resumed = task with { State = "moving" };
        agv.Tasks[taskId] = resumed;
        return resumed;
    }

    public void ApplyControl(string mode)
    {
        var agv = _agvs.Values
            .Where(candidate => candidate.CurrentTaskId is not null)
            .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? DefaultAgv;
        ApplyControl(agv.Id, mode);
    }

    public void ApplyControl(Guid taskId, string mode)
    {
        var agv = _agvs.Values.SingleOrDefault(candidate => candidate.CurrentTaskId == taskId);
        if (agv is null) throw new InvalidOperationException($"Device task {taskId} is not currently active on an AGV.");
        ApplyControl(agv.Id, mode);
    }

    public void ApplyControl(string agvId, string mode)
    {
        var agv = GetAgv(agvId);
        switch (mode)
        {
            case "arrive":
                if (agv.CurrentTaskId is { } taskId && agv.Tasks.TryGetValue(taskId, out var task))
                {
                    agv.Tasks[taskId] = task with { State = "arrived" };
                    agv.CurrentStationId = task.TargetStationId;
                    agv.CurrentTaskId = null;
                }
                break;
            case "fail": agv.NextFault = "fail"; break;
            case "timeout": agv.NextFault = "timeout"; break;
            case "offline": agv.Online = false; break;
            case "recover": agv.Online = true; break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private SimulatedAgv DefaultAgv => _agvs["AGV-01"];

    private SimulatedAgv GetAgv(string agvId) =>
        _agvs.TryGetValue(agvId, out var agv)
            ? agv
            : throw new KeyNotFoundException($"AGV {agvId} is not configured.");

    private static void EnsureAvailable(SimulatedAgv agv)
    {
        if (!agv.Online) throw new InvalidOperationException("AGV is offline.");
    }

    private sealed class SimulatedAgv(string id)
    {
        public string Id { get; } = id;
        public Dictionary<Guid, SimulatedTask> Tasks { get; } = [];
        public bool Online { get; set; } = true;
        public string ControlOwner { get; } = "adapter";
        public string CurrentStationId { get; set; } = "CHARGE_01";
        public Guid? CurrentTaskId { get; set; }
        public string? NextFault { get; set; }
    }
}

public sealed record SimulatedTask(
    Guid TaskId,
    string TargetStationId,
    string State,
    string? LastError,
    string AgvId = "AGV-01",
    IReadOnlyList<string>? Path = null);

public sealed record SimulatorSnapshot(
    string AgvId,
    bool Online,
    string ControlOwner,
    string? CurrentStationId,
    Guid? CurrentTaskId);
