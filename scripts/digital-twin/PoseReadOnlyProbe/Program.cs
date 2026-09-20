using System.Text.Json;
using MesControlAgv.Adapter;
using MesControlAgv.Adapter.Services;
using MesControlAgv.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Explicit opt-in diagnostic. No application host, scheduler, push setup, or control API is started.
if (args.Length != 2 || args[0] != "--live-readonly")
{
    Console.Error.WriteLine("Usage: --live-readonly <new-evidence-json-path> (reads AGV-01 at 192.168.1.2:19204 only)");
    return 2;
}
var output = Path.GetFullPath(args[1]);
if (File.Exists(output)) throw new IOException("Refusing to overwrite existing evidence.");
using var client = new TcpAgvClient(Options.Create(new TcpAgvOptions {
    Host="192.168.1.2",StatusPort=19204,CommandPort=1,ControlPort=1,OtherPort=1,
    EnablePush=false,AcquireControl=false,RequestTimeoutMs=2000,ConnectTimeoutMs=1500
}),NullLogger<TcpAgvClient>.Instance,AdapterRunMode.ReadOnlyPreflight);
var readings = new List<AgvPoseResponse>();
try
{
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    for(var i=0;i<3;i++)
    {
        readings.Add((await client.GetPoseAsync(stop.Token)) with {AgvId="AGV-01"});
        if(i<2) await Task.Delay(500,stop.Token);
    }
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new {
        readOnly=true,source="192.168.1.2:19204",apis=new[]{1300,1302,1101},
        physicalActionsSent=false,readings
    },new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine(JsonSerializer.Serialize(new{readings=readings.Count,last=readings[^1],evidence=output}));
    return 0;
}
catch(Exception ex)
{
    Console.Error.WriteLine($"Read-only pose probe failed: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
