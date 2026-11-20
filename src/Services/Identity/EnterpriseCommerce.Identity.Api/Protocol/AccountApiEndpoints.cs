using EnterpriseCommerce.Identity.Application.Sessions;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace EnterpriseCommerce.Identity.Api.Protocol;

/// <summary>Identity's REST endpoints for authenticated users (IDENTITY_API_CONTRACT.md section 2).</summary>
internal static class AccountApiEndpoints
{
    public static IEndpointRouteBuilder MapAccountApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Bearer only (the user-bearer policy never accepts the SSO cookie), so the endpoint is not reachable by CSRF.
        endpoints.MapPost("/api/v1/auth/logout-all", LogoutAllAsync)
            .RequireAuthorization(IdentityServerSetup.UserBearerPolicy);
        return endpoints;
    }

    /// <summary>
    /// Ends every session of the caller (IDENTITY_API_CONTRACT.md section 2.6). The user comes from the validated
    /// access token only; no request value selects whose sessions end. <c>204</c> once the security-version increment
    /// is committed, whatever the outcome of the cleanup; <c>503</c> (exception handler) if it could not be committed.
    /// </summary>
    private static async Task<IResult> LogoutAllAsync(
        HttpContext httpContext,
        LogoutAllSessionsHandler handler,
        CancellationToken cancellationToken)
    {
        // The policy guarantees the claim. Checked again so that a misconfiguration can never select no user.
        var subject = httpContext.User.GetClaim(Claims.Subject);
        if (string.IsNullOrEmpty(subject))
        {
            return Results.Forbid(authenticationSchemes: [OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme]);
        }

        await handler.HandleAsync(subject, cancellationToken);
        return Results.NoContent();
    }
}
