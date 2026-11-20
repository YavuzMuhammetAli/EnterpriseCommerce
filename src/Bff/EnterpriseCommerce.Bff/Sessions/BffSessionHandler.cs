using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using EnterpriseCommerce.Bff.Configuration;
using EnterpriseCommerce.Bff.Logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Bff.Sessions;

internal static class BffSessionDefaults
{
    public const string Scheme = "bff-session";
}

internal sealed class BffSessionOptions : AuthenticationSchemeOptions;

/// <summary>The session of the current request, once authenticated.</summary>
internal sealed record BffSessionFeature(BffSession Session);

/// <summary>
/// The browser session (ADR-016 sections 3 and 5). The cookie carries only a random 256-bit key; everything else,
/// including every token, stays in the session Redis.
/// <list type="bullet">
/// <item>Sign-in always creates a new key and deletes the session the request arrived with, so a session ID planted
/// before login is never promoted (session fixation) and the ID rotates on every authentication.</item>
/// <item>Sign-out deletes the record first; the cookie is expired only after that succeeded.</item>
/// <item>An unreadable store throws (503 upstream): it is never read as "anonymous".</item>
/// </list>
/// </summary>
internal sealed class BffSessionHandler(
    IOptionsMonitor<BffSessionOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    RedisSessionStore store,
    IOptions<BffSettings> settings,
    BffCookieNames cookieNames)
    : SignInAuthenticationHandler<BffSessionOptions>(options, loggerFactory, encoder)
{
    public const string LoginHint = "BffSession login=\"" + BffPaths.Login + "\"";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.Features.Get<BffSessionFeature>() is { } existing)
        {
            return AuthenticateResult.Success(Ticket(existing.Session));
        }

        var cookieKey = Request.Cookies[cookieNames.Session];
        if (cookieKey is null)
        {
            return AuthenticateResult.NoResult();
        }

        if (!SessionIds.IsWellFormedKey(cookieKey))
        {
            ExpireCookie();
            return AuthenticateResult.Fail("Malformed session cookie.");
        }

        var (session, failure) = await store.LoadAsync(cookieKey, Context.RequestAborted);
        if (session is null)
        {
            BffLog.SessionNotFound(Logger, settings.Value.Deployment, failure.ToString());
            ExpireCookie();
            return AuthenticateResult.Fail("No session.");
        }

        Context.Features.Set(new BffSessionFeature(session));
        return AuthenticateResult.Success(Ticket(session));
    }

    protected override async Task HandleSignInAsync(ClaimsPrincipal user, AuthenticationProperties? properties)
    {
        properties ??= new AuthenticationProperties();
        var tokens = ExtractTokens(properties);

        // Session fixation: whatever session the request arrived with is deleted, never upgraded.
        var previousKey = Request.Cookies[cookieNames.Session];
        if (SessionIds.IsWellFormedKey(previousKey))
        {
            await store.DeleteAsync(SessionIds.RecordId(previousKey!), Context.RequestAborted);
        }

        var persistent = properties.IsPersistent && settings.Value.Session.AllowPersistent;
        var (cookieKey, session) = await store.CreateAsync(user, tokens, persistent, Context.RequestAborted);

        Response.Cookies.Append(cookieNames.Session, cookieKey, SessionCookie(persistent ? session.AbsoluteExpiresAt : null));
        Context.Features.Set(new BffSessionFeature(session));
        Context.User = session.Principal;
        BffLog.SessionCreated(Logger, settings.Value.Deployment, SessionIds.LogReference(session.RecordId), persistent);

        // The tokens were moved into the session record; nothing of them stays in the properties.
        properties.StoreTokens([]);
    }

    protected override async Task HandleSignOutAsync(AuthenticationProperties? properties)
    {
        var recordId = Context.Features.Get<BffSessionFeature>()?.Session.RecordId
            ?? (SessionIds.IsWellFormedKey(Request.Cookies[cookieNames.Session]) ? SessionIds.RecordId(Request.Cookies[cookieNames.Session]!) : null);

        if (recordId is not null)
        {
            // Authoritative step: if it throws, the cookie stays and the caller reports 503.
            await store.DeleteAsync(recordId, CancellationToken.None);
            BffLog.SessionDeleted(Logger, settings.Value.Deployment, SessionIds.LogReference(recordId));
        }

        Context.Features.Set<BffSessionFeature?>(null);
        ExpireCookie();
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // API-style: no redirect. The front end navigates to /bff/login (silently first, ADR-017 section 7).
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = LoginHint;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    internal static CookieOptions SessionCookie(DateTimeOffset? expires) => new()
    {
        // __Host- prefix rules: Secure, Path=/, no Domain. All attributes are explicit (ADR-016 section 5).
        Secure = true,
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Domain = null,
        IsEssential = true,
        Expires = expires,
    };

    private void ExpireCookie()
    {
        if (Request.Cookies.ContainsKey(cookieNames.Session) && !Response.HasStarted)
        {
            Response.Cookies.Delete(cookieNames.Session, SessionCookie(null));
        }
    }

    private AuthenticationTicket Ticket(BffSession session) =>
        new(session.Principal, new AuthenticationProperties { ExpiresUtc = session.AbsoluteExpiresAt, IsPersistent = session.Persistent }, Scheme.Name);

    private static SessionTokens ExtractTokens(AuthenticationProperties properties)
    {
        var access = properties.GetTokenValue("access_token");
        var expiresAt = properties.GetTokenValue("expires_at");
        if (string.IsNullOrEmpty(access) || !DateTimeOffset.TryParse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires))
        {
            throw new InvalidOperationException("The token response did not contain an access token and its lifetime.");
        }

        var issuedAt = DateTimeOffset.TryParse(properties.GetTokenValue(BffTokenNames.IssuedAt), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var issued)
            ? issued
            : expires.AddMinutes(-10);

        return new SessionTokens(access, properties.GetTokenValue("refresh_token"), properties.GetTokenValue("id_token"), expires, issuedAt);
    }
}

internal static class BffTokenNames
{
    /// <summary>When the BFF received the access token; with its expiry it gives the token lifetime.</summary>
    public const string IssuedAt = "bff_issued_at";
}
