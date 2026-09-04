using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkflowRunStartupBindingTests
{
    [Fact]
    public void Parses_an_explicit_execution_id_argument_without_guessing_latest()
    {
        var executionId = Guid.NewGuid();
        var binding = WorkflowRunStartupBinding.Parse(
            ["--workflow-execution-id", executionId.ToString("D")],
            _ => null);

        Assert.True(binding.IsValid);
        Assert.Equal(executionId, binding.ExecutionId);
        Assert.Null(binding.RequestId);
    }

    [Fact]
    public void Parses_an_explicit_request_id_from_the_environment()
    {
        var requestId = Guid.NewGuid();
        var binding = WorkflowRunStartupBinding.Parse(
            [],
            key => key == "WPF_INITIAL_WORKFLOW_REQUEST_ID" ? requestId.ToString("D") : null);

        Assert.True(binding.IsValid);
        Assert.Equal(requestId, binding.RequestId);
        Assert.Null(binding.ExecutionId);
    }

    [Fact]
    public void Rejects_ambiguous_or_invalid_startup_identity_instead_of_binding_a_run()
    {
        var both = WorkflowRunStartupBinding.Parse(
            ["--workflow-execution-id", Guid.NewGuid().ToString(), "--workflow-request-id", Guid.NewGuid().ToString()],
            _ => null);
        var invalid = WorkflowRunStartupBinding.Parse(
            ["--workflow-execution-id", "not-a-guid"],
            _ => null);

        Assert.False(both.IsValid);
        Assert.False(invalid.IsValid);
        Assert.Null(both.ExecutionId);
        Assert.Null(invalid.ExecutionId);
    }
}
