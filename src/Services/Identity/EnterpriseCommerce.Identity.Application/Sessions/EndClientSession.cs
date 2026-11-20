using EnterpriseCommerce.Identity.Application.Security;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.Identity.Application.Sessions;

/// <summary>
/// Ends one client session (one OpenIddict authorization) at logout (PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.5.1):
/// when a client revokes its refresh token, and at end-session for the session named by the ID token hint.
/// </summary>
/// <remarks>
/// The authorization status is the revocation authority and commits in its own short transaction. The authorization's
/// tokens are then revoked as idempotent cleanup. If the status change cannot be committed, a
/// <see cref="SecurityStateUnavailableException"/> is thrown: logout never reports success without it. Ending a session
/// that is already ended succeeds.
/// </remarks>
public sealed partial class EndClientSessionHandler(
    IAuthoritativeSecurityTransaction transaction,
    ISessionRevocationCleanup cleanup,
    ILogger<EndClientSessionHandler> logger)
{
    /// <param name="authorizationId">The authorization to end. Callers resolve it from a validated token only.</param>
    /// <param name="source">Where the logout came from (<c>revocation</c> or <c>end-session</c>), for the audit event.</param>
    public async Task HandleAsync(string authorizationId, string source, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var revoked = await transaction.ExecuteAsync(
            "end-client-session",
            async (unit, ct) => TransactionDecision.Commit(await unit.TryRevokeAuthorizationAsync(authorizationId, ct)),
            cancellationToken);

        var cleanupOutcome = await cleanup.RevokeAuthorizationTokensAsync(authorizationId, cancellationToken);
        LogSessionEnded(logger, source, authorizationId, revoked, cleanupOutcome);
    }

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Information,
        Message = "Security event SessionEnded: source {Source}, authorization {AuthorizationId}, newly revoked {Revoked}; cleanup {CleanupOutcome}")]
    private static partial void LogSessionEnded(ILogger logger, string source, string authorizationId, bool revoked, CleanupOutcome cleanupOutcome);
}
