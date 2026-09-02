using MesControlAgv.Adapter.Data;
using MesControlAgv.Adapter.Drivers;
using MesControlAgv.Adapter.Services;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Adapter.Modules;

public sealed class AgvAdapterModule : IDeviceAdapterModule
{
    public DeviceAdapterModuleDescriptor Descriptor { get; } = new(
        ModuleId: "agv",
        DeviceType: "agv",
        SupportedTransports: [DeviceTransportKind.Simulator, DeviceTransportKind.Tcp]);

    public IReadOnlyList<DeviceAdapterRegistration> GetDevices(DeviceAdapterModuleContext context)
    {
        var profile = BindProfile(context.Configuration);
        var driverId = NormalizeDriverId(context.Configuration["Agv:Driver"]);
        var transport = string.Equals(driverId, SimulatorDriver.DriverKind, StringComparison.OrdinalIgnoreCase)
            ? DeviceTransportKind.Simulator
            : DeviceTransportKind.Tcp;
        return profile.Agvs.Select(agv => new DeviceAdapterRegistration(
            agv.AgvId,
            Descriptor.DeviceType,
            Descriptor.ModuleId,
            driverId,
            transport,
            agv.Enabled,
            agv.Enabled && (context.Configuration.GetValue<bool?>(
                $"Devices:{agv.AgvId}:ControlEnabled") ?? true)))
            .ToArray();
    }

    public void AddServices(IServiceCollection services, DeviceAdapterModuleContext context)
    {
        var configuration = context.Configuration;
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
        services.AddDbContext<AdapterDbContext>(options => options.UseSqlite(context.ConnectionString));
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
            var endpoint = context.SimulatorBaseUrl
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
    }

