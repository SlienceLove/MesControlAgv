using MesControlAgv.Adapter;
using MesControlAgv.Adapter.Modules.AuboArm;
using MesControlAgv.Application;
using MesControlAgv.Domain.Profiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Adapter.Tests;

public sealed class FieldStandardConfigurationTests
{
    [Fact]
    public void Field_standard_template_is_explicit_and_valid_without_opening_a_socket()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "MesControlAgv.Adapter", "appsettings.FieldStandard.json");
        using var stream = File.OpenRead(path);
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        var profile = configuration.GetSection("Profile").Get<ProfileConfiguration>();
        Assert.NotNull(profile);
        var validation = new ProfileConfigurationValidator().Validate(profile);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(error => error.Message)));
        Assert.Equal("vendor-tcp", configuration["Agv:Driver"]);
        Assert.Equal("192.168.1.2", configuration["Agv:Tcp:Host"]);
        Assert.True(configuration.GetValue<bool>("Agv:Tcp:AcquireControl"));
        Assert.False(configuration.GetValue<bool>("Agv:Tcp:EnablePush"));
        Assert.Equal("9012", configuration["Devices:AuboArm:Port"]);
        Assert.Equal(15000, configuration.GetValue<int>("Devices:AuboArm:ProgramCatalogScanTimeoutMs"));
        Assert.Equal(30000, configuration.GetValue<int>("Devices:AuboArm:ProgramCatalogCacheTtlMs"));
        Assert.Equal("standard", configuration["Adapter:RunMode"]);
        Assert.Equal(
            new[] { "取料盘.pro", "放料盘.pro", "回收料盘.pro" },
            configuration.GetSection("Devices:AuboArm:AllowedProgramNames").Get<string[]>());
        Assert.False(configuration.GetValue<bool>("Devices:AuboArm:ControlEnabled"));

        // AddServices only builds registrations; no driver call means no network
        // connection is opened during this configuration smoke test.
        using var provider = new ServiceCollection()
            .AddServices(configuration, "Data Source=field-standard-template-test.db")
            .BuildServiceProvider();
        Assert.IsType<AuboArmProgramDriver>(provider.GetRequiredService<IAuboArmProgramController>());
        Assert.NotNull(provider.GetRequiredService<AuboArmProgramCatalogCache>());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MesControlAgv.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
