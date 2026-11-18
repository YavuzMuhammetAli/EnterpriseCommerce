using System.Globalization;
using System.Security.Claims;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace EnterpriseCommerce.Identity.Api.Protocol;

/// <summary>EnterpriseCommerce claim names and their token destinations (ADR-017 section 2).</summary>
internal static class IdentityClaims
{
    /// <summary>Principal type: <c>user</c> or <c>service</c>. Public, in access tokens.</summary>
    public const string PrincipalType = "ec_ptype";

    /// <summary>Private: the security version the session was established at. Never in access or ID tokens.</summary>
    public const string SecurityVersion = "ec_sv";

    /// <summary>Private: the absolute session deadline (Unix seconds). Never in access or ID tokens.</summary>
    public const string SessionDeadline = "ec_sdl";

    public const string UserPrincipal = "user";
    public const string ServicePrincipal = "service";

    /// <summary>
    /// Explicit destinations for every claim. Claims not listed (including the private security claims) reach no
    /// access or ID token; OpenIddict still keeps them inside the encrypted authorization codes and refresh tokens.
    /// </summary>
    public static IEnumerable<string> DestinationsOf(Claim claim) => claim.Type switch
    {
        Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
        Claims.AuthenticationMethodReference => [Destinations.AccessToken, Destinations.IdentityToken],
        PrincipalType => [Destinations.AccessToken],
        _ => [],
    };

    public static ClaimsIdentity WithDestinations(this ClaimsIdentity identity)
    {
        identity.SetDestinations(DestinationsOf);
        return identity;
    }

    public static ClaimsPrincipal WithDestinations(this ClaimsPrincipal principal)
    {
        principal.SetDestinations(DestinationsOf);
        return principal;
    }

    public static long? GetSecurityVersion(this ClaimsPrincipal principal) =>
        long.TryParse(principal.GetClaim(SecurityVersion), NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : null;

    public static DateTimeOffset? GetSessionDeadline(this ClaimsPrincipal principal) =>
        long.TryParse(principal.GetClaim(SessionDeadline), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
