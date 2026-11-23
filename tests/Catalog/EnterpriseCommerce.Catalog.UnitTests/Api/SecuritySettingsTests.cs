using EnterpriseCommerce.Catalog.Api.Security;

namespace EnterpriseCommerce.Catalog.UnitTests.Api;

public sealed class SecuritySettingsTests
{
    [Fact]
    public void TaxonomySettings_AcceptOnlyServiceClients()
    {
        Assert.Empty(new TaxonomySettings().Validate());
        Assert.Empty(new TaxonomySettings { AllowedClientIds = ["svc-admin"] }.Validate());
        Assert.NotEmpty(new TaxonomySettings { AllowedClientIds = ["bff-admin"] }.Validate());
        Assert.NotEmpty(new TaxonomySettings { AllowedClientIds = ["svc-"] }.Validate());
    }

    [Fact]
    public void AuthenticationSettings_RequireAnHttpsIssuerOutsideDevelopment()
    {
        Assert.NotEmpty(new CatalogAuthenticationSettings().Validate(isDevelopment: true));
        Assert.NotEmpty(new CatalogAuthenticationSettings { Issuer = "http://identity.internal/" }.Validate(isDevelopment: false));
        Assert.Empty(new CatalogAuthenticationSettings { Issuer = "http://localhost:5101/" }.Validate(isDevelopment: true));
        Assert.Empty(new CatalogAuthenticationSettings { Issuer = "https://id.example/" }.Validate(isDevelopment: false));
    }

    [Fact]
    public void AuthenticationSettings_KeepUserAndServiceAudiencesApart()
    {
        var settings = new CatalogAuthenticationSettings { Issuer = "https://id.example/", UserAudiences = ["catalog-api"] };

        Assert.NotEmpty(settings.Validate(isDevelopment: false));
        Assert.Equal(["ec-merchant"], new CatalogAuthenticationSettings().EffectiveUserAudiences);
        Assert.Equal("catalog-api", new CatalogAuthenticationSettings().ServiceAudience);
    }
}
