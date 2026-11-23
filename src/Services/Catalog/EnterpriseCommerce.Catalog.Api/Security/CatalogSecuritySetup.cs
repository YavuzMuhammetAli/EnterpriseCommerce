using EnterpriseCommerce.BuildingBlocks.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace EnterpriseCommerce.Catalog.Api.Security;

/// <summary>
/// Authentication and authorization of the service (ADR-017 section 4, ADR-025).
/// </summary>
/// <remarks>
/// <para>
/// One JWT bearer scheme validates every access token locally (<see cref="ResourceServerAuthentication"/>): the service
/// audience <c>catalog-api</c> or an allowed front-end audience is required. A token that fails validation answers
/// <c>401</c> on every protected endpoint; public discovery endpoints are anonymous and ignore any token.
/// </para>
/// <para>
/// Two policies separate the callers. A service token is never accepted where a user token is required, nor the
/// reverse, and both answer <c>403</c>. Every other endpoint requires an authenticated caller by default (fallback
/// policy), so a new endpoint fails closed until it states its policy. Merchant access to a tenant is then decided per
/// request from Tenant &amp; Merchant's verdict (ADR-025 section 2).
/// </para>
/// </remarks>
internal static class CatalogSecuritySetup
{
    /// <summary>The allow-listed service caller of the internal taxonomy API.</summary>
    public const string TaxonomyServicePolicy = "taxonomy-service";

    /// <summary>An authenticated merchant-portal user. Membership is verified per request by the Application layer.</summary>
    public const string MerchantUserPolicy = "merchant-user";

    public static WebApplicationBuilder AddCatalogSecurity(this WebApplicationBuilder builder)
    {
        var authentication = builder.Configuration.GetSection(CatalogAuthenticationSettings.SectionName).Get<CatalogAuthenticationSettings>()
            ?? new CatalogAuthenticationSettings();
        var taxonomy = builder.Configuration.GetSection(TaxonomySettings.SectionName).Get<TaxonomySettings>()
            ?? new TaxonomySettings();

        var problems = authentication.Validate(builder.Environment.IsDevelopment()).Concat(taxonomy.Validate()).ToList();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"Invalid Catalog security configuration: {string.Join(" ", problems)}");
        }

        var taxonomyClients = taxonomy.AllowedClientIds.ToHashSet(StringComparer.Ordinal);
        var userAudiences = authentication.EffectiveUserAudiences;

        builder.Services.AddResourceServerJwtBearer(new ResourceServerTokenSettings(
            authentication.Issuer!,
            authentication.MetadataAddress,
            [authentication.ServiceAudience, .. userAudiences],
            RequireHttpsMetadata: !builder.Environment.IsDevelopment()));

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build())
            .AddPolicy(TaxonomyServicePolicy, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    CallerClaims.HasAudience(context.User, authentication.ServiceAudience)
                    && CallerClaims.HasScope(context.User, TaxonomySettings.Scope)
                    && CallerClaims.ServiceClientOf(context.User) is { } client
                    && taxonomyClients.Contains(client)))
            .AddPolicy(MerchantUserPolicy, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    userAudiences.Any(audience => CallerClaims.HasAudience(context.User, audience))
                    && CallerClaims.UserSubjectOf(context.User) is not null));

        return builder;
    }
}
