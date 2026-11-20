using System.Net;
using System.Security.Cryptography;
using EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;
using EnterpriseCommerce.Bff.Protocol;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Bff.IntegrationTests;

/// <summary>State, correlation, code replay, client authentication and redirect validation (ADR-016 section 2).</summary>
public sealed class CallbackSecurityTests(BffTestEnvironment environment) : IClassFixture<BffTestEnvironment>
{
    private static bool IsCallback(Uri url) => url.AbsolutePath == "/bff/callback";

    [Fact]
    public async Task TamperedOrMissingState_IsRejected_AndNoSessionIsCreated()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();

        var beforeCallback = await environment.SignInAsync(browser, bff, email, stopBefore: IsCallback);
        var callback = beforeCallback.Location!;
        var query = QueryHelpers.ParseQuery(callback.Query);

        var tampered = QueryHelpers.AddQueryString(bff.Url("/bff/callback").AbsoluteUri, new Dictionary<string, string?>
        {
            ["code"] = query["code"],
            ["state"] = query["state"].ToString()[..^4] + "AAAA",
        });
        var withoutState = QueryHelpers.AddQueryString(bff.Url("/bff/callback").AbsoluteUri, "code", query["code"].ToString());

        foreach (var url in new[] { tampered, withoutState })
        {
            var response = await browser.GetAsync(new Uri(url));
            Assert.Equal(HttpStatusCode.BadRequest, response.Status);
            Assert.DoesNotContain("state", response.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Null(browser.Cookie(bff.Origin, bff.SessionCookieName));
        }

        Assert.Equal(0, bff.Channel.TokenRequests);
    }

    [Fact]
    public async Task ACallback_IsProcessedOnce()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();

        var beforeCallback = await environment.SignInAsync(browser, bff, email, stopBefore: IsCallback);
        var callback = beforeCallback.Location!;

        var first = await browser.GetAsync(callback);
        Assert.Equal(HttpStatusCode.Redirect, first.Status);
        var session = browser.Cookie(bff.Origin, bff.SessionCookieName);
        Assert.NotNull(session);

        // The correlation and nonce cookies were consumed: the same callback cannot run again.
        var replay = await browser.GetAsync(callback);
        Assert.Equal(HttpStatusCode.BadRequest, replay.Status);
        Assert.Equal(1, bff.Channel.TokenRequests);
        Assert.Equal(session, browser.Cookie(bff.Origin, bff.SessionCookieName));
    }

    [Fact]
    public async Task ACallbackFromAnotherBrowser_IsRejected_LoginCsrf()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var attacker = environment.CreateBrowser(bff);
        using var victim = environment.CreateBrowser(bff);
        var attackerEmail = await environment.CreateUserAsync();

        // The attacker completes Identity's login for their own account and hands the callback URL to the victim.
        var beforeCallback = await environment.SignInAsync(attacker, bff, attackerEmail, stopBefore: IsCallback);

        var response = await victim.GetAsync(beforeCallback.Location!);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Null(victim.Cookie(bff.Origin, bff.SessionCookieName));
        Assert.Equal(0, bff.Channel.TokenRequests);
    }

    [Fact]
    public async Task AReplayedAuthorizationCode_IsRejectedByIdentity_AndEndsTheSessionItCreated()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();
        await environment.SignInAsync(browser, bff, email);

        // Replay the exact code exchange the BFF made (code, verifier, redirect URI), with a fresh client assertion.
        var exchange = QueryHelpers.ParseQuery("?" + Assert.Single(bff.Channel.TokenRequestBodies));
        var parameters = exchange.ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
        parameters["client_assertion"] = bff.Services.GetRequiredService<ClientAssertionFactory>().Create();
        using var backChannel = new HttpClient(environment.Identity.Server.CreateHandler()) { BaseAddress = BffTestEnvironment.Issuer };
        using var replay = await backChannel.PostAsync(new Uri("/connect/token", UriKind.Relative), new FormUrlEncodedContent(parameters), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains("invalid_grant", await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        // Identity revoked what the code produced (finding I3): the BFF session ends at its next refresh.
        bff.Time.Advance(TimeSpan.FromMinutes(9));
        var (user, _) = await BffTestEnvironment.UserAsync(browser, bff);
        Assert.Equal(HttpStatusCode.Unauthorized, user.Status);
        Assert.Equal(1, bff.Channel.RefreshRequests);
    }

    [Fact]
    public async Task AWrongClientKey_FailsTheCodeExchange_WithoutASession()
    {
        environment.RequireAvailable();
        using var otherKey = RSA.Create(3072);
        var bff = environment.CreateBff(environment.Storefront, options => options.PrivateKeyPem = otherKey.ExportPkcs8PrivateKeyPem());
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();

        var response = await environment.SignInAsync(browser, bff, email);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal(bff.Url("/bff/callback").AbsolutePath, response.Url.AbsolutePath);
        Assert.Contains("Sign-in was not completed", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid_client", response.Body, StringComparison.Ordinal);
        Assert.Null(browser.Cookie(bff.Origin, bff.SessionCookieName));
        Assert.Equal(1, bff.Channel.TokenRequests);
    }

    [Fact]
    public async Task AnUnregisteredRedirectUri_IsNeverRedirectedTo()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront, options => options.PublicOrigin = "https://evil.example.test");
        using var browser = environment.CreateBrowser(bff);

        var response = await browser.NavigateAsync(bff.Url("/bff/login"));

        Assert.Equal(BffTestEnvironment.Issuer.Host, response.Url.Host);
        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.DoesNotContain(browser.Transcript, r => r.Url.Host == "evil.example.test" && r.Url.AbsolutePath == "/bff/callback");
    }

    [Fact]
    public async Task AnUnreachableIdentity_IsATemporary503_NotAnError()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront, options => options.AuthorityHandler = new UnreachableHandler());
        using var browser = environment.CreateBrowser(bff);

        var response = await browser.GetAsync(bff.Url("/bff/login"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.Status);
        Assert.NotNull(response.Header("Retry-After"));
        Assert.DoesNotContain("IDX", response.Body, StringComparison.Ordinal);
        Assert.Null(browser.Cookie(bff.Origin, bff.SessionCookieName));
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(HttpRequestError.SecureConnectionError, "TLS failure (test).");
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("//evil.example/steal")]
    [InlineData("/\\evil.example")]
    public async Task ExternalReturnUrls_LandOnTheFrontEndRoot(string returnUrl)
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();

        var landing = await environment.SignInAsync(browser, bff, email, returnUrl: returnUrl);

        Assert.Equal(bff.Url("/"), landing.Url);
    }
}
