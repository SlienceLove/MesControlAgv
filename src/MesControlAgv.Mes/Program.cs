using MesControlAgv.Application;
using MesControlAgv.Domain;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Endpoints;
using MesControlAgv.Mes.Services;
using MesControlAgv.Mes;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsEnvironment(FieldSimulationConfiguration.EnvironmentName))
{
    FieldSimulationConfiguration.ReplaceDefaultSources(
        builder.Configuration,
        builder.Environment.ContentRootPath,
        args);
}
else if (builder.Environment.IsEnvironment(PhysicalAcceptanceConfiguration.EnvironmentName))
{
    PhysicalAcceptanceConfiguration.ReplaceDefaultSources(
        builder.Configuration,
        builder.Environment.ContentRootPath,
        args);
}
var connectionString = builder.Configuration.GetConnectionString("Mes") ?? "Data Source=data/mes.db";
var profile = BindProfile(builder.Configuration);
var map = AgvMap.FromProfile(profile.Map);
var workflowCatalogs = BuiltInWorkflowCatalog.Create();
var workflowPublicationContext = WorkflowPublicationContext.FromProfile(profile);

builder.Services.AddDbContext<MesDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddHttpClient<IAgvGateway, AdapterClient>(client =>
    client.BaseAddress = new Uri(
        builder.Configuration["Adapter:BaseUrl"] ?? "http://localhost:5041/"));
builder.Services.AddHttpClient<IAgvIoGateway, AdapterIoClient>(client =>
    client.BaseAddress = new Uri(
        builder.Configuration["Adapter:BaseUrl"] ?? "http://localhost:5041/"));
builder.Services.AddHttpClient<IAuboArmGateway, AdapterAuboArmClient>(client =>
    client.BaseAddress = new Uri(
        builder.Configuration["Adapter:BaseUrl"] ?? "http://localhost:5041/"));
var physicalReadinessOptions = builder.Configuration
    .GetSection(PhysicalReadinessSupervisorOptions.SectionName)
    .Get<PhysicalReadinessSupervisorOptions>() ?? new PhysicalReadinessSupervisorOptions();
builder.Services.AddSingleton(physicalReadinessOptions);
builder.Services.AddScoped<IPhysicalDeviceReadinessProbe, PhysicalAgvReadinessProbe>();
builder.Services.AddScoped<IPhysicalDeviceReadinessProbe, PhysicalAuboReadinessProbe>();
builder.Services.AddSingleton(builder.Configuration
    .GetSection("AgvAuboSequence")
    .Get<AgvAuboSequenceOptions>() ?? new AgvAuboSequenceOptions());
var workflowAuboWorkerOptions = builder.Configuration
    .GetSection("WorkflowAuboWorker")
    .Get<WorkflowAuboProgramWorkerOptions>() ?? new WorkflowAuboProgramWorkerOptions();
var workflowFieldNavigationWorkerOptions = builder.Configuration
    .GetSection("WorkflowFieldNavigationWorker")
    .Get<WorkflowFieldNavigationWorkerOptions>() ?? new WorkflowFieldNavigationWorkerOptions();
builder.Services.AddSingleton(workflowAuboWorkerOptions);
builder.Services.AddSingleton(workflowFieldNavigationWorkerOptions);
var physicalBatchEnabled = !profile.Features.UseSimulator &&
                           profile.Features.EnableFieldNavigationAcceptance &&
                           workflowFieldNavigationWorkerOptions.Enabled &&
                           workflowFieldNavigationWorkerOptions.AutoAuthorizeFromRunRequest &&
                           workflowAuboWorkerOptions.Enabled;
builder.Services.AddSingleton(new WorkflowPhysicalBatchAdmissionGate(
    physicalBatchEnabled,
    physicalBatchEnabled
        ? "现场批量 worker 已启用。"
        : "MES 启动时未同时启用现场导航、自动许可和 AUBO worker，因此拒绝一键现场执行。"));
