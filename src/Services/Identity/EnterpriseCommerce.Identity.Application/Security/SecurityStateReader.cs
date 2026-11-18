using EnterpriseCommerce.Identity.Domain.Accounts;
using EnterpriseCommerce.Identity.Domain.Security;

namespace EnterpriseCommerce.Identity.Application.Security;

/// <summary>The authoritative security facts of a user at one point in time.</summary>
/// <param name="Account">The account state, or <see langword="null"/> when the user does not exist.</param>
/// <param name="SecurityVersion">The current security version, or <see langword="null"/> when the record is missing.</param>
public sealed record UserSecurityState(AccountSignInState? Account, long? SecurityVersion)
{
    /// <summary>Whether a session established at <paramref name="sessionVersion"/> is still valid. Fails closed.</summary>
    public bool AllowsSession(long? sessionVersion) =>
        Account is { CanSignIn: true }
        && SecurityVersionPolicy.Evaluate(SecurityVersion, sessionVersion) == SecurityVersionVerdict.Current;
}

/// <summary>
/// Reads the security version and account state for Identity cookie validation, the login page and the
/// authorization endpoint (ADR-017 section 7). A database failure surfaces as
/// <see cref="SecurityStateUnavailableException"/> (503); it never silently clears a cookie.
/// </summary>
public sealed class SecurityStateReader(IAuthoritativeSecurityTransaction transaction)
{
    public Task<UserSecurityState> ReadAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return transaction.ExecuteAsync(
            "read-security-state",
            async (unit, ct) => TransactionDecision.Rollback(new UserSecurityState(
                await unit.ReadAccountStateAsync(userId, ct),
                await unit.ReadSecurityVersionAsync(userId, ct))),
            cancellationToken);
    }
}
