using EnterpriseCommerce.Catalog.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace EnterpriseCommerce.Catalog.Api.Diagnostics;

/// <summary>
/// <c>/health</c> and <c>/health/live</c> report process liveness only: a MongoDB outage never fails them.
/// <c>/health/ready</c> runs the readiness checks registered by Infrastructure (MongoDB reachable and every index
/// ensured). Responses carry the status word only, never descriptions, exceptions or configuration. They are anonymous.
/// </summary>
internal static class CatalogHealthChecks
{
    public static IEndpointRouteBuilder MapCatalogHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        var liveness = new HealthCheckOptions { Predicate = _ => false };
        endpoints.MapHealthChecks("/health", liveness).AllowAnonymous();
        endpoints.MapHealthChecks("/health/live", liveness).AllowAnonymous();
        endpoints.MapHealthChecks(
            "/health/ready",
            new HealthCheckOptions { Predicate = check => check.Tags.Contains(CatalogInfrastructureHealth.ReadyTag) })
            .AllowAnonymous();
        return endpoints;
    }
}
