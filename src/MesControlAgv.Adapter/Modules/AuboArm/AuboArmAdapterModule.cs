using System.Net.WebSockets;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MesControlAgv.Adapter.Modules.AuboArm;

/// <summary>
/// AUBO arm adapter module. Reads are exposed when the device is enabled; program
/// mutation routes additionally require ControlEnabled=true. The historical
/// named-variable handshake remains separately quarantined behind
/// EnableLegacyHandshake.
/// </summary>
public sealed class AuboArmAdapterModule : IDeviceAdapterModule
{
    private bool _legacyHandshakeEnabled;
    public const string ModuleId = "aubo-arm";
    public const string DriverId = "aubo-websocket-jsonrpc";
    public const string SimulatorDriverId = "aubo-loopback-simulator";

    public DeviceAdapterModuleDescriptor Descriptor { get; } = new(
        ModuleId,
        "robot-arm",
        [DeviceTransportKind.Tcp]);

    public IReadOnlyList<DeviceAdapterRegistration> GetDevices(DeviceAdapterModuleContext context)
    {
        var options = AuboArmOptions.BindAndValidate(context.Configuration);
        _legacyHandshakeEnabled = options.EnableLegacyHandshake;
        return
        [
            new DeviceAdapterRegistration(
                options.DeviceId,
                Descriptor.DeviceType,
                Descriptor.ModuleId,
                options.IsSimulator ? SimulatorDriverId : DriverId,
                DeviceTransportKind.Tcp,
                options.Enabled,
                options.ControlEnabled)
        ];
    }

