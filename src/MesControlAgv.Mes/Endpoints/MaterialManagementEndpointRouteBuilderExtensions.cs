using MesControlAgv.Contracts.Materials;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Endpoints;

/// <summary>HTTP boundary for the small material registration/inventory module.</summary>
public static class MaterialManagementEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapMaterialManagementEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/materials/catalog", async (
            MaterialKind? kind,
            string? search,
            IMaterialManagementService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListCatalogAsync(kind, search, cancellationToken)));

        endpoints.MapGet("/api/materials/locations", async (
            IMaterialManagementService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListLocationsAsync(cancellationToken)));

        endpoints.MapGet("/api/materials/samples", async (
            string? barcode,
            SampleLifecycleStatus? status,
            string? search,
            IMaterialManagementService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListSamplesAsync(barcode, status, search, cancellationToken)));

        endpoints.MapGet("/api/materials/inventory", async (
            string? materialCode,
            string? lotCode,
            string? locationCode,
            bool? includeQuarantined,
            IMaterialManagementService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListInventoryAsync(
                materialCode,
                lotCode,
                locationCode,
                includeQuarantined ?? false,
                cancellationToken)));

        endpoints.MapPost("/api/materials/import/preview", async (
            MaterialImportRequest request,
            IMaterialManagementService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => service.PreviewImportAsync(request, cancellationToken),
                Results.Ok));

        endpoints.MapPost("/api/materials/import", async (
            MaterialImportRequest request,
            IMaterialManagementService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => service.ImportAsync(request, cancellationToken),
                result => result.IsIdempotentReplay
                    ? Results.Ok(result)
                    : Results.Created($"/api/materials/import/{result.RequestId}", result)));

        endpoints.MapPost("/api/materials/scan", async (
            MaterialScanRequest request,
            IMaterialManagementService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => service.ScanAsync(request, cancellationToken),
                Results.Ok));

        endpoints.MapPost("/api/materials/receive", async (
            ReceiveMaterialRequest request,
            IMaterialManagementService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => service.ReceiveAsync(request, cancellationToken),
                result => result.IsIdempotentReplay
                    ? Results.Ok(result)
                    : Results.Created($"/api/materials/lots/{result.Data?.LotId}", result)));

        endpoints.MapPost("/api/materials/move", async (
            MoveMaterialRequest request,
            IMaterialManagementService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => service.MoveAsync(request, cancellationToken),
                Results.Ok));

        endpoints.MapPost("/api/materials/adjust", async (
            AdjustMaterialRequest request,
            IMaterialManagementService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => service.AdjustAsync(request, cancellationToken),
                Results.Ok));

        endpoints.MapGet("/api/materials/trace", async (
            string? barcode,
            string? materialCode,
            string? lotCode,
            Guid? experimentJobId,
            int? limit,
            IMaterialManagementService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => service.TraceAsync(
                    barcode,
                    materialCode,
                    lotCode,
                    experimentJobId,
                    limit ?? 200,
                    cancellationToken),
                Results.Ok));

        endpoints.MapGet("/api/experiment-jobs/{jobId:guid}/materials", async (
            Guid jobId,
            IMaterialManagementService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => service.ListBindingsAsync(jobId, cancellationToken),
                Results.Ok));

        endpoints.MapPost("/api/experiment-jobs/{jobId:guid}/materials/reserve", async (
            Guid jobId,
            ReserveExperimentMaterialsRequest request,
            IMaterialManagementService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => service.ReserveAsync(request with { ExperimentJobId = jobId }, cancellationToken),
                result => result.IsIdempotentReplay ? Results.Ok(result) : Results.Created($"/api/experiment-jobs/{jobId}/materials", result)));

        endpoints.MapPost("/api/experiment-jobs/{jobId:guid}/materials/release", async (
            Guid jobId,
            ReleaseExperimentMaterialsRequest request,
            IMaterialManagementService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => service.ReleaseAsync(request with { ExperimentJobId = jobId }, cancellationToken),
                Results.Ok));

        endpoints.MapPost("/api/experiment-jobs/{jobId:guid}/materials/consume", async (
            Guid jobId,
            ConsumeExperimentMaterialsRequest request,
            IMaterialManagementService service,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(
                () => service.ConsumeAsync(request with { ExperimentJobId = jobId }, cancellationToken),
                Results.Ok));

        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync<T>(
        Func<Task<T>> action,
        Func<T, IResult> success)
    {
        try
        {
            return success(await action());
        }
        catch (MaterialRequestIdReusedException exception)
        {
            return Results.Conflict(new
            {
                code = MaterialIssueCodes.RequestIdReused,
                detail = exception.Message,
                requestId = exception.RequestId
            });
        }
        catch (MaterialManagementException exception)
        {
            return Results.Json(
                new
                {
                    code = exception.Code,
                    detail = exception.Message,
                    issues = exception.Issues
                },
                statusCode: exception.StatusCode);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { detail = exception.Message });
        }
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new { detail = exception.Message });
        }
        catch (DbUpdateException)
        {
            return Results.Conflict(new
            {
                code = MaterialIssueCodes.InventoryStateInvalid,
                detail = "物料数据与当前库存状态冲突，操作未提交。"
            });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new
            {
                code = MaterialIssueCodes.InventoryStateInvalid,
                detail = exception.Message
            });
        }
    }
}
