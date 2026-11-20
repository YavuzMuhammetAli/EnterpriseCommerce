using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using EnterpriseCommerce.Bff.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace EnterpriseCommerce.Bff.Sessions;

/// <summary>The session store could not be read or written safely. Requests fail closed with 503.</summary>
internal sealed class SessionStoreUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Server-side sessions in the dedicated session Redis (ADR-016 section 3). One hash per session under the
/// deployment's key prefix:
/// <list type="bullet">
/// <item><c>client</c>: the OAuth client ID; a record with another client ID is rejected.</item>
/// <item><c>principal</c>, <c>tokens</c>: Data Protection-protected; tokens never leave the server.</item>
/// <item><c>tver</c>, <c>state</c>, <c>lease</c>, <c>leaseExp</c>: the refresh coordination (ADR-016 section 4).</item>
/// <item><c>abs</c>, <c>idle</c>, <c>persistent</c>: the expiry rules; the key's TTL is min(absolute, now + idle).</item>
/// </list>
/// Every multi-step mutation is one Lua script, so it is atomic. The scripts use the Redis clock for TTLs and leases,
/// so the instances of one deployment agree on them.
/// </summary>
internal sealed class RedisSessionStore
{
    private const string NowLua = "local t = redis.call('TIME') local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000) ";

    private const string CreateScript = NowLua + """
        if redis.call('EXISTS', KEYS[1]) == 1 then return -1 end
        local abs = now + tonumber(ARGV[4])
        redis.call('HSET', KEYS[1], 'client', ARGV[1], 'principal', ARGV[2], 'tokens', ARGV[3], 'tver', 1, 'state', 'usable', 'abs', abs, 'idle', ARGV[5], 'persistent', ARGV[6])
        redis.call('PEXPIREAT', KEYS[1], math.min(abs, now + tonumber(ARGV[5])))
        return abs
        """;

    // Loads a session and slides its idle expiry, never past the absolute deadline.
    private const string LoadScript = NowLua + """
        local v = redis.call('HMGET', KEYS[1], 'client', 'principal', 'abs', 'idle', 'persistent')
        if not v[1] then return nil end
        local abs = tonumber(v[3])
        if abs <= now then redis.call('DEL', KEYS[1]) return nil end
        redis.call('PEXPIREAT', KEYS[1], math.min(abs, now + tonumber(v[4])))
        return { v[1], v[2], v[3], v[5] }
        """;

    // Takes the refresh lease only if none is live. A lease that expired without a result means the refresh outcome
    // is unknown: the state becomes 'uncertain' and nobody presents that refresh token again.
    private const string AcquireScript = NowLua + """
        if redis.call('EXISTS', KEYS[1]) == 0 then return { -1, 0 } end
        local v = redis.call('HMGET', KEYS[1], 'lease', 'leaseExp', 'tver', 'state')
        local tver = tonumber(v[3])
        if v[4] ~= 'usable' then return { 3, tver } end
        if v[1] then
          if tonumber(v[2]) > now then return { 0, tver } end
          redis.call('HSET', KEYS[1], 'state', 'uncertain')
          redis.call('HDEL', KEYS[1], 'lease', 'leaseExp')
          redis.call('HINCRBY', KEYS[1], 'tver', 1)
          return { 2, tver + 1 }
        end
        redis.call('HSET', KEYS[1], 'lease', ARGV[1], 'leaseExp', now + tonumber(ARGV[2]))
        return { 1, tver }
        """;

    // Stores a refresh result only while the caller still owns the lease (compare-and-set), then releases it.
    private const string CommitScript = """
        if redis.call('HGET', KEYS[1], 'lease') ~= ARGV[1] then return 0 end
        redis.call('HSET', KEYS[1], 'tokens', ARGV[2], 'state', ARGV[3])
        redis.call('HINCRBY', KEYS[1], 'tver', 1)
        redis.call('HDEL', KEYS[1], 'lease', 'leaseExp')
        return 1
        """;

    private const string MarkScript = """
        if redis.call('HGET', KEYS[1], 'lease') ~= ARGV[1] then return 0 end
        redis.call('HSET', KEYS[1], 'state', ARGV[2])
        redis.call('HINCRBY', KEYS[1], 'tver', 1)
        redis.call('HDEL', KEYS[1], 'lease', 'leaseExp')
        return 1
        """;

