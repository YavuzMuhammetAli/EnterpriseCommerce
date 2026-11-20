using EnterpriseCommerce.Bff.Configuration;
using EnterpriseCommerce.Bff.Logging;
using EnterpriseCommerce.Bff.Protocol;
using EnterpriseCommerce.Bff.Security;
using EnterpriseCommerce.Bff.Sessions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Bff.Endpoints;

/// <summary>The browser-facing endpoints (P22 section 4.1, ADR-016). None of them ever returns a token.</summary>
internal static class BffEndpoints
{
    public static IEndpointRouteBuilder MapBffEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(BffPaths.Login, Login);
        app.MapGet(BffPaths.User, UserAsync);
        app.MapPost(BffPaths.Logout, LogoutAsync);
        app.MapPost(BffPaths.LogoutAll, LogoutAllAsync);
        app.MapGet(BffPaths.SignedOut, (IOptions<BffSettings> settings) => Results.Redirect(settings.Value.PostLogoutLandingPath));
        return app;
    }

    /// <summary>
    /// Starts a sign-in with a full-page navigation. <c>silent=1</c> first tries <c>prompt=none</c> through Identity's
    /// SSO cookie (ADR-017 section 7); <c>persistent=1</c> asks for "keep me signed in" where the deployment allows it.
    /// </summary>
    private static IResult Login(string? returnUrl, string? silent, string? persistent, IOptions<BffSettings> settings)
    {
        var target = ReturnUrlValidator.Normalize(returnUrl);
        var properties = new AuthenticationProperties
        {
            RedirectUri = target,
            IsPersistent = persistent == "1" && settings.Value.Session.AllowPersistent,
        };
        properties.Items[OidcSetup.ReturnUrlItem] = target;
        if (silent == "1")
        {
            properties.Items[OidcSetup.SilentItem] = "1";
        }

        return Results.Challenge(properties, [OidcSetup.Scheme]);
    }

    /// <summary>
    /// The signed-in user (no tokens), and a fresh session-bound antiforgery token in a readable cookie. It also
    /// checks that the session can still obtain a valid access token, refreshing if due.
    /// </summary>
    private static async Task<IResult> UserAsync(HttpContext context, RefreshCoordinator coordinator, IAntiforgery antiforgery, BffCookieNames cookieNames)
    {
        var session = await AuthenticatedSessionAsync(context);
        if (session is null)
        {
            return Unauthorized(context);
        }

        var token = await coordinator.GetAccessTokenAsync(session, context.RequestAborted);
        switch (token.Status)
        {
            case AccessTokenStatus.SessionEnded:
                await context.SignOutAsync(BffSessionDefaults.Scheme);
                return Unauthorized(context);
            case AccessTokenStatus.Unavailable:
                return Unavailable(context);
        }

        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append(cookieNames.RequestToken, tokens.RequestToken!, new CookieOptions
        {
            // Readable by the front end's own script, which echoes it in X-XSRF-TOKEN (ADR-016 section 6).
            Secure = true,
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });

        return Results.Ok(new
        {
            sub = session.Principal.FindFirst(BffClaims.Subject)?.Value,
            amr = session.Principal.FindAll(BffClaims.AuthenticationMethod).Select(claim => claim.Value).ToArray(),
            sessionExpiresAt = session.AbsoluteExpiresAt,
        });
    }

    /// <summary>
    /// Logout (ADR-017 section 8): revoke at Identity (best effort), delete the session (authoritative), expire the
    /// cookies, then hand the browser Identity's end-session URL. The URL carries no token: <c>id_token_hint</c>
    /// would expose the ID token to the browser, and the revocation has already ended the authorization.
    /// </summary>
    private static async Task<IResult> LogoutAsync(HttpContext context, RedisSessionStore store, IdentityProtocolClient identity, IOptions<BffSettings> settings, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(BffEndpoints));
        var session = context.Features.Get<BffSessionFeature>()!.Session;
        var reference = SessionIds.LogReference(session.RecordId);

        var tokens = await store.GetTokensAsync(session.RecordId, context.RequestAborted);
        var revocation = "none";
        if (tokens?.Tokens.RefreshToken is { } refreshToken)
        {
            revocation = (await identity.RevokeAsync(refreshToken, context.RequestAborted)).ToString();
            if (revocation != nameof(CallOutcome.Succeeded))
            {
                BffLog.RevocationFailed(logger, settings.Value.Deployment, reference);
            }
        }

        return await EndLocalSessionAsync(context, identity, settings.Value, () => BffLog.LoggedOut(logger, settings.Value.Deployment, reference, revocation));
    }

    /// <summary>
    /// Logout-all: Identity raises the security version and revokes every session of the user, identified only by the
    /// bearer token. Other BFF sessions end at their next refresh (<c>invalid_grant</c>). Nothing is claimed unless
    /// Identity confirmed it.
    /// </summary>
    private static async Task<IResult> LogoutAllAsync(HttpContext context, RefreshCoordinator coordinator, IdentityProtocolClient identity, IOptions<BffSettings> settings, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(BffEndpoints));
        var session = context.Features.Get<BffSessionFeature>()!.Session;
        var reference = SessionIds.LogReference(session.RecordId);

        var token = await coordinator.GetAccessTokenAsync(session, context.RequestAborted);
        if (token.Status == AccessTokenStatus.SessionEnded)
        {
            await context.SignOutAsync(BffSessionDefaults.Scheme);
            BffLog.LogoutAll(logger, settings.Value.Deployment, reference, "session_ended");
            return Unauthorized(context);
        }

        if (token.Status == AccessTokenStatus.Unavailable)
        {
            return Unavailable(context);
        }

        var outcome = await identity.LogoutAllAsync(token.AccessToken!, context.RequestAborted);
        BffLog.LogoutAll(logger, settings.Value.Deployment, reference, outcome.ToString());
        return outcome switch
        {
            CallOutcome.Succeeded => await EndLocalSessionAsync(context, identity, settings.Value, static () => { }),
            CallOutcome.Unauthorized or CallOutcome.Forbidden => Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Sign-out of all sessions could not be authorized. Sign in again and retry."),
            _ => Unavailable(context),
        };
    }

    private static async Task<IResult> EndLocalSessionAsync(HttpContext context, IdentityProtocolClient identity, BffSettings settings, Action log)
    {
        // Throws if the store cannot delete the record: the cookie then stays and the request fails with 503.
        await context.SignOutAsync(BffSessionDefaults.Scheme);
        context.Response.Cookies.Delete(context.RequestServices.GetRequiredService<BffCookieNames>().RequestToken, new CookieOptions { Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
        log();

        string endSession;
        try
        {
            var configuration = await identity.GetConfigurationAsync(context.RequestAborted);
            endSession = string.IsNullOrEmpty(configuration.EndSessionEndpoint)
                ? settings.PostLogoutLandingPath
                : QueryHelpers.AddQueryString(configuration.EndSessionEndpoint, new Dictionary<string, string?>
                {
                    ["client_id"] = settings.ClientId,
                    ["post_logout_redirect_uri"] = settings.PostLogoutRedirectUri.AbsoluteUri,
                });
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or IOException or OperationCanceledException)
        {
            // The local session is gone either way; Identity's SSO cookie then ends by its own lifetime.
            endSession = settings.PostLogoutLandingPath;
        }

        return Results.Ok(new { signedOut = true, endSessionUrl = endSession });
    }

    private static async Task<BffSession?> AuthenticatedSessionAsync(HttpContext context)
    {
        var result = await context.AuthenticateAsync(BffSessionDefaults.Scheme);
        return result.Succeeded ? context.Features.Get<BffSessionFeature>()?.Session : null;
    }

    private static IResult Unauthorized(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = BffSessionHandler.LoginHint;
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in required.");
    }

    private static IResult Unavailable(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "2";
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Temporarily unavailable. Retry shortly.");
    }
}
