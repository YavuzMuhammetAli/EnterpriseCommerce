using EnterpriseCommerce.Identity.Api.Protocol;
using EnterpriseCommerce.Identity.Application.Sessions;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace EnterpriseCommerce.Identity.Api.Pages.Connect;

/// <summary>
/// The OpenID Connect end-session endpoint (passthrough mode, IDENTITY_API_CONTRACT.md section 2.5). OpenIddict has
/// already validated the request, the ID token hint and the exact registered <c>post_logout_redirect_uri</c> before
/// this page runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>GET never changes state.</b> It renders a sign-out confirmation that submits the validated parameters by POST.
/// A link or an image therefore cannot sign a user out. The POST is protected by antiforgery and by the Fetch Metadata
/// and Origin checks (<see cref="Accounts.CredentialFormProtection"/>).
/// </para>
/// <para>
/// The POST first ends the client session named by the ID token hint, but only when the hint belongs to the user of
/// the current SSO cookie. That change commits in the authoritative transaction; if it cannot be committed, the
/// request fails with <c>503</c> and the cookie is kept. Only then is the SSO cookie cleared and the browser sent to
/// the registered post-logout URI, or to the local signed-out page. Repeating it succeeds.
/// </para>
/// </remarks>
[EnableRateLimiting(IdentityServerSetup.CredentialRateLimitPolicy)]
public sealed partial class EndSessionModel(EndClientSessionHandler endSession, ILogger<EndSessionModel> logger) : PageModel
{
    public const string SignedOutPage = "/account/signed-out";

    /// <summary>The validated OpenID Connect parameters, carried to the POST. Never logged.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Parameters { get; private set; } = [];

    public void OnGet() =>
        Parameters = [.. Request.Query
            .Where(parameter => !string.Equals(parameter.Key, "__RequestVerificationToken", StringComparison.Ordinal))
            .SelectMany(parameter => parameter.Value.Select(value => new KeyValuePair<string, string>(parameter.Key, value ?? string.Empty)))];

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        _ = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // Validates the cookie against the authoritative security state; a database failure surfaces as 503.
        var cookie = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var cookieSubject = cookie.Succeeded ? cookie.Principal?.GetClaim(Claims.Subject) : null;

        // For an end-session request, this returns the principal of the validated id_token_hint, if one was sent.
        var hint = (await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        var authorizationId = hint?.GetAuthorizationId();

        if (!string.IsNullOrEmpty(authorizationId))
        {
            if (cookieSubject is not null && string.Equals(hint!.GetClaim(Claims.Subject), cookieSubject, StringComparison.Ordinal))
            {
                await endSession.HandleAsync(authorizationId, "end-session", cancellationToken);
            }
            else
            {
                // A hint without the matching signed-in user never ends that user's session.
                LogHintNotApplied(logger, authorizationId);
            }
        }

        // Only after the authoritative change: clear the SSO cookie.
        await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);

        return SignOut(
            new AuthenticationProperties { RedirectUri = SignedOutPage },
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [LoggerMessage(
        EventId = 5200,
        Level = LogLevel.Warning,
        Message = "Security event EndSessionHintNotApplied: the ID token hint of authorization {AuthorizationId} does not belong to the signed-in user; no session was revoked")]
    private static partial void LogHintNotApplied(ILogger logger, string authorizationId);
}
