using MesControlAgv.Simulator;

var builder = WebApplication.CreateBuilder(args);
var agvIds = builder.Configuration.GetSection("Agv:Ids").Get<string[]>()
    ?? ["AGV-01", "AGV-02", "AGV-03"];
builder.Services.AddSingleton(new SimulatorState(agvIds));
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { service = "simulator", status = "ok" }));

app.MapGet("/agvs", (SimulatorState state) => Results.Ok(state.GetSnapshots()));

app.MapPost("/commands/navigate", (NavigateRequest request, SimulatorState state) =>
    Navigate(state, "AGV-01", request));

app.MapGet("/tasks/{taskId:guid}", (Guid taskId, SimulatorState state) =>
{
    var task = state.GetTask(taskId);
    return task is null ? Results.NotFound() : Results.Ok(ToResponse(task));
});

app.MapPost("/commands/{taskId:guid}/{action}", (Guid taskId, string action, SimulatorState state) =>
    ApplyTaskAction(state, "AGV-01", taskId, action));

app.MapGet("/snapshot", (SimulatorState state) => ToSnapshotResponse(state.GetSnapshot("AGV-01")));

app.MapPost("/controls/{mode}", (string mode, SimulatorState state) => ApplyDefaultControl(state, mode));

app.MapPost("/controls/{mode}/{taskId:guid}", (string mode, Guid taskId, SimulatorState state) => ApplyTaskControl(state, taskId, mode));

app.MapGet("/agvs/{agvId}/snapshot", (string agvId, SimulatorState state) =>
{
    try { return Results.Ok(ToSnapshotResponse(state.GetSnapshot(agvId))); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});

app.MapPost("/agvs/{agvId}/commands/navigate", (string agvId, NavigateRequest request, SimulatorState state) =>
    Navigate(state, agvId, request));

app.MapGet("/agvs/{agvId}/tasks/{taskId:guid}", (string agvId, Guid taskId, SimulatorState state) =>
{
    try
    {
        var task = state.GetTask(agvId, taskId);
        return task is null ? Results.NotFound() : Results.Ok(ToResponse(task));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});

app.MapPost("/agvs/{agvId}/commands/{taskId:guid}/{action}", (string agvId, Guid taskId, string action, SimulatorState state) =>
    ApplyTaskAction(state, agvId, taskId, action));

app.MapPost("/agvs/{agvId}/controls/{mode}", (string agvId, string mode, SimulatorState state) =>
    ApplyAgvControl(state, agvId, mode));

app.Run();

static IResult Navigate(SimulatorState state, string agvId, NavigateRequest request)
{
    try
    {
        var task = state.Navigate(agvId, request.TaskId, request.SourceStationId, request.TargetStationId, request.Path);
        return Results.Ok(ToResponse(task));
    }
    catch (TimeoutException)
    {
        return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { detail = exception.Message });
    }
}

static IResult ApplyTaskAction(SimulatorState state, string agvId, Guid taskId, string action)
{
    try
    {
        var task = action switch
        {
            "pause" => state.Pause(agvId, taskId),
            "resume" => state.Resume(agvId, taskId),
            "cancel" => state.Cancel(agvId, taskId),
            _ => null
        };
        if (action is not ("pause" or "resume" or "cancel")) return Results.NotFound();
        return task is null ? Results.NotFound() : Results.Ok(ToResponse(task));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { detail = exception.Message }); }
}

static IResult ApplyAgvControl(SimulatorState state, string agvId, string mode)
{
    try
    {
        state.ApplyControl(agvId, mode);
        return Results.NoContent();
    }
    catch (ArgumentOutOfRangeException) { return Results.NotFound(); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}

static IResult ApplyDefaultControl(SimulatorState state, string mode)
{
    try
    {
        state.ApplyControl(mode);
        return Results.NoContent();
    }
    catch (ArgumentOutOfRangeException) { return Results.NotFound(); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { detail = exception.Message }); }
}

static IResult ApplyTaskControl(SimulatorState state, Guid taskId, string mode)
{
    try
    {
        state.ApplyControl(taskId, mode);
        return Results.NoContent();
    }
    catch (ArgumentOutOfRangeException) { return Results.NotFound(); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { detail = exception.Message }); }
}

static DeviceTaskResponse ToResponse(SimulatedTask task) =>
    new(task.TaskId, task.TaskId.ToString("N"), task.TargetStationId, task.State, task.LastError, task.AgvId, task.Path);

static SnapshotResponse ToSnapshotResponse(SimulatorSnapshot snapshot) =>
    new(snapshot.Online, snapshot.ControlOwner, snapshot.CurrentStationId, snapshot.CurrentTaskId, snapshot.AgvId);

public sealed record NavigateRequest(
    Guid TaskId,
    string TargetStationId,
    string? SourceStationId = null,
    IReadOnlyList<string>? Path = null);

public sealed record DeviceTaskResponse(
    Guid TaskId,
    string DeviceTaskId,
    string TargetStationId,
    string State,
    string? LastError,
    string AgvId = "AGV-01",
    IReadOnlyList<string>? Path = null);

public sealed record SnapshotResponse(
    bool Online,
    string ControlOwner,
    string? CurrentStationId,
    Guid? CurrentTaskId,
    string AgvId = "AGV-01");

public partial class Program;