builder.Services.AddSingleton<WorkflowFieldNavigationRetryState>();
builder.Services.AddHttpClient<ISampleWorkstationReader, SampleWorkstationAdapterClient>(client =>
    client.BaseAddress = new Uri(
        builder.Configuration["Adapter:BaseUrl"] ?? "http://localhost:5041/"));
builder.Services.AddHttpClient<IIonChromatographyStatusReader, IonChromatographyGatewayClient>(client =>
    client.BaseAddress = new Uri(
        builder.Configuration["IonChromatographyGateway:BaseUrl"] ?? "http://127.0.0.1:5190/"));
builder.Services.AddOptions<ShineLabTcpOptions>()
    .Bind(builder.Configuration.GetSection(ShineLabTcpOptions.SectionName))
    .Validate(options => options.Port is >= 1 and <= 65535, "ShineLabTcp:Port must be between 1 and 65535.")
    .Validate(options => options.StaleAfterSeconds > 0, "ShineLabTcp:StaleAfterSeconds must be positive.")
    .Validate(options => options.CommandTimeoutMs > 0, "ShineLabTcp:CommandTimeoutMs must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton<ShineLabStatusHub>();
builder.Services.AddSingleton<ShineLabConnectionManager>();
builder.Services.AddSingleton<ShineLabCommandService>();
builder.Services.AddScoped<ShineLabTaskRepository>();
builder.Services.AddScoped<ShineLabTaskService>();
builder.Services.AddHostedService<ShineLabTcpServer>();
builder.Services.AddHostedService<ShineLabTaskRecoveryService>();
builder.Services.AddSingleton(profile);
builder.Services.AddSingleton(map);
builder.Services.AddSingleton(workflowCatalogs);
builder.Services.AddSingleton(workflowPublicationContext);
builder.Services.AddSingleton(new ExperimentResourceCatalog(profile));
builder.Services.AddSingleton<ExperimentSchedulingMutationGate>();
builder.Services.AddSingleton(new PathPlanner(map));
builder.Services.AddSingleton(TimeProvider.System);
var experimentCompositeRuntimeWorkerOptions = builder.Configuration
    .GetSection("ExperimentCompositeRuntimeWorker")
    .Get<ExperimentCompositeRuntimeWorkerOptions>() ?? new ExperimentCompositeRuntimeWorkerOptions();
builder.Services.AddSingleton(experimentCompositeRuntimeWorkerOptions);
builder.Services.AddSingleton(builder.Configuration
    .GetSection("WorkflowSimulatorWorker")
    .Get<WorkflowSimulatorWorkerOptions>() ?? new WorkflowSimulatorWorkerOptions());
builder.Services.Configure<WorkflowRunControlAuthorizationOptions>(
    builder.Configuration.GetSection("WorkflowRunControl"));
builder.Services.AddSingleton<IWorkflowRunControlAuthorizer, ConfiguredWorkflowRunControlAuthorizer>();
builder.Services.AddSingleton<WorkflowValidator>();
builder.Services.AddSingleton<IWorkflowRuntimeAdmissionPolicy, ActiveProfileWorkflowAdmissionPolicy>();
builder.Services.AddScoped<MesWorkflowVersionReader>();
builder.Services.AddScoped<IWorkflowVersionReader>(services => services.GetRequiredService<MesWorkflowVersionReader>());
builder.Services.AddScoped<WorkflowRuntimeExecutor>();
builder.Services.AddScoped<IWorkflowRuntimeExecutor>(services => services.GetRequiredService<WorkflowRuntimeExecutor>());
builder.Services.AddScoped<WorkflowApplicationService>();
builder.Services.AddScoped<IWorkflowApplicationService>(services => services.GetRequiredService<WorkflowApplicationService>());
builder.Services.AddScoped<IExperimentSchedulingQueryService, ExperimentSchedulingQueryService>();
builder.Services.AddScoped<IExperimentSchedulingCommandService, ExperimentSchedulingCommandService>();
builder.Services.AddScoped<ExperimentRuntimeLeaseLifecycle>();
builder.Services.AddScoped<ExperimentRuntimeAdmissionService>();
builder.Services.AddScoped<IExperimentRuntimeAdmissionService>(services =>
    services.GetRequiredService<ExperimentRuntimeAdmissionService>());
builder.Services.AddScoped<ExperimentCompositeRuntimeService>();
builder.Services.AddScoped<IExperimentCompositeRuntimeService>(services =>
    services.GetRequiredService<ExperimentCompositeRuntimeService>());
builder.Services.AddScoped<ExperimentRuntimeRecoveryCoordinator>();
builder.Services.AddScoped<FieldNavigationAcceptanceRepository>();
builder.Services.AddScoped<IFieldNavigationAcceptanceApplicationService, FieldNavigationAcceptanceService>();
builder.Services.AddScoped<TaskRepository>();
builder.Services.AddScoped<ITaskApplicationService, TaskService>();
builder.Services.AddScoped<IKpiDashboardApplicationService, KpiDashboardService>();
builder.Services.AddScoped<IAgvAuboSequenceService, AgvAuboSequenceService>();
builder.Services.AddSingleton<PhysicalReadinessStateStore>();
builder.Services.AddSingleton<PhysicalReadinessSupervisor>();
builder.Services.AddSingleton<IPhysicalReadinessState>(services =>
    services.GetRequiredService<PhysicalReadinessSupervisor>());
builder.Services.AddSingleton<IPhysicalReadinessSupervisor>(services =>
    services.GetRequiredService<PhysicalReadinessSupervisor>());
builder.Services.AddHostedService(services =>
    services.GetRequiredService<PhysicalReadinessSupervisor>());
builder.Services.AddHostedService<RecoveryService>();
builder.Services.AddHostedService<FieldNavigationAcceptanceRecoveryService>();
builder.Services.AddHostedService<ExperimentRuntimeRecoveryService>();
builder.Services.AddHostedService<WorkflowRecoveryService>();
builder.Services.AddHostedService<ExperimentCompositeRuntimeWorker>();
builder.Services.AddHostedService<WorkflowSimulatorWorker>();
builder.Services.AddHostedService<WorkflowAdvancedRuntimeWorker>();
builder.Services.AddHostedService<WorkflowAuboProgramWorker>();
builder.Services.AddHostedService<WorkflowFieldNavigationWorker>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
    await database.Database.EnsureCreatedAsync();
    await EnsureTaskColumnsAsync(database);
    await EnsureWorkflowTablesAsync(database);
    await EnsureExperimentSchedulingTablesAsync(database);
    await EnsureFieldNavigationAcceptanceTablesAsync(database);
    await EnsureShineLabTablesAsync(database);
}

