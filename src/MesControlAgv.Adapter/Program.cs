using MesControlAgv.Adapter.Contracts;
using MesControlAgv.Adapter.Data;
using MesControlAgv.Adapter.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Adapter") ?? "Data Source=data/adapter.db";
var simulatorUrl = builder.Configuration["Simulator:BaseUrl"] ?? "http://localhost:5183/";

builder.Services.AddDbContext<AdapterDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddHttpClient<ISimulatorClient, SimulatorClient>(client => client.BaseAddress = new Uri(simulatorUrl));
builder.Services.AddScoped<AdapterService>();

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<AdapterDbContext>();
    await database.Database.EnsureCreatedAsync();
}

app.MapGet("/health", () => Results.Ok(new { service = "adapter", status = "ok" }));

app.MapPost("/tasks/{taskId:guid}/dispatch", async (Guid taskId, DispatchRequest request, AdapterService service, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await service.DispatchAsync(taskId, request.TargetStationId, cancellationToken)); }
    catch (ControlUnavailableException exception) { return Results.Conflict(new { detail = exception.Message }); }
});

app.MapGet("/tasks/{taskId:guid}", async (Guid taskId, AdapterService service, CancellationToken cancellationToken) =>
{
    var task = await service.GetAsync(taskId, cancellationToken);
    return task is null ? Results.NotFound() : Results.Ok(task);
});

app.MapPost("/tasks/{taskId:guid}/{action}", async (Guid taskId, string action, AdapterService service, CancellationToken cancellationToken) =>
{
    if (action is not ("pause" or "resume" or "cancel")) return Results.NotFound();
    var task = action switch
    {
        "pause" => await service.PauseAsync(taskId, cancellationToken),
        "resume" => await service.ResumeAsync(taskId, cancellationToken),
        _ => await service.CancelAsync(taskId, cancellationToken)
    };
    return task is null ? Results.NotFound() : Results.Ok(task);
});

app.MapGet("/agv/snapshot", async (ISimulatorClient simulator, CancellationToken cancellationToken) => Results.Ok(await simulator.GetSnapshotAsync(cancellationToken)));

app.Run();

public sealed record DispatchRequest(string TargetStationId);

public partial class Program;
