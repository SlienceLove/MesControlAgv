using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Wpf.Services;

public sealed record DashboardTask(
    Guid Id,
    int SourceStationCode,
    int TargetStationCode,
    string Status,
    int RetryCount,
    string? LastError,
    int Priority = 0,
    string? Description = null,
    string? ExternalId = null,
    DateTime CreatedAt = default,
    DateTime? EndedAt = null,
    string? ActiveAgvId = null,
    string? ActiveDeviceTaskId = null,
    IReadOnlyList<string>? ActivePath = null);

public sealed record AgvDashboardSnapshot(
    bool Online,
    string ControlOwner,
    string? CurrentStationId,
    Guid? CurrentTaskId,
    string AgvId = "AGV-01",
    AgvCapabilitiesResponse? Capabilities = null);

public sealed record AgvActiveTaskStatus(
    Guid TransportTaskId,
    Guid OperationId,
    string MesStatus,
    string? DeviceTaskId,
    string? DeviceState,
    string? TargetStationId,
    string? LastError,
    IReadOnlyList<string>? Path);

public sealed record AgvFleetDashboardStatus(
    AgvDashboardSnapshot Snapshot,
    AgvActiveTaskStatus? ActiveTask);

public sealed record AgvCommandResult(Guid TaskId, string DeviceTaskId, string TargetStationId, string State, string? LastError, string AgvId = "AGV-01", IReadOnlyList<string>? Path = null);
public sealed record DashboardTaskEvent(Guid Id, string EventType, string Payload, DateTime CreatedAt);
public sealed record DashboardTaskDetail(DashboardTask Task, IReadOnlyList<DashboardTaskEvent> Events);
public sealed record DashboardStation(int Code, string Name, string AgvStationId, bool Enabled, string? Type = null);
public sealed record DashboardRuntimeSettings(
    string? ProfileProductId,
    string? ProfileVersion,
    TimeSpan TaskRefreshInterval)
{
    public static DashboardRuntimeSettings Default { get; } = new(null, null, TimeSpan.FromSeconds(2));
}
public sealed record DashboardPlannedPath(
    IReadOnlyList<string> Stations,
    double Cost,
    string? SourceStationId = null,
    string? TargetStationId = null);
public sealed record DashboardMapSnapshot(
    IReadOnlyList<DashboardStation> Stations,
    IReadOnlyList<MapEdgeResponse> Edges,
    string? ProfileProductId,
    string? ProfileVersion,
    string? ProfileMapName,
    string? ProfileMapVersion,
    string? ProfileMapMd5);

public sealed record DashboardWorkflowNextStep(
    Guid StepRequestId,
    Guid ExecutionId,
    Guid WorkflowId,
    int Version,
    Guid NodeId,
    int NodeType,
    string NodeName,
    string? TargetStation,
    bool DryRun,
    IReadOnlyDictionary<string, string?> Parameters);

public sealed record DashboardWorkflowExecution(
    bool IsAccepted,
    bool IsIdempotentReplay,
    Guid RequestId,
    Guid ExecutionId,
    Guid WorkflowId,
    int Version,
    bool DryRun,
    string? RejectionCode,
    string? RejectionReason,
    DashboardWorkflowNextStep? NextStep);

