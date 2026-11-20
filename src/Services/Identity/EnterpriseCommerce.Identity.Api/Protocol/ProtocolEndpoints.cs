using EnterpriseCommerce.Identity.Application.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Application.Tokens;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace EnterpriseCommerce.Identity.Api.Protocol;

/// <summary>
/// The custom parts of the OpenID Connect authorization and token endpoints (passthrough mode).
/// OpenIddict validates the protocol (client, redirect URI, PKCE, codes, refresh tokens) before these run.
/// </summary>
internal static class ProtocolEndpoints
{
    public static IEndpointRouteBuilder MapProtocolEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/connect/authorize", AuthorizeAsync);
        endpoints.MapPost("/connect/token", TokenAsync);
        return endpoints;
    }

    /// <summary>
    /// Authorization endpoint (ADR-017 sections 7 and 9). Credentials are entered only on the Identity login page.
    /// Each successful request creates a new ad-hoc authorization, which is one client session.
    /// </summary>
    private static async Task<IResult> AuthorizeAsync(
        HttpContext httpContext,
        SecurityStateReader securityState,
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        IOptions<IdentityServerSettings> settings,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        var login = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var userId = login.Principal?.GetClaim(Claims.Subject);

        if (!login.Succeeded || userId is null || request.HasPromptValue(PromptValues.Login))
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Forbid(Errors.LoginRequired, "The user is not signed in.");
            }

            // prompt=login: start a fresh sign-in, then come back without the prompt to avoid a loop.
            var returnUrl = httpContext.Request.PathBase + httpContext.Request.Path
                + QueryString.Create(httpContext.Request.Query.Where(parameter => parameter.Key != Parameters.Prompt));
            return Results.Challenge(new AuthenticationProperties { RedirectUri = returnUrl }, [IdentityConstants.ApplicationScheme]);
        }

        // ADR-017 section 7: checked again at the authorization endpoint, failing closed.
        var state = await securityState.ReadAsync(userId, cancellationToken);
        var sessionVersion = login.Principal!.GetSecurityVersion();
        if (!state.AllowsSession(sessionVersion))
        {
            await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Forbid(Errors.AccessDenied, "The user session is no longer valid.");
        }

        var client = settings.Value.Clients.Single(c => c.ClientId == request.ClientId);
        var application = await applications.FindByClientIdAsync(request.ClientId!, cancellationToken)
            ?? throw new InvalidOperationException("The client application cannot be found.");

        var deadline = timeProvider.GetUtcNow() + client.AbsoluteSessionLifetime;
        var identity = new ClaimsIdentity(
            TokenValidationParameters.DefaultAuthenticationType,
            Claims.Name,
            Claims.Role);
        identity.SetClaim(Claims.Subject, userId)
            .SetClaim(IdentityClaims.PrincipalType, IdentityClaims.UserPrincipal)
            .SetClaims(Claims.AuthenticationMethodReference, ["pwd"])
            .SetClaim(IdentityClaims.SecurityVersion, state.SecurityVersion!.Value.ToString(CultureInfo.InvariantCulture))
            .SetClaim(IdentityClaims.SessionDeadline, deadline.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        identity.SetScopes(request.GetScopes());
        identity.SetResources(client.Audience!);

        var authorization = await authorizations.CreateAsync(
            principal: new ClaimsPrincipal(identity),
            subject: userId,
            client: (await applications.GetIdAsync(application, cancellationToken))!,
            type: AuthorizationTypes.AdHoc,
            scopes: identity.GetScopes(),
            cancellationToken: cancellationToken);
        identity.SetAuthorizationId(await authorizations.GetIdAsync(authorization, cancellationToken));

        return Results.SignIn(new ClaimsPrincipal(identity.WithDestinations()), properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Token endpoint (ADR-017 sections 5 to 8). Order: presenter, absolute deadline, then the authoritative state
    /// and the redemption ledger in one short transaction, then native redemption and issuance.
    /// </summary>
    private static async Task<IResult> TokenAsync(
        HttpContext httpContext,
        TokenGrantGuard guard,
        IOptions<IdentityServerSettings> settings,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");
        var client = settings.Value.Clients.SingleOrDefault(c => c.ClientId == request.ClientId);

        if (client is null)
        {
            return Forbid(Errors.InvalidClient, "The client is not registered.");
        }

        if (request.IsClientCredentialsGrantType())
        {
            var result = IssueServiceToken(request, client, out var issued);
            RecordGrant("client_credentials", issued ? "none" : "invalid_request");
            return result;
        }

        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
        {
            return Forbid(Errors.UnsupportedGrantType, "The grant type is not supported.");
        }

        var grantType = request.IsRefreshTokenGrantType() ? "refresh_token" : "authorization_code";
        var principal = (await httpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        var subject = principal?.GetClaim(Claims.Subject);
        if (principal is null || subject is null || client.Kind != ClientKind.Browser)
        {
            RecordGrant(grantType, "invalid_grant");
            return Forbid(Errors.InvalidGrant, "The grant is invalid.");
        }

        // Defense in depth: OpenIddict already binds the token to its presenters (V42).
        if (!principal.GetPresenters().Contains(client.ClientId, StringComparer.Ordinal))
        {
            RecordGrant(grantType, "presenter_mismatch");
            return Forbid(Errors.InvalidGrant, "The grant is invalid.");
        }

        var remaining = principal.GetSessionDeadline() - timeProvider.GetUtcNow();
        if (remaining is null || remaining <= TimeSpan.Zero)
        {
            RecordGrant(grantType, "session_expired");
            return Forbid(Errors.InvalidGrant, "The session has expired.");
        }

        TokenGrantDenial denial;
        try
        {
            denial = await guard.EvaluateAsync(
                new TokenGrantRequest(
                    subject,
                    principal.GetAuthorizationId(),
                    principal.GetTokenId(),
                    principal.GetClaim(IdentityClaims.SecurityVersion),
                    request.IsRefreshTokenGrantType(),
                    client.ClientId),
                cancellationToken);
        }
        catch (SecurityStateUnavailableException)
        {
            // 503 through the exception handler: nothing is issued.
            RecordGrant(grantType, "unavailable");
            throw;
        }

        RecordGrant(grantType, denial switch
        {
            TokenGrantDenial.None => "none",
            TokenGrantDenial.AccountNotAllowed => "account_not_allowed",
            TokenGrantDenial.StaleSession => "stale_session",
            TokenGrantDenial.MissingSecurityState => "missing_security_state",
            _ => "reuse",
        });

        if (denial != TokenGrantDenial.None)
        {
            return Forbid(Errors.InvalidGrant, "The grant is invalid.");
        }

        // The new refresh token never outlives the absolute session (ADR-017 section 5, PoC-3c).
        principal.SetRefreshTokenLifetime(remaining < client.RefreshTokenLifetime ? remaining : client.RefreshTokenLifetime);
        return Results.SignIn(principal.WithDestinations(), properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>Counts a grant decision. <paramref name="reason"/> is <c>none</c> when tokens are issued.</summary>
    private static void RecordGrant(string grantType, string reason) =>
        IdentityMetrics.TokenGrants.Add(
            1,
            IdentityMetrics.Tag("grant_type", grantType),
            IdentityMetrics.Tag("outcome", reason == "none" ? "issued" : "denied"),
            IdentityMetrics.Tag("reason", reason));

    private static IResult IssueServiceToken(OpenIddictRequest request, ClientRegistration client, out bool issued)
    {
        issued = false;
        if (client.Kind != ClientKind.Service)
        {
            return Forbid(Errors.UnauthorizedClient, "The client may not use this grant.");
        }

        var scopes = request.GetScopes();
        var audiences = client.ServiceScopes
            .Where(registration => scopes.Contains(registration.Scope, StringComparer.Ordinal))
            .Select(registration => registration.Audience)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (audiences.Count == 0)
        {
            return Forbid(Errors.InvalidScope, "At least one service scope is required.");
        }

        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, client.ClientId)
            .SetClaim(IdentityClaims.PrincipalType, IdentityClaims.ServicePrincipal);
        identity.SetScopes(scopes);
        identity.SetResources(audiences);

        issued = true;
        return Results.SignIn(new ClaimsPrincipal(identity.WithDestinations()), properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult Forbid(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
