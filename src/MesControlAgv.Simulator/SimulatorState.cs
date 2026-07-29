namespace MesControlAgv.Simulator;

public sealed class SimulatorState
{
    private readonly Dictionary<Guid, SimulatedTask> _tasks = [];
    private string? _nextFault;

    public bool Online { get; private set; } = true;
    public string ControlOwner { get; private set; } = "adapter";
    public string? CurrentStationId { get; private set; } = "CHARGE_01";
    public Guid? CurrentTaskId { get; private set; }

    public SimulatedTask Navigate(Guid taskId, string stationId)
    {
        EnsureAvailable();
        if (ControlOwner != "adapter") throw new InvalidOperationException($"AGV control owner is {ControlOwner}.");
        if (_nextFault == "timeout") { _nextFault = null; throw new TimeoutException(); }
        if (_nextFault == "fail")
        {
            _nextFault = null;
            var failed = new SimulatedTask(taskId, stationId, "failed", "navigation failed");
            _tasks[taskId] = failed;
            return failed;
        }
        var task = new SimulatedTask(taskId, stationId, "moving", null);
        _tasks[taskId] = task;
        CurrentTaskId = taskId;
        return task;
    }

    public SimulatedTask? GetTask(Guid taskId) => _tasks.GetValueOrDefault(taskId);

    public void ApplyControl(string mode)
    {
        switch (mode)
        {
            case "arrive":
                if (CurrentTaskId is { } taskId && _tasks.TryGetValue(taskId, out var task))
                {
                    _tasks[taskId] = task with { State = "arrived" };
                    CurrentStationId = task.TargetStationId;
                    CurrentTaskId = null;
                }
                break;
            case "fail": _nextFault = "fail"; break;
            case "timeout": _nextFault = "timeout"; break;
            case "offline": Online = false; break;
            case "recover": Online = true; break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private void EnsureAvailable()
    {
        if (!Online) throw new InvalidOperationException("AGV is offline.");
    }
}

public sealed record SimulatedTask(Guid TaskId, string TargetStationId, string State, string? LastError);
