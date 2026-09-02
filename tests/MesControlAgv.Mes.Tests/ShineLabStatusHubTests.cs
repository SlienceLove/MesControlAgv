using System.Text.Json;
using System.Net.Http.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Tests;

public sealed class ShineLabStatusHubTests(MesWebApplicationFactory factory)
    : IClassFixture<MesWebApplicationFactory>
{
    [Fact]
    public void UpdateInfo_is_projected_and_stale_connection_becomes_offline()
    {
        var hub = CreateHub();
        var body = JsonSerializer.SerializeToElement(new
        {
            status = 1,
            task_uuid = "task-001",
            sampleID = "S-01",
            sampleName = "标准样",
            channel = "A",
            position = 11,
            stage = "Injecting",
            progress = 35
        });

        hub.Apply("msg-001", "UpdateInfo", "SHA18I", body);

        var online = Assert.Single(hub.GetStatuses(DateTimeOffset.UtcNow));
        Assert.True(online.Online);
        Assert.True(online.HasActiveTask);
        Assert.Equal("task-001", online.TaskUuid);
        Assert.Equal("A", online.Channel);
        Assert.Equal(11, online.Position);
        Assert.Equal(35, online.Progress);

        hub.Apply("msg-002", "Certification", "SHA18I", JsonSerializer.SerializeToElement(new { }), "connection-2");
        hub.MarkDisconnected("SHA18I", "connection-1");
        var reconnected = Assert.Single(hub.GetStatuses(DateTimeOffset.UtcNow));
        Assert.True(reconnected.Online);

        var stale = Assert.Single(hub.GetStatuses(reconnected.LastSeenAtUtc.AddSeconds(11)));
        Assert.False(stale.Online);
        Assert.False(stale.HasActiveTask);
    }

    [Fact]
    public async Task Mes_status_endpoint_returns_only_ShineLab_pushed_devices()
    {
        var hub = CreateHub();
        hub.Apply(
            "msg-001",
            "Certification",
            "SHA18I",
            JsonSerializer.SerializeToElement(new { }));
        using var configuredFactory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ShineLabStatusHub>();
            services.AddSingleton(hub);
        }));
        using var client = configuredFactory.CreateClient();

        var response = await client.GetAsync("/api/shinelab/devices/status");
        var statuses = await response.Content.ReadFromJsonAsync<List<ShineLabDeviceStatusResponse>>();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(statuses);
        Assert.Single(statuses);
        Assert.Equal("SHA18I", statuses[0].EquipmentCode);
    }

    private static ShineLabStatusHub CreateHub() =>
        new(Options.Create(new ShineLabTcpOptions { StaleAfterSeconds = 10 }));
}
