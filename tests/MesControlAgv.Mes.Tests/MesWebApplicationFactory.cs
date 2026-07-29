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
            services.RemoveAll<IAdapterClient>();
            services.AddDbContext<MesDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
            services.AddScoped<IAdapterClient, TestAdapterClient>();
        });
    }
}
