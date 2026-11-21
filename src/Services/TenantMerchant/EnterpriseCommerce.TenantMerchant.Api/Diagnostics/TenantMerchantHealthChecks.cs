using EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EnterpriseCommerce.TenantMerchant.Api.Diagnostics;

/// <summary>Readiness: the service cannot answer any request without its database.</summary>
internal sealed class TenantMerchantDatabaseHealthCheck(TenantMerchantDbContext context) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext healthContext, CancellationToken cancellationToken = default) =>
        await context.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The Tenant & Merchant database is unreachable.");
}

/// <summary>
/// <c>/health</c> and <c>/health/live</c> report process liveness only. <c>/health/ready</c> runs the readiness checks.
/// Responses carry the status word only, never descriptions, exceptions or configuration. They are anonymous.
/// </summary>
internal static class TenantMerchantHealthChecks
{
    private const string ReadyTag = "ready";

    /// <summary>Every dependency check gives up after this long, so a hanging dependency cannot hang the probe.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    public static IServiceCollection AddTenantMerchantHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<TenantMerchantDatabaseHealthCheck>("tenant-merchant-database", failureStatus: HealthStatus.Unhealthy, tags: [ReadyTag], timeout: CheckTimeout);
        return services;
    }

    public static IEndpointRouteBuilder MapTenantMerchantHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        var liveness = new HealthCheckOptions { Predicate = _ => false };
        endpoints.MapHealthChecks("/health", liveness).AllowAnonymous();
        endpoints.MapHealthChecks("/health/live", liveness).AllowAnonymous();
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) }).AllowAnonymous();
        return endpoints;
    }
}
