using MesControlAgv.Application;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MesControlAgv.Mes.Tests;

public sealed class MesWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"mes-control-agv-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<MesDbContext>>();
            services.RemoveAll<MesDbContext>();
            services.RemoveAll<IAgvGateway>();
            services.AddDbContext<MesDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
            // Keep the adapter's current operation id across the create/dispatch
            // request and the subsequent fleet-status request.  This gives API
            // tests the same deterministic correlation signal as a real Adapter.
            services.AddSingleton<IAgvGateway, TestAdapterClient>();
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration =>
        {
            // Minimal-host applications execute their top-level statements
            // before WebApplicationFactory replays ConfigureAppConfiguration.
            // DeferredHostBuilder turns this early host configuration into
            // command-line arguments, so Program sees the complete simulator
            // profile before it binds ProfileConfiguration.
            var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter() }
            };
            var json = JsonSerializer.Serialize(
                new { Profile = ProfileConfiguration.Default },
                serializerOptions);
            configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        });

        return base.CreateHost(builder);
    }
}


