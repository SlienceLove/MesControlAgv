using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Mvc;

namespace MesControlAgv.Mes.Endpoints;

public static class AiPlanningEndpoints
{
    public static IServiceCollection AddAiPlanning(this IServiceCollection services)
    {
        services.AddScoped<AiPlanningService>();
        services.AddSingleton<IAiPlanningModel>(provider => new OllamaPlanningModel(
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            { Timeout = Timeout.InfiniteTimeSpan },
            provider.GetRequiredService<IConfiguration>()));
        return services;
    }

    public static IEndpointRouteBuilder MapAiPlanning(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/ai-planning/catalog", (AiPlanningService service, CancellationToken token) =>
            Handle(async () => Results.Ok(await service.CatalogAsync(token))));
        endpoints.MapPost("/api/ai-planning/propose", [RequestSizeLimit(65536)] (
            AiPlanningRequest request, AiPlanningService service, CancellationToken token) =>
            Handle(async () => Results.Ok(await service.GenerateAsync(request, token))));
        endpoints.MapPost("/api/ai-planning/drafts", [RequestSizeLimit(65536)] (
            CreateAiPlanDraftRequest request, AiPlanningService service, CancellationToken token) =>
            Handle(async () =>
            {
                var plan = await service.CreateDraftAsync(request, token);
                return Results.Created($"/api/experiment-plans/{plan.PlanId}/versions/{plan.Version}", plan);
            }));
        return endpoints;
    }

    private static async Task<IResult> Handle(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { detail = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
    }
}
