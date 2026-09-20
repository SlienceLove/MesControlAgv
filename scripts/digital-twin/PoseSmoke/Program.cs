using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.DigitalTwin;
using MesControlAgv.Wpf.Views;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output=Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/digital-twin-pose-smoke");
        Directory.CreateDirectory(output);
        var exit=1;
        var app=new System.Windows.Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        app.Startup+=async (_,_)=>{
            Window? window=null;
            try
            {
                var source=new OfflineSource();
                var view=new DigitalTwinView { StatusSource=source };
                // Test private state only; never write synthetic calibration into the user's saved settings.
                Set(view,"_calibration",null);
                window=new Window { Title="数字孪生离线位姿验证",Content=view,Width=1480,Height=1000,
                    Left=-10000,Top=0,ShowInTaskbar=false,ShowActivated=false };
                window.Show();
                await Wait(()=>view.State.IsReady,()=>view.State.Status);
                var core=((Grid)view.FindName("BrowserHost")).Children.OfType<WebView2>().Single().CoreWebView2;
                await Wait(()=>source.ReadCount>=2,()=>"pose source not polled");
                var initial=await Inspect(core);
                Check(initial.GetProperty("cameraView").GetString()=="overview","Default view is not overview");
                Check(Math.Abs(initial.GetProperty("cameraDirection")[2].GetDouble())<.000001,"Default view is diagonally skewed along Z");
                Check(initial.GetProperty("ground").GetProperty("enabled").GetBoolean(),"Ground missing");
                Check(!initial.GetProperty("ground").GetProperty("aligned").GetBoolean(),"Unconfirmed map was overlaid on CAD");
                Check(initial.GetProperty("ground").GetProperty("stationCount").GetInt32()==6,"Missing map station references");
                var groundBounds=initial.GetProperty("ground").GetProperty("bounds");
                Check(Math.Abs(groundBounds[1][0].GetDouble()-groundBounds[0][0].GetDouble()-20)<.001,"Expected 2 m extra floor margin per X side");
                Check(Math.Abs(groundBounds[1][2].GetDouble()-groundBounds[0][2].GetDouble()-15)<.001,"Expected 2 m extra floor margin per Z side");
                Check(initial.GetProperty("ground").GetProperty("gridMeters").GetInt32()==1,"Grid scale must remain one metre");
                void Command(string tag)=>Find<Button>(view).Single(b=>b.IsVisible && b.Tag as string==tag).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(!Find<Button>(view).Any(b=>b.Tag as string=="top"),"Removed top-view entry still present");
                await ReferenceVisibilityCheck.CheckAsync(core,false);
                Command("routes-toggle");await WaitScript(core,"!window.digitalTwin.inspect().ground.routesEnabled");
                Command("routes-toggle");await ReferenceVisibilityCheck.CheckAsync(core,false);
                Command("map-reference-toggle");await ReferenceVisibilityCheck.CheckAsync(core,true);
                Command("map-reference-toggle");await ReferenceVisibilityCheck.CheckAsync(core,false);
                Command("ground-toggle");await WaitScript(core,"!window.digitalTwin.inspect().ground.enabled");
                Command("ground-toggle");await WaitScript(core,"window.digitalTwin.inspect().ground.enabled");
                Command("equipment-overview");
                var agv=Node(initial,"agv-composite-a");
                var x=agv.GetProperty("position")[0].GetDouble(); var z=agv.GetProperty("position")[2].GetDouble();
                source.X=.6;
                await Task.Delay(800);
                var uncalibrated=await Inspect(core);
                Check(Node(uncalibrated,"agv-composite-a").GetProperty("position")[0].GetDouble()==x,"Uncalibrated model moved");
                Check(((TextBlock)view.FindName("PoseStatusText")).Text.Contains("未标定"),"Missing uncalibrated label");
                await Capture.Save(view,Path.Combine(output,"position-unconfigured-ui.png"));
                Command("reset");await Task.Delay(100);await Capture.Save(view,Path.Combine(output,"floor-full-overview.png"));
                Command("equipment-overview");
                // Schematic mode follows actual input coordinates without making a measured calibration.
                var calibrationFile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MesControlAgv","DigitalTwin","606-agv-calibration.json");
                var calibrationBefore=File.Exists(calibrationFile)?File.ReadAllText(calibrationFile):null;
                source.X=0;source.Y=.8;source.Angle=-3.1237;
                await Task.Delay(600);
                ((CheckBox)view.FindName("SchematicFollowCheck")).IsChecked=true;
                var lm1Expected=TwinSchematicAlignment.Project(new AgvPoseResponse("AGV-01",0,.8,-3.1237,.95,null,null,DateTimeOffset.UtcNow,"guangzhou606",TwinCalibration.MapMd5,DateTimeOffset.UtcNow));
                await WaitScript(core,$"window.digitalTwin.inspect().ground.schematic && Math.abs(window.digitalTwin.inspect().nodes[0].position[0]-({Invariant(lm1Expected.X)}))<.002");
                Check((await Inspect(core)).GetProperty("cameraView").GetString()=="overview","Unexpected view after first pose");
                Check(!Find<Expander>(view).Single().IsExpanded,"Reference mode opened calibration editor");
                await ReferenceVisibilityCheck.CheckAsync(core,false);
                await Capture.Save(view,Path.Combine(output,"reference-off.png"));
                var beforeReference=await Inspect(core);
                Command("map-reference-toggle");await ReferenceVisibilityCheck.CheckAsync(core,true);
                Check(Node(beforeReference,"agv-composite-a").GetProperty("position").ToString()==Node(await Inspect(core),"agv-composite-a").GetProperty("position").ToString(),"Reference toggle moved robot");
                await WaitScript(core,"window.digitalTwin.inspect().stationScreen.length===6 && window.digitalTwin.inspect().stationScreen.every(s=>s.ndc.every(n=>Math.abs(n)<1))");
                await Capture.Save(view,Path.Combine(output,"reference-on.png"));
                Command("routes-toggle");await WaitScript(core,"!window.digitalTwin.inspect().ground.routesVisible");
                Command("map-reference-toggle");Command("routes-toggle");await ReferenceVisibilityCheck.CheckAsync(core,false);
                Command("equipment-overview");
                await WaitScript(core,"window.digitalTwin.inspect().cameraView==='overview'");
                await Capture.Save(view,Path.Combine(output,"aligned-overview-lm1.png"));
                var beforeResize=await Inspect(core);
                window.Width=1100;window.Height=850;
                await Task.Delay(700);
                var afterResize=await Inspect(core);
                Check(afterResize.GetProperty("cameraView").GetString()=="overview","Resize changed camera type");
                Check(Math.Abs(afterResize.GetProperty("cameraDirection")[2].GetDouble())<.000001,"Resize skewed overview");
                foreach(var id in new[]{"agv-composite-a","d160-a","autosampler-a","decapper-a"})
                    Check(Node(beforeResize,id).GetProperty("position").ToString()==Node(afterResize,id).GetProperty("position").ToString(),"View change moved device: "+id);
                window.Width=1480;window.Height=1000;await Task.Delay(300);
                Check(!((await Inspect(core)).GetProperty("ground").GetProperty("aligned").GetBoolean()),"Schematic mode claimed measured calibration");
                await Capture.Save(view,Path.Combine(output,"schematic-lm1.png"));
                for(var step=1;step<=16;step++)
                {
                    if(step==8){Command("map-reference-toggle");await ReferenceVisibilityCheck.CheckAsync(core,true);}
                    source.X=3.671*step/16;source.Y=.8+.285*step/16;
                    source.Angle=-3.1237+(Math.Atan2(.285,3.671)+3.1237)*step/16;
                    var expected=TwinSchematicAlignment.Project(new AgvPoseResponse("AGV-01",source.X,source.Y,source.Angle,.95,null,null,DateTimeOffset.UtcNow,"guangzhou606",TwinCalibration.MapMd5,DateTimeOffset.UtcNow));
                    await WaitScript(core,$"Math.abs(window.digitalTwin.inspect().nodes[0].position[0]-({Invariant(expected.X)}))<.0000001 && Math.abs(window.digitalTwin.inspect().nodes[0].position[2]-({Invariant(expected.Z)}))<.0000001");
                }
                var schematicEnd=await Inspect(core);
                Check(schematicEnd.GetProperty("cameraView").GetString()=="overview","Continuous poses changed camera type");
                Check(view.IsSchematicFollowing,"Reference toggles stopped schematic following");
                var stationLm6=schematicEnd.GetProperty("ground").GetProperty("stationPositions").EnumerateArray().Single(s=>s.GetProperty("id").GetString()=="LM6").GetProperty("position");
                var robotAtLm6=Node(schematicEnd,"agv-composite-a").GetProperty("position");
                Check(Math.Abs(stationLm6[0].GetDouble()-robotAtLm6[0].GetDouble())<.002&&Math.Abs(stationLm6[1].GetDouble()-robotAtLm6[2].GetDouble())<.002,"Robot and station used different transforms");
                foreach(var id in new[]{"d160-a","autosampler-a","decapper-a"})
                    Check(Node(initial,id).GetProperty("position").ToString()==Node(schematicEnd,id).GetProperty("position").ToString(),"Schematic follow moved a static device");
                source.Stale=true;source.X+=.4;await Task.Delay(1000);
                Check(Node(await Inspect(core),"agv-composite-a").GetProperty("position").ToString()==robotAtLm6.ToString(),"Schematic mode accepted stale coordinates");
                source.Stale=false;source.Confidence=.1;await Task.Delay(800);
                Check(Node(await Inspect(core),"agv-composite-a").GetProperty("position").ToString()==robotAtLm6.ToString(),"Schematic mode accepted low confidence");
                source.X=3.671;source.Confidence=.95;await Task.Delay(700);
                await Capture.Save(view,Path.Combine(output,"schematic-lm6.png"));
                // Exercise a real overview ray selection, then focus and check world pose preservation.
                // D160 is occluded by the benches in the new perspective-only overview.
                // Pick the exposed AGV mesh; disable DOM label interception only in this test.
                await core.ExecuteScriptAsync("document.querySelectorAll('.device-label').forEach(e=>e.style.pointerEvents='none')");
                var selectedBefore=Node(await Inspect(core),"agv-composite-a");
                var screenPoint=selectedBefore.GetProperty("screenPoint");
                foreach(var type in new[]{"mousePressed","mouseReleased"})
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{
                        type,x=screenPoint[0].GetDouble(),y=screenPoint[1].GetDouble(),button="left",clickCount=1}));
                await WaitScript(core,"window.digitalTwin.inspect().selectedId==='agv-composite-a'");
                await core.ExecuteScriptAsync("document.querySelectorAll('.device-label').forEach(e=>e.style.pointerEvents='')");
                Command("focus");
                await WaitScript(core,"window.digitalTwin.inspect().cameraView==='overview' && window.digitalTwin.inspect().nodes.find(n=>n.id==='agv-composite-a').visible");
                Check(Node(await Inspect(core),"agv-composite-a").GetProperty("position").ToString()==selectedBefore.GetProperty("position").ToString(),"Focus moved selected device");
                Command("show-all");
                Check((await Inspect(core)).GetProperty("cameraView").GetString()=="overview","Show all changed camera type");
                Command("map-reference-toggle");await ReferenceVisibilityCheck.CheckAsync(core,false);
                // Actual WPF poller -> bridge -> renderer continuity, with a synthetic time-based source.
                var motionStart=Stopwatch.GetTimestamp();
                var motionAngle=source.Angle;
                source.Trajectory=()=> (3.671+.15*Math.Clamp(Stopwatch.GetElapsedTime(motionStart).TotalSeconds,0,5),1.085,motionAngle);
                TwinScenePose Project(double px,double py,double angle)=>TwinSchematicAlignment.Project(new AgvPoseResponse("AGV-01",px,py,angle,.95,null,null,DateTimeOffset.UtcNow,"guangzhou606",TwinCalibration.MapMd5,DateTimeOffset.UtcNow));
                var continuity=await ContinuityCheck.MeasureAsync(core,output,"steady-015mps",Project(4.421,1.085,motionAngle));
                Command("map-reference-toggle");await ReferenceVisibilityCheck.CheckAsync(core,true);
                source.LatencyPattern=[0,120,40,80,0,60];
                motionStart=Stopwatch.GetTimestamp();
                source.Trajectory=()=> {var elapsed=Math.Clamp(Stopwatch.GetElapsedTime(motionStart).TotalSeconds,0,5);return (4.421+.3*elapsed,1.085,motionAngle+.12*elapsed);};
                var jitterContinuity=await ContinuityCheck.MeasureAsync(core,output,"jitter-030mps-turn",Project(5.921,1.085,motionAngle+.6));
                Check(view.State.Readings[0].Condition==TwinCondition.Online,"Unavailable workstation blocked AGV status");
                Check(view.State.Readings[1].Condition==TwinCondition.Online,"Unavailable workstation blocked arm status");
                Check(view.State.Readings[2].Condition==TwinCondition.Unavailable,"Unresponsive workstation was shown healthy");
                Check(source.WorkstationReads==1,"Unresponsive workstation reads overlapped");
                source.Trajectory=null;source.LatencyPattern=null;source.X=5.921;source.Y=1.085;source.Angle=motionAngle+.6;
                await Capture.Save(view,Path.Combine(output,"continuous-motion-offline.png"));
                ((CheckBox)view.FindName("SchematicFollowCheck")).IsChecked=false;
                await WaitScript(core,"!window.digitalTwin.inspect().ground.schematic");
                Check(!view.IsSchematicFollowing,"Schematic mode did not exit");
                Check(calibrationBefore==(File.Exists(calibrationFile)?File.ReadAllText(calibrationFile):null),"Schematic mode wrote measured calibration");
                source.X=.6;source.Y=.8;source.Angle=0;
                // Wait for the new synthetic frame before installing a different test transform.
                // Otherwise an old LM6 sample can legitimately trip the 1.5 m jump guard.
                await Wait(()=>((TextBlock)view.FindName("PoseStatusText")).Text.Contains("x=0.600"),()=>"New offline frame not received");
                var settings=new TwinCalibrationSettings(TwinCalibration.MapMd5,TwinCalibration.AssetSha256,
                    TwinCalibration.Stations.Where(p=>p.Id is "LM1" or "LM2" or "LM6").Select(p=>new TwinCalibrationPoint(p.Id,p.X+x,-p.Y+z+.8)).ToArray(),
                    -.025,0,0,0,true);
                Set(view,"_calibration",TwinCalibration.Fit(settings));
                await WaitScript(core,$"Math.abs(window.digitalTwin.inspect().nodes[0].position[0]-{Invariant(x+.6)})<.002");
                var moving=await Inspect(core);
                Check(moving.GetProperty("ground").GetProperty("aligned").GetBoolean(),"Confirmed transform not applied to map overlay");
                foreach(var id in new[]{"d160-a","autosampler-a","decapper-a"})
                    Check(Node(initial,id).GetProperty("position").ToString()==Node(moving,id).GetProperty("position").ToString(),"Static device moved: "+id);
                Check(moving.GetProperty("modelTriangles").GetInt32()==1287310,"Model geometry changed");
                Check(moving.GetProperty("triangles").GetInt32()<=1288000,"Ground geometry budget exceeded");
                Check(moving.GetProperty("nodes").GetArrayLength()==4,"AGV and arm were split");
                using(var capture=File.Create(Path.Combine(output,"offline-pose-follow.png")))
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,capture);
                source.Confidence=.1; source.X=1;
                await Task.Delay(1000);
                var lowConfidence=await Inspect(core);
                Check(Math.Abs(Node(lowConfidence,"agv-composite-a").GetProperty("position")[0].GetDouble()-(x+.6))<.002,"Low-confidence pose moved model");
                source.Confidence=.95; source.X=.9;
                await WaitScript(core,$"Math.abs(window.digitalTwin.inspect().nodes[0].position[0]-{Invariant(x+.9)})<.002");
                source.Stale=true;source.X=1.2;
                await Task.Delay(1000);
                var stale=await Inspect(core);
                Check(Math.Abs(Node(stale,"agv-composite-a").GetProperty("position")[0].GetDouble()-(x+.9))<.002,"Stale pose moved model");
                source.Stale=false;source.MapHash="wrong";
                await Task.Delay(1000);
                Check(((TextBlock)view.FindName("PoseStatusText")).Text.Contains("地图"),"Map mismatch not reported");
                var expander=Find<Expander>(view).Single();expander.IsExpanded=true;
                await WaitScript(core,"!window.digitalTwin.inspect().ground.aligned");
                Command("equipment-overview");
                Invoke(view,"PickCalibration_Click");
                await Task.Delay(100);
                using(var screen=JsonDocument.Parse(await core.ExecuteScriptAsync("({x:innerWidth/2,y:innerHeight/2})")))
                    foreach(var type in new[]{"mousePressed","mouseReleased"})
                        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{
                            type,x=screen.RootElement.GetProperty("x").GetDouble(),y=screen.RootElement.GetProperty("y").GetDouble(),button="left",clickCount=1}));
                await Wait(()=>((TextBlock)view.FindName("CalibrationPointsText")).Text.Contains("LM1:"),()=>"Ground-picking bridge failed");
                await Capture.Save(view,Path.Combine(output,"calibration-ui-offline.png"));
                var confirmation=(CheckBox)view.FindName("CalibrationConfirmed");
                foreach(var field in new[]{"CalibrationReferenceX","CalibrationReferenceZ","CalibrationHeading","CalibrationFloor"})
                {
                    confirmation.IsChecked=true;
                    ((TextBox)view.FindName(field)).Text="0.01";
                    Check(confirmation.IsChecked==false,"Changed calibration reused old confirmation: "+field);
                }
                Check(((TextBlock)view.FindName("CalibrationPointsText")).Text=="尚未选点","Floor change retained incompatible pick points");
                window.Content=null;
                await Wait(()=>!view.State.IsReady,()=>"Viewer not released");
                var count=source.ReadCount; await Task.Delay(900);
                Check(count==source.ReadCount,"Pose polling continued after unload");
                source.WorkstationRelease.TrySetResult(TwinProjection.Pending(TwinChannel.Workstation,source.Bindings.WorkstationId));
                var evidence=new{passed=true,source="offline synthetic coordinates only; no physical I/O",uncalibratedStatic=true,
                    rigidCompositeMoved=true,staticDevicesUnchanged=true,lowConfidenceFrozen=true,staleFrozen=true,
                    mapMismatchRejected=true,groundPickingPassed=true,numericChangesInvalidateConfirmation=true,unloadStopsPolling=true,
                    mapGroundPassed=true,uncalibratedMapSeparate=true,groundAndRouteTogglesPassed=true,
                    schematicRealCoordinateFollowPassed=true,schematicNoCalibrationWritePassed=true,schematicStationAlignmentPassed=true,
                    alignedOverviewPassed=true,topEntryRemoved=true,referenceMasterTogglePassed=true,referenceDoesNotBlockPose=true,
                    unresponsiveWorkstationIsolated=true,raySelectionAndFocusPassed=true,continuityPassed=continuity.GetProperty("passed").GetBoolean(),
                    jitterContinuityPassed=jitterContinuity.GetProperty("passed").GetBoolean(),initial,moving,schematicEnd};
                File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}));
                Console.WriteLine("PASS: offline WPF/WebView pose, calibration picking, rigid group, freshness and unload.");exit=0;
            }
            catch(Exception ex){Console.Error.WriteLine(ex);File.WriteAllText(Path.Combine(output,"error.txt"),ex.ToString());}
            finally{window?.Close();app.Shutdown();}
        };
        app.Run();return exit;
    }
    private static string Invariant(double value)=>value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static JsonElement Node(JsonElement scene,string id)=>scene.GetProperty("nodes").EnumerateArray().Single(n=>n.GetProperty("id").GetString()==id);
    private static async Task<JsonElement> Inspect(CoreWebView2 core){using var d=JsonDocument.Parse(await core.ExecuteScriptAsync("window.digitalTwin.inspect()"));return d.RootElement.Clone();}
    private static void Set(object target,string field,object? value)=>target.GetType().GetField(field,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(target,value);
    private static void Invoke(object target,string method)=>target.GetType().GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(target,[target,new RoutedEventArgs()]);
    private static void Check(bool value,string detail){if(!value)throw new InvalidOperationException(detail);}
    private static async Task Wait(Func<bool> condition,Func<string> error){var until=DateTime.UtcNow.AddSeconds(30);while(!condition()){if(DateTime.UtcNow>until)throw new TimeoutException(error());await Task.Delay(50);}}
    private static async Task WaitScript(CoreWebView2 core,string expression){var until=DateTime.UtcNow.AddSeconds(10);while(await core.ExecuteScriptAsync(expression)!="true"){if(DateTime.UtcNow>until)throw new TimeoutException(expression);await Task.Delay(50);}}
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T:DependencyObject
    {for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);i++){var child=System.Windows.Media.VisualTreeHelper.GetChild(parent,i);if(child is T t)yield return t;foreach(var nested in Find<T>(child))yield return nested;}}
    private sealed class OfflineSource:IDigitalTwinStatusSource,IDigitalTwinPoseSource
    {
        public string SourceDisplay=>"离线测试数据 · 非现场定位";public TwinBindings Bindings {get;}=new();
        public double X,Y=.8,Angle,Confidence=.95;public bool Stale;public string MapHash=TwinCalibration.MapMd5;public int ReadCount;
        public Func<(double X,double Y,double Angle)>? Trajectory;
        public int[]? LatencyPattern;
        public int WorkstationReads;
        public TaskCompletionSource<TwinReading> WorkstationRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TwinReading> ReadAsync(TwinChannel channel,CancellationToken cancellationToken)
        {
            if(channel==TwinChannel.Workstation){Interlocked.Increment(ref WorkstationReads);return WorkstationRelease.Task;}
            return Task.FromResult(new TwinReading(channel,Bindings.Id(channel),TwinCondition.Online,"在线（离线模拟）","仅离线测试",DateTimeOffset.UtcNow));
        }
        public async Task<AgvPoseResponse> ReadPoseAsync(CancellationToken cancellationToken)
        {
            var count=Interlocked.Increment(ref ReadCount);
            if(LatencyPattern is {Length:>0} pattern)await Task.Delay(pattern[count%pattern.Length],cancellationToken);
            var now=DateTimeOffset.UtcNow;var p=Trajectory?.Invoke()??(X,Y,Angle);
            return new AgvPoseResponse("AGV-01",p.Item1,p.Item2,p.Item3,Confidence,"LM1","offline",Stale?now.AddSeconds(-5):now,"guangzhou606",MapHash,now);
        }
    }
}
