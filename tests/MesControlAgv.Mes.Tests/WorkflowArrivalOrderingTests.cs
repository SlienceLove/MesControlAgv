using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Tests;

public sealed partial class WorkflowFieldNavigationWorkerTests
{
    [Theory]
    [InlineData(FieldNavigationAcceptanceStatuses.Accepted)]
    [InlineData(FieldNavigationAcceptanceStatuses.Moving)]
    public async Task Idle_ready_without_durable_arrival_never_completes_or_releases(string pendingStatus)
    {
        await using var fixture = await FinalMoveFixture.CreateAsync();
        var acceptance = (await fixture.Repository.GetByWorkflowNodeExecutionIdAsync(
            fixture.NodeExecutionId, CancellationToken.None))!;
        acceptance.Status = pendingStatus;
        await fixture.Repository.SaveWithAuditAsync(acceptance, "TestAwaitingArrival", new { }, CancellationToken.None);
        fixture.Readiness.DeviceOverride = ArrivedStabilizingSnapshot(acceptance);
        var dispatches = fixture.Adapter.DispatchCalls;
        await fixture.CompleteAsync();
        var operationBefore = Assert.Single(await fixture.Workflows.ListDeviceOperationsAsync(fixture.ExecutionId, CancellationToken.None));
        await fixture.CompleteAsync();
        var operationWaiting = Assert.Single(await fixture.Workflows.ListDeviceOperationsAsync(fixture.ExecutionId, CancellationToken.None));
        Assert.Equal(operationBefore.UpdatedAt, operationWaiting.UpdatedAt);
        fixture.Readiness.DeviceOverride = fixture.Readiness.DeviceOverride with { State = PhysicalDeviceReadinessState.Ready };
        await fixture.CompleteAsync();
        await fixture.CompleteAsync();
        Assert.Equal(WorkflowRuntimeStatus.Running,
            (await fixture.Workflows.GetExecutionAsync(fixture.ExecutionId, CancellationToken.None))!.RuntimeStatus);
        Assert.Equal(dispatches, fixture.Adapter.DispatchCalls);
        Assert.Equal(0, fixture.Adapter.ReleaseControlCalls);

        acceptance.Status = FieldNavigationAcceptanceStatuses.Arrived;
        await fixture.Repository.SaveWithAuditAsync(acceptance, "TestDurableArrival", new { }, CancellationToken.None);
        await fixture.CompleteAsync();
        await fixture.CompleteAsync();
        Assert.Equal(WorkflowRuntimeStatus.Completed,
            (await fixture.Workflows.GetExecutionAsync(fixture.ExecutionId, CancellationToken.None))!.RuntimeStatus);
        Assert.Equal(dispatches, fixture.Adapter.DispatchCalls);
        Assert.Equal(1, fixture.Adapter.ReleaseControlCalls);
    }

    [Theory]
    [InlineData("unconsumed")]
    [InlineData("no-vendor")]
    [InlineData("acceptance-epoch")]
    [InlineData("acceptance-supervisor")]
    [InlineData("acceptance-agv")]
    [InlineData("unknown")]
    [InlineData("offline")]
    [InlineData("wrong-station")]
    [InlineData("blocker")]
    [InlineData("map")]
    [InlineData("other-task")]
    [InlineData("restart")]
    public async Task Waiting_for_arrival_does_not_accept_unbound_or_unsafe_observation(string scenario)
    {
        await using var fixture = await FinalMoveFixture.CreateAsync();
        var acceptance = (await fixture.Repository.GetByWorkflowNodeExecutionIdAsync(
            fixture.NodeExecutionId, CancellationToken.None))!;
        acceptance.Status = scenario == "unknown" ? FieldNavigationAcceptanceStatuses.Unknown : FieldNavigationAcceptanceStatuses.Moving;
        if (scenario == "unconsumed") acceptance.PermitConsumedAtUtc = null;
        if (scenario == "no-vendor") acceptance.DeviceTaskId = null;
        if (scenario == "acceptance-epoch") acceptance.DeviceEpoch++;
        if (scenario == "acceptance-supervisor") acceptance.ReadinessSupervisorInstanceId = "previous-supervisor";
        if (scenario == "acceptance-agv") fixture.Database.Entry(acceptance).Property(item => item.AgvId).CurrentValue = "AGV-OTHER";
        await fixture.Repository.SaveWithAuditAsync(acceptance, "TestPendingArrival", new { }, CancellationToken.None);
        fixture.Readiness.DeviceOverride = ArrivedStabilizingSnapshot(acceptance) with
        {
            Online = scenario != "offline",
            CurrentStationId = scenario == "wrong-station" ? "other-station" : acceptance.TargetStationId,
            BlockingReasons = scenario == "blocker" ? ["controller_faults_active"] : [],
            MapMd5 = scenario == "map" ? "other-map" : acceptance.MapMd5,
            ActiveTaskId = scenario == "other-task" ? Guid.NewGuid() : null
        };
        var dispatches = fixture.Adapter.DispatchCalls;
        if (scenario == "restart")
            await new WorkflowFieldNavigationDispatcher(fixture.Workflows, fixture.AcceptanceService,
                fixture.Repository, fixture.Profile, new WorkflowFieldNavigationWorkerOptions { Enabled = true },
                agv: fixture.Adapter, physicalReadiness: fixture.Readiness).RecoverAsync(CancellationToken.None);
        else
            await fixture.CompleteAsync();
        var mustStop = scenario is "unconsumed" or "no-vendor" or "acceptance-agv" or "unknown" or "blocker" or "other-task" or "restart";
        Assert.Equal(mustStop ? WorkflowRuntimeStatus.Unknown : WorkflowRuntimeStatus.Running,
            (await fixture.Workflows.GetExecutionAsync(fixture.ExecutionId, CancellationToken.None))!.RuntimeStatus);
        Assert.Equal(dispatches, fixture.Adapter.DispatchCalls);
        Assert.Equal(0, fixture.Adapter.ReleaseControlCalls);
    }
}
