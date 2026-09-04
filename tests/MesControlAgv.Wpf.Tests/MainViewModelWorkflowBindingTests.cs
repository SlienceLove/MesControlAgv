using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class MainViewModelWorkflowBindingTests
{
    [Fact]
    public async Task Explicit_execution_binding_loads_the_monitor_without_latest_run_discovery()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture);
        using var viewModel = new MainViewModel(client);

        var bound = await viewModel.BindWorkflowRunAsync(
            new WorkflowRunStartupBinding(fixture.Run.ExecutionId, null));

        Assert.True(bound);
        Assert.Equal(fixture.Run.ExecutionId, viewModel.WorkflowRunMonitor.Run?.ExecutionId);
        Assert.Equal(fixture.Run.ExecutionId.ToString("D"), viewModel.WorkflowRunMonitor.RunIdText);
    }

    [Fact]
    public async Task Unknown_explicit_execution_does_not_clear_the_current_monitor_run()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture);
        using var viewModel = new MainViewModel(client);
        Assert.True(await viewModel.BindWorkflowRunAsync(
            new WorkflowRunStartupBinding(fixture.Run.ExecutionId, null)));

        client.Run = null;
        var unknownId = Guid.NewGuid();
        var bound = await viewModel.BindWorkflowRunAsync(
            new WorkflowRunStartupBinding(unknownId, null));

        Assert.False(bound);
        Assert.Equal(fixture.Run.ExecutionId, viewModel.WorkflowRunMonitor.Run?.ExecutionId);
    }
}
