using System.Collections.Concurrent;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Mes.Tests;

// These tests intentionally compress the production 8 s/10 s windows to 100/250 ms.
// Keep unrelated ASP.NET host startup workloads from exhausting those artificial deadlines.
[CollectionDefinition("Physical readiness timing", DisableParallelization = true)]
public sealed class PhysicalReadinessTimingCollection { }

[Collection("Physical readiness timing")]
public sealed class PhysicalReadinessIsolationTests
{
    [Fact]
    public async Task Unresponsive_workstation_does_not_stale_or_revoke_healthy_agv()
    {
        var state=new ProbeState { BlockWorkstation=true };
        using var provider=Provider(state);
        var supervisor=provider.GetRequiredService<PhysicalReadinessSupervisor>();
        await supervisor.StartAsync(CancellationToken.None);
        try
        {
            await Until(()=>Device(supervisor,"AGV-01").State==PhysicalDeviceReadinessState.Ready);
            var ready=Device(supervisor,"AGV-01");
            Assert.True(supervisor.AcknowledgeAuthorization("AGV-01",ready.DeviceEpoch,supervisor.GetSnapshot().SupervisorInstanceId));
            // More than several observation-staleness windows, while the other call never returns.
            await Task.Delay(850);
            var fresh=Device(supervisor,"AGV-01");
            Assert.Equal(PhysicalDeviceReadinessState.Ready,fresh.State);
            Assert.Equal(ready.DeviceEpoch,fresh.DeviceEpoch);
            Assert.False(fresh.RequiresReauthorization);
            Assert.True(state.Count("AGV-01")>=5);
            Assert.Equal(1,state.Count("SAMPLE-WORKSTATION-01"));
            Assert.NotEqual(PhysicalDeviceReadinessState.Ready,Device(supervisor,"SAMPLE-WORKSTATION-01").State);
            Assert.False(supervisor.GetSnapshot().SchedulingPermitted); // whole-device policy unchanged
            Assert.True(supervisor.GetSnapshot().RefreshInProgress); // abandoned I/O still owns its slot
            Assert.Equal(0,state.WorkstationDisposed);
        }
        finally { await supervisor.StopAsync(CancellationToken.None);state.Release.TrySetResult(); }
        await Until(()=>state.WorkstationDisposed==1);
    }

