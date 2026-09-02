using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class AuboArmControlViewModelTests
{
    [Fact]
    public async Task Refresh_projects_the_four_controller_states_and_loaded_program()
    {
        var client = new FakeMesClientForArm();
        using var viewModel = new AuboArmControlViewModel(client);

        await viewModel.RefreshAsync();

        Assert.Equal("在线", viewModel.ConnectionStatus);
        Assert.Equal("rob1", viewModel.RobotName);
        Assert.Equal("Running", viewModel.RobotMode);
        Assert.Equal("Normal", viewModel.SafetyMode);
        Assert.Equal("Automatic", viewModel.OperationalMode);
        Assert.Equal("Stopped", viewModel.Runtime);
        Assert.Equal("测试", viewModel.LoadedProgram);
        Assert.Equal("就绪", viewModel.Readiness);
        Assert.True(viewModel.RunProgramCommand.CanExecute(null));
    }

    [Fact]
    public async Task Run_command_is_single_flight_and_forwards_operator_and_program()
    {
        var client = new FakeMesClientForArm { RunGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var viewModel = new AuboArmControlViewModel(client);
        await viewModel.RefreshAsync();

        viewModel.RunProgramCommand.Execute(null);
        await WaitUntilAsync(() => client.RunCalls == 1 && viewModel.IsBusy);
        viewModel.RunProgramCommand.Execute(null);
        Assert.Equal(1, client.RunCalls);

        client.RunGate.SetResult(true);
        await WaitUntilAsync(() => !viewModel.IsBusy);
        Assert.Equal("测试", client.LastProgram);
        Assert.Equal(Environment.UserName, client.LastOperator);
    }

    [Fact]
    public async Task Program_catalog_refresh_populates_selectable_names_without_hardcoding_them()
    {
        var client = new FakeMesClientForArm
        {
            Catalog = new AuboArmProgramCatalogResponse(
                "ARM-01",
                true,
                "现场工程",
                ["现场工程", "后处理工程"],
                [],
                true,
                [],
                DateTimeOffset.UtcNow)
        };
        using var viewModel = new AuboArmControlViewModel(client);
        await viewModel.RefreshAsync();

        viewModel.RefreshProgramsCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsBusy && viewModel.AvailablePrograms.Count == 2);

        Assert.Equal(["后处理工程", "现场工程"], viewModel.AvailablePrograms);
        viewModel.ProgramName = "后处理工程";
        Assert.Equal("后处理工程", viewModel.ProgramName);
    }

    [Fact]
    public async Task Disconnect_clears_controller_derived_program_state()
    {
        var client = new FakeMesClientForArm
        {
            Catalog = new AuboArmProgramCatalogResponse(
                "ARM-01",
                true,
                "现场工程",
                ["现场工程"],
                [],
                true,
                [],
                DateTimeOffset.UtcNow)
        };
        using var viewModel = new AuboArmControlViewModel(client);
        await viewModel.RefreshAsync();
        viewModel.RefreshProgramsCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.AvailablePrograms.Count == 1 && !viewModel.IsBusy);

        client.ThrowOnStatus = true;
        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsOnline);
        Assert.Equal("-", viewModel.LoadedProgram);
        Assert.Empty(viewModel.AvailablePrograms);
        Assert.Equal("程序目录不可用", viewModel.ProgramCatalogStatus);
        Assert.Null(viewModel.ObservedAt);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("The expected asynchronous operation did not complete.");
    }

    private sealed class FakeMesClientForArm : IMesClient
    {
        public TaskCompletionSource<bool>? RunGate { get; init; }
        public int RunCalls { get; private set; }
        public string? LastProgram { get; private set; }
        public string? LastOperator { get; private set; }
        public AuboArmProgramCatalogResponse? Catalog { get; init; }
        public bool ThrowOnStatus { get; set; }

        public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DashboardTask>>([]);
        public Task<KpiDashboard> GetKpiDashboardAsync(DateOnly date, CancellationToken cancellationToken) => Task.FromResult(new KpiDashboard(date, new KpiTaskSummary(0, 0, 0, 0, 0), [], new KpiSampleSummary(0, 0, 0, 0, 0, 0, "test"), [], []));
        public Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult<DashboardTaskDetail?>(null);
        public Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new AgvDashboardSnapshot(false, "none", null, null));
        public Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken) => Task.FromResult(new DashboardTask(Guid.NewGuid(), 0, 1, "Created", 0, null));
        public Task<DashboardTask> CreateTaskAsync(int sourceStationCode, int targetStationCode, int priority, string? description, string? externalId, CancellationToken cancellationToken) => Task.FromResult(new DashboardTask(Guid.NewGuid(), sourceStationCode, targetStationCode, "Created", 0, null));
        public Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(new DashboardTask(taskId, 0, 1, "Arrived", 0, null));
        public Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromResult(new DashboardTask(taskId, 0, 1, "Completed", 0, null));
        public Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromResult(new DashboardTask(taskId, 0, 1, "Completed", 0, null));
        public Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(new DashboardTask(taskId, 0, 1, "Created", 0, null));
        public Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(new DashboardTask(taskId, 0, 1, "Created", 0, null));
        public Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromResult(new DashboardTask(taskId, 0, 1, "Cancelled", 0, null));

        public Task<AuboArmStatusResponse?> GetAuboArmStatusAsync(string deviceId, CancellationToken cancellationToken) =>
            ThrowOnStatus
                ? Task.FromException<AuboArmStatusResponse?>(new HttpRequestException("offline"))
                : Task.FromResult<AuboArmStatusResponse?>(new AuboArmStatusResponse(
                    deviceId, "rob1", true, AuboArmMode.Running, 8, AuboArmSafetyMode.Normal, 1,
                    AuboArmRuntimeState.Stopped, 6, AuboArmOperationalMode.Automatic, 1, DateTimeOffset.UtcNow));
        public Task<AuboArmReadinessResponse?> GetAuboArmReadinessAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult<AuboArmReadinessResponse?>(new AuboArmReadinessResponse(
                deviceId, true, [], new AuboArmStatusResponse(
                    deviceId, "rob1", true, AuboArmMode.Running, 8, AuboArmSafetyMode.Normal, 1,
                    AuboArmRuntimeState.Stopped, 6, AuboArmOperationalMode.Automatic, 1, DateTimeOffset.UtcNow),
                "测试", DateTimeOffset.UtcNow));
        public Task<AuboArmProgramStatusResponse?> GetAuboArmProgramAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult<AuboArmProgramStatusResponse?>(new AuboArmProgramStatusResponse(deviceId, true, "测试", AuboArmRuntimeState.Stopped, "Stopped", DateTimeOffset.UtcNow) { ControlEnabled = true });
        public Task<AuboArmProgramCatalogResponse?> GetAuboArmProgramCatalogAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(Catalog);
        public Task<AuboArmProgramOperationResponse> LoadAuboProgramAsync(string deviceId, string programName, string operatorName, Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(Operation(operationId, deviceId, programName, "load", operatorName, AuboArmProgramOperationState.Loaded));
        public async Task<AuboArmProgramOperationResponse> RunAuboProgramAsync(string deviceId, string? programName, string operatorName, Guid operationId, CancellationToken cancellationToken)
        {
            RunCalls++;
            LastProgram = programName;
            LastOperator = operatorName;
            if (RunGate is not null) await RunGate.Task.WaitAsync(cancellationToken);
            return Operation(operationId, deviceId, programName ?? "测试", "run", operatorName, AuboArmProgramOperationState.Running);
        }
        public Task<AuboArmProgramOperationResponse> StopAuboProgramAsync(string deviceId, string operatorName, Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(Operation(operationId, deviceId, "测试", "stop", operatorName, AuboArmProgramOperationState.Stopped));

        private static AuboArmProgramOperationResponse Operation(Guid id, string device, string program, string action, string actor, AuboArmProgramOperationState state) =>
            new(id, device, program, action, actor, state, AuboArmRuntimeState.Running, "Running", program, 0, null, true, DateTimeOffset.UtcNow);
    }
}
