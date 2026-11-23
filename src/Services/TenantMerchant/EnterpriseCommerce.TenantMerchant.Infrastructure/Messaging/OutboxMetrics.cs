using System.Diagnostics.Metrics;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;

/// <summary>
/// The relay's meters (<c>EnterpriseCommerce.TenantMerchant.Outbox</c>; PHASE_3_S4_EVENT_FLOW.md section 7). Tags are
/// bounded: the failure category only, never a tenant, event or correlation ID. Exported once the observability phase
/// wires an exporter; tests observe them with a <see cref="MeterListener"/>.
/// </summary>
internal static class OutboxMetrics
{
    public const string MeterName = "EnterpriseCommerce.TenantMerchant.Outbox";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Messages the broker acknowledged.</summary>
    public static readonly Counter<long> Published = Meter.CreateCounter<long>("outbox.published", unit: "{message}");

    /// <summary>Failed publication attempts, tagged by <c>category</c>.</summary>
    public static readonly Counter<long> PublishFailures = Meter.CreateCounter<long>("outbox.publish_failures", unit: "{attempt}");

    /// <summary>Transitions into the halted state, tagged by <c>category</c>: the terminal failures an operator must resolve.</summary>
    public static readonly Counter<long> Halts = Meter.CreateCounter<long>("outbox.halts", unit: "{halt}");

    /// <summary>The time from producing one message to its delivery report, successful or not.</summary>
    public static readonly Histogram<double> PublishDuration = Meter.CreateHistogram<double>("outbox.publish.duration", unit: "s");

    /// <summary>The duration of one relay cycle: read, publish and mark one batch.</summary>
    public static readonly Histogram<double> CycleDuration = Meter.CreateHistogram<double>("outbox.cycle.duration", unit: "s");

    private static OutboxRelayState? _observed;

    static OutboxMetrics()
    {
        Meter.CreateObservableGauge("outbox.pending", () => _observed?.Current.Backlog.Count ?? 0, unit: "{message}");
        Meter.CreateObservableGauge(
            "outbox.oldest_pending_age_seconds",
            () => _observed?.Current.Backlog.OldestOccurredAt is { } oldest ? Math.Max(0, (TimeProvider.System.GetUtcNow() - oldest).TotalSeconds) : 0,
            unit: "s");
        Meter.CreateObservableGauge("outbox.leader", () => _observed?.Current.IsLeader == true ? 1 : 0);
        Meter.CreateObservableGauge("outbox.halted", () => _observed?.Current.IsHalted == true ? 1 : 0);
    }

    /// <summary>The relay state the observable gauges report: the hosted relay's, set once at startup.</summary>
    public static void Observe(OutboxRelayState state) => _observed = state;

    public static KeyValuePair<string, object?> CategoryTag(PublishFailureCategory category) =>
        new("category", category switch
        {
            PublishFailureCategory.Transient => "transient",
            PublishFailureCategory.Topic => "topic",
            PublishFailureCategory.Authorization => "authorization",
            PublishFailureCategory.Rejected => "rejected",
            PublishFailureCategory.Configuration => "configuration",
            _ => "unknown",
        });
}