    [Fact]
    public async Task Concurrent_manual_refreshes_never_overlap_the_same_device()
    {
        var state=new ProbeState { BlockWorkstation=true,AgvDelay=30 };
        using var provider=Provider(state);
        var supervisor=provider.GetRequiredService<PhysicalReadinessSupervisor>();
        await supervisor.StartAsync(CancellationToken.None);
        try
        {
            await Until(()=>state.Count("SAMPLE-WORKSTATION-01")==1);
            await Task.WhenAll(Enumerable.Range(0,20).Select(_=>supervisor.RefreshAsync(true,CancellationToken.None)))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.All(state.Maximum.Values,n=>Assert.Equal(1,n));
            Assert.Equal(1,state.Count("SAMPLE-WORKSTATION-01"));
            Assert.True(state.Count("AGV-01")>0);
        }
        finally { await supervisor.StopAsync(CancellationToken.None);state.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Timed_out_success_is_ignored_and_recovery_requires_new_authorization()
    {
        var state=new ProbeState();
        using var provider=Provider(state);
        var supervisor=provider.GetRequiredService<PhysicalReadinessSupervisor>();
        await supervisor.RefreshAsync(true,CancellationToken.None);
        var before=Device(supervisor,"SAMPLE-WORKSTATION-01");
        Assert.True(supervisor.AcknowledgeAuthorization(before.DeviceId,before.DeviceEpoch,supervisor.GetSnapshot().SupervisorInstanceId));
        state.BlockWorkstation=true;
        await supervisor.RefreshAsync(false,CancellationToken.None);
        var failed=Device(supervisor,before.DeviceId);
        Assert.NotEqual(PhysicalDeviceReadinessState.Ready,failed.State);
        Assert.True(failed.RequiresReauthorization);
        var disposed=state.WorkstationDisposed;
        state.BlockWorkstation=false;state.Release.TrySetResult();
        await Until(()=>state.WorkstationDisposed>disposed && !supervisor.GetSnapshot().RefreshInProgress);
        Assert.Equal(failed,Device(supervisor,before.DeviceId)); // late success was never published
        await supervisor.RefreshAsync(false,CancellationToken.None);
        var recovered=Device(supervisor,before.DeviceId);
        Assert.Equal(PhysicalDeviceReadinessState.Ready,recovered.State);
        Assert.True(recovered.DeviceEpoch>before.DeviceEpoch);
        Assert.True(recovered.RequiresReauthorization);
        Assert.False(supervisor.IsCurrentAndReady(before.DeviceId,before.DeviceEpoch,out _));
        Assert.Contains(state.FullCalls,c=>c.Id==before.DeviceId && c.Full);
    }

    [Fact]
    public async Task Shutdown_does_not_wait_for_noncooperative_io_or_publish_its_late_result()
    {
        var state=new ProbeState { BlockWorkstation=true };
        using var provider=Provider(state);
        var supervisor=provider.GetRequiredService<PhysicalReadinessSupervisor>();
        await supervisor.StartAsync(CancellationToken.None);
        await Until(()=>state.Count("SAMPLE-WORKSTATION-01")==1);
        await supervisor.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        var before=Device(supervisor,"SAMPLE-WORKSTATION-01");
        var agvCalls=state.Count("AGV-01");
        state.Release.TrySetResult();
        await Until(()=>state.WorkstationDisposed==1);
        Assert.Equal(before,Device(supervisor,before.DeviceId));
        Assert.Equal(agvCalls,state.Count("AGV-01"));
        Assert.False(supervisor.GetSnapshot().RefreshInProgress);
    }

    [Fact]
    public async Task Fault_triggered_by_timeout_cancellation_is_drained_before_slot_reuse()
    {
        var state=new ProbeState { BlockWorkstation=true,FaultOnCancellation=true };
        using var provider=Provider(state);
        var supervisor=provider.GetRequiredService<PhysicalReadinessSupervisor>();
        await supervisor.RefreshAsync(true,CancellationToken.None);
        await Until(()=>state.WorkstationDisposed==1 && !supervisor.GetSnapshot().RefreshInProgress);
        Assert.True(state.Release.Task.IsFaulted);
        Assert.NotEqual(PhysicalDeviceReadinessState.Ready,Device(supervisor,"SAMPLE-WORKSTATION-01").State);
        state.BlockWorkstation=false;
        await supervisor.RefreshAsync(true,CancellationToken.None);
        Assert.Equal(PhysicalDeviceReadinessState.Ready,Device(supervisor,"SAMPLE-WORKSTATION-01").State);
        Assert.All(state.Maximum.Values,n=>Assert.Equal(1,n));
    }

    [Fact]
    public async Task Canceled_manual_refresh_keeps_scope_until_late_fault_is_observed()
    {
        var state=new ProbeState { BlockWorkstation=true };
        using var provider=Provider(state);
        var supervisor=provider.GetRequiredService<PhysicalReadinessSupervisor>();
        using var cancel=new CancellationTokenSource();
        var refresh=supervisor.RefreshAsync(true,cancel.Token);
        await Until(()=>state.Count("SAMPLE-WORKSTATION-01")==1);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>refresh);
        Assert.Equal(0,state.WorkstationDisposed);
        state.Release.TrySetException(new IOException("Late transport failure"));
        await Until(()=>state.WorkstationDisposed==1 && !supervisor.GetSnapshot().RefreshInProgress);
        Assert.NotEqual(PhysicalDeviceReadinessState.Ready,Device(supervisor,"SAMPLE-WORKSTATION-01").State);
    }

    private static PhysicalDeviceReadinessSnapshot Device(PhysicalReadinessSupervisor s,string id)=>s.GetSnapshot().Devices.Single(d=>d.DeviceId==id);
    private static async Task Until(Func<bool> condition)
    {
        var until=DateTime.UtcNow.AddSeconds(4);
        while(!condition()){if(DateTime.UtcNow>until)throw new TimeoutException("Readiness test condition");await Task.Delay(10);}
    }
    private static ServiceProvider Provider(ProbeState state)
    {
        var services=new ServiceCollection();
        services.AddSingleton(new ProfileConfiguration {
            Product=new ProductProfile{ProductId="test",DisplayName="test",Version="1"},
            Agvs=[new AgvProfile{AgvId="AGV-01",Model="test",Driver="vendor-tcp",Endpoint="tcp://unreachable.invalid:1",Enabled=true,HomeStationId="LM1"}],
            WorkflowDevices=[new WorkflowDeviceProfile{DeviceId="SAMPLE-WORKSTATION-01",DeviceFamily="sample-workstation",Enabled=true,ControlEnabled=true}],
            Stations=[],Map=new MapProfile(),Features=new FeatureFlags{UseSimulator=false},Timeouts=new TimeoutOptions()});
        services.AddSingleton(new PhysicalReadinessSupervisorOptions{Enabled=true,PollInterval=TimeSpan.FromMilliseconds(30),
            ProbeTimeout=TimeSpan.FromMilliseconds(100),ObservationStaleAfter=TimeSpan.FromMilliseconds(250),ReadyStabilityWindow=TimeSpan.Zero});
        services.AddSingleton<TimeProvider>(TimeProvider.System);services.AddSingleton(state);
        services.AddSingleton<PhysicalReadinessStateStore>();services.AddScoped<IPhysicalDeviceReadinessProbe,ScopedProbe>();
        services.AddSingleton<PhysicalReadinessSupervisor>();services.AddLogging();
        return services.BuildServiceProvider();
    }
    private sealed class ProbeState
    {
        public volatile bool BlockWorkstation;
        public bool FaultOnCancellation;
        public int AgvDelay,WorkstationDisposed;
        public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<string,int> Calls {get;}=new();
        public ConcurrentDictionary<string,int> Active {get;}=new();
        public ConcurrentDictionary<string,int> Maximum {get;}=new();
        public ConcurrentBag<(string Id,bool Full)> FullCalls {get;}=[];
        public int Count(string id)=>Calls.GetValueOrDefault(id);
    }
    private sealed class ScopedProbe(ProbeState state):IPhysicalDeviceReadinessProbe,IDisposable
    {
        private bool _workstation,_disposed;
        public bool CanProbe(PhysicalDeviceDescriptor device)=>true;
        public async Task<PhysicalDeviceReadinessObservation> ProbeAsync(PhysicalDeviceDescriptor device,bool fullPreflight,CancellationToken token)
        {
            _workstation=device.DeviceId=="SAMPLE-WORKSTATION-01";
            state.Calls.AddOrUpdate(device.DeviceId,1,(_,n)=>n+1);
            var active=state.Active.AddOrUpdate(device.DeviceId,1,(_,n)=>n+1);
            state.Maximum.AddOrUpdate(device.DeviceId,active,(_,n)=>Math.Max(n,active));
            state.FullCalls.Add((device.DeviceId,fullPreflight));
            try
            {
                using var faultRegistration=token.Register(()=>
                {
                    if(_workstation && state.FaultOnCancellation)state.Release.TrySetException(new IOException("Fault at timeout/cancellation boundary"));
                });
                if(_workstation && state.BlockWorkstation)await state.Release.Task; // deliberately ignores cancellation
                if(!_workstation && state.AgvDelay>0)await Task.Delay(state.AgvDelay,token);
                Assert.False(_disposed);
                var now=DateTimeOffset.UtcNow;
                return new PhysicalDeviceReadinessObservation{DeviceId=device.DeviceId,DeviceFamily=device.DeviceFamily,Online=true,ProbeSucceeded=true,
                    IsFullPreflight=fullPreflight,FullPreflightPassed=true,ObservedAtUtc=now,FullPreflightObservedAtUtc=fullPreflight?now:null};
            }
            finally { state.Active.AddOrUpdate(device.DeviceId,0,(_,n)=>n-1); }
        }
        public void Dispose(){_disposed=true;if(_workstation)Interlocked.Increment(ref state.WorkstationDisposed);}
    }
}
