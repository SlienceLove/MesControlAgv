using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace MesControlAgv.Mes.Tests;

public sealed class FieldNavigationAcceptanceServiceTests
{
    [Fact]
    public async Task Physical_authorize_is_rejected_by_the_shared_gate_before_acceptance_lookup()
    {
        var readiness = new DisabledReadinessState();
        var policy = new PhysicalExecutionAdmissionPolicy(
            ProfileConfiguration.Default with
            {
                Features = ProfileConfiguration.Default.Features with { UseSimulator = false }
            },
            readiness,
            NullLogger<PhysicalExecutionAdmissionPolicy>.Instance);
        var service = CreateService(new FieldAcceptanceAdapter(), physicalReadiness: readiness, admissionPolicy: policy);

        var exception = await Assert.ThrowsAsync<PhysicalExecutionAdmissionException>(() => service.AuthorizeAsync(
            Guid.NewGuid(),
            new AuthorizeFieldNavigationAcceptanceRequest("operator", "observer", "permit", DateTimeOffset.UtcNow.AddMinutes(1)),
            CancellationToken.None));

        Assert.Equal(PhysicalReadinessReasonCodes.SupervisorDisabled, exception.Code);
    }

    [Fact]
    public async Task Draft_authorization_and_dispatch_persist_the_supervised_flow_and_audits()
    {
        var adapter = new FieldAcceptanceAdapter { DispatchState = "moving" };
        var service = CreateService(adapter);

        var draft = await service.CreateAsync(
            new CreateFieldNavigationAcceptanceRequest("AGV-01", "LM1", "LM2", "one leg"),
            CancellationToken.None);
        Assert.Equal(FieldNavigationAcceptanceStatuses.Draft, draft.Status);
        Assert.Equal(["LM1", "LM2"], draft.PlannedPath);

        var authorized = await service.AuthorizeAsync(
            draft.Id,
            new AuthorizeFieldNavigationAcceptanceRequest(
                "operator",
                "observer",
                "permit-001",
                DateTimeOffset.UtcNow.AddMinutes(10)),
            CancellationToken.None);
        Assert.Equal(FieldNavigationAcceptanceStatuses.Authorized, authorized.Status);

        var moving = await service.DispatchAsync(draft.Id, CancellationToken.None);

        Assert.Equal(FieldNavigationAcceptanceStatuses.Moving, moving.Status);
        Assert.Equal(draft.Id, adapter.LastAcceptanceId);
        Assert.Equal(1, adapter.DispatchCalls);
        var detail = await service.GetAsync(draft.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Contains(detail!.Audits, audit => audit.EventType == "Created");
        Assert.Contains(detail.Audits, audit => audit.EventType == "Authorized");
        Assert.Contains(detail.Audits, audit => audit.EventType == "DispatchRequested");
        Assert.Contains(detail.Audits, audit => audit.EventType == "DispatchConfirmed");
    }

    [Fact]
    public async Task Adapter_preflight_rejection_is_terminal_rejected_and_never_retried_as_motion()
    {
        var adapter = new FieldAcceptanceAdapter
        {
            DispatchException = new AdapterHttpException(HttpStatusCode.UnprocessableEntity, "map mismatch")
        };
        var service = CreateService(adapter);
        var draft = await CreateAuthorizedAsync(service, "permit-rejected");

        var rejected = await service.DispatchAsync(draft.Id, CancellationToken.None);

        Assert.Equal(FieldNavigationAcceptanceStatuses.Rejected, rejected.Status);
        Assert.Contains("map mismatch", rejected.LastError, StringComparison.Ordinal);
        Assert.Equal(1, adapter.DispatchCalls);
    }

    [Fact]
    public async Task Temporary_vendor_pause_remains_in_flight_without_a_second_dispatch()
    {
        var adapter = new FieldAcceptanceAdapter { DispatchState = "paused" };
        var service = CreateService(adapter);
        var authorized = await CreateAuthorizedAsync(service, "permit-paused");

        var paused = await service.DispatchAsync(authorized.Id, CancellationToken.None);

        Assert.Equal(FieldNavigationAcceptanceStatuses.Moving, paused.Status);
        Assert.Equal(1, adapter.DispatchCalls);
        Assert.NotEqual(FieldNavigationAcceptanceStatuses.Unknown, paused.Status);
    }

    [Fact]
    public async Task Route_not_present_in_the_approved_directed_edge_snapshot_is_rejected_offline()
    {
        var adapter = new FieldAcceptanceAdapter();
        var service = CreateService(adapter, includeApprovedEdge: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            new CreateFieldNavigationAcceptanceRequest("AGV-01", "LM1", "LM2"),
            CancellationToken.None));
        Assert.Equal(0, adapter.DispatchCalls);
    }

