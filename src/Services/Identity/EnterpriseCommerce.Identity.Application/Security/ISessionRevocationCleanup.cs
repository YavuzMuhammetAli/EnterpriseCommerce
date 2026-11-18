namespace EnterpriseCommerce.Identity.Application.Security;

/// <summary>Result of a post-commit cleanup step.</summary>
public enum CleanupOutcome
{
    Completed,
    Failed,
}

/// <summary>
/// Post-commit OpenIddict bulk revocation (ADR-017 section 8). Runs only after the authoritative transaction
/// has committed, is idempotent, retries its own deadlocks separately, and reports a failure instead of
/// throwing: a failed cleanup never reverses or misrepresents the committed security decision.
/// </summary>
public interface ISessionRevocationCleanup
{
    /// <summary>Revokes every authorization and token of the subject.</summary>
    Task<CleanupOutcome> RevokeAllForSubjectAsync(string subject, CancellationToken cancellationToken);

    /// <summary>Revokes every token of one authorization.</summary>
    Task<CleanupOutcome> RevokeAuthorizationTokensAsync(string authorizationId, CancellationToken cancellationToken);
}
