using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Http.Features;

namespace MesControlAgv.Mes.Endpoints;

/// <summary>Standalone gateway module: no workflow scheduling or vendor URLs.</summary>
public static class SampleWorkstationEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapSampleWorkstationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/workstations/{deviceId}/capabilities", async (
            string deviceId, ISampleWorkstationCapabilityReader reader, CancellationToken cancellationToken) =>
            await ExecuteAsync(() => reader.GetCapabilitiesAsync(deviceId, cancellationToken), cancellationToken));

        endpoints.MapGet("/api/workstations/{deviceId}/status", async (
            string deviceId,
            ISampleWorkstationReader reader,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => reader.GetStatusAsync(deviceId, cancellationToken),
                cancellationToken));

        endpoints.MapGet("/api/workstations/{deviceId}/errors", async (
            string deviceId,
            ISampleWorkstationReader reader,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => reader.GetErrorsAsync(deviceId, cancellationToken),
                cancellationToken));

        endpoints.MapPost("/api/workstations/{deviceId}/initialize", async (
            string deviceId,
            ISampleWorkstationCommands controller,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => controller.InitializeAsync(deviceId, cancellationToken),
                cancellationToken, isCommand: true));

        endpoints.MapPost("/api/workstations/{deviceId}/tasks/{taskNo}/start", async (
            string deviceId,
            string taskNo,
            SampleWorkstationTaskBarcodes? barcodes,
            HttpRequest request,
            ISampleWorkstationCommands controller,
            ISampleWorkstationBarcodeCommands barcodeController,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () =>
                {
                    taskNo = SampleWorkstationTaskRoute.ReadCommandTaskNo(request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget, taskNo);
                    return barcodes is null
                        ? controller.StartTaskAsync(deviceId, taskNo, cancellationToken)
                        : barcodeController.StartTaskAsync(deviceId, taskNo, barcodes, cancellationToken);
                },
                cancellationToken, isCommand: true));

        endpoints.MapPost("/api/workstations/{deviceId}/tasks/{taskNo}/barcodes", async (
            string deviceId, string taskNo, SampleWorkstationTaskBarcodes barcodes,
            HttpRequest request,
            ISampleWorkstationBarcodeCommands controller, CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () =>
                {
                    taskNo = SampleWorkstationTaskRoute.ReadCommandTaskNo(request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget, taskNo);
                    return controller.UpdateTaskBarcodesAsync(deviceId, taskNo, barcodes, cancellationToken);
                },
                cancellationToken, isCommand: true));

        endpoints.MapGet("/api/workstations/{deviceId}/tasks", async (
            string deviceId,
            string? state,
            string? startDate,
            string? endDate,
            int? startNo,
            int? recordNum,
            ISampleWorkstationReader reader,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => reader.GetTasksAsync(
                    deviceId,
                    new SampleWorkstationTaskQuery(
                        state,
                        startDate,
                        endDate,
                        startNo ?? 1,
                        recordNum ?? 50),
                    cancellationToken),
                cancellationToken));

        endpoints.MapGet("/api/workstations/{deviceId}/tasks/{taskNo}", async (
            string deviceId,
            string taskNo,
            ISampleWorkstationReader reader,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => reader.GetTaskDetailsAsync(deviceId, taskNo, cancellationToken),
                cancellationToken));

        endpoints.MapGet("/api/workstations/{deviceId}/tasks/{taskNo}/state", async (
            string deviceId,
            string taskNo,
            ISampleWorkstationReader reader,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => reader.GetTaskStateAsync(deviceId, taskNo, cancellationToken),
                cancellationToken));

        endpoints.MapGet("/api/workstations/{deviceId}/protocol/{operation}", async (
            string deviceId,
            string operation,
            string? key,
            string? startDate,
            string? endDate,
            int? startNo,
            int? recordNum,
            ISampleWorkstationReader reader,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () =>
                {
                    if (!Enum.TryParse<SampleWorkstationProtocolOperation>(operation, true, out var parsed)
                        || !Enum.IsDefined(parsed))
                    {
                        throw new ArgumentException($"Unsupported sample workstation operation '{operation}'.");
                    }

                    return reader.GetProtocolReadAsync(
                        deviceId,
                        parsed,
                        new SampleWorkstationProtocolReadQuery(
                            key,
                            startDate,
                            endDate,
                            startNo ?? 1,
                            recordNum ?? 50),
                        cancellationToken);
                },
                cancellationToken));

        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync<T>(
        Func<Task<T>> operation, CancellationToken cancellationToken, bool isCommand = false)
    {
        try
        {
            return Results.Ok(await operation());
        }
        catch (SampleWorkstationGatewayException exception)
        {
            return Problem(exception.StatusCode, exception.ErrorCode, exception.Message,
                exception.OutcomeUnknown, exception.VendorCode, exception.VendorData);
        }
        catch (ArgumentException exception)
        {
            return Problem(400, SampleWorkstationErrorCodes.InvalidRequest, exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Problem(504, SampleWorkstationErrorCodes.Timeout,
                "Workstation Adapter request timed out; query task state before retrying a command.", isCommand);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return Problem(503, SampleWorkstationErrorCodes.Unavailable,
                "The sample workstation Adapter is unavailable or returned an invalid payload.", isCommand);
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
