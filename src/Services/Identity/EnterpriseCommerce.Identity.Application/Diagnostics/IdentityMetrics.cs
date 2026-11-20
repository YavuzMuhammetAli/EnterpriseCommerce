using System.Diagnostics.Metrics;

namespace EnterpriseCommerce.Identity.Application.Diagnostics;

/// <summary>
/// Identity's operational metrics (meter <see cref="MeterName"/>, docs/security/IDENTITY_OPERATIONS.md). Exported once
/// the observability phase wires an exporter; tests observe them with a <see cref="MeterListener"/>.
/// </summary>
/// <remarks>
/// Every tag has a small, fixed value set. Tags never carry user, session, client or delivery identifiers, email
/// addresses, tokens, codes or any other unbounded or sensitive value.
/// </remarks>
public static class IdentityMetrics
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

    /// <summary>Deliveries handed over to the delivery adapter (tag: <c>purpose</c>).</summary>
    public static readonly Counter<long> SecurityEmailSent =
        Meter.CreateCounter<long>("identity.security_email.sent");

    /// <summary>Retention runs (tag: <c>outcome</c>: completed, skipped or failed).</summary>
    public static readonly Counter<long> RetentionRuns =
        Meter.CreateCounter<long>("identity.retention.runs");

    /// <summary>Rows deleted by retention (tag: <c>category</c>).</summary>
    public static readonly Counter<long> RetentionDeleted =
        Meter.CreateCounter<long>("identity.retention.deleted");

    /// <summary>Retention statements retried after a transient failure (tag: <c>category</c>).</summary>
    public static readonly Counter<long> RetentionRetries =
        Meter.CreateCounter<long>("identity.retention.retries");

    /// <summary>
    /// Login page credential checks (tag: <c>outcome</c>: succeeded, failed, locked_out, not_allowed, email_unconfirmed).
    /// Unknown accounts count as failed, so the metric reveals no more than the page does.
    /// </summary>
    public static readonly Counter<long> AuthenticationAttempts =
        Meter.CreateCounter<long>("identity.authentication.attempts");

    /// <summary>
    /// Token endpoint grants (tags: <c>grant_type</c>, <c>outcome</c>: issued or denied, <c>reason</c>). The reason
    /// <c>stale_session</c> is a security-version rejection.
    /// </summary>
    public static readonly Counter<long> TokenGrants =
        Meter.CreateCounter<long>("identity.token.grants");

    /// <summary>Detected refresh-token reuse (tags: <c>source</c>, <c>action</c>).</summary>
    public static readonly Counter<long> RefreshReuseDetected =
        Meter.CreateCounter<long>("identity.refresh.reuse_detected");

    /// <summary>Identity SSO cookies rejected at validation (tag: <c>reason</c>: absolute_lifetime or stale_session).</summary>
    public static readonly Counter<long> SsoSessionRejections =
        Meter.CreateCounter<long>("identity.sso.rejections");

    /// <summary>Account lifecycle operations (tags: <c>action</c>: disable or reactivate, <c>outcome</c>).</summary>
    public static readonly Counter<long> AccountLifecycle =
        Meter.CreateCounter<long>("identity.account.lifecycle");

    /// <summary>Single-use security token consumption (tags: <c>purpose</c>, <c>outcome</c>: consumed or rejected).</summary>
    public static readonly Counter<long> SecurityTokenConsumption =
        Meter.CreateCounter<long>("identity.security_token.consumption");

    /// <summary>Retired keys not loaded at startup because their retention window had passed.</summary>
    public static readonly Counter<long> KeysRetiredDropped =
        Meter.CreateCounter<long>("identity.keys.retired_dropped");

    /// <summary>Registers the gauge of configured keys (tags: <c>use</c>, <c>state</c>). Called once by the host.</summary>
    public static ObservableGauge<int> ObserveKeys(Func<IEnumerable<Measurement<int>>> observe) =>
        Meter.CreateObservableGauge("identity.keys.configured", observe);

    public static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);
}
