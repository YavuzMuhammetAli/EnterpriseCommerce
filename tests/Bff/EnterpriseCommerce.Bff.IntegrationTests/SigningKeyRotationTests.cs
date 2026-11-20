using System.Net;
using EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Bff.IntegrationTests;

/// <summary>
/// The BFF across a signing key rotation of its provider (Phase 2.3 S6.3, docs/security/IDENTITY_OPERATIONS.md):
/// a pre-published key is used without any extra metadata request, an emergency rotation costs at most one failed
/// login per instance (the handler refreshes the JWKS on an unknown key ID), and a key that is never published is
/// never accepted.
/// </summary>
public sealed class SigningKeyRotationTests(BffTestEnvironment environment) : IClassFixture<BffTestEnvironment>
{
    [Fact]
    public async Task APrePublishedKey_IsAcceptedAsSoonAsItSigns_WithoutRefetchingTheJwks()
    {
        environment.RequireAvailable();
        await using var provider = await StubProvider.StartAsync(StubScenario.Valid);
        provider.PublishNextKey();
        var bff = CreateBff(provider);

        Assert.True(await LoginAsync(bff, provider));
        var fetches = provider.JwksRequests;

        provider.ActivateNextKey();

        Assert.True(await LoginAsync(bff, provider));
        Assert.Equal(fetches, provider.JwksRequests);
    }

    [Fact]
    public async Task AfterAnEmergencyRotation_TheFirstLoginFails_AndTheRefreshedJwksAcceptsTheNext()
    {
        environment.RequireAvailable();
        await using var provider = await StubProvider.StartAsync(StubScenario.Valid);
        var bff = CreateBff(provider);
        Assert.True(await LoginAsync(bff, provider));
        var fetches = provider.JwksRequests;

        provider.ReplaceKey();

        Assert.False(await LoginAsync(bff, provider));
        Assert.True(await LoginAsync(bff, provider));
        Assert.True(provider.JwksRequests > fetches);
    }

    [Fact]
    public async Task ATokenSignedWithAnUnpublishedKey_IsNeverAccepted()
    {
        environment.RequireAvailable();
        await using var provider = await StubProvider.StartAsync(StubScenario.Valid);
        var bff = CreateBff(provider);
        Assert.True(await LoginAsync(bff, provider));

        provider.SignWithUnpublishedKey();

        Assert.False(await LoginAsync(bff, provider));
        Assert.False(await LoginAsync(bff, provider));
    }

    private BffHost CreateBff(StubProvider provider) =>
        environment.CreateBff(environment.Storefront, options =>
        {
            options.Authority = StubProvider.Issuer.AbsoluteUri;
            options.AuthorityHandler = provider.Handler;
            options.Settings["Bff:Metadata:MinimumRefreshInterval"] = "00:00:01";
        });

    /// <summary>A full login in a fresh browser; true when a session was established.</summary>
    private async Task<bool> LoginAsync(BffHost bff, StubProvider provider)
    {
        using var browser = environment.CreateBrowser(bff).Map(StubProvider.Issuer, provider.Handler);
        var landing = await browser.NavigateAsync(bff.Url("/bff/login?returnUrl=%2Fdone"));
        if (landing.Url == bff.Url("/done") && browser.Cookie(bff.Origin, bff.SessionCookieName) is not null)
        {
            return true;
        }

        Assert.Equal(HttpStatusCode.BadRequest, landing.Status);
        Assert.Null(browser.Cookie(bff.Origin, bff.SessionCookieName));
        return false;
    }
}
