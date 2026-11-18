using System.Diagnostics.Metrics;

namespace EnterpriseCommerce.Identity.Infrastructure.Diagnostics;

/// <summary>
/// Operational metrics for the authoritative security path (ADR-017 section 8).
/// Exported once the observability phase wires an exporter. Tests observe them with a <see cref="MeterListener"/>.
/// Tags never contain user identifiers or token material.
/// </summary>
internal static class IdentitySecurityMetrics
{
    public const string MeterName = "EnterpriseCommerce.Identity.Security";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Authoritative transactions retried after a deadlock (tag: <c>operation</c>).</summary>
    public static readonly Counter<long> TransactionDeadlockRetries =
        Meter.CreateCounter<long>("identity.security.transaction.deadlock_retries");

    /// <summary>Authoritative transactions that failed closed (tags: <c>operation</c>, <c>reason</c>). Alert on any increase.</summary>
    public static readonly Counter<long> TransactionFailures =
        Meter.CreateCounter<long>("identity.security.transaction.failures");

    /// <summary>Post-commit cleanup attempts retried after a deadlock (tag: <c>operation</c>).</summary>
    public static readonly Counter<long> CleanupDeadlockRetries =
        Meter.CreateCounter<long>("identity.security.cleanup.deadlock_retries");

    /// <summary>Post-commit cleanups that did not complete (tag: <c>operation</c>). The committed decision stands.</summary>
    public static readonly Counter<long> CleanupFailures =
        Meter.CreateCounter<long>("identity.security.cleanup.failures");

    /// <summary>Security email requests that found the in-process queue full (ADR-018 section 3).</summary>
    public static readonly Counter<long> SecurityEmailQueueSaturated =
        Meter.CreateCounter<long>("identity.security_email.queue_saturated");

    /// <summary>Deliveries that became Dropped (tag: <c>reason</c>: queue-full or stale-queued).</summary>
    public static readonly Counter<long> SecurityEmailDropped =
        Meter.CreateCounter<long>("identity.security_email.dropped");

    /// <summary>Deliveries that became Indeterminate (tag: <c>reason</c>).</summary>
    public static readonly Counter<long> SecurityEmailIndeterminate =
        Meter.CreateCounter<long>("identity.security_email.indeterminate");

    /// <summary>Deliveries that became Failed (tag: <c>reason</c>, including retries-exhausted).</summary>
    public static readonly Counter<long> SecurityEmailFailed =
        Meter.CreateCounter<long>("identity.security_email.failed");
}
