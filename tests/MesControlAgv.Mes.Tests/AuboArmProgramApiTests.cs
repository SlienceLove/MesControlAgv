using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MesControlAgv.Mes.Tests;

public sealed class AuboArmProgramApiTests
{
    private static readonly JsonSerializerOptions WorkflowJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Program_routes_forward_explicit_operations_to_the_arm_gateway()
    {
        var gateway = new FakeAuboGateway();
        using var factory = new MesWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuboArmGateway>();
                services.AddSingleton<IAuboArmGateway>(gateway);
            }));
        using var client = factory.CreateClient();

        var status = await client.GetAsync("/api/robot-arms/ARM-01/program");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);

        var catalog = await client.GetAsync("/api/robot-arms/ARM-01/programs?fresh=true");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        Assert.True(gateway.LastCatalogForceFresh);

        var load = await client.PostAsJsonAsync(
            "/api/robot-arms/ARM-01/program/load",
            new { program = "测试.pro", @operator = "alice" });
        Assert.Equal(HttpStatusCode.OK, load.StatusCode);
        // MES preserves the operator's explicit display name; the Adapter is
        // the single place that strips .pro/.lua before the vendor RPC.
        Assert.Equal("测试.pro", gateway.LastProgram);
        Assert.Equal("alice", gateway.LastOperator);

        var run = await client.PostAsJsonAsync(
            "/api/robot-arms/ARM-01/program/run",
            new { programName = "测试", operatorName = "alice" });
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var stop = await client.PostAsJsonAsync(
            "/api/robot-arms/ARM-01/program/stop",
            new { operatorName = "alice", operationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        Assert.Equal(1, gateway.LoadCalls);
        Assert.Equal(1, gateway.RunCalls);
        Assert.Equal(1, gateway.StopCalls);
    }

    [Fact]
    public async Task Partial_workflow_correlation_is_rejected_before_the_arm_gateway()
    {
        var gateway = new FakeAuboGateway();
        using var factory = new MesWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuboArmGateway>();
                services.AddSingleton<IAuboArmGateway>(gateway);
            }));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/robot-arms/ARM-01/program/load",
            new
            {
                program = "娴嬭瘯",
                @operator = "alice",
                operationId = Guid.NewGuid(),
                workflowRunId = Guid.NewGuid()
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUBO_WORKFLOW_CORRELATION_INVALID", body.GetProperty("code").GetString());
        Assert.Equal(0, gateway.LoadCalls);
    }

    [Fact]
    public async Task Enabled_physical_supervisor_blocks_uncorrelated_program_writes()
    {
        var gateway = new FakeAuboGateway();
        using var factory = new MesWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuboArmGateway>();
                services.RemoveAll<IPhysicalReadinessState>();
                services.RemoveAll<PhysicalExecutionAdmissionPolicy>();
                services.AddSingleton<IAuboArmGateway>(gateway);
                var readiness = new BlockingPhysicalReadinessState();
                services.AddSingleton<IPhysicalReadinessState>(readiness);
                services.AddSingleton(new PhysicalExecutionAdmissionPolicy(
                    ProfileConfiguration.Default with
                    {
                        Features = ProfileConfiguration.Default.Features with { UseSimulator = false }
                    },
                    readiness,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<PhysicalExecutionAdmissionPolicy>.Instance));
            }));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/robot-arms/ARM-01/program/load",
            new { program = "测试.pro", @operator = "alice" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(PhysicalReadinessReasonCodes.EpochAuthorizationRequired, body.GetProperty("code").GetString());
        Assert.Equal(0, gateway.LoadCalls);
    }

    [Fact]
    public async Task Correlated_physical_program_writes_require_an_epoch_bound_authorization()
    {
        var readiness = new ApiPhysicalReadinessState(
            supervisorInstanceId: "current-supervisor",
            deviceEpoch: 7);
        var gateway = new FakeAuboGateway();
        using var factory = CreatePhysicalFactory(gateway, readiness);
        using var client = factory.CreateClient();
        var correlation = await SeedRunningAuboOperationAsync(
            factory,
            new WorkflowPhysicalRunAuthorization
            {
                OperatorName = "operator",
                SafetyObserverName = "observer",
                PermitPrefix = "missing-epoch",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
                ReadinessSupervisorInstanceId = "current-supervisor"
            });

        await AssertProgramWritesRejectedAsync(
            client,
            correlation,
            PhysicalReadinessReasonCodes.EpochAuthorizationRequired);

        Assert.Equal(0, gateway.LoadCalls);
        Assert.Equal(0, gateway.RunCalls);
    }

    [Fact]
    public async Task Correlated_physical_program_writes_reject_a_previous_supervisor_instance()
    {
        var readiness = new ApiPhysicalReadinessState(
            supervisorInstanceId: "current-supervisor",
            deviceEpoch: 7);
        var gateway = new FakeAuboGateway();
        using var factory = CreatePhysicalFactory(gateway, readiness);
        using var client = factory.CreateClient();
        var correlation = await SeedRunningAuboOperationAsync(
            factory,
            CurrentAuthorization(7, "previous-supervisor"));

        await AssertProgramWritesRejectedAsync(
            client,
            correlation,
            PhysicalReadinessReasonCodes.SupervisorInstanceMismatch);

        Assert.Equal(0, gateway.LoadCalls);
        Assert.Equal(0, gateway.RunCalls);
    }

    [Fact]
    public async Task Correlated_physical_program_writes_reject_a_stale_device_epoch()
    {
        var readiness = new ApiPhysicalReadinessState(
            supervisorInstanceId: "current-supervisor",
            deviceEpoch: 8);
        var gateway = new FakeAuboGateway();
        using var factory = CreatePhysicalFactory(gateway, readiness);
        using var client = factory.CreateClient();
        var correlation = await SeedRunningAuboOperationAsync(
            factory,
            CurrentAuthorization(7, "current-supervisor"));

        await AssertProgramWritesRejectedAsync(
            client,
            correlation,
            PhysicalReadinessReasonCodes.EpochMismatch);

        Assert.Equal(0, gateway.LoadCalls);
        Assert.Equal(0, gateway.RunCalls);
    }

    [Theory]
    [InlineData(false, PhysicalDeviceReadinessState.Ready)]
    [InlineData(true, PhysicalDeviceReadinessState.Offline)]
    public async Task Correlated_physical_program_writes_reject_missing_or_non_ready_devices(
        bool devicePresent,
        PhysicalDeviceReadinessState state)
    {
        var readiness = new ApiPhysicalReadinessState(
            supervisorInstanceId: "current-supervisor",
            deviceEpoch: 7,
            devicePresent: devicePresent,
            deviceState: state);
        var gateway = new FakeAuboGateway();
        using var factory = CreatePhysicalFactory(gateway, readiness);
        using var client = factory.CreateClient();
        var correlation = await SeedRunningAuboOperationAsync(
            factory,
            CurrentAuthorization(7, "current-supervisor"));

        await AssertProgramWritesRejectedAsync(
            client,
            correlation,
            PhysicalReadinessReasonCodes.DeviceNotReady);

        Assert.Equal(0, gateway.LoadCalls);
        Assert.Equal(0, gateway.RunCalls);
    }

    [Theory]
    [InlineData("load")]
    [InlineData("run")]
    public async Task Physical_program_load_and_run_fail_closed_when_supervisor_is_off(string operation)
    {
        var gateway = new FakeAuboGateway();
        using var factory = new MesWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuboArmGateway>();
                services.RemoveAll<IPhysicalReadinessState>();
                services.RemoveAll<PhysicalExecutionAdmissionPolicy>();
                services.AddSingleton<IAuboArmGateway>(gateway);
                var readiness = new DisabledPhysicalReadinessState();
                services.AddSingleton<IPhysicalReadinessState>(readiness);
                services.AddSingleton(new PhysicalExecutionAdmissionPolicy(
                    ProfileConfiguration.Default with
                    {
                        Features = ProfileConfiguration.Default.Features with { UseSimulator = false }
                    },
                    readiness,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<PhysicalExecutionAdmissionPolicy>.Instance));
            }));
        using var client = factory.CreateClient();

        HttpResponseMessage response;
        if (operation == "load")
        {
            response = await client.PostAsJsonAsync(
                "/api/robot-arms/ARM-01/program/load",
                new { program = "test.pro", @operator = "alice" });
        }
        else
        {
            response = await client.PostAsJsonAsync(
                "/api/robot-arms/ARM-01/program/run",
                new { programName = "test", operatorName = "alice" });
        }

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(PhysicalReadinessReasonCodes.SupervisorDisabled, body.GetProperty("code").GetString());
        Assert.Equal(0, gateway.LoadCalls);
        Assert.Equal(0, gateway.RunCalls);
    }

    private static WebApplicationFactory<Program> CreatePhysicalFactory(
        FakeAuboGateway gateway,
        IPhysicalReadinessState readiness) =>
        new MesWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuboArmGateway>();
                services.RemoveAll<IPhysicalReadinessState>();
                services.RemoveAll<PhysicalExecutionAdmissionPolicy>();
                services.AddSingleton<IAuboArmGateway>(gateway);
                services.AddSingleton<IPhysicalReadinessState>(readiness);
                services.AddSingleton(new PhysicalExecutionAdmissionPolicy(
                    ProfileConfiguration.Default with
                    {
                        Features = ProfileConfiguration.Default.Features with { UseSimulator = false }
                    },
                    readiness,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<PhysicalExecutionAdmissionPolicy>.Instance));
            }));

    private static WorkflowPhysicalRunAuthorization CurrentAuthorization(
        long deviceEpoch,
        string supervisorInstanceId) => new()
    {
        OperatorName = "operator",
        SafetyObserverName = "observer",
        PermitPrefix = "api-readiness",
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
        DeviceEpochs = new Dictionary<string, long> { ["ARM-01"] = deviceEpoch },
        ReadinessSupervisorInstanceId = supervisorInstanceId
    };

    private static async Task<AuboArmOperationCorrelation> SeedRunningAuboOperationAsync(
        WebApplicationFactory<Program> factory,
        WorkflowPhysicalRunAuthorization authorization)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        var workflowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var nodeExecutionId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        const int attempt = 1;
        const string correlationId = "aubo-api-readiness";
        var now = DateTime.UtcNow;
        var request = new WorkflowExecutionRequest
        {
            WorkflowId = workflowId,
            Version = 1,
            RequestId = requestId,
            RequestedBy = "test",
            CorrelationId = correlationId,
            PhysicalAuthorization = authorization
        };
        var result = new WorkflowExecutionResult
        {
            Status = WorkflowExecutionStatus.Accepted,
            RequestId = requestId,
            ExecutionId = runId,
            WorkflowId = workflowId,
            Version = 1,
            RequestedAt = new DateTimeOffset(now, TimeSpan.Zero)
        };

        database.WorkflowExecutions.Add(new WorkflowExecutionRecord
        {
            RequestId = requestId,
            Fingerprint = $"aubo-api-{runId:N}",
            WorkflowId = workflowId,
            Version = 1,
            ExecutionId = runId,
            Outcome = WorkflowExecutionStatus.Accepted.ToString(),
            RequestJson = JsonSerializer.Serialize(request, WorkflowJsonOptions),
            ResultJson = JsonSerializer.Serialize(result, WorkflowJsonOptions),
            CreatedAtUtc = now,
            RuntimeStatus = WorkflowRuntimeStatus.Running.ToString(),
            CurrentNodeId = nodeId,
            TransportOperationId = operationId,
            Attempt = attempt,
            UpdatedAtUtc = now
        });
        database.WorkflowNodeExecutions.Add(new WorkflowNodeExecutionRecord
        {
            Id = nodeExecutionId,
            WorkflowRunId = runId,
            WorkflowId = workflowId,
            Version = 1,
            StepRequestId = Guid.NewGuid(),
            NodeId = nodeId,
            NodeTypeId = WorkflowGraphNodeTypeIds.RobotExecuteProgram,
            NodeName = "AUBO API readiness fixture",
            Attempt = attempt,
            Status = WorkflowNodeExecutionStatus.Running.ToString(),
            InputJson = "{}",
            OutputJson = "{}",
            StartedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        database.WorkflowDeviceOperations.Add(new WorkflowDeviceOperationRecord
        {
            OperationId = operationId,
            WorkflowRunId = runId,
            NodeExecutionId = nodeExecutionId,
            RequestId = requestId,
            Attempt = attempt,
            CapabilityId = WorkflowCapabilityIds.RobotExecuteProgram,
            DeviceId = "ARM-01",
            IdempotencyKey = operationId.ToString("N"),
            CorrelationId = correlationId,
            Status = WorkflowDeviceOperationStatus.Running.ToString(),
            RequestSummaryJson = "{}",
            ResultSummaryJson = "{}",
            RequestedAtUtc = now,
            UpdatedAtUtc = now
        });
        await database.SaveChangesAsync();

        return AuboArmOperationCorrelation.Create(
            runId,
            nodeExecutionId,
            operationId,
            requestId,
            correlationId,
            attempt);
    }

    private static async Task AssertProgramWritesRejectedAsync(
        HttpClient client,
        AuboArmOperationCorrelation correlation,
        string expectedCode)
    {
        using var load = await client.PostAsJsonAsync(
            "/api/robot-arms/ARM-01/program/load",
            new AuboArmProgramRequest("test.lua", "alice", correlation.DeviceOperationId)
            {
                Correlation = correlation
            });
        Assert.Equal(HttpStatusCode.Conflict, load.StatusCode);
        var loadBody = await load.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedCode, loadBody.GetProperty("code").GetString());

        using var run = await client.PostAsJsonAsync(
            "/api/robot-arms/ARM-01/program/run",
            new AuboArmProgramRunRequest("test.lua", "alice", correlation.DeviceOperationId)
            {
                Correlation = correlation
            });
        Assert.Equal(HttpStatusCode.Conflict, run.StatusCode);
        var runBody = await run.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedCode, runBody.GetProperty("code").GetString());
    }

    private sealed class FakeAuboGateway : IAuboArmGateway
    {
        public int LoadCalls { get; private set; }
        public int RunCalls { get; private set; }
        public int StopCalls { get; private set; }
        public string? LastProgram { get; private set; }
        public string? LastOperator { get; private set; }
        public bool LastCatalogForceFresh { get; private set; }

        public Task<AuboArmStatusResponse> GetStatusAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new AuboArmStatusResponse(
                deviceId, "rob1", true, AuboArmMode.Running, 8,
                AuboArmSafetyMode.Normal, 1, AuboArmRuntimeState.Stopped, 6,
                AuboArmOperationalMode.Automatic, 1, DateTimeOffset.UtcNow));

        public Task<AuboArmReadinessResponse> GetReadinessAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new AuboArmReadinessResponse(
                deviceId, true, [],
                new AuboArmStatusResponse(
                    deviceId, "rob1", true, AuboArmMode.Running, 8,
                    AuboArmSafetyMode.Normal, 1, AuboArmRuntimeState.Stopped, 6,
                    AuboArmOperationalMode.Automatic, 1, DateTimeOffset.UtcNow),
                "测试", DateTimeOffset.UtcNow));

        public Task<AuboArmVariableResponse> GetVariableAsync(string deviceId, string key, CancellationToken cancellationToken) =>
            Task.FromResult(new AuboArmVariableResponse(deviceId, key, false, null, null, null, null, null, DateTimeOffset.UtcNow));

        public Task<AuboArmHandshakeSnapshotResponse> GetHandshakeSnapshotAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new AuboArmHandshakeSnapshotResponse(deviceId, AuboArmHandshakeState.Idle, null, null, null, null, null, DateTimeOffset.UtcNow));

        public Task<AuboArmHandshakeResultResponse> DispatchAsync(string deviceId, Guid operationId, int commandCode, CancellationToken cancellationToken) =>
            Task.FromResult(new AuboArmHandshakeResultResponse(operationId, deviceId, commandCode, 1, AuboArmHandshakeState.Completed, 1, null, true, DateTimeOffset.UtcNow));

        public Task<AuboArmProgramStatusResponse> GetProgramAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new AuboArmProgramStatusResponse(deviceId, true, "测试", AuboArmRuntimeState.Running, "Running", DateTimeOffset.UtcNow));

        public Task<AuboArmProgramCatalogResponse> GetProgramCatalogAsync(string deviceId, CancellationToken cancellationToken) =>
            GetProgramCatalogAsync(deviceId, forceFresh: false, cancellationToken);

        public Task<AuboArmProgramCatalogResponse> GetProgramCatalogAsync(
            string deviceId,
            bool forceFresh,
            CancellationToken cancellationToken)
        {
            LastCatalogForceFresh = forceFresh;
            return Task.FromResult(new AuboArmProgramCatalogResponse(
                deviceId, true, "测试", ["测试"], ["测试"], true, [], DateTimeOffset.UtcNow));
        }

        public Task<AuboArmProgramOperationResponse> LoadProgramAsync(string deviceId, string programName, string operatorName, Guid operationId, CancellationToken cancellationToken)
        {
            LoadCalls++;
            LastProgram = programName;
            LastOperator = operatorName;
            return Task.FromResult(Result(operationId, deviceId, programName, "load", AuboArmProgramOperationState.Loaded, operatorName));
        }

        public Task<AuboArmProgramOperationResponse> RunProgramAsync(string deviceId, string? programName, string operatorName, Guid operationId, CancellationToken cancellationToken)
        {
            RunCalls++;
            LastProgram = programName;
            LastOperator = operatorName;
            return Task.FromResult(Result(operationId, deviceId, programName ?? "测试", "run", AuboArmProgramOperationState.Running, operatorName));
        }

        public Task<AuboArmProgramOperationResponse> StopProgramAsync(string deviceId, string operatorName, Guid operationId, CancellationToken cancellationToken)
        {
            StopCalls++;
            LastOperator = operatorName;
            return Task.FromResult(Result(operationId, deviceId, "测试", "stop", AuboArmProgramOperationState.Stopped, operatorName));
        }

        private static AuboArmProgramOperationResponse Result(Guid id, string device, string program, string operation, AuboArmProgramOperationState state, string operatorName) =>
            new(id, device, program, operation, operatorName, state, AuboArmRuntimeState.Stopped, "Stopped", program, 0, null, true, DateTimeOffset.UtcNow);
    }

    private sealed class ApiPhysicalReadinessState(
        string supervisorInstanceId,
        long deviceEpoch,
        bool devicePresent = true,
        PhysicalDeviceReadinessState deviceState = PhysicalDeviceReadinessState.Ready)
        : IPhysicalReadinessState
    {
        public bool Enabled => true;

        public PhysicalReadinessResponse GetSnapshot() => new()
        {
            Enabled = true,
            SupervisorInstanceId = supervisorInstanceId,
            Devices = devicePresent ? [CreateDevice()] : []
        };

        public bool TryGetDevice(string deviceId, out PhysicalDeviceReadinessSnapshot snapshot)
        {
            snapshot = CreateDevice();
            return devicePresent &&
                   string.Equals(deviceId, snapshot.DeviceId, StringComparison.OrdinalIgnoreCase);
        }

        public bool IsCurrentAndReady(
            string deviceId,
            long? expectedEpoch,
            out string? reason) =>
            IsCurrentAndReady(deviceId, expectedEpoch, supervisorInstanceId, out reason);

        public bool IsCurrentAndReady(
            string deviceId,
            long? expectedEpoch,
            string? expectedSupervisorInstanceId,
            out string? reason)
        {
            if (!string.Equals(
                    expectedSupervisorInstanceId,
                    supervisorInstanceId,
                    StringComparison.Ordinal))
            {
                reason = PhysicalReadinessReasonCodes.SupervisorInstanceMismatch;
                return false;
            }
            if (!devicePresent || deviceState != PhysicalDeviceReadinessState.Ready)
            {
                reason = PhysicalReadinessReasonCodes.DeviceNotReady;
                return false;
            }
            if (!expectedEpoch.HasValue || expectedEpoch.Value != deviceEpoch)
            {
                reason = PhysicalReadinessReasonCodes.EpochMismatch;
                return false;
            }

            reason = null;
            return true;
        }

        public bool AcknowledgeAuthorization(string deviceId, long expectedEpoch, string? expectedSupervisorInstanceId) => false;

        private PhysicalDeviceReadinessSnapshot CreateDevice() => new()
        {
            DeviceId = "ARM-01",
            DeviceEpoch = deviceEpoch,
            State = deviceState,
            RequiresReauthorization = false
        };
    }

    private sealed class BlockingPhysicalReadinessState : IPhysicalReadinessState
    {
        public bool Enabled => true;

        public PhysicalReadinessResponse GetSnapshot() => new()
        {
            Enabled = true,
            SupervisorInstanceId = "blocking-test",
            SchedulingPermitted = false,
            BlockingReasons = [PhysicalReadinessReasonCodes.EpochRequired]
        };

        public bool TryGetDevice(
            string deviceId,
            out PhysicalDeviceReadinessSnapshot snapshot)
        {
            snapshot = new PhysicalDeviceReadinessSnapshot();
            return false;
        }

        public bool IsCurrentAndReady(
            string deviceId,
            long? expectedEpoch,
            out string? reason)
        {
            reason = PhysicalReadinessReasonCodes.EpochRequired;
            return false;
        }

        public bool IsCurrentAndReady(
            string deviceId,
            long? expectedEpoch,
            string? expectedSupervisorInstanceId,
            out string? reason)
        {
            reason = PhysicalReadinessReasonCodes.EpochRequired;
            return false;
        }

        public bool AcknowledgeAuthorization(string deviceId, long expectedEpoch, string? expectedSupervisorInstanceId) => false;
    }

    private sealed class DisabledPhysicalReadinessState : IPhysicalReadinessState
    {
        public bool Enabled => false;
        public PhysicalReadinessResponse GetSnapshot() => new() { Enabled = false };
        public bool TryGetDevice(string deviceId, out PhysicalDeviceReadinessSnapshot snapshot) { snapshot = null!; return false; }
        public bool IsCurrentAndReady(string deviceId, long? expectedEpoch, out string? reason) { reason = PhysicalReadinessReasonCodes.SupervisorDisabled; return false; }
        public bool IsCurrentAndReady(string deviceId, long? expectedEpoch, string? expectedSupervisorInstanceId, out string? reason) { reason = PhysicalReadinessReasonCodes.SupervisorDisabled; return false; }
        public bool AcknowledgeAuthorization(string deviceId, long expectedEpoch, string? expectedSupervisorInstanceId) => false;
    }
}
