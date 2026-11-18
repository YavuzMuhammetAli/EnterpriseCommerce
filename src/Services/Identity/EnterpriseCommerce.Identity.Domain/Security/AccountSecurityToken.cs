namespace EnterpriseCommerce.Identity.Domain.Security;

/// <summary>
/// A persisted, single-use account security token (ADR-018 section 1).
/// Only the SHA-256 hash of the token is stored; the raw token never reaches this type.
/// </summary>
/// <remarks>
/// Consumption is performed by a conditional database update inside the authoritative
/// transaction, so that concurrent consumers cannot both succeed. <see cref="IsConsumableAt"/>
/// states the same rule for in-memory checks and tests.
/// </remarks>
public sealed class AccountSecurityToken
{
    /// <summary>Length of a lowercase hexadecimal SHA-256 hash.</summary>
    public const int TokenHashLength = 64;

    private AccountSecurityToken()
    {
    }

    public Guid Id { get; private set; }

    public string UserId { get; private set; } = string.Empty;

    public SecurityTokenPurpose Purpose { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    public DateTimeOffset? SupersededAt { get; private set; }

    public static AccountSecurityToken Issue(
        string userId,
        SecurityTokenPurpose purpose,
        string tokenHash,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown security token purpose.");
        }

        if (tokenHash.Length != TokenHashLength || !tokenHash.All(IsLowercaseHex))
        {
            throw new ArgumentException("The token hash must be a lowercase hexadecimal SHA-256 hash.", nameof(tokenHash));
        }

        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "The token lifetime must be positive.");
        }

        return new AccountSecurityToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Purpose = purpose,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
    }

    /// <summary>A token is consumable only once, before it expires, and only while it is not superseded.</summary>
    public bool IsConsumableAt(DateTimeOffset now) =>
        ConsumedAt is null && SupersededAt is null && now < ExpiresAt;

    private static bool IsLowercaseHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f');
}
