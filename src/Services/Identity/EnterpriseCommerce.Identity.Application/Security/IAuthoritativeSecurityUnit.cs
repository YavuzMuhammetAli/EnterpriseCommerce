using EnterpriseCommerce.Identity.Domain.Security;

namespace EnterpriseCommerce.Identity.Application.Security;

/// <summary>
/// The operations that may take part in one authoritative security transaction (ADR-017 section 8):
/// the security-version change, single-use token consumption and supersession, and a single
/// authorization state change. Account changes are added by the flows that need them.
/// Bulk OpenIddict revocation is deliberately absent: it is post-commit cleanup
/// (<see cref="ISessionRevocationCleanup"/>).
/// </summary>
public interface IAuthoritativeSecurityUnit
{
    /// <summary>Reads the user's current security version, or <see langword="null"/> when the record is missing.</summary>
    Task<long?> ReadSecurityVersionAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Raises the user's security version by one and returns the new value,
    /// or <see langword="null"/> when the record is missing (callers fail closed).
    /// </summary>
    Task<long?> IncrementSecurityVersionAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Stores a new token and supersedes the user's older unconsumed tokens of the same purpose.</summary>
    Task AddSecurityTokenAsync(AccountSecurityToken token, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically consumes the token with the given hash and purpose if it is not consumed, not superseded
    /// and not expired. Returns the owning user ID, or <see langword="null"/> when nothing was consumed.
    /// </summary>
    Task<string?> TryConsumeSecurityTokenAsync(
        string tokenHash,
        SecurityTokenPurpose purpose,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Supersedes every outstanding token of the user, whatever its purpose. Returns the number superseded.</summary>
    Task<int> SupersedeSecurityTokensAsync(string userId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes one authorization (one client session).
    /// Returns <see langword="false"/> if it does not exist or is already revoked.
    /// </summary>
    Task<bool> TryRevokeAuthorizationAsync(string authorizationId, CancellationToken cancellationToken);
}
