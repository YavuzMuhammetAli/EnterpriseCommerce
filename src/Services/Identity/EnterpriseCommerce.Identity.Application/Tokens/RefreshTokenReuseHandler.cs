using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Domain.Security;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.Identity.Application.Tokens;

/// <summary>Client facts the reuse policy depends on.</summary>
/// <param name="AdminConsoleClientId">The admin console BFF client. Reuse there ends every session of the user.</param>
public sealed record RefreshReuseSettings(string AdminConsoleClientId);

/// <summary>A detected refresh-token reuse. Never carries token material.</summary>
/// <param name="Source">How the reuse was detected: an already redeemed token, or a duplicate in the redemption ledger.</param>
public sealed record RefreshReuseEvent(
    string Source,
    string? TokenClientId,
    string? PresentingClientId,
    string? AuthorizationId,
    string? Subject);

/// <summary>
/// Applies the refresh-token reuse policy (ADR-017 section 5): presenter check first, then either the affected
/// session or, for the admin console, every session of the user. The revocation authority (authorization status
/// and security version) commits in one short transaction. OpenIddict bulk revocation follows as cleanup.
/// </summary>
public sealed partial class RefreshTokenReuseHandler(
    IAuthoritativeSecurityTransaction transaction,
    ISessionRevocationCleanup cleanup,
    RefreshReuseSettings settings,
    ILogger<RefreshTokenReuseHandler> logger)
{
    public async Task<RefreshReuseAction> HandleAsync(RefreshReuseEvent reuse, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reuse);

        var action = RefreshReusePolicy.Decide(reuse.TokenClientId, reuse.PresentingClientId, settings.AdminConsoleClientId);

        switch (action)
        {
            case RefreshReuseAction.RejectPresenterMismatch:
                LogPresenterMismatch(logger, reuse.Source, reuse.TokenClientId, reuse.PresentingClientId, reuse.AuthorizationId);
                return action;

            case RefreshReuseAction.RevokeSession when !string.IsNullOrEmpty(reuse.AuthorizationId):
                await transaction.ExecuteAsync(
                    "refresh-reuse-revoke-session",
                    async (unit, ct) => TransactionDecision.Commit(await unit.TryRevokeAuthorizationAsync(reuse.AuthorizationId, ct)),
                    cancellationToken);
                await cleanup.RevokeAuthorizationTokensAsync(reuse.AuthorizationId, cancellationToken);
                break;

            case RefreshReuseAction.RevokeAllSessions when !string.IsNullOrEmpty(reuse.Subject):
                await transaction.ExecuteAsync(
                    "refresh-reuse-revoke-all",
                    async (unit, ct) =>
                    {
                        var version = await unit.IncrementSecurityVersionAsync(reuse.Subject, ct);
                        if (!string.IsNullOrEmpty(reuse.AuthorizationId))
                        {
                            await unit.TryRevokeAuthorizationAsync(reuse.AuthorizationId, ct);
                        }

                        return TransactionDecision.Commit(version);
                    },
                    cancellationToken);
                await cleanup.RevokeAllForSubjectAsync(reuse.Subject, cancellationToken);
                break;

            default:
                // No authorization or subject to act on. The native rejection still applies.
                break;
        }

        LogReuseDetected(logger, reuse.Source, action, reuse.TokenClientId, reuse.AuthorizationId, reuse.Subject);
        return action;
    }

    [LoggerMessage(
        EventId = 3100,
        Level = LogLevel.Warning,
        Message = "Security event RefreshTokenReuseDetected: source {Source}, action {Action}, client {ClientId}, authorization {AuthorizationId}, subject {Subject}")]
    private static partial void LogReuseDetected(
        ILogger logger,
        string source,
        RefreshReuseAction action,
        string? clientId,
        string? authorizationId,
        string? subject);

    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Warning,
        Message = "Security event RefreshTokenPresenterMismatch: source {Source}, token client {TokenClientId}, presenting client {PresentingClientId}, authorization {AuthorizationId}; rejected without revocation")]
    private static partial void LogPresenterMismatch(
        ILogger logger,
        string source,
        string? tokenClientId,
        string? presentingClientId,
        string? authorizationId);
}
