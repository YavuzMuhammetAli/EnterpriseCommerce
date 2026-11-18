using EnterpriseCommerce.Identity.Api.Protocol;

namespace EnterpriseCommerce.Identity.UnitTests.Api;

public sealed class TrustedProxySetupTests
{
    private static TrustedProxySettings Valid() => new()
    {
        KnownProxies = ["10.0.0.5"],
        KnownNetworks = ["10.1.0.0/16"],
        PublicHosts = ["id.example.test"],
    };

    [Fact]
    public void Validate_CompleteDeploymentConfiguration_IsAccepted() =>
        Assert.Empty(TrustedProxySetup.Validate(Valid(), "id.example.test;identity-internal", isDevelopment: false));

    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    [InlineData("id.example.test;*")]
    public void Validate_OutsideDevelopment_RequiresExplicitHostFiltering(string? allowedHosts) =>
        Assert.Contains(TrustedProxySetup.Validate(Valid(), allowedHosts, isDevelopment: false), error => error.Contains("AllowedHosts", StringComparison.Ordinal));

    [Fact]
    public void Validate_OutsideDevelopment_RequiresTheGatewayAndPublicHosts()
    {
        var errors = TrustedProxySetup.Validate(new TrustedProxySettings(), "id.example.test", isDevelopment: false).ToList();

        Assert.Contains(errors, error => error.Contains("KnownProxies", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("PublicHosts", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsMalformedAddressesInEveryEnvironment()
    {
        var settings = new TrustedProxySettings { KnownProxies = ["gateway"], KnownNetworks = ["10.0.0.0/99"] };

        Assert.Equal(2, TrustedProxySetup.Validate(settings, "*", isDevelopment: true).Count());
    }
}
