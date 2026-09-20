using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using MesControlAgv.Wpf.DigitalTwin;

internal static class ContinuityCheck
{
    // Runs only inside the offline smoke host. No device or network source is used here.
    public static async Task<JsonElement> MeasureAsync(CoreWebView2 core, string output, string scenario, TwinScenePose expected)
    {
        await core.ExecuteScriptAsync("""
            (()=>{
              const start=performance.now(), samples=[];
              window.poseContinuitySamples=null;
              function tick(){
                const s=window.digitalTwin.inspectMotion();
                samples.push({...s,elapsed:s.now-start});
                // 5 s path + up to 620 ms polling gap + up to 1 s settling + final samples.
                if(s.now-start<7000)requestAnimationFrame(tick);
                else window.poseContinuitySamples=samples;
              }
              requestAnimationFrame(tick);
            })()
            """);
        var until=DateTime.UtcNow.AddSeconds(20);
        while(await core.ExecuteScriptAsync("window.poseContinuitySamples!==null")!="true")
        {
            if(DateTime.UtcNow>until)throw new TimeoutException("Offline animation measurement did not complete");
            await Task.Delay(100);
        }
        using var all=JsonDocument.Parse(await core.ExecuteScriptAsync("window.poseContinuitySamples"));
        var samples=all.RootElement.EnumerateArray().ToArray();
        var moving=samples.Where(s=>s.GetProperty("elapsed").GetDouble() is >=1500 and <=4500).ToArray();
        if(moving.Length<20)throw new InvalidOperationException("Too few native frames to assess continuity");
        var intervals=new List<double>();var idle=0;var reversals=0;
        for(var i=1;i<moving.Length;i++)
        {
            var dx=moving[i].GetProperty("pose").GetProperty("x").GetDouble()-moving[i-1].GetProperty("pose").GetProperty("x").GetDouble();
            if(Math.Abs(dx)<1e-7)idle++;
            if(dx>1e-6)reversals++; // map +X projects to scene -X in the accepted frame.
            intervals.Add(moving[i].GetProperty("now").GetDouble()-moving[i-1].GetProperty("now").GetDouble());
        }
        intervals.Sort();
        var idleFraction=(double)idle/(moving.Length-1);
        if(idleFraction>=.2||reversals!=0)throw new InvalidOperationException($"Native continuity failed: idle={idleFraction:P1}, reversals={reversals}");
        var last=samples[^1];
        if(last.GetProperty("active").GetBoolean())throw new InvalidOperationException("Animation did not settle after repeated stationary targets");
        foreach(var sample in samples.TakeLast(4))
        {
            var p=sample.GetProperty("pose");var delta=p.GetProperty("yaw").GetDouble()-expected.Yaw;
            if(Math.Abs(p.GetProperty("x").GetDouble()-expected.X)>1e-6||
               Math.Abs(p.GetProperty("z").GetDouble()-expected.Z)>1e-6||
               Math.Abs(p.GetProperty("y").GetDouble()-expected.Y)>1e-6||
               Math.Abs(Math.Atan2(Math.Sin(delta),Math.Cos(delta)))>1e-6)
                throw new InvalidOperationException("Animation stopped without reaching the final known pose");
        }
        var evidence=new {
            source="offline synthetic path, existing 500 ms WPF poller; no physical I/O",scenario,expected,
            passed=true,sampleCount=samples.Length,movingSamples=moving.Length,idleFraction,reversals,
            frameIntervalMedianMs=intervals[intervals.Count/2],frameIntervalP95Ms=intervals[(int)((intervals.Count-1)*.95)],
            frameIntervalMaxMs=intervals[^1],settled=true,samples=all.RootElement.Clone()
        };
        var json=JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true});
        await File.WriteAllTextAsync(Path.Combine(output,$"continuity-{scenario}.json"),json);
        using var result=JsonDocument.Parse(json);return result.RootElement.Clone();
    }
}
