using EnterpriseCommerce.Identity.Domain.Accounts;
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
    /// <summary>Reads the account facts that decide sign-in, or <see langword="null"/> when the user does not exist.</summary>
    Task<AccountSignInState?> ReadAccountStateAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the account facts that decide an email verification check (status, confirmation and the normalized
    /// address), or <see langword="null"/> when the user does not exist. Read only.
    /// </summary>
    Task<Accounts.AccountEmailState?> ReadAccountEmailStateAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Records the redemption of a refresh token entry in the redemption ledger (ADR-017 section 5).
    /// Returns <see langword="false"/> when the entry was already recorded, which means the token is being reused.
    /// </summary>
    Task<bool> TryRecordRefreshRedemptionAsync(string tokenId, string? authorizationId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Reads the user's current security version, or <see langword="null"/> when the record is missing.</summary>
    Task<long?> ReadSecurityVersionAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the user's current security version and keeps a shared lock on it until the transaction ends, or returns
    /// <see langword="null"/> when the record is missing. A decision based on the version (token issuance) then commits
    /// before any concurrent increment can: the increment waits for it. Concurrent readers do not block each other.
    /// </summary>
    Task<long?> ReadSecurityVersionForDecisionAsync(string userId, CancellationToken cancellationToken);

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

    /// <summary>
    /// Supersedes the user's outstanding tokens of one purpose, or of every purpose when <paramref name="purpose"/> is
    /// <see langword="null"/>. Returns the number superseded.
    /// </summary>
    Task<int> SupersedeSecurityTokensAsync(string userId, SecurityTokenPurpose? purpose, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Creates an account with an unconfirmed email, applying the ASP.NET Core Identity password policy and the unique
    /// normalized email, together with its security-version record. Both commit or neither does.
    /// </summary>
    Task<AccountCreation> CreateAccountAsync(string email, string password, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Finds an account by its normalized email, or <see langword="null"/>.</summary>
    Task<AccountByEmail?> FindAccountByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    /// <summary>Marks the email of an active account as confirmed. Returns <see langword="false"/> when no row qualified.</summary>
    Task<bool> ConfirmEmailAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the password hash of an active account in one conditional update, rotating the ASP.NET Core Identity
    /// security stamp and clearing the lockout state. When <paramref name="expectedCurrentHash"/> is given, the update
    /// applies only while the stored hash still equals it, so of two concurrent changes exactly one wins.
    /// Returns <see langword="false"/> when no row qualified.
    /// </summary>
    Task<bool> TrySetPasswordHashAsync(string userId, string newPasswordHash, string? expectedCurrentHash, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the account's security-version row until the transaction ends, serializing security email requests of
    /// one account across Identity instances. Returns the database clock, or <see langword="null"/> when the record is missing.
    /// </summary>
    Task<DateTime?> LockAccountForSecurityEmailAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Creation times (database clock) of the account's deliveries of one purpose since <paramref name="since"/>.</summary>
    Task<IReadOnlyList<DateTime>> ReadSecurityEmailRequestTimesAsync(string userId, SecurityTokenPurpose purpose, DateTime since, CancellationToken cancellationToken);

    /// <summary>Records a new <c>Queued</c> delivery. No token exists until the worker claims it.</summary>
    Task<Guid> CreateSecurityEmailDeliveryAsync(string userId, SecurityTokenPurpose purpose, CancellationToken cancellationToken);

    /// <summary>Links a minted token to a delivery that is still dispatching. Returns <see langword="false"/> otherwise.</summary>
    Task<bool> LinkSecurityEmailTokenAsync(Guid deliveryId, Guid tokenId, CancellationToken cancellationToken);

    /// <summary>
    /// Changes the account status from <paramref name="from"/> to <paramref name="to"/> in one conditional update that
    /// applies only while the status is still <paramref name="from"/>, recording the reason, the actor reference and the
    /// database time. Disabling also rotates the ASP.NET Core Identity security stamp. Returns <see langword="false"/>
    /// when no row qualified (unknown account, or the status is already different).
    /// </summary>
    Task<bool> TryChangeAccountStatusAsync(
        string userId,
        AccountStatus from,
        AccountStatus to,
        AccountStatusReason reason,
        string actorReference,
        CancellationToken cancellationToken);

    /// <summary>
    /// Revokes one authorization (one client session).
    /// Returns <see langword="false"/> if it does not exist or is already revoked.
    /// </summary>
    Task<bool> TryRevokeAuthorizationAsync(string authorizationId, CancellationToken cancellationToken);
}

/// <summary>Result of <see cref="IAuthoritativeSecurityUnit.CreateAccountAsync"/>.</summary>
public enum AccountCreationStatus
{
    Created,
    DuplicateEmail,
    Rejected,
}

/// <summary>Result of an account creation. <paramref name="UserId"/> is set only when created.</summary>
public sealed record AccountCreation(AccountCreationStatus Status, string? UserId);

/// <summary>An account found by email.</summary>
public sealed record AccountByEmail(string UserId, AccountSignInState State);
