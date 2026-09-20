using System.Text;
using System.Text.Json;
using MesControlAgv.Adapter.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Adapter.Tests;

public sealed class TcpAgvPoseTests
{
    private const string Hash = "9bd67a8b01f4da2617ce67e5f8a8d6b1";
    private static byte[] Respond(AgvTcpPacket packet, string pose) => Encoding.UTF8.GetBytes(packet.ApiId switch
    {
        1300 => "{\"ret_code\":0,\"current_map\":\"guangzhou606\",\"maps\":[\"guangzhou606.smap\"]}",
        1302 => "{\"ret_code\":0,\"map_info\":[{\"name\":\"guangzhou606.smap\",\"md5\":\"" + Hash + "\"}]}",
        1101 => pose,
        _ => throw new InvalidOperationException($"Unexpected (potentially mutating) API {packet.ApiId}")
    });
    private static TcpAgvClient Client(int port) => new(Options.Create(new TcpAgvOptions {
        Host="127.0.0.1", StatusPort=port, CommandPort=1, ControlPort=1, OtherPort=1,
        EnablePush=false, ConnectTimeoutMs=1000, RequestTimeoutMs=1000 }), NullLogger<TcpAgvClient>.Instance);

    [Fact]
    public async Task Pose_reads_only_status_apis_uses_angle_and_caches_identity_without_refreshing_its_time()
    {
        const string payload = "{\"ret_code\":0,\"x\":0.0049,\"y\":\"0.8012\",\"angle\":-3.1237,\"yaw\":0.0396,\"confidence\":0.9514,\"current_station\":\"LM1\",\"create_on\":\"untrusted-clock\"}";
        await using var server = new TcpApiTestServer(4, p => {
            if (p.ApiId == 1101) { using var json=JsonDocument.Parse(p.Payload); Assert.False(json.RootElement.GetProperty("return_laser").GetBoolean()); }
            return Task.FromResult(Respond(p,payload));
        });
        using var client = Client(server.Port);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var before = DateTimeOffset.UtcNow;
        var first = await client.GetPoseAsync(stop.Token);
        var second = await client.GetPoseAsync(stop.Token);
        await server.Completion;
        Assert.Equal(new ushort[] {1300,1302,1101,1101}, server.ApiIds);
        Assert.Equal(-3.1237, first.Angle); Assert.Equal(.8012, first.Y); Assert.Equal(.0049,first.X);
        Assert.Equal(.9514, first.Confidence); Assert.Equal("LM1", first.CurrentStation);
        Assert.Equal("untrusted-clock", first.ControllerTimestamp); Assert.Equal(Hash,first.MapMd5);
        Assert.Equal(first.MapObservedAt,second.MapObservedAt);
        Assert.InRange(first.ReceivedAt,before,DateTimeOffset.UtcNow);
        Assert.Equal("", first.AgvId); // Endpoint, not controller payload, establishes configured identity.
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"x\":\"NaN\",\"y\":\"Infinity\",\"angle\":\"NaN\"}")]
    [InlineData("{\"x\":null,\"y\":{},\"yaw\":0.5}")]
    public async Task Missing_or_invalid_values_are_not_silently_replaced_with_zero(string payload)
    {
        await using var server = new TcpApiTestServer(3,p => Task.FromResult(Respond(p,payload)));
        using var client=Client(server.Port);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pose=await client.GetPoseAsync(stop.Token);
        Assert.Null(pose.X); Assert.Null(pose.Y); Assert.Null(pose.Angle); Assert.Null(pose.Confidence);
        await server.Completion;
    }

    [Fact]
    public async Task Reconnect_invalidates_identity_until_fresh_identity_queries_complete()
    {
        await using var server=new TcpApiTestServer(7,p=>Task.FromResult(Respond(p,"{\"ret_code\":0,\"x\":0,\"y\":0,\"angle\":0,\"confidence\":1}")),
            closeAfterResponse: (_,index)=>index==2);
        using var client=Client(server.Port);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var first=await client.GetPoseAsync(stop.Token);
        var reconnected=await client.GetPoseAsync(stop.Token);
        var refreshed=await client.GetPoseAsync(stop.Token);
        Assert.NotNull(first.MapMd5); Assert.Null(reconnected.MapMd5); Assert.Null(reconnected.MapObservedAt);
        Assert.Equal(Hash,refreshed.MapMd5);
        Assert.Equal(new ushort[]{1300,1302,1101,1101,1300,1302,1101},server.ApiIds);
        await server.Completion;
    }
}
