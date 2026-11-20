using System.Net;
using EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;

namespace EnterpriseCommerce.Bff.IntegrationTests;

/// <summary>
/// Password change and silent re-authorization (ADR-017 section 7, finding I14): the change ends every client session,
/// including this browser's BFF session; Identity reissues only its SSO cookie, through which the BFF re-authorizes
/// with <c>prompt=none</c> and the normal state, nonce and PKCE validation.
/// </summary>
public sealed class PasswordChangeTests(BffTestEnvironment environment) : IClassFixture<BffTestEnvironment>
{
    [Fact]
    public async Task AfterAPasswordChange_TheOldTokensAreDropped_AndTheBffReauthorizesSilently()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();
        await environment.SignInAsync(browser, bff, email);
        var oldRecord = bff.RecordIdOf(browser)!;
        var oldTokens = (await bff.TokensOfAsync(browser))!.Tokens;

        var change = await BffTestEnvironment.ChangePasswordAsync(browser);
        Assert.True(change.Status is HttpStatusCode.OK or HttpStatusCode.Redirect, change.Status.ToString());

        // The next refresh is rejected (the authorization was revoked): the session ends, no retry with the old token.
        bff.Time.Advance(TimeSpan.FromMinutes(9));
        var (user, _) = await BffTestEnvironment.UserAsync(browser, bff);
        Assert.Equal(HttpStatusCode.Unauthorized, user.Status);
        Assert.Equal(1, bff.Channel.RefreshRequests);
        Assert.Null(await bff.Store.GetTokensAsync(oldRecord, TestContext.Current.CancellationToken));

        // Silent re-authorization through the reissued SSO cookie: no login page, a new validated session.
        var transcriptStart = browser.Transcript.Count;
        var landing = await browser.NavigateAsync(bff.Url("/bff/login?silent=1&returnUrl=%2Fcheckout"));

        Assert.Equal(bff.Url("/checkout"), landing.Url);
        var flow = browser.Transcript.Skip(transcriptStart).ToList();
        Assert.DoesNotContain(flow, response => response.Url.AbsolutePath == "/account/login");
        var authorize = flow.Single(response => response.Url.AbsolutePath == "/connect/authorize").Url;
        Assert.Equal("none", QueryHelpers.ParseQuery(authorize.Query)["prompt"]);
        Assert.Equal("S256", QueryHelpers.ParseQuery(authorize.Query)["code_challenge_method"]);

        var newRecord = bff.RecordIdOf(browser);
        Assert.NotNull(newRecord);
        Assert.NotEqual(oldRecord, newRecord);
        var newTokens = (await bff.TokensOfAsync(browser))!.Tokens;
        Assert.NotEqual(oldTokens.RefreshToken, newTokens.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, (await BffTestEnvironment.UserAsync(browser, bff)).User.Status);
    }

    [Fact]
    public async Task WithoutAnIdentitySession_SilentReauthorizationFallsBackToAnInteractiveLogin()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        await environment.SignInAsync(browser, bff, await environment.CreateUserAsync());
        var record = bff.RecordIdOf(browser)!;

        // The Identity SSO cookie is gone (expired, signed out elsewhere): prompt=none returns login_required.
        browser.RemoveCookie(BffTestEnvironment.Issuer, "__Host-ec-identity");
        var landing = await browser.NavigateAsync(bff.Url("/bff/login?silent=1&returnUrl=%2Faccount"));

        Assert.Equal("/account/login", landing.Url.AbsolutePath);
        Assert.Equal(BffTestEnvironment.Issuer.Host, landing.Url.Host);
        var fallback = browser.Transcript.Single(response => response.Url.AbsolutePath == "/bff/callback" && response.Url.Query.Contains("error=login_required", StringComparison.Ordinal));
        Assert.Equal(bff.Url("/bff/login?returnUrl=%2Faccount"), fallback.Location);

        // The unusable BFF session was ended, not kept.
        Assert.Null(await bff.Store.GetTokensAsync(record, TestContext.Current.CancellationToken));
        Assert.Null(browser.Cookie(bff.Origin, bff.SessionCookieName));
    }
}
