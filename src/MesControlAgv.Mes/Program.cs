using MesControlAgv.Application;
using MesControlAgv.Domain;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Mes") ?? "Data Source=data/mes.db";
var profile = BindProfile(builder.Configuration);
var map = AgvMap.FromProfile(profile.Map);

builder.Services.AddDbContext<MesDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddHttpClient<IAgvGateway, AdapterClient>(client =>
    client.BaseAddress = new Uri(
        builder.Configuration["Adapter:BaseUrl"] ?? "http://localhost:5041/"));
builder.Services.AddHttpClient<IIonChromatographyStatusReader, IonChromatographyGatewayClient>(client =>
    client.BaseAddress = new Uri(
        builder.Configuration["IonChromatographyGateway:BaseUrl"] ?? "http://127.0.0.1:5190/"));
builder.Services.AddSingleton(profile);
builder.Services.AddSingleton(map);
builder.Services.AddSingleton(new PathPlanner(map));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(builder.Configuration
    .GetSection("WorkflowSimulatorWorker")
    .Get<WorkflowSimulatorWorkerOptions>() ?? new WorkflowSimulatorWorkerOptions());
builder.Services.AddSingleton<WorkflowValidator>();
builder.Services.AddSingleton<IWorkflowRuntimeAdmissionPolicy, ActiveProfileWorkflowAdmissionPolicy>();
builder.Services.AddScoped<MesWorkflowVersionReader>();
builder.Services.AddScoped<IWorkflowVersionReader>(services => services.GetRequiredService<MesWorkflowVersionReader>());
builder.Services.AddScoped<WorkflowRuntimeExecutor>();
builder.Services.AddScoped<IWorkflowRuntimeExecutor>(services => services.GetRequiredService<WorkflowRuntimeExecutor>());
builder.Services.AddScoped<WorkflowApplicationService>();
builder.Services.AddScoped<IWorkflowApplicationService>(services => services.GetRequiredService<WorkflowApplicationService>());
builder.Services.AddScoped<FieldNavigationAcceptanceRepository>();
builder.Services.AddScoped<IFieldNavigationAcceptanceApplicationService, FieldNavigationAcceptanceService>();
builder.Services.AddScoped<TaskRepository>();
builder.Services.AddScoped<ITaskApplicationService, TaskService>();
builder.Services.AddScoped<IKpiDashboardApplicationService, KpiDashboardService>();
builder.Services.AddHostedService<RecoveryService>();
builder.Services.AddHostedService<WorkflowRecoveryService>();
builder.Services.AddHostedService<WorkflowSimulatorWorker>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
    await database.Database.EnsureCreatedAsync();
    await EnsureTaskColumnsAsync(database);
    await EnsureWorkflowTablesAsync(database);
    await EnsureFieldNavigationAcceptanceTablesAsync(database);
}

app.MapGet("/health", () => Results.Ok(new { service = "mes", status = "ok" }));

