using MesControlAgv.Domain;
using MesControlAgv.Mes.Contracts;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Mes") ?? "Data Source=data/mes.db";

builder.Services.AddDbContext<MesDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddHttpClient<IAdapterClient, AdapterClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["Adapter:BaseUrl"] ?? "http://localhost:5001/"));
builder.Services.AddScoped<TaskRepository>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddHostedService<RecoveryService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
    await database.Database.EnsureCreatedAsync();
}

app.MapGet("/health", () => Results.Ok(new { service = "mes", status = "ok" }));

app.MapGet("/api/agv", async (IAdapterClient adapter, CancellationToken cancellationToken) =>
    Results.Ok(await adapter.GetSnapshotAsync(cancellationToken)));

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

app.Run();

public partial class Program;
