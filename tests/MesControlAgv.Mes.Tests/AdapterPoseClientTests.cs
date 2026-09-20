using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Tests;

public sealed class AdapterPoseClientTests
{
    [Theory]
    [InlineData("AGV-01",true)]
    [InlineData("AGV-02",false)]
    public async Task Pose_is_a_single_get_and_rejects_other_device_identity(string returnedId,bool accepted)
    {
        var expected=new AgvPoseResponse(returnedId,1,2,3,.95,"LM1","raw",DateTimeOffset.UtcNow,"guangzhou606","hash",DateTimeOffset.UtcNow);
        var handler=new Handler(HttpStatusCode.OK,JsonContent.Create(expected));
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://adapter.invalid/")};
        var client=new AdapterClient(http);
        if(accepted) Assert.Equal(expected,await client.GetPoseAsync("AGV-01",CancellationToken.None));
        else await Assert.ThrowsAsync<InvalidOperationException>(()=>client.GetPoseAsync("AGV-01",CancellationToken.None));
        Assert.Equal(1,handler.Count);
    }
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Pose_preserves_gateway_failures_without_mutation_or_retry(HttpStatusCode status)
    {
        var handler=new Handler(status,JsonContent.Create(new{detail="pose unavailable"}));
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://adapter.invalid/")};
        var ex=await Assert.ThrowsAsync<AdapterHttpException>(()=>new AdapterClient(http).GetPoseAsync("AGV-01",CancellationToken.None));
        Assert.Equal(status,ex.ResponseStatusCode); Assert.Equal(1,handler.Count);
    }
    private sealed class Handler(HttpStatusCode status,HttpContent content):HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Count++; Assert.Equal(HttpMethod.Get,request.Method); Assert.Equal("/agvs/AGV-01/pose",request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status){Content=content});
        }
    }
}
