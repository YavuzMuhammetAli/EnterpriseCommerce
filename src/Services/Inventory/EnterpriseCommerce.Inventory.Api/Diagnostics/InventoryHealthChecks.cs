using EnterpriseCommerce.BuildingBlocks.Messaging;
using EnterpriseCommerce.Inventory.Infrastructure;
using EnterpriseCommerce.Inventory.Infrastructure.Reservations;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EnterpriseCommerce.Inventory.Api.Diagnostics;

/// <summary>
/// The health endpoints (INVENTORY_OPERATIONS.md section 3). Responses carry the status word only, never descriptions,
/// exceptions or configuration; <c>Degraded</c> answers 200. They are anonymous.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>/health</c> and <c>/health/live</c>: process liveness only; no dependency outage fails them.</item>
/// <item><c>/health/ready</c>: the database is reachable and its schema has every migration applied.</item>
/// <item><c>/health/outbox</c>: the integration event relay (ADR-023 section 6). Never part of readiness: a Kafka outage
/// never takes the API out of rotation.</item>
/// <item><c>/health/expiry</c>: the reservation expiry (ADR-026 section 7). Never part of readiness: commit and release
/// expire overdue reservations themselves.</item>
/// </list>
/// </remarks>
internal static class InventoryHealthChecks
{
    private const string EventRelayTag = "outbox";

    public static IServiceCollection AddInventoryHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<InventoryDatabaseHealthCheck>(
                "inventory-database", failureStatus: HealthStatus.Unhealthy, tags: [InventoryInfrastructureHealth.ReadyTag], timeout: InventoryInfrastructureHealth.CheckTimeout)
            .AddCheck<OutboxHealthCheck>("outbox", failureStatus: HealthStatus.Degraded, tags: [EventRelayTag], timeout: InventoryInfrastructureHealth.CheckTimeout)
            .AddCheck<ReservationExpiryHealthCheck>(
                "reservation-expiry", failureStatus: HealthStatus.Degraded, tags: [InventoryInfrastructureHealth.ExpiryTag], timeout: InventoryInfrastructureHealth.CheckTimeout);
        return services;
    }

    public static IEndpointRouteBuilder MapInventoryHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        var liveness = new HealthCheckOptions { Predicate = _ => false };
        endpoints.MapHealthChecks("/health", liveness).AllowAnonymous();
        endpoints.MapHealthChecks("/health/live", liveness).AllowAnonymous();
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(InventoryInfrastructureHealth.ReadyTag) }).AllowAnonymous();
        endpoints.MapHealthChecks("/health/outbox", new HealthCheckOptions { Predicate = check => check.Tags.Contains(EventRelayTag) }).AllowAnonymous();
        endpoints.MapHealthChecks("/health/expiry", new HealthCheckOptions { Predicate = check => check.Tags.Contains(InventoryInfrastructureHealth.ExpiryTag) }).AllowAnonymous();
        return endpoints;
    }
}
