using EnterpriseCommerce.Identity.Api.Protocol.Keys;
using EnterpriseCommerce.Identity.Application.SecurityEmails;
using EnterpriseCommerce.Identity.Infrastructure.Diagnostics;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using EnterpriseCommerce.Identity.Infrastructure.Retention;
using EnterpriseCommerce.Identity.Infrastructure.SecurityEmails;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

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
/// Readiness: an Active signing and encryption key exists and is within its validity period. Degraded when one expires
/// within <see cref="ExpiryWarning"/>, so the rollover is started in time.
/// </summary>
internal sealed class SigningKeysHealthCheck(KeyRing ring, TimeProvider timeProvider) : IHealthCheck
{
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(14);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var result = HealthCheckResult.Healthy();
        foreach (var key in new[] { ring.ActiveSigning, ring.ActiveEncryption })
        {
            if (key.NotAfter is not { } notAfter)
            {
                continue;
            }

            if (notAfter <= now)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy($"The Active {key.Use} key has expired."));
            }

            if (notAfter - now < ExpiryWarning)
            {
                result = HealthCheckResult.Degraded($"The Active {key.Use} key expires within {ExpiryWarning.TotalDays} days.");
            }
        }

        return Task.FromResult(result);
    }
}

/// <summary>
/// Readiness: security emails have a delivery adapter. Outside Development, startup already refuses to run without the
/// Notification integration (ADR-018 section 3); in Development without it or the mail sink, deliveries fail (Degraded).
/// </summary>
internal sealed class SecurityEmailDeliveryHealthCheck(
    ISecurityEmailDispatcher dispatcher,
    IOptions<NotificationDeliveryOptions> notification) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(dispatcher is NotificationSecurityEmailDispatcher && !notification.Value.IsConfigured
            ? HealthCheckResult.Degraded("No security email delivery adapter is configured.")
            : HealthCheckResult.Healthy());
}

/// <summary>
/// Readiness: the periodic workers run. Degraded, never Unhealthy, when one has missed three intervals: a stuck cleanup
/// or reconciliation must be visible, but it must not take request traffic away from an instance that serves it.
/// </summary>
internal sealed class BackgroundWorkersHealthCheck(
    BackgroundWorkerHeartbeats heartbeats,
    IOptions<IdentityRetentionOptions> retention,
    IOptions<SecurityEmailDeliveryOptions> email,
    TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var overdue = new List<string>();
        if (retention.Value.Enabled && heartbeats.Retention.IsOverdue(now, retention.Value.InitialDelay, 3 * retention.Value.Interval))
        {
            overdue.Add("retention");
        }

        if (heartbeats.SecurityEmailReconciliation.IsOverdue(now, TimeSpan.Zero, 3 * email.Value.ReconcileInterval))
        {
            overdue.Add("security-email-reconciliation");
        }

        return Task.FromResult(overdue.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded($"Overdue background workers: {string.Join(", ", overdue)}."));
    }
}

/// <summary>
/// <c>/health</c> and <c>/health/live</c> report process liveness only. <c>/health/ready</c> runs the readiness checks,
/// so that an orchestrator stops routing to an instance that would fail closed with 503. Responses carry the status word
/// only (<c>Healthy</c>, <c>Degraded</c>, <c>Unhealthy</c>), never descriptions, exceptions or configuration. Degraded
/// answers 200; Unhealthy answers 503. Identity has no Redis dependency, so no Redis check exists.
/// </summary>
internal static class IdentityHealthChecks
{
    private const string ReadyTag = "ready";

    /// <summary>Every dependency check gives up after this long, so a hanging dependency cannot hang the probe.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    public static IServiceCollection AddIdentityHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<IdentityDatabaseHealthCheck>("identity-database", failureStatus: HealthStatus.Unhealthy, tags: [ReadyTag], timeout: CheckTimeout)
            .AddCheck<SigningKeysHealthCheck>("signing-keys", failureStatus: HealthStatus.Unhealthy, tags: [ReadyTag], timeout: CheckTimeout)
            .AddCheck<SecurityEmailDeliveryHealthCheck>("security-email-delivery", failureStatus: HealthStatus.Degraded, tags: [ReadyTag], timeout: CheckTimeout)
            .AddCheck<BackgroundWorkersHealthCheck>("background-workers", failureStatus: HealthStatus.Degraded, tags: [ReadyTag], timeout: CheckTimeout);
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
