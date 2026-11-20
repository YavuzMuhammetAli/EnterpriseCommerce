using System.Net;
using System.Text.Json;
using EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using OpenIddict.Abstractions;

namespace EnterpriseCommerce.Bff.IntegrationTests;

/// <summary>Logout and logout-all through the BFF (ADR-017 section 8).</summary>
public sealed class LogoutTests(BffTestEnvironment environment) : IClassFixture<BffTestEnvironment>
{
    [Fact]
    public async Task Logout_RevokesAtIdentity_DeletesTheSession_AndHandsOverATokenFreeEndSessionUrl()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();
        var userId = await environment.UserIdAsync(email);
        await environment.SignInAsync(browser, bff, email);
        var recordId = bff.RecordIdOf(browser)!;
        var idToken = (await bff.TokensOfAsync(browser))!.Tokens.IdToken!;

        var logout = await BffTestEnvironment.PostAsync(browser, bff, "/bff/logout");

        Assert.Equal(HttpStatusCode.OK, logout.Status);
        Assert.Null(await bff.Store.GetTokensAsync(recordId, TestContext.Current.CancellationToken));
        Assert.Null(browser.Cookie(bff.Origin, bff.SessionCookieName));
        Assert.All(await environment.AuthorizationStatusesAsync(userId), status => Assert.Equal(OpenIddictConstants.Statuses.Revoked, status));

        using var body = JsonDocument.Parse(logout.Body);
        var endSession = new Uri(body.RootElement.GetProperty("endSessionUrl").GetString()!);
        var query = QueryHelpers.ParseQuery(endSession.Query);
        Assert.Equal(new Uri(BffTestEnvironment.Issuer, "/connect/endsession").AbsoluteUri, endSession.GetLeftPart(UriPartial.Path));
        Assert.Equal(environment.Storefront.ClientId, query["client_id"]);
        Assert.Equal(bff.Url("/bff/signed-out").AbsoluteUri, query["post_logout_redirect_uri"]);
        Assert.False(query.ContainsKey("id_token_hint"));
        Assert.DoesNotContain(idToken, logout.Body, StringComparison.Ordinal);

        // Identity's end-session page accepts the registered post-logout URI (confirmation step, finding I17).
        var confirmation = await browser.GetAsync(endSession);
        Assert.Equal(HttpStatusCode.OK, confirmation.Status);

        // The old cookie value is no session any more, and the browser is signed out of the BFF.
        Assert.Equal(HttpStatusCode.Unauthorized, (await BffTestEnvironment.UserAsync(browser, bff)).User.Status);
    }

    [Fact]
    public async Task Logout_StillEndsTheLocalSession_WhenIdentityIsUnreachable()
    {
        environment.RequireAvailable();
        var down = new UnavailableHandler();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        await environment.SignInAsync(browser, bff, await environment.CreateUserAsync());
        var cookie = browser.Cookie(bff.Origin, bff.SessionCookieName)!;

        // A second instance of the same deployment, whose back channel to Identity is down.
        var isolated = environment.CreateBff(environment.Storefront, options => options.AuthorityHandler = down);
        using var other = environment.CreateBrowser(isolated);
        other.SetCookie(isolated.Origin, isolated.SessionCookieName, cookie);
        var recordId = isolated.RecordIdOf(other)!;

        down.Enabled = true;
        var logout = await BffTestEnvironment.PostAsync(other, isolated, "/bff/logout");

        Assert.Equal(HttpStatusCode.OK, logout.Status);
        Assert.Null(await isolated.Store.GetTokensAsync(recordId, TestContext.Current.CancellationToken));
        Assert.Null(other.Cookie(isolated.Origin, isolated.SessionCookieName));
    }

    [Fact]
    public async Task LogoutAll_EndsEverySessionOfTheUser_AcrossBrowsersAndFrontEnds()
    {
        environment.RequireAvailable();
        var storefront = environment.CreateBff(environment.Storefront);
        var merchant = environment.CreateBff(environment.Merchant);
        var email = await environment.CreateUserAsync();
        var userId = await environment.UserIdAsync(email);
        using var laptop = environment.CreateBrowser(storefront, merchant);
        using var phone = environment.CreateBrowser(storefront);
        await environment.SignInAsync(laptop, storefront, email);
        await environment.SignInAsync(laptop, merchant, email);
        await environment.SignInAsync(phone, storefront, email);
        var versionBefore = await environment.SecurityVersionAsync(userId);

        var response = await BffTestEnvironment.PostAsync(laptop, storefront, "/bff/logout-all");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal(versionBefore + 1, await environment.SecurityVersionAsync(userId));
        Assert.Null(laptop.Cookie(storefront.Origin, storefront.SessionCookieName));

        // The other sessions learn it at their next refresh (invalid_grant), never by trusting a stale token.
        storefront.Time.Advance(TimeSpan.FromMinutes(9));
        merchant.Time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(HttpStatusCode.Unauthorized, (await BffTestEnvironment.UserAsync(phone, storefront)).User.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await BffTestEnvironment.UserAsync(laptop, merchant)).User.Status);
        Assert.All(await environment.AuthorizationStatusesAsync(userId), status => Assert.Equal(OpenIddictConstants.Statuses.Revoked, status));

        // The browser's Identity session ended too: a silent sign-in now needs an interactive login.
        var silent = await laptop.NavigateAsync(storefront.Url("/bff/login?silent=1&returnUrl=%2F"));
        Assert.Equal("/account/login", silent.Url.AbsolutePath);
    }

    [Fact]
    public async Task LogoutAll_DoesNotClaimSuccess_WhenItCannotBeAuthorized()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();
        var userId = await environment.UserIdAsync(email);
        await environment.SignInAsync(browser, bff, email);
        var (_, xsrf) = await BffTestEnvironment.UserAsync(browser, bff);

        // The refresh outcome becomes unknown, then the access token expires: there is no usable bearer token.
        bff.Channel.Fault = RefreshFault.LoseResponse;
        bff.Time.Advance(TimeSpan.FromMinutes(9));
        await BffTestEnvironment.UserAsync(browser, bff);
        bff.Time.Advance(TimeSpan.FromMinutes(2));

        var response = await browser.FetchAsync(HttpMethod.Post, bff.Url("/bff/logout-all"), xsrf);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.DoesNotContain("signedOut", response.Body, StringComparison.Ordinal);
        Assert.Equal(1, await environment.SecurityVersionAsync(userId));
    }

    private sealed class UnavailableHandler : DelegatingHandler
    {
        public UnavailableHandler()
            : base(new HttpClientHandler())
        {
        }

        public volatile bool Enabled;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Identity unreachable (test).");
    }
}
