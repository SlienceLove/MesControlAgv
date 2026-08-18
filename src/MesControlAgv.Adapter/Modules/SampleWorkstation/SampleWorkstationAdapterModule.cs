using MesControlAgv.Application;
using MesControlAgv.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MesControlAgv.Adapter.Modules.SampleWorkstation;

public sealed class SampleWorkstationAdapterModule : IDeviceAdapterModule
{
    public const string ModuleId = "sample-workstation";
    public const string DriverId = "vendor-http-read-only";

    public DeviceAdapterModuleDescriptor Descriptor { get; } = new(
        ModuleId,
        "sample-workstation",
        [DeviceTransportKind.Http]);

    public IReadOnlyList<DeviceAdapterRegistration> GetDevices(DeviceAdapterModuleContext context)
    {
        var options = SampleWorkstationOptions.BindAndValidate(context.Configuration);
        return
        [
            new DeviceAdapterRegistration(
                options.DeviceId,
                Descriptor.DeviceType,
                Descriptor.ModuleId,
                DriverId,
                DeviceTransportKind.Http,
                options.Enabled,
                options.ControlEnabled)
        ];
    }

    public void AddServices(IServiceCollection services, DeviceAdapterModuleContext context)
    {
        var options = SampleWorkstationOptions.BindAndValidate(context.Configuration);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient<VendorSampleWorkstationHttpClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
            client.Timeout = TimeSpan.FromMilliseconds(options.RequestTimeoutMs);
        });
        services.AddScoped<ISampleWorkstationDriver, SampleWorkstationReadOnlyDriver>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/workstations/{deviceId}/status", async (
            string deviceId,
            ISampleWorkstationDriver driver,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureWorkstation(policy.EnsureReadEnabled(deviceId));
                return await driver.GetStatusAsync(deviceId, cancellationToken);
            }));

        endpoints.MapGet("/api/workstations/{deviceId}/errors", async (
            string deviceId,
            ISampleWorkstationDriver driver,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureWorkstation(policy.EnsureReadEnabled(deviceId));
                return await driver.GetErrorsAsync(deviceId, cancellationToken);
            }));

        endpoints.MapGet("/api/workstations/{deviceId}/tasks", async (
            string deviceId,
            string? state,
            string? startDate,
            string? endDate,
            int? startNo,
            int? recordNum,
            ISampleWorkstationDriver driver,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureWorkstation(policy.EnsureReadEnabled(deviceId));
                return await driver.GetTasksAsync(
                    deviceId,
                    new SampleWorkstationTaskQuery(
                        state,
                        startDate,
                        endDate,
                        startNo ?? 1,
                        recordNum ?? 50),
                    cancellationToken);
            }));

        endpoints.MapGet("/api/workstations/{deviceId}/tasks/{taskNo}", async (
            string deviceId,
            string taskNo,
            ISampleWorkstationDriver driver,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureWorkstation(policy.EnsureReadEnabled(deviceId));
                return await driver.GetTaskDetailsAsync(deviceId, taskNo, cancellationToken);
            }));

        endpoints.MapGet("/api/workstations/{deviceId}/tasks/{taskNo}/state", async (
            string deviceId,
            string taskNo,
            ISampleWorkstationDriver driver,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureWorkstation(policy.EnsureReadEnabled(deviceId));
                return await driver.GetTaskStateAsync(deviceId, taskNo, cancellationToken);
            }));
    }

    private static void EnsureWorkstation(DeviceAdapterRegistration device)
    {
        if (!string.Equals(device.ModuleId, ModuleId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Adapter device '{device.DeviceId}' is not a sample workstation.");
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
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new { detail = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { detail = exception.Message });
        }
        catch (SampleWorkstationProtocolException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (HttpRequestException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }
}
