using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class MesPoseClientTests
{
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task Legacy_endpoint_reports_not_ready(HttpStatusCode status)
    {
        using var http=new HttpClient(new Handler(status)){BaseAddress=new Uri("http://mes.invalid/")};
        await Assert.ThrowsAsync<NotSupportedException>(()=>new MesClient(http).GetAgvPoseAsync("AGV-01",CancellationToken.None));
    }
    [Fact]
    public async Task Timeout_is_not_mislabelled_as_unsupported()
    {
        using var http=new HttpClient(new Handler(HttpStatusCode.GatewayTimeout)){BaseAddress=new Uri("http://mes.invalid/")};
        await Assert.ThrowsAsync<HttpRequestException>(()=>new MesClient(http).GetAgvPoseAsync("AGV-01",CancellationToken.None));
    }
    [Fact]
    public async Task Pose_contract_is_preserved()
    {
        using var http=new HttpClient(new Handler(HttpStatusCode.OK)){BaseAddress=new Uri("http://mes.invalid/")};
        var pose=await new MesClient(http).GetAgvPoseAsync("AGV-01",CancellationToken.None);
        Assert.Equal("AGV-01",pose.AgvId);Assert.Equal(.8,pose.Y);Assert.Equal("raw",pose.ControllerTimestamp);
    }
    private sealed class Handler(HttpStatusCode status):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Assert.Equal(HttpMethod.Get,request.Method);Assert.Equal("/api/agvs/AGV-01/pose",request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status){Content=JsonContent.Create(new AgvPoseResponse("AGV-01",0,.8,0,.95,"LM1","raw",DateTimeOffset.UtcNow,"guangzhou606","hash",DateTimeOffset.UtcNow))});
        }
    }
}
