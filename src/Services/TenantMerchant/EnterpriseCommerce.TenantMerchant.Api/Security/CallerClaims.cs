using System.Security.Claims;

namespace EnterpriseCommerce.TenantMerchant.Api.Security;

/// <summary>
/// Reads the caller from a validated access token (IDENTITY_API_CONTRACT.md section 3). Claims are inbound-unmapped, so
/// they keep their JWT names. Only <c>sub</c>, <c>client_id</c>, <c>aud</c>, <c>scope</c> and <c>ec_ptype</c> are used;
/// tokens carry no role or tenant claims, and none would be honoured.
/// </summary>
internal static class CallerClaims
{
    public const string Subject = "sub";
    public const string ClientId = "client_id";
    public const string Audience = "aud";
    public const string Scope = "scope";
    public const string PrincipalType = "ec_ptype";
    public const string ServicePrincipal = "service";
    public const string UserPrincipal = "user";

    /// <summary>The role claim type of validated principals: a type no token carries, so no token ever yields a role.</summary>
    public const string NoRoleClaimType = "urn:enterprisecommerce:tenant-merchant:no-token-roles";

    /// <summary>Reserved for service clients (ADR-017 section 3).</summary>
    public const string ServiceClientPrefix = "svc-";

    public static bool HasAudience(ClaimsPrincipal principal, string audience) =>
        principal.FindAll(Audience).Any(claim => string.Equals(claim.Value, audience, StringComparison.Ordinal));

    /// <summary>Whether a scope was granted. The <c>scope</c> claim is space-separated and may occur more than once.</summary>
    public static bool HasScope(ClaimsPrincipal principal, string scope) =>
        principal.FindAll(Scope)
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);

    /// <summary>
    /// The calling service client: a service principal whose <c>sub</c> is its own <c>svc-</c> <c>client_id</c>.
    /// <c>null</c> for anything else, including every user token.
    /// </summary>
    public static string? ServiceClientOf(ClaimsPrincipal principal)
    {
        var clientId = principal.FindFirstValue(ClientId);
        return principal.FindFirstValue(PrincipalType) == ServicePrincipal
            && clientId is { Length: > 0 }
            && clientId.StartsWith(ServiceClientPrefix, StringComparison.Ordinal)
            && string.Equals(principal.FindFirstValue(Subject), clientId, StringComparison.Ordinal)
                ? clientId
                : null;
    }

    /// <summary>
    /// The authenticated user's Identity subject. <c>null</c> for service tokens, tokens without a subject, and tokens
    /// issued to a <c>svc-</c> client.
    /// </summary>
    public static string? UserSubjectOf(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue(Subject);
        var clientId = principal.FindFirstValue(ClientId);
        return principal.FindFirstValue(PrincipalType) == UserPrincipal
            && subject is { Length: > 0 }
            && (clientId is null || !clientId.StartsWith(ServiceClientPrefix, StringComparison.Ordinal))
                ? subject
                : null;
    }
}
