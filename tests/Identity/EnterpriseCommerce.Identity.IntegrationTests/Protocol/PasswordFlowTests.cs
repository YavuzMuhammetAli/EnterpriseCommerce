using System.Net;
using EnterpriseCommerce.Identity.Application.Accounts;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>
/// Password reset and password change through the Identity pages (IDENTITY_API_CONTRACT.md sections 2.8 and 2.9,
/// ADR-017 section 7, ADR-018), on SQL Server.
/// </summary>
public sealed class PasswordFlowTests(ControlledIdentityServerFixture identity) : IClassFixture<ControlledIdentityServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Reset request ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ResetRequest_IsNeutral_AndSendsOnlyWhatTheAccountQualifiesFor()
    {
        identity.RequireAvailable();
        var confirmed = await identity.CreateUserAsync();
        var unconfirmed = await identity.CreateUserAsync(emailConfirmed: false);
        var disabled = await identity.CreateUserAsync();
        await identity.DisableAsync(await identity.GetUserIdAsync(disabled));
        var unknown = $"nobody-{Guid.NewGuid():N}@example.test";

        using var browser = identity.CreateBrowser();
        var pages = new List<string>();
        foreach (var email in new[] { confirmed, unconfirmed, disabled, unknown })
        {
            using var response = await identity.SubmitPageFormAsync(browser, "/account/forgot-password", new Dictionary<string, string> { ["Email"] = email });
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            pages.Add(response.Headers.Location!.OriginalString);
        }

        Assert.All(pages, location => Assert.Equal("/account/reset-requested", location));
        Assert.Equal(SecurityTokenPurpose.PasswordReset, Assert.Single(await identity.Dispatcher.WaitForAsync(confirmed)).Purpose);
        Assert.Equal(SecurityTokenPurpose.EmailConfirmation, Assert.Single(await identity.Dispatcher.WaitForAsync(unconfirmed)).Purpose);

        // Recorded deliveries are authoritative: nothing at all for the disabled account.
        Assert.Equal(0, await identity.DeliveryCountAsync(await identity.GetUserIdAsync(disabled)));
        Assert.Empty(identity.Dispatcher.MessagesTo(disabled));
        Assert.Empty(identity.Dispatcher.MessagesTo(unknown));
    }

    [Fact]
    public async Task ResetRequest_IsRateLimitedSilently()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        using var browser = identity.CreateBrowser();

        for (var i = 0; i < 2; i++)
        {
            using var response = await identity.SubmitPageFormAsync(browser, "/account/forgot-password", new Dictionary<string, string> { ["Email"] = email });
            Assert.Equal("/account/reset-requested", response.Headers.Location?.OriginalString);
        }

        Assert.Equal(1, await identity.DeliveryCountAsync(await identity.GetUserIdAsync(email)));
    }

    // ---- Reset confirmation ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Reset_GetNeverConsumesTheToken_AndThePageIsNotCachedOrReferred()
    {
        identity.RequireAvailable();
        var token = await identity.RequestResetTokenAsync(await identity.CreateUserAsync());
        using var browser = identity.CreateBrowser();

        using var response = await browser.GetAsync("/account/reset-password?token=" + token, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("no-referrer", response.Headers.GetValues("Referrer-Policy"));
        Assert.False(await identity.IsConsumedAsync(token));
    }

    [Fact]
    public async Task Reset_ChangesThePassword_EndsEverySession_AndSignsNobodyIn()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        using var signedIn = identity.CreateBrowser();
        var session = await identity.SignInAsync(identity.Storefront, email, signedIn);
        var version = await identity.SecurityVersionOfAsync(userId);
        var token = await identity.RequestResetTokenAsync(email);

        using var browser = identity.CreateBrowser();
        using var response = await identity.SubmitResetAsync(browser, token);
        var page = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("Your password has been changed", page, StringComparison.Ordinal);
        Assert.DoesNotContain(token, page, StringComparison.Ordinal);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(c => c.StartsWith(IdentityServerFixtureCookies.Sso + "=", StringComparison.Ordinal) && !c.StartsWith(IdentityServerFixtureCookies.Sso + "=;", StringComparison.Ordinal)));
        Assert.False(await identity.IsSignedInAsync(browser));

        Assert.Equal(version + 1, await identity.SecurityVersionOfAsync(userId));
        Assert.True(await identity.PasswordWorksAsync(email, IdentityServerFixture.NewPassword));
        Assert.False(await identity.PasswordWorksAsync(email, IdentityServerFixture.Password));
        Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Storefront, session.RefreshToken!)).Status);
        Assert.False(await identity.IsSignedInAsync(signedIn));
    }

    [Fact]
    public async Task Reset_RejectsReplayedInvalidAndMalformedTokens_WithOneGenericMessage()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var token = await identity.RequestResetTokenAsync(email);
        using var browser = identity.CreateBrowser();

        using (var first = await identity.SubmitResetAsync(browser, token))
        {
            Assert.Contains("Your password has been changed", await first.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        foreach (var candidate in new[] { token, SecurityTokenCodecTestValues.WellFormedUnknown, "malformed-token-value" })
        {
            using var response = await identity.SubmitResetAsync(browser, candidate, "Another-Valid-Password-8");
            var page = await response.Content.ReadAsStringAsync(Ct);
            Assert.Contains("This link is invalid or has expired", page, StringComparison.Ordinal);
            Assert.DoesNotContain(candidate, page, StringComparison.Ordinal);
        }

        Assert.True(await identity.PasswordWorksAsync(email, IdentityServerFixture.NewPassword));
    }

    [Fact]
    public async Task Reset_PolicyFailure_ChangesNothing_AndTheTokenStaysUsable()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        var version = await identity.SecurityVersionOfAsync(userId);
        var token = await identity.RequestResetTokenAsync(email);
        using var browser = identity.CreateBrowser();

        using (var weak = await identity.SubmitResetAsync(browser, token, "weak"))
        {
            Assert.Contains("at least 12 characters", await weak.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        Assert.False(await identity.IsConsumedAsync(token));
        Assert.Equal(version, await identity.SecurityVersionOfAsync(userId));
        Assert.True(await identity.PasswordWorksAsync(email, IdentityServerFixture.Password));

        using var retry = await identity.SubmitResetAsync(browser, token);
        Assert.Contains("Your password has been changed", await retry.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_WhenTheDatabaseFails_Returns503AndChangesNothing()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var token = await identity.RequestResetTokenAsync(email);
        using var browser = identity.CreateBrowser();

        using (identity.Transactions.FailOperation(ResetPasswordHandler.Operation))
        {
            using var response = await identity.SubmitResetAsync(browser, token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        Assert.False(await identity.IsConsumedAsync(token));
        Assert.True(await identity.PasswordWorksAsync(email, IdentityServerFixture.Password));
    }

    // ---- Password change -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Change_ReissuesTheCurrentCookie_RejectsTheOldOne_AndEndsOtherSessions()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        var jar = new CookieJar();
        using var browser = identity.CreateBrowser(jar);
        var current = await identity.SignInAsync(identity.Storefront, email, browser);
        var other = await identity.SignInAsync(identity.Merchant, email);
        using var otherBrowser = identity.CreateBrowser();
        await identity.SignInAsync(identity.Admin, email, otherBrowser);
        var oldCookies = jar.Snapshot();
        var version = await identity.SecurityVersionOfAsync(userId);

        using var response = await identity.SubmitChangeAsync(browser, IdentityServerFixture.Password);

        Assert.Contains("Your password has been changed. Your other sessions", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(version + 1, await identity.SecurityVersionOfAsync(userId));
        Assert.NotEqual(oldCookies[IdentityServerFixtureCookies.Sso], jar.ValueOf(IdentityServerFixtureCookies.Sso));

        // The current browser stays signed in with the reissued cookie and can authorize again without a password.
        Assert.True(await identity.IsSignedInAsync(browser));
        var (code, verifier) = await identity.AuthorizeAsync(browser, identity.Storefront, email);
        Assert.Equal(HttpStatusCode.OK, (await identity.ExchangeCodeAsync(identity.Storefront, code, verifier)).Status);

        // Every earlier session ends: the old cookie, the other browser and every refresh chain (ADR-017 section 7).
        var replay = new CookieJar();
        replay.Restore(oldCookies);
        using (var oldBrowser = identity.CreateBrowser(replay))
        {
            Assert.False(await identity.IsSignedInAsync(oldBrowser));
        }

        Assert.False(await identity.IsSignedInAsync(otherBrowser));
        Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Merchant, other.RefreshToken!)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Storefront, current.RefreshToken!)).Status);

        Assert.True(await identity.PasswordWorksAsync(email, IdentityServerFixture.NewPassword));
        Assert.False(await identity.PasswordWorksAsync(email, IdentityServerFixture.Password));
    }

    [Fact]
    public async Task Change_WithAWrongCurrentPassword_CountsTowardsLockoutAndChangesNothing()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        using var browser = identity.CreateBrowser();
        await identity.SignInAsync(identity.Storefront, email, browser);
        var version = await identity.SecurityVersionOfAsync(userId);

        using var response = await identity.SubmitChangeAsync(browser, "Wrong-Current-Password-1");

        Assert.Contains("The current password is not correct", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(1, (await identity.ReadUserAsync(userId)).AccessFailedCount);
        Assert.Equal(version, await identity.SecurityVersionOfAsync(userId));
        Assert.True(await identity.IsSignedInAsync(browser));
        Assert.True(await identity.PasswordWorksAsync(email, IdentityServerFixture.Password));
    }

    [Fact]
    public async Task Change_PolicyFailure_ChangesNothing()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        using var browser = identity.CreateBrowser();
        await identity.SignInAsync(identity.Storefront, email, browser);
        var version = await identity.SecurityVersionOfAsync(userId);

        using var response = await identity.SubmitChangeAsync(browser, IdentityServerFixture.Password, "short");

        Assert.Contains("at least 12 characters", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(version, await identity.SecurityVersionOfAsync(userId));
        Assert.True(await identity.IsSignedInAsync(browser));
    }

    [Fact]
    public async Task Change_RequiresTheSsoSession_AndAGetChangesNothing()
    {
        identity.RequireAvailable();
        using var browser = identity.CreateBrowser();

        using var response = await browser.GetAsync("/account/change-password", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://localhost/account/login", response.Headers.Location!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Change_WhenTheDatabaseFails_Returns503AndKeepsThePasswordAndSession()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        using var browser = identity.CreateBrowser();
        await identity.SignInAsync(identity.Storefront, email, browser);

        using (identity.Transactions.FailOperation(ChangePasswordHandler.Operation))
        {
            using var response = await identity.SubmitChangeAsync(browser, IdentityServerFixture.Password);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        Assert.True(await identity.IsSignedInAsync(browser));
        Assert.True(await identity.PasswordWorksAsync(email, IdentityServerFixture.Password));
    }

    [Fact]
    public async Task Change_WhenTheCookieCannotBeReissued_TheChangeStandsAndTheUserSignsInAgain()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        var jar = new CookieJar();
        using var browser = identity.CreateBrowser(jar);
        await identity.SignInAsync(identity.Storefront, email, browser);
        var version = await identity.SecurityVersionOfAsync(userId);

        identity.FailNextCookieSignIn = true;
        string page;
        try
        {
            using var response = await identity.SubmitChangeAsync(browser, IdentityServerFixture.Password);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            page = await response.Content.ReadAsStringAsync(Ct);
        }
        finally
        {
            identity.FailNextCookieSignIn = false;
        }

        Assert.Contains("Sign in again", page, StringComparison.Ordinal);
        Assert.Equal(version + 1, await identity.SecurityVersionOfAsync(userId));
        Assert.True(await identity.PasswordWorksAsync(email, IdentityServerFixture.NewPassword));
        Assert.False(await identity.IsSignedInAsync(browser));
    }
}

internal static class SecurityTokenCodecTestValues
{
    /// <summary>A token with the right shape that was never issued.</summary>
    public static string WellFormedUnknown { get; } = Application.Security.SecurityTokenCodec.Generate().RawToken;
}