public interface IMesClient
{
    Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DashboardTask>> GetTasksAsync(DateOnly date, CancellationToken cancellationToken) => GetTasksAsync(cancellationToken);
    Task<KpiDashboard> GetKpiDashboardAsync(DateOnly date, CancellationToken cancellationToken);
    Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DashboardStation>> GetStationsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DashboardStation>>([]);
    Task<DashboardRuntimeSettings> GetRuntimeSettingsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(DashboardRuntimeSettings.Default);
    Task<IonChromatographyControlCenterStatusResponse?> GetIonChromatographyStatusAsync(
        string instrumentId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IonChromatographyControlCenterStatusResponse?>(null);
    Task<IReadOnlyList<ShineLabDeviceStatusResponse>> GetShineLabDeviceStatusesAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ShineLabDeviceStatusResponse>>([]);
    Task<ShineLabCommandResponse> SendShineLabConfigAsync(
        string equipmentCode,
        ShineLabConfigRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<ShineLabCommandResponse>(new NotSupportedException("ShineLab Config is not supported by this MES client."));
    Task<ShineLabCommandResponse> SendShineLabCommandAsync(
        string equipmentCode,
        ShineLabCommandRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<ShineLabCommandResponse>(new NotSupportedException("ShineLab Command is not supported by this MES client."));
    Task<ShineLabTaskResponse> CreateShineLabTaskAsync(
        ShineLabTaskCreateRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<ShineLabTaskResponse>(new NotSupportedException("ShineLab tasks are not supported by this MES client."));
    Task<ShineLabTaskResponse> ConfigureShineLabTaskAsync(
        string taskUuid,
        CancellationToken cancellationToken) =>
        Task.FromException<ShineLabTaskResponse>(new NotSupportedException("ShineLab task configuration is not supported."));
    Task<ShineLabTaskResponse> SendShineLabTaskCommandAsync(
        string taskUuid,
        ShineLabCommandRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<ShineLabTaskResponse>(new NotSupportedException("ShineLab task commands are not supported."));
    Task<IReadOnlyList<ShineLabTaskResponse>> GetShineLabTasksAsync(
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ShineLabTaskResponse>>([]);
    Task<DashboardPlannedPath> PlanPathAsync(
        string fromStationId,
        string toStationId,
        IReadOnlyCollection<string>? blockedStations,
        CancellationToken cancellationToken) =>
        Task.FromException<DashboardPlannedPath>(new NotSupportedException("Path planning is not supported by this MES client."));
    Task<DashboardMapSnapshot> GetMapSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromException<DashboardMapSnapshot>(new NotSupportedException("Map snapshots are not supported by this MES client."));
    Task<PhysicalAgvPreflightResponse?> GetPhysicalPreflightAsync(CancellationToken cancellationToken) =>
        Task.FromException<PhysicalAgvPreflightResponse?>(new NotSupportedException("Physical preflight is not supported by this MES client."));
    Task<PhysicalReadinessResponse?> GetPhysicalReadinessAsync(CancellationToken cancellationToken) =>
        Task.FromResult<PhysicalReadinessResponse?>(null);
    Task<PhysicalReadinessResponse?> RefreshPhysicalReadinessAsync(
        bool forceFull,
        CancellationToken cancellationToken) =>
        GetPhysicalReadinessAsync(cancellationToken);
    Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken);
    async Task<IReadOnlyList<AgvDashboardSnapshot>> GetAgvFleetAsync(CancellationToken cancellationToken) => [await GetAgvSnapshotAsync(cancellationToken)];
    async Task<IReadOnlyList<AgvFleetDashboardStatus>> GetAgvFleetStatusAsync(CancellationToken cancellationToken) =>
        (await GetAgvFleetAsync(cancellationToken))
            .Select(snapshot => new AgvFleetDashboardStatus(snapshot, null))
            .ToList();
    Task<AgvCommandResult?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken cancellationToken) => Task.FromResult<AgvCommandResult?>(null);

    // AUBO program surface.  Defaults keep existing offline/test clients source
    // compatible while the production MesClient opts into the HTTP routes.
    Task<AuboArmStatusResponse?> GetAuboArmStatusAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        Task.FromResult<AuboArmStatusResponse?>(null);

    Task<AuboArmReadinessResponse?> GetAuboArmReadinessAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        Task.FromResult<AuboArmReadinessResponse?>(null);

    Task<AuboArmProgramStatusResponse?> GetAuboArmProgramAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        Task.FromResult<AuboArmProgramStatusResponse?>(null);

    Task<AuboArmProgramCatalogResponse?> GetAuboArmProgramCatalogAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        Task.FromResult<AuboArmProgramCatalogResponse?>(null);

    /// <summary>
    /// Reads the AUBO catalog, optionally bypassing the Adapter's short-lived
    /// cache. Existing clients keep the ordinary cached-read behavior.
    /// </summary>
    Task<AuboArmProgramCatalogResponse?> GetAuboArmProgramCatalogAsync(
        string deviceId,
        bool forceFresh,
        CancellationToken cancellationToken) =>
        GetAuboArmProgramCatalogAsync(deviceId, cancellationToken);

    Task<AuboArmProgramOperationResponse> LoadAuboProgramAsync(
        string deviceId,
        string programName,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        Task.FromException<AuboArmProgramOperationResponse>(
            new NotSupportedException("AUBO program control is not supported by this MES client."));

    Task<AuboArmProgramOperationResponse> RunAuboProgramAsync(
        string deviceId,
        string? programName,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        Task.FromException<AuboArmProgramOperationResponse>(
            new NotSupportedException("AUBO program control is not supported by this MES client."));

    Task<AuboArmProgramOperationResponse> StopAuboProgramAsync(
        string deviceId,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        Task.FromException<AuboArmProgramOperationResponse>(
            new NotSupportedException("AUBO program control is not supported by this MES client."));

    /// <summary>
    /// Additive overloads used by a workflow worker or an explicitly correlated
    /// control surface. Legacy/manual callers may continue to use the four
    /// argument methods above; production implementations forward the durable
    /// identity in the JSON body when supplied.
    /// </summary>
    Task<AuboArmProgramOperationResponse> LoadAuboProgramAsync(
        string deviceId,
        string programName,
        string operatorName,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken) =>
        LoadAuboProgramAsync(deviceId, programName, operatorName, operationId, cancellationToken);

    Task<AuboArmProgramOperationResponse> RunAuboProgramAsync(
        string deviceId,
        string? programName,
        string operatorName,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken) =>
        RunAuboProgramAsync(deviceId, programName, operatorName, operationId, cancellationToken);

    Task<AuboArmProgramOperationResponse> StopAuboProgramAsync(
        string deviceId,
        string operatorName,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken) =>
        StopAuboProgramAsync(deviceId, operatorName, operationId, cancellationToken);

    // More generic aliases make the boundary convenient for callers that refer to
    // the device family as a robot arm rather than by the AUBO vendor name.
    Task<AuboArmStatusResponse?> GetRobotArmStatusAsync(string deviceId, CancellationToken cancellationToken) =>
        GetAuboArmStatusAsync(deviceId, cancellationToken);
    Task<AuboArmReadinessResponse?> GetRobotArmReadinessAsync(string deviceId, CancellationToken cancellationToken) =>
        GetAuboArmReadinessAsync(deviceId, cancellationToken);
    Task<AuboArmProgramStatusResponse?> GetRobotArmProgramAsync(string deviceId, CancellationToken cancellationToken) =>
        GetAuboArmProgramAsync(deviceId, cancellationToken);
    Task<AuboArmProgramStatusResponse?> GetRobotArmProgramStatusAsync(string deviceId, CancellationToken cancellationToken) =>
        GetAuboArmProgramAsync(deviceId, cancellationToken);
    Task<AuboArmProgramCatalogResponse?> GetRobotArmProgramCatalogAsync(string deviceId, CancellationToken cancellationToken) =>
        GetAuboArmProgramCatalogAsync(deviceId, cancellationToken);
    Task<AuboArmProgramCatalogResponse?> GetRobotArmProgramCatalogAsync(
        string deviceId,
        bool forceFresh,
        CancellationToken cancellationToken) =>
        GetAuboArmProgramCatalogAsync(deviceId, forceFresh, cancellationToken);
    Task<AuboArmProgramOperationResponse> LoadRobotArmProgramAsync(
        string deviceId,
        string programName,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        LoadAuboProgramAsync(deviceId, programName, operatorName, operationId, cancellationToken);
    Task<AuboArmProgramOperationResponse> RunRobotArmProgramAsync(
        string deviceId,
        string? programName,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        RunAuboProgramAsync(deviceId, programName, operatorName, operationId, cancellationToken);
    Task<AuboArmProgramOperationResponse> StopRobotArmProgramAsync(
        string deviceId,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        StopAuboProgramAsync(deviceId, operatorName, operationId, cancellationToken);

    Task<AuboArmProgramOperationResponse> LoadRobotArmProgramAsync(
        string deviceId,
        string programName,
        string operatorName,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken) =>
        LoadAuboProgramAsync(deviceId, programName, operatorName, operationId, correlation, cancellationToken);

    Task<AuboArmProgramOperationResponse> RunRobotArmProgramAsync(
        string deviceId,
        string? programName,
        string operatorName,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken) =>
        RunAuboProgramAsync(deviceId, programName, operatorName, operationId, correlation, cancellationToken);

    Task<AuboArmProgramOperationResponse> StopRobotArmProgramAsync(
        string deviceId,
        string operatorName,
        Guid operationId,
        AuboArmOperationCorrelation? correlation,
        CancellationToken cancellationToken) =>
        StopAuboProgramAsync(deviceId, operatorName, operationId, correlation, cancellationToken);
    Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken);
    Task<DashboardTask> CreateTaskAsync(int sourceStationCode, int targetStationCode, int priority, string? description, string? externalId, CancellationToken cancellationToken);
    Task<DashboardTask> DispatchTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromException<DashboardTask>(new NotSupportedException("Task dispatch is not supported by this MES client."));
    Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken);
    Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken);
    Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken);
    Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken);
    Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken);
    Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowsAsync(CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<WorkflowDefinition>>(new NotSupportedException("Workflow APIs are not supported by this MES client."));

    Task<WorkflowDefinition?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken) =>
        Task.FromException<WorkflowDefinition?>(new NotSupportedException("Workflow APIs are not supported by this MES client."));

    Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(Guid workflowId, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<WorkflowVersion>>(new NotSupportedException("Workflow APIs are not supported by this MES client."));

    Task<WorkflowVersion?> GetWorkflowVersionAsync(Guid workflowId, int version, CancellationToken cancellationToken) =>
        Task.FromException<WorkflowVersion?>(new NotSupportedException("Workflow APIs are not supported by this MES client."));

    Task<WorkflowVersion> CreateWorkflowDraftAsync(WorkflowDefinition definition, string actor, CancellationToken cancellationToken) =>
        Task.FromException<WorkflowVersion>(new NotSupportedException("Workflow APIs are not supported by this MES client."));

    Task<WorkflowVersion> UpdateWorkflowDraftAsync(Guid workflowId, int version, WorkflowDefinition definition, string actor, CancellationToken cancellationToken) =>
        Task.FromException<WorkflowVersion>(new NotSupportedException("Workflow APIs are not supported by this MES client."));

    Task<WorkflowValidationResult> ValidateWorkflowAsync(WorkflowDefinition definition, CancellationToken cancellationToken) =>
        Task.FromException<WorkflowValidationResult>(new NotSupportedException("Workflow APIs are not supported by this MES client."));

    Task<WorkflowValidationResult> ValidateWorkflowVersionAsync(Guid workflowId, int version, CancellationToken cancellationToken) =>
        Task.FromException<WorkflowValidationResult>(new NotSupportedException("Workflow APIs are not supported by this MES client."));

    Task<WorkflowVersion> PublishWorkflowAsync(Guid workflowId, int version, string actor, CancellationToken cancellationToken) =>
        Task.FromException<WorkflowVersion>(new NotSupportedException("Workflow APIs are not supported by this MES client."));

    Task<WorkflowExecutionResult> ExecuteWorkflowAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken) =>
        Task.FromException<WorkflowExecutionResult>(new NotSupportedException("Workflow APIs are not supported by this MES client."));

    Task<WorkflowExecutionSnapshot?> GetWorkflowExecutionAsync(Guid executionId, CancellationToken cancellationToken) =>
        Task.FromResult<WorkflowExecutionSnapshot?>(null);

    Task<WorkflowExecutionSnapshot?> GetWorkflowExecutionByRequestAsync(Guid requestId, CancellationToken cancellationToken) =>
        Task.FromResult<WorkflowExecutionSnapshot?>(null);

    Task<IReadOnlyList<WorkflowNodeExecutionSnapshot>> GetWorkflowNodeExecutionsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkflowNodeExecutionSnapshot>>([]);

    Task<IReadOnlyList<WorkflowDeviceOperationSnapshot>> GetWorkflowDeviceOperationsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkflowDeviceOperationSnapshot>>([]);

    Task<IReadOnlyList<WorkflowRunTimelineEntry>> GetWorkflowRunTimelineAsync(
        Guid workflowRunId,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkflowRunTimelineEntry>>([]);

    Task<IReadOnlyList<FieldNavigationAcceptanceResponse>> GetWorkflowFieldNavigationAcceptancesAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FieldNavigationAcceptanceResponse>>([]);

    Task<FieldNavigationAcceptanceResponse> CreateFieldNavigationAcceptanceAsync(
        CreateFieldNavigationAcceptanceRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<FieldNavigationAcceptanceResponse>(
            new NotSupportedException("Field-navigation acceptance APIs are not supported by this MES client."));

    Task<FieldNavigationAcceptanceResponse> AuthorizeFieldNavigationAcceptanceAsync(
        Guid acceptanceId,
        AuthorizeFieldNavigationAcceptanceRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<FieldNavigationAcceptanceResponse>(
            new NotSupportedException("Field-navigation acceptance APIs are not supported by this MES client."));

    Task<WorkflowRunControlPermissionsSnapshot> GetWorkflowRunControlPermissionsAsync(
        string actor,
        CancellationToken cancellationToken) =>
        Task.FromResult(new WorkflowRunControlPermissionsSnapshot { Actor = actor });

    Task<WorkflowRunControlResult> PauseWorkflowRunAsync(
        Guid workflowRunId,
        WorkflowRunControlRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<WorkflowRunControlResult>(new NotSupportedException("Workflow run controls are not supported by this MES client."));

    Task<WorkflowRunControlResult> ResumeWorkflowRunAsync(
        Guid workflowRunId,
        WorkflowRunControlRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<WorkflowRunControlResult>(new NotSupportedException("Workflow run controls are not supported by this MES client."));

    Task<WorkflowRunControlResult> CancelWorkflowRunAsync(
        Guid workflowRunId,
        WorkflowRunControlRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<WorkflowRunControlResult>(new NotSupportedException("Workflow run controls are not supported by this MES client."));

    Task<WorkflowRunControlResult> ResolveWorkflowRunUnknownAsync(
        Guid workflowRunId,
        WorkflowUnknownResolutionRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<WorkflowRunControlResult>(new NotSupportedException("Workflow run controls are not supported by this MES client."));

    Task<IReadOnlyList<WorkflowAuditResponse>> GetWorkflowAuditsAsync(
        Guid workflowId,
        int? version,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkflowAuditResponse>>([]);

    Task<IReadOnlyList<ExperimentPlan>> GetExperimentPlansAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExperimentPlan>>([]);

    Task<IReadOnlyList<ExperimentPlan>> GetExperimentPlanVersionsAsync(
        Guid planId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExperimentPlan>>([]);

    Task<ExperimentPlan?> GetExperimentPlanAsync(
        Guid planId,
        int version,
        CancellationToken cancellationToken) =>
        Task.FromResult<ExperimentPlan?>(null);

    Task<ExperimentPlan> CreateExperimentPlanDraftAsync(
        SaveExperimentPlanDraftRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<ExperimentPlan>(new NotSupportedException("Experiment planning APIs are not supported by this MES client."));

    Task<ExperimentPlan> UpdateExperimentPlanDraftAsync(
        Guid planId,
        int version,
        SaveExperimentPlanDraftRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<ExperimentPlan>(new NotSupportedException("Experiment planning APIs are not supported by this MES client."));

    Task<ExperimentPlan> ValidateExperimentPlanAsync(
        Guid planId,
        int version,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<ExperimentPlan>(new NotSupportedException("Experiment planning APIs are not supported by this MES client."));

    Task<ExperimentPlan> PublishExperimentPlanAsync(
        Guid planId,
        int version,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<ExperimentPlan>(new NotSupportedException("Experiment planning APIs are not supported by this MES client."));

    Task<ExperimentPlan> CreateNextExperimentPlanDraftAsync(
        Guid planId,
        int sourceVersion,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<ExperimentPlan>(new NotSupportedException("Experiment planning APIs are not supported by this MES client."));

    Task<IReadOnlyList<ExperimentJob>> GetExperimentJobsAsync(
        ExperimentJobStatus? status,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExperimentJob>>([]);

    Task<ExperimentJob?> GetExperimentJobAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        Task.FromResult<ExperimentJob?>(null);

    Task<ExperimentRun?> GetExperimentRunAsync(
        Guid experimentRunId,
        CancellationToken cancellationToken) =>
        Task.FromResult<ExperimentRun?>(null);

    Task<ExperimentRun?> GetExperimentRunForJobAsync(
        Guid experimentJobId,
        CancellationToken cancellationToken) =>
        Task.FromResult<ExperimentRun?>(null);

    Task<ExperimentRun> PrepareExperimentRunAsync(
        PrepareExperimentRunRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<ExperimentRun>(
            new NotSupportedException("Composite experiment runtime APIs are not supported by this MES client."));

    Task<ExperimentRun> ReconcileExperimentChildAsync(
        Guid experimentRunId,
        ReconcileExperimentChildRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<ExperimentRun>(
            new NotSupportedException("Composite child reconciliation APIs are not supported by this MES client."));

    Task<ExperimentJob> CreateExperimentJobAsync(
        CreateExperimentJobRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<ExperimentJob>(new NotSupportedException("Experiment scheduling APIs are not supported by this MES client."));

    Task<ExperimentScheduleSnapshot> GetExperimentScheduleAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ExperimentScheduleSnapshot());

    Task<IReadOnlyList<ExperimentResourceAvailability>> GetExperimentResourceAvailabilityAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExperimentResourceAvailability>>([]);

    Task<IReadOnlyList<ExperimentSchedulingAuditEntry>> GetExperimentSchedulingAuditsAsync(
        Guid? planId,
        Guid? experimentJobId,
        Guid? scheduleEntryId,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExperimentSchedulingAuditEntry>>([]);

    Task<ScheduleEntry> ScheduleExperimentJobAsync(
        Guid jobId,
        ScheduleExperimentJobRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<ScheduleEntry>(new NotSupportedException("Experiment scheduling APIs are not supported by this MES client."));

    Task<ExperimentJob> UnscheduleExperimentJobAsync(
        Guid jobId,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<ExperimentJob>(new NotSupportedException("Experiment scheduling APIs are not supported by this MES client."));

    Task<ExperimentJob> CancelExperimentJobAsync(
        Guid jobId,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<ExperimentJob>(new NotSupportedException("Experiment scheduling APIs are not supported by this MES client."));

    Task<ExperimentJobAdmissionResult> AdmitExperimentJobAsync(
        Guid jobId,
        AdmitExperimentJobRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<ExperimentJobAdmissionResult>(new NotSupportedException("Experiment runtime admission APIs are not supported by this MES client."));
}
