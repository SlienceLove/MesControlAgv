using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain;
using MesControlAgv.Mes.Services;
using ContractTaskResponse = MesControlAgv.Contracts.TaskResponse;
using ContractTaskDetailResponse = MesControlAgv.Contracts.TaskDetailResponse;

namespace MesControlAgv.Mes.Tests;

public sealed class AgvAuboSequenceServiceTests
{
    [Fact]
    public async Task Arrived_task_loads_only_when_needed_then_runs_once_and_records_completion()
    {
        var taskId = Guid.NewGuid();
        var tasks = new FakeTaskService(taskId, "WaitingDropoffConfirmation");
        var arm = new FakeArmGateway();
        using var service = new AgvAuboSequenceService(
            tasks,
            arm,
            new AgvAuboSequenceOptions { PollIntervalMs = 50, CompletionTimeoutMs = 1000 });
        var sequenceId = Guid.NewGuid();

        var result = await service.StartAsync(
            new AgvAuboSequenceRequest(taskId, "ARM-01", "测试.pro", "alice", sequenceId),
            CancellationToken.None);

        Assert.Equal(AgvAuboSequenceState.Completed, result.State);
        Assert.Equal(1, arm.LoadCalls);
        Assert.Equal(1, arm.RunCalls);
        var replay = await service.StartAsync(
            new AgvAuboSequenceRequest(taskId, "ARM-01", "测试", "alice", sequenceId),
            CancellationToken.None);
        Assert.Same(result, replay);
        Assert.Equal(1, arm.RunCalls);
    }

    [Fact]
    public async Task Non_arrived_task_stays_waiting_and_does_not_touch_the_arm()
    {
        var taskId = Guid.NewGuid();
        var tasks = new FakeTaskService(taskId, "MovingToDropoff");
        var arm = new FakeArmGateway();
        using var service = new AgvAuboSequenceService(tasks, arm);

        var result = await service.StartAsync(
            new AgvAuboSequenceRequest(taskId, "ARM-01", "测试", "alice"),
            CancellationToken.None);

        Assert.Equal(AgvAuboSequenceState.WaitingForArrival, result.State);
        Assert.Equal(0, arm.RunCalls);
    }

    private sealed class FakeArmGateway : IAuboArmGateway
    {
        public int LoadCalls { get; private set; }
        public int RunCalls { get; private set; }
        public int GetProgramCalls { get; private set; }

        public Task<AuboArmStatusResponse> GetStatusAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(Status(deviceId));
        public Task<AuboArmReadinessResponse> GetReadinessAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new AuboArmReadinessResponse(deviceId, true, [], Status(deviceId), "测试", DateTimeOffset.UtcNow));
        public Task<AuboArmVariableResponse> GetVariableAsync(string deviceId, string key, CancellationToken cancellationToken) =>
            Task.FromResult(new AuboArmVariableResponse(deviceId, key, false, null, null, null, null, null, DateTimeOffset.UtcNow));
        public Task<AuboArmHandshakeSnapshotResponse> GetHandshakeSnapshotAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new AuboArmHandshakeSnapshotResponse(deviceId, AuboArmHandshakeState.Idle, null, null, null, null, null, DateTimeOffset.UtcNow));
        public Task<AuboArmHandshakeResultResponse> DispatchAsync(string deviceId, Guid operationId, int commandCode, CancellationToken cancellationToken) =>
            Task.FromResult(new AuboArmHandshakeResultResponse(operationId, deviceId, commandCode, 1, AuboArmHandshakeState.Completed, 1, null, true, DateTimeOffset.UtcNow));

        public Task<AuboArmProgramStatusResponse> GetProgramAsync(string deviceId, CancellationToken cancellationToken)
        {
            GetProgramCalls++;
            return Task.FromResult(new AuboArmProgramStatusResponse(
                deviceId, true, LoadCalls == 0 ? null : "测试", AuboArmRuntimeState.Stopped, "Stopped", DateTimeOffset.UtcNow));
        }

        public Task<AuboArmProgramOperationResponse> LoadProgramAsync(string deviceId, string programName, string operatorName, Guid operationId, CancellationToken cancellationToken)
        {
            LoadCalls++;
            return Task.FromResult(Operation(operationId, deviceId, "测试", "load", AuboArmProgramOperationState.Loaded, operatorName));
        }

        public Task<AuboArmProgramOperationResponse> RunProgramAsync(string deviceId, string? programName, string operatorName, Guid operationId, CancellationToken cancellationToken)
        {
            RunCalls++;
            return Task.FromResult(Operation(operationId, deviceId, "测试", "run", AuboArmProgramOperationState.Running, operatorName));
        }

        public Task<AuboArmProgramOperationResponse> StopProgramAsync(string deviceId, string operatorName, Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(Operation(operationId, deviceId, "测试", "stop", AuboArmProgramOperationState.Stopped, operatorName));

        private static AuboArmStatusResponse Status(string id) => new(
            id, "rob1", true, AuboArmMode.Running, 8, AuboArmSafetyMode.Normal, 1,
            AuboArmRuntimeState.Stopped, 6, AuboArmOperationalMode.Automatic, 1, DateTimeOffset.UtcNow);

        private static AuboArmProgramOperationResponse Operation(Guid id, string device, string program, string action, AuboArmProgramOperationState state, string actor) =>
            new(id, device, program, action, actor, state, AuboArmRuntimeState.Stopped, "Stopped", program, 0, null, true, DateTimeOffset.UtcNow);
    }

    private sealed class FakeTaskService(Guid taskId, string status) : ITaskApplicationService
    {
        private readonly ContractTaskResponse _task = new(taskId, 1, 2, status, 0, null, ActiveAgvId: "AGV-01");

        public Task<ContractTaskResponse> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken) => Task.FromResult(_task);
        public Task<ContractTaskResponse> DispatchAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_task);
        public Task<ContractTaskResponse> RecordArrivalAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_task);
        public Task<ContractTaskResponse> ConfirmPickupAsync(Guid id, string actor, CancellationToken cancellationToken) => Task.FromResult(_task);
        public Task<ContractTaskResponse> ConfirmDropoffAsync(Guid id, string actor, CancellationToken cancellationToken) => Task.FromResult(_task);
        public Task<ContractTaskResponse> RetryAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_task);
        public Task<ContractTaskResponse> CancelAsync(Guid id, string actor, CancellationToken cancellationToken) => Task.FromResult(_task);
        public Task<ContractTaskResponse> RecoverAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_task);
        public Task<ContractTaskResponse?> RecordAgvCommandAsync(Guid id, string command, AgvTaskResponse result, CancellationToken cancellationToken) => Task.FromResult<ContractTaskResponse?>(_task);
        public Task<IReadOnlyList<AgvFleetStatusResponse>> GetFleetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgvFleetStatusResponse>>([
                new AgvFleetStatusResponse(
                    new AgvSnapshotResponse(true, "adapter", "LM2", taskId),
                    new AgvActiveTaskStatusResponse(taskId, Guid.NewGuid(), _task.Status, "device", "arrived", "LM2", null, ["LM1", "LM2"]))
            ]);
        public Task ReconcileIncompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReconcileActiveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ContractTaskDetailResponse?> GetDetailAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<ContractTaskDetailResponse?>(id == taskId ? new ContractTaskDetailResponse(_task, []) : null);
        public Task<IReadOnlyList<ContractTaskResponse>> ListAsync(DateOnly date, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContractTaskResponse>>([_task]);
        public Task<IReadOnlyList<ContractTaskResponse>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContractTaskResponse>>([_task]);
    }
}