app.MapGet("/api/instruments/{instrumentId}/status", async (
    string instrumentId,
    IIonChromatographyStatusReader reader,
    CancellationToken cancellationToken) =>
{
    try
    {
        var status = await reader.GetStatusAsync(instrumentId, cancellationToken);
        return Results.Ok(new IonChromatographyControlCenterStatusResponse(
            new IonChromatographyStatusResponse(
                status.InstrumentId,
                status.Model,
                status.SerialNumber,
                status.Online,
                status.DeviceState,
                status.PortOwned,
                status.ObservedAtUtc,
                status.Pressure,
                status.ColumnTemperature,
                status.DetectorTemperature,
                status.Alarm,
                status.Conductivity,
                status.TotalConductivity,
                status.Flow,
                status.MappingConfidence),
            IonChromatographyReadOnlyPolicy.TaskAdmissionEnabled,
            IonChromatographyReadOnlyPolicy.EnabledOperations.Select(operation => operation.ToString()).ToArray(),
            "ReadOnlyCaptureCorrelated"));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { detail = exception.Message });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception exception) when (exception is
        HttpRequestException or
        TaskCanceledException or
        InvalidOperationException)
    {
        return Results.Problem(
            "The read-only instrument status gateway is unavailable.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/workflows", async (IWorkflowApplicationService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.ListAsync(cancellationToken)));

app.MapGet("/api/workflows/{workflowId:guid}", async (
    Guid workflowId,
    IWorkflowApplicationService service,
    CancellationToken cancellationToken) =>
{
    var workflow = await service.GetAsync(workflowId, cancellationToken);
    return workflow is null ? Results.NotFound() : Results.Ok(workflow);
});

app.MapGet("/api/workflows/{workflowId:guid}/versions", async (
    Guid workflowId,
    IWorkflowApplicationService service,
    CancellationToken cancellationToken) =>
    Results.Ok(await service.ListVersionsAsync(workflowId, cancellationToken)));

app.MapGet("/api/workflows/{workflowId:guid}/audits", async (
    Guid workflowId,
    int? version,
    int? limit,
    IWorkflowApplicationService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.ListAuditsAsync(
            workflowId,
            version,
            limit ?? 100,
            cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { detail = exception.Message });
    }
});

app.MapGet("/api/workflows/{workflowId:guid}/versions/{version:int}", async (
    Guid workflowId,
    int version,
    IWorkflowVersionReader reader,
    CancellationToken cancellationToken) =>
{
    var workflowVersion = await reader.GetVersionAsync(workflowId, version, cancellationToken);
    return workflowVersion is null ? Results.NotFound() : Results.Ok(workflowVersion);
});

app.MapPost("/api/workflows", async (
    WorkflowDefinition definition,
    string actor,
    IWorkflowApplicationService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var draft = await service.CreateDraftAsync(definition, actor, cancellationToken);
        return Results.Created($"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}", draft);
    }
    catch (ArgumentException exception)
    {
        return Results.UnprocessableEntity(new { detail = exception.Message });
    }
});

app.MapPut("/api/workflows/{workflowId:guid}/versions/{version:int}/draft", async (
    Guid workflowId,
    int version,
    WorkflowDefinition definition,
    string actor,
    IWorkflowApplicationService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.UpdateDraftAsync(workflowId, version, definition, actor, cancellationToken));
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new { detail = exception.Message });
    }
    catch (ArgumentException exception)
    {
        return Results.UnprocessableEntity(new { detail = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.UnprocessableEntity(new { detail = exception.Message });
    }
});

app.MapPost("/api/workflows/validate", async (
    WorkflowDefinition definition,
    IWorkflowApplicationService service,
    CancellationToken cancellationToken) =>
    Results.Ok(await service.ValidateAsync(definition, cancellationToken)));

app.MapPost("/api/workflows/{workflowId:guid}/versions/{version:int}/validate", async (
    Guid workflowId,
    int version,
    IWorkflowApplicationService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.ValidateVersionAsync(workflowId, version, cancellationToken));
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new { detail = exception.Message });
    }
});

app.MapPost("/api/workflows/{workflowId:guid}/versions/{version:int}/publish", async (
    Guid workflowId,
    int version,
    string actor,
    IWorkflowApplicationService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.PublishAsync(workflowId, version, actor, cancellationToken));
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new { detail = exception.Message });
    }
    catch (ArgumentException exception)
    {
        return Results.UnprocessableEntity(new { detail = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.UnprocessableEntity(new { detail = exception.Message });
    }
});

