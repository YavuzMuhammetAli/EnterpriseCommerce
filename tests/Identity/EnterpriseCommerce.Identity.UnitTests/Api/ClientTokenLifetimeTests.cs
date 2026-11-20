using EnterpriseCommerce.Identity.Api.Protocol;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace EnterpriseCommerce.Identity.UnitTests.Api;

/// <summary>
/// Access token lifetimes per client kind (PHASE_2_2_AUTHENTICATION_FLOWS.md section 6.4, finding I38): service clients
/// default to 5 minutes, browser (BFF) clients keep 10 minutes, and an explicit setting still wins (ADR-017 section 12).
/// </summary>
public sealed class ClientTokenLifetimeTests
{
    [Fact]
    public void AServiceClient_DefaultsToFiveMinutes_AndGetsNoRefreshTokenLifetime()
    {
        var descriptor = ClientRegistrationSynchronizer.CreateDescriptor(Service());

        Assert.Equal("00:05:00", descriptor.Settings[Settings.TokenLifetimes.AccessToken]);
        Assert.False(descriptor.Settings.ContainsKey(Settings.TokenLifetimes.RefreshToken));
    }

    [Fact]
    public void ABrowserClient_KeepsTenMinutes_AndItsRefreshTokenLifetime()
    {
        var descriptor = ClientRegistrationSynchronizer.CreateDescriptor(Browser());

        Assert.Equal("00:10:00", descriptor.Settings[Settings.TokenLifetimes.AccessToken]);
        Assert.Equal("02:00:00", descriptor.Settings[Settings.TokenLifetimes.RefreshToken]);
    }

    [Fact]
    public void AnExplicitLifetime_OverridesTheDefaultOfEitherKind()
    {
        var service = Service();
        service.AccessTokenLifetime = TimeSpan.FromMinutes(2);
        var browser = Browser();
        browser.AccessTokenLifetime = TimeSpan.FromMinutes(5);

        Assert.Equal(TimeSpan.FromMinutes(2), service.EffectiveAccessTokenLifetime);
        Assert.Equal(TimeSpan.FromMinutes(5), browser.EffectiveAccessTokenLifetime);
        Assert.Equal("00:05:00", ClientRegistrationSynchronizer.CreateDescriptor(browser).Settings[Settings.TokenLifetimes.AccessToken]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveServiceLifetime_IsRejected(int minutes)
    {
        var service = Service();
        service.AccessTokenLifetime = TimeSpan.FromMinutes(minutes);

        Assert.Contains(service.Validate(isDevelopment: true), e => e.Contains("non-positive access token lifetime", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDefaultRegistrations_AreValid()
    {
        Assert.Empty(Service().Validate(isDevelopment: true));
        Assert.Empty(Browser().Validate(isDevelopment: true));
    }

    private static ClientRegistration Service() => new()
    {
        ClientId = "svc-unit-orders",
        Kind = ClientKind.Service,
        ClientSecret = "development-only",
        ServiceScopes = [new ServiceScopeRegistration { Scope = "svc.inventory.reservations", Audience = "inventory-api" }],
    };

    private static ClientRegistration Browser() => new()
    {
        ClientId = "bff-unit",
        Kind = ClientKind.Browser,
        Audience = "ec-unit",
        ClientSecret = "development-only",
        RedirectUris = ["https://unit.example.test/bff/callback"],
    };
}