    public async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AdapterDbContext>();
        await database.Database.EnsureCreatedAsync(cancellationToken);
        await AddColumnIfMissingAsync(database, "AgvId", cancellationToken);
        await AddColumnIfMissingAsync(database, "PathJson", cancellationToken);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/tasks/{taskId:guid}/dispatch", async (
            Guid taskId,
            AdapterAgvDispatchRequest request,
            AdapterService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.DispatchAsync(
                    taskId,
                    request.SourceStationId,
                    request.TargetStationId,
                    request.AgvId,
                    request.Path,
                    cancellationToken));
            }
            catch (DispatchDisabledException exception) { return Results.Conflict(new { detail = exception.Message }); }
            catch (ControlUnavailableException exception) { return Results.Conflict(new { detail = exception.Message }); }
            catch (AgvUnavailableException exception) { return Results.Conflict(new { detail = exception.Message }); }
            catch (KeyNotFoundException exception) { return Results.UnprocessableEntity(new { detail = exception.Message }); }
            catch (InvalidOperationException exception) { return Results.UnprocessableEntity(new { detail = exception.Message }); }
        });

        endpoints.MapPost("/field-navigation-acceptances/{acceptanceId:guid}/dispatch", async (
            Guid acceptanceId,
            FieldNavigationDispatchCommand command,
            AdapterService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.DispatchFieldNavigationAcceptanceAsync(
                    acceptanceId,
                    command,
                    cancellationToken));
            }
            catch (DispatchDisabledException exception) { return Results.Conflict(new { detail = exception.Message }); }
            catch (ControlUnavailableException exception) { return Results.Conflict(new { detail = exception.Message }); }
            catch (AgvUnavailableException exception) { return Results.Conflict(new { detail = exception.Message }); }
            catch (PhysicalPreflightRejectedException exception)
            {
                return Results.UnprocessableEntity(new { detail = exception.Message, reasons = exception.Reasons });
            }
            catch (KeyNotFoundException exception) { return Results.UnprocessableEntity(new { detail = exception.Message }); }
            catch (InvalidOperationException exception) { return Results.UnprocessableEntity(new { detail = exception.Message }); }
        });

        endpoints.MapGet("/tasks/{taskId:guid}", async (
            Guid taskId,
            AdapterService service,
            CancellationToken cancellationToken) =>
        {
            var task = await service.GetAsync(taskId, cancellationToken);
            return task is null ? Results.NotFound() : Results.Ok(task);
        });

        endpoints.MapPost("/tasks/{taskId:guid}/{action}", async (
            Guid taskId,
            string action,
            AdapterService service,
            CancellationToken cancellationToken) =>
        {
            if (action is not ("pause" or "resume" or "cancel")) return Results.NotFound();
            try
            {
                var task = action switch
                {
                    "pause" => await service.PauseAsync(taskId, cancellationToken),
                    "resume" => await service.ResumeAsync(taskId, cancellationToken),
                    _ => await service.CancelAsync(taskId, cancellationToken)
                };
                return task is null ? Results.NotFound() : Results.Ok(task);
            }
            catch (DispatchDisabledException exception)
            {
                return Results.Conflict(new { detail = exception.Message });
            }
            catch (ControlUnavailableException exception)
            {
                return Results.Conflict(new { detail = exception.Message });
            }
        });

        endpoints.MapGet("/agv/snapshot", async (
            IAgvDeviceClient device,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await device.GetSnapshotAsync(cancellationToken);
            return Results.Ok(snapshot with
            {
                Capabilities = snapshot.Capabilities ?? AgvCapabilitiesResponse.Standard
            });
        });

        endpoints.MapGet("/agv/io", async (
            IAgvDeviceClient device,
            CancellationToken cancellationToken) =>
        {
            if (device is not IAgvIoDeviceClient io)
            {
                return Results.Problem(
                    "The configured AGV driver does not expose digital I/O.",
                    statusCode: StatusCodes.Status501NotImplemented);
            }

            try
            {
                return Results.Ok(await io.GetIoAsync(cancellationToken));
            }
            catch (AgvApiException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (AgvProtocolException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (IOException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (TimeoutException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });

        endpoints.MapPost("/agvs/{agvId}/io/do/{id:int}", async (
            string agvId,
            int id,
            AgvDoWriteRequest request,
            AdapterService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.SetDoAsync(agvId, id, request.Status, cancellationToken));
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { detail = exception.Message });
            }
            catch (DeviceDisabledException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (DeviceControlDisabledException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (NotSupportedException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status501NotImplemented);
            }
            catch (ControlUnavailableException exception)
            {
                return Results.Conflict(new { detail = exception.Message });
            }
            catch (ReadOnlyPreflightModeException)
            {
                return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
            }
            catch (AgvApiException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (AgvProtocolException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (IOException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (TimeoutException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (InvalidOperationException exception)
            {
                return Results.UnprocessableEntity(new { detail = exception.Message });
            }
        });

        endpoints.MapPost("/agv/control/release", async (
            AdapterService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return await service.ReleaseControlAsync(cancellationToken)
                    ? Results.Ok(new { released = true })
                    : Results.Conflict(new { detail = "AGV control is not owned by the Adapter." });
            }
            catch (ControlReleaseUnconfirmedException exception)
            {
                return Results.Conflict(new { detail = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.UnprocessableEntity(new { detail = exception.Message });
            }
        });

        endpoints.MapGet("/physical/preflight", async (
            PhysicalAcceptancePreflightService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(cancellationToken)));

        endpoints.MapGet("/agvs", async (
            AdapterService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetFleetAsync(cancellationToken)));

        endpoints.MapPost("/agvs/{agvId}/command", async (
            string agvId,
            AgvCommandRequest request,
            AdapterService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.ExecuteCommandAsync(
                    agvId,
                    request.Command,
                    request.TaskId,
                    cancellationToken));
            }
            catch (DispatchDisabledException exception) { return Results.Conflict(new { detail = exception.Message }); }
            catch (ControlUnavailableException exception) { return Results.Conflict(new { detail = exception.Message }); }
            catch (AgvUnavailableException exception) { return Results.Conflict(new { detail = exception.Message }); }
            catch (KeyNotFoundException exception) { return Results.NotFound(new { detail = exception.Message }); }
            catch (InvalidOperationException exception) { return Results.UnprocessableEntity(new { detail = exception.Message }); }
        });
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

    private static async Task AddColumnIfMissingAsync(
        AdapterDbContext database,
        string columnName,
        CancellationToken cancellationToken)
    {
        try
        {
            if (columnName == "AgvId")
            {
                await database.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE Tasks ADD COLUMN AgvId TEXT NOT NULL DEFAULT 'AGV-01'",
                    cancellationToken);
            }
            else if (columnName == "PathJson")
            {
                await database.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE Tasks ADD COLUMN PathJson TEXT NULL",
                    cancellationToken);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(columnName));
            }
        }
        catch (SqliteException exception) when (
            exception.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }
}

public sealed record AdapterAgvDispatchRequest(
    string TargetStationId,
    string? SourceStationId = null,
    string? AgvId = null,
    IReadOnlyList<string>? Path = null);
