using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace EnterpriseCommerce.Bff.Sessions;

/// <summary>The token set of a session. It lives only in the server-side session record, never in a browser response.</summary>
internal sealed record SessionTokens(string AccessToken, string? RefreshToken, string? IdToken, DateTimeOffset AccessTokenExpiresAt, DateTimeOffset AccessTokenIssuedAt)
{
    /// <summary>Whether the access token is within the refresh threshold: min(threshold, a third of its lifetime).</summary>
    public bool NeedsRefresh(DateTimeOffset now, TimeSpan threshold)
    {
        var lifetime = AccessTokenExpiresAt - AccessTokenIssuedAt;
        var effective = lifetime > TimeSpan.Zero && lifetime / 3 < threshold ? lifetime / 3 : threshold;
        return AccessTokenExpiresAt - now <= effective;
    }

    public bool IsExpired(DateTimeOffset now) => AccessTokenExpiresAt <= now;
}

/// <summary>Whether the session's refresh token may still be presented (ADR-016 section 4).</summary>
internal enum RefreshState
{
    /// <summary>The refresh token is unused and may be presented by the lease owner.</summary>
    Usable,

    /// <summary>
    /// A refresh outcome is unknown (timeout after sending, connection reset, 5xx, or a lease that expired without a
    /// result). The refresh token is never presented again: the session continues until the access token expires.
    /// </summary>
    Uncertain,

    /// <summary>Identity rejected the client (<c>invalid_client</c>/<c>unauthorized_client</c>): never retried.</summary>
    Rejected,
}

/// <summary>The tokens of a session together with the refresh state and the record's token version.</summary>
internal sealed record SessionTokenSnapshot(SessionTokens Tokens, RefreshState State, long Version);

/// <summary>A session loaded for a request.</summary>
internal sealed record BffSession(string RecordId, ClaimsPrincipal Principal, DateTimeOffset AbsoluteExpiresAt, bool Persistent);

/// <summary>The outcome of trying to take the per-session refresh lease.</summary>
internal enum LeaseOutcome
{
    /// <summary>The session record no longer exists.</summary>
    SessionMissing,

    /// <summary>Another request holds a live lease and is refreshing.</summary>
    HeldByOther,

    /// <summary>This request owns the lease and may present the refresh token once.</summary>
    Acquired,

    /// <summary>A previous lease expired without a result: the outcome is unknown, so the state became Uncertain.</summary>
    PreviousOutcomeUnknown,

    /// <summary>The refresh token may not be presented (Uncertain or Rejected).</summary>
    NotRefreshable,
}

internal readonly record struct LeaseResult(LeaseOutcome Outcome, long Version);

/// <summary>Session identifiers. The cookie carries a random 256-bit key; Redis and logs only ever see derived values.</summary>
internal static class SessionIds
{
    /// <summary>A new cryptographically random session key for the cookie.</summary>
    public static string NewKey() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The record ID: SHA-256 of the cookie key. The cookie key itself is never stored.</summary>
    public static string RecordId(string cookieKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(cookieKey)));

    /// <summary>A short, non-reversible reference for logs and correlation; it cannot be used to find the record.</summary>
    public static string LogReference(string recordId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("log:" + recordId)))[..12];

    /// <summary>Whether a cookie value has the shape of a session key, before it is used for any lookup.</summary>
    public static bool IsWellFormedKey(string? value) =>
        value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Claim types the BFF puts on the session principal.</summary>
internal static class BffClaims
{
    public const string Subject = "sub";
    public const string AuthenticationMethod = "amr";

    /// <summary>The session record ID (server-side only; the principal is never serialized to the browser).</summary>
    public const string SessionRecord = "bff_sid";
}
