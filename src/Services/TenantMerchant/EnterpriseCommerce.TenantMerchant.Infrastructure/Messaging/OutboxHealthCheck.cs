using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;

/// <summary>
/// The <c>outbox</c> health check (ADR-023 section 6, PHASE_3_S4_EVENT_FLOW.md section 7): <c>Degraded</c> when the
/// oldest pending row is older than <see cref="OutboxOptions.StaleAfter"/>, or when this instance's relay leads and is
/// backing off, halted or without its topic. It reads the backlog from the database, so every instance, leader or
/// standby, reports the same age.
/// </summary>
/// <remarks>
/// It is never part of readiness or liveness: a Kafka outage must not take the API out of rotation, because business
/// requests do not need Kafka. It answers on its own path, <c>/health/outbox</c>, for monitoring and alerting.
/// </remarks>
internal sealed class OutboxHealthCheck(IOutboxStore store, OutboxRelayState state, IOptions<OutboxOptions> options, TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var backlog = await store.GetBacklogAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var relay = state.Current;

        if (backlog.OldestOccurredAt is { } oldest && now - oldest > options.Value.StaleAfter)
        {
            return HealthCheckResult.Degraded("The oldest pending integration event is older than the threshold.");
        }

        if (relay.IsLeader && (relay.IsHalted || relay.BackingOffUntil > now || relay.Topic is not (TopicStatus.Exists or TopicStatus.Unknown)))
        {
            return HealthCheckResult.Degraded("The outbox relay is backing off, halted, or its topic is unavailable.");
        }

        return HealthCheckResult.Healthy();
    }
}
