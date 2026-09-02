using MesControlAgv.Mes.Services;
using MesControlAgv.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Endpoints;

public static class ShineLabStatusEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapShineLabStatusEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/shinelab/server/status", (
            ShineLabStatusHub hub,
            IOptions<ShineLabTcpOptions> configuredOptions) =>
        {
            var options = configuredOptions.Value;
            var devices = hub.GetStatuses();
            return Results.Ok(new
            {
                enabled = options.Enabled,
                role = "server",
                options.ListenAddress,
                options.Port,
                options.StaleAfterSeconds,
                options.CommandTimeoutMs,
                knownDeviceCount = devices.Count,
                onlineDeviceCount = devices.Count(item => item.Online)
            });
        });

        endpoints.MapGet("/api/shinelab/devices/status", (ShineLabStatusHub hub) =>
            Results.Ok(hub.GetStatuses()));

        endpoints.MapGet("/api/shinelab/devices/{equipmentCode}/status", (
            string equipmentCode,
            ShineLabStatusHub hub) =>
        {
            var status = hub.GetStatus(equipmentCode);
            return status is null ? Results.NotFound() : Results.Ok(status);
        });

        endpoints.MapPost("/api/shinelab/devices/{equipmentCode}/config", async (
            string equipmentCode,
            ShineLabConfigRequest request,
            ShineLabCommandService commands,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await commands.SendConfigAsync(equipmentCode, request, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        endpoints.MapPost("/api/shinelab/devices/{equipmentCode}/command", async (
            string equipmentCode,
            ShineLabCommandRequest request,
            ShineLabCommandService commands,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await commands.SendCommandAsync(equipmentCode, request, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        endpoints.MapPost("/api/shinelab/tasks", async (
            ShineLabTaskCreateRequest request,
            ShineLabTaskService tasks,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var task = await tasks.CreateAsync(request, cancellationToken);
                return Results.Created($"/api/shinelab/tasks/{Uri.EscapeDataString(task.TaskUuid)}", task);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { detail = exception.Message });
            }
        });

        endpoints.MapGet("/api/shinelab/tasks", async (
            int? limit,
            ShineLabTaskService tasks,
            CancellationToken cancellationToken) =>
            Results.Ok(await tasks.ListAsync(limit ?? 100, cancellationToken)));

        endpoints.MapGet("/api/shinelab/tasks/{taskUuid}", async (
            string taskUuid,
            ShineLabTaskService tasks,
            CancellationToken cancellationToken) =>
        {
            var task = await tasks.GetAsync(taskUuid, cancellationToken);
            return task is null ? Results.NotFound() : Results.Ok(task);
        });

        endpoints.MapPost("/api/shinelab/tasks/{taskUuid}/config", async (
            string taskUuid,
            ShineLabTaskService tasks,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await tasks.ConfigureAsync(taskUuid, cancellationToken));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        endpoints.MapPost("/api/shinelab/tasks/{taskUuid}/command", async (
            string taskUuid,
            ShineLabCommandRequest request,
            ShineLabTaskService tasks,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await tasks.CommandAsync(taskUuid, request, cancellationToken));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        return endpoints;
    }
}