app.MapPost("/api/workflows/execute", async (
    WorkflowExecutionRequest request,
    IWorkflowApplicationService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.ExecuteAsync(request, cancellationToken);
    if (result.IsAccepted)
    {
        return Results.Json(result, statusCode: result.IsIdempotentReplay ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
    }

    return result.RejectionCode switch
    {
        WorkflowExecutionRejectionCodes.VersionNotFound => Results.NotFound(result),
        WorkflowExecutionRejectionCodes.RequestIdReused => Results.Conflict(result),
        _ => Results.UnprocessableEntity(result)
    };
});

app.MapGet("/api/workflow-executions/{executionId:guid}", async (
    Guid executionId,
    IWorkflowApplicationService service,
    CancellationToken cancellationToken) =>
{
    var execution = await service.GetExecutionAsync(executionId, cancellationToken);
    return execution is null ? Results.NotFound() : Results.Ok(execution);
});

app.MapGet("/api/workflow-executions/by-request/{requestId:guid}", async (
    Guid requestId,
    IWorkflowApplicationService service,
    CancellationToken cancellationToken) =>
{
    var execution = await service.GetExecutionByRequestAsync(requestId, cancellationToken);
    return execution is null ? Results.NotFound() : Results.Ok(execution);
});

app.MapPost("/api/field-navigation-acceptances", async (
    CreateFieldNavigationAcceptanceRequest request,
    IFieldNavigationAcceptanceApplicationService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var acceptance = await service.CreateAsync(request, cancellationToken);
        return Results.Created($"/api/field-navigation-acceptances/{acceptance.Id}", acceptance);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { detail = exception.Message });
    }
    catch (KeyNotFoundException exception)
    {
        return Results.UnprocessableEntity(new { detail = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.UnprocessableEntity(new { detail = exception.Message });
    }
});

app.MapGet("/api/field-navigation-acceptances/{acceptanceId:guid}", async (
    Guid acceptanceId,
    IFieldNavigationAcceptanceApplicationService service,
    CancellationToken cancellationToken) =>
{
    var acceptance = await service.GetAsync(acceptanceId, cancellationToken);
    return acceptance is null ? Results.NotFound() : Results.Ok(acceptance);
});

app.MapPost("/api/field-navigation-acceptances/{acceptanceId:guid}/authorize", async (
    Guid acceptanceId,
    AuthorizeFieldNavigationAcceptanceRequest request,
    IFieldNavigationAcceptanceApplicationService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.AuthorizeAsync(acceptanceId, request, cancellationToken));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { detail = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { detail = exception.Message });
    }
});

app.MapPost("/api/field-navigation-acceptances/{acceptanceId:guid}/dispatch", async (
    Guid acceptanceId,
    IFieldNavigationAcceptanceApplicationService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.DispatchAsync(acceptanceId, cancellationToken));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { detail = exception.Message });
    }
});

app.MapPost("/api/field-navigation-acceptances/{acceptanceId:guid}/cancel", async (
    Guid acceptanceId,
    IFieldNavigationAcceptanceApplicationService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.CancelAsync(acceptanceId, cancellationToken));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { detail = exception.Message });
    }
});
app.MapGet("/api/agv", async (IAgvGateway adapter, CancellationToken cancellationToken) =>
    Results.Ok(await adapter.GetSnapshotAsync(cancellationToken)));

app.MapGet("/api/physical/preflight", async (IAgvGateway adapter, CancellationToken cancellationToken) =>
{
    if (adapter is not IPhysicalPreflightAgvGateway physical)
    {
        return Results.NotFound(new { detail = "The configured AGV gateway does not support physical preflight." });
    }

    return Results.Ok(await physical.GetPhysicalPreflightAsync(cancellationToken));
});

app.MapGet("/api/dashboard/kpi", async (
    DateOnly? date,
    IKpiDashboardApplicationService service,
    CancellationToken cancellationToken) =>
    Results.Ok(await service.GetAsync(
        date ?? DateOnly.FromDateTime(DateTime.UtcNow),
        cancellationToken)));

app.MapGet("/api/agvs/fleet", async (IAgvGateway adapter, CancellationToken cancellationToken) =>
{
    if (adapter is IFleetAwareAgvGateway fleet)
    {
        return Results.Ok(await fleet.GetFleetSnapshotAsync(cancellationToken));
    }

    return Results.Ok(new[] { await adapter.GetSnapshotAsync(cancellationToken) });
});

app.MapGet("/api/agvs/fleet/status", async (ITaskApplicationService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.GetFleetStatusAsync(cancellationToken)));

