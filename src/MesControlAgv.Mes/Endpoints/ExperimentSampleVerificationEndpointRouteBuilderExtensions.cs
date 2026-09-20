using MesControlAgv.Application;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Routing;

namespace MesControlAgv.Mes.Endpoints;

/// <summary>HTTP transport for central sample registration and task-row verification.</summary>
public static class ExperimentSampleVerificationEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapMesExperimentSampleVerificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/experiment-samples", async (string? batchId, IExperimentSampleVerificationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListSamplesAsync(new QueryExperimentSamplesRequest { BatchId = batchId }, cancellationToken)));

        endpoints.MapPut("/api/experiment-samples/{sampleId:guid}", async (Guid sampleId, SaveExperimentSampleRequest request, IExperimentSampleVerificationService service, CancellationToken cancellationToken) =>
            await ExecuteAsync(() => service.SaveSampleAsync(sampleId, request, cancellationToken), Results.Ok));

        endpoints.MapGet("/api/experiment-jobs/{jobId:guid}/sample-verifications/current", async (Guid jobId, IExperimentSampleVerificationService service, CancellationToken cancellationToken) =>
        {
            var current = await service.GetCurrentAsync(jobId, cancellationToken);
            return current is null ? Results.NotFound() : Results.Ok(current);
        });

        endpoints.MapPut("/api/experiment-jobs/{jobId:guid}/sample-verifications/current", async (Guid jobId, SaveExperimentSampleVerificationRequest request, IExperimentSampleVerificationService service, CancellationToken cancellationToken) =>
            await ExecuteAsync(() => service.SaveCurrentAsync(jobId, request, cancellationToken), Results.Ok));

        endpoints.MapPost("/api/experiment-jobs/{jobId:guid}/sample-verifications/{revision:int}/verify", async (Guid jobId, int revision, CompleteExperimentSampleVerificationRequest request, IExperimentSampleVerificationService service, CancellationToken cancellationToken) =>
            await ExecuteAsync(() => service.VerifyAsync(jobId, revision, request, cancellationToken), Results.Ok));

        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action, Func<T, IResult> success)
    {
        try { return success(await action()); }
        catch (ArgumentException exception) { return Results.BadRequest(new { detail = exception.Message }); }
        catch (KeyNotFoundException exception) { return Results.NotFound(new { detail = exception.Message }); }
        catch (ExperimentSampleVerificationException exception) { return Results.Conflict(new { detail = exception.Message, code = exception.Code }); }
        catch (ExperimentSchedulingConflictException exception) { return Results.Conflict(new { detail = exception.Message, code = exception.Code }); }
    }
}
