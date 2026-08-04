using MesControlAgv.Adapter.Contracts;
using MesControlAgv.Adapter.Data;
using MesControlAgv.Adapter.Services;
using MesControlAgv.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var configuredConnectionString = builder.Configuration.GetConnectionString("Adapter") ?? "Data Source=data/adapter.db";
var connectionString = ResolveSqliteConnectionString(configuredConnectionString);
var simulatorUrl = builder.Configuration["Simulator:BaseUrl"] ?? "http://localhost:5183/";
var agvDriver = builder.Configuration["Agv:Driver"] ?? "simulator";

builder.Services.AddDbContext<AdapterDbContext>(options => options.UseSqlite(connectionString));
builder.Services.Configure<TcpAgvOptions>(builder.Configuration.GetSection("Agv:Tcp"));
builder.Services.AddHttpClient<SimulatorClient>(client => client.BaseAddress = new Uri(simulatorUrl));
if (string.Equals(agvDriver, "tcp", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<TcpAgvClient>();
    builder.Services.AddSingleton<IAgvDeviceClient>(services => services.GetRequiredService<TcpAgvClient>());
    builder.Services.AddSingleton<IAgvFleetDeviceClient>(services =>
        new SingleAgvFleetDeviceClient("AGV-01", services.GetRequiredService<IAgvDeviceClient>()));
    builder.Services.AddHostedService(services => services.GetRequiredService<TcpAgvClient>());
}
else
{
    builder.Services.AddSingleton<IAgvDeviceClient>(services => services.GetRequiredService<SimulatorClient>());
    builder.Services.AddSingleton<IAgvFleetDeviceClient>(services => services.GetRequiredService<SimulatorClient>());
}
builder.Services.AddSingleton(new MultiAgvScheduler(new PathPlanner(AgvMap.Default)));
builder.Services.AddScoped<AdapterService>();

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<AdapterDbContext>();
    await database.Database.EnsureCreatedAsync();
    await AddColumnIfMissingAsync(database, "AgvId");
    await AddColumnIfMissingAsync(database, "PathJson");
}

app.MapGet("/health", () => Results.Ok(new { service = "adapter", status = "ok" }));

app.MapPost("/tasks/{taskId:guid}/dispatch", async (Guid taskId, DispatchRequest request, AdapterService service, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.DispatchAsync(
            taskId,
            request.SourceStationId,
            request.TargetStationId,
            request.AgvId,
            request.Path,
            cancellationToken));
    }
    catch (ControlUnavailableException exception) { return Results.Conflict(new { detail = exception.Message }); }
    catch (AgvUnavailableException exception) { return Results.Conflict(new { detail = exception.Message }); }
});

app.MapGet("/tasks/{taskId:guid}", async (Guid taskId, AdapterService service, CancellationToken cancellationToken) =>
{
    var task = await service.GetAsync(taskId, cancellationToken);
    return task is null ? Results.NotFound() : Results.Ok(task);
});

app.MapPost("/tasks/{taskId:guid}/{action}", async (Guid taskId, string action, AdapterService service, CancellationToken cancellationToken) =>
{
    if (action is not ("pause" or "resume" or "cancel")) return Results.NotFound();
    try
    {
        var task = action switch
        {
            "pause" => await service.PauseAsync(taskId, cancellationToken),
            "resume" => await service.ResumeAsync(taskId, cancellationToken),
            _ => await service.CancelAsync(taskId, cancellationToken)
        };
        return task is null ? Results.NotFound() : Results.Ok(task);
    }
    catch (ControlUnavailableException exception)
    {
        return Results.Conflict(new { detail = exception.Message });
    }
});

app.MapGet("/agv/snapshot", async (IAgvDeviceClient device, CancellationToken cancellationToken) => Results.Ok(await device.GetSnapshotAsync(cancellationToken)));
app.MapGet("/agvs", async (AdapterService service, CancellationToken cancellationToken) => Results.Ok(await service.GetFleetAsync(cancellationToken)));

app.Run();

