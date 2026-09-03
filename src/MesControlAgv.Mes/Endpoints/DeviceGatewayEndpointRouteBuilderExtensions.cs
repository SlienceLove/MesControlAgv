using System.Net;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Routing;

namespace MesControlAgv.Mes.Endpoints;

/// <summary>
/// Registers device-gateway HTTP endpoints. The module only adapts transport
/// errors to HTTP results; all device operations remain behind application ports.
/// </summary>
public static class DeviceGatewayEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapMesDeviceGatewayEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/instruments/{instrumentId}/status", async (
            string instrumentId,
            IIonChromatographyStatusReader reader,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var status = await reader.GetStatusAsync(instrumentId, cancellationToken);
                return Results.Ok(new IonChromatographyControlCenterStatusResponse(
                    new IonChromatographyStatusResponse(
                        status.InstrumentId,
                        status.Model,
                        status.SerialNumber,
                        status.Online,
                        status.DeviceState,
                        status.PortOwned,
                        status.ObservedAtUtc,
                        status.Pressure,
                        status.ColumnTemperature,
                        status.DetectorTemperature,
                        status.Alarm,
                        status.Conductivity,
                        status.TotalConductivity,
                        status.Flow,
                        status.MappingConfidence,
                        status.FlowSetpoint,
                        status.ColumnTemperatureSetpoint,
                        status.TemperatureControlStateRaw,
                        status.PumpStateRaw,
                        status.PressureRaw,
                        status.SuppressorEluentStateRaw,
                        status.FaultCode1Raw,
                        status.FaultCode2Raw),
                    IonChromatographyReadOnlyPolicy.TaskAdmissionEnabled,
                    IonChromatographyReadOnlyPolicy.EnabledOperations.Select(operation => operation.ToString()).ToArray(),
                    "ReadOnlyCaptureCorrelated"));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is
                HttpRequestException or
                TaskCanceledException or
                InvalidOperationException)
            {
                return Results.Problem(
                    "The read-only instrument status gateway is unavailable.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        endpoints.MapGet("/api/workstations/{deviceId}/status", async (
            string deviceId,
            ISampleWorkstationReader reader,
            CancellationToken cancellationToken) =>
            await ExecuteWorkstationReadAsync(
                () => reader.GetStatusAsync(deviceId, cancellationToken),
                cancellationToken));

        endpoints.MapGet("/api/workstations/{deviceId}/errors", async (
            string deviceId,
            ISampleWorkstationReader reader,
            CancellationToken cancellationToken) =>
            await ExecuteWorkstationReadAsync(
                () => reader.GetErrorsAsync(deviceId, cancellationToken),
                cancellationToken));

        endpoints.MapGet("/api/workstations/{deviceId}/tasks", async (
            string deviceId,
            string? state,
            string? startDate,
            string? endDate,
            int? startNo,
            int? recordNum,
            ISampleWorkstationReader reader,
            CancellationToken cancellationToken) =>
            await ExecuteWorkstationReadAsync(
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
            await ExecuteWorkstationReadAsync(
                () => reader.GetTaskDetailsAsync(deviceId, taskNo, cancellationToken),
                cancellationToken));

        endpoints.MapGet("/api/workstations/{deviceId}/tasks/{taskNo}/state", async (
            string deviceId,
            string taskNo,
            ISampleWorkstationReader reader,
            CancellationToken cancellationToken) =>
            await ExecuteWorkstationReadAsync(
                () => reader.GetTaskStateAsync(deviceId, taskNo, cancellationToken),
                cancellationToken));

        endpoints.MapGet("/api/agv", async (
            IAgvGateway adapter,
            CancellationToken cancellationToken) =>
            await ExecuteAgvReadAsync(
                () => adapter.GetSnapshotAsync(cancellationToken),
                "AGV snapshot is unavailable."));

        endpoints.MapGet("/api/physical/preflight", async (
            IAgvGateway adapter,
            CancellationToken cancellationToken) =>
        {
            if (adapter is not IPhysicalPreflightAgvGateway physical)
            {
                return Results.NotFound(new { detail = "The configured AGV gateway does not support physical preflight." });
            }

            return await ExecuteAgvReadAsync(
                () => physical.GetPhysicalPreflightAsync(cancellationToken),
                "AGV physical preflight is unavailable.");
        });

        endpoints.MapGet("/api/agvs/fleet", async (
            IAgvGateway adapter,
            CancellationToken cancellationToken) =>
        {
            if (adapter is IFleetAwareAgvGateway fleet)
            {
                return await ExecuteAgvReadAsync(
                    () => fleet.GetFleetSnapshotAsync(cancellationToken),
                    "AGV fleet status is unavailable.");
            }

            return await ExecuteAgvReadAsync(
                async () => new[] { await adapter.GetSnapshotAsync(cancellationToken) },
                "AGV fleet status is unavailable.");
        });

        endpoints.MapGet("/api/agvs/fleet/status", async (
            ITaskApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetFleetStatusAsync(cancellationToken)));

        endpoints.MapPost("/api/agvs/{agvId}/command", async (
            string agvId,
            AgvCommandRequest request,
            IAgvGateway adapter,
            ITaskApplicationService tasks,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await adapter.ExecuteAgvCommandAsync(
                    agvId,
                    request.Command,
                    request.TaskId,
                    cancellationToken);
                if (result is not null &&
                    request.TaskId is { } operationId &&
                    request.Command.Trim().ToLowerInvariant() is "pause" or "resume" or "continue")
                {
                    await tasks.RecordAgvCommandAsync(operationId, request.Command, result, cancellationToken);
                }

                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (AdapterHttpException exception)
            {
                return Results.Json(
                    new { detail = exception.Detail ?? exception.Message },
                    statusCode: (int?)exception.ResponseStatusCode);
            }
        });

        endpoints.MapGet("/api/agvs/{agvId}/io", async (
            string agvId,
            IAgvIoGateway io,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await io.GetIoAsync(agvId, cancellationToken));
            }
            catch (AdapterHttpException exception)
            {
                return Results.Json(
                    new { detail = exception.Detail ?? exception.Message },
                    statusCode: (int)exception.ResponseStatusCode);
            }
        });

        endpoints.MapPost("/api/agvs/{agvId}/io/do/{id:int}", async (
            string agvId,
            int id,
            AgvDoWriteRequest request,
            IAgvIoGateway io,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await io.SetDoAsync(agvId, id, request.Status, cancellationToken));
            }
            catch (AdapterHttpException exception)
            {
                return Results.Json(
                    new { detail = exception.Detail ?? exception.Message },
                    statusCode: (int)exception.ResponseStatusCode);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
        });

        endpoints.MapGet("/api/robot-arms/{deviceId}/status", async (
            string deviceId,
            IAuboArmGateway arm,
            CancellationToken cancellationToken) =>
            await ExecuteArmReadAsync(
                () => arm.GetStatusAsync(deviceId, cancellationToken)));

        endpoints.MapGet("/api/robot-arms/{deviceId}/readiness", async (
            string deviceId,
            IAuboArmGateway arm,
            CancellationToken cancellationToken) =>
            await ExecuteArmReadAsync(
                () => arm.GetReadinessAsync(deviceId, cancellationToken)));

        endpoints.MapGet("/api/robot-arms/{deviceId}/handshake", async (
            string deviceId,
            IAuboArmGateway arm,
            CancellationToken cancellationToken) =>
            await ExecuteArmReadAsync(
                () => arm.GetHandshakeSnapshotAsync(deviceId, cancellationToken)));

        endpoints.MapPost("/api/robot-arms/{deviceId}/handshake/dispatch", async (
            string deviceId,
            AuboArmHandshakeDispatchRequest request,
            IAuboArmGateway arm,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var operationId = request.OperationId.GetValueOrDefault(Guid.NewGuid());
                return Results.Ok(await arm.DispatchAsync(
                    deviceId,
                    operationId,
                    request.CommandCode,
                    cancellationToken));
            }
            catch (AdapterHttpException exception)
            {
                return Results.Json(
                    new { detail = exception.Detail ?? exception.Message },
                    statusCode: (int)exception.ResponseStatusCode);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
        });

        endpoints.MapGet("/api/robot-arms/{deviceId}/program", async (
            string deviceId,
            IAuboArmGateway arm,
            CancellationToken cancellationToken) =>
            await ExecuteArmReadAsync(
                () => arm.GetProgramAsync(deviceId, cancellationToken)));

        endpoints.MapGet("/api/robot-arms/{deviceId}/programs", async (
            string deviceId,
            IAuboArmGateway arm,
            CancellationToken cancellationToken) =>
            await ExecuteArmReadAsync(
                () => arm.GetProgramCatalogAsync(deviceId, cancellationToken)));

        endpoints.MapPost("/api/robot-arms/{deviceId}/program/load", async (
            string deviceId,
            AuboArmProgramRequest request,
            IAuboArmGateway arm,
            CancellationToken cancellationToken) =>
            await ExecuteArmProgramWriteAsync(
                () => RequireProgramRequest(request.EffectiveProgramName, request.EffectiveOperatorName)
                    ? arm.LoadProgramAsync(
                    deviceId,
                    request.EffectiveProgramName,
                    request.EffectiveOperatorName,
                    request.OperationId.GetValueOrDefault(Guid.NewGuid()),
                    cancellationToken)
                    : throw new ArgumentException("ProgramName and OperatorName are required.")));

        endpoints.MapPost("/api/robot-arms/{deviceId}/program/run", async (
            string deviceId,
            AuboArmProgramRunRequest request,
            IAuboArmGateway arm,
            CancellationToken cancellationToken) =>
            await ExecuteArmProgramWriteAsync(
                () => RequireProgramRequest(request.EffectiveProgramName, request.EffectiveOperatorName)
                    ? arm.RunProgramAsync(
                    deviceId,
                    request.EffectiveProgramName,
                    request.EffectiveOperatorName,
                    request.OperationId.GetValueOrDefault(Guid.NewGuid()),
                    cancellationToken)
                    : throw new ArgumentException("OperatorName is required.")));

        endpoints.MapPost("/api/robot-arms/{deviceId}/program/stop", async (
            string deviceId,
            AuboArmProgramStopRequest request,
            IAuboArmGateway arm,
            CancellationToken cancellationToken) =>
            await ExecuteArmProgramWriteAsync(
                () => RequireProgramRequest("ok", request.EffectiveOperatorName)
                    ? arm.StopProgramAsync(
                    deviceId,
                    request.EffectiveOperatorName,
                    request.OperationId.GetValueOrDefault(Guid.NewGuid()),
                    cancellationToken)
                    : throw new ArgumentException("OperatorName is required.")));

        endpoints.MapPost("/api/agv-aubo-sequences", async (
            AgvAuboSequenceRequest request,
            IAgvAuboSequenceService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await service.StartAsync(request, cancellationToken);
                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { detail = exception.Message });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        });

        endpoints.MapGet("/api/agv-aubo-sequences/{sequenceId:guid}", async (
            Guid sequenceId,
            IAgvAuboSequenceService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.GetAsync(sequenceId, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        return endpoints;
    }

    private static async Task<IResult> ExecuteAgvReadAsync<T>(
        Func<Task<T>> operation,
        string unavailableMessage)
    {
        try
        {
            return Results.Ok(await operation());
        }
        catch (AdapterHttpException exception)
        {
            return Results.Json(
                new { detail = exception.Detail ?? exception.Message },
                statusCode: (int)exception.ResponseStatusCode);
        }
        catch (TimeoutException exception)
        {
            return Results.Problem(
                $"{unavailableMessage} {exception.Message}",
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (HttpRequestException exception)
        {
            return Results.Problem(
                $"{unavailableMessage} {exception.Message}",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException exception)
        {
            return Results.Problem(
                $"{unavailableMessage} {exception.Message}",
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException exception)
        {
            return Results.UnprocessableEntity(new { detail = exception.Message });
        }
    }

    private static async Task<IResult> ExecuteArmReadAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return Results.Ok(await operation());
        }
        catch (AdapterHttpException exception)
        {
            return Results.Json(
                new { detail = exception.Detail ?? exception.Message },
                statusCode: (int)exception.ResponseStatusCode);
        }
        catch (HttpRequestException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (InvalidOperationException exception)
        {
            return Results.UnprocessableEntity(new { detail = exception.Message });
        }
    }

    private static async Task<IResult> ExecuteArmProgramWriteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return Results.Ok(await operation());
        }
        catch (AdapterHttpException exception)
        {
            return Results.Json(
                new { detail = exception.Detail ?? exception.Message },
                statusCode: (int)exception.ResponseStatusCode);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { detail = exception.Message });
        }
        catch (NotSupportedException)
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        }
        catch (InvalidOperationException exception)
        {
            return Results.UnprocessableEntity(new { detail = exception.Message });
        }
        catch (TimeoutException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }

    private static bool RequireProgramRequest(string? programName, string? operatorName) =>
        !string.IsNullOrWhiteSpace(programName) && !string.IsNullOrWhiteSpace(operatorName);

    private static async Task<IResult> ExecuteWorkstationReadAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await operation());
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { detail = exception.Message });
        }
        catch (AdapterHttpException exception) when (exception.ResponseStatusCode == HttpStatusCode.NotFound)
        {
            return Results.NotFound(new { detail = exception.Detail });
        }
        catch (AdapterHttpException exception) when (exception.ResponseStatusCode == HttpStatusCode.BadRequest)
        {
            return Results.BadRequest(new { detail = exception.Detail });
        }
        catch (AdapterHttpException exception) when (
            exception.ResponseStatusCode is
                HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or
                HttpStatusCode.GatewayTimeout)
        {
            return Results.Problem(
                exception.Detail ?? "The sample workstation Adapter is unavailable.",
                statusCode: (int)exception.ResponseStatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return Results.Problem(
                "The sample workstation Adapter is unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
