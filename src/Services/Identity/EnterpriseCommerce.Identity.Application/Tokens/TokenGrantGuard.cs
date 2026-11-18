using System.Globalization;
using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Domain.Security;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.Identity.Application.Tokens;

/// <summary>A code exchange or refresh request, as seen after OpenIddict validated the code or refresh token.</summary>
/// <param name="Subject">The user (<c>sub</c>).</param>
/// <param name="AuthorizationId">The authorization (session) the token belongs to.</param>
/// <param name="TokenId">The token entry ID of the presented refresh token. Required for refresh requests.</param>
/// <param name="SessionVersion">The security version recorded in the session when it was established.</param>
/// <param name="ClientId">The authenticated client, already verified to be a presenter of the token.</param>
public sealed record TokenGrantRequest(
    string Subject,
    string? AuthorizationId,
    string? TokenId,
    string? SessionVersion,
    bool IsRefresh,
    string ClientId);

/// <summary>Why a grant was denied. Every denial maps to <c>invalid_grant</c>.</summary>
public enum TokenGrantDenial
{
    None,
    AccountNotAllowed,
    StaleSession,
    MissingSecurityState,
    RefreshTokenReuse,
}

/// <summary>
/// Confirms the authoritative security state before Identity issues tokens at the code exchange or a refresh
/// (ADR-017 sections 5 to 7). Nothing is issued unless this returns <see cref="TokenGrantDenial.None"/>.
/// </summary>
/// <remarks>
/// One short transaction reads the account and the security version and, for refresh requests, inserts the
/// token entry into the redemption ledger. Of two simultaneous refreshes with the same token, only one insert
/// succeeds; the other is a reuse. A database failure surfaces as <see cref="SecurityStateUnavailableException"/>
/// and revokes nothing.
/// </remarks>
public sealed partial class TokenGrantGuard(
    IAuthoritativeSecurityTransaction transaction,
    ISessionRevocationCleanup cleanup,
    RefreshTokenReuseHandler reuseHandler,
    TimeProvider timeProvider,
    ILogger<TokenGrantGuard> logger)
{
    public async Task<TokenGrantDenial> EvaluateAsync(TokenGrantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.IsRefresh && string.IsNullOrEmpty(request.TokenId))
        {
            return TokenGrantDenial.RefreshTokenReuse;
        }

        long? sessionVersion = long.TryParse(request.SessionVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
        var now = timeProvider.GetUtcNow();

        var denial = await transaction.ExecuteAsync(
            request.IsRefresh ? "token-grant-refresh" : "token-grant-code",
            async (unit, ct) =>
            {
                var account = await unit.ReadAccountStateAsync(request.Subject, ct);
                var currentVersion = await unit.ReadSecurityVersionAsync(request.Subject, ct);

                if (currentVersion is null)
                {
                    // Fail closed without touching data whose consistency is in doubt.
                    return TransactionDecision.Rollback(TokenGrantDenial.MissingSecurityState);
                }

                var verdict = SecurityVersionPolicy.Evaluate(currentVersion, sessionVersion);
                var reason = account is not { CanSignIn: true } ? TokenGrantDenial.AccountNotAllowed
                    : verdict != SecurityVersionVerdict.Current ? TokenGrantDenial.StaleSession
                    : TokenGrantDenial.None;

                if (reason != TokenGrantDenial.None)
                {
                    if (!string.IsNullOrEmpty(request.AuthorizationId))
                    {
                        await unit.TryRevokeAuthorizationAsync(request.AuthorizationId, ct);
                    }

                    return TransactionDecision.Commit(reason);
                }

                if (request.IsRefresh && !await unit.TryRecordRefreshRedemptionAsync(request.TokenId!, request.AuthorizationId, now, ct))
                {
                    return TransactionDecision.Rollback(TokenGrantDenial.RefreshTokenReuse);
                }

                return TransactionDecision.Commit(TokenGrantDenial.None);
            },
            cancellationToken);

        switch (denial)
        {
            case TokenGrantDenial.None:
                return denial;

            case TokenGrantDenial.RefreshTokenReuse:
                await reuseHandler.HandleAsync(
                    new RefreshReuseEvent("redemption-ledger", request.ClientId, request.ClientId, request.AuthorizationId, request.Subject),
                    cancellationToken);
                break;

            case TokenGrantDenial.AccountNotAllowed or TokenGrantDenial.StaleSession when !string.IsNullOrEmpty(request.AuthorizationId):
                await cleanup.RevokeAuthorizationTokensAsync(request.AuthorizationId, cancellationToken);
                break;
        }

        LogGrantDenied(logger, request.IsRefresh ? "refresh_token" : "authorization_code", denial, request.ClientId, request.AuthorizationId, request.Subject);
        return denial;
    }

    [LoggerMessage(
        EventId = 3200,
        Level = LogLevel.Warning,
        Message = "Token grant {GrantType} denied ({Reason}) for client {ClientId}, authorization {AuthorizationId}, subject {Subject}")]
    private static partial void LogGrantDenied(
        ILogger logger,
        string grantType,
        TokenGrantDenial reason,
        string clientId,
        string? authorizationId,
        string subject);
}
