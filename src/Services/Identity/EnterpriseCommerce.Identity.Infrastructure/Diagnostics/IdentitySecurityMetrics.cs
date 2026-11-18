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
}
