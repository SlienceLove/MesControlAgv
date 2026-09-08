using MesControlAgv.Application;
using MesControlAgv.Contracts;
using Microsoft.AspNetCore.Routing;

namespace MesControlAgv.Mes.Endpoints;

/// <summary>
/// Read-only physical readiness routes.  Refresh never acquires control and
/// never forwards a device command; it only asks the registered probes for
/// status/preflight evidence.
/// </summary>
public static class PhysicalReadinessEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapPhysicalReadinessEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/physical/readiness",
            (IPhysicalReadinessState state) => Results.Ok(state.GetSnapshot()));

        endpoints.MapPost(
            "/api/physical/readiness/refresh",
            async (
                bool? forceFull,
                IPhysicalReadinessSupervisor supervisor,
                CancellationToken cancellationToken) =>
                Results.Ok(await supervisor.RefreshAsync(
                    forceFull ?? true,
                    cancellationToken)));

        return endpoints;
    }
}
