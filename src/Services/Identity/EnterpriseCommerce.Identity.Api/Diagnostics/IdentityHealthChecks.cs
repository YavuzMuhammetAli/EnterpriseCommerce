using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EnterpriseCommerce.Identity.Api.Diagnostics;

/// <summary>Readiness: Identity cannot confirm any security state without its database (ADR-017 section 8).</summary>
internal sealed class IdentityDatabaseHealthCheck(IdentityServiceDbContext context) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext healthContext, CancellationToken cancellationToken = default) =>
        await context.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The Identity database is unreachable.");
}

/// <summary>
/// <c>/health</c> and <c>/health/live</c> report process liveness only. <c>/health/ready</c> also checks the database,
/// so that an orchestrator stops routing to an instance that would fail closed with 503.
/// </summary>
internal static class IdentityHealthChecks
{
    private const string ReadyTag = "ready";

    public static IServiceCollection AddIdentityHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks().AddCheck<IdentityDatabaseHealthCheck>("identity-database", tags: [ReadyTag]);
        return services;
    }

    public static IEndpointRouteBuilder MapIdentityHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        var liveness = new HealthCheckOptions { Predicate = _ => false };
        endpoints.MapHealthChecks("/health", liveness);
        endpoints.MapHealthChecks("/health/live", liveness);
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) });
        return endpoints;
    }
}
