using EnterpriseCommerce.TenantMerchant.Api.Security;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Api;

public sealed class SecuritySettingsTests
{
    [Fact]
    public void ProvisioningSettings_AcceptOnlyServiceClients()
    {
        Assert.Empty(new ProvisioningSettings { AllowedClientIds = ["svc-admin"] }.Validate());
        Assert.NotEmpty(new ProvisioningSettings { AllowedClientIds = ["bff-admin"] }.Validate());
        Assert.NotEmpty(new ProvisioningSettings { AllowedClientIds = ["svc-"] }.Validate());
    }

    [Fact]
    public void MembershipVerificationSettings_AcceptOnlyServiceClients()
    {
        Assert.Empty(new MembershipVerificationSettings().Validate());
        Assert.Empty(new MembershipVerificationSettings { AllowedClientIds = ["svc-catalog"] }.Validate());
        Assert.NotEmpty(new MembershipVerificationSettings { AllowedClientIds = ["bff-merchant"] }.Validate());
        Assert.NotEmpty(new MembershipVerificationSettings { AllowedClientIds = ["svc-"] }.Validate());
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
}