    public void AddServices(IServiceCollection services, DeviceAdapterModuleContext context)
    {
        var options = AuboArmOptions.BindAndValidate(context.Configuration);
        _legacyHandshakeEnabled = options.EnableLegacyHandshake;
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        if (options.IsSimulator)
        {
            // Explicitly in-process and loopback only. This branch has no socket
            // client, so a simulator run cannot accidentally reach the field arm.
            services.AddSingleton<AuboArmLoopbackController>(_ => new AuboArmLoopbackController(options)
            {
                RuntimeState = 6,
                AutoCompletePrograms = true,
                ProgramRunDuration = TimeSpan.FromSeconds(1)
            });
            services.AddSingleton<IAuboArmReadOnlyRpcTransport>(serviceProvider =>
                serviceProvider.GetRequiredService<AuboArmLoopbackController>());
        }
        else
        {
            // Constructing the client opens no socket; the first read connects lazily.
            // The field-verified control plane is WebSocket JSON-RPC on port 9012.
            services.AddSingleton<AuboArmWebSocketClient>(_ => new AuboArmWebSocketClient(options));
            services.AddSingleton<IAuboArmReadOnlyRpcTransport>(serviceProvider =>
                serviceProvider.GetRequiredService<AuboArmWebSocketClient>());
            services.AddSingleton<AuboArmDashboardClient>();
            services.AddSingleton<IAuboArmLoadedProgramReader>(serviceProvider =>
                serviceProvider.GetRequiredService<AuboArmDashboardClient>());
        }
        services.AddScoped<AuboArmReadOnlyDriver>(serviceProvider =>
            new AuboArmReadOnlyDriver(
                serviceProvider.GetRequiredService<IAuboArmReadOnlyRpcTransport>(),
                options,
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetService<IAuboArmLoadedProgramReader>()));
        services.AddScoped<IAuboArmDriver>(serviceProvider =>
            serviceProvider.GetRequiredService<AuboArmReadOnlyDriver>());

        // The program projection is also needed by the read-only GET route.  It
        // receives the read-only transport; the HTTP policy and the driver's own
        // ControlEnabled check gate all mutations.
        services.AddScoped<AuboArmProgramDriver>(serviceProvider =>
            new AuboArmProgramDriver(
                serviceProvider.GetRequiredService<IAuboArmReadOnlyRpcTransport>(),
                serviceProvider.GetRequiredService<AuboArmReadOnlyDriver>(),
                options,
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetService<IAuboArmLoadedProgramReader>()));
        services.AddScoped<IAuboArmProgramDriver>(serviceProvider =>
            serviceProvider.GetRequiredService<AuboArmProgramDriver>());
        services.AddScoped<IAuboArmProgramController>(serviceProvider =>
            serviceProvider.GetRequiredService<AuboArmProgramDriver>());

        if (options.ControlEnabled && options.EnableLegacyHandshake)
        {
            services.AddSingleton<IAuboArmControlledRpcTransport>(serviceProvider =>
                options.IsSimulator
                    ? serviceProvider.GetRequiredService<AuboArmLoopbackController>()
                    : serviceProvider.GetRequiredService<AuboArmWebSocketClient>());
            services.AddScoped<IAuboArmHandshakeWriter>(serviceProvider =>
                new AuboArmControlledHandshakeSession(
                    serviceProvider.GetRequiredService<IAuboArmControlledRpcTransport>(),
                    (AuboArmReadOnlyDriver)serviceProvider.GetRequiredService<IAuboArmDriver>(),
                    options,
                    serviceProvider.GetRequiredService<TimeProvider>(),
                    options.AllowedCommandCodes));
        }
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/robot-arms/{deviceId}/status", async (
            string deviceId,
            IAuboArmDriver driver,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureArm(policy.EnsureReadEnabled(deviceId));
                return await driver.GetStatusAsync(deviceId, cancellationToken);
            }));

        endpoints.MapGet("/api/robot-arms/{deviceId}/readiness", async (
            string deviceId,
            IAuboArmDriver driver,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureArm(policy.EnsureReadEnabled(deviceId));
                return await driver.GetReadinessAsync(deviceId, cancellationToken);
            }));

        if (_legacyHandshakeEnabled)
        {
        endpoints.MapGet("/api/robot-arms/{deviceId}/handshake", async (
            string deviceId,
            IAuboArmDriver driver,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureArm(policy.EnsureReadEnabled(deviceId));
                return await driver.GetHandshakeSnapshotAsync(deviceId, cancellationToken);
            }));

        endpoints.MapGet("/api/robot-arms/{deviceId}/variables/{key}", async (
            string deviceId,
            string key,
            IAuboArmDriver driver,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureArm(policy.EnsureReadEnabled(deviceId));
                return await driver.GetVariableAsync(deviceId, key, cancellationToken);
            }));

        endpoints.MapPost("/api/robot-arms/{deviceId}/handshake/dispatch", async (
            string deviceId,
            AuboArmHandshakeDispatchRequest request,
            DeviceOperationPolicy policy,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureArm(policy.EnsureControlEnabled(deviceId));
                var writer = serviceProvider.GetService<IAuboArmHandshakeWriter>()
                    ?? throw new DeviceControlDisabledException(deviceId);
                var operationId = request.OperationId.GetValueOrDefault(Guid.NewGuid());
                return await writer.DispatchAsync(
                    deviceId,
                    operationId,
                    request.CommandCode,
                    cancellationToken);
              }));
        }

        endpoints.MapGet("/api/robot-arms/{deviceId}/program", async (
            string deviceId,
            IAuboArmProgramController controller,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureArm(policy.EnsureReadEnabled(deviceId));
                return await controller.GetProgramAsync(deviceId, cancellationToken);
            }));

        endpoints.MapGet("/api/robot-arms/{deviceId}/programs", async (
            string deviceId,
            IAuboArmProgramController controller,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureArm(policy.EnsureReadEnabled(deviceId));
                return await controller.GetProgramCatalogAsync(deviceId, cancellationToken);
            }));

        endpoints.MapPost("/api/robot-arms/{deviceId}/program/load", async (
            string deviceId,
            AuboArmProgramRequest request,
            IAuboArmProgramController controller,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureArm(policy.EnsureControlEnabled(deviceId));
                var operationId = request.OperationId.GetValueOrDefault(Guid.NewGuid());
                return await controller.LoadProgramAsync(
                    deviceId,
                    request.EffectiveProgramName,
                    request.EffectiveOperatorName,
                    operationId,
                    cancellationToken);
            }));

        endpoints.MapPost("/api/robot-arms/{deviceId}/program/run", async (
            string deviceId,
            AuboArmProgramRunRequest request,
            IAuboArmProgramController controller,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureArm(policy.EnsureControlEnabled(deviceId));
                var operationId = request.OperationId.GetValueOrDefault(Guid.NewGuid());
                return await controller.RunProgramAsync(
                    deviceId,
                    request.EffectiveProgramName,
                    request.EffectiveOperatorName,
                    operationId,
                    cancellationToken);
            }));

        endpoints.MapPost("/api/robot-arms/{deviceId}/program/stop", async (
            string deviceId,
            AuboArmProgramStopRequest request,
            IAuboArmProgramController controller,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureArm(policy.EnsureControlEnabled(deviceId));
                var operationId = request.OperationId.GetValueOrDefault(Guid.NewGuid());
                return await controller.StopProgramAsync(
                    deviceId,
                    request.EffectiveOperatorName,
                    operationId,
                    cancellationToken);
            }));
    }

    private static void EnsureArm(DeviceAdapterRegistration device)
    {
        if (!string.Equals(device.ModuleId, ModuleId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Adapter device '{device.DeviceId}' is not an AUBO arm.");
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (DeviceDisabledException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (DeviceControlDisabledException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new { detail = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { detail = exception.Message });
        }
        catch (AuboArmProtocolException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (AuboArmRpcException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (IOException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (WebSocketException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TimeoutException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (AuboArmOutcomeUnknownException exception)
        {
            return Results.Conflict(new
            {
                detail = exception.Message,
                state = AuboArmHandshakeState.Unknown,
                programState = AuboArmProgramOperationState.Unknown,
                mayHaveWritten = exception.MayHaveWritten
            });
        }
        catch (InvalidOperationException exception)
        {
            return Results.UnprocessableEntity(new { detail = exception.Message });
        }
    }
}
