using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Tests;

public sealed partial class WorkflowFieldNavigationWorkerTests
{
    [Theory]
    [InlineData("map")]
    [InlineData("epoch")]
    [InlineData("reauthorization")]
    [InlineData("probe")]
    [InlineData("offline")]
    [InlineData("obstacle")]
    public async Task Commissioning_observation_waits_for_its_task_without_repeating_startup_admission(string reason)
    {
        await using var fixture = await FinalMoveFixture.CreateAsync();
        var acceptance = (await fixture.Repository.GetByWorkflowNodeExecutionIdAsync(fixture.NodeExecutionId, CancellationToken.None))!;
        acceptance.Status = FieldNavigationAcceptanceStatuses.Moving;
        await fixture.Repository.SaveWithAuditAsync(acceptance, "TestMoving", new { }, CancellationToken.None);
        fixture.Readiness.DeviceOverride = ArrivedStabilizingSnapshot(acceptance) with
        {
            MapMd5 = reason == "map" ? "changed-observer-map" : acceptance.MapMd5,
            DeviceEpoch = reason == "epoch" ? 2 : 1,
            RequiresReauthorization = reason == "reauthorization",
            Online = reason != "offline", ProbeSucceeded = reason != "probe",
            Blocked = reason == "obstacle"
        };
        var dispatches = fixture.Adapter.DispatchCalls;
        await fixture.CompleteAsync();
        await fixture.CompleteAsync();
        Assert.Equal(WorkflowRuntimeStatus.Running,
            (await fixture.Workflows.GetExecutionAsync(fixture.ExecutionId, CancellationToken.None))!.RuntimeStatus);
        Assert.Equal(dispatches, fixture.Adapter.DispatchCalls);
        Assert.Equal(0, fixture.Adapter.ReleaseControlCalls);
    }
}
