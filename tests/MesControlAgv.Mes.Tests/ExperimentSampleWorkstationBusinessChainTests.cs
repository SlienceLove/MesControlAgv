using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MesControlAgv.Mes.Tests;

public sealed class ExperimentSampleWorkstationBusinessChainTests
{
    [Fact]
    public async Task Formal_experiment_admission_dispatches_and_closes_the_sample_workstation_business_chain()
    {
        var gateway = new RecordingWorkstation();
        using var factory = ConfigureGateway(new PhysicalMesWebApplicationFactory(PhysicalProfile()), gateway);
        using var client = factory.CreateClient();
        var workflow = await PublishWorkflowAsync(client);
        var plan = await CreatePublishedPlanAsync(client, workflow);
        var scheduled = await CreateScheduledJobAsync(client, plan);
        var verification = await VerifyCurrentSampleAsync(client, scheduled.JobId, "TEST-001");

        var admissionResponse = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/admit", Action("Admit TEST-001"));
        Assert.Equal(HttpStatusCode.Accepted, admissionResponse.StatusCode);
        var admitted = (await admissionResponse.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>())!;
        Assert.True(admitted.IsAdmitted);
        Assert.NotNull(admitted.WorkflowRunId);
        Assert.Equal(ExperimentJobStatus.Admitted, admitted.Job!.Status);
        Assert.Equal(ScheduleEntryStatus.Admitted, admitted.ScheduleEntry!.Status);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            Assert.Single(await db.WorkflowExecutions.AsNoTracking().ToListAsync());
            var lease = Assert.Single(await db.WorkflowResourceLeases.AsNoTracking().Where(x => x.ActiveResourceKey != null).ToListAsync());
            Assert.Equal(admitted.WorkflowRunId, lease.WorkflowRunId);
            var admissionAudit = Assert.Single(await db.ExperimentSchedulingAudits.AsNoTracking()
                .Where(audit => audit.RequestId == admitted.RequestId).ToListAsync());
            Assert.Contains(verification.VerificationId.ToString("D"), admissionAudit.DetailsJson);
            Assert.Contains(verification.Revision.ToString(), admissionAudit.DetailsJson);
            Assert.Contains(verification.SnapshotHash, admissionAudit.DetailsJson);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var dispatcher = new WorkflowSampleWorkstationDispatcher(
                scope.ServiceProvider.GetRequiredService<IWorkflowApplicationService>(), gateway, gateway,
                PhysicalProfile(), new WorkflowSampleWorkstationWorkerOptions
                { Enabled = true, PollIntervalMs = 1, ReadinessRetryIntervalMs = 1, StartObservationTimeoutMs = 5000, CompletionTimeoutMs = 5000 });
            await dispatcher.ProcessAsync(CancellationToken.None);
        }

