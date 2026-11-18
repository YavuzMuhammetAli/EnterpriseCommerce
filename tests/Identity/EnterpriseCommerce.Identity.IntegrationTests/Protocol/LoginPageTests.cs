using System.Net;
using EnterpriseCommerce.Identity.Application.Sessions;
using EnterpriseCommerce.Identity.Domain.Accounts;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>
/// The Identity-hosted login page (ADR-017 section 9, PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.2):
/// enumeration resistance, lockout, account status, CSRF layers, return URLs and the SSO cookie.
/// </summary>
public sealed class LoginPageTests(IdentityServerFixture identity) : IClassFixture<IdentityServerFixture>
{
    private const string GenericFailure = "Invalid email or password.";
    private const string ConfirmHint = "Confirm your email address";

    [Fact]
    public async Task UnknownAccountAndWrongPassword_GetTheSameGenericResponse()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();

        var unknown = await FailedLoginBodyAsync($"nobody-{Guid.NewGuid():N}@example.test", IdentityServerFixture.Password);
        var wrong = await FailedLoginBodyAsync(email, "Wrong-Password-123");

        Assert.Contains(GenericFailure, unknown, StringComparison.Ordinal);
        Assert.Contains(GenericFailure, wrong, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FiveFailures_LockTheAccount_AndTheCorrectPasswordIsThenRefusedGenerically()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();

        for (var i = 0; i < 5; i++)
        {
            await FailedLoginBodyAsync(email, $"Wrong-Password-{i}");
        }

        using var browser = identity.CreateBrowser();
        using var response = await identity.SubmitLoginAsync(browser, email, IdentityServerFixture.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(GenericFailure, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnconfirmedEmail_HintIsShownOnlyAfterTheCorrectPassword_AndNoSessionIsCreated()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync(emailConfirmed: false);

        var wrong = await FailedLoginBodyAsync(email, "Wrong-Password-123");
        using var browser = identity.CreateBrowser();
        using var correct = await identity.SubmitLoginAsync(browser, email, IdentityServerFixture.Password);
        var correctBody = await correct.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(ConfirmHint, wrong, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, correct.StatusCode);
        Assert.Contains(ConfirmHint, correctBody, StringComparison.Ordinal);
        Assert.DoesNotContain(correct.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [], c => c.StartsWith("__Host-ec-identity=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisabledAccount_IsRefusedGenerically_EvenWithTheCorrectPassword()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        await using (var scope = identity.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().Users
                .Where(user => user.Id == userId)
                .ExecuteUpdateAsync(set => set.SetProperty(user => user.Status, AccountStatus.Disabled), TestContext.Current.CancellationToken);
        }

        var body = await FailedLoginBodyAsync(email, IdentityServerFixture.Password);

        Assert.Contains(GenericFailure, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossOriginPost_IsRejectedBeforeCredentialsAreChecked()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        using var browser = identity.CreateBrowser();

        using var withoutOrigin = await identity.SubmitLoginAsync(browser, email, IdentityServerFixture.Password, sameOrigin: false);

        using var crossSite = new HttpRequestMessage(HttpMethod.Post, "/account/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["Input.Email"] = email, ["Input.Password"] = IdentityServerFixture.Password }),
        };
        crossSite.Headers.Add("Sec-Fetch-Site", "same-site");
        crossSite.Headers.Add("Origin", "https://localhost");
        using var crossSiteResponse = await browser.SendAsync(crossSite, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, withoutOrigin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossSiteResponse.StatusCode);
    }

    [Fact]
    public async Task Login_SetsAHostOnlySecureHttpOnlyLaxCookie_AndRedirectsOnlyToLocalPaths()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        using var browser = identity.CreateBrowser();

        using var response = await identity.SubmitLoginAsync(browser, email, IdentityServerFixture.Password, returnUrl: "https://attacker.example/steal");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-ec-identity=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("secure", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.Ordinal);
        Assert.Contains("samesite=lax", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/", cookie, StringComparison.Ordinal);
        Assert.DoesNotContain("domain=", cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SsoCookie_IsNoLongerAccepted_AfterTheSecurityVersionIsRaised()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        using var browser = identity.CreateBrowser();
        await identity.AuthorizeAsync(browser, identity.Storefront, email);

        var userId = await identity.GetUserIdAsync(email);
        await using (var scope = identity.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<LogoutAllSessionsHandler>().HandleAsync(userId, TestContext.Current.CancellationToken);
        }

        using var response = await browser.GetAsync(
            identity.BuildAuthorizeUrl(identity.Storefront, IdentityServerFixture.CodeChallenge(new string('v', 43)), "S256"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    private async Task<string> FailedLoginBodyAsync(string email, string password)
    {
        using var browser = identity.CreateBrowser();
        using var response = await identity.SubmitLoginAsync(browser, email, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
