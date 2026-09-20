using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Devices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MesControlAgv.Adapter.Modules.SampleWorkstation;

public sealed class SampleWorkstationAdapterModule : IDeviceAdapterModule
{
    public const string ModuleId = "sample-workstation";
    public const string DriverId = "vendor-http";

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
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<ISampleWorkstationDriver, SampleWorkstationDriver>();
        services.AddScoped<ISampleWorkstationReader>(provider => provider.GetRequiredService<ISampleWorkstationDriver>());
        services.AddSingleton<SampleWorkstationOperationJournal>();
        services.AddScoped<ISampleWorkstationController, SampleWorkstationControlledDriver>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/workstations/{deviceId}/tasks/{taskNo}/template", async (
            string deviceId, string taskNo, HttpRequest request, ISampleWorkstationDriver driver,
            DeviceOperationPolicy policy, CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureWorkstation(policy.EnsureReadEnabled(deviceId));
                taskNo = SampleWorkstationTaskRoute.ReadCommandTaskNo(request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget, taskNo);
                return await driver.GetTaskTemplateAsync(deviceId, taskNo, cancellationToken);
            }));

        endpoints.MapPost("/api/workstations/{deviceId}/tasks/import", async (
            string deviceId, string fileName, HttpRequest request, ISampleWorkstationDriver driver,
            DeviceOperationPolicy policy, CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureWorkstation(policy.EnsureControlEnabled(deviceId));
                if (!string.Equals(request.ContentType?.Split(';')[0].Trim(), "application/octet-stream", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Template import requires application/octet-stream XLSX content.");
                return await driver.ImportTasksAsync(deviceId, fileName, request.Body, cancellationToken);
            }, isCommand: true));

        endpoints.MapGet("/api/workstations/{deviceId}/capabilities", async (
            string deviceId, ISampleWorkstationDriver driver, CancellationToken cancellationToken) =>
            await ExecuteAsync(() => driver.GetCapabilitiesAsync(deviceId, cancellationToken)));

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

        endpoints.MapPost("/api/workstations/{deviceId}/initialize", async (
            string deviceId,
            ISampleWorkstationDriver driver,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureWorkstation(policy.EnsureControlEnabled(deviceId));
                return await driver.InitializeAsync(deviceId, cancellationToken);
            }, isCommand: true));

        endpoints.MapPost("/api/workstations/{deviceId}/tasks/{taskNo}/start", async (
            string deviceId,
            string taskNo,
            SampleWorkstationTaskBarcodes? barcodes,
            HttpRequest request,
            ISampleWorkstationDriver driver,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureWorkstation(policy.EnsureControlEnabled(deviceId));
                taskNo = SampleWorkstationTaskRoute.ReadCommandTaskNo(request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget, taskNo);
                return barcodes is null
                    ? await driver.StartTaskAsync(deviceId, taskNo, cancellationToken)
                    : await driver.StartTaskAsync(deviceId, taskNo, barcodes, cancellationToken);
            }, isCommand: true));

        endpoints.MapPost("/api/workstations/{deviceId}/tasks/{taskNo}/barcodes", async (
            string deviceId, string taskNo, SampleWorkstationTaskBarcodes barcodes,
            HttpRequest request,
            ISampleWorkstationDriver driver, DeviceOperationPolicy policy, CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                EnsureWorkstation(policy.EnsureControlEnabled(deviceId));
                taskNo = SampleWorkstationTaskRoute.ReadCommandTaskNo(request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget, taskNo);
                return await driver.UpdateTaskBarcodesAsync(deviceId, taskNo, barcodes, cancellationToken);
            }, isCommand: true));

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

        endpoints.MapPost("/api/workstations/{deviceId}/legacy/initialize", async (
            string deviceId,
            SampleWorkstationOperationRequest request,
            ISampleWorkstationController controller,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteCommandAsync(async () =>
            {
                EnsureWorkstation(policy.EnsureControlEnabled(deviceId));
                return await controller.InitializeAsync(deviceId, request, cancellationToken);
            }));

        endpoints.MapPost("/api/workstations/{deviceId}/legacy/tasks", async (
            string deviceId,
            SampleWorkstationTaskCreateRequest request,
            ISampleWorkstationController controller,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteCommandAsync(async () =>
            {
                EnsureWorkstation(policy.EnsureControlEnabled(deviceId));
                return await controller.CreateTaskAsync(deviceId, request, cancellationToken);
            }));

        endpoints.MapPost("/api/workstations/{deviceId}/legacy/tasks/{taskNo}/trajectory", async (
            string deviceId,
            string taskNo,
            SampleWorkstationTrajectoryRequest request,
            ISampleWorkstationController controller,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteCommandAsync(async () =>
            {
                EnsureWorkstation(policy.EnsureControlEnabled(deviceId));
                if (!string.Equals(request.TaskNo?.Trim(), taskNo?.Trim(), StringComparison.Ordinal))
                    throw new ArgumentException("Route task number and request TaskNo must match.");
                return await controller.AddTrajectoryAsync(deviceId, request, cancellationToken);
            }));

        endpoints.MapPost("/api/workstations/{deviceId}/legacy/tasks/{taskNo}/start", async (
            string deviceId,
            string taskNo,
            SampleWorkstationStartRequest request,
            ISampleWorkstationController controller,
            DeviceOperationPolicy policy,
            CancellationToken cancellationToken) =>
            await ExecuteCommandAsync(async () =>
            {
                EnsureWorkstation(policy.EnsureControlEnabled(deviceId));
                if (!string.Equals(request.TaskNo?.Trim(), taskNo?.Trim(), StringComparison.Ordinal))
                    throw new ArgumentException("Route task number and request TaskNo must match.");
                return await controller.StartExperimentAsync(deviceId, request, cancellationToken);
            }));

        endpoints.MapGet("/api/workstations/{deviceId}/protocol/{operation}", async (
            string deviceId,
            string operation,
            string? key,
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
                if (!Enum.TryParse<SampleWorkstationProtocolOperation>(operation, true, out var parsed)
                    || !Enum.IsDefined(parsed))
                {
                    throw new ArgumentException($"Unsupported sample workstation operation '{operation}'.");
                }

                return await driver.GetProtocolReadAsync(
                    deviceId,
                    parsed,
                    new SampleWorkstationProtocolReadQuery(
                        key,
                        startDate,
                        endDate,
                        startNo ?? 1,
                        recordNum ?? 50),
                    cancellationToken);
            }));
    }

    private static void EnsureWorkstation(DeviceAdapterRegistration device)
    {
        if (!string.Equals(device.ModuleId, ModuleId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Adapter device '{device.DeviceId}' is not a sample workstation.");
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action, bool isCommand = false)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (DeviceDisabledException exception)
        {
            return Problem(503, SampleWorkstationErrorCodes.Disabled, exception.Message);
        }
        catch (DeviceControlDisabledException exception)
        {
            return Problem(403, SampleWorkstationErrorCodes.ControlDisabled, exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            return Problem(404, SampleWorkstationErrorCodes.NotFound, exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Problem(400, SampleWorkstationErrorCodes.InvalidRequest, exception.Message);
        }
        catch (SampleWorkstationProtocolException exception)
        {
            var explicitlyRejected = exception.ErrorCode == SampleWorkstationErrorCodes.CommandRejected ||
                exception.ErrorCode == SampleWorkstationErrorCodes.CommandUnconfirmed &&
                exception.VendorData is { ValueKind: System.Text.Json.JsonValueKind.String } data &&
                string.Equals(data.GetString()?.Trim(), "启动失败", StringComparison.Ordinal);
            return Problem(exception.ErrorCode == SampleWorkstationErrorCodes.UnsupportedOperation ? 501 : 502,
                exception.ErrorCode,
                exception.Message,
                isCommand && !explicitlyRejected,
                exception.VendorCode,
                exception.VendorData);
        }
        catch (HttpRequestException exception)
        {
            return Problem(exception.StatusCode == System.Net.HttpStatusCode.NotFound ? 502 : 503,
                exception.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? SampleWorkstationErrorCodes.UnsupportedOperation : SampleWorkstationErrorCodes.Unavailable,
                exception.Message, isCommand);
        }
        catch (OperationCanceledException exception)
        {
            return Problem(504, SampleWorkstationErrorCodes.Timeout, exception.Message, isCommand);
        }
        catch (System.Text.Json.JsonException exception)
        {
            return Problem(502, SampleWorkstationErrorCodes.InvalidPayload, exception.Message, isCommand);
        }
    }

    private static async Task<IResult> ExecuteCommandAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (DeviceControlDisabledException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (SampleWorkstationOperationUnknownException exception)
        {
            return Results.Conflict(new
            {
                detail = exception.Message,
                state = DeviceOperationLifecycle.Unknown,
                unknownReason = exception.Reason,
                operation = exception.Operation,
                operationId = exception.Request.OperationId,
                runId = exception.Request.RunId,
                nodeExecutionId = exception.Request.NodeExecutionId,
                vendorTaskId = exception.VendorTaskId
            });
        }
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new { detail = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { detail = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.UnprocessableEntity(new { detail = exception.Message });
        }
        catch (HttpRequestException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
    private static IResult Problem(int status, string code, string detail, bool outcomeUnknown = false,
        int? vendorCode = null, System.Text.Json.JsonElement? vendorData = null) =>
        Results.Problem(detail, statusCode: status, extensions: new Dictionary<string, object?>
        {
            ["errorCode"] = code,
            ["outcomeUnknown"] = outcomeUnknown,
            ["vendorCode"] = vendorCode,
            ["vendorData"] = vendorData
        });
}