app.MapPost("/api/agvs/{agvId}/command", async (
    string agvId,
    AgvCommandRequest request,
    IAgvGateway adapter,
    ITaskApplicationService tasks,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await adapter.ExecuteAgvCommandAsync(agvId, request.Command, request.TaskId, cancellationToken);
        if (result is not null && request.TaskId is { } operationId && request.Command.Trim().ToLowerInvariant() is "pause" or "resume" or "continue")
        {
            await tasks.RecordAgvCommandAsync(operationId, request.Command, result, cancellationToken);
        }
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (AdapterHttpException exception)
    {
        return Results.Json(new { detail = exception.Detail ?? exception.Message }, statusCode: (int?)exception.ResponseStatusCode);
    }
});

app.MapGet("/api/map", (ProfileConfiguration configuredProfile, AgvMap configuredMap) =>
    Results.Ok(new MapSnapshotResponse(
        Stations.FromProfile(configuredProfile)
            .Select(station => new StationResponse(
                station.Code,
                station.Name,
                station.AgvStationId,
                station.Enabled,
                station.Type))
            .ToList(),
        configuredMap.Edges
            .Select(edge => new MapEdgeResponse(edge.From, edge.To, edge.Cost, edge.Bidirectional))
            .ToList(),
        configuredProfile.Product.ProductId,
        configuredProfile.Product.Version,
        configuredProfile.PhysicalAcceptance?.MapSnapshot.MapName,
        configuredProfile.PhysicalAcceptance?.MapSnapshot.Version,
        configuredProfile.PhysicalAcceptance?.MapSnapshot.Md5)));

app.MapPost("/api/planning/path", (PlanPathRequest request, PathPlanner planner) =>
{
    try
    {
        var path = planner.Plan(
            request.FromStationId,
            request.ToStationId,
            request.BlockedStations?.ToHashSet(StringComparer.Ordinal));
        return Results.Ok(new PlannedPathResponse(
            path.Stations,
            path.Cost,
            request.FromStationId,
            request.ToStationId));
    }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { detail = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.UnprocessableEntity(new { detail = exception.Message }); }
});

app.MapGet("/api/stations", (ProfileConfiguration configuredProfile) => Results.Ok(Stations.FromProfile(configuredProfile).Select(station => new StationResponse(
    station.Code,
    station.Name,
    station.AgvStationId,
    station.Enabled,
    station.Type))));

app.MapGet("/api/runtime-settings", (ProfileConfiguration configuredProfile) => Results.Ok(new RuntimeSettingsResponse(
    configuredProfile.Product.ProductId,
    configuredProfile.Product.Version,
    configuredProfile.Timeouts.TaskPollingInterval)));

app.MapPost("/api/tasks", async (
    CreateTaskRequest request,
    ITaskApplicationService service,
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

app.MapPost("/api/tasks/{taskId:guid}/dispatch", async (Guid taskId, ITaskApplicationService service, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.DispatchAsync(taskId, cancellationToken));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (InvalidTaskTransitionException exception)
    {
        return Results.Conflict(new { detail = exception.Message });
    }
});

app.MapPost("/api/tasks/{taskId:guid}/arrived", async (Guid taskId, ITaskApplicationService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.RecordArrivalAsync(taskId, cancellationToken)));

app.MapPost("/api/tasks/{taskId:guid}/confirm-pickup", async (Guid taskId, OperatorActionRequest request, ITaskApplicationService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.ConfirmPickupAsync(taskId, request.OperatorName, cancellationToken)));

app.MapPost("/api/tasks/{taskId:guid}/confirm-dropoff", async (Guid taskId, OperatorActionRequest request, ITaskApplicationService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.ConfirmDropoffAsync(taskId, request.OperatorName, cancellationToken)));

app.MapPost("/api/tasks/{taskId:guid}/retry", async (Guid taskId, ITaskApplicationService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.RetryAsync(taskId, cancellationToken)));

app.MapPost("/api/tasks/{taskId:guid}/cancel", async (Guid taskId, OperatorActionRequest request, ITaskApplicationService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.CancelAsync(taskId, request.OperatorName, cancellationToken)));

app.MapPost("/api/tasks/{taskId:guid}/recover", async (Guid taskId, ITaskApplicationService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.RecoverAsync(taskId, cancellationToken)));

app.MapGet("/api/tasks", async (DateOnly? date, ITaskApplicationService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.ListAsync(date ?? DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken)));

app.MapGet("/api/tasks/{taskId:guid}", async (
    Guid taskId,
    ITaskApplicationService service,
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
        (Name: "ExternalId", Sql: "TEXT NULL"),
        (Name: "EndedAt", Sql: "TEXT NULL"),
        (Name: "ActiveAgvId", Sql: "TEXT NULL"),
        (Name: "ActiveDeviceTaskId", Sql: "TEXT NULL"),
        (Name: "ActivePathJson", Sql: "TEXT NULL")
    })
    {
        if (columns.Contains(definition.Name)) continue;
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE TransportTasks ADD COLUMN {definition.Name} {definition.Sql};";
        await alter.ExecuteNonQueryAsync();
    }
}

