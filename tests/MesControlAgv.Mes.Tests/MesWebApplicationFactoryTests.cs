using MesControlAgv.Domain.Profiles;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Mes.Tests;

public sealed class MesWebApplicationFactoryTests
{
    [Fact]
    public void Test_host_binds_the_complete_simulator_profile()
    {
        using var factory = new MesWebApplicationFactory();

        var profile = factory.Services.GetRequiredService<ProfileConfiguration>();

        Assert.True(profile.Features.UseSimulator);
        Assert.Equal("simulator", profile.Agvs.Single().Driver);
        Assert.Equal("http://localhost:5183/", profile.Agvs.Single().Endpoint);
        Assert.Null(profile.PhysicalAcceptance);
        Assert.Contains("CHARGE_01", profile.Map.StationIds);
    }
}
