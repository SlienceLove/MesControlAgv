using MesControlAgv.Simulator;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<SimulatorState>();
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { service = "simulator", status = "ok" }));

app.MapPost("/commands/navigate", (NavigateRequest request, SimulatorState state) =>
{
    try
    {
        var task = state.Navigate(request.TaskId, request.TargetStationId);
        return Results.Ok(ToResponse(task));
    }
    catch (TimeoutException)
    {
        return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { detail = exception.Message });
    }
});

app.MapGet("/tasks/{taskId:guid}", (Guid taskId, SimulatorState state) =>
{
    var task = state.GetTask(taskId);
    return task is null ? Results.NotFound() : Results.Ok(ToResponse(task));
});

app.MapPost("/commands/{taskId:guid}/cancel", (Guid taskId, SimulatorState state) =>
{
    try
    {
        var task = state.Cancel(taskId);
        return task is null ? Results.NotFound() : Results.Ok(ToResponse(task));
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { detail = exception.Message });
    }
});

app.MapGet("/snapshot", (SimulatorState state) => Results.Ok(new SnapshotResponse(
    state.Online,
    state.ControlOwner,
    state.CurrentStationId,
    state.CurrentTaskId)));

app.MapPost("/controls/{mode}", (string mode, SimulatorState state) =>
{
    try
    {
        state.ApplyControl(mode);
        return Results.NoContent();
    }
    catch (ArgumentOutOfRangeException)
    {
        return Results.NotFound();
    }
});

app.Run();

static DeviceTaskResponse ToResponse(SimulatedTask task) => new(task.TaskId, task.TaskId.ToString("N"), task.TargetStationId, task.State, task.LastError);

public sealed record NavigateRequest(Guid TaskId, string TargetStationId);
public sealed record DeviceTaskResponse(Guid TaskId, string DeviceTaskId, string TargetStationId, string State, string? LastError);
public sealed record SnapshotResponse(bool Online, string ControlOwner, string? CurrentStationId, Guid? CurrentTaskId);

public partial class Program;
