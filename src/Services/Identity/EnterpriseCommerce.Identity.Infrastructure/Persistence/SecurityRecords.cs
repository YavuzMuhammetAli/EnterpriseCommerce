namespace EnterpriseCommerce.Identity.Infrastructure.Persistence;

/// <summary>
/// The dedicated per-user security version (ADR-017 section 7). One row per user, created with the account.
/// A missing row fails closed.
/// </summary>
internal sealed class SecurityVersionRecord
{
    public string UserId { get; set; } = string.Empty;

    public long Version { get; set; }
}

/// <summary>
/// One row per redeemed refresh token entry (ADR-017 section 5). The primary key on the token entry ID makes a
/// second redemption of the same refresh token fail with a duplicate key, which is treated as reuse.
/// </summary>
internal sealed class RefreshTokenRedemption
{
    public string TokenId { get; set; } = string.Empty;

    public string? AuthorizationId { get; set; }

    public DateTimeOffset RedeemedAt { get; set; }
}

/// <summary>
/// One security email delivery (ADR-018 section 3). It never holds the raw token, the link or the email body:
/// only the delivery state and, once minted, the ID of the hashed token record. Timestamps are UTC from the
/// database clock (<c>SYSUTCDATETIME()</c>).
/// </summary>
internal sealed class SecurityEmailDeliveryRecord
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public Domain.Security.SecurityTokenPurpose Purpose { get; set; }

    public Domain.SecurityEmails.SecurityEmailDeliveryStatus Status { get; set; }

    public int AttemptCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ClaimedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>A coarse, non-sensitive reason for a final failure (for example <c>queue-full</c>).</summary>
    public string? ErrorCategory { get; set; }

    /// <summary>The hashed token record minted for this delivery after the claim.</summary>
    public Guid? SecurityTokenId { get; set; }
}
