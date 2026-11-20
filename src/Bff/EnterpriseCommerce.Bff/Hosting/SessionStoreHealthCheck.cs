using EnterpriseCommerce.Bff.Sessions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace EnterpriseCommerce.Bff.Hosting;

/// <summary>Healthy only while the session Redis answers and still uses <c>noeviction</c>.</summary>
internal sealed class SessionStoreHealthCheck(IRedisConnection redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var policy = await redis.GetEvictionPolicyAsync(cancellationToken);
            return SessionStorePolicyCheck.IsAccepted(policy)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The session store does not use noeviction.");
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("The session store is unavailable.");
        }
    }
}
