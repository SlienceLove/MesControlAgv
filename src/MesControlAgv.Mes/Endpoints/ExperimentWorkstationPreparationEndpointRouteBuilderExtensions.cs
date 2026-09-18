using MesControlAgv.Application;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Endpoints;

public static class ExperimentWorkstationPreparationEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapMesExperimentWorkstationPreparationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/experiment-jobs/{jobId:guid}/workstation-preparations/current", async (
            Guid jobId,
            IExperimentWorkstationPreparationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var current = await service.GetCurrentAsync(jobId, cancellationToken);
                return current is null ? Results.NotFound() : Results.Ok(current);
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { detail = exception.Message }); }
            catch (KeyNotFoundException exception) { return Results.NotFound(new { detail = exception.Message }); }
        });

        endpoints.MapPost("/api/experiment-jobs/{jobId:guid}/workstation-preparations/prepare", async (
            Guid jobId,
            PrepareExperimentWorkstationTaskRequest request,
            IExperimentWorkstationPreparationService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => service.PrepareAsync(jobId, request, cancellationToken),
                result => Results.Created(
                    $"/api/experiment-jobs/{jobId}/workstation-preparations/current",
                    result)));

        endpoints.MapPost("/api/experiment-jobs/{jobId:guid}/workstation-preparations/{preparationId:guid}/import", async (
            Guid jobId,
            Guid preparationId,
            ImportExperimentWorkstationTaskRequest request,
            IExperimentWorkstationPreparationService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(() => service.ImportAsync(jobId, preparationId, request, cancellationToken), Results.Ok));

        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action, Func<T, IResult> success)
    {
        try
        {
            var result = await action();
            return success(result);
        }
        catch (ArgumentException exception) { return Results.BadRequest(new { detail = exception.Message }); }
        catch (KeyNotFoundException exception) { return Results.NotFound(new { detail = exception.Message }); }
        catch (ExperimentWorkstationPreparationException exception)
        {
            return Results.Conflict(new { detail = exception.Message, code = exception.Code });
        }
        catch (ExperimentSampleVerificationException exception)
        {
            return Results.Conflict(new { detail = exception.Message, code = exception.Code });
        }
    }

}
