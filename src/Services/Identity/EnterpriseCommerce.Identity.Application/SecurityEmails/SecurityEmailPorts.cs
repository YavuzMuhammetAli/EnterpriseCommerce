using EnterpriseCommerce.Identity.Domain.Accounts;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.Domain.SecurityEmails;

namespace EnterpriseCommerce.Identity.Application.SecurityEmails;

/// <summary>
/// One security email to hand over for delivery. It carries a credential (the link), so it exists only in memory,
/// is never persisted, and never renders the link.
/// </summary>
/// <param name="DeliveryId">The delivery identity. Also the idempotency key of the handover: retries reuse it.</param>
public sealed record SecurityEmailMessage(Guid DeliveryId, SecurityTokenPurpose Purpose, string RecipientEmail, Uri Link)
{
    public override string ToString() => $"SecurityEmailMessage {{ DeliveryId = {DeliveryId}, Purpose = {Purpose} }}";
}

/// <summary>
/// Hands a security email over for delivery (ADR-018 section 3). Production: the Notification integration.
/// Development only: an isolated local mail sink. Exactly one implementation is registered.
/// </summary>
public interface ISecurityEmailDispatcher
{
    /// <summary>Never throws for delivery problems; it classifies them instead.</summary>
    Task<SecurityEmailDispatchOutcome> DispatchAsync(SecurityEmailMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// The bounded in-process wake-up channel for the delivery worker. It carries opaque delivery references only.
/// The database record, not the channel, is authoritative.
/// </summary>
public interface ISecurityEmailQueue
{
    /// <summary>Returns <see langword="false"/> when the queue is full.</summary>
    bool TryEnqueue(Guid deliveryId);
}

/// <summary>A delivery that the worker has claimed, with the facts needed to send it.</summary>
public sealed record ClaimedSecurityEmail(Guid DeliveryId, string UserId, SecurityTokenPurpose Purpose, string Email, AccountSignInState Account);

/// <summary>Counts of one reconciliation run.</summary>
public sealed record SecurityEmailReconciliation(int Dropped, int Indeterminate);

/// <summary>
/// Status transitions of delivery records. Every transition is one conditional statement, so of two competing
/// writers (worker and reconciler) exactly one can win. Timestamps come from the database clock.
/// </summary>
public interface ISecurityEmailDeliveryStore
{
    /// <summary><see cref="SecurityEmailDeliveryStatus.Queued"/> to <see cref="SecurityEmailDeliveryStatus.Dispatching"/>.</summary>
    Task<bool> TryClaimAsync(Guid deliveryId, CancellationToken cancellationToken);

    /// <summary>Reads a delivery in <see cref="SecurityEmailDeliveryStatus.Dispatching"/>, or <see langword="null"/>.</summary>
    Task<ClaimedSecurityEmail?> ReadClaimedAsync(Guid deliveryId, CancellationToken cancellationToken);

    /// <summary>Counts one handover attempt of a delivery that is still dispatching.</summary>
    Task<bool> RecordAttemptAsync(Guid deliveryId, CancellationToken cancellationToken);

    /// <summary>Moves a dispatching delivery to its final status. Returns <see langword="false"/> if it is no longer dispatching.</summary>
    Task<bool> CompleteAsync(Guid deliveryId, SecurityEmailDeliveryStatus status, string? errorCategory, CancellationToken cancellationToken);

    /// <summary><see cref="SecurityEmailDeliveryStatus.Queued"/> to <see cref="SecurityEmailDeliveryStatus.Dropped"/> (queue full).</summary>
    Task<bool> MarkDroppedAsync(Guid deliveryId, string errorCategory, CancellationToken cancellationToken);

    /// <summary>
    /// Status-only reconciliation: stale Queued becomes Dropped and stale Dispatching becomes Indeterminate.
    /// Never resends and never mints a token.
    /// </summary>
    Task<SecurityEmailReconciliation> ReconcileAsync(TimeSpan staleAfter, CancellationToken cancellationToken);
}

/// <summary>Security email settings.</summary>
/// <param name="ConfirmationPage">The absolute URL of the Identity-hosted email confirmation page.</param>
/// <param name="ConfirmationTokenLifetime">ADR-018: 24 hours.</param>
/// <param name="MaxDispatchAttempts">Bounded handover attempts per delivery.</param>
/// <param name="RetryBaseDelay">Base delay between attempts (exponential).</param>
public sealed record SecurityEmailSettings(
    Uri ConfirmationPage,
    TimeSpan ConfirmationTokenLifetime,
    int MaxDispatchAttempts,
    TimeSpan RetryBaseDelay);
