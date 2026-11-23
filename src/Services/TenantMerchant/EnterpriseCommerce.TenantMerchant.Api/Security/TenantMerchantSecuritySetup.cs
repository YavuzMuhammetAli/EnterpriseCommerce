using EnterpriseCommerce.BuildingBlocks.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace EnterpriseCommerce.TenantMerchant.Api.Security;

/// <summary>
/// Authentication and authorization of the service (ADR-017 section 4, PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.7).
/// </summary>
/// <remarks>
/// <para>
/// One JWT bearer scheme validates every access token locally (<see cref="ResourceServerAuthentication"/>): the service
/// audience or an allowed front-end audience is required. A token that fails validation, or carries another service's
/// audience, answers <c>401</c>.
/// </para>
/// <para>
/// Two policies then separate the callers. A service token is never accepted where a user token is required, nor the
/// reverse, and both answer <c>403</c>. Every other endpoint requires an authenticated caller by default (fallback
/// policy), so a new endpoint fails closed until it states its policy.
/// </para>
/// </remarks>
internal static class TenantMerchantSecuritySetup
{
    /// <summary>The allow-listed service caller of the internal provisioning API.</summary>
    public const string ProvisioningServicePolicy = "provisioning-service";

    /// <summary>The allow-listed service caller of the internal membership verification API (ADR-025).</summary>
    public const string MembershipVerificationServicePolicy = "membership-verification-service";

    /// <summary>An authenticated merchant-portal user. Membership is checked per request by the Application layer.</summary>
    public const string MerchantUserPolicy = "merchant-user";

    public static WebApplicationBuilder AddTenantMerchantSecurity(this WebApplicationBuilder builder)
    {
        var authentication = builder.Configuration.GetSection(TenantMerchantAuthenticationSettings.SectionName).Get<TenantMerchantAuthenticationSettings>()
            ?? new TenantMerchantAuthenticationSettings();
        var provisioning = builder.Configuration.GetSection(ProvisioningSettings.SectionName).Get<ProvisioningSettings>()
            ?? new ProvisioningSettings();
        var membershipVerification = builder.Configuration.GetSection(MembershipVerificationSettings.SectionName).Get<MembershipVerificationSettings>()
            ?? new MembershipVerificationSettings();

        var problems = authentication.Validate(builder.Environment.IsDevelopment()).Concat(provisioning.Validate())
            .Concat(membershipVerification.Validate())
            .ToList();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"Invalid Tenant & Merchant security configuration: {string.Join(" ", problems)}");
        }

        var allowedClients = provisioning.AllowedClientIds.ToHashSet(StringComparer.Ordinal);
        var verificationClients = membershipVerification.AllowedClientIds.ToHashSet(StringComparer.Ordinal);
        var userAudiences = authentication.EffectiveUserAudiences;

        builder.Services.AddResourceServerJwtBearer(new ResourceServerTokenSettings(
            authentication.Issuer!,
            authentication.MetadataAddress,
            [authentication.ServiceAudience, .. userAudiences],
            RequireHttpsMetadata: !builder.Environment.IsDevelopment()));

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build())
            .AddPolicy(ProvisioningServicePolicy, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    CallerClaims.HasAudience(context.User, authentication.ServiceAudience)
                    && CallerClaims.HasScope(context.User, ProvisioningSettings.Scope)
                    && CallerClaims.ServiceClientOf(context.User) is { } client
                    && allowedClients.Contains(client)))
            .AddPolicy(MembershipVerificationServicePolicy, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    CallerClaims.HasAudience(context.User, authentication.ServiceAudience)
                    && CallerClaims.HasScope(context.User, MembershipVerificationSettings.Scope)
                    && CallerClaims.ServiceClientOf(context.User) is { } client
                    && verificationClients.Contains(client)))
            .AddPolicy(MerchantUserPolicy, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    userAudiences.Any(audience => CallerClaims.HasAudience(context.User, audience))
                    && CallerClaims.UserSubjectOf(context.User) is not null));

        return builder;
    }
}
