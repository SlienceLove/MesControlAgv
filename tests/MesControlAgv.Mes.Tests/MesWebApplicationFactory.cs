using MesControlAgv.Application;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
}