app.MapGet("/health", () => Results.Ok(new { service = "mes", status = "ok" }));

app.MapMesDeviceGatewayEndpoints();
app.MapPhysicalReadinessEndpoints();
app.MapShineLabStatusEndpoints();

app.MapMesWorkflowEndpoints();

app.MapMesExperimentSchedulingEndpoints();

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

app.MapGet("/api/workflow-runs/{workflowRunId:guid}/field-navigation-acceptances", async (
    Guid workflowRunId,
    IFieldNavigationAcceptanceApplicationService service,
    CancellationToken cancellationToken) =>
    Results.Ok(await service.ListForWorkflowRunAsync(workflowRunId, cancellationToken)));

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
app.MapGet("/api/dashboard/kpi", async (
    DateOnly? date,
    IKpiDashboardApplicationService service,
    CancellationToken cancellationToken) =>
    Results.Ok(await service.GetAsync(
        date ?? DateOnly.FromDateTime(DateTime.UtcNow),
        cancellationToken)));

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

app.MapMesTaskEndpoints();

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
        CREATE TABLE IF NOT EXISTS WorkflowNodeExecutions (
            Id TEXT NOT NULL PRIMARY KEY,
            WorkflowRunId TEXT NOT NULL,
            WorkflowId TEXT NOT NULL,
            Version INTEGER NOT NULL,
            StepRequestId TEXT NOT NULL,
            NodeId TEXT NOT NULL,
            NodeTypeId TEXT NOT NULL,
            NodeName TEXT NOT NULL,
            Attempt INTEGER NOT NULL,
            Status TEXT NOT NULL,
            InputJson TEXT NOT NULL,
            OutputJson TEXT NOT NULL,
            StartedAtUtc TEXT NULL,
            CompletedAtUtc TEXT NULL,
            LastError TEXT NULL,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS WorkflowDeviceOperations (
            OperationId TEXT NOT NULL PRIMARY KEY,
            WorkflowRunId TEXT NOT NULL,
            NodeExecutionId TEXT NOT NULL,
            RequestId TEXT NOT NULL,
            Attempt INTEGER NOT NULL,
            CapabilityId TEXT NOT NULL,
            DeviceId TEXT NULL,
            IdempotencyKey TEXT NOT NULL,
            CorrelationId TEXT NULL,
            Status TEXT NOT NULL,
            RequestSummaryJson TEXT NOT NULL,
            ResultSummaryJson TEXT NOT NULL,
            RequestedAtUtc TEXT NOT NULL,
            CompletedAtUtc TEXT NULL,
            ReconciledAtUtc TEXT NULL,
            LastError TEXT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS WorkflowRuntimeInteractions (
            RequestId TEXT NOT NULL PRIMARY KEY,
            Fingerprint TEXT NOT NULL,
            WorkflowRunId TEXT NOT NULL,
            NodeExecutionId TEXT NULL,
            InteractionType TEXT NOT NULL,
            Status TEXT NOT NULL,
            SignalName TEXT NULL,
            CorrelationValue TEXT NULL,
            Actor TEXT NOT NULL,
            Reason TEXT NOT NULL,
            RequestJson TEXT NOT NULL,
            DataJson TEXT NOT NULL,
            ReceivedAtUtc TEXT NOT NULL,
            AppliedAtUtc TEXT NULL,
            UpdatedAtUtc TEXT NOT NULL
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
        "CREATE INDEX IF NOT EXISTS IX_WorkflowNodeExecutions_WorkflowRunId_CreatedAtUtc ON WorkflowNodeExecutions (WorkflowRunId, CreatedAtUtc);",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_WorkflowNodeExecutions_StepRequestId ON WorkflowNodeExecutions (StepRequestId);",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_WorkflowNodeExecutions_WorkflowRunId_NodeId_Attempt ON WorkflowNodeExecutions (WorkflowRunId, NodeId, Attempt);",
        "CREATE INDEX IF NOT EXISTS IX_WorkflowDeviceOperations_WorkflowRunId_RequestedAtUtc ON WorkflowDeviceOperations (WorkflowRunId, RequestedAtUtc);",
        "CREATE INDEX IF NOT EXISTS IX_WorkflowDeviceOperations_NodeExecutionId ON WorkflowDeviceOperations (NodeExecutionId);",
        "CREATE INDEX IF NOT EXISTS IX_WorkflowRuntimeInteractions_WorkflowRunId_InteractionType_Status_ReceivedAtUtc ON WorkflowRuntimeInteractions (WorkflowRunId, InteractionType, Status, ReceivedAtUtc);",
        "CREATE INDEX IF NOT EXISTS IX_WorkflowRuntimeInteractions_WorkflowRunId_SignalName_CorrelationValue_Status ON WorkflowRuntimeInteractions (WorkflowRunId, SignalName, CorrelationValue, Status);",
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

static async Task EnsureExperimentSchedulingTablesAsync(MesDbContext database)
{
    var connection = database.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
    {
        await connection.OpenAsync();
    }

    var tableStatements = new[]
    {
        """
        CREATE TABLE IF NOT EXISTS ExperimentPlans (
            PlanId TEXT NOT NULL,
            Version INTEGER NOT NULL,
            Name TEXT NOT NULL,
            Description TEXT NOT NULL,
            WorkflowId TEXT NOT NULL,
            WorkflowVersion INTEGER NOT NULL,
            WorkflowStepsJson TEXT NOT NULL DEFAULT '[]',
            Status TEXT NOT NULL,
            MaterialRequirementsJson TEXT NOT NULL,
            DefaultParametersJson TEXT NOT NULL,
            ResourceRequirementsJson TEXT NOT NULL,
            ProfileProductId TEXT NULL,
            ProfileVersion TEXT NULL,
            LayoutId TEXT NULL,
            ValidationJson TEXT NULL,
            ValidatedBy TEXT NULL,
            ValidatedAtUtc TEXT NULL,
            CreatedBy TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            PublishedBy TEXT NULL,
            PublishedAtUtc TEXT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            PRIMARY KEY (PlanId, Version)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS ExperimentJobs (
            JobId TEXT NOT NULL PRIMARY KEY,
            PlanId TEXT NOT NULL,
            PlanVersion INTEGER NOT NULL,
            WorkflowId TEXT NOT NULL,
            WorkflowVersion INTEGER NOT NULL,
            WorkflowStepsJson TEXT NOT NULL DEFAULT '[]',
            SampleBatchId TEXT NOT NULL,
            SampleId TEXT NULL,
            ParametersJson TEXT NOT NULL,
            Status TEXT NOT NULL,
            WorkflowRunId TEXT NULL,
            CreatedBy TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            StartedAtUtc TEXT NULL,
            CompletedAtUtc TEXT NULL,
            LastError TEXT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS ExperimentRuns (
            ExperimentRunId TEXT NOT NULL PRIMARY KEY,
            ExperimentJobId TEXT NOT NULL,
            PlanId TEXT NOT NULL,
            PlanVersion INTEGER NOT NULL,
            AdmissionRequestId TEXT NOT NULL,
            Status TEXT NOT NULL,
            CurrentStepOrder INTEGER NOT NULL,
            CurrentStepRunId TEXT NULL,
            StepsJson TEXT NOT NULL,
            LastError TEXT NULL,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS ScheduleEntries (
            ScheduleEntryId TEXT NOT NULL PRIMARY KEY,
            ExperimentJobId TEXT NOT NULL,
            PlannedStartUtc TEXT NOT NULL,
            PlannedEndUtc TEXT NOT NULL,
            Priority INTEGER NOT NULL,
            Status TEXT NOT NULL,
            RequestedResourcesJson TEXT NOT NULL,
            BlockingReasonsJson TEXT NOT NULL,
            CreatedBy TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS ResourceReservations (
            ReservationId TEXT NOT NULL PRIMARY KEY,
            ScheduleEntryId TEXT NOT NULL,
            ResourceType TEXT NOT NULL,
            ResourceId TEXT NOT NULL,
            ResourceKey TEXT NOT NULL,
            StartsAtUtc TEXT NOT NULL,
            EndsAtUtc TEXT NOT NULL,
            Status TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS WorkflowResourceLeases (
            LeaseId TEXT NOT NULL PRIMARY KEY,
            ScheduleEntryId TEXT NULL,
            WorkflowRunId TEXT NOT NULL,
            NodeExecutionId TEXT NULL,
            ResourceType TEXT NOT NULL,
            ResourceId TEXT NOT NULL,
            ResourceKey TEXT NOT NULL,
            ActiveResourceKey TEXT NULL,
            Status TEXT NOT NULL,
            AcquiredBy TEXT NOT NULL,
            AcquiredAtUtc TEXT NOT NULL,
            ExpiresAtUtc TEXT NOT NULL,
            ReleasedBy TEXT NULL,
            ReleaseReason TEXT NULL,
            ReleasedAtUtc TEXT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS ExperimentSchedulingAudits (
            Id TEXT NOT NULL PRIMARY KEY,
            EventType TEXT NOT NULL,
            Outcome TEXT NOT NULL,
            Code TEXT NULL,
            RequestId TEXT NOT NULL,
            RequestFingerprint TEXT NOT NULL,
            Actor TEXT NOT NULL,
            Reason TEXT NOT NULL,
            PlanId TEXT NULL,
            PlanVersion INTEGER NULL,
            ExperimentJobId TEXT NULL,
            ScheduleEntryId TEXT NULL,
            DetailsJson TEXT NOT NULL,
            ResultJson TEXT NOT NULL,
            OccurredAtUtc TEXT NOT NULL
        );
        """
    };

    foreach (var statement in tableStatements)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }

    await EnsureExperimentSchedulingColumnsAsync(connection);

    var indexStatements = new[]
    {
        "CREATE INDEX IF NOT EXISTS IX_ExperimentPlans_Status_UpdatedAtUtc ON ExperimentPlans (Status, UpdatedAtUtc);",
        "CREATE INDEX IF NOT EXISTS IX_ExperimentPlans_WorkflowId_WorkflowVersion ON ExperimentPlans (WorkflowId, WorkflowVersion);",
        "CREATE INDEX IF NOT EXISTS IX_ExperimentJobs_Status_CreatedAtUtc ON ExperimentJobs (Status, CreatedAtUtc);",
        "CREATE INDEX IF NOT EXISTS IX_ExperimentJobs_PlanId_PlanVersion ON ExperimentJobs (PlanId, PlanVersion);",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_ExperimentJobs_WorkflowRunId ON ExperimentJobs (WorkflowRunId);",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_ExperimentRuns_ExperimentJobId ON ExperimentRuns (ExperimentJobId);",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_ExperimentRuns_AdmissionRequestId ON ExperimentRuns (AdmissionRequestId);",
        "CREATE INDEX IF NOT EXISTS IX_ExperimentRuns_Status_UpdatedAtUtc ON ExperimentRuns (Status, UpdatedAtUtc);",
        "CREATE INDEX IF NOT EXISTS IX_ScheduleEntries_Status_PlannedStartUtc_Priority ON ScheduleEntries (Status, PlannedStartUtc, Priority);",
        "CREATE INDEX IF NOT EXISTS IX_ScheduleEntries_ExperimentJobId ON ScheduleEntries (ExperimentJobId);",
        "CREATE INDEX IF NOT EXISTS IX_ResourceReservations_ScheduleEntryId ON ResourceReservations (ScheduleEntryId);",
        "CREATE INDEX IF NOT EXISTS IX_ResourceReservations_ResourceKey_StartsAtUtc_EndsAtUtc ON ResourceReservations (ResourceKey, StartsAtUtc, EndsAtUtc);",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_WorkflowResourceLeases_ActiveResourceKey ON WorkflowResourceLeases (ActiveResourceKey);",
        "CREATE INDEX IF NOT EXISTS IX_WorkflowResourceLeases_WorkflowRunId_AcquiredAtUtc ON WorkflowResourceLeases (WorkflowRunId, AcquiredAtUtc);",
        "CREATE INDEX IF NOT EXISTS IX_WorkflowResourceLeases_ScheduleEntryId ON WorkflowResourceLeases (ScheduleEntryId);",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_ExperimentSchedulingAudits_RequestId ON ExperimentSchedulingAudits (RequestId);",
        "CREATE INDEX IF NOT EXISTS IX_ExperimentSchedulingAudits_PlanId_PlanVersion_OccurredAtUtc ON ExperimentSchedulingAudits (PlanId, PlanVersion, OccurredAtUtc);",
        "CREATE INDEX IF NOT EXISTS IX_ExperimentSchedulingAudits_ExperimentJobId_OccurredAtUtc ON ExperimentSchedulingAudits (ExperimentJobId, OccurredAtUtc);",
        "CREATE INDEX IF NOT EXISTS IX_ExperimentSchedulingAudits_ScheduleEntryId_OccurredAtUtc ON ExperimentSchedulingAudits (ScheduleEntryId, OccurredAtUtc);"
    };

    foreach (var statement in indexStatements)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }
}

static async Task EnsureExperimentSchedulingColumnsAsync(System.Data.Common.DbConnection connection)
{
    await EnsureColumnsAsync(
        connection,
        "ExperimentPlans",
        [
            (Name: "WorkflowStepsJson", Sql: "TEXT NOT NULL DEFAULT '[]'"),
            (Name: "ValidationJson", Sql: "TEXT NULL"),
            (Name: "ValidatedBy", Sql: "TEXT NULL"),
            (Name: "ValidatedAtUtc", Sql: "TEXT NULL")
        ]);
    await EnsureColumnsAsync(
        connection,
        "ScheduleEntries",
        [
            (Name: "RequestedResourcesJson", Sql: "TEXT NOT NULL DEFAULT '[]'")
        ]);
    await EnsureColumnsAsync(
        connection,
        "ExperimentJobs",
        [
            (Name: "WorkflowStepsJson", Sql: "TEXT NOT NULL DEFAULT '[]'")
        ]);
}

static async Task EnsureColumnsAsync(
    System.Data.Common.DbConnection connection,
    string tableName,
    IReadOnlyList<(string Name, string Sql)> definitions)
{
    await using var command = connection.CreateCommand();
    command.CommandText = $"PRAGMA table_info({tableName});";
    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
    await reader.CloseAsync();

    foreach (var definition in definitions)
    {
        if (columns.Contains(definition.Name)) continue;
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {definition.Name} {definition.Sql};";
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
            DeviceEpoch INTEGER NULL,
            ReadinessSupervisorInstanceId TEXT NULL,
            WorkflowRunId TEXT NULL,
            WorkflowNodeExecutionId TEXT NULL,
            WorkflowDeviceOperationId TEXT NULL,
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

    await EnsureColumnsAsync(
        connection,
        "FieldNavigationAcceptances",
        [
            (Name: "WorkflowRunId", Sql: "TEXT NULL"),
            (Name: "WorkflowNodeExecutionId", Sql: "TEXT NULL"),
            (Name: "WorkflowDeviceOperationId", Sql: "TEXT NULL"),
            (Name: "DeviceEpoch", Sql: "INTEGER NULL"),
            (Name: "ReadinessSupervisorInstanceId", Sql: "TEXT NULL")
        ]);

    foreach (var statement in new[]
    {
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_FieldNavigationAcceptances_WorkflowNodeExecutionId ON FieldNavigationAcceptances (WorkflowNodeExecutionId);",
        "CREATE INDEX IF NOT EXISTS IX_FieldNavigationAcceptances_WorkflowRunId ON FieldNavigationAcceptances (WorkflowRunId);"
    })
    {
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }
}

static async Task EnsureShineLabTablesAsync(MesDbContext database)
{
    var connection = database.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
    {
        await connection.OpenAsync();
    }

    var statements = new[]
    {
        """
        CREATE TABLE IF NOT EXISTS ShineLabTasks (
            Id TEXT NOT NULL PRIMARY KEY,
            TaskUuid TEXT NOT NULL,
            EquipmentCode TEXT NOT NULL,
            Status TEXT NOT NULL,
            CurrentStage TEXT NOT NULL,
            RequestFingerprint TEXT NOT NULL,
            ConfigJson TEXT NOT NULL,
            ConfigResponseJson TEXT NULL,
            CommandResponseJson TEXT NULL,
            ResultJson TEXT NULL,
            LastError TEXT NULL,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            StartedAtUtc TEXT NULL,
            CompletedAtUtc TEXT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS ShineLabTaskEvents (
            Id TEXT NOT NULL PRIMARY KEY,
            TaskUuid TEXT NOT NULL,
            EventType TEXT NOT NULL,
            PayloadJson TEXT NOT NULL,
            OccurredAtUtc TEXT NOT NULL
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_ShineLabTasks_TaskUuid ON ShineLabTasks (TaskUuid);",
        "CREATE INDEX IF NOT EXISTS IX_ShineLabTasks_Status_UpdatedAtUtc ON ShineLabTasks (Status, UpdatedAtUtc);",
        "CREATE INDEX IF NOT EXISTS IX_ShineLabTaskEvents_TaskUuid_OccurredAtUtc ON ShineLabTaskEvents (TaskUuid, OccurredAtUtc);"
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






