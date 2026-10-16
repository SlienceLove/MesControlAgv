using MesControlAgv.Contracts.Materials;
using MesControlAgv.Mes.Services;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Endpoints;

public static class DetectionStandardEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDetectionStandardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/materials/detection-standards", async (
            string? search, bool? includeHistory, DetectionStandardService service, CancellationToken token) =>
            Results.Ok(await service.ListAsync(search, includeHistory ?? false, token)));

        endpoints.MapGet("/api/materials/detection-standards/lookup", async (
            string methodCode, int? revision, DetectionStandardService service, CancellationToken token) =>
        {
            var standard = await service.GetAsync(methodCode, revision, token);
            return standard is null ? Results.NotFound() : Results.Ok(standard);
        });

        endpoints.MapPost("/api/materials/detection-standards", async (
            SaveDetectionStandardRequest request, DetectionStandardService service, CancellationToken token) =>
            await ExecuteAsync(() => service.SaveAsync(request, token),
                value => Results.Created($"/api/materials/detection-standards/lookup?methodCode={Uri.EscapeDataString(value.MethodCode)}&revision={value.Revision}", value)));

        endpoints.MapPost("/api/materials/detection-standards/deactivate", async (
            DeactivateDetectionStandardRequest request, DetectionStandardService service, CancellationToken token) =>
            await ExecuteAsync(() => service.DeactivateAsync(request.MethodCode, request.RevisionId, token), Results.Ok));

        endpoints.MapPost("/api/materials/detection-assessments/preview", async (
            DetectionAssessmentRequest request, DetectionStandardService service, CancellationToken token) =>
            await ExecuteAsync(() => service.PreviewAsync(request, token), Results.Ok));

        endpoints.MapPost("/api/materials/detection-assessments", async (
            DetectionAssessmentRequest request, DetectionStandardService service, CancellationToken token) =>
            await ExecuteAsync(() => service.AssessAsync(request, token),
                value => Results.Created($"/api/materials/samples/{value.SampleId}/detection-assessments", value)));

        endpoints.MapGet("/api/materials/samples/{sampleId:guid}/detection-assessments", async (
            Guid sampleId, DetectionStandardService service, CancellationToken token) =>
            await ExecuteAsync(() => service.HistoryAsync(sampleId, token), Results.Ok));

        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action, Func<T, IResult> success)
    {
        try { return success(await action()); }
        catch (ArgumentException exception) { return Results.BadRequest(new { detail = exception.Message }); }
        catch (KeyNotFoundException exception) { return Results.NotFound(new { detail = exception.Message }); }
        catch (InvalidOperationException exception) { return Results.Conflict(new { detail = exception.Message }); }
        catch (DbUpdateException) { return Results.Conflict(new { detail = "检测标准或判定记录与当前数据冲突，请刷新后重试。" }); }
    }
}
