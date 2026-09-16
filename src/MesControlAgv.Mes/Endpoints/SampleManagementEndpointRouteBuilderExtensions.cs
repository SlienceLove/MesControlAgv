using MesControlAgv.Contracts.Samples;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MesControlAgv.Mes.Endpoints;

public static class SampleManagementEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapSampleManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/samples/import", async (
            ImportSamplesRequest request,
            SampleManagementService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.ImportAsync(request, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
        });

        endpoints.MapGet("/api/samples", async (
            int? limit,
            SampleManagementService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.ListAsync(limit ?? 100, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
        });

        endpoints.MapPost("/api/samples", async (
            RegisterSampleRequest request,
            SampleManagementService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Created(
                    $"/api/samples/{request.SampleId}",
                    await service.RegisterAsync(request, cancellationToken));
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

        endpoints.MapGet("/api/samples/{sampleId}", async (
            string sampleId,
            SampleManagementService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var sample = await service.GetAsync(sampleId, cancellationToken);
                return sample is null ? Results.NotFound() : Results.Ok(sample);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
        });

        endpoints.MapPost("/api/samples/{sampleId}/bind-run", async (
            string sampleId,
            BindSampleRunRequest request,
            SampleManagementService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.BindRunAsync(sampleId, request, cancellationToken));
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
                return Results.Conflict(new { detail = exception.Message });
            }
        });

        endpoints.MapPost("/api/samples/{sampleId}/moves", async (
            string sampleId,
            MoveSampleRequest request,
            SampleManagementService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.MoveAsync(sampleId, request, cancellationToken));
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
                return Results.Conflict(new { detail = exception.Message });
            }
        });

        endpoints.MapGet("/api/samples/{sampleId}/events", async (
            string sampleId,
            SampleManagementService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.GetEventsAsync(sampleId, cancellationToken));
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { detail = exception.Message });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
        });

        return endpoints;
    }
}
