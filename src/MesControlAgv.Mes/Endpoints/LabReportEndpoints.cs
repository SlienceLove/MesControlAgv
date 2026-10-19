using MesControlAgv.Mes.Services;
using MesControlAgv.Reporting;
using Microsoft.AspNetCore.Mvc;

namespace MesControlAgv.Mes.Endpoints;

public static class LabReportEndpoints
{
    public static IServiceCollection AddLabReports(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<LabReportService>();
        services.AddSingleton(_ =>
        {
            var url = configuration["LabReports:OllamaBaseUrl"] ?? "http://127.0.0.1:11434/";
            var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };
            return new OllamaReportClient(http, configuration["LabReports:Model"] ?? "qwen3:1.7b");
        });
        return services;
    }

    public static IEndpointRouteBuilder MapLabReports(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/lab-reports/snapshot", async (DateTimeOffset from, DateTimeOffset to,
            string? batch, LabReportService service, CancellationToken token) =>
        {
            try { return Results.Ok(await service.ReadAsync(new(from, to, batch), token)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { detail = ex.Message }); }
        });
        // This POST performs analysis only; it has no device/DB mutation dependency.
        endpoints.MapPost("/api/lab-reports/summary", [RequestSizeLimit(8 * 1024 * 1024)] async (
            ReportSnapshot snapshot, OllamaReportClient model, CancellationToken token) =>
            Results.Ok(await model.SummarizeAsync(snapshot, token)));
        return endpoints;
    }
}
