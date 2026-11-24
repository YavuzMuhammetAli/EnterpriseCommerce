using System.Security.Claims;
using EnterpriseCommerce.BuildingBlocks.Authentication;
using EnterpriseCommerce.Cart.Domain.Carts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace EnterpriseCommerce.Cart.Api.Security;

/// <summary>
/// Authentication and authorization of the service (ADR-017 section 4, ADR-029 section 1).
/// </summary>
/// <remarks>
/// <para>
/// One JWT bearer scheme validates every access token locally (<see cref="ResourceServerAuthentication"/>) and accepts
/// only the customer audiences, <c>ec-storefront</c> by default: a merchant-portal, admin or service token fails
/// validation and answers <c>401</c>.
/// </para>
/// <para>
/// The <see cref="CustomerPolicy"/> then requires a user token (<c>ec_ptype=user</c>, a subject, a client that is not a
/// <c>svc-</c> client); any other token answers <c>403</c>. Every other endpoint requires an authenticated caller by
/// default (fallback policy), so a new endpoint fails closed until it states its policy. The cart is always the
/// subject's own: no request value names an owner.
/// </para>
/// </remarks>
internal static class CartSecuritySetup
{
    /// <summary>An authenticated storefront customer.</summary>
    public const string CustomerPolicy = "customer";

    public static WebApplicationBuilder AddCartSecurity(this WebApplicationBuilder builder)
    {
        var authentication = builder.Configuration.GetSection(CartAuthenticationSettings.SectionName).Get<CartAuthenticationSettings>()
            ?? new CartAuthenticationSettings();

        var problems = authentication.Validate(builder.Environment.IsDevelopment()).ToList();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"Invalid Cart security configuration: {string.Join(" ", problems)}");
        }

        var userAudiences = authentication.EffectiveUserAudiences;
        builder.Services.AddSingleton(new CustomerAudiences(userAudiences));

        builder.Services.AddResourceServerJwtBearer(new ResourceServerTokenSettings(
            authentication.Issuer!,
            authentication.MetadataAddress,
            [.. userAudiences],
            RequireHttpsMetadata: !builder.Environment.IsDevelopment()));

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build())
            .AddPolicy(CustomerPolicy, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => CustomerSubjectOf(context.User, userAudiences) is not null));

        return builder;
    }

    /// <summary>The customer's subject, or <c>null</c> when the principal is not a storefront customer.</summary>
    public static string? CustomerSubjectOf(ClaimsPrincipal user, IReadOnlyList<string> audiences) =>
        audiences.Any(audience => CallerClaims.HasAudience(user, audience))
        && CallerClaims.UserSubjectOf(user) is { } subject
        && CustomerSubject.IsValid(subject)
            ? subject
            : null;
}

/// <summary>The accepted customer audiences, for the endpoints that read the subject.</summary>
internal sealed record CustomerAudiences(IReadOnlyList<string> Values);
