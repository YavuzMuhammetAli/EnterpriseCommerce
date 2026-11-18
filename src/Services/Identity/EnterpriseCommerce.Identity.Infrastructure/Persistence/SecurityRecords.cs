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
