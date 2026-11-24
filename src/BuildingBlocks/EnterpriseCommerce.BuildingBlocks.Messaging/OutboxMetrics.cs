using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace EnterpriseCommerce.BuildingBlocks.Messaging;

/// <summary>
/// The relay's meters, one meter per publishing service (<see cref="OutboxRelayProfile.MeterName"/>, for example
/// <c>EnterpriseCommerce.TenantMerchant.Outbox</c>; ADR-023 section 6). Tags are bounded: the failure category only, never
/// a tenant, event or correlation ID. Exported once the observability phase wires an exporter; tests observe them with a
/// <see cref="MeterListener"/>.
/// </summary>
/// <remarks>
/// One instance exists per meter name and process (<see cref="For"/>), as the static meter of the original Tenant &amp;
/// Merchant implementation did: the observable gauges report the relay state given to <see cref="Observe"/> last.
/// </remarks>
public sealed class OutboxMetrics
{
    private static readonly ConcurrentDictionary<string, OutboxMetrics> Instances = new(StringComparer.Ordinal);

    private OutboxRelayState? _observed;

    private OutboxMetrics(string meterName)
    {
        MeterName = meterName;
        var meter = new Meter(meterName);
        Published = meter.CreateCounter<long>("outbox.published", unit: "{message}");
        PublishFailures = meter.CreateCounter<long>("outbox.publish_failures", unit: "{attempt}");
        Halts = meter.CreateCounter<long>("outbox.halts", unit: "{halt}");
        PublishDuration = meter.CreateHistogram<double>("outbox.publish.duration", unit: "s");
        CycleDuration = meter.CreateHistogram<double>("outbox.cycle.duration", unit: "s");

        meter.CreateObservableGauge("outbox.pending", () => _observed?.Current.Backlog.Count ?? 0, unit: "{message}");
        meter.CreateObservableGauge(
            "outbox.oldest_pending_age_seconds",
            () => _observed?.Current.Backlog.OldestOccurredAt is { } oldest ? Math.Max(0, (TimeProvider.System.GetUtcNow() - oldest).TotalSeconds) : 0,
            unit: "s");
        meter.CreateObservableGauge("outbox.leader", () => _observed?.Current.IsLeader == true ? 1 : 0);
        meter.CreateObservableGauge("outbox.halted", () => _observed?.Current.IsHalted == true ? 1 : 0);
    }

    public string MeterName { get; }

    /// <summary>Messages the broker acknowledged.</summary>
    public Counter<long> Published { get; }

    /// <summary>Failed publication attempts, tagged by <c>category</c>.</summary>
    public Counter<long> PublishFailures { get; }

    /// <summary>Transitions into the halted state, tagged by <c>category</c>: the terminal failures an operator must resolve.</summary>
    public Counter<long> Halts { get; }

    /// <summary>The time from producing one message to its delivery report, successful or not.</summary>
    public Histogram<double> PublishDuration { get; }

    /// <summary>The duration of one relay cycle: read, publish and mark one batch.</summary>
    public Histogram<double> CycleDuration { get; }

    /// <summary>The meters of one service, created once per process.</summary>
    public static OutboxMetrics For(string meterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(meterName);
        return Instances.GetOrAdd(meterName, name => new OutboxMetrics(name));
    }

    /// <summary>The relay state the observable gauges report: the hosted relay's, set once at startup.</summary>
    public void Observe(OutboxRelayState state) => _observed = state;

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
