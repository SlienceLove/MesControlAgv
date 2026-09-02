using MesControlAgv.Adapter;
using MesControlAgv.Adapter.Modules;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsEnvironment(PhysicalAcceptanceConfiguration.EnvironmentName))
{
    PhysicalAcceptanceConfiguration.ReplaceDefaultSources(
        builder.Configuration,
        builder.Environment.ContentRootPath,
        args);
}
else if (builder.Environment.IsEnvironment(FieldSimulationConfiguration.EnvironmentName))
{
    FieldSimulationConfiguration.ReplaceDefaultSources(
        builder.Configuration,
        builder.Environment.ContentRootPath,
        args);
}

if (builder.Environment.IsEnvironment(PhysicalAcceptanceConfiguration.EnvironmentName) &&
    string.Equals(
        builder.Configuration["Devices:AuboArm:Driver"],
        "simulator",
        StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "PhysicalAcceptance cannot use the in-process AUBO simulator; restore the site-confirmed WebSocket driver.");
}

var configuredConnectionString = builder.Configuration.GetConnectionString("Adapter")
    ?? "Data Source=data/adapter.db";
var connectionString = ResolveSqliteConnectionString(configuredConnectionString);
var simulatorBaseUrl = builder.Configuration["Simulator:BaseUrl"] ?? "http://localhost:5183/";
builder.Services.AddServices(builder.Configuration, connectionString, simulatorBaseUrl);

var app = builder.Build();
var runMode = app.Services.GetRequiredService<AdapterRunMode>();
var modules = app.Services.GetRequiredService<DeviceAdapterModuleCatalog>();
var devices = app.Services.GetRequiredService<DeviceAdapterRegistry>();

app.Use(async (context, next) =>
{
    if (runMode.IsReadOnlyPreflight
        && !HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.Headers["Allow"] = "GET, HEAD";
        await context.Response.WriteAsJsonAsync(new
        {
            detail = "Adapter read-only preflight mode rejects all state-changing HTTP requests."
        });
        return;
    }

    await next();
});

await modules.InitializeAsync(app.Services, CancellationToken.None);

app.MapGet("/health", (IAgvDriver driver) => Results.Ok(new AdapterRuntimeIdentityResponse(
    Service: "adapter",
    Status: "ok",
    RunMode: runMode.Value,
    Driver: driver.DriverId,
    Modules: modules.Descriptors.Select(descriptor => new AdapterModuleIdentityResponse(
        descriptor.ModuleId,
        descriptor.DeviceType,
        descriptor.SupportedTransports.Select(transport => transport.ToString()).ToArray()))
        .ToArray(),
    Devices: devices.Devices.Select(ToIdentityResponse).ToArray())));

app.MapGet("/api/adapter/devices", () => Results.Ok(
    devices.Devices.Select(ToIdentityResponse).ToArray()));

modules.MapEndpoints(app);

app.Run();

static AdapterDeviceIdentityResponse ToIdentityResponse(DeviceAdapterRegistration device) => new(
    device.DeviceId,
    device.DeviceType,
    device.ModuleId,
    device.DriverId,
    device.Transport.ToString(),
    device.Enabled,
    device.ControlEnabled);

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

static string GetProjectDataSourcePath(
    string relativeDataSource,
    string projectDirectory,
    string dataDirectory)
{
    var normalizedDataSource = relativeDataSource
        .Replace('\\', Path.DirectorySeparatorChar)
        .Replace('/', Path.DirectorySeparatorChar);
    var pathParts = normalizedDataSource.Split(
        Path.DirectorySeparatorChar,
        StringSplitOptions.RemoveEmptyEntries);
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
            var projectDirectory = string.Equals(
                directory.Name,
                "MesControlAgv.Adapter",
                StringComparison.OrdinalIgnoreCase)
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
                    .Select(dataDirectory => GetProjectDataSourcePath(
                        relativeDataSource,
                        projectDirectory.FullName,
                        dataDirectory.FullName))
                    .FirstOrDefault(File.Exists);
                if (existingDatabasePath is not null)
                {
                    return existingDatabasePath;
                }

                var existingDataDirectoryPath = dataDirectories
                    .Where(dataDirectory => dataDirectory.Exists)
                    .Select(dataDirectory => GetProjectDataSourcePath(
                        relativeDataSource,
                        projectDirectory.FullName,
                        dataDirectory.FullName))
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

public partial class Program;
