using MesControlAgv.Domain;
using MesControlAgv.Adapter.Data;
using MesControlAgv.Adapter.Drivers;
using MesControlAgv.Adapter.Services;
using MesControlAgv.Application;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Adapter;

public static class AdapterCompositionRoot
{
    public static IServiceCollection AddServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString,
        string? simulatorBaseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var profile = BindProfile(configuration);
        var driverId = NormalizeDriverId(configuration["Agv:Driver"]);
        var runMode = AdapterRunMode.Parse(configuration["Adapter:RunMode"]);
        var tcpOptions = configuration.GetSection("Agv:Tcp").Get<TcpAgvOptions>() ?? new TcpAgvOptions();
        ValidatePhysicalAcceptanceOptions(profile, driverId, tcpOptions, runMode);
        var agv = profile.Agvs.FirstOrDefault(item => item.Enabled) ?? profile.Agvs[0];

        services.AddSingleton(profile);
        services.AddSingleton(runMode);
        services.AddSingleton<IProfileConfigurationValidator, ProfileConfigurationValidator>();
        services.AddSingleton<IProfileConfigurationLoader, JsonProfileConfigurationLoader>();
        services.AddSingleton<WorkflowValidator>();
        services.AddDbContext<AdapterDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton(new PathPlanner(AgvMap.FromProfile(profile.Map)));
        services.AddSingleton<MultiAgvScheduler>();
        services.AddSingleton<PhysicalAgvSessionGate>();
        services.AddSingleton<PhysicalAcceptancePreflightService>();
        services.Configure<TcpAgvOptions>(configuration.GetSection("Agv:Tcp"));
        services.PostConfigure<TcpAgvOptions>(options =>
        {
            var physical = profile.PhysicalAcceptance;
            options.RequireCompleteSafetyStatus = physical is not null;
            if (physical is null) return;

            options.RequireAutomaticMode = physical.Safety.RequireAutomaticMode;
            options.MaximumNavigationSpeedMetersPerSecond =
                physical.Safety.MaximumDispatchSpeedMetersPerSecond;
        });

        services.AddHttpClient("simulator", (serviceProvider, client) =>
        {
            var endpoint = simulatorBaseUrl
                ?? configuration["Simulator:BaseUrl"]
                ?? serviceProvider.GetRequiredService<ProfileConfiguration>()
                    .Agvs.FirstOrDefault(item => item.Enabled)?.Endpoint
                ?? "http://localhost:5183/";
            client.BaseAddress = new Uri(endpoint);
        });
        services.AddSingleton<SimulatorClient>(serviceProvider =>
            new SimulatorClient(serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("simulator")));
        services.AddSingleton<TcpAgvClient>();

        services.AddSingleton<SimulatorDriverFactory>(serviceProvider =>
            new SimulatorDriverFactory(
                serviceProvider.GetRequiredService<SimulatorClient>(),
                serviceProvider.GetRequiredService<SimulatorClient>()));
        services.AddSingleton<VendorTcpDriverFactory>(serviceProvider =>
            new VendorTcpDriverFactory(serviceProvider.GetRequiredService<TcpAgvClient>()));
        services.AddSingleton<IAgvDriverFactory>(serviceProvider =>
            serviceProvider.GetRequiredService<SimulatorDriverFactory>());
        services.AddSingleton<IAgvDriverFactory>(serviceProvider =>
            serviceProvider.GetRequiredService<VendorTcpDriverFactory>());
        services.AddSingleton<DriverRegistry>();

        services.AddSingleton<IAgvDeviceClient>(serviceProvider => driverId switch
        {
            SimulatorDriver.DriverKind => serviceProvider.GetRequiredService<SimulatorClient>(),
            VendorTcpDriver.DriverKind => serviceProvider.GetRequiredService<TcpAgvClient>(),
            _ => throw new InvalidOperationException(
                $"Unsupported AGV driver '{driverId}'. Configure 'simulator' or 'vendor-tcp'.")
        });
        if (string.Equals(driverId, SimulatorDriver.DriverKind, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IAgvFleetDeviceClient>(serviceProvider =>
                serviceProvider.GetRequiredService<SimulatorClient>());
        }

        services.AddSingleton<IAgvDriver>(serviceProvider => serviceProvider
            .GetRequiredService<DriverRegistry>()
            .Create(driverId, new AgvDriverOptions(agv.AgvId)));
        services.AddScoped<AdapterService>();
        return services;
    }

    private static string NormalizeDriverId(string? configuredDriverId)
    {
        if (string.IsNullOrWhiteSpace(configuredDriverId)) return SimulatorDriver.DriverKind;
        return string.Equals(configuredDriverId.Trim(), "tcp", StringComparison.OrdinalIgnoreCase)
            ? VendorTcpDriver.DriverKind
            : configuredDriverId.Trim();
    }
    private static void ValidatePhysicalAcceptanceOptions(
        ProfileConfiguration profile,
        string driverId,
        TcpAgvOptions tcpOptions,
        AdapterRunMode runMode)
    {
        var physical = profile.PhysicalAcceptance;
        if (physical is null)
        {
            if (runMode.IsReadOnlyPreflight)
            {
                throw new InvalidOperationException(
                    "Adapter:RunMode=read-only-preflight requires a physical acceptance profile.");
            }
            return;
        }

        if (!string.Equals(driverId, VendorTcpDriver.DriverKind, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Physical acceptance profiles require Agv:Driver=vendor-tcp.");
        }

        if (!string.Equals(tcpOptions.NickName, physical.ExpectedControlOwner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Agv:Tcp:NickName must match Profile:PhysicalAcceptance:ExpectedControlOwner.");
        }

        if (runMode.IsReadOnlyPreflight)
        {
            if (tcpOptions.AcquireControl)
            {
                throw new InvalidOperationException(
                    "Read-only preflight requires Agv:Tcp:AcquireControl=false.");
            }
            if (tcpOptions.EnablePush)
            {
                throw new InvalidOperationException(
                    "Read-only preflight requires Agv:Tcp:EnablePush=false.");
            }
            if (profile.Features.EnableAutomaticDispatch
                || profile.Features.EnableFieldNavigationAcceptance
                || profile.Features.EnableTaskCancellation)
            {
                throw new InvalidOperationException(
                    "Read-only preflight requires automatic dispatch, field navigation acceptance, and task cancellation to be disabled.");
            }
        }
        else if (!tcpOptions.AcquireControl)
        {
            throw new InvalidOperationException(
                "Physical acceptance profiles require Agv:Tcp:AcquireControl=true outside read-only preflight mode.");
        }

        if (profile.Features.EnableAutomaticDispatch)
        {
            throw new InvalidOperationException(
                "Physical acceptance profiles must keep automatic dispatch disabled until live controller map verification is available.");
        }

        if (!double.IsFinite(tcpOptions.MinimumConfidence)
            || tcpOptions.MinimumConfidence < physical.Safety.MinimumLocalizationConfidence)
        {
            throw new InvalidOperationException(
                "Agv:Tcp:MinimumConfidence cannot be below the approved physical acceptance threshold.");
        }
    }

    private static ProfileConfiguration BindProfile(IConfiguration configuration)
    {
        var profile = configuration.GetSection("Profile").Get<ProfileConfiguration>()
            ?? ProfileConfiguration.Default;
        var validation = new ProfileConfigurationValidator().Validate(profile);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                "The configured AGV profile is invalid: " +
                string.Join("; ", validation.Errors.Select(error => error.Message)));
        }
        return profile;
    }
}
