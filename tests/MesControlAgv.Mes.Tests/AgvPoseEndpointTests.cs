using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MesControlAgv.Mes.Tests;

public sealed class AgvPoseEndpointTests
{
    [Fact]
    public async Task Unsupported_gateway_has_explicit_501()
    {
        using var factory=new MesWebApplicationFactory();using var http=factory.CreateClient();
        using var result=await http.GetAsync("/api/agvs/AGV-01/pose");
        Assert.Equal(HttpStatusCode.NotImplemented,result.StatusCode);
    }
    [Theory]
    [InlineData(200)]
    [InlineData(504)]
    [InlineData(501)]
    public async Task Endpoint_only_invokes_pose_and_preserves_gateway_status(int code)
    {
        var gateway=new PoseGateway(code);
        using var factory=new MesWebApplicationFactory();
        using var configured=factory.WithWebHostBuilder(builder=>builder.ConfigureTestServices(services=>{
            services.RemoveAll<IAgvGateway>();services.AddSingleton<IAgvGateway>(gateway);
        }));
        using var http=configured.CreateClient();
        using var response=await http.GetAsync("/api/agvs/AGV-01/pose");
        Assert.Equal(code,(int)response.StatusCode);Assert.Equal(1,gateway.Reads);
        if(code==200){var pose=await response.Content.ReadFromJsonAsync<AgvPoseResponse>();Assert.Equal("AGV-01",pose!.AgvId);Assert.Equal(1,pose.X);}
    }
    private sealed class PoseGateway(int code):IAgvGateway,IAgvPoseGateway
    {
        public int Reads;
        public Task<AgvPoseResponse> GetPoseAsync(string agvId,CancellationToken cancellationToken)
        {Reads++;if(code!=200)throw new AdapterHttpException((HttpStatusCode)code,"probe status");return Task.FromResult(new AgvPoseResponse(agvId,1,2,3,.95,null,null,DateTimeOffset.UtcNow,"map","hash",DateTimeOffset.UtcNow));}
        public Task<AgvTaskResponse> DispatchAsync(Guid id,string target,CancellationToken token)=>throw new InvalidOperationException("Unexpected control call");
        public Task<AgvTaskResponse?> GetTaskAsync(Guid id,CancellationToken token)=>throw new InvalidOperationException("Unexpected task call");
        public Task<AgvTaskResponse?> CancelAsync(Guid id,CancellationToken token)=>throw new InvalidOperationException("Unexpected control call");
        public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken token)=>throw new InvalidOperationException("Unexpected snapshot call");
        public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(string id,string command,Guid? taskId,CancellationToken token)=>throw new InvalidOperationException("Unexpected control call");
    }
}
