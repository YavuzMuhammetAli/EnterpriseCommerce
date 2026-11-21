using System.Security.Claims;
using EnterpriseCommerce.TenantMerchant.Api.Security;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Api;

public sealed class CallerClaimsTests
{
    [Fact]
    public void AServiceToken_IdentifiesItsClient()
    {
        var principal = Principal(("ec_ptype", "service"), ("sub", "svc-admin"), ("client_id", "svc-admin"));

        Assert.Equal("svc-admin", CallerClaims.ServiceClientOf(principal));
        Assert.Null(CallerClaims.UserSubjectOf(principal));
    }

    [Theory]
    [InlineData("user", "svc-admin", "svc-admin")]
    [InlineData("service", "someone-else", "svc-admin")]
    [InlineData("service", "admin", "admin")]
    [InlineData("service", "", "")]
    public void AnythingElse_IsNoServiceClient(string principalType, string subject, string clientId)
    {
        var principal = Principal(("ec_ptype", principalType), ("sub", subject), ("client_id", clientId));

        Assert.Null(CallerClaims.ServiceClientOf(principal));
    }

    [Fact]
    public void AUserToken_IdentifiesItsSubject()
    {
        var principal = Principal(("ec_ptype", "user"), ("sub", "user-1"), ("client_id", "bff-merchant"));

        Assert.Equal("user-1", CallerClaims.UserSubjectOf(principal));
        Assert.Null(CallerClaims.ServiceClientOf(principal));
    }

    [Theory]
    [InlineData("service", "user-1", "bff-merchant")]
    [InlineData("user", "", "bff-merchant")]
    [InlineData("user", "user-1", "svc-admin")]
    public void AnythingElse_IsNoUser(string principalType, string subject, string clientId)
    {
        var principal = Principal(("ec_ptype", principalType), ("sub", subject), ("client_id", clientId));

        Assert.Null(CallerClaims.UserSubjectOf(principal));
    }

    [Fact]
    public void Scopes_AreSpaceSeparatedAndExact()
    {
        var principal = Principal(("scope", "svc.other svc.tenant-merchant.provisioning"), ("scope", "extra"));

        Assert.True(CallerClaims.HasScope(principal, "svc.tenant-merchant.provisioning"));
        Assert.True(CallerClaims.HasScope(principal, "extra"));
        Assert.False(CallerClaims.HasScope(principal, "svc.tenant-merchant"));
    }

    [Fact]
    public void Audiences_MayBeSeveral()
    {
        var principal = Principal(("aud", "identity-api"), ("aud", "tenant-merchant-api"));

        Assert.True(CallerClaims.HasAudience(principal, "tenant-merchant-api"));
        Assert.False(CallerClaims.HasAudience(principal, "ec-merchant"));
    }

    [Fact]
    public void ProvisioningSettings_AcceptOnlyServiceClients()
    {
        Assert.Empty(new ProvisioningSettings { AllowedClientIds = ["svc-admin"] }.Validate());
        Assert.NotEmpty(new ProvisioningSettings { AllowedClientIds = ["bff-admin"] }.Validate());
        Assert.NotEmpty(new ProvisioningSettings { AllowedClientIds = ["svc-"] }.Validate());
    }

    [Fact]
    public void AuthenticationSettings_RequireAnHttpsIssuerOutsideDevelopment()
    {
        Assert.NotEmpty(new TenantMerchantAuthenticationSettings().Validate(isDevelopment: true));
        Assert.NotEmpty(new TenantMerchantAuthenticationSettings { Issuer = "http://identity.internal/" }.Validate(isDevelopment: false));
        Assert.Empty(new TenantMerchantAuthenticationSettings { Issuer = "http://localhost:5101/" }.Validate(isDevelopment: true));
        Assert.Empty(new TenantMerchantAuthenticationSettings { Issuer = "https://id.example/" }.Validate(isDevelopment: false));
    }

    [Fact]
    public void AuthenticationSettings_KeepUserAndServiceAudiencesApart()
    {
        var settings = new TenantMerchantAuthenticationSettings { Issuer = "https://id.example/", UserAudiences = ["tenant-merchant-api"] };

        Assert.NotEmpty(settings.Validate(isDevelopment: false));
        Assert.Equal(["ec-merchant"], new TenantMerchantAuthenticationSettings().EffectiveUserAudiences);
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(claim => new Claim(claim.Type, claim.Value)), "test"));
}
