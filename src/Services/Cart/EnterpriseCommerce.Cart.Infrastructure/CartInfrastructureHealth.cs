using EnterpriseCommerce.Cart.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EnterpriseCommerce.Cart.Infrastructure;

/// <summary>The tags and bound of the Infrastructure health checks (CART_OPERATIONS.md section 3).</summary>
public static class CartInfrastructureHealth
{
    /// <summary>Checks that decide readiness: the service cannot answer any request without them.</summary>
    public const string ReadyTag = "ready";

    /// <summary>The cache check, on its own endpoint, never part of readiness (ADR-030 section 5).</summary>
    public const string CacheTag = "cache";

    /// <summary>The expired-cart cleanup check, on its own endpoint, never part of readiness.</summary>
    public const string CleanupTag = "cleanup";

    /// <summary>Every dependency check gives up after this long, so a hanging dependency cannot hang the probe.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Readiness: the Cart database is reachable and has every migration of this build applied. A database behind the code
/// (a deployment that forgot its migration step) is not ready, so no request runs against a missing constraint.
/// </summary>
internal sealed class CartDatabaseHealthCheck(CartDbContext context) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext healthContext, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await context.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("The Cart database is unreachable.");
            }

            return (await context.Database.GetPendingMigrationsAsync(cancellationToken)).Any()
                ? HealthCheckResult.Unhealthy("The Cart database has pending migrations.")
                : HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException && SqlServerErrors.IsSqlServerFailure(exception))
        {
            return HealthCheckResult.Unhealthy("The Cart database is unreachable.");
        }
    }
}