    [Fact]
    public async Task Timeout_is_unknown_and_expired_permit_is_blocked_before_adapter_call()
    {
        var adapter = new FieldAcceptanceAdapter
        {
            DispatchException = new TimeoutException("gateway timeout")
        };
        var service = CreateService(adapter);
        var authorized = await CreateAuthorizedAsync(service, "permit-timeout");

        var unknown = await service.DispatchAsync(authorized.Id, CancellationToken.None);
        Assert.Equal(FieldNavigationAcceptanceStatuses.Unknown, unknown.Status);
        Assert.Equal(1, adapter.DispatchCalls);

        var expired = await service.CreateAsync(
            new CreateFieldNavigationAcceptanceRequest("AGV-01", "LM1", "LM2"),
            CancellationToken.None);
        // Authorization rejects an already expired permit, so no device call can
        // be made with a stale safety permit.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.AuthorizeAsync(
            expired.Id,
            new AuthorizeFieldNavigationAcceptanceRequest(
                "operator", "observer", "permit-expired", DateTimeOffset.UtcNow.AddSeconds(-1)),
            CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Adapter_gateway_uncertainty_is_unknown_and_not_a_confirmed_failure(
        HttpStatusCode statusCode)
    {
        var adapter = new FieldAcceptanceAdapter
        {
            DispatchException = new AdapterHttpException(statusCode, "navigation confirmation unavailable")
        };
        var service = CreateService(adapter);
        var authorized = await CreateAuthorizedAsync(service, $"permit-{(int)statusCode}");

        var unknown = await service.DispatchAsync(authorized.Id, CancellationToken.None);

        Assert.Equal(FieldNavigationAcceptanceStatuses.Unknown, unknown.Status);
        Assert.Contains("confirmation unavailable", unknown.LastError, StringComparison.Ordinal);
        Assert.Equal(1, adapter.DispatchCalls);
        var persisted = await service.GetAsync(authorized.Id, CancellationToken.None);
        Assert.Contains(persisted!.Audits, audit => audit.EventType == "DispatchUnknown");
    }

    [Fact]
    public async Task Adapter_cancel_gateway_timeout_is_unknown_and_never_confirmed_cancelled()
    {
        var adapter = new FieldAcceptanceAdapter { DispatchState = "moving" };
        var service = CreateService(adapter);
        var authorized = await CreateAuthorizedAsync(service, "permit-cancel-timeout");
        var moving = await service.DispatchAsync(authorized.Id, CancellationToken.None);
        Assert.Equal(FieldNavigationAcceptanceStatuses.Moving, moving.Status);
        adapter.CancelException = new AdapterHttpException(
            HttpStatusCode.GatewayTimeout,
            "cancel confirmation unavailable");

        var unknown = await service.CancelAsync(authorized.Id, CancellationToken.None);

        Assert.Equal(FieldNavigationAcceptanceStatuses.Unknown, unknown.Status);
        Assert.Contains("confirmation unavailable", unknown.LastError, StringComparison.Ordinal);
        var persisted = await service.GetAsync(authorized.Id, CancellationToken.None);
        Assert.Contains(persisted!.Audits, audit => audit.EventType == "CancelUnknown");
    }

    [Fact]
    public async Task Authorized_permit_from_an_old_device_epoch_cannot_dispatch_after_power_cycle()
    {
        var adapter = new FieldAcceptanceAdapter();
        var state = new PhysicalReadinessStateStore(instanceId: "field-acceptance-test");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", "agv", true);
        var now = DateTimeOffset.UtcNow;
        state.Configure(true, [descriptor], now);
        state.Apply(
            descriptor,
            new PhysicalDeviceReadinessObservation
            {
                DeviceId = "AGV-01",
                DeviceFamily = "agv",
                ProbeSucceeded = true,
                Online = true,
                MapName = "acceptance-map",
                MapVersion = "1",
                MapMd5 = "0123456789abcdef0123456789abcdef",
                VehicleModel = "test",
                IsFullPreflight = true,
                FullPreflightPassed = true,
                ObservedAtUtc = now
            },
            TimeSpan.Zero,
            requireFullPreflight: true,
            now);
        var first = Assert.Single(state.GetSnapshot().Devices);
        Assert.True(state.AcknowledgeAuthorization(
            "AGV-01",
            first.DeviceEpoch,
            state.GetSnapshot().SupervisorInstanceId));

        var service = CreateService(adapter, physicalReadiness: state);
        var authorized = await CreateAuthorizedAsync(service, "permit-old-epoch");
        Assert.Equal(first.DeviceEpoch, authorized.DeviceEpoch);
        Assert.Equal(
            state.GetSnapshot().SupervisorInstanceId,
            authorized.ReadinessSupervisorInstanceId);

        state.Apply(
            descriptor,
            new PhysicalDeviceReadinessObservation
            {
                DeviceId = "AGV-01",
                DeviceFamily = "agv",
                ProbeSucceeded = true,
                Online = false,
                IsFullPreflight = true,
                FullPreflightPassed = false,
                BlockingReasons = [PhysicalReadinessReasonCodes.DeviceOffline],
                FullPreflightBlockingReasons = [PhysicalReadinessReasonCodes.DeviceOffline],
                ObservedAtUtc = now.AddSeconds(1)
            },
            TimeSpan.Zero,
            requireFullPreflight: true,
            now.AddSeconds(1));

        var exception = await Assert.ThrowsAsync<PhysicalExecutionAdmissionException>(() =>
            service.DispatchAsync(authorized.Id, CancellationToken.None));

        Assert.Equal(PhysicalReadinessReasonCodes.EpochMismatch, exception.Code);
        var blocked = await service.GetAsync(authorized.Id, CancellationToken.None);
        Assert.Equal(FieldNavigationAcceptanceStatuses.Rejected, blocked!.Acceptance.Status);
        Assert.Contains("epoch_invalid", blocked.Acceptance.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(blocked.Audits, audit =>
            audit.EventType == "DispatchBlockedByPhysicalReadiness" &&
            audit.Details.Contains(PhysicalReadinessReasonCodes.EpochMismatch, StringComparison.Ordinal));
        Assert.Equal(0, adapter.DispatchCalls);
    }

    [Theory]
    [InlineData(PhysicalReadinessReasonCodes.SupervisorInstanceMismatch)]
    [InlineData(PhysicalReadinessReasonCodes.EpochMismatch)]
    [InlineData(PhysicalReadinessReasonCodes.DeviceNotReady)]
    public async Task Dispatch_readiness_rejection_is_durable_and_uses_a_stable_admission_code(
        string reasonCode)
    {
        var adapter = new FieldAcceptanceAdapter();
        var readiness = new MutableDispatchReadinessState();
        var service = CreateService(adapter, physicalReadiness: readiness);
        var authorized = await CreateAuthorizedAsync(service, $"permit-{reasonCode}");
        readiness.RejectWith(reasonCode);

        var exception = await Assert.ThrowsAsync<PhysicalExecutionAdmissionException>(() =>
            service.DispatchAsync(authorized.Id, CancellationToken.None));

        Assert.Equal(reasonCode, exception.Code);
        Assert.Equal(0, adapter.DispatchCalls);
        var persisted = await service.GetAsync(authorized.Id, CancellationToken.None);
        Assert.Equal(FieldNavigationAcceptanceStatuses.Rejected, persisted!.Acceptance.Status);
        Assert.Equal($"physical_readiness_epoch_invalid:{reasonCode}", persisted.Acceptance.LastError);
        Assert.Contains(persisted.Audits, audit =>
            audit.EventType == "DispatchBlockedByPhysicalReadiness" &&
            audit.Details.Contains(reasonCode, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispatch_endpoint_returns_409_with_the_admission_code()
    {
        const string code = PhysicalReadinessReasonCodes.DeviceNotReady;
        using var factory = new MesWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFieldNavigationAcceptanceApplicationService>();
                services.AddSingleton<IFieldNavigationAcceptanceApplicationService>(
                    new RejectingFieldNavigationService(code));
            }));
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/field-navigation-acceptances/{Guid.NewGuid():D}/dispatch",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
    }

    private static async Task<FieldNavigationAcceptanceResponse> CreateAuthorizedAsync(
        FieldNavigationAcceptanceService service,
        string permitId)
    {
        var draft = await service.CreateAsync(
            new CreateFieldNavigationAcceptanceRequest("AGV-01", "LM1", "LM2"),
            CancellationToken.None);
        return await service.AuthorizeAsync(
            draft.Id,
            new AuthorizeFieldNavigationAcceptanceRequest(
                "operator", "observer", permitId, DateTimeOffset.UtcNow.AddMinutes(10)),
            CancellationToken.None);
    }

    private static FieldNavigationAcceptanceService CreateService(
        FieldAcceptanceAdapter adapter,
        bool includeApprovedEdge = true,
        IPhysicalReadinessState? physicalReadiness = null,
        PhysicalExecutionAdmissionPolicy? admissionPolicy = null)
    {
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var profile = new ProfileConfiguration
        {
            Product = new ProductProfile { ProductId = "TEST", DisplayName = "Test", Version = "1" },
            Agvs =
            [
                new AgvProfile
                {
                    AgvId = "AGV-01",
                    Driver = "vendor-tcp",
                    Model = "test",
                    HomeStationId = "LM1",
                    MaxSpeedMetersPerSecond = 0.1
                }
            ],
            Stations =
            [
                new StationProfile { Code = 1, StationId = "LM1", AgvStationId = "LM1", Name = "LM1", Type = "Test" },
                new StationProfile { Code = 2, StationId = "LM2", AgvStationId = "LM2", Name = "LM2", Type = "Test" }
            ],
            Map = new MapProfile
            {
                StationIds = ["LM1", "LM2"],
                Edges = [new MapEdgeProfile { From = "LM1", To = "LM2", Cost = 1 }]
            },
            PhysicalAcceptance = new PhysicalAcceptanceProfile
            {
                ExpectedControlOwner = "adapter",
                MapSnapshot = new ControllerMapSnapshot
                {
                    MapName = "acceptance-map",
                    Version = "1",
                    Md5 = "0123456789abcdef0123456789abcdef",
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    StationIds = ["LM1", "LM2"],
                    DirectedEdges = includeApprovedEdge
                        ? [new DirectedMapEdgeProfile { From = "LM1", To = "LM2" }]
                        : []
                },
                Safety = new PhysicalAgvSafetyProfile
                {
                    MinimumLocalizationConfidence = 0.9,
                    MaximumDispatchSpeedMetersPerSecond = 0.1,
                    RequireControlOwnership = true,
                    RequireNoEmergency = true,
                    RequireNoBlocked = true,
                    RequireNoFaults = true,
                    RequireAutomaticMode = true
                }
            },
            Features = new FeatureFlags
            {
                UseSimulator = false,
                EnableAutomaticDispatch = false,
                EnableFieldNavigationAcceptance = true
            }
        };
        var database = new MesDbContext(options);
        database.Database.EnsureCreated();
        admissionPolicy ??= new PhysicalExecutionAdmissionPolicy(
            ProfileConfiguration.Default with
            {
                Features = ProfileConfiguration.Default.Features with { UseSimulator = true }
            },
            new DisabledReadinessState(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PhysicalExecutionAdmissionPolicy>.Instance);
        return new FieldNavigationAcceptanceService(
            new FieldNavigationAcceptanceRepository(database),
            adapter,
            profile,
            new PathPlanner(AgvMap.FromProfile(profile.Map)),
            physicalReadiness: physicalReadiness,
            admissionPolicy: admissionPolicy);
    }

    private sealed class DisabledReadinessState : IPhysicalReadinessState
    {
        public bool Enabled => false;
        public PhysicalReadinessResponse GetSnapshot() => new() { Enabled = false };
        public bool TryGetDevice(string deviceId, out PhysicalDeviceReadinessSnapshot snapshot) { snapshot = null!; return false; }
        public bool IsCurrentAndReady(string deviceId, long? expectedEpoch, out string? reason) { reason = null; return true; }
        public bool IsCurrentAndReady(string deviceId, long? expectedEpoch, string? expectedSupervisorInstanceId, out string? reason) { reason = null; return true; }
        public bool AcknowledgeAuthorization(string deviceId, long expectedEpoch, string? expectedSupervisorInstanceId) => true;
    }

    private sealed class MutableDispatchReadinessState : IPhysicalReadinessState
    {
        private string? _rejectionReason;

        public bool Enabled => true;

        public PhysicalReadinessResponse GetSnapshot() => new()
        {
            Enabled = true,
            SupervisorInstanceId = "field-navigation-test",
            Devices =
            [
                new PhysicalDeviceReadinessSnapshot
                {
                    DeviceId = "AGV-01",
                    DeviceEpoch = 7,
                    State = PhysicalDeviceReadinessState.Ready,
                    RequiresReauthorization = false
                }
            ]
        };

        public bool TryGetDevice(string deviceId, out PhysicalDeviceReadinessSnapshot snapshot)
        {
            snapshot = GetSnapshot().Devices.Single();
            return string.Equals(deviceId, snapshot.DeviceId, StringComparison.Ordinal);
        }

        public bool IsCurrentAndReady(string deviceId, long? expectedEpoch, out string? reason) =>
            IsCurrentAndReady(deviceId, expectedEpoch, "field-navigation-test", out reason);

        public bool IsCurrentAndReady(
            string deviceId,
            long? expectedEpoch,
            string? expectedSupervisorInstanceId,
            out string? reason)
        {
            reason = _rejectionReason;
            return _rejectionReason is null;
        }

        public bool AcknowledgeAuthorization(string deviceId, long expectedEpoch, string? expectedSupervisorInstanceId) =>
            _rejectionReason is null && deviceId == "AGV-01" && expectedEpoch == 7;

        public void RejectWith(string reasonCode) => _rejectionReason = reasonCode;
    }

    private sealed class RejectingFieldNavigationService(string code)
        : IFieldNavigationAcceptanceApplicationService
    {
        public Task<FieldNavigationAcceptanceResponse> DispatchAsync(
            Guid acceptanceId,
            CancellationToken cancellationToken) =>
            Task.FromException<FieldNavigationAcceptanceResponse>(
                new PhysicalExecutionAdmissionException(code, "readiness rejected for test"));

        public Task<FieldNavigationAcceptanceResponse> CreateAsync(
            CreateFieldNavigationAcceptanceRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FieldNavigationAcceptanceResponse> AuthorizeAsync(
            Guid acceptanceId,
            AuthorizeFieldNavigationAcceptanceRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FieldNavigationAcceptanceResponse> DispatchForWorkflowAsync(
            Guid acceptanceId,
            Guid workflowNodeExecutionId,
            Guid workflowDeviceOperationId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FieldNavigationAcceptanceResponse> CancelAsync(
            Guid acceptanceId,
            string operatorName,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FieldNavigationAcceptanceDetailResponse?> GetAsync(
            Guid acceptanceId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<FieldNavigationAcceptanceResponse>> ListForWorkflowRunAsync(
            Guid workflowRunId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FieldAcceptanceAdapter : IAgvGateway, IFieldNavigationAcceptanceGateway
    {
        public string DispatchState { get; set; } = "moving";
        public Exception? DispatchException { get; set; }
        public Exception? CancelException { get; set; }
        public int DispatchCalls { get; private set; }
        public Guid LastAcceptanceId { get; private set; }

        public Task<AgvTaskResponse> DispatchFieldNavigationAcceptanceAsync(
            Guid acceptanceId,
            FieldNavigationDispatchCommand command,
            CancellationToken cancellationToken)
        {
            DispatchCalls++;
            LastAcceptanceId = acceptanceId;
            return DispatchException is not null
                ? Task.FromException<AgvTaskResponse>(DispatchException)
                : Task.FromResult(new AgvTaskResponse(
                    acceptanceId,
                    acceptanceId.ToString("N"),
                    command.TargetStationId,
                    DispatchState,
                    null,
                    command.AgvId,
                    command.PlannedPath));
        }

        public Task<PhysicalAgvPreflightResponse> GetPhysicalPreflightAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PhysicalAgvPreflightResponse(
                new AgvSnapshotResponse(true, "adapter", "LM1", null, "AGV-01"),
                null,
                false,
                ["test_preflight"]));

        public Task<AgvTaskResponse> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken) =>
            Task.FromResult(new AgvTaskResponse(operationId, operationId.ToString("N"), targetStationId, "moving", null));

        public Task<AgvTaskResponse?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult<AgvTaskResponse?>(null);

        public Task<AgvTaskResponse?> CancelAsync(Guid operationId, CancellationToken cancellationToken) =>
            CancelException is not null
                ? Task.FromException<AgvTaskResponse?>(CancelException)
                : Task.FromResult<AgvTaskResponse?>(new AgvTaskResponse(operationId, operationId.ToString("N"), "LM2", "cancelled", null));

        public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgvSnapshotResponse(true, "adapter", "LM1", null, "AGV-01"));

        public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken cancellationToken) =>
            Task.FromResult<AgvTaskResponse?>(null);
    }
}