        Assert.Equal(1, gateway.StartCalls);
        Assert.Equal(0, gateway.InitializeCalls);
        Assert.Equal("SAMPLE-WORKSTATION-01", gateway.StartDeviceId);
        Assert.Equal("TEST-001", gateway.StartTaskNo);
        Assert.Equal(
        [
            new RecordingWorkstation.ConsumedObservation("SAMPLE-WORKSTATION-01", "EQ-01", SampleWorkstationDeviceState.Idle, 0, 0, "TEST-001", SampleWorkstationTaskState.Completed, "Completed"),
            new RecordingWorkstation.ConsumedObservation("SAMPLE-WORKSTATION-01", "EQ-01", SampleWorkstationDeviceState.Running, 1, 3, "TEST-001", SampleWorkstationTaskState.Running, "Running"),
            new RecordingWorkstation.ConsumedObservation("SAMPLE-WORKSTATION-01", "EQ-01", SampleWorkstationDeviceState.Idle, 0, 0, "TEST-001", SampleWorkstationTaskState.Completed, "Completed")
        ],
        gateway.ConsumedObservations);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var run = Assert.Single(await db.WorkflowExecutions.AsNoTracking().ToListAsync());
            Assert.Equal(WorkflowRuntimeStatus.Completed.ToString(), run.RuntimeStatus);
            Assert.Equal(WorkflowNodeExecutionStatus.Succeeded.ToString(), (await db.WorkflowNodeExecutions.SingleAsync(x => x.WorkflowRunId == run.ExecutionId && x.NodeTypeId == WorkflowGraphNodeTypeIds.SampleWorkstationExecuteExistingTask)).Status);
            var operation = Assert.Single(await db.WorkflowDeviceOperations.AsNoTracking().Where(x => x.WorkflowRunId == run.ExecutionId).ToListAsync());
            Assert.Equal(WorkflowDeviceOperationStatus.Succeeded.ToString(), operation.Status);
            Assert.Equal(ExperimentJobStatus.Completed.ToString(), (await db.ExperimentJobs.SingleAsync(x => x.JobId == scheduled.JobId)).Status);
            Assert.Equal(ScheduleEntryStatus.Completed.ToString(), (await db.ScheduleEntries.SingleAsync(x => x.ScheduleEntryId == scheduled.ScheduleId)).Status);
            var lease = await db.WorkflowResourceLeases.SingleAsync(x => x.WorkflowRunId == run.ExecutionId);
            Assert.Equal(ResourceLeaseStatus.Released.ToString(), lease.Status);
            Assert.Null(lease.ActiveResourceKey);
        }

        var schedule = (await (await client.GetAsync("/api/schedule")).Content.ReadFromJsonAsync<ExperimentScheduleSnapshot>())!;
        var activity = Assert.Single(schedule.Activities);
        Assert.Equal(scheduled.JobId, activity.ExperimentJobId);
        Assert.Equal(admitted.WorkflowRunId, activity.WorkflowRunId);
        Assert.Equal(ExperimentResourceTypeIds.Workstation, activity.Resource.ResourceType);
        Assert.Equal("SAMPLE-WORKSTATION-01", activity.Resource.ResourceId);
        Assert.Equal(WorkflowDeviceOperationStatus.Succeeded.ToString(), activity.Status);
        Assert.NotNull(activity.ActualEnd);
    }

    [Fact]
    public async Task Workstation_admission_without_a_verified_snapshot_is_rejected_before_runtime_side_effects()
    {
        var gateway = new RecordingWorkstation();
        using var factory = ConfigureGateway(new PhysicalMesWebApplicationFactory(PhysicalProfile()), gateway);
        using var client = factory.CreateClient();
        var workflow = await PublishWorkflowAsync(client);
        var plan = await CreatePublishedPlanAsync(client, workflow);
        var scheduled = await CreateScheduledJobAsync(client, plan);
        var request = Action("Reject missing sample verification");

        var response = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/admit", request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var rejected = (await response.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>())!;
        Assert.True(rejected.IsRejected);
        Assert.Equal(ExperimentSampleVerificationIssueCodes.VerificationRequired, rejected.RejectionCode);
        Assert.Null(rejected.WorkflowRunId);
        Assert.Equal(0, gateway.StartCalls);
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Empty(await database.WorkflowExecutions.ToListAsync());
        Assert.Empty(await database.WorkflowResourceLeases.ToListAsync());
        Assert.Single(await database.ExperimentSchedulingAudits.Where(audit => audit.RequestId == request.RequestId && audit.EventType == "ExperimentJobAdmissionRejected").ToListAsync());
    }

    private static WebApplicationFactory<Program> ConfigureGateway(WebApplicationFactory<Program> factory, RecordingWorkstation gateway) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISampleWorkstationReader>(); services.RemoveAll<ISampleWorkstationCommands>();
            services.AddSingleton<ISampleWorkstationReader>(gateway); services.AddSingleton<ISampleWorkstationCommands>(gateway);
        }));

    private static ProfileConfiguration PhysicalProfile() => ProfileConfiguration.Default with
    {
        Features = ProfileConfiguration.Default.Features with { UseSimulator = false },
        WorkflowDevices = ProfileConfiguration.Default.WorkflowDevices.Concat([new WorkflowDeviceProfile
        { DeviceId = "SAMPLE-WORKSTATION-01", DeviceFamily = WorkflowDeviceFamilyIds.SampleWorkstation, Enabled = true, ControlEnabled = true, CapabilityIds = [WorkflowCapabilityIds.SampleWorkstationStartExistingTask] }]).ToArray()
    };

    private static async Task<WorkflowVersion> PublishWorkflowAsync(HttpClient client)
    {
        var create = await client.PostAsJsonAsync("/api/workflows?actor=test", WorkstationWorkflow()); create.EnsureSuccessStatusCode();
        var draft = (await create.Content.ReadFromJsonAsync<WorkflowVersion>())!;
        (await client.PostAsync($"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/validate", null)).EnsureSuccessStatusCode();
        var publish = await client.PostAsync($"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/publish?actor=test", null); publish.EnsureSuccessStatusCode();
        return (await publish.Content.ReadFromJsonAsync<WorkflowVersion>())!;
    }

    private static async Task<ExperimentPlan> CreatePublishedPlanAsync(HttpClient client, WorkflowVersion workflow)
    {
        var response = await client.PostAsJsonAsync("/api/experiment-plans", new SaveExperimentPlanDraftRequest { RequestId = Guid.NewGuid(), Actor = "test", Reason = "Create", Draft = new ExperimentPlanDraft { Name = "Sample workstation plan", WorkflowId = workflow.WorkflowId, WorkflowVersion = workflow.Version, ProfileProductId = "MES-AGV", ProfileVersion = "1.0", ResourceRequirements = [new ExperimentResourceRequirement { ResourceType = ExperimentResourceTypeIds.Workstation, ResourceId = "SAMPLE-WORKSTATION-01" }] } }); response.EnsureSuccessStatusCode();
        var draft = (await response.Content.ReadFromJsonAsync<ExperimentPlan>())!;
        (await client.PostAsJsonAsync($"/api/experiment-plans/{draft.PlanId}/versions/{draft.Version}/validate", Action("Validate"))).EnsureSuccessStatusCode();
        var publish = await client.PostAsJsonAsync($"/api/experiment-plans/{draft.PlanId}/versions/{draft.Version}/publish", Action("Publish")); publish.EnsureSuccessStatusCode();
        return (await publish.Content.ReadFromJsonAsync<ExperimentPlan>())!;
    }

    private static async Task<(Guid JobId, Guid ScheduleId)> CreateScheduledJobAsync(HttpClient client, ExperimentPlan plan)
    {
        var create = await client.PostAsJsonAsync("/api/experiment-jobs", new CreateExperimentJobRequest { RequestId = Guid.NewGuid(), Actor = "test", Reason = "Create", PlanId = plan.PlanId, PlanVersion = plan.Version, SampleBatchId = "TEST-001" }); create.EnsureSuccessStatusCode();
        var job = (await create.Content.ReadFromJsonAsync<ExperimentJob>())!;
        var start = new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
        var schedule = await client.PutAsJsonAsync($"/api/experiment-jobs/{job.JobId}/schedule", new ScheduleExperimentJobRequest { RequestId = Guid.NewGuid(), Actor = "test", Reason = "Schedule", PlannedStart = start, PlannedEnd = start.AddHours(1), Resources = [new ExperimentResourceReference { ResourceType = ExperimentResourceTypeIds.Workstation, ResourceId = "SAMPLE-WORKSTATION-01" }] }); schedule.EnsureSuccessStatusCode();
        return (job.JobId, (await schedule.Content.ReadFromJsonAsync<ScheduleEntry>())!.ScheduleEntryId);
    }

    private static async Task<ExperimentSampleVerification> VerifyCurrentSampleAsync(HttpClient client, Guid jobId, string batchId)
    {
        var sampleId = Guid.NewGuid();
        (await client.PutAsJsonAsync($"/api/experiment-samples/{sampleId}", new SaveExperimentSampleRequest
        {
            RequestId = Guid.NewGuid(), Actor = "test", Reason = "Register workstation sample",
            Sample = new ExperimentSample { SampleId = sampleId, BusinessSampleId = "S-" + sampleId.ToString("N"), BatchId = batchId, Barcode = "BC-" + sampleId.ToString("N"), Status = ExperimentSampleStatus.Active }
        })).EnsureSuccessStatusCode();
        var savedResponse = await client.PutAsJsonAsync($"/api/experiment-jobs/{jobId}/sample-verifications/current", new SaveExperimentSampleVerificationRequest
        {
            RequestId = Guid.NewGuid(), Actor = "test", Reason = "Snapshot workstation sample",
            Rows = [new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = sampleId, SampleBarcode = "BC-" + sampleId.ToString("N"), Position = "A1", Order = 1 }]
        });
        savedResponse.EnsureSuccessStatusCode();
        var saved = (await savedResponse.Content.ReadFromJsonAsync<ExperimentSampleVerification>())!;
        var verifiedResponse = await client.PostAsJsonAsync($"/api/experiment-jobs/{jobId}/sample-verifications/{saved.Revision}/verify", new CompleteExperimentSampleVerificationRequest
        {
            RequestId = Guid.NewGuid(), Actor = "test", Reason = "Verify workstation sample", Revision = saved.Revision, SnapshotHash = saved.SnapshotHash
        });
        verifiedResponse.EnsureSuccessStatusCode();
        return (await verifiedResponse.Content.ReadFromJsonAsync<ExperimentSampleVerification>())!;
    }

    private static ExperimentSchedulingActionRequest Action(string reason) => new() { RequestId = Guid.NewGuid(), Actor = "test", Reason = reason };

    private static WorkflowDefinition WorkstationWorkflow()
    {
        var catalog = BuiltInWorkflowCatalog.Create();
        WorkflowNode Node(string type, int order, IReadOnlyDictionary<string, string?>? config = null) { var d = catalog.NodeTypes.GetLatest(type)!; return new WorkflowNode { Id = Guid.NewGuid(), Type = WorkflowGraphNodeTypeIds.ToContractType(type), NodeTypeId = type, SchemaVersion = d.SchemaVersion, Name = type, Order = order, Ports = d.Ports, Configuration = config ?? new Dictionary<string, string?>() }; }
        var start = Node(WorkflowGraphNodeTypeIds.Start, 1); var station = Node(WorkflowGraphNodeTypeIds.SampleWorkstationExecuteExistingTask, 2, new Dictionary<string, string?> { [WorkflowNodeConfigurationKeys.DeviceId] = "SAMPLE-WORKSTATION-01", [WorkflowNodeConfigurationKeys.TaskNo] = "TEST-001" }); var end = Node(WorkflowGraphNodeTypeIds.End, 3);
        start = start with { NextNodeIds = [station.Id] }; station = station with { NextNodeIds = [end.Id] };
        return new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Sample workstation", SchemaVersion = WorkflowGraphDocument.CurrentSchemaVersion, Nodes = [start, station, end], Edges = [Edge(start, station), Edge(station, end)] };
    }
    private static WorkflowEdgeDefinition Edge(WorkflowNode source, WorkflowNode target) => new() { SourceNodeId = source.Id, SourcePort = "success", TargetNodeId = target.Id, TargetPort = "in", Kind = WorkflowEdgeKind.Success };

    private sealed class RecordingWorkstation : ISampleWorkstationReader, ISampleWorkstationCommands
    {
        private int _observation; public int StartCalls { get; private set; } public int InitializeCalls { get; private set; }
        public string? StartDeviceId { get; private set; } public string? StartTaskNo { get; private set; }
        public List<ConsumedObservation> ConsumedObservations { get; } = [];
        private (SampleWorkstationDeviceState State, int Raw, int Error, SampleWorkstationTaskState Task) Current => _observation++ switch { 0 => (SampleWorkstationDeviceState.Idle, 0, 0, SampleWorkstationTaskState.Completed), 1 => (SampleWorkstationDeviceState.Running, 1, 3, SampleWorkstationTaskState.Running), _ => (SampleWorkstationDeviceState.Idle, 0, 0, SampleWorkstationTaskState.Completed) };
        private (SampleWorkstationDeviceState State, int Raw, int Error, SampleWorkstationTaskState Task)? _snapshot;
        private (SampleWorkstationDeviceState State, int Raw, int Error, SampleWorkstationTaskState Task) Snapshot => _snapshot ??= Current;
        public Task<SampleWorkstationStatusResponse> GetStatusAsync(string id, CancellationToken ct) { _snapshot = Current; return Task.FromResult(new SampleWorkstationStatusResponse(id, "EQ-01", true, Snapshot.State, Snapshot.Raw, DateTimeOffset.UtcNow)); }
        public Task<SampleWorkstationErrorResponse> GetErrorsAsync(string id, CancellationToken ct) => Task.FromResult(new SampleWorkstationErrorResponse(id, Snapshot.Error, "result", true, DateTimeOffset.UtcNow));
        public Task<SampleWorkstationTaskStateResponse> GetTaskStateAsync(string id, string task, CancellationToken ct)
        {
            ConsumedObservations.Add(new ConsumedObservation(id, "EQ-01", Snapshot.State, Snapshot.Raw, Snapshot.Error, task, Snapshot.Task, Snapshot.Task.ToString()));
            return Task.FromResult(new SampleWorkstationTaskStateResponse(id, task, Snapshot.Task, Snapshot.Task.ToString(), DateTimeOffset.UtcNow));
        }
        public Task<SampleWorkstationCommandResponse> InitializeAsync(string id, CancellationToken ct) { InitializeCalls++; throw new InvalidOperationException(); }
        public Task<SampleWorkstationCommandResponse> StartTaskAsync(string id, string task, CancellationToken ct) { StartCalls++; StartDeviceId = id; StartTaskNo = task; using var json = JsonDocument.Parse("null"); return Task.FromResult(new SampleWorkstationCommandResponse(id, SampleWorkstationCommandOperation.StartTask, 0, json.RootElement.Clone(), DateTimeOffset.UtcNow) { TaskNo = task, Acknowledged = true }); }
        public Task<IReadOnlyList<SampleWorkstationTaskSummaryResponse>> GetTasksAsync(string id, SampleWorkstationTaskQuery query, CancellationToken ct) => throw new NotSupportedException(); public Task<SampleWorkstationTaskDetailsResponse> GetTaskDetailsAsync(string id, string task, CancellationToken ct) => throw new NotSupportedException(); public Task<SampleWorkstationProtocolResponse> GetProtocolReadAsync(string id, SampleWorkstationProtocolOperation op, SampleWorkstationProtocolReadQuery query, CancellationToken ct) => throw new NotSupportedException();

        public sealed record ConsumedObservation(
            string DeviceId,
            string EquipmentId,
            SampleWorkstationDeviceState DeviceState,
            int RawDeviceState,
            int ErrorCode,
            string TaskNo,
            SampleWorkstationTaskState TaskState,
            string RawTaskState);
    }

    private sealed class PhysicalMesWebApplicationFactory(ProfileConfiguration profile) : WebApplicationFactory<Program>
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"mes-control-agv-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<DbContextOptions<MesDbContext>>(); services.RemoveAll<MesDbContext>(); services.RemoveAll<IAgvGateway>();
                services.AddDbContext<MesDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
                services.AddSingleton<IAgvGateway, TestAdapterClient>();
            });
        }
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration =>
            {
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
                configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Profile = profile }, options))));
            });
            return base.CreateHost(builder);
        }
    }
}
