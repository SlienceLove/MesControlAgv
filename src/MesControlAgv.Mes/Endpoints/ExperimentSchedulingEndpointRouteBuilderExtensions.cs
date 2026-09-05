using MesControlAgv.Application;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Routing;

namespace MesControlAgv.Mes.Endpoints;

/// <summary>
/// Registers experiment plan, job, schedule and resource-availability endpoints.
/// The extension keeps HTTP error mapping at the transport boundary while the
/// scheduling services remain independent of ASP.NET types.
/// </summary>
public static class ExperimentSchedulingEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapMesExperimentSchedulingEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/experiment-plans", async (
            IExperimentSchedulingQueryService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListPlansAsync(cancellationToken)));

        endpoints.MapGet("/api/experiment-plans/{planId:guid}/versions", async (
            Guid planId,
            IExperimentSchedulingQueryService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListPlanVersionsAsync(planId, cancellationToken)));

        endpoints.MapGet("/api/experiment-plans/{planId:guid}/versions/{version:int}", async (
            Guid planId,
            int version,
            IExperimentSchedulingQueryService service,
            CancellationToken cancellationToken) =>
        {
            var plan = await service.GetPlanAsync(planId, version, cancellationToken);
            return plan is null ? Results.NotFound() : Results.Ok(plan);
        });

        endpoints.MapPost("/api/experiment-plans", async (
            SaveExperimentPlanDraftRequest request,
            IExperimentSchedulingCommandService service,
            CancellationToken cancellationToken) =>
            await ExecuteSchedulingCommandAsync(
                () => service.CreatePlanDraftAsync(request, cancellationToken),
                plan => Results.Created(
                    $"/api/experiment-plans/{plan.PlanId}/versions/{plan.Version}",
                    plan)));

        endpoints.MapPut("/api/experiment-plans/{planId:guid}/versions/{version:int}/draft", async (
            Guid planId,
            int version,
            SaveExperimentPlanDraftRequest request,
            IExperimentSchedulingCommandService service,
            CancellationToken cancellationToken) =>
            await ExecuteSchedulingCommandAsync(
                () => service.UpdatePlanDraftAsync(planId, version, request, cancellationToken),
                Results.Ok));

        endpoints.MapPost("/api/experiment-plans/{planId:guid}/versions/{version:int}/validate", async (
            Guid planId,
            int version,
            ExperimentSchedulingActionRequest request,
            IExperimentSchedulingCommandService service,
            CancellationToken cancellationToken) =>
            await ExecuteSchedulingCommandAsync(
                () => service.ValidatePlanAsync(planId, version, request, cancellationToken),
                Results.Ok));

        endpoints.MapPost("/api/experiment-plans/{planId:guid}/versions/{version:int}/publish", async (
            Guid planId,
            int version,
            ExperimentSchedulingActionRequest request,
            IExperimentSchedulingCommandService service,
            CancellationToken cancellationToken) =>
            await ExecuteSchedulingCommandAsync(
                () => service.PublishPlanAsync(planId, version, request, cancellationToken),
                Results.Ok));

        endpoints.MapPost("/api/experiment-plans/{planId:guid}/versions/{sourceVersion:int}/next-draft", async (
            Guid planId,
            int sourceVersion,
            ExperimentSchedulingActionRequest request,
            IExperimentSchedulingCommandService service,
            CancellationToken cancellationToken) =>
            await ExecuteSchedulingCommandAsync(
                () => service.CreateNextPlanDraftAsync(planId, sourceVersion, request, cancellationToken),
                plan => Results.Created(
                    $"/api/experiment-plans/{plan.PlanId}/versions/{plan.Version}",
                    plan)));

        endpoints.MapGet("/api/experiment-jobs", async (
            ExperimentJobStatus? status,
            IExperimentSchedulingQueryService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListJobsAsync(status, cancellationToken)));

        endpoints.MapGet("/api/experiment-jobs/{jobId:guid}", async (
            Guid jobId,
            IExperimentSchedulingQueryService service,
            CancellationToken cancellationToken) =>
        {
            var job = await service.GetJobAsync(jobId, cancellationToken);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        endpoints.MapPost("/api/experiment-jobs", async (
            CreateExperimentJobRequest request,
            IExperimentSchedulingCommandService service,
            CancellationToken cancellationToken) =>
            await ExecuteSchedulingCommandAsync(
                () => service.CreateJobAsync(request, cancellationToken),
                job => Results.Created($"/api/experiment-jobs/{job.JobId}", job)));

        endpoints.MapPut("/api/experiment-jobs/{jobId:guid}/schedule", async (
            Guid jobId,
            ScheduleExperimentJobRequest request,
            IExperimentSchedulingCommandService service,
            CancellationToken cancellationToken) =>
            await ExecuteSchedulingCommandAsync(
                () => service.ScheduleJobAsync(jobId, request, cancellationToken),
                Results.Ok));

        endpoints.MapPost("/api/experiment-jobs/{jobId:guid}/admit", async (
            Guid jobId,
            AdmitExperimentJobRequest request,
            IExperimentRuntimeAdmissionService service,
            CancellationToken cancellationToken) =>
            await ExecuteAdmissionAsync(
                () => service.AdmitJobAsync(jobId, request, cancellationToken)));

        endpoints.MapPost("/api/experiment-runs/prepare", async (
            PrepareExperimentRunRequest request,
            IExperimentCompositeRuntimeService service,
            CancellationToken cancellationToken) =>
            await ExecuteSchedulingCommandAsync(
                () => service.PrepareAsync(request, cancellationToken),
                run => Results.Created($"/api/experiment-runs/{run.ExperimentRunId}", run)));

        endpoints.MapGet("/api/experiment-runs/{runId:guid}", async (
            Guid runId,
            IExperimentCompositeRuntimeService service,
            CancellationToken cancellationToken) =>
        {
            var run = await service.GetAsync(runId, cancellationToken);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        endpoints.MapGet("/api/experiment-jobs/{jobId:guid}/experiment-run", async (
            Guid jobId,
            IExperimentCompositeRuntimeService service,
            CancellationToken cancellationToken) =>
        {
            var run = await service.GetForJobAsync(jobId, cancellationToken);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        endpoints.MapPost("/api/experiment-runs/{runId:guid}/reconcile-child", async (
            Guid runId,
            ReconcileExperimentChildRequest request,
            IExperimentCompositeRuntimeService service,
            CancellationToken cancellationToken) =>
            await ExecuteSchedulingCommandAsync(
                () => service.ReconcileChildAsync(runId, request, cancellationToken),
                Results.Ok));

        endpoints.MapPost("/api/experiment-jobs/{jobId:guid}/unschedule", async (
            Guid jobId,
            ExperimentSchedulingActionRequest request,
            IExperimentSchedulingCommandService service,
            CancellationToken cancellationToken) =>
            await ExecuteSchedulingCommandAsync(
                () => service.UnscheduleJobAsync(jobId, request, cancellationToken),
                Results.Ok));

        endpoints.MapPost("/api/experiment-jobs/{jobId:guid}/cancel", async (
            Guid jobId,
            ExperimentSchedulingActionRequest request,
            IExperimentSchedulingCommandService service,
            CancellationToken cancellationToken) =>
            await ExecuteSchedulingCommandAsync(
                () => service.CancelJobAsync(jobId, request, cancellationToken),
                Results.Ok));

        endpoints.MapGet("/api/schedule", async (
            DateTimeOffset? from,
            DateTimeOffset? to,
            IExperimentSchedulingQueryService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.GetScheduleAsync(from, to, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
        });

        endpoints.MapGet("/api/resources/availability", async (
            DateTimeOffset? from,
            DateTimeOffset? to,
            IExperimentSchedulingQueryService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.ListResourceAvailabilityAsync(from, to, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
        });

        endpoints.MapGet("/api/experiment-scheduling/audits", async (
            Guid? planId,
            Guid? experimentJobId,
            Guid? scheduleEntryId,
            int? limit,
            IExperimentSchedulingQueryService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.ListAuditsAsync(
                    planId,
                    experimentJobId,
                    scheduleEntryId,
                    limit ?? 200,
                    cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
        });

        return endpoints;
    }

    private static async Task<IResult> ExecuteSchedulingCommandAsync<T>(
        Func<Task<T>> action,
        Func<T, IResult> success)
    {
        try
        {
            return success(await action());
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { detail = exception.Message });
        }
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new { detail = exception.Message });
        }
        catch (ExperimentPlanValidationException exception)
        {
            return Results.UnprocessableEntity(new
            {
                detail = exception.Message,
                validation = exception.Validation
            });
        }
        catch (ExperimentSchedulingConflictException exception)
        {
            return Results.Conflict(new { detail = exception.Message, code = exception.Code });
        }
    }

    private static async Task<IResult> ExecuteAdmissionAsync(
        Func<Task<ExperimentJobAdmissionResult>> action)
    {
        try
        {
            var result = await action();
            if (result.IsAdmitted)
            {
                return Results.Json(
                    result,
                    statusCode: result.IsIdempotentReplay
                        ? StatusCodes.Status200OK
                        : StatusCodes.Status202Accepted);
            }

            return Results.Json(
                result,
                statusCode: result.RejectionCode == ExperimentSchedulingIssueCodes.WorkflowAdmissionRejected
                    ? StatusCodes.Status422UnprocessableEntity
                    : StatusCodes.Status409Conflict);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { detail = exception.Message });
        }
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new { detail = exception.Message });
        }
        catch (ExperimentSchedulingConflictException exception)
        {
            return Results.Conflict(new { detail = exception.Message, code = exception.Code });
        }
    }
}
