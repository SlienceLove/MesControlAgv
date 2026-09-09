using MesControlAgv.Application;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Routing;

namespace MesControlAgv.Mes.Endpoints;

/// <summary>
/// Registers workflow definition, execution and run-control HTTP endpoints.
/// Route paths and result mapping are kept here so startup remains composition-only.
/// </summary>
public static class WorkflowEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapMesWorkflowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/workflows", async (
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        endpoints.MapGet("/api/workflows/{workflowId:guid}", async (
            Guid workflowId,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
        {
            var workflow = await service.GetAsync(workflowId, cancellationToken);
            return workflow is null ? Results.NotFound() : Results.Ok(workflow);
        });

        endpoints.MapGet("/api/workflows/{workflowId:guid}/versions", async (
            Guid workflowId,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListVersionsAsync(workflowId, cancellationToken)));

        endpoints.MapGet("/api/workflows/{workflowId:guid}/audits", async (
            Guid workflowId,
            int? version,
            int? limit,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.ListAuditsAsync(
                    workflowId,
                    version,
                    limit ?? 100,
                    cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
        });

        endpoints.MapGet("/api/workflows/{workflowId:guid}/versions/{version:int}", async (
            Guid workflowId,
            int version,
            IWorkflowVersionReader reader,
            CancellationToken cancellationToken) =>
        {
            var workflowVersion = await reader.GetVersionAsync(workflowId, version, cancellationToken);
            return workflowVersion is null ? Results.NotFound() : Results.Ok(workflowVersion);
        });

        endpoints.MapPost("/api/workflows", async (
            WorkflowDefinition definition,
            string actor,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var draft = await service.CreateDraftAsync(definition, actor, cancellationToken);
                return Results.Created($"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}", draft);
            }
            catch (ArgumentException exception)
            {
                return Results.UnprocessableEntity(new { detail = exception.Message });
            }
        });

        endpoints.MapPut("/api/workflows/{workflowId:guid}/versions/{version:int}/draft", async (
            Guid workflowId,
            int version,
            WorkflowDefinition definition,
            string actor,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.UpdateDraftAsync(workflowId, version, definition, actor, cancellationToken));
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { detail = exception.Message });
            }
            catch (ArgumentException exception)
            {
                return Results.UnprocessableEntity(new { detail = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.UnprocessableEntity(new { detail = exception.Message });
            }
        });

        endpoints.MapPost("/api/workflows/validate", async (
            WorkflowDefinition definition,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ValidateAsync(definition, cancellationToken)));

        endpoints.MapPost("/api/workflows/{workflowId:guid}/versions/{version:int}/validate", async (
            Guid workflowId,
            int version,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.ValidateVersionAsync(workflowId, version, cancellationToken));
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { detail = exception.Message });
            }
        });

        endpoints.MapPost("/api/workflows/{workflowId:guid}/versions/{version:int}/publish", async (
            Guid workflowId,
            int version,
            string actor,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.PublishAsync(workflowId, version, actor, cancellationToken));
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { detail = exception.Message });
            }
            catch (ArgumentException exception)
            {
                return Results.UnprocessableEntity(new { detail = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.UnprocessableEntity(new { detail = exception.Message });
            }
        });

        endpoints.MapPost("/api/workflows/execute", async (
            WorkflowExecutionRequest request,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
        {
            WorkflowExecutionResult result;
            try
            {
                result = await service.ExecuteAsync(request, cancellationToken);
            }
            catch (PhysicalExecutionAdmissionException exception)
            {
                return Results.Conflict(new { code = exception.Code, detail = exception.Detail });
            }
            if (result.IsAccepted)
            {
                return Results.Json(
                    result,
                    statusCode: result.IsIdempotentReplay
                        ? StatusCodes.Status200OK
                        : StatusCodes.Status202Accepted);
            }

            return result.RejectionCode switch
            {
                WorkflowExecutionRejectionCodes.VersionNotFound => Results.NotFound(result),
                WorkflowExecutionRejectionCodes.RequestIdReused or
                WorkflowExecutionRejectionCodes.PhysicalAgvBusy => Results.Conflict(result),
                _ => Results.UnprocessableEntity(result)
            };
        });

        endpoints.MapGet("/api/workflow-executions/{executionId:guid}", async (
            Guid executionId,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
        {
            var execution = await service.GetExecutionAsync(executionId, cancellationToken);
            return execution is null ? Results.NotFound() : Results.Ok(execution);
        });

        endpoints.MapGet("/api/workflow-executions/by-request/{requestId:guid}", async (
            Guid requestId,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
        {
            var execution = await service.GetExecutionByRequestAsync(requestId, cancellationToken);
            return execution is null ? Results.NotFound() : Results.Ok(execution);
        });

        endpoints.MapGet("/api/workflow-runs/{workflowRunId:guid}", async (
            Guid workflowRunId,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
        {
            var execution = await service.GetExecutionAsync(workflowRunId, cancellationToken);
            return execution is null ? Results.NotFound() : Results.Ok(execution);
        });

        endpoints.MapGet("/api/workflow-runs/{workflowRunId:guid}/nodes", async (
            Guid workflowRunId,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
        {
            if (await service.GetExecutionAsync(workflowRunId, cancellationToken) is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(await service.ListNodeExecutionsAsync(workflowRunId, cancellationToken));
        });

        endpoints.MapGet("/api/workflow-runs/{workflowRunId:guid}/device-operations", async (
            Guid workflowRunId,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
        {
            if (await service.GetExecutionAsync(workflowRunId, cancellationToken) is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(await service.ListDeviceOperationsAsync(workflowRunId, cancellationToken));
        });

        endpoints.MapGet("/api/workflow-runs/{workflowRunId:guid}/timeline", async (
            Guid workflowRunId,
            int? limit,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
        {
            if (await service.GetExecutionAsync(workflowRunId, cancellationToken) is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(await service.ListRunTimelineAsync(
                workflowRunId,
                limit ?? 200,
                cancellationToken));
        });

        endpoints.MapGet("/api/workflow-runs/{workflowRunId:guid}/interactions", async (
            Guid workflowRunId,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
        {
            if (await service.GetExecutionAsync(workflowRunId, cancellationToken) is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(await service.ListRuntimeInteractionsAsync(workflowRunId, cancellationToken));
        });

        endpoints.MapPost("/api/workflow-runs/{workflowRunId:guid}/signals", async (
            Guid workflowRunId,
            WorkflowExternalSignalRequest request,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
            await ExecuteWorkflowInteractionAsync(
                () => service.SubmitExternalSignalAsync(workflowRunId, request, cancellationToken)));

        endpoints.MapPost("/api/workflow-runs/{workflowRunId:guid}/nodes/{nodeExecutionId:guid}/manual-confirmation", async (
            Guid workflowRunId,
            Guid nodeExecutionId,
            WorkflowManualConfirmationRequest request,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
            await ExecuteWorkflowInteractionAsync(
                () => service.CompleteManualConfirmationAsync(
                    workflowRunId,
                    nodeExecutionId,
                    request,
                    cancellationToken)));

        endpoints.MapGet("/api/workflow-run-controls/permissions", (
            string actor,
            IWorkflowRunControlAuthorizer authorizer) =>
        {
            try
            {
                return Results.Ok(authorizer.GetPermissions(actor));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { detail = exception.Message });
            }
        });

        endpoints.MapPost("/api/workflow-runs/{workflowRunId:guid}/pause", async (
            Guid workflowRunId,
            WorkflowRunControlRequest request,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
            await ExecuteWorkflowRunControlAsync(
                () => service.PauseRunAsync(workflowRunId, request, cancellationToken)));

        endpoints.MapPost("/api/workflow-runs/{workflowRunId:guid}/resume", async (
            Guid workflowRunId,
            WorkflowRunControlRequest request,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
            await ExecuteWorkflowRunControlAsync(
                () => service.ResumeRunAsync(workflowRunId, request, cancellationToken)));

        endpoints.MapPost("/api/workflow-runs/{workflowRunId:guid}/cancel", async (
            Guid workflowRunId,
            WorkflowRunControlRequest request,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
            await ExecuteWorkflowRunControlAsync(
                () => service.CancelRunAsync(workflowRunId, request, cancellationToken)));

        endpoints.MapPost("/api/workflow-runs/{workflowRunId:guid}/unknown-resolution", async (
            Guid workflowRunId,
            WorkflowUnknownResolutionRequest request,
            IWorkflowApplicationService service,
            CancellationToken cancellationToken) =>
            await ExecuteWorkflowRunControlAsync(
                () => service.ResolveUnknownAsync(workflowRunId, request, cancellationToken)));

        return endpoints;
    }

    private static async Task<IResult> ExecuteWorkflowRunControlAsync(
        Func<Task<WorkflowRunControlResult>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { detail = exception.Message });
        }
        catch (WorkflowRunControlForbiddenException exception)
        {
            return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (PhysicalExecutionAdmissionException exception)
        {
            return Results.Conflict(new { code = exception.Code, detail = exception.Detail });
        }
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new { detail = exception.Message });
        }
        catch (WorkflowRunControlConflictException exception)
        {
            return Results.Conflict(new { detail = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { detail = exception.Message });
        }
    }

    private static async Task<IResult> ExecuteWorkflowInteractionAsync(
        Func<Task<WorkflowRuntimeInteractionResult>> action)
    {
        try
        {
            var result = await action();
            return Results.Json(
                result,
                statusCode: result.Status == WorkflowRuntimeInteractionStatus.Pending && !result.IsIdempotentReplay
                    ? StatusCodes.Status202Accepted
                    : StatusCodes.Status200OK);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { detail = exception.Message });
        }
        catch (WorkflowRunControlForbiddenException exception)
        {
            return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (PhysicalExecutionAdmissionException exception)
        {
            return Results.Conflict(new { code = exception.Code, detail = exception.Detail });
        }
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new { detail = exception.Message });
        }
        catch (WorkflowAdvancedRuntimeConflictException exception)
        {
            return Results.Conflict(new { code = exception.Code, detail = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { detail = exception.Message });
        }
    }
}
