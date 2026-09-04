using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MesControlAgv.Mes.Tests;

public sealed class AuboArmProgramApiTests
{
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
            new { operatorName = "alice" });
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
            Task.FromResult(new AuboArmProgramStatusResponse(deviceId, true, "测试", AuboArmRuntimeState.Stopped, "Stopped", DateTimeOffset.UtcNow));

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
}