static string ResolveSqliteConnectionString(string connectionString)
{
    var sqliteConnection = new SqliteConnectionStringBuilder(connectionString);
    var dataSource = sqliteConnection.DataSource;

    if (string.IsNullOrWhiteSpace(dataSource)
        || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
        || Path.IsPathRooted(dataSource))
    {
        return connectionString;
    }

    var workingDirectoryPath = Path.GetFullPath(dataSource, Directory.GetCurrentDirectory());
    var workingDirectoryParent = Path.GetDirectoryName(workingDirectoryPath);
    if (!string.IsNullOrEmpty(workingDirectoryParent) && Directory.Exists(workingDirectoryParent))
    {
        sqliteConnection.DataSource = workingDirectoryPath;
        return sqliteConnection.ToString();
    }

    var projectDataPath = FindExistingProjectDataPath(dataSource);
    if (projectDataPath is not null)
    {
        sqliteConnection.DataSource = projectDataPath;
        return sqliteConnection.ToString();
    }

    if (!string.IsNullOrEmpty(workingDirectoryParent))
    {
        Directory.CreateDirectory(workingDirectoryParent);
    }

    sqliteConnection.DataSource = workingDirectoryPath;
    return sqliteConnection.ToString();
}

static string GetProjectDataSourcePath(string relativeDataSource, string projectDirectory, string dataDirectory)
{
    var normalizedDataSource = relativeDataSource.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    var pathParts = normalizedDataSource.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
    if (pathParts.Length > 0 && string.Equals(pathParts[0], "data", StringComparison.OrdinalIgnoreCase))
    {
        var remainingPath = string.Join(Path.DirectorySeparatorChar, pathParts.Skip(1));
        return Path.GetFullPath(remainingPath, dataDirectory);
    }

    return Path.GetFullPath(normalizedDataSource, projectDirectory);
}
static string? FindExistingProjectDataPath(string relativeDataSource)
{
    var startDirectories = new[]
    {
        Directory.GetCurrentDirectory(),
        AppContext.BaseDirectory
    };

    foreach (var startDirectory in startDirectories)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var projectDirectory = string.Equals(directory.Name, "MesControlAgv.Adapter", StringComparison.OrdinalIgnoreCase)
                ? directory
                : new DirectoryInfo(Path.Combine(directory.FullName, "src", "MesControlAgv.Adapter"));

            if (projectDirectory.Exists)
            {
                var dataDirectories = new[]
                {
                    new DirectoryInfo(Path.Combine(projectDirectory.FullName, "Data")),
                    new DirectoryInfo(Path.Combine(projectDirectory.FullName, "data"))
                };

                var existingDatabasePath = dataDirectories
                    .Select(dataDirectory => GetProjectDataSourcePath(relativeDataSource, projectDirectory.FullName, dataDirectory.FullName))
                    .FirstOrDefault(File.Exists);
                if (existingDatabasePath is not null)
                {
                    return existingDatabasePath;
                }

                var existingDataDirectoryPath = dataDirectories
                    .Where(dataDirectory => dataDirectory.Exists)
                    .Select(dataDirectory => GetProjectDataSourcePath(relativeDataSource, projectDirectory.FullName, dataDirectory.FullName))
                    .FirstOrDefault();
                if (existingDataDirectoryPath is not null)
                {
                    return existingDataDirectoryPath;
                }
            }

            directory = directory.Parent;
        }
    }

    return null;
}
static async Task AddColumnIfMissingAsync(AdapterDbContext database, string columnName)
{
    try
    {
        if (columnName == "AgvId")
        {
            await database.Database.ExecuteSqlRawAsync("ALTER TABLE Tasks ADD COLUMN AgvId TEXT NOT NULL DEFAULT 'AGV-01'");
        }
        else if (columnName == "PathJson")
        {
            await database.Database.ExecuteSqlRawAsync("ALTER TABLE Tasks ADD COLUMN PathJson TEXT NULL");
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(columnName));
        }
    }
    catch (SqliteException exception) when (exception.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
    {
    }
}

public sealed record DispatchRequest(
    string TargetStationId,
    string? SourceStationId = null,
    string? AgvId = null,
    IReadOnlyList<string>? Path = null);

public partial class Program;
