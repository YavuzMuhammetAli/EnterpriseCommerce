using EnterpriseCommerce.Bff.Configuration;
using EnterpriseCommerce.Bff.Logging;
using EnterpriseCommerce.Bff.Protocol;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Bff.Sessions;

internal enum AccessTokenStatus
{
    /// <summary>A usable access token.</summary>
    Valid,

    /// <summary>The session ended (or never existed); the browser must sign in again (silently first).</summary>
    SessionEnded,

    /// <summary>No usable token right now (store unavailable, another refresh still running): 503, retry later.</summary>
    Unavailable,
}

internal readonly record struct AccessTokenResult(AccessTokenStatus Status, string? AccessToken = null)
{
    public static AccessTokenResult Valid(string token) => new(AccessTokenStatus.Valid, token);

    public static readonly AccessTokenResult Ended = new(AccessTokenStatus.SessionEnded);

    public static readonly AccessTokenResult Unavailable = new(AccessTokenStatus.Unavailable);
}

/// <summary>
/// Server-side access-token lifecycle (ADR-016 section 4, ADR-017 sections 5 and 6):
/// <list type="bullet">
/// <item>Refresh only near expiry, never on every request.</item>
/// <item>At most one refresh per session across all instances: a lease taken atomically in the session record; only
/// the lease owner presents the refresh token, and only the owner may store the result (compare-and-set).</item>
/// <item>An unknown outcome is never retried with the same refresh token: the session continues until its access
/// token expires and then ends. Retrying is allowed only when the request provably was not sent.</item>
/// <item><c>invalid_grant</c> ends the session at once (revocation, reuse, password change, logout-all).</item>
/// </list>
/// </summary>
internal sealed class RefreshCoordinator(
    RedisSessionStore store,
    IdentityProtocolClient identity,
    IOptions<BffSettings> settings,
    TimeProvider time,
    ILogger<RefreshCoordinator> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly BffSettings _settings = settings.Value;

    public async Task<AccessTokenResult> GetAccessTokenAsync(BffSession session, CancellationToken cancellationToken)
    {
        var reference = SessionIds.LogReference(session.RecordId);
        var snapshot = await store.GetTokensAsync(session.RecordId, cancellationToken);
        if (snapshot is null)
        {
            return AccessTokenResult.Ended;
        }

        if (!snapshot.Tokens.NeedsRefresh(time.GetUtcNow(), _settings.Refresh.Threshold))
        {
            return AccessTokenResult.Valid(snapshot.Tokens.AccessToken);
        }

        if (snapshot.State != RefreshState.Usable || snapshot.Tokens.RefreshToken is null)
        {
            return await ValidOrEndAsync(session.RecordId, snapshot.Tokens, reference);
        }

        var owner = Guid.NewGuid().ToString("N");
        var lease = await store.TryAcquireLeaseAsync(session.RecordId, owner, cancellationToken);
        switch (lease.Outcome)
        {
            case LeaseOutcome.SessionMissing:
                return AccessTokenResult.Ended;

            case LeaseOutcome.PreviousOutcomeUnknown:
                BffLog.RefreshOutcomeUncertain(logger, _settings.Deployment, reference, "lease_expired");
                return await ValidOrEndAsync(session.RecordId, snapshot.Tokens, reference);

            case LeaseOutcome.NotRefreshable:
                return await ValidOrEndAsync(session.RecordId, snapshot.Tokens, reference);

            case LeaseOutcome.HeldByOther:
                return await WaitForOtherRefreshAsync(session.RecordId, lease.Version, snapshot.Tokens, cancellationToken);

            case LeaseOutcome.Acquired:
                return await RefreshAsync(session.RecordId, owner, snapshot.Tokens, reference);

            default:
                throw new InvalidOperationException("Unexpected lease outcome.");
        }
    }

    private async Task<AccessTokenResult> RefreshAsync(string recordId, string owner, SessionTokens current, string reference)
    {
        // The refresh is not tied to the browser request: an aborted browser request must not turn a refresh that
        // reached Identity into an unknown outcome. Store writes after the call are not cancellable for the same reason.
        var result = await identity.RefreshAsync(current.RefreshToken!, CancellationToken.None);
        switch (result.Outcome)
        {
            case RefreshOutcome.Success:
                if (await store.CommitAsync(recordId, owner, result.Tokens!, CancellationToken.None))
                {
                    BffLog.RefreshSucceeded(logger, _settings.Deployment, reference);
                    return AccessTokenResult.Valid(result.Tokens!.AccessToken);
                }

                // The lease was lost meanwhile: the new tokens cannot be stored, the old refresh token is spent.
                BffLog.RefreshOutcomeUncertain(logger, _settings.Deployment, reference, "lease_lost");
                return await ValidOrEndAsync(recordId, current, reference);

            case RefreshOutcome.InvalidGrant:
                BffLog.RefreshRejected(logger, _settings.Deployment, reference);
                await store.DeleteAsync(recordId, CancellationToken.None);
                BffLog.SessionDeleted(logger, _settings.Deployment, reference);
                return AccessTokenResult.Ended;

            case RefreshOutcome.ClientRejected:
                BffLog.RefreshClientRejected(logger, _settings.Deployment, reference, result.Error ?? "unknown");
                await store.MarkAsync(recordId, owner, RefreshState.Rejected, CancellationToken.None);
                return await ValidOrEndAsync(recordId, current, reference);

            case RefreshOutcome.NotSent:
                await store.ReleaseAsync(recordId, owner, CancellationToken.None);
                return current.IsExpired(time.GetUtcNow()) ? AccessTokenResult.Unavailable : AccessTokenResult.Valid(current.AccessToken);

            default:
                BffLog.RefreshOutcomeUncertain(logger, _settings.Deployment, reference, result.Error ?? "unknown");
                await store.MarkAsync(recordId, owner, RefreshState.Uncertain, CancellationToken.None);
                return await ValidOrEndAsync(recordId, current, reference);
        }
    }

    private async Task<AccessTokenResult> WaitForOtherRefreshAsync(string recordId, long version, SessionTokens current, CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + _settings.Refresh.WaitForOtherRefresh;
        while (time.GetUtcNow() < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken);
            var snapshot = await store.GetTokensAsync(recordId, cancellationToken);
            if (snapshot is null)
            {
                return AccessTokenResult.Ended;
            }

            if (snapshot.Version != version)
            {
                // Another request finished: use what it stored (new tokens, or the old ones if it failed).
                return snapshot.Tokens.IsExpired(time.GetUtcNow())
                    ? (snapshot.State == RefreshState.Usable ? AccessTokenResult.Unavailable : await ValidOrEndAsync(recordId, snapshot.Tokens, SessionIds.LogReference(recordId)))
                    : AccessTokenResult.Valid(snapshot.Tokens.AccessToken);
            }
        }

        return current.IsExpired(time.GetUtcNow()) ? AccessTokenResult.Unavailable : AccessTokenResult.Valid(current.AccessToken);
    }

    /// <summary>The access token while it is still valid; once it has expired, the session ends.</summary>
    private async Task<AccessTokenResult> ValidOrEndAsync(string recordId, SessionTokens tokens, string reference)
    {
        if (!tokens.IsExpired(time.GetUtcNow()))
        {
            return AccessTokenResult.Valid(tokens.AccessToken);
        }

        await store.DeleteAsync(recordId, CancellationToken.None);
        BffLog.SessionEndedWithoutRefresh(logger, _settings.Deployment, reference);
        return AccessTokenResult.Ended;
    }
}
