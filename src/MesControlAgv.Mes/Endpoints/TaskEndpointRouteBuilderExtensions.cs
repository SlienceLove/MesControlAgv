using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Routing;

namespace MesControlAgv.Mes.Endpoints;

/// <summary>
/// Registers the task lifecycle HTTP contract without coupling endpoint details to startup.
/// The routes and response semantics intentionally remain identical to the original mappings.
/// </summary>
public static class TaskEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapMesTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/tasks", async (
            CreateTaskRequest request,
            ITaskApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var task = await service.CreateAsync(request, cancellationToken);
                return Results.Created($"/api/tasks/{task.Id}", task);
            }
            catch (UnsupportedRouteException exception)
            {
                return Results.UnprocessableEntity(new { detail = exception.Message });
            }
        });

        endpoints.MapPost("/api/tasks/{taskId:guid}/dispatch", async (
            Guid taskId,
            ITaskApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.DispatchAsync(taskId, cancellationToken));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (PhysicalExecutionAdmissionException exception)
            {
                return Results.Conflict(new { code = exception.Code, detail = exception.Detail });
            }
            catch (InvalidTaskTransitionException exception)
            {
                return Results.Conflict(new { detail = exception.Message });
            }
        });

        endpoints.MapPost("/api/tasks/{taskId:guid}/arrived", async (
            Guid taskId,
            ITaskApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.RecordArrivalAsync(taskId, cancellationToken)));

        endpoints.MapPost("/api/tasks/{taskId:guid}/confirm-pickup", async (
            Guid taskId,
            OperatorActionRequest request,
            ITaskApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.ConfirmPickupAsync(taskId, request.OperatorName, cancellationToken));
            }
            catch (PhysicalExecutionAdmissionException exception)
            {
                return Results.Conflict(new { code = exception.Code, detail = exception.Detail });
            }
        });

        endpoints.MapPost("/api/tasks/{taskId:guid}/confirm-dropoff", async (
            Guid taskId,
            OperatorActionRequest request,
            ITaskApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ConfirmDropoffAsync(taskId, request.OperatorName, cancellationToken)));

        endpoints.MapPost("/api/tasks/{taskId:guid}/retry", async (
            Guid taskId,
            ITaskApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.RetryAsync(taskId, cancellationToken));
            }
            catch (PhysicalExecutionAdmissionException exception)
            {
                return Results.Conflict(new { code = exception.Code, detail = exception.Detail });
            }
        });

        endpoints.MapPost("/api/tasks/{taskId:guid}/cancel", async (
            Guid taskId,
            OperatorActionRequest request,
            ITaskApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.CancelAsync(taskId, request.OperatorName, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
            catch (PhysicalExecutionAdmissionException exception)
            {
                return Results.Conflict(new { code = exception.Code, detail = exception.Detail });
            }
            catch (InvalidTaskTransitionException exception)
            {
                return Results.Conflict(new { detail = exception.Message });
            }
        });

        endpoints.MapPost("/api/tasks/{taskId:guid}/recover", async (
            Guid taskId,
            ITaskApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.RecoverAsync(taskId, cancellationToken)));

        endpoints.MapGet("/api/tasks", async (
            DateOnly? date,
            ITaskApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(date ?? DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken)));

        endpoints.MapGet("/api/tasks/{taskId:guid}", async (
            Guid taskId,
            ITaskApplicationService service,
            CancellationToken cancellationToken) =>
        {
            var task = await service.GetDetailAsync(taskId, cancellationToken);
            return task is null ? Results.NotFound() : Results.Ok(task);
        });

        return endpoints;
    }
}
