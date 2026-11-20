using System.Net;
using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>
/// Token revocation, end-session, Identity-local logout and logout-all (IDENTITY_API_CONTRACT.md sections 2.5 and 2.6,
/// ADR-017 sections 6 to 8), through the real host on SQL Server.
/// </summary>
public sealed class SessionRevocationTests(ControlledIdentityServerFixture identity) : IClassFixture<ControlledIdentityServerFixture>
{
    // ---- /connect/revoke -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Revoke_EndsTheWholeSessionOfTheToken_AndNoOtherSession()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        var first = await identity.SignInAsync(identity.Storefront, email);
        var second = await identity.SignInAsync(identity.Storefront, email);

        Assert.Equal(HttpStatusCode.OK, await identity.RevokeAsync(identity.Storefront, first.RefreshToken!, "refresh_token"));

        Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Storefront, first.RefreshToken!)).Status);
        Assert.Equal(HttpStatusCode.OK, (await identity.RefreshAsync(identity.Storefront, second.RefreshToken!)).Status);

        var statuses = await identity.AuthorizationStatusesAsync(userId);
        Assert.Equal(1, statuses.Count(status => status == Statuses.Revoked));
        Assert.Equal(1, statuses.Count(status => status == Statuses.Valid));
    }

    [Fact]
    public async Task Revoke_IsRepeatable_AndAnUnknownTokenIsAccepted()
    {
        identity.RequireAvailable();
        var session = await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync());

        Assert.Equal(HttpStatusCode.OK, await identity.RevokeAsync(identity.Storefront, session.RefreshToken!));
        Assert.Equal(HttpStatusCode.OK, await identity.RevokeAsync(identity.Storefront, session.RefreshToken!));
        Assert.Equal(HttpStatusCode.OK, await identity.RevokeAsync(identity.Storefront, "not-a-token-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task Revoke_ByAnotherClient_RevokesNothing()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var session = await identity.SignInAsync(identity.Storefront, email);

        // OpenIddict answers 200 without revoking, as RFC 7009 allows, so that the response reveals nothing.
        Assert.Equal(HttpStatusCode.OK, await identity.RevokeAsync(identity.Merchant, session.RefreshToken!));

        Assert.All(await identity.AuthorizationStatusesAsync(await identity.GetUserIdAsync(email)), status => Assert.Equal(Statuses.Valid, status));
        Assert.Equal(HttpStatusCode.OK, (await identity.RefreshAsync(identity.Storefront, session.RefreshToken!)).Status);
    }

    [Fact]
    public async Task Revoke_WhenCleanupFails_TheCommittedAuthorizationRevocationStillEndsTheSession()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var session = await identity.SignInAsync(identity.Storefront, email);

        identity.Cleanup.Fail = true;
        try
        {
            // Revoking the access token: its refresh token is never touched by the revocation itself.
            Assert.Equal(HttpStatusCode.OK, await identity.RevokeAsync(identity.Storefront, session.AccessToken!, "access_token"));
        }
        finally
        {
            identity.Cleanup.Fail = false;
        }

        // The refresh token entry is still valid, but its authorization is revoked: the session is over.
        Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Storefront, session.RefreshToken!)).Status);
        Assert.Contains(Statuses.Revoked, await identity.AuthorizationStatusesAsync(await identity.GetUserIdAsync(email)));
    }

    [Fact]
    public async Task Revoke_WhenTheDatabaseFails_Returns503AndReportsNothingAsRevoked()
    {
        identity.RequireAvailable();
        var session = await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync());

        using (identity.Transactions.FailOperation("end-client-session"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await identity.RevokeAsync(identity.Storefront, session.RefreshToken!));
        }

        // Nothing was revoked: the token itself was not either (the native revocation runs after the session change).
        Assert.Equal(HttpStatusCode.OK, (await identity.RefreshAsync(identity.Storefront, session.RefreshToken!)).Status);
    }

    // ---- /connect/endsession ---------------------------------------------------------------------------------------

    [Fact]
    public async Task EndSession_Get_RendersAConfirmationAndChangesNothing()
    {
        identity.RequireAvailable();
        using var browser = identity.CreateBrowser();
        var session = await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync(), browser);

        using var response = await browser.GetAsync(
            "/connect/endsession" + IdentityServerFixture.EndSessionQuery(identity.Storefront, session.IdToken, "s1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Do you want to sign out?", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("no-referrer", response.Headers.GetValues("Referrer-Policy"));
        Assert.True(await identity.IsSignedInAsync(browser));
        Assert.Equal(HttpStatusCode.OK, (await identity.RefreshAsync(identity.Storefront, session.RefreshToken!)).Status);
    }

    [Fact]
    public async Task EndSession_Post_EndsTheHintedSession_ClearsTheCookie_AndRedirectsWithState()
    {
        identity.RequireAvailable();
        var jar = new CookieJar();
        using var browser = identity.CreateBrowser(jar);
        var session = await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync(), browser);

        using var response = await identity.SubmitEndSessionAsync(browser, IdentityServerFixture.EndSessionQuery(identity.Storefront, session.IdToken, "state-123"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith(identity.Storefront.PostLogoutRedirectUri, location, StringComparison.Ordinal);
        Assert.Contains("state=state-123", location, StringComparison.Ordinal);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(IdentityServerFixtureCookies.Sso + "=;", StringComparison.Ordinal));
        Assert.Null(jar.ValueOf(IdentityServerFixtureCookies.Sso));
        Assert.False(await identity.IsSignedInAsync(browser));

        // The client session named by the hint is revoked as well.
        Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Storefront, session.RefreshToken!)).Status);

        // Repeating the logout still succeeds.
        using var again = await identity.SubmitEndSessionAsync(browser, IdentityServerFixture.EndSessionQuery(identity.Storefront, session.IdToken, "state-456"));
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.Contains("state=state-456", again.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndSession_WithoutRedirectUri_GoesToTheLocalSignedOutPage()
    {
        identity.RequireAvailable();
        using var browser = identity.CreateBrowser();
        await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync(), browser);

        using var response = await identity.SubmitEndSessionAsync(browser, string.Empty);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/signed-out", response.Headers.Location!.OriginalString);
        Assert.False(await identity.IsSignedInAsync(browser));
    }

    [Fact]
    public async Task EndSession_RejectsAnUnregisteredOrExternalRedirectUri()
    {
        identity.RequireAvailable();
        using var browser = identity.CreateBrowser();
        await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync(), browser);

        foreach (var target in new[] { "https://evil.example.test/", identity.Merchant.PostLogoutRedirectUri })
        {
            using var response = await browser.GetAsync(
                "/connect/endsession?client_id=" + identity.Storefront.ClientId + "&post_logout_redirect_uri=" + Uri.EscapeDataString(target),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }

        Assert.True(await identity.IsSignedInAsync(browser));
    }

    [Fact]
    public async Task EndSession_CrossOriginOrWithoutAntiforgery_IsRejectedAndKeepsTheSession()
    {
        identity.RequireAvailable();
        using var browser = identity.CreateBrowser();
        var session = await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync(), browser);
        var query = IdentityServerFixture.EndSessionQuery(identity.Storefront, session.IdToken, "s");

        using (var crossOrigin = await identity.SubmitEndSessionAsync(browser, query, sameOrigin: false))
        {
            Assert.Equal(HttpStatusCode.Forbidden, crossOrigin.StatusCode);
        }

        using (var noAntiforgery = await identity.SubmitEndSessionAsync(browser, query, withAntiforgery: false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noAntiforgery.StatusCode);
        }

        Assert.True(await identity.IsSignedInAsync(browser));
        Assert.Equal(HttpStatusCode.OK, (await identity.RefreshAsync(identity.Storefront, session.RefreshToken!)).Status);
    }

    [Fact]
    public async Task EndSession_WithAnotherUsersHint_EndsOnlyTheCurrentBrowserSession()
    {
        identity.RequireAvailable();
        using var browser = identity.CreateBrowser();
        await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync(), browser);
        var victim = await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync());

        using var response = await identity.SubmitEndSessionAsync(browser, IdentityServerFixture.EndSessionQuery(identity.Storefront, victim.IdToken, "s"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(await identity.IsSignedInAsync(browser));
        Assert.Equal(HttpStatusCode.OK, (await identity.RefreshAsync(identity.Storefront, victim.RefreshToken!)).Status);
    }

    [Fact]
    public async Task EndSession_WhenTheDatabaseFails_Returns503AndKeepsTheCookie()
    {
        identity.RequireAvailable();
        var jar = new CookieJar();
        using var browser = identity.CreateBrowser(jar);
        var session = await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync(), browser);

        using (identity.Transactions.FailOperation("end-client-session"))
        {
            using var response = await identity.SubmitEndSessionAsync(browser, IdentityServerFixture.EndSessionQuery(identity.Storefront, session.IdToken, "s"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith(IdentityServerFixtureCookies.Sso + "=;", StringComparison.Ordinal)));
        }

        Assert.NotNull(jar.ValueOf(IdentityServerFixtureCookies.Sso));
        Assert.True(await identity.IsSignedInAsync(browser));
    }

    // ---- /account/logout -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://evil.example.test/steal", "/account/signed-out")]
    [InlineData("//evil.example.test/steal", "/account/signed-out")]
    [InlineData("/\\evil.example.test", "/account/signed-out")]
    [InlineData("/account/login", "/account/login")]
    public async Task LocalLogout_IsPostOnly_AndRedirectsOnlyToLocalUrls(string returnUrl, string expected)
    {
        identity.RequireAvailable();
        using var browser = identity.CreateBrowser();
        await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync(), browser);

        using (var get = await browser.GetAsync("/account/logout?returnUrl=" + Uri.EscapeDataString(returnUrl), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        }

        Assert.True(await identity.IsSignedInAsync(browser));

        using var post = await identity.SubmitPageFormAsync(browser, "/account/logout", new Dictionary<string, string> { ["ReturnUrl"] = returnUrl });
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal(expected, post.Headers.Location!.OriginalString);
        Assert.False(await identity.IsSignedInAsync(browser));
    }

    // ---- POST /api/v1/auth/logout-all ------------------------------------------------------------------------------

    [Fact]
    public async Task LogoutAll_EndsEverySessionOfTheCaller_AndNoOtherUsersSession()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        using var browser = identity.CreateBrowser();
        var storefront = await identity.SignInAsync(identity.Storefront, email, browser);
        var merchant = await identity.SignInAsync(identity.Merchant, email);
        var other = await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync());
        var version = await identity.SecurityVersionOfAsync(userId);

        Assert.Equal(HttpStatusCode.NoContent, await identity.LogoutAllAsync(storefront.AccessToken));

        Assert.Equal(version + 1, await identity.SecurityVersionOfAsync(userId));
        Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Storefront, storefront.RefreshToken!)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Merchant, merchant.RefreshToken!)).Status);
        Assert.False(await identity.IsSignedInAsync(browser));
        Assert.All(await identity.AuthorizationStatusesAsync(userId), status => Assert.Equal(Statuses.Revoked, status));
        Assert.Equal(HttpStatusCode.OK, (await identity.RefreshAsync(identity.Storefront, other.RefreshToken!)).Status);

        // Repeating it is safe.
        Assert.Equal(HttpStatusCode.NoContent, await identity.LogoutAllAsync(storefront.AccessToken));
        Assert.Equal(version + 2, await identity.SecurityVersionOfAsync(userId));
    }

    [Fact]
    public async Task LogoutAll_RejectsMissingInvalidAndNonUserCredentials()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        using var browser = identity.CreateBrowser();
        var session = await identity.SignInAsync(identity.Storefront, email, browser);
        var version = await identity.SecurityVersionOfAsync(userId);

        Assert.Equal(HttpStatusCode.Unauthorized, await identity.LogoutAllAsync(null));
        Assert.Equal(HttpStatusCode.Unauthorized, await identity.LogoutAllAsync("not.a.token"));
        Assert.Equal(HttpStatusCode.Unauthorized, await identity.LogoutAllAsync(session.AccessToken![..^4] + "AAAA"));

        // The SSO cookie alone is never accepted: the endpoint is bearer-only, so it cannot be reached by CSRF.
        Assert.Equal(HttpStatusCode.Unauthorized, await identity.LogoutAllAsync(null, browser));

        // A service token (client credentials) carries a service audience and is not a user.
        var service = await identity.RequestTokenAsync(identity.Service, new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = "svc.inventory.reservations",
        });
        Assert.Equal(HttpStatusCode.OK, service.Status);
        Assert.Contains(await identity.LogoutAllAsync(service.AccessToken), new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });

        Assert.Equal(version, await identity.SecurityVersionOfAsync(userId));
        Assert.True(await identity.IsSignedInAsync(browser));
    }

    [Fact]
    public async Task LogoutAll_WhenCleanupFails_StillSucceedsAndEverySessionIsDenied()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var session = await identity.SignInAsync(identity.Storefront, email);

        identity.Cleanup.Fail = true;
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, await identity.LogoutAllAsync(session.AccessToken));
        }
        finally
        {
            identity.Cleanup.Fail = false;
        }

        // OpenIddict still lists the authorization as valid, but the security version denies the session.
        Assert.Contains(Statuses.Valid, await identity.AuthorizationStatusesAsync(await identity.GetUserIdAsync(email)));
        Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Storefront, session.RefreshToken!)).Status);
    }

    [Fact]
    public async Task LogoutAll_WhenTheDatabaseFails_Returns503AndChangesNothing()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        var session = await identity.SignInAsync(identity.Storefront, email);
        var version = await identity.SecurityVersionOfAsync(userId);

        using (identity.Transactions.FailOperation("logout-all"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await identity.LogoutAllAsync(session.AccessToken));
        }

        Assert.Equal(version, await identity.SecurityVersionOfAsync(userId));
        Assert.Equal(HttpStatusCode.OK, (await identity.RefreshAsync(identity.Storefront, session.RefreshToken!)).Status);
    }
}

internal static class IdentityServerFixtureCookies
{
    public const string Sso = "__Host-ec-identity";
}