    // Releases only the caller's own lease: never an unconditional delete.
    private const string ReleaseScript = """
        if redis.call('HGET', KEYS[1], 'lease') ~= ARGV[1] then return 0 end
        redis.call('HDEL', KEYS[1], 'lease', 'leaseExp')
        return 1
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IRedisConnection _redis;
    private readonly BffSettings _settings;
    private readonly IDataProtector _principalProtector;
    private readonly IDataProtector _tokenProtector;

    public RedisSessionStore(IRedisConnection redis, IOptions<BffSettings> settings, IDataProtectionProvider dataProtection)
    {
        _redis = redis;
        _settings = settings.Value;
        var root = dataProtection.CreateProtector("EnterpriseCommerce.Bff.Session.v1", _settings.ClientId);
        _principalProtector = root.CreateProtector("principal");
        _tokenProtector = root.CreateProtector("tokens");
    }

    public RedisKey KeyOf(string recordId) => $"{_settings.KeyPrefix}:s:{recordId}";

    /// <summary>Creates a new session under a new random key. Returns the cookie key and the session.</summary>
    public async Task<(string CookieKey, BffSession Session)> CreateAsync(ClaimsPrincipal principal, SessionTokens tokens, bool persistent, CancellationToken cancellationToken)
    {
        var cookieKey = SessionIds.NewKey();
        var recordId = SessionIds.RecordId(cookieKey);
        var sessionPrincipal = WithRecordId(principal, recordId);

        var result = await RunAsync(db => db.ScriptEvaluateAsync(CreateScript, [KeyOf(recordId)],
        [
            _settings.ClientId,
            ProtectPrincipal(sessionPrincipal),
            ProtectTokens(tokens),
            (long)_settings.Session.AbsoluteLifetime.TotalMilliseconds,
            (long)_settings.Session.IdleTimeout.TotalMilliseconds,
            persistent ? "1" : "0",
        ]), cancellationToken);

        var absolute = (long)result;
        if (absolute < 0)
        {
            throw new InvalidOperationException("Session key collision.");
        }

        return (cookieKey, new BffSession(recordId, sessionPrincipal, DateTimeOffset.FromUnixTimeMilliseconds(absolute), persistent));
    }

    /// <summary>
    /// Loads the session of a cookie key and slides its idle expiry. Null when the session does not exist, has expired,
    /// belongs to another client or cannot be decrypted. Throws <see cref="SessionStoreUnavailableException"/> when
    /// Redis cannot answer: an unreadable store is never treated as "no session" silently.
    /// </summary>
    public async Task<(BffSession? Session, SessionLoadFailure Failure)> LoadAsync(string cookieKey, CancellationToken cancellationToken)
    {
        var recordId = SessionIds.RecordId(cookieKey);
        var result = await RunAsync(db => db.ScriptEvaluateAsync(LoadScript, [KeyOf(recordId)]), cancellationToken);
        if (result.IsNull)
        {
            return (null, SessionLoadFailure.NotFound);
        }

        var fields = (RedisResult[])result!;
        if (!string.Equals((string?)fields[0], _settings.ClientId, StringComparison.Ordinal))
        {
            return (null, SessionLoadFailure.OtherClient);
        }

        var principal = UnprotectPrincipal((string?)fields[1]);
        if (principal is null || principal.FindFirst(BffClaims.SessionRecord)?.Value != recordId)
        {
            return (null, SessionLoadFailure.Unreadable);
        }

        var absolute = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse((string)fields[2]!, CultureInfo.InvariantCulture));
        return (new BffSession(recordId, principal, absolute, (string?)fields[3] == "1"), SessionLoadFailure.None);
    }

    public async Task<SessionTokenSnapshot?> GetTokensAsync(string recordId, CancellationToken cancellationToken)
    {
        var values = await RunAsync(db => db.HashGetAsync(KeyOf(recordId), ["tokens", "state", "tver"]), cancellationToken);
        if (values[0].IsNull)
        {
            return null;
        }

        var tokens = UnprotectTokens(values[0]!);
        if (tokens is null)
        {
            return null;
        }

        var state = (string?)values[1] switch
        {
            "usable" => RefreshState.Usable,
            "rejected" => RefreshState.Rejected,
            _ => RefreshState.Uncertain,
        };
        return new SessionTokenSnapshot(tokens, state, (long)values[2]);
    }

    public async Task<LeaseResult> TryAcquireLeaseAsync(string recordId, string owner, CancellationToken cancellationToken)
    {
        var result = (RedisResult[])(await RunAsync(db => db.ScriptEvaluateAsync(AcquireScript, [KeyOf(recordId)],
            [owner, (long)_settings.Refresh.LeaseDuration.TotalMilliseconds]), cancellationToken))!;
        var outcome = (int)result[0] switch
        {
            -1 => LeaseOutcome.SessionMissing,
            0 => LeaseOutcome.HeldByOther,
            1 => LeaseOutcome.Acquired,
            2 => LeaseOutcome.PreviousOutcomeUnknown,
            _ => LeaseOutcome.NotRefreshable,
        };
        return new LeaseResult(outcome, (long)result[1]);
    }

    /// <summary>Stores new tokens if this owner still holds the lease. False means the result was not stored.</summary>
    public async Task<bool> CommitAsync(string recordId, string owner, SessionTokens tokens, CancellationToken cancellationToken) =>
        (int)await RunAsync(db => db.ScriptEvaluateAsync(CommitScript, [KeyOf(recordId)], [owner, ProtectTokens(tokens), "usable"]), cancellationToken) == 1;

    /// <summary>Records that the refresh token may not be presented again, if this owner still holds the lease.</summary>
    public async Task<bool> MarkAsync(string recordId, string owner, RefreshState state, CancellationToken cancellationToken) =>
        (int)await RunAsync(db => db.ScriptEvaluateAsync(MarkScript, [KeyOf(recordId)],
            [owner, state == RefreshState.Rejected ? "rejected" : "uncertain"]), cancellationToken) == 1;

    public async Task ReleaseAsync(string recordId, string owner, CancellationToken cancellationToken) =>
        await RunAsync(db => db.ScriptEvaluateAsync(ReleaseScript, [KeyOf(recordId)], [owner]), cancellationToken);

    /// <summary>Deletes a session. Idempotent.</summary>
    public async Task DeleteAsync(string recordId, CancellationToken cancellationToken) =>
        await RunAsync(db => db.KeyDeleteAsync(KeyOf(recordId)), cancellationToken);

    private async Task<T> RunAsync<T>(Func<IDatabase, Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await operation(_redis.Database).WaitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException or ObjectDisposedException)
        {
            throw new SessionStoreUnavailableException("The session store is unavailable.", exception);
        }
    }

    private static ClaimsPrincipal WithRecordId(ClaimsPrincipal principal, string recordId)
    {
        var claims = principal.Claims
            .Where(claim => claim.Type is BffClaims.Subject or BffClaims.AuthenticationMethod)
            .Select(claim => new Claim(claim.Type, claim.Value))
            .Append(new Claim(BffClaims.SessionRecord, recordId));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, BffSessionDefaults.Scheme, BffClaims.Subject, null));
    }

    private string ProtectPrincipal(ClaimsPrincipal principal) =>
        _principalProtector.Protect(JsonSerializer.Serialize(principal.Claims.Select(claim => new[] { claim.Type, claim.Value }), Json));

    private ClaimsPrincipal? UnprotectPrincipal(string? protectedValue)
    {
        if (protectedValue is null)
        {
            return null;
        }

        try
        {
            var pairs = JsonSerializer.Deserialize<string[][]>(_principalProtector.Unprotect(protectedValue), Json) ?? [];
            var claims = pairs.Where(pair => pair.Length == 2).Select(pair => new Claim(pair[0], pair[1]));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, BffSessionDefaults.Scheme, BffClaims.Subject, null));
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }

    private string ProtectTokens(SessionTokens tokens) => _tokenProtector.Protect(JsonSerializer.Serialize(tokens, Json));

    private SessionTokens? UnprotectTokens(string protectedValue)
    {
        try
        {
            return JsonSerializer.Deserialize<SessionTokens>(_tokenProtector.Unprotect(protectedValue), Json);
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }
}

internal enum SessionLoadFailure
{
    None,
    NotFound,
    OtherClient,
    Unreadable,
}
