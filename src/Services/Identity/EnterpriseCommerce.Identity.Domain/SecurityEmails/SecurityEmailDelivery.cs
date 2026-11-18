namespace EnterpriseCommerce.Identity.Domain.SecurityEmails;

/// <summary>Status of one security email delivery (ADR-018 section 3). The database record is authoritative.</summary>
public enum SecurityEmailDeliveryStatus
{
    /// <summary>Recorded and waiting for the worker. No token exists yet.</summary>
    Queued,

    /// <summary>Claimed by the worker. The token is minted only after this claim.</summary>
    Dispatching,

    /// <summary>Handed over to the delivery adapter.</summary>
    Sent,

    /// <summary>The handover failed definitively. The user can request a new email.</summary>
    Failed,

    /// <summary>Never dispatched: the queue was full, or the record went stale in <see cref="Queued"/>.</summary>
    Dropped,

    /// <summary>The handover may or may not have happened: stale in <see cref="Dispatching"/>, or an unknown outcome.</summary>
    Indeterminate,
}

/// <summary>The allowed status transitions. Each is applied by a conditional single-statement update.</summary>
public static class SecurityEmailDeliveryTransitions
{
    public static bool IsAllowed(SecurityEmailDeliveryStatus from, SecurityEmailDeliveryStatus to) => (from, to) switch
    {
        (SecurityEmailDeliveryStatus.Queued, SecurityEmailDeliveryStatus.Dispatching) => true, // worker claim
        (SecurityEmailDeliveryStatus.Queued, SecurityEmailDeliveryStatus.Dropped) => true, // queue full, or stale (reconciliation)
        (SecurityEmailDeliveryStatus.Dispatching, SecurityEmailDeliveryStatus.Sent) => true,
        (SecurityEmailDeliveryStatus.Dispatching, SecurityEmailDeliveryStatus.Failed) => true,
        (SecurityEmailDeliveryStatus.Dispatching, SecurityEmailDeliveryStatus.Indeterminate) => true, // unknown outcome, or stale (reconciliation)
        _ => false,
    };

    /// <summary>Terminal statuses never change again. Recovery is a new, user-requested delivery.</summary>
    public static bool IsTerminal(SecurityEmailDeliveryStatus status) =>
        status is SecurityEmailDeliveryStatus.Sent
            or SecurityEmailDeliveryStatus.Failed
            or SecurityEmailDeliveryStatus.Dropped
            or SecurityEmailDeliveryStatus.Indeterminate;
}
