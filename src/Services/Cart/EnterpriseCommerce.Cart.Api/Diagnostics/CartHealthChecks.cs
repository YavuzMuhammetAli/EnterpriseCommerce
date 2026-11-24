using EnterpriseCommerce.Cart.Infrastructure;
using EnterpriseCommerce.Cart.Infrastructure.Caching;
using EnterpriseCommerce.Cart.Infrastructure.Cleanup;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EnterpriseCommerce.Cart.Api.Diagnostics;

/// <summary>
/// The health endpoints (CART_OPERATIONS.md section 3). Responses carry the status word only, never descriptions,
/// exceptions or configuration; <c>Degraded</c> answers 200. They are anonymous.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>/health</c> and <c>/health/live</c>: process liveness only; no dependency outage fails them.</item>
/// <item><c>/health/ready</c>: the database is reachable and its schema has every migration applied. Neither Redis nor
/// Catalog is part of it: every read is served from SQL Server without Redis, and only add and refresh need Catalog
/// (ADR-029 section 10).</item>
/// <item><c>/health/cache</c>: the cache Redis (ADR-030 section 5), <c>Degraded</c> when it is unavailable.</item>
/// <item><c>/health/cleanup</c>: the expired-cart cleanup (ADR-029 section 9), <c>Degraded</c> when it has not
/// completed recently. Expired carts read as empty anyway.</item>
/// </list>
/// </remarks>
internal static class CartHealthChecks
{
    public static IServiceCollection AddCartHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<CartDatabaseHealthCheck>(
                "cart-database", failureStatus: HealthStatus.Unhealthy, tags: [CartInfrastructureHealth.ReadyTag], timeout: CartInfrastructureHealth.CheckTimeout)
            .AddCheck<CartCacheHealthCheck>(
                "cart-cache", failureStatus: HealthStatus.Degraded, tags: [CartInfrastructureHealth.CacheTag], timeout: CartInfrastructureHealth.CheckTimeout)
            .AddCheck<CartCleanupHealthCheck>(
                "cart-cleanup", failureStatus: HealthStatus.Degraded, tags: [CartInfrastructureHealth.CleanupTag], timeout: CartInfrastructureHealth.CheckTimeout);
        return services;
    }

    public static IEndpointRouteBuilder MapCartHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        var liveness = new HealthCheckOptions { Predicate = _ => false };
        endpoints.MapHealthChecks("/health", liveness).AllowAnonymous();
        endpoints.MapHealthChecks("/health/live", liveness).AllowAnonymous();
        endpoints.MapHealthChecks("/health/ready", Tagged(CartInfrastructureHealth.ReadyTag)).AllowAnonymous();
        endpoints.MapHealthChecks("/health/cache", Tagged(CartInfrastructureHealth.CacheTag)).AllowAnonymous();
        endpoints.MapHealthChecks("/health/cleanup", Tagged(CartInfrastructureHealth.CleanupTag)).AllowAnonymous();
        return endpoints;
    }

    private static HealthCheckOptions Tagged(string tag) => new() { Predicate = check => check.Tags.Contains(tag) };
}
