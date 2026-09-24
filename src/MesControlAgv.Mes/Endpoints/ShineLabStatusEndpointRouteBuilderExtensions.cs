using MesControlAgv.Mes.Services;
using MesControlAgv.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using MesControlAgv.Mes.Data;

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
                options.EnableTrayCommands,
                payloadProfile = options.PayloadProfile.ToString(),
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

        endpoints.MapGet("/api/shinelab/devices/{equipmentCode}/push-evidence", async (
            string equipmentCode,
            int? limit,
            string? taskUuid,
            string? method,
            ShineLabTaskRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(await repository.GetPushEvidenceAsync(equipmentCode, limit ?? 100, cancellationToken, taskUuid, method)));

        endpoints.MapGet("/api/shinelab/tasks/{taskUuid}/command-audit", async (
            string taskUuid, MesDbContext db, CancellationToken cancellationToken) =>
        {
            if (!await db.ShineLabTasks.AnyAsync(t => t.TaskUuid == taskUuid, cancellationToken)) return Results.NotFound();
            return Results.Ok(new
            {
                attempts = await db.ShineLabCommandAttempts.AsNoTracking().Where(a => a.TaskUuid == taskUuid)
                    .OrderByDescending(a => a.PreparedAtUtcTicks).Take(100).ToListAsync(cancellationToken),
                replies = await db.ShineLabCommandReplies.AsNoTracking().Where(r => r.TaskUuid == taskUuid)
                    .OrderByDescending(r => r.ReceivedAtUtcTicks).Take(500).ToListAsync(cancellationToken)
            });
        });
        endpoints.MapGet("/api/shinelab/devices/{equipmentCode}/command-replies", async (
            string equipmentCode, int? limit, MesDbContext db, CancellationToken cancellationToken) =>
            Results.Ok(await db.ShineLabCommandReplies.AsNoTracking().Where(r => r.EquipmentCode == equipmentCode)
                .OrderByDescending(r => r.ReceivedAtUtcTicks).Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync(cancellationToken)));

        endpoints.MapGet("/api/shinelab/devices/{equipmentCode}/command-attempts", async (
            string equipmentCode, int? limit, MesDbContext db, CancellationToken cancellationToken) =>
            Results.Ok(await db.ShineLabCommandAttempts.AsNoTracking().Where(a => a.EquipmentCode == equipmentCode)
                .OrderByDescending(a => a.PreparedAtUtcTicks).Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync(cancellationToken)));

        // Offline previews use the same configured builders as the send path.
        // They do not register tasks or access the live connection manager.
        endpoints.MapPost("/api/shinelab/preview/config", (
            ShineLabConfigRequest request,
            IOptions<ShineLabTcpOptions> options) =>
        {
            try
            {
                return Results.Ok(new
                {
                    payloadProfile = options.Value.PayloadProfile.ToString(),
                    strMethod = "Config",
                    body = ShineLabConfigPayloadBuilder.Build(request, options.Value.PayloadProfile)
                });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
        });

        endpoints.MapPost("/api/shinelab/preview/command", (
            ShineLabCommandRequest request,
            IOptions<ShineLabTcpOptions> options) =>
        {
            try
            {
                return Results.Ok(new
                {
                    payloadProfile = options.Value.PayloadProfile.ToString(),
                    strMethod = "Command",
                    body = ShineLabCommandPayloadBuilder.Build(request, options.Value.PayloadProfile)
                });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
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

        endpoints.MapGet("/api/shinelab/tasks/{taskUuid}/config-preview", async (
            string taskUuid,
            ShineLabTaskRepository repository,
            IOptions<ShineLabTcpOptions> options,
            CancellationToken cancellationToken) =>
        {
            var task = await repository.GetAsync(taskUuid, cancellationToken);
            if (task is null) return Results.NotFound();
            var config = System.Text.Json.JsonSerializer.Deserialize<ShineLabConfigRequest>(task.ConfigJson)!;
            return Results.Ok(new
            {
                task.TaskUuid, task.EquipmentCode, payloadProfile = options.Value.PayloadProfile.ToString(),
                strMethod = "Config", body = ShineLabConfigPayloadBuilder.Build(config, options.Value.PayloadProfile)
            });
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
