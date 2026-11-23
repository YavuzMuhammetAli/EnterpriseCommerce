using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Driver;

namespace EnterpriseCommerce.Catalog.Infrastructure.Persistence;

/// <summary>
/// Readiness (<c>/health/ready</c>): the service can serve requests correctly only when MongoDB answers and every index
/// exists. Liveness never runs this check, so a MongoDB outage never restarts the process.
/// </summary>
internal sealed class CatalogStoreHealthCheck(CatalogMongoContext context, CatalogIndexState indexes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext healthContext, CancellationToken cancellationToken = default)
    {
        if (indexes.Status != IndexSetupStatus.Ready)
        {
            return HealthCheckResult.Unhealthy("The Catalog indexes are not ready.");
        }

        try
        {
            await context.PingAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is MongoException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("The Catalog database is unreachable.");
        }
    }
}
