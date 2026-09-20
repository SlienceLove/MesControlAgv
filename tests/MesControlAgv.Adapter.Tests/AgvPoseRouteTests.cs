using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Adapter.Modules;
using MesControlAgv.Adapter.Services;
using MesControlAgv.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MesControlAgv.Adapter.Tests;

public sealed class AgvPoseRouteTests
{
    [Theory]
    [InlineData("AGV-01", 200)]
    [InlineData("OTHER", 404)]
    [InlineData("AGV-01", 501)]
    [InlineData("AGV-01", 503)]
    [InlineData("AGV-01", 504)]
    public async Task Readonly_route_validates_identity_capability_and_failures(string id, int code)
    {
        // Ephemeral loopback HTTP only. No Adapter Program/hosted workers, no
        // device initialization, no database creation, no physical connection.
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var module = new AgvAdapterModule();
        module.AddServices(builder.Services, new(builder.Configuration, "Data Source=:memory:"));
        var device = code == 501 ? new NoPoseDevice() : new PoseDevice(code);
        builder.Services.RemoveAll<IAgvDeviceClient>();
        builder.Services.AddSingleton<IAgvDeviceClient>(device);
        await using var app = builder.Build();
        module.MapEndpoints(app);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await http.GetAsync($"/agvs/{id}/pose");
        Assert.Equal(code, (int)response.StatusCode);
        if (code == 200)
        {
            var pose = await response.Content.ReadFromJsonAsync<AgvPoseResponse>();
            Assert.Equal("AGV-01", pose!.AgvId); // Configured identity, not driver payload.
            Assert.Equal(.8, pose.Y);
            Assert.Equal("raw-clock", pose.ControllerTimestamp);
        }
        if (device is PoseDevice reader) Assert.Equal(code == 404 ? 0 : 1, reader.Reads);
        Assert.Equal(0, device.NonPoseCalls);
    }

    private sealed class PoseDevice(int code) : NoPoseDevice, IAgvPoseDeviceClient
    {
        public int Reads;
        public Task<AgvPoseResponse> GetPoseAsync(CancellationToken token)
        {
            Reads++;
            if (code == 503) throw new IOException("Disconnected");
            if (code == 504) throw new TimeoutException();
            return Task.FromResult(new AgvPoseResponse("UNTRUSTED", 0, .8, -3.1237, .95, "LM1", "raw-clock",
                DateTimeOffset.UtcNow, "guangzhou606", "hash", DateTimeOffset.UtcNow));
        }
    }

    private class NoPoseDevice : IAgvDeviceClient
    {
        public int NonPoseCalls;
        private Exception Unexpected() { NonPoseCalls++; return new InvalidOperationException("Unexpected non-pose device call"); }
        public Task EnsureControlAsync(CancellationToken token) => throw Unexpected();
        public Task<bool> ReleaseControlAsync(CancellationToken token) => throw Unexpected();
        public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken token) => throw Unexpected();
        public Task<AgvTaskResponse?> GetTaskAsync(Guid id, CancellationToken token) => throw Unexpected();
        public Task<AgvTaskResponse> NavigateAsync(Guid id, string? source, string target, CancellationToken token) => throw Unexpected();
        public Task<AgvTaskResponse?> PauseAsync(Guid id, CancellationToken token) => throw Unexpected();
        public Task<AgvTaskResponse?> ResumeAsync(Guid id, CancellationToken token) => throw Unexpected();
        public Task<AgvTaskResponse?> CancelAsync(Guid id, CancellationToken token) => throw Unexpected();
    }
}
