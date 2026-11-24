using EnterpriseCommerce.BuildingBlocks.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace EnterpriseCommerce.Inventory.Api.Security;

/// <summary>
/// Authentication and authorization of the service (ADR-017 section 4, ADR-025, ADR-026 section 10).
/// </summary>
/// <remarks>
/// <para>
/// One JWT bearer scheme validates every access token locally (<see cref="ResourceServerAuthentication"/>): the service
/// audience <c>inventory-api</c> or an allowed front-end audience is required. A token that fails validation answers
/// <c>401</c>.
/// </para>
/// <para>
/// Two policies separate the callers. A service token is never accepted where a user token is required, nor the
/// reverse, and both answer <c>403</c>. Every other endpoint requires an authenticated caller by default (fallback
/// policy), so a new endpoint fails closed until it states its policy. Merchant access to a tenant is then decided per
/// request from Tenant &amp; Merchant's verdict; a reservation is visible only to the client that created it.
/// </para>
/// </remarks>
internal static class InventorySecuritySetup
{
    /// <summary>An authenticated merchant-portal user. Membership is verified per request by the Application layer.</summary>
    public const string MerchantUserPolicy = "merchant-user";

    /// <summary>The allow-listed service caller of the internal reservation API.</summary>
    public const string ReservationServicePolicy = "reservation-service";

    public static WebApplicationBuilder AddInventorySecurity(this WebApplicationBuilder builder)
    {
        var authentication = builder.Configuration.GetSection(InventoryAuthenticationSettings.SectionName).Get<InventoryAuthenticationSettings>()
            ?? new InventoryAuthenticationSettings();
        var reservations = builder.Configuration.GetSection(ReservationClientSettings.SectionName).Get<ReservationClientSettings>()
            ?? new ReservationClientSettings();

        var problems = authentication.Validate(builder.Environment.IsDevelopment()).Concat(reservations.Validate()).ToList();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"Invalid Inventory security configuration: {string.Join(" ", problems)}");
        }

        var reservationClients = reservations.AllowedClientIds.ToHashSet(StringComparer.Ordinal);
        var userAudiences = authentication.EffectiveUserAudiences;

        builder.Services.AddResourceServerJwtBearer(new ResourceServerTokenSettings(
            authentication.Issuer!,
            authentication.MetadataAddress,
            [authentication.ServiceAudience, .. userAudiences],
            RequireHttpsMetadata: !builder.Environment.IsDevelopment()));

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build())
            .AddPolicy(MerchantUserPolicy, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    userAudiences.Any(audience => CallerClaims.HasAudience(context.User, audience))
                    && CallerClaims.UserSubjectOf(context.User) is not null))
            .AddPolicy(ReservationServicePolicy, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    CallerClaims.HasAudience(context.User, authentication.ServiceAudience)
                    && CallerClaims.HasScope(context.User, ReservationClientSettings.Scope)
                    && CallerClaims.ServiceClientOf(context.User) is { } client
                    && reservationClients.Contains(client)));

        return builder;
    }
}
