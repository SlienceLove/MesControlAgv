using MesControlAgv.Domain;
using MesControlAgv.Mes.Contracts;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Mes") ?? "Data Source=data/mes.db";

builder.Services.AddDbContext<MesDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddHttpClient<IAdapterClient, AdapterClient>(client =>
    client.BaseAddress = new Uri(
        builder.Configuration["Adapter:BaseUrl"] ?? "http://localhost:5041/"));
builder.Services.AddSingleton(new PathPlanner(AgvMap.Default));
builder.Services.AddScoped<TaskRepository>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddHostedService<RecoveryService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
    await database.Database.EnsureCreatedAsync();
    await EnsureTaskColumnsAsync(database);
}

app.MapGet("/health", () => Results.Ok(new { service = "mes", status = "ok" }));

app.MapGet("/api/agv", async (IAdapterClient adapter, CancellationToken cancellationToken) =>
    Results.Ok(await adapter.GetSnapshotAsync(cancellationToken)));

app.MapGet("/api/agvs/fleet", async (IAdapterClient adapter, CancellationToken cancellationToken) =>
{
    if (adapter is IFleetAwareAdapterClient fleet)
    {
        return Results.Ok(await fleet.GetFleetSnapshotAsync(cancellationToken));
    }

    return Results.Ok(new[] { await adapter.GetSnapshotAsync(cancellationToken) });
});

app.MapPost("/api/agvs/{agvId}/command", async (
    string agvId,
    AgvCommandRequest request,
    IAdapterClient adapter,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await adapter.ExecuteAgvCommandAsync(agvId, request.Command, request.TaskId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (AdapterHttpException exception)
    {
        return Results.Json(new { detail = exception.Detail ?? exception.Message }, statusCode: (int?)exception.ResponseStatusCode);
    }
});

app.MapGet("/api/map", () => Results.Ok(new
{
    stations = Stations.All,
    edges = AgvMap.Default.Edges
}));

app.MapPost("/api/planning/path", (PlanPathRequest request, PathPlanner planner) =>
{
    try
    {
        var path = planner.Plan(
            request.FromStationId,
            request.ToStationId,
            request.BlockedStations?.ToHashSet(StringComparer.Ordinal));
        return Results.Ok(new PlannedPathResponse(path.Stations, path.Cost));
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { detail = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.UnprocessableEntity(new { detail = exception.Message }); }
});

app.MapGet("/api/stations", () => Results.Ok(Stations.All.Select(station => new StationResponse(
    station.Code,
    station.Name,
    station.AgvStationId,
    station.Enabled))));

app.MapPost("/api/tasks", async (
    CreateTaskRequest request,
    TaskService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var task = await service.CreateAsync(request, cancellationToken);
        return Results.Created($"/api/tasks/{task.Id}", task);
    }
    catch (UnsupportedRouteException exception)
    {
        return Results.UnprocessableEntity(new { detail = exception.Message });
    }
});

app.MapPost("/api/tasks/{taskId:guid}/arrived", async (Guid taskId, TaskService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.RecordArrivalAsync(taskId, cancellationToken)));

app.MapPost("/api/tasks/{taskId:guid}/confirm-pickup", async (Guid taskId, OperatorActionRequest request, TaskService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.ConfirmPickupAsync(taskId, request.OperatorName, cancellationToken)));

app.MapPost("/api/tasks/{taskId:guid}/confirm-dropoff", async (Guid taskId, OperatorActionRequest request, TaskService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.ConfirmDropoffAsync(taskId, request.OperatorName, cancellationToken)));

app.MapPost("/api/tasks/{taskId:guid}/retry", async (Guid taskId, TaskService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.RetryAsync(taskId, cancellationToken)));

app.MapPost("/api/tasks/{taskId:guid}/cancel", async (Guid taskId, OperatorActionRequest request, TaskService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.CancelAsync(taskId, request.OperatorName, cancellationToken)));

app.MapPost("/api/tasks/{taskId:guid}/recover", async (Guid taskId, TaskService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.RecoverAsync(taskId, cancellationToken)));

app.MapGet("/api/tasks", async (TaskService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.ListAsync(cancellationToken)));

app.MapGet("/api/tasks/{taskId:guid}", async (
    Guid taskId,
    TaskService service,
    CancellationToken cancellationToken) =>
{
    var task = await service.GetDetailAsync(taskId, cancellationToken);
    return task is null ? Results.NotFound() : Results.Ok(task);
});

static async Task EnsureTaskColumnsAsync(MesDbContext database)
{
    var connection = database.Database.GetDbConnection();
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA table_info(TransportTasks);";
    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
    await reader.CloseAsync();

    foreach (var definition in new[]
    {
        (Name: "Priority", Sql: "INTEGER NOT NULL DEFAULT 0"),
        (Name: "Description", Sql: "TEXT NULL"),
        (Name: "ExternalId", Sql: "TEXT NULL")
    })
    {
        if (columns.Contains(definition.Name)) continue;
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE TransportTasks ADD COLUMN {definition.Name} {definition.Sql};";
        await alter.ExecuteNonQueryAsync();
    }
}

app.Run();

public partial class Program;
