using System.IO;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.DigitalTwin;

namespace MesControlAgv.Wpf.Tests;

public sealed class DigitalTwinPoseTests
{
    private static AgvPoseResponse Pose(DateTimeOffset now) => new("AGV-01",.2,.8,3.12,.95,"LM1","raw",now,
        "guangzhou606",TwinCalibration.MapMd5,now);
    private static TwinCalibrationSettings Settings(double theta=.4) => new(TwinCalibration.MapMd5,TwinCalibration.AssetSha256,
        TwinCalibration.Stations.Where(p=>p.Id is "LM1" or "LM2" or "LM6").Select(p=>new TwinCalibrationPoint(p.Id,
            Math.Cos(theta)*p.X+Math.Sin(theta)*p.Y+16,Math.Sin(theta)*p.X-Math.Cos(theta)*p.Y-4)).ToArray(),
        -.025,0,0,0,true);

    [Fact]
    public void Rigid_fit_recovers_translation_rotation_and_y_up_heading()
    {
        var fit=TwinCalibration.Fit(Settings());
        Assert.Equal(.4,fit.Theta,10); Assert.Equal(16,fit.Tx,10); Assert.Equal(-4,fit.Tz,10); Assert.InRange(fit.MaxResidual,0,1e-10);
        var p=Pose(DateTimeOffset.UtcNow) with{X=2,Y=3,Angle=1};
        var scene=fit.Project(p);
        Assert.Equal(Math.Cos(.4)*2+Math.Sin(.4)*3+16,scene.X,10);
        Assert.Equal(Math.Sin(.4)*2-Math.Cos(.4)*3-4,scene.Z,10);
        Assert.Equal(.6,scene.Yaw,10); Assert.Equal(-.025,scene.Y);
    }
    [Fact]
    public void Reference_offset_rotates_with_the_whole_robot()
    {
        var fit=TwinCalibration.Fit(Settings(0) with{ReferenceX=.2,ReferenceZ=.1,HeadingOffsetRadians=.3});
        var p=Pose(DateTimeOffset.UtcNow) with{X=0,Y=0,Angle=Math.PI/2-.3};
        var scene=fit.Project(p);
        Assert.Equal(15.9,scene.X,10); Assert.Equal(-3.8,scene.Z,10); Assert.Equal(Math.PI/2,scene.Yaw,10);
    }
    [Fact]
    public void Nearly_collinear_LM1_LM7_LM2_are_rejected()
    {
        var points=TwinCalibration.Stations.Where(p=>p.Id is "LM1" or "LM2" or "LM7").Select(p=>new TwinCalibrationPoint(p.Id,p.X,-p.Y)).ToArray();
        Assert.Contains("共线",Assert.Throws<ArgumentException>(()=>TwinCalibration.Fit(Settings() with{Points=points})).Message);
    }
    [Theory]
    [InlineData("unconfirmed")]
    [InlineData("asset")]
    [InlineData("map")]
    [InlineData("duplicate")]
    [InlineData("nonfinite")]
    [InlineData("scale")]
    [InlineData("mirror")]
    [InlineData("offset")]
    public void Bad_calibration_cannot_be_enabled(string fault)
    {
        var s=Settings();
        s=fault switch {
            "unconfirmed"=>s with{Confirmed=false}, "asset"=>s with{AssetHash="other"},"map"=>s with{MapHash="other"},
            "duplicate"=>s with{Points=[s.Points[0],s.Points[0],s.Points[2]]},
            "nonfinite"=>s with{FloorY=double.NaN}, "offset"=>s with{ReferenceX=double.PositiveInfinity},
            "scale"=>s with{Points=s.Points.Select(p=>p with{SceneX=p.SceneX*2,SceneZ=p.SceneZ*2}).ToArray()},
            _=>s with{Points=s.Points.Select(p=>p with{SceneZ=-p.SceneZ}).ToArray()}
        };
        Assert.Throws<ArgumentException>(()=>TwinCalibration.Fit(s));
    }
    [Fact]
    public void Save_and_load_revalidate_identity_and_preserve_points()
    {
        var folder=Path.Combine(Path.GetTempPath(),"twin-test-"+Guid.NewGuid()); var path=Path.Combine(folder,"calibration.json");
        try {
            TwinCalibration.Save(path,Settings()); var fit=TwinCalibration.Load(path);
            Assert.Equal(.4,fit.Theta,10); Assert.Equal(Settings().Points,fit.Settings.Points);
            Assert.Single(Directory.GetFiles(folder));
        } finally { if(Directory.Exists(folder)) Directory.Delete(folder,true); }
    }
    [Fact]
    public void Fresh_pose_is_accepted_and_invalid_variants_are_rejected()
    {
        var now=DateTimeOffset.UtcNow; var p=Pose(now);
        Assert.Null(TwinPoseValidation.Rejection(p,"AGV-01",now));
        AgvPoseResponse[] invalid=[p with{AgvId="AGV-02"},p with{X=null},p with{Y=double.NaN},p with{Angle=double.PositiveInfinity},
            p with{Confidence=.79},p with{Confidence=null},p with{Confidence=double.NaN},p with{Confidence=1.1},
            p with{ReceivedAt=now.AddSeconds(-3)},p with{ReceivedAt=now.AddSeconds(2)},p with{MapObservedAt=null},
            p with{MapObservedAt=now.AddSeconds(-16)},p with{MapMd5="other"},p with{MapName="other"}];
        Assert.All(invalid,item=>Assert.NotNull(TwinPoseValidation.Rejection(item,"AGV-01",now)));
    }
    [Fact]
    public async Task Polling_is_serial_and_dispose_stops_reading()
    {
        var source=new Source(); using var session=new DigitalTwinPoseSession(source,TimeSpan.FromMilliseconds(10));
        var received=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Reading+=(_,_)=>received.TrySetResult(); session.Start();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3)); session.Dispose(); await session.Completion;
        var count=source.Count; await Task.Delay(80);
        Assert.Equal(count,source.Count); Assert.Equal(1,source.MaxActive);
    }
    [Fact]
    public async Task Timed_out_noncooperative_source_never_overlaps_a_replacement_or_publishes_late_success()
    {
        var source=new Source { Slow=true }; using var session=new DigitalTwinPoseSession(source,TimeSpan.FromMilliseconds(1),TimeSpan.FromMilliseconds(10));
        var failed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var successes=0;
        session.Reading+=(p,e)=>{if(p is not null)successes++;if(e is not null)failed.TrySetResult();};
        session.Start(); await failed.Task.WaitAsync(TimeSpan.FromSeconds(3)); await Task.Delay(40);
        Assert.Equal(1,source.Count); session.Dispose(); await session.Completion;
        source.Release.TrySetResult(); await Task.Delay(30); Assert.Equal(0,successes);
    }
    private sealed class Source:IDigitalTwinPoseSource
    {
        public int Count,Active,MaxActive; public bool Slow;
        public TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AgvPoseResponse> ReadPoseAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count); var active=Interlocked.Increment(ref Active); MaxActive=Math.Max(MaxActive,active);
            try { if(Slow) await Release.Task; else await Task.Delay(20,cancellationToken); return Pose(DateTimeOffset.UtcNow); }
            finally {Interlocked.Decrement(ref Active);}
        }
    }
}
