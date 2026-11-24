using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace EnterpriseCommerce.Cart.Infrastructure.Caching;

/// <summary>
/// <c>/health/cache</c> (ADR-030 section 5): Redis answers a ping. Reported as <c>Degraded</c> on failure and never part
/// of readiness, because every request is served from SQL Server without Redis. A disabled cache is healthy.
/// </summary>
internal sealed class CartCacheHealthCheck(IOptions<CartCacheOptions> options, IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!options.Value.Enabled)
        {
            return HealthCheckResult.Healthy("The cache is disabled.");
        }

        try
        {
            // Resolved here, not injected: a disabled cache has no connection string to connect with.
            var connection = (CartCacheConnection)services.GetService(typeof(CartCacheConnection))!;
            await connection.Database.PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "The cache Redis is unavailable.");
        }
    }
}