static async Task EnsureWorkflowTablesAsync(MesDbContext database)
{
    var connection = database.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
    {
        await connection.OpenAsync();
    }

    var statements = new[]
    {
        """
        CREATE TABLE IF NOT EXISTS WorkflowVersions (
            WorkflowId TEXT NOT NULL,
            Version INTEGER NOT NULL,
            DefinitionJson TEXT NOT NULL,
            Status TEXT NOT NULL,
            PublishStatus TEXT NOT NULL,
            ValidationJson TEXT NULL,
            CreatedBy TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            ChangeSummary TEXT NULL,
            PublishedBy TEXT NULL,
            PublishedAtUtc TEXT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            PRIMARY KEY (WorkflowId, Version)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS WorkflowExecutions (
            RequestId TEXT NOT NULL PRIMARY KEY,
            Fingerprint TEXT NOT NULL,
            WorkflowId TEXT NOT NULL,
            Version INTEGER NOT NULL,
            ExecutionId TEXT NOT NULL,
            Outcome TEXT NOT NULL,
            RejectionCode TEXT NULL,
            RequestJson TEXT NOT NULL,
            ResultJson TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            DefinitionSnapshotJson TEXT NULL,
            RuntimeStatus TEXT NULL,
            CurrentNodeId TEXT NULL,
            PendingStepJson TEXT NULL,
            TransportOperationId TEXT NULL,
            Attempt INTEGER NOT NULL DEFAULT 0,
            LastError TEXT NULL,
            UpdatedAtUtc TEXT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS WorkflowAudits (
            Id TEXT NOT NULL PRIMARY KEY,
            EventType TEXT NOT NULL,
            Outcome TEXT NOT NULL,
            Code TEXT NULL,
            Reason TEXT NULL,
            WorkflowId TEXT NOT NULL,
            Version INTEGER NOT NULL,
            RequestId TEXT NULL,
            ExecutionId TEXT NULL,
            Actor TEXT NULL,
            CorrelationId TEXT NULL,
            DetailsJson TEXT NOT NULL,
            OccurredAtUtc TEXT NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_WorkflowVersions_WorkflowId_PublishStatus ON WorkflowVersions (WorkflowId, PublishStatus);",
        "CREATE INDEX IF NOT EXISTS IX_WorkflowExecutions_WorkflowId_Version_CreatedAtUtc ON WorkflowExecutions (WorkflowId, Version, CreatedAtUtc);",
        "CREATE INDEX IF NOT EXISTS IX_WorkflowAudits_WorkflowId_Version_OccurredAtUtc ON WorkflowAudits (WorkflowId, Version, OccurredAtUtc);",
        "CREATE INDEX IF NOT EXISTS IX_WorkflowAudits_RequestId ON WorkflowAudits (RequestId);"
    };

    foreach (var statement in statements)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }

    await EnsureWorkflowExecutionColumnsAsync(connection);
    await using var runtimeIndex = connection.CreateCommand();
    runtimeIndex.CommandText =
        "CREATE INDEX IF NOT EXISTS IX_WorkflowExecutions_RuntimeStatus_UpdatedAtUtc ON WorkflowExecutions (RuntimeStatus, UpdatedAtUtc);";
    await runtimeIndex.ExecuteNonQueryAsync();
}

static async Task EnsureWorkflowExecutionColumnsAsync(System.Data.Common.DbConnection connection)
{
    await using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA table_info(WorkflowExecutions);";
    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
    await reader.CloseAsync();

    foreach (var definition in new[]
    {
        (Name: "DefinitionSnapshotJson", Sql: "TEXT NULL"),
        (Name: "RuntimeStatus", Sql: "TEXT NULL"),
        (Name: "CurrentNodeId", Sql: "TEXT NULL"),
        (Name: "PendingStepJson", Sql: "TEXT NULL"),
        (Name: "TransportOperationId", Sql: "TEXT NULL"),
        (Name: "Attempt", Sql: "INTEGER NOT NULL DEFAULT 0"),
        (Name: "LastError", Sql: "TEXT NULL"),
        (Name: "UpdatedAtUtc", Sql: "TEXT NULL")
    })
    {
        if (columns.Contains(definition.Name)) continue;
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE WorkflowExecutions ADD COLUMN {definition.Name} {definition.Sql};";
        await alter.ExecuteNonQueryAsync();
    }
}

static async Task EnsureFieldNavigationAcceptanceTablesAsync(MesDbContext database)
{
    var connection = database.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
    {
        await connection.OpenAsync();
    }

    var statements = new[]
    {
        """
        CREATE TABLE IF NOT EXISTS FieldNavigationAcceptances (
            Id TEXT NOT NULL PRIMARY KEY,
            Status TEXT NOT NULL,
            AgvId TEXT NOT NULL,
            SourceStationId TEXT NOT NULL,
            TargetStationId TEXT NOT NULL,
            MapName TEXT NOT NULL,
            MapMd5 TEXT NOT NULL,
            PlannedPathJson TEXT NOT NULL,
            Description TEXT NULL,
            OperatorName TEXT NULL,
            SafetyObserverName TEXT NULL,
            PermitId TEXT NULL,
            AuthorizedAtUtc TEXT NULL,
            ExpiresAtUtc TEXT NULL,
            PermitConsumedAtUtc TEXT NULL,
            DeviceTaskId TEXT NULL,
            LastError TEXT NULL,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS FieldNavigationAcceptanceAudits (
            Id TEXT NOT NULL PRIMARY KEY,
            AcceptanceId TEXT NOT NULL,
            EventType TEXT NOT NULL,
            DetailsJson TEXT NOT NULL,
            OccurredAtUtc TEXT NOT NULL
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_FieldNavigationAcceptances_PermitId ON FieldNavigationAcceptances (PermitId);",
        "CREATE INDEX IF NOT EXISTS IX_FieldNavigationAcceptances_Status_CreatedAtUtc ON FieldNavigationAcceptances (Status, CreatedAtUtc);",
        "CREATE INDEX IF NOT EXISTS IX_FieldNavigationAcceptanceAudits_AcceptanceId_OccurredAtUtc ON FieldNavigationAcceptanceAudits (AcceptanceId, OccurredAtUtc);"
    };

    foreach (var statement in statements)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }
}

static ProfileConfiguration BindProfile(IConfiguration configuration)
{
    var profile = configuration.GetSection("Profile").Get<ProfileConfiguration>()
        ?? ProfileConfiguration.Default;
    var validation = new ProfileConfigurationValidator().Validate(profile);
    if (!validation.IsValid)
    {
        throw new InvalidOperationException(
            "The configured AGV profile is invalid: " +
            string.Join("; ", validation.Errors.Select(error => error.Message)));
    }

    return profile;
}

app.Run();

public partial class Program;






